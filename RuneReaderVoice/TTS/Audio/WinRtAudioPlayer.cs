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
//
// Single-file playback: PlayAsync — direct MediaSource, awaits MediaEnded.
//
// Multi-phrase gapless playback: PlaylistPlayAsync — uses MediaPlaybackList.
// Files are enqueued dynamically as encoding completes. WinRT pre-buffers the
// next item while the current one plays, eliminating the ~250ms gap between
// phrases that would otherwise occur from reinitializing the MediaPlayer.

using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;

namespace RuneReaderVoice.TTS.Audio;

[SupportedOSPlatform("windows10.0.14393.0")]
public sealed class WinRtAudioPlayer : IAudioPlayer
{
    private readonly MediaPlayer _player = new();
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
        get => _speed;
        set
        {
            _speed = (float)Math.Clamp(value, 0.75, 1.5);
            // NOTE: writing PlaybackRate mid-playlist can cause early termination
            // if the session is actively transitioning between items. Known edge case —
            // playback resumes correctly on the next dialog at the updated speed.
            _player.PlaybackSession.PlaybackRate = _speed;
        }
    }

    private float _speed = 1.0f;

    public WinRtAudioPlayer()
    {
        _player.MediaEnded  += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
    }

    // ── Single-file playback ──────────────────────────────────────────────────

    public async Task PlayAsync(string filePath, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        var source = MediaSource.CreateFromUri(new Uri(filePath));
        _player.Source = source;

        _playbackTcs = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var reg = ct.Register(() =>
        {
            _player.Pause();
            _playbackTcs?.TrySetCanceled();
        });

        _player.Play();
        _player.PlaybackSession.PlaybackRate = _speed;
        await _playbackTcs.Task;
    }

    // ── Gapless playlist playback ─────────────────────────────────────────────

    /// <summary>
    /// Plays a sequence of audio files gaplessly using MediaPlaybackList.
    /// Playback starts as soon as the first file is enqueued. Additional files
    /// are appended to the list as the async stream yields them — WinRT
    /// pre-buffers each next item during the current item's playback,
    /// eliminating the inter-phrase gap.
    ///
    /// Completes when the final item finishes playing or ct is cancelled.
    /// </summary>
    public async Task PlaylistPlayAsync(IAsyncEnumerable<string> filePaths, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();

        var list = new MediaPlaybackList
        {
            MaxPrefetchTime   = TimeSpan.FromSeconds(5),
            AutoRepeatEnabled = false,
        };

        // Re-apply speed on every item transition — WinRT can reset PlaybackRate
        // to 1.0 when the session changes between playlist items.
        // Named handler so we can unsubscribe in finally before _player is disposed.
        void OnItemChanged(MediaPlaybackList sender, CurrentMediaPlaybackItemChangedEventArgs args)
        {
            if (_disposed) return;
            try { _player.PlaybackSession.PlaybackRate = _speed; }
            catch (Exception) { /* player disposed during shutdown */ }
        }
        list.CurrentItemChanged += OnItemChanged;

        var tcs      = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int totalAdded  = 0;
        bool streamDone = false;
        var  listLock   = new object();

        // Fires when the list naturally runs out of items (last item finished).
        void OnPlayerEnded(MediaPlayer sender, object args)
        {
            bool shouldComplete;
            lock (listLock) shouldComplete = streamDone;
            if (shouldComplete) tcs.TrySetResult(true);
        }

        // Fires on cancellation
        using var reg = ct.Register(() =>
        {
            _player.Pause();
            tcs.TrySetCanceled();
        });

        _player.MediaEnded += OnPlayerEnded;
        _player.Source      = list;

        try
        {
            await foreach (var path in filePaths.WithCancellation(ct))
            {
                var item = new MediaPlaybackItem(
                    MediaSource.CreateFromUri(new Uri(path)));

                lock (listLock) totalAdded++;
                list.Items.Add(item);

                if (totalAdded == 1)
                {
                    _player.Play();
                    _player.PlaybackSession.PlaybackRate = _speed;
                }
            }

            // Stream exhausted — all phrases have been enqueued
            lock (listLock)
            {
                streamDone = true;
                // Edge case: player already fired MediaEnded before stream finished
                if (totalAdded == 0)
                {
                    tcs.TrySetResult(false);
                }
                else
                {
                    try
                    {
                        var state = _player.PlaybackSession.PlaybackState;
                        if (state == MediaPlaybackState.None ||
                            state == MediaPlaybackState.Paused)
                            tcs.TrySetResult(true);
                    }
                    catch (Exception) { tcs.TrySetResult(true); }
                }
            }

            await tcs.Task;
        }
        finally
        {
            list.CurrentItemChanged -= OnItemChanged;
            _player.MediaEnded      -= OnPlayerEnded;
        }
    }

    // ── Stop ──────────────────────────────────────────────────────────────────

    public void Stop()
    {
        _player.Pause();
        _playbackTcs?.TrySetResult(false);
        _playbackTcs = null;
    }

    // ── Device management ─────────────────────────────────────────────────────

    public void SetOutputDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
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
        var devices   = new List<AudioDeviceInfo>();
        var defaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
        var selector  = MediaDevice.GetAudioRenderSelector();
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

    // ── Player-level events (used by single-file PlayAsync only) ─────────────

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