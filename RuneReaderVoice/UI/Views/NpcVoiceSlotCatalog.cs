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
        new NpcVoiceSlotCatalogItem(VoiceSlot.Narrator, "Narrator", "Narrator", 0),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.NeutralAmerican, Gender.Male),   "Human / Male NPC", "Neutral American", 10),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.NeutralAmerican, Gender.Female), "Human / Female NPC", "Neutral American", 11),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.AmericanRaspy, Gender.Male),   "Forsaken / Male NPC", "American Raspy", 20),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.AmericanRaspy, Gender.Female), "Forsaken / Female NPC", "American Raspy", 21),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Scottish, Gender.Male),   "Dwarf / Male NPC", "Scottish", 30),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Scottish, Gender.Female), "Dwarf / Female NPC", "Scottish", 31),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.BritishHaughty, Gender.Male),   "Blood Elf / Male NPC", "British Haughty", 40),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.BritishHaughty, Gender.Female), "Blood Elf / Female NPC", "British Haughty", 41),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.BritishRugged, Gender.Male),   "Worgen / Male NPC", "British Rugged", 50),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.BritishRugged, Gender.Female), "Worgen / Female NPC", "British Rugged", 51),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.PlayfulSqueaky, Gender.Male),   "Gnome / Male NPC", "Playful Squeaky", 60),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.PlayfulSqueaky, Gender.Female), "Gnome / Female NPC", "Playful Squeaky", 61),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.EasternEuropean, Gender.Male),   "Draenei / Male NPC", "Eastern European", 70),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.EasternEuropean, Gender.Female), "Draenei / Female NPC", "Eastern European", 71),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Caribbean, Gender.Male),   "Troll / Male NPC", "Caribbean", 80),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Caribbean, Gender.Female), "Troll / Female NPC", "Caribbean", 81),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.RegalTribal, Gender.Male),   "Zandalari / Male NPC", "Regal Tribal", 90),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.RegalTribal, Gender.Female), "Zandalari / Female NPC", "Regal Tribal", 91),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.DeepResonant, Gender.Male),   "Tauren / Male NPC", "Deep Resonant", 100),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.DeepResonant, Gender.Female), "Tauren / Female NPC", "Deep Resonant", 101),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.NewYork, Gender.Male),   "Goblin / Male NPC", "New York", 110),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.NewYork, Gender.Female), "Goblin / Female NPC", "New York", 111),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.EastAsian, Gender.Male),   "Pandaren / Male NPC", "East Asian", 120),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.EastAsian, Gender.Female), "Pandaren / Female NPC", "East Asian", 121),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.French, Gender.Male),   "Nightborne / Male NPC", "French", 130),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.French, Gender.Female), "Nightborne / Female NPC", "French", 131),

        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Scrappy, Gender.Male),   "Vulpera / Male NPC", "Scrappy", 140),
        new NpcVoiceSlotCatalogItem(new VoiceSlot(AccentGroup.Scrappy, Gender.Female), "Vulpera / Female NPC", "Scrappy", 141),
    };
}