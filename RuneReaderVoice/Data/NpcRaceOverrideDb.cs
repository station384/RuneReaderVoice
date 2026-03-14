// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

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

    public Task UpsertAsync(int npcId, int raceId, string? notes)
        => _db.Connection.InsertOrReplaceAsync(new NpcRaceOverrideRow
        {
            NpcId  = npcId,
            RaceId = raceId,
            Notes  = notes ?? string.Empty,
        });

    public Task DeleteAsync(int npcId)
        => _db.Connection.DeleteAsync<NpcRaceOverrideRow>(npcId);

    private static NpcRaceOverride ToModel(NpcRaceOverrideRow row)
        => new()
        {
            NpcId       = row.NpcId,
            RaceId      = row.RaceId,
            Notes       = row.Notes,
            AccentGroup = RaceAccentMapping.ResolveAccentGroup(row.RaceId)
                          ?? AccentGroup.Narrator,
            Source      = NpcOverrideSource.Local,
        };
}
