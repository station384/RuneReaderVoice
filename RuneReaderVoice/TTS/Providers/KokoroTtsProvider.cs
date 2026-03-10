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

using System;
using System.Collections.Generic;
using KokoroSharp;
using KokoroSharp.Core;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed partial class KokoroTtsProvider : ITtsProvider
{
    // ── State ─────────────────────────────────────────────────────────────────

    private KokoroTTS? _tts;
    private bool _initialized;
    private bool _initializing;
    private bool _disposed;
    private readonly object _initLock = new();

    private readonly Dictionary<VoiceSlot, string> _voiceAssignments = new();
    private readonly Dictionary<string, KokoroVoice> _voiceCache = new();

    // ── Runtime-configurable options ──────────────────────────────────────────

    /// <summary>
    /// When true (default), SynthesizePhraseStreamAsync splits text via TextSplitter
    /// and streams one WAV per phrase — playback starts on the first phrase while
    /// subsequent phrases encode in parallel.
    /// When false, the full segment is synthesized as a single phrase — better
    /// prosody continuity, higher initial latency.
    /// Can be toggled live without recreating the provider.
    /// </summary>
    public bool EnablePhraseChunking { get; set; } = true;

    // ── ITtsProvider ──────────────────────────────────────────────────────────

    public string ProviderId => "kokoro";
    public string DisplayName => "Kokoro (Local ONNX)";
    public bool IsAvailable => true;
    public bool RequiresFullText => true;
    public bool SupportsInlinePronunciationHints => true;
}