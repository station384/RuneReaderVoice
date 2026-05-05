// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.

using System;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Pronunciation;

public static class PronunciationDebug
{
    public static string Test(string text, string catalogId)
    {
        var processor = new DialoguePronunciationProcessor(
            WowPronunciationRules.CreateDefault());

        var key = VoiceSlot.NormalizeCatalogId(catalogId);
        var slot = string.Equals(key, "Narrator", StringComparison.OrdinalIgnoreCase)
            ? VoiceSlot.Narrator
            : VoiceSlot.CreateCatalog(key, Gender.Male);

        return processor.ProcessText(text, slot);
    }
}
