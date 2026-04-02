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

using System;
using System.Collections.Generic;
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

    public static IReadOnlyList<string> GetLookalikeNotes()
        => new[]
        {
            ": is the normal keyboard colon. ː is the IPA length mark.",
            "' is the normal apostrophe. ˈ is primary stress.",
            ", is a comma. ˌ is secondary stress.",
            "g is ASCII g. ɡ is the IPA hard-g symbol.",
        };
}