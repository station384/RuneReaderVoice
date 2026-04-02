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
using System.Text;
using System.Text.RegularExpressions;

namespace RuneReaderVoice.TTS.Providers;

public static class TextSplitter
{
    public const int MinFragmentWords = 3;

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "rev", "gen", "sgt",
        "cpl", "pvt", "capt", "lt", "col", "maj", "brig", "adm",
        "st", "ave", "blvd", "rd", "vs", "etc", "approx", "dept",
        "est", "govt", "inc", "corp", "ltd", "vol", "no", "fig",
    };

    public static List<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string> { text };

        return TextChunkingPolicy.BuildChunks(text, "generic", null, true).Select(c => c.Text).ToList();
    }

    public static string NormalizeLineEndings(string text)
        => string.IsNullOrEmpty(text) ? text : text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static List<string> SplitParagraphs(string text)
    {
        var normalized = NormalizeLineEndings(text);
        return Regex.Split(normalized, @"\n\s*\n+")
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    public static List<string> SplitSingleLines(string text)
    {
        var normalized = NormalizeLineEndings(text);
        return normalized.Split('\n', StringSplitOptions.TrimEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    public static List<string> SplitSentences(string text)
    {
        var input = text.Trim();
        if (input.Length == 0) return new List<string>();

        var parts = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            current.Append(c);

            if (c is '.' or '!' or '?' || c == '…')
            {
                var token = GetTokenBeforeBoundary(current.ToString());
                if (c == '.' && IsAbbreviation(token))
                    continue;

                int j = i + 1;
                while (j < input.Length && char.IsWhiteSpace(input[j]))
                    j++;
                while (j < input.Length && (input[j] == '"' || input[j] == '\'' || input[j] == ')' || input[j] == ']'))
                {
                    current.Append(input[j]);
                    j++;
                }

                var piece = current.ToString().Trim();
                if (piece.Length > 0) parts.Add(piece);
                current.Clear();
                i = j - 1;
            }
        }

        var tail = current.ToString().Trim();
        if (tail.Length > 0) parts.Add(tail);
        return parts.Count > 0 ? parts : new List<string> { input };
    }

    public static List<string> SplitClauses(string text)
    {
        return SplitOnPunctuation(text, new[] { ';', ':' });
    }

    public static List<string> SplitCommas(string text)
    {
        return SplitOnPunctuation(text, new[] { ',' });
    }

    public static List<string> SplitByLength(string text, int targetChars, int hardCapChars)
    {
        var result = new List<string>();
        var remaining = text.Trim();
        while (remaining.Length > hardCapChars)
        {
            int cut = FindWhitespaceCut(remaining, targetChars, hardCapChars);
            result.Add(remaining[..cut].Trim());
            remaining = remaining[cut..].Trim();
        }
        if (remaining.Length > 0)
            result.Add(remaining);
        return result;
    }

    public static int WordCount(string s)
    {
        bool inWord = false;
        int count = 0;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) inWord = false;
            else if (!inWord) { inWord = true; count++; }
        }
        return count;
    }

    private static List<string> SplitOnPunctuation(string text, char[] separators)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        foreach (char c in text)
        {
            current.Append(c);
            if (separators.Contains(c))
            {
                var piece = current.ToString().Trim();
                if (piece.Length > 0) parts.Add(piece);
                current.Clear();
            }
        }
        var tail = current.ToString().Trim();
        if (tail.Length > 0) parts.Add(tail);
        return parts.Count > 1 ? parts : new List<string> { text.Trim() };
    }

    private static string GetTokenBeforeBoundary(string text)
    {
        var token = Regex.Match(text, @"([A-Za-z\.]+)[\.!\?…]+\s*$");
        return token.Success ? token.Groups[1].Value.Trim('.').ToLowerInvariant() : string.Empty;
    }

    private static bool IsAbbreviation(string token)
        => !string.IsNullOrWhiteSpace(token) && Abbreviations.Contains(token);

    private static int FindWhitespaceCut(string text, int targetChars, int hardCapChars)
    {
        int start = Math.Min(targetChars, text.Length - 1);
        for (int i = start; i < Math.Min(hardCapChars, text.Length); i++)
            if (char.IsWhiteSpace(text[i])) return i;
        for (int i = start; i >= Math.Max(1, targetChars / 2); i--)
            if (char.IsWhiteSpace(text[i])) return i;
        return Math.Min(hardCapChars, text.Length);
    }
}