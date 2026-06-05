// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Linq;
using System.Text;
using OpenCvSharp;
using ZXing;
using RuneReaderVoice.Diagnostics;

namespace RuneReaderVoice.Session;

/// <summary>
/// OpenCV geometry decoder for RuneReader Barcode font v10.
///
/// Font model:
///   - Data glyph advance: 250 em. Drawn glyph width: 300 em.
///   - Data glyph: 50-em left guard + 200-em 8-lane data zone + 50-em right guard.
///   - Adjacent glyph guards overlap exactly, producing one solid 50-em boundary span.
///   - Start/stop glyph (§): sentinel with left + center + right full-height vertical bars.
///   - Data payload is Latin-1 bytes, MSB in top lane.
///
/// Decoder is generic. It emits decoded text only; caller owns application payload parsing.
/// </summary>
public static class RrvBarcodeDetector
{
    private const int NumLanes = 8;
    private const int RrvbBinaryThreshold = 10;
    private const int MinVerticalRunPixels = 4;
    private const int GuardOpenKernelHeight = 3;
    private const double RowOverlapThreshold = 0.55;
    private const double MinBarHeightToWidthRatio = 1.20;
    private const double LanePeakInkFraction = 0.42;
    private const int MaxDebugPayloadPreviewChars = 80;
    private const string ExpectedPayloadPrefix = "RRVX-";
    private static readonly double[] DecodeYPhases = { -0.35, -0.20, 0.0, 0.20, 0.35 };
    private static readonly double[] DecodeXFractions = { 0.35, 0.50, 0.65 };
    private const double LogicalAdvance = 250.0;
    private const double LogicalGuardWidth = 50.0;
    private const double LogicalDataWidth = 200.0;

    private const bool DebugDumpImages = false;
    private const int DebugDumpMaxImages = 240;
    private static readonly string DebugDumpDir = Path.Combine(AppContext.BaseDirectory, "rrvb-debug");
    private static int _debugDumpImageId;
    private static int _debugDumpPathLogged;

    private static void Trace(string message)
    {
        RrvDebug.RrvbDebug(message);
    }

    private static bool ReserveDebugDump(out int id)
    {
        id = 0;
        if (!DebugDumpImages)
            return false;

        int next = Interlocked.Increment(ref _debugDumpImageId);
        if (next > DebugDumpMaxImages)
            return false;

        id = next;
        return true;
    }

    private static void DumpDebugMat(int id, string name, Mat mat)
    {
        if (!DebugDumpImages || id <= 0 || mat.Empty())
            return;

        try
        {
            Directory.CreateDirectory(DebugDumpDir);

            if (Interlocked.Exchange(ref _debugDumpPathLogged, 1) == 0)
                Trace($"[RRVB] Debug image dir: {DebugDumpDir}");

            string safeName = new string(name.Select(c =>
                char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());

            string path = Path.Combine(DebugDumpDir, $"{id:0000}_{safeName}.png");
            Cv2.ImWrite(path, mat);
        }
        catch (Exception ex)
        {
            Trace($"[RRVB] Debug image dump failed: {ex.Message}");
        }
    }

    private static void DumpBarsOverlay(int id, string name, Mat source, IReadOnlyCollection<BarSpan> bars)
    {
        if (!DebugDumpImages || id <= 0 || source.Empty())
            return;

        try
        {
            using var overlay = new Mat();
            if (source.Channels() == 1)
                Cv2.CvtColor(source, overlay, ColorConversionCodes.GRAY2BGR);
            else
                source.CopyTo(overlay);

            foreach (var bar in bars)
            {
                var rect = new Rect(bar.XStart, bar.YStart, Math.Max(1, bar.Width), Math.Max(1, bar.Height));
                Cv2.Rectangle(overlay, rect, Scalar.Red, 1);
            }

            DumpDebugMat(id, name, overlay);
        }
        catch (Exception ex)
        {
            Trace($"[RRVB] Debug overlay dump failed: {ex.Message}");
        }
    }

    private static void DumpRowsOverlay(int id, string name, Mat source, IReadOnlyCollection<BarRow> rows)
    {
        if (!DebugDumpImages || id <= 0 || source.Empty())
            return;

        try
        {
            using var overlay = new Mat();
            if (source.Channels() == 1)
                Cv2.CvtColor(source, overlay, ColorConversionCodes.GRAY2BGR);
            else
                source.CopyTo(overlay);

            foreach (var row in rows)
            {
                int left = Math.Clamp(row.Bars.Min(b => b.XStart), 0, source.Cols - 1);
                int right = Math.Clamp(row.Bars.Max(b => b.XEnd), 0, source.Cols - 1);
                int top = Math.Clamp(row.Top, 0, source.Rows - 1);
                int bottom = Math.Clamp(row.Bottom, 0, source.Rows - 1);
                Cv2.Rectangle(overlay, new Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)), Scalar.Lime, 1);
            }

            DumpDebugMat(id, name, overlay);
        }
        catch (Exception ex)
        {
            Trace($"[RRVB] Debug row overlay dump failed: {ex.Message}");
        }
    }

    public static Result[]? Detect(Mat frame)
    {
        if (frame == null || frame.Empty()) return null;

        Mat? gray = null;
        Mat? binary = null;
        Mat? guardMask = null;

        try
        {
            gray = frame.Channels() == 1 ? frame : new Mat();
            if (frame.Channels() != 1)
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

            if (gray.Rows < 4 || gray.Cols < 4)
                return null;

            Trace($"[RRVB] Detect frame rows={gray.Rows} cols={gray.Cols}");
            bool dumpThisFrame = ReserveDebugDump(out int debugDumpId);
            if (dumpThisFrame)
                DumpDebugMat(debugDumpId, "00_crop", frame);

            binary = new Mat();
            Cv2.Threshold(gray, binary, RrvbBinaryThreshold, 255, ThresholdTypes.BinaryInv);
            if (dumpThisFrame)
                DumpDebugMat(debugDumpId, "01_binary", binary);

            int binaryInk = Cv2.CountNonZero(binary);
            Trace($"[RRVB] Binary ink pixels: {binaryInk}");

            guardMask = BuildGuardMask(binary);
            if (dumpThisFrame)
                DumpDebugMat(debugDumpId, "02_guardmask", guardMask);

            int guardInk = Cv2.CountNonZero(guardMask);
            Trace($"[RRVB] Guard mask pixels: {guardInk}");

            var bars = FindVerticalBars(guardMask, gray.Rows, gray.Cols, "guardMask");
            if (dumpThisFrame)
                DumpBarsOverlay(debugDumpId, "03_guardmask_bars", frame, bars);
            if (bars.Count < 3)
            {
                // Fallback: use threshold mask directly. Some WoW-rendered crops already contain
                // usable vertical guard components, and morphology-open can erase them at small scale.
                var directBars = FindVerticalBars(binary, gray.Rows, gray.Cols, "binary");
                if (dumpThisFrame)
                    DumpBarsOverlay(debugDumpId, "04_binary_bars", frame, directBars);

                if (directBars.Count > bars.Count)
                    bars = directBars;
            }

            if (bars.Count < 3)
            {
                Trace($"[RRVB] Reject frame: only {bars.Count} vertical bar candidate(s)");
                return null;
            }

            var rows = GroupBarsIntoRows(bars);
            if (dumpThisFrame)
                DumpRowsOverlay(debugDumpId, "05_rows", frame, rows);

            if (rows.Count == 0)
                return null;

            var results = DecodeRows(binary, rows, gray.Rows, gray.Cols);
            return results.Count > 0 ? results.ToArray() : null;
        }
        catch (Exception ex)
        {
            Trace($"[RRVB] Detect failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (frame.Channels() != 1)
                gray?.Dispose();
            binary?.Dispose();
            guardMask?.Dispose();
        }
    }

    private static Mat BuildGuardMask(Mat binary)
    {
        var mask = new Mat();

        // RRVB glyphs are small on screen. Do not scale this from crop height;
        // locked regions include padding and can erase real 13pt guard bars.
        using var verticalKernel = Cv2.GetStructuringElement(
            MorphShapes.Rect,
            new Size(1, GuardOpenKernelHeight));
        Cv2.MorphologyEx(binary, mask, MorphTypes.Open, verticalKernel);

        // Fill tiny antialias cracks in vertical bars without joining separate bars.
        using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 1));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, closeKernel);
        return mask;
    }

    private readonly struct BarSpan
    {
        public readonly int XStart, XEnd, YStart, YEnd;
        public int Width => XEnd - XStart;
        public int Height => YEnd - YStart;
        public int CenterX => (XStart + XEnd) / 2;
        public int CenterY => (YStart + YEnd) / 2;

        public BarSpan(int xStart, int xEnd, int yStart, int yEnd)
        {
            XStart = xStart;
            XEnd = xEnd;
            YStart = yStart;
            YEnd = yEnd;
        }
    }

    private sealed class BarRow
    {
        public readonly List<BarSpan> Bars;
        public readonly int Top;
        public readonly int Bottom;
        public readonly int Height;
        public readonly int DataGap;

        public BarRow(List<BarSpan> bars)
        {
            Bars = bars.OrderBy(b => b.XStart).ToList();
            Top = Median(Bars.Select(b => b.YStart));
            Bottom = Median(Bars.Select(b => b.YEnd));
            Height = Math.Max(1, Bottom - Top);
            DataGap = EstimateDataGap(Bars, Height);
        }

        public bool Usable => Bars.Count >= 3 && Height >= MinVerticalRunPixels * 2 && DataGap > 0;
    }

    private static List<BarSpan> FindVerticalBars(Mat sourceMask, int rows, int cols, string sourceName)
    {
        Cv2.FindContours(sourceMask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        var bars = new List<BarSpan>();
        foreach (var contour in contours)
        {
            var r = Cv2.BoundingRect(contour);
            if (r.Height < MinVerticalRunPixels || r.Width <= 0)
                continue;

            // Ignore crop borders and large UI panels. Real RRVB guards are tall relative to width,
            // but they are not the whole candidate rectangle.
            if (r.Width > cols * 0.35 || r.Height > rows * 0.90)
                continue;

            if (r.Height < Math.Max(MinVerticalRunPixels, r.Width * MinBarHeightToWidthRatio))
                continue;

            bars.Add(new BarSpan(r.Left, r.Right, r.Top, r.Bottom));
        }

        bars = bars
            .OrderBy(b => b.YStart)
            .ThenBy(b => b.XStart)
            .ToList();

        Trace($"[RRVB] Vertical bar candidates ({sourceName}): {bars.Count}");
        if (bars.Count > 0)
        {
            var preview = string.Join(", ", bars.Take(12).Select(b => $"({b.XStart},{b.YStart},{b.Width},{b.Height})"));
            Trace($"[RRVB]   {sourceName} bar preview: {preview}");
        }
        return bars;
    }

    private static List<BarRow> GroupBarsIntoRows(List<BarSpan> bars)
    {
        var groups = new List<List<BarSpan>>();

        foreach (var bar in bars.OrderBy(b => b.YStart).ThenBy(b => b.XStart))
        {
            List<BarSpan>? best = null;
            double bestOverlap = 0.0;

            foreach (var group in groups)
            {
                int top = Median(group.Select(b => b.YStart));
                int bottom = Median(group.Select(b => b.YEnd));
                double overlap = OverlapRatio(top, bottom, bar.YStart, bar.YEnd);
                if (overlap > bestOverlap)
                {
                    bestOverlap = overlap;
                    best = group;
                }
            }

            if (best != null && bestOverlap >= RowOverlapThreshold)
                best.Add(bar);
            else
                groups.Add(new List<BarSpan> { bar });
        }

        var rows = groups
            .Select(g => new BarRow(g))
            .Where(r => r.Usable)
            .OrderBy(r => r.Top)
            .ThenBy(r => r.Bars.Min(b => b.XStart))
            .ToList();

        Trace($"[RRVB] Bar rows found: {rows.Count}");
        foreach (var row in rows)
        {
            int left = row.Bars.Min(b => b.XStart);
            int right = row.Bars.Max(b => b.XEnd);
            int medW = Median(row.Bars.Select(b => b.Width));
            Trace($"[RRVB]   Row Y={row.Top} H={row.Height} X={left}..{right} bars={row.Bars.Count} medBarW={medW} dataGap={row.DataGap}");
        }

        return rows;
    }

    private static int EstimateDataGap(List<BarSpan> bars, int rowHeight)
    {
        var gaps = new List<int>();
        for (int i = 0; i < bars.Count - 1; i++)
        {
            int gap = bars[i + 1].XStart - bars[i].XEnd;
            if (gap <= 0)
                continue;

            // Reject huge unrelated UI gaps, but keep marker gaps and data gaps.
            if (gap <= Math.Max(rowHeight * 16, 8))
                gaps.Add(gap);
        }

        if (gaps.Count == 0)
            return 0;

        gaps.Sort();
        // Data gaps are the larger gap class. Marker gaps are smaller.
        int index = Math.Clamp((int)Math.Round((gaps.Count - 1) * 0.70), 0, gaps.Count - 1);
        return gaps[index];
    }

    private static List<Result> DecodeRows(Mat binary, List<BarRow> rows, int imageRows, int imageCols)
    {
        var results = new List<Result>();

        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            for (int barIndex = 0; barIndex <= row.Bars.Count - 3; barIndex++)
            {
                if (!IsMarkerAt(row, barIndex))
                    continue;

                var result = DecodeFromStartMarker(binary, rows, rowIndex, barIndex, imageRows, imageCols);
                if (result != null)
                {
                    results.Add(result);

                    // A closed RRVB block is complete. Do not continue scanning this
                    // frame or the stop marker can be re-read as a fresh start marker,
                    // producing harmless but noisy "Unclosed block" traces.
                    Trace("[RRVB] Closed block decoded; stopping frame scan");
                    return results;
                }
            }
        }

        return results;
    }

    private readonly struct DecodeCandidate
    {
        public readonly List<byte> Payload;
        public readonly int X1, Y1, X2, Y2;
        public readonly double YPhase;
        public readonly double XFraction;
        public readonly int Score;
        public readonly bool IsProtocolValid;
        public readonly string Text;

        public DecodeCandidate(List<byte> payload, int x1, int y1, int x2, int y2, double yPhase, double xFraction)
        {
            Payload = payload;
            X1 = x1;
            Y1 = y1;
            X2 = x2;
            Y2 = y2;
            YPhase = yPhase;
            XFraction = xFraction;
            Text = Encoding.Latin1.GetString(payload.ToArray());
            IsProtocolValid = LooksLikeRrvxPayload(Text);
            Score = ScorePayload(Text);
        }
    }

    private static Result? DecodeFromStartMarker(Mat binary, List<BarRow> rows, int startRowIndex, int startMarkerBarIndex, int imageRows, int imageCols)
    {
        var startRow = rows[startRowIndex];
        Trace($"[RRVB] START marker row={startRowIndex} bar={startMarkerBarIndex} Y={startRow.Top} dataGap={startRow.DataGap}");

        DecodeCandidate? best = null;
        DecodeCandidate? bestHighBitNormalized = null;

        foreach (double yPhase in DecodeYPhases)
        {
            foreach (double xFraction in DecodeXFractions)
            {
                var candidate = DecodeFromStartMarkerCore(binary, rows, startRowIndex, startMarkerBarIndex, imageRows, imageCols, yPhase, xFraction);
                if (candidate == null)
                    continue;

                if (best == null || candidate.Value.Score > best.Value.Score)
                    best = candidate;

                string normalized = ClearHighBits(candidate.Value.Text);
                if (LooksLikeRrvxPayload(normalized))
                {
                    // Diagnostic only. If this wins but raw payload does not, the top lane is polluted.
                    if (bestHighBitNormalized == null || candidate.Value.Score > bestHighBitNormalized.Value.Score)
                        bestHighBitNormalized = candidate;
                }

                if (candidate.Value.IsProtocolValid)
                {
                    Trace($"[RRVB] Accepted candidate phase={yPhase:0.00} x={xFraction:0.00} score={candidate.Value.Score}");
                    LogFirstBytes(candidate.Value.Payload);
                    return MakeResult(candidate.Value.Payload, candidate.Value.X1, candidate.Value.Y1, candidate.Value.X2, candidate.Value.Y2);
                }
            }
        }

        if (best != null)
        {
            string preview = Preview(best.Value.Text);
            Trace($"[RRVB] Reject closed block: no valid {ExpectedPayloadPrefix} payload; best phase={best.Value.YPhase:0.00} x={best.Value.XFraction:0.00} score={best.Value.Score} text='{preview}' hex={ToHexPreview(best.Value.Payload)}");
        }

        if (bestHighBitNormalized != null)
        {
            string normalized = ClearHighBits(bestHighBitNormalized.Value.Text);
            Trace($"[RRVB] High-bit-normalized candidate looks valid; likely top-lane pollution. normalized='{Preview(normalized)}'");
        }

        return null;
    }

    private static DecodeCandidate? DecodeFromStartMarkerCore(Mat binary, List<BarRow> rows, int startRowIndex, int startMarkerBarIndex, int imageRows, int imageCols, double yPhase, double xFraction)
    {
        var payload = new List<byte>();
        var startRow = rows[startRowIndex];
        int refHeight = startRow.Height;

        int blockLeft = startRow.Bars[startMarkerBarIndex].XStart;
        int blockRight = startRow.Bars[startMarkerBarIndex + 2].XEnd;
        int blockTop = startRow.Top;
        int blockBottom = startRow.Bottom;
        int lastRowBottom = startRow.Bottom;

        for (int rowIndex = startRowIndex; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            if (rowIndex > startRowIndex)
            {
                int gap = row.Top - lastRowBottom;
                if (gap < 0 || gap > Math.Max(12, refHeight * 2))
                    break;
            }

            int barIndex = rowIndex == startRowIndex ? startMarkerBarIndex + 2 : 0;
            while (barIndex < row.Bars.Count - 1)
            {
                if (barIndex <= row.Bars.Count - 3 && IsMarkerAt(row, barIndex))
                {
                    if (payload.Count == 0)
                    {
                        barIndex += 2;
                        continue;
                    }

                    blockRight = Math.Max(blockRight, row.Bars[barIndex + 2].XEnd);
                    blockTop = Math.Min(blockTop, row.Top);
                    blockBottom = Math.Max(blockBottom, row.Bottom);
                    return new DecodeCandidate(payload, blockLeft, blockTop, blockRight, blockBottom, yPhase, xFraction);
                }

                int gap = CellGap(row, barIndex);
                if (!IsPlausibleDataGap(row, gap))
                {
                    barIndex++;
                    continue;
                }

                byte value = DecodeDataCell(binary, row, barIndex, imageRows, imageCols, yPhase, xFraction);
                payload.Add(value);

                blockLeft = Math.Min(blockLeft, row.Bars[barIndex].XStart);
                blockRight = Math.Max(blockRight, row.Bars[barIndex + 1].XEnd);
                blockTop = Math.Min(blockTop, row.Top);
                blockBottom = Math.Max(blockBottom, row.Bottom);

                barIndex++;
            }

            lastRowBottom = row.Bottom;
        }

        Trace($"[RRVB] Unclosed block startRow={startRowIndex} startBar={startMarkerBarIndex} payloadBytes={payload.Count}");
        return null;
    }

    private static bool LooksLikeRrvxPayload(string text)
    {
        return text.StartsWith(ExpectedPayloadPrefix, StringComparison.Ordinal) &&
               text.Contains("G=", StringComparison.Ordinal) &&
               text.Contains(";N=", StringComparison.Ordinal);
    }

    private static int ScorePayload(string text)
    {
        int score = 0;
        if (text.StartsWith(ExpectedPayloadPrefix, StringComparison.Ordinal)) score += 10000;
        if (text.Contains("G=", StringComparison.Ordinal)) score += 1000;
        if (text.Contains(";N=", StringComparison.Ordinal)) score += 1000;

        foreach (char ch in text)
        {
            if (ch >= 32 && ch <= 126) score += 2;
            else score -= 8;

            if (ch > 127) score -= 12;
            if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == ':' || ch == ';' || ch == '=' || ch == '.' || ch == ' ') score += 1;
        }

        string normalized = ClearHighBits(text);
        if (!ReferenceEquals(normalized, text) && LooksLikeRrvxPayload(normalized))
            score += 500;

        return score;
    }

    private static string ClearHighBits(string text)
    {
        char[] chars = new char[text.Length];
        bool changed = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char normalized = c > 127 ? (char)(c & 0x7F) : c;
            chars[i] = normalized;
            changed |= normalized != c;
        }

        return changed ? new string(chars) : text;
    }

    private static string Preview(string text)
    {
        return text.Length <= MaxDebugPayloadPreviewChars ? text : text[..MaxDebugPayloadPreviewChars] + "...";
    }

    private static void LogFirstBytes(List<byte> payload)
    {
        for (int i = 0; i < Math.Min(payload.Count, 12); i++)
            Trace($"[RRVB] byte[{i}] value=0x{payload[i]:X2} '{Printable(payload[i])}'");
    }

    private static bool IsMarkerAt(BarRow row, int barIndex)
    {
        if (barIndex < 0 || barIndex > row.Bars.Count - 3)
            return false;

        int gap1 = CellGap(row, barIndex);
        int gap2 = CellGap(row, barIndex + 1);
        if (row.DataGap <= 0)
            return false;

        // v10 marker: left/center/right vertical bars. Marker gaps are about 75/200 = 0.375 of data gap.
        double minMarkerGap = row.DataGap * 0.20;
        double maxMarkerGap = row.DataGap * 0.65;
        return gap1 >= minMarkerGap && gap1 <= maxMarkerGap &&
               gap2 >= minMarkerGap && gap2 <= maxMarkerGap;
    }

    private static int CellGap(BarRow row, int barIndex)
    {
        return row.Bars[barIndex + 1].XStart - row.Bars[barIndex].XEnd;
    }

    private static bool IsPlausibleDataGap(BarRow row, int gap)
    {
        if (row.DataGap <= 0)
            return false;

        return gap >= row.DataGap * 0.60 && gap <= row.DataGap * 1.45;
    }

    private static byte DecodeDataCell(Mat binary, BarRow row, int barIndex, int imageRows, int imageCols, double yPhase, double xFraction)
    {
        var leftBar = row.Bars[barIndex];
        var rightBar = row.Bars[barIndex + 1];

        // Bar spans are inclusive. Sample only the middle of the data gap.
        // At v10 screen scale observed data gaps can be ~5 px; full-gap sampling
        // catches guard bleed and turns every lane into 1 (0xFF).
        int gapLeft = leftBar.XEnd + 1;
        int gapRight = rightBar.XStart - 1;
        if (gapRight < gapLeft)
            return 0;

        int gapWidth = gapRight - gapLeft + 1;
        int center = (int)Math.Round(gapLeft + (gapWidth - 1) * Math.Clamp(xFraction, 0.0, 1.0));

        // Decode against a small center band, not a single center pixel.
        // At 14px WoW font scale the data gap is still only ~5-6 px wide,
        // and single-pixel sampling is too sensitive to raster speckles/guard
        // bleed. Real lane strokes span across the data zone, so requiring a
        // small center-band majority is more size-tolerant while still avoiding
        // the guard edges.
        int halfWidth = gapWidth <= 2 ? 0 : Math.Max(1, gapWidth / 4);

        int x0 = Math.Max(gapLeft, center - halfWidth);
        int x1 = Math.Min(gapRight, center + halfWidth);

        byte value = 0;
        for (int lane = 0; lane < NumLanes; lane++)
        {
            if (LaneHasInk(binary, row, x0, x1, lane, imageRows, imageCols, yPhase))
                value |= (byte)(1 << (7 - lane));
        }

        return value;
    }

    private static bool LaneHasInk(Mat binary, BarRow row, int x0, int x1, int lane, int rows, int cols, double yPhase)
    {
        int xa = Math.Clamp(Math.Min(x0, x1), 0, cols - 1);
        int xb = Math.Clamp(Math.Max(x0, x1), 0, cols - 1);
        if (xb < xa)
            return false;

        // The guard bar gives the row envelope, but WoW font rasterization can shift
        // the actual 8 data lanes within that envelope by a fraction of a lane.
        // Sample a small band around a phase-adjusted lane center instead of treating
        // the whole ideal slot as valid; otherwise a single speck in the top lane can
        // set the high bit and turn "R" into 0xD2.
        double slotH = row.Height / (double)NumLanes;
        double centerY = row.Top + (lane + 0.5 + yPhase) * slotH;
        int radius = Math.Max(0, (int)Math.Floor(slotH * 0.22));
        if (slotH >= 2.5)
            radius = Math.Max(radius, 1);

        int slotTop = (int)Math.Round(centerY) - radius;
        int slotBottom = (int)Math.Round(centerY) + radius;

        slotTop = Math.Clamp(slotTop, 0, rows - 1);
        slotBottom = Math.Clamp(slotBottom, 0, rows - 1);
        if (slotBottom < slotTop)
            (slotTop, slotBottom) = (slotBottom, slotTop);

        int sampleWidth = xb - xa + 1;
        int sampleHeight = slotBottom - slotTop + 1;
        int ink = 0;

        unsafe
        {
            byte* ptr = (byte*)binary.DataPointer;
            int step = (int)binary.Step();

            for (int y = slotTop; y <= slotBottom; y++)
            {
                byte* data = ptr + y * step;
                for (int x = xa; x <= xb; x++)
                {
                    if (data[x] > 0)
                        ink++;
                }
            }
        }

        int total = sampleWidth * sampleHeight;
        int requiredInk = Math.Max(1, (int)Math.Ceiling(total * LanePeakInkFraction));

        // With a 3+ pixel center band, a one-pixel speck should not become a bit.
        if (sampleWidth >= 3)
            requiredInk = Math.Max(requiredInk, 2);

        return ink >= requiredInk;
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
