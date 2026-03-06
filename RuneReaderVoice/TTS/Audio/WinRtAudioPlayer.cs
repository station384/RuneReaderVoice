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

#if WINDOWS
// WinRtAudioPlayer.cs
// Audio playback via Windows.Media.Playback.MediaPlayer (WinRT).
// Handles WAV, MP3, OGG, AAC natively — no additional dependencies.
// Supports volume, playback speed, and audio device selection.

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.Media.Core;
using Windows.Media.Devices;


namespace RuneReaderVoice.TTS.Audio;

[SupportedOSPlatform("windows10.0.14393.0")]
public sealed class WinRtAudioPlayer : IAudioPlayer
{
    private readonly  MediaPlayer _player = new();
    private TaskCompletionSource<bool>? _playbackTcs;
    private bool _disposed;

    public bool IsPlaying =>
        _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public float Volume
    {
        get => (float)_player.Volume;
        set => _player.Volume = Math.Clamp(value, 0f, 1f);
    }

    public float Speed
    {
        get => (float)_player.PlaybackSession.PlaybackRate;
        set => _player.PlaybackSession.PlaybackRate = Math.Clamp(value, 0.75, 1.5);
    }

    public WinRtAudioPlayer()
    {
        _player.MediaEnded  += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
    }

    public async Task PlayAsync(string filePath, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop(); // ensure previous playback is stopped

        var uri    = new Uri(filePath);
        var source = MediaSource.CreateFromUri(uri);
        _player.Source = source;

        _playbackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reg = ct.Register(() =>
        {
            _player.Pause();
            _playbackTcs?.TrySetCanceled();
        });

        _player.Play();
        await _playbackTcs.Task;
    }

    public void Stop()
    {
        _player.Pause();
        _playbackTcs?.TrySetResult(false);
        _playbackTcs = null;
    }
    

    public void SetOutputDevice(string? deviceId)
    {
        if (deviceId == null || deviceId.Length == 0)
        {
            _player.AudioDevice = null;
            return;
        }

        var device = Windows.Devices.Enumeration.DeviceInformation
            .CreateFromIdAsync(deviceId)
            .GetAwaiter()
            .GetResult();

        _player.AudioDevice = device;
    }
    
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();

        // Get the default render device ID for comparison
        var defaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);

        // Enumerate all active audio render devices
        var selector = MediaDevice.GetAudioRenderSelector();
        var deviceList = Windows.Devices.Enumeration.DeviceInformation
            .FindAllAsync(selector)
            .GetAwaiter()
            .GetResult();

        foreach (var d in deviceList)
        {
            devices.Add(new AudioDeviceInfo
            {
                DeviceId   = d.Id,
                DeviceName = d.Name,
                IsDefault  = d.Id == defaultId,
            });
        }

        return devices;
    }
    
    // public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    // {
    //     // Full implementation: enumerate via Windows.Devices.Enumeration.
    //     // Stubbed for Phase 2.
    //     return new[]
    //     {
    //         new AudioDeviceInfo { DeviceId = "", DeviceName = "System Default", IsDefault = true }
    //     };
    // }

    private void OnMediaEnded(MediaPlayer sender, object args)
        => _playbackTcs?.TrySetResult(true);

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        => _playbackTcs?.TrySetException(new Exception(args.ErrorMessage));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _player.MediaEnded  -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        _player.Dispose();
    }
    

    
}
#endif
