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
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NVorbis;
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
    /// Returns decoded cached PCM on a hit, or null on a miss.
    /// Only OGG cache entries are considered valid in the current architecture.
    /// </summary>
    public async Task<PcmAudio?> TryGetDecodedAsync(string text, string voiceId, string providerId, CancellationToken ct)
    {
        string? path = null;
        var key = ComputeKey(text, voiceId, providerId);

        await _manifestLock.WaitAsync(ct);
        try
        {
            if (!_manifest.TryGetValue(key, out var entry))
            {
                MissCount++;
                return null;
            }

            path = Path.Combine(_cacheDirectory, entry.FileName);
            if (!File.Exists(path))
            {
                _manifest.Remove(key);
                MissCount++;
                return null;
            }

            if (!path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(path); } catch { }
                _manifest.Remove(key);
                await SaveManifestAsync(ct);
                MissCount++;
                return null;
            }

            entry.LastAccessed = DateTime.UtcNow;
            HitCount++;
        }
        finally
        {
            _manifestLock.Release();
        }

        return await DecodeCachedOggAsync(path!, ct);
    }

    /// <summary>
    /// Stores synthesized PCM in the cache and returns the final cached OGG path.
    /// No WAV files are generated.
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
            if (existing != null && existing.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                return existing;

            var processedAudio = _silenceTrimEnabled ? TrimSilence(audio) : audio;

            var cachedOggPath = Path.Combine(_cacheDirectory, key + ".ogg");
            await TranscodeToOggAsync(processedAudio, cachedOggPath, ct);

            var oggFi = new FileInfo(cachedOggPath);
            var entry = new CacheEntry
            {
                Key = key,
                FileName = Path.GetFileName(cachedOggPath),
                VoiceSlotId = voiceId,
                TextPreview = text.Length > 60 ? text[..60] : text,
                FileSizeBytes = oggFi.Length,
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

            EvictToSizeLimit();
            return cachedOggPath;
        }
        finally
        {
            keyLock.Release();
        }
    }

    private static async Task<PcmAudio> DecodeCachedOggAsync(string oggPath, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var fs = new FileStream(oggPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var vorbis = new VorbisReader(fs, leaveOpen: false);
            vorbis.Initialize(); // Required — populates _streamDecoder; SampleRate/Channels are null without this

            var sampleRate = vorbis.SampleRate;
            var channels   = vorbis.Channels;
            var samples    = new List<float>(sampleRate * channels * 5);
            var readBuf    = new float[4096];

            int read;
            while ((read = vorbis.ReadSamples(readBuf)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                for (var i = 0; i < read; i++)
                    samples.Add(readBuf[i]);
            }

            return new PcmAudio(samples.ToArray(), sampleRate, channels);
        }, ct);
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