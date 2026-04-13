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
using System.Threading.Tasks;
using RuneReaderVoice.Data;

namespace RuneReaderVoice.TTS.TextSwap;

// ── Row extension helpers ─────────────────────────────────────────────────────

public static class TextSwapRuleRowExtensions
{
    public static TextSwapRuleRow ToRow(this TextSwapRuleEntry entry)
        => new()
        {
            FindText       = entry.FindText,
            ReplaceText    = entry.ReplaceText,
            ReplaceWithCrLf = entry.ReplaceWithCrLf,
            WholeWord      = entry.WholeWord,
            CaseSensitive  = entry.CaseSensitive,
            Enabled        = entry.Enabled,
            Priority       = entry.Priority,
            Notes          = entry.Notes,
        };

    public static TextSwapRuleEntry ToEntry(this TextSwapRuleRow row)
        => new()
        {
            FindText       = row.FindText,
            ReplaceText    = row.ReplaceText,
            ReplaceWithCrLf = row.ReplaceWithCrLf,
            WholeWord      = row.WholeWord,
            CaseSensitive  = row.CaseSensitive,
            Enabled        = row.Enabled,
            Priority       = row.Priority,
            Notes          = row.Notes,
        };
}

// ── Instance store ────────────────────────────────────────────────────────────


public sealed record TextSwapRulePage(IReadOnlyList<TextSwapRuleEntry> Items, int TotalCount, int PageNumber, int PageSize);

public sealed class TextSwapRuleStore
{
    private readonly RvrDb _db;

    public TextSwapRuleStore(RvrDb db)
    {
        _db = db;
    }

    public async Task<List<TextSwapRuleEntry>> GetAllEntriesAsync()
    {
        var rows = await _db.Connection.Table<TextSwapRuleRow>().ToListAsync();
        return rows.Select(r => r.ToEntry()).ToList();
    }

    public async Task<TextSwapRulePage> QueryPageAsync(int pageNumber, int pageSize)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 25;

        var rows = await _db.Connection.Table<TextSwapRuleRow>().ToListAsync();
        var ordered = rows
            .Select(r => r.ToEntry())
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.FindText, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var totalCount = ordered.Count;
        var items = ordered.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToList();
        return new TextSwapRulePage(items, totalCount, pageNumber, pageSize);
    }

    /// <summary>
    /// Returns enabled, valid rules as domain objects — used to rebuild the processor.
    /// </summary>
    public async Task<IReadOnlyList<TextSwapRule>> LoadUserRulesAsync()
    {
        var rows = await _db.Connection.Table<TextSwapRuleRow>().ToListAsync();
        return rows
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.FindText))
            .Select(r => r.ToEntry().ToRule())
            .ToList();
    }

    public async Task AddDefaultRulesAsync()
    {
        foreach (var entry in DefaultTextSwapRules.CreateDefaultEntries())
            await UpsertRuleAsync(entry);
    }

    public async Task UpsertRuleAsync(TextSwapRuleEntry entry)
    {
        var existing = await _db.Connection.Table<TextSwapRuleRow>()
            .Where(r => r.FindText == entry.FindText
                     && r.WholeWord == entry.WholeWord
                     && r.CaseSensitive == entry.CaseSensitive)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.ReplaceText    = entry.ReplaceText;
            existing.ReplaceWithCrLf = entry.ReplaceWithCrLf;
            existing.Enabled        = entry.Enabled;
            existing.Priority       = entry.Priority;
            existing.Notes          = entry.Notes;
            await _db.Connection.UpdateAsync(existing);
        }
        else
        {
            await _db.Connection.InsertAsync(entry.ToRow());
        }
    }

    public async Task DeleteRuleAsync(TextSwapRuleEntry entry)
    {
        var existing = await _db.Connection.Table<TextSwapRuleRow>()
            .Where(r => r.FindText == entry.FindText
                     && r.WholeWord == entry.WholeWord
                     && r.CaseSensitive == entry.CaseSensitive)
            .FirstOrDefaultAsync();

        if (existing != null)
            await _db.Connection.DeleteAsync(existing);
    }

    public Task ClearAllAsync()
        => _db.ClearTableAsync(RvrTable.TextSwapRules);
}

// ── Legacy domain models (kept for JSON migration deserialization) ─────────────

public sealed class TextSwapRuleFile
{
    public int Version { get; set; } = 1;
    public List<TextSwapRuleEntry> Rules { get; set; } = new();
}

public sealed class TextSwapRuleEntry
{
    public string FindText { get; set; } = string.Empty;
    public string ReplaceText { get; set; } = string.Empty;
    public bool ReplaceWithCrLf { get; set; }
    public bool WholeWord { get; set; }
    public bool CaseSensitive { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public string Notes { get; set; } = string.Empty;

    public TextSwapRule ToRule()
        => new(
            FindText:      FindText,
            ReplaceText:   ReplaceWithCrLf ? "\r\n" : ReplaceText,
            WholeWord:     WholeWord,
            CaseSensitive: CaseSensitive,
            Priority:      Priority);

    public static TextSwapRuleEntry FromRule(TextSwapRule rule, string notes = "")
        => new()
        {
            FindText       = rule.FindText,
            ReplaceText    = rule.ReplaceText == "\r\n" ? string.Empty : rule.ReplaceText,
            ReplaceWithCrLf = rule.ReplaceText == "\r\n",
            WholeWord      = rule.WholeWord,
            CaseSensitive  = rule.CaseSensitive,
            Enabled        = true,
            Priority       = rule.Priority,
            Notes          = notes,
        };
}