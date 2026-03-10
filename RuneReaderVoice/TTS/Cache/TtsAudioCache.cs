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
//   1. Silence trimming (if enabled): strip leading/trailing silence from PCM
//   2. OGG transcode (if compression enabled): convert PCM → OGG

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
using RuneReaderVoice.TTS.Providers;

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
    /// Stores synthesized PCM in the cache and returns the cached WAV path immediately
    /// so playback can begin without waiting for OGG compression.
    ///
    /// No temporary files are created. The first write to disk is the final cache file.
    /// </summary>
    public async Task<string> StoreAsync(
        PcmAudio audio, string text, string voiceId, string providerId,
        CancellationToken ct)
    {
        var key     = ComputeKey(text, voiceId, providerId);
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

            if (_compressionEnabled)
                TrackCompressionTask(CompressInBackgroundAsync(processedAudio, cachedWavPath, key, voiceId, text));

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
        PcmAudio audio, string cachedWavPath, string key, string voiceId, string text)
    {
        try
        {
            var oggPath = Path.Combine(_cacheDirectory, key + ".ogg");
            await TranscodeToOggAsync(audio, oggPath, CancellationToken.None);

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
    /// Trims leading and trailing silence from PCM.
    /// TODO: implement proper sample-based trimming.
    /// Currently a pass-through to preserve existing behavior.
    /// </summary>
    private static PcmAudio TrimSilence(PcmAudio audio)
    {
        return audio;
    }

    /// <summary>
    /// Transcodes interleaved float PCM to OGG/Vorbis using OggVorbisEncoder.
    /// Pure managed, cross-platform, no native dependencies.
    /// </summary>
    private async Task TranscodeToOggAsync(PcmAudio audio, string oggPath, CancellationToken ct)
    {
        var pcmChannels = Deinterleave(audio);
        int sampleRate  = audio.SampleRate;

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

    private static float[][] Deinterleave(PcmAudio audio)
    {
        int channels = Math.Max(1, audio.Channels);
        if (audio.Samples.Length == 0)
        {
            var empty = new float[channels][];
            for (int c = 0; c < channels; c++) empty[c] = Array.Empty<float>();
            return empty;
        }

        int sampleCount = audio.Samples.Length / channels;
        var channelArrays = new float[channels][];
        for (int c = 0; c < channels; c++)
            channelArrays[c] = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            for (int c = 0; c < channels; c++)
                channelArrays[c][i] = audio.Samples[(i * channels) + c];
        }

        return channelArrays;
    }

    private static async Task WritePcmAsWavAsync(PcmAudio audio, string wavPath, CancellationToken ct)
    {
        int channels = Math.Max(1, audio.Channels);
        int bitsPerSample = 16;
        int blockAlign = channels * (bitsPerSample / 8);
        int byteRate = audio.SampleRate * blockAlign;
        int dataLength = audio.Samples.Length * 2;

        await using var fs = File.Create(wavPath);
        using var writer = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(audio.SampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        var pcm16 = new byte[dataLength];
        for (int i = 0; i < audio.Samples.Length; i++)
        {
            short sample = FloatToPcm16(audio.Samples[i]);
            pcm16[i * 2] = (byte)(sample & 0xFF);
            pcm16[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        await fs.WriteAsync(pcm16, ct);
        await fs.FlushAsync(ct);
    }

    private static short FloatToPcm16(float sample)
    {
        sample = Math.Clamp(sample, -1f, 1f);
        return (short)Math.Round(sample * short.MaxValue);
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