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

// ITtsProvider.cs
// Abstraction for all TTS synthesis backends.
//
// Providers synthesize to in-memory PCM. They do not create temporary WAV files.
// The cache layer is responsible for the first on-disk write, which is the
// final cached WAV or OGG file.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed class VoiceInfo
{
    public string VoiceId      { get; init; } = string.Empty;
    public string Name         { get; init; } = string.Empty;
    public string Description  { get; init; } = string.Empty;
    public string Language     { get; init; } = string.Empty;
    public Gender Gender       { get; init; }
}

public sealed class PcmAudio
{
    public PcmAudio(float[] samples, int sampleRate, int channels = 1)
    {
        Samples = samples ?? Array.Empty<float>();
        SampleRate = sampleRate;
        Channels = channels <= 0 ? 1 : channels;
    }

    /// <summary>
    /// Interleaved floating-point PCM samples in the range [-1, 1].
    /// Mono providers use Channels = 1.
    /// </summary>
    public float[] Samples { get; }

    public int SampleRate { get; }

    public int Channels { get; }
}

public interface ITtsProvider : IDisposable
{
    /// <summary>Stable identifier included in the cache key.</summary>
    string ProviderId { get; }

    /// <summary>Human-readable name shown in the settings UI.</summary>
    string DisplayName { get; }

    /// <summary>True if this provider is available on the current platform.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// If true, the playback coordinator waits for all chunks to arrive before
    /// synthesis regardless of the user's stream/batch setting.
    /// Set this for providers that produce better prosody with full-sentence input.
    /// </summary>
    bool RequiresFullText { get; }

    /// <summary>
    /// True when the provider supports Kokoro/Misaki-style inline pronunciation hints
    /// embedded directly in the text stream. Providers like WinRT should receive
    /// plain text and therefore return false.
    /// </summary>
    bool SupportsInlinePronunciationHints { get; }

    /// <summary>
    /// Synthesizes text phrase-by-phrase, yielding PCM as each phrase completes.
    /// Allows the coordinator to begin caching and playback of the first phrase
    /// while later phrases are still synthesizing.
    ///
    /// Implementations that cannot stream should yield a single result identical
    /// to <see cref="SynthesizeAsync"/>.
    /// </summary>
    IAsyncEnumerable<(PcmAudio audio, int phraseIndex, int phraseCount)> SynthesizePhraseStreamAsync(
        string text,
        VoiceSlot slot,
        string tempDirectory,
        CancellationToken ct);

    /// <summary>
    /// Synthesizes text to in-memory PCM.
    /// Providers must not create temporary files here.
    /// </summary>
    Task<PcmAudio> SynthesizeAsync(
        string text,
        VoiceSlot slot,
        CancellationToken ct);

    /// <summary>
    /// Returns the resolved voice identifier for the given slot — the actual voice ID
    /// string (e.g. "bm_lewis" or "mix:am_adam:0.2|bm_lewis:0.8") that will be used
    /// for synthesis. Used as the cache key component so that changing a voice
    /// assignment produces a natural cache miss without requiring a full cache clear.
    /// </summary>
    string ResolveVoiceId(VoiceSlot slot);

    /// <summary>
    /// Returns the full VoiceProfile for the given slot, including DSP settings and
    /// the DisableChunking flag. Returns null if no profile is configured (caller
    /// should use defaults). The coordinator uses this to apply DSP post-synthesis
    /// and to decide whether to pass text through the TextSplitter.
    /// </summary>
    VoiceProfile? ResolveProfile(VoiceSlot slot);

    /// <summary>
    /// Returns the voices available for this provider on this platform.
    /// The UI uses this to populate the accent group voice assignment grid.
    /// </summary>
    IReadOnlyList<VoiceInfo> GetAvailableVoices();
}
