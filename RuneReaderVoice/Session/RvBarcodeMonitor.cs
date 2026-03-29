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
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using System.Text;
using ZXing;
using ZXing.Common;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.Platform;
using ZXing.QrCode;


namespace RuneReaderVoice.Session;
// Shim for Marshal.Copy — needs using System.Runtime.InteropServices
using System.Runtime.InteropServices;

public sealed class RvBarcodeMonitor : IDisposable
{
    
    // Our statics
    private BarcodeReaderGeneric multiReader = new ZXing.BarcodeReaderGeneric();
    private BarcodeReaderGeneric singleReader = new ZXing.BarcodeReaderGeneric();

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
    public event Action<Mat>? OnRegionCaptured;
    public event Action<Rect>? OnLockedRegionChanged;
    

    // ── Configuration ─────────────────────────────────────────────────────────

    public int CaptureIntervalMs   { get; set; } = 5;
    public int ReScanIntervalMs    { get; set; } = 5000;
    public int SourceGoneThresholdMs { get; set; } = 2000;

    // ── Internal state ────────────────────────────────────────────────────────

    private readonly IScreenCaptureProvider _capture;

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
    //private QRCodeDetector  _QRCodeDetector  = new QRCodeDetector();
    
    public RvBarcodeMonitor(IScreenCaptureProvider capture)
    {
        _capture = capture;
       // Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        multiReader.Options.Hints.Add(DecodeHintType.CHARACTER_SET, "ISO-8859-1");
        
        multiReader.Options.PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE };
        multiReader.Options.Hints.Add(DecodeHintType.TRY_HARDER, true);
        multiReader.Options.Hints.Add(DecodeHintType.PURE_BARCODE, false);
      //  multiReader.Options.Hints.Add(DecodeHintType.POSSIBLE_FORMATS, new List<BarcodeFormat> { BarcodeFormat.QR_CODE });

        singleReader.Options.Hints.Add(DecodeHintType.CHARACTER_SET, "ISO-8859-1");
        singleReader.Options.PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE };
        singleReader.Options.Hints.Add(DecodeHintType.TRY_HARDER, true);
        singleReader.Options.Hints.Add(DecodeHintType.PURE_BARCODE, true);
      //  singleReader.Options.Hints.Add(DecodeHintType.POSSIBLE_FORMATS, new List<BarcodeFormat> { BarcodeFormat.QR_CODE });
       // OpenCvSharp.QRCodeDetector  = new QRCodeDetector();
  
        // _reader = new BarcodeReaderGeneric
        // {
        //     Options = new DecodingOptions
        //     {
        //         PossibleFormats    = new[] { BarcodeFormat.QR_CODE },
        //         TryHarder          = true,
        //         TryInverted        = false,
        //         ReturnCodabarStartEnd = false,
        //     },
        //     AutoRotate = false,
        // };
    }

    public void TrySetInitialLockedRegion(SavedBarcodeRegion? saved)
    {
        if (saved == null) return;

        var clamped = ClampRegionToScreen(new Rect(saved.X, saved.Y, saved.Width, saved.Height));
        if (!clamped.HasValue) return;

        lock (_gate)
        {
            if (_captureTask is { IsCompleted: false }) return;
            _lockedRegion = clamped.Value;
        }
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
            _cts                 = new CancellationTokenSource();
            _rvQrFound           = false;
            _sourceGoneSignalled = false;

            var token    = _cts.Token;
            _captureTask  = CaptureLoopAsync(token);
            _reScanTask   = ReScanLoopAsync(token);
            _sourceGoneTask = SourceGoneLoopAsync(token);
        }
    }
    private int _frameCount = 0;
    private void CheckIfWeShouldGC()
    {
        if (_frameCount++ > 10)
        {
            GC.Collect();
            _frameCount = 0;
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
                Rect? lockedRegion;
                lock (_gate)
                    lockedRegion = _lockedRegion;

                if (lockedRegion.HasValue)
                {
                    _capture.EnableRegion  = true;
                    _capture.CaptureRegion = lockedRegion.Value;
                    _capture.CaptureOnce();
                }



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
        Mat _frame = frame.Clone();
        try
        {
            if (_frame.Empty()) return;

             OnFrameCaptured?.Invoke(_frame);

            // DecodeMultiple: handle 1 or 2 QR codes on screen simultaneously
            // ZXing.Net BarcodeReaderGeneric can decode one at a time.
            // For multi-decode, we use the LuminanceSource + HybridBinarizer approach.
            var results = DecodeMultiple(_frame);
            if (results == null || results.Length == 0) return;
            foreach (var result in results)
            {
               // var raw = GetPacketRaw(result);
                //if (raw == null) continue;
                if (string.IsNullOrEmpty(result.Text)) continue;  
                // Filter by "RV" magic prefix — only process RuneReaderVoice packets
                var packet = RvPacket.TryParse(Base45Simple.DecodeUtf8(result.Text));
                if (packet == null) continue;

                // Discard preview packets
                if (packet.IsPreview) continue;

                // Update region lock from barcode bounding box
                UpdateRegionLock(result);

                lock (_gate)
                {
                    _rvQrFound = true;
                    _lastRvDecodeTime = DateTime.UtcNow;
                    _sourceGoneSignalled = false;
                }
               // This only finds the barcode.  it doesn't process it.
               // OnPacketDecoded?.Invoke(packet);
            }
        }
        finally
        {
            _frame.Dispose();
            if (!frame.IsDisposed)
                frame.Dispose();
            CheckIfWeShouldGC();
        }
    }

    public void ProcessFrameRegion(Mat frame)
    {
        Mat _frame = frame.Clone();
        try
        {
            if (_frame.Empty()) return;

            OnRegionCaptured?.Invoke(_frame);

            // DecodeMultiple: handle 1 or 2 QR codes on screen simultaneously
            // ZXing.Net BarcodeReaderGeneric can decode one at a time.
            // For multi-decode, we use the LuminanceSource + HybridBinarizer approach.
            var results = DecodeSingle(_frame);  // this needs to be updated as it should only ever return a single barcode.
            if (results == null || results.Length == 0) return;
//            foreach (var result in results)
 //           {
                var raw = GetPacketRaw(results);
                if (raw == null) return;

                // Filter by "RV" magic prefix — only process RuneReaderVoice packets
                var packet = RvPacket.TryParse(raw);
                if (packet == null) return;

                // Discard preview packets
                if (packet.IsPreview) return;

                // Update region lock from barcode bounding box
             //   UpdateRegionLock(results);

                lock (_gate)
                {
                    _rvQrFound = true;
                    _lastRvDecodeTime = DateTime.UtcNow;
                    _sourceGoneSignalled = false;
                }

                OnPacketDecoded?.Invoke(packet);
   //         }
        }
        finally
        {
            _frame.Dispose();
            if (!frame.IsDisposed)
                frame.Dispose();
            CheckIfWeShouldGC();
            
        }
    }
    
    private static string? GetPacketRaw(Result? result)
    {
        if (result == null)
            return null;

        if (result.RawBytes is { Length: > 0 })
        {
            try
            {
                return Encoding.Latin1.GetString(result.RawBytes);
            }
            catch
            {
                // fall back to ZXing text below
            }
        }

        return result.Text;
    }

    private static string? GetPacketRaw(string result)
    {
        if (string.IsNullOrEmpty(result))
            return null;


        return result;
    }
    
    
    private List<string> tempStringHolder = new List<string>();
    // reuseable mem buffer for capture.   better to update mem then buildup/teardown
    private byte[] _fullScanBuffer = new byte[1];
    // May want to use OpenCv's QRDecodeer since its faster than ZXing's
    private Result[]? DecodeMultiple(Mat frame)
    {
        Mat gray = new Mat();
        try
        {
            // Convert Mat to ZXing LuminanceSource
            // Using grayscale byte array approach
            gray = frame.Channels() == 1 ? frame.Clone() : frame.CvtColor(ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, gray, 20, 255, ThresholdTypes.Binary);

            if (_fullScanBuffer.Length != gray.Rows * gray.Cols)
            {
                Array.Clear(_fullScanBuffer);
                _fullScanBuffer = new byte[gray.Rows * gray.Cols];

            }

            //var bytes  = new byte[gray.Rows * gray.Cols];
            Marshal.Copy(gray.Data, _fullScanBuffer, 0, _fullScanBuffer.Length);

            var luminance = new ZXing.RGBLuminanceSource(_fullScanBuffer, gray.Cols, gray.Rows,
                ZXing.RGBLuminanceSource.BitmapFormat.Gray8);

            var decResult = multiReader.DecodeMultiple(luminance);
           
            return decResult;
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
    
    // reuseable mem buffer for capture.   better to update mem then buildup/teardown
    private byte[] _singleScanBuffer = new byte[1];
    // this needs to be re done so it doesn't do MultiDecode.  
    // Maybe even use OpenCV's qr decoder since its faster than ZXing.  will need to try it out.
    private string DecodeSingle(Mat frame)
    {
        Mat gray = new Mat();
        try
        {
            // Convert Mat to ZXing LuminanceSource
            // Using grayscale byte array approach
             gray = frame.Channels() == 1 ? frame.Clone() : frame.CvtColor(ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, gray, 20, 255, ThresholdTypes.Binary);
            
            // using var scaled = new Mat();
            // Cv2.Resize(gray, scaled, new Size(), 2.0, 2.0, InterpolationFlags.Nearest);
            //
            //
            using var padded = new Mat();
            Cv2.CopyMakeBorder(
                gray,
                padded,
                50, 50, 50, 50,
                BorderTypes.Constant,
                Scalar.White);


            if (_singleScanBuffer.Length != padded.Rows * padded.Cols)
            {
                Array.Clear(_singleScanBuffer);
                _singleScanBuffer = new byte[padded.Rows * padded.Cols];
            
            }
            
            
            Marshal.Copy(padded.Data, _singleScanBuffer, 0, _singleScanBuffer.Length);
            
            var luminance = new ZXing.RGBLuminanceSource(_singleScanBuffer, padded.Cols, padded.Rows,
                ZXing.RGBLuminanceSource.BitmapFormat.Gray8);

            //Cv2.ImShow("bmp", padded);

            var results = singleReader.DecodeMultiple(luminance);
            //string decodeResult = _QRCodeDetector.DetectAndDecode(padded, out Point2f[] points, null);
            var result = results != null ?  results.First()  : null;
            
            if (result != null && !tempStringHolder.Contains(Base45Simple.DecodeUtf8(result.Text)))
            {
                tempStringHolder.Add(Base45Simple.DecodeUtf8(result.Text));
                Debug.WriteLine($@" {Base45Simple.DecodeUtf8(result.Text)} ");
            }

            return result != null ? Base45Simple.DecodeUtf8(result.Text) : null;
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
    
    
    // private void UpdateRegionLock(Result result)
    // {
    //     if (result.ResultPoints == null || result.ResultPoints.Length < 3) return;
    //
    //     float minX = result.ResultPoints.Min(p => p.X);
    //     float minY = result.ResultPoints.Min(p => p.Y);
    //     float maxX = result.ResultPoints.Max(p => p.X);
    //     float maxY = result.ResultPoints.Max(p => p.Y);
    //
    //     const int Margin = 20;
    //     var candidate = new Rect(
    //         Math.Max(0, (int)minX - Margin),
    //         Math.Max(0, (int)minY - Margin),
    //         (int)(maxX - minX) + Margin * 2,
    //         (int)(maxY - minY) + Margin * 2);
    //
    //     var clamped = ClampRegionToScreen(candidate);
    //     if (!clamped.HasValue) return;
    //
    //     var changed = false;
    //     lock (_gate)
    //     {
    //         if (!_lockedRegion.HasValue || !_lockedRegion.Value.Equals(clamped.Value))
    //         {
    //             _lockedRegion = clamped.Value;
    //             changed = true;
    //         }
    //     }
    //
    //     if (changed)
    //         OnLockedRegionChanged?.Invoke(clamped.Value);
    // }

    
    private void UpdateRegionLock(Result result)
    {
        if (result.ResultPoints == null || result.ResultPoints.Length < 3)
            return;

        float minX = result.ResultPoints.Min(p => p.X);
        float minY = result.ResultPoints.Min(p => p.Y);
        float maxX = result.ResultPoints.Max(p => p.X);
        float maxY = result.ResultPoints.Max(p => p.Y);

        const int padding = 30; // 10–20 px is reasonable

        int screenWidth =   _capture.ScreenWidth; // whatever your actual source is
        int screenHeight =  _capture.ScreenHeight; // whatever your actual source is

        int left   = Math.Max(0, (int)Math.Floor(minX) - padding);
        int top    = Math.Max(0, (int)Math.Floor(minY) - padding);
        int right  = Math.Min(screenWidth,  (int)Math.Ceiling(maxX) + padding);
        int bottom = Math.Min(screenHeight, (int)Math.Ceiling(maxY) + padding);

        int width = right - left;
        int height = bottom - top;

        if (width <= 0 || height <= 0)
            return;

        var clamped = new Rect(left, top, width, height);

        var changed = false;
        lock (_gate)
        {
            if (!_lockedRegion.HasValue || !_lockedRegion.Value.Equals(clamped))
            {
                _lockedRegion = clamped;
                changed = true;
            }
        }

        if (changed)
            OnLockedRegionChanged?.Invoke(clamped);
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
            lock (_gate) 
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
            lock (_gate)
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

