// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Pronunciation;

public static class WowPronunciationRules
{
    public static IReadOnlyList<PronunciationRule> CreateDefault()
    {
        return new List<PronunciationRule>
        {
            // ── Global lore/name fixes ────────────────────────────────────────
            // Tune these by ear against Kokoro. These are intentionally practical,
            // not academic. The goal is "sounds right in Kokoro".
            new("Atal'zul",      "ə tɑl zʊl", WholeWord: true, Priority: 100),
            new("Atal'Zul",      "ə tɑl zʊl", WholeWord: true, Priority: 100),
            new("Atal'Dazar",    "ə tɑl dɑ zɑɹ", WholeWord: true, Priority: 100),
            new("Dazar'alor",    "dɑ zɑɹ ə lɔɹ", WholeWord: true, Priority: 100),
            new("Zuldazar",      "zul dɑ zɑɹ", WholeWord: true, Priority: 100),
            new("Zul'Gurub",     "zul ɡʊ ɹub", WholeWord: true, Priority: 100),
            new("Zul'Farrak",    "zul fæɹ æk", WholeWord: true, Priority: 100),
            new("Draenei",       "dræ nəi", WholeWord: true, Priority: 100),
            new("Quel'Thalas",   "kwɛl θɑ lɑs", WholeWord: true, Priority: 100),
            new("Teldrassil",    "tɛl dræ sɪl", WholeWord: true, Priority: 100),
            new("Scholomance",   "skɑ lə mæns", WholeWord: true, Priority: 100),
            new("Har'alnor",   "hɑɹ ˈælnɔɹ", WholeWord: true, Priority: 100),
            new("Amirdrassil",   "əmirdræsɪl", WholeWord: true, Priority: 100),
            new("need",   "nid", WholeWord: true, Priority: 100),

            // ── Troll / Caribbean accent-group-specific ──────────────────────
            // These should only apply to Caribbean voices.
            new("mon",           "mɔn", Group: AccentGroup.Caribbean, WholeWord: true, Priority: 200),
            new("Mon",           "mɔn", Group: AccentGroup.Caribbean, WholeWord: true, Priority: 200),
            new("ya",            "jɑ", Group: AccentGroup.Caribbean, WholeWord: true, Priority: 150),
            new("Ya",            "jɑ", Group: AccentGroup.Caribbean, WholeWord: true, Priority: 150),

            // ── Scottish examples ────────────────────────────────────────────
            // Keep these conservative. The authored line should carry most of the flavor.
            // These are just pronunciation nudges.
            new("aye",           "aɪ", Group: AccentGroup.Scottish, WholeWord: true, Priority: 150),
            new("Aye",           "aɪ", Group: AccentGroup.Scottish, WholeWord: true, Priority: 150),

            // ── British Haughty examples ─────────────────────────────────────
            new("mana",          "mɑːnə", Group: AccentGroup.BritishHaughty, WholeWord: true, Priority: 120),

            // ── Eastern European / Draenei example hooks ─────────────────────
            new("Exodar",        "ɛk soʊ dɑɹ", Group: AccentGroup.EasternEuropean, WholeWord: true, Priority: 120),
        };
    }
}