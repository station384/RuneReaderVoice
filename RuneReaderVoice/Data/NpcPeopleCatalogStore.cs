// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;


namespace RuneReaderVoice.Data;

public sealed class NpcPeopleCatalogStore
{
    private readonly RvrDb _db;
    public NpcPeopleCatalogStore(RvrDb db) => _db = db;

    public Task<List<NpcPeopleCatalogRow>> GetAllAsync()
        => _db.Connection.Table<NpcPeopleCatalogRow>().ToListAsync();

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
                AccentGroupName = x.AccentGroupName,
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
