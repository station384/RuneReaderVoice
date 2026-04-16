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

// VoiceUserSettings.cs
// Portable application settings, persisted voice profiles, and capture preferences.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice;


public sealed class SavedBarcodeRegion
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
}

public sealed class VoiceUserSettings
{
    public bool TtsEnabled { get; set; } = true;
    public string ActiveProvider { get; set; } = "winrt";

    // Legacy in-memory storage retained only for migration/runtime convenience.
    // Provider profiles now persist in the SQLite store, not settings.json.
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, Dictionary<string, string>> PerProviderVoiceAssignments { get; set; } = new();

    // New structured storage.
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, Dictionary<string, VoiceProfile>> PerProviderVoiceProfiles { get; set; } = new();

    // Base/default settings for concrete voice/sample IDs, per provider.
    // Key1 = provider id, Key2 = voice/sample id.
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, Dictionary<string, VoiceProfile>> PerProviderSampleProfiles { get; set; } = new();


    // Recency weights (0-10) used to float frequently-saved selections to the top.
    // For voices: Key = providerId|voiceId. For races: Key = raceId string.
    public Dictionary<string, byte> RecentVoiceSelectionRanks { get; set; } = new();
    public Dictionary<string, byte> RecentRaceSelectionRanks { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, VoiceProfile> VoiceProfiles
    {
        get
        {
            if (!PerProviderVoiceProfiles.TryGetValue(ActiveProvider, out var dict))
            {
                dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                PerProviderVoiceProfiles[ActiveProvider] = dict;
            }
            return dict;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<string, string> VoiceAssignments
    {
        get
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in VoiceProfiles)
                result[kvp.Key] = kvp.Value?.VoiceId ?? "";
            return result;
        }
    }

    public string PlaybackMode { get; set; } = "WaitForFullText";
    public bool EnablePhraseChunking { get; set; } = false;

    public float Volume { get; set; } = 0.8f;
    public float PlaybackSpeed { get; set; } = 1.0f;
    public string? AudioDeviceId { get; set; } = null;

    public bool EnableQuestGreeting { get; set; } = true;
    public bool EnableQuestDetail { get; set; } = true;
    public bool EnableQuestProgress { get; set; } = true;
    public bool EnableQuestReward { get; set; } = true;
    public bool EnableBooks { get; set; } = false;

    // Player-name replacement for cache-friendly synthesis.
    // generic = replace with a cache-friendly preset title
    // actual  = speak detected player name
    public string PlayerNameMode { get; set; } = "generic";
    public string PlayerNameReplacementPreset { get; set; } = "champion";
    public bool PlayerNameAppendRealm { get; set; } = false;
    public string PlayerNameSplitStrategy { get; set; } = "containing_sentence";

    public int CaptureIntervalMs { get; set; } = 5;
    public void NormalizeCaptureSettings()
    {
        CaptureIntervalMs = Math.Clamp(CaptureIntervalMs, 4, 100);
        ReScanIntervalMs = Math.Clamp(ReScanIntervalMs, 1000, 30000);
        SourceGoneThresholdMs = Math.Clamp(SourceGoneThresholdMs, 250, 30000);
    }
    public int ReScanIntervalMs { get; set; } = 5000;
    public int SourceGoneThresholdMs { get; set; } = 2000;
    public SavedBarcodeRegion? LastBarcodeRegion { get; set; } = null;

    public bool CompressionEnabled { get; set; } = true;
    public int OggQuality { get; set; } = 4;
    public long CacheSizeLimitBytes { get; set; } = 500L * 1024 * 1024;
    public string? CacheDirectoryOverride { get; set; } = null;
    public bool SilenceTrimEnabled { get; set; } = true;
    public Dictionary<string, float> VolumeTrimDb { get; set; } = new();

    public string PiperBinaryPath { get; set; } = "";
    public string PiperModelDirectory { get; set; } = "";

    public string RemoteServerUrl { get; set; } = "";
    public string RemoteApiKey { get; set; } = "";
    public string RemoteProviderCatalogJson { get; set; } = "";

    // ── Community sync settings ───────────────────────────────────────────────

    /// <summary>
    /// Token sent with NPC override contributions (Bearer).
    /// Matches RRV_CONTRIBUTE_KEY on the server. Empty = open contribution.
    /// </summary>
    public string ContributeKey { get; set; } = "";

    /// <summary>
    /// Admin token for pushing defaults to the server (Bearer).
    /// Matches RRV_ADMIN_KEY on the server. Empty = open admin (LAN mode).
    /// </summary>
    public string AdminKey { get; set; } = "";

    /// <summary>
    /// When true, saving a Local NPC override automatically contributes it
    /// to the server in the background (fire-and-forget, silent failure).
    /// </summary>
    public bool ContributeByDefault { get; set; } = false;

    /// <summary>
    /// Unix timestamp of the last successful NPC override poll from server.
    /// 0 = never synced. Used by the polling loop for delta fetches.
    /// </summary>
    public double LastNpcSyncAt { get; set; } = 0.0;

    /// <summary>
    /// When false and RemoteServerUrl is set, the app pulls server defaults
    /// for all four data types on startup (first-load seeding).
    /// Set to true after the first successful pull — or manually to force re-seed.
    /// </summary>
    public bool FirstLoadComplete { get; set; } = false;

    public double AppStartX { get; set; } = 150;
    public double AppStartY { get; set; } = 150;

    public string PronunciationWorkbenchTestSentence { get; set; } = "Stay away from Atal'zul, mon.";
    public string PronunciationWorkbenchTargetText { get; set; } = "Atal'zul";
    public string PronunciationWorkbenchPhonemeText { get; set; } = "ə tɑl zʊl";
    public string PronunciationWorkbenchAccentGroup { get; set; } = nameof(Protocol.AccentGroup.Troll);
    public string PronunciationWorkbenchGender { get; set; } = "Male";

    public bool RepeatSuppressionEnabled { get; set; } = true;
    public int RepeatSuppressionWindowSeconds { get; set; } = 5;

    public string TextSwapWorkbenchOriginalText { get; set; } = "Wait... I know what to do - give me a moment.";
    public string TextSwapWorkbenchFindText { get; set; } = " - ";
    public bool TextSwapWorkbenchReplaceWithCrLf { get; set; } = false;
    public string TextSwapWorkbenchReplaceText { get; set; } = " ... ";
    public bool TextSwapWorkbenchWholeWord { get; set; } = false;
    public bool TextSwapWorkbenchCaseSensitive { get; set; } = false;
    public string TextSwapWorkbenchNotes { get; set; } = string.Empty;

    /// <summary>
    /// Persisted expander collapsed states. Key = expander name, Value = IsExpanded.
    /// Defaults to collapsed (false) for any key not present.
    /// </summary>
    public Dictionary<string, bool> ExpanderStates { get; set; } = new();

    /// <summary>Returns the saved IsExpanded state for a named expander, defaulting to false.</summary>
    public bool GetExpanderState(string name)
        => ExpanderStates.TryGetValue(name, out var v) ? v : false;

    /// <summary>Saves the IsExpanded state for a named expander.</summary>
    public void SetExpanderState(string name, bool isExpanded)
        => ExpanderStates[name] = isExpanded;

    public void NormalizeVoiceProfiles()
    {
        foreach (var (providerId, assignments) in PerProviderVoiceAssignments)
        {
            if (!PerProviderVoiceProfiles.TryGetValue(providerId, out var profileDict))
            {
                profileDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                PerProviderVoiceProfiles[providerId] = profileDict;
            }

            foreach (var (slotKey, voiceId) in assignments)
            {
                if (!profileDict.ContainsKey(slotKey))
                    profileDict[slotKey] = VoiceProfileDefaults.Create(voiceId);
            }
        }

        foreach (var dict in PerProviderVoiceProfiles.Values)
        {
            var keys = new List<string>(dict.Keys);
            foreach (var key in keys)
            {
                var profile = dict[key] ?? new VoiceProfile();
                if (string.IsNullOrWhiteSpace(profile.LangCode))
                    profile.LangCode = VoiceProfileDefaults.GetDefaultLangCodeForVoice(profile.VoiceId);
                if (profile.SpeechRate <= 0f)
                    profile.SpeechRate = 1.0f;
                profile.NormalizeForStorage();
                dict[key] = profile;
            }
        }

        foreach (var dict in PerProviderSampleProfiles.Values)
        {
            var keys = new List<string>(dict.Keys);
            foreach (var key in keys)
            {
                var profile = dict[key] ?? VoiceProfileDefaults.Create(key);
                if (string.IsNullOrWhiteSpace(profile.VoiceId))
                    profile.VoiceId = key;
                if (string.IsNullOrWhiteSpace(profile.LangCode))
                    profile.LangCode = VoiceProfileDefaults.GetDefaultLangCodeForVoice(profile.VoiceId);
                if (profile.SpeechRate <= 0f)
                    profile.SpeechRate = 1.0f;
                profile.NormalizeForStorage();
                dict[key] = profile;
            }
        }
    }
}

public static class VoiceSettingsManager
{
    private const string SettingsFileName = "settings.json";
    private static readonly JsonSerializerOptions JsonSaveOptions = new() { WriteIndented = true };
    private static string SettingsFilePath => Path.Combine(GetConfigDirectory(), SettingsFileName);

    public static string GetConfigDirectory() => Path.Combine(AppContext.BaseDirectory, "config");
    public static string GetDefaultCacheDirectory() => Path.Combine(AppContext.BaseDirectory, "tts_cache");
    public static string GetDefaultModelDirectory() => Path.Combine(AppContext.BaseDirectory, "models");

    public static VoiceUserSettings LoadSettings()
    {
        try
        {
            var path = SettingsFilePath;
            if (!File.Exists(path))
            {
                var s = new VoiceUserSettings();
                s.NormalizeVoiceProfiles();
                s.NormalizeCaptureSettings();
                return s;
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<VoiceUserSettings>(json) ?? new VoiceUserSettings();
            settings.NormalizeVoiceProfiles();
            settings.NormalizeCaptureSettings();
            return settings;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceSettingsManager] Load error: {ex.Message}");
            var s = new VoiceUserSettings();
            s.NormalizeVoiceProfiles();
            s.NormalizeCaptureSettings();
            return s;
        }
    }

    public static void SaveSettings(VoiceUserSettings settings)
    {
        try
        {
            settings.NormalizeVoiceProfiles();
            settings.NormalizeCaptureSettings();
            var dir = Path.GetDirectoryName(SettingsFilePath)!;
            Directory.CreateDirectory(dir);
            var tmp = SettingsFilePath + ".tmp";
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
            settings.NormalizeVoiceProfiles();
            settings.NormalizeCaptureSettings();
            var dir = Path.GetDirectoryName(SettingsFilePath)!;
            Directory.CreateDirectory(dir);
            var tmp = SettingsFilePath + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonSaveOptions);
            await File.WriteAllTextAsync(tmp, json);
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