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
// Maps the RACE byte from the QR protocol to one of 13 accent groups.
// Each group (except Narrator) has Male + Female voice slots.
// Total: 13 groups × 2 genders - 1 (Narrator single slot) = 25 + 2 = 27 slots.

using System;
using System.Collections.Generic;

namespace RuneReaderVoice.Protocol;

public enum AccentGroup
{
    NeutralAmerican,    // Human(1), Orc(2), Mag'har Orc(36)
    AmericanRaspy,      // Undead/Forsaken(5)
    Scottish,           // Dwarf(3), Dark Iron Dwarf(30)
    BritishHaughty,     // Blood Elf(10), Void Elf(29)
    BritishRugged,      // Worgen(22), Kul Tiran(32)
    PlayfulSqueaky,     // Gnome(7), Mechagnome(37)
    EasternEuropean,    // Draenei(11), Lightforged Draenei(28)
    Caribbean,          // Troll(8)
    RegalTribal,        // Zandalari Troll(31)
    DeepResonant,       // Tauren(6), Highmountain Tauren(27)
    NewYork,            // Goblin(9)
    EastAsian,          // Pandaren(13)
    French,             // Nightborne(24)  NOTE: verify raceID in-game with /rrv race
    Scrappy,            // Vulpera(35)
    Narrator,           // RACE=0x00, FLAG_NARRATOR set, or any unmapped value
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
/// Run /rrv race in-game to verify all IDs.
/// </summary>
public static class RaceAccentMapping
{
    // Player race IDs (from UnitRace() raceID).
    // NOTE: Allied race IDs should be verified in-game — they shift as Blizzard adds races.
    private static readonly Dictionary<int, AccentGroup> PlayerRaceMap = new()
    {
        { 1,  AccentGroup.NeutralAmerican },   // Human
        { 2,  AccentGroup.NeutralAmerican },   // Orc
        { 3,  AccentGroup.Scottish        },   // Dwarf
        { 4,  AccentGroup.NeutralAmerican },   // Night Elf (no accent group yet → neutral fallback)
        { 5,  AccentGroup.AmericanRaspy   },   // Undead/Forsaken
        { 6,  AccentGroup.DeepResonant    },   // Tauren
        { 7,  AccentGroup.PlayfulSqueaky  },   // Gnome
        { 8,  AccentGroup.Caribbean       },   // Troll
        { 9,  AccentGroup.NewYork         },   // Goblin
        { 10, AccentGroup.BritishHaughty  },   // Blood Elf
        { 11, AccentGroup.EasternEuropean },   // Draenei
        { 13, AccentGroup.EastAsian       },   // Pandaren (NOTE: may differ for Alliance/Horde)
        { 22, AccentGroup.BritishRugged   },   // Worgen
        { 24, AccentGroup.French          },   // Nightborne — verify raceID in-game
        { 25, AccentGroup.NeutralAmerican },   // Highmountain Tauren placeholder — verify
        { 26, AccentGroup.EasternEuropean },   // Lightforged Draenei — verify
        { 27, AccentGroup.DeepResonant    },   // Highmountain Tauren — verify raceID
        { 28, AccentGroup.EasternEuropean },   // Lightforged Draenei — verify raceID
        { 29, AccentGroup.BritishHaughty  },   // Void Elf
        { 30, AccentGroup.Scottish        },   // Dark Iron Dwarf
        { 31, AccentGroup.RegalTribal     },   // Zandalari Troll
        { 32, AccentGroup.BritishRugged   },   // Kul Tiran
        { 35, AccentGroup.Scrappy         },   // Vulpera
        { 36, AccentGroup.NeutralAmerican },   // Mag'har Orc
        { 37, AccentGroup.PlayfulSqueaky  },   // Mechagnome
    };

    // Creature type IDs (from UnitCreatureType(), mapped to 0x50–0x58 range by the addon).
    private static readonly Dictionary<int, AccentGroup> CreatureTypeMap = new()
    {
        { 0x50, AccentGroup.NeutralAmerican }, // Humanoid (non-playable)
        { 0x51, AccentGroup.Narrator        }, // Beast
        { 0x52, AccentGroup.DeepResonant    }, // Dragonkin
        { 0x53, AccentGroup.AmericanRaspy   }, // Undead (non-Forsaken)
        { 0x54, AccentGroup.AmericanRaspy   }, // Demon
        { 0x55, AccentGroup.DeepResonant    }, // Elemental
        { 0x56, AccentGroup.DeepResonant    }, // Giant
        { 0x57, AccentGroup.PlayfulSqueaky  }, // Mechanical
        { 0x58, AccentGroup.Narrator        }, // Aberration
    };

    /// <summary>
    /// Maps a RACE byte and FLAGS to a VoiceSlot.
    /// FLAG_NARRATOR always returns VoiceSlot.Narrator regardless of race or gender.
    /// </summary>
    public static VoiceSlot Resolve(int raceByte, int flags, bool isMale, bool isFemale)
    {
        // Narrator flag overrides everything
        if ((flags & RvFlags.FlagNarrator) != 0)
            return VoiceSlot.Narrator;

        var group = ResolveGroup(raceByte);

        // Unknown gender or unknown race → Narrator fallback
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

        if (raceByte is >= 0x01 and <= 0x3F)
            return PlayerRaceMap.TryGetValue(raceByte, out var g) ? g : AccentGroup.NeutralAmerican;

        if (raceByte is >= 0x40 and <= 0x4F)
            return AccentGroup.NeutralAmerican; // reserved future player races — neutral fallback

        if (raceByte is >= 0x50 and <= 0x58)
            return CreatureTypeMap.TryGetValue(raceByte, out var g) ? g : AccentGroup.Narrator;

        return AccentGroup.Narrator; // 0x59–0xFF reserved or unknown
    }
}