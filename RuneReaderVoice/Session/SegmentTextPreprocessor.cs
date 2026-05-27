// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RuneReaderVoice.Session;

internal static class SegmentTextPreprocessor
{
    public static string QuoteDialogueParagraphs(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var rawParts = Regex.Split(normalized, @"(\n\s*\n)+");
        var paragraphs = new List<string>();

        for (int i = 0; i < rawParts.Length; i += 2)
        {
            var part = rawParts[i];
            if (string.IsNullOrWhiteSpace(part))
                continue;

            var trimmed = part.Trim();
            if (trimmed.Length == 0)
                continue;

            paragraphs.Add(IsAlreadyQuoted(trimmed) ? trimmed : $"\"{trimmed}\"");
        }

        return paragraphs.Count == 0 ? text : string.Join("\n\n", paragraphs);
    }

    public static bool IsPunctuationOnlySegment(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        return !text.Any(char.IsLetterOrDigit);
    }

    public static string InjectSyntheticParagraphPeriods(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var rawParts = Regex.Split(normalized, "(\\n\\s*\\n)+");
        var paragraphs = new List<string>();

        for (int i = 0; i < rawParts.Length; i += 2)
        {
            var part = rawParts[i];
            if (string.IsNullOrWhiteSpace(part))
                continue;

            var trimmedEnd = part.TrimEnd();
            if (trimmedEnd.Length == 0)
                continue;

            if (!Regex.IsMatch(trimmedEnd, @"^\s*[-*•]"))
            {
                var last = trimmedEnd[^1];
                if (".!?…:;)]}\"'".IndexOf(last, StringComparison.Ordinal) < 0)
                    trimmedEnd += ".";
            }

            var wordCount = CountWords(trimmedEnd);
            var isBullet = Regex.IsMatch(trimmedEnd, @"^\s*[-*•]");

            if (!isBullet && wordCount > 0 && wordCount <= 3 && paragraphs.Count > 0)
            {
                paragraphs[^1] = MergeShortParagraph(paragraphs[^1], trimmedEnd);

                while (paragraphs.Count > 1 && CountWords(paragraphs[^1]) < 6)
                {
                    var carry = paragraphs[^1];
                    paragraphs.RemoveAt(paragraphs.Count - 1);
                    paragraphs[^1] = MergeShortParagraph(paragraphs[^1], carry);
                }
            }
            else
            {
                paragraphs.Add(trimmedEnd);
            }
        }

        return string.Join("\n\n", paragraphs);
    }

    public static int CountWords(string text)
        => Regex.Matches(text ?? string.Empty, @"[\p{L}\p{N}']+").Count;

    private static bool IsAlreadyQuoted(string text)
    {
        if (text.Length < 2)
            return false;

        return (text[0] == '"' && text[^1] == '"') ||
               (text[0] == '“' && text[^1] == '”') ||
               (text[0] == '‘' && text[^1] == '’');
    }

    private static string MergeShortParagraph(string left, string right)
    {
        left = left.TrimEnd();
        right = right.TrimStart();
        if (left.Length == 0) return right;
        if (right.Length == 0) return left;

        if (left.EndsWith(".", StringComparison.Ordinal) ||
            left.EndsWith("!", StringComparison.Ordinal) ||
            left.EndsWith("?", StringComparison.Ordinal) ||
            left.EndsWith("…", StringComparison.Ordinal))
        {
            return left + " " + right;
        }

        return left + "., " + right;
    }
}
