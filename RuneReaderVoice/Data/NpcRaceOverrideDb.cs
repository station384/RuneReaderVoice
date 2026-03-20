// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.Data;

/// <summary>
/// Exposes the NpcRaceOverrides table via the shared RvrDb.
/// NpcRaceOverride domain model and NpcOverrideSource enum are defined elsewhere in the project.
/// </summary>
public sealed class NpcRaceOverrideDb
{
    private readonly RvrDb _db;

    public NpcRaceOverrideDb(RvrDb db)
    {
        _db = db;
    }

    // Called by Program.cs after RvrDb.InitializeAsync() — no-op; table already created.
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task<IReadOnlyList<NpcRaceOverride>> GetAllAsync(
        CancellationToken ct = default)
    {
        var rows = await _db.Connection.Table<NpcRaceOverrideRow>().ToListAsync();
        return rows.Select(ToModel).ToList();
    }

    public async Task<NpcRaceOverride?> GetOverrideAsync(int npcId)
    {
        var row = await _db.Connection.Table<NpcRaceOverrideRow>()
            .Where(r => r.NpcId == npcId)
            .FirstOrDefaultAsync();
        return row == null ? null : ToModel(row);
    }

    public Task UpsertAsync(int npcId, int raceId, string? notes,
        string? bespokeSampleId = null,
        float? bespokeExaggeration = null,
        float? bespokeCfgWeight = null,
        string source = "Local",
        int confidence = 0)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return _db.Connection.InsertOrReplaceAsync(new NpcRaceOverrideRow
        {
            NpcId               = npcId,
            RaceId              = raceId,
            Notes               = notes ?? string.Empty,
            BespokeSampleId     = bespokeSampleId,
            BespokeExaggeration = bespokeExaggeration,
            BespokeCfgWeight    = bespokeCfgWeight,
            Source              = source,
            Confidence          = confidence,
            UpdatedAt           = now,
        });
    }

    public Task DeleteAsync(int npcId)
        => _db.Connection.DeleteAsync<NpcRaceOverrideRow>(npcId);

    /// <summary>
    /// Merges a batch of server records into the local DB.
    /// Local entries always win — server records only fill gaps or update
    /// existing server-sourced entries. Never overwrites a Local entry.
    /// Returns the count of records actually written.
    /// </summary>
    public async Task<int> MergeFromServerAsync(IEnumerable<NpcRaceOverride> serverRecords)
    {
        int count = 0;
        foreach (var record in serverRecords)
        {
            var existing = await _db.Connection.Table<NpcRaceOverrideRow>()
                .Where(r => r.NpcId == record.NpcId)
                .FirstOrDefaultAsync();

            // Local always wins — skip if a local entry exists
            if (existing != null && existing.Source == "Local")
                continue;

            await _db.Connection.InsertOrReplaceAsync(new NpcRaceOverrideRow
            {
                NpcId               = record.NpcId,
                RaceId              = record.RaceId,
                Notes               = record.Notes ?? string.Empty,
                BespokeSampleId     = record.BespokeSampleId,
                BespokeExaggeration = record.BespokeExaggeration,
                BespokeCfgWeight    = record.BespokeCfgWeight,
                Source              = record.Source.ToString(),
                Confidence          = record.Confidence ?? 0,
                UpdatedAt           = record.UpdatedAt,
            });
            count++;
        }
        return count;
    }

    private static NpcRaceOverride ToModel(NpcRaceOverrideRow row)
    {
        Enum.TryParse<NpcOverrideSource>(row.Source, out var source);
        return new()
        {
            NpcId               = row.NpcId,
            RaceId              = row.RaceId,
            Notes               = row.Notes,
            BespokeSampleId     = row.BespokeSampleId,
            BespokeExaggeration = row.BespokeExaggeration,
            BespokeCfgWeight    = row.BespokeCfgWeight,
            AccentGroup         = RaceAccentMapping.ResolveAccentGroup(row.RaceId)
                                  ?? AccentGroup.Narrator,
            Source              = source,
            Confidence          = row.Confidence,
            UpdatedAt           = row.UpdatedAt,
        };
    }
}
