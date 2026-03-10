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
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached audio file path if a hit, or null on a miss.
    /// Updates last-accessed timestamp on hit.
    /// </summary>
    public async Task<string?> TryGetAsync(string text, string voiceId, string providerId)
    {
        var key = ComputeKey(text, voiceId, providerId);
        await _manifestLock.WaitAsync();
        try
        {
            if (!_manifest.TryGetValue(key, out var entry))
            {
                MissCount++;
                return null;
            }

            var path = Path.Combine(_cacheDirectory, entry.FileName);
            if (!File.Exists(path))
            {
                _manifest.Remove(key);
                MissCount++;
                return null;
            }

            entry.LastAccessed = DateTime.UtcNow;
            HitCount++;
            return path;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>
    /// Stores synthesized PCM in the cache and returns the cached WAV path immediately
    /// so playback can begin without waiting for OGG compression.
    ///
    /// No temporary files are created. The first write to disk is the final cache file.
    /// </summary>
    public async Task<string> StoreAsync(
        PcmAudio audio, string text, string voiceId, string providerId,
        CancellationToken ct)
    {
        var key = ComputeKey(text, voiceId, providerId);
        var keyLock = GetKeyLock(key);

        await keyLock.WaitAsync(ct);
        try
        {
            var existing = await TryGetAsync(text, voiceId, providerId);
            if (existing != null) return existing;

            var processedAudio = _silenceTrimEnabled ? TrimSilence(audio) : audio;

            var cachedWavPath = Path.Combine(_cacheDirectory, key + ".wav");
            await WritePcmAsWavAsync(processedAudio, cachedWavPath, ct);

            var wavFi = new FileInfo(cachedWavPath);
            var entry = new CacheEntry
            {
                Key = key,
                FileName = Path.GetFileName(cachedWavPath),
                VoiceSlotId = voiceId,
                TextPreview = text.Length > 60 ? text[..60] : text,
                FileSizeBytes = wavFi.Length,
                Created = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow,
            };

            await _manifestLock.WaitAsync(ct);
            try
            {
                _manifest[key] = entry;
                await SaveManifestAsync(ct);
            }
            finally
            {
                _manifestLock.Release();
            }

            if (_compressionEnabled)
                TrackCompressionTask(CompressInBackgroundAsync(processedAudio, cachedWavPath, key, voiceId, text));

            EvictToSizeLimit();
            return cachedWavPath;
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <summary>
    /// Transcodes the cached WAV to OGG on the thread-pool, then atomically
    /// swaps the manifest entry to point at the .ogg and deletes the .wav.
    /// Uses CancellationToken.None — session cancellation must not abort
    /// compression, since we want the compressed file for future plays.
    /// </summary>
    private async Task CompressInBackgroundAsync(
        PcmAudio audio, string cachedWavPath, string key, string voiceId, string text)
    {
        try
        {
            var oggPath = Path.Combine(_cacheDirectory, key + ".ogg");
            await TranscodeToOggAsync(audio, oggPath, CancellationToken.None);

            var oggFi = new FileInfo(oggPath);
            var updatedEntry = new CacheEntry
            {
                Key = key,
                FileName = Path.GetFileName(oggPath),
                VoiceSlotId = voiceId,
                TextPreview = text.Length > 60 ? text[..60] : text,
                FileSizeBytes = oggFi.Length,
                Created = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow,
            };

            await _manifestLock.WaitAsync();
            try
            {
                // Only swap if the manifest still has the .wav for this key
                // (guards against a ClearAsync that ran during compression)
                if (_manifest.TryGetValue(key, out var current) &&
                    current.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    _manifest[key] = updatedEntry;
                    await SaveManifestAsync(CancellationToken.None);
                }
                else
                {
                    // Key was cleared or evicted — discard the orphaned OGG
                    if (File.Exists(oggPath)) File.Delete(oggPath);
                    return;
                }
            }
            finally
            {
                _manifestLock.Release();
            }

            // WAV is now superseded — delete it
            if (File.Exists(cachedWavPath)) File.Delete(cachedWavPath);

            Debug.WriteLine(
                $"[TtsAudioCache] Background compressed {key} ({oggFi.Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            // Non-fatal — the .wav remains in the cache and plays fine
            Debug.WriteLine(
                $"[TtsAudioCache] Background compression failed for {key}: {ex.Message}");
        }
    }

    private void TrackCompressionTask(Task task)
    {
        lock (_compressionTasksGate)
            _compressionTasks.Add(task);

        task.ContinueWith(_ =>
        {
            lock (_compressionTasksGate)
                _compressionTasks.RemoveAll(t => t.IsCompleted);
        }, TaskScheduler.Default);
    }

    /// <summary>Deletes all cache files and resets the manifest.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        await _manifestLock.WaitAsync(ct);
        try
        {
            foreach (var entry in _manifest.Values)
            {
                var path = Path.Combine(_cacheDirectory, entry.FileName);
                if (File.Exists(path)) File.Delete(path);
            }

            _manifest.Clear();
            HitCount = 0;
            MissCount = 0;
            await SaveManifestAsync(ct);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    // ── Key computation ───────────────────────────────────────────────────────

    public static string ComputeKey(string text, string voiceId, string providerId)
    {
        var input = $"{text}\x00{voiceId}\x00{providerId}";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}