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

using System.Collections.Generic;
using KokoroSharp;
using KokoroSharp.Core;

namespace RuneReaderVoice.TTS.Providers;

public sealed partial class KokoroTtsProvider : ITtsProvider
{
    private KokoroTTS? _tts;
    private bool _initialized;
    private bool _initializing;
    private bool _disposed;
    private readonly object _initLock = new();

    // Changed from slot->string to slot->profile
    private readonly Dictionary<Protocol.VoiceSlot, VoiceProfile> _voiceProfiles = new();
    private readonly Dictionary<string, KokoroVoice> _voiceCache = new();

    public bool EnablePhraseChunking { get; set; } = true;

    public string ProviderId => "kokoro";
    public string DisplayName => "Kokoro (Local ONNX)";
    public bool IsAvailable => true;
    public bool RequiresFullText => true;
    public bool SupportsInlinePronunciationHints => true;
}