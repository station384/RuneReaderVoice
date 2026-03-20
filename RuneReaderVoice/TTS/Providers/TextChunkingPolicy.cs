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
    public bool IsTurbo { get; init; }
    public int PivotMergeWordLimit { get; init; }
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
        foreach (var paragraph in TextSplitter.SplitParagraphs(normalized))
        {
            AppendBlock(paragraph, "paragraph", chunkProfile, results);
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

        foreach (var lineBlock in TextSplitter.SplitSingleLines(trimmed))
        {
            AppendSized(lineBlock.Trim(), boundaryBefore, profile, dest);
            boundaryBefore = "line";
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

        // Chatterbox limits derived from Provider_Tests.md:
        //
        // Chatterbox Full (exaggeration=0.5):
        //   Plain prose:     ~450 chars safe, 600+ chars risky
        //   Repeated frames: fails at 3-4 sentences regardless of length
        //   Comma lists:     fails at ~10 items
        //   Number words:    fails at 20-item sequences
        //
        // Chatterbox Turbo:
        //   Plain prose:     ~650 chars safe, 870+ chars fails
        //   Repeated frames: fails at 3 sentences
        //   Comma lists:     fails at ~10 items
        //
        // Key insight: TargetChars controls where we split prose.
        // ListItemLimit and RepeatedSentenceLimit control pattern-based splits
        // that trigger BEFORE the char limit is reached.

        var result = new ChunkProfile
        {
            // Chatterbox Full: tightened from 550/650 to 380/480
            // Turbo is more robust — keep at 600/720 (tightened from 700/850)
            // F5 and Kokoro unchanged
            TargetChars           = kokoro    ? 850
                                  : f5        ? 575
                                  : turbo     ? 600
                                  : chatterbox ? 380
                                  : 700,

            HardCapChars          = kokoro    ? 1050
                                  : f5        ? 725
                                  : turbo     ? 720
                                  : chatterbox ? 480
                                  : 850,

            // Chatterbox Full fails at ~10 comma-separated items; cap at 7 (was 7, keep)
            // Turbo fails at ~10 items; cap at 8 (was 9)
            ListItemLimit         = kokoro    ? 12
                                  : f5        ? 10
                                  : turbo     ?  8
                                  : chatterbox ?  6
                                  : 10,

            // Chatterbox fails on repeated sentence frames at 3+; cap at 2 (was 3)
            RepeatedSentenceLimit = kokoro    ? 5
                                  : f5        ? 4
                                  : turbo     ? 3
                                  : chatterbox ? 2
                                  : 4,

            IsChatterboxFamily    = chatterbox,
            IsTurbo               = turbo,
            PivotMergeWordLimit   = turbo ? 11 : (chatterbox ? 10 : 0),
        };

        // Further tighten for high exaggeration — test showed exaggeration >= 1.0
        // fails at roughly 60-70% of the normal safe length
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
                IsTurbo               = result.IsTurbo,
                PivotMergeWordLimit   = result.PivotMergeWordLimit,
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
