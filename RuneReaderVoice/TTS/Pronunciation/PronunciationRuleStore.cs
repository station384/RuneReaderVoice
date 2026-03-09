// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Pronunciation;

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
            MatchText: MatchText,
            PhonemeText: PhonemeText,
            Group: group,
            WholeWord: WholeWord,
            CaseSensitive: CaseSensitive,
            Priority: Priority);
    }

    public static PronunciationRuleEntry FromRule(PronunciationRule rule, string notes = "")
        => new()
        {
            MatchText = rule.MatchText,
            PhonemeText = rule.PhonemeText,
            Scope = rule.Group.HasValue ? "AccentGroup" : "Global",
            AccentGroup = rule.Group?.ToString(),
            WholeWord = rule.WholeWord,
            CaseSensitive = rule.CaseSensitive,
            Enabled = true,
            Priority = rule.Priority,
            Notes = notes,
        };
}

public static class PronunciationRuleStore
{
    private const string FileName = "pronunciation-rules.json";
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    public static string GetRulesFilePath()
        => Path.Combine(VoiceSettingsManager.GetConfigDirectory(), FileName);

    public static PronunciationRuleFile LoadRuleFile()
    {
        try
        {
            var path = GetRulesFilePath();
            if (!File.Exists(path))
                return new PronunciationRuleFile();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PronunciationRuleFile>(json) ?? new PronunciationRuleFile();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PronunciationRuleStore] Load error: {ex.Message}");
            return new PronunciationRuleFile();
        }
    }

    public static IReadOnlyList<PronunciationRule> LoadUserRules()
        => LoadRuleFile().Rules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.MatchText) && !string.IsNullOrWhiteSpace(r.PhonemeText))
            .Select(r => r.ToRule())
            .ToList();

    public static void SaveRuleFile(PronunciationRuleFile file)
    {
        try
        {
            var dir = VoiceSettingsManager.GetConfigDirectory();
            Directory.CreateDirectory(dir);
            var path = GetRulesFilePath();
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file, SaveOptions));
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[PronunciationRuleStore] Save error: {ex.Message}");
            throw;
        }
    }

    public static PronunciationRuleFile UpsertRule(PronunciationRuleEntry entry)
    {
        var file = LoadRuleFile();
        var existing = file.Rules.FirstOrDefault(r =>
            string.Equals(r.MatchText, entry.MatchText, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Scope, entry.Scope, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.AccentGroup ?? string.Empty, entry.AccentGroup ?? string.Empty, StringComparison.OrdinalIgnoreCase) &&
            r.WholeWord == entry.WholeWord &&
            r.CaseSensitive == entry.CaseSensitive);

        if (existing != null)
        {
            existing.PhonemeText = entry.PhonemeText;
            existing.Enabled = entry.Enabled;
            existing.Priority = entry.Priority;
            existing.Notes = entry.Notes;
        }
        else
        {
            file.Rules.Add(entry);
        }

        SaveRuleFile(file);
        return file;
    }
}
