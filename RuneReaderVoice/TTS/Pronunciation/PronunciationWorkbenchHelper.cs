// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Text;

namespace RuneReaderVoice.TTS.Pronunciation;

public static class PronunciationWorkbenchHelper
{
    public static string BuildPreview(string sentence, string targetText, string phonemeText)
    {
        if (string.IsNullOrWhiteSpace(sentence))
            return string.Empty;

        if (string.IsNullOrWhiteSpace(targetText) || string.IsNullOrWhiteSpace(phonemeText))
            return sentence;

        var source = sentence;
        var target = targetText.Trim();
        if (target.Length == 0)
            return sentence;

        var sb = new StringBuilder(source.Length + 32);
        int scan = 0;

        while (scan < source.Length)
        {
            int index = source.IndexOf(target, scan, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                sb.Append(source, scan, source.Length - scan);
                break;
            }

            sb.Append(source, scan, index - scan);
            var visible = source.Substring(index, target.Length);
            sb.Append('[')
              .Append(visible)
              .Append("](/")
              .Append(phonemeText.Trim())
              .Append("/)");

            scan = index + target.Length;
        }

        return sb.ToString();
    }
}
