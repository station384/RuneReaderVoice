// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Data;

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache : IDisposable
{
    // ── Configuration ─────────────────────────────────────────────────────────

    private readonly string _cacheDirectory;
    private long _maxSizeBytes;
    private bool _compressionEnabled;
    private int  _oggQuality;           // 0–10 Vorbis quality scale
    private bool _silenceTrimEnabled;

    // ── DB back-end (replaces _manifest dict + _manifestLock) ────────────────

    private readonly RvrDb _db;

    // Per-key synthesis lock: prevents duplicate synthesis for the same key
    private readonly Dictionary<string, SemaphoreSlim> _keyLocks = new();
    private readonly object _keyLocksGate = new();

    // ── Diagnostics (for settings UI) ────────────────────────────────────────

    public int  HitCount  { get; private set; }
    public int  MissCount { get; private set; }

    /// <summary>Sum of all file sizes as recorded in the DB manifest.</summary>
    public long TotalSizeBytes { get; private set; }

    /// <summary>Number of entries in the DB manifest.</summary>
    public int EntryCount { get; private set; }

    // ── Construction ──────────────────────────────────────────────────────────

    public TtsAudioCache(
        string cacheDirectory,
        RvrDb  db,
        long   maxSizeBytes       = 500L * 1024 * 1024,
        bool   compressionEnabled = true,
        int    oggQuality         = 4,
        bool   silenceTrimEnabled = true)
    {
        _cacheDirectory    = cacheDirectory;
        _db                = db;
        _maxSizeBytes      = maxSizeBytes;
        _compressionEnabled = compressionEnabled;
        _oggQuality        = oggQuality;
        _silenceTrimEnabled = silenceTrimEnabled;

        Directory.CreateDirectory(_cacheDirectory);

        // Warm the diagnostic counters from the DB (best-effort, sync at startup)
        RefreshCountersAsync().GetAwaiter().GetResult();

        // Kick off initial eviction (fire-and-forget; errors are swallowed in EvictToSizeLimitAsync)
        _ = EvictToSizeLimitAsync();
    }

    // ── Reconfiguration (called from Settings UI) ─────────────────────────────

    public void SetMaxSize(long bytes)
    {
        _maxSizeBytes = bytes;
        _ = EvictToSizeLimitAsync();
    }

    public void SetCompression(bool enabled, int quality)
    {
        _compressionEnabled = enabled;
        _oggQuality = quality;
    }

    public void SetSilenceTrim(bool enabled) => _silenceTrimEnabled = enabled;

    // ── Internal helpers ──────────────────────────────────────────────────────

    private async Task RefreshCountersAsync()
    {
        try
        {
            var rows = await _db.Connection.Table<AudioCacheManifestRow>().ToListAsync();
            TotalSizeBytes = rows.Sum(r => r.FileSizeBytes);
            EntryCount     = rows.Count;
        }
        catch { /* best-effort */ }
    }
}
