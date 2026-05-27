// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS;

namespace RuneReaderVoice.Session;

internal static class NarratorSegmentExpander
{
    public static IReadOnlyList<AssembledSegment> Expand(AssembledSegment segment)
    {
        if (segment.SkipNarratorMarkerExpansion || segment.NpcId == 0 || string.IsNullOrWhiteSpace(segment.Text))
            return new[] { segment };

        var runs = SplitNarratorForcedRuns(segment.Text);
        if (runs.Count <= 1)
            return new[] { segment };

        var narratorSlot = segment.Slot.Gender == Gender.Female
            ? VoiceSlot.FemaleNarrator
            : VoiceSlot.MaleNarrator;

        var expanded = new List<AssembledSegment>(runs.Count);
        foreach (var run in runs)
        {
            var trimmed = run.Text.Trim();
            if (trimmed.Length == 0)
                continue;

            if (run.IsNarrator)
            {
                expanded.Add(new AssembledSegment
                {
                    Text = trimmed,
                    Slot = narratorSlot,
                    DialogId = segment.DialogId,
                    SegmentIndex = segment.SegmentIndex,
                    DialogSegmentCount = segment.DialogSegmentCount,
                    NpcId = 0,
                    NpcName = segment.NpcName,
                    PlayerName = segment.PlayerName,
                    PlayerRealm = segment.PlayerRealm,
                    PlayerClass = segment.PlayerClass,
                    PlayerTitle = segment.PlayerTitle,
                    BatchId = null,
                    BatchSegmentId = null,
                    PrimeFromBatchSegmentId = null,
                    BatchSegments = null,
                    BespokeSampleId = null,
                    BespokeExaggeration = null,
                    BespokeCfgWeight = null,
                    UseNpcIdAsSeed = false,
                    SkipNarratorMarkerExpansion = false,
                    IsNarratorSegment = true,
                });
            }
            else
            {
                expanded.Add(new AssembledSegment
                {
                    Text = trimmed,
                    Slot = segment.Slot,
                    DialogId = segment.DialogId,
                    SegmentIndex = segment.SegmentIndex,
                    DialogSegmentCount = segment.DialogSegmentCount,
                    NpcId = segment.NpcId,
                    NpcName = segment.NpcName,
                    PlayerName = segment.PlayerName,
                    PlayerRealm = segment.PlayerRealm,
                    PlayerClass = segment.PlayerClass,
                    PlayerTitle = segment.PlayerTitle,
                    BatchId = segment.BatchId,
                    BatchSegmentId = segment.BatchSegmentId,
                    PrimeFromBatchSegmentId = segment.PrimeFromBatchSegmentId,
                    BatchSegments = segment.BatchSegments,
                    BespokeSampleId = segment.BespokeSampleId,
                    BespokeMatchedByNpcName = segment.BespokeMatchedByNpcName,
                    MissingBespokeSampleId = segment.MissingBespokeSampleId,
                    BespokeExaggeration = segment.BespokeExaggeration,
                    BespokeCfgWeight = segment.BespokeCfgWeight,
                    UseNpcIdAsSeed = segment.UseNpcIdAsSeed,
                    SkipNarratorMarkerExpansion = segment.SkipNarratorMarkerExpansion,
                    IsNarratorSegment = false,
                });
            }
        }

        return expanded.Count == 0 ? new[] { segment } : expanded;
    }

    private static List<(string Text, bool IsNarrator)> SplitNarratorForcedRuns(string text)
    {
        var runs = new List<(string Text, bool IsNarrator)>();
        if (string.IsNullOrWhiteSpace(text))
            return runs;

        var sb = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            bool isAngle = ch == '<';
            bool isBracket = ch == '[';
            if (!isAngle && !isBracket)
            {
                sb.Append(ch);
                continue;
            }

            var close = isAngle ? '>' : ']';
            var end = text.IndexOf(close, i + 1);
            if (end <= i + 1)
            {
                sb.Append(ch);
                continue;
            }

            if (sb.Length > 0)
            {
                runs.Add((sb.ToString(), false));
                sb.Clear();
            }

            var inner = text.Substring(i + 1, end - i - 1);
            if (!string.IsNullOrWhiteSpace(inner))
                runs.Add((inner, true));

            i = end;
        }

        if (sb.Length > 0)
            runs.Add((sb.ToString(), false));

        return runs;
    }
}
