// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

using System.Diagnostics;

namespace RuneReaderVoice.Diagnostics;

/// <summary>
/// Central debug-output router for high-volume diagnostic messages.
/// Calls are DEBUG-only and category-gated so normal debug sessions stay readable.
/// </summary>
internal static class RrvDebug
{
    public const bool RaceVoice = false;
    public const bool TextPipeline = false;
    public const bool PlayerSplit = false;
    public const bool NpcQuickSet = false;
    public const bool NpcSync = false;
    public const bool Cache = false;
    public const bool Rrvb = false;
    public const bool Playback = false;
    public const bool Assembler = false;
    public const bool RemoteTts = false;
    public const bool MainWindow = false;
    public const bool Voices = false;
    public const bool Perf = false;

    [Conditional("DEBUG")]
    public static void Write(bool enabled, string category, string message)
    {
        if (!enabled)
            return;

        Debug.WriteLine($"[{category}] {message}");
    }

    [Conditional("DEBUG")]
    public static void RaceVoiceDebug(string message) => Write(RaceVoice, "RaceVoiceDebug", message);

    [Conditional("DEBUG")]
    public static void TextPipelineDebug(string message) => Write(TextPipeline, "TextPipeline", message);

    [Conditional("DEBUG")]
    public static void PlayerSplitDebug(string message) => Write(PlayerSplit, "PlayerSplit", message);

    [Conditional("DEBUG")]
    public static void NpcQuickSetDebug(string message) => Write(NpcQuickSet, "NpcQuickSet", message);

    [Conditional("DEBUG")]
    public static void NpcSyncDebug(string message) => Write(NpcSync, "NpcSync", message);

    [Conditional("DEBUG")]
    public static void CacheDebug(string message) => Write(Cache, "Cache", message);

    [Conditional("DEBUG")]
    public static void RrvbDebug(string message) => Write(Rrvb, "RRVB", message);

    [Conditional("DEBUG")]
    public static void PlaybackDebug(string message) => Write(Playback, "PC", message);

    [Conditional("DEBUG")]
    public static void AssemblerDebug(string message) => Write(Assembler, "Assembler", message);

    [Conditional("DEBUG")]
    public static void RemoteTtsDebug(string message) => Write(RemoteTts, "RemoteTTS", message);

    [Conditional("DEBUG")]
    public static void MainWindowDebug(string message) => Write(MainWindow, "MainWindow", message);

    [Conditional("DEBUG")]
    public static void VoicesDebug(string message) => Write(Voices, "VoicesTab", message);

    [Conditional("DEBUG")]
    public static void PerfDebug(string message) => Write(Perf, "PERF", message);
}
