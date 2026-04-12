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
using System.Text.RegularExpressions;
using Avalonia.Controls;

namespace RuneReaderVoice.UI.Views;

internal static class SelectionRecencyHelper
{
    private const byte MaxRank = 10;

    public static void BumpVoice(VoiceUserSettings settings, string providerId, string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(voiceId))
            return;
        Bump(settings.RecentVoiceSelectionRanks, MakeVoiceKey(providerId, voiceId));
    }

    public static void BumpRace(VoiceUserSettings settings, int raceId)
    {
        if (raceId <= 0)
            return;
        Bump(settings.RecentRaceSelectionRanks, raceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static IEnumerable<(int raceId, string label)> SortRaces(
        IEnumerable<(int raceId, string label)> items,
        VoiceUserSettings settings)
    {
        return items.OrderByDescending(i => GetRaceRank(settings, i.raceId))
                    .ThenBy(i => i.label, StringComparer.OrdinalIgnoreCase);
    }

    public static int GetVoiceRank(VoiceUserSettings settings, string providerId, string? voiceId)
        => string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(voiceId)
            ? 0
            : settings.RecentVoiceSelectionRanks.TryGetValue(MakeVoiceKey(providerId, voiceId), out var rank) ? rank : 0;

    public static int GetRaceRank(VoiceUserSettings settings, int raceId)
        => raceId <= 0 ? 0
            : settings.RecentRaceSelectionRanks.TryGetValue(raceId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var rank) ? rank : 0;

    public static IEnumerable<T> SortByVoiceRecency<T>(
        IEnumerable<T> items,
        VoiceUserSettings settings,
        string providerId,
        Func<T, string?> voiceIdSelector,
        Func<T, string> alphaSelector,
        bool bespokeOnly = false,
        bool bespokeLast = false)
    {
        var query = items.Select(i => new
            {
                Item  = i,
                Id    = voiceIdSelector(i),
                Alpha = alphaSelector(i),
            });

        if (bespokeOnly)
            query = query.Where(x => IsBespokeVoiceId(x.Id));

        return query
            .OrderByDescending(x => GetVoiceRank(settings, providerId, x.Id))
            .ThenBy(x => GetVoiceGroupOrder(x.Id, bespokeLast))
            .ThenBy(x => GetVoiceGroupName(x.Id), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => GetVoiceDisplayLabel(x.Id, preservePrefixForGenerics: !bespokeOnly), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => GetVoiceNumericSuffix(x.Id))
            .ThenBy(x => x.Alpha, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Item);
    }

    public static bool IsBespokeVoiceId(string? voiceId)
        => !string.IsNullOrWhiteSpace(voiceId) && voiceId.StartsWith("U_", StringComparison.OrdinalIgnoreCase);

    public static string GetVoiceDisplayLabel(string? voiceId, bool preservePrefixForGenerics = false)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return string.Empty;

        var id = voiceId.Trim();
        if (id.StartsWith("U_", StringComparison.OrdinalIgnoreCase))
            return id[2..].Replace('_', ' ');

        if (id.StartsWith("M_", StringComparison.OrdinalIgnoreCase) || id.StartsWith("F_", StringComparison.OrdinalIgnoreCase))
            return preservePrefixForGenerics ? id : id[2..].Replace('_', ' ');

        return id.Replace('_', ' ');
    }

    private static int GetVoiceGroupOrder(string? voiceId, bool bespokeLast)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return 99;

        if (voiceId.StartsWith("M_", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (voiceId.StartsWith("F_", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (voiceId.StartsWith("U_", StringComparison.OrdinalIgnoreCase))
            return bespokeLast ? 10 : 0;
        return bespokeLast ? 5 : 2;
    }

    private static string GetVoiceGroupName(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return string.Empty;

        var parts = voiceId.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && (parts[0].Equals("M", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("F", StringComparison.OrdinalIgnoreCase)))
            return parts[1];
        if (parts.Length >= 2 && parts[0].Equals("U", StringComparison.OrdinalIgnoreCase))
            return string.Join('_', parts.Skip(1));
        return voiceId;
    }

    private static int GetVoiceNumericSuffix(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return int.MaxValue;

        var m = Regex.Match(voiceId, @"_(\d+)$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : int.MaxValue;
    }

    public static void PopulateComboBoxWithSortedItems<T>(ComboBox combo, IEnumerable<T> items)
    {
        combo.ItemsSource = items.ToList();
    }

    private static void Bump(Dictionary<string, byte> map, string selectedKey)
    {
        var keys = map.Keys.ToList();
        foreach (var key in keys)
        {
            var v = map[key];
            map[key] = v <= 1 ? (byte)0 : (byte)(v - 1);
        }
        map[selectedKey] = MaxRank;
    }

    private static string MakeVoiceKey(string providerId, string voiceId) => $"{providerId}|{voiceId}";
}