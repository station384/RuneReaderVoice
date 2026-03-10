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
        => ResolveVoiceProfile(slot).BuildIdentityKey();

    private VoiceProfile GetDefaultProfile(VoiceSlot slot)
    {
        if (slot.Group == AccentGroup.Narrator)
            return VoiceProfileDefaults.Create("af_bella");

        return slot.Group switch
        {
            AccentGroup.NeutralAmerican => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "af_sarah" : "am_michael"),
            AccentGroup.AmericanRaspy   => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "af_alloy" : "am_onyx"),
            AccentGroup.Scottish        => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "bf_alice" : "bm_george"),
            AccentGroup.BritishHaughty  => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "bf_isabella" : "bm_lewis"),
            AccentGroup.BritishRugged   => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "bf_emma" : "bm_daniel"),
            AccentGroup.PlayfulSqueaky  => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "af_nova" : "am_puck"),
            AccentGroup.EasternEuropean => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "bf_isabella" : "bm_lewis"),
            AccentGroup.Caribbean       => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "af_aoede" : "am_echo"),
            AccentGroup.RegalTribal     => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "bf_fable" : "bm_george"),
            AccentGroup.DeepResonant    => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "af_bella" : "am_fenrir"),
            AccentGroup.NewYork         => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "af_nova" : "am_eric"),
            AccentGroup.EastAsian       => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "jf_alpha" : "jm_kumo"),
            AccentGroup.French          => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "bf_fable" : "bm_fable"),
            AccentGroup.Scrappy         => VoiceProfileDefaults.Create(slot.Gender == Gender.Female ? "af_sky" : "am_puck"),
            _                           => VoiceProfileDefaults.Create(DefaultVoiceId)
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