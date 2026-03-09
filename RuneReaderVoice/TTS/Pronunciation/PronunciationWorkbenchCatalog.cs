// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Generic;
using System.Linq;

namespace RuneReaderVoice.TTS.Pronunciation;

public static class PronunciationWorkbenchCatalog
{
    public const string StressTimingCategory = "Stress / Timing";
    public const string DiphthongCategory = "Diphthongs";
    public const string VowelCategory = "Vowels";
    public const string ConsonantCategory = "Consonants";

    public static IReadOnlyList<string> GroupOrder { get; } = new[]
    {
        StressTimingCategory,
        DiphthongCategory,
        VowelCategory,
        ConsonantCategory,
    };

    public static IReadOnlyList<PronunciationSymbol> Symbols { get; } = new List<PronunciationSymbol>
    {
        new("ˈ", "Primary stress", "Places the main stress on the following syllable.", "bəˈrɑːləs", StressTimingCategory, "ˈ Stress"),
        new("ˌ", "Secondary stress", "Places lighter stress on the following syllable.", "ˌɪn.təˈnæʃ.ən.əl", StressTimingCategory, "ˌ 2nd"),
        new("ː", "Length mark", "Makes the previous sound longer. This is NOT the normal keyboard colon.", "ɑː", StressTimingCategory, "ː Long"),
        new(" ", "Space", "Separates sound chunks while testing. Spaces improve readability.", "hɑɹ ˈælnɔɹ", StressTimingCategory, "␠ Space"),

        new("aɪ", "Long i", "Diphthong like eye.", "my", DiphthongCategory),
        new("aʊ", "Ow", "Diphthong like now.", "now", DiphthongCategory),
        new("ɔɪ", "Oy", "Diphthong like boy.", "boy", DiphthongCategory),
        new("eɪ", "Long a", "Diphthong like day.", "say", DiphthongCategory),
        new("oʊ", "Long o", "Diphthong like go.", "go", DiphthongCategory),

        new("ə", "Schwa", "Soft neutral vowel.", "about", VowelCategory, "ə Schwa"),
        new("ɚ", "Er (unstressed)", "R-colored schwa, often like the end of butter in American English.", "butter", VowelCategory, "ɚ Er"),
        new("ɝ", "Er (stressed)", "Stressed r-colored vowel.", "her", VowelCategory, "ɝ Er"),
        new("æ", "Short a", "Flat a sound.", "cat", VowelCategory, "æ Cat"),
        new("ɑ", "Broad ah", "Open ah sound.", "father", VowelCategory, "ɑ Ah"),
        new("ɔ", "Aw", "Rounded aw sound.", "thought", VowelCategory, "ɔ Aw"),
        new("ɒ", "Short o", "Rounded short o used in some accents.", "lot", VowelCategory, "ɒ Lot"),
        new("ɛ", "Short e", "Open e sound.", "bed", VowelCategory, "ɛ Bed"),
        new("ɜ", "Open er", "Open central vowel used in some er sounds.", "nurse", VowelCategory, "ɜ Er"),
        new("ɪ", "Short i", "Short i sound.", "bit", VowelCategory, "ɪ Bit"),
        new("i", "Long ee", "Long ee sound.", "see", VowelCategory, "i See"),
        new("ʊ", "Short oo", "Short oo sound.", "book", VowelCategory, "ʊ Book"),
        new("u", "Long oo", "Long oo sound.", "food", VowelCategory, "u Food"),
        new("ʌ", "Short u", "Short u sound.", "strut", VowelCategory, "ʌ Strut"),
        new("ɐ", "Short ah", "Central open vowel, sometimes useful for fantasy names.", "sofa (broad)", VowelCategory, "ɐ Ah"),

        new("ð", "Th (this)", "Voiced th.", "this", ConsonantCategory, "ð This"),
        new("ŋ", "Ng", "Back nasal ng.", "sing", ConsonantCategory, "ŋ Ng"),
        new("ɡ", "Hard g", "Single-story hard g; useful when you want a clear g sound distinct from ASCII g lookalikes.", "go", ConsonantCategory, "ɡ G"),
        new("ɲ", "Ny", "Palatal n sound.", "canyon", ConsonantCategory, "ɲ Ny"),
        new("ɹ", "English r", "Standard English r.", "red", ConsonantCategory, "ɹ R"),
        new("ɾ", "Tapped r/t", "Quick tap used in some accents.", "butter", ConsonantCategory, "ɾ Tap"),
        new("ʃ", "Sh", "Sh sound.", "ship", ConsonantCategory, "ʃ Sh"),
        new("ʒ", "Zh", "Voiced zh sound.", "measure", ConsonantCategory, "ʒ Zh"),
        new("ʔ", "Glottal stop", "Brief stop in the throat.", "uh-oh", ConsonantCategory, "ʔ Stop"),
        new("ʎ", "Ly", "Palatal l-like sound.", "million (some accents)", ConsonantCategory, "ʎ Ly"),
        new("θ", "Th (thin)", "Unvoiced th.", "thin", ConsonantCategory, "θ Thin"),
        new("tʃ", "Ch", "Ch affricate.", "church", ConsonantCategory, "tʃ Ch"),
        new("dʒ", "J", "J affricate.", "judge", ConsonantCategory, "dʒ J"),
        new("ʤ", "J (alt)", "Alternate J ligature seen in some IPA references.", "judge", ConsonantCategory, "ʤ J alt"),
        new("ʧ", "Ch (alt)", "Alternate Ch ligature seen in some IPA references.", "church", ConsonantCategory, "ʧ Ch alt"),
    };

    public static IReadOnlyList<PronunciationSymbol> GetByCategory(string category)
        => Symbols.Where(s => s.Category == category).ToList();
}
