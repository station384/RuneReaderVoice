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
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.Data;

/// <summary>
/// Exposes the NpcRaceOverrides table via the shared RvrDb.
/// NpcRaceOverride domain model and NpcOverrideSource enum are defined elsewhere in the project.
/// </summary>

public sealed record NpcRaceOverridePage(
    IReadOnlyList<NpcRaceOverride> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

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

    public async Task<NpcRaceOverridePage> QueryPageAsync(
        string? filter,
        int pageNumber,
        int pageSize,
        CancellationToken ct = default)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize   = Math.Clamp(pageSize, 25, 500);

        var whereClauses = new List<string>();
        var args = new List<object>();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var like = $"%{filter.Trim()}%";
            whereClauses.Add("(CAST(NpcId AS TEXT) LIKE ? OR Notes LIKE ? OR Source LIKE ? OR CatalogId LIKE ?)");
            args.Add(like);
            args.Add(like);
            args.Add(like);
            args.Add(like);
        }

        var whereSql = whereClauses.Count == 0
            ? string.Empty
            : " WHERE " + string.Join(" AND ", whereClauses);

        var countSql = "SELECT COUNT(*) AS Value FROM NpcRaceOverrides" + whereSql;
        var total = (await _db.Connection.QueryAsync<CountRow>(countSql, args.ToArray()))
            .FirstOrDefault()?.Value ?? 0;

        var offset = (pageNumber - 1) * pageSize;
        var pageArgs = new List<object>(args) { pageSize, offset };
        var pageSql = "SELECT * FROM NpcRaceOverrides" + whereSql + " ORDER BY NpcId LIMIT ? OFFSET ?";
        var rows = await _db.Connection.QueryAsync<NpcRaceOverrideRow>(pageSql, pageArgs.ToArray());

        return new NpcRaceOverridePage(rows.Select(ToModel).ToList(), total, pageNumber, pageSize);
    }

    private sealed class CountRow
    {
        public int Value { get; set; }
    }


    public Task UpsertAsync(int npcId, string catalogId, string? notes,
        int raceId = 0,
        string? bespokeSampleId = null,
        float? bespokeExaggeration = null,
        float? bespokeCfgWeight = null,
        bool useNpcIdAsSeed = false,
        string source = "Local",
        int confidence = 0)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return _db.Connection.InsertOrReplaceAsync(new NpcRaceOverrideRow
        {
            NpcId               = npcId,
            RaceId              = raceId,
            CatalogId           = catalogId ?? string.Empty,
            Notes               = notes ?? string.Empty,
            BespokeSampleId     = bespokeSampleId,
            BespokeExaggeration = bespokeExaggeration,
            BespokeCfgWeight    = bespokeCfgWeight,
            UseNpcIdAsSeed     = useNpcIdAsSeed,
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
                CatalogId           = record.CatalogId ?? string.Empty,
                Notes               = record.Notes ?? string.Empty,
                BespokeSampleId     = record.BespokeSampleId,
                BespokeExaggeration = record.BespokeExaggeration,
                BespokeCfgWeight    = record.BespokeCfgWeight,
                UseNpcIdAsSeed     = record.UseNpcIdAsSeed,
                Source              = record.Source.ToString(),
                Confidence          = record.Confidence ?? 0,
                UpdatedAt           = record.UpdatedAt,
            });
            count++;
        }
        return count;
    }


    /// <summary>
    /// Temporary legacy shim used only for importing older NPC override JSON files
    /// that still carry RaceId but not CatalogId. This should be removed after
    /// one-time migration of old exports is no longer needed.
    /// </summary>
    public static string LegacyRaceIdToCatalogId(int raceId)
    {
        if (raceId <= 0)
            return string.Empty;

        var group = RaceAccentMapping.ResolveAccentGroup(raceId);
        if (group == null)
            return string.Empty;

        return group.Value switch
        {
            AccentGroup.Human => "human",
            AccentGroup.NightElf => "nightelf",
            AccentGroup.Dwarf => "dwarf",
            AccentGroup.DarkIronDwarf => "darkirondwarf",
            AccentGroup.Gnome => "gnome",
            AccentGroup.Mechagnome => "mechagnome",
            AccentGroup.Draenei => "draenei",
            AccentGroup.LightforgedDraenei => "lightforgeddraenei",
            AccentGroup.Worgen => "worgen",
            AccentGroup.KulTiran => "kultiran",
            AccentGroup.BloodElf => "bloodelf",
            AccentGroup.VoidElf => "voidelf",
            AccentGroup.Orc => "orc",
            AccentGroup.MagharOrc => "magharorc",
            AccentGroup.Undead => "undead",
            AccentGroup.Tauren => "tauren",
            AccentGroup.HighmountainTauren => "highmountaintauren",
            AccentGroup.Troll => "troll",
            AccentGroup.ZandalariTroll => "zandalaritroll",
            AccentGroup.Goblin => "goblin",
            AccentGroup.Nightborne => "nightborne",
            AccentGroup.Vulpera => "vulpera",
            AccentGroup.Pandaren => "pandaren",
            AccentGroup.Earthen => "earthen",
            AccentGroup.Haranir => "haranir",
            AccentGroup.Dracthyr => "dracthyr",
            AccentGroup.Dragonkin => "dragonkin",
            AccentGroup.Elemental => "elemental",
            AccentGroup.Giant => "giant",
            AccentGroup.Mechanical => "mechanical",
            AccentGroup.Illidari => "illidari",
            AccentGroup.Amani => "amani",
            AccentGroup.Arathi => "arathi",
            AccentGroup.Broken => "broken",
            AccentGroup.Centaur => "centaur",
            AccentGroup.DarkTroll => "darktroll",
            AccentGroup.Dredger => "dredger",
            AccentGroup.Dryad => "dryad",
            AccentGroup.Faerie => "faerie",
            AccentGroup.Fungarian => "fungarian",
            AccentGroup.Grummle => "grummle",
            AccentGroup.Hobgoblin => "hobgoblin",
            AccentGroup.Kyrian => "kyrian",
            AccentGroup.Nerubian => "nerubian",
            AccentGroup.Refti => "refti",
            AccentGroup.Revantusk => "revantusk",
            AccentGroup.Rutaani => "rutaani",
            AccentGroup.Shadowpine => "shadowpine",
            AccentGroup.Titan => "titan",
            AccentGroup.Tortollan => "tortollan",
            AccentGroup.Tuskarr => "tuskarr",
            AccentGroup.Venthyr => "venthyr",
            AccentGroup.ZulAman => "zulaman",
            _ => string.Empty,
        };
    }

    private static NpcRaceOverride ToModel(NpcRaceOverrideRow row)
    {
        Enum.TryParse<NpcOverrideSource>(row.Source, out var source);
        return new()
        {
            NpcId               = row.NpcId,
            RaceId              = row.RaceId,
            CatalogId           = row.CatalogId ?? string.Empty,
            Notes               = row.Notes,
            BespokeSampleId     = row.BespokeSampleId,
            BespokeExaggeration = row.BespokeExaggeration,
            BespokeCfgWeight    = row.BespokeCfgWeight,
            UseNpcIdAsSeed      = row.UseNpcIdAsSeed,
            Source              = source,
            Confidence          = row.Confidence,
            UpdatedAt           = row.UpdatedAt,
        };
    }
}