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

    public static IEnumerable<T> SortByVoiceRecency<T>(
        IEnumerable<T> items,
        VoiceUserSettings settings,
        string providerId,
        Func<T, string?> voiceIdSelector,
        Func<T, string> alphaSelector)
    {
        return items.OrderByDescending(i => GetVoiceRank(settings, providerId, voiceIdSelector(i)))
                    .ThenBy(i => alphaSelector(i), StringComparer.OrdinalIgnoreCase);
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