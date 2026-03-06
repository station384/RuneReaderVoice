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

#if LINUX
// LinuxVoicePlatformServices.cs
// Linux implementation of IVoicePlatformServices.
// Screen capture: Wayland portal (PortalScreenCastSession + GStreamer appsink).
// Hotkeys: inputd connection (same as RuneReader).
//
// NOTE: inputd reports ESC keypresses but cannot suppress them.
// ESC will always reach the game on Linux. The app still calls Stop() on audio.
// True suppression deferred to a future runereaderd kernel-level update.

namespace RuneReaderVoice.Platform.Linux;

public sealed class LinuxVoicePlatformServices : IVoicePlatformServices
{
    public IScreenCaptureProvider ScreenCapture { get; }
    public IVoiceHotkeys Hotkeys { get; }

    public LinuxVoicePlatformServices()
    {
        ScreenCapture = new LinuxVoiceScreenCapture();
        Hotkeys       = new LinuxVoiceHotkeys();
    }

    public void Dispose()
    {
        Hotkeys.Dispose();
        ScreenCapture.Dispose();
    }
}

public sealed class LinuxVoiceHotkeys : IVoiceHotkeys
{
    public event Func<bool>? EscPressed;
    public bool IsStarted { get; private set; }

    // inputd connection — same IPC as RuneReader's LinuxInputdHotkeysClient
    // Wired here to report ESC. Cannot suppress the key.
    // TODO: connect InputdConnection, subscribe to key events, filter VK_ESCAPE,
    //       call EscPressed?.Invoke() and ignore the return value (can't suppress).

    public void Start()
    {
        // TODO: open InputdConnection, register ESC listener
        IsStarted = true;
    }

    public void Stop()
    {
        // TODO: close InputdConnection
        IsStarted = false;
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Thin wrapper around the Wayland PortalScreenCastSession + GStreamer appsink.
/// Mirrors WaylandScreenCaptureProvider from RuneReader.
/// TODO: refactor to share the implementation.
/// </summary>
public sealed class LinuxVoiceScreenCapture : IScreenCaptureProvider
{
    public int ScreenWidth  => 1920; // TODO: read from portal session
    public int ScreenHeight => 1080;

    public OpenCvSharp.Rect CaptureRegion { get; set; }
    public bool EnableRegion     { get; set; }
    public bool EnableFullScreen { get; set; } = true;

    public event Action<OpenCvSharp.Mat>? OnFullScreenUpdated;
    public event Action<OpenCvSharp.Mat>? OnRegionUpdated;

    public void CaptureOnce()
    {
        // TODO: pull frame from GStreamer appsink, convert to Mat, fire events.
    }

    public void Dispose() { }
}
#endif
