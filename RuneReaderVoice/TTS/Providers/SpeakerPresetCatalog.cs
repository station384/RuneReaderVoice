using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed class SpeakerPreset
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public VoiceSlot Slot { get; init; }
    public VoiceProfile Profile { get; init; } = new();
    public bool IsRecommended { get; init; }

    public override string ToString() => DisplayName;
}

public static class SpeakerPresetCatalog
{
    public static IReadOnlyList<SpeakerPreset> All { get; } = Build();

    public static SpeakerPreset? GetRecommendedForSlot(VoiceSlot slot)
    {
        if (slot.Group == AccentGroup.Narrator)
            return All.FirstOrDefault(p => p.Slot.Group == AccentGroup.Narrator && p.IsRecommended);

        return All.FirstOrDefault(p => p.Slot.Equals(slot) && p.IsRecommended)
            ?? All.FirstOrDefault(p => p.Slot.Group == slot.Group && p.Slot.Gender == slot.Gender && p.IsRecommended);
    }

    public static IReadOnlyList<SpeakerPreset> GetForSlot(VoiceSlot slot)
    {
        IEnumerable<SpeakerPreset> query =
            slot.Group == AccentGroup.Narrator
                ? All.Where(p => p.Slot.Group == AccentGroup.Narrator)
                : All.Where(p => p.Slot.Group != AccentGroup.Narrator && p.Slot.Gender == slot.Gender);

        return query
            .OrderByDescending(p => p.Slot.Equals(slot))
            .ThenByDescending(p => p.IsRecommended)
            .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<SpeakerPreset> Build()
    {
        var list = new List<SpeakerPreset>();

        // Narrator
        list.Add(PresetSingle("narrator.recommended", "Narrator — Recommended", VoiceSlot.Narrator,
            "Warm neutral narrator",
            "af_bella", "en-us", 1.00f, true));

        // Alliance
        AddBoth(list, "human", "Human — Recommended", AccentGroup.NeutralAmerican,
            male: Mix("am_michael", 0.70f, "am_adam", 0.30f),
            female: Mix("af_sarah", 0.65f, "af_bella", 0.35f),
            lang: "en-us", rate: 1.00f, recommended: true,
            description: "Neutral approachable human voice");

        AddBoth(list, "dwarf", "Dwarf — Recommended", AccentGroup.Scottish,
            male: Mix("bm_george", 0.70f, "bm_daniel", 0.30f),
            female: Mix("bf_alice", 0.60f, "bf_emma", 0.40f),
            lang: "en-gb-scotland", rate: 0.98f, recommended: true,
            description: "Scottish dwarven voice");

        AddBoth(list, "nightelf", "Night Elf — Recommended", AccentGroup.NeutralAmerican,
            male: Mix("am_liam", 0.55f, "bm_fable", 0.45f),
            female: Mix("af_bella", 0.60f, "bf_emma", 0.40f),
            lang: "en-us", rate: 0.96f, recommended: false,
            description: "Calm mystical night elf voice");

        AddBoth(list, "gnome", "Gnome — Recommended", AccentGroup.PlayfulSqueaky,
            male: Mix("am_puck", 0.60f, "am_adam", 0.40f),
            female: Mix("af_nova", 0.60f, "af_sky", 0.40f),
            lang: "en-us", rate: 1.06f, recommended: true,
            description: "Bright playful gnome voice");

        AddBoth(list, "draenei", "Draenei — Recommended", AccentGroup.EasternEuropean,
            male: Mix("bm_lewis", 0.40f, "bm_fable", 0.25f, "am_liam", 0.35f),
            female: Mix("bf_isabella", 0.40f, "bf_emma", 0.25f, "af_bella", 0.35f),
            lang: "sk", rate: 0.95f, recommended: true,
            description: "Formal otherworldly draenei voice");

        AddBoth(list, "worgen", "Worgen — Recommended", AccentGroup.BritishRugged,
            male: Mix("bm_daniel", 0.50f, "bm_george", 0.50f),
            female: Mix("bf_emma", 0.55f, "bf_alice", 0.45f),
            lang: "en-gb", rate: 1.00f, recommended: true,
            description: "Rugged British worgen voice");

        AddBoth(list, "pandaren", "Pandaren — Recommended", AccentGroup.EastAsian,
            male: Mix("jm_kumo", 0.45f, "am_liam", 0.55f),
            female: Mix("jf_alpha", 0.45f, "af_bella", 0.55f),
            lang: "cmn", rate: 0.95f, recommended: true,
            description: "Warm centered pandaren voice");

        AddBoth(list, "dracthyr", "Dracthyr — Recommended", AccentGroup.BritishHaughty,
            male: Mix("bm_lewis", 0.40f, "bm_fable", 0.35f, "am_fenrir", 0.25f),
            female: Mix("bf_isabella", 0.45f, "bf_emma", 0.35f, "af_alloy", 0.20f),
            lang: "en-gb", rate: 0.97f, recommended: false,
            description: "Measured ancient dracthyr voice");

        AddBoth(list, "voidelf", "Void Elf — Recommended", AccentGroup.BritishHaughty,
            male: Mix("bm_fable", 0.50f, "bm_lewis", 0.30f, "am_onyx", 0.20f),
            female: Mix("bf_isabella", 0.50f, "af_alloy", 0.25f, "bf_emma", 0.25f),
            lang: "en-gb-x-rp", rate: 0.94f, recommended: false,
            description: "Refined eerie void elf voice");

        AddBoth(list, "lightforged", "Lightforged Draenei — Recommended", AccentGroup.EasternEuropean,
            male: Mix("bm_lewis", 0.35f, "am_michael", 0.35f, "bm_fable", 0.30f),
            female: Mix("bf_isabella", 0.40f, "af_bella", 0.35f, "bf_emma", 0.25f),
            lang: "sk", rate: 0.96f, recommended: false,
            description: "Brighter resolute draenei voice");

        AddBoth(list, "darkirondwarf", "Dark Iron Dwarf — Recommended", AccentGroup.Scottish,
            male: Mix("bm_george", 0.45f, "bm_daniel", 0.25f, "am_onyx", 0.30f),
            female: Mix("bf_alice", 0.45f, "af_alloy", 0.35f, "bf_emma", 0.20f),
            lang: "en-gb-scotland", rate: 0.97f, recommended: false,
            description: "Harsher smoky dwarf voice");

        AddBoth(list, "kultiran", "Kul Tiran — Recommended", AccentGroup.BritishRugged,
            male: Mix("bm_daniel", 0.45f, "am_fenrir", 0.25f, "bm_george", 0.30f),
            female: Mix("bf_emma", 0.45f, "af_alloy", 0.25f, "bf_alice", 0.30f),
            lang: "en-gb", rate: 0.96f, recommended: false,
            description: "Weathered maritime Kul Tiran voice");

        AddBoth(list, "mechagnome", "Mechagnome — Recommended", AccentGroup.PlayfulSqueaky,
            male: Mix("am_puck", 0.30f, "am_adam", 0.40f, "bm_fable", 0.30f),
            female: Mix("af_nova", 0.35f, "af_jessica", 0.35f, "bf_emma", 0.30f),
            lang: "en-us", rate: 1.02f, recommended: false,
            description: "Sharper cleaner mechanized gnome voice");

        AddBoth(list, "haranir", "Haranir — Recommended", AccentGroup.RegalTribal,
            male: Mix("am_liam", 0.30f, "am_echo", 0.25f, "bm_fable", 0.20f, "am_fenrir", 0.25f),
            female: Mix("af_bella", 0.35f, "af_aoede", 0.25f, "bf_fable", 0.20f, "af_kore", 0.20f),
            lang: "sw", rate: 0.96f, recommended: false,
            description: "Wildsong best-effort haranir voice");

        // Horde
        AddBoth(list, "orc", "Orc — Recommended", AccentGroup.NeutralAmerican,
            male: Mix("am_fenrir", 0.45f, "am_onyx", 0.30f, "bm_daniel", 0.25f),
            female: Mix("af_alloy", 0.45f, "af_kore", 0.30f, "bf_emma", 0.25f),
            lang: "en-us", rate: 0.96f, recommended: false,
            description: "Broad strong orc voice");

        AddBoth(list, "undead", "Undead — Recommended", AccentGroup.AmericanRaspy,
            male: Mix("am_onyx", 0.55f, "bm_fable", 0.20f, "bm_daniel", 0.25f),
            female: Mix("af_alloy", 0.55f, "bf_isabella", 0.20f, "af_kore", 0.25f),
            lang: "en-us", rate: 0.93f, recommended: true,
            description: "Dry hollow undead voice");

        AddBoth(list, "tauren", "Tauren — Recommended", AccentGroup.DeepResonant,
            male: Mix("am_fenrir", 0.60f, "am_liam", 0.15f, "bm_george", 0.25f),
            female: Mix("af_bella", 0.35f, "af_kore", 0.30f, "bf_emma", 0.35f),
            lang: "en-us", rate: 0.92f, recommended: true,
            description: "Deep grounded tauren voice");

        AddBoth(list, "troll", "Troll — Recommended", AccentGroup.Caribbean,
            male: Mix("am_echo", 0.50f, "am_eric", 0.20f, "am_adam", 0.30f),
            female: Mix("af_aoede", 0.45f, "af_nova", 0.25f, "af_bella", 0.30f),
            lang: "en-029", rate: 1.02f, recommended: true,
            description: "Island troll voice");

        AddBoth(list, "bloodelf", "Blood Elf — Recommended", AccentGroup.BritishHaughty,
            male: Mix("bm_lewis", 0.50f, "bm_fable", 0.30f, "am_michael", 0.20f),
            female: Mix("bf_isabella", 0.55f, "bf_emma", 0.25f, "af_bella", 0.20f),
            lang: "en-gb-x-rp", rate: 0.98f, recommended: true,
            description: "Elegant Silvermoon noble voice");

        AddBoth(list, "goblin", "Goblin — Recommended", AccentGroup.NewYork,
            male: Mix("am_eric", 0.45f, "am_puck", 0.30f, "am_adam", 0.25f),
            female: Mix("af_nova", 0.50f, "af_jessica", 0.25f, "af_sky", 0.25f),
            lang: "en-us-nyc", rate: 1.06f, recommended: true,
            description: "Fast streetwise goblin voice");

        AddBoth(list, "maghar", "Mag’har Orc — Recommended", AccentGroup.NeutralAmerican,
            male: Mix("am_fenrir", 0.35f, "bm_daniel", 0.30f, "am_onyx", 0.35f),
            female: Mix("af_alloy", 0.40f, "af_kore", 0.30f, "bf_emma", 0.30f),
            lang: "en-us", rate: 0.95f, recommended: false,
            description: "Older-world Mag’har orc voice");

        AddBoth(list, "earthen", "Earthen — Recommended", AccentGroup.DeepResonant,
            male: Mix("bm_george", 0.35f, "am_fenrir", 0.35f, "bm_daniel", 0.30f),
            female: Mix("bf_emma", 0.40f, "af_alloy", 0.30f, "bf_alice", 0.30f),
            lang: "en-gb", rate: 0.88f, recommended: false,
            description: "Flat steady stonebound voice");

        AddBoth(list, "zandalari", "Zandalari Troll — Recommended", AccentGroup.RegalTribal,
            male: Mix("bm_george", 0.35f, "am_echo", 0.25f, "bm_lewis", 0.20f, "am_fenrir", 0.20f),
            female: Mix("bf_fable", 0.35f, "af_aoede", 0.25f, "bf_isabella", 0.25f, "af_bella", 0.15f),
            lang: "sw", rate: 0.97f, recommended: true,
            description: "Regal Zandalari voice");

        AddBoth(list, "vulpera", "Vulpera — Recommended", AccentGroup.Scrappy,
            male: Mix("am_puck", 0.35f, "am_liam", 0.30f, "am_eric", 0.20f, "bm_fable", 0.15f),
            female: Mix("af_sky", 0.35f, "af_nova", 0.30f, "af_bella", 0.20f, "bf_emma", 0.15f),
            lang: "en-us", rate: 1.03f, recommended: true,
            description: "Quick scrappy vulpera voice");

        AddBoth(list, "nightborne", "Nightborne — Recommended", AccentGroup.French,
            male: Mix("bm_fable", 0.45f, "bm_lewis", 0.25f, "am_onyx", 0.30f),
            female: Mix("bf_fable", 0.45f, "bf_isabella", 0.30f, "af_alloy", 0.25f),
            lang: "fr-fr", rate: 0.96f, recommended: true,
            description: "Arcane court nightborne voice");

        return list;
    }

    private static void AddBoth(
        List<SpeakerPreset> list,
        string idRoot,
        string displayName,
        AccentGroup group,
        string male,
        string female,
        string lang,
        float rate,
        bool recommended,
        string description)
    {
        list.Add(Preset(idRoot + ".male", displayName, new VoiceSlot(group, Gender.Male), male, lang, rate, recommended, description));
        list.Add(Preset(idRoot + ".female", displayName, new VoiceSlot(group, Gender.Female), female, lang, rate, recommended, description));
    }

    private static SpeakerPreset Preset(
        string id,
        string displayName,
        VoiceSlot slot,
        string voiceId,
        string lang,
        float rate,
        bool recommended,
        string description) =>
        new()
        {
            Id = id,
            DisplayName = displayName,
            Slot = slot,
            Description = description,
            IsRecommended = recommended,
            Profile = new VoiceProfile
            {
                VoiceId = voiceId,
                LangCode = lang,
                SpeechRate = rate
            }
        };

    private static SpeakerPreset PresetSingle(
        string id,
        string displayName,
        VoiceSlot slot,
        string description,
        string voiceId,
        string lang,
        float rate,
        bool recommended) =>
        Preset(id, displayName, slot, voiceId, lang, rate, recommended, description);

    private static string Mix(params object[] parts)
    {
        if (parts.Length == 0 || parts.Length % 2 != 0)
            throw new ArgumentException("Mix requires alternating voiceId and weight values.");

        var items = new List<string>();
        for (int i = 0; i < parts.Length; i += 2)
        {
            var voiceId = Convert.ToString(parts[i], CultureInfo.InvariantCulture) ?? "";
            var weight = Convert.ToSingle(parts[i + 1], CultureInfo.InvariantCulture);
            items.Add($"{voiceId}:{weight.ToString("0.####", CultureInfo.InvariantCulture)}");
        }

        return $"{KokoroTtsProvider.MixPrefix}{string.Join("|", items)}";
    }
}
