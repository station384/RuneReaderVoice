// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using OpenCvSharp;
using ZXing;

namespace RuneReaderVoice.Session;

/// <summary>
/// CV2-based detector for the RuneReader Barcode font symbology.
///
/// Input is expected to be a pre-thresholded image where barcode ink is black
/// and the barcode background / quiet zone is white.
///
/// Decoder model:
///   - Locate vertical guard bars as global black-run components, not by broad row bands.
///   - Group guard bars into rows by vertical overlap.
///   - Treat every gap between adjacent guard bars as a cell.
///   - Empty cell between two guards = start/stop marker.
///   - Non-empty cells between the first and second marker = payload bytes.
///   - If a row starts but does not stop, payload carries to the next guard row
///     in row-major order so wrapped FontStrings decode as one barcode.
///   - Bits are read by scanning the upper lane band for ink anywhere in the cell;
///     horizontal data-bar length is not assumed.
/// </summary>
public static class RrvBarcodeDetector
{
    private const int MinVerticalRunPixels = 4;
    private const double RunMergeOverlapThreshold = 0.60;
    private const double RowOverlapThreshold = 0.50;
    private const double MinGuardHeightToWidthRatio = 1.50;
    private const double LaneBandStartFraction = 0.15;
    private const double LaneBandEndFraction = 0.40;
    private const int NumLanes = 8;
    private const int MaxDebugCellsPerRow = 80;
    private const int MaxDebugPayloadPreviewChars = 80;
    private const bool DebugTraceEnabled = false;


    private static void Trace(string message)
    {
        if (DebugTraceEnabled)
            Debug.WriteLine(message);
    }

    public static Result[]? Detect(Mat frame)
    {
        if (frame == null || frame.Empty()) return null;

        try
        {
            Mat gray;
            bool ownGray = false;
            if (frame.Channels() == 1)
            {
                gray = frame;
            }
            else
            {
                gray = new Mat();
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
                ownGray = true;
            }

            try
            {
                int rows = gray.Rows;
                int cols = gray.Cols;
                if (rows < 4 || cols < 4) return null;

                Trace($"[RRVB] Detect frame rows={rows} cols={cols}");

                var guards = FindGuardBarsByGlobalRuns(gray, rows, cols);
                if (guards.Count < 2)
                {
                    Trace($"[RRVB] Reject frame: only {guards.Count} global guard candidates");
                    return null;
                }

                var guardRows = GroupGuardBarsIntoRows(guards);
                if (guardRows.Count == 0) return null;

                var results = DecodeGuardRowsRowMajor(gray, guardRows, rows, cols);
                return results.Count > 0 ? results.ToArray() : null;
            }
            finally
            {
                if (ownGray) gray.Dispose();
            }
        }
        catch (Exception ex)
        {
            Trace($"[RRVB] Detect failed: {ex.Message}");
            return null;
        }
    }

    private readonly struct GuardBar
    {
        public readonly int XStart, XEnd, YStart, YEnd;
        public int Width => XEnd - XStart;
        public int Height => YEnd - YStart;

        public GuardBar(int xStart, int xEnd, int yStart, int yEnd)
        {
            XStart = xStart;
            XEnd = xEnd;
            YStart = yStart;
            YEnd = yEnd;
        }
    }

    private sealed class GuardBuild
    {
        public int XStart;
        public int XEnd;
        public int YStart;
        public int YEnd;
        public int LastX;

        public int Width => XEnd - XStart;
        public int Height => YEnd - YStart;

        public GuardBuild(int x, int y0, int y1)
        {
            XStart = x;
            XEnd = x + 1;
            YStart = y0;
            YEnd = y1;
            LastX = x;
        }

        public void Add(int x, int y0, int y1)
        {
            XEnd = x + 1;
            YStart = Math.Min(YStart, y0);
            YEnd = Math.Max(YEnd, y1);
            LastX = x;
        }

        public GuardBar ToGuardBar() => new(XStart, XEnd, YStart, YEnd);
    }

    private readonly struct BlackRun
    {
        public readonly int X;
        public readonly int YStart;
        public readonly int YEnd;
        public int Height => YEnd - YStart;

        public BlackRun(int x, int yStart, int yEnd)
        {
            X = x;
            YStart = yStart;
            YEnd = yEnd;
        }
    }

    private static List<GuardBar> FindGuardBarsByGlobalRuns(Mat gray, int rows, int cols)
    {
        var active = new List<GuardBuild>();
        var finished = new List<GuardBuild>();

        unsafe
        {
            byte* ptr = (byte*)gray.DataPointer;
            int step = (int)gray.Step();

            for (int x = 0; x < cols; x++)
            {
                var runs = GetColumnRuns(ptr, step, x, rows);
                var used = new HashSet<GuardBuild>();

                foreach (var run in runs)
                {
                    GuardBuild? best = null;
                    double bestOverlap = 0.0;

                    foreach (var candidate in active)
                    {
                        if (candidate.LastX != x - 1 || used.Contains(candidate))
                            continue;

                        double overlap = OverlapRatio(candidate.YStart, candidate.YEnd, run.YStart, run.YEnd);
                        if (overlap > bestOverlap)
                        {
                            bestOverlap = overlap;
                            best = candidate;
                        }
                    }

                    if (best != null && bestOverlap >= RunMergeOverlapThreshold)
                    {
                        best.Add(x, run.YStart, run.YEnd);
                        used.Add(best);
                    }
                    else
                    {
                        var created = new GuardBuild(x, run.YStart, run.YEnd);
                        active.Add(created);
                        used.Add(created);
                    }
                }

                for (int i = active.Count - 1; i >= 0; i--)
                {
                    if (active[i].LastX < x)
                    {
                        finished.Add(active[i]);
                        active.RemoveAt(i);
                    }
                }
            }
        }

        finished.AddRange(active);

        var guards = finished
            .Where(g => g.Height >= MinVerticalRunPixels)
            .Where(g => g.Width > 0)
            .Where(g => g.Height >= Math.Max(MinVerticalRunPixels, g.Width * MinGuardHeightToWidthRatio))
            .Select(g => g.ToGuardBar())
            .OrderBy(g => g.YStart)
            .ThenBy(g => g.XStart)
            .ToList();

        Trace($"[RRVB] Global guard candidates: {guards.Count}");
        if (guards.Count > 0)
        {
            int minH = guards.Min(g => g.Height);
            int maxH = guards.Max(g => g.Height);
            int medH = Median(guards.Select(g => g.Height));
            int minW = guards.Min(g => g.Width);
            int maxW = guards.Max(g => g.Width);
            int medW = Median(guards.Select(g => g.Width));
            Trace($"[RRVB] Guard stats H min/med/max={minH}/{medH}/{maxH} W min/med/max={minW}/{medW}/{maxW}");
        }
        return guards;
    }

    private static unsafe List<BlackRun> GetColumnRuns(byte* ptr, int step, int x, int rows)
    {
        var runs = new List<BlackRun>();
        int? start = null;

        for (int y = 0; y <= rows; y++)
        {
            bool ink = false;
            if (y < rows)
            {
                byte* row = ptr + y * step;
                ink = row[x] < 128;
            }

            if (ink)
            {
                start ??= y;
            }
            else if (start.HasValue)
            {
                int y0 = start.Value;
                int y1 = y;
                if (y1 - y0 >= MinVerticalRunPixels)
                    runs.Add(new BlackRun(x, y0, y1));
                start = null;
            }
        }

        return runs;
    }

    private static List<List<GuardBar>> GroupGuardBarsIntoRows(List<GuardBar> guards)
    {
        var rows = new List<List<GuardBar>>();

        foreach (var guard in guards.OrderBy(g => g.YStart).ThenBy(g => g.XStart))
        {
            List<GuardBar>? best = null;
            double bestOverlap = 0.0;

            foreach (var row in rows)
            {
                int rowTop = Median(row.Select(g => g.YStart));
                int rowBottom = Median(row.Select(g => g.YEnd));
                double overlap = OverlapRatio(rowTop, rowBottom, guard.YStart, guard.YEnd);

                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = row;
                }
            }

            if (best != null && bestOverlap >= RowOverlapThreshold)
                best.Add(guard);
            else
                rows.Add(new List<GuardBar> { guard });
        }

        rows = rows
            .Where(r => r.Count >= 2)
            .Select(r => r.OrderBy(g => g.XStart).ToList())
            .OrderBy(r => Median(r.Select(g => g.YStart)))
            .ToList();

        Trace($"[RRVB] Guard rows found: {rows.Count}");
        foreach (var row in rows)
        {
            int top = Median(row.Select(g => g.YStart));
            int bottom = Median(row.Select(g => g.YEnd));
            int left = row.Min(g => g.XStart);
            int right = row.Max(g => g.XEnd);
            int medW = Median(row.Select(g => g.Width));
            Trace($"[RRVB]   Guard row Y={top} H={bottom - top} X={left}..{right} guards={row.Count} medGuardW={medW}");
        }

        return rows;
    }

    private sealed class DecodeState
    {
        public readonly List<Result> Results = new();
        public readonly List<byte> Payload = new();
        public bool InPayload;
        public int Bx1, By1, Bx2, By2;
        public int? ExpectedDataW;
        public int? ExpectedRowHeight;

        public void Reset()
        {
            Payload.Clear();
            InPayload = false;
            Bx1 = By1 = Bx2 = By2 = 0;
            ExpectedDataW = null;
            ExpectedRowHeight = null;
        }

        public void StartBounds(GuardBar left, GuardBar right, int dataW, int rowHeight)
        {
            Bx1 = Math.Min(left.XStart, right.XStart);
            By1 = Math.Min(left.YStart, right.YStart);
            Bx2 = Math.Max(left.XEnd, right.XEnd);
            By2 = Math.Max(left.YEnd, right.YEnd);
            ExpectedDataW = dataW;
            ExpectedRowHeight = rowHeight;
        }

        public void ExpandBounds(GuardBar left, GuardBar right)
        {
            Bx1 = Math.Min(Bx1, Math.Min(left.XStart, right.XStart));
            By1 = Math.Min(By1, Math.Min(left.YStart, right.YStart));
            Bx2 = Math.Max(Bx2, Math.Max(left.XEnd, right.XEnd));
            By2 = Math.Max(By2, Math.Max(left.YEnd, right.YEnd));
        }
    }

    private static List<Result> DecodeGuardRowsRowMajor(Mat gray, List<List<GuardBar>> guardRows, int rows, int cols)
    {
        var state = new DecodeState();

        foreach (var guardRow in guardRows
                     .Where(r => r.Count >= 2)
                     .OrderBy(r => Median(r.Select(g => g.YStart)))
                     .ThenBy(r => r.Min(g => g.XStart)))
        {
            DecodeGuardRowIntoState(gray, guardRow.OrderBy(g => g.XStart).ToList(), rows, cols, state);
        }

        if (state.InPayload)
        {
            Trace($"[RRVB] Wrapped decode ended with unterminated payload bytes={state.Payload.Count}; ignored");
            state.Reset();
        }

        return state.Results;
    }

    private static void DecodeGuardRowIntoState(Mat gray, List<GuardBar> guards, int rows, int cols, DecodeState state)
    {
        if (guards.Count < 2) return;

        int rowTop = Median(guards.Select(g => g.YStart));
        int rowBottom = Median(guards.Select(g => g.YEnd));
        int rowHeight = Math.Max(1, rowBottom - rowTop);
        if (rowHeight < MinVerticalRunPixels) return;

        int guardW = Math.Max(1, Median(guards.Select(g => g.Width)));
        int dataW = EstimateDataCellWidth(guards, guardW, rowHeight);
        if (dataW <= 0)
        {
            Trace($"[RRVB] Reject row Y={rowTop} H={rowHeight} guards={guards.Count}: no usable data cell width");
            return;
        }

        if (state.InPayload && !GeometryCompatible(state, dataW, rowHeight))
        {
            Trace($"[RRVB] Wrapped row geometry mismatch Y={rowTop} H={rowHeight} dataW={dataW}; reset unterminated payloadBytes={state.Payload.Count}");
            state.Reset();
        }

        double minCellW = Math.Max(1, dataW * 0.45);
        double maxCellW = Math.Max(minCellW + 1, dataW * 1.80);

        LogRowCellSummary(gray, guards, rowTop, rowHeight, dataW, minCellW, maxCellW, rows, cols);
        Trace($"[RRVB] Decode row Y={rowTop} H={rowHeight} guards={guards.Count} guardW={guardW} dataW={dataW} cellWRange={minCellW:F1}..{maxCellW:F1} carrying={state.InPayload} carriedBytes={state.Payload.Count}");

        int startMarkers = 0;
        int stopMarkers = 0;
        int decodedCells = 0;
        int skippedBeforeStart = 0;
        int geometryResets = 0;

        for (int i = 0; i < guards.Count - 1; i++)
        {
            GuardBar left = guards[i];
            GuardBar right = guards[i + 1];

            int cellLeft = left.XEnd;
            int cellRight = right.XStart;
            int cellW = cellRight - cellLeft;

            if (cellW <= 0)
                continue;

            bool plausibleCell = cellW >= minCellW && cellW <= maxCellW;
            if (!plausibleCell)
            {
                if (state.InPayload)
                    Trace($"[RRVB] Row Y={rowTop}: reset payload at cell {i} gapW={cellW} outside {minCellW:F1}..{maxCellW:F1} payloadBytes={state.Payload.Count}");
                geometryResets++;
                state.Reset();
                continue;
            }

            var (cellRowTop, cellRowHeight) = RowFromGuardPair(left, right);
            bool emptyCell = !CellHasAnyLaneInk(gray, cellLeft, cellRight - 1, cellRowTop, cellRowHeight, rows, cols);

            if (emptyCell)
            {
                if (!state.InPayload)
                {
                    state.Reset();
                    state.InPayload = true;
                    startMarkers++;
                    state.StartBounds(left, right, dataW, rowHeight);
                    Trace($"[RRVB] Row Y={rowTop}: START cell={i} x={cellLeft}..{cellRight - 1} w={cellW} activeY={cellRowTop} activeH={cellRowHeight}");
                }
                else
                {
                    stopMarkers++;
                    state.ExpandBounds(left, right);
                    Trace($"[RRVB] Row Y={rowTop}: STOP cell={i} x={cellLeft}..{cellRight - 1} w={cellW} payloadBytes={state.Payload.Count}");
                    FinalizePayload(state);
                    state.Reset();
                }

                continue;
            }

            if (!state.InPayload)
            {
                skippedBeforeStart++;
                continue;
            }

            byte value = DecodeDataCell(gray, cellLeft, cellRight - 1, cellRowTop, cellRowHeight, rows, cols);
            state.Payload.Add(value);
            decodedCells++;
            state.ExpandBounds(left, right);

            if (state.Payload.Count <= 12)
                Trace($"[RRVB] Row Y={rowTop}: byte[{state.Payload.Count - 1}] cell={i} x={cellLeft}..{cellRight - 1} w={cellW} activeY={cellRowTop} activeH={cellRowHeight} value=0x{value:X2} '{Printable(value)}'");
        }

        Trace($"[RRVB] Row Y={rowTop}: summary start={startMarkers} stop={stopMarkers} decodedCells={decodedCells} skippedBeforeStart={skippedBeforeStart} geometryResets={geometryResets} results={state.Results.Count} carrying={state.InPayload} carriedBytes={state.Payload.Count}");
    }

    private static bool GeometryCompatible(DecodeState state, int dataW, int rowHeight)
    {
        if (!state.ExpectedDataW.HasValue || !state.ExpectedRowHeight.HasValue)
            return true;

        int expectedDataW = Math.Max(1, state.ExpectedDataW.Value);
        int expectedRowHeight = Math.Max(1, state.ExpectedRowHeight.Value);

        double dataRatio = dataW / (double)expectedDataW;
        double heightRatio = rowHeight / (double)expectedRowHeight;

        return dataRatio >= 0.50 && dataRatio <= 2.00 &&
               heightRatio >= 0.50 && heightRatio <= 2.00;
    }

    private static (int Top, int Height) RowFromGuardPair(GuardBar left, GuardBar right)
    {
        int top = (left.YStart + right.YStart) / 2;
        int bottom = (left.YEnd + right.YEnd) / 2;
        return (top, Math.Max(1, bottom - top));
    }

    private static void FinalizePayload(DecodeState state)
    {
        if (state.Payload.Count == 0) return;

        string text = Encoding.Latin1.GetString(state.Payload.ToArray());
        if (string.IsNullOrEmpty(text)) return;

        var points = new[]
        {
            new ResultPoint(state.Bx1, state.By1),
            new ResultPoint(state.Bx2, state.By1),
            new ResultPoint(state.Bx2, state.By2),
            new ResultPoint(state.Bx1, state.By2),
        };

        string preview = text.Length <= MaxDebugPayloadPreviewChars ? text : text[..MaxDebugPayloadPreviewChars] + "...";
        Trace($"[RRVB] Decoded payload bytes={state.Payload.Count} chars={text.Length} text='{preview}' hex={ToHexPreview(state.Payload)}");
        state.Results.Add(new Result(text, null, points, BarcodeFormat.MSI));
    }

    private static void LogRowCellSummary(
        Mat gray, List<GuardBar> guards, int rowTop, int rowHeight,
        int dataW, double minCellW, double maxCellW, int rows, int cols)
    {
        var widths = new List<int>();
        int plausible = 0;
        int empty = 0;
        int ink = 0;
        int tooSmall = 0;
        int tooLarge = 0;

        int sampleCount = Math.Min(guards.Count - 1, MaxDebugCellsPerRow);
        var sample = new StringBuilder();

        for (int i = 0; i < guards.Count - 1; i++)
        {
            int cellLeft = guards[i].XEnd;
            int cellRight = guards[i + 1].XStart;
            int w = cellRight - cellLeft;
            if (w <= 0) continue;

            widths.Add(w);
            bool plausibleCell = w >= minCellW && w <= maxCellW;
            if (w < minCellW) tooSmall++;
            else if (w > maxCellW) tooLarge++;
            else plausible++;

            bool isEmpty = false;
            if (plausibleCell)
            {
                isEmpty = !CellHasAnyLaneInk(gray, cellLeft, cellRight - 1, rowTop, rowHeight, rows, cols);
                if (isEmpty) empty++; else ink++;
            }

            if (i < sampleCount)
            {
                char kind = !plausibleCell ? (w < minCellW ? 's' : 'L') : (isEmpty ? 'E' : 'D');
                if (sample.Length > 0) sample.Append(' ');
                sample.Append(i).Append(':').Append(w).Append(kind);
            }
        }

        if (widths.Count == 0)
        {
            Trace($"[RRVB] Row Y={rowTop}: no guard-to-guard cells");
            return;
        }

        widths.Sort();
        int min = widths[0];
        int med = widths[widths.Count / 2];
        int max = widths[^1];

        Trace($"[RRVB] Row Y={rowTop}: cells={widths.Count} width min/med/max={min}/{med}/{max} dataW={dataW} plausible={plausible} emptyMarkers={empty} dataLike={ink} tooSmall={tooSmall} tooLarge={tooLarge}");
        Trace($"[RRVB] Row Y={rowTop}: cell sample {sample}");
    }

    private static string ToHexPreview(List<byte> bytes)
    {
        int count = Math.Min(bytes.Count, 32);
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        if (bytes.Count > count) sb.Append(" ...");
        return sb.ToString();
    }

    private static char Printable(byte value)
    {
        return value >= 32 && value <= 126 ? (char)value : '.';
    }

    private static int EstimateDataCellWidth(List<GuardBar> guards, int guardW, int rowHeight)
    {
        var gaps = new List<int>();
        int maxReasonableGap = Math.Max(guardW + 1, rowHeight * 8);

        for (int i = 0; i < guards.Count - 1; i++)
        {
            int gap = guards[i + 1].XStart - guards[i].XEnd;
            if (gap >= Math.Max(1, guardW / 2) && gap <= maxReasonableGap)
                gaps.Add(gap);
        }

        if (gaps.Count == 0) return 0;

        gaps.Sort();
        int index = Math.Clamp(gaps.Count / 4, 0, gaps.Count - 1);
        return gaps[index];
    }

    private static bool CellHasAnyLaneInk(Mat gray, int x0, int x1, int rowTop, int rowHeight, int rows, int cols)
    {
        for (int lane = 0; lane < NumLanes; lane++)
        {
            var (y0, y1) = GetLaneBand(rowTop, rowHeight, lane, rows);
            if (LaneHasInk(gray, x0, x1, y0, y1, cols))
                return true;
        }

        return false;
    }

    private static byte DecodeDataCell(Mat gray, int x0, int x1, int rowTop, int rowHeight, int rows, int cols)
    {
        byte value = 0;

        for (int lane = 0; lane < NumLanes; lane++)
        {
            var (y0, y1) = GetLaneBand(rowTop, rowHeight, lane, rows);
            if (LaneHasInk(gray, x0, x1, y0, y1, cols))
                value |= (byte)(1 << (7 - lane));
        }

        return value;
    }

    private static (int Y0, int Y1) GetLaneBand(int rowTop, int rowHeight, int lane, int rows)
    {
        double slotH = rowHeight / (double)NumLanes;
        int y0 = (int)Math.Round(rowTop + lane * slotH + slotH * LaneBandStartFraction);
        int y1 = (int)Math.Round(rowTop + lane * slotH + slotH * LaneBandEndFraction);

        y0 = Math.Clamp(y0, 0, rows - 1);
        y1 = Math.Clamp(y1, 0, rows - 1);
        if (y1 < y0) (y0, y1) = (y1, y0);
        return (y0, y1);
    }

    private static bool LaneHasInk(Mat gray, int x0, int x1, int y0, int y1, int cols)
    {
        int xa = Math.Clamp(Math.Min(x0, x1), 0, cols - 1);
        int xb = Math.Clamp(Math.Max(x0, x1), 0, cols - 1);

        int minRun = Math.Max(1, (y1 - y0 + 1) / 2);

        unsafe
        {
            byte* ptr = (byte*)gray.DataPointer;
            int step = (int)gray.Step();

            for (int x = xa; x <= xb; x++)
            {
                int run = 0;
                for (int y = y0; y <= y1; y++)
                {
                    byte* row = ptr + y * step;
                    if (row[x] < 128)
                    {
                        run++;
                        if (run >= minRun)
                            return true;
                    }
                    else
                    {
                        run = 0;
                    }
                }
            }
        }

        return false;
    }

    private static double OverlapRatio(int a0, int a1, int b0, int b1)
    {
        int overlap = Math.Min(a1, b1) - Math.Max(a0, b0);
        if (overlap <= 0) return 0.0;

        int minLen = Math.Min(Math.Max(1, a1 - a0), Math.Max(1, b1 - b0));
        return overlap / (double)minLen;
    }

    private static int Median(IEnumerable<int> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
    }
}
