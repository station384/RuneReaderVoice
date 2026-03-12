// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

// NpcRaceOverrideDb.cs
// SQLite-backed store for NPC → race overrides.
//
// One row per NpcId. Local entries take priority over crowd-sourced or
// confirmed entries from the server. Client code never deletes non-local rows.
//
// Uses sqlite-net-pcl (SQLiteAsyncConnection) — no WinRT dependency.
//
// Thread safety: SQLiteAsyncConnection is internally serialized.
//
// Schema version: 1
// Location: config/npc-overrides.db (alongside settings.json)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.Data;

// ── ORM row type ──────────────────────────────────────────────────────────────

[Table("npc_race_overrides")]
internal sealed class NpcRaceOverrideRow
{
    [PrimaryKey, Column("npc_id")]
    public int NpcId { get; set; }

    [Column("race_id")]
    public int RaceId { get; set; }

    [Column("accent_group")]
    public string AccentGroup { get; set; } = string.Empty;

    [Column("notes")]
    public string? Notes { get; set; }

    [Column("source")]
    public string Source { get; set; } = "local";

    [Column("confidence")]
    public int? Confidence { get; set; }

    [Column("created_at")]
    public string CreatedAt { get; set; } = string.Empty;

    [Column("updated_at")]
    public string UpdatedAt { get; set; } = string.Empty;
}

// ── Public DB service ─────────────────────────────────────────────────────────

public sealed class NpcRaceOverrideDb : IDisposable
{
    private readonly SQLiteAsyncConnection _conn;
    private bool _disposed;

    // ── Construction / schema ─────────────────────────────────────────────────

    public NpcRaceOverrideDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var flags =
            SQLiteOpenFlags.ReadWrite |
            SQLiteOpenFlags.Create    |
            SQLiteOpenFlags.FullMutex;

        _conn = new SQLiteAsyncConnection(dbPath, flags);
    }

    /// <summary>
    /// Creates tables and applies WAL mode. Call once after construction.
    /// </summary>
    public async Task InitializeAsync()
    {
        await _conn.CreateTableAsync<NpcRaceOverrideRow>();
        await _conn.ExecuteScalarAsync<string>("PRAGMA journal_mode = WAL;");
        await _conn.ExecuteAsync("PRAGMA synchronous = NORMAL;");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the override for the given NPC, or null if none exists.
    /// Local entries shadow crowd-sourced/confirmed entries for the same NpcId.
    /// </summary>
    public async Task<NpcRaceOverride?> GetOverrideAsync(int npcId, CancellationToken ct = default)
    {
        var rows = await _conn.QueryAsync<NpcRaceOverrideRow>(
            "SELECT * FROM npc_race_overrides WHERE npc_id = ? ORDER BY CASE source WHEN 'local' THEN 0 WHEN 'crowdsourced' THEN 1 ELSE 2 END LIMIT 1;",
            npcId);

        var row = rows.FirstOrDefault();
        return row == null ? null : ToModel(row);
    }

    /// <summary>
    /// Inserts or updates a local override for the given NPC.
    /// Non-local (server) rows for the same NpcId are not affected.
    /// </summary>
    public async Task UpsertAsync(int npcId, int raceId, string? notes, CancellationToken ct = default)
    {
        var now   = DateTime.UtcNow.ToString("O");
        var group = ResolveAccentGroup(raceId).ToString();

        var existing = await _conn.FindAsync<NpcRaceOverrideRow>(npcId);

        if (existing != null && existing.Source == "local")
        {
            existing.RaceId      = raceId;
            existing.AccentGroup = group;
            existing.Notes       = notes;
            existing.UpdatedAt   = now;
            await _conn.UpdateAsync(existing);
        }
        else if (existing == null)
        {
            await _conn.InsertAsync(new NpcRaceOverrideRow
            {
                NpcId       = npcId,
                RaceId      = raceId,
                AccentGroup = group,
                Notes       = notes,
                Source      = "local",
                Confidence  = null,
                CreatedAt   = now,
                UpdatedAt   = now,
            });
        }
        // If existing is non-local, we leave it alone — server owns those rows.
    }

    /// <summary>
    /// Deletes a LOCAL override for the given NPC.
    /// Non-local rows are never deleted by the client.
    /// </summary>
    public async Task DeleteAsync(int npcId, CancellationToken ct = default)
    {
        await _conn.ExecuteAsync(
            "DELETE FROM npc_race_overrides WHERE npc_id = ? AND source = 'local';",
            npcId);
    }

    /// <summary>Returns all overrides, sorted by source priority then NpcId.</summary>
    public async Task<IReadOnlyList<NpcRaceOverride>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await _conn.QueryAsync<NpcRaceOverrideRow>(
            "SELECT * FROM npc_race_overrides ORDER BY CASE source WHEN 'local' THEN 0 WHEN 'crowdsourced' THEN 1 ELSE 2 END, npc_id;");

        return rows.Select(ToModel).ToList();
    }

    /// <summary>
    /// Merges a batch of server-supplied entries (crowd-sourced or confirmed).
    /// Local entries for the same NpcId are never overwritten.
    /// </summary>
    public async Task MergeServerEntriesAsync(
        IEnumerable<NpcRaceOverride> entries, CancellationToken ct = default)
    {
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var existing = await _conn.FindAsync<NpcRaceOverrideRow>(entry.NpcId);
            if (existing?.Source == "local")
                continue;

            var row = new NpcRaceOverrideRow
            {
                NpcId       = entry.NpcId,
                RaceId      = entry.RaceId,
                AccentGroup = entry.AccentGroup.ToString(),
                Notes       = entry.Notes,
                Source      = entry.Source.ToString().ToLowerInvariant(),
                Confidence  = entry.Confidence,
                CreatedAt   = entry.CreatedAt.ToString("O"),
                UpdatedAt   = entry.UpdatedAt.ToString("O"),
            };

            if (existing == null)
                await _conn.InsertAsync(row);
            else
                await _conn.UpdateAsync(row);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static NpcRaceOverride ToModel(NpcRaceOverrideRow r)
    {
        var source = r.Source switch
        {
            "crowdsourced" => NpcOverrideSource.CrowdSourced,
            "confirmed"    => NpcOverrideSource.Confirmed,
            _              => NpcOverrideSource.Local,
        };

        Enum.TryParse<AccentGroup>(r.AccentGroup, out var group);

        return new NpcRaceOverride
        {
            NpcId       = r.NpcId,
            RaceId      = r.RaceId,
            AccentGroup = group,
            Notes       = r.Notes,
            Source      = source,
            Confidence  = r.Confidence,
            CreatedAt   = DateTime.Parse(r.CreatedAt),
            UpdatedAt   = DateTime.Parse(r.UpdatedAt),
        };
    }

    private static AccentGroup ResolveAccentGroup(int raceId)
    {
        var slot = RaceAccentMapping.Resolve(raceId, flags: 0, isMale: true, isFemale: false);
        return slot.Group;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = _conn.CloseAsync();
    }
}
