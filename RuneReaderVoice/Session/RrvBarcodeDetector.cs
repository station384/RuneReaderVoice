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

                var results = new List<Result>();
                foreach (var guardRow in guardRows)
                {
                    if (guardRow.Count < 2) continue;

                    var rowResults = DecodeGuardCells(gray, guardRow.OrderBy(g => g.XStart).ToList(), rows, cols);
                    if (rowResults != null)
                        results.AddRange(rowResults);
                }

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

    private static List<Result>? DecodeGuardCells(Mat gray, List<GuardBar> guards, int rows, int cols)
    {
        if (guards.Count < 2) return null;

        int rowTop = Median(guards.Select(g => g.YStart));
        int rowBottom = Median(guards.Select(g => g.YEnd));
        int rowHeight = Math.Max(1, rowBottom - rowTop);
        if (rowHeight < MinVerticalRunPixels) return null;

        int guardW = Math.Max(1, Median(guards.Select(g => g.Width)));
        int dataW = EstimateDataCellWidth(guards, guardW, rowHeight);
        if (dataW <= 0)
        {
            Trace($"[RRVB] Reject row Y={rowTop} H={rowHeight} guards={guards.Count}: no usable data cell width");
            return null;
        }

        double minCellW = Math.Max(1, dataW * 0.45);
        double maxCellW = Math.Max(minCellW + 1, dataW * 1.80);

        LogRowCellSummary(gray, guards, rowTop, rowHeight, dataW, minCellW, maxCellW, rows, cols);
        Trace($"[RRVB] Decode row Y={rowTop} H={rowHeight} guards={guards.Count} guardW={guardW} dataW={dataW} cellWRange={minCellW:F1}..{maxCellW:F1}");

        var results = new List<Result>();
        var payload = new List<byte>();
        bool inPayload = false;
        int bx1 = 0, by1 = 0, bx2 = 0, by2 = 0;
        int activeRowTop = rowTop;
        int activeRowHeight = rowHeight;

        void StartBounds(GuardBar left, GuardBar right)
        {
            bx1 = Math.Min(left.XStart, right.XStart);
            by1 = Math.Min(left.YStart, right.YStart);
            bx2 = Math.Max(left.XEnd, right.XEnd);
            by2 = Math.Max(left.YEnd, right.YEnd);
        }

        void ExpandBounds(GuardBar left, GuardBar right)
        {
            bx1 = Math.Min(bx1, Math.Min(left.XStart, right.XStart));
            by1 = Math.Min(by1, Math.Min(left.YStart, right.YStart));
            bx2 = Math.Max(bx2, Math.Max(left.XEnd, right.XEnd));
            by2 = Math.Max(by2, Math.Max(left.YEnd, right.YEnd));
        }

        void ResetState()
        {
            payload.Clear();
            inPayload = false;
            bx1 = by1 = bx2 = by2 = 0;
            activeRowTop = rowTop;
            activeRowHeight = rowHeight;
        }

        static (int Top, int Height) RowFromGuardPair(GuardBar left, GuardBar right)
        {
            int top = (left.YStart + right.YStart) / 2;
            int bottom = (left.YEnd + right.YEnd) / 2;
            return (top, Math.Max(1, bottom - top));
        }

        void FinalizePayload()
        {
            if (payload.Count == 0) return;

            string text = Encoding.Latin1.GetString(payload.ToArray());
            if (string.IsNullOrEmpty(text)) return;

            var points = new[]
            {
                new ResultPoint(bx1, by1),
                new ResultPoint(bx2, by1),
                new ResultPoint(bx2, by2),
                new ResultPoint(bx1, by2),
            };

            string preview = text.Length <= MaxDebugPayloadPreviewChars ? text : text[..MaxDebugPayloadPreviewChars] + "...";
            Trace($"[RRVB] Decoded payload bytes={payload.Count} chars={text.Length} text='{preview}' hex={ToHexPreview(payload)}");
            results.Add(new Result(text, null, points, BarcodeFormat.MSI));
        }

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
                if (inPayload)
                    Trace($"[RRVB] Row Y={rowTop}: reset payload at cell {i} gapW={cellW} outside {minCellW:F1}..{maxCellW:F1} payloadBytes={payload.Count}");
                geometryResets++;
                ResetState();
                continue;
            }

            var cellRow = inPayload ? (activeRowTop, activeRowHeight) : RowFromGuardPair(left, right);
            bool emptyCell = !CellHasAnyLaneInk(gray, cellLeft, cellRight - 1, cellRow.Item1, cellRow.Item2, rows, cols);

            if (emptyCell)
            {
                if (!inPayload)
                {
                    ResetState();
                    inPayload = true;
                    startMarkers++;
                    (activeRowTop, activeRowHeight) = RowFromGuardPair(left, right);
                    StartBounds(left, right);
                    Trace($"[RRVB] Row Y={rowTop}: START cell={i} x={cellLeft}..{cellRight - 1} w={cellW} activeY={activeRowTop} activeH={activeRowHeight}");
                }
                else
                {
                    stopMarkers++;
                    ExpandBounds(left, right);
                    Trace($"[RRVB] Row Y={rowTop}: STOP cell={i} x={cellLeft}..{cellRight - 1} w={cellW} payloadBytes={payload.Count}");
                    FinalizePayload();
                    ResetState();
                }

                continue;
            }

            if (!inPayload)
            {
                skippedBeforeStart++;
                continue;
            }

            byte value = DecodeDataCell(gray, cellLeft, cellRight - 1, activeRowTop, activeRowHeight, rows, cols);
            payload.Add(value);
            decodedCells++;
            ExpandBounds(left, right);

            if (payload.Count <= 12)
                Trace($"[RRVB] Row Y={rowTop}: byte[{payload.Count - 1}] cell={i} x={cellLeft}..{cellRight - 1} w={cellW} activeY={activeRowTop} activeH={activeRowHeight} value=0x{value:X2} '{Printable(value)}'");
        }

        Trace($"[RRVB] Row Y={rowTop}: summary start={startMarkers} stop={stopMarkers} decodedCells={decodedCells} skippedBeforeStart={skippedBeforeStart} geometryResets={geometryResets} results={results.Count}");

        return results.Count > 0 ? results : null;
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
