// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

// RvBarcodeMonitor.cs
// Continuously captures screen frames and scans for RV QR codes.
//
// Two QR codes may appear simultaneously on screen:
//   - RuneReaderVoice TTS QR (identified by "RV" magic prefix)
//   - RuneReader combat QR (different magic prefix)
// Uses ZXing DecodeMultiple on all decoded results, filters by "RV" prefix.
// Single-QR (RV only) is the normal operating path.
//
// Region locking:
//   On first successful RV decode, the bounding box of the QR code is recorded.
//   Subsequent captures are restricted to that region (with clamping to screen bounds).
//   A full-screen rescan runs every ReScanIntervalMs when no RV QR is found.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using ZXing;
using ZXing.Common;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.Platform;

namespace RuneReaderVoice.Session;
// Shim for Marshal.Copy — needs using System.Runtime.InteropServices
using System.Runtime.InteropServices;

public sealed class RvBarcodeMonitor : IDisposable
{
    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fires when a valid (non-preview) RV packet is decoded.</summary>
    public event Action<RvPacket>? OnPacketDecoded;

    /// <summary>
    /// Fires when no RV QR has been seen for SourceGoneThresholdMs.
    /// The caller should call TtsSessionAssembler.SignalSourceGone().
    /// </summary>
    public event Action? OnSourceGone;

    /// <summary>Fires with the latest full-screen Mat for the UI preview.</summary>
    public event Action<Mat>? OnFrameCaptured;

    // ── Configuration ─────────────────────────────────────────────────────────

    public int CaptureIntervalMs   { get; set; } = 5;
    public int ReScanIntervalMs    { get; set; } = 5000;
    public int SourceGoneThresholdMs { get; set; } = 2000;

    // ── Internal state ────────────────────────────────────────────────────────

    private readonly IScreenCaptureProvider _capture;
    private readonly BarcodeReaderGeneric _reader;

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Task? _reScanTask;
    private Task? _sourceGoneTask;

    private bool _rvQrFound;
    private Rect? _lockedRegion;
    private DateTime _lastRvDecodeTime = DateTime.MinValue;
    private bool _sourceGoneSignalled;

    private readonly object _gate = new();
    private bool _disposed;

    public RvBarcodeMonitor(IScreenCaptureProvider capture)
    {
        _capture = capture;

        _reader = new BarcodeReaderGeneric
        {
            Options = new DecodingOptions
            {
                PossibleFormats    = new[] { BarcodeFormat.QR_CODE },
                TryHarder          = true,
                TryInverted        = false,
                ReturnCodabarStartEnd = false,
            },
            AutoRotate = false,
        };
    }

    // ── Control ───────────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_gate)
        {
            if (_captureTask is { IsCompleted: false }) return;
            _cts         = new CancellationTokenSource();
            _rvQrFound   = false;
            _lockedRegion = null;
            _sourceGoneSignalled = false;

            var token    = _cts.Token;
            _captureTask  = CaptureLoopAsync(token);
            _reScanTask   = ReScanLoopAsync(token);
            _sourceGoneTask = SourceGoneLoopAsync(token);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        lock (_gate) { cts = _cts; _cts = null; }
        if (cts == null) return;

        await cts.CancelAsync();
        var tasks = new[] { _captureTask, _reScanTask, _sourceGoneTask }
            .Where(t => t != null)
            .Select(t => t!);
        try { await Task.WhenAll(tasks); }
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
                // Apply locked region if available
                if (_lockedRegion.HasValue)
                {
                    _capture.EnableRegion  = true;
                    _capture.CaptureRegion = _lockedRegion.Value;
                }
                else
                {
                    _capture.EnableFullScreen = true;
                }

                _capture.CaptureOnce();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RvBarcodeMonitor] Capture error: {ex.Message}");
            }

            try { await Task.Delay(CaptureIntervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Frame processing (called by IScreenCaptureProvider.OnFullScreenUpdated) ──

    public void ProcessFrame(Mat frame)
    {
        if (frame.Empty()) return;

        OnFrameCaptured?.Invoke(frame);

        // DecodeMultiple: handle 1 or 2 QR codes on screen simultaneously
        // ZXing.Net BarcodeReaderGeneric can decode one at a time.
        // For multi-decode, we use the LuminanceSource + HybridBinarizer approach.
        var results = DecodeMultiple(frame);
        if (results == null || results.Length == 0) return;
        foreach (var result in results)
        {
            if (result?.Text == null) continue;

            // Filter by "RV" magic prefix — only process RuneReaderVoice packets
            var packet = RvPacket.TryParse(result.Text);
            if (packet == null) continue;

            // Discard preview packets
            if (packet.IsPreview) continue;

            // Update region lock from barcode bounding box
            UpdateRegionLock(result);

            lock (_gate)
            {
                _rvQrFound         = true;
                _lastRvDecodeTime  = DateTime.UtcNow;
                _sourceGoneSignalled = false;
            }

            OnPacketDecoded?.Invoke(packet);
        }
    }

    private Result[]? DecodeMultiple(Mat frame)
    {
        try
        {
            // Convert Mat to ZXing LuminanceSource
            // Using grayscale byte array approach
            using var gray = frame.Channels() == 1
                ? frame.Clone()
                : frame.CvtColor(ColorConversionCodes.BGR2GRAY);

            var bytes  = new byte[gray.Rows * gray.Cols];
            Marshal.Copy(gray.Data, bytes, 0, bytes.Length);

            var luminance  = new ZXing.RGBLuminanceSource(bytes, gray.Cols, gray.Rows,
                ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
            // var binarizer  = new HybridBinarizer(luminance);
            // var bitmap     = new BinaryBitmap(binarizer);
            
            
            // ZXing.Net multi-decode via QRCodeMultiReader
            var multiReader = new ZXing.BarcodeReaderGeneric();
            multiReader.Options.Hints.Add(DecodeHintType.CHARACTER_SET, "ISO-8859-1");
            multiReader.Options.Hints.Add(DecodeHintType.TRY_HARDER, true);
            multiReader.Options.Hints.Add(DecodeHintType.PURE_BARCODE, false);
            
            multiReader.Options.Hints.Add(DecodeHintType.POSSIBLE_FORMATS, new List<BarcodeFormat> { BarcodeFormat.QR_CODE });
            var decResult = multiReader.DecodeMultiple(luminance);
            return decResult;
        }
        catch
        {
            return null;
        }
    }

    private void UpdateRegionLock(Result result)
    {
        if (result.ResultPoints == null || result.ResultPoints.Length < 3) return;

        float minX = result.ResultPoints.Min(p => p.X);
        float minY = result.ResultPoints.Min(p => p.Y);
        float maxX = result.ResultPoints.Max(p => p.X);
        float maxY = result.ResultPoints.Max(p => p.Y);

        // Add margin and clamp to screen bounds
        const int Margin = 20;
        int x = Math.Max(0, (int)minX - Margin);
        int y = Math.Max(0, (int)minY - Margin);
        int w = (int)(maxX - minX) + Margin * 2;
        int h = (int)(maxY - minY) + Margin * 2;

        // Clamp to screen dimensions
        w = Math.Min(w, _capture.ScreenWidth  - x);
        h = Math.Min(h, _capture.ScreenHeight - y);

        if (w > 0 && h > 0)
            _lockedRegion = new Rect(x, y, w, h);
    }

    // ── Full-screen rescan loop ───────────────────────────────────────────────

    private async Task ReScanLoopAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(ReScanIntervalMs, ct); }
            catch (OperationCanceledException) { break; }

            bool needsScan;
            //lock (_gate) 
                needsScan = !_rvQrFound;

            if (needsScan)
            {
                // Force a full-screen capture to search for the QR frame
                _lockedRegion = null;
                _capture.EnableFullScreen = true;
                _capture.EnableRegion     = false;
                _capture.CaptureOnce();
            }
        }
    }

    // ── Source-gone detection loop ────────────────────────────────────────────

    private async Task SourceGoneLoopAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(200, ct); }
            catch (OperationCanceledException) { break; }

            bool shouldSignal;
           // lock (_gate)
            {
                var elapsed = (DateTime.UtcNow - _lastRvDecodeTime).TotalMilliseconds;
                shouldSignal = _rvQrFound
                              && !_sourceGoneSignalled
                              && elapsed > SourceGoneThresholdMs;
                if (shouldSignal)
                {
                    _rvQrFound           = false;
                    _lockedRegion        = null;
                    _sourceGoneSignalled = true;
                }
            }

            if (shouldSignal) OnSourceGone?.Invoke();
        }
    }

    // public void Dispose()
    // {
    //     if (_disposed) return;
    //     _disposed = true;
    //     StopAsync().GetAwaiter().GetResult();
    // }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    
        CancellationTokenSource? cts;
        lock (_gate) 
        { cts = _cts; _cts = null; }
        cts?.Cancel();
        cts?.Dispose();
    
        // Give background tasks a moment to see the cancellation
        _captureTask?.Wait(500);
        _reScanTask?.Wait(500);
        _sourceGoneTask?.Wait(500);
    }
}

