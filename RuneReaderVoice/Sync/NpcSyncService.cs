// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

// Sync/NpcSyncService.cs
// Background service for NPC override crowd-source sync.
//
// Responsibilities:
//   1. On startup: if !FirstLoadComplete, pull all four defaults from server
//   2. Poll every 5 minutes for new NPC override records (delta since LastNpcSyncAt)
//   3. Merge new records into local DB (Local entries always win)
//   4. Contribute local saves to server when ContributeByDefault = true

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Data;
using RuneReaderVoice.TTS.Pronunciation;
using RuneReaderVoice.TTS.TextSwap;

namespace RuneReaderVoice.Sync;

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
        await PullAndApplyDefaultsAsync("voice-profiles").ConfigureAwait(false);
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
            // Apply to in-memory assembler store so next dialog picks up new entries
            foreach (var record in domainRecords)
            {
                await _assemblerBridge.ApplyIfNotLocalAsync(
                    record.NpcId, record.RaceId,
                    record.BespokeSampleId,
                    record.BespokeExaggeration,
                    record.BespokeCfgWeight);
            }

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

    private Task ApplyVoiceProfilesDefaultsAsync(string json)
    {
        var export = JsonSerializer.Deserialize<TTS.Providers.VoiceProfileExport>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (export?.Profiles == null) return Task.CompletedTask;

        if (!_settings.PerProviderVoiceProfiles.TryGetValue(export.ProviderId, out var existing))
        {
            existing = new System.Collections.Generic.Dictionary<string, TTS.Providers.VoiceProfile>(
                StringComparer.OrdinalIgnoreCase);
            _settings.PerProviderVoiceProfiles[export.ProviderId] = existing;
        }

        foreach (var (slot, profile) in export.Profiles)
        {
            if (!existing.ContainsKey(slot))
                existing[slot] = profile;
        }

        VoiceSettingsManager.SaveSettings(_settings);
        return Task.CompletedTask;
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
// Thin wrapper so NpcSyncService doesn't depend directly on TtsSessionAssembler.
// Avoids circular dependency between Session and Sync namespaces.

public sealed class TtsSessionAssemblerBridge
{
    private readonly Session.TtsSessionAssembler _assembler;
    private readonly NpcRaceOverrideDb           _npcDb;

    public TtsSessionAssemblerBridge(
        Session.TtsSessionAssembler assembler,
        NpcRaceOverrideDb npcDb)
    {
        _assembler = assembler;
        _npcDb     = npcDb;
    }

    /// <summary>
    /// Applies a server record to the in-memory assembler store
    /// only if no Local entry exists for this NpcId.
    /// </summary>
    public async Task ApplyIfNotLocalAsync(
        int npcId, int raceId,
        string? bespokeSampleId,
        float? bespokeExaggeration,
        float? bespokeCfgWeight)
    {
        if (_assembler == null) return;

        var existing = await _npcDb.GetOverrideAsync(npcId).ConfigureAwait(false);
        if (existing?.Source == NpcOverrideSource.Local)
            return;

        _assembler.ApplyRaceOverride(
            npcId, raceId,
            bespokeSampleId:     bespokeSampleId,
            bespokeExaggeration: bespokeExaggeration,
            bespokeCfgWeight:    bespokeCfgWeight);
    }
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
