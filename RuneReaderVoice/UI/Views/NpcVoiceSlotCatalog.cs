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

// NpcVoiceSlotCatalog.cs
// All voice slots shown in the Voices UI tab.
// Every playable race has its own slot pair so users can assign distinct voices.
// Creature-type slots cover non-playable NPC groups.
// SortOrder groups: 0=Narrator, 10s=Alliance, 100s=Horde, 200s=Neutral/Cross-faction,
//                   300s=Creature types.

using System.Collections.Generic;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.UI.Views;

public sealed record NpcVoiceSlotCatalogItem(
    VoiceSlot Slot,
    string NpcLabel,
    string AccentLabel,
    int SortOrder);

public static class NpcVoiceSlotCatalog
{
    public static IReadOnlyList<NpcVoiceSlotCatalogItem> All { get; } = new[]
    {
        // ── Narrator ──────────────────────────────────────────────────────────
        new NpcVoiceSlotCatalogItem(VoiceSlot.Narrator, "Narrator", "Narrator", 0),

        // ── Alliance ──────────────────────────────────────────────────────────
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Human, Gender.Male),   "Human / Male",   "Neutral American", 10),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Human, Gender.Female), "Human / Female", "Neutral American", 11),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.NightElf, Gender.Male),   "Night Elf / Male",   "Mystical American", 20),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.NightElf, Gender.Female), "Night Elf / Female", "Mystical American", 21),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Dwarf, Gender.Male),   "Dwarf / Male",   "Scottish", 30),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Dwarf, Gender.Female), "Dwarf / Female", "Scottish", 31),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.DarkIronDwarf, Gender.Male),   "Dark Iron Dwarf / Male",   "Scottish (Dark)", 40),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.DarkIronDwarf, Gender.Female), "Dark Iron Dwarf / Female", "Scottish (Dark)", 41),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Gnome, Gender.Male),   "Gnome / Male",   "Playful Squeaky", 50),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Gnome, Gender.Female), "Gnome / Female", "Playful Squeaky", 51),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Mechagnome, Gender.Male),   "Mechagnome / Male",   "Playful Mechanical", 60),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Mechagnome, Gender.Female), "Mechagnome / Female", "Playful Mechanical", 61),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Draenei, Gender.Male),   "Draenei / Male",   "Eastern European", 70),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Draenei, Gender.Female), "Draenei / Female", "Eastern European", 71),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.LightforgedDraenei, Gender.Male),   "Lightforged Draenei / Male",   "Eastern European (Radiant)", 80),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.LightforgedDraenei, Gender.Female), "Lightforged Draenei / Female", "Eastern European (Radiant)", 81),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Worgen, Gender.Male),   "Worgen / Male",   "British Rugged", 90),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Worgen, Gender.Female), "Worgen / Female", "British Rugged", 91),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.KulTiran, Gender.Male),   "Kul Tiran / Male",   "British Rugged (Maritime)", 100),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.KulTiran, Gender.Female), "Kul Tiran / Female", "British Rugged (Maritime)", 101),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.BloodElf, Gender.Male),   "Blood Elf / Male",   "British Haughty", 110),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.BloodElf, Gender.Female), "Blood Elf / Female", "British Haughty", 111),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.VoidElf, Gender.Male),   "Void Elf / Male",   "British Haughty (Eerie)", 120),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.VoidElf, Gender.Female), "Void Elf / Female", "British Haughty (Eerie)", 121),

        // ── Horde ─────────────────────────────────────────────────────────────
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Orc, Gender.Male),   "Orc / Male",   "Neutral American (Broad)", 200),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Orc, Gender.Female), "Orc / Female", "Neutral American (Broad)", 201),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.MagharOrc, Gender.Male),   "Mag'har Orc / Male",   "Neutral American (Earthy)", 210),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.MagharOrc, Gender.Female), "Mag'har Orc / Female", "Neutral American (Earthy)", 211),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Undead, Gender.Male),   "Forsaken / Male",   "American Raspy", 220),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Undead, Gender.Female), "Forsaken / Female", "American Raspy", 221),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Tauren, Gender.Male),   "Tauren / Male",   "Deep Resonant", 230),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Tauren, Gender.Female), "Tauren / Female", "Deep Resonant", 231),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.HighmountainTauren, Gender.Male),   "Highmountain Tauren / Male",   "Deep Resonant (Rugged)", 240),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.HighmountainTauren, Gender.Female), "Highmountain Tauren / Female", "Deep Resonant (Rugged)", 241),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Troll, Gender.Male),   "Troll / Male",   "Caribbean", 250),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Troll, Gender.Female), "Troll / Female", "Caribbean", 251),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.ZandalariTroll, Gender.Male),   "Zandalari Troll / Male",   "Regal Tribal", 260),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.ZandalariTroll, Gender.Female), "Zandalari Troll / Female", "Regal Tribal", 261),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Goblin, Gender.Male),   "Goblin / Male",   "New York", 270),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Goblin, Gender.Female), "Goblin / Female", "New York", 271),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Nightborne, Gender.Male),   "Nightborne / Male",   "French", 280),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Nightborne, Gender.Female), "Nightborne / Female", "French", 281),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Vulpera, Gender.Male),   "Vulpera / Male",   "Scrappy", 290),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Vulpera, Gender.Female), "Vulpera / Female", "Scrappy", 291),

        // ── Neutral / Cross-faction ───────────────────────────────────────────
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Pandaren, Gender.Male),   "Pandaren / Male",   "East Asian", 300),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Pandaren, Gender.Female), "Pandaren / Female", "East Asian", 301),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Earthen, Gender.Male),   "Earthen / Male",   "Deep British (Stone)", 310),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Earthen, Gender.Female), "Earthen / Female", "Deep British (Stone)", 311),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Haranir, Gender.Male),   "Haranir / Male",   "Primordial Forest", 320),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Haranir, Gender.Female), "Haranir / Female", "Primordial Forest", 321),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Dracthyr, Gender.Male),   "Dracthyr / Male",   "British Measured", 330),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Dracthyr, Gender.Female), "Dracthyr / Female", "British Measured", 331),

        // ── Creature types ────────────────────────────────────────────────────
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Dragonkin, Gender.Male),   "Dragonkin NPC / Male",   "Deep Resonant (Ancient)", 400),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Dragonkin, Gender.Female), "Dragonkin NPC / Female", "Deep Resonant (Ancient)", 401),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Elemental, Gender.Male),   "Elemental NPC / Male",   "Elemental", 410),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Elemental, Gender.Female), "Elemental NPC / Female", "Elemental", 411),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Giant, Gender.Male),   "Giant NPC / Male",   "Deep Resonant (Giant)", 420),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Giant, Gender.Female), "Giant NPC / Female", "Deep Resonant (Giant)", 421),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Mechanical, Gender.Male),   "Mechanical NPC / Male",   "Mechanical", 430),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Mechanical, Gender.Female), "Mechanical NPC / Female", "Mechanical", 431),
    };
}
