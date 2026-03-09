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

// VoiceUserSettings.cs
// All user-configurable settings for RuneReader Voice.
// Persisted as JSON in the portable app config directory.
// All config stays under the application folder tree.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice;

// ── Voice slot assignments ────────────────────────────────────────────────────
// Serializable dictionary: VoiceSlot string representation → voice ID string.
// Key format: "AccentGroup/Gender" (e.g. "Scottish/Male") or "Narrator"

public sealed class VoiceUserSettings
{
    // ── Basic settings ────────────────────────────────────────────────────────

    public bool TtsEnabled        { get; set; } = true;
    public string ActiveProvider  { get; set; } = "winrt"; // "winrt" | "piper" | "onnx" | "cloud"

    /// <summary>
    /// Voice assignments keyed first by provider ID then by VoiceSlot string.
    /// e.g. PerProviderVoiceAssignments["kokoro"]["Scottish/Male"] = "bm_george"
    /// Keeps WinRT, Kokoro, and Piper assignments fully independent.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> PerProviderVoiceAssignments { get; set; } = new();

    /// <summary>
    /// Convenience accessor — returns the assignment dict for the active provider,
    /// creating it on first access. All existing code that reads VoiceAssignments
    /// automatically scopes to the currently selected provider.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, string> VoiceAssignments
    {
        get
        {
            if (!PerProviderVoiceAssignments.TryGetValue(ActiveProvider, out var d))
            {
                d = new Dictionary<string, string>();
                PerProviderVoiceAssignments[ActiveProvider] = d;
            }
            return d;
        }
    }

    public string PlaybackMode    { get; set; } = "WaitForFullText"; // "WaitForFullText" | "StreamOnFirstChunk"

    /// <summary>
    /// When true, long segments are split into sentences/clauses and synthesized
    /// phrase-by-phrase — playback starts on the first phrase while the rest encode.
    /// When false, the full segment text is synthesized as one unit before playback
    /// begins — better prosody continuity at the cost of higher initial latency.
    /// Default: true (phrase chunking on).
    /// </summary>
    public bool EnablePhraseChunking { get; set; } = true;

    public float  Volume          { get; set; } = 0.8f;   // 0.0–1.0
    public float  PlaybackSpeed   { get; set; } = 1.0f;   // 0.75–1.5
    public string? AudioDeviceId  { get; set; } = null;   // null = system default

    // Per-dialog source toggles (mirrors addon EnableXxx settings)
    public bool EnableQuestGreeting  { get; set; } = true;
    public bool EnableQuestDetail    { get; set; } = true;
    public bool EnableQuestProgress  { get; set; } = true;
    public bool EnableQuestReward    { get; set; } = true;
    public bool EnableBooks          { get; set; } = false;

    // ── Advanced settings ─────────────────────────────────────────────────────

    public int   CaptureIntervalMs      { get; set; } = 5;       // ms between frame captures
    public int   ReScanIntervalMs       { get; set; } = 5000;    // ms between full-screen rescans
    public int   SourceGoneThresholdMs  { get; set; } = 2000;    // ms before source-gone signal

    public bool  CompressionEnabled     { get; set; } = true;    // OGG transcode before caching
    public int   OggQuality             { get; set; } = 4;       // 0–10 Vorbis quality
    public long  CacheSizeLimitBytes    { get; set; } = 500L * 1024 * 1024; // 500 MB
    public string? CacheDirectoryOverride { get; set; } = null;  // null = platform default

    public bool  SilenceTrimEnabled     { get; set; } = true;

    /// <summary>Per-slot volume trim in dB (±12 dB). Key = VoiceSlot.ToString().</summary>
    public Dictionary<string, float> VolumeTrimDb { get; set; } = new();

    // Linux-only (ignored on Windows)
    public string PiperBinaryPath   { get; set; } = "";
    public string PiperModelDirectory { get; set; } = "";

    // ── Startup / window ─────────────────────────────────────────────────────

    public double AppStartX { get; set; } = 150;
    public double AppStartY { get; set; } = 150;

    // Pronunciation workbench
    public string PronunciationWorkbenchTestSentence { get; set; } = "Stay away from Atal'zul, mon.";
    public string PronunciationWorkbenchTargetText { get; set; } = "Atal'zul";
    public string PronunciationWorkbenchPhonemeText { get; set; } = "ə tɑl zʊl";
    public string PronunciationWorkbenchAccentGroup { get; set; } = nameof(AccentGroup.Caribbean);
    public string PronunciationWorkbenchGender { get; set; } = "Male";

    // Recently-spoken suppression (live playback only; workbench preview bypasses this)
    public bool RepeatSuppressionEnabled { get; set; } = true;
    public int RepeatSuppressionWindowSeconds { get; set; } = 5;
}

// ── Settings manager ──────────────────────────────────────────────────────────

public static class VoiceSettingsManager
{
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonSaveOptions = new() { WriteIndented = true };

    private static string SettingsFilePath => Path.Combine(GetConfigDirectory(), SettingsFileName);

    public static string GetConfigDirectory()
        => Path.Combine(AppContext.BaseDirectory, "config");

    public static string GetDefaultCacheDirectory()
        => Path.Combine(AppContext.BaseDirectory, "tts_cache");

    /// <summary>
    /// Directory where large local data lives (TTS model, cache, etc.).
    /// Stored next to the exe so the user can manage it directly and it
    /// never roams or bloats the Windows user profile.
    /// </summary>
    public static string GetDefaultModelDirectory()
        => Path.Combine(AppContext.BaseDirectory, "models");

    public static VoiceUserSettings LoadSettings()
    {
        try
        {
            var path = SettingsFilePath;
            if (!File.Exists(path)) return new VoiceUserSettings();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<VoiceUserSettings>(json) ?? new VoiceUserSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceSettingsManager] Load error: {ex.Message}");
            return new VoiceUserSettings();
        }
    }

    public static void SaveSettings(VoiceUserSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath)!;
            Directory.CreateDirectory(dir);
            var tmp  = SettingsFilePath + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonSaveOptions);
            File.WriteAllText(tmp, json);
            if (File.Exists(SettingsFilePath))
                File.Replace(tmp, SettingsFilePath, null);
            else
                File.Move(tmp, SettingsFilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceSettingsManager] Save error: {ex.Message}");
        }
    }

    public static async Task SaveSettingsAsync(VoiceUserSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsFilePath)!;
            Directory.CreateDirectory(dir);
            var tmp  = SettingsFilePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(settings, JsonSaveOptions));
            if (File.Exists(SettingsFilePath))
                File.Replace(tmp, SettingsFilePath, null);
            else
                File.Move(tmp, SettingsFilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceSettingsManager] Async save error: {ex.Message}");
        }
    }
}