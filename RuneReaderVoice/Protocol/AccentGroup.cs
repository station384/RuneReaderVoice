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

// AccentGroup.cs
// Maps the RACE byte from the QR protocol to an accent group.
// Each group (except Narrator) has Male + Female voice slots.
// Every player race has its own group for independent voice and DSP assignment.
// User headcanon is fully supported — races that share a lore accent still get
// separate slots so they can diverge if desired. Defaults match lore expectations.

using System;
using System.Collections.Generic;

namespace RuneReaderVoice.Protocol;

public enum AccentGroup
{
    // ── Core Alliance ──────────────────────────────────────────────────────────
    Human,              // Human (1)
    NightElf,           // Night Elf (4)
    Dwarf,              // Dwarf (3)
    DarkIronDwarf,      // Dark Iron Dwarf (30)
    Gnome,              // Gnome (7)
    Mechagnome,         // Mechagnome (37)
    Draenei,            // Draenei (11)
    LightforgedDraenei, // Lightforged Draenei (28)
    Worgen,             // Worgen (22)
    KulTiran,           // Kul Tiran (32)
    BloodElf,           // Blood Elf (10)
    VoidElf,            // Void Elf (29)

    // ── Core Horde ────────────────────────────────────────────────────────────
    Orc,                // Orc (2)
    MagharOrc,          // Mag'har Orc (36)
    Undead,             // Undead / Forsaken (5)
    Tauren,             // Tauren (6)
    HighmountainTauren, // Highmountain Tauren (27)
    Troll,              // Troll (8)
    ZandalariTroll,     // Zandalari Troll (31)
    Goblin,             // Goblin (9)
    Nightborne,         // Nightborne (24)
    Vulpera,            // Vulpera (35)

    // ── Neutral / Cross-faction ───────────────────────────────────────────────
    Pandaren,           // Pandaren (13)
    Earthen,            // Earthen (TWW allied race — verify raceID in-game)
    Haranir,            // Haranir (Midnight allied race — verify raceID in-game)
    Dracthyr,           // Dracthyr (Dragonflight — verify raceID in-game)

    // ── Creature types (non-playable NPC groups) ──────────────────────────────
    Dragonkin,          // 0x52 — generic dragonkin NPCs
    Elemental,          // 0x55
    Giant,              // 0x56
    Mechanical,         // 0x57 — non-Gnome/Mechagnome mechanical NPCs

    // ── Fallback ──────────────────────────────────────────────────────────────
    Narrator,           // RACE=0x00, FLAG_NARRATOR set, unmapped, or unknown gender
}

/// <summary>
/// Identifies a specific voice slot: accent group + gender.
/// Narrator group ignores gender and always maps to a single slot.
/// </summary>
public readonly record struct VoiceSlot(AccentGroup Group, Gender Gender)
{
    public static readonly VoiceSlot Narrator = new(AccentGroup.Narrator, Gender.Unknown);

    public override string ToString() =>
        Group == AccentGroup.Narrator ? "Narrator" : $"{Group}/{Gender}";

    /// <summary>
    /// Parses a VoiceSlot from its ToString() representation.
    /// Valid forms: "Narrator" or "AccentGroup/Gender" (e.g. "Scottish/Male").
    /// </summary>
    public static bool TryParse(string s, out VoiceSlot slot)
    {
        if (s == "Narrator")
        {
            slot = Narrator;
            return true;
        }

        var parts = s.Split('/');
        if (parts.Length == 2
            && Enum.TryParse<AccentGroup>(parts[0], out var group)
            && Enum.TryParse<Gender>(parts[1], out var gender))
        {
            slot = new VoiceSlot(group, gender);
            return true;
        }

        slot = default;
        return false;
    }
}

public enum Gender { Unknown = 0, Male = 1, Female = 2 }

/// <summary>
/// Authoritative RACE byte → AccentGroup mapping.
/// Run /rrv race in-game to verify all IDs, especially allied races added in TWW and Midnight.
/// </summary>
public static class RaceAccentMapping
{
    // Player race IDs (from UnitRace() raceID).
    // NOTE: Allied race IDs must be verified in-game for each expansion.
    //       Placeholders marked with (?) should be confirmed with /rrv race.
    private static readonly Dictionary<int, AccentGroup> PlayerRaceMap = new()
    {
        { 1,  AccentGroup.Human              },  // Human
        { 2,  AccentGroup.Orc               },  // Orc
        { 3,  AccentGroup.Dwarf             },  // Dwarf
        { 4,  AccentGroup.NightElf          },  // Night Elf
        { 5,  AccentGroup.Undead            },  // Undead / Forsaken
        { 6,  AccentGroup.Tauren            },  // Tauren
        { 7,  AccentGroup.Gnome             },  // Gnome
        { 8,  AccentGroup.Troll             },  // Troll
        { 9,  AccentGroup.Goblin            },  // Goblin
        { 10, AccentGroup.BloodElf          },  // Blood Elf
        { 11, AccentGroup.Draenei           },  // Draenei
        { 13, AccentGroup.Pandaren          },  // Pandaren (Alliance/Horde share same raceID)
        { 22, AccentGroup.Worgen            },  // Worgen
        { 24, AccentGroup.Nightborne        },  // Nightborne (?) verify
        { 25, AccentGroup.HighmountainTauren},  // Highmountain Tauren (?) verify
        { 26, AccentGroup.LightforgedDraenei},  // Lightforged Draenei (?) verify
        { 27, AccentGroup.HighmountainTauren},  // Highmountain Tauren (verify — may be 25 or 27)
        { 28, AccentGroup.LightforgedDraenei},  // Lightforged Draenei (verify — may be 26 or 28)
        { 29, AccentGroup.VoidElf           },  // Void Elf
        { 30, AccentGroup.DarkIronDwarf     },  // Dark Iron Dwarf
        { 31, AccentGroup.ZandalariTroll    },  // Zandalari Troll
        { 32, AccentGroup.KulTiran          },  // Kul Tiran
        { 34, AccentGroup.Dracthyr          },  // Dracthyr (?) verify
        { 35, AccentGroup.Vulpera           },  // Vulpera
        { 36, AccentGroup.MagharOrc         },  // Mag'har Orc
        { 37, AccentGroup.Mechagnome        },  // Mechagnome
        { 52, AccentGroup.Earthen           },  // Earthen (TWW — placeholder, verify in-game)
        { 70, AccentGroup.Haranir           },  // Haranir (Midnight — placeholder, verify in-game)
    };

    // Creature type IDs (from UnitCreatureType(), mapped to 0x50–0x58 range by the addon).
    private static readonly Dictionary<int, AccentGroup> CreatureTypeMap = new()
    {
        { 0x50, AccentGroup.Human       },  // Humanoid (non-playable) — neutral fallback
        { 0x51, AccentGroup.Narrator    },  // Beast
        { 0x52, AccentGroup.Dragonkin   },  // Dragonkin
        { 0x53, AccentGroup.Undead      },  // Undead (non-Forsaken)
        { 0x54, AccentGroup.Narrator    },  // Demon
        { 0x55, AccentGroup.Elemental   },  // Elemental
        { 0x56, AccentGroup.Giant       },  // Giant
        { 0x57, AccentGroup.Mechanical  },  // Mechanical
        { 0x58, AccentGroup.Narrator    },  // Aberration
    };

    // ── Inverted maps (AccentGroup → raceId) — used by UI dropdowns ──────────────

    /// <summary>
    /// Maps each player-race AccentGroup to its canonical raceId.
    /// When multiple raceIds map to the same group the lowest id is kept.
    /// </summary>
    public static IReadOnlyDictionary<AccentGroup, int> PlayerRaceIds { get; } =
        BuildInverse(PlayerRaceMap);

    /// <summary>
    /// Maps each creature-type AccentGroup to its creature-type byte (0x50–0x58).
    /// </summary>
    public static IReadOnlyDictionary<AccentGroup, int> CreatureTypeIds { get; } =
        BuildInverse(CreatureTypeMap);

    private static System.Collections.Generic.Dictionary<AccentGroup, int>
        BuildInverse(Dictionary<int, AccentGroup> map)
    {
        var inv = new System.Collections.Generic.Dictionary<AccentGroup, int>();
        foreach (var (id, group) in map)
        {
            if (!inv.TryGetValue(group, out int existing) || id < existing)
                inv[group] = id;
        }
        return inv;
    }

    /// <summary>
    /// Maps a RACE byte and FLAGS to a VoiceSlot.
    /// FLAG_NARRATOR always returns VoiceSlot.Narrator regardless of race or gender.
    /// </summary>
    public static VoiceSlot Resolve(int raceByte, int flags, bool isMale, bool isFemale)
    {
        if ((flags & RvFlags.FlagNarrator) != 0)
            return VoiceSlot.Narrator;

        var group = ResolveGroup(raceByte);

        if (group == AccentGroup.Narrator)
            return VoiceSlot.Narrator;

        var gender = isFemale ? Gender.Female : isMale ? Gender.Male : Gender.Unknown;
        if (gender == Gender.Unknown)
            return VoiceSlot.Narrator;

        return new VoiceSlot(group, gender);
    }

    private static AccentGroup ResolveGroup(int raceByte)
    {
        if (raceByte == 0) return AccentGroup.Narrator;

        if (raceByte is >= 0x01 and <= 0x7F)
            return PlayerRaceMap.TryGetValue(raceByte, out var g) ? g : AccentGroup.Human;

        if (raceByte is >= 0x50 and <= 0x58)
            return CreatureTypeMap.TryGetValue(raceByte, out var g) ? g : AccentGroup.Narrator;

        return AccentGroup.Narrator;
    }
}
