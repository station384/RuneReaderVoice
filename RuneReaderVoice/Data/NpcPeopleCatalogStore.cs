// SPDX-License-Identifier: GPL-3.0-only
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SQLite;

namespace RuneReaderVoice.Data;

public sealed record NpcPeopleCatalogPage(
    IReadOnlyList<NpcPeopleCatalogRow> Items,
    int TotalCount,
    int PageNumber,
    int PageSize);

public sealed class NpcPeopleCatalogStore
{
    private readonly RvrDb _db;
    public NpcPeopleCatalogStore(RvrDb db) => _db = db;

    public Task<List<NpcPeopleCatalogRow>> GetAllAsync()
        => _db.Connection.Table<NpcPeopleCatalogRow>().ToListAsync();

    public Task<List<NpcPeopleCatalogRow>> GetEnabledAsync()
        => _db.Connection.Table<NpcPeopleCatalogRow>()
            .Where(x => x.Enabled)
            .ToListAsync();


    public Task<List<NpcPeopleCatalogRow>> QueryEnabledAsync(string? filter, int limit = 500)
    {
        limit = Math.Clamp(limit, 25, 1000);

        var whereClauses = new List<string> { "Enabled = 1" };
        var args = new List<object>();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var like = $"%{filter.Trim()}%";
            whereClauses.Add("(Id LIKE ? OR DisplayName LIKE ? OR AccentLabel LIKE ? OR Source LIKE ?)");
            args.Add(like);
            args.Add(like);
            args.Add(like);
            args.Add(like);
        }

        var sql = "SELECT * FROM NpcPeopleCatalog WHERE " + string.Join(" AND ", whereClauses) +
                  " ORDER BY SortOrder, DisplayName COLLATE NOCASE LIMIT ?";
        args.Add(limit);
        return _db.Connection.QueryAsync<NpcPeopleCatalogRow>(sql, args.ToArray());
    }
    public async Task<NpcPeopleCatalogPage> QueryPageAsync(string? filter, int pageNumber, int pageSize)
    {
        pageNumber = Math.Max(1, pageNumber);
        pageSize   = Math.Clamp(pageSize, 25, 500);

        var whereClauses = new List<string>();
        var args = new List<object>();

        if (!string.IsNullOrWhiteSpace(filter))
        {
            var like = $"%{filter.Trim()}%";
            whereClauses.Add("(Id LIKE ? OR DisplayName LIKE ? OR AccentLabel LIKE ? OR Source LIKE ?)");
            args.Add(like);
            args.Add(like);
            args.Add(like);
            args.Add(like);
        }

        var whereSql = whereClauses.Count == 0
            ? string.Empty
            : " WHERE " + string.Join(" AND ", whereClauses);

        var countSql = "SELECT COUNT(*) AS Value FROM NpcPeopleCatalog" + whereSql;
        var total = (await _db.Connection.QueryAsync<CountRow>(countSql, args.ToArray()))
            .FirstOrDefault()?.Value ?? 0;

        var offset = (pageNumber - 1) * pageSize;
        var pageArgs = new List<object>(args) { pageSize, offset };
        var pageSql = "SELECT * FROM NpcPeopleCatalog" + whereSql + " ORDER BY SortOrder, DisplayName COLLATE NOCASE LIMIT ? OFFSET ?";
        var rows = await _db.Connection.QueryAsync<NpcPeopleCatalogRow>(pageSql, pageArgs.ToArray());

        return new NpcPeopleCatalogPage(rows, total, pageNumber, pageSize);
    }

    private sealed class CountRow
    {
        public int Value { get; set; }
    }

    public async Task<NpcPeopleCatalogRow?> GetByIdAsync(string id)
    {
        NpcPeopleCatalogRow? row = await _db.Connection.FindAsync<NpcPeopleCatalogRow>(id);
        return row;
    }

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
