// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache
{
    // ── LRU eviction ──────────────────────────────────────────────────────────

    private void EvictToSizeLimit()
    {
        var total = _manifest.Values.Sum(e => e.FileSizeBytes);
        if (total <= _maxSizeBytes) return;

        var ordered = _manifest.Values
            .OrderBy(e => e.LastAccessed)
            .ToList();

        foreach (var entry in ordered)
        {
            if (total <= _maxSizeBytes) break;
            var path = Path.Combine(_cacheDirectory, entry.FileName);
            if (File.Exists(path)) File.Delete(path);
            total -= entry.FileSizeBytes;
            _manifest.Remove(entry.Key);
        }

        SaveManifestAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    // ── Manifest persistence ──────────────────────────────────────────────────

    private void LoadManifest()
    {
        var path = Path.Combine(_cacheDirectory, ManifestFileName);
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json);
            if (entries == null) return;

            foreach (var e in entries)
                _manifest[e.Key] = e;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TtsAudioCache] Failed to load manifest: {ex.Message}");
        }
    }

    private async Task SaveManifestAsync(CancellationToken ct)
    {
        var path = Path.Combine(_cacheDirectory, ManifestFileName);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(_manifest.Values.ToList(), JsonOptions);
        await File.WriteAllTextAsync(tmp, json, ct);

        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}