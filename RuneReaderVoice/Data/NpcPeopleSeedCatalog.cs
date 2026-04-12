// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;

namespace RuneReaderVoice.Data;

public sealed record NpcPeopleSeedItem(
    string Id,
    string DisplayName,
    string AccentGroupName,
    string AccentLabel,
    bool HasMale,
    bool HasFemale,
    bool HasNeutral,
    int SortOrder);

public static class NpcPeopleSeedCatalog
{
    public static IReadOnlyList<NpcPeopleSeedItem> All { get; } = new[]
    {
        new NpcPeopleSeedItem("human", "Human", "Human", "Neutral American", true, true, false, 10),
        new NpcPeopleSeedItem("nightelf", "Night Elf", "NightElf", "Mystical American", true, true, false, 20),
        new NpcPeopleSeedItem("dwarf", "Dwarf", "Dwarf", "Scottish", true, true, false, 30),
        new NpcPeopleSeedItem("darkirondwarf", "Dark Iron Dwarf", "DarkIronDwarf", "Scottish (Dark)", true, true, false, 40),
        new NpcPeopleSeedItem("gnome", "Gnome", "Gnome", "Playful Squeaky", true, true, false, 50),
        new NpcPeopleSeedItem("mechagnome", "Mechagnome", "Mechagnome", "Playful Mechanical", true, true, false, 60),
        new NpcPeopleSeedItem("draenei", "Draenei", "Draenei", "Eastern European", true, true, false, 70),
        new NpcPeopleSeedItem("lightforgeddraenei", "Lightforged Draenei", "LightforgedDraenei", "Eastern European (Radiant)", true, true, false, 80),
        new NpcPeopleSeedItem("worgen", "Worgen", "Worgen", "British Rugged", true, true, false, 90),
        new NpcPeopleSeedItem("kultiran", "Kul Tiran", "KulTiran", "British Rugged (Maritime)", true, true, false, 100),
        new NpcPeopleSeedItem("bloodelf", "Blood Elf", "BloodElf", "British Haughty", true, true, false, 110),
        new NpcPeopleSeedItem("voidelf", "Void Elf", "VoidElf", "British Haughty (Eerie)", true, true, false, 120),
        new NpcPeopleSeedItem("orc", "Orc", "Orc", "Neutral American (Broad)", true, true, false, 200),
        new NpcPeopleSeedItem("magharorc", "Mag'har Orc", "MagharOrc", "Neutral American (Earthy)", true, true, false, 210),
        new NpcPeopleSeedItem("undead", "Forsaken", "Undead", "American Raspy", true, true, false, 220),
        new NpcPeopleSeedItem("tauren", "Tauren", "Tauren", "Deep Resonant", true, true, false, 230),
        new NpcPeopleSeedItem("highmountaintauren", "Highmountain Tauren", "HighmountainTauren", "Deep Resonant (Rugged)", true, true, false, 240),
        new NpcPeopleSeedItem("troll", "Troll", "Troll", "Caribbean", true, true, false, 250),
        new NpcPeopleSeedItem("zandalaritroll", "Zandalari Troll", "ZandalariTroll", "Regal Tribal", true, true, false, 260),
        new NpcPeopleSeedItem("goblin", "Goblin", "Goblin", "New York", true, true, false, 270),
        new NpcPeopleSeedItem("nightborne", "Nightborne", "Nightborne", "French", true, true, false, 280),
        new NpcPeopleSeedItem("vulpera", "Vulpera", "Vulpera", "Scrappy", true, true, false, 290),
        new NpcPeopleSeedItem("pandaren", "Pandaren", "Pandaren", "East Asian", true, true, false, 300),
        new NpcPeopleSeedItem("earthen", "Earthen", "Earthen", "Deep British (Stone)", true, true, false, 310),
        new NpcPeopleSeedItem("haranir", "Haranir", "Haranir", "Primordial Forest", true, true, false, 320),
        new NpcPeopleSeedItem("dracthyr", "Dracthyr", "Dracthyr", "British Measured", true, true, false, 330),
        new NpcPeopleSeedItem("dragonkin", "Dragonkin NPC", "Dragonkin", "Deep Resonant (Ancient)", true, true, false, 400),
        new NpcPeopleSeedItem("elemental", "Elemental NPC", "Elemental", "Elemental", true, true, false, 410),
        new NpcPeopleSeedItem("giant", "Giant NPC", "Giant", "Deep Resonant (Giant)", true, true, false, 420),
        new NpcPeopleSeedItem("mechanical", "Mechanical NPC", "Mechanical", "Mechanical", true, true, false, 430),
        new NpcPeopleSeedItem("illidari", "Illidari", "Illidari", "Intense British (Demon Hunter)", true, true, false, 440),
        new NpcPeopleSeedItem("amani", "Amani Troll", "Amani", "Caribbean (Fierce)", true, true, false, 500),
        new NpcPeopleSeedItem("arathi", "Arathi", "Arathi", "British Measured", true, true, false, 510),
        new NpcPeopleSeedItem("broken", "Broken", "Broken", "Eastern European (Broken)", true, true, false, 520),
        new NpcPeopleSeedItem("centaur", "Centaur", "Centaur", "Deep Rough Tribal", true, true, false, 530),
        new NpcPeopleSeedItem("darktroll", "Dark Troll", "DarkTroll", "Deep Earthy Caribbean", true, true, false, 540),
        new NpcPeopleSeedItem("dredger", "Dredger", "Dredger", "Gravelly Subservient", true, true, false, 550),
        new NpcPeopleSeedItem("dryad", "Dryad", "Dryad", "Mystical Nature", true, true, false, 560),
        new NpcPeopleSeedItem("faerie", "Faerie", "Faerie", "Light Whimsical", true, true, false, 570),
        new NpcPeopleSeedItem("fungarian", "Fungarian", "Fungarian", "Soft Bubbly", true, true, false, 580),
        new NpcPeopleSeedItem("grummle", "Grummle", "Grummle", "Nasal Singsongy", true, true, false, 590),
        new NpcPeopleSeedItem("hobgoblin", "Hobgoblin", "Hobgoblin", "New York (Crude)", true, true, false, 600),
        new NpcPeopleSeedItem("kyrian", "Kyrian", "Kyrian", "British Ethereal", true, true, false, 610),
        new NpcPeopleSeedItem("nerubian", "Nerubian", "Nerubian", "Deep Raspy Ancient", true, true, false, 620),
        new NpcPeopleSeedItem("refti", "Refti", "Refti", "Deep Aquatic Gravelly", true, true, false, 630),
        new NpcPeopleSeedItem("revantusk", "Revantusk Troll", "Revantusk", "Caribbean", true, true, false, 640),
        new NpcPeopleSeedItem("rutaani", "Rutaani", "Rutaani", "Sharp Avian", true, true, false, 650),
        new NpcPeopleSeedItem("shadowpine", "Shadowpine Troll", "Shadowpine", "Caribbean", true, true, false, 660),
        new NpcPeopleSeedItem("titan", "Titan Construct", "Titan", "Deep Ancient", true, true, false, 670),
        new NpcPeopleSeedItem("tortollan", "Tortollan", "Tortollan", "Wise Slow", true, true, false, 680),
        new NpcPeopleSeedItem("tuskarr", "Tuskarr", "Tuskarr", "Deep Slow", true, true, false, 690),
        new NpcPeopleSeedItem("venthyr", "Venthyr", "Venthyr", "British Aristocratic", true, true, false, 700),
        new NpcPeopleSeedItem("zulaman", "Zul'Aman Troll", "ZulAman", "Caribbean (Ancient)", true, true, false, 710),
    };
}
