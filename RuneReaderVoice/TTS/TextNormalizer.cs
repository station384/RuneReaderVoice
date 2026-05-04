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
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Humanizer;

namespace RuneReaderVoice.TTS;

/// <summary>
/// Deterministic TTS text normalization for WoW dialogue.
/// Runs after user text shaping and before pronunciation/cache/provider calls.
/// The server is intentionally treated as a dumb renderer; client text is authoritative.
/// </summary>
public sealed class TextNormalizer
{
    private static readonly Regex VersionRegex = new(
        @"(?<![\p{L}\p{N}])(?<version>\d+(?:\.\d+){2,})(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CurrencyRegex = new(
        @"(?<![\p{L}\p{N}])(?:(?<g>\d[\d,]*)\s*[gG](?:\s*(?<s>\d[\d,]*)\s*[sS])?(?:\s*(?<c>\d[\d,]*)\s*[cC])?|(?<s>\d[\d,]*)\s*[sS](?:\s*(?<c>\d[\d,]*)\s*[cC])?|(?<c>\d[\d,]*)\s*[cC])(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KSuffixRegex = new(
        @"(?<![\p{L}\p{N}.])(?<num>\d[\d,]*)\s*[kK](?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TimeRegex = new(
        @"(?<![\p{L}\p{N}.])(?<hour>[01]?\d|2[0-3]):(?<minute>[0-5]\d)\s*(?<ampm>[AaPp][Mm])?(?![\p{L}\p{N}.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PercentageRegex = new(
        @"(?<![\p{L}\p{N}.])(?<num>\d[\d,]*)\s*%(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FractionRegex = new(
        @"(?<![\p{L}\p{N}/\\.:-])(?<num>\d{1,3})/(?<den>\d{1,3})(?![\p{L}\p{N}/\\.:-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OrdinalRegex = new(
        @"(?<![\p{L}\p{N}.])(?<num>\d{1,6})(?:st|nd|rd|th)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex CommaNumberRegex = new(
        @"(?<![\p{L}\p{N}.])(?<num>\d{1,3}(?:,\d{3})+)(?![\p{L}\p{N}.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PlainNumberRegex = new(
        @"(?<![\p{L}\p{N}])(?<num>\d+)(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AllCapsRegex = new(
        @"(?<![\p{L}\p{N}])(?<word>[A-Z][A-Z']{2,})(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> KnownAcronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "HP", "MP", "XP", "WOW", "NPC", "UI", "PVP", "PVE", "AOE", "DOT", "HOT", "CC",
        "DPS", "HPS", "LFG", "LFR", "AH", "BG", "WG", "AV"
    };

    private static readonly HashSet<string> UnitWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "gold", "silver", "copper", "xp", "hp", "mp", "yard", "yards", "meter", "meters",
        "second", "seconds", "minute", "minutes", "hour", "hours", "day", "days",
        "week", "weeks", "year", "years"
    };

    private static readonly HashSet<string> FractionProgressWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "complete", "completed", "done", "progress", "remaining", "finished"
    };

    public string Normalize(string? input, VoiceUserSettings settings)
    {
        if (string.IsNullOrEmpty(input))
            return input ?? string.Empty;

        if (settings.EnableTextNormalization == false)
            return input;

        var text = input;

        text = NormalizeVersions(text);
        text = NormalizeCurrency(text);
        text = NormalizeKSuffix(text);
        text = NormalizeTimes(text);
        text = NormalizePercentages(text);
        text = NormalizeFractions(text);
        text = NormalizeOrdinals(text);
        text = NormalizeCommaNumbers(text);
        text = NormalizePlainNumbersAndYears(text);
        text = NormalizeAllCaps(text);

        return text;
    }

    private static string NormalizeVersions(string text)
        => VersionRegex.Replace(text, m => string.Join(" point ",
            m.Groups["version"].Value.Split('.').Select(ParseIntToWords)));

    private static string NormalizeCurrency(string text)
        => CurrencyRegex.Replace(text, m =>
        {
            if (!m.Groups["g"].Success && !m.Groups["s"].Success && !m.Groups["c"].Success)
                return m.Value;

            var parts = new List<string>(3);
            if (m.Groups["g"].Success && TryParseInt(m.Groups["g"].Value, out var gold))
                parts.Add($"{NumberToWords(gold)} gold");
            if (m.Groups["s"].Success && TryParseInt(m.Groups["s"].Value, out var silver))
                parts.Add($"{NumberToWords(silver)} silver");
            if (m.Groups["c"].Success && TryParseInt(m.Groups["c"].Value, out var copper))
                parts.Add($"{NumberToWords(copper)} copper");

            return parts.Count == 0 ? m.Value : string.Join(" ", parts);
        });

    private static string NormalizeKSuffix(string text)
        => KSuffixRegex.Replace(text, m =>
        {
            if (!TryParseInt(m.Groups["num"].Value, out var n))
                return m.Value;

            return NumberToWords(n * 1000);
        });

    private static string NormalizeTimes(string text)
        => TimeRegex.Replace(text, m =>
        {
            if (!int.TryParse(m.Groups["hour"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour) ||
                !int.TryParse(m.Groups["minute"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minute))
                return m.Value;

            var suffix = m.Groups["ampm"].Success ? m.Groups["ampm"].Value.ToUpperInvariant() : string.Empty;
            var spokenHour = hour;

            if (string.IsNullOrEmpty(suffix) && hour >= 13)
            {
                spokenHour = hour - 12;
                suffix = "PM";
            }
            else if (string.IsNullOrEmpty(suffix) && hour == 0)
            {
                spokenHour = 12;
                suffix = "AM";
            }
            else if (!string.IsNullOrEmpty(suffix))
            {
                spokenHour = hour % 12;
                if (spokenHour == 0)
                    spokenHour = 12;
            }

            var result = minute == 0
                ? NumberToWords(spokenHour)
                : $"{NumberToWords(spokenHour)} {MinuteToWords(minute)}";

            return string.IsNullOrEmpty(suffix) ? result : $"{result} {suffix}";
        });

    private static string NormalizePercentages(string text)
        => PercentageRegex.Replace(text, m =>
        {
            if (!TryParseInt(m.Groups["num"].Value, out var n))
                return m.Value;

            return $"{NumberToWords(n)} percent";
        });

    private static string NormalizeFractions(string text)
        => FractionRegex.Replace(text, m =>
        {
            if (!TryParseInt(m.Groups["num"].Value, out var numerator) ||
                !TryParseInt(m.Groups["den"].Value, out var denominator) ||
                denominator == 0)
                return m.Value;

            if (IsFollowedByAnyWord(text, m.Index + m.Length, FractionProgressWords))
                return m.Value;

            return FractionToWords(numerator, denominator);
        });

    private static string NormalizeOrdinals(string text)
        => OrdinalRegex.Replace(text, m =>
        {
            if (!TryParseInt(m.Groups["num"].Value, out var n))
                return m.Value;

            return OrdinalToWords(n);
        });

    private static string NormalizeCommaNumbers(string text)
        => CommaNumberRegex.Replace(text, m =>
        {
            if (!TryParseInt(m.Groups["num"].Value, out var n))
                return m.Value;

            return NumberToWords(n);
        });

    private static string NormalizePlainNumbersAndYears(string text)
        => PlainNumberRegex.Replace(text, m =>
        {
            if (!TryParseInt(m.Groups["num"].Value, out var n))
                return m.Value;

            if (IsPartOfPunctuationToken(text, m.Index, m.Length))
                return m.Value;

            if (IsFollowedByUnitWord(text, m.Index + m.Length))
                return NumberToWords(n);

            if (n is >= 1000 and <= 2099)
                return YearToWords(n);

            return NumberToWords(n);
        });

    private static string NormalizeAllCaps(string text)
        => AllCapsRegex.Replace(text, m =>
        {
            var word = m.Groups["word"].Value;
            var acronymKey = word.Replace("'", string.Empty, StringComparison.Ordinal);
            if (KnownAcronyms.Contains(acronymKey))
                return word;

            return word.ToLowerInvariant();
        });

    private static string YearToWords(int year)
    {
        if (year == 2000)
            return "two thousand";

        if (year is >= 2001 and <= 2009)
            return $"two thousand {NumberToWords(year % 100)}";

        var first = year / 100;
        var last = year % 100;

        if (last == 0)
            return $"{NumberToWords(first)} hundred";

        if (last < 10)
            return $"{NumberToWords(first)} oh {NumberToWords(last)}";

        return $"{NumberToWords(first)} {NumberToWords(last)}";
    }

    private static string MinuteToWords(int minute)
    {
        if (minute < 10)
            return $"oh {NumberToWords(minute)}";

        return NumberToWords(minute);
    }

    private static string FractionToWords(int numerator, int denominator)
    {
        var num = NumberToWords(numerator);
        if (denominator == 2)
            return numerator == 1 ? $"{num} half" : $"{num} halves";
        if (denominator == 4)
            return numerator == 1 ? $"{num} quarter" : $"{num} quarters";

        var den = OrdinalToWords(denominator);
        if (numerator != 1 && !den.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            den += "s";
        return $"{num} {den}";
    }

    private static string ParseIntToWords(string value)
        => TryParseInt(value, out var n) ? NumberToWords(n) : value;

    private static bool TryParseInt(string value, out int number)
    {
        var clean = value.Replace(",", string.Empty, StringComparison.Ordinal);
        return int.TryParse(clean, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static string NumberToWords(int number)
        => number.ToWords().Replace(" and ", " ", StringComparison.OrdinalIgnoreCase);

    private static string OrdinalToWords(int number)
        => number.ToOrdinalWords().Replace(" and ", " ", StringComparison.OrdinalIgnoreCase);

    private static bool IsPartOfPunctuationToken(string text, int index, int length)
    {
        var before = index > 0 ? text[index - 1] : '\0';
        var afterIndex = index + length;
        var after = afterIndex < text.Length ? text[afterIndex] : '\0';

        return before is '.' or ':' or '/' or '\\' || after is '.' or ':' or '/' or '\\';
    }

    private static bool IsFollowedByUnitWord(string text, int start)
        => IsFollowedByAnyWord(text, start, UnitWords);

    private static bool IsFollowedByAnyWord(string text, int start, HashSet<string> words)
    {
        var i = start;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        var wordStart = i;
        while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '-'))
            i++;

        if (i == wordStart)
            return false;

        var word = text[wordStart..i];
        return words.Contains(word);
    }
}
