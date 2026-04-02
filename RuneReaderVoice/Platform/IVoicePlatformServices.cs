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

namespace RuneReaderVoice.Platform;
// IVoicePlatformServices.cs
// Stripped-down platform abstraction for RuneReader Voice.
// No combat rotation, no key sending, no GCD logic.
// Only what TTS needs: screen capture and global hotkeys.
public interface IVoicePlatformServices : IDisposable
{
    IScreenCaptureProvider ScreenCapture { get; }
    IVoiceHotkeys Hotkeys { get; }
}

// Re-export IScreenCaptureProvider from RuneReader's platform layer
// (or define a minimal version here if not sharing the assembly).
// For now, reference the same interface shape.

public interface IScreenCaptureProvider : IDisposable
{
    int ScreenWidth  { get; }
    int ScreenHeight { get; }
    OpenCvSharp.Rect CaptureRegion    { get; set; }
    bool EnableRegion     { get; set; }
    bool EnableFullScreen { get; set; }
    void CaptureOnce();
    event Action<OpenCvSharp.Mat>? OnFullScreenUpdated;
    event Action<OpenCvSharp.Mat>? OnRegionUpdated;
}

/// <summary>
/// Hotkeys interface for Voice — only needs ESC (abort playback).
/// Additional hotkeys (pause, skip) can be added in Phase 2.
/// </summary>
public interface IVoiceHotkeys : IDisposable
{
    /// <summary>
    /// Fires when ESC is pressed.
    /// The subscriber (PlaybackCoordinator) returns true to consume the key,
    /// false to pass it through. On Linux, passthrough is always the result
    /// (inputd cannot suppress the key).
    /// </summary>
    event Func<bool>? EscPressed;

    void Start();
    void Stop();
    bool IsStarted { get; }
}