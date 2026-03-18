// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NVorbis;
using RuneReaderVoice.Data;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache
{
    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the cached audio file path if a hit, or null on a miss.
    /// Updates last-accessed timestamp on hit.
    /// </summary>
    public async Task<string?> TryGetAsync(string text, string voiceId, string providerId, string dspKey = "")
    {
        var key = ComputeKey(text, voiceId, providerId, dspKey);

        var row = await _db.Connection.Table<AudioCacheManifestRow>()
            .Where(r => r.Key == key)
            .FirstOrDefaultAsync();

        if (row == null)
        {
            MissCount++;
            return null;
        }

        var path = Path.Combine(_cacheDirectory, row.FileName);
        if (!File.Exists(path))
        {
            await _db.Connection.DeleteAsync(row);
            MissCount++;
            EntryCount = Math.Max(0, EntryCount - 1);
            return null;
        }

        row.LastAccessedUtcTicks = DateTime.UtcNow.Ticks;
        await _db.Connection.UpdateAsync(row);
        HitCount++;
        return path;
    }

    /// <summary>
    /// Returns decoded cached PCM on a hit, or null on a miss.
    /// Only OGG cache entries are considered valid.
    /// </summary>
    public async Task<PcmAudio?> TryGetDecodedAsync(
        string text, string voiceId, string providerId, string dspKey, CancellationToken ct)
    {
        var key = ComputeKey(text, voiceId, providerId, dspKey);

        var row = await _db.Connection.Table<AudioCacheManifestRow>()
            .Where(r => r.Key == key)
            .FirstOrDefaultAsync();

        if (row == null)
        {
            MissCount++;
            return null;
        }

        var path = Path.Combine(_cacheDirectory, row.FileName);
        if (!File.Exists(path))
        {
            await _db.Connection.DeleteAsync(row);
            MissCount++;
            EntryCount = Math.Max(0, EntryCount - 1);
            return null;
        }

        if (!path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(path); } catch { }
            await _db.Connection.DeleteAsync(row);
            MissCount++;
            EntryCount = Math.Max(0, EntryCount - 1);
            return null;
        }

        row.LastAccessedUtcTicks = DateTime.UtcNow.Ticks;
        await _db.Connection.UpdateAsync(row);
        HitCount++;

        return await DecodeCachedOggAsync(path, ct);
    }

    /// <summary>
    /// Stores synthesized PCM in the cache and returns the final cached OGG path.
    /// No WAV files are generated.
    /// </summary>
    public async Task<string> StoreAsync(
        PcmAudio audio, string text, string voiceId, string providerId, string dspKey,
        CancellationToken ct)
    {
        var key     = ComputeKey(text, voiceId, providerId, dspKey);
        var keyLock = GetKeyLock(key);

        await keyLock.WaitAsync(ct);
        try
        {
            var existing = await TryGetAsync(text, voiceId, providerId, dspKey);
            if (existing != null && existing.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                return existing;

            var processedAudio  = _silenceTrimEnabled ? TrimSilence(audio) : audio;
            var cachedOggPath   = Path.Combine(_cacheDirectory, key + ".ogg");
            await TranscodeToOggAsync(processedAudio, cachedOggPath, ct);

            var fi = new FileInfo(cachedOggPath);
            var row = new AudioCacheManifestRow
            {
                Key                  = key,
                FileName             = Path.GetFileName(cachedOggPath),
                FileSizeBytes        = fi.Length,
                LastAccessedUtcTicks = DateTime.UtcNow.Ticks,
                IsCompressed         = _compressionEnabled,
            };

            await _db.Connection.InsertOrReplaceAsync(row);
            TotalSizeBytes += fi.Length;
            EntryCount++;

            _ = EvictToSizeLimitAsync();
            return cachedOggPath;
        }
        finally
        {
            keyLock.Release();
        }
    }


    /// <summary>
    /// Stores already-encoded OGG bytes in the cache without re-encoding.
    /// Used by remote providers so the exact server artifact is preserved.
    /// </summary>
    public async Task<string> StoreOggAsync(
        byte[] oggBytes, string text, string voiceId, string providerId, string dspKey,
        CancellationToken ct)
    {
        var key     = ComputeKey(text, voiceId, providerId, dspKey);
        var keyLock = GetKeyLock(key);

        await keyLock.WaitAsync(ct);
        try
        {
            var existing = await TryGetAsync(text, voiceId, providerId, dspKey);
            if (existing != null && existing.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                return existing;

            var cachedOggPath = Path.Combine(_cacheDirectory, key + ".ogg");
            await File.WriteAllBytesAsync(cachedOggPath, oggBytes, ct);

            var fi = new FileInfo(cachedOggPath);
            var row = new AudioCacheManifestRow
            {
                Key                  = key,
                FileName             = Path.GetFileName(cachedOggPath),
                FileSizeBytes        = fi.Length,
                LastAccessedUtcTicks = DateTime.UtcNow.Ticks,
                IsCompressed         = true,
            };

            await _db.Connection.InsertOrReplaceAsync(row);
            TotalSizeBytes += fi.Length;
            EntryCount++;

            _ = EvictToSizeLimitAsync();
            return cachedOggPath;
        }
        finally
        {
            keyLock.Release();
        }
    }

    /// <summary>Deletes all cache files and clears the DB manifest table.</summary>
    public async Task ClearAsync(CancellationToken ct = default)
    {
        var rows = await _db.Connection.Table<AudioCacheManifestRow>().ToListAsync();
        foreach (var row in rows)
        {
            var path = Path.Combine(_cacheDirectory, row.FileName);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        await _db.ClearTableAsync(Data.RvrTable.AudioCacheManifest);
        HitCount       = 0;
        MissCount      = 0;
        TotalSizeBytes = 0;
        EntryCount     = 0;
    }

    // ── Key computation ───────────────────────────────────────────────────────

    public static string ComputeKey(string text, string voiceId, string providerId, string dspKey = "")
    {
        var input = $"{text}\x00{voiceId}\x00{providerId}\x00{dspKey}";
        var hash  = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    // ── OGG decode ────────────────────────────────────────────────────────────

    private static async Task<PcmAudio> DecodeCachedOggAsync(string oggPath, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var fs     = new FileStream(oggPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var vorbis = new VorbisReader(fs, leaveOpen: false);
            vorbis.Initialize();

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
}
