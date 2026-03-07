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
//   Sentence endings (.  !  ?  …): punctuation stays with LEFT chunk.
//   Clause breaks (, ; :):         punctuation moves to START of RIGHT chunk,
//                                  so Kokoro sees ", next clause" and renders
//                                  the natural brief pause before continuing.
//
// Example:
//   "Follow the road north, past the mill, until you reach the river."
//   → ["Follow the road north", ", past the mill", ", until you reach the river."]
//
// Short fragments (< MinFragmentWords words) are merged into the next chunk
// rather than sent to the model alone, avoiding clipped micro-clips.
//
// Abbreviations (Mr. Mrs. Dr. etc.) are protected from false sentence splits.
// Decimal numbers (1.5, 3.14) are also protected.

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

    // Matches a decimal number like 1.5 or 3.14 — period is NOT a sentence break.
    private static readonly Regex DecimalPattern = new(@"\d\.\d", RegexOptions.Compiled);

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

            // ── Ellipsis (…  or  ...) ─────────────────────────────────────
            if (c == '…')
            {
                current.Append(c);
                FlushIfNonEmpty(current, segments);
                i++;
                continue;
            }
            if (c == '.' && i + 2 < text.Length && text[i+1] == '.' && text[i+2] == '.')
            {
                current.Append("...");
                FlushIfNonEmpty(current, segments);
                i += 3;
                continue;
            }

            // ── Period ────────────────────────────────────────────────────
            if (c == '.')
            {
                // Protect decimals: digit.digit
                if (i > 0 && i + 1 < text.Length &&
                    char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
                {
                    current.Append(c);
                    i++;
                    continue;
                }

                // Protect abbreviations: grab word to the left of the dot
                var leftWord = GetWordLeftOf(text, i);
                if (Abbreviations.Contains(leftWord))
                {
                    current.Append(c);
                    i++;
                    continue;
                }

                // Real sentence end
                current.Append(c);
                FlushIfNonEmpty(current, segments);
                i++;
                continue;
            }

            // ── Sentence-final punctuation (! ?) ─────────────────────────
            if (c == '!' || c == '?')
            {
                current.Append(c);
                // Absorb any trailing ?! combinations (e.g. "What?!")
                while (i + 1 < text.Length && (text[i+1] == '!' || text[i+1] == '?'))
                {
                    i++;
                    current.Append(text[i]);
                }
                FlushIfNonEmpty(current, segments);
                i++;
                continue;
            }

            // ── Clause breaks (, ; :) — punctuation goes to NEXT segment ─
            // if (c == ',' || c == ';' || c == ':')
            // {
            //     FlushIfNonEmpty(current, segments);
            //     // Punctuation moves to the START of the next segment so Kokoro
            //     // hears ", next clause" and renders the natural leading pause.
            //     // We add a space after it so the model sees ", word" not ",word".
            //     current.Append(c);
            //     current.Append(' ');
            //     i++;
            //     // Skip the original whitespace that followed the punctuation
            //     while (i < text.Length && text[i] == ' ')
            //         i++;
            //     continue;
            // }

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
        if (segments.Count <= 1) return segments;

        bool changed = true;
        while (changed && segments.Count > 1)
        {
            changed = false;
            for (int i = 0; i < segments.Count; i++)
            {
                if (WordCount(segments[i]) < MinFragmentWords)
                {
                    if (i + 1 < segments.Count)
                    {
                        // If the right segment starts with clause punctuation it
                        // already carries its own leading ", " — attach directly
                        // so we get "Greetings, traveler!" not "Greetings , traveler!"
                        var right = segments[i + 1];
                        segments[i] = right.Length > 0 && right[0] is ',' or ';' or ':'
                            ? segments[i].TrimEnd() + right
                            : segments[i].TrimEnd() + " " + right.TrimStart();
                        segments.RemoveAt(i + 1);
                    }
                    else
                    {
                        // Last segment — merge backward
                        segments[i - 1] = segments[i - 1].TrimEnd() + " " + segments[i].TrimStart();
                        segments.RemoveAt(i);
                    }
                    changed = true;
                    break;
                }
            }
        }

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

    /// <summary>Returns the word immediately to the left of position <paramref name="dotIndex"/>.</summary>
    private static string GetWordLeftOf(string text, int dotIndex)
    {
        int end = dotIndex - 1;
        while (end >= 0 && char.IsWhiteSpace(text[end])) end--;
        int start = end;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        return end >= start ? text[start..(end + 1)] : string.Empty;
    }
}