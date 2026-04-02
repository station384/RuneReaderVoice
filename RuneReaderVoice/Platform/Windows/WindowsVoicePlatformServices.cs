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

#if WINDOWS
// WindowsVoicePlatformServices.cs
// Windows implementation of IVoicePlatformServices.
// Screen capture: ScreenCapture.NET DX11.
// Hotkeys: WH_KEYBOARD_LL low-level keyboard hook.
//   ESC is consumed (return 1) if PlaybackCoordinator.HandleEscPressed() returns true.
//   ESC passes through (return CallNextHookEx) if idle.

using System;
using System.Diagnostics;
using System.DirectoryServices.ActiveDirectory;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RuneReaderVoice.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsVoicePlatformServices : IVoicePlatformServices
{
    public IScreenCaptureProvider ScreenCapture { get; }
    public IVoiceHotkeys Hotkeys { get; }

    public WindowsVoicePlatformServices()
    {
        ScreenCapture = new WindowsVoiceScreenCapture();
        Hotkeys       = new WindowsVoiceHotkeys();
    }

    public void Dispose()
    {
        Hotkeys.Dispose();
        ScreenCapture.Dispose();
    }
}

[SupportedOSPlatform("windows")]
public sealed class WindowsVoiceHotkeys : IVoiceHotkeys
{
    public event Func<bool>? EscPressed;
    public bool IsStarted { get; private set; }

    private static nint _hookId = nint.Zero;
    private static WindowsHookProc? _proc;

    private delegate nint WindowsHookProc(int nCode, nint wParam, nint lParam);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int VK_ESCAPE      = 0x1B;

    [DllImport("user32.dll")]
    private static extern nint SetWindowsHookEx(int idHook, WindowsHookProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? lpModuleName);

    private bool _keyConsumed = false;
    private nint HookCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && wParam == WM_KEYDOWN)
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VK_ESCAPE)
            {
                // Ask coordinator whether to consume this keypress
                _keyConsumed = EscPressed?.Invoke() ?? false;
                if (_keyConsumed)
                    return CallNextHookEx(0, nCode, wParam, lParam);
                    //return 1; // suppress — do not pass to game
                // else fall through to CallNextHookEx
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Start()
    {
        if (_hookId != nint.Zero) return;
        _proc   = HookCallback;
        using var process = Process.GetCurrentProcess();
        using var module  = process.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc,
            GetModuleHandle(module.ModuleName), 0);
        IsStarted = true;
    }

    public void Stop()
    {
        if (_hookId == nint.Zero) return;
        UnhookWindowsHookEx(_hookId);
        _hookId   = nint.Zero;
        _proc     = null;
        IsStarted = false;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Windows DX11 screen capture for RuneReader Voice.
/// Mirrors WindowsCaptureScreen from RuneReader — same ScreenCapture.NET pattern,
/// same unsafe Mat copy, same DXGI_ERROR_WAIT_TIMEOUT handling.
///
/// Both RuneReader and RuneReaderVoice can run simultaneously; each holds its own
/// DX11ScreenCaptureService instance — DXGI Desktop Duplication allows multiple
/// consumers on the same output.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsVoiceScreenCapture : IScreenCaptureProvider
{
    // ── DXGI wait-timeout detection (same helper as RuneReader) ───────────────

    private static bool IsDxgiWaitTimeout(Exception ex)
    {
        const int dxgiErrorWaitTimeout = unchecked((int)0x887A0027);
        if (ex.HResult == dxgiErrorWaitTimeout) return true;
        var inner = ex.InnerException;
        while (inner != null)
        {
            if (inner.HResult == dxgiErrorWaitTimeout) return true;
            inner = inner.InnerException;
        }
        return ex.Message.Contains("DXGI_ERROR_WAIT_TIMEOUT", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("887A0027",                StringComparison.OrdinalIgnoreCase);
    }

    // ── ScreenCapture.NET objects ─────────────────────────────────────────────

    private readonly ScreenCapture.NET.DX11ScreenCaptureService _service;
    private readonly ScreenCapture.NET.IScreenCapture           _screenCapture;
    private readonly ScreenCapture.NET.ICaptureZone             _zoneRegion;
    private readonly ScreenCapture.NET.ICaptureZone             _zoneFullScreen;

    private int  _disposed; // 0 = live, 1 = disposed
    private bool IsDisposed => System.Threading.Volatile.Read(ref _disposed) == 1;

    // ── IScreenCaptureProvider ────────────────────────────────────────────────

    public int ScreenWidth  { get; }
    public int ScreenHeight { get; }

    private OpenCvSharp.Rect _captureRegion;
    public OpenCvSharp.Rect CaptureRegion
    {
        get => _captureRegion;
        set
        {
            if (_captureRegion == value) return;

            // Clamp to screen bounds (same logic as RuneReader)
            int x = Math.Clamp(value.X, 0, ScreenWidth);
            int y = Math.Clamp(value.Y, 0, ScreenHeight);
            int w = Math.Clamp(value.Width,  0, ScreenWidth);
            int h = Math.Clamp(value.Height, 0, ScreenHeight);

            if (x + w > ScreenWidth)  x = ScreenWidth  - w;
            if (y + h > ScreenHeight) y = ScreenHeight - h;

            _captureRegion = new OpenCvSharp.Rect(x, y, w, h);
            _screenCapture.UpdateCaptureZone(_zoneRegion, x, y, w, h, downscaleLevel: 0);
        }
    }

    public bool EnableRegion     { get; set; }
    public bool EnableFullScreen { get; set; } = true;

    public event Action<OpenCvSharp.Mat>? OnRegionUpdated;
    public event Action<OpenCvSharp.Mat>? OnFullScreenUpdated;

    // ── Constructor ───────────────────────────────────────────────────────────

    public WindowsVoiceScreenCapture()
    {
        _service = new ScreenCapture.NET.DX11ScreenCaptureService();

        var gpus     = _service.GetGraphicsCards();
        var displays = _service.GetDisplays(gpus.First()).ToList();
        var display  = displays.First();

        _screenCapture = _service.GetScreenCapture(display);
        ScreenWidth    = display.Width;
        ScreenHeight   = display.Height;

        // Region zone — starts full-screen; CaptureRegion setter will resize it.
        _zoneRegion = _screenCapture.RegisterCaptureZone(
            0, 0, ScreenWidth, ScreenHeight, downscaleLevel: 0);
        _zoneRegion.AutoUpdate = false;
        _zoneRegion.Updated   += OnZoneRegionUpdated;

        // Full-screen zone — always full resolution.
        _zoneFullScreen = _screenCapture.RegisterCaptureZone(
            0, 0, ScreenWidth, ScreenHeight, downscaleLevel: 0);
        _zoneFullScreen.AutoUpdate = false;
        _zoneFullScreen.Updated   += OnZoneFullScreenUpdated;
    }

    // ── CaptureOnce ───────────────────────────────────────────────────────────

    public void CaptureOnce()
    {
        if (IsDisposed) return;

        if (EnableRegion)     _zoneRegion.RequestUpdate();
        if (EnableFullScreen) _zoneFullScreen.RequestUpdate();

        try
        {
            _screenCapture.CaptureScreen();
        }
        catch (Exception ex) when (IsDxgiWaitTimeout(ex))
        {
            // No new frame yet — benign, skip.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WindowsVoiceScreenCapture] CaptureScreen failed: {ex.Message}");
        }
    }

    // ── Zone callbacks ────────────────────────────────────────────────────────

    private unsafe void OnZoneRegionUpdated(object? sender, EventArgs e)
    {
        if (IsDisposed || !EnableRegion) return;
        var handler = OnRegionUpdated;
        if (handler == null) return;

        OpenCvSharp.Mat mat;
        using (_zoneRegion.Lock())
        {
            int stride = _zoneRegion.Width * _zoneRegion.ColorFormat.BytesPerPixel;
            var span   = _zoneRegion.RawBuffer;
            fixed (byte* ptr = span)
            {
                mat = OpenCvSharp.Mat
                    .FromPixelData(_zoneRegion.Height, _zoneRegion.Width,
                                   OpenCvSharp.MatType.CV_8UC4, (IntPtr)ptr, stride)
                    .Clone();
            }
        }
        handler.Invoke(mat);
    }

    private unsafe void OnZoneFullScreenUpdated(object? sender, EventArgs e)
    {
        if (IsDisposed || !EnableFullScreen) return;
        var handler = OnFullScreenUpdated;
        if (handler == null) return;

        OpenCvSharp.Mat mat;
        using (_zoneFullScreen.Lock())
        {
            int stride = _zoneFullScreen.Width * _zoneFullScreen.ColorFormat.BytesPerPixel;
            var span   = _zoneFullScreen.RawBuffer;
            fixed (byte* ptr = span)
            {
                mat = OpenCvSharp.Mat
                    .FromPixelData(_zoneFullScreen.Height, _zoneFullScreen.Width,
                                   OpenCvSharp.MatType.CV_8UC4, (IntPtr)ptr, stride)
                    .Clone();
            }
        }
        handler.Invoke(mat);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;

        try { _zoneRegion.Updated     -= OnZoneRegionUpdated;     } catch { /* ignore */ }
        try { _zoneFullScreen.Updated -= OnZoneFullScreenUpdated; } catch { /* ignore */ }

        try { _screenCapture.Dispose(); } catch { /* ignore */ }
        try { _service.Dispose();       } catch { /* ignore */ }
    }
}
#endif