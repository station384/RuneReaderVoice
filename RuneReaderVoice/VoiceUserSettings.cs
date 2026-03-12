using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice;

public sealed class VoiceUserSettings
{
    public bool TtsEnabled { get; set; } = true;
    public string ActiveProvider { get; set; } = "winrt";

    // Legacy storage retained for migration.
    public Dictionary<string, Dictionary<string, string>> PerProviderVoiceAssignments { get; set; } = new();

    // New structured storage.
    public Dictionary<string, Dictionary<string, VoiceProfile>> PerProviderVoiceProfiles { get; set; } = new();

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
    public bool EnablePhraseChunking { get; set; } = true;

    public float Volume { get; set; } = 0.8f;
    public float PlaybackSpeed { get; set; } = 1.0f;
    public string? AudioDeviceId { get; set; } = null;

    public bool EnableQuestGreeting { get; set; } = true;
    public bool EnableQuestDetail { get; set; } = true;
    public bool EnableQuestProgress { get; set; } = true;
    public bool EnableQuestReward { get; set; } = true;
    public bool EnableBooks { get; set; } = false;

    public int CaptureIntervalMs { get; set; } = 5;
    public int ReScanIntervalMs { get; set; } = 5000;
    public int SourceGoneThresholdMs { get; set; } = 2000;

    public bool CompressionEnabled { get; set; } = true;
    public int OggQuality { get; set; } = 4;
    public long CacheSizeLimitBytes { get; set; } = 500L * 1024 * 1024;
    public string? CacheDirectoryOverride { get; set; } = null;
    public bool SilenceTrimEnabled { get; set; } = true;
    public Dictionary<string, float> VolumeTrimDb { get; set; } = new();

    public string PiperBinaryPath { get; set; } = "";
    public string PiperModelDirectory { get; set; } = "";

    public double AppStartX { get; set; } = 150;
    public double AppStartY { get; set; } = 150;

    public string PronunciationWorkbenchTestSentence { get; set; } = "Stay away from Atal'zul, mon.";
    public string PronunciationWorkbenchTargetText { get; set; } = "Atal'zul";
    public string PronunciationWorkbenchPhonemeText { get; set; } = "ə tɑl zʊl";
    public string PronunciationWorkbenchAccentGroup { get; set; } = nameof(Protocol.AccentGroup.Caribbean);
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
                return s;
            }

            var json = File.ReadAllText(path);
            var settings = JsonSerializer.Deserialize<VoiceUserSettings>(json) ?? new VoiceUserSettings();
            settings.NormalizeVoiceProfiles();
            return settings;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VoiceSettingsManager] Load error: {ex.Message}");
            var s = new VoiceUserSettings();
            s.NormalizeVoiceProfiles();
            return s;
        }
    }

    public static void SaveSettings(VoiceUserSettings settings)
    {
        try
        {
            settings.NormalizeVoiceProfiles();
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
