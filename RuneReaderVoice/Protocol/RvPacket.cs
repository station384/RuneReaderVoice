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

// RvPacket.cs
// Parsed representation of a RuneReaderVoice QR payload header (protocol v04).
//
// Header layout (22 ASCII chars):
//   MAGIC(2) VER(2) DIALOG(4) IDX(2) TOTAL(2) FLAGS(2) RACE(2) NPC(6)
//
// Magic bytes "RV" identify this as a TTS packet.
// All multi-char fields are uppercase hex strings.

using System;

namespace RuneReaderVoice.Protocol;

public sealed class RvPacket
{
    // ── Header fields ────────────────────────────────────────────────────────

    /// <summary>Protocol version (e.g. 4 for v04).</summary>
    public int Version { get; init; }

    /// <summary>
    /// Dialog block ID. Increments once per NPC interaction.
    /// A change signals a new NPC — discard any in-progress assembly.
    /// </summary>
    public int DialogId { get; init; }

    /// <summary>0-based chunk index within this segment.</summary>
    public int ChunkIndex { get; init; }

    /// <summary>Total chunk count for this segment.</summary>
    public int ChunkTotal { get; init; }

    /// <summary>Raw FLAGS bitmask. See RvFlags for named constants.</summary>
    public int Flags { get; init; }

    /// <summary>
    /// NPC race or creature-type byte.
    /// 0x01–0x3F = player race IDs; 0x50–0x58 = creature types; 0x00 = unknown.
    /// </summary>
    public int Race { get; init; }

    /// <summary>
    /// NPC ID extracted from unit GUID segment 6 (hex → int).
    /// Zero for non-creature units, books, and preview packets.
    /// </summary>
    public int NpcId { get; init; }

    /// <summary>
    /// Base64-encoded text chunk (trailing spaces are padding — trim after decode).
    /// </summary>
    public string Base64Payload { get; init; } = string.Empty;

    // ── Derived convenience properties ───────────────────────────────────────

    public bool IsNarrator => (Flags & RvFlags.FlagNarrator) != 0;
    public bool IsPreview  => (Flags & RvFlags.FlagPreview)  != 0;
    public bool IsMale     => (Flags & RvFlags.GenderMask)   == RvFlags.GenderMale;
    public bool IsFemale   => (Flags & RvFlags.GenderMask)   == RvFlags.GenderFemale;
    public bool IsLastChunk => ChunkIndex == ChunkTotal - 1;

    // ── Parser ───────────────────────────────────────────────────────────────

    private const string Magic = "RV";
    private const int HeaderLength = 22;

    /// <summary>
    /// Attempts to parse a raw QR string into an RvPacket.
    /// Returns null if the string is not a valid RV packet.
    /// </summary>
    public static RvPacket? TryParse(string raw)
    {
        if (raw.Length < HeaderLength) return null;
        if (!raw.StartsWith(Magic, StringComparison.Ordinal)) return null;

        try
        {
            int ver     = ParseHex(raw, 2, 2);
            int dialog  = ParseHex(raw, 4, 4);
            int idx     = ParseHex(raw, 8, 2);
            int total   = ParseHex(raw, 10, 2);
            int flags   = ParseHex(raw, 12, 2);
            int race    = ParseHex(raw, 14, 2);
            int npcId   = ParseHex(raw, 16, 6);
            string b64  = raw[HeaderLength..];

            if (total == 0) return null; // malformed

            return new RvPacket
            {
                Version      = ver,
                DialogId     = dialog,
                ChunkIndex   = idx,
                ChunkTotal   = total,
                Flags        = flags,
                Race         = race,
                NpcId        = npcId,
                Base64Payload = b64,
            };
        }
        catch
        {
            return null;
        }
    }

    private static int ParseHex(string s, int start, int length)
        => Convert.ToInt32(s.Substring(start, length), 16);
}

/// <summary>Named bitmask constants for the FLAGS field.</summary>
public static class RvFlags
{
    public const int FlagNarrator  = 0x01;
    public const int GenderMale    = 0x02;
    public const int GenderFemale  = 0x04;
    public const int GenderMask    = 0x06;
    public const int FlagPreview   = 0x08;
}
