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
//     works by zeroing fields when a group is disabled, so DspProfile.Enabled=true
//     is the only flag needed here — the field values themselves carry the state.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NWaves.Effects;
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
            desc: "Warm authoritative narrator",
            dsp: Dsp(
                compThresholdDb: -18f, compRatio: 3f)));

        // ── Alliance ──────────────────────────────────────────────────────────

        AddBoth(list, "human", "Human", AccentGroup.Human,
            maleVoice:   Mix("am_michael", 0.70f, "am_adam", 0.30f),
            femaleVoice: Mix("af_sarah", 0.65f, "af_bella", 0.35f),
            lang: "en-us", rate: 1.00f, rec: true,
            desc: "Neutral approachable human voice",
            dsp: Dsp(
                compThresholdDb: -20f, compRatio: 3f,
                lowShelfDb: 1.0f));

        AddBoth(list, "nightelf", "Night Elf", AccentGroup.NightElf,
            maleVoice:   Mix("bm_george", 0.40f, "bm_lewis", 0.40f, "am_liam", 0.20f),
            femaleVoice: Mix("bf_alice", 0.50f, "af_river", 0.30f, "af_aoede", 0.20f),
            lang: "en-gb", rate: 0.96f, rec: true,
            desc: "Calm ancient night elf voice",
            dsp: Dsp(
                hpfHz: 60f,
                highShelfDb: 1.5f, exciter: 0.12f,
                reverbWet: 0.15f, reverbRoom: 0.55f, reverbDamp: 0.7f));

        AddBoth(list, "dwarf", "Dwarf", AccentGroup.Dwarf,
            maleVoice:   Mix("bm_george", 0.70f, "bm_daniel", 0.30f),
            femaleVoice: Mix("bf_alice", 0.60f, "bf_emma", 0.40f),
            lang: "en-gb-scotland", rate: 0.98f, rec: true,
            desc: "Scottish dwarven voice",
            dsp: Dsp(
                compThresholdDb: -18f, compRatio: 3.5f,
                lowShelfDb: 2.0f, midGainDb: 0.5f, midFreqHz: 400f));

        AddBoth(list, "darkirondwarf", "Dark Iron Dwarf", AccentGroup.DarkIronDwarf,
            maleVoice:   Mix("bm_george", 0.45f, "bm_daniel", 0.25f, "am_onyx", 0.30f),
            femaleVoice: Mix("bf_alice", 0.45f, "af_alloy", 0.35f, "bf_emma", 0.20f),
            lang: "en-gb-scotland", rate: 0.97f, rec: true,
            desc: "Harsher smoky dark iron dwarf voice",
            dsp: Dsp(
                compThresholdDb: -16f, compRatio: 4f,
                lowShelfDb: 3.0f, highShelfDb: -1.5f,
                tubeDistortion: true, tubeDist: 3.5f, tubeQ: -0.3f,
                reverbWet: 0.08f, reverbRoom: 0.4f, reverbDamp: 0.5f));

        AddBoth(list, "gnome", "Gnome", AccentGroup.Gnome,
            maleVoice:   Mix("am_puck", 0.60f, "am_adam", 0.40f),
            femaleVoice: Mix("af_nova", 0.60f, "af_sky", 0.40f),
            lang: "en-us", rate: 1.06f, rec: true,
            desc: "Bright playful gnome voice",
            dsp: Dsp(
                hpfHz: 80f,
                highShelfDb: 2.0f, exciter: 0.15f));

        AddBoth(list, "mechagnome", "Mechagnome", AccentGroup.Mechagnome,
            maleVoice:   Mix("am_puck", 0.50f, "am_liam", 0.50f),
            femaleVoice: Mix("af_nova", 0.35f, "af_jessica", 0.35f, "bf_emma", 0.30f),
            lang: "en-us", rate: 1.02f, rec: true,
            desc: "Sharper mechanized gnome voice",
            dsp: Dsp(
                hpfHz: 100f,
                highShelfDb: 2.5f,
                bitCrush: 10,
                flangerWet: 0.18f, flangerRateHz: 1.2f, flangerFb: 0.4f));

        AddBoth(list, "draenei", "Draenei", AccentGroup.Draenei,
            maleVoice:   Mix("bm_lewis", 0.45f, "bm_george", 0.35f, "am_adam", 0.20f),
            femaleVoice: Mix("bf_isabella", 0.40f, "bf_emma", 0.25f, "af_bella", 0.35f),
            lang: "sk", rate: 0.95f, rec: true,
            desc: "Formal otherworldly draenei voice",
            dsp: Dsp(
                phaserWet: 0.20f, phaserRateHz: 0.3f,
                reverbWet: 0.18f, reverbRoom: 0.60f, reverbDamp: 0.65f));

        AddBoth(list, "lightforged", "Lightforged Draenei", AccentGroup.LightforgedDraenei,
            maleVoice:   Mix("bm_george", 0.50f, "am_adam", 0.35f, "am_onyx", 0.15f),
            femaleVoice: Mix("bf_isabella", 0.40f, "af_bella", 0.35f, "bf_emma", 0.25f),
            lang: "sk", rate: 0.96f, rec: true,
            desc: "Brighter resolute lightforged voice",
            dsp: Dsp(
                highShelfDb: 1.5f, exciter: 0.10f,
                phaserWet: 0.12f, phaserRateHz: 0.2f,
                reverbWet: 0.12f, reverbRoom: 0.50f, reverbDamp: 0.75f));

        AddBoth(list, "worgen", "Worgen", AccentGroup.Worgen,
            maleVoice:   Mix("bm_daniel", 0.55f, "am_fenrir", 0.45f),
            femaleVoice: Mix("bf_emma", 0.55f, "bf_alice", 0.45f),
            lang: "en-gb", rate: 1.00f, rec: true,
            desc: "Rugged British worgen voice",
            dsp: Dsp(
                compThresholdDb: -16f, compRatio: 4f,
                lowShelfDb: 2.5f, midGainDb: 1.0f, midFreqHz: 350f,
                tubeDistortion: true, tubeDist: 4.0f, tubeQ: -0.25f));

        AddBoth(list, "kultiran", "Kul Tiran", AccentGroup.KulTiran,
            maleVoice:   Mix("bm_george", 0.50f, "am_eric", 0.30f, "am_adam", 0.20f),
            femaleVoice: Mix("bf_emma", 0.45f, "af_alloy", 0.25f, "bf_alice", 0.30f),
            lang: "en-gb", rate: 0.96f, rec: true,
            desc: "Weathered maritime Kul Tiran voice",
            dsp: Dsp(
                compThresholdDb: -18f, compRatio: 3f,
                lowShelfDb: 1.5f,
                reverbWet: 0.12f, reverbRoom: 0.45f, reverbDamp: 0.55f));

        AddBoth(list, "bloodelf", "Blood Elf", AccentGroup.BloodElf,
            maleVoice:   Mix("bm_lewis", 0.60f, "bm_daniel", 0.40f),
            femaleVoice: Mix("bf_isabella", 0.55f, "bf_lily", 0.45f),
            lang: "en-gb-x-rp", rate: 0.98f, rec: true,
            desc: "Elegant Silvermoon noble voice",
            dsp: Dsp(
                hpfHz: 70f,
                highShelfDb: 2.0f, exciter: 0.18f,
                chorusWet: 0.08f, chorusRateHz: 1.0f, chorusWidth: 0.012f));

        AddBoth(list, "voidelf", "Void Elf", AccentGroup.VoidElf,
            maleVoice:   Mix("bm_lewis", 0.40f, "bm_daniel", 0.30f, "am_echo", 0.30f),
            femaleVoice: Mix("bf_isabella", 0.40f, "bf_lily", 0.30f, "af_nova", 0.30f),
            lang: "en-gb-x-rp", rate: 0.94f, rec: true,
            desc: "Refined eerie void elf voice",
            dsp: Dsp(
                pitchSt: -1.0f,
                phaserWet: 0.30f, phaserRateHz: 0.25f,
                chorusWet: 0.12f, chorusRateHz: 0.8f, chorusWidth: 0.018f,
                reverbWet: 0.20f, reverbRoom: 0.65f, reverbDamp: 0.60f));

        // ── Horde ─────────────────────────────────────────────────────────────

        AddBoth(list, "orc", "Orc", AccentGroup.Orc,
            maleVoice:   Mix("am_fenrir", 0.60f, "am_onyx", 0.40f),
            femaleVoice: Mix("af_alloy", 0.65f, "af_kore", 0.35f),
            lang: "en-us", rate: 0.96f, rec: true,
            desc: "Broad strong orc voice",
            dsp: Dsp(
                compThresholdDb: -16f, compRatio: 4f,
                lowShelfDb: 2.5f, midGainDb: 1.0f, midFreqHz: 300f,
                highShelfDb: -1.0f));

        AddBoth(list, "maghar", "Mag'har Orc", AccentGroup.MagharOrc,
            maleVoice:   Mix("am_fenrir", 0.50f, "am_adam", 0.50f),
            femaleVoice: Mix("af_alloy", 0.40f, "af_kore", 0.30f, "bf_emma", 0.30f),
            lang: "en-us", rate: 0.95f, rec: true,
            desc: "Older-world Mag'har orc voice",
            dsp: Dsp(
                compThresholdDb: -18f, compRatio: 3.5f,
                lowShelfDb: 2.0f, midGainDb: 0.5f, midFreqHz: 350f));

        AddBoth(list, "undead", "Forsaken", AccentGroup.Undead,
            maleVoice:   Mix("am_onyx", 0.55f, "bm_fable", 0.20f, "bm_daniel", 0.25f),
            femaleVoice: Mix("af_alloy", 0.55f, "bf_isabella", 0.20f, "af_kore", 0.25f),
            lang: "en-us", rate: 0.93f, rec: true,
            desc: "Dry hollow undead voice",
            dsp: Dsp(
                compThresholdDb: -20f, compRatio: 3f,
                hpfHz: 80f, highShelfDb: -2.0f,
                tubeDistortion: true, tubeDist: 2.5f, tubeQ: -0.4f,
                tremoloDepth: 0.18f, tremoloRateHz: 3.5f,
                reverbWet: 0.22f, reverbRoom: 0.70f, reverbDamp: 0.40f));

        AddBoth(list, "tauren", "Tauren", AccentGroup.Tauren,
            maleVoice:   Mix("am_fenrir", 0.50f, "am_onyx", 0.35f, "am_adam", 0.15f),
            femaleVoice: Mix("af_bella", 0.35f, "af_kore", 0.30f, "bf_emma", 0.35f),
            lang: "en-us", rate: 0.90f, rec: true,
            disableChunking: true,
            desc: "Deep grounded tauren voice",
            dsp: Dsp(
                compThresholdDb: -14f, compRatio: 5f,
                lowShelfDb: 3.5f, midGainDb: -0.5f, midFreqHz: 500f,
                reverbWet: 0.25f, reverbRoom: 0.80f, reverbDamp: 0.50f));

        AddBoth(list, "highmountain", "Highmountain Tauren", AccentGroup.HighmountainTauren,
            maleVoice:   Mix("am_fenrir", 0.45f, "am_onyx", 0.35f, "bm_george", 0.20f),
            femaleVoice: Mix("af_bella", 0.40f, "af_kore", 0.30f, "bf_alice", 0.30f),
            lang: "en-us", rate: 0.90f, rec: true,
            disableChunking: true,
            desc: "Rugged highmountain tauren voice",
            dsp: Dsp(
                compThresholdDb: -14f, compRatio: 5f,
                lowShelfDb: 3.0f,
                reverbWet: 0.22f, reverbRoom: 0.75f, reverbDamp: 0.45f));

        AddBoth(list, "troll", "Troll", AccentGroup.Troll,
            maleVoice:   Mix("am_echo", 0.50f, "am_liam", 0.30f, "af_aoede", 0.20f),
            femaleVoice: Mix("af_aoede", 0.45f, "af_nova", 0.25f, "af_bella", 0.30f),
            lang: "en-029", rate: 1.02f, rec: true,
            desc: "Island troll voice",
            dsp: Dsp(
                autoWahWet: 0.30f, autoWahMinHz: 250f, autoWahMaxHz: 2800f,
                chorusWet: 0.10f, chorusRateHz: 1.8f, chorusWidth: 0.015f));

        AddBoth(list, "zandalari", "Zandalari Troll", AccentGroup.ZandalariTroll,
            maleVoice:   Mix("am_adam", 0.45f, "am_onyx", 0.35f, "am_echo", 0.20f),
            femaleVoice: Mix("bf_fable", 0.35f, "af_aoede", 0.25f, "bf_isabella", 0.25f, "af_bella", 0.15f),
            lang: "sw", rate: 0.95f, rec: true,
            disableChunking: true,
            desc: "Regal Zandalari voice",
            dsp: Dsp(
                compThresholdDb: -18f, compRatio: 3f,
                lowShelfDb: 1.5f,
                reverbWet: 0.18f, reverbRoom: 0.60f, reverbDamp: 0.55f));

        AddBoth(list, "goblin", "Goblin", AccentGroup.Goblin,
            maleVoice:   Mix("am_eric", 0.55f, "am_puck", 0.45f),
            femaleVoice: Mix("af_nova", 0.50f, "af_jessica", 0.25f, "af_sky", 0.25f),
            lang: "en-us-nyc", rate: 1.06f, rec: true,
            desc: "Fast streetwise goblin voice",
            dsp: Dsp(
                hpfHz: 90f,
                highShelfDb: 1.5f,
                bitCrush: 12,
                chorusWet: 0.12f, chorusRateHz: 2.5f, chorusWidth: 0.010f));

        AddBoth(list, "nightborne", "Nightborne", AccentGroup.Nightborne,
            maleVoice:   Mix("bm_fable", 0.60f, "bm_lewis", 0.40f),
            femaleVoice: Mix("zf_xiaoni", 0.30f, "bf_lily", 0.40f, "bf_fable", 0.30f),
            lang: "fr-fr", rate: 0.96f, rec: true,
            desc: "Arcane court nightborne voice",
            dsp: Dsp(
                phaserWet: 0.22f, phaserRateHz: 0.4f,
                chorusWet: 0.10f, chorusRateHz: 1.0f, chorusWidth: 0.014f,
                reverbWet: 0.18f, reverbRoom: 0.55f, reverbDamp: 0.65f));

        AddBoth(list, "vulpera", "Vulpera", AccentGroup.Vulpera,
            maleVoice:   Mix("am_puck", 0.35f, "am_liam", 0.30f, "am_eric", 0.20f, "bm_fable", 0.15f),
            femaleVoice: Mix("af_sky", 0.35f, "af_nova", 0.30f, "af_bella", 0.20f, "bf_emma", 0.15f),
            lang: "en-us", rate: 1.03f, rec: true,
            desc: "Quick scrappy vulpera voice",
            dsp: Dsp(
                hpfHz: 70f,
                highShelfDb: 1.5f,
                chorusWet: 0.10f, chorusRateHz: 2.0f, chorusWidth: 0.012f));

        // ── Neutral / Cross-faction ───────────────────────────────────────────

        AddBoth(list, "pandaren", "Pandaren", AccentGroup.Pandaren,
            maleVoice:   Mix("zm_yunjian", 0.50f, "am_onyx", 0.50f),
            femaleVoice: Mix("zf_xiaoxiao", 0.55f, "af_bella", 0.45f),
            lang: "cmn", rate: 0.95f, rec: true,
            desc: "Warm centered pandaren voice",
            dsp: Dsp(
                compThresholdDb: -20f, compRatio: 3f,
                reverbWet: 0.12f, reverbRoom: 0.45f, reverbDamp: 0.70f));

        AddBoth(list, "earthen", "Earthen", AccentGroup.Earthen,
            maleVoice:   Mix("am_adam", 0.40f, "am_onyx", 0.35f, "bm_george", 0.25f),
            femaleVoice: Mix("bf_emma", 0.40f, "af_alloy", 0.30f, "bf_alice", 0.30f),
            lang: "en-gb", rate: 0.88f, rec: true,
            disableChunking: true,
            desc: "Flat steady stonebound voice",
            dsp: Dsp(
                compThresholdDb: -14f, compRatio: 5f,
                lowShelfDb: 2.5f, highShelfDb: -1.5f,
                reverbWet: 0.15f, reverbRoom: 0.50f, reverbDamp: 0.45f));

        AddBoth(list, "haranir", "Haranir", AccentGroup.Haranir,
            maleVoice:   Mix("am_adam", 0.35f, "bm_george", 0.35f, "jm_kumo", 0.30f),
            femaleVoice: Mix("af_bella", 0.35f, "bf_alice", 0.35f, "jf_alpha", 0.30f),
            lang: "sw", rate: 0.94f, rec: true,
            desc: "Primordial forest guardian haranir voice",
            dsp: Dsp(
                phaserWet: 0.18f, phaserRateHz: 0.35f,
                reverbWet: 0.22f, reverbRoom: 0.65f, reverbDamp: 0.50f));

        AddBoth(list, "dracthyr", "Dracthyr", AccentGroup.Dracthyr,
            maleVoice:   Mix("bm_lewis", 0.40f, "bm_fable", 0.35f, "am_fenrir", 0.25f),
            femaleVoice: Mix("bf_isabella", 0.45f, "bf_emma", 0.35f, "af_alloy", 0.20f),
            lang: "en-gb", rate: 0.97f, rec: true,
            desc: "Measured ancient dracthyr voice",
            dsp: Dsp(
                hpfHz: 60f,
                highShelfDb: 1.5f, exciter: 0.10f,
                reverbWet: 0.18f, reverbRoom: 0.60f, reverbDamp: 0.65f));

        // ── Creature types ────────────────────────────────────────────────────

        AddBoth(list, "dragonkin", "Dragonkin NPC", AccentGroup.Dragonkin,
            maleVoice:   Mix("am_onyx", 0.45f, "am_adam", 0.35f, "bm_george", 0.20f),
            femaleVoice: Mix("bf_isabella", 0.45f, "af_alloy", 0.35f, "bf_alice", 0.20f),
            lang: "en-gb", rate: 0.90f, rec: true,
            desc: "Deep ancient dragonkin NPC voice",
            dsp: Dsp(
                compThresholdDb: -14f, compRatio: 5f,
                lowShelfDb: 2.0f,
                reverbWet: 0.28f, reverbRoom: 0.80f, reverbDamp: 0.45f));

        AddBoth(list, "elemental", "Elemental NPC", AccentGroup.Elemental,
            maleVoice:   Mix("am_onyx", 0.55f, "am_fenrir", 0.45f),
            femaleVoice: Mix("af_alloy", 0.55f, "af_kore", 0.45f),
            lang: "en-us", rate: 0.88f, rec: true,
            desc: "Booming elemental NPC voice",
            dsp: Dsp(
                compThresholdDb: -14f, compRatio: 6f,
                lowShelfDb: 2.0f, highShelfDb: -1.0f,
                flangerWet: 0.20f, flangerRateHz: 0.6f, flangerFb: 0.6f,
                reverbWet: 0.30f, reverbRoom: 0.85f, reverbDamp: 0.35f));

        AddBoth(list, "giant", "Giant NPC", AccentGroup.Giant,
            maleVoice:   Mix("am_fenrir", 0.50f, "am_onyx", 0.30f, "bm_george", 0.20f),
            femaleVoice: Mix("af_kore", 0.50f, "af_alloy", 0.30f, "bf_emma", 0.20f),
            lang: "en-us", rate: 0.85f, rec: true,
            desc: "Rumbling giant NPC voice",
            dsp: Dsp(
                compThresholdDb: -12f, compRatio: 6f,
                pitchSt: -2.0f,
                lowShelfDb: 4.0f, midGainDb: -1.0f, midFreqHz: 500f,
                reverbWet: 0.32f, reverbRoom: 0.90f, reverbDamp: 0.40f));

        AddBoth(list, "mechanical", "Mechanical NPC", AccentGroup.Mechanical,
            maleVoice:   Mix("am_puck", 0.50f, "am_liam", 0.50f),
            femaleVoice: Mix("af_nova", 0.50f, "af_jessica", 0.50f),
            lang: "en-us", rate: 1.04f, rec: true,
            desc: "Crisp mechanical NPC voice",
            dsp: Dsp(
                hpfHz: 100f,
                highShelfDb: 2.0f,
                bitCrush: 8,
                flangerWet: 0.25f, flangerRateHz: 1.5f, flangerFb: 0.45f));

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

    // ── DSP preset factories ──────────────────────────────────────────────────

    /// <summary>
    /// Constructs a DspProfile with Enabled=true and only the supplied fields set.
    /// Any effect group left at its zero/null default is a no-op in DspFilterChain.
    /// </summary>
    private static DspProfile Dsp(
        // Dynamics
        float compThresholdDb   = 0f,
        float compRatio         = 4f,
        // Pitch / Tempo
        float pitchSt           = 0f,
        float tempoPct          = 0f,
        // EQ
        float hpfHz             = 0f,
        float lowShelfDb        = 0f,
        float midGainDb         = 0f,
        float midFreqHz         = 1000f,
        float highShelfDb       = 0f,
        float exciter           = 0f,
        // Distortion
        bool  tubeDistortion    = false,
        float tubeDist          = 5f,
        float tubeQ             = -0.2f,
        NWaves.Effects.DistortionMode? distMode = null,
        float distInDb          = 0f,
        float distOutDb         = 0f,
        int   bitCrush          = 0,
        // Modulation
        float chorusWet         = 0f,
        float chorusRateHz      = 1.5f,
        float chorusWidth       = 0.02f,
        float vibratoWidth      = 0f,
        float vibratoRateHz     = 2f,
        float phaserWet         = 0f,
        float phaserRateHz      = 0.5f,
        float flangerWet        = 0f,
        float flangerRateHz     = 0.5f,
        float flangerFb         = 0.5f,
        float autoWahWet        = 0f,
        float autoWahMinHz      = 300f,
        float autoWahMaxHz      = 3000f,
        float tremoloDepth      = 0f,
        float tremoloRateHz     = 3f,
        // Delay / Space
        float echoDelaySec      = 0f,
        float echoFb            = 0.4f,
        float echoWet           = 0f,
        float reverbWet         = 0f,
        float reverbRoom        = 0.5f,
        float reverbDamp        = 0.5f,
        // Spectral
        int   robotHop          = 0,
        int   whisperHop        = 0) =>
        new DspProfile
        {
            Enabled                = true,
            CompressorThresholdDb  = compThresholdDb,
            CompressorRatio        = compRatio,
            PitchSemitones         = pitchSt,
            TempoPercent           = tempoPct,
            HighPassHz             = hpfHz,
            LowShelfDb             = lowShelfDb,
            MidGainDb              = midGainDb,
            MidFrequencyHz         = midFreqHz,
            HighShelfDb            = highShelfDb,
            ExciterAmount          = exciter,
            TubeDistortion         = tubeDistortion,
            TubeDistortionDist     = tubeDist,
            TubeDistortionQ        = tubeQ,
            DistortionMode         = distMode,
            DistortionInputGainDb  = distInDb,
            DistortionOutputGainDb = distOutDb,
            BitCrushDepth          = bitCrush,
            ChorusWet              = chorusWet,
            ChorusRateHz           = chorusRateHz,
            ChorusWidth            = chorusWidth,
            VibratoWidth           = vibratoWidth,
            VibratoRateHz          = vibratoRateHz,
            PhaserWet              = phaserWet,
            PhaserRateHz           = phaserRateHz,
            FlangerWet             = flangerWet,
            FlangerRateHz          = flangerRateHz,
            FlangerFeedback        = flangerFb,
            AutoWahWet             = autoWahWet,
            AutoWahMinHz           = autoWahMinHz,
            AutoWahMaxHz           = autoWahMaxHz,
            TremoloDepth           = tremoloDepth,
            TremoloRateHz          = tremoloRateHz,
            EchoDelaySeconds       = echoDelaySec,
            EchoFeedback           = echoFb,
            EchoWet                = echoWet,
            ReverbWet              = reverbWet,
            ReverbRoomSize         = reverbRoom,
            ReverbDamping          = reverbDamp,
            RobotHopSize           = robotHop,
            WhisperHopSize         = whisperHop,
        };

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
