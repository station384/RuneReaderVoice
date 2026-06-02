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
using RuneReaderVoice.Diagnostics;

namespace RuneReaderVoice.Session;

// RvBarcodeMonitor.cs
// Continuously captures screen frames and scans for RuneReader Voice barcodes.
//
// Channels:
//   - QR: primary dialog/text channel. Payload is Base45 -> RV protocol packet.
//   - RRVB identity: combined side-channel metadata identified by decoded prefix "RRVX-".
//     Payload format: RRVX-G=<guid>;N=<name>.
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
    private string _pendingRrvbGuid = string.Empty;
    private int _fullScreenRrvbMissCount = 0;
    private int _lockedRrvbRegionMissCount = 0;
    private const int MaxFullScreenRrvbMisses = 5; // clear stale lock after this many consecutive full-screen misses
    private const int MaxLockedRrvbRegionMisses = 3; // clear stale locked crop after this many failed region reads
    private string _pendingRrvbName = string.Empty;
    private int _pendingRrvbIdentityCount;
    private DateTime _pendingRrvbIdentityTime = DateTime.MinValue;
    private bool _sourceGoneSignalled;

    private readonly object _gate = new();
    private readonly object _captureIoGate = new();
    private bool _disposed;

    private const string RrvbIdentityPrefix = "RRVX-";
    private const string RrvbGuidFieldPrefix = "G=";
    private const string RrvbNameFieldMarker = ";N=";

    // RRVB is a visual side-channel; single-frame reads can flip one field on
    // antialias/crop noise. Accept an identity only after seeing the same
    // GUID+name twice in a short window. QR remains primary, so a one-frame
    // metadata delay is safer than poisoning the current dialog identity.
    private const int RrvbIdentityDebounceRequiredReads = 2;
    private const int RrvbIdentityDebounceWindowMs = 750;

    // Full-screen scans are only needed to acquire or recover lost regions.
    // Once region polling is actively decoding all channels, keep full-screen
    // capture off. This avoids expensive 5-second rescans while stable barcodes
    // remain on screen.
    private const int RegionStableGraceMultiplier = 3;


    private static void TraceRrvb(string message)
    {
        RrvDebug.RrvbDebug(message);
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
        // RRVX uses one combined identity barcode. Legacy name-region state is ignored.
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
            ResetPendingRrvbIdentityLocked();
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
                lock (_gate)
                {
                    qrRegion = _lockedRegion;
                    rrvbGuidRegion = _lockedRrvbGuidRegion;
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


                    if (!qrRegion.HasValue && !rrvbGuidRegion.HasValue)
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
        try
        {
            if (frame.Empty()) return;

            OnFrameCaptured?.Invoke(frame);

            var qrResults = DecodeQrMultiple(frame);
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

            var rrvbResults = DecodeRrvbMultiple(frame);
            var identity = SelectBestRrvbIdentity(rrvbResults);
            if (identity != null)
            {
                _fullScreenRrvbMissCount = 0;
                lock (_gate)
                    _lockedRrvbRegionMissCount = 0;
                UpdateRegionLock(identity.Result, RegionKind.RrvbGuid);
                RecordRrvbIdentity(identity.Guid, identity.Name);
            }
            else
            {
                // If full-screen scan keeps failing to decode RRVB while a locked
                // region exists, the region is stale (barcode moved). Clear it so
                // the capture loop stops polling the wrong position.
                _fullScreenRrvbMissCount++;
                if (_fullScreenRrvbMissCount >= MaxFullScreenRrvbMisses)
                {
                    bool cleared = false;
                    lock (_gate)
                    {
                        if (_lockedRrvbGuidRegion.HasValue)
                        {
                            _lockedRrvbGuidRegion = null;
                            _lockedRrvbNameRegion = null;
                            _lockedRrvbRegionMissCount = 0;
                            _regionHasRrvbGuid    = false;
                            _regionHasRrvbName    = false;
                            cleared = true;
                        }
                    }
                    if (cleared)
                    {
                        _fullScreenRrvbMissCount = 0;
                        TraceRrvb($"[RRVB] Stale locked region cleared after {MaxFullScreenRrvbMisses} full-screen misses");
                    }
                }
            }
        }
        finally
        {
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

        try
        {
            if (frame.Empty()) return;

            if (kind == RegionKind.Qr)
                OnRegionCaptured?.Invoke(frame);

            if (kind == RegionKind.RrvbGuid)
            {
                ProcessRrvbIdentityRegion(frame);
                return;
            }

            ProcessQrRegion(frame);
        }
        finally
        {
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

    private void ProcessRrvbIdentityRegion(Mat frame)
    {
        var identity = SelectBestRrvbIdentity(DecodeMultipleRrvb(frame, ref _singleRrvbScanBuffer, pad: 20));
        if (identity == null)
        {
            bool cleared = false;
            lock (_gate)
            {
                _regionHasRrvbGuid = false;
                _regionHasRrvbName = false;
                ResetPendingRrvbIdentityLocked();

                _lockedRrvbRegionMissCount++;
                if (_lockedRrvbRegionMissCount >= MaxLockedRrvbRegionMisses && _lockedRrvbGuidRegion.HasValue)
                {
                    _lockedRrvbGuidRegion = null;
                    _lockedRrvbNameRegion = null;
                    _lockedRrvbRegionMissCount = 0;
                    cleared = true;
                }
            }

            if (cleared)
                TraceRrvb($"[RRVB] Stale locked region cleared after {MaxLockedRrvbRegionMisses} failed region reads");

            return;
        }

        lock (_gate)
            _lockedRrvbRegionMissCount = 0;

        RecordRrvbIdentity(identity.Guid, identity.Name);
    }

    private void RecordRrvbIdentity(string guid, string name)
    {
        var accepted = false;
        var acceptedGuid = string.Empty;
        var acceptedName = string.Empty;

        lock (_gate)
        {
            var now = DateTime.UtcNow;

            // Raw RRVB identity was decoded from the locked region, so keep region
            // stability fresh even while debounce is waiting for a second read.
            _regionHasRrvbGuid = true;
            _regionHasRrvbName = true;
            _lastRrvbDecodeTime = now;
            _lastRrvbGuidDecodeTime = now;
            _lastRrvbNameDecodeTime = now;

            if (string.Equals(_lastRrvbGuid, guid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_lastRrvbName, name, StringComparison.Ordinal))
            {
                // Already accepted. Times above are still refreshed so full-screen
                // rescans remain suppressed while the barcode is stable.
                return;
            }

            var samePending = string.Equals(_pendingRrvbGuid, guid, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(_pendingRrvbName, name, StringComparison.Ordinal);
            var pendingFresh = _pendingRrvbIdentityTime != DateTime.MinValue &&
                               (now - _pendingRrvbIdentityTime).TotalMilliseconds <= RrvbIdentityDebounceWindowMs;

            if (!samePending || !pendingFresh)
            {
                _pendingRrvbGuid = guid;
                _pendingRrvbName = name;
                _pendingRrvbIdentityCount = 1;
                _pendingRrvbIdentityTime = now;
                TraceRrvb($"[RRVB] Pending identity {guid} / {name}");
                return;
            }

            _pendingRrvbIdentityCount++;
            _pendingRrvbIdentityTime = now;
            if (_pendingRrvbIdentityCount < RrvbIdentityDebounceRequiredReads)
                return;

            _lastRrvbGuid = guid;
            _lastRrvbName = name;
            acceptedGuid = guid;
            acceptedName = name;
            accepted = true;
            ResetPendingRrvbIdentityLocked();
        }

        if (!accepted) return;

        TraceRrvb($"[RRVB] Identity {acceptedGuid} / {acceptedName}");
        OnRrvbGuidDecoded?.Invoke(acceptedGuid);
        OnRrvbNameDecoded?.Invoke(acceptedName);
    }

    private void ResetPendingRrvbIdentityLocked()
    {
        _pendingRrvbGuid = string.Empty;
        _pendingRrvbName = string.Empty;
        _pendingRrvbIdentityCount = 0;
        _pendingRrvbIdentityTime = DateTime.MinValue;
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

    private sealed record RrvbIdentity(Result Result, string Guid, string Name);

    private static RrvbIdentity? SelectBestRrvbIdentity(Result[]? results)
    {
        if (results is not { Length: > 0 }) return null;

        RrvbIdentity? best = null;
        foreach (var result in results)
        {
            var parsed = TryExtractRrvbIdentity(result);
            if (parsed == null) continue;

            if (best == null || parsed.Result.Text.Length > best.Result.Text.Length)
                best = parsed;
        }

        return best;
    }

    private static RrvbIdentity? TryExtractRrvbIdentity(Result result)
    {
        var text = result.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!text.StartsWith(RrvbIdentityPrefix, StringComparison.Ordinal)) return null;

        var body = text[RrvbIdentityPrefix.Length..];
        if (!body.StartsWith(RrvbGuidFieldPrefix, StringComparison.Ordinal)) return null;

        var nameMarkerIndex = body.IndexOf(RrvbNameFieldMarker, StringComparison.Ordinal);
        if (nameMarkerIndex < RrvbGuidFieldPrefix.Length) return null;

        var guid = body[RrvbGuidFieldPrefix.Length..nameMarkerIndex].Trim();
        var name = body[(nameMarkerIndex + RrvbNameFieldMarker.Length)..].Trim();

        if (string.IsNullOrWhiteSpace(guid)) return null;
        if (string.IsNullOrWhiteSpace(name)) return null;

        return new RrvbIdentity(result, guid, name);
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

    /// <summary>
    /// Full-screen RRVB detection.
    /// Uses vertical morphology to find candidate regions containing repeating
    /// guard bars, then decodes each candidate crop individually.
    /// </summary>
    private Result[]? DecodeRrvbMultiple(Mat frame)
    {
        try
        {
            var candidates = FindRrvbCandidateRects(frame);
            TraceRrvb($"[RRVB] Full-screen candidates={candidates.Count} frame={frame.Cols}x{frame.Rows}");
            if (candidates.Count == 0) return null;

            var allResults = new List<Result>();
            foreach (var rect in candidates)
            {
                using var crop    = new Mat(frame, rect);
                var       decoded = DecodeMultipleRrvb(crop, ref _fullRrvbScanBuffer, pad: 0);
                if (decoded == null) continue;
                // Offset result points back to full-frame coordinates.
                foreach (var r in decoded)
                {
                    var pts     = r.ResultPoints ?? Array.Empty<ResultPoint>();
                    var shifted = pts.Select(p => new ResultPoint(p.X + rect.X, p.Y + rect.Y)).ToArray();
                    allResults.Add(new Result(r.Text, null, shifted, r.BarcodeFormat));
                }
            }
            return allResults.Count > 0 ? allResults.ToArray() : null;
        }
        catch (Exception ex)
        {
            TraceRrvb($"[RRVB] DecodeRrvbMultiple error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Finds RRVB candidate bounding rects by first making the barcode ink a
    /// single connected component, then decoding every plausible component.
    ///
    /// Important project invariant:
    ///   Threshold must match the QR path exactly: 20, BinaryInv.
    ///
    /// With BinaryInv, dark RRVB bars become white foreground for OpenCV.
    /// The 3x3 morphology below is only for candidate discovery; decoding
    /// still uses the original frame/crop.
    /// </summary>
    private static List<Rect> FindRrvbCandidateRects(Mat frame)
    {
        var results = new List<Rect>();
        Mat? gray = null, inkMask = null, panelMask = null, labels = null, stats = null, centroids = null;
        try
        {
            gray = frame.Channels() == 1 ? frame.Clone() : frame.CvtColor(ColorConversionCodes.BGR2GRAY);
            inkMask = new Mat();

            // Keep the currently verified monitor threshold path.
            // BinaryInv: dark barcode/world pixels become white foreground.
            Cv2.Threshold(gray, inkMask, 10, 255, ThresholdTypes.BinaryInv);

            // Region acquisition should find the framed light barcode panel, not dark ink/world blobs.
            // Invert the ink mask so light panel/background becomes white, then remove anything
            // white-connected to the screen edge. Enclosed white islands are candidate panels.
            panelMask = BuildInteriorPanelMask(inkMask);

            labels = new Mat();
            stats = new Mat();
            centroids = new Mat();
            int count = Cv2.ConnectedComponentsWithStats(
                panelMask,
                labels,
                stats,
                centroids,
                PixelConnectivity.Connectivity4,
                MatType.CV_32S);

            double frameArea = (double)frame.Rows * frame.Cols;
            int minWidth = 120;
            int minHeight = 40;
            int minArea = Math.Max(1000, (int)(frameArea * 0.00010));
            int maxArea = (int)(frameArea * 0.20);

            TraceRrvb($"[RRVB] Panel components found={count - 1} minArea={minArea} maxArea={maxArea}");

            for (int i = 1; i < count; i++) // component 0 is background
            {
                int x = stats.Get<int>(i, (int)ConnectedComponentsTypes.Left);
                int y = stats.Get<int>(i, (int)ConnectedComponentsTypes.Top);
                int w = stats.Get<int>(i, (int)ConnectedComponentsTypes.Width);
                int h = stats.Get<int>(i, (int)ConnectedComponentsTypes.Height);
                int area = stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);

                if (area < minArea || area > maxArea) continue;
                if (w < minWidth || h < minHeight) continue;

                double aspect = w / (double)Math.Max(1, h);
                if (aspect < 0.45 || aspect > 10.0) continue;

                var rect = new Rect(x, y, w, h);
                if (!HasBarcodeInkPattern(inkMask, rect)) continue;

                // Component bounds are the barcode panel. Do not pad.
                results.Add(rect);

                TraceRrvb($"[RRVB] Candidate rect=({rect.X},{rect.Y},{rect.Width},{rect.Height}) panelArea={area} aspect={aspect:F2}");
            }
        }
        finally
        {
            gray?.Dispose();
            inkMask?.Dispose();
            panelMask?.Dispose();
            labels?.Dispose();
            stats?.Dispose();
            centroids?.Dispose();
        }

        return results
            .OrderByDescending(r => r.Width * r.Height)
            .Take(12)
            .ToList();
    }

    private static Mat BuildInteriorPanelMask(Mat inkMask)
    {
        var panelMask = new Mat();
        Cv2.BitwiseNot(inkMask, panelMask);

        // Kill all light regions connected to the image edge. The RRVB panel is framed/enclosed,
        // so it should remain as an interior white island.
        using var floodMask = new Mat(
            panelMask.Rows + 2,
            panelMask.Cols + 2,
            MatType.CV_8UC1,
            Scalar.Black);

        void KillEdgePoint(int x, int y)
        {
            if (x < 0 || y < 0 || x >= panelMask.Cols || y >= panelMask.Rows)
                return;

            if (panelMask.At<byte>(y, x) == 0)
                return;

            Cv2.FloodFill(panelMask, floodMask, new Point(x, y), Scalar.Black);
        }

        for (int x = 0; x < panelMask.Cols; x++)
        {
            KillEdgePoint(x, 0);
            KillEdgePoint(x, panelMask.Rows - 1);
        }

        for (int y = 0; y < panelMask.Rows; y++)
        {
            KillEdgePoint(0, y);
            KillEdgePoint(panelMask.Cols - 1, y);
        }

        return panelMask;
    }

    private static bool HasBarcodeInkPattern(Mat inkMask, Rect panel)
    {
        const int inset = 2;
        if (panel.Width <= inset * 2 || panel.Height <= inset * 2)
            return false;

        var inner = new Rect(
            panel.X + inset,
            panel.Y + inset,
            panel.Width - inset * 2,
            panel.Height - inset * 2);

        using var roi = new Mat(inkMask, inner);

        int ink = Cv2.CountNonZero(roi);
        double inkRatio = ink / (double)Math.Max(1, roi.Width * roi.Height);

        // Barcode panel should contain ink, but not be mostly dark world/noise.
        if (inkRatio < 0.02 || inkRatio > 0.60)
            return false;

        int strongColumns = 0;
        int minColumnInk = Math.Max(3, (int)Math.Round(roi.Height * 0.10));

        for (int x = 0; x < roi.Width; x++)
        {
            using var col = roi.Col(x);
            if (Cv2.CountNonZero(col) >= minColumnInk)
                strongColumns++;
        }

        return strongColumns >= 20;
    }

    private string? DecodeRrvbSingle(Mat frame)
    {
        var result = DecodeMultipleRrvb(frame, ref _singleRrvbScanBuffer, pad: 20)?.FirstOrDefault();
        return result?.Text;
    }

    private static Mat PrepareMatForDecode(Mat frame, int pad, out Mat toDispose)
    {
        var gray = frame.Channels() == 1 ? frame.Clone() : frame.CvtColor(ColorConversionCodes.BGR2GRAY);
        Cv2.Threshold(gray, gray, 20, 255, ThresholdTypes.Binary);

        if (pad > 0)
        {
            var padded = new Mat();
            Cv2.CopyMakeBorder(gray, padded, pad, pad, pad, pad, BorderTypes.Constant, Scalar.White);
            gray.Dispose();
            toDispose = padded;
            return padded;
        }

        toDispose = gray;
        return gray;
    }

    private static Result[]? DecodeMultipleRrvb(Mat frame, ref byte[] buffer, int pad)
    {
        try
        {
            var source = PrepareMatForDecode(frame, pad, out var toDispose);
            try
            {
                return RrvBarcodeDetector.Detect(source);
            }
            finally
            {
                toDispose.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    private static Result[]? DecodeMultiple(Mat frame, BarcodeReaderGeneric reader, ref byte[] buffer, int pad)
    {
        try
        {
            var source = PrepareMatForDecode(frame, pad, out var toDispose);
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
                toDispose.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    private void UpdateRegionLock(Result result, RegionKind kind)
    {
        if (result.ResultPoints == null || result.ResultPoints.Length < 2)
            return;

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var p in result.ResultPoints)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        const int padding = 30;
        var minHeight = (kind == RegionKind.RrvbGuid) ? 80 : 0;

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
        {
            OnLockedRrvbGuidRegionChanged?.Invoke(clamped);
            OnLockedRrvbNameRegionChanged?.Invoke(clamped);
        }
        else
        {
            OnLockedRegionChanged?.Invoke(clamped);
        }
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

                var identityStable = _lockedRrvbGuidRegion.HasValue
                                  && _regionHasRrvbGuid
                                  && _regionHasRrvbName
                                  && _lastRrvbDecodeTime != DateTime.MinValue
                                  && (now - _lastRrvbDecodeTime).TotalMilliseconds <= stableGraceMs;

                needsScan = !(qrStable && identityStable);
                reason = needsScan
                    ? $"qr={qrStable} rrvx={identityStable}"
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
