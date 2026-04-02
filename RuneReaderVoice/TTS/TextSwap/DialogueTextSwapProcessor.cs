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
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace RuneReaderVoice.TTS.TextSwap;

public sealed class DialogueTextSwapProcessor
{
    private readonly IReadOnlyList<TextSwapRule> _rules;

    public DialogueTextSwapProcessor(IEnumerable<TextSwapRule> rules)
    {
        _rules = (rules ?? Array.Empty<TextSwapRule>())
            .Where(r => r is not null && !r.IsEmpty)
            .OrderByDescending(r => r.FindText.Length)
            .ThenByDescending(r => r.Priority)
            .ToList();
    }

    public string Process(string? text)
    {
        if (text == null)
            return string.Empty;

        var current = text;

        foreach (var rule in _rules)
            current = ApplyRule(current, rule);

        return current;
    }

    public IReadOnlyList<TextSwapRule> GetRules() => _rules;

    private static string ApplyRule(string source, TextSwapRule rule)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(rule.FindText))
            return source;

        if (!rule.WholeWord)
        {
            var comparison = rule.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            var sb = new StringBuilder(source.Length + 16);
            var scan = 0;
            while (scan < source.Length)
            {
                var index = source.IndexOf(rule.FindText, scan, comparison);
                if (index < 0)
                {
                    sb.Append(source, scan, source.Length - scan);
                    break;
                }

                sb.Append(source, scan, index - scan);
                sb.Append(rule.ReplaceText);
                scan = index + rule.FindText.Length;
            }

            return sb.ToString();
        }

        var escaped = Regex.Escape(rule.FindText);
        var pattern = $@"(?<![\p{{L}}\p{{N}}_]){escaped}(?![\p{{L}}\p{{N}}_])";
        var options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        if (!rule.CaseSensitive)
            options |= RegexOptions.IgnoreCase;

        return Regex.Replace(source, pattern, rule.ReplaceText, options);
    }
}