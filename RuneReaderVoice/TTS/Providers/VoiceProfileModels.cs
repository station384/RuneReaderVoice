// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3 of the License.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.


using System;
using System.Collections.Generic;
using System.Linq;
using NWaves.Effects;

namespace RuneReaderVoice.TTS.Providers;

// VoiceProfileModels.cs
// VoiceProfile: synthesis identity (voice, language, speech rate, chunking flag, DSP).
// DspProfile:   post-synthesis audio processing parameters.
//
// DSP chain order is user-defined: DspProfile.Effects is an ordered list.
// Each DspEffectItem has a Kind, an Enabled flag, and a string->float Params dict.
// DspFilterChain iterates Effects in order, applying only enabled items.

// -- DspEffectKind -------------------------------------------------------------

public enum DspEffectKind
{
    // Dynamics
    EvenOut,        // Compressor.   params: threshold, ratio
    Level,          // Normalize.    params: (none)
    // Pitch / Time
    Pitch,          // PitchShift.   params: semitones
    Speed,          // Tempo.        params: percent
    // Tone (EQ bands)
    RumbleRemover,  // HighPass.     params: hz
    Bass,           // LowShelf.     params: db
    Presence,       // MidPeak.      params: db, hz
    Brightness,     // HighShelf.    params: db
    Air,            // Exciter.      params: amount
    // Distortion
    Grit,           // Distortion.   params: mode, inDb, outDb
    WarmthGrit,     // TubeDistort.  params: drive, q
    LoFi,           // BitCrusher.   params: bits
    // Modulation
    Thickness,      // Chorus.       params: wet, rate, width
    Wobble,         // Vibrato.      params: width, rate
    Swirl,          // Phaser.       params: wet, rate, minHz, maxHz
    Jet,            // Flanger.      params: wet, rate, feedback
    Wah,            // AutoWah.      params: wet, minHz, maxHz
    Tremor,         // Tremolo.      params: depth, rate
    // Space
    Room,           // Reverb.       params: wet, roomSize, damping
    Echo,           // Echo.         params: delay, feedback, wet
    // Character
    Robot,          // RobotEffect.  params: strength (0-3)
    Whisper,        // Whisper.      params: strength (0-3)
}

// -- DspEffectItem -------------------------------------------------------------

public sealed class DspEffectItem
{
    public DspEffectKind             Kind    { get; set; }
    public bool                      Enabled { get; set; } = true;
    public Dictionary<string, float> Params  { get; set; } = new();

    public float Get(string key, float defaultValue = 0f)
        => Params.TryGetValue(key, out var v) ? v : defaultValue;

    public void Set(string key, float value) => Params[key] = value;

    public DspEffectItem Clone() => new()
    {
        Kind    = Kind,
        Enabled = Enabled,
        Params  = new Dictionary<string, float>(Params),
    };

    public static DspEffectItem CreateDefault(DspEffectKind kind)
    {
        var item = new DspEffectItem { Kind = kind };
        switch (kind)
        {
            case DspEffectKind.EvenOut:
                item.Set("threshold", -18f); item.Set("ratio", 4f); break;
            case DspEffectKind.Level:
                break;
            case DspEffectKind.Pitch:
                item.Set("semitones", 0f); break;
            case DspEffectKind.Speed:
                item.Set("percent", 0f); break;
            case DspEffectKind.RumbleRemover:
                item.Set("hz", 100f); break;
            case DspEffectKind.Bass:
                item.Set("db", 0f); break;
            case DspEffectKind.Presence:
                item.Set("db", 0f); item.Set("hz", 1000f); break;
            case DspEffectKind.Brightness:
                item.Set("db", 0f); break;
            case DspEffectKind.Air:
                item.Set("amount", 0.3f); break;
            case DspEffectKind.Grit:
                item.Set("mode", 1f); item.Set("inDb", 12f); item.Set("outDb", -12f); break;
            case DspEffectKind.WarmthGrit:
                item.Set("drive", 5f); item.Set("q", -0.2f); break;
            case DspEffectKind.LoFi:
                item.Set("bits", 8f); break;
            case DspEffectKind.Thickness:
                item.Set("wet", 0.5f); item.Set("rate", 1.5f); item.Set("width", 0.02f); break;
            case DspEffectKind.Wobble:
                item.Set("width", 0.005f); item.Set("rate", 3f); break;
            case DspEffectKind.Swirl:
                item.Set("wet", 0.5f); item.Set("rate", 1f); item.Set("minHz", 300f); item.Set("maxHz", 3000f); break;
            case DspEffectKind.Jet:
                item.Set("wet", 0.5f); item.Set("rate", 1f); item.Set("feedback", 0.5f); break;
            case DspEffectKind.Wah:
                item.Set("wet", 0.5f); item.Set("minHz", 300f); item.Set("maxHz", 3000f); break;
            case DspEffectKind.Tremor:
                item.Set("depth", 0.5f); item.Set("rate", 4f); break;
            case DspEffectKind.Room:
                item.Set("wet", 0.3f); item.Set("roomSize", 0.5f); item.Set("damping", 0.5f); break;
            case DspEffectKind.Echo:
                item.Set("delay", 0.3f); item.Set("feedback", 0.4f); item.Set("wet", 0.5f); break;
            case DspEffectKind.Robot:
                item.Set("strength", 2f); break;
            case DspEffectKind.Whisper:
                item.Set("strength", 1f); break;
        }
        return item;
    }

    public static string DisplayName(DspEffectKind kind) => kind switch
    {
        DspEffectKind.EvenOut       => "Even Out",
        DspEffectKind.Level         => "Level",
        DspEffectKind.Pitch         => "Pitch",
        DspEffectKind.Speed         => "Speed",
        DspEffectKind.RumbleRemover => "Rumble Remover",
        DspEffectKind.Bass          => "Bass",
        DspEffectKind.Presence      => "Presence",
        DspEffectKind.Brightness    => "Brightness",
        DspEffectKind.Air           => "Air",
        DspEffectKind.Grit          => "Grit",
        DspEffectKind.WarmthGrit    => "Warmth Grit",
        DspEffectKind.LoFi          => "Lo-Fi",
        DspEffectKind.Thickness     => "Thickness",
        DspEffectKind.Wobble        => "Wobble",
        DspEffectKind.Swirl         => "Swirl",
        DspEffectKind.Jet           => "Jet",
        DspEffectKind.Wah           => "Wah",
        DspEffectKind.Tremor        => "Tremor",
        DspEffectKind.Room          => "Room",
        DspEffectKind.Echo          => "Echo",
        DspEffectKind.Robot         => "Robot",
        DspEffectKind.Whisper       => "Whisper",
        _                           => kind.ToString(),
    };

    public static string Description(DspEffectKind kind) => kind switch
    {
        DspEffectKind.EvenOut       => "Keeps loud and quiet parts at a more consistent volume.",
        DspEffectKind.Level         => "Brings the final volume to a consistent peak level.",
        DspEffectKind.Pitch         => "Makes the voice higher or lower in pitch.",
        DspEffectKind.Speed         => "Makes speech faster or slower without changing pitch.",
        DspEffectKind.RumbleRemover => "Cleans up low rumbling background noise and boxiness.",
        DspEffectKind.Bass          => "Adds warmth and body, or thins out a heavy voice.",
        DspEffectKind.Presence      => "Makes the voice cut through more, or sit further back.",
        DspEffectKind.Brightness    => "Makes the voice sound crisper and clearer, or softer and duller.",
        DspEffectKind.Air           => "Adds a subtle sparkle and openness to the top of the voice.",
        DspEffectKind.Grit          => "Adds roughness and edge — from mild crunch to harsh distortion.",
        DspEffectKind.WarmthGrit    => "Softer, warmer grit — like an old radio or tube amplifier.",
        DspEffectKind.LoFi          => "Deliberately degrades the sound — robotic, old game, or walkie-talkie feel.",
        DspEffectKind.Thickness     => "Makes the voice sound fuller, like multiple people speaking together.",
        DspEffectKind.Wobble        => "Adds a wavering pitch effect — ghostly or unsettling quality.",
        DspEffectKind.Swirl         => "A sweeping, otherworldly shimmer — psychedelic sci-fi feel.",
        DspEffectKind.Jet           => "A metallic whooshing effect — mechanical or alien quality.",
        DspEffectKind.Wah           => "The voice opens and closes dynamically as it speaks — throaty and expressive.",
        DspEffectKind.Tremor        => "Volume pulses in and out rhythmically — haunted or nervous quality.",
        DspEffectKind.Room          => "Makes it sound like the voice is speaking in a larger space.",
        DspEffectKind.Echo          => "Adds a decaying repeat of the voice — cave or dungeon ambiance.",
        DspEffectKind.Robot         => "Makes the voice sound mechanical and synthetic.",
        DspEffectKind.Whisper       => "Breathes the voice into a breathy, whispery texture.",
        _                           => string.Empty,
    };

    public static string Category(DspEffectKind kind) => kind switch
    {
        DspEffectKind.EvenOut or DspEffectKind.Level                           => "Dynamics",
        DspEffectKind.Pitch or DspEffectKind.Speed                             => "Pitch & Time",
        DspEffectKind.RumbleRemover or DspEffectKind.Bass or DspEffectKind.Presence
            or DspEffectKind.Brightness or DspEffectKind.Air                   => "Tone",
        DspEffectKind.Grit or DspEffectKind.WarmthGrit or DspEffectKind.LoFi  => "Distortion",
        DspEffectKind.Thickness or DspEffectKind.Wobble or DspEffectKind.Swirl
            or DspEffectKind.Jet or DspEffectKind.Wah or DspEffectKind.Tremor => "Modulation",
        DspEffectKind.Room or DspEffectKind.Echo                               => "Space",
        DspEffectKind.Robot or DspEffectKind.Whisper                           => "Character",
        _                                                                       => "Other",
    };

    public string BuildCacheKey()
    {
        if (!Enabled) return string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.Append((int)Kind);
        foreach (var kv in Params.OrderBy(k => k.Key))
            sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value.ToString("G4"));
        return sb.ToString();
    }
}

// -- DspProfile ----------------------------------------------------------------

public sealed class DspProfile
{
    public bool                Enabled { get; set; } = true;
    public List<DspEffectItem> Effects { get; set; } = new();

    public bool IsNeutral => !Enabled || Effects.Count == 0
                          || Effects.All(e => !e.Enabled);

    public DspProfile Clone() => new()
    {
        Enabled = Enabled,
        Effects = Effects.Select(e => e.Clone()).ToList(),
    };

    public static DspProfile Neutral() => new() { Enabled = false };

    public string BuildCacheKey()
    {
        if (IsNeutral) return string.Empty;
        var parts = Effects.Where(e => e.Enabled).Select(e => e.BuildCacheKey());
        return string.Join("+", parts);
    }
}

// -- VoiceProfile --------------------------------------------------------------

public sealed class VoiceProfile
{
    public string VoiceId      { get; set; } = string.Empty;
    public string LangCode     { get; set; } = string.Empty;
    public float  SpeechRate   { get; set; } = 1.0f;
    public float? CfgWeight    { get; set; } = null;
    public float? Exaggeration { get; set; } = null;

    public float? CfgStrength       { get; set; } = null;
    public int?   NfeStep           { get; set; } = null;
    public float? CrossFadeDuration { get; set; } = null;
    public float? SwaysamplingCoef  { get; set; } = null;

    public string? VoiceInstruct         { get; set; } = null;
    public string? CosyInstruct          { get; set; } = null;
    public int?    SynthesisSeed         { get; set; } = null;
    public float?  ChatterboxTemperature { get; set; } = null;
    public float?  ChatterboxTopP        { get; set; } = null;
    public float?  ChatterboxRepetitionPenalty { get; set; } = null;
    public int?    LongcatSteps          { get; set; } = null;
    public float?  LongcatCfgStrength    { get; set; } = null;
    public string? LongcatGuidance       { get; set; } = null;

    public bool        DisableChunking { get; set; } = false;
    public DspProfile? Dsp             { get; set; } = null;

    public VoiceProfile Clone() => new()
    {
        VoiceId                    = VoiceId,
        LangCode                   = LangCode,
        SpeechRate                 = SpeechRate,
        CfgWeight                  = CfgWeight,
        Exaggeration               = Exaggeration,
        CfgStrength                = CfgStrength,
        NfeStep                    = NfeStep,
        CrossFadeDuration          = CrossFadeDuration,
        SwaysamplingCoef           = SwaysamplingCoef,
        VoiceInstruct              = VoiceInstruct,
        CosyInstruct               = CosyInstruct,
        SynthesisSeed              = SynthesisSeed,
        ChatterboxTemperature      = ChatterboxTemperature,
        ChatterboxTopP             = ChatterboxTopP,
        ChatterboxRepetitionPenalty = ChatterboxRepetitionPenalty,
        LongcatSteps               = LongcatSteps,
        LongcatCfgStrength         = LongcatCfgStrength,
        LongcatGuidance            = LongcatGuidance,
        DisableChunking            = DisableChunking,
        Dsp                        = Dsp?.Clone(),
    };

    public void CopyCacheAffectingFieldsFrom(VoiceProfile source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        VoiceId                     = source.VoiceId;
        LangCode                    = source.LangCode;
        SpeechRate                  = source.SpeechRate;
        CfgWeight                   = source.CfgWeight;
        Exaggeration                = source.Exaggeration;
        CfgStrength                 = source.CfgStrength;
        NfeStep                     = source.NfeStep;
        CrossFadeDuration           = source.CrossFadeDuration;
        SwaysamplingCoef            = source.SwaysamplingCoef;
        VoiceInstruct               = source.VoiceInstruct;
        CosyInstruct                = source.CosyInstruct;
        SynthesisSeed               = source.SynthesisSeed;
        ChatterboxTemperature       = source.ChatterboxTemperature;
        ChatterboxTopP              = source.ChatterboxTopP;
        ChatterboxRepetitionPenalty = source.ChatterboxRepetitionPenalty;
        LongcatSteps                = source.LongcatSteps;
        LongcatCfgStrength          = source.LongcatCfgStrength;
        LongcatGuidance             = source.LongcatGuidance;
        DisableChunking             = source.DisableChunking;
    }

    public bool CacheAffectingEquals(VoiceProfile? other)
    {
        if (other == null) return false;

        return string.Equals(VoiceId, other.VoiceId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(LangCode, other.LangCode, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(SpeechRate - other.SpeechRate) < 0.0001f
            && Nullable.Equals(CfgWeight, other.CfgWeight)
            && Nullable.Equals(Exaggeration, other.Exaggeration)
            && Nullable.Equals(CfgStrength, other.CfgStrength)
            && Nullable.Equals(NfeStep, other.NfeStep)
            && Nullable.Equals(CrossFadeDuration, other.CrossFadeDuration)
            && Nullable.Equals(SwaysamplingCoef, other.SwaysamplingCoef)
            && string.Equals((VoiceInstruct ?? string.Empty).Trim(), (other.VoiceInstruct ?? string.Empty).Trim(), StringComparison.Ordinal)
            && string.Equals((CosyInstruct ?? string.Empty).Trim(), (other.CosyInstruct ?? string.Empty).Trim(), StringComparison.Ordinal)
            && Nullable.Equals(SynthesisSeed, other.SynthesisSeed)
            && Nullable.Equals(ChatterboxTemperature, other.ChatterboxTemperature)
            && Nullable.Equals(ChatterboxTopP, other.ChatterboxTopP)
            && Nullable.Equals(ChatterboxRepetitionPenalty, other.ChatterboxRepetitionPenalty)
            && Nullable.Equals(LongcatSteps, other.LongcatSteps)
            && Nullable.Equals(LongcatCfgStrength, other.LongcatCfgStrength)
            && string.Equals((LongcatGuidance ?? string.Empty).Trim(), (other.LongcatGuidance ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)
            && DisableChunking == other.DisableChunking;
    }

    public string BuildIdentityKey()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var cfg      = CfgWeight.HasValue ? CfgWeight.Value.ToString("0.00", inv) : "-";
        var exg      = Exaggeration.HasValue ? Exaggeration.Value.ToString("0.00", inv) : "-";
        var cfs      = CfgStrength.HasValue ? CfgStrength.Value.ToString("0.00", inv) : "-";
        var nfe      = NfeStep.HasValue ? NfeStep.Value.ToString() : "-";
        var sway     = SwaysamplingCoef.HasValue ? SwaysamplingCoef.Value.ToString("0.00", inv) : "-";
        var vInstr   = string.IsNullOrWhiteSpace(VoiceInstruct) ? "-" : VoiceInstruct.Trim().Replace("|", "/");
        var cInstr   = string.IsNullOrWhiteSpace(CosyInstruct) ? "-" : CosyInstruct.Trim().Replace("|", "/");
        var seed     = SynthesisSeed.HasValue ? SynthesisSeed.Value.ToString(inv) : "-";
        var cbTemp   = ChatterboxTemperature.HasValue ? ChatterboxTemperature.Value.ToString("0.00", inv) : "-";
        var cbTopP   = ChatterboxTopP.HasValue ? ChatterboxTopP.Value.ToString("0.00", inv) : "-";
        var cbRep    = ChatterboxRepetitionPenalty.HasValue ? ChatterboxRepetitionPenalty.Value.ToString("0.00", inv) : "-";
        var lcSteps  = LongcatSteps.HasValue ? LongcatSteps.Value.ToString(inv) : "-";
        var lcCfg    = LongcatCfgStrength.HasValue ? LongcatCfgStrength.Value.ToString("0.00", inv) : "-";
        var lcGuide  = string.IsNullOrWhiteSpace(LongcatGuidance) ? "-" : LongcatGuidance.Trim().ToLowerInvariant().Replace("|", "/");
        return $"{VoiceId}|{LangCode}|{SpeechRate:0.00}|cfg:{cfg}|ex:{exg}|cfs:{cfs}|nfe:{nfe}|sway:{sway}|vinstr:{vInstr}|cinstr:{cInstr}|seed:{seed}|cbt:{cbTemp}|cbp:{cbTopP}|cbr:{cbRep}|lcs:{lcSteps}|lcc:{lcCfg}|lcg:{lcGuide}";
    }
}

// -- EspeakLanguageOption ------------------------------------------------------

public sealed class EspeakLanguageOption
{
    public string Code        { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool   IsPinned    { get; init; }
    public override string ToString() => DisplayName;
}

// -- VoiceProfileDefaults ------------------------------------------------------

public static class VoiceProfileDefaults
{
    public static string GetDefaultLangCodeForVoice(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId)) return "en-us";
        if (voiceId.Contains("-en-us", StringComparison.OrdinalIgnoreCase)) return "en-us";
        if (voiceId.Contains("-en-gb", StringComparison.OrdinalIgnoreCase)) return "en-gb";
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
            voiceId.StartsWith("bm_", StringComparison.OrdinalIgnoreCase)) return "en-gb";
        if (voiceId.StartsWith("jf_", StringComparison.OrdinalIgnoreCase) ||
            voiceId.StartsWith("jm_", StringComparison.OrdinalIgnoreCase)) return "ja";
        if (voiceId.StartsWith("zf_", StringComparison.OrdinalIgnoreCase) ||
            voiceId.StartsWith("zm_", StringComparison.OrdinalIgnoreCase)) return "cmn";
        return "en-us";
    }

    public static VoiceProfile Create(string voiceId) => new()
    {
        VoiceId    = voiceId,
        LangCode   = GetDefaultLangCodeForVoice(voiceId),
        SpeechRate = 1.0f,
    };
}

// -- EspeakLanguageCatalog -----------------------------------------------------

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
            Add("af",    "Afrikaans"), Add("am",    "Amharic"),     Add("an",    "Aragonese"),
            Add("ar",    "Arabic"),   Add("as",    "Assamese"),     Add("az",    "Azerbaijani"),
            Add("ba",    "Bashkir"),  Add("be",    "Belarusian"),   Add("bg",    "Bulgarian"),
            Add("bn",    "Bengali"),  Add("bpy",   "Bishnupriya Manipuri"), Add("bs", "Bosnian"),
            Add("ca",    "Catalan"),  Add("chr-US-Qaaa-x-west", "Cherokee"),
            Add("cmn-latn-pinyin", "Chinese (Mandarin, Latin as Pinyin)"),
            Add("cs",  "Czech"),    Add("cv",  "Chuvash"),  Add("cy",  "Welsh"),
            Add("da",  "Danish"),   Add("de",  "German"),   Add("el",  "Greek"),
            Add("en-gb-x-gbclan", "English (Lancaster)"), Add("en-gb-x-gbcwmd", "English (West Midlands)"),
            Add("eo",  "Esperanto"), Add("es",  "Spanish (Spain)"), Add("es-419","Spanish (Latin America)"),
            Add("et",  "Estonian"), Add("eu",  "Basque"),   Add("fa",  "Persian"),
            Add("fa-latn","Persian (Pinglish)"), Add("fi",  "Finnish"),
            Add("fr-be","French (Belgium)"), Add("fr-ch","French (Switzerland)"),
            Add("ga",  "Gaelic (Irish)"), Add("gd",  "Gaelic (Scottish)"), Add("gn",  "Guarani"),
            Add("grc", "Greek (Ancient)"), Add("gu",  "Gujarati"), Add("hak", "Hakka Chinese"),
            Add("haw", "Hawaiian"), Add("he",  "Hebrew"), Add("hi",  "Hindi"),
            Add("hr",  "Croatian"), Add("ht",  "Haitian Creole"), Add("hu",  "Hungarian"),
            Add("hy",  "Armenian (East Armenia)"), Add("hyw","Armenian (West Armenia)"),
            Add("ia",  "Interlingua"), Add("id", "Indonesian"), Add("io",  "Ido"),
            Add("is",  "Icelandic"), Add("it",  "Italian"), Add("jbo", "Lojban"),
            Add("ka",  "Georgian"), Add("kk",  "Kazakh"), Add("kl",  "Greenlandic"),
            Add("kn",  "Kannada"), Add("ko",   "Korean"), Add("kok", "Konkani"),
            Add("ku",  "Kurdish"), Add("ky",   "Kyrgyz"), Add("la",  "Latin"),
            Add("lb",  "Luxembourgish"), Add("lfn","Lingua Franca Nova"),
            Add("lt",  "Lithuanian"), Add("ltg","Latgalian"), Add("lv",  "Latvian"),
            Add("mi",  "Māori"), Add("mk",  "Macedonian"), Add("ml",  "Malayalam"),
            Add("mr",  "Marathi"), Add("ms", "Malay"), Add("mt", "Maltese"),
            Add("my",  "Myanmar (Burmese)"), Add("nb","Norwegian Bokmål"),
            Add("nci", "Nahuatl (Classical)"), Add("ne","Nepali"), Add("nl","Dutch"),
            Add("nog", "Nogai"), Add("om",  "Oromo"), Add("or",  "Oriya"),
            Add("pa",  "Punjabi"), Add("pap","Papiamento"), Add("piqd","Klingon"),
            Add("pl",  "Polish"), Add("pt", "Portuguese (Portugal)"), Add("pt-br","Portuguese (Brazil)"),
            Add("py",  "Pyash"), Add("qdb","Lang Belta"), Add("qu", "Quechua"),
            Add("quc", "K'iche'"), Add("qya","Quenya"), Add("ro","Romanian"),
            Add("ru",  "Russian"), Add("ru-lv","Russian (Latvia)"), Add("sd","Sindhi"),
            Add("shn", "Shan (Tai Yai)"), Add("si","Sinhala"), Add("sjn","Sindarin"),
            Add("sk",  "Slovak"), Add("sl", "Slovenian"), Add("smj","Lule Saami"),
            Add("sq",  "Albanian"), Add("sr","Serbian"), Add("sv","Swedish"),
            Add("ta",  "Tamil"), Add("te", "Telugu"), Add("th","Thai"),
            Add("tk",  "Turkmen"), Add("tn","Setswana"), Add("tr","Turkish"),
            Add("tt",  "Tatar"), Add("ug", "Uyghur"), Add("uk","Ukrainian"),
            Add("ur",  "Urdu"), Add("uz",  "Uzbek"),
            Add("vi",  "Vietnamese (Northern)"), Add("vi-vn-x-central","Vietnamese (Central)"),
            Add("vi-vn-x-south","Vietnamese (Southern)"),
            Add("yue", "Chinese (Cantonese)"), Add("yue-latn-jyutping","Chinese (Cantonese, Latin as Jyutping)"),
        };
        var pinned = list.Where(x => x.IsPinned);
        var rest   = list.Where(x => !x.IsPinned).OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase);
        return pinned.Concat(rest).ToList();
    }

    private static EspeakLanguageOption Add(string code, string displayName, bool pinned = false)
        => new() { Code = code, DisplayName = displayName, IsPinned = pinned };
}

// -- VoiceProfileExport --------------------------------------------------------

public sealed class VoiceProfileExport
{
    public string ProviderId { get; set; } = string.Empty;
    public Dictionary<string, VoiceProfile> Profiles { get; set; } = new();
}

public sealed class MultiProviderVoiceProfileExport
{
    public Dictionary<string, Dictionary<string, VoiceProfile>> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class MultiProviderSampleProfileExport
{
    public Dictionary<string, Dictionary<string, VoiceProfile>> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}