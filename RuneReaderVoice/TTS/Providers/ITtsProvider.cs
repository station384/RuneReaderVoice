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
// Design: providers write synthesized audio to a file path.
// The cache layer sits between the provider and the playback layer.
// Providers never play audio directly.
//
// All providers synthesize to WAV internally. The cache layer handles
// OGG transcoding and silence trimming before writing to the cache.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed class VoiceInfo
{
    public string VoiceId   { get; init; } = string.Empty;
    public string Name      { get; init; } = string.Empty;
    public string Language  { get; init; } = string.Empty;
    public Gender Gender    { get; init; }
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
    /// Synthesizes text phrase-by-phrase, yielding a WAV file path as each
    /// phrase completes encoding. Allows the coordinator to begin playback of
    /// the first phrase while later phrases are still being synthesized.
    ///
    /// Implementations that cannot stream (e.g. WinRT, Piper) should yield a
    /// single result identical to SynthesizeToFileAsync — the coordinator
    /// handles both cases identically.
    ///
    /// Each yielded path is a temporary WAV. The coordinator owns the file
    /// after it is yielded and will move/delete it.
    ///
    /// phraseIndex is 0-based and monotonically increasing within a call.
    /// The full original text is passed as fullText for cache-key purposes.
    /// </summary>
    IAsyncEnumerable<(string wavPath, int phraseIndex, int phraseCount)> SynthesizePhraseStreamAsync(
        string text,
        VoiceSlot slot,
        string tempDirectory,
        CancellationToken ct);

    /// <summary>
    /// Synthesizes text and writes raw WAV audio to outputPath.
    /// Returns the path actually written (may differ in extension if the provider
    /// chooses a different format).
    /// The caller is responsible for post-processing (silence trim, OGG transcode).
    /// </summary>
    Task<string> SynthesizeToFileAsync(
        string text,
        VoiceSlot slot,
        string outputPath,
        CancellationToken ct);

    /// <summary>
    /// Returns the resolved voice identifier for the given slot — the actual voice ID
    /// string (e.g. "bm_lewis" or "mix:am_adam:0.2|bm_lewis:0.8") that will be used
    /// for synthesis. Used as the cache key component so that changing a voice
    /// assignment produces a natural cache miss without requiring a full cache clear.
    /// </summary>
    string ResolveVoiceId(VoiceSlot slot);

    /// <summary>
    /// Returns the voices available for this provider on this platform.
    /// The UI uses this to populate the accent group voice assignment grid.
    /// </summary>
    IReadOnlyList<VoiceInfo> GetAvailableVoices();
}