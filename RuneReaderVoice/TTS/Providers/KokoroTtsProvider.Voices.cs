// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

using System;
using System.Collections.Generic;
using KokoroSharp;
using KokoroSharp.Core;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed partial class KokoroTtsProvider
{
    public static readonly VoiceInfo[] KnownVoices =
    {
        new() { VoiceId = "af_heart",   Name = "Heart ★ (AF)",    Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_bella",   Name = "Bella ★ (AF)",    Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_nicole",  Name = "Nicole (AF)",     Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_sarah",   Name = "Sarah (AF)",      Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_aoede",   Name = "Aoede (AF)",      Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_kore",    Name = "Kore (AF)",       Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_alloy",   Name = "Alloy (AF)",      Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_nova",    Name = "Nova (AF)",       Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_sky",     Name = "Sky (AF)",        Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_jessica", Name = "Jessica (AF)",    Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_river",   Name = "River (AF)",      Language = "en-US", Gender = Gender.Female },

        new() { VoiceId = "am_michael", Name = "Michael (AM)",    Language = "en-US", Gender = Gender.Male },
        new() { VoiceId = "am_fenrir",  Name = "Fenrir (AM)",     Language = "en-US", Gender = Gender.Male },
        new() { VoiceId = "am_puck",    Name = "Puck (AM)",       Language = "en-US", Gender = Gender.Male },
        new() { VoiceId = "am_echo",    Name = "Echo (AM)",       Language = "en-US", Gender = Gender.Male },
        new() { VoiceId = "am_eric",    Name = "Eric (AM)",       Language = "en-US", Gender = Gender.Male },
        new() { VoiceId = "am_liam",    Name = "Liam (AM)",       Language = "en-US", Gender = Gender.Male },
        new() { VoiceId = "am_onyx",    Name = "Onyx (AM)",       Language = "en-US", Gender = Gender.Male },
        new() { VoiceId = "am_adam",    Name = "Adam (AM)",       Language = "en-US", Gender = Gender.Male },
        new() { VoiceId = "am_santa",   Name = "Santa (AM)",      Language = "en-US", Gender = Gender.Male },

        new() { VoiceId = "bf_emma",     Name = "Emma (BF)",      Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bf_isabella", Name = "Isabella (BF)",  Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bf_alice",    Name = "Alice (BF)",     Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bf_lily",     Name = "Lily (BF)",      Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bf_fable",    Name = "Fable (BF)",     Language = "en-GB", Gender = Gender.Female },

        new() { VoiceId = "bm_george",   Name = "George (BM)",    Language = "en-GB", Gender = Gender.Male },
        new() { VoiceId = "bm_fable",    Name = "Fable (BM)",     Language = "en-GB", Gender = Gender.Male },
        new() { VoiceId = "bm_daniel",   Name = "Daniel (BM)",    Language = "en-GB", Gender = Gender.Male },
        new() { VoiceId = "bm_lewis",    Name = "Lewis (BM)",     Language = "en-GB", Gender = Gender.Male },

        new() { VoiceId = "jf_alpha",      Name = "Alpha (JF)",      Language = "ja", Gender = Gender.Female },
        new() { VoiceId = "jf_gongitsune", Name = "Gongitsune (JF)", Language = "ja", Gender = Gender.Female },
        new() { VoiceId = "jf_nezumi",     Name = "Nezumi (JF)",     Language = "ja", Gender = Gender.Female },
        new() { VoiceId = "jf_tebukuro",   Name = "Tebukuro (JF)",   Language = "ja", Gender = Gender.Female },
        new() { VoiceId = "jm_kumo",       Name = "Kumo (JM)",       Language = "ja", Gender = Gender.Male },

        new() { VoiceId = "zf_xiaobei",  Name = "Xiaobei (ZF)",  Language = "zh", Gender = Gender.Female },
        new() { VoiceId = "zf_xiaoni",   Name = "Xiaoni (ZF)",   Language = "zh", Gender = Gender.Female },
        new() { VoiceId = "zf_xiaoxiao", Name = "Xiaoxiao (ZF)", Language = "zh", Gender = Gender.Female },
        new() { VoiceId = "zf_xiaoyi",   Name = "Xiaoyi (ZF)",   Language = "zh", Gender = Gender.Female },
        new() { VoiceId = "zm_yunjian",  Name = "Yunjian (ZM)",  Language = "zh", Gender = Gender.Male },
        new() { VoiceId = "zm_yunxi",    Name = "Yunxi (ZM)",    Language = "zh", Gender = Gender.Male },
        new() { VoiceId = "zm_yunxia",   Name = "Yunxia (ZM)",   Language = "zh", Gender = Gender.Male },
        new() { VoiceId = "zm_yunyang",  Name = "Yunyang (ZM)",  Language = "zh", Gender = Gender.Male },
    };

    public const string MixPrefix = "mix:";
    public const string DefaultVoiceId = "af_heart";

    public IReadOnlyList<VoiceInfo> GetAvailableVoices() => KnownVoices;

    public void SetVoice(VoiceSlot slot, string voiceId)
        => _voiceProfiles[slot] = VoiceProfileDefaults.Create(voiceId);

    public void SetVoiceProfile(VoiceSlot slot, VoiceProfile profile)
        => _voiceProfiles[slot] = profile.Clone();

    public VoiceProfile ResolveVoiceProfile(VoiceSlot slot)
    {
        if (_voiceProfiles.TryGetValue(slot, out var profile))
            return profile;

        return GetDefaultProfile(slot);
    }

    public string ResolveVoiceId(VoiceSlot slot)
        => ResolveVoiceProfile(slot).VoiceId;

    /// <summary>ITtsProvider: returns the full profile (including DSP + DisableChunking).</summary>
    public VoiceProfile? ResolveProfile(VoiceSlot slot)
        => ResolveVoiceProfile(slot);

    private VoiceProfile GetDefaultProfile(VoiceSlot slot)
    {
        // Prefer the recommended preset from the catalog — it includes DisableChunking + voice mixes.
        var preset = SpeakerPresetCatalog.GetRecommendedForSlot(slot);
        if (preset != null)
            return preset.Profile.Clone();

        if (slot.Group == AccentGroup.Narrator)
            return VoiceProfileDefaults.Create("am_adam");

        // Per-race single-voice fallback (no mix, for simplicity).
        bool f = slot.Gender == Gender.Female;
        return slot.Group switch
        {
            AccentGroup.Human               => VoiceProfileDefaults.Create(f ? "af_sarah"    : "am_michael"),
            AccentGroup.NightElf            => VoiceProfileDefaults.Create(f ? "bf_alice"    : "bm_george"),
            AccentGroup.Dwarf               => VoiceProfileDefaults.Create(f ? "bf_alice"    : "bm_george"),
            AccentGroup.DarkIronDwarf       => VoiceProfileDefaults.Create(f ? "bf_alice"    : "bm_george"),
            AccentGroup.Gnome               => VoiceProfileDefaults.Create(f ? "af_nova"     : "am_puck"),
            AccentGroup.Mechagnome          => VoiceProfileDefaults.Create(f ? "af_nova"     : "am_puck"),
            AccentGroup.Draenei             => VoiceProfileDefaults.Create(f ? "bf_isabella" : "bm_lewis"),
            AccentGroup.LightforgedDraenei  => VoiceProfileDefaults.Create(f ? "bf_isabella" : "bm_george"),
            AccentGroup.Worgen              => VoiceProfileDefaults.Create(f ? "bf_emma"     : "bm_daniel"),
            AccentGroup.KulTiran            => VoiceProfileDefaults.Create(f ? "bf_emma"     : "bm_george"),
            AccentGroup.BloodElf            => VoiceProfileDefaults.Create(f ? "bf_isabella" : "bm_lewis"),
            AccentGroup.VoidElf             => VoiceProfileDefaults.Create(f ? "bf_isabella" : "bm_lewis"),
            AccentGroup.Orc                 => VoiceProfileDefaults.Create(f ? "af_alloy"    : "am_fenrir"),
            AccentGroup.MagharOrc           => VoiceProfileDefaults.Create(f ? "af_alloy"    : "am_fenrir"),
            AccentGroup.Undead              => VoiceProfileDefaults.Create(f ? "af_alloy"    : "am_onyx"),
            AccentGroup.Tauren              => VoiceProfileDefaults.Create(f ? "af_bella"    : "am_fenrir"),
            AccentGroup.HighmountainTauren  => VoiceProfileDefaults.Create(f ? "af_bella"    : "am_fenrir"),
            AccentGroup.Troll               => VoiceProfileDefaults.Create(f ? "af_aoede"    : "am_echo"),
            AccentGroup.ZandalariTroll      => VoiceProfileDefaults.Create(f ? "bf_fable"    : "am_adam"),
            AccentGroup.Goblin              => VoiceProfileDefaults.Create(f ? "af_nova"     : "am_eric"),
            AccentGroup.Nightborne          => VoiceProfileDefaults.Create(f ? "bf_fable"    : "bm_fable"),
            AccentGroup.Vulpera             => VoiceProfileDefaults.Create(f ? "af_sky"      : "am_puck"),
            AccentGroup.Pandaren            => VoiceProfileDefaults.Create(f ? "jf_alpha"    : "jm_kumo"),
            AccentGroup.Earthen             => VoiceProfileDefaults.Create(f ? "bf_emma"     : "am_adam"),
            AccentGroup.Haranir             => VoiceProfileDefaults.Create(f ? "af_bella"    : "am_adam"),
            AccentGroup.Dracthyr            => VoiceProfileDefaults.Create(f ? "bf_isabella" : "bm_lewis"),
            AccentGroup.Dragonkin           => VoiceProfileDefaults.Create(f ? "bf_isabella" : "am_onyx"),
            AccentGroup.Elemental           => VoiceProfileDefaults.Create(f ? "af_alloy"    : "am_onyx"),
            AccentGroup.Giant               => VoiceProfileDefaults.Create(f ? "af_kore"     : "am_fenrir"),
            AccentGroup.Mechanical          => VoiceProfileDefaults.Create(f ? "af_nova"     : "am_puck"),
            _                               => VoiceProfileDefaults.Create(DefaultVoiceId)
        };
    }

    private KokoroVoice GetVoiceForSlot(VoiceSlot slot)
    {
        var profile = ResolveVoiceProfile(slot);
        var id = profile.VoiceId;

        if (!_voiceCache.TryGetValue(id, out var voice))
        {
            voice = id.StartsWith(MixPrefix, StringComparison.OrdinalIgnoreCase)
                ? ResolveMix(id[MixPrefix.Length..])
                : KokoroVoiceManager.GetVoice(id);

            _voiceCache[id] = voice;
        }

        return voice;
    }

    public static KokoroVoice ResolveMix(string spec)
    {
        var parts = spec.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var components = new List<(KokoroVoice, float)>();

        foreach (var part in parts)
        {
            var colon = part.LastIndexOf(':');
            string voiceId;
            float weight;

            if (colon < 0)
            {
                voiceId = part;
                weight = 1f;
            }
            else
            {
                voiceId = part[..colon];
                weight = float.TryParse(
                    part[(colon + 1)..],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var w) ? w : 1f;
            }

            components.Add((KokoroVoiceManager.GetVoice(voiceId), weight));
        }

        if (components.Count == 1) return components[0].Item1;
        return KokoroVoiceManager.Mix(components.ToArray());
    }
}
