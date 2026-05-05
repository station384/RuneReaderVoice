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

using System.Collections.Generic;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Pronunciation;

public static class WowPronunciationRules
{
    public static IReadOnlyList<PronunciationRule> CreateDefault()
    {
        return new List<PronunciationRule>
        {
            // // ── Global lore/name fixes ────────────────────────────────────────
            // // Tune these by ear against Kokoro. These are intentionally practical,
            // // not academic. The goal is "sounds right in Kokoro".
            // new("Atal'zul",      "ə tɑl zʊl", WholeWord: true, Priority: 100),
            // new("Atal'Zul",      "ə tɑl zʊl", WholeWord: true, Priority: 100),
            // new("Atal'Dazar",    "ə tɑl dɑ zɑɹ", WholeWord: true, Priority: 100),
            // new("Dazar'alor",    "dɑ zɑɹ ə lɔɹ", WholeWord: true, Priority: 100),
            // new("Zuldazar",      "zul dɑ zɑɹ", WholeWord: true, Priority: 100),
            // new("Zul'Gurub",     "zul ɡʊ ɹub", WholeWord: true, Priority: 100),
            // new("Zul'Farrak",    "zul fæɹ æk", WholeWord: true, Priority: 100),
            // new("Draenei",       "dræ nəi", WholeWord: true, Priority: 100),
            // new("Quel'Thalas",   "kwɛl θɑ lɑs", WholeWord: true, Priority: 100),
            // new("Teldrassil",    "tɛl dræ sɪl", WholeWord: true, Priority: 100),
            // new("Scholomance",   "skɑ lə mæns", WholeWord: true, Priority: 100),
            // new("Har'alnor",   "hɑɹ ˈælnɔɹ", WholeWord: true, Priority: 100),
            // new("Amirdrassil",   "əmirdræsɪl", WholeWord: true, Priority: 100),
            // new("need",   "nid", WholeWord: true, Priority: 100),
            //
            // new("Har'alnor", "hɑɹ ˈælnɔɹ", WholeWord: true, Priority: 100),
            //
            // new("Amirdrassil", "əmirdræsɪl", WholeWord: true, Priority: 100),
            // new("Dornogal", "dɔːrnəɡæl", WholeWord: true, Priority: 100),
            //
            //
            // // ── Scottish examples ────────────────────────────────────────────
            // // Keep these conservative. The authored line should carry most of the flavor.
            // // These are just pronunciation nudges.
            //
            // // ── British Haughty examples ─────────────────────────────────────
            //
            // // ── Eastern European / Draenei example hooks ─────────────────────
            //
            //
            //
            // // Cities & Major Settlements
            // new("Stormwind City", "stɔːrmwɪnd sɪti", WholeWord: true, Priority: 100),
            // new("Dalaran", "dæləræn", WholeWord: true, Priority: 100),
            // new("Ironforge", "aɪrnfɔːrdʒ", WholeWord: true, Priority: 100),
            // new("Orgrimmar", "ɔːrɡrɪmɑːr", WholeWord: true, Priority: 100),
            // new("Suramar City", "sʊrəmær sɪti", WholeWord: true, Priority: 100),
            //
            // // World Regions & Continents
            // new("Azeroth", "æzroθ", WholeWord: true, Priority: 100),
            // new("Kalimdor", "kælɪmɔːdər", WholeWord: true, Priority: 100),
            // new("The Eastern Kingdoms", "ðiː ɛstən kɪŋdəmz", WholeWord: true, Priority: 100),
            // new("Northrend", "nɔːθrɛnd", WholeWord: true, Priority: 100),
            // new("Pandaria", "pændəriə", WholeWord: true, Priority: 100),
            // new("The Broken Isles", "ðiː brɒkən aɪlz", WholeWord: true, Priority: 100),
            // new("Outland", "aʊtlænd", WholeWord: true, Priority: 100),
            //
            // // Sub-Areas & Towns
            // new("Goldshire", "goʊldʃɪər", WholeWord: true, Priority: 100),
            // new("Westfall", "wɛstfɔːl", WholeWord: true, Priority: 100),
            // new("Elwynn Forest", "ɛlwɪn fɔːrst", WholeWord: true, Priority: 100),
            // new("Tirisfal Glades", "tɪrɪsfæl glædz", WholeWord: true, Priority: 100),
            // new("Stratholme", "strəθɒlm", WholeWord: true, Priority: 100),
            // new("Duskwood", "dʌskwʊd", WholeWord: true, Priority: 100),
            // new("The Hinterlands", "ðiː hɪntərlændz", WholeWord: true, Priority: 100),
            // new("Durotar", "duːrɔːtɑːr", WholeWord: true, Priority: 100),
            // new("Thunder Bluff", "θʌndər blʌf", WholeWord: true, Priority: 100),
            // new("Gadgetzan", "gædʒɪtzeɪn", WholeWord: true, Priority: 100),
            // new("Tanaris", "tænɑːris", WholeWord: true, Priority: 100),
            // new("Zangarmarsh", "zæŋɡɑːrmɑːʃ", WholeWord: true, Priority: 100),
            // new("Silithus", "sɪlɪθəs", WholeWord: true, Priority: 100),
            // new("Dun Morogh", "dʌn mɔːrɒx", WholeWord: true, Priority: 100),
            // new("New Hearthglen", "nuː hɜːthglɛn", WholeWord: true, Priority: 100),
            // new("Dragonblight", "drægnblaɪt", WholeWord: true, Priority: 100),
            // new("Grizzly Hills", "grɪzli haɪlz", WholeWord: true, Priority: 100),
            // new("Howling Fjord", "həʊlɪŋ fjɔːrd", WholeWord: true, Priority: 100),
            // new("The Vale of Eternal Blossoms", "ðiː væl ɒv ˈɛtənl blosəmz", WholeWord: true, Priority: 100),
            // new("The Valley of the Four Winds", "ðiː vælɪ ɒv ðʌf wɪndz", WholeWord: true, Priority: 100),
            // new("Kun-Lai Summit", "kʌn laɪ sʌmɪt", WholeWord: true, Priority: 100),
            // new("The Jade Forest", "ðiː dʒɛd fɔːrst", WholeWord: true, Priority: 100),
            //
            // // Key Locations (Non-Cities)
            // new("Blackrock Mountain", "blækrok məʊntɪn", WholeWord: true, Priority: 100),
            // new("The Deadmines", "ðiː dɛmɪnz", WholeWord: true, Priority: 100),
            // new("Stratholme", "strəθɒlm", WholeWord: true, Priority: 100),
            // new("Uldaman", "ʊldəmæn", WholeWord: true, Priority: 100),
            // new("Molten Core", "mɔːltn kɔːr", WholeWord: true, Priority: 100),
            // new("Naxxramas", "næksræməs", WholeWord: true, Priority: 100),
            // new("Sunwell Plateau", "sʌnwel plætəʊ", WholeWord: true, Priority: 100),
            // new("The Barrens", "ðiː bɛrənz", WholeWord: true, Priority: 100),
            // new("Duskwood", "dʌskwʊd", WholeWord: true, Priority: 100),
            // new("Ashenvale", "æʃɛnvæl", WholeWord: true, Priority: 100),
            // new("Grizzly Hills", "grɪzli haɪlz", WholeWord: true, Priority: 100),
            // new("The Plaguelands", "ðiː plægjəlændz", WholeWord: true, Priority: 100),
            // new("The Dark Portal", "ðiː dɑːk pɔːrtəl", WholeWord: true, Priority: 100),
            // new("The Broken Shore", "ðiː brɒkən sɔːr", WholeWord: true, Priority: 100),
            //
            // // Races
            // new("Humans", "hjuːmənz", WholeWord: true, Priority: 100),
            // new("Dwarves", "dwɔːvz", WholeWord: true, Priority: 100),
            // new("Night Elves", "naɪt ɛlvz", WholeWord: true, Priority: 100),
            // new("Gnomes", "nəʊmz", WholeWord: true, Priority: 100),
            // new("Draenei", "drænɛi", WholeWord: true, Priority: 100),
            // new("Blood Elves", "blʌd ɛlvz", WholeWord: true, Priority: 100),
            // new("Worgen", "wɔːgən", WholeWord: true, Priority: 100),
            // new("Orcs", "ɔːks", WholeWord: true, Priority: 100),
            // new("Trolls", "trɒlz", WholeWord: true, Priority: 100),
            // new("Tauren", "tɔːrn", WholeWord: true, Priority: 100),
            // new("Forsaken", "fɔːrsækən", WholeWord: true, Priority: 100),
            // new("Pandaren", "pændærən", WholeWord: true, Priority: 100),
            // new("Goblins", "goblɪnz", WholeWord: true, Priority: 100),
            //
            // // Lore Names & Historical Places
            // new("The Burning Legion", "ðiː bʌrniŋ lɪdʒən", WholeWord: true, Priority: 100),
            // new("Draenor", "drænɔːr", WholeWord: true, Priority: 100),
            // new("Outland", "aʊtlænd", WholeWord: true, Priority: 100),
            // new("The Broken Shore", "ðiː brɒkən sɔːr", WholeWord: true, Priority: 100),
            //
            // // Key NPCs
            // new("Thrall", "θrɔːl", WholeWord: true, Priority: 100),
            // new("Jaina Proudmoore", "dʒeɪnə pruːdmɔːr", WholeWord: true, Priority: 100),
            // new("Arthas Menethil", "ɑːθəs mɛnɛθɪl", WholeWord: true, Priority: 100),
            // new("Sylvanas Windrunner", "sɪlvənæs wɪndrʌnər", WholeWord: true, Priority: 100),
            // new("Garrosh Hellscream", "ɡærɒʃ hɛlskriːm", WholeWord: true, Priority: 100),
            //
            // // Other Planes and Realms
            // new("Dalaran", "dæləræn", WholeWord: true, Priority: 100),
            // new("The Maelstrom", "ðiː meɪlstrɔːm", WholeWord: true, Priority: 100),
            // new("The Shadowlands", "ðiː ʃædəulændz", WholeWord: true, Priority: 100),
            // new("Azeroth (Pre-Cataclysm)", "æzroθ prɪ kætəklzɒm", WholeWord: true, Priority: 100),
            //
            // new("Dazar'alor", "dæzərˈælɔːr", WholeWord: true, Priority: 100),
            // new("Kul Tiran Coast", "kʊl tɪræn koʊst", WholeWord: true, Priority: 100),
            // new("The Golden Empire", "ði ɡɒldən ɛmpaɪri", WholeWord: true, Priority: 100),
            // new("The Maw", "ði meɪ", WholeWord: true, Priority: 100),
            // new("Dunemaul", "dʌnɛmɔːl", WholeWord: true, Priority: 100),
            //
            // // Key NPCs from Dragonflight
            // new("Jaina Proudmoore", "dʒeɪnə pruːdmɔːr", WholeWord: true, Priority: 100),
            // new("Alleria Windrunner", "ælɪriə wɪndrʌnər", WholeWord: true, Priority: 100),
            // new("Tyrael", "taɪˈreɪl", WholeWord: true, Priority: 100),
            // new("Kael'thas Sunstrider", "keɪlzθæs sʌnˈstriːdər", WholeWord: true, Priority: 100),
            //
            // // Key Zones from Dragonflight
            // new("Dazar'alor", "dæzərˈælɔːr", WholeWord: true, Priority: 100),
            // new("Kul Tiran Coast", "kʊl tɪræn koʊst", WholeWord: true, Priority: 100),
            //
            //
            // new("The Maw", "ði meɪ", WholeWord: true, Priority: 100),
            // new("Maldraxxus", "mældræksəs", WholeWord: true, Priority: 100),
            // new("Revendreth", "revɛndrɛθ", WholeWord: true, Priority: 100),
            // new("Torghast: The City of Trials", "tɔːrɡhæst ði sɪti ɒv tɹaɪəlz", WholeWord: true, Priority: 100),
            //
            // // Key NPCs from The War Within
            // new("Alleria Windrunner", "ælɪriə wɪndrʌnər", WholeWord: true, Priority: 100),
            // new("Tyrande Whisperwind", "tɛrænd ˈwɪspərwɪnd", WholeWord: true, Priority: 100),
            //
            // // Other locations from The War Within
            // new("Azeroth", "æzroθ", WholeWord: true, Priority: 100),
            // new("The Shadowlands", "ðiː ʃædəulændz", WholeWord: true, Priority: 100),
            
            
        };
    }
}
