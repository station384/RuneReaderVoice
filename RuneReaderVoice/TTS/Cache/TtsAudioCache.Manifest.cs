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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RuneReaderVoice.Data;

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache
{
    // ── LRU eviction ──────────────────────────────────────────────────────────

    private async Task EvictToSizeLimitAsync()
    {
        try
        {
            var rows = await _db.Connection.Table<AudioCacheManifestRow>().ToListAsync();
            var total = rows.Sum(r => r.FileSizeBytes);

            if (total <= _maxSizeBytes)
            {
                TotalSizeBytes = total;
                EntryCount     = rows.Count;
                return;
            }

            var ordered = rows.OrderBy(r => r.LastAccessedUtcTicks).ToList();

            foreach (var row in ordered)
            {
                if (total <= _maxSizeBytes) break;

                var path = Path.Combine(_cacheDirectory, row.FileName);
                try { if (File.Exists(path)) File.Delete(path); } catch { }

                await _db.Connection.DeleteAsync(row);
                total -= row.FileSizeBytes;
            }

            TotalSizeBytes = total;
            EntryCount     = (await _db.Connection.Table<AudioCacheManifestRow>().CountAsync());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TtsAudioCache] Eviction error: {ex.Message}");
        }
    }

    // ── Manifest migration (one-time, on first DB-backed startup) ─────────────

    /// <summary>
    /// If a legacy cache_manifest.json exists in the cache directory, import its
    /// entries into the DB and delete the file. Called during first run.
    /// </summary>
    public async Task MigrateLegacyManifestAsync()
    {
        var manifestPath = Path.Combine(_cacheDirectory, "cache_manifest.json");
        if (!File.Exists(manifestPath)) return;

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath);
            var entries = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<CacheEntry>>(json);
            if (entries != null)
            {
                foreach (var e in entries)
                {
                    var filePath = Path.Combine(_cacheDirectory, e.FileName);
                    if (!File.Exists(filePath)) continue;

                    var row = new AudioCacheManifestRow
                    {
                        Key                  = e.Key,
                        FileName             = e.FileName,
                        FileSizeBytes        = e.FileSizeBytes,
                        LastAccessedUtcTicks = e.LastAccessed.Ticks,
                        IsCompressed         = e.IsCompressed,
                    };
                    await _db.Connection.InsertOrReplaceAsync(row);
                }
            }

            File.Delete(manifestPath);
            await RefreshCountersAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TtsAudioCache] Legacy manifest migration failed: {ex.Message}");
        }
    }
}