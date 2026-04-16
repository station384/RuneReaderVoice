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
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Data;
using RuneReaderVoice.Protocol;

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
    public string?    PlayerName        { get; init; } = null;
    public string?    PlayerRealm       { get; init; } = null;
    public string?    PlayerClass       { get; init; } = null;

    // Experimental remote batch priming metadata for player-name split testing.
    public string? BatchId { get; init; } = null;
    public string? BatchSegmentId { get; init; } = null;
    public string? PrimeFromBatchSegmentId { get; init; } = null;
    public IReadOnlyList<BatchSegmentPlan>? BatchSegments { get; init; } = null;

    // Bespoke voice override resolved at assembly time from NpcRaceOverride.
    // Null means use the race slot defaults.
    public string?   BespokeSampleId    { get; init; } = null;
    public float?    BespokeExaggeration { get; init; } = null;
    public float?    BespokeCfgWeight   { get; init; } = null;
    public bool      UseNpcIdAsSeed    { get; init; } = false;
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
                OnSessionReset?.Invoke(_currentDialogId);
                System.Diagnostics.Debug.WriteLine(
                    $"[Assembler] New dialog 0x{packet.DialogId:X4} seqTotal={packet.SeqTotal}");
            }

            // ── Runtime routing baseline ─────────────────────────────────────
            // DB is source of truth. Feed() only chooses immediate narrator/default
            // fallback routing. Final NPC override resolution happens when the
            // segment completes and is read directly from SQLite by NPCID.
            int effectiveRace = 0;
            var resolvedSlot = packet.IsFemale ? VoiceSlot.FemaleNarrator : VoiceSlot.MaleNarrator;

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
            var audibleCount = toFire.Count;
            foreach (var seg in toFire)
            {
                var emitted = new AssembledSegment
                {
                    Text = seg.Text,
                    Slot = seg.Slot,
                    DialogId = seg.DialogId,
                    SegmentIndex = seg.SegmentIndex,
                    DialogSegmentCount = audibleCount,
                    NpcId = seg.NpcId,
                    PlayerName = seg.PlayerName,
                    PlayerRealm = seg.PlayerRealm,
                    PlayerClass = seg.PlayerClass,
                    BatchId = seg.BatchId,
                    BatchSegmentId = seg.BatchSegmentId,
                    PrimeFromBatchSegmentId = seg.PrimeFromBatchSegmentId,
                    BatchSegments = seg.BatchSegments,
                    BespokeSampleId = seg.BespokeSampleId,
                    BespokeExaggeration = seg.BespokeExaggeration,
                    BespokeCfgWeight = seg.BespokeCfgWeight,
                    UseNpcIdAsSeed = seg.UseNpcIdAsSeed,
                };
                System.Diagnostics.Debug.WriteLine(
                    $"[Assembler] Firing seg={emitted.SegmentIndex} slot={emitted.Slot} npc={emitted.NpcId}" +
                    $" bespoke={emitted.BespokeSampleId ?? "none"} text='{emitted.Text.Substring(0, Math.Min(60, emitted.Text.Length))}'");
                AppServices.LastSegment = emitted;
                OnSegmentComplete?.Invoke(emitted);
            }
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
        text = ExtractAndApplyDialogMetadata(text);

        var utteranceKey = MakeUtteranceKey(_currentDialogId, acc.Slot, acc.NpcId, text, acc.SeqIndex);
        if (_completedUtteranceKeys.Contains(utteranceKey)) return;

        _completedKeys.Add(key);
        _completedUtteranceKeys.Add(utteranceKey);

        var slot = acc.Slot;
        string? bespokeSampleId = null;
        float? bespokeExaggeration = null;
        float? bespokeCfgWeight = null;
        var useNpcIdAsSeed = false;

        if (!acc.IsNarrator && acc.NpcId != 0)
        {
            var entry = _overrideDb.GetOverrideAsync(acc.NpcId).GetAwaiter().GetResult();
            if (entry != null)
            {
                var g = acc.IsMale ? Gender.Male : acc.IsFemale ? Gender.Female : Gender.Unknown;
                var catalogId = !string.IsNullOrWhiteSpace(entry.CatalogId)
                    ? entry.CatalogId
                    : NpcRaceOverrideDb.LegacyRaceIdToCatalogId(entry.RaceId);

                if (!string.IsNullOrWhiteSpace(catalogId))
                {
                    slot = AppServices.NpcPeopleCatalog?.ResolveCatalogSlot(catalogId, g)
                           ?? new VoiceSlot(catalogId, g);
                }

                bespokeSampleId = entry.BespokeSampleId;
                bespokeExaggeration = entry.BespokeExaggeration;
                bespokeCfgWeight = entry.BespokeCfgWeight;
                useNpcIdAsSeed = entry.UseNpcIdAsSeed;
            }
        }

        _completedSegments[acc.SeqIndex] = new AssembledSegment
        {
            Text                = text,
            Slot                = slot,
            DialogId            = _currentDialogId,
            SegmentIndex        = acc.SeqIndex,
            NpcId               = acc.NpcId,
            PlayerName          = string.IsNullOrWhiteSpace(_currentPlayerName) ? null : _currentPlayerName,
            PlayerRealm         = string.IsNullOrWhiteSpace(_currentPlayerRealm) ? null : _currentPlayerRealm,
            PlayerClass         = string.IsNullOrWhiteSpace(_currentPlayerClass) ? null : _currentPlayerClass,
            BespokeSampleId     = bespokeSampleId,
            BespokeExaggeration = bespokeExaggeration,
            BespokeCfgWeight    = bespokeCfgWeight,
            UseNpcIdAsSeed      = useNpcIdAsSeed,
        };
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
    }

    private string ApplyPlayerReplacement(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(_currentPlayerName))
            return text;

        var mode = (AppServices.Settings.PlayerNameMode ?? "generic").Trim().ToLowerInvariant();

        var replacement = _currentPlayerName;
        if (mode != "actual" && mode != "split")
        {
            var preset = (AppServices.Settings.PlayerNameReplacementPreset ?? "hero").Trim().ToLowerInvariant();
            replacement = preset switch
            {
                "champion" => "Champion",
                "class" => string.IsNullOrWhiteSpace(_currentPlayerClass) ? "Hero" : _currentPlayerClass,
                _ => "Hero",
            };
        }

        if (AppServices.Settings.PlayerNameAppendRealm && !string.IsNullOrWhiteSpace(_currentPlayerRealm))
            replacement = $"{replacement} of {_currentPlayerRealm}";

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