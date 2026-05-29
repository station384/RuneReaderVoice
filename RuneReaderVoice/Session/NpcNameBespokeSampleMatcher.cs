// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.Session;

internal static class NpcNameBespokeSampleMatcher
{
    public static bool TryResolve(
        string? npcName,
        string? configuredSampleId,
        out string? matchedSampleId,
        out string? missingConfiguredSampleId)
    {
        matchedSampleId = configuredSampleId;
        missingConfiguredSampleId = null;

        if (string.IsNullOrWhiteSpace(npcName))
            return false;

        var provider = AppServices.Provider;
        if (provider is not RemoteTtsProvider remoteProvider || !remoteProvider.UsesRemoteSamples)
            return false;

        var voices = provider.GetAvailableVoices();
        if (voices.Count == 0)
            return false;

        // Explicit assignment wins when available. Name matching only fills missing/broken assignments.
        if (!string.IsNullOrWhiteSpace(configuredSampleId))
        {
            if (voices.Any(v => string.Equals(v.VoiceId, configuredSampleId, StringComparison.OrdinalIgnoreCase)))
                return false;

            missingConfiguredSampleId = configuredSampleId;
        }

        var npcTokens = NormalizeNameTokens(npcName);
        if (npcTokens.Count == 0)
            return false;

        var requiredMatches = npcTokens.Count <= 2 ? npcTokens.Count : 2;
        var npcCompact = NormalizeCompactName(npcName);

        var best = voices
            // Name fallback must only consider bespoke/name-style samples.
            // Generic race/sex samples like M_Tauren or F_Tauren are too broad:
            // "Rivermane Tauren" should not auto-pick F_Tauren.
            .Where(v => !IsGenericGenderRaceSampleId(v.VoiceId))
            .Select(v =>
            {
                var sampleTokens = NormalizeNameTokens(v.VoiceId);
                var sampleCompact = CompactFromTokens(sampleTokens);
                var matchedTokens = npcTokens
                    .Where(t => sampleTokens.Any(st => string.Equals(st, t, StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
                var compactMatch = IsCompactNpcNameMatch(npcCompact, sampleCompact, npcTokens.Count);

                return new
                {
                    Voice = v,
                    Tokens = sampleTokens,
                    MatchedTokens = matchedTokens,
                    CompactMatch = compactMatch,
                    Score = ScoreNpcSampleMatch(npcTokens, sampleTokens, matchedTokens, compactMatch, v.VoiceId)
                };
            })
            .Where(x => IsStrongEnoughMatch(x.Tokens, x.MatchedTokens, x.CompactMatch, requiredMatches))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Voice.VoiceId.Length)
            .ThenBy(x => x.Voice.VoiceId, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        if (best == null)
            return false;

        matchedSampleId = best.Voice.VoiceId;
        return true;
    }

    private static bool IsGenericGenderRaceSampleId(string? sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId))
            return false;

        return sampleId.StartsWith("M_", StringComparison.OrdinalIgnoreCase) ||
               sampleId.StartsWith("F_", StringComparison.OrdinalIgnoreCase);
    }


    private static bool IsStrongEnoughMatch(
        IReadOnlyList<string> sampleTokens,
        IReadOnlyList<string> matchedTokens,
        bool compactMatch,
        int requiredMatches)
    {
        // Avoid weak single-word matches such as:
        //   NPC: "Magistrix Landra Dawnstrider"
        //   Sample: "U_Dawn_1"
        // Multi-token NPCs must match at least two meaningful name tokens,
        // or a compact missing-space match backed by at least two sample tokens.
        if (matchedTokens.Count >= requiredMatches)
            return true;

        return compactMatch && sampleTokens.Count >= requiredMatches;
    }

    private static int ScoreNpcSampleMatch(
        IReadOnlyList<string> npcTokens,
        IReadOnlyList<string> sampleTokens,
        IReadOnlyList<string> matchedTokens,
        bool compactMatch,
        string sampleId)
    {
        var score = matchedTokens.Count * 1000;

        if (compactMatch)
            score += 750;

        if (sampleId.StartsWith("U_", StringComparison.OrdinalIgnoreCase))
            score += 100;

        var extraTokenCount = sampleTokens.Count(t =>
            t.Length > 1 &&
            !npcTokens.Any(n => string.Equals(n, t, StringComparison.OrdinalIgnoreCase)) &&
            !int.TryParse(t, out _));
        score -= extraTokenCount * 10;

        return score;
    }

    private static bool IsCompactNpcNameMatch(string npcCompact, string sampleCompact, int npcTokenCount)
    {
        if (string.IsNullOrWhiteSpace(npcCompact) || string.IsNullOrWhiteSpace(sampleCompact))
            return false;

        // Compact matching is for missing spaces in decoded names, e.g.
        // "FirstArcanistThalyssra" or "First ArcanistThalyssra".
        // Keep threshold high enough so a single short token like "Blockhead"
        // does not match "TheronBlockhead".
        var minLength = npcTokenCount <= 1 ? 12 : 8;
        if (npcCompact.Length < minLength)
            return false;

        return sampleCompact.Contains(npcCompact, StringComparison.OrdinalIgnoreCase) ||
               npcCompact.Contains(sampleCompact, StringComparison.OrdinalIgnoreCase);
    }

    private static string CompactFromTokens(IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0)
            return string.Empty;

        return string.Concat(tokens.Where(t =>
            t.Length > 1 &&
            !int.TryParse(t, out _)));
    }

    private static string NormalizeCompactName(string value)
        => CompactFromTokens(NormalizeNameTokens(value));

    private static IReadOnlyList<string> NormalizeNameTokens(string value)
    {
        var normalized = NormalizeSampleName(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1 && !int.TryParse(t, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeSampleName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;

            if (ch == '\'' || ch == '’' || ch == '`')
                continue;

            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
            else
                sb.Append(' ');
        }

        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
