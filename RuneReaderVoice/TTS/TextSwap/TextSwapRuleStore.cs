// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RuneReaderVoice.TTS.TextSwap;

public sealed class TextSwapRuleFile
{
    public int Version { get; set; } = 1;
    public List<TextSwapRuleEntry> Rules { get; set; } = new();
}

public sealed class TextSwapRuleEntry
{
    public string FindText { get; set; } = string.Empty;
    public string ReplaceText { get; set; } = string.Empty;
    public bool ReplaceWithCrLf { get; set; } = false;
    public bool WholeWord { get; set; } = false;
    public bool CaseSensitive { get; set; } = false;
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public string Notes { get; set; } = string.Empty;

    public TextSwapRule ToRule()
        => new(
            FindText: FindText,
            ReplaceText: ReplaceWithCrLf ? "\r\n" : ReplaceText,
            WholeWord: WholeWord,
            CaseSensitive: CaseSensitive,
            Priority: Priority);

    public static TextSwapRuleEntry FromRule(TextSwapRule rule, string notes = "")
        => new()
        {
            FindText = rule.FindText,
            ReplaceText = rule.ReplaceText == "\r\n" ? string.Empty : rule.ReplaceText,
            ReplaceWithCrLf = rule.ReplaceText == "\r\n",
            WholeWord = rule.WholeWord,
            CaseSensitive = rule.CaseSensitive,
            Enabled = true,
            Priority = rule.Priority,
            Notes = notes,
        };
}

public static class TextSwapRuleStore
{
    private const string FileName = "text-swap-rules.json";
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    public static string GetRulesFilePath()
        => Path.Combine(VoiceSettingsManager.GetConfigDirectory(), FileName);

    public static TextSwapRuleFile LoadRuleFile()
    {
        try
        {
            var path = GetRulesFilePath();
            if (!File.Exists(path))
                return new TextSwapRuleFile();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TextSwapRuleFile>(json) ?? new TextSwapRuleFile();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TextSwapRuleStore] Load error: {ex.Message}");
            return new TextSwapRuleFile();
        }
    }

    public static IReadOnlyList<TextSwapRule> LoadUserRules()
        => LoadRuleFile().Rules
            .Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.FindText))
            .Select(r => r.ToRule())
            .ToList();

    public static void SaveRuleFile(TextSwapRuleFile file)
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
            Debug.WriteLine($"[TextSwapRuleStore] Save error: {ex.Message}");
            throw;
        }
    }

    public static TextSwapRuleFile UpsertRule(TextSwapRuleEntry entry)
    {
        var file = LoadRuleFile();
        var existing = file.Rules.FirstOrDefault(r =>
            string.Equals(r.FindText, entry.FindText, StringComparison.OrdinalIgnoreCase) &&
            r.WholeWord == entry.WholeWord &&
            r.CaseSensitive == entry.CaseSensitive);

        if (existing != null)
        {
            existing.ReplaceText = entry.ReplaceText;
            existing.ReplaceWithCrLf = entry.ReplaceWithCrLf;
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

    public static TextSwapRuleFile DeleteRule(TextSwapRuleEntry entry)
    {
        var file = LoadRuleFile();
        file.Rules.RemoveAll(r =>
            string.Equals(r.FindText, entry.FindText, StringComparison.OrdinalIgnoreCase) &&
            r.WholeWord == entry.WholeWord &&
            r.CaseSensitive == entry.CaseSensitive);
        SaveRuleFile(file);
        return file;
    }
}