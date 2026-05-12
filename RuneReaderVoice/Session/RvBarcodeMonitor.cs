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
//   - RRVB GUID: side-channel metadata identified by decoded prefix "RRVG-".
//   - RRVB Name: side-channel metadata identified by decoded prefix "RRVN-".
//
// Each channel maintains its own locked region. Full-screen rescans locate all
// regions. Region polling captures known regions independently without
// treating RRVB channel as a source-gone signal.
public sealed class RvBarcodeMonitor : IDisposable
{
    private enum RegionKind
    {
        None,
        Qr,
        RrvbGuid,
        RrvbName,
    }

    private readonly BarcodeReaderGeneric _qrMultiReader = new();
    private readonly BarcodeReaderGeneric _qrSingleReader = new();

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fires when a valid (non-preview) RV QR packet is decoded.</summary>
    public event Action<RvPacket>? OnPacketDecoded;

    /// <summary>Fires when a valid RRVB GUID side-channel is decoded.</summary>
    public event Action<string>? OnRrvbGuidDecoded;

    /// <summary>Fires when a valid RRVB NPC name side-channel is decoded.</summary>
    public event Action<string>? OnRrvbNameDecoded;

    /// <summary>
    /// Fires when no RV QR has been seen for SourceGoneThresholdMs.
    /// RRVB presence does not keep a dialog alive; QR remains the source clock.
    /// </summary>
    public event Action? OnSourceGone;

    /// <summary>Fires with the latest full-screen Mat for the UI preview.</summary>
    public event Action<Mat>? OnFrameCaptured;
    public event Action<Mat>? OnRegionCaptured;
    public event Action<Rect>? OnLockedRegionChanged;
    public event Action<Rect>? OnLockedRrvbGuidRegionChanged;
    public event Action<Rect>? OnLockedRrvbNameRegionChanged;

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
    private bool _regionHasRrvbGuid;
    private bool _regionHasRrvbName;
    private Rect? _lockedRegion;
    private Rect? _lockedRrvbGuidRegion;
    private Rect? _lockedRrvbNameRegion;
    private RegionKind _activeRegionKind = RegionKind.None;
    private DateTime _lastRvDecodeTime = DateTime.MinValue;
    private DateTime _lastRrvbDecodeTime = DateTime.MinValue;
    private DateTime _lastRrvbGuidDecodeTime = DateTime.MinValue;
    private DateTime _lastRrvbNameDecodeTime = DateTime.MinValue;
    private string _lastRrvbGuid = string.Empty;
    private string _lastRrvbName = string.Empty;
    private bool _sourceGoneSignalled;

    private readonly object _gate = new();
    private readonly object _captureIoGate = new();
    private bool _disposed;

    private const string RrvbGuidPrefix = "RRVG-";
    private const string RrvbNamePrefix = "RRVN-";

    // Full-screen scans are only needed to acquire or recover lost regions.
    // Once region polling is actively decoding all channels, keep full-screen
    // capture off. This avoids expensive 5-second rescans while stable barcodes
    // remain on screen.
    private const int RegionStableGraceMultiplier = 3;
    private const bool RrvbDebugTraceEnabled = false;


    private static void TraceRrvb(string message)
    {
        if (RrvbDebugTraceEnabled)
            Debug.WriteLine(message);
    }

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

    public void TrySetInitialLockedRrvbGuidRegion(SavedBarcodeRegion? saved)
    {
        var clamped = ToClampedRect(saved);
        if (!clamped.HasValue) return;

        lock (_gate)
        {
            if (_captureTask is { IsCompleted: false }) return;
            _lockedRrvbGuidRegion = clamped.Value;
        }
    }

    public void TrySetInitialLockedRrvbNameRegion(SavedBarcodeRegion? saved)
    {
        var clamped = ToClampedRect(saved);
        if (!clamped.HasValue) return;

        lock (_gate)
        {
            if (_captureTask is { IsCompleted: false }) return;
            _lockedRrvbNameRegion = clamped.Value;
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
            _regionHasRrvbGuid = false;
            _regionHasRrvbName = false;
            _sourceGoneSignalled = false;
            _lastRrvbGuidDecodeTime = DateTime.MinValue;
            _lastRrvbNameDecodeTime = DateTime.MinValue;
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
                Rect? rrvbGuidRegion;
                Rect? rrvbNameRegion;
                lock (_gate)
                {
                    qrRegion = _lockedRegion;
                    rrvbGuidRegion = _lockedRrvbGuidRegion;
                    rrvbNameRegion = _lockedRrvbNameRegion;
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

                    if (rrvbGuidRegion.HasValue)
                    {
                        _activeRegionKind = RegionKind.RrvbGuid;
                        _capture.EnableRegion = true;
                        _capture.CaptureRegion = rrvbGuidRegion.Value;
                        _capture.CaptureOnce();
                    }

                    if (rrvbNameRegion.HasValue)
                    {
                        _activeRegionKind = RegionKind.RrvbName;
                        _capture.EnableRegion = true;
                        _capture.CaptureRegion = rrvbNameRegion.Value;
                        _capture.CaptureOnce();
                    }

                    if (!qrRegion.HasValue && !rrvbGuidRegion.HasValue && !rrvbNameRegion.HasValue)
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

            var rrvbResults = DecodeRrvbMultiple(fullFrame);
            if (rrvbResults is { Length: > 0 })
            {
                foreach (var result in rrvbResults)
                {
                    if (TryExtractRrvbGuid(result.Text) is { } guid)
                    {
                        UpdateRegionLock(result, RegionKind.RrvbGuid);
                        RecordRrvbGuid(guid);
                        continue;
                    }

                    if (TryExtractRrvbName(result.Text) is { } name)
                    {
                        UpdateRegionLock(result, RegionKind.RrvbName);
                        RecordRrvbName(name);
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

            if (kind == RegionKind.RrvbGuid)
            {
                ProcessRrvbGuidRegion(regionFrame);
                return;
            }

            if (kind == RegionKind.RrvbName)
            {
                ProcessRrvbNameRegion(regionFrame);
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

    private void ProcessRrvbGuidRegion(Mat frame)
    {
        var decodedText = DecodeRrvbSingle(frame);
        var guid = TryExtractRrvbGuid(decodedText);
        if (guid == null)
        {
            lock (_gate)
                _regionHasRrvbGuid = false;
            return;
        }

        lock (_gate)
        {
            _regionHasRrvbGuid = true;
            _lastRrvbDecodeTime = DateTime.UtcNow;
            _lastRrvbGuidDecodeTime = _lastRrvbDecodeTime;
        }

        RecordRrvbGuid(guid);
    }

    private void ProcessRrvbNameRegion(Mat frame)
    {
        var decodedText = DecodeRrvbSingle(frame);
        var name = TryExtractRrvbName(decodedText);
        if (name == null)
        {
            lock (_gate)
                _regionHasRrvbName = false;
            return;
        }

        lock (_gate)
        {
            _regionHasRrvbName = true;
            _lastRrvbDecodeTime = DateTime.UtcNow;
            _lastRrvbNameDecodeTime = _lastRrvbDecodeTime;
        }

        RecordRrvbName(name);
    }

    private void RecordRrvbGuid(string guid)
    {
        var shouldRaise = false;
        lock (_gate)
        {
            _regionHasRrvbGuid = true;
            _lastRrvbDecodeTime = DateTime.UtcNow;
            _lastRrvbGuidDecodeTime = _lastRrvbDecodeTime;
            if (!string.Equals(_lastRrvbGuid, guid, StringComparison.OrdinalIgnoreCase))
            {
                _lastRrvbGuid = guid;
                shouldRaise = true;
            }
        }

        if (shouldRaise)
        {
            TraceRrvb($"[RRVB] GUID {guid}");
            OnRrvbGuidDecoded?.Invoke(guid);
        }
    }

    private void RecordRrvbName(string name)
    {
        var shouldRaise = false;
        lock (_gate)
        {
            _regionHasRrvbName = true;
            _lastRrvbDecodeTime = DateTime.UtcNow;
            _lastRrvbNameDecodeTime = _lastRrvbDecodeTime;
            if (!string.Equals(_lastRrvbName, name, StringComparison.Ordinal))
            {
                _lastRrvbName = name;
                shouldRaise = true;
            }
        }

        if (shouldRaise)
        {
            TraceRrvb($"[RRVB] Name {name}");
            OnRrvbNameDecoded?.Invoke(name);
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

    private static string? TryExtractRrvbGuid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!text.StartsWith(RrvbGuidPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var guid = text[RrvbGuidPrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(guid) ? null : guid;
    }

    private static string? TryExtractRrvbName(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!text.StartsWith(RrvbNamePrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var name = text[RrvbNamePrefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    // ── Decode helpers ───────────────────────────────────────────────────────

    private byte[] _fullQrScanBuffer    = new byte[1];
    private byte[] _singleQrScanBuffer  = new byte[1];
    private byte[] _fullRrvbScanBuffer  = new byte[1];
    private byte[] _singleRrvbScanBuffer = new byte[1];

    private Result[]? DecodeQrMultiple(Mat frame)
        => DecodeMultiple(frame, _qrMultiReader, ref _fullQrScanBuffer, pad: 0);

    private string? DecodeQrSingle(Mat frame)
    {
        var result = DecodeMultiple(frame, _qrSingleReader, ref _singleQrScanBuffer, pad: 50)?.FirstOrDefault();
        return result == null ? null : TryDecodeBase45(result.Text);
    }

    private Result[]? DecodeRrvbMultiple(Mat frame)
        => DecodeMultipleRrvb(frame, ref _fullRrvbScanBuffer, pad: 0);

    private string? DecodeRrvbSingle(Mat frame)
    {
        var result = DecodeMultipleRrvb(frame, ref _singleRrvbScanBuffer, pad: 20)?.FirstOrDefault();
        return result?.Text;
    }

    // RRVB decode pipeline: same gray+threshold+pad cleanup as DecodeMultiple,
    // then calls RrvBarcodeDetector.Detect on the cleaned Mat instead of ZXing.
    private static Result[]? DecodeMultipleRrvb(Mat frame, ref byte[] buffer, int pad)
    {
        Mat gray   = new();
        Mat? padded = null;
        try
        {
            gray = frame.Channels() == 1 ? frame.Clone() : frame.CvtColor(ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, gray, 20, 255, ThresholdTypes.Binary);

            Mat source = gray;
            if (pad > 0)
            {
                padded = new Mat();
                Cv2.CopyMakeBorder(gray, padded, pad, pad, pad, pad, BorderTypes.Constant, Scalar.White);
                source = padded;
            }

            return RrvBarcodeDetector.Detect(source);
        }
        catch
        {
            return null;
        }
        finally
        {
            padded?.Dispose();
            gray.Dispose();
        }
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
        var minHeight = (kind == RegionKind.RrvbGuid || kind == RegionKind.RrvbName) ? 80 : 0;

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
            if (kind == RegionKind.RrvbGuid)
            {
                if (!_lockedRrvbGuidRegion.HasValue || !_lockedRrvbGuidRegion.Value.Equals(clamped))
                {
                    _lockedRrvbGuidRegion = clamped;
                    changed = true;
                }
            }
            else if (kind == RegionKind.RrvbName)
            {
                if (!_lockedRrvbNameRegion.HasValue || !_lockedRrvbNameRegion.Value.Equals(clamped))
                {
                    _lockedRrvbNameRegion = clamped;
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

        if (kind == RegionKind.RrvbGuid)
            OnLockedRrvbGuidRegionChanged?.Invoke(clamped);
        else if (kind == RegionKind.RrvbName)
            OnLockedRrvbNameRegionChanged?.Invoke(clamped);
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
            string reason;
            Rect? qrRegion;

            lock (_gate)
            {
                var now = DateTime.UtcNow;
                var stableGraceMs = Math.Max(ReScanIntervalMs * RegionStableGraceMultiplier, SourceGoneThresholdMs);

                var qrStable = _lockedRegion.HasValue
                            && _regionHasRvQr
                            && _lastRvDecodeTime != DateTime.MinValue
                            && (now - _lastRvDecodeTime).TotalMilliseconds <= stableGraceMs;

                var guidStable = _lockedRrvbGuidRegion.HasValue
                              && _regionHasRrvbGuid
                              && _lastRrvbGuidDecodeTime != DateTime.MinValue
                              && (now - _lastRrvbGuidDecodeTime).TotalMilliseconds <= stableGraceMs;

                var nameStable = _lockedRrvbNameRegion.HasValue
                              && _regionHasRrvbName
                              && _lastRrvbNameDecodeTime != DateTime.MinValue
                              && (now - _lastRrvbNameDecodeTime).TotalMilliseconds <= stableGraceMs;

                needsScan = !(qrStable && guidStable && nameStable);
                reason = needsScan
                    ? $"qr={qrStable} guid={guidStable} name={nameStable}"
                    : "all regions stable";
                qrRegion = _lockedRegion;
            }

            if (needsScan)
            {
                TraceRrvb($"[RvBarcodeMonitor] Full-screen rescan: {reason}");

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
            else
            {
                TraceRrvb($"[RvBarcodeMonitor] Full-screen rescan skipped: {reason}");
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
