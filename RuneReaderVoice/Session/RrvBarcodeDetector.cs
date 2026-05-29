// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

using System;
using System.Buffers;
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
/// v8 compact bounded model:
///   - Normal glyph: left full-height guard bar + 8-lane data cell + right full-height guard bar.
///   - Start/stop glyph: left full-height guard bar + empty data cell + right full-height guard bar.
///   - Glyphs are decoded as cells between adjacent guard spans; adjacent glyph guards may merge into a wider shared guard span.
///   - Empty cell = start/stop marker.
///   - Data cell = one Latin-1 byte, MSB in top lane.
///
/// Input is expected to be a pre-thresholded image where barcode ink is black
/// and the barcode background / quiet zone is white.
///
/// Decoder is generic: it does not filter for RRVG-/RRVN-. Caller owns payload filtering.
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
    private const int MaxWrappedRowGapPixels = 10;
    private const int MinContinuationDataRunCells = 3;

    // A valid v8 data cell is bounded by guards, but must not contain a
    // guard-like vertical run inside the data zone. If it does, the cell is
    // almost certainly a seam/merged-guard artifact from a wrapped row.
    private const double InternalGuardRunThreshold = 0.70;
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

                var results = DecodeRowsAsBlocks(gray, guardRows, rows, cols);
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

    private sealed class GuardRow
    {
        public readonly List<GuardBar> Guards;
        public readonly int Top;
        public readonly int Bottom;
        public readonly int Height;
        public readonly int GuardW;
        public readonly int DataW;
        public readonly double MinCellW;
        public readonly double MaxCellW;

        public GuardRow(List<GuardBar> guards)
        {
            Guards = guards.OrderBy(g => g.XStart).ToList();
            Top = Median(Guards.Select(g => g.YStart));
            Bottom = Median(Guards.Select(g => g.YEnd));
            Height = Math.Max(1, Bottom - Top);
            GuardW = Math.Max(1, Median(Guards.Select(g => g.Width)));
            DataW = EstimateDataCellWidth(Guards, GuardW, Height);
            MinCellW = Math.Max(1, DataW * 0.45);
            MaxCellW = Math.Max(MinCellW + 1, DataW * 1.80);
        }

        public bool Usable => Guards.Count >= 2 && Height >= MinVerticalRunPixels && DataW > 0;
    }



    private readonly struct BlockBounds
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public BlockBounds(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public static BlockBounds FromResult(Result result)
        {
            var points = result.ResultPoints ?? Array.Empty<ResultPoint>();
            if (points.Length == 0)
                return new BlockBounds(0, 0, -1, -1);

            int left = (int)Math.Floor(points.Min(p => p.X));
            int right = (int)Math.Ceiling(points.Max(p => p.X));
            int top = (int)Math.Floor(points.Min(p => p.Y));
            int bottom = (int)Math.Ceiling(points.Max(p => p.Y));
            return new BlockBounds(left, top, right, bottom);
        }

        public bool ContainsCell(GuardRow row, int cellIndex)
        {
            var leftGuard = row.Guards[cellIndex];
            var rightGuard = row.Guards[cellIndex + 1];
            int cx = (leftGuard.XStart + rightGuard.XEnd) / 2;
            int cy = (row.Top + row.Bottom) / 2;

            // Small expansion handles 1-2 px antialias drift at wrapped row boundaries.
            return cx >= Left - 2 && cx <= Right + 2 &&
                   cy >= Top - 2 && cy <= Bottom + 2;
        }
    }

    private readonly struct CellBox
    {
        public readonly int Left;
        public readonly int Right;
        public readonly int Top;
        public readonly int Height;
        public readonly int Bottom;

        public CellBox(int left, int right, int top, int bottom)
        {
            Left = left;
            Right = right;
            Top = top;
            Bottom = bottom;
            Height = Math.Max(1, bottom - top);
        }
    }

    private static List<Result> DecodeRowsAsBlocks(Mat gray, List<List<GuardBar>> guardRows, int rows, int cols)
    {
        var rowInfos = guardRows
            .Select(r => new GuardRow(r))
            .Where(r => r.Usable)
            .OrderBy(r => r.Top)
            .ThenBy(r => r.Guards.Min(g => g.XStart))
            .ToList();

        var results = new List<Result>();
        var consumedBlocks = new List<BlockBounds>();

        for (int rowIndex = 0; rowIndex < rowInfos.Count; rowIndex++)
        {
            var row = rowInfos[rowIndex];
            for (int cellIndex = 0; cellIndex < row.Guards.Count - 1; cellIndex++)
            {
                if (!IsMarkerCell(gray, row, cellIndex, rows, cols))
                    continue;

                if (IsCellInsideConsumedBlock(row, cellIndex, consumedBlocks))
                {
                    Trace($"[RRVB] Skip marker inside consumed wrapped block row={rowIndex} cell={cellIndex}");
                    continue;
                }

                var result = DecodeBlockFromStart(gray, rowInfos, rowIndex, cellIndex, rows, cols);
                if (result != null)
                {
                    results.Add(result);
                    consumedBlocks.Add(BlockBounds.FromResult(result));
                }
            }
        }

        return results;
    }


    private static bool IsCellInsideConsumedBlock(GuardRow row, int cellIndex, List<BlockBounds> consumedBlocks)
    {
        foreach (var block in consumedBlocks)
        {
            if (block.ContainsCell(row, cellIndex))
                return true;
        }

        return false;
    }

    private static Result? DecodeBlockFromStart(Mat gray, List<GuardRow> rowsInfo, int startRowIndex, int startCellIndex, int rows, int cols)
    {
        var payload = new List<byte>();
        var startRow = rowsInfo[startRowIndex];

        int blockLeft = startRow.Guards[startCellIndex].XStart;
        int blockRight = startRow.Guards[startCellIndex + 1].XEnd;
        int blockTop = Math.Min(startRow.Guards[startCellIndex].YStart, startRow.Guards[startCellIndex + 1].YStart);
        int blockBottom = Math.Max(startRow.Guards[startCellIndex].YEnd, startRow.Guards[startCellIndex + 1].YEnd);
        int lastRowBottom = startRow.Bottom;
        int refDataW = startRow.DataW;
        int refHeight = startRow.Height;

        Trace($"[RRVB] START block row={startRowIndex} cell={startCellIndex} Y={startRow.Top} dataW={refDataW}");

        for (int rowIndex = startRowIndex; rowIndex < rowsInfo.Count; rowIndex++)
        {
            var row = rowsInfo[rowIndex];

            if (rowIndex > startRowIndex)
            {
                int gap = row.Top - lastRowBottom;
                if (gap < 0 || gap > MaxWrappedRowGapPixels)
                    break;

                if (!LooksLikeContinuationRow(row, refDataW, refHeight))
                    break;
            }

            int firstCell;
            if (rowIndex == startRowIndex)
            {
                firstCell = startCellIndex + 1;
            }
            else
            {
                // v8 bounded glyphs are self-bounded: [guard][data][guard].
                // A wrapped continuation row should begin with a normal data cell,
                // but full-screen detection can see a short preamble of guard-like
                // noise before the actual wrapped line. Since one font instance has
                // fixed data-cell width, prefer the first sustained run of data-like
                // cells and ignore short preambles before it.
                firstCell = FindContinuationStartCell(gray, row, rows, cols);
                if (firstCell < 0)
                    break;
            }

            bool continueWithNextRow = false;

            for (int cellIndex = firstCell; cellIndex < row.Guards.Count - 1; cellIndex++)
            {
                int cellW = CellWidth(row, cellIndex);
                if (!IsPlausibleCellWidth(row, cellW))
                    continue;

                var cell = GetCellBox(row, cellIndex);

                if (CellContainsInternalGuardRun(gray, cell.Left, cell.Right, cell.Top, cell.Height, rows, cols))
                {
                    Trace($"[RRVB] Skip seam/internal-guard cell row={rowIndex} cell={cellIndex}");
                    continue;
                }

                bool empty = !CellHasAnyLaneInk(gray, cell.Left, cell.Right, cell.Top, cell.Height, rows, cols);
                if (empty)
                {
                    if (payload.Count == 0)
                    {
                        Trace($"[RRVB] Reject empty block row={rowIndex} cell={cellIndex}");
                        return null;
                    }

                    // Wrapped rows can produce a false empty marker at the line seam when
                    // antialiasing drops a guard/data cell at the row edge. If another
                    // compatible row is immediately below, prefer continuing the block
                    // instead of prematurely finalizing at this row.
                    if (HasCompatibleContinuationRow(rowsInfo, rowIndex, row.Bottom, refDataW, refHeight))
                    {
                        Trace($"[RRVB] Ignore seam empty row={rowIndex} cell={cellIndex}; continuing wrapped block");
                        continueWithNextRow = true;
                        break;
                    }

                    // If the first cell of a continuation row looks empty, treat it as a
                    // row-start seam artifact, not a stop marker. The next cells may still
                    // carry the continuation payload.
                    if (rowIndex > startRowIndex && cellIndex == 0)
                    {
                        Trace($"[RRVB] Skip continuation row-start empty row={rowIndex} cell={cellIndex}");
                        continue;
                    }

                    blockRight = Math.Max(blockRight, row.Guards[cellIndex + 1].XEnd);
                    blockTop = Math.Min(blockTop, Math.Min(row.Guards[cellIndex].YStart, row.Guards[cellIndex + 1].YStart));
                    blockBottom = Math.Max(blockBottom, Math.Max(row.Guards[cellIndex].YEnd, row.Guards[cellIndex + 1].YEnd));

                    return MakeResult(payload, blockLeft, blockTop, blockRight, blockBottom);
                }

                byte value = DecodeDataCell(gray, cell.Left, cell.Right, cell.Top, cell.Height, rows, cols);
                payload.Add(value);

                blockLeft = Math.Min(blockLeft, row.Guards[cellIndex].XStart);
                blockRight = Math.Max(blockRight, row.Guards[cellIndex + 1].XEnd);
                blockTop = Math.Min(blockTop, Math.Min(row.Guards[cellIndex].YStart, row.Guards[cellIndex + 1].YStart));
                blockBottom = Math.Max(blockBottom, Math.Max(row.Guards[cellIndex].YEnd, row.Guards[cellIndex + 1].YEnd));

                if (payload.Count <= 12)
                    Trace($"[RRVB] byte[{payload.Count - 1}] row={rowIndex} cell={cellIndex} value=0x{value:X2} '{Printable(value)}'");
            }

            lastRowBottom = row.Bottom;
            if (continueWithNextRow)
                continue;
        }

        Trace($"[RRVB] Unclosed block startRow={startRowIndex} startCell={startCellIndex} payloadBytes={payload.Count}");
        return null;
    }


    private static int FindContinuationStartCell(Mat gray, GuardRow row, int rows, int cols)
    {
        int firstNonEmpty = -1;
        int runStart = -1;
        int runLength = 0;

        for (int cellIndex = 0; cellIndex < row.Guards.Count - 1; cellIndex++)
        {
            int cellW = CellWidth(row, cellIndex);
            if (!IsPlausibleCellWidth(row, cellW))
            {
                if (runLength >= MinContinuationDataRunCells)
                    return runStart;
                runStart = -1;
                runLength = 0;
                continue;
            }

            var cell = GetCellBox(row, cellIndex);
            bool hasInk = CellHasAnyLaneInk(gray, cell.Left, cell.Right, cell.Top, cell.Height, rows, cols);
            if (!hasInk)
            {
                if (runLength >= MinContinuationDataRunCells)
                    return runStart;
                runStart = -1;
                runLength = 0;
                continue;
            }

            firstNonEmpty = firstNonEmpty < 0 ? cellIndex : firstNonEmpty;
            if (runStart < 0)
            {
                runStart = cellIndex;
                runLength = 1;
            }
            else
            {
                runLength++;
            }
        }

        if (runLength >= MinContinuationDataRunCells)
            return runStart;

        return firstNonEmpty;
    }


    private static bool HasCompatibleContinuationRow(List<GuardRow> rowsInfo, int currentRowIndex, int lastRowBottom, int refDataW, int refHeight)
    {
        // Rows are ordered top-to-bottom. Once a row is beyond the gap
        // threshold, no further rows can be in range — stop scanning.
        for (int i = currentRowIndex + 1; i < rowsInfo.Count; i++)
        {
            var row = rowsInfo[i];
            int gap = row.Top - lastRowBottom;
            if (gap < 0)
                continue;
            if (gap > MaxWrappedRowGapPixels)
                break;

            if (LooksLikeContinuationRow(row, refDataW, refHeight))
                return true;
        }

        return false;
    }

    private static bool LooksLikeContinuationRow(GuardRow row, int refDataW, int refHeight)
    {
        if (!row.Usable) return false;

        double heightRatio = row.Height / (double)Math.Max(1, refHeight);
        if (heightRatio < 0.65 || heightRatio > 1.55)
            return false;

        double dataRatio = row.DataW / (double)Math.Max(1, refDataW);
        if (dataRatio < 0.60 || dataRatio > 1.70)
            return false;

        // Wrapped v8 rows are self-bounded. Do not require exact X alignment:
        // FontString wrapping can shift the continuation line. Row proximity +
        // similar height/spacing is the reliable block-continuation signal.
        return true;
    }

    private static Result MakeResult(List<byte> payload, int x1, int y1, int x2, int y2)
    {
        string text = Encoding.Latin1.GetString(payload.ToArray());
        var points = new[]
        {
            new ResultPoint(x1, y1),
            new ResultPoint(x2, y1),
            new ResultPoint(x2, y2),
            new ResultPoint(x1, y2),
        };

        string preview = text.Length <= MaxDebugPayloadPreviewChars ? text : text[..MaxDebugPayloadPreviewChars] + "...";
        Trace($"[RRVB] Decoded payload bytes={payload.Count} chars={text.Length} text='{preview}' hex={ToHexPreview(payload)}");
        return new Result(text, null, points, BarcodeFormat.MSI);
    }

    private static bool IsMarkerCell(Mat gray, GuardRow row, int cellIndex, int rows, int cols)
    {
        int cellW = CellWidth(row, cellIndex);
        if (!IsPlausibleCellWidth(row, cellW))
            return false;

        var cell = GetCellBox(row, cellIndex);
        return !CellHasAnyLaneInk(gray, cell.Left, cell.Right, cell.Top, cell.Height, rows, cols);
    }

    private static int CellWidth(GuardRow row, int cellIndex)
    {
        return row.Guards[cellIndex + 1].XStart - row.Guards[cellIndex].XEnd;
    }

    private static CellBox GetCellBox(GuardRow row, int cellIndex)
    {
        var leftGuard = row.Guards[cellIndex];
        var rightGuard = row.Guards[cellIndex + 1];

        int left = leftGuard.XEnd;
        int right = rightGuard.XStart - 1;

        // Decode from the local guard-pair height instead of the row median.
        // Wrapped rows can have a slightly different antialiasing envelope, and
        // a single noisy/tall guard must not move the lane sample bands for the
        // whole row.
        int top = Math.Min(leftGuard.YStart, rightGuard.YStart);
        int bottom = Math.Max(leftGuard.YEnd, rightGuard.YEnd);

        // If one side is a merged inter-glyph guard, its Y can be a pixel or two
        // taller. Clamp extreme local height drift back toward the row median.
        int localHeight = Math.Max(1, bottom - top);
        if (localHeight > row.Height * 1.35 || localHeight < row.Height * 0.65)
        {
            top = row.Top;
            bottom = row.Bottom;
        }

        return new CellBox(left, right, top, bottom);
    }

    private static bool IsPlausibleCellWidth(GuardRow row, int cellW)
    {
        return cellW >= row.MinCellW && cellW <= row.MaxCellW;
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

    private static bool CellContainsInternalGuardRun(Mat gray, int x0, int x1, int rowTop, int rowHeight, int rows, int cols)
    {
        int xa = Math.Clamp(Math.Min(x0, x1), 0, cols - 1);
        int xb = Math.Clamp(Math.Max(x0, x1), 0, cols - 1);
        int ya = Math.Clamp(rowTop, 0, rows - 1);
        int yb = Math.Clamp(rowTop + rowHeight - 1, 0, rows - 1);
        int minRun = Math.Max(2, (int)Math.Round((yb - ya + 1) * InternalGuardRunThreshold));

        for (int x = xa; x <= xb; x++)
        {
            int run = 0;
            for (int y = ya; y <= yb; y++)
            {
                if (gray.At<byte>(y, x) < 128)
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

        return false;
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

    private static double OverlapRatio(int a0, int a1, int b0, int b1)
    {
        int overlap = Math.Min(a1, b1) - Math.Max(a0, b0);
        if (overlap <= 0) return 0.0;

        int minLen = Math.Min(Math.Max(1, a1 - a0), Math.Max(1, b1 - b0));
        return overlap / (double)minLen;
    }

    private static int Median(IEnumerable<int> values)
    {
        var rented = ArrayPool<int>.Shared.Rent(16);
        var count = 0;
        try
        {
            foreach (var value in values)
            {
                if (count == rented.Length)
                {
                    var larger = ArrayPool<int>.Shared.Rent(rented.Length * 2);
                    Array.Copy(rented, larger, count);
                    ArrayPool<int>.Shared.Return(rented);
                    rented = larger;
                }

                rented[count++] = value;
            }

            if (count == 0)
                return 0;

            Array.Sort(rented, 0, count);
            return rented[count / 2];
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }
}
