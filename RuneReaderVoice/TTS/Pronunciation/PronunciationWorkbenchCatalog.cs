// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;

namespace RuneReaderVoice.TTS.Pronunciation;

public static class PronunciationWorkbenchCatalog
{
    public static IReadOnlyList<PronunciationSymbol> Symbols { get; } = new List<PronunciationSymbol>
    {
        new("ə", "Schwa", "Soft neutral vowel.", "about", "Vowel"),
        new("ɑ", "Broad ah", "Open ah sound.", "father", "Vowel"),
        new("æ", "Short a", "Flat a sound.", "cat", "Vowel"),
        new("ɛ", "Short e", "Open e sound.", "bed", "Vowel"),
        new("ɪ", "Short i", "Short i sound.", "bit", "Vowel"),
        new("i", "Long ee", "Long ee sound.", "see", "Vowel"),
        new("ɔ", "Aw", "Rounded aw sound.", "thought", "Vowel"),
        new("oʊ", "Long o", "Long o glide.", "go", "Vowel"),
        new("ʊ", "Short oo", "Short oo sound.", "book", "Vowel"),
        new("u", "Long oo", "Long oo sound.", "food", "Vowel"),
        new("aɪ", "Long i", "Eye diphthong.", "my", "Vowel"),
        new("aʊ", "Ow", "Ow diphthong.", "now", "Vowel"),
        new("ɔɪ", "Oy", "Oy diphthong.", "boy", "Vowel"),

        new("ɹ", "English r", "Standard English r.", "red", "Consonant"),
        new("ʃ", "Sh", "Sh sound.", "ship", "Consonant"),
        new("ʒ", "Zh", "Voiced zh sound.", "measure", "Consonant"),
        new("θ", "Th (thin)", "Unvoiced th.", "thin", "Consonant"),
        new("ð", "Th (this)", "Voiced th.", "this", "Consonant"),
        new("ŋ", "Ng", "Back nasal ng.", "sing", "Consonant"),
        new("tʃ", "Ch", "Ch affricate.", "church", "Consonant"),
        new("dʒ", "J", "J affricate.", "judge", "Consonant"),
        new("j", "Y", "Y glide.", "yes", "Consonant"),

        new("ˈ", "Primary stress", "Emphasizes the next syllable.", "reCORD vs REcord", "Stress"),
        new("ˌ", "Secondary stress", "Lighter emphasis before the main stress.", "counterintelligence", "Stress"),
        new(" ", "Space", "Separates syllables or sound chunks while testing.", "hɑɹ ˈælnɔɹ", "Control")
    };
}
