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

// TtsAudioCache.cs
// Content-addressable audio cache for synthesized TTS audio.
//
// Cache key: SHA256(text + resolvedVoiceId + providerId) truncated to 16 hex chars.
// Storage format: OGG/Vorbis (when compression enabled) or WAV (when disabled).
// Eviction: LRU size-limit only. No TTL — quest dialog never becomes stale.
// Manifest: cache_manifest.json maps keys to file metadata.
//
// Post-processing pipeline (applied before writing to cache):
//   1. Silence trimming (if enabled): strip leading/trailing silence from WAV
//   2. OGG transcode (if compression enabled): convert WAV → OGG

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Cache;

public sealed class CacheEntry
{
    public string Key         { get; init; } = string.Empty;
    public string FileName    { get; init; } = string.Empty;
    public string VoiceSlotId { get; init; } = string.Empty;
    public string TextPreview { get; init; } = string.Empty; // first 60 chars
    public long   FileSizeBytes { get; init; }
    public DateTime LastAccessed { get; set; }
    public DateTime Created      { get; init; }
}

public sealed class TtsAudioCache : IDisposable
{
    // ── Configuration ─────────────────────────────────────────────────────────

    private readonly string _cacheDirectory;
    private long _maxSizeBytes;
    private bool _compressionEnabled;
    private int _oggQuality;      // 0–10 Vorbis quality scale
    private bool _silenceTrimEnabled;

    // ── Internal state ────────────────────────────────────────────────────────

    private readonly Dictionary<string, CacheEntry> _manifest = new();
    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    // Per-key synthesis lock: prevents duplicate synthesis for the same key
    private readonly Dictionary<string, SemaphoreSlim> _keyLocks = new();
    private readonly object _keyLocksGate = new();

    private const string ManifestFileName = "cache_manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // ── Diagnostics (for settings UI) ────────────────────────────────────────
    public int  HitCount  { get; private set; }
    public int  MissCount { get; private set; }
    public long TotalSizeBytes => _manifest.Values.Sum(e => e.FileSizeBytes);
    public int  EntryCount     => _manifest.Count;

    // ── Construction ──────────────────────────────────────────────────────────

    public TtsAudioCache(
        string cacheDirectory,
        long maxSizeBytes       = 500L * 1024 * 1024,
        bool compressionEnabled = true,
        int oggQuality          = 4,
        bool silenceTrimEnabled = true)
    {
        _cacheDirectory     = cacheDirectory;
        _maxSizeBytes       = maxSizeBytes;
        _compressionEnabled = compressionEnabled;
        _oggQuality         = oggQuality;
        _silenceTrimEnabled = silenceTrimEnabled;

        Directory.CreateDirectory(_cacheDirectory);
        LoadManifest();
        EvictToSizeLimit();
    }

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
            if (!_manifest.TryGetValue(key, out var entry)) { MissCount++; return null; }

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
        finally { _manifestLock.Release(); }
    }

    /// <summary>
    /// Stores a synthesized WAV file in the cache after post-processing.
    /// wavPath is the raw output from the provider — it will be processed and
    /// the original may be deleted. Returns the final cached file path.
    /// </summary>
    public async Task<string> StoreAsync(
        string wavPath, string text, string voiceId, string providerId,
        CancellationToken ct)
    {
        var key      = ComputeKey(text, voiceId, providerId);
        var keyLock  = GetKeyLock(key);

        await keyLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring key lock (another task may have cached it)
            var existing = await TryGetAsync(text, voiceId, providerId);
            if (existing != null) return existing;

            // Post-processing pipeline
            var processedPath = wavPath;

            // 1. Silence trimming
            if (_silenceTrimEnabled)
                processedPath = TrimSilence(processedPath);

            // 2. OGG transcode
            string finalPath;
            if (_compressionEnabled)
            {
                var oggPath = Path.Combine(_cacheDirectory, key + ".ogg");
                await TranscodeToOggAsync(processedPath, oggPath, ct);
                finalPath = oggPath;
            }
            else
            {
                finalPath = Path.Combine(_cacheDirectory, key + ".wav");
                File.Copy(processedPath, finalPath, overwrite: true);
            }

            // Clean up the temp WAV if it's not the final destination
            if (!string.Equals(processedPath, wavPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(processedPath) && processedPath != finalPath)
                File.Delete(processedPath);
            if (File.Exists(wavPath) && wavPath != finalPath)
                File.Delete(wavPath);

            var fi = new FileInfo(finalPath);
            var entry = new CacheEntry
            {
                Key          = key,
                FileName     = Path.GetFileName(finalPath),
                VoiceSlotId  = voiceId,
                TextPreview  = text.Length > 60 ? text[..60] : text,
                FileSizeBytes = fi.Length,
                Created      = DateTime.UtcNow,
                LastAccessed = DateTime.UtcNow,
            };

            await _manifestLock.WaitAsync(ct);
            try
            {
                _manifest[key] = entry;
                await SaveManifestAsync(ct);
            }
            finally { _manifestLock.Release(); }

            EvictToSizeLimit();
            return finalPath;
        }
        finally { keyLock.Release(); }
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
            HitCount  = 0;
            MissCount = 0;
            await SaveManifestAsync(ct);
        }
        finally { _manifestLock.Release(); }
    }

    // ── Post-processing ───────────────────────────────────────────────────────

    /// <summary>
    /// Trims leading and trailing silence from a WAV file.
    /// Returns the path to the trimmed file (may be the same path, modified in-place).
    /// TODO: implement proper WAV silence detection using PCM sample analysis.
    /// Currently a stub — returns the input path unchanged.
    /// </summary>
    private static string TrimSilence(string wavPath)
    {
        // Phase 2 TODO: read PCM samples, find first/last non-silent frame,
        // re-write WAV header and sample data.
        // Threshold: samples below ~1% of max amplitude are treated as silence.
        // For now: pass-through.
        return wavPath;
    }

    /// <summary>
    /// Transcodes a WAV file to OGG/Vorbis using NVorbis-based encoding.
    /// TODO: NVorbis is a decoder; encoding requires VorbisEncoder or FFmpeg.
    /// This is a stub that copies the file as a placeholder.
    /// </summary>
    private async Task TranscodeToOggAsync(string wavPath, string oggPath, CancellationToken ct)
    {
        // Phase 2 TODO: implement WAV → OGG/Vorbis transcode.
        // Options:
        //   - VorbisEncoder NuGet package (pure managed)
        //   - Invoke ffmpeg as subprocess (more robust, requires ffmpeg on PATH)
        //
        // For now: copy WAV with .ogg extension as placeholder.
        // The playback layer handles WAV fine, so this is a temporary no-op.
        await using var src  = File.OpenRead(wavPath);
        await using var dest = File.Create(oggPath);
        await src.CopyToAsync(dest, ct);

        Debug.WriteLine($"[TtsAudioCache] TODO: transcode {wavPath} → {oggPath} (currently passthrough)");
    }

    // ── Key computation ───────────────────────────────────────────────────────

    public static string ComputeKey(string text, string voiceId, string providerId)
    {
        var input = $"{text}\x00{voiceId}\x00{providerId}";
        var hash  = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

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
            var json    = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json);
            if (entries == null) return;
            foreach (var e in entries) _manifest[e.Key] = e;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TtsAudioCache] Failed to load manifest: {ex.Message}");
        }
    }

    private async Task SaveManifestAsync(CancellationToken ct)
    {
        var path = Path.Combine(_cacheDirectory, ManifestFileName);
        var tmp  = path + ".tmp";
        var json = JsonSerializer.Serialize(_manifest.Values.ToList(), JsonOptions);
        await File.WriteAllTextAsync(tmp, json, ct);
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    // ── Per-key lock management ───────────────────────────────────────────────

    private SemaphoreSlim GetKeyLock(string key)
    {
        lock (_keyLocksGate)
        {
            if (!_keyLocks.TryGetValue(key, out var sem))
            {
                sem = new SemaphoreSlim(1, 1);
                _keyLocks[key] = sem;
            }
            return sem;
        }
    }

    public void Dispose()
    {
        _manifestLock.Dispose();
    }
}

