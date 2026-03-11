// SPDX-License-Identifier: GPL-3.0-or-later

namespace RuneReaderVoice.TTS.TextSwap;

public static class TextSwapWorkbenchHelper
{
    public static string BuildPreview(string sentence, string findText, string replaceText, bool wholeWord, bool caseSensitive)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(findText))
            return sentence;

        var processor = new DialogueTextSwapProcessor(new[]
        {
            new TextSwapRule(findText.Trim(), replaceText ?? string.Empty, wholeWord, caseSensitive, 100)
        });

        return processor.Process(sentence);
    }
}
