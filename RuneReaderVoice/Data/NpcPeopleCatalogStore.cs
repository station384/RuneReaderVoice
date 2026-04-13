// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SQLite;

namespace RuneReaderVoice.Data;

public sealed class NpcPeopleCatalogStore
{
    private readonly RvrDb _db;
    public NpcPeopleCatalogStore(RvrDb db) => _db = db;

    public Task<List<NpcPeopleCatalogRow>> GetAllAsync()
        => _db.Connection.Table<NpcPeopleCatalogRow>().ToListAsync();


    public Task<NpcPeopleCatalogRow?> GetByIdAsync(string id)
        => _db.Connection.FindAsync<NpcPeopleCatalogRow>(id);

    public async Task UpsertAsync(NpcPeopleCatalogRow row)
    {
        var existing = await GetByIdAsync(row.Id);
        if (existing == null)
            await _db.Connection.InsertAsync(row);
        else
            await _db.Connection.InsertOrReplaceAsync(row);
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        var row = await GetByIdAsync(id);
        if (row == null)
            return;

        row.Enabled = enabled;
        row.UpdatedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await _db.Connection.InsertOrReplaceAsync(row);
    }

    public async Task SeedFromLegacyCatalogAsync()
    {
        var count = await _db.Connection.Table<NpcPeopleCatalogRow>().CountAsync();
        if (count > 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var rows = NpcPeopleSeedCatalog.All
            .Select(x => new NpcPeopleCatalogRow
            {
                Id = x.Id,
                DisplayName = x.DisplayName,
                AccentLabel = x.AccentLabel,
                HasMale = x.HasMale,
                HasFemale = x.HasFemale,
                HasNeutral = x.HasNeutral,
                Enabled = true,
                SortOrder = x.SortOrder,
                Source = "Seeded",
                UpdatedUtc = now,
            })
            .ToList();
        await _db.Connection.InsertAllAsync(rows);
    }
}
