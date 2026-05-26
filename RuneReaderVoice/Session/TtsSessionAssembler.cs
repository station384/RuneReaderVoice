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
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Data;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.Session;

// TtsSessionAssembler.cs
// Collects QR chunks for a single dialog session and fires OnSegmentComplete
// once all segments in the dialog are fully assembled, in SeqIndex order.
//
// Protocol v05 reality (from payload.lua):
//   - One dialog = one DialogID, N segments (narrator splits etc.).
//   - Each packet carries SEQ/SEQTOTAL (segment position in dialog) and
//     SUB/SUBTOTAL (barcode chunk position within that segment).
//   - SEQTOTAL is known from the very first barcode scan of a dialog.
//   - Segments are streamed sequentially and cycle continuously until dialog closes.
//
// Assembly strategy:
//   - On first packet of a new dialog, record SeqTotal.
//   - Each segment has its own SegmentAccumulator keyed by SeqIndex.
//   - When a segment's SUB chunks are all received it is marked complete but
//     NOT yet fired — it waits in _completedSegments.
//   - Only when _completedSegments.Count == SeqTotal do we fire OnSegmentComplete
//     for all segments in SeqIndex order.
//   - This guarantees ordered delivery regardless of which segment assembles fastest.
//
// Re-loop handling:
//   When a completed segment's SUB=0 chunk arrives again it is ignored.
//   _completedKeys tracks which segments have already fired.
//
// Chunk ordering:
//   Non-zero SUB chunks that arrive before SUB=0 are stashed in _earlyChunks
//   and replayed when SUB=0 arrives to establish the segment key.
//
// NPC race override lookup chain:
//   1. _npcRaceStore (in-memory, pre-loaded from NpcRaceOverrideDb at startup)
//   2. packet.Race (from QR header — creature type or player race)
//   3. Falls back to narrator by packet gender when no explicit NPC override exists

public sealed class AssembledSegment
{
    public string    Text              { get; init; } = string.Empty;
    public VoiceSlot Slot              { get; init; }
    public int       DialogId          { get; init; }
    public int       SegmentIndex      { get; init; }
    public int       DialogSegmentCount { get; init; }
    public int       NpcId             { get; init; }
    public string?    NpcName           { get; init; } = null;
    public string?    PlayerName        { get; init; } = null;
    public string?    PlayerRealm       { get; init; } = null;
    public string?    PlayerClass       { get; init; } = null;
    public string?    PlayerTitle       { get; init; } = null;

    // Experimental remote batch priming metadata for player-name split testing.
    public string? BatchId { get; init; } = null;
    public string? BatchSegmentId { get; init; } = null;
    public string? PrimeFromBatchSegmentId { get; init; } = null;
    public IReadOnlyList<BatchSegmentPlan>? BatchSegments { get; init; } = null;

    // Bespoke voice override resolved at assembly time from NpcRaceOverride.
    // Null means use the race slot defaults.
    public string?   BespokeSampleId    { get; init; } = null;
    public bool      BespokeMatchedByNpcName { get; init; } = false;
    public string?   MissingBespokeSampleId { get; init; } = null;
    public float?    BespokeExaggeration { get; init; } = null;
    public float?    BespokeCfgWeight   { get; init; } = null;
    public bool      UseNpcIdAsSeed    { get; init; } = false;
    public bool      SkipNarratorMarkerExpansion { get; init; } = false;

    // True only for protocol/forced narrator text. A slot may still be Narrator as
    // a race/unknown fallback; that must not block NPC bespoke sample matching.
    public bool      IsNarratorSegment { get; init; } = false;
}

public sealed class BatchSegmentPlan
{
    public string SegmentId { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string? PrimeFromSegmentId { get; init; } = null;
}

public sealed class TtsSessionAssembler
{
    // ── Events ───────────────────────────────────────────────────────────────

    public event Action<AssembledSegment>? OnSegmentComplete;
    public event Action<int>?              OnSessionReset;

    // ── Per-segment accumulator ───────────────────────────────────────────────

    private sealed class SegmentAccumulator
    {
        public string?[] Subs         { get; }          // barcode chunks for this segment
        public int       SubsReceived { get; set; }
        public VoiceSlot Slot         { get; init; }
        public int       NpcId        { get; init; }
        public int       SeqIndex     { get; init; }    // position within dialog, assigned at creation
        public bool      IsNarrator   { get; init; }
        public bool      IsFemale     { get; init; }
        public bool      IsMale       { get; init; }

        public SegmentAccumulator(int subTotal, VoiceSlot slot, int npcId, int seqIndex, bool isNarrator, bool isFemale, bool isMale)
        {
            Subs       = new string?[subTotal];
            Slot       = slot;
            NpcId      = npcId;
            SeqIndex   = seqIndex;
            IsNarrator = isNarrator;
            IsFemale   = isFemale;
            IsMale     = isMale;
        }
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private int _currentDialogId = -1;
    private int _seqTotal;          // how many segments this dialog has (from SeqTotal field)

    // Active accumulators: key = MakeKey(subTotal, flags, race, sub0payload)
    private readonly Dictionary<string, SegmentAccumulator> _segments          = new();
    // Keys of segments whose chunks are all received (re-loop guard)
    private readonly HashSet<string>                        _completedKeys      = new();
    private readonly HashSet<string>                        _completedUtteranceKeys = new();
    // Early sub-chunks (arrived before SUB=0): key = MakeEarlyKey(subTotal, flags, race, seqIndex)
    private readonly Dictionary<string, List<(int sub, string payload)>> _earlyChunks = new();
    // Fully assembled segments waiting for the rest of the dialog to complete
    // before being fired. Key = SeqIndex, guaranteed 0-based contiguous.
    private readonly Dictionary<int, AssembledSegment> _completedSegments = new();

    private readonly object _lock = new();

    private readonly NpcRaceOverrideDb _overrideDb;

    private string _currentPlayerName  = string.Empty;
    private string _currentPlayerRealm = string.Empty;
    private string _currentPlayerClass = string.Empty;
    private string _currentPlayerTitle = string.Empty;
    private string _currentNpcName     = string.Empty;

    // ── Construction ──────────────────────────────────────────────────────────

    public TtsSessionAssembler(NpcRaceOverrideDb overrideDb)
    {
        _overrideDb = overrideDb;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Feed(RvPacket packet)
    {
        List<AssembledSegment>? toFire = null;

        lock (_lock)
        {
            // ── New dialog ────────────────────────────────────────────────────
            if (packet.DialogId != _currentDialogId)
            {
                _currentDialogId = packet.DialogId;
                _seqTotal        = packet.SeqTotal;
                _segments.Clear();
                _completedKeys.Clear();
                _completedUtteranceKeys.Clear();
                _earlyChunks.Clear();
                _completedSegments.Clear();
                _currentPlayerName  = AppServices.CurrentPlayerName  ?? string.Empty;
                _currentPlayerRealm = AppServices.CurrentPlayerRealm ?? string.Empty;
                _currentPlayerClass = AppServices.CurrentPlayerClass ?? string.Empty;
                _currentPlayerTitle = string.Empty;
                _currentNpcName     = string.Empty;
                AppServices.CurrentPlayerTitle = string.Empty;
                OnSessionReset?.Invoke(_currentDialogId);
                System.Diagnostics.Debug.WriteLine(
                    $"[Assembler] New dialog 0x{packet.DialogId:X4} seqTotal={packet.SeqTotal}");
            }

            // ── Runtime routing baseline ─────────────────────────────────────
            // Packet race/gender provides the protocol baseline. Local NPC overrides
            // remain authoritative and are applied when the segment completes.
            int effectiveRace = packet.Race;
            var packetGender = packet.IsFemale ? Gender.Female : packet.IsMale ? Gender.Male : Gender.Unknown;
            VoiceSlot resolvedSlot;
            if (packet.IsNarrator)
            {
                resolvedSlot = packet.IsFemale ? VoiceSlot.FemaleNarrator : VoiceSlot.MaleNarrator;
            }
            else
            {
                var catalogId = NpcPeopleCatalogService.CatalogIdFromRaceId(effectiveRace);
                resolvedSlot = !string.IsNullOrWhiteSpace(catalogId)
                    ? (AppServices.NpcPeopleCatalog?.ResolveCatalogSlot(catalogId, packetGender) ?? VoiceSlot.CreateCatalog(catalogId, packetGender))
                    : (packet.IsFemale ? VoiceSlot.FemaleNarrator : VoiceSlot.MaleNarrator);
            }

            if (packet.SubIndex == 0)
            {
                var slot = resolvedSlot;
                var key  = MakeKey(packet.SubTotal, packet.Flags,
                                   effectiveRace, packet.Base64Payload,
                                   packet.SeqIndex);

                // Already completed — re-loop, ignore
                if (_completedKeys.Contains(key)) return;

                if (!_segments.TryGetValue(key, out var acc))
                {
                    acc = new SegmentAccumulator(packet.SubTotal, slot, packet.NpcId,
                                                 packet.SeqIndex, packet.IsNarrator, packet.IsFemale, packet.IsMale);
                    _segments[key] = acc;
                    System.Diagnostics.Debug.WriteLine(
                        $"[Assembler] New acc seq={packet.SeqIndex} sub=0/{packet.SubTotal} npc={packet.NpcId} slot={slot}");

                    // Replay stashed early sub-chunks for this segment
                    var earlyKey = MakeEarlyKey(packet.SubTotal, packet.Flags, effectiveRace, packet.SeqIndex);
                    if (_earlyChunks.TryGetValue(earlyKey, out var early))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Assembler] Replaying {early.Count} early subs for seq={packet.SeqIndex}");
                        foreach (var (sub, payload) in early)
                        {
                            if (sub < acc.Subs.Length && acc.Subs[sub] == null)
                            {
                                acc.Subs[sub] = payload;
                                acc.SubsReceived++;
                            }
                        }
                        _earlyChunks.Remove(earlyKey);
                    }
                }

                // Store SUB=0 (idempotent)
                if (acc.Subs[0] == null)
                {
                    acc.Subs[0] = packet.Base64Payload;
                    acc.SubsReceived++;
                }

                TryCompleteSegment(acc, key);
            }
            else
            {
                // Non-zero sub-chunk: find the matching in-progress accumulator.
                // Require Subs[0] to be populated — this anchors the accumulator
                // to a specific segment identity and prevents stale subs from a
                // previous transmission of a same-shaped segment from matching a
                // new accumulator that hasn't received its SUB=0 yet.
                var acc = _segments.Values.FirstOrDefault(a =>
                    a.Subs.Length == packet.SubTotal &&
                    a.Subs[0] != null &&
                    a.Subs[packet.SubIndex] == null);

                if (acc != null)
                {
                    acc.Subs[packet.SubIndex] = packet.Base64Payload;
                    acc.SubsReceived++;
                    System.Diagnostics.Debug.WriteLine(
                        $"[Assembler] sub {packet.SubIndex}/{packet.SubTotal} -> seq={acc.SeqIndex} ({acc.SubsReceived}/{acc.Subs.Length} received)");
                    var key = _segments.First(kv => kv.Value == acc).Key;
                    TryCompleteSegment(acc, key);
                }
                else
                {
                    // SUB=0 hasn't arrived yet — stash
                    var earlyKey = MakeEarlyKey(packet.SubTotal, packet.Flags, effectiveRace, packet.SeqIndex);
                    if (!_earlyChunks.TryGetValue(earlyKey, out var early))
                    {
                        early = new List<(int, string)>();
                        _earlyChunks[earlyKey] = early;
                    }
                    if (early.All(e => e.sub != packet.SubIndex))
                    {
                        early.Add((packet.SubIndex, packet.Base64Payload));
                        System.Diagnostics.Debug.WriteLine(
                            $"[Assembler] Stashed early sub={packet.SubIndex}/{packet.SubTotal} seq={packet.SeqIndex} (no anchor yet)");
                    }
                }
            }

            // ── Fire all segments once the full dialog is assembled ───────────
            // Only when every expected segment is in _completedSegments do we
            // release them to the coordinator, in SeqIndex order.
            if (_completedSegments.Count == _seqTotal && _seqTotal > 0)
            {
                toFire = new List<AssembledSegment>(_seqTotal);
                for (int i = 0; i < _seqTotal; i++)
                {
                    var seg = _completedSegments[i];
                    if (!string.IsNullOrWhiteSpace(seg.Text))
                        toFire.Add(seg);
                }
                _completedSegments.Clear();
                System.Diagnostics.Debug.WriteLine(
                    $"[Assembler] Dialog 0x{_currentDialogId:X4} complete — firing {toFire.Count} audible segment(s)");
            }
        }

        if (toFire != null)
        {
            var expandedSegments = new List<AssembledSegment>();
            foreach (var seg in toFire)
                expandedSegments.AddRange(ExpandNarratorForcedSegments(seg));

            var processedSegments = new List<(AssembledSegment Segment, string Text)>(expandedSegments.Count);
            foreach (var seg in expandedSegments)
            {
                LogTextPipeline("expanded-before-period-quote", seg, seg.Text);
                var emittedText = InjectSyntheticParagraphPeriods(seg.Text);
                LogTextPipeline("after-period-injection", seg, emittedText);
                if (!seg.IsNarratorSegment &&
                    !IsSyntheticBookNpcId(seg.NpcId) &&
                    AppServices.Settings.QuoteDialogueParagraphsForTts)
                {
                    emittedText = QuoteDialogueParagraphs(emittedText);
                    LogTextPipeline("after-dialogue-quote", seg, emittedText);
                }

                if (!IsPunctuationOnlySegment(emittedText))
                    processedSegments.Add((seg, emittedText));
                else
                    LogTextPipeline("dropped-punctuation-only", seg, emittedText);
            }

            var audibleCount = processedSegments.Count;
            for (var audibleIndex = 0; audibleIndex < processedSegments.Count; audibleIndex++)
            {
                var (seg, emittedText) = processedSegments[audibleIndex];
                var emitted = new AssembledSegment
                {
                    Text = emittedText,
                    Slot = seg.Slot,
                    DialogId = seg.DialogId,
                    SegmentIndex = audibleIndex,
                    DialogSegmentCount = audibleCount,
                    NpcId = seg.NpcId,
                    NpcName = seg.NpcName,
                    PlayerName = seg.PlayerName,
                    PlayerRealm = seg.PlayerRealm,
                    PlayerClass = seg.PlayerClass,
                    PlayerTitle = seg.PlayerTitle,
                    BatchId = seg.BatchId,
                    BatchSegmentId = seg.BatchSegmentId,
                    PrimeFromBatchSegmentId = seg.PrimeFromBatchSegmentId,
                    BatchSegments = seg.BatchSegments,
                    BespokeSampleId = seg.BespokeSampleId,
                    BespokeMatchedByNpcName = seg.BespokeMatchedByNpcName,
                    MissingBespokeSampleId = seg.MissingBespokeSampleId,
                    BespokeExaggeration = seg.BespokeExaggeration,
                    BespokeCfgWeight = seg.BespokeCfgWeight,
                    UseNpcIdAsSeed = seg.UseNpcIdAsSeed,
                    SkipNarratorMarkerExpansion = seg.SkipNarratorMarkerExpansion,
                    IsNarratorSegment = seg.IsNarratorSegment,
                };
                System.Diagnostics.Debug.WriteLine(
                    $"[Assembler] Firing seg={emitted.SegmentIndex} slot={emitted.Slot} npc={emitted.NpcId}" +
                    $" bespoke={emitted.BespokeSampleId ?? "none"} text='{emitted.Text.Substring(0, Math.Min(60, emitted.Text.Length))}'");
                AppServices.LastSegment = emitted;
                RuneReaderVoice.AppServices.RecordSegmentAssembled(emitted);
                OnSegmentComplete?.Invoke(emitted);
            }
        }
    }

    public void SignalSourceGone()
    {
        // No-op: playback continues; same dialog may reappear.
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void LogTextPipeline(string stage, AssembledSegment segment, string? text)
    {
        var safe = VisibleText(text);
        var line =
            $"[TextPipeline][Assembler] {stage} dialog=0x{segment.DialogId:X} seg={segment.SegmentIndex}/{segment.DialogSegmentCount} " +
            $"slot={segment.Slot} npc={segment.NpcId} narrator={segment.IsNarratorSegment} " +
            $"player='{segment.PlayerName ?? string.Empty}' title='{segment.PlayerTitle ?? string.Empty}' realm='{segment.PlayerRealm ?? string.Empty}' " +
            $"len={text?.Length ?? 0} words={CountWords(text ?? string.Empty)} text=<<<{safe}>>>";
        Console.WriteLine(line);
        Debug.WriteLine(line);
    }

    private static string VisibleText(string? text)
    {
        if (text == null)
            return string.Empty;

        return text
            .Replace("\r\n", "␊", StringComparison.Ordinal)
            .Replace("\n", "␊", StringComparison.Ordinal)
            .Replace("\r", "␍", StringComparison.Ordinal)
            .Replace("\t", "␉", StringComparison.Ordinal);
    }

    /// <summary>
    /// If all sub-chunks for this segment have arrived, decodes the text and
    /// stores the result in _completedSegments keyed by SeqIndex.
    /// Does NOT fire OnSegmentComplete — that only happens once the full dialog
    /// (_seqTotal segments) are all present in _completedSegments.
    /// </summary>
    private void TryCompleteSegment(SegmentAccumulator acc, string key)
    {
        if (acc.SubsReceived != acc.Subs.Length) return;
        if (acc.Subs.Any(s => s == null)) return;
        if (_completedKeys.Contains(key)) return;

        var text = DecodeAndClean(acc.Subs!);
        text = ExtractAndApplyDialogMetadata(text);
        var htmlMode = HtmlRenderedTextExtractor.LooksLikeHtml(text);
        text = htmlMode ? HtmlRenderedTextExtractor.ExtractFromMixedText(text) : HtmlTextStripper.Strip(text);

        var effectiveNpcId = ResolveEffectiveNpcId(acc.NpcId);
        var npcName = ResolveCurrentNpcName();

        var utteranceKey = MakeUtteranceKey(_currentDialogId, acc.Slot, effectiveNpcId, text, acc.SeqIndex);
        if (_completedUtteranceKeys.Contains(utteranceKey)) return;

        _completedKeys.Add(key);
        _completedUtteranceKeys.Add(utteranceKey);

        var slot = acc.Slot;
        string? bespokeSampleId = null;
        string? missingBespokeSampleId = null;
        var bespokeMatchedByNpcName = false;
        float? bespokeExaggeration = null;
        float? bespokeCfgWeight = null;
        var useNpcIdAsSeed = false;

        if (!acc.IsNarrator || IsSyntheticBookNpcId(effectiveNpcId))
        {
            var entry = effectiveNpcId > 0
                ? Task.Run(() => _overrideDb.GetOverrideAsync(effectiveNpcId)).GetAwaiter().GetResult()
                : null;
            if (entry != null)
            {
                var g = entry.GenderOverride switch
                {
                    NpcGenderOverride.Male   => Gender.Male,
                    NpcGenderOverride.Female => Gender.Female,
                    _ => acc.IsMale ? Gender.Male : acc.IsFemale ? Gender.Female : Gender.Unknown,
                };
                var catalogId = !string.IsNullOrWhiteSpace(entry.CatalogId)
                    ? entry.CatalogId
                    : NpcRaceOverrideDb.LegacyRaceIdToCatalogId(entry.RaceId);

                if (!string.IsNullOrWhiteSpace(catalogId))
                {
                    slot = AppServices.NpcPeopleCatalog?.ResolveCatalogSlot(catalogId, g)
                           ?? VoiceSlot.CreateCatalog(catalogId, g);
                }

                bespokeSampleId = entry.BespokeSampleId;
                bespokeExaggeration = entry.BespokeExaggeration;
                bespokeCfgWeight = entry.BespokeCfgWeight;
                useNpcIdAsSeed = entry.UseNpcIdAsSeed;
            }

            bespokeMatchedByNpcName = TryResolveBespokeSampleFromNpcName(
                npcName,
                bespokeSampleId,
                out var matchedSampleId,
                out missingBespokeSampleId);

            if (bespokeMatchedByNpcName && !string.IsNullOrWhiteSpace(matchedSampleId))
            {
                Debug.WriteLine($"[Assembler] NPC name matched bespoke sample npc='{npcName}' sample='{matchedSampleId}' slot={slot} isNarratorFlag={acc.IsNarrator}");
                bespokeSampleId = matchedSampleId;
            }
            else if (!string.IsNullOrWhiteSpace(npcName) && string.IsNullOrWhiteSpace(bespokeSampleId))
            {
                Debug.WriteLine($"[Assembler] NPC name had no bespoke sample match npc='{npcName}' slot={slot} isNarratorFlag={acc.IsNarrator}");
            }
        }

        _completedSegments[acc.SeqIndex] = new AssembledSegment
        {
            Text                = text,
            Slot                = slot,
            DialogId            = _currentDialogId,
            SegmentIndex        = acc.SeqIndex,
            NpcId               = effectiveNpcId,
            NpcName             = string.IsNullOrWhiteSpace(npcName) ? null : npcName,
            PlayerName          = string.IsNullOrWhiteSpace(_currentPlayerName) ? null : _currentPlayerName,
            PlayerRealm         = string.IsNullOrWhiteSpace(_currentPlayerRealm) ? null : _currentPlayerRealm,
            PlayerClass         = string.IsNullOrWhiteSpace(_currentPlayerClass) ? null : _currentPlayerClass,
            PlayerTitle         = string.IsNullOrWhiteSpace(_currentPlayerTitle) ? null : _currentPlayerTitle,
            BespokeSampleId     = bespokeSampleId,
            BespokeMatchedByNpcName = bespokeMatchedByNpcName,
            MissingBespokeSampleId = missingBespokeSampleId,
            BespokeExaggeration = bespokeExaggeration,
            BespokeCfgWeight    = bespokeCfgWeight,
            UseNpcIdAsSeed      = useNpcIdAsSeed,
            IsNarratorSegment   = acc.IsNarrator,
        };
    }

    private static IReadOnlyList<AssembledSegment> ExpandNarratorForcedSegments(AssembledSegment segment)
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


    private static string QuoteDialogueParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var rawParts = Regex.Split(normalized, @"(\n\s*\n)+");
        var paragraphs = new List<string>();

        for (int i = 0; i < rawParts.Length; i += 2)
        {
            var part = rawParts[i];
            if (string.IsNullOrWhiteSpace(part))
                continue;

            var trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;

            paragraphs.Add(IsAlreadyQuoted(trimmed) ? trimmed : $"\"{trimmed}\"");
        }

        return paragraphs.Count == 0 ? text : string.Join("\n\n", paragraphs);
    }

    private static bool IsAlreadyQuoted(string text)
    {
        if (text.Length < 2)
            return false;

        return (text[0] == '"' && text[^1] == '"') ||
               (text[0] == '“' && text[^1] == '”') ||
               (text[0] == '‘' && text[^1] == '’');
    }

    private static bool IsPunctuationOnlySegment(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        return !text.Any(char.IsLetterOrDigit);
    }

    private static string InjectSyntheticParagraphPeriods(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var rawParts = Regex.Split(normalized, "(\\n\\s*\\n)+");
        var paragraphs = new List<string>();

        for (int i = 0; i < rawParts.Length; i += 2)
        {
            var part = rawParts[i];
            if (string.IsNullOrWhiteSpace(part))
                continue;

            var trimmedEnd = part.TrimEnd();
            if (trimmedEnd.Length == 0)
                continue;

            if (!Regex.IsMatch(trimmedEnd, @"^\s*[-*•]"))
            {
                var last = trimmedEnd[^1];
                if (".!?…:;)]}\"'".IndexOf(last) < 0)
                    trimmedEnd += ".";
            }

            var wordCount = CountWords(trimmedEnd);
            var isBullet = Regex.IsMatch(trimmedEnd, @"^\s*[-*•]");

            if (!isBullet && wordCount > 0 && wordCount <= 3 && paragraphs.Count > 0)
            {
                paragraphs[^1] = MergeShortParagraph(paragraphs[^1], trimmedEnd);

                while (paragraphs.Count > 1 && CountWords(paragraphs[^1]) < 6)
                {
                    var carry = paragraphs[^1];
                    paragraphs.RemoveAt(paragraphs.Count - 1);
                    paragraphs[^1] = MergeShortParagraph(paragraphs[^1], carry);
                }
            }
            else
            {
                paragraphs.Add(trimmedEnd);
            }
        }

        return string.Join("\n\n", paragraphs);
    }


    private static int ResolveEffectiveNpcId(int packetNpcId)
    {
        if (packetNpcId > 0)
            return packetNpcId;

        return TryExtractNpcIdFromGuid(AppServices.CurrentRrvbGuid) ?? 0;
    }

    private string ResolveCurrentNpcName()
    {
        if (!string.IsNullOrWhiteSpace(_currentNpcName))
            return _currentNpcName;

        return AppServices.CurrentRrvbName ?? string.Empty;
    }

    private static bool TryResolveBespokeSampleFromNpcName(
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

        var requiredMatches = npcTokens.Count <= 2 ? npcTokens.Count : npcTokens.Count - 1;
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
            .Where(x => x.MatchedTokens.Length >= requiredMatches || x.CompactMatch)
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

    private static int? TryExtractNpcIdFromGuid(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return null;

        var parts = guid.Split('-');
        if (parts.Length < 6)
            return null;

        if (!string.Equals(parts[0], "Creature", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parts[0], "Vehicle", StringComparison.OrdinalIgnoreCase))
            return null;

        return int.TryParse(parts[5], out var npcId) && npcId > 0 ? npcId : null;
    }

    private static bool IsSyntheticBookNpcId(int npcId)
        => npcId >= 0xF00000 && npcId <= 0xFFFFFF;

    private static int CountWords(string text)
        => string.IsNullOrWhiteSpace(text)
            ? 0
            : Regex.Matches(text, @"\b[\p{L}\p{N}']+\b").Count;

    private static string MergeShortParagraph(string left, string right)
    {
        var mergedLeft = (left ?? string.Empty).TrimEnd();
        var mergedRight = (right ?? string.Empty).Trim();

        if (mergedLeft.Length == 0)
            return mergedRight;
        if (mergedRight.Length == 0)
            return mergedLeft;

        if (mergedLeft.EndsWith(".,", StringComparison.Ordinal))
            return mergedLeft + " " + mergedRight;

        if (mergedLeft.EndsWith(".", StringComparison.Ordinal))
            mergedLeft = mergedLeft[..^1] + ".,";
        else if (mergedLeft.EndsWith("!", StringComparison.Ordinal) ||
                 mergedLeft.EndsWith("?", StringComparison.Ordinal) ||
                 mergedLeft.EndsWith("…", StringComparison.Ordinal))
            mergedLeft += ",";
        else if (!mergedLeft.EndsWith(",", StringComparison.Ordinal))
            mergedLeft += ".,";

        return mergedLeft + " " + mergedRight;
    }

    private static string MakeUtteranceKey(int dialogId, VoiceSlot slot, int npcId,
                                            string text, int seqIndex)
        => $"{dialogId}|{seqIndex}|{slot}|{npcId}|{text}";

    private static string MakeKey(int subTotal, int flags, int race, string sub0,
                                   int seqIndex = -1)
        => seqIndex >= 0
            ? $"{seqIndex}|{subTotal}|{flags}|{race}|{sub0}"
            : $"{subTotal}|{flags}|{race}|{sub0}";

    private static string MakeEarlyKey(int subTotal, int flags, int race, int seqIndex)
        => $"{subTotal}|{flags}|{race}|{seqIndex}";

    private string ExtractAndApplyDialogMetadata(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var cleaned = ExtractMetadataTokens(text);
        return ApplyPlayerReplacement(cleaned);
    }

    private string ExtractMetadataTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\x02')
            {
                int end = text.IndexOf('\x03', i + 1);
                if (end > i)
                {
                    ParseMetadataBody(text.Substring(i + 1, end - i - 1));
                    i = end + 1;
                    continue;
                }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString().Trim();
    }

    private void ParseMetadataBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        if (body.StartsWith("RRV:", StringComparison.OrdinalIgnoreCase))
            body = body.Substring(4);

        var equals = body.IndexOf('=');
        if (equals <= 0 || equals >= body.Length - 1) return;

        var key = body[..equals].Trim().ToUpperInvariant();
        var value = body[(equals + 1)..].Trim();

        if (key == "TITLE")
        {
            _currentPlayerTitle = value;
            AppServices.CurrentPlayerTitle = value;
            return;
        }

        if (string.IsNullOrWhiteSpace(value)) return;

        if (key == "PLAYER")
        {
            var (name, realm) = SplitNameAndRealm(value);
            if (!string.IsNullOrWhiteSpace(name)) _currentPlayerName = name;
            if (!string.IsNullOrWhiteSpace(realm)) _currentPlayerRealm = realm;
            AppServices.CurrentPlayerName = _currentPlayerName;
            if (!string.IsNullOrWhiteSpace(_currentPlayerRealm)) AppServices.CurrentPlayerRealm = _currentPlayerRealm;
        }
        else if (key == "REALM")
        {
            _currentPlayerRealm = value;
            AppServices.CurrentPlayerRealm = value;
        }
        else if (key == "CLASS")
        {
            _currentPlayerClass = value;
            AppServices.CurrentPlayerClass = value;
        }
        else if (key == "NPCNAME")
        {
            _currentNpcName = value;
        }
    }

    private string BuildPlayerNameWithOptionalRealm(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return string.Empty;

        if (AppServices.Settings.PlayerNameAppendRealm && !string.IsNullOrWhiteSpace(_currentPlayerRealm))
            return $"{playerName} of {_currentPlayerRealm}";

        return playerName;
    }

    private string ResolvePlayerTitleReplacement(string? titleText, string? playerName)
    {
        var title = titleText?.Trim() ?? string.Empty;
        var resolvedPlayerName = playerName?.Trim() ?? string.Empty;

        if (!AppServices.Settings.PlayerNameEnableTitle)
            return string.IsNullOrWhiteSpace(resolvedPlayerName) ? "Hero" : resolvedPlayerName;

        if (string.IsNullOrWhiteSpace(title))
            return string.IsNullOrWhiteSpace(resolvedPlayerName) ? "Hero" : resolvedPlayerName;

        if (!string.IsNullOrWhiteSpace(resolvedPlayerName) && title.Contains("%s", StringComparison.OrdinalIgnoreCase))
            return Regex.Replace(title, "%s", m => resolvedPlayerName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return title;
    }

    private string ApplyPlayerReplacement(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_currentPlayerName))
            return text;

        var mode = (AppServices.Settings.PlayerNameMode ?? "generic").Trim().ToLowerInvariant();

        var actualNameWithRealm = BuildPlayerNameWithOptionalRealm(_currentPlayerName);
        var replacement = actualNameWithRealm;
        if (mode != "actual" && mode != "split")
        {
            var preset = (AppServices.Settings.PlayerNameReplacementPreset ?? "hero").Trim().ToLowerInvariant();
            replacement = preset switch
            {
                "champion" => "Champion",
                "class" => string.IsNullOrWhiteSpace(_currentPlayerClass) ? "Hero" : _currentPlayerClass,
                "title" => ResolvePlayerTitleReplacement(_currentPlayerTitle, actualNameWithRealm),
                _ => "Hero",
            };
        }
        else if (AppServices.Settings.PlayerNameEnableTitle)
        {
            replacement = ResolvePlayerTitleReplacement(_currentPlayerTitle, actualNameWithRealm);
        }

        if (mode != "actual" && mode != "split")
        {
            var preset = (AppServices.Settings.PlayerNameReplacementPreset ?? "hero").Trim().ToLowerInvariant();
            if (preset != "title" && AppServices.Settings.PlayerNameAppendRealm && !string.IsNullOrWhiteSpace(_currentPlayerRealm))
                replacement = $"{replacement} of {_currentPlayerRealm}";
        }

        if (string.Equals(replacement, _currentPlayerName, StringComparison.Ordinal))
            return text;

        var escaped = Regex.Escape(_currentPlayerName);
        var pattern = $@"(?<![\p{{L}}\p{{N}}_'-]){escaped}(?![\p{{L}}\p{{N}}_'-])";
        return Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static (string name, string realm) SplitNameAndRealm(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return (string.Empty, string.Empty);
        var hyphen = value.IndexOf('-');
        return hyphen > 0 ? (value[..hyphen].Trim(), value[(hyphen + 1)..].Trim()) : (value.Trim(), string.Empty);
    }

    // ── Text decoding and cleaning ────────────────────────────────────────────

    private static string DecodeAndClean(string[] subs)
    {
        // var totalBytes = 0;
        // var decodedChunks = new byte[subs.Length][];
        //
        // for (int i = 0; i < subs.Length; i++)
        // {
        //     var b64 = subs[i];
        //     var bytes = Convert.FromBase64String(b64);
        //     decodedChunks[i] = bytes;
        //     totalBytes += bytes.Length;
        //
        //     System.Diagnostics.Debug.WriteLine(
        //         $"[Assembler] sub {i}/{subs.Length - 1} b64len={b64.Length} " +
        //         $"bytelen={bytes.Length}");
        // }
        //
        // var allBytes = new byte[totalBytes];
        // var offset = 0;
        // for (int i = 0; i < decodedChunks.Length; i++)
        // {
        //     var bytes = decodedChunks[i];
        //     Buffer.BlockCopy(bytes, 0, allBytes, offset, bytes.Length);
        //     offset += bytes.Length;
        // }

        //var text = Encoding.UTF8.GetString(allBytes);

        //System.Diagnostics.Debug.WriteLine($"[Assembler] final bytelen={allBytes.Length} text='{text}'");
        
        StringBuilder sb = new StringBuilder();
        foreach (var s in subs)
        {
            if (!string.IsNullOrEmpty(s))
              sb.Append(s);
        }
        
        var result = "";
        
        if (sb.Length > 0)
            result = sb.ToString();

        return result;
    }
}