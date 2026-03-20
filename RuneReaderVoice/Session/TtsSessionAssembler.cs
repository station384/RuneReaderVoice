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
//   3. Falls through to RaceAccentMapping which returns Narrator on unknown values

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Data;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.Session;

public sealed class AssembledSegment
{
    public string    Text              { get; init; } = string.Empty;
    public VoiceSlot Slot              { get; init; }
    public int       DialogId          { get; init; }
    public int       SegmentIndex      { get; init; }
    public int       NpcId             { get; init; }

    // Bespoke voice override resolved at assembly time from NpcRaceOverride.
    // Null means use the race slot defaults.
    public string?   BespokeSampleId    { get; init; } = null;
    public float?    BespokeExaggeration { get; init; } = null;
    public float?    BespokeCfgWeight   { get; init; } = null;
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

        public SegmentAccumulator(int subTotal, VoiceSlot slot, int npcId, int seqIndex)
        {
            Subs     = new string?[subTotal];
            Slot     = slot;
            NpcId    = npcId;
            SeqIndex = seqIndex;
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

    // ── NPC voice override store ──────────────────────────────────────────────
    // Pre-loaded from NpcRaceOverrideDb at startup. Feed() is synchronous so
    // we maintain an in-memory copy here — the DB is the durable backing store.

    private record struct NpcVoiceOverride(
        int     RaceId,
        string? BespokeSampleId,
        float?  BespokeExaggeration,
        float?  BespokeCfgWeight);

    private readonly Dictionary<int, NpcVoiceOverride> _npcVoiceStore = new();
    private readonly NpcRaceOverrideDb                 _overrideDb;

    // ── Construction ──────────────────────────────────────────────────────────

    public TtsSessionAssembler(NpcRaceOverrideDb overrideDb)
    {
        _overrideDb = overrideDb;
    }

    /// <summary>
    /// Pre-loads all local overrides from the DB into the in-memory race store.
    /// Call once at startup after the DB is initialized.
    /// </summary>
    public async Task LoadOverridesAsync(CancellationToken ct = default)
    {
        var all = await _overrideDb.GetAllAsync(ct);
        lock (_lock)
        {
            foreach (var entry in all)
                _npcVoiceStore[entry.NpcId] = new NpcVoiceOverride(
                    entry.RaceId,
                    entry.BespokeSampleId,
                    entry.BespokeExaggeration,
                    entry.BespokeCfgWeight);
        }
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
                OnSessionReset?.Invoke(_currentDialogId);
                System.Diagnostics.Debug.WriteLine(
                    $"[Assembler] New dialog 0x{packet.DialogId:X4} seqTotal={packet.SeqTotal}");
            }

            // ── NPC race override lookup ──────────────────────────────────────
            // Priority: user override (in-memory) > packet race > Narrator fallback
            int effectiveRace = packet.Race;
            if (packet.NpcId != 0)
            {
                if (_npcVoiceStore.TryGetValue(packet.NpcId, out var stored))
                    effectiveRace = stored.RaceId;
                else if (packet.Race != 0)
                    _npcVoiceStore[packet.NpcId] = new NpcVoiceOverride(packet.Race, null, null, null);
            }

            if (packet.SubIndex == 0)
            {
                var slot = RaceAccentMapping.Resolve(
                    effectiveRace, packet.Flags, packet.IsMale, packet.IsFemale);
                var key  = MakeKey(packet.SubTotal, packet.Flags,
                                   effectiveRace, packet.Base64Payload);

                // Already completed — re-loop, ignore
                if (_completedKeys.Contains(key)) return;

                if (!_segments.TryGetValue(key, out var acc))
                {
                    acc = new SegmentAccumulator(packet.SubTotal, slot, packet.NpcId,
                                                 packet.SeqIndex);
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
                    toFire.Add(_completedSegments[i]);
                _completedSegments.Clear();
                System.Diagnostics.Debug.WriteLine(
                    $"[Assembler] Dialog 0x{_currentDialogId:X4} complete — firing {toFire.Count} segment(s)");
            }
        }

        if (toFire != null)
        {
            foreach (var seg in toFire)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Assembler] Firing seg={seg.SegmentIndex} slot={seg.Slot} npc={seg.NpcId}" +
                    $" bespoke={seg.BespokeSampleId ?? "none"} text='{seg.Text.Substring(0, Math.Min(60, seg.Text.Length))}'");
                AppServices.LastSegment = seg;
                OnSegmentComplete?.Invoke(seg);
            }
        }
    }

    /// <summary>
    /// Applies a new NPC voice override at runtime (called from the UI after the
    /// user saves an assignment). Updates the in-memory store immediately so the
    /// next dialog using this NpcId picks up the new settings without a restart.
    /// The caller is responsible for persisting to the DB via NpcRaceOverrideDb.
    /// </summary>
    public void ApplyRaceOverride(int npcId, int raceId,
        string? bespokeSampleId = null,
        float? bespokeExaggeration = null,
        float? bespokeCfgWeight = null)
    {
        if (npcId == 0) return;
        lock (_lock)
        {
            _npcVoiceStore[npcId] = new NpcVoiceOverride(
                raceId, bespokeSampleId, bespokeExaggeration, bespokeCfgWeight);
        }
    }

    /// <summary>
    /// Removes an NPC voice override from the in-memory store (called from the UI
    /// after the user deletes a local override).
    /// The caller is responsible for deleting from the DB via NpcRaceOverrideDb.
    /// </summary>
    public void RemoveRaceOverride(int npcId)
    {
        lock (_lock)
        {
            _npcVoiceStore.Remove(npcId);
        }
    }

    public void SignalSourceGone()
    {
        // No-op: playback continues; same dialog may reappear.
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        if (string.IsNullOrWhiteSpace(text)) return;

        var utteranceKey = MakeUtteranceKey(_currentDialogId, acc.Slot, acc.NpcId, text);
        if (_completedUtteranceKeys.Contains(utteranceKey)) return;

        _completedKeys.Add(key);
        _completedUtteranceKeys.Add(utteranceKey);

        // Resolve bespoke override for this NPC (null if not set)
        _npcVoiceStore.TryGetValue(acc.NpcId, out var voiceOverride);

        _completedSegments[acc.SeqIndex] = new AssembledSegment
        {
            Text                = text,
            Slot                = acc.Slot,
            DialogId            = _currentDialogId,
            SegmentIndex        = acc.SeqIndex,
            NpcId               = acc.NpcId,
            BespokeSampleId     = voiceOverride.BespokeSampleId,
            BespokeExaggeration = voiceOverride.BespokeExaggeration,
            BespokeCfgWeight    = voiceOverride.BespokeCfgWeight,
        };
    }

    private static string MakeUtteranceKey(int dialogId, VoiceSlot slot, int npcId, string text)
        => $"{dialogId}|{slot}|{npcId}|{text.Trim()}";

    private static string MakeKey(int subTotal, int flags, int race, string sub0)
        => $"{subTotal}|{flags}|{race}|{sub0}";

    private static string MakeEarlyKey(int subTotal, int flags, int race, int seqIndex)
        => $"{subTotal}|{flags}|{race}|{seqIndex}";

    // ── Text decoding and cleaning ────────────────────────────────────────────

    private static string DecodeAndClean(string[] subs)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < subs.Length; i++)
        {
            var b64   = subs[i];
            var bytes = Convert.FromBase64String(b64);
            var raw   = Encoding.UTF8.GetString(bytes).Trim();

            System.Diagnostics.Debug.WriteLine(
                $"[Assembler] sub {i}/{subs.Length - 1} b64len={b64.Length} " +
                $"bytelen={bytes.Length} text='{raw}'");

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(raw);
        }

        var text = sb.ToString().Trim();

        System.Diagnostics.Debug.WriteLine($"[Assembler] final text='{text}'");

        return text;
    }
}
