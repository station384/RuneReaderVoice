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
using OggVorbisEncoder;
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

    // Background compression tasks — tracked so Dispose() can await them
    private readonly List<Task> _compressionTasks = new();
    private readonly object _compressionTasksGate = new();

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
    /// Stores a synthesized WAV in the cache and returns the WAV path immediately
    /// so playback can begin without waiting for compression.
    ///
    /// Post-processing pipeline (play-first strategy):
    ///   1. Silence trimming — applied synchronously before returning
    ///   2. WAV is registered in the manifest and returned to the caller for playback
    ///   3. OGG transcode runs in the background — when complete, the manifest entry
    ///      is updated to point at the .ogg and the .wav is deleted
    ///
    /// If compression is disabled, the WAV is simply stored and returned.
    /// </summary>
    public async Task<string> StoreAsync(
        string wavPath, string text, string voiceId, string providerId,
        CancellationToken ct)
    {
        var key     = ComputeKey(text, voiceId, providerId);
        var keyLock = GetKeyLock(key);

        await keyLock.WaitAsync(ct);
        try
        {
            // Re-check after acquiring key lock (another task may have cached it)
            var existing = await TryGetAsync(text, voiceId, providerId);
            if (existing != null) return existing;

            // 1. Silence trimming
            var processedPath = _silenceTrimEnabled ? TrimSilence(wavPath) : wavPath;

            // Copy to cache as .wav — returned immediately for playback
            var cachedWavPath = Path.Combine(_cacheDirectory, key + ".wav");
            File.Copy(processedPath, cachedWavPath, overwrite: true);

            // Clean up any intermediate trimmed file
            if (!string.Equals(processedPath, wavPath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(processedPath))
                File.Delete(processedPath);
            if (File.Exists(wavPath) && wavPath != cachedWavPath)
                File.Delete(wavPath);

            // 2. Register .wav in manifest so caller can play it right away
            var wavFi = new FileInfo(cachedWavPath);
            var entry = new CacheEntry
            {
                Key           = key,
                FileName      = Path.GetFileName(cachedWavPath),
                VoiceSlotId   = voiceId,
                TextPreview   = text.Length > 60 ? text[..60] : text,
                FileSizeBytes = wavFi.Length,
                Created       = DateTime.UtcNow,
                LastAccessed  = DateTime.UtcNow,
            };

            await _manifestLock.WaitAsync(ct);
            try
            {
                _manifest[key] = entry;
                await SaveManifestAsync(ct);
            }
            finally { _manifestLock.Release(); }

            // 3. Fire-and-forget background OGG transcode (if enabled)
            if (_compressionEnabled)
                TrackCompressionTask(CompressInBackgroundAsync(cachedWavPath, key, voiceId, text));

            EvictToSizeLimit();
            return cachedWavPath;
        }
        finally { keyLock.Release(); }
    }

    /// <summary>
    /// Transcodes the cached WAV to OGG on the thread-pool, then atomically
    /// swaps the manifest entry to point at the .ogg and deletes the .wav.
    /// Uses CancellationToken.None — session cancellation must not abort
    /// compression, since we want the compressed file for future plays.
    /// </summary>
    private async Task CompressInBackgroundAsync(
        string cachedWavPath, string key, string voiceId, string text)
    {
        try
        {
            var oggPath = Path.Combine(_cacheDirectory, key + ".ogg");
            await TranscodeToOggAsync(cachedWavPath, oggPath, CancellationToken.None);

            var oggFi = new FileInfo(oggPath);
            var updatedEntry = new CacheEntry
            {
                Key           = key,
                FileName      = Path.GetFileName(oggPath),
                VoiceSlotId   = voiceId,
                TextPreview   = text.Length > 60 ? text[..60] : text,
                FileSizeBytes = oggFi.Length,
                Created       = DateTime.UtcNow,
                LastAccessed  = DateTime.UtcNow,
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
            finally { _manifestLock.Release(); }

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
    /// Transcodes a 16-bit mono WAV file to OGG/Vorbis using OggVorbisEncoder.
    /// Pure managed, cross-platform, no native dependencies.
    /// Quality scale: 0.0–1.0 mapped from the 0–10 user setting.
    /// At quality 0.4 (~64 kbps) speech is indistinguishable from the source WAV.
    /// WinRT MediaPlayer and GStreamer both play OGG/Vorbis natively.
    /// </summary>
    private async Task TranscodeToOggAsync(string wavPath, string oggPath, CancellationToken ct)
    {
        // Read WAV on calling thread — file is small and already written
        float[][] pcmChannels;
        int sampleRate;

        await using (var fs = File.OpenRead(wavPath))
        using (var r = new BinaryReader(fs))
        {
            (pcmChannels, sampleRate) = ReadWavPcmForVorbis(r);
        }

        ct.ThrowIfCancellationRequested();

        // Encode on thread-pool — CPU-bound
        await Task.Run(() =>
        {
            float quality = Math.Clamp(_oggQuality / 10f, 0f, 1f);
            int channels  = pcmChannels.Length;

            var info    = VorbisInfo.InitVariableBitRate(channels, sampleRate, quality);
            var comments = new OggVorbisEncoder.Comments();
            comments.AddTag("ENCODER", "RuneReaderVoice");

            var serial  = new Random().Next();
            var oggStream = new OggStream(serial);

            // Write the three Vorbis header packets
            oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
            oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
            oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

            using var outFile      = File.Create(oggPath);
            var processingState    = ProcessingState.Create(info);

            // Flush headers to file
            while (oggStream.PageOut(out OggPage page, true))
            {
                outFile.Write(page.Header, 0, page.Header.Length);
                outFile.Write(page.Body,   0, page.Body.Length);
            }

            // Feed PCM in chunks of 1024 samples
            const int chunkSize  = 1024;
            int totalSamples     = pcmChannels[0].Length;
            int offset           = 0;

            while (offset < totalSamples)
            {
                int count = Math.Min(chunkSize, totalSamples - offset);

                // Build a float[][] slice for this chunk
                var chunk = new float[channels][];
                for (int c = 0; c < channels; c++)
                {
                    chunk[c] = new float[count];
                    Array.Copy(pcmChannels[c], offset, chunk[c], 0, count);
                }

                processingState.WriteData(chunk, count);
                offset += count;

                while (processingState.PacketOut(out OggPacket packet))
                {
                    oggStream.PacketIn(packet);
                    while (oggStream.PageOut(out OggPage page, false))
                    {
                        outFile.Write(page.Header, 0, page.Header.Length);
                        outFile.Write(page.Body,   0, page.Body.Length);
                    }
                }
            }

            // Signal end of stream and flush remaining pages
            processingState.WriteEndOfStream();
            while (processingState.PacketOut(out OggPacket packet))
            {
                oggStream.PacketIn(packet);
                while (oggStream.PageOut(out OggPage page, true))
                {
                    outFile.Write(page.Header, 0, page.Header.Length);
                    outFile.Write(page.Body,   0, page.Body.Length);
                }
            }
        }, ct);
    }

    /// <summary>
    /// Reads a 16-bit mono or stereo WAV and returns float[][] PCM in [-1, 1]
    /// plus the sample rate. OggVorbisEncoder expects separate channel arrays.
    /// </summary>
    private static (float[][] channels, int sampleRate) ReadWavPcmForVorbis(BinaryReader r)
    {
        // RIFF header
        r.ReadBytes(4);  // "RIFF"
        r.ReadInt32();   // file size
        r.ReadBytes(4);  // "WAVE"

        int sampleRate = 0, channels = 0, bitsPerSample = 0;
        byte[]? dataBytes = null;

        while (r.BaseStream.Position < r.BaseStream.Length - 8)
        {
            var chunkId   = new string(r.ReadChars(4));
            var chunkSize = r.ReadInt32();

            if (chunkId == "fmt ")
            {
                r.ReadInt16();               // audio format (1 = PCM)
                channels      = r.ReadInt16();
                sampleRate    = r.ReadInt32();
                r.ReadInt32();               // byte rate
                r.ReadInt16();               // block align
                bitsPerSample = r.ReadInt16();
                if (chunkSize > 16) r.ReadBytes(chunkSize - 16);
            }
            else if (chunkId == "data")
            {
                dataBytes = r.ReadBytes(chunkSize);
            }
            else
            {
                r.ReadBytes(chunkSize);
            }

            if (sampleRate > 0 && dataBytes != null) break;
        }

        if (dataBytes == null || sampleRate == 0)
            throw new InvalidDataException("WAV file missing fmt or data chunk.");

        int sampleCount    = dataBytes.Length / (bitsPerSample / 8) / channels;
        var channelArrays  = new float[channels][];
        for (int c = 0; c < channels; c++)
            channelArrays[c] = new float[sampleCount];

        // Deinterleave 16-bit samples into per-channel float arrays
        for (int i = 0; i < sampleCount; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                int byteIdx = (i * channels + c) * 2;
                short s = (short)(dataBytes[byteIdx] | (dataBytes[byteIdx + 1] << 8));
                channelArrays[c][i] = s / 32768f;
            }
        }

        return (channelArrays, sampleRate);
    }

    // ── Key computation ───────────────────────────────────────────────────────

    public static string ComputeKey(string text, string voiceId, string providerId)
    {
        var input = $"{text}\x00{voiceId}\x00{providerId}";
        var hash  = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(input));
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
        // Wait briefly for any in-flight background compression to finish so we
        // don't leave orphaned .wav files alongside half-written .ogg files.
        Task[] pending;
        lock (_compressionTasksGate)
            pending = _compressionTasks.Where(t => !t.IsCompleted).ToArray();

        if (pending.Length > 0)
            Task.WaitAll(pending, TimeSpan.FromSeconds(5));

        _manifestLock.Dispose();
    }
}