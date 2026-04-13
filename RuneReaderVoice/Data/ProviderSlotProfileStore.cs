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
    // Read-through passthru cache. Do not mirror entire DB in memory.
    private readonly Dictionary<string, Dictionary<string, VoiceProfile>> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProviderSlotProfileStore(RvrDb db) => _db = db;

    private static string SampleSlotId(string sampleId) => $"{SampleKeyPrefix}{sampleId}";

    private static VoiceProfile? DeserializeProfile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<VoiceProfile>(json);
        }
        catch
        {
            return null;
        }
    }

    private bool TryGetCachedProfile(string providerId, string slotId, out VoiceProfile? profile)
    {
        profile = null;
        if (_cache.TryGetValue(providerId, out var dict) && dict.TryGetValue(slotId, out var stored) && stored != null)
        {
            profile = stored.Clone();
            return true;
        }

        return false;
    }

    private void CacheProfile(string providerId, string slotId, VoiceProfile profile)
    {
        if (!_cache.TryGetValue(providerId, out var dict))
        {
            dict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            _cache[providerId] = dict;
        }

        dict[slotId] = profile.Clone();
    }

    private void RemoveCachedProfile(string providerId, string slotId)
    {
        if (_cache.TryGetValue(providerId, out var dict))
            dict.Remove(slotId);
    }

    public bool TryGetProfile(string providerId, string slotId, out VoiceProfile? profile)
    {
        profile = null;
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(slotId))
            return false;

        if (TryGetCachedProfile(providerId, slotId, out profile) && profile != null)
            return true;

        var row = _db.Connection.Table<ProviderSlotProfileRow>()
            .Where(x => x.ProviderId == providerId && x.SlotId == slotId)
            .FirstOrDefaultAsync()
            .GetAwaiter()
            .GetResult();

        var loaded = DeserializeProfile(row?.ProfileJson);
        if (loaded == null)
            return false;

        CacheProfile(providerId, slotId, loaded);
        profile = loaded.Clone();
        return true;
    }

    public bool TryGetSampleProfile(string providerId, string sampleId, out VoiceProfile? profile)
        => TryGetProfile(providerId, SampleSlotId(sampleId), out profile);

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

            var profile = DeserializeProfile(row.ProfileJson);
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
        _cache.Clear();
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
        CacheProfile(providerId, slotId, profile);
    }

    public async Task RemoveAsync(string providerId, string slotId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(slotId))
            return;

        await _db.Connection.ExecuteAsync(
            "DELETE FROM ProviderSlotProfiles WHERE ProviderId = ? AND SlotId = ?",
            providerId,
            slotId);

        RemoveCachedProfile(providerId, slotId);
    }

    public Task UpsertSampleAsync(string providerId, string sampleId, VoiceProfile profile, string source = "Local")
        => UpsertAsync(providerId, SampleSlotId(sampleId), profile, source);

    public Task RemoveSampleAsync(string providerId, string sampleId)
        => RemoveAsync(providerId, SampleSlotId(sampleId));


    public Dictionary<string, VoiceProfile> GetVoiceProfilesForProvider(string providerId)
    {
        var result = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(providerId))
            return result;

        var rows = _db.Connection.Table<ProviderSlotProfileRow>()
            .Where(x => x.ProviderId == providerId && !x.SlotId.StartsWith(SampleKeyPrefix))
            .ToListAsync()
            .GetAwaiter()
            .GetResult();

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SlotId))
                continue;

            var profile = DeserializeProfile(row.ProfileJson);
            if (profile == null)
                continue;

            result[row.SlotId] = profile;
            CacheProfile(providerId, row.SlotId, profile);
        }

        return result;
    }

    public Dictionary<string, VoiceProfile> GetSampleProfilesForProvider(string providerId)
    {
        var result = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(providerId))
            return result;

        var rows = _db.Connection.Table<ProviderSlotProfileRow>()
            .Where(x => x.ProviderId == providerId && x.SlotId.StartsWith(SampleKeyPrefix))
            .ToListAsync()
            .GetAwaiter()
            .GetResult();

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SlotId))
                continue;

            var profile = DeserializeProfile(row.ProfileJson);
            if (profile == null)
                continue;

            var sampleId = row.SlotId.Substring(SampleKeyPrefix.Length);
            result[sampleId] = profile;
            CacheProfile(providerId, row.SlotId, profile);
        }

        return result;
    }
    public Dictionary<string, Dictionary<string, VoiceProfile>> GetVoiceProfilesSnapshot()
    {
        var result = new Dictionary<string, Dictionary<string, VoiceProfile>>(StringComparer.OrdinalIgnoreCase);
        var rows = _db.Connection.Table<ProviderSlotProfileRow>().ToListAsync().GetAwaiter().GetResult();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.ProviderId) || string.IsNullOrWhiteSpace(row.SlotId) || row.SlotId.StartsWith(SampleKeyPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var profile = DeserializeProfile(row.ProfileJson);
            if (profile == null)
                continue;

            if (!result.TryGetValue(row.ProviderId, out var providerDict))
            {
                providerDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                result[row.ProviderId] = providerDict;
            }

            providerDict[row.SlotId] = profile;
        }

        return result;
    }

    public Dictionary<string, Dictionary<string, VoiceProfile>> GetSampleProfilesSnapshot()
    {
        var result = new Dictionary<string, Dictionary<string, VoiceProfile>>(StringComparer.OrdinalIgnoreCase);
        var rows = _db.Connection.Table<ProviderSlotProfileRow>().ToListAsync().GetAwaiter().GetResult();
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.ProviderId) || string.IsNullOrWhiteSpace(row.SlotId) || !row.SlotId.StartsWith(SampleKeyPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var profile = DeserializeProfile(row.ProfileJson);
            if (profile == null)
                continue;

            if (!result.TryGetValue(row.ProviderId, out var providerDict))
            {
                providerDict = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                result[row.ProviderId] = providerDict;
            }

            providerDict[row.SlotId.Substring(SampleKeyPrefix.Length)] = profile;
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

        foreach (var (slotId, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(slotId) || profile == null)
                continue;

            if (existing.TryGetValue(slotId, out var row))
            {
                if (!IsServerOwnedSource(row.Source))
                {
                    skippedLocal++;
                    var localProfile = DeserializeProfile(row.ProfileJson);
                    if (localProfile != null)
                        CacheProfile(providerId, slotId, localProfile);
                    continue;
                }

                row.ProfileJson = JsonSerializer.Serialize(profile);
                row.Source = source;
                row.UpdatedUtc = now;
                await _db.Connection.InsertOrReplaceAsync(row);
                CacheProfile(providerId, slotId, profile);
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
            CacheProfile(providerId, slotId, profile);
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
                    var localProfile = DeserializeProfile(row.ProfileJson);
                    if (localProfile != null)
                        CacheProfile(providerId, slotId, localProfile);
                    continue;
                }

                row.ProfileJson = JsonSerializer.Serialize(profile);
                row.Source = source;
                row.UpdatedUtc = now;
                await _db.Connection.InsertOrReplaceAsync(row);
                CacheProfile(providerId, slotId, profile);
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
            CacheProfile(providerId, slotId, profile);
            inserted++;
        }

        return (inserted, updated, skippedLocal);
    }

    public async Task ReplaceVoiceProfilesAsync(string providerId, IReadOnlyDictionary<string, VoiceProfile> profiles, string source = "Local")
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return;

        await _db.Connection.ExecuteAsync(
            "DELETE FROM ProviderSlotProfiles WHERE ProviderId = ? AND SlotId NOT LIKE ?",
            providerId,
            SampleKeyPrefix + "%");

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

        foreach (var (slotId, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(slotId) || profile == null)
                continue;

            CacheProfile(providerId, slotId, profile);
        }
    }

    public async Task ReplaceSampleProfilesAsync(string providerId, IReadOnlyDictionary<string, VoiceProfile> profiles, string source = "Local")
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return;

        await _db.Connection.ExecuteAsync(
            "DELETE FROM ProviderSlotProfiles WHERE ProviderId = ? AND SlotId LIKE ?",
            providerId,
            SampleKeyPrefix + "%");

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

        foreach (var (sampleId, profile) in profiles)
        {
            if (string.IsNullOrWhiteSpace(sampleId) || profile == null)
                continue;

            CacheProfile(providerId, SampleSlotId(sampleId), profile);
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

        _cache.Clear();
    }
}
