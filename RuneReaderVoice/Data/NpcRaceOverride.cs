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
namespace RuneReaderVoice.Data;
// NpcRaceOverride.cs
// Model for a user-defined (or crowd-sourced) NPC → race mapping.
//
// Source hierarchy (highest wins):
//   Local/Submitted > CrowdSourced > Confirmed (server-verified, read-only from client)
// Submitted means user-authored and successfully pushed to server. Next edit returns it to Local.
//
// Confidence is unused locally (always null). Reserved for server-side
// vote aggregation in the crowd-source path.
public enum NpcOverrideSource
{
    Local       = 0,   // User-entered on this machine. Full CRUD. Eligible for contribution.
    CrowdSourced = 1,  // Received from server aggregation. Read-only; shadowed by Local/Submitted.
    Confirmed   = 2,   // Hand-verified by server admin. Read-only; shadowed by Local/Submitted.
    Submitted   = 3,   // User-entered here and already pushed to server. Full CRUD; not pushed again unless edited.
}

public enum NpcGenderOverride
{
    Auto   = 0,
    Male   = 1,
    Female = 2,
}

public sealed class NpcRaceOverride
{
    /// <summary>NPC ID from the RV packet NPC field (unit GUID segment 6).</summary>
    public int NpcId { get; init; }

    /// <summary>
    /// Legacy race id kept only for compatibility with older sync/export paths.
    /// Runtime override resolution now prefers CatalogId.
    /// </summary>
    public int RaceId { get; set; }

    /// <summary>
    /// Catalog row id selected for this NPC override. This is the authoritative
    /// runtime identity used to derive a slot at playback time.
    /// </summary>
    public string CatalogId { get; set; } = string.Empty;


    /// <summary>Optional user-friendly label, e.g. "Rexxar" or "Thrall".</summary>
    public string? Notes { get; set; }

    /// <summary>NPC display name captured from QR/Code39 side-channel. Used for UI and future fallback matching.</summary>
    public string? NpcName { get; set; }

    /// <summary>
    /// When set, overrides the sample used for voice-matching synthesis for this NPC.
    /// The race slot's DSP profile still applies — only the reference sample is replaced.
    /// Null means use the race slot's default sample selection.
    /// </summary>
    public string? BespokeSampleId { get; set; } = null;

    /// <summary>
    /// When true, automatic NPC-name bespoke sample matching is disabled for this NPC.
    /// Null BespokeSampleId then means use race/default voice only, not auto-match.
    /// </summary>
    public bool DisableBespokeAutoMatch { get; set; } = false;

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

    /// <summary>
    /// When true, the NPC's numeric ID is passed to the remote TTS server as the
    /// synthesis seed. This is intentionally per-NPC rather than global so a voice
    /// can keep its normal profile seed behavior everywhere else while gaining stable
    /// NPC-specific variance for this override.
    /// </summary>
    public bool UseNpcIdAsSeed { get; set; } = false;

    /// <summary>Optional per-NPC gender override. Auto preserves Blizzard/QR-detected gender.</summary>
    public NpcGenderOverride GenderOverride { get; set; } = NpcGenderOverride.Auto;

    /// <summary>Where this entry came from.</summary>
    public NpcOverrideSource Source { get; set; } = NpcOverrideSource.Local;

    /// <summary>
    /// Server-assigned confidence score (null for local entries).
    /// Higher = more users agreed on this mapping.
    /// </summary>
    public int? Confidence { get; set; }

    /// <summary>Unix timestamp of last update. Used for delta sync polling.</summary>
    public double UpdatedAt { get; set; } = 0.0;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>True if this entry was received from the server and must not be client-deleted.</summary>
    public bool IsReadOnly => Source is NpcOverrideSource.CrowdSourced or NpcOverrideSource.Confirmed;
}