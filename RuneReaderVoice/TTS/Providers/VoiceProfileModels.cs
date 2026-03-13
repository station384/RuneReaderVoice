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

// VoiceProfileModels.cs
// VoiceProfile: synthesis identity (voice, language, speech rate, chunking flag, DSP).
// DspProfile:   post-synthesis audio processing parameters.
//
// DSP is applied AFTER synthesis and BEFORE caching.
// Cache key is built from synthesis-only fields (VoiceId, LangCode, SpeechRate).
// Two slots with the same voice but different DSP hit the same synthesis cache
// and process the PcmAudio differently — this is correct and intentional.
//
// DSP chain order (fixed, implemented in DspFilterChain):
//   Compressor → Pitch → Tempo(WSOLA) → HPF → LowShelf → MidPeak → HighShelf
//   → Exciter → Distortion/Tube → BitCrusher → Chorus → Vibrato → Phaser → Flanger
//   → AutoWah → Echo → Reverb(Freeverb) → Tremolo → Robot → Whisper → Normalize

using System;
using System.Collections.Generic;
using System.Linq;
using NWaves.Effects;

namespace RuneReaderVoice.TTS.Providers;

// ── DspProfile ────────────────────────────────────────────────────────────────

/// <summary>
/// Post-synthesis DSP parameters for a voice slot.
/// All fields are at neutral/disabled defaults — set only what you need.
/// Applied after synthesis, before caching. Chain order is fixed (see DspFilterChain).
/// </summary>
public sealed class DspProfile
{
    // ── Master ────────────────────────────────────────────────────────────────

    /// <summary>When false the entire DSP chain is bypassed (zero cost).</summary>
    public bool Enabled { get; set; } = true;

    // ── Dynamics ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Compressor threshold in dB. 0 = disabled (checked as &lt; 0).
    /// Recommended range: -30 to -6 dB. Levels uneven TTS dynamics before other effects.
    /// </summary>
    public float CompressorThresholdDb { get; set; } = 0f;

    /// <summary>
    /// Compressor ratio. E.g. 4 means 4:1 compression above threshold.
    /// Only used when CompressorThresholdDb &lt; 0.
    /// </summary>
    public float CompressorRatio { get; set; } = 4f;

    // ── Pitch / Tempo ─────────────────────────────────────────────────────────

    /// <summary>
    /// Pitch shift in semitones. 0 = no shift. Range: -12 to +12.
    /// Converted to ratio internally: ratio = 2^(semitones/12).
    /// Uses NWaves PitchShiftVocoderEffect (phase vocoder, online).
    /// </summary>
    public float PitchSemitones { get; set; } = 0f;

    /// <summary>
    /// Tempo change as percent offset. 0 = unchanged. Positive = faster, negative = slower.
    /// Range: -50 to +50. Pitch-corrected via NWaves WSOLA time-stretch.
    /// Independent of SpeechRate (which controls synthesis speed, not playback).
    /// </summary>
    public float TempoPercent { get; set; } = 0f;

    // ── EQ ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// High-pass filter cutoff Hz. 0 = disabled.
    /// Typical: 80–200 Hz to remove low-end mud from deep voices.
    /// </summary>
    public float HighPassHz { get; set; } = 0f;

    /// <summary>Low shelf gain dB at ~200 Hz. Range: -12 to +12. Shapes bass body.</summary>
    public float LowShelfDb { get; set; } = 0f;

    /// <summary>Mid peaking EQ gain dB. Range: -12 to +12. Center set by MidFrequencyHz.</summary>
    public float MidGainDb { get; set; } = 0f;

    /// <summary>Center frequency for mid peaking band. Default 1000 Hz.</summary>
    public float MidFrequencyHz { get; set; } = 1000f;

    /// <summary>High shelf gain dB at ~5000 Hz. Range: -12 to +12. Positive = bright/airy, negative = dark/muffled.</summary>
    public float HighShelfDb { get; set; } = 0f;

    // ── Harmonic Exciter ──────────────────────────────────────────────────────

    /// <summary>
    /// Harmonic exciter amount. 0 = off. Range: 0–1.
    /// High-passes the signal, soft-clips the result, mixes harmonics back in.
    /// Adds clarity and presence — good for voices that sound too clean/thin.
    /// </summary>
    public float ExciterAmount { get; set; } = 0f;

    // ── Distortion ────────────────────────────────────────────────────────────

    /// <summary>
    /// NWaves DistortionMode. Null = distortion off.
    /// SoftClipping: warm saturation. HardClipping: harsh grit.
    /// Exponential: asymmetric drive. FullWaveRectify/HalfWaveRectify: extreme metallic.
    /// Mutually exclusive with TubeDistortion (TubeDistortion takes precedence if both set).
    /// </summary>
    public DistortionMode? DistortionMode { get; set; } = null;

    /// <summary>Distortion input gain dB. Default 12. Higher = more clipping.</summary>
    public float DistortionInputGainDb { get; set; } = 12f;

    /// <summary>Distortion output gain dB. Default -12. Compensates for level increase from clipping.</summary>
    public float DistortionOutputGainDb { get; set; } = -12f;

    /// <summary>
    /// When true, uses NWaves TubeDistortionEffect (DAFX analog tube model) instead of DistortionEffect.
    /// Richer, more musical saturation — good for Orc, Dwarf, coarse voices.
    /// Takes precedence over DistortionMode if both are set.
    /// </summary>
    public bool TubeDistortion { get; set; } = false;

    /// <summary>Tube distortion character. Higher = harder. Default 5. Range: 1–20.</summary>
    public float TubeDistortionDist { get; set; } = 5f;

    /// <summary>
    /// Tube distortion work point Q. Controls linearity at low levels.
    /// More negative = more linear at low volumes. Default -0.2. Range: -1 to 0.
    /// </summary>
    public float TubeDistortionQ { get; set; } = -0.2f;

    // ── BitCrusher ────────────────────────────────────────────────────────────

    /// <summary>
    /// Bit depth for BitCrusherEffect. 0 = disabled.
    /// 16 = barely audible, 8 = lo-fi digital, 4 = extreme glitch.
    /// Great for Gnome/Mechagnome or construct/clockwork voices.
    /// </summary>
    public int BitCrushDepth { get; set; } = 0;

    // ── Chorus ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Chorus wet mix. 0 = off. Range: 0–1.
    /// Blends multiple slightly-detuned copies with dry signal — thickening/layering.
    /// Good for Night Elf, Void Elf, ethereal beings. Uses NWaves ChorusEffect (2 voices).
    /// </summary>
    public float ChorusWet { get; set; } = 0f;

    /// <summary>Chorus LFO rate Hz for both voices. Range: 0.1–4.0. Default 0.5.</summary>
    public float ChorusRateHz { get; set; } = 0.5f;

    /// <summary>Chorus voice width (max delay) in seconds. Default 0.02 (20ms). Range: 0.005–0.04.</summary>
    public float ChorusWidth { get; set; } = 0.02f;

    // ── Vibrato ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Vibrato width in seconds (max pitch delay). 0 = off. Range: 0–0.02.
    /// Pure pitch wobble with no dry blend — unsettling ghost/demon quality.
    /// Stacks with Chorus (they are independent).
    /// Uses NWaves VibratoEffect (Wet=1, Dry=0).
    /// </summary>
    public float VibratoWidth { get; set; } = 0f;

    /// <summary>Vibrato LFO rate Hz. Range: 0.5–10. Default 5.</summary>
    public float VibratoRateHz { get; set; } = 5f;

    // ── Phaser ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phaser wet mix. 0 = off. Range: 0–1.
    /// Sweeping notch filter — psychedelic/otherworldly shimmer.
    /// Good for Nightborne, Void Elf, ethereal spirits. Uses NWaves PhaserEffect.
    /// </summary>
    public float PhaserWet { get; set; } = 0f;

    /// <summary>Phaser LFO rate Hz. Range: 0.1–4.0. Default 1.0.</summary>
    public float PhaserRateHz { get; set; } = 1f;

    /// <summary>Phaser min sweep frequency Hz. Default 300.</summary>
    public float PhaserMinHz { get; set; } = 300f;

    /// <summary>Phaser max sweep frequency Hz. Default 3000.</summary>
    public float PhaserMaxHz { get; set; } = 3000f;

    // ── Flanger ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Flanger wet mix (depth). 0 = off. Range: 0–1.
    /// Short comb delay with LFO — metallic whoosh quality.
    /// Uses NWaves FlangerEffect. Stacks with Chorus and Phaser.
    /// </summary>
    public float FlangerWet { get; set; } = 0f;

    /// <summary>Flanger LFO rate Hz. Default 1.0.</summary>
    public float FlangerRateHz { get; set; } = 1f;

    /// <summary>Flanger feedback amount. Range: 0–0.9. Higher = more resonant metallic tone.</summary>
    public float FlangerFeedback { get; set; } = 0f;

    // ── AutoWah ───────────────────────────────────────────────────────────────

    /// <summary>
    /// AutoWah wet mix. 0 = off. Range: 0–1.
    /// Envelope-following wah — reacts to voice dynamics automatically (no LFO).
    /// Adds a throaty, expressive formant sweep. Good for Troll, Goblin, animated voices.
    /// Uses NWaves AutowahEffect.
    /// </summary>
    public float AutoWahWet { get; set; } = 0f;

    /// <summary>AutoWah min frequency Hz. Default 100.</summary>
    public float AutoWahMinHz { get; set; } = 100f;

    /// <summary>AutoWah max frequency Hz. Default 3000.</summary>
    public float AutoWahMaxHz { get; set; } = 3000f;

    /// <summary>AutoWah Q (resonance). Range: 0.1–1.0. Higher = more pronounced sweep peak.</summary>
    public float AutoWahQ { get; set; } = 0.5f;

    // ── Echo ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Echo delay in seconds. 0 = off.
    /// Adds a single decaying repeat — cave/dungeon ambiance, ancient spirits.
    /// Uses NWaves EchoEffect. Range: 0.05–1.0.
    /// </summary>
    public float EchoDelaySeconds { get; set; } = 0f;

    /// <summary>Echo feedback. Range: 0–0.9. How much each repeat feeds back into the next. Default 0.4.</summary>
    public float EchoFeedback { get; set; } = 0.4f;

    /// <summary>Echo wet mix. Range: 0–1. Default 0.5.</summary>
    public float EchoWet { get; set; } = 0.5f;

    // ── Reverb (hand-rolled Freeverb) ─────────────────────────────────────────

    /// <summary>
    /// Reverb wet/dry mix. 0 = off. Range: 0–1.
    /// Freeverb design (Schroeder comb + allpass). Adds space and depth.
    /// </summary>
    public float ReverbWet { get; set; } = 0f;

    /// <summary>Reverb room size. Range: 0–1. Larger = longer decay tail.</summary>
    public float ReverbRoomSize { get; set; } = 0.5f;

    /// <summary>Reverb damping. Range: 0–1. Higher = warmer/absorbs highs. Lower = cold/bright cave.</summary>
    public float ReverbDamping { get; set; } = 0.5f;

    // ── Tremolo ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Tremolo depth. 0 = off. Range: 0–1.
    /// Amplitude LFO — wavering undead quality, haunted spirit, nervous creature.
    /// Uses NWaves TremoloEffect.
    /// </summary>
    public float TremoloDepth { get; set; } = 0f;

    /// <summary>Tremolo LFO rate Hz. Range: 0.5–12. Default 4.</summary>
    public float TremoloRateHz { get; set; } = 4f;

    // ── Special Spectral ──────────────────────────────────────────────────────

    /// <summary>
    /// Robot effect hop size. 0 = off.
    /// Zeros all STFT phases — robotic monotone quality.
    /// Recommended values: 64–256. Good for Mechanical/Clockwork constructs.
    /// Uses NWaves RobotEffect. Applied after all other effects.
    /// </summary>
    public int RobotHopSize { get; set; } = 0;

    /// <summary>
    /// Whisper effect hop size. 0 = off.
    /// Randomizes all STFT phases — breathy whisper quality.
    /// Recommended: 40–80 (small hop = more whisper-like).
    /// Uses NWaves WhisperEffect. Applied after Robot (if both set, Whisper wins).
    /// </summary>
    public int WhisperHopSize { get; set; } = 0;

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the profile has no active effects and can be skipped entirely.
    /// Checked by DspFilterChain.Apply() before doing any work.
    /// </summary>
    public bool IsNeutral =>
        !Enabled ||
        (CompressorThresholdDb >= 0f &&
         PitchSemitones == 0f && TempoPercent == 0f &&
         HighPassHz == 0f && LowShelfDb == 0f && MidGainDb == 0f && HighShelfDb == 0f &&
         ExciterAmount == 0f &&
         !TubeDistortion && DistortionMode == null &&
         BitCrushDepth == 0 &&
         ChorusWet == 0f && VibratoWidth == 0f &&
         PhaserWet == 0f && FlangerWet == 0f &&
         AutoWahWet == 0f &&
         EchoDelaySeconds == 0f && ReverbWet == 0f &&
         TremoloDepth == 0f &&
         RobotHopSize == 0 && WhisperHopSize == 0);

    public DspProfile Clone() => new()
    {
        Enabled                  = Enabled,
        CompressorThresholdDb    = CompressorThresholdDb,
        CompressorRatio          = CompressorRatio,
        PitchSemitones           = PitchSemitones,
        TempoPercent             = TempoPercent,
        HighPassHz               = HighPassHz,
        LowShelfDb               = LowShelfDb,
        MidGainDb                = MidGainDb,
        MidFrequencyHz           = MidFrequencyHz,
        HighShelfDb              = HighShelfDb,
        ExciterAmount            = ExciterAmount,
        DistortionMode           = DistortionMode,
        DistortionInputGainDb    = DistortionInputGainDb,
        DistortionOutputGainDb   = DistortionOutputGainDb,
        TubeDistortion           = TubeDistortion,
        TubeDistortionDist       = TubeDistortionDist,
        TubeDistortionQ          = TubeDistortionQ,
        BitCrushDepth            = BitCrushDepth,
        ChorusWet                = ChorusWet,
        ChorusRateHz             = ChorusRateHz,
        ChorusWidth              = ChorusWidth,
        VibratoWidth             = VibratoWidth,
        VibratoRateHz            = VibratoRateHz,
        PhaserWet                = PhaserWet,
        PhaserRateHz             = PhaserRateHz,
        PhaserMinHz              = PhaserMinHz,
        PhaserMaxHz              = PhaserMaxHz,
        FlangerWet               = FlangerWet,
        FlangerRateHz            = FlangerRateHz,
        FlangerFeedback          = FlangerFeedback,
        AutoWahWet               = AutoWahWet,
        AutoWahMinHz             = AutoWahMinHz,
        AutoWahMaxHz             = AutoWahMaxHz,
        AutoWahQ                 = AutoWahQ,
        EchoDelaySeconds         = EchoDelaySeconds,
        EchoFeedback             = EchoFeedback,
        EchoWet                  = EchoWet,
        ReverbWet                = ReverbWet,
        ReverbRoomSize           = ReverbRoomSize,
        ReverbDamping            = ReverbDamping,
        TremoloDepth             = TremoloDepth,
        TremoloRateHz            = TremoloRateHz,
        RobotHopSize             = RobotHopSize,
        WhisperHopSize           = WhisperHopSize,
    };

    /// <summary>Returns a disabled DSP profile that bypasses the entire chain.</summary>
    public static DspProfile Neutral() => new() { Enabled = false };

    /// <summary>
    /// Compact deterministic string of all non-neutral DSP field values.
    /// Used as a component of the audio cache key so that different DSP
    /// configurations never share the same cache entry.
    ///
    /// Returns "" when the profile is neutral/bypassed, matching the key
    /// produced by a null DspProfile — both mean "no DSP applied".
    ///
    /// Only fields that actually influence the audio output are included.
    /// Default/neutral values are omitted to keep the string short.
    /// </summary>
    public string BuildCacheKey()
    {
        if (IsNeutral) return "";

        var sb = new System.Text.StringBuilder();

        // Helper: append only when value differs from its neutral default
        void F(string tag, float v, float neutral = 0f)
        { if (v != neutral) sb.Append(tag).Append(v.ToString("G4")).Append(';'); }

        void I(string tag, int v, int neutral = 0)
        { if (v != neutral) sb.Append(tag).Append(v).Append(';'); }

        void B(string tag, bool v)
        { if (v) sb.Append(tag).Append(';'); }

        // Compressor
        F("ct", CompressorThresholdDb);
        F("cr", CompressorRatio, 4f);

        // Pitch / Tempo
        F("ps", PitchSemitones);
        F("tp", TempoPercent);

        // EQ
        F("hp", HighPassHz);
        F("ls", LowShelfDb);
        F("mg", MidGainDb);
        F("mf", MidFrequencyHz, 1000f);
        F("hs", HighShelfDb);
        F("ex", ExciterAmount);

        // Distortion
        B("td", TubeDistortion);
        F("dd", TubeDistortionDist, 5f);
        F("dq", TubeDistortionQ, -0.2f);
        if (DistortionMode.HasValue)
            sb.Append("dm").Append((int)DistortionMode.Value).Append(';');
        F("di", DistortionInputGainDb);
        F("do", DistortionOutputGainDb);
        I("bc", BitCrushDepth);

        // Modulation
        F("cw", ChorusWet);
        F("cr2", ChorusRateHz, 1.5f);
        F("cw2", ChorusWidth, 0.02f);
        F("vw", VibratoWidth);
        F("vr", VibratoRateHz, 2f);
        F("pw", PhaserWet);
        F("pr", PhaserRateHz, 0.5f);
        F("fw", FlangerWet);
        F("fr", FlangerRateHz, 0.5f);
        F("ff", FlangerFeedback, 0.5f);
        F("aw", AutoWahWet);
        F("an", AutoWahMinHz, 300f);
        F("ax", AutoWahMaxHz, 3000f);
        F("tr", TremoloDepth);
        F("tr2", TremoloRateHz, 3f);

        // Time-based
        F("ed", EchoDelaySeconds);
        F("ef", EchoFeedback, 0.4f);
        F("ew", EchoWet);
        F("rw", ReverbWet);
        F("rs", ReverbRoomSize, 0.5f);
        F("rd", ReverbDamping, 0.5f);

        // Spectral
        I("ro", RobotHopSize);
        I("wh", WhisperHopSize);

        return sb.ToString();
    }
}

// ── VoiceProfile ──────────────────────────────────────────────────────────────

public sealed class VoiceProfile
{
    public string VoiceId    { get; set; } = string.Empty;
    public string LangCode   { get; set; } = string.Empty;
    public float  SpeechRate { get; set; } = 1.0f;

    /// <summary>
    /// When true the TextSplitter is skipped for this slot and the full assembled
    /// segment is synthesized as one unit. Useful for slow, deliberate voices
    /// (Tauren, Zandalari, Earthen) where mid-thought phrase breaks hurt prosody.
    /// </summary>
    public bool DisableChunking { get; set; } = false;

    /// <summary>
    /// Post-synthesis DSP chain settings for this slot.
    /// Applied after SynthesizePhraseStreamAsync yields PcmAudio, before StoreAsync.
    /// Null or Enabled=false means bypass.
    /// </summary>
    public DspProfile? Dsp { get; set; } = null;

    public VoiceProfile Clone() => new()
    {
        VoiceId         = VoiceId,
        LangCode        = LangCode,
        SpeechRate      = SpeechRate,
        DisableChunking = DisableChunking,
        Dsp             = Dsp?.Clone(),
    };

    /// <summary>
    /// Synthesis identity key used for cache keying.
    /// DSP is intentionally excluded — DSP is post-synthesis.
    /// DisableChunking is intentionally excluded — it affects routing, not synthesis output.
    /// </summary>
    public string BuildIdentityKey() => $"{VoiceId}|{LangCode}|{SpeechRate:0.00}";
}

// ── EspeakLanguageOption ──────────────────────────────────────────────────────

public sealed class EspeakLanguageOption
{
    public string Code        { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool   IsPinned    { get; init; }

    public override string ToString() => DisplayName;
}

// ── VoiceProfileDefaults ──────────────────────────────────────────────────────

public static class VoiceProfileDefaults
{
    public static string GetDefaultLangCodeForVoice(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return "en-us";

        if (voiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var body  = voiceId[KokoroTtsProvider.MixPrefix.Length..];
            var first = body.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                var colon     = first.IndexOf(':');
                var firstVoice = colon > 0 ? first[..colon] : first;
                return GetDefaultLangCodeForVoice(firstVoice);
            }
        }

        if (voiceId.StartsWith("bf_", StringComparison.OrdinalIgnoreCase) ||
            voiceId.StartsWith("bm_", StringComparison.OrdinalIgnoreCase))
            return "en-gb";

        if (voiceId.StartsWith("jf_", StringComparison.OrdinalIgnoreCase) ||
            voiceId.StartsWith("jm_", StringComparison.OrdinalIgnoreCase))
            return "ja";

        if (voiceId.StartsWith("zf_", StringComparison.OrdinalIgnoreCase) ||
            voiceId.StartsWith("zm_", StringComparison.OrdinalIgnoreCase))
            return "cmn";

        return "en-us";
    }

    public static VoiceProfile Create(string voiceId) => new()
    {
        VoiceId  = voiceId,
        LangCode = GetDefaultLangCodeForVoice(voiceId),
        SpeechRate = 1.0f,
    };
}

// ── EspeakLanguageCatalog ─────────────────────────────────────────────────────

public static class EspeakLanguageCatalog
{
    public static IReadOnlyList<EspeakLanguageOption> All { get; } = Build();

    private static IReadOnlyList<EspeakLanguageOption> Build()
    {
        var list = new List<EspeakLanguageOption>
        {
            Add("en-us",          "English (America)",               true),
            Add("en-gb",          "English (Great Britain)",         true),
            Add("en-gb-scotland", "English (Scotland)",              true),
            Add("en-gb-x-rp",     "English (Received Pronunciation)",true),
            Add("en-029",         "English (Caribbean)",             true),
            Add("en-us-nyc",      "English (America, New York City)",true),
            Add("ja",             "Japanese",                        true),
            Add("cmn",            "Chinese (Mandarin)",              true),
            Add("fr-fr",          "French (France)",                 true),
            Add("sk",             "Slovak",                          true),
            Add("sw",             "Swahili",                        true),

            Add("af",    "Afrikaans"),
            Add("am",    "Amharic"),
            Add("an",    "Aragonese"),
            Add("ar",    "Arabic"),
            Add("as",    "Assamese"),
            Add("az",    "Azerbaijani"),
            Add("ba",    "Bashkir"),
            Add("be",    "Belarusian"),
            Add("bg",    "Bulgarian"),
            Add("bn",    "Bengali"),
            Add("bpy",   "Bishnupriya Manipuri"),
            Add("bs",    "Bosnian"),
            Add("ca",    "Catalan"),
            Add("chr-US-Qaaa-x-west", "Cherokee"),
            Add("cmn-latn-pinyin",    "Chinese (Mandarin, Latin as Pinyin)"),
            Add("cs",    "Czech"),
            Add("cv",    "Chuvash"),
            Add("cy",    "Welsh"),
            Add("da",    "Danish"),
            Add("de",    "German"),
            Add("el",    "Greek"),
            Add("en-gb-x-gbclan", "English (Lancaster)"),
            Add("en-gb-x-gbcwmd", "English (West Midlands)"),
            Add("eo",    "Esperanto"),
            Add("es",    "Spanish (Spain)"),
            Add("es-419","Spanish (Latin America)"),
            Add("et",    "Estonian"),
            Add("eu",    "Basque"),
            Add("fa",    "Persian"),
            Add("fa-latn","Persian (Pinglish)"),
            Add("fi",    "Finnish"),
            Add("fr-be", "French (Belgium)"),
            Add("fr-ch", "French (Switzerland)"),
            Add("ga",    "Gaelic (Irish)"),
            Add("gd",    "Gaelic (Scottish)"),
            Add("gn",    "Guarani"),
            Add("grc",   "Greek (Ancient)"),
            Add("gu",    "Gujarati"),
            Add("hak",   "Hakka Chinese"),
            Add("haw",   "Hawaiian"),
            Add("he",    "Hebrew"),
            Add("hi",    "Hindi"),
            Add("hr",    "Croatian"),
            Add("ht",    "Haitian Creole"),
            Add("hu",    "Hungarian"),
            Add("hy",    "Armenian (East Armenia)"),
            Add("hyw",   "Armenian (West Armenia)"),
            Add("ia",    "Interlingua"),
            Add("id",    "Indonesian"),
            Add("io",    "Ido"),
            Add("is",    "Icelandic"),
            Add("it",    "Italian"),
            Add("jbo",   "Lojban"),
            Add("ka",    "Georgian"),
            Add("kk",    "Kazakh"),
            Add("kl",    "Greenlandic"),
            Add("kn",    "Kannada"),
            Add("ko",    "Korean"),
            Add("kok",   "Konkani"),
            Add("ku",    "Kurdish"),
            Add("ky",    "Kyrgyz"),
            Add("la",    "Latin"),
            Add("lb",    "Luxembourgish"),
            Add("lfn",   "Lingua Franca Nova"),
            Add("lt",    "Lithuanian"),
            Add("ltg",   "Latgalian"),
            Add("lv",    "Latvian"),
            Add("mi",    "Māori"),
            Add("mk",    "Macedonian"),
            Add("ml",    "Malayalam"),
            Add("mr",    "Marathi"),
            Add("ms",    "Malay"),
            Add("mt",    "Maltese"),
            Add("my",    "Myanmar (Burmese)"),
            Add("nb",    "Norwegian Bokmål"),
            Add("nci",   "Nahuatl (Classical)"),
            Add("ne",    "Nepali"),
            Add("nl",    "Dutch"),
            Add("nog",   "Nogai"),
            Add("om",    "Oromo"),
            Add("or",    "Oriya"),
            Add("pa",    "Punjabi"),
            Add("pap",   "Papiamento"),
            Add("piqd",  "Klingon"),
            Add("pl",    "Polish"),
            Add("pt",    "Portuguese (Portugal)"),
            Add("pt-br", "Portuguese (Brazil)"),
            Add("py",    "Pyash"),
            Add("qdb",   "Lang Belta"),
            Add("qu",    "Quechua"),
            Add("quc",   "K'iche'"),
            Add("qya",   "Quenya"),
            Add("ro",    "Romanian"),
            Add("ru",    "Russian"),
            Add("ru-lv", "Russian (Latvia)"),
            Add("sd",    "Sindhi"),
            Add("shn",   "Shan (Tai Yai)"),
            Add("si",    "Sinhala"),
            Add("sjn",   "Sindarin"),
            Add("sk",    "Slovak"),
            Add("sl",    "Slovenian"),
            Add("smj",   "Lule Saami"),
            Add("sq",    "Albanian"),
            Add("sr",    "Serbian"),
            Add("sv",    "Swedish"),
            Add("ta",    "Tamil"),
            Add("te",    "Telugu"),
            Add("th",    "Thai"),
            Add("tk",    "Turkmen"),
            Add("tn",    "Setswana"),
            Add("tr",    "Turkish"),
            Add("tt",    "Tatar"),
            Add("ug",    "Uyghur"),
            Add("uk",    "Ukrainian"),
            Add("ur",    "Urdu"),
            Add("uz",    "Uzbek"),
            Add("vi",    "Vietnamese (Northern)"),
            Add("vi-vn-x-central", "Vietnamese (Central)"),
            Add("vi-vn-x-south",   "Vietnamese (Southern)"),
            Add("yue",             "Chinese (Cantonese)"),
            Add("yue-latn-jyutping","Chinese (Cantonese, Latin as Jyutping)"),
        };

        var pinned = list.Where(x => x.IsPinned);
        var rest   = list.Where(x => !x.IsPinned)
                         .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase);

        return pinned.Concat(rest).ToList();
    }

    private static EspeakLanguageOption Add(string code, string displayName, bool pinned = false)
        => new() { Code = code, DisplayName = displayName, IsPinned = pinned };
}
