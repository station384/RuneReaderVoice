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

// IAudioPlayer.cs
// Abstraction for platform audio playback.
// Implementations: WasapiStreamAudioPlayer (#if WINDOWS), GstAudioPlayer (#if LINUX).
// The player consumes decoded PCM only. Cache/file decoding happens above the player.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.TTS.Audio;

public interface IAudioPlayer : IDisposable
{
    bool IsPlaying { get; }

    /// <summary>Plays decoded PCM audio.</summary>
    Task PlayAsync(PcmAudio audio, CancellationToken ct);

    /// <summary>
    /// Plays a sequence of decoded PCM chunks, preserving order.
    /// Playback begins on the first chunk without waiting for the rest.
    /// </summary>
    Task PlaylistPlayAsync(IAsyncEnumerable<PcmAudio> audioChunks, CancellationToken ct);

    /// <summary>Stops playback immediately.</summary>
    void Stop();

    /// <summary>Volume 0.0–1.0.</summary>
    float Volume { get; set; }

    /// <summary>Playback speed multiplier (0.75–1.5).</summary>
    float Speed { get; set; }

    /// <summary>
    /// Optional: set the audio output device by device ID.
    /// Pass null to use the system default.
    /// </summary>
    void SetOutputDevice(string? deviceId);

    /// <summary>Enumerates available audio output devices on this platform.</summary>
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
}

public sealed class AudioDeviceInfo
{
    public string DeviceId   { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public bool IsDefault    { get; init; }
}