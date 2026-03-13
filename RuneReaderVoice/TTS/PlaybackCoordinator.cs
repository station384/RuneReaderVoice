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
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.TTS.Providers;
using RuneReaderVoice.TTS.Cache;
using RuneReaderVoice.TTS.Audio;
using RuneReaderVoice.TTS.Dsp;
using RuneReaderVoice.Session;

namespace RuneReaderVoice.TTS;

public enum PlaybackMode { WaitForFullText, StreamOnFirstChunk }

public sealed class PlaybackCoordinator : IDisposable
{
    private ITtsProvider  _provider;
    private readonly TtsAudioCache _cache;
    private readonly IAudioPlayer  _player;
    private PlaybackMode  _mode;
    private readonly string        _tempDirectory;
    private readonly RecentSpeechSuppressor _recentSpeechSuppressor;

    // Playback queue — segments are held in a reorder buffer keyed by SegmentIndex
    // and released in strict order. This prevents a fast-assembling narrator segment
    // (few chunks) from jumping ahead of a slower NPC segment (many chunks).
    private readonly Queue<AssembledSegment>           _segmentQueue  = new();
    private readonly Dictionary<int, AssembledSegment> _reorderBuffer = new();
    private int _nextExpectedIndex;                     // reset on each new dialog
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object _queueLock = new();

    private CancellationTokenSource? _sessionCts;
    private Task? _playbackTask;
    private bool _disposed;

    // Diagnostics
    public TimeSpan LastSynthesisLatency { get; private set; }

    public bool IsPlaying => _player.IsPlaying;
    public RecentSpeechSuppressor RecentSpeechSuppressor => _recentSpeechSuppressor;

    /// <summary>Hot-swaps the TTS provider without restarting the playback loop.</summary>
    public void SetProvider(ITtsProvider provider) => _provider = provider;

    public PlaybackCoordinator(
        ITtsProvider provider,
        TtsAudioCache cache,
        IAudioPlayer player,
        PlaybackMode mode,
        string tempDirectory,
        RecentSpeechSuppressor recentSpeechSuppressor)
    {
        _provider       = provider;
        _cache          = cache;
        _player         = player;
        _mode           = mode;
        _tempDirectory  = tempDirectory;
        _recentSpeechSuppressor = recentSpeechSuppressor;

        Directory.CreateDirectory(_tempDirectory);
    }

    // ── Segment intake ────────────────────────────────────────────────────────

    /// <summary>
    /// Called by TtsSessionAssembler.OnSegmentComplete.
    /// Holds the segment in a reorder buffer until all earlier SegmentIndexes
    /// have been enqueued, then flushes consecutive segments into the play queue.
    /// This ensures a fast-assembling narrator segment never jumps ahead of a
    /// slower NPC segment that was started first.
    /// </summary>
    public void EnqueueSegment(AssembledSegment segment)
    {
        lock (_queueLock)
        {
            _reorderBuffer[segment.SegmentIndex] = segment;

            // Flush any consecutive run starting from _nextExpectedIndex
            while (_reorderBuffer.TryGetValue(_nextExpectedIndex, out var next))
            {
                _reorderBuffer.Remove(_nextExpectedIndex);
                _segmentQueue.Enqueue(next);
                _nextExpectedIndex++;
                _queueSignal.Release();
            }
        }
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
    /// We deliberately do NOT cancel here — the user has walked away from the NPC
    /// but the dialog audio that was already queued should finish playing naturally.
    /// Only a new dialog ID (OnSessionReset) should interrupt playback.
    /// </summary>
    public void OnSourceGone()
    {
        // Intentional no-op: let the current queue drain and audio finish.
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
        //_recentSpeechSuppressor.Clear();
        _sessionCts?.Cancel();

        lock (_queueLock)
        {
            _segmentQueue.Clear();
            _reorderBuffer.Clear();
            _nextExpectedIndex = 0;
        }

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
        if (_recentSpeechSuppressor.ShouldSuppress(segment.Text))
        {
            System.Diagnostics.Debug.WriteLine($"[PlaybackCoordinator] Suppressed repeated line: {segment.Text}");
            return;
        }

        var voiceId = _provider.ResolveVoiceId(segment.Slot);
        var profile = _provider.ResolveProfile(segment.Slot);
        var dspKey  = profile?.Dsp?.BuildCacheKey() ?? "";

        // ── Cache hit: entire segment is already encoded ──────────────────────
        // Cache stores DSP-processed audio — play directly, no second DSP pass.
        var cachedAudio = await _cache.TryGetDecodedAsync(segment.Text, voiceId, _provider.ProviderId, dspKey, ct);
        if (cachedAudio != null)
        {
            ct.ThrowIfCancellationRequested();
            await _player.PlayAsync(cachedAudio, ct);
            return;
        }

        // ── Cache miss: stream phrases into the gapless playlist ─────────────
        // SynthesizePhraseStreamAsync enqueues all ONNX jobs at once.
        // We pipe each phrase path into PlaylistPlayAsync as it becomes ready —
        // the player starts phrase 0 immediately and pre-buffers phrase 1 while
        // phrase 0 is playing, giving seamless gapless playback.
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await _player.PlaylistPlayAsync(
            PhraseAudioStream(segment, voiceId, profile, dspKey, sw, ct), ct);
    }

    /// <summary>
    /// Adapts the phrase stream into a PCM stream for PlaylistPlayAsync.
    /// Stores each phrase in the OGG cache as it arrives, records first-phrase latency.
    /// DSP is applied to each phrase after synthesis and before cache storage.
    /// </summary>
    private async IAsyncEnumerable<PcmAudio> PhraseAudioStream(
        AssembledSegment segment,
        string voiceId,
        VoiceProfile? profile,
        string dspKey,
        System.Diagnostics.Stopwatch sw,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var (audio, phraseIndex, phraseCount) in
            _provider.SynthesizePhraseStreamAsync(segment.Text, segment.Slot, _tempDirectory, ct))
        {
            if (phraseIndex == 0) { sw.Stop(); LastSynthesisLatency = sw.Elapsed; }

            // Apply DSP post-synthesis. Cache stores DSP-processed audio so
            // cache hits play directly without re-applying DSP.
            var processedAudio = DspFilterChain.Apply(audio, profile?.Dsp);

            var phraseText = GetPhraseText(segment.Text, phraseIndex, phraseCount);
            await _cache.StoreAsync(
                processedAudio, phraseText, voiceId, _provider.ProviderId, dspKey, ct);

            yield return processedAudio;
        }
    }

    /// <summary>
    /// Returns the text of phrase <paramref name="index"/> within <paramref name="fullText"/>.
    /// Used as the cache key for individual phrases so they can be reused across segments.
    /// Falls back to the full text if splitting produces a different count than expected
    /// (e.g. provider stub returned phraseCount=1).
    /// </summary>
    private static string GetPhraseText(string fullText, int index, int phraseCount)
    {
        if (phraseCount == 1) return fullText;
        var phrases = TextSplitter.Split(fullText);
        return index < phrases.Count ? phrases[index] : fullText;
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