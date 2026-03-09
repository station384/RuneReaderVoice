// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

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
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var value = text.Trim();
        value = Regex.Replace(value, @"\s+", " ");
        value = value.ToLowerInvariant();
        return value;
    }
}
