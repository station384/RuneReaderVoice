// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RuneReaderVoice.TTS.Providers;

public static class StandardVoiceProfileCatalog
{
    private const string NarratorSlotKey = "Narrator";
    private const string MaleNarratorSlotKey = "Narrator/Male";
    private const string FemaleNarratorSlotKey = "Narrator/Female";
    private const string HardNarratorVoiceId = "M_Narrator";
    private const string HardFemaleNarratorVoiceId = "F_Narrator";

    private static readonly object _gate = new();
    private static Dictionary<string, Dictionary<string, VoiceProfile>>? _voiceProfiles;
    private static Dictionary<string, Dictionary<string, VoiceProfile>>? _sampleProfiles;

    public static bool TryGetVoiceStandard(string providerId, string slotKey, out VoiceProfile? profile)
    {
        EnsureLoaded();

        var canonical = NormalizeProviderId(providerId);
        if (TryGetProfile(_voiceProfiles, canonical, slotKey, out profile))
            return true;

        if (slotKey.Equals(MaleNarratorSlotKey, StringComparison.OrdinalIgnoreCase) ||
            slotKey.Equals(NarratorSlotKey, StringComparison.OrdinalIgnoreCase))
        {
            profile = CreateHardNarratorProfile();
            return true;
        }

        if (slotKey.Equals(FemaleNarratorSlotKey, StringComparison.OrdinalIgnoreCase))
        {
            profile = CreateHardFemaleNarratorProfile();
            return true;
        }

        profile = null;
        return false;
    }

    public static bool TryGetSampleStandard(string providerId, string key, out VoiceProfile? profile)
    {
        EnsureLoaded();

        var canonical = NormalizeProviderId(providerId);
        if (TryGetProfile(_sampleProfiles, canonical, key, out profile))
            return true;

        if (key.Equals(MaleNarratorSlotKey, StringComparison.OrdinalIgnoreCase) ||
            key.Equals(NarratorSlotKey, StringComparison.OrdinalIgnoreCase))
        {
            profile = CreateHardNarratorProfile();
            return true;
        }

        if (key.Equals(FemaleNarratorSlotKey, StringComparison.OrdinalIgnoreCase))
        {
            profile = CreateHardFemaleNarratorProfile();
            return true;
        }

        profile = null;
        return false;
    }

    private static string NormalizeProviderId(string providerId)
        => string.IsNullOrWhiteSpace(providerId) ? string.Empty : providerId.Trim();

    private static VoiceProfile CreateHardNarratorProfile()
        => VoiceProfileDefaults.Create(HardNarratorVoiceId);

    private static VoiceProfile CreateHardFemaleNarratorProfile()
        => VoiceProfileDefaults.Create(HardFemaleNarratorVoiceId);

    private static bool TryGetProfile(
        Dictionary<string, Dictionary<string, VoiceProfile>>? profiles,
        string providerId,
        string key,
        out VoiceProfile? profile)
    {
        profile = null;
        if (profiles == null || string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(key))
            return false;

        if (profiles.TryGetValue(providerId, out var dict) &&
            dict.TryGetValue(key, out var found) &&
            found != null)
        {
            profile = found.Clone();
            return true;
        }

        return false;
    }

    private static void EnsureLoaded()
    {
        if (_voiceProfiles != null && _sampleProfiles != null)
            return;

        lock (_gate)
        {
            if (_voiceProfiles == null)
                _voiceProfiles = LoadMultiProviderProfiles("voice-profiles-all-providers.json");
            if (_sampleProfiles == null)
                _sampleProfiles = LoadMultiProviderProfiles("voice-sample-profiles.json");
        }
    }

    private static Dictionary<string, Dictionary<string, VoiceProfile>> LoadMultiProviderProfiles(string fileName)
    {
        try
        {
            var path = ResolveConfigPath(fileName);
            if (path == null || !File.Exists(path))
                return new(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(path);
            var export = JsonSerializer.Deserialize<MultiProviderVoiceProfileExport>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            return export?.Providers?.ToDictionary(
                kvp => kvp.Key,
                kvp => new Dictionary<string, VoiceProfile>(kvp.Value, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase)
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? ResolveConfigPath(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "config", fileName);
        return File.Exists(path) ? path : null;
    }
}
