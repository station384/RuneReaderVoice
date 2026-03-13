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
// once per fully-assembled segment.
//
// Protocol reality (from payload.lua):
//   - One dialog = one DialogID, but N independent segments (narrator splits).
//   - Each segment has its own IDX (0-based) and TOTAL (chunk count for that segment).
//   - Segments are streamed sequentially and cycle continuously until dialog closes.
//   - The reader may catch segment 2 before segment 1 — all must be tracked.
//
// Segment identity within a dialog:
//   FLAGS + RACE uniquely identify a segment's voice slot. Combined with TOTAL
//   and the first chunk's payload they form a stable key that survives re-loops.
//
// Re-loop handling:
//   When a completed segment's IDX=0 chunk arrives again, it is ignored.
//   _completedKeys tracks which segments have already fired.
//
// Chunk ordering:
//   Non-zero chunks that arrive before IDX=0 are stashed in _earlyChunks and
//   replayed when IDX=0 arrives to establish the segment key.
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
    public string    Text         { get; init; } = string.Empty;
    public VoiceSlot Slot         { get; init; }
    public int       DialogId     { get; init; }
    public int       SegmentIndex { get; init; }
    public int       NpcId        { get; init; }
}

public sealed class TtsSessionAssembler
{
    // ── Events ───────────────────────────────────────────────────────────────

    public event Action<AssembledSegment>? OnSegmentComplete;
    public event Action<int>?              OnSessionReset;

    // ── Per-segment accumulator ───────────────────────────────────────────────

    private sealed class SegmentAccumulator
    {
        public string?[] Slots         { get; }
        public int       SlotsReceived { get; set; }
        public VoiceSlot Slot          { get; init; }
        public int       NpcId         { get; init; }
        /// <summary>
        /// Order in which this segment was started (IDX=0 received), not completed.
        /// Used as the SegmentIndex so the playback coordinator can reorder segments
        /// that complete out of sequence (e.g. a short narrator segment finishing
        /// before a longer NPC segment that started earlier).
        /// </summary>
        public int       OrderIndex    { get; init; }

        public SegmentAccumulator(int total, VoiceSlot slot, int npcId, int orderIndex)
        {
            Slots      = new string?[total];
            Slot       = slot;
            NpcId      = npcId;
            OrderIndex = orderIndex;
        }
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private int _currentDialogId = -1;
    private int _nextSegmentIndex;

    // Active accumulators: key = MakeKey(total, flags, race, slot0payload)
    private readonly Dictionary<string, SegmentAccumulator> _segments          = new();
    // Keys of segments that have already fired — re-loops are ignored
    private readonly HashSet<string>                        _completedKeys      = new();
    private readonly HashSet<string>                        _completedUtteranceKeys = new();
    // Early chunks (arrived before IDX=0): key = (flags<<16|race<<8|total)
    private readonly Dictionary<string, List<(int idx, string payload)>> _earlyChunks = new();

    private readonly object _lock = new();

    // ── NPC race override store ───────────────────────────────────────────────
    // Pre-loaded from NpcRaceOverrideDb at startup. Feed() is synchronous so
    // we maintain an in-memory copy here — the DB is the durable backing store.
    // Key = NpcId, Value = RaceId.

    private readonly Dictionary<int, int> _npcRaceStore = new();
    private readonly NpcRaceOverrideDb    _overrideDb;

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
                _npcRaceStore[entry.NpcId] = entry.RaceId;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Feed(RvPacket packet)
    {
        AssembledSegment? completed = null;

        lock (_lock)
        {
            // ── New dialog ────────────────────────────────────────────────────
            if (packet.DialogId != _currentDialogId)
            {
                _currentDialogId  = packet.DialogId;
                _nextSegmentIndex = 0;
                _segments.Clear();
                _completedKeys.Clear();
                _completedUtteranceKeys.Clear();
                _earlyChunks.Clear();
                OnSessionReset?.Invoke(_currentDialogId);
            }

            // ── NPC race override lookup ──────────────────────────────────────
            // Priority: user override (in-memory) > packet race > Narrator fallback
            int effectiveRace = packet.Race;
            if (packet.NpcId != 0)
            {
                if (_npcRaceStore.TryGetValue(packet.NpcId, out var stored))
                    effectiveRace = stored;
                else if (packet.Race != 0)
                    _npcRaceStore[packet.NpcId] = packet.Race;
            }

            if (packet.ChunkIndex == 0)
            {
                var slot = RaceAccentMapping.Resolve(
                    effectiveRace, packet.Flags, packet.IsMale, packet.IsFemale);
                var key  = MakeKey(packet.ChunkTotal, packet.Flags,
                                   effectiveRace, packet.Base64Payload);

                // Already completed — re-loop, ignore
                if (_completedKeys.Contains(key)) return;

                if (!_segments.TryGetValue(key, out var acc))
                {
                    acc = new SegmentAccumulator(packet.ChunkTotal, slot, packet.NpcId,
                                                 _nextSegmentIndex++);
                    _segments[key] = acc;

                    // Replay stashed early chunks for this segment signature
                    var earlyKey = MakeEarlyKey(packet.ChunkTotal, packet.Flags, effectiveRace);
                    if (_earlyChunks.TryGetValue(earlyKey, out var early))
                    {
                        foreach (var (idx, payload) in early)
                        {
                            if (idx < acc.Slots.Length && acc.Slots[idx] == null)
                            {
                                acc.Slots[idx] = payload;
                                acc.SlotsReceived++;
                            }
                        }
                        _earlyChunks.Remove(earlyKey);
                    }
                }

                // Store IDX=0 (idempotent)
                if (acc.Slots[0] == null)
                {
                    acc.Slots[0] = packet.Base64Payload;
                    acc.SlotsReceived++;
                }

                completed = TryComplete(acc, key);
            }
            else
            {
                // Non-zero chunk: find the matching in-progress accumulator
                var acc = _segments.Values.FirstOrDefault(a =>
                    a.Slots.Length == packet.ChunkTotal &&
                    a.Slots[packet.ChunkIndex] == null);

                if (acc != null)
                {
                    acc.Slots[packet.ChunkIndex] = packet.Base64Payload;
                    acc.SlotsReceived++;

                    var key = _segments.First(kv => kv.Value == acc).Key;
                    completed = TryComplete(acc, key);
                }
                else
                {
                    // IDX=0 hasn't arrived yet — stash
                    var earlyKey = MakeEarlyKey(packet.ChunkTotal, packet.Flags, effectiveRace);
                    if (!_earlyChunks.TryGetValue(earlyKey, out var early))
                    {
                        early = new List<(int, string)>();
                        _earlyChunks[earlyKey] = early;
                    }
                    if (early.All(e => e.idx != packet.ChunkIndex))
                        early.Add((packet.ChunkIndex, packet.Base64Payload));
                }
            }
        }

        if (completed != null)
        {
            AppServices.LastSegment = completed;
            OnSegmentComplete?.Invoke(completed);
        }
    }

    /// <summary>
    /// Applies a new NPC race override at runtime (called from the UI after the
    /// user saves an assignment). Updates the in-memory store immediately so the
    /// next dialog using this NpcId picks up the new race without a restart.
    /// The caller is responsible for persisting to the DB via NpcRaceOverrideDb.
    /// </summary>
    public void ApplyRaceOverride(int npcId, int raceId)
    {
        if (npcId == 0) return;
        lock (_lock)
        {
            _npcRaceStore[npcId] = raceId;
        }
    }

    /// <summary>
    /// Removes an NPC race override from the in-memory store (called from the UI
    /// after the user deletes a local override).
    /// The caller is responsible for deleting from the DB via NpcRaceOverrideDb.
    /// </summary>
    public void RemoveRaceOverride(int npcId)
    {
        lock (_lock)
        {
            _npcRaceStore.Remove(npcId);
        }
    }

    public void SignalSourceGone()
    {
        // No-op: playback continues; same dialog may reappear.
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string MakeUtteranceKey(int dialogId, VoiceSlot slot, int npcId, string text)
        => $"{dialogId}|{slot}|{npcId}|{text.Trim()}";

    private AssembledSegment? TryComplete(SegmentAccumulator acc, string key)
    {
        if (acc.SlotsReceived != acc.Slots.Length) return null;
        if (acc.Slots.Any(s => s == null)) return null;
        if (_completedKeys.Contains(key)) return null;

        var text = DecodeAndClean(acc.Slots!);
        if (string.IsNullOrWhiteSpace(text)) return null;

        var utteranceKey = MakeUtteranceKey(_currentDialogId, acc.Slot, acc.NpcId, text);
        if (_completedUtteranceKeys.Contains(utteranceKey)) return null;

        _completedKeys.Add(key);
        _completedUtteranceKeys.Add(utteranceKey);

        return new AssembledSegment
        {
            Text         = text,
            Slot         = acc.Slot,
            DialogId     = _currentDialogId,
            SegmentIndex = acc.OrderIndex,
            NpcId        = acc.NpcId,
        };
    }

    private static string MakeKey(int total, int flags, int race, string slot0)
        => $"{total}|{flags}|{race}|{slot0}";

    private static string MakeEarlyKey(int total, int flags, int race)
        => $"{total}|{flags}|{race}";

    // ── Text decoding and cleaning ────────────────────────────────────────────

    private static string DecodeAndClean(string[] slots)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < slots.Length; i++)
        {
            var b64   = slots[i];
            var bytes = Convert.FromBase64String(b64);
            var raw   = Encoding.UTF8.GetString(bytes).Trim();

            System.Diagnostics.Debug.WriteLine(
                $"[Assembler] slot {i}/{slots.Length - 1} b64len={b64.Length} " +
                $"bytelen={bytes.Length} text='{raw}'");

            if (sb.Length > 0) sb.Append(' ');
            sb.Append(raw);
        }

        var text = sb.ToString().Trim();

        System.Diagnostics.Debug.WriteLine($"[Assembler] final text='{text}'");

        return text;
    }
}
