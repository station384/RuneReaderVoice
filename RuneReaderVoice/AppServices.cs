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

// AppServices.cs
// Simple service locator that holds references to all live components.
// Used by the Avalonia UI to bind to live state without constructor injection
// through the Avalonia application lifecycle.

using System;
using System.Collections.Generic;
using RuneReaderVoice.Data;
using RuneReaderVoice.Platform;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.Session;
using RuneReaderVoice.Sync;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Audio;
using RuneReaderVoice.TTS.Cache;
using RuneReaderVoice.TTS.Pronunciation;
using RuneReaderVoice.TTS.Providers;
using RuneReaderVoice.TTS.TextSwap;

namespace RuneReaderVoice;

public enum MainActivityKind
{
    None,
    Playing,
    Waiting,
    Generating,
    Capturing,
    UpdateAvailable,
}

public readonly record struct MainActivityState(MainActivityKind Kind, string Headline, string Detail)
{
    public static MainActivityState None => new(MainActivityKind.None, string.Empty, string.Empty);
    public bool IsActive => Kind != MainActivityKind.None;
}

public static class AppServices
{
    public static VoiceUserSettings              Settings               { get; private set; } = new();
    public static IVoicePlatformServices         Platform               { get; private set; } = null!;
    public static ITtsProvider                   Provider               { get; private set; } = null!;
    public static ProviderRegistry               ProviderRegistry       { get; set; } = null!;
    public static TtsAudioCache                  Cache                  { get; private set; } = null!;
    public static IAudioPlayer                   Player                 { get; private set; } = null!;
    public static TtsSessionAssembler            Assembler              { get; private set; } = null!;
    public static PlaybackCoordinator            Coordinator            { get; private set; } = null!;
    public static RvBarcodeMonitor               Monitor                { get; private set; } = null!;
    public static DialoguePronunciationProcessor PronunciationProcessor { get; private set; } = null!;
    public static DialogueTextSwapProcessor      TextSwapProcessor      { get; private set; } = null!;
    public static TextNormalizer                 TextNormalizer         { get; private set; } = new();
    public static NpcRaceOverrideDb              NpcOverrides           { get; private set; } = null!;
    public static NpcSyncService                 NpcSync                { get; private set; } = null!;
    public static UpdateService                  Updater                { get; private set; } = null!;
    public static NpcPeopleCatalogService        NpcPeopleCatalog       { get; private set; } = null!;
    public static ProviderSlotProfileStore       ProviderSlotProfiles   { get; private set; } = null!;

    // ── SQLite back-end (single shared DB) ───────────────────────────────────
    public static RvrDb                Db                 { get; private set; } = null!;
    public static PronunciationRuleStore PronunciationRules { get; private set; } = null!;
    public static TextSwapRuleStore      TextSwapRules      { get; private set; } = null!;

    public static string    LastDecodedText   { get; set; } = string.Empty;
    public static string    LastProcessedText { get; set; } = string.Empty;
    public static string    LastTextSpoken    { get; set; } = string.Empty;
    public static VoiceSlot LastRuntimeSlot   { get; set; } = VoiceSlot.Narrator;

    public static string CurrentPlayerName  { get; set; } = string.Empty;
    public static string CurrentPlayerRealm { get; set; } = string.Empty;
    public static string CurrentPlayerClass { get; set; } = string.Empty;
    public static string CurrentPlayerTitle { get; set; } = string.Empty;

    public static string OperationStatus { get; private set; } = string.Empty;
    public static event Action<string>? OperationStatusChanged;

    private static readonly object _mainActivityLock = new();
    private static MainActivityState _playbackActivity = MainActivityState.None;
    private static MainActivityState _generationActivity = MainActivityState.None;
    private static MainActivityState _captureActivity = MainActivityState.None;
    private static MainActivityState _updateActivity = MainActivityState.None;
    public static event Action<MainActivityState>? MainActivityChanged;

    public static void SetOperationStatus(string status)
    {
        OperationStatus = status ?? string.Empty;
        OperationStatusChanged?.Invoke(OperationStatus);
    }

    public static void ClearOperationStatus() => SetOperationStatus(string.Empty);

    public static void SetPlaybackActivity(MainActivityKind kind, string headline, string? detail = null)
    {
        lock (_mainActivityLock)
            _playbackActivity = new(kind, headline ?? string.Empty, detail ?? string.Empty);
        RaiseMainActivityChanged();
    }

    public static void ClearPlaybackActivity()
    {
        lock (_mainActivityLock)
            _playbackActivity = MainActivityState.None;
        RaiseMainActivityChanged();
    }

    public static void SetGenerationActivity(string headline, string? detail = null)
    {
        lock (_mainActivityLock)
            _generationActivity = new(MainActivityKind.Generating, headline ?? string.Empty, detail ?? string.Empty);
        RaiseMainActivityChanged();
    }

    public static void ClearGenerationActivity()
    {
        lock (_mainActivityLock)
            _generationActivity = MainActivityState.None;
        RaiseMainActivityChanged();
    }

    public static void SetCaptureActivity(string headline, string? detail = null)
    {
        lock (_mainActivityLock)
            _captureActivity = new(MainActivityKind.Capturing, headline ?? string.Empty, detail ?? string.Empty);
        RaiseMainActivityChanged();
    }

    public static void ClearCaptureActivity()
    {
        lock (_mainActivityLock)
            _captureActivity = MainActivityState.None;
        RaiseMainActivityChanged();
    }

    public static void SetUpdateActivity(string headline, string? detail = null)
    {
        lock (_mainActivityLock)
            _updateActivity = new(MainActivityKind.UpdateAvailable, headline ?? string.Empty, detail ?? string.Empty);
        RaiseMainActivityChanged();
    }

    public static void ClearUpdateActivity()
    {
        lock (_mainActivityLock)
            _updateActivity = MainActivityState.None;
        RaiseMainActivityChanged();
    }

    public static MainActivityState GetResolvedMainActivity()
    {
        lock (_mainActivityLock)
        {
            if (_playbackActivity.IsActive)
            {
                var detail = !string.IsNullOrWhiteSpace(_generationActivity.Headline)
                    ? _generationActivity.Headline
                    : _playbackActivity.Detail;
                return _playbackActivity with { Detail = detail ?? string.Empty };
            }

            if (_generationActivity.IsActive)
            {
                var detail = !string.IsNullOrWhiteSpace(_captureActivity.Headline)
                    ? _captureActivity.Headline
                    : _generationActivity.Detail;
                return _generationActivity with { Detail = detail ?? string.Empty };
            }

            if (_captureActivity.IsActive)
            {
                var detail = !string.IsNullOrWhiteSpace(_updateActivity.Headline)
                    ? _updateActivity.Headline
                    : _captureActivity.Detail;
                return _captureActivity with { Detail = detail ?? string.Empty };
            }

            if (_updateActivity.IsActive)
                return _updateActivity;

            return MainActivityState.None;
        }
    }

    private static void RaiseMainActivityChanged()
    {
        MainActivityChanged?.Invoke(GetResolvedMainActivity());
    }

    /// <summary>
    /// The most recently completed segment. Updated by TtsSessionAssembler
    /// via OnSegmentComplete. The UI reads this to populate the "Last NPC" panel.
    /// </summary>
    public static AssembledSegment? LastSegment { get; set; }

    public static void Initialize(
        VoiceUserSettings settings,
        IVoicePlatformServices platform,
        ITtsProvider provider,
        TtsAudioCache cache,
        IAudioPlayer player,
        TtsSessionAssembler assembler,
        PlaybackCoordinator coordinator,
        RvBarcodeMonitor monitor,
        DialoguePronunciationProcessor pronunciationProcessor,
        DialogueTextSwapProcessor textSwapProcessor,
        TextNormalizer textNormalizer,
        NpcRaceOverrideDb npcOverrides,
        NpcSyncService npcSync,
        UpdateService updater,
        NpcPeopleCatalogService npcPeopleCatalog,
        ProviderSlotProfileStore providerSlotProfiles,
        RvrDb db,
        PronunciationRuleStore pronunciationRules,
        TextSwapRuleStore textSwapRules,
        ProviderRegistry providerRegistry)
    {
        Settings               = settings;
        Platform               = platform;
        Provider               = provider;
        Cache                  = cache;
        Player                 = player;
        Assembler              = assembler;
        Coordinator            = coordinator;
        Monitor                = monitor;
        PronunciationProcessor = pronunciationProcessor;
        TextSwapProcessor      = textSwapProcessor;
        TextNormalizer         = textNormalizer;
        NpcOverrides           = npcOverrides;
        NpcSync                = npcSync;
        Updater                = updater;
        NpcPeopleCatalog        = npcPeopleCatalog;
        ProviderSlotProfiles    = providerSlotProfiles;
        Db                     = db;
        PronunciationRules     = pronunciationRules;
        TextSwapRules          = textSwapRules;
        ProviderRegistry       = providerRegistry;
    }

    /// <summary>
    /// Hot-swaps the active TTS provider at runtime (called from the UI when the
    /// user changes provider). Also rewires the coordinator to use the new provider.
    /// The caller is responsible for disposing the old provider after this returns.
    /// </summary>
    public static void SwapProvider(ITtsProvider newProvider)
    {
        Provider = newProvider;
        Coordinator.SetProvider(newProvider);
    }

    public static void SwapNpcSync(NpcSyncService newNpcSync)
        => NpcSync = newNpcSync;

    public static void SetPronunciationProcessor(DialoguePronunciationProcessor processor)
        => PronunciationProcessor = processor;

    public static void SetTextSwapProcessor(DialogueTextSwapProcessor processor)
        => TextSwapProcessor = processor;

    public static bool TryGetStoredVoiceProfile(string providerId, VoiceSlot slot, out VoiceProfile? profile)
    {
        profile = null;
        var slotId = slot.ToString();
        var ok = ProviderSlotProfiles != null
            && ProviderSlotProfiles.TryGetProfile(providerId, slotId, out profile)
            && profile != null;

#if DEBUG
        Console.WriteLine($"[RaceVoiceDebug] TryGetStoredVoiceProfile provider={providerId} slot={slotId} hit={ok} voiceId={(profile?.VoiceId ?? "<null>")}");
#endif
        return ok;
    }

    public static bool TryGetStoredSampleProfile(string providerId, string sampleId, out VoiceProfile? profile)
    {
        profile = null;
        return ProviderSlotProfiles != null
            && ProviderSlotProfiles.TryGetSampleProfile(providerId, sampleId, out profile)
            && profile != null;
    }
}
