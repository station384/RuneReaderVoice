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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public static class StandardVoiceProfileCatalog
{
    private static readonly object _gate = new();
    private static Dictionary<string, Dictionary<string, VoiceProfile>>? _voiceProfiles;
    private static Dictionary<string, Dictionary<string, VoiceProfile>>? _sampleProfiles;

    private static readonly string[] ChatterboxDonorProviderIds = { "remote:chatterbox_full", "remote:chatterbox" };
    private const string NarratorSlotKey = "Narrator";
    private const string HardNarratorVoiceId = "M_Narrator";

    public static VoiceProfile? TryGetVoiceStandard(string providerId, VoiceSlot slot)
    {
        EnsureLoaded();

        if (IsKokoroProvider(providerId))
        {
            if (TryGetProfile(_voiceProfiles, providerId, slot.ToString(), out var kokoroExact))
                return kokoroExact;

            var preset = SpeakerPresetCatalog.GetRecommendedForSlot(slot);
            return preset?.Profile?.Clone();
        }

        if (IsChatterboxProvider(providerId))
        {
            if (TryGetProfile(_voiceProfiles, providerId, slot.ToString(), out var chatterboxExact))
                return chatterboxExact;

            if (TryGetFirstDonorVoice(slot.ToString(), out var chatterboxDonor))
                return chatterboxDonor;

            if (TryGetFirstDonorNarrator(out var chatterboxNarrator))
                return chatterboxNarrator;

            return CreateHardNarratorProfile();
        }

        if (slot.ToString().Equals(NarratorSlotKey, StringComparison.OrdinalIgnoreCase))
        {
            if (TryGetFirstDonorNarrator(out var donorNarrator))
                return donorNarrator;

            return CreateHardNarratorProfile();
        }

        if (TryGetFirstDonorVoice(slot.ToString(), out var donorSlot))
            return donorSlot;

        if (TryGetFirstDonorNarrator(out var donorFallbackNarrator))
            return donorFallbackNarrator;

        return CreateHardNarratorProfile();
    }

    public static VoiceProfile? TryGetSampleStandard(string providerId, string sampleId)
    {
        EnsureLoaded();

        if (IsKokoroProvider(providerId))
        {
            if (TryGetProfile(_sampleProfiles, providerId, sampleId, out var kokoroExact))
                return kokoroExact;

            return null;
        }

        if (IsChatterboxProvider(providerId))
        {
            if (TryGetProfile(_sampleProfiles, providerId, sampleId, out var chatterboxExact))
                return chatterboxExact;

            if (TryGetFirstDonorSample(sampleId, out var chatterboxDonorSample))
                return chatterboxDonorSample;

            if (TryGetFirstDonorNarrator(out var chatterboxNarrator))
                return chatterboxNarrator;

            return CreateHardNarratorProfile();
        }

        if (TryGetFirstDonorSample(sampleId, out var donorSample))
            return donorSample;

        if (TryGetFirstDonorNarrator(out var donorNarrator))
            return donorNarrator;

        return CreateHardNarratorProfile();
    }




    private static bool TryGetFirstDonorVoice(string key, out VoiceProfile? profile)
    {
        foreach (var donorProviderId in ChatterboxDonorProviderIds)
        {
            if (TryGetProfile(_voiceProfiles, donorProviderId, key, out profile))
                return true;
        }

        profile = null;
        return false;
    }

    private static bool TryGetFirstDonorSample(string key, out VoiceProfile? profile)
    {
        foreach (var donorProviderId in ChatterboxDonorProviderIds)
        {
            if (TryGetProfile(_sampleProfiles, donorProviderId, key, out profile))
                return true;
        }

        profile = null;
        return false;
    }

    private static bool TryGetFirstDonorNarrator(out VoiceProfile? profile)
        => TryGetFirstDonorVoice(NarratorSlotKey, out profile);

    private static bool IsChatterboxProvider(string providerId)
        => ChatterboxDonorProviderIds.Any(x => x.Equals(providerId, StringComparison.OrdinalIgnoreCase));

    private static VoiceProfile CreateHardNarratorProfile()
        => VoiceProfileDefaults.Create(HardNarratorVoiceId);

    private static bool TryGetProfile(
        Dictionary<string, Dictionary<string, VoiceProfile>>? profiles,
        string providerId,
        string key,
        out VoiceProfile? profile)
    {
        profile = null;
        if (profiles == null)
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

    private static bool IsKokoroProvider(string providerId)
        => providerId.Equals("kokoro", StringComparison.OrdinalIgnoreCase) ||
           providerId.IndexOf("kokoro", StringComparison.OrdinalIgnoreCase) >= 0;

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
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "config", fileName),
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
