// SPDX-License-Identifier: GPL-3.0-or-later

using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Pronunciation;

public static class PronunciationDebug
{
    public static string Test(string text, AccentGroup group)
    {
        var processor = new DialoguePronunciationProcessor(
            WowPronunciationRules.CreateDefault());

        var slot = group == AccentGroup.Narrator
            ? VoiceSlot.Narrator
            : new VoiceSlot(group, Gender.Male);

        return processor.ProcessText(text, slot);
    }
}
