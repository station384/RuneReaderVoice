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

namespace RuneReaderVoice.TTS;

/// <summary>
/// Suppresses recently-spoken lines for a configurable time window.
/// Used only in the live playback path; manual pronunciation preview bypasses this.
/// </summary>
public sealed class RecentSpeechSuppressor
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _recent = new();

    public bool Enabled { get; set; } = true;
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(5);

    public bool ShouldSuppress(string? text)
    {
        if (!Enabled || Window <= TimeSpan.Zero)
            return false;

        var key = Normalize(text);
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            PurgeExpired_NoLock(now);

            if (_recent.TryGetValue(key, out var lastSpoken) && now - lastSpoken <= Window)
                return true;

            _recent[key] = now;
            return false;
        }
    }

    /// <summary>
    /// Slot-aware suppression check. Narrator and NPC slots with the same text
    /// are treated as distinct — suppressing an NPC line should never silence
    /// a narrator line with the same text, and vice versa.
    /// </summary>
    public bool ShouldSuppress(string? text, string slotKey)
    {
        if (!Enabled || Window <= TimeSpan.Zero)
            return false;

        var normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        // Include slot in key so Narrator:text and BloodElf/Male:text never collide
        var key = $"{slotKey}\x00{normalized}";
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            PurgeExpired_NoLock(now);

            if (_recent.TryGetValue(key, out var lastSpoken) && now - lastSpoken <= Window)
                return true;

            _recent[key] = now;
            return false;
        }
    }

    public void Clear()
    {
        lock (_lock)
            _recent.Clear();
    }

    private void PurgeExpired_NoLock(DateTimeOffset now)
    {
        if (_recent.Count == 0)
            return;

        List<string>? toRemove = null;

        foreach (var kvp in _recent)
        {
            if (now - kvp.Value > Window)
            {
                toRemove ??= new List<string>();
                toRemove.Add(kvp.Key);
            }
        }

        if (toRemove == null)
            return;

        foreach (var key in toRemove)
            _recent.Remove(key);
    }

    private static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text;
    }
}