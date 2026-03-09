// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.Session;

namespace RuneReaderVoice.TTS.Pronunciation;

/// <summary>
/// Injects Kokoro pronunciation hints into dialogue text.
/// Rules are evaluated against the ORIGINAL input text only.
/// Generated markup is never reprocessed during the same call.
/// </summary>
public sealed class DialoguePronunciationProcessor
{
    private static readonly Regex ExistingKokoroMarkupRegex =
        new(@"\[[^\]]+\]\(/[^)]*\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyList<PronunciationRule> _rules;

    public DialoguePronunciationProcessor(IEnumerable<PronunciationRule> rules)
    {
        _rules = rules
            .OrderByDescending(r => r.MatchText.Length) // longest phrases first
            .ThenByDescending(r => r.Priority)
            .ToArray();
    }

    public AssembledSegment Process(AssembledSegment segment)
    {
        if (string.IsNullOrWhiteSpace(segment.Text))
            return segment;

        var processed = ProcessText(segment.Text, segment.Slot);
        if (ReferenceEquals(processed, segment.Text) || processed == segment.Text)
            return segment;

        return new AssembledSegment
        {
            Text = processed,
            Slot = segment.Slot,
            DialogId = segment.DialogId,
            SegmentIndex = segment.SegmentIndex,
            NpcId = segment.NpcId
        };
    }

    public string ProcessText(string text, VoiceSlot slot)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var applicableRules = _rules
            .Where(r => r.Group is null || r.Group == slot.Group)
            .ToArray();

        if (applicableRules.Length == 0)
            return text;

        var protectedRanges = GetProtectedRanges(text);

        var candidates = new List<MatchCandidate>(128);

        foreach (var rule in applicableRules)
        {
            foreach (var candidate in FindCandidates(text, rule, protectedRanges))
                candidates.Add(candidate);
        }

        if (candidates.Count == 0)
            return text;

        // Single-pass resolution:
        // - earliest start wins
        // - for same start, longer match wins
        // - then higher priority
        var selected = candidates
            .OrderBy(c => c.Start)
            .ThenByDescending(c => c.Length)
            .ThenByDescending(c => c.Rule.Priority)
            .ToList();

        var accepted = new List<MatchCandidate>(selected.Count);
        int nextFreeIndex = 0;

        foreach (var candidate in selected)
        {
            if (candidate.Start < nextFreeIndex)
                continue;

            accepted.Add(candidate);
            nextFreeIndex = candidate.End;
        }

        if (accepted.Count == 0)
            return text;

        var sb = new StringBuilder(text.Length + accepted.Count * 16);
        int cursor = 0;

        foreach (var match in accepted)
        {
            if (cursor < match.Start)
                sb.Append(text, cursor, match.Start - cursor);

            sb.Append(match.VisibleMarkup);
            cursor = match.End;
        }

        if (cursor < text.Length)
            sb.Append(text, cursor, text.Length - cursor);

        return sb.ToString();
    }

    private static IEnumerable<MatchCandidate> FindCandidates(
        string text,
        PronunciationRule rule,
        IReadOnlyList<TextRange> protectedRanges)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
        if (!rule.CaseSensitive)
            options |= RegexOptions.IgnoreCase;

        var escaped = Regex.Escape(rule.MatchText);
        var pattern = rule.WholeWord
            ? $@"(?<![\p{{L}}\p{{N}}]){escaped}(?![\p{{L}}\p{{N}}])"
            : escaped;

        foreach (Match match in Regex.Matches(text, pattern, options))
        {
            if (!match.Success || match.Length == 0)
                continue;

            if (OverlapsProtected(match.Index, match.Index + match.Length, protectedRanges))
                continue;

            var visible = text.Substring(match.Index, match.Length);
            var markup = $"[{visible}](/{rule.PhonemeText}/)";

            yield return new MatchCandidate(
                match.Index,
                match.Length,
                rule,
                markup);
        }
    }

    private static List<TextRange> GetProtectedRanges(string text)
    {
        var ranges = new List<TextRange>();

        foreach (Match match in ExistingKokoroMarkupRegex.Matches(text))
        {
            if (match.Success && match.Length > 0)
                ranges.Add(new TextRange(match.Index, match.Index + match.Length));
        }

        return ranges;
    }

    private static bool OverlapsProtected(int start, int end, IReadOnlyList<TextRange> ranges)
    {
        foreach (var range in ranges)
        {
            if (start < range.End && end > range.Start)
                return true;
        }

        return false;
    }

    private readonly record struct MatchCandidate(
        int Start,
        int Length,
        PronunciationRule Rule,
        string VisibleMarkup)
    {
        public int End => Start + Length;
    }

    private readonly record struct TextRange(int Start, int End);
}