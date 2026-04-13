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
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Pronunciation;

// ── Row extension helpers ─────────────────────────────────────────────────────

public static class PronunciationRuleRowExtensions
{
    public static PronunciationRuleRow ToRow(this PronunciationRuleEntry entry)
        => new()
        {
            MatchText     = entry.MatchText,
            PhonemeText   = entry.PhonemeText,
            Scope         = entry.Scope,
            AccentGroup   = entry.AccentGroup,
            WholeWord     = entry.WholeWord,
            CaseSensitive = entry.CaseSensitive,
            Enabled       = entry.Enabled,
            Priority      = entry.Priority,
            Notes         = entry.Notes,
        };

    public static PronunciationRuleEntry ToEntry(this PronunciationRuleRow row)
        => new()
        {
            MatchText     = row.MatchText,
            PhonemeText   = row.PhonemeText,
            Scope         = row.Scope,
            AccentGroup   = row.AccentGroup,
            WholeWord     = row.WholeWord,
            CaseSensitive = row.CaseSensitive,
            Enabled       = row.Enabled,
            Priority      = row.Priority,
            Notes         = row.Notes,
        };
}

// ── Instance store ────────────────────────────────────────────────────────────


public sealed record PronunciationRulePage(IReadOnlyList<PronunciationRuleEntry> Items, int TotalCount, int PageNumber, int PageSize);

public sealed class PronunciationRuleStore
{
    private readonly RvrDb _db;

    public PronunciationRuleStore(RvrDb db)
    {
        _db = db;
    }

    public async Task<List<PronunciationRuleEntry>> GetAllEntriesAsync()
    {
        var rows = await _db.Connection.Table<PronunciationRuleRow>().ToListAsync();
        return rows.Select(r => r.ToEntry()).ToList();
    }

    public async Task<PronunciationRulePage> QueryPageAsync(int pageNumber, int pageSize)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 25;

        var offset = (pageNumber - 1) * pageSize;
        var totalCount = await _db.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PronunciationRules");
        var rows = await _db.Connection.QueryAsync<PronunciationRuleRow>(
            "SELECT * FROM PronunciationRules ORDER BY Priority DESC, MatchText COLLATE NOCASE ASC, Id ASC LIMIT ? OFFSET ?",
            pageSize,
            offset);

        var items = rows.Select(r => r.ToEntry()).ToList();
        return new PronunciationRulePage(items, totalCount, pageNumber, pageSize);
    }

    /// <summary>
    /// Returns the enabled, valid rules as domain objects — used to rebuild the processor.
    /// </summary>
    public async Task<IReadOnlyList<PronunciationRule>> LoadUserRulesAsync()
    {
        var rows = await _db.Connection.Table<PronunciationRuleRow>().ToListAsync();
        return rows
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.MatchText) && !string.IsNullOrWhiteSpace(r.PhonemeText))
            .Select(r => r.ToEntry().ToRule())
            .ToList();
    }

    public async Task UpsertRuleAsync(PronunciationRuleEntry entry)
    {
        var existing = await _db.Connection.Table<PronunciationRuleRow>()
            .Where(r => r.MatchText == entry.MatchText
                     && r.Scope == entry.Scope
                     && r.WholeWord == entry.WholeWord
                     && r.CaseSensitive == entry.CaseSensitive)
            .FirstOrDefaultAsync();

        if (existing != null)
        {
            existing.PhonemeText  = entry.PhonemeText;
            existing.AccentGroup  = entry.AccentGroup;
            existing.Enabled      = entry.Enabled;
            existing.Priority     = entry.Priority;
            existing.Notes        = entry.Notes;
            await _db.Connection.UpdateAsync(existing);
        }
        else
        {
            await _db.Connection.InsertAsync(entry.ToRow());
        }
    }

    public async Task DeleteRuleAsync(PronunciationRuleEntry entry)
    {
        var existing = await _db.Connection.Table<PronunciationRuleRow>()
            .Where(r => r.MatchText == entry.MatchText
                     && r.Scope == entry.Scope
                     && r.WholeWord == entry.WholeWord
                     && r.CaseSensitive == entry.CaseSensitive)
            .FirstOrDefaultAsync();

        if (existing != null)
            await _db.Connection.DeleteAsync(existing);
    }

    public Task ClearAllAsync()
        => _db.ClearTableAsync(RvrTable.PronunciationRules);
}

// ── Legacy domain models (kept for JSON migration deserialization) ─────────────

public sealed class PronunciationRuleFile
{
    public int Version { get; set; } = 1;
    public List<PronunciationRuleEntry> Rules { get; set; } = new();
}

public sealed class PronunciationRuleEntry
{
    public string MatchText { get; set; } = string.Empty;
    public string PhonemeText { get; set; } = string.Empty;
    public string Scope { get; set; } = "Global";
    public string? AccentGroup { get; set; }
    public bool WholeWord { get; set; } = true;
    public bool CaseSensitive { get; set; }
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public string Notes { get; set; } = string.Empty;

    public PronunciationRule ToRule()
    {
        AccentGroup? group = null;
        if (string.Equals(Scope, "AccentGroup", StringComparison.OrdinalIgnoreCase)
            && Enum.TryParse<AccentGroup>(AccentGroup, out var parsed))
        {
            group = parsed;
        }

        return new PronunciationRule(
            MatchText:     MatchText,
            PhonemeText:   PhonemeText,
            Group:         group,
            WholeWord:     WholeWord,
            CaseSensitive: CaseSensitive,
            Priority:      Priority);
    }

    public static PronunciationRuleEntry FromRule(PronunciationRule rule, string notes = "")
        => new()
        {
            MatchText     = rule.MatchText,
            PhonemeText   = rule.PhonemeText,
            Scope         = rule.Group.HasValue ? "AccentGroup" : "Global",
            AccentGroup   = rule.Group?.ToString(),
            WholeWord     = rule.WholeWord,
            CaseSensitive = rule.CaseSensitive,
            Enabled       = true,
            Priority      = rule.Priority,
            Notes         = notes,
        };
}