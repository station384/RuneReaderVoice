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

// Program.cs
// RuneReader Voice entry point.
// Wires platform services, TTS providers, cache, audio player, assembler,
// coordinator, and Avalonia UI.

using System.IO;
using Avalonia;
using RuneReaderVoice;
using RuneReaderVoice.Platform;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Cache;
using RuneReaderVoice.TTS.Providers;
using RuneReaderVoice.TTS.Audio;
using RuneReaderVoice.Session;
using RuneReaderVoice.TTS.Pronunciation;

// ── Load settings ─────────────────────────────────────────────────────────────
var settings = VoiceSettingsManager.LoadSettings();

// ── Platform services ─────────────────────────────────────────────────────────
var platform = VoicePlatformFactory.Create();

// ── TTS Provider ──────────────────────────────────────────────────────────────
ITtsProvider provider = settings.ActiveProvider switch
{
#if WINDOWS
    "winrt"   => new WinRtTtsProvider(),
#elif LINUX
    "piper"   => new LinuxPiperTtsProvider(
                     settings.PiperBinaryPath,
                     settings.PiperModelDirectory),
#endif
    "kokoro"  => new KokoroTtsProvider(),
    "cloud"   => new NotImplementedTtsProvider("cloud", "Cloud TTS"),
    _         => CreateDefaultProvider(settings),
};

// ── Apply saved voice assignments ────────────────────────────────────────────
#if WINDOWS
if (provider is WinRtTtsProvider winRtProvider)
{
    foreach (var (key, voiceId) in settings.VoiceAssignments)
        if (VoiceSlot.TryParse(key, out var slot))
            winRtProvider.SetVoice(slot, voiceId);
}
#endif

if (provider is KokoroTtsProvider kokoroProvider)
{
    foreach (var (key, voiceId) in settings.VoiceAssignments)
        if (VoiceSlot.TryParse(key, out var slot))
            kokoroProvider.SetVoice(slot, voiceId);
    kokoroProvider.EnablePhraseChunking = settings.EnablePhraseChunking;
}

// ── Audio cache ───────────────────────────────────────────────────────────────
var cacheDir = !string.IsNullOrWhiteSpace(settings.CacheDirectoryOverride)
    ? settings.CacheDirectoryOverride
    : VoiceSettingsManager.GetDefaultCacheDirectory();

var cache = new TtsAudioCache(
    cacheDir,
    maxSizeBytes:       settings.CacheSizeLimitBytes,
    compressionEnabled: settings.CompressionEnabled,
    oggQuality:         settings.OggQuality,
    silenceTrimEnabled: settings.SilenceTrimEnabled);

// ── Audio player ──────────────────────────────────────────────────────────────
IAudioPlayer player = CreateAudioPlayer();
player.Volume = settings.Volume;
player.Speed  = settings.PlaybackSpeed;
if (settings.AudioDeviceId != null)
    player.SetOutputDevice(settings.AudioDeviceId);

// ── Session assembler ─────────────────────────────────────────────────────────
var assembler   = new TtsSessionAssembler();
var pronunciationProcessor = new DialoguePronunciationProcessor(
    WowPronunciationRules.CreateDefault());




// ── Playback coordinator ──────────────────────────────────────────────────────
var tempDir     = Path.Combine(Path.GetTempPath(), "RuneReaderVoice");
var playbackMode = settings.PlaybackMode == "StreamOnFirstChunk"
    ? PlaybackMode.StreamOnFirstChunk
    : PlaybackMode.WaitForFullText;

var coordinator = new PlaybackCoordinator(
    provider, cache, player, playbackMode, tempDir);

coordinator.StartSession();

// Wire assembler → coordinator
//assembler.OnSegmentComplete += coordinator.EnqueueSegment;
assembler.OnSegmentComplete += seg =>
{
    var processed = pronunciationProcessor.Process(seg);
    coordinator.EnqueueSegment(processed);
};


assembler.OnSessionReset    += coordinator.OnSessionReset;

// ── Barcode monitor ───────────────────────────────────────────────────────────
var monitor = new RvBarcodeMonitor(platform.ScreenCapture);
monitor.CaptureIntervalMs      = settings.CaptureIntervalMs;
monitor.ReScanIntervalMs       = settings.ReScanIntervalMs;
monitor.SourceGoneThresholdMs  = settings.SourceGoneThresholdMs;

monitor.OnPacketDecoded += assembler.Feed;
monitor.OnSourceGone    += () =>
{
    assembler.SignalSourceGone();
    coordinator.OnSourceGone();
};

// Wire frame capture events to monitor
platform.ScreenCapture.OnFullScreenUpdated += monitor.ProcessFrame;
platform.ScreenCapture.OnRegionUpdated     += monitor.ProcessFrame;

// ── ESC hotkey ────────────────────────────────────────────────────────────────
platform.Hotkeys.EscPressed += coordinator.HandleEscPressed;
platform.Hotkeys.Start();

// ── Launch Avalonia UI ────────────────────────────────────────────────────────
// Pass all wired components into the app so the UI can bind to live state.
AppServices.Initialize(
    settings, platform, provider, cache, player,
    assembler, coordinator, monitor);

return AppBuilder
    .Configure<App>()
    .UsePlatformDetect()
    .WithInterFont()
    .LogToTrace()
    .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnMainWindowClose);

// ── Helpers ───────────────────────────────────────────────────────────────────

static ITtsProvider CreateDefaultProvider(VoiceUserSettings settings)
{
#if WINDOWS
    return new WinRtTtsProvider();
#elif LINUX
    return new LinuxPiperTtsProvider(settings.PiperBinaryPath, settings.PiperModelDirectory);
#else
    return new NotImplementedTtsProvider("none", "No provider available");
#endif
}

static IAudioPlayer CreateAudioPlayer()
{
#if WINDOWS
    return new WinRtAudioPlayer();
#elif LINUX
    return new GstAudioPlayer();
#else
    throw new PlatformNotSupportedException("No audio player for this platform.");
#endif
}