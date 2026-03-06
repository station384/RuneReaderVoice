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


// PlaybackCoordinator.cs
// Sits between TtsSessionAssembler and the audio layer.
// Receives assembled segments, checks cache, synthesizes if needed,
// and manages playback sequencing.
//
// Modes:
//   Batch: wait for all chunks (assembler fires per-segment), synthesize, play.
//   Stream: begin synthesis on first chunk arrival, play while later segments arrive.
//          If playback catches up before next segment is ready, pause and poll.
//
// ESC hotkey:
//   If IsPlaying → consume keypress, Stop(), do NOT pass through.
//   If idle      → pass through to game (Windows: return 1 from hook; Linux: inputd reports only).

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.TTS.Providers;
using RuneReaderVoice.TTS.Cache;
using RuneReaderVoice.TTS.Audio;
using RuneReaderVoice.Session;

namespace RuneReaderVoice.TTS;

public enum PlaybackMode { WaitForFullText, StreamOnFirstChunk }

public sealed class PlaybackCoordinator : IDisposable
{
    private readonly ITtsProvider  _provider;
    private readonly TtsAudioCache _cache;
    private readonly IAudioPlayer  _player;
    private PlaybackMode  _mode;
    private readonly string        _tempDirectory;

    // Queue of assembled segments waiting for playback
    private readonly Queue<AssembledSegment> _segmentQueue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object _queueLock = new();

    private CancellationTokenSource? _sessionCts;
    private Task? _playbackTask;
    private bool _disposed;

    // Diagnostics
    public TimeSpan LastSynthesisLatency { get; private set; }

    public bool IsPlaying => _player.IsPlaying;

    public PlaybackCoordinator(
        ITtsProvider provider,
        TtsAudioCache cache,
        IAudioPlayer player,
        PlaybackMode mode,
        string tempDirectory)
    {
        _provider       = provider;
        _cache          = cache;
        _player         = player;
        _mode           = mode;
        _tempDirectory  = tempDirectory;

        Directory.CreateDirectory(_tempDirectory);
    }

    // ── Segment intake ────────────────────────────────────────────────────────

    /// <summary>
    /// Called by TtsSessionAssembler.OnSegmentComplete.
    /// Enqueues the segment for synthesis and playback.
    /// </summary>
    public void EnqueueSegment(AssembledSegment segment)
    {
        lock (_queueLock)
        {
            _segmentQueue.Enqueue(segment);
        }
        _queueSignal.Release();
    }

    /// <summary>
    /// Called when a new dialog session begins (DIALOG ID changed).
    /// Cancels current playback and clears the queue.
    /// </summary>
    public void OnSessionReset(int newDialogId)
    {
        CancelCurrentSession();
    }

    /// <summary>
    /// Called when the QR source disappears (no barcode for >2 seconds).
    /// Stops playback and discards the queue.
    /// </summary>
    public void OnSourceGone()
    {
        CancelCurrentSession();
    }

    // ── ESC hotkey ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by the hotkey monitor when ESC is pressed.
    /// Returns true if the keypress was consumed (audio stopped).
    /// Returns false if idle (caller should pass through to game).
    /// </summary>
    public bool HandleEscPressed()
    {
        if (!_player.IsPlaying) return false;
        CancelCurrentSession();
        return true;
    }

    // ── Session management ────────────────────────────────────────────────────

    public void StartSession()
    {
        if (_playbackTask is { IsCompleted: false }) return;
        _sessionCts   = new CancellationTokenSource();
        _playbackTask = RunPlaybackLoopAsync(_sessionCts.Token);
    }

    private void CancelCurrentSession()
    {
        _player.Stop();
        _sessionCts?.Cancel();

        lock (_queueLock) _segmentQueue.Clear();

        // Drain the semaphore
        while (_queueSignal.CurrentCount > 0)
            _queueSignal.Wait(0);

        // Restart the playback loop so it is ready for the next EnqueueSegment.
        // Without this, OnSessionReset cancels the loop and the segment that
        // immediately follows from OnSegmentComplete lands in a dead queue.
        _sessionCts = new CancellationTokenSource();
        _playbackTask = RunPlaybackLoopAsync(_sessionCts.Token);
    }
    public PlaybackMode Mode
    {
        get => _mode;
        set => _mode = value;  // change _mode from readonly to private
    }
    
    // ── Playback loop ─────────────────────────────────────────────────────────

    private async Task RunPlaybackLoopAsync(CancellationToken ct)
    {
        await Task.Yield();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(ct);
            }
            catch (OperationCanceledException) { break; }

            AssembledSegment segment;
            lock (_queueLock)
            {
                if (_segmentQueue.Count == 0) continue;
                segment = _segmentQueue.Dequeue();
            }

            try
            {
                await SynthesizeAndPlayAsync(segment, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PlaybackCoordinator] Error playing segment: {ex.Message}");
            }
        }
    }

    private async Task SynthesizeAndPlayAsync(AssembledSegment segment, CancellationToken ct)
    {
        // Check cache first
        var audioPath = await _cache.TryGetAsync(segment.Text, segment.Slot, _provider.ProviderId);

        if (audioPath == null)
        {
            // Cache miss — synthesize
            var sw      = System.Diagnostics.Stopwatch.StartNew();
            var tmpPath = Path.Combine(_tempDirectory,
                $"synth_{Guid.NewGuid():N}.wav");

            var wavPath = await _provider.SynthesizeToFileAsync(
                segment.Text, segment.Slot, tmpPath, ct);

            sw.Stop();
            LastSynthesisLatency = sw.Elapsed;

            audioPath = await _cache.StoreAsync(
                wavPath, segment.Text, segment.Slot, _provider.ProviderId, ct);
        }

        ct.ThrowIfCancellationRequested();
        await _player.PlayAsync(audioPath, ct);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelCurrentSession();
        _sessionCts?.Dispose();
        _queueSignal.Dispose();
    }
}