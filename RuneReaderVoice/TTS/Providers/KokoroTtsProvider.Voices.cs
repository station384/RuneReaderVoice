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

using System;
using System.Collections.Generic;
using KokoroSharp;
using KokoroSharp.Core;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed partial class KokoroTtsProvider
{
    // ── Complete voice list (hexgrad/Kokoro-82M VOICES.md) ───────────────────

    public static readonly VoiceInfo[] KnownVoices =
    {
        // ── American English ──────────────────────────────────────────────
        new() { VoiceId = "af_heart",      Name = "Heart ★ (AF)",      Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_bella",      Name = "Bella ★ (AF)",      Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_nicole",     Name = "Nicole (AF)",        Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_sarah",      Name = "Sarah (AF)",         Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_aoede",      Name = "Aoede (AF)",         Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_kore",       Name = "Kore (AF)",          Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_alloy",      Name = "Alloy (AF)",         Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_nova",       Name = "Nova (AF)",          Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_sky",        Name = "Sky (AF)",           Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_jessica",    Name = "Jessica (AF)",       Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_river",      Name = "River (AF)",         Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "am_michael",    Name = "Michael (AM)",       Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_fenrir",     Name = "Fenrir (AM)",        Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_puck",       Name = "Puck (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_echo",       Name = "Echo (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_eric",       Name = "Eric (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_liam",       Name = "Liam (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_onyx",       Name = "Onyx (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_adam",       Name = "Adam (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_santa",      Name = "Santa (AM)",         Language = "en-US", Gender = Gender.Male   },
        // ── British English ───────────────────────────────────────────────
        new() { VoiceId = "bf_emma",       Name = "Emma (BF)",          Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bf_isabella",   Name = "Isabella (BF)",      Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bf_alice",      Name = "Alice (BF)",         Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bf_lily",       Name = "Lily (BF)",          Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bm_george",     Name = "George (BM)",        Language = "en-GB", Gender = Gender.Male   },
        new() { VoiceId = "bm_fable",      Name = "Fable (BM)",         Language = "en-GB", Gender = Gender.Male   },
        new() { VoiceId = "bm_daniel",     Name = "Daniel (BM)",        Language = "en-GB", Gender = Gender.Male   },
        new() { VoiceId = "bm_lewis",      Name = "Lewis (BM)",         Language = "en-GB", Gender = Gender.Male   },
        // ── Spanish ───────────────────────────────────────────────────────
        new() { VoiceId = "ef_dora",       Name = "Dora (EF)",          Language = "es",    Gender = Gender.Female },
        new() { VoiceId = "em_alex",       Name = "Alex (EM)",          Language = "es",    Gender = Gender.Male   },
        new() { VoiceId = "em_santa",      Name = "Santa (EM)",         Language = "es",    Gender = Gender.Male   },
        // ── French ────────────────────────────────────────────────────────
        new() { VoiceId = "ff_siwis",      Name = "Siwis (FF)",         Language = "fr",    Gender = Gender.Female },
        // ── Hindi ─────────────────────────────────────────────────────────
        new() { VoiceId = "hf_alpha",      Name = "Alpha (HF)",         Language = "hi",    Gender = Gender.Female },
        new() { VoiceId = "hf_beta",       Name = "Beta (HF)",          Language = "hi",    Gender = Gender.Female },
        new() { VoiceId = "hm_omega",      Name = "Omega (HM)",         Language = "hi",    Gender = Gender.Male   },
        new() { VoiceId = "hm_psi",        Name = "Psi (HM)",           Language = "hi",    Gender = Gender.Male   },
        // ── Italian ───────────────────────────────────────────────────────
        new() { VoiceId = "if_sara",       Name = "Sara (IF)",          Language = "it",    Gender = Gender.Female },
        new() { VoiceId = "im_nicola",     Name = "Nicola (IM)",        Language = "it",    Gender = Gender.Male   },
        // ── Brazilian Portuguese ──────────────────────────────────────────
        new() { VoiceId = "pf_dora",       Name = "Dora (PF)",          Language = "pt-BR", Gender = Gender.Female },
        new() { VoiceId = "pm_alex",       Name = "Alex (PM)",          Language = "pt-BR", Gender = Gender.Male   },
        new() { VoiceId = "pm_santa",      Name = "Santa (PM)",         Language = "pt-BR", Gender = Gender.Male   },
        // ── Japanese ──────────────────────────────────────────────────────
        new() { VoiceId = "jf_alpha",      Name = "Alpha (JF)",         Language = "ja",    Gender = Gender.Female },
        new() { VoiceId = "jf_gongitsune", Name = "Gongitsune (JF)",    Language = "ja",    Gender = Gender.Female },
        new() { VoiceId = "jf_nezumi",     Name = "Nezumi (JF)",        Language = "ja",    Gender = Gender.Female },
        new() { VoiceId = "jf_tebukuro",   Name = "Tebukuro (JF)",      Language = "ja",    Gender = Gender.Female },
        new() { VoiceId = "jm_kumo",       Name = "Kumo (JM)",          Language = "ja",    Gender = Gender.Male   },
        // ── Mandarin Chinese ──────────────────────────────────────────────
        new() { VoiceId = "zf_xiaobei",    Name = "Xiaobei (ZF)",       Language = "zh",    Gender = Gender.Female },
        new() { VoiceId = "zf_xiaoni",     Name = "Xiaoni (ZF)",        Language = "zh",    Gender = Gender.Female },
        new() { VoiceId = "zf_xiaoxiao",   Name = "Xiaoxiao (ZF)",      Language = "zh",    Gender = Gender.Female },
        new() { VoiceId = "zf_xiaoyi",     Name = "Xiaoyi (ZF)",        Language = "zh",    Gender = Gender.Female },
        new() { VoiceId = "zm_yunjian",    Name = "Yunjian (ZM)",       Language = "zh",    Gender = Gender.Male   },
        new() { VoiceId = "zm_yunxi",      Name = "Yunxi (ZM)",         Language = "zh",    Gender = Gender.Male   },
        new() { VoiceId = "zm_yunxia",     Name = "Yunxia (ZM)",        Language = "zh",    Gender = Gender.Male   },
        new() { VoiceId = "zm_yunyang",    Name = "Yunyang (ZM)",       Language = "zh",    Gender = Gender.Male   },
    };

    // Blend spec prefix — voice IDs starting with this are parsed as mixes.
    // Format: "mix:af_heart:0.7|bm_george:0.3"
    public const string MixPrefix = "mix:";
    public const string DefaultVoiceId = "af_heart";

    public IReadOnlyList<VoiceInfo> GetAvailableVoices() => KnownVoices;

    public void SetVoice(VoiceSlot slot, string voiceId)
    {
        _voiceAssignments[slot] = voiceId;
        _voiceCache.Remove(voiceId);
    }

    public string ResolveVoiceId(VoiceSlot slot)
        => _voiceAssignments.TryGetValue(slot, out var id) ? id : DefaultVoiceId;

    private KokoroVoice GetVoiceForSlot(VoiceSlot slot)
    {
        var id = _voiceAssignments.TryGetValue(slot, out var v) ? v : DefaultVoiceId;

        if (!_voiceCache.TryGetValue(id, out var voice))
        {
            voice = id.StartsWith(MixPrefix)
                ? ResolveMix(id[MixPrefix.Length..])
                : KokoroVoiceManager.GetVoice(id);
            _voiceCache[id] = voice;
        }

        return voice;
    }

    /// <summary>
    /// Parses a blend spec and returns a mixed KokoroVoice.
    /// Format: "af_heart:0.7|bm_george:0.3"
    /// Weights do not need to sum to 1 — KokoroSharp normalises internally.
    /// </summary>
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