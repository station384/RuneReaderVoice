// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3 of the License.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using RuneReaderVoice.Platform;
using RuneReaderVoice.Protocol;
using ZXing;
using ZXing.Common;

namespace RuneReaderVoice.Session;

// RvBarcodeMonitor.cs
// Continuously captures screen frames and scans for RuneReader Voice barcodes.
//
// Channels:
//   - QR: primary dialog/text channel. Payload is Base45 -> RV protocol packet.
//   - Code39 GUID: side-channel metadata identified by decoded prefix "RRVG-".
//   - Code39 Name: side-channel metadata identified by decoded prefix "RRVN-".
//
// Each channel maintains its own locked region. Full-screen rescans locate all
// regions. Region polling captures known regions independently without
// treating Code39 channels as a source-gone signal.
public sealed class RvBarcodeMonitor : IDisposable
{
    private enum RegionKind
    {
        None,
        Qr,
        Code39Guid,
        Code39Name,
    }

    private readonly BarcodeReaderGeneric _qrMultiReader = new();
    private readonly BarcodeReaderGeneric _qrSingleReader = new();
    private readonly BarcodeReaderGeneric _code39MultiReader = new();
    private readonly BarcodeReaderGeneric _code39SingleReader = new();

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fires when a valid (non-preview) RV QR packet is decoded.</summary>
    public event Action<RvPacket>? OnPacketDecoded;

    /// <summary>Fires when a valid RRV Code39 GUID side-channel is decoded.</summary>
    public event Action<string>? OnCode39GuidDecoded;

    /// <summary>Fires when a valid RRV Code39 NPC name side-channel is decoded.</summary>
    public event Action<string>? OnCode39NameDecoded;

    /// <summary>
    /// Fires when no RV QR has been seen for SourceGoneThresholdMs.
    /// Code39 presence does not keep a dialog alive; QR remains the source clock.
    /// </summary>
    public event Action? OnSourceGone;

    /// <summary>Fires with the latest full-screen Mat for the UI preview.</summary>
    public event Action<Mat>? OnFrameCaptured;
    public event Action<Mat>? OnRegionCaptured;
    public event Action<Rect>? OnLockedRegionChanged;
    public event Action<Rect>? OnLockedCode39GuidRegionChanged;
    public event Action<Rect>? OnLockedCode39NameRegionChanged;

    // ── Configuration ─────────────────────────────────────────────────────────

    public int CaptureIntervalMs { get; set; } = 5;
    public int ReScanIntervalMs { get; set; } = 5000;
    public int SourceGoneThresholdMs { get; set; } = 2000;

    // ── Internal state ────────────────────────────────────────────────────────

    private readonly IScreenCaptureProvider _capture;

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Task? _reScanTask;
    private Task? _sourceGoneTask;

    private bool _regionHasRvQr;
    private bool _regionHasCode39Guid;
    private bool _regionHasCode39Name;
    private Rect? _lockedRegion;
    private Rect? _lockedCode39GuidRegion;
    private Rect? _lockedCode39NameRegion;
    private RegionKind _activeRegionKind = RegionKind.None;
    private DateTime _lastRvDecodeTime = DateTime.MinValue;
    private DateTime _lastCode39DecodeTime = DateTime.MinValue;
    private string _lastCode39Guid = string.Empty;
    private string _lastCode39Name = string.Empty;
    private bool _sourceGoneSignalled;

    private readonly object _gate = new();
    private readonly object _captureIoGate = new();
    private bool _disposed;

    private const string Code39GuidPrefix = "RRVG-";
    private const string Code39NamePrefix = "RRVN-";

    public RvBarcodeMonitor(IScreenCaptureProvider capture)
    {
        _capture = capture;

        _qrMultiReader.Options.Hints.Add(DecodeHintType.CHARACTER_SET, "ISO-8859-1");
        _qrMultiReader.Options.PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE };
        _qrMultiReader.Options.Hints.Add(DecodeHintType.TRY_HARDER, true);
        _qrMultiReader.Options.Hints.Add(DecodeHintType.PURE_BARCODE, false);

        _qrSingleReader.Options.Hints.Add(DecodeHintType.CHARACTER_SET, "ISO-8859-1");
        _qrSingleReader.Options.PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE };
        _qrSingleReader.Options.Hints.Add(DecodeHintType.TRY_HARDER, true);
        _qrSingleReader.Options.Hints.Add(DecodeHintType.PURE_BARCODE, true);

        _code39MultiReader.Options.PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.CODE_39 };
        _code39MultiReader.Options.Hints.Remove(DecodeHintType.USE_CODE_39_EXTENDED_MODE);
        _code39MultiReader.Options.Hints.Add(DecodeHintType.USE_CODE_39_EXTENDED_MODE, false);
        _code39MultiReader.Options.Hints.Add(DecodeHintType.TRY_HARDER, true);

        _code39MultiReader.Options.Hints.Add(DecodeHintType.PURE_BARCODE, false);

        _code39SingleReader.Options.PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.CODE_39 };
        _code39SingleReader.Options.Hints.Add(DecodeHintType.TRY_HARDER, true);
        _code39SingleReader.Options.Hints.Remove(DecodeHintType.USE_CODE_39_EXTENDED_MODE);
        _code39SingleReader.Options.Hints.Add(DecodeHintType.USE_CODE_39_EXTENDED_MODE, false);
        _code39SingleReader.Options.Hints.Add(DecodeHintType.PURE_BARCODE, false);
    }

    public void TrySetInitialLockedRegion(SavedBarcodeRegion? saved)
    {
        var clamped = ToClampedRect(saved);
        if (!clamped.HasValue) return;

        lock (_gate)
        {
            if (_captureTask is { IsCompleted: false }) return;
            _lockedRegion = clamped.Value;
        }
    }

    public void TrySetInitialLockedCode39GuidRegion(SavedBarcodeRegion? saved)
    {
        var clamped = ToClampedRect(saved);
        if (!clamped.HasValue) return;

        lock (_gate)
        {
            if (_captureTask is { IsCompleted: false }) return;
            _lockedCode39GuidRegion = clamped.Value;
        }
    }

    public void TrySetInitialLockedCode39NameRegion(SavedBarcodeRegion? saved)
    {
        var clamped = ToClampedRect(saved);
        if (!clamped.HasValue) return;

        lock (_gate)
        {
            if (_captureTask is { IsCompleted: false }) return;
            _lockedCode39NameRegion = clamped.Value;
        }
    }

    private Rect? ToClampedRect(SavedBarcodeRegion? saved)
    {
        if (saved == null) return null;
        return ClampRegionToScreen(new Rect(saved.X, saved.Y, saved.Width, saved.Height));
    }

    private Rect? ClampRegionToScreen(Rect rect)
    {
        if (_capture.ScreenWidth <= 0 || _capture.ScreenHeight <= 0)
            return null;

        var x = Math.Max(0, rect.X);
        var y = Math.Max(0, rect.Y);
        var maxWidth = _capture.ScreenWidth - x;
        var maxHeight = _capture.ScreenHeight - y;
        if (maxWidth <= 0 || maxHeight <= 0)
            return null;

        var width = Math.Min(rect.Width, maxWidth);
        var height = Math.Min(rect.Height, maxHeight);
        if (width <= 0 || height <= 0)
            return null;

        return new Rect(x, y, width, height);
    }

    // ── Control ───────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_gate)
        {
            if (_captureTask is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            _regionHasRvQr = false;
            _regionHasCode39Guid = false;
            _regionHasCode39Name = false;
            _sourceGoneSignalled = false;
            _activeRegionKind = RegionKind.None;

            var token = _cts.Token;
            _captureTask = CaptureLoopAsync(token);
            _reScanTask = ReScanLoopAsync(token);
            _sourceGoneTask = SourceGoneLoopAsync(token);
        }
    }

    private const double HotIntervalFactor = 0.5;
    private const double ColdIntervalFactor = 1.5;
    private const int HotWindowMs = 250;
    private const int WarmWindowMs = 20000;
    private const double GcMemoryLoadThreshold = 0.10;
    private const int GcCooldownMs = 1000;
    private DateTime _lastForcedGcUtc = DateTime.MinValue;

    private static int ClampBaseCaptureInterval(int value)
        => Math.Clamp(value, 4, 100);

    private int GetAdaptiveCaptureIntervalMs()
    {
        int baseInterval;
        bool regionHasRvQr;
        DateTime lastRvDecodeTime;
        lock (_gate)
        {
            baseInterval = ClampBaseCaptureInterval(CaptureIntervalMs);
            regionHasRvQr = _regionHasRvQr;
            lastRvDecodeTime = _lastRvDecodeTime;
        }

        if (regionHasRvQr)
            return Math.Max(2, (int)Math.Round(baseInterval * HotIntervalFactor));

        if (lastRvDecodeTime == DateTime.MinValue)
            return baseInterval;

        var ageMs = (DateTime.UtcNow - lastRvDecodeTime).TotalMilliseconds;
        if (ageMs <= HotWindowMs)
            return Math.Max(2, (int)Math.Round(baseInterval * HotIntervalFactor));
        if (ageMs <= WarmWindowMs)
            return baseInterval;
        return Math.Max(baseInterval + 1, (int)Math.Round(baseInterval * ColdIntervalFactor));
    }

    private void CheckIfWeShouldGC()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastForcedGcUtc).TotalMilliseconds < GcCooldownMs)
            return;

        var process = Process.GetCurrentProcess();
        var workingSet = process.WorkingSet64;
        var totalAvailable = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (totalAvailable <= 0)
            return;

        var load = (double)workingSet / totalAvailable;
        if (load < GcMemoryLoadThreshold)
            return;

        GC.Collect();
        _lastForcedGcUtc = now;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
        }

        if (cts == null) return;

        await cts.CancelAsync().ConfigureAwait(false);
        var tasks = new[] { _captureTask, _reScanTask, _sourceGoneTask }
            .Where(t => t != null)
            .Select(t => t!);
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        finally { cts.Dispose(); }
    }

    // ── Capture loop ──────────────────────────────────────────────────────────

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        await Task.Yield();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                Rect? qrRegion;
                Rect? code39GuidRegion;
                Rect? code39NameRegion;
                lock (_gate)
                {
                    qrRegion = _lockedRegion;
                    code39GuidRegion = _lockedCode39GuidRegion;
                    code39NameRegion = _lockedCode39NameRegion;
                }

                lock (_captureIoGate)
                {
                    _capture.EnableFullScreen = false;

                    if (qrRegion.HasValue)
                    {
                        _activeRegionKind = RegionKind.Qr;
                        _capture.EnableRegion = true;
                        _capture.CaptureRegion = qrRegion.Value;
                        _capture.CaptureOnce();
                    }

                    if (code39GuidRegion.HasValue)
                    {
                        _activeRegionKind = RegionKind.Code39Guid;
                        _capture.EnableRegion = true;
                        _capture.CaptureRegion = code39GuidRegion.Value;
                        _capture.CaptureOnce();
                    }

                    if (code39NameRegion.HasValue)
                    {
                        _activeRegionKind = RegionKind.Code39Name;
                        _capture.EnableRegion = true;
                        _capture.CaptureRegion = code39NameRegion.Value;
                        _capture.CaptureOnce();
                    }

                    if (!qrRegion.HasValue && !code39GuidRegion.HasValue && !code39NameRegion.HasValue)
                    {
                        _activeRegionKind = RegionKind.None;
                        _capture.EnableRegion = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RvBarcodeMonitor] Capture error: {ex.Message}");
            }

            var nextDelayMs = GetAdaptiveCaptureIntervalMs();
            try { await Task.Delay(nextDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Full-screen processing ────────────────────────────────────────────────

    public void ProcessFrame(Mat frame)
    {
        Mat fullFrame = frame.Clone();
        try
        {
            if (fullFrame.Empty()) return;

            OnFrameCaptured?.Invoke(fullFrame);

            var qrResults = DecodeQrMultiple(fullFrame);
            if (qrResults is { Length: > 0 })
            {
                foreach (var result in qrResults)
                {
                    if (string.IsNullOrEmpty(result.Text)) continue;

                    var raw = TryDecodeBase45(result.Text);
                    if (raw == null) continue;

                    var packet = RvPacket.TryParse(raw);
                    if (packet == null || packet.IsPreview) continue;

                    UpdateRegionLock(result, RegionKind.Qr);
                }
            }

            var code39Results = DecodeCode39Multiple(fullFrame);
            if (code39Results is { Length: > 0 })
            {
                foreach (var result in code39Results)
                {
                    if (TryExtractCode39Guid(result.Text) is { } guid)
                    {
                        UpdateRegionLock(result, RegionKind.Code39Guid);
                        RecordCode39Guid(guid);
                        continue;
                    }

                    if (TryExtractCode39Name(result.Text) is { } name)
                    {
                        UpdateRegionLock(result, RegionKind.Code39Name);
                        RecordCode39Name(name);
                    }
                }
            }
        }
        finally
        {
            fullFrame.Dispose();
            if (!frame.IsDisposed)
                frame.Dispose();
            CheckIfWeShouldGC();
        }
    }

    public void ProcessFrameRegion(Mat frame)
    {
        RegionKind kind;
        lock (_gate)
            kind = _activeRegionKind;

        Mat regionFrame = frame.Clone();
        try
        {
            if (regionFrame.Empty()) return;

            if (kind == RegionKind.Qr)
                OnRegionCaptured?.Invoke(regionFrame);

            if (kind == RegionKind.Code39Guid)
            {
                ProcessCode39GuidRegion(regionFrame);
                return;
            }

            if (kind == RegionKind.Code39Name)
            {
                ProcessCode39NameRegion(regionFrame);
                return;
            }

            ProcessQrRegion(regionFrame);
        }
        finally
        {
            regionFrame.Dispose();
            if (!frame.IsDisposed)
                frame.Dispose();
            CheckIfWeShouldGC();
        }
    }

    private void ProcessQrRegion(Mat frame)
    {
        var decodedText = DecodeQrSingle(frame);
        if (string.IsNullOrEmpty(decodedText))
        {
            lock (_gate)
                _regionHasRvQr = false;
            return;
        }

        var packet = RvPacket.TryParse(decodedText);
        if (packet == null || packet.IsPreview)
        {
            lock (_gate)
                _regionHasRvQr = false;
            return;
        }

        lock (_gate)
        {
            _regionHasRvQr = true;
            _lastRvDecodeTime = DateTime.UtcNow;
            _sourceGoneSignalled = false;
        }

        RuneReaderVoice.AppServices.RecordQrPacketDecoded(packet);
        OnPacketDecoded?.Invoke(packet);
    }

    private void ProcessCode39GuidRegion(Mat frame)
    {
        var decodedText = DecodeCode39Single(frame);
        var guid = TryExtractCode39Guid(decodedText);
        if (guid == null)
        {
            lock (_gate)
                _regionHasCode39Guid = false;
            return;
        }

        lock (_gate)
        {
            _regionHasCode39Guid = true;
            _lastCode39DecodeTime = DateTime.UtcNow;
        }

        RecordCode39Guid(guid);
    }

    private void ProcessCode39NameRegion(Mat frame)
    {
        var decodedText = DecodeCode39Single(frame);
        var name = TryExtractCode39Name(decodedText);
        if (name == null)
        {
            lock (_gate)
                _regionHasCode39Name = false;
            return;
        }

        lock (_gate)
        {
            _regionHasCode39Name = true;
            _lastCode39DecodeTime = DateTime.UtcNow;
        }

        RecordCode39Name(name);
    }

    private void RecordCode39Guid(string guid)
    {
        var shouldRaise = false;
        lock (_gate)
        {
            _regionHasCode39Guid = true;
            _lastCode39DecodeTime = DateTime.UtcNow;
            if (!string.Equals(_lastCode39Guid, guid, StringComparison.OrdinalIgnoreCase))
            {
                _lastCode39Guid = guid;
                shouldRaise = true;
            }
        }

        if (shouldRaise)
        {
            Debug.WriteLine($"[Code39] GUID {guid}");
            OnCode39GuidDecoded?.Invoke(guid);
        }
    }

    private void RecordCode39Name(string name)
    {
        var shouldRaise = false;
        lock (_gate)
        {
            _regionHasCode39Name = true;
            _lastCode39DecodeTime = DateTime.UtcNow;
            if (!string.Equals(_lastCode39Name, name, StringComparison.Ordinal))
            {
                _lastCode39Name = name;
                shouldRaise = true;
            }
        }

        if (shouldRaise)
        {
            Debug.WriteLine($"[Code39] Name {name}");
            OnCode39NameDecoded?.Invoke(name);
        }
    }

    private static string? TryDecodeBase45(string text)
    {
        try
        {
            return Base45Simple.DecodeUtf8(text);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryExtractCode39Guid(string? decodedText)
    {
        var text = NormalizeCode39DecodedText(decodedText);
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (!text.StartsWith(Code39GuidPrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var guid = text[Code39GuidPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(guid) ? null : guid;
    }

    private static string? TryExtractCode39Name(string? decodedText)
    {
        var text = NormalizeCode39DecodedText(decodedText);
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (!text.StartsWith(Code39NamePrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        var name = text[Code39NamePrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static string NormalizeCode39DecodedText(string? decodedText)
    {
        if (string.IsNullOrWhiteSpace(decodedText)) return string.Empty;

        var text = decodedText.Trim();
        if (text.Length >= 2 && text[0] == '*' && text[^1] == '*')
            text = text[1..^1];

        // Mapping belongs to the Code39 decode layer, not just RRVN.
        // Both RRVG and RRVN use the same custom LibreBarcode39 font.
        // The addon does not transform secret text; these corrections happen
        // only after ZXing decodes the rendered barcode.
        return text
            .Replace('+', ' ')
            .Replace('$', '\'')
            .Replace('.', ',');
    }

    // ── Decode helpers ───────────────────────────────────────────────────────

    private byte[] _fullQrScanBuffer = new byte[1];
    private byte[] _singleQrScanBuffer = new byte[1];
    private byte[] _fullCode39ScanBuffer = new byte[1];
    private byte[] _singleCode39ScanBuffer = new byte[1];

    private Result[]? DecodeQrMultiple(Mat frame)
        => DecodeMultiple(frame, _qrMultiReader, ref _fullQrScanBuffer, pad: 0);

    private Result[]? DecodeCode39Multiple(Mat frame)
        => DecodeMultiple(frame, _code39MultiReader, ref _fullCode39ScanBuffer, pad: 0);

    private string? DecodeQrSingle(Mat frame)
    {
        var result = DecodeMultiple(frame, _qrSingleReader, ref _singleQrScanBuffer, pad: 50)?.FirstOrDefault();
        return result == null ? null : TryDecodeBase45(result.Text);
    }

    private string? DecodeCode39Single(Mat frame)
    {
        var result = DecodeMultiple(frame, _code39SingleReader, ref _singleCode39ScanBuffer, pad: 20)?.FirstOrDefault();
        return result?.Text;
    }

    private static Result[]? DecodeMultiple(Mat frame, BarcodeReaderGeneric reader, ref byte[] buffer, int pad)
    {
        Mat gray = new();
        try
        {
            gray = frame.Channels() == 1 ? frame.Clone() : frame.CvtColor(ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, gray, 20, 255, ThresholdTypes.Binary);

            Mat source = gray;
            Mat? padded = null;
            if (pad > 0)
            {
                padded = new Mat();
                Cv2.CopyMakeBorder(gray, padded, pad, pad, pad, pad, BorderTypes.Constant, Scalar.White);
                source = padded;
            }

            try
            {
                var required = source.Rows * source.Cols;
                if (buffer.Length != required)
                    buffer = new byte[required];

                Marshal.Copy(source.Data, buffer, 0, buffer.Length);

                var luminance = new RGBLuminanceSource(buffer, source.Cols, source.Rows, RGBLuminanceSource.BitmapFormat.Gray8);
                return reader.DecodeMultiple(luminance);
            }
            finally
            {
                padded?.Dispose();
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            gray.Dispose();
        }
    }

    private void UpdateRegionLock(Result result, RegionKind kind)
    {
        if (result.ResultPoints == null || result.ResultPoints.Length < 2)
            return;

        float minX = result.ResultPoints.Min(p => p.X);
        float minY = result.ResultPoints.Min(p => p.Y);
        float maxX = result.ResultPoints.Max(p => p.X);
        float maxY = result.ResultPoints.Max(p => p.Y);

        const int padding = 30;
        var minHeight = (kind == RegionKind.Code39Guid || kind == RegionKind.Code39Name) ? 80 : 0;

        int left = Math.Max(0, (int)Math.Floor(minX) - padding);
        int top = Math.Max(0, (int)Math.Floor(minY) - padding);
        int right = Math.Min(_capture.ScreenWidth, (int)Math.Ceiling(maxX) + padding);
        int bottom = Math.Min(_capture.ScreenHeight, (int)Math.Ceiling(maxY) + padding);

        if (minHeight > 0 && bottom - top < minHeight)
        {
            var centerY = (top + bottom) / 2;
            top = Math.Max(0, centerY - minHeight / 2);
            bottom = Math.Min(_capture.ScreenHeight, top + minHeight);
        }

        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
            return;

        var clamped = new Rect(left, top, width, height);
        var changed = false;

        lock (_gate)
        {
            if (kind == RegionKind.Code39Guid)
            {
                if (!_lockedCode39GuidRegion.HasValue || !_lockedCode39GuidRegion.Value.Equals(clamped))
                {
                    _lockedCode39GuidRegion = clamped;
                    changed = true;
                }
            }
            else if (kind == RegionKind.Code39Name)
            {
                if (!_lockedCode39NameRegion.HasValue || !_lockedCode39NameRegion.Value.Equals(clamped))
                {
                    _lockedCode39NameRegion = clamped;
                    changed = true;
                }
            }
            else
            {
                if (!_lockedRegion.HasValue || !_lockedRegion.Value.Equals(clamped))
                {
                    _lockedRegion = clamped;
                    changed = true;
                }
            }
        }

        if (!changed) return;

        if (kind == RegionKind.Code39Guid)
            OnLockedCode39GuidRegionChanged?.Invoke(clamped);
        else if (kind == RegionKind.Code39Name)
            OnLockedCode39NameRegionChanged?.Invoke(clamped);
        else
            OnLockedRegionChanged?.Invoke(clamped);
    }

    // ── Full-screen rescan loop ───────────────────────────────────────────────

    private async Task ReScanLoopAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            bool needsScan;
            lock (_gate)
                needsScan = !_regionHasRvQr || !_regionHasCode39Guid || !_regionHasCode39Name;

            if (needsScan)
            {
                Rect? qrRegion;
                lock (_gate)
                    qrRegion = _lockedRegion;

                lock (_captureIoGate)
                {
                    _activeRegionKind = RegionKind.None;
                    _capture.EnableRegion = qrRegion.HasValue;
                    if (qrRegion.HasValue)
                        _capture.CaptureRegion = qrRegion.Value;

                    _capture.EnableFullScreen = true;
                    _capture.CaptureOnce();
                    _capture.EnableFullScreen = false;
                }
            }

            try { await Task.Delay(ReScanIntervalMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Source-gone detection loop ────────────────────────────────────────────

    private async Task SourceGoneLoopAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            bool shouldSignal;
            lock (_gate)
            {
                var elapsed = (DateTime.UtcNow - _lastRvDecodeTime).TotalMilliseconds;
                shouldSignal = _lastRvDecodeTime != DateTime.MinValue
                              && !_sourceGoneSignalled
                              && elapsed > SourceGoneThresholdMs;
                if (shouldSignal)
                {
                    _regionHasRvQr = false;
                    _sourceGoneSignalled = true;
                }
            }

            if (shouldSignal) OnSourceGone?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        catch (AggregateException ae) when (ae.InnerExceptions.All(ex => ex is OperationCanceledException))
        {
        }
    }
}
