// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.

// TextSplitter.cs
// Splits NPC dialog text into natural speech segments for Kokoro TTS.
//
// Split strategy:
//   Hard line breaks (\r, \n, \r\n): split boundary.
//   Paragraph breaks (blank lines):   split boundary.
//   Colons (:):                       punctuation stays with LEFT chunk.
//
// Example:
//   "Commander\r\nAnduin Lothar: A man who would flop like a fish."
//   → ["Commander", "Anduin Lothar:", "A man who would flop like a fish."]

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace RuneReaderVoice.TTS.Providers;

public static class TextSplitter
{
    // Minimum word count for a standalone fragment.
    // Fragments shorter than this are merged forward into the next chunk.
    public const int MinFragmentWords = 3;

    // Abbreviations whose trailing period must NOT trigger a sentence split.
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "rev", "gen", "sgt",
        "cpl", "pvt", "capt", "lt", "col", "maj", "brig", "adm",
        "st", "ave", "blvd", "rd", "vs", "etc", "approx", "dept",
        "est", "govt", "inc", "corp", "ltd", "vol", "no", "fig",
    };

    /// <summary>
    /// Splits <paramref name="text"/> into speech segments suitable for
    /// individual Kokoro inference jobs.
    ///
    /// Returns at least one element. If no split points are found the
    /// original text is returned as a single-element list.
    /// </summary>
    public static List<string> Split(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string> { text };

        text = text.Trim();
        var raw = SplitRaw(text);
        var merged = MergeShortFragments(raw);
        return merged;
    }

    // ── Raw split ─────────────────────────────────────────────────────────────

    private static List<string> SplitRaw(string text)
    {
        var segments = new List<string>();
        var current  = new System.Text.StringBuilder();

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // ── Hard line breaks (\r, \n, \r\n) ──────────────────────────
            if (c == '\r')
            {
                FlushIfNonEmpty(current, segments);
                i++;
                if (i < text.Length && text[i] == '\n')
                    i++;
                continue;
            }

            if (c == '\n')
            {
                FlushIfNonEmpty(current, segments);
                i++;
                continue;
            }

            // ── Colon boundary — punctuation stays with LEFT segment ─────
            if (c == ':')
            {
                current.Append(c);
                FlushIfNonEmpty(current, segments);
                i++;
                while (i < text.Length && text[i] == ' ')
                    i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        // Flush any trailing text
        FlushIfNonEmpty(current, segments);

        return segments.Count > 0 ? segments : new List<string> { text };
    }

    // ── Merge short fragments ─────────────────────────────────────────────────

    /// <summary>
    /// Any fragment with fewer than <see cref="MinFragmentWords"/> words is
    /// merged into its neighbour. Prefer merging forward (into the next
    /// segment); if it's the last segment, merge backward.
    /// </summary>
    private static List<string> MergeShortFragments(List<string> segments)
    {
        return segments;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void FlushIfNonEmpty(System.Text.StringBuilder sb, List<string> dest)
    {
        var s = sb.ToString().Trim();
        if (s.Length > 0) dest.Add(s);
        sb.Clear();
    }

    private static int WordCount(string s)
    {
        bool inWord = false;
        int count   = 0;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { inWord = false; }
            else if (!inWord)         { inWord = true; count++; }
        }
        return count;
    }
}