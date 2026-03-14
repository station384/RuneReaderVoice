// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

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
