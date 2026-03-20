// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

// NpcRaceOverride.cs
// Model for a user-defined (or crowd-sourced) NPC → race mapping.
//
// Source hierarchy (highest wins):
//   Local > CrowdSourced > Confirmed (server-verified, read-only from client)
//
// Confidence is unused locally (always null). Reserved for server-side
// vote aggregation in the crowd-source path (Phase 5+).

using System;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.Data;

public enum NpcOverrideSource
{
    Local       = 0,   // User-entered on this machine. Full CRUD.
    CrowdSourced = 1,  // Received from server aggregation. Read-only; shadowed by Local.
    Confirmed   = 2,   // Hand-verified by server admin. Read-only; shadowed by Local.
}

public sealed class NpcRaceOverride
{
    /// <summary>NPC ID from the RV packet NPC field (unit GUID segment 6).</summary>
    public int NpcId { get; init; }

    /// <summary>
    /// Race ID that maps into RaceAccentMapping.
    /// Use values from the player race map (1–37) or creature type map (0x50–0x58).
    /// </summary>
    public int RaceId { get; set; }

    /// <summary>
    /// Accent group derived from RaceId. Stored for quick lookup —
    /// does not need to be persisted (recomputed on load).
    /// </summary>
    public AccentGroup AccentGroup { get; set; }

    /// <summary>Optional user-friendly label, e.g. "Rexxar" or "Thrall".</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// When set, overrides the sample used for voice-matching synthesis for this NPC.
    /// The race slot's DSP profile still applies — only the reference sample is replaced.
    /// Null means use the race slot's default sample selection.
    /// </summary>
    public string? BespokeSampleId { get; set; } = null;

    /// <summary>
    /// Overrides the exaggeration parameter for this NPC's synthesis.
    /// Null means inherit from the race slot's VoiceProfile.
    /// </summary>
    public float? BespokeExaggeration { get; set; } = null;

    /// <summary>
    /// Overrides the cfg_weight parameter for this NPC's synthesis.
    /// Null means inherit from the race slot's VoiceProfile.
    /// </summary>
    public float? BespokeCfgWeight { get; set; } = null;

    /// <summary>Where this entry came from.</summary>
    public NpcOverrideSource Source { get; set; } = NpcOverrideSource.Local;

    /// <summary>
    /// Server-assigned confidence score (null for local entries).
    /// Higher = more users agreed on this mapping.
    /// </summary>
    public int? Confidence { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>True if this entry was received from the server and must not be client-deleted.</summary>
    public bool IsReadOnly => Source != NpcOverrideSource.Local;
}
