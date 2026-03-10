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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache : IDisposable
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

    public int HitCount { get; private set; }
    public int MissCount { get; private set; }
    public long TotalSizeBytes => _manifest.Values.Sum(e => e.FileSizeBytes);
    public int EntryCount => _manifest.Count;

    // ── Construction ──────────────────────────────────────────────────────────

    public TtsAudioCache(
        string cacheDirectory,
        long maxSizeBytes = 500L * 1024 * 1024,
        bool compressionEnabled = true,
        int oggQuality = 4,
        bool silenceTrimEnabled = true)
    {
        _cacheDirectory = cacheDirectory;
        _maxSizeBytes = maxSizeBytes;
        _compressionEnabled = compressionEnabled;
        _oggQuality = oggQuality;
        _silenceTrimEnabled = silenceTrimEnabled;

        Directory.CreateDirectory(_cacheDirectory);
        LoadManifest();
        EvictToSizeLimit();
    }
}