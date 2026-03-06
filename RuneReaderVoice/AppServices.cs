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


// AppServices.cs
// Simple service locator that holds references to all live components.
// Used by the Avalonia UI to bind to live state without constructor injection
// through the Avalonia application lifecycle.

using RuneReaderVoice.Platform;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Cache;
using RuneReaderVoice.TTS.Providers;
using RuneReaderVoice.TTS.Audio;
using RuneReaderVoice.Session;

namespace RuneReaderVoice;

public static class AppServices
{
    public static VoiceUserSettings    Settings    { get; private set; } = new();
    public static IVoicePlatformServices Platform  { get; private set; } = null!;
    public static ITtsProvider         Provider    { get; private set; } = null!;
    public static TtsAudioCache        Cache       { get; private set; } = null!;
    public static IAudioPlayer         Player      { get; private set; } = null!;
    public static TtsSessionAssembler  Assembler   { get; private set; } = null!;
    public static PlaybackCoordinator  Coordinator { get; private set; } = null!;
    public static RvBarcodeMonitor     Monitor     { get; private set; } = null!;

    public static void Initialize(
        VoiceUserSettings settings,
        IVoicePlatformServices platform,
        ITtsProvider provider,
        TtsAudioCache cache,
        IAudioPlayer player,
        TtsSessionAssembler assembler,
        PlaybackCoordinator coordinator,
        RvBarcodeMonitor monitor)
    {
        Settings    = settings;
        Platform    = platform;
        Provider    = provider;
        Cache       = cache;
        Player      = player;
        Assembler   = assembler;
        Coordinator = coordinator;
        Monitor     = monitor;
    }
}
