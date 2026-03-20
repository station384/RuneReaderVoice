// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// AppServices.cs
// Simple service locator that holds references to all live components.
// Used by the Avalonia UI to bind to live state without constructor injection
// through the Avalonia application lifecycle.

using System;
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
    public static NpcRaceOverrideDb              NpcOverrides           { get; private set; } = null!;
    public static NpcSyncService                 NpcSync                { get; private set; } = null!;

    // ── SQLite back-end (single shared DB) ───────────────────────────────────
    public static RvrDb                Db                 { get; private set; } = null!;
    public static PronunciationRuleStore PronunciationRules { get; private set; } = null!;
    public static TextSwapRuleStore      TextSwapRules      { get; private set; } = null!;

    public static string    LastDecodedText   { get; set; } = string.Empty;
    public static string    LastProcessedText { get; set; } = string.Empty;
    public static string    LastTextSpoken    { get; set; } = string.Empty;
    public static VoiceSlot LastRuntimeSlot   { get; set; } = VoiceSlot.Narrator;

    public static string OperationStatus { get; private set; } = string.Empty;
    public static event Action<string>? OperationStatusChanged;

    public static void SetOperationStatus(string status)
    {
        OperationStatus = status ?? string.Empty;
        OperationStatusChanged?.Invoke(OperationStatus);
    }

    public static void ClearOperationStatus() => SetOperationStatus(string.Empty);

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
        NpcRaceOverrideDb npcOverrides,
        NpcSyncService npcSync,
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
        NpcOverrides           = npcOverrides;
        NpcSync                = npcSync;
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

    public static void SetPronunciationProcessor(DialoguePronunciationProcessor processor)
        => PronunciationProcessor = processor;

    public static void SetTextSwapProcessor(DialogueTextSwapProcessor processor)
        => TextSwapProcessor = processor;
}
