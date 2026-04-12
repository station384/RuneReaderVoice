// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.Data;

public sealed class ProviderSlotProfileStore
{
    private const string SampleKeyPrefix = "sample:";

    private readonly RvrDb _db;
    private readonly Dictionary<string, Dictionary<string, VoiceProfile>> _cache = new(StringComparer.OrdinalIgnoreCase);
    public ProviderSlotProfileStore(RvrDb db) => _db = db;

    private static string SampleSlotId(string sampleId) => $"{SampleKeyPrefix}{sampleId}";

    public bool TryGetProfile(string providerId, string slotId, out VoiceProfile? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(slotId))
            return false;
        if (_cache.TryGetValue(providerId, out var dict) && dict.TryGetValue(slotId, out var stored) && stored != null)
        {
            profile = stored.Clone();
            return true;
        }
        return false;
    }


    public bool TryGetSampleProfile(string providerId, string sampleId, out VoiceProfile? profile)
        => TryGetProfile(providerId, SampleSlotId(sampleId), out profile);

    private void RebuildCacheFromSettings(VoiceUserSettings settings)
    {
        _cache.Clear();
        foreach (var provider in settings.PerProviderVoiceProfiles)
        {
            var dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var slot in provider.Value)
                dict[slot.Key] = slot.Value.Clone();
            _cache[provider.Key] = dict;
        }

        foreach (var provider in settings.PerProviderSampleProfiles)
        {
            if (!_cache.TryGetValue(provider.Key, out var dict))
            {
                dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                _cache[provider.Key] = dict;
            }

            foreach (var sample in provider.Value)
                dict[SampleSlotId(sample.Key)] = sample.Value.Clone();
        }
    }

    public async Task SeedFromSettingsAsync(VoiceUserSettings settings)
    {
        var count = await _db.Connection.Table<ProviderSlotProfileRow>().CountAsync();
        if (count > 0) return;
        await ReplaceFromSettingsAsync(settings, "Seeded");
    }

    public async Task LoadIntoSettingsAsync(VoiceUserSettings settings)
    {
        var rows = await _db.Connection.Table<ProviderSlotProfileRow>().ToListAsync();
        if (rows.Count == 0)
            return;

        settings.PerProviderVoiceProfiles = new Dictionary<string, Dictionary<string, VoiceProfile>>(StringComparer.OrdinalIgnoreCase);
        settings.PerProviderSampleProfiles = new Dictionary<string, Dictionary<string, VoiceProfile>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.ProviderId) || string.IsNullOrWhiteSpace(row.SlotId) || string.IsNullOrWhiteSpace(row.ProfileJson))
                continue;

            VoiceProfile? profile;
            try
            {
                profile = JsonSerializer.Deserialize<VoiceProfile>(row.ProfileJson);
            }
            catch
            {
                continue;
            }

            if (profile == null)
                continue;

            if (row.SlotId.StartsWith(SampleKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var sampleId = row.SlotId.Substring(SampleKeyPrefix.Length);
                if (!settings.PerProviderSampleProfiles.TryGetValue(row.ProviderId, out var sampleDict))
                {
                    sampleDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                    settings.PerProviderSampleProfiles[row.ProviderId] = sampleDict;
                }

                sampleDict[sampleId] = profile;
            }
            else
            {
                if (!settings.PerProviderVoiceProfiles.TryGetValue(row.ProviderId, out var dict))
                {
                    dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                    settings.PerProviderVoiceProfiles[row.ProviderId] = dict;
                }

                dict[row.SlotId] = profile;
            }
        }

        settings.NormalizeVoiceProfiles();
        RebuildCacheFromSettings(settings);
    }

    public async Task UpsertAsync(string providerId, string slotId, VoiceProfile profile, string source = "Local")
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(slotId) || profile == null)
            return;

        var row = new ProviderSlotProfileRow
        {
            ProviderId = providerId,
            SlotId = slotId,
            ProfileJson = JsonSerializer.Serialize(profile),
            Source = source,
            UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        await _db.Connection.InsertOrReplaceAsync(row);

        if (!_cache.TryGetValue(providerId, out var dict))
        {
            dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            _cache[providerId] = dict;
        }
        dict[slotId] = profile.Clone();
    }


    public async Task RemoveAsync(string providerId, string slotId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(slotId))
            return;

        var row = await _db.Connection.Table<ProviderSlotProfileRow>()
            .Where(x => x.ProviderId == providerId && x.SlotId == slotId)
            .FirstOrDefaultAsync();

        if (row != null)
            await _db.Connection.DeleteAsync(row);

        if (_cache.TryGetValue(providerId, out var dict))
            dict.Remove(slotId);
    }


    public Task UpsertSampleAsync(string providerId, string sampleId, VoiceProfile profile, string source = "Local")
        => UpsertAsync(providerId, SampleSlotId(sampleId), profile, source);

    public Task RemoveSampleAsync(string providerId, string sampleId)
        => RemoveAsync(providerId, SampleSlotId(sampleId));

    public Dictionary<string, Dictionary<string, VoiceProfile>> GetVoiceProfilesSnapshot()
    {
        var result = new Dictionary<string, Dictionary<string, VoiceProfile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (providerId, dict) in _cache)
        {
            foreach (var (slotId, profile) in dict)
            {
                if (slotId.StartsWith(SampleKeyPrefix, StringComparison.OrdinalIgnoreCase) || profile == null)
                    continue;

                if (!result.TryGetValue(providerId, out var providerDict))
                {
                    providerDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                    result[providerId] = providerDict;
                }

                providerDict[slotId] = profile.Clone();
            }
        }

        return result;
    }

    public Dictionary<string, Dictionary<string, VoiceProfile>> GetSampleProfilesSnapshot()
    {
        var result = new Dictionary<string, Dictionary<string, VoiceProfile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (providerId, dict) in _cache)
        {
            foreach (var (slotId, profile) in dict)
            {
                if (!slotId.StartsWith(SampleKeyPrefix, StringComparison.OrdinalIgnoreCase) || profile == null)
                    continue;

                if (!result.TryGetValue(providerId, out var providerDict))
                {
                    providerDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                    result[providerId] = providerDict;
                }

                providerDict[slotId.Substring(SampleKeyPrefix.Length)] = profile.Clone();
            }
        }

        return result;
    }



    private static bool IsServerOwnedSource(string? source)
        => string.Equals(source, "ServerSync", StringComparison.OrdinalIgnoreCase)
           || string.Equals(source, "Seeded", StringComparison.OrdinalIgnoreCase)
           || string.Equals(source, "ServerSeed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(source, "CrowdSourced", StringComparison.OrdinalIgnoreCase)
           || string.Equals(source, "Confirmed", StringComparison.OrdinalIgnoreCase);

    public async Task<(int inserted, int updated, int skippedLocal)> MergeVoiceProfilesFromServerAsync(string providerId, IReadOnlyDictionary<string, VoiceProfile> profiles, string source = "ServerSync")
    {
        if (string.IsNullOrWhiteSpace(providerId) || profiles == null || profiles.Count == 0)
            return (0, 0, 0);

        var existingRows = await _db.Connection.Table<ProviderSlotProfileRow>()
            .Where(x => x.ProviderId == providerId && !x.SlotId.StartsWith(SampleKeyPrefix))
            .ToListAsync();

        var existing = existingRows.ToDictionary(x => x.SlotId, x => x, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var inserted = 0;
        var updated = 0;
        var skippedLocal = 0;

        if (!_cache.TryGetValue(providerId, out var cacheDict))
        {
            cacheDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            _cache[providerId] = cacheDict;
        }

        foreach (var (slotId, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(slotId) || profile == null)
                continue;

            if (existing.TryGetValue(slotId, out var row))
            {
                if (!IsServerOwnedSource(row.Source))
                {
                    skippedLocal++;
                    if (!cacheDict.ContainsKey(slotId))
                    {
                        try
                        {
                            var localProfile = JsonSerializer.Deserialize<VoiceProfile>(row.ProfileJson);
                            if (localProfile != null)
                                cacheDict[slotId] = localProfile.Clone();
                        }
                        catch { }
                    }
                    continue;
                }

                row.ProfileJson = JsonSerializer.Serialize(profile);
                row.Source = source;
                row.UpdatedUtc = now;
                await _db.Connection.InsertOrReplaceAsync(row);
                cacheDict[slotId] = profile.Clone();
                updated++;
                continue;
            }

            var newRow = new ProviderSlotProfileRow
            {
                ProviderId = providerId,
                SlotId = slotId,
                ProfileJson = JsonSerializer.Serialize(profile),
                Source = source,
                UpdatedUtc = now,
            };
            await _db.Connection.InsertOrReplaceAsync(newRow);
            cacheDict[slotId] = profile.Clone();
            inserted++;
        }

        return (inserted, updated, skippedLocal);
    }

    public async Task<(int inserted, int updated, int skippedLocal)> MergeSampleProfilesFromServerAsync(string providerId, IReadOnlyDictionary<string, VoiceProfile> profiles, string source = "ServerSync")
    {
        if (string.IsNullOrWhiteSpace(providerId) || profiles == null || profiles.Count == 0)
            return (0, 0, 0);

        var existingRows = await _db.Connection.Table<ProviderSlotProfileRow>()
            .Where(x => x.ProviderId == providerId && x.SlotId.StartsWith(SampleKeyPrefix))
            .ToListAsync();

        var existing = existingRows.ToDictionary(x => x.SlotId, x => x, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var inserted = 0;
        var updated = 0;
        var skippedLocal = 0;

        if (!_cache.TryGetValue(providerId, out var cacheDict))
        {
            cacheDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            _cache[providerId] = cacheDict;
        }

        foreach (var (sampleId, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(sampleId) || profile == null)
                continue;

            var slotId = SampleSlotId(sampleId);
            if (existing.TryGetValue(slotId, out var row))
            {
                if (!IsServerOwnedSource(row.Source))
                {
                    skippedLocal++;
                    if (!cacheDict.ContainsKey(slotId))
                    {
                        try
                        {
                            var localProfile = JsonSerializer.Deserialize<VoiceProfile>(row.ProfileJson);
                            if (localProfile != null)
                                cacheDict[slotId] = localProfile.Clone();
                        }
                        catch { }
                    }
                    continue;
                }

                row.ProfileJson = JsonSerializer.Serialize(profile);
                row.Source = source;
                row.UpdatedUtc = now;
                await _db.Connection.InsertOrReplaceAsync(row);
                cacheDict[slotId] = profile.Clone();
                updated++;
                continue;
            }

            var newRow = new ProviderSlotProfileRow
            {
                ProviderId = providerId,
                SlotId = slotId,
                ProfileJson = JsonSerializer.Serialize(profile),
                Source = source,
                UpdatedUtc = now,
            };
            await _db.Connection.InsertOrReplaceAsync(newRow);
            cacheDict[slotId] = profile.Clone();
            inserted++;
        }

        return (inserted, updated, skippedLocal);
    }

    public async Task ReplaceVoiceProfilesAsync(string providerId, IReadOnlyDictionary<string, VoiceProfile> profiles, string source = "Local")
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return;

        var existingRows = await _db.Connection.Table<ProviderSlotProfileRow>()
            .Where(x => x.ProviderId == providerId && !x.SlotId.StartsWith(SampleKeyPrefix))
            .ToListAsync();

        foreach (var row in existingRows)
            await _db.Connection.DeleteAsync(row);

        if (_cache.TryGetValue(providerId, out var dict))
        {
            foreach (var key in dict.Keys.Where(x => !x.StartsWith(SampleKeyPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
                dict.Remove(key);
        }

        if (profiles == null || profiles.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new List<ProviderSlotProfileRow>();
        foreach (var (slotId, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(slotId) || profile == null)
                continue;

            rows.Add(new ProviderSlotProfileRow
            {
                ProviderId = providerId,
                SlotId = slotId,
                ProfileJson = JsonSerializer.Serialize(profile),
                Source = source,
                UpdatedUtc = now,
            });
        }

        if (rows.Count > 0)
            await _db.Connection.InsertAllAsync(rows);

        if (!_cache.TryGetValue(providerId, out var cacheDict))
        {
            cacheDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            _cache[providerId] = cacheDict;
        }

        foreach (var (slotId, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(slotId) || profile == null)
                continue;

            cacheDict[slotId] = profile.Clone();
        }
    }

    public async Task ReplaceSampleProfilesAsync(string providerId, IReadOnlyDictionary<string, VoiceProfile> profiles, string source = "Local")
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return;

        var existingRows = await _db.Connection.Table<ProviderSlotProfileRow>()
            .Where(x => x.ProviderId == providerId && x.SlotId.StartsWith(SampleKeyPrefix))
            .ToListAsync();

        foreach (var row in existingRows)
            await _db.Connection.DeleteAsync(row);

        if (_cache.TryGetValue(providerId, out var dict))
        {
            foreach (var key in dict.Keys.Where(x => x.StartsWith(SampleKeyPrefix, StringComparison.OrdinalIgnoreCase)).ToList())
                dict.Remove(key);
        }

        if (profiles == null || profiles.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new List<ProviderSlotProfileRow>();
        foreach (var (sampleId, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(sampleId) || profile == null)
                continue;

            rows.Add(new ProviderSlotProfileRow
            {
                ProviderId = providerId,
                SlotId = SampleSlotId(sampleId),
                ProfileJson = JsonSerializer.Serialize(profile),
                Source = source,
                UpdatedUtc = now,
            });
        }

        if (rows.Count > 0)
            await _db.Connection.InsertAllAsync(rows);

        if (!_cache.TryGetValue(providerId, out var cacheDict))
        {
            cacheDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            _cache[providerId] = cacheDict;
        }

        foreach (var (sampleId, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(sampleId) || profile == null)
                continue;

            cacheDict[SampleSlotId(sampleId)] = profile.Clone();
        }
    }

    public void WriteBackToSettings(VoiceUserSettings settings)
    {
        if (settings == null)
            return;

        settings.PerProviderVoiceProfiles = GetVoiceProfilesSnapshot();
        settings.PerProviderSampleProfiles = GetSampleProfilesSnapshot();
        settings.NormalizeVoiceProfiles();
    }

    public async Task ReplaceFromSettingsAsync(VoiceUserSettings settings, string source = "Local")
    {
        await _db.Connection.DeleteAllAsync<ProviderSlotProfileRow>();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = new List<ProviderSlotProfileRow>();
        foreach (var provider in settings.PerProviderVoiceProfiles)
        {
            foreach (var slot in provider.Value)
            {
                rows.Add(new ProviderSlotProfileRow
                {
                    ProviderId = provider.Key,
                    SlotId = slot.Key,
                    ProfileJson = JsonSerializer.Serialize(slot.Value),
                    Source = source,
                    UpdatedUtc = now,
                });
            }
        }

        foreach (var provider in settings.PerProviderSampleProfiles)
        {
            foreach (var sample in provider.Value)
            {
                rows.Add(new ProviderSlotProfileRow
                {
                    ProviderId = provider.Key,
                    SlotId = SampleSlotId(sample.Key),
                    ProfileJson = JsonSerializer.Serialize(sample.Value),
                    Source = source,
                    UpdatedUtc = now,
                });
            }
        }
        if (rows.Count > 0)
            await _db.Connection.InsertAllAsync(rows);

        RebuildCacheFromSettings(settings);
    }
}
