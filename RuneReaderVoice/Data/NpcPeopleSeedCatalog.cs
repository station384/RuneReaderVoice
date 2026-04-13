// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Generic;

namespace RuneReaderVoice.Data;

public sealed record NpcPeopleSeedItem(
    string Id,
    string DisplayName,
    string AccentLabel,
    bool HasMale,
    bool HasFemale,
    bool HasNeutral,
    int SortOrder);

public static class NpcPeopleSeedCatalog
{
    public static IReadOnlyList<NpcPeopleSeedItem> All { get; } = new[]
    {
        new NpcPeopleSeedItem("human", "Human", "Neutral American", true, true, false, 10),
        new NpcPeopleSeedItem("nightelf", "Night Elf", "Mystical American", true, true, false, 20),
        new NpcPeopleSeedItem("dwarf", "Dwarf", "Scottish", true, true, false, 30),
        new NpcPeopleSeedItem("darkirondwarf", "Dark Iron Dwarf", "Scottish (Dark)", true, true, false, 40),
        new NpcPeopleSeedItem("gnome", "Gnome", "Playful Squeaky", true, true, false, 50),
        new NpcPeopleSeedItem("mechagnome", "Mechagnome", "Playful Mechanical", true, true, false, 60),
        new NpcPeopleSeedItem("draenei", "Draenei", "Eastern European", true, true, false, 70),
        new NpcPeopleSeedItem("lightforgeddraenei", "Lightforged Draenei", "Eastern European (Radiant)", true, true, false, 80),
        new NpcPeopleSeedItem("worgen", "Worgen", "British Rugged", true, true, false, 90),
        new NpcPeopleSeedItem("kultiran", "Kul Tiran", "British Rugged (Maritime)", true, true, false, 100),
        new NpcPeopleSeedItem("bloodelf", "Blood Elf", "British Haughty", true, true, false, 110),
        new NpcPeopleSeedItem("voidelf", "Void Elf", "British Haughty (Eerie)", true, true, false, 120),
        new NpcPeopleSeedItem("orc", "Orc", "Neutral American (Broad)", true, true, false, 200),
        new NpcPeopleSeedItem("magharorc", "Mag'har Orc", "Neutral American (Earthy)", true, true, false, 210),
        new NpcPeopleSeedItem("undead", "Forsaken", "American Raspy", true, true, false, 220),
        new NpcPeopleSeedItem("tauren", "Tauren", "Deep Resonant", true, true, false, 230),
        new NpcPeopleSeedItem("highmountaintauren", "Highmountain Tauren", "Deep Resonant (Rugged)", true, true, false, 240),
        new NpcPeopleSeedItem("troll", "Troll", "Caribbean", true, true, false, 250),
        new NpcPeopleSeedItem("zandalaritroll", "Zandalari Troll", "Regal Tribal", true, true, false, 260),
        new NpcPeopleSeedItem("goblin", "Goblin", "New York", true, true, false, 270),
        new NpcPeopleSeedItem("nightborne", "Nightborne", "French", true, true, false, 280),
        new NpcPeopleSeedItem("vulpera", "Vulpera", "Scrappy", true, true, false, 290),
        new NpcPeopleSeedItem("pandaren", "Pandaren", "East Asian", true, true, false, 300),
        new NpcPeopleSeedItem("earthen", "Earthen", "Deep British (Stone)", true, true, false, 310),
        new NpcPeopleSeedItem("haranir", "Haranir", "Primordial Forest", true, true, false, 320),
        new NpcPeopleSeedItem("dracthyr", "Dracthyr", "British Measured", true, true, false, 330),
        new NpcPeopleSeedItem("dragonkin", "Dragonkin NPC", "Deep Resonant (Ancient)", true, true, false, 400),
        new NpcPeopleSeedItem("elemental", "Elemental NPC", "Elemental", true, true, false, 410),
        new NpcPeopleSeedItem("giant", "Giant NPC", "Deep Resonant (Giant)", true, true, false, 420),
        new NpcPeopleSeedItem("mechanical", "Mechanical NPC", "Mechanical", true, true, false, 430),
        new NpcPeopleSeedItem("illidari", "Illidari", "Intense British (Demon Hunter)", true, true, false, 440),
        new NpcPeopleSeedItem("amani", "Amani Troll", "Caribbean (Fierce)", true, true, false, 500),
        new NpcPeopleSeedItem("arathi", "Arathi", "British Measured", true, true, false, 510),
        new NpcPeopleSeedItem("broken", "Broken", "Eastern European (Broken)", true, true, false, 520),
        new NpcPeopleSeedItem("centaur", "Centaur", "Deep Rough Tribal", true, true, false, 530),
        new NpcPeopleSeedItem("darktroll", "Dark Troll", "Deep Earthy Caribbean", true, true, false, 540),
        new NpcPeopleSeedItem("dredger", "Dredger", "Gravelly Subservient", true, true, false, 550),
        new NpcPeopleSeedItem("dryad", "Dryad", "Mystical Nature", true, true, false, 560),
        new NpcPeopleSeedItem("faerie", "Faerie", "Light Whimsical", true, true, false, 570),
        new NpcPeopleSeedItem("fungarian", "Fungarian", "Soft Bubbly", true, true, false, 580),
        new NpcPeopleSeedItem("grummle", "Grummle", "Nasal Singsongy", true, true, false, 590),
        new NpcPeopleSeedItem("hobgoblin", "Hobgoblin", "New York (Crude)", true, true, false, 600),
        new NpcPeopleSeedItem("kyrian", "Kyrian", "British Ethereal", true, true, false, 610),
        new NpcPeopleSeedItem("nerubian", "Nerubian", "Deep Raspy Ancient", true, true, false, 620),
        new NpcPeopleSeedItem("refti", "Refti", "Deep Aquatic Gravelly", true, true, false, 630),
        new NpcPeopleSeedItem("revantusk", "Revantusk Troll", "Caribbean", true, true, false, 640),
        new NpcPeopleSeedItem("rutaani", "Rutaani", "Sharp Avian", true, true, false, 650),
        new NpcPeopleSeedItem("shadowpine", "Shadowpine Troll", "Caribbean", true, true, false, 660),
        new NpcPeopleSeedItem("titan", "Titan Construct", "Deep Ancient", true, true, false, 670),
        new NpcPeopleSeedItem("tortollan", "Tortollan", "Wise Slow", true, true, false, 680),
        new NpcPeopleSeedItem("tuskarr", "Tuskarr", "Deep Slow", true, true, false, 690),
        new NpcPeopleSeedItem("venthyr", "Venthyr", "British Aristocratic", true, true, false, 700),
        new NpcPeopleSeedItem("zulaman", "Zul'Aman Troll", "Caribbean (Ancient)", true, true, false, 710),
    };
}
