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

// TextChunkingPolicy.cs
// Provider-aware chunking limits and heuristics for remote and local synthesis.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RuneReaderVoice.TTS.Providers;

public sealed class TextChunk
{
    public required string Text { get; init; }
    public string BoundaryBefore { get; init; } = string.Empty;
    public string BoundaryAfter { get; init; } = string.Empty;
}

internal sealed class ChunkProfile
{
    public int TargetChars { get; init; }
    public int HardCapChars { get; init; }
    public int ListItemLimit { get; init; }
    public int RepeatedSentenceLimit { get; init; }
    public bool IsChatterboxFamily { get; init; }
    public bool IsCosyVoiceFamily { get; init; }
    public bool IsLongcatFamily { get; init; }
    public bool IsTurbo { get; init; }
    public int PivotMergeWordLimit { get; init; }
    public bool SplitOnParagraphs { get; init; } = true;
    public bool SplitOnSingleLines { get; init; } = true;
}

public static class TextChunkingPolicy
{
    private static readonly string[] RumorFrames =
    {
        "some say", "others say", "they say", "folk say", "people say", "old folk say"
    };

    private static readonly string[] PivotStarters =
    {
        "me?", "well", "listen", "truth be told", "that said", "mind you", "so", "and so", "look"
    };

    public static IReadOnlyList<TextChunk> BuildChunks(string text, ITtsProvider provider, VoiceProfile? profile, VoiceUserSettings settings)
        => BuildChunks(text, provider.ProviderId, profile, settings.EnablePhraseChunking && !(profile?.DisableChunking ?? false));

    public static IReadOnlyList<TextChunk> BuildChunks(string text, string providerId, VoiceProfile? profile, bool enabled)
    {
        if (!enabled || string.IsNullOrWhiteSpace(text))
            return new[] { new TextChunk { Text = text } };

        var chunkProfile = ResolveProfile(providerId, profile);
        var normalized = TextSplitter.NormalizeLineEndings(text).Trim();
        if (normalized.Length == 0)
            return new[] { new TextChunk { Text = text } };

        var results = new List<TextChunk>();
        var paragraphBlocks = chunkProfile.SplitOnParagraphs
            ? TextSplitter.SplitParagraphs(normalized)
            : new List<string> { normalized };

        foreach (var paragraph in paragraphBlocks)
        {
            AppendBlock(paragraph, chunkProfile.SplitOnParagraphs ? "paragraph" : string.Empty, chunkProfile, results);
        }

        if (results.Count == 0)
            results.Add(new TextChunk { Text = normalized });

        return MergeTinyChunks(results, chunkProfile);
    }

    public static List<string> GetChunkTexts(string text, ITtsProvider provider, VoiceProfile? profile, VoiceUserSettings settings)
        => BuildChunks(text, provider, profile, settings).Select(c => c.Text).ToList();

    public static List<string> GetChunkTexts(string text, string providerId, VoiceProfile? profile, bool enabled)
        => BuildChunks(text, providerId, profile, enabled).Select(c => c.Text).ToList();

    private static void AppendBlock(string block, string boundaryBefore, ChunkProfile profile, List<TextChunk> dest)
    {
        var trimmed = block.Trim();
        if (trimmed.Length == 0) return;

        var lineBlocks = profile.SplitOnSingleLines
            ? TextSplitter.SplitSingleLines(trimmed)
            : new List<string> { trimmed };

        foreach (var lineBlock in lineBlocks)
        {
            AppendSized(lineBlock.Trim(), boundaryBefore, profile, dest);
            boundaryBefore = profile.SplitOnSingleLines ? "line" : boundaryBefore;
        }
    }

    private static void AppendSized(string text, string boundaryBefore, ChunkProfile profile, List<TextChunk> dest)
    {
        if (text.Length <= profile.HardCapChars && !ShouldAggressivelySplit(text, profile))
        {
            dest.Add(new TextChunk { Text = text, BoundaryBefore = boundaryBefore });
            return;
        }

        var sentences = TextSplitter.SplitSentences(text);
        if (sentences.Count > 1)
        {
            var grouped = GroupBySize(sentences, profile, boundaryBefore, "sentence");
            dest.AddRange(grouped);
            return;
        }

        var clauses = TextSplitter.SplitClauses(text);
        if (clauses.Count > 1)
        {
            var grouped = GroupBySize(clauses, profile, boundaryBefore, "clause");
            dest.AddRange(grouped);
            return;
        }

        var commaParts = TextSplitter.SplitCommas(text);
        if (commaParts.Count > 1)
        {
            var grouped = GroupBySize(commaParts, profile, boundaryBefore, "comma");
            dest.AddRange(grouped);
            return;
        }

        foreach (var piece in TextSplitter.SplitByLength(text, profile.TargetChars, profile.HardCapChars))
        {
            dest.Add(new TextChunk { Text = piece, BoundaryBefore = boundaryBefore, BoundaryAfter = "forced" });
            boundaryBefore = "forced";
        }
    }

    private static bool ShouldAggressivelySplit(string text, ChunkProfile profile)
    {
        if (text.Length > profile.TargetChars)
            return true;

        int itemCount = CountLikelyListItems(text);
        if (itemCount >= profile.ListItemLimit)
            return true;

        if (LooksLikeRepeatedFrames(text, profile.RepeatedSentenceLimit))
            return true;

        if (LooksNumberHeavy(text) && itemCount >= Math.Max(6, profile.ListItemLimit - 2))
            return true;

        if (profile.IsChatterboxFamily && ContainsRumorOrPivotCluster(text))
            return true;

        // CosyVoice-specific: hyphenated compound number words (twenty-one, thirty-two etc.)
        // cause LLM attention collapse even at short char counts. The A2/K benchmark failures
        // (247 and 228 chars) happen well below the char limit — detect and split early.
        if (profile.IsCosyVoiceFamily && LooksHyphenatedNumberHeavy(text))
            return true;

        return false;
    }

    private static IEnumerable<TextChunk> GroupBySize(List<string> units, ChunkProfile profile, string firstBoundaryBefore, string boundaryAfter)
    {
        var output = new List<TextChunk>();
        var current = new List<string>();
        int itemCount = 0;
        string boundaryBefore = firstBoundaryBefore;

        foreach (var unit in units.Select(u => u.Trim()).Where(u => u.Length > 0))
        {
            bool forceBreakBefore = current.Count > 0 && profile.IsChatterboxFamily && ShouldBreakBeforeUnit(current, unit, profile);
            int proposedItems = itemCount + CountLikelyListItems(unit);
            string candidate = current.Count == 0 ? unit : string.Join(" ", current.Concat(new[] { unit }));
            bool repeatedTooLarge = current.Count >= profile.RepeatedSentenceLimit && LooksLikeRepeatedFrames(candidate, profile.RepeatedSentenceLimit);

            if (current.Count > 0 &&
                (forceBreakBefore || candidate.Length > profile.TargetChars || proposedItems >= profile.ListItemLimit || repeatedTooLarge))
            {
                output.Add(new TextChunk { Text = string.Join(" ", current), BoundaryBefore = boundaryBefore, BoundaryAfter = boundaryAfter });
                current.Clear();
                itemCount = 0;
                boundaryBefore = forceBreakBefore ? "rhetoric" : boundaryAfter;
            }

            current.Add(unit);
            itemCount += CountLikelyListItems(unit);
        }

        if (current.Count > 0)
            output.Add(new TextChunk { Text = string.Join(" ", current), BoundaryBefore = boundaryBefore, BoundaryAfter = boundaryAfter });

        return MergeTinyChunks(output, profile);
    }

    private static List<TextChunk> MergeTinyChunks(List<TextChunk> chunks, ChunkProfile profile)
    {
        if (chunks.Count <= 1) return chunks;

        var merged = new List<TextChunk>();
        int i = 0;
        while (i < chunks.Count)
        {
            var current = chunks[i];
            bool mergePivotBackward = profile.IsChatterboxFamily && StartsWithPivot(current.Text) &&
                                      TextSplitter.WordCount(current.Text) <= profile.PivotMergeWordLimit &&
                                      merged.Count > 0 && CanMergeWithoutOverrun(merged[^1].Text, current.Text, profile);

            if (mergePivotBackward)
            {
                var prev = merged[^1];
                merged[^1] = new TextChunk
                {
                    Text = prev.Text + " " + current.Text,
                    BoundaryBefore = prev.BoundaryBefore,
                    BoundaryAfter = current.BoundaryAfter,
                };
            }
            else if (TextSplitter.WordCount(current.Text) < TextSplitter.MinFragmentWords)
            {
                if (i + 1 < chunks.Count && (!profile.IsChatterboxFamily || !StartsWithRumorFrame(chunks[i + 1].Text)))
                {
                    var next = chunks[i + 1];
                    chunks[i + 1] = new TextChunk
                    {
                        Text = current.Text + " " + next.Text,
                        BoundaryBefore = current.BoundaryBefore,
                        BoundaryAfter = next.BoundaryAfter,
                    };
                }
                else if (merged.Count > 0)
                {
                    var prev = merged[^1];
                    merged[^1] = new TextChunk
                    {
                        Text = prev.Text + " " + current.Text,
                        BoundaryBefore = prev.BoundaryBefore,
                        BoundaryAfter = current.BoundaryAfter,
                    };
                }
                else
                {
                    merged.Add(current);
                }
            }
            else
            {
                merged.Add(current);
            }
            i++;
        }

        return merged;
    }

    private static ChunkProfile ResolveProfile(string providerId, VoiceProfile? profile)
    {
        var id = providerId ?? string.Empty;
        bool chatterbox = id.Contains("chatterbox", StringComparison.OrdinalIgnoreCase);
        bool turbo      = id.Contains("turbo", StringComparison.OrdinalIgnoreCase);
        bool f5         = id.Contains("f5", StringComparison.OrdinalIgnoreCase);
        bool kokoro     = id.Contains("kokoro", StringComparison.OrdinalIgnoreCase);
        bool cosyvoice  = id.Contains("cosyvoice", StringComparison.OrdinalIgnoreCase);
        bool longcat    = id.Contains("longcat", StringComparison.OrdinalIgnoreCase);

        // Limits derived from Provider_Tests.md (2026-04-06 benchmark run):
        //
        // Chatterbox Full (exaggeration=0.5, M_Narrator):
        //   Plain prose:     passes through ~539 chars (L002), fails at 726 chars (L003, hits 40s cap)
        //   Practical safe ceiling: ~550 chars plain prose
        //   Number lists:    fails at 228–247 chars (J, K — ratio 2.0–2.1× = hallucination, not truncation)
        //   Comma lists:     fails at 40 items in paragraph grouping (C1, 399 chars)
        //   Repeated frames: fails at 3-4 sentences regardless of length
        //
        // Chatterbox Turbo (M_Narrator):
        //   Plain prose:     ~650 chars safe, 870+ chars fails
        //   Repeated frames: fails at 3 sentences
        //   Comma lists:     fails at ~10 items
        //
        // CosyVoice-vLLM (M_Narrator):
        //   Plain prose:     passes at 302 chars (A), 436 chars (B), 399 chars (Stage4); FAILS at 409 chars (L001)
        //   Failure mode:    sentence SKIPPING (attention collapse on repeated tokens), not truncation
        //   Number lists:    fails at 196 chars (A1, 1-20), 247 chars (A2, 21-40) — hyphenated words trigger early
        //   Repeated text:   collapses around 300-400 chars; the L-series (same paragraph repeated) fails at L001
        //   Pacing:          "progressively slowed", "rushed", "stilted" even on passes — model is marginal at 350+
        //   Key insight:     failure is semantic repetition in the LLM stage, not raw sequence length.
        //                    Varied prose is more forgiving than repeated-structure text.
        //
        // Key insight: TargetChars controls where we split prose.
        // ListItemLimit and RepeatedSentenceLimit control pattern-based splits
        // that trigger BEFORE the char limit is reached.

        var result = new ChunkProfile
        {
            // Current guidance (2026-04 client update prompt):
            //   Kokoro:            850 / 1050
            //   F5-TTS:           575 / 725
            //   Chatterbox Turbo: 600 / 720
            //   Chatterbox Full:  8000 / 8000
            //   CosyVoice:        380 / 480
            //   LongCat: use conservative client-side chunking around ~20 words (~100 chars)
            //            with a harder cap around ~30 words. The backend can continue
            //            across chunks transparently, but the client should still avoid
            //            sending oversized blocks as a single request.
            TargetChars           = longcat    ? 100
                                  : kokoro     ? 850
                                  : f5         ? 575
                                  : turbo      ? 600
                                  : chatterbox ? 380
                                  : cosyvoice  ? 380
                                  : 700,

            HardCapChars          = longcat    ? 150
                                  : kokoro     ? 1050
                                  : f5         ? 725
                                  : turbo      ? 720
                                  : chatterbox ? 800
                                  : cosyvoice  ? 480
                                  : 850,

            ListItemLimit         = longcat    ? 6
                                  : kokoro     ? 12
                                  : f5         ? 10
                                  : turbo      ?  8
                                  : chatterbox ?  6
                                  : cosyvoice  ?  6
                                  : 10,

            RepeatedSentenceLimit = longcat    ? 2
                                  : kokoro     ? 5
                                  : f5         ? 4
                                  : turbo      ? 3
                                  : chatterbox ? 2
                                  : cosyvoice  ? 2
                                  : 4,

            IsChatterboxFamily    = chatterbox,
            IsCosyVoiceFamily     = cosyvoice,
            IsLongcatFamily       = longcat,
            IsTurbo               = turbo,
            PivotMergeWordLimit   = longcat ? 0 : (turbo ? 11 : (chatterbox ? 10 : 0)),
            //SplitOnParagraphs     = !chatterbox,
            //SplitOnSingleLines    = !chatterbox,
        };

        // Further tighten for high exaggeration — test showed exaggeration >= 1.0
        // fails at roughly 60-70% of the normal safe length (Chatterbox only)
        var exaggeration = profile?.Exaggeration ?? 0f;
        if (chatterbox && exaggeration >= 1.0f)
        {
            result = new ChunkProfile
            {
                TargetChars           = (int)Math.Round(result.TargetChars   * 0.70),
                HardCapChars          = (int)Math.Round(result.HardCapChars  * 0.75),
                ListItemLimit         = Math.Max(4, result.ListItemLimit    - 2),
                RepeatedSentenceLimit = Math.Max(2, result.RepeatedSentenceLimit - 1),
                IsChatterboxFamily    = result.IsChatterboxFamily,
                IsCosyVoiceFamily     = result.IsCosyVoiceFamily,
                IsLongcatFamily       = result.IsLongcatFamily,
                IsTurbo               = result.IsTurbo,
                PivotMergeWordLimit   = result.PivotMergeWordLimit,
                SplitOnParagraphs     = result.SplitOnParagraphs,
                SplitOnSingleLines    = result.SplitOnSingleLines,
            };
        }

        return result;
    }

    private static int CountLikelyListItems(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;

        int commas = text.Count(c => c == ',');
        int listish = Math.Max(1, commas + 1);
        if (!LooksNumberHeavy(text) && commas < 3)
            listish = 0;

        int lines = TextSplitter.SplitSingleLines(text).Count;
        if (lines > 1)
            listish = Math.Max(listish, lines);

        if (StartsWithRumorFrame(text))
            listish = Math.Max(listish, 2);

        return listish;
    }

    private static bool LooksNumberHeavy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return Regex.IsMatch(text, @"\b(one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen|seventeen|eighteen|nineteen|twenty|thirty|forty|fifty|sixty|first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|monday|tuesday|wednesday|thursday|friday|saturday|sunday|january|february|march|april|may|june|july|august|september|october|november|december)\b", RegexOptions.IgnoreCase);
    }

    // Detects hyphenated compound number words: twenty-one, thirty-two, forty-five etc.
    // CosyVoice benchmark (A2: 21-40 in words, 247 chars) showed LLM attention collapse
    // on these sequences even at char counts well below the TargetChars limit.
    // Threshold: 4 or more hyphenated number compounds in the text triggers the guard.
    private static readonly Regex HyphenatedNumberPattern = new(
        @"\b(twenty|thirty|forty|fifty|sixty|seventy|eighty|ninety)-(one|two|three|four|five|six|seven|eight|nine)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool LooksHyphenatedNumberHeavy(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return HyphenatedNumberPattern.Matches(text).Count >= 4;
    }

    private static bool LooksLikeRepeatedFrames(string text, int threshold)
    {
        var sentences = TextSplitter.SplitSentences(text);
        if (sentences.Count < threshold) return false;
        var prefixes = sentences
            .Select(s => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3)).ToLowerInvariant())
            .Where(s => s.Length > 0)
            .ToList();
        return prefixes.GroupBy(p => p).Any(g => g.Count() >= threshold);
    }

    private static bool ShouldBreakBeforeUnit(List<string> currentUnits, string nextUnit, ChunkProfile profile)
    {
        string previous = currentUnits[^1];
        bool nextRumor = StartsWithRumorFrame(nextUnit);
        bool prevRumor = StartsWithRumorFrame(previous);
        bool nextPivot = StartsWithPivot(nextUnit);
        bool nextPivotShort = nextPivot && TextSplitter.WordCount(nextUnit) <= profile.PivotMergeWordLimit;

        if (nextRumor && currentUnits.Count > 0)
            return true;

        if (prevRumor && nextRumor)
            return true;

        if (prevRumor && nextPivotShort)
            return false;

        if (ContainsDashAside(previous) && (nextRumor || nextPivot))
            return true;

        if (ContainsRumorOrPivotCluster(previous) && nextRumor)
            return true;

        return false;
    }

    private static bool ContainsRumorOrPivotCluster(string text)
    {
        var sentences = TextSplitter.SplitSentences(text);
        if (sentences.Count == 0) return false;

        int rumorCount = sentences.Count(StartsWithRumorFrame);
        bool hasPivot = sentences.Any(StartsWithPivot);
        return rumorCount >= 2 || (rumorCount >= 1 && hasPivot);
    }

    private static bool StartsWithRumorFrame(string text)
    {
        var normalized = NormalizeLead(text);
        return RumorFrames.Any(frame => normalized.StartsWith(frame, StringComparison.OrdinalIgnoreCase));
    }

    private static bool StartsWithPivot(string text)
    {
        var normalized = NormalizeLead(text);
        return PivotStarters.Any(p => normalized.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeLead(string text)
    {
        return (text ?? string.Empty).TrimStart('"', '\'', '“', '”', '‘', '’', '(', '[', ' ');
    }

    private static bool ContainsDashAside(string text)
        => text.Contains('—') || text.Contains(" -- ", StringComparison.Ordinal) || text.Contains(" - ", StringComparison.Ordinal);

    private static bool CanMergeWithoutOverrun(string left, string right, ChunkProfile profile)
        => (left.Length + 1 + right.Length) <= profile.HardCapChars;
}