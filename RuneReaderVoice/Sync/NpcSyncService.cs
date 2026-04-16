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
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Data;
using RuneReaderVoice.TTS.Pronunciation;
using RuneReaderVoice.TTS.Providers;
using RuneReaderVoice.TTS.TextSwap;

namespace RuneReaderVoice.Sync;
// Sync/NpcSyncService.cs
// Background service for NPC override crowd-source sync.
//
// Responsibilities:
//   1. On startup: if !FirstLoadComplete, pull all four defaults from server
//   2. Poll every 5 minutes for new NPC override records (delta since LastNpcSyncAt)
//   3. Merge new records into local DB (Local entries always win)
//   4. Contribute local saves to server when ContributeByDefault = true
public sealed class NpcSyncService : IDisposable
{
    private readonly VoiceUserSettings       _settings;
    private readonly NpcRaceOverrideDb       _npcDb;
    private readonly PronunciationRuleStore  _pronunciationRules;
    private readonly TextSwapRuleStore       _textSwapRules;
    private readonly ServerDefaultsClient    _client;
    private readonly TtsSessionAssemblerBridge _assemblerBridge;

    private CancellationTokenSource? _cts;
    private Task?                    _pollTask;

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // Callbacks fired after a merge so the UI can refresh
    public event Action<int>? NpcRecordsMerged;
    public event Action<string>? SyncStatusChanged;

    /// <summary>
    /// Creates a no-op instance when no server URL is configured.
    /// All operations are safe no-ops — no HTTP calls are made.
    /// </summary>
    public static NpcSyncService CreateNoOp(
        VoiceUserSettings settings,
        NpcRaceOverrideDb npcDb,
        PronunciationRuleStore pronunciationRules,
        TextSwapRuleStore textSwapRules)
    {
        // Dummy client pointing nowhere — all calls return null/false immediately
        var noopClient = new ServerDefaultsClient("http://localhost:0");
        var noopBridge = new TtsSessionAssemblerBridge(null!, npcDb);
        return new NpcSyncService(settings, npcDb, pronunciationRules, textSwapRules,
            noopClient, noopBridge);
    }

    public NpcSyncService(
        VoiceUserSettings      settings,
        NpcRaceOverrideDb      npcDb,
        PronunciationRuleStore pronunciationRules,
        TextSwapRuleStore      textSwapRules,
        ServerDefaultsClient   client,
        TtsSessionAssemblerBridge assemblerBridge)
    {
        _settings           = settings;
        _npcDb              = npcDb;
        _pronunciationRules = pronunciationRules;
        _textSwapRules      = textSwapRules;
        _client             = client;
        _assemblerBridge    = assemblerBridge;
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Call once at startup. Performs first-load pull if needed,
    /// then starts the background polling loop.
    /// </summary>
    public async Task StartAsync()
    {
        if (!_settings.FirstLoadComplete)
            await DoFirstLoadAsync().ConfigureAwait(false);

        _cts      = new CancellationTokenSource();
        _pollTask = PollLoopAsync(_cts.Token);
    }

    private async Task DoFirstLoadAsync()
    {
        System.Diagnostics.Debug.WriteLine("[NpcSync] First load — pulling all server defaults");
        SetStatus("First load — pulling server defaults…");

        // Pull NPC overrides (full pull: t=0)
        await PollNpcOverridesAsync(sinceTs: 0.0).ConfigureAwait(false);

        // Pull the three seed types
        await PullAndApplyProviderSlotProfilesAsync("voice_slot").ConfigureAwait(false);
        await PullAndApplyProviderSlotProfilesAsync("sample").ConfigureAwait(false);
        await PullAndApplyDefaultsAsync("pronunciation").ConfigureAwait(false);
        await PullAndApplyDefaultsAsync("text-shaping").ConfigureAwait(false);

        _settings.FirstLoadComplete = true;
        _settings.LastNpcSyncAt     = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        VoiceSettingsManager.SaveSettings(_settings);

        SetStatus("First load complete.");
    }

    // ── Polling loop ──────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
                await PollNpcOverridesAsync(_settings.LastNpcSyncAt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NpcSyncService] Poll error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Polls for NPC override records updated since sinceTs, merges into local DB,
    /// updates LastNpcSyncAt, and fires NpcRecordsMerged if any records were written.
    /// </summary>
    public async Task<int> PollNpcOverridesAsync(double sinceTs)
    {
        System.Diagnostics.Debug.WriteLine($"[NpcSync] Polling since={sinceTs:F0}");
        var records = await _client.GetNpcOverridesSinceAsync(sinceTs).ConfigureAwait(false);
        if (records == null || records.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("[NpcSync] Poll returned 0 records");
            return 0;
        }
        System.Diagnostics.Debug.WriteLine($"[NpcSync] Poll returned {records.Count} record(s)");

        // Map server records to domain model
        var domainRecords = records.Select(r => new NpcRaceOverride
        {
            NpcId               = r.NpcId,
            CatalogId           = r.CatalogId ?? string.Empty,
            RaceId              = r.RaceId,
            Notes               = r.Notes,
            BespokeSampleId     = r.BespokeSampleId,
            BespokeExaggeration = r.BespokeExaggeration,
            BespokeCfgWeight    = r.BespokeCfgWeight,
            Source              = r.Source == "confirmed"
                                  ? NpcOverrideSource.Confirmed
                                  : NpcOverrideSource.CrowdSourced,
            Confidence          = r.Confidence,
            UpdatedAt           = r.UpdatedAt,
        }).ToList();

        int merged = await _npcDb.MergeFromServerAsync(domainRecords).ConfigureAwait(false);
        System.Diagnostics.Debug.WriteLine($"[NpcSync] Merged {merged}/{domainRecords.Count} records (skipped Local entries)");

        if (merged > 0)
        {
            // DB is source of truth. No assembler-side mirror to update.
            NpcRecordsMerged?.Invoke(merged);
            SetStatus($"Synced {merged} NPC override(s) from server.");
        }

        // Update sync timestamp to the most recent record we received
        var latestTs = records.Max(r => r.UpdatedAt);
        _settings.LastNpcSyncAt = latestTs;
        VoiceSettingsManager.SaveSettings(_settings);

        return merged;
    }

    // ── Contribution ──────────────────────────────────────────────────────────

    /// <summary>
    /// Contributes a single entry to the server regardless of ContributeByDefault.
    /// Used by the manual Push to Server button. Returns true on success.
    /// </summary>
    public async Task<bool> ContributeOneAsync(NpcRaceOverride entry)
    {
        try
        {
            return await _client.ContributeNpcOverrideAsync(entry).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Contributes a local NPC override to the server if ContributeByDefault is on.
    /// Fire-and-forget — called after a successful local save.
    /// </summary>
    public void ContributeIfEnabled(NpcRaceOverride entry)
    {
        if (!_settings.ContributeByDefault)
            return;

        _ = Task.Run(async () =>
        {
            var ok = await _client.ContributeNpcOverrideAsync(entry).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine(
                ok ? $"[NpcSyncService] Contributed NPC {entry.NpcId}"
                   : $"[NpcSyncService] Contribute failed for NPC {entry.NpcId}");
        });
    }


    public async Task<(int Upserted, int Batches)> ContributeManyAsync(IReadOnlyList<NpcRaceOverride> entries, int batchSize = 100)
    {
        if (entries == null || entries.Count == 0)
            return (0, 0);

        int upserted = 0;
        int batches = 0;
        for (int i = 0; i < entries.Count; i += batchSize)
        {
            var batch = entries.Skip(i).Take(batchSize).ToList();
            int wrote = await _client.ContributeNpcOverridesBatchAsync(batch).ConfigureAwait(false);
            upserted += wrote;
            batches++;
        }
        return (upserted, batches);
    }

    // ── Defaults push/pull ────────────────────────────────────────────────────

    /// <summary>
    /// Pushes a JSON payload to the server as the defaults for a type.
    /// Requires admin key if server is configured with one.
    /// </summary>
    public async Task<bool> PushDefaultsAsync(string dataType, string json)
    {
        try
        {
            return await _client.PutDefaultsJsonAsync(dataType, json).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pulls server defaults for a type and applies them locally (full replace).
    /// Used for first-load and manual "Pull from Server" button.
    /// </summary>
    public async Task<bool> PullAndApplyDefaultsAsync(string dataType)
    {
        var json = await _client.GetDefaultsJsonAsync(dataType).ConfigureAwait(false);
        if (json == null)
            return false;

        try
        {
            switch (dataType)
            {
                case "npc-overrides":
                    await ApplyNpcOverridesDefaultsAsync(json).ConfigureAwait(false);
                    break;

                case "voice-profiles":
                    await ApplyVoiceProfilesDefaultsAsync(json);
                    break;

                case "voice-sample-profiles":
                    await ApplyVoiceSampleProfilesDefaultsAsync(json);
                    break;

                case "pronunciation":
                    await ApplyPronunciationDefaultsAsync(json).ConfigureAwait(false);
                    break;

                case "text-shaping":
                    await ApplyTextShapingDefaultsAsync(json).ConfigureAwait(false);
                    break;
            }
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NpcSyncService] Apply {dataType} failed: {ex.Message}");
            return false;
        }
    }

    public async Task<int> PushProviderSlotProfilesAsync(string kind, Dictionary<string, Dictionary<string, TTS.Providers.VoiceProfile>> providers)
    {
        var normalizedKind = NormalizeProfileKind(kind);
        var batch = new List<ServerProviderSlotProfileBatchRecord>();

        foreach (var (providerId, profiles) in providers)
        {
            var serverProviderId = ToServerProviderProfileId(providerId);
            if (string.IsNullOrWhiteSpace(serverProviderId) || profiles == null)
                continue;

            foreach (var (profileId, profile) in profiles)
            {
                if (string.IsNullOrWhiteSpace(profileId) || profile == null)
                    continue;

                batch.Add(new ServerProviderSlotProfileBatchRecord
                {
                    ProviderId = serverProviderId,
                    ProfileKind = normalizedKind,
                    ProfileId = profileId,
                    ProfileJson = profile,
                });
            }
        }

        var total = 0;
        for (int i = 0; i < batch.Count; i += 100)
        {
            var chunk = batch.Skip(i).Take(100).ToList();
            var upserted = await _client.UpsertProviderSlotProfilesBatchAsync(chunk).ConfigureAwait(false);
            if (!upserted.HasValue)
                return -1;
            total += upserted.Value;
        }

        return total;
    }

    public async Task<bool> PullAndApplyProviderSlotProfilesAsync(string kind, string? providerId = null, double sinceTs = 0.0)
    {
        var normalizedKind = NormalizeProfileKind(kind);
        var serverProviderId = ToServerProviderProfileId(providerId);
        var records = await _client.GetProviderSlotProfilesAsync(serverProviderId, normalizedKind, sinceTs).ConfigureAwait(false);
        if (records == null)
            return false;

        if (normalizedKind == "voice_slot")
            await ApplyProviderSlotProfileRecordsAsVoiceProfilesAsync(records).ConfigureAwait(false);
        else
            await ApplyProviderSlotProfileRecordsAsSampleProfilesAsync(records).ConfigureAwait(false);

        return true;
    }

    private static string NormalizeProfileKind(string kind)
        => string.Equals(kind, "voice-profiles", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(kind, "voice_slot", StringComparison.OrdinalIgnoreCase)
            ? "voice_slot"
            : "sample";

    private static string ToServerProviderProfileId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return string.Empty;

        var normalized = providerId.Trim();
        return normalized.StartsWith("remote:", StringComparison.OrdinalIgnoreCase)
            ? normalized["remote:".Length..]
            : normalized;
    }

    private static string ToClientProviderProfileId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId))
            return string.Empty;

        var normalized = providerId.Trim();
        return normalized.StartsWith("remote:", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"remote:{normalized}";
    }

    private async Task ApplyProviderSlotProfileRecordsAsVoiceProfilesAsync(List<ServerProviderSlotProfileRecord> records)
    {
        var grouped = new Dictionary<string, Dictionary<string, RuneReaderVoice.TTS.Providers.VoiceProfile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in records)
        {
            if (string.IsNullOrWhiteSpace(rec.ProviderId) || string.IsNullOrWhiteSpace(rec.ProfileId) || rec.ProfileJson.ValueKind == JsonValueKind.Undefined || rec.ProfileJson.ValueKind == JsonValueKind.Null)
                continue;

            var profile = rec.ProfileJson.Deserialize<RuneReaderVoice.TTS.Providers.VoiceProfile>(JsonOptions);
            if (profile == null)
                continue;

            var providerId = ToClientProviderProfileId(rec.ProviderId);
            if (string.IsNullOrWhiteSpace(providerId))
                continue;

            if (!grouped.TryGetValue(providerId, out var map))
            {
                map = new Dictionary<string, RuneReaderVoice.TTS.Providers.VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                grouped[providerId] = map;
            }

            map[rec.ProfileId.Trim()] = profile;
        }

        if (AppServices.ProviderSlotProfiles != null)
        {
            foreach (var (pid, map) in grouped)
                await AppServices.ProviderSlotProfiles.ReplaceVoiceProfilesAsync(pid, map, "ServerSync");
        }
    }

    private async Task ApplyProviderSlotProfileRecordsAsSampleProfilesAsync(List<ServerProviderSlotProfileRecord> records)
    {
        var grouped = new Dictionary<string, Dictionary<string, TTS.Providers.VoiceProfile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (!string.Equals(record.ProfileKind, "sample", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(record.ProviderId) || string.IsNullOrWhiteSpace(record.ProfileId) ||
                record.ProfileJson.ValueKind == JsonValueKind.Undefined || record.ProfileJson.ValueKind == JsonValueKind.Null)
                continue;

            var profile = record.ProfileJson.Deserialize<TTS.Providers.VoiceProfile>(JsonOptions);
            if (profile == null)
                continue;

            var providerId = ToClientProviderProfileId(record.ProviderId);
            if (string.IsNullOrWhiteSpace(providerId))
                continue;

            if (!grouped.TryGetValue(providerId, out var map))
            {
                map = new Dictionary<string, TTS.Providers.VoiceProfile>(StringComparer.OrdinalIgnoreCase);
                grouped[providerId] = map;
            }

            map[record.ProfileId.Trim()] = profile;
        }

        if (AppServices.ProviderSlotProfiles != null)
        {
            foreach (var (pid, map) in grouped)
                await AppServices.ProviderSlotProfiles.ReplaceSampleProfilesAsync(pid, map, "ServerSync");
        }
        else
        {
            foreach (var (pid, map) in grouped)
                _settings.PerProviderSampleProfiles[pid] = new Dictionary<string, TTS.Providers.VoiceProfile>(map, StringComparer.OrdinalIgnoreCase);

            VoiceSettingsManager.SaveSettings(_settings);
        }
    }

    private async Task ApplyNpcOverridesDefaultsAsync(string json)
    {
        var file = JsonSerializer.Deserialize<NpcOverrideExportFile>(json);
        if (file?.Entries == null) return;

        var records = file.Entries.Select(e => new NpcRaceOverride
        {
            NpcId               = e.NpcId,
            RaceId              = e.RaceId,
            Notes               = e.Notes,
            BespokeSampleId     = e.BespokeSampleId,
            BespokeExaggeration = e.BespokeExaggeration,
            BespokeCfgWeight    = e.BespokeCfgWeight,
            Source              = NpcOverrideSource.CrowdSourced,
        }).ToList();

        await _npcDb.MergeFromServerAsync(records).ConfigureAwait(false);
    }

    private async Task ApplyVoiceProfilesDefaultsAsync(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        var multi = JsonSerializer.Deserialize<TTS.Providers.MultiProviderVoiceProfileExport>(json, options);
        if (multi?.Providers != null && multi.Providers.Count > 0)
        {
            if (AppServices.ProviderSlotProfiles != null)
            {
                foreach (var (providerId, incomingProfiles) in multi.Providers)
                {
                    if (string.IsNullOrWhiteSpace(providerId) || incomingProfiles == null)
                        continue;

                    await AppServices.ProviderSlotProfiles.ReplaceVoiceProfilesAsync(
                        providerId,
                        new System.Collections.Generic.Dictionary<string, TTS.Providers.VoiceProfile>(incomingProfiles, StringComparer.OrdinalIgnoreCase),
                        "ServerSeed");
                }

            }
            else
            {
                foreach (var (providerId, incomingProfiles) in multi.Providers)
                {
                    if (string.IsNullOrWhiteSpace(providerId) || incomingProfiles == null)
                        continue;

                    _settings.PerProviderVoiceProfiles[providerId] = new System.Collections.Generic.Dictionary<string, TTS.Providers.VoiceProfile>(
                        incomingProfiles,
                        StringComparer.OrdinalIgnoreCase);
                }
            }

            if (AppServices.ProviderSlotProfiles == null)
                VoiceSettingsManager.SaveSettings(_settings);
            return;
        }

        var single = JsonSerializer.Deserialize<TTS.Providers.VoiceProfileExport>(json, options);
        if (single?.Profiles == null || string.IsNullOrWhiteSpace(single.ProviderId))
            return;

        if (AppServices.ProviderSlotProfiles != null)
        {
            await AppServices.ProviderSlotProfiles.ReplaceVoiceProfilesAsync(
                single.ProviderId,
                new System.Collections.Generic.Dictionary<string, TTS.Providers.VoiceProfile>(single.Profiles, StringComparer.OrdinalIgnoreCase),
                "ServerSeed");
        }
        else
        {
            _settings.PerProviderVoiceProfiles[single.ProviderId] = new System.Collections.Generic.Dictionary<string, TTS.Providers.VoiceProfile>(
                single.Profiles,
                StringComparer.OrdinalIgnoreCase);
        }

        if (AppServices.ProviderSlotProfiles == null)
            VoiceSettingsManager.SaveSettings(_settings);
    }


    private async Task ApplyVoiceSampleProfilesDefaultsAsync(string json)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var multi = JsonSerializer.Deserialize<TTS.Providers.MultiProviderSampleProfileExport>(json, options);
        if (multi?.Providers == null || multi.Providers.Count == 0)
            return;

        if (AppServices.ProviderSlotProfiles != null)
        {
            foreach (var (providerId, incomingProfiles) in multi.Providers)
            {
                if (string.IsNullOrWhiteSpace(providerId) || incomingProfiles == null)
                    continue;

                await AppServices.ProviderSlotProfiles.ReplaceSampleProfilesAsync(
                    providerId,
                    new System.Collections.Generic.Dictionary<string, TTS.Providers.VoiceProfile>(incomingProfiles, StringComparer.OrdinalIgnoreCase),
                    "ServerSeed");
            }

        }
        else
        {
            foreach (var (providerId, incomingProfiles) in multi.Providers)
            {
                if (string.IsNullOrWhiteSpace(providerId) || incomingProfiles == null)
                    continue;

                _settings.PerProviderSampleProfiles[providerId] = new System.Collections.Generic.Dictionary<string, TTS.Providers.VoiceProfile>(
                    incomingProfiles,
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        if (AppServices.ProviderSlotProfiles == null)
            VoiceSettingsManager.SaveSettings(_settings);
    }

    private async Task ApplyPronunciationDefaultsAsync(string json)
    {
        var file = JsonSerializer.Deserialize<TTS.Pronunciation.PronunciationRuleFile>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (file?.Rules == null) return;

        foreach (var entry in file.Rules)
            await _pronunciationRules.UpsertRuleAsync(entry).ConfigureAwait(false);
    }

    private async Task ApplyTextShapingDefaultsAsync(string json)
    {
        var file = JsonSerializer.Deserialize<TTS.TextSwap.TextSwapRuleFile>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (file?.Rules == null) return;

        foreach (var entry in file.Rules)
            await _textSwapRules.UpsertRuleAsync(entry).ConfigureAwait(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetStatus(string msg) => SyncStatusChanged?.Invoke(msg);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

// ── Bridge to TtsSessionAssembler ─────────────────────────────────────────────
// Legacy compatibility wrapper. Runtime no longer keeps an in-memory NPC
// override mirror; SQLite is authoritative. Kept so constructor wiring does
// not need to change yet.

public sealed class TtsSessionAssemblerBridge
{
    public TtsSessionAssemblerBridge(
        Session.TtsSessionAssembler assembler,
        NpcRaceOverrideDb npcDb)
    {
    }

    public Task ApplyIfNotLocalAsync(
        int npcId, string? catalogId, int raceId,
        string? bespokeSampleId,
        float? bespokeExaggeration,
        float? bespokeCfgWeight)
        => Task.CompletedTask;
}

// ── Export file DTOs (mirrors client export format) ───────────────────────────
// NpcOverrideExportFile mirrors the format written by MainWindow.NpcOverrides.cs

internal sealed class NpcOverrideExportFile
{
    [System.Text.Json.Serialization.JsonPropertyName("version")]
    public string Version { get; set; } = "1";

    [System.Text.Json.Serialization.JsonPropertyName("entries")]
    public List<NpcOverrideExportEntry> Entries { get; set; } = new();
}

internal sealed class NpcOverrideExportEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("NpcId")]
    public int     NpcId               { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("RaceId")]
    public int     RaceId              { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("Notes")]
    public string? Notes               { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("BespokeSampleId")]
    public string? BespokeSampleId     { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("BespokeExaggeration")]
    public float?  BespokeExaggeration { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("BespokeCfgWeight")]
    public float?  BespokeCfgWeight    { get; set; }
}