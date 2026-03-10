// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

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