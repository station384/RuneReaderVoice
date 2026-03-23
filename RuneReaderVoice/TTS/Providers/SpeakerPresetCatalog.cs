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

// SpeakerPresetCatalog.cs
// Default recommended voice profile for every VoiceSlot.
// One "recommended" preset per slot — the value applied on first run / reset.
// Additional alternate presets per slot can be added in a future pass.
//
// Voice mix design notes:
//   - British voices (bm_/bf_) get en-gb lang; American (am_/af_) get en-us.
//   - Mixed pools use the dominant accent's lang code.
//   - DisableChunking=true on slow/deliberate races: Tauren, Zandalari, Earthen.
//   - All races have DSP presets set. The per-group enable/disable in the UI
//     is the only flag needed here — the field values themselves carry the state.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed class SpeakerPreset
{
    public string Id          { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public VoiceSlot Slot     { get; init; }
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

        // ── Narrator ──────────────────────────────────────────────────────────
        list.Add(P("narrator", "Narrator", VoiceSlot.Narrator,
            voiceM: Mix("am_adam", 0.20f, "bm_lewis", 0.80f),
            voiceF: Mix("am_adam", 0.20f, "bm_lewis", 0.80f),
            lang: "en-gb", rate: 1.00f, rec: true,
            desc: "Warm authoritative narrator"));

        // ── Alliance ──────────────────────────────────────────────────────────

        AddBoth(list, "human", "Human", AccentGroup.Human,
            maleVoice:   Mix("am_michael", 0.70f, "am_adam", 0.30f),
            femaleVoice: Mix("af_sarah", 0.65f, "af_bella", 0.35f),
            lang: "en-us", rate: 1.00f, rec: true,
            desc: "Neutral approachable human voice");

        AddBoth(list, "nightelf", "Night Elf", AccentGroup.NightElf,
            maleVoice:   Mix("bm_george", 0.40f, "bm_lewis", 0.40f, "am_liam", 0.20f),
            femaleVoice: Mix("bf_alice", 0.50f, "af_river", 0.30f, "af_aoede", 0.20f),
            lang: "en-gb", rate: 0.96f, rec: true,
            desc: "Calm ancient night elf voice");

        AddBoth(list, "dwarf", "Dwarf", AccentGroup.Dwarf,
            maleVoice:   Mix("bm_george", 0.70f, "bm_daniel", 0.30f),
            femaleVoice: Mix("bf_alice", 0.60f, "bf_emma", 0.40f),
            lang: "en-gb-scotland", rate: 0.98f, rec: true,
            desc: "Scottish dwarven voice");

        AddBoth(list, "darkirondwarf", "Dark Iron Dwarf", AccentGroup.DarkIronDwarf,
            maleVoice:   Mix("bm_george", 0.45f, "bm_daniel", 0.25f, "am_onyx", 0.30f),
            femaleVoice: Mix("bf_alice", 0.45f, "af_alloy", 0.35f, "bf_emma", 0.20f),
            lang: "en-gb-scotland", rate: 0.97f, rec: true,
            desc: "Harsher smoky dark iron dwarf voice");

        AddBoth(list, "gnome", "Gnome", AccentGroup.Gnome,
            maleVoice:   Mix("am_puck", 0.60f, "am_adam", 0.40f),
            femaleVoice: Mix("af_nova", 0.60f, "af_sky", 0.40f),
            lang: "en-us", rate: 1.06f, rec: true,
            desc: "Bright playful gnome voice");

        AddBoth(list, "mechagnome", "Mechagnome", AccentGroup.Mechagnome,
            maleVoice:   Mix("am_puck", 0.50f, "am_liam", 0.50f),
            femaleVoice: Mix("af_nova", 0.35f, "af_jessica", 0.35f, "bf_emma", 0.30f),
            lang: "en-us", rate: 1.02f, rec: true,
            desc: "Sharper mechanized gnome voice");

        AddBoth(list, "draenei", "Draenei", AccentGroup.Draenei,
            maleVoice:   Mix("bm_lewis", 0.45f, "bm_george", 0.35f, "am_adam", 0.20f),
            femaleVoice: Mix("bf_isabella", 0.40f, "bf_emma", 0.25f, "af_bella", 0.35f),
            lang: "sk", rate: 0.95f, rec: true,
            desc: "Formal otherworldly draenei voice");

        AddBoth(list, "lightforged", "Lightforged Draenei", AccentGroup.LightforgedDraenei,
            maleVoice:   Mix("bm_george", 0.50f, "am_adam", 0.35f, "am_onyx", 0.15f),
            femaleVoice: Mix("bf_isabella", 0.40f, "af_bella", 0.35f, "bf_emma", 0.25f),
            lang: "sk", rate: 0.96f, rec: true,
            desc: "Brighter resolute lightforged voice");

        AddBoth(list, "worgen", "Worgen", AccentGroup.Worgen,
            maleVoice:   Mix("bm_daniel", 0.55f, "am_fenrir", 0.45f),
            femaleVoice: Mix("bf_emma", 0.55f, "bf_alice", 0.45f),
            lang: "en-gb", rate: 1.00f, rec: true,
            desc: "Rugged British worgen voice");

        AddBoth(list, "kultiran", "Kul Tiran", AccentGroup.KulTiran,
            maleVoice:   Mix("bm_george", 0.50f, "am_eric", 0.30f, "am_adam", 0.20f),
            femaleVoice: Mix("bf_emma", 0.45f, "af_alloy", 0.25f, "bf_alice", 0.30f),
            lang: "en-gb", rate: 0.96f, rec: true,
            desc: "Weathered maritime Kul Tiran voice");

        AddBoth(list, "bloodelf", "Blood Elf", AccentGroup.BloodElf,
            maleVoice:   Mix("bm_lewis", 0.60f, "bm_daniel", 0.40f),
            femaleVoice: Mix("bf_isabella", 0.55f, "bf_lily", 0.45f),
            lang: "en-gb-x-rp", rate: 0.98f, rec: true,
            desc: "Elegant Silvermoon noble voice");

        AddBoth(list, "voidelf", "Void Elf", AccentGroup.VoidElf,
            maleVoice:   Mix("bm_lewis", 0.40f, "bm_daniel", 0.30f, "am_echo", 0.30f),
            femaleVoice: Mix("bf_isabella", 0.40f, "bf_lily", 0.30f, "af_nova", 0.30f),
            lang: "en-gb-x-rp", rate: 0.94f, rec: true,
            desc: "Refined eerie void elf voice");

        // ── Horde ─────────────────────────────────────────────────────────────

        AddBoth(list, "orc", "Orc", AccentGroup.Orc,
            maleVoice:   Mix("am_fenrir", 0.60f, "am_onyx", 0.40f),
            femaleVoice: Mix("af_alloy", 0.65f, "af_kore", 0.35f),
            lang: "en-us", rate: 0.96f, rec: true,
            desc: "Broad strong orc voice");

        AddBoth(list, "maghar", "Mag'har Orc", AccentGroup.MagharOrc,
            maleVoice:   Mix("am_fenrir", 0.50f, "am_adam", 0.50f),
            femaleVoice: Mix("af_alloy", 0.40f, "af_kore", 0.30f, "bf_emma", 0.30f),
            lang: "en-us", rate: 0.95f, rec: true,
            desc: "Older-world Mag'har orc voice");

        AddBoth(list, "undead", "Forsaken", AccentGroup.Undead,
            maleVoice:   Mix("am_onyx", 0.55f, "bm_fable", 0.20f, "bm_daniel", 0.25f),
            femaleVoice: Mix("af_alloy", 0.55f, "bf_isabella", 0.20f, "af_kore", 0.25f),
            lang: "en-us", rate: 0.93f, rec: true,
            desc: "Dry hollow undead voice");

        AddBoth(list, "tauren", "Tauren", AccentGroup.Tauren,
            maleVoice:   Mix("am_fenrir", 0.50f, "am_onyx", 0.35f, "am_adam", 0.15f),
            femaleVoice: Mix("af_bella", 0.35f, "af_kore", 0.30f, "bf_emma", 0.35f),
            lang: "en-us", rate: 0.90f, rec: true,
            disableChunking: true,
            desc: "Deep grounded tauren voice");

        AddBoth(list, "highmountain", "Highmountain Tauren", AccentGroup.HighmountainTauren,
            maleVoice:   Mix("am_fenrir", 0.45f, "am_onyx", 0.35f, "bm_george", 0.20f),
            femaleVoice: Mix("af_bella", 0.40f, "af_kore", 0.30f, "bf_alice", 0.30f),
            lang: "en-us", rate: 0.90f, rec: true,
            disableChunking: true,
            desc: "Rugged highmountain tauren voice");

        AddBoth(list, "troll", "Troll", AccentGroup.Troll,
            maleVoice:   Mix("am_echo", 0.50f, "am_liam", 0.30f, "af_aoede", 0.20f),
            femaleVoice: Mix("af_aoede", 0.45f, "af_nova", 0.25f, "af_bella", 0.30f),
            lang: "en-029", rate: 1.02f, rec: true,
            desc: "Island troll voice");

        AddBoth(list, "zandalari", "Zandalari Troll", AccentGroup.ZandalariTroll,
            maleVoice:   Mix("am_adam", 0.45f, "am_onyx", 0.35f, "am_echo", 0.20f),
            femaleVoice: Mix("bf_fable", 0.35f, "af_aoede", 0.25f, "bf_isabella", 0.25f, "af_bella", 0.15f),
            lang: "sw", rate: 0.95f, rec: true,
            disableChunking: true,
            desc: "Regal Zandalari voice");

        AddBoth(list, "goblin", "Goblin", AccentGroup.Goblin,
            maleVoice:   Mix("am_eric", 0.55f, "am_puck", 0.45f),
            femaleVoice: Mix("af_nova", 0.50f, "af_jessica", 0.25f, "af_sky", 0.25f),
            lang: "en-us-nyc", rate: 1.06f, rec: true,
            desc: "Fast streetwise goblin voice");

        AddBoth(list, "nightborne", "Nightborne", AccentGroup.Nightborne,
            maleVoice:   Mix("bm_fable", 0.60f, "bm_lewis", 0.40f),
            femaleVoice: Mix("zf_xiaoni", 0.30f, "bf_lily", 0.40f, "bf_fable", 0.30f),
            lang: "fr-fr", rate: 0.96f, rec: true,
            desc: "Arcane court nightborne voice");

        AddBoth(list, "vulpera", "Vulpera", AccentGroup.Vulpera,
            maleVoice:   Mix("am_puck", 0.35f, "am_liam", 0.30f, "am_eric", 0.20f, "bm_fable", 0.15f),
            femaleVoice: Mix("af_sky", 0.35f, "af_nova", 0.30f, "af_bella", 0.20f, "bf_emma", 0.15f),
            lang: "en-us", rate: 1.03f, rec: true,
            desc: "Quick scrappy vulpera voice");

        // ── Neutral / Cross-faction ───────────────────────────────────────────

        AddBoth(list, "pandaren", "Pandaren", AccentGroup.Pandaren,
            maleVoice:   Mix("zm_yunjian", 0.50f, "am_onyx", 0.50f),
            femaleVoice: Mix("zf_xiaoxiao", 0.55f, "af_bella", 0.45f),
            lang: "cmn", rate: 0.95f, rec: true,
            desc: "Warm centered pandaren voice");

        AddBoth(list, "earthen", "Earthen", AccentGroup.Earthen,
            maleVoice:   Mix("am_adam", 0.40f, "am_onyx", 0.35f, "bm_george", 0.25f),
            femaleVoice: Mix("bf_emma", 0.40f, "af_alloy", 0.30f, "bf_alice", 0.30f),
            lang: "en-gb", rate: 0.88f, rec: true,
            disableChunking: true,
            desc: "Flat steady stonebound voice");

        AddBoth(list, "haranir", "Haranir", AccentGroup.Haranir,
            maleVoice:   Mix("am_adam", 0.35f, "bm_george", 0.35f, "jm_kumo", 0.30f),
            femaleVoice: Mix("af_bella", 0.35f, "bf_alice", 0.35f, "jf_alpha", 0.30f),
            lang: "sw", rate: 0.94f, rec: true,
            desc: "Primordial forest guardian haranir voice");

        AddBoth(list, "dracthyr", "Dracthyr", AccentGroup.Dracthyr,
            maleVoice:   Mix("bm_lewis", 0.40f, "bm_fable", 0.35f, "am_fenrir", 0.25f),
            femaleVoice: Mix("bf_isabella", 0.45f, "bf_emma", 0.35f, "af_alloy", 0.20f),
            lang: "en-gb", rate: 0.97f, rec: true,
            desc: "Measured ancient dracthyr voice");

        // ── Creature types ────────────────────────────────────────────────────

        AddBoth(list, "dragonkin", "Dragonkin NPC", AccentGroup.Dragonkin,
            maleVoice:   Mix("am_onyx", 0.45f, "am_adam", 0.35f, "bm_george", 0.20f),
            femaleVoice: Mix("bf_isabella", 0.45f, "af_alloy", 0.35f, "bf_alice", 0.20f),
            lang: "en-gb", rate: 0.90f, rec: true,
            desc: "Deep ancient dragonkin NPC voice");

        AddBoth(list, "elemental", "Elemental NPC", AccentGroup.Elemental,
            maleVoice:   Mix("am_onyx", 0.55f, "am_fenrir", 0.45f),
            femaleVoice: Mix("af_alloy", 0.55f, "af_kore", 0.45f),
            lang: "en-us", rate: 0.88f, rec: true,
            desc: "Booming elemental NPC voice");

        AddBoth(list, "giant", "Giant NPC", AccentGroup.Giant,
            maleVoice:   Mix("am_fenrir", 0.50f, "am_onyx", 0.30f, "bm_george", 0.20f),
            femaleVoice: Mix("af_kore", 0.50f, "af_alloy", 0.30f, "bf_emma", 0.20f),
            lang: "en-us", rate: 0.85f, rec: true,
            desc: "Rumbling giant NPC voice");

        AddBoth(list, "mechanical", "Mechanical NPC", AccentGroup.Mechanical,
            maleVoice:   Mix("am_puck", 0.50f, "am_liam", 0.50f),
            femaleVoice: Mix("af_nova", 0.50f, "af_jessica", 0.50f),
            lang: "en-us", rate: 1.04f, rec: true,
            desc: "Crisp mechanical NPC voice");

        return list;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds Male and Female presets for a race in one call.
    /// The Narrator slot uses P() directly since it has no gender pair.
    /// </summary>
    private static void AddBoth(
        List<SpeakerPreset> list,
        string idRoot,
        string displayName,
        AccentGroup group,
        string maleVoice,
        string femaleVoice,
        string lang,
        float rate,
        bool rec,
        string desc,
        bool disableChunking = false,
        DspProfile? dsp = null)
    {
        list.Add(MakePreset(idRoot + ".male",   displayName, new VoiceSlot(group, Gender.Male),   maleVoice,   lang, rate, rec, desc, disableChunking, dsp));
        list.Add(MakePreset(idRoot + ".female", displayName, new VoiceSlot(group, Gender.Female), femaleVoice, lang, rate, rec, desc, disableChunking, dsp));
    }

    /// <summary>Single-slot preset (Narrator only).</summary>
    private static SpeakerPreset P(
        string idRoot,
        string displayName,
        VoiceSlot slot,
        string voiceM,
        string voiceF,
        string lang,
        float rate,
        bool rec,
        string desc,
        DspProfile? dsp = null) =>
        MakePreset(idRoot, displayName, slot, voiceM, lang, rate, rec, desc, false, dsp);

    private static SpeakerPreset MakePreset(
        string id,
        string displayName,
        VoiceSlot slot,
        string voiceId,
        string lang,
        float rate,
        bool recommended,
        string description,
        bool disableChunking = false,
        DspProfile? dsp = null) =>
        new()
        {
            Id            = id,
            DisplayName   = displayName,
            Slot          = slot,
            Description   = description,
            IsRecommended = recommended,
            Profile = new VoiceProfile
            {
                VoiceId         = voiceId,
                LangCode        = lang,
                SpeechRate      = rate,
                DisableChunking = disableChunking,
                Dsp             = dsp,
            }
        };

    // ── Voice blend helper ────────────────────────────────────────────────────

    private static string Mix(params object[] parts)
    {
        if (parts.Length == 0 || parts.Length % 2 != 0)
            throw new ArgumentException("Mix requires alternating voiceId and weight values.");

        var items = new List<string>();
        for (int i = 0; i < parts.Length; i += 2)
        {
            var voiceId = Convert.ToString(parts[i], CultureInfo.InvariantCulture) ?? "";
            var weight  = Convert.ToSingle(parts[i + 1], CultureInfo.InvariantCulture);
            items.Add($"{voiceId}:{weight.ToString("0.####", CultureInfo.InvariantCulture)}");
        }

        return $"{KokoroTtsProvider.MixPrefix}{string.Join("|", items)}";
    }
}
