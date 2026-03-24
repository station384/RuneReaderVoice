// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

// PlaybackCoordinator.cs
// Sits between TtsSessionAssembler and the audio layer.
// Receives assembled segments, fires synthesis concurrently for all segments
// in a dialog, and plays them in strict order as each synthesis task completes.
//
// Pipeline:
//   EnqueueSegment(0) -> fire SynthesizeSegmentAsync(0) -> Task<PcmAudio?> stored
//   EnqueueSegment(1) -> fire SynthesizeSegmentAsync(1) -> Task<PcmAudio?> stored
//   EnqueueSegment(2) -> fire SynthesizeSegmentAsync(2) -> Task<PcmAudio?> stored
//
//   PlaybackLoop: await task[0] -> play -> await task[1] -> play -> await task[2] -> play
//
// Buffer-underrun handling:
//   If task[N+1] is not ready when task[N] finishes playing, the loop naturally
//   awaits task[N+1]. No busy-polling needed.
//
// ESC hotkey:
//   If IsPlaying -> consume keypress, Stop(), do NOT pass through.
//   If idle      -> pass through to game.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
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
    private ITtsProvider           _provider;
    private readonly TtsAudioCache _cache;
    private readonly IAudioPlayer  _player;
    private PlaybackMode           _mode;
    private readonly string        _tempDirectory;
    private readonly RecentSpeechSuppressor _recentSpeechSuppressor;

    // Synthesis task map keyed by SegmentIndex.
    // Tasks are fired immediately on EnqueueSegment and consumed in strict order.
    private readonly Dictionary<int, Task<PcmAudio?>> _synthTasks = new();
    private int _nextExpectedIndex;
    private readonly object _queueLock = new();

    // Per-segment ready signals. The playback loop awaits the TCS for exactly
    // the next expected index — no semaphore counting, no signal loss on
    // out-of-order arrivals. EnqueueSegment creates or completes the TCS for
    // the arriving index; the loop creates it if it arrives before the segment.
    private readonly Dictionary<int, TaskCompletionSource<bool>> _segmentReady = new();



    private CancellationTokenSource? _sessionCts;
    private Task?                    _playbackTask;
    private bool                     _disposed;

    public TimeSpan LastSynthesisLatency { get; private set; }
    public bool IsPlaying => _player.IsPlaying;
    public RecentSpeechSuppressor RecentSpeechSuppressor => _recentSpeechSuppressor;

    public PlaybackMode Mode
    {
        get => _mode;
        set => _mode = value;
    }

    public void SetProvider(ITtsProvider provider) => _provider = provider;

    public PlaybackCoordinator(
        ITtsProvider provider,
        TtsAudioCache cache,
        IAudioPlayer player,
        PlaybackMode mode,
        string tempDirectory,
        RecentSpeechSuppressor recentSpeechSuppressor)
    {
        _provider               = provider;
        _cache                  = cache;
        _player                 = player;
        _mode                   = mode;
        _tempDirectory          = tempDirectory;
        _recentSpeechSuppressor = recentSpeechSuppressor;
        Directory.CreateDirectory(_tempDirectory);
    }

    // ── Segment intake ────────────────────────────────────────────────────────

    /// <summary>
    /// Immediately fires a background synthesis task for this segment so all
    /// segments in a dialog synthesize concurrently. The playback loop awaits
    /// results in strict SegmentIndex order.
    /// </summary>
    public void EnqueueSegment(AssembledSegment segment)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PC] Enqueued segment {segment.SegmentIndex}: \"{segment.Text.Substring(0, Math.Min(40, segment.Text.Length))}\"");
        lock (_queueLock)
        {
            var ct        = _sessionCts?.Token ?? CancellationToken.None;
            var synthTask = SynthesizeSegmentAsync(segment, ct);
            _synthTasks[segment.SegmentIndex] = synthTask;

            // Signal the TCS for this index.
            // If the loop already created a TCS awaiting this segment → complete it.
            // Otherwise pre-create a completed TCS so the loop finds it ready.
            if (!_segmentReady.TryGetValue(segment.SegmentIndex, out var tcs))
            {
                tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _segmentReady[segment.SegmentIndex] = tcs;
            }
            tcs.TrySetResult(true);
        }
    }

    public void OnSessionReset(int newDialogId) => CancelCurrentSession();

    /// <summary>
    /// Source gone does NOT cancel — queued audio finishes naturally.
    /// Only a new dialog ID interrupts playback.
    /// </summary>
    public void OnSourceGone() { }

    // ── ESC hotkey ────────────────────────────────────────────────────────────

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
        _sessionCts?.Dispose();
        _sessionCts   = new CancellationTokenSource();
        _playbackTask = RunPlaybackLoopAsync(_sessionCts.Token);
    }

    private void CancelCurrentSession()
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PC] Session reset — cancelling {_synthTasks.Count} pending task(s)");
        _player.Stop();
        _sessionCts?.Cancel();
        lock (_queueLock)
        {
            _synthTasks.Clear();
            // Cancel any pending TCS awaits so the playback loop unblocks cleanly
            foreach (var tcs in _segmentReady.Values)
                tcs.TrySetCanceled();
            _segmentReady.Clear();
            _nextExpectedIndex = 0;
        }

        var oldCts    = _sessionCts;
        _sessionCts   = new CancellationTokenSource();
        _playbackTask = RunPlaybackLoopAsync(_sessionCts.Token);
        oldCts?.Dispose();
    }

    // ── Playback loop ─────────────────────────────────────────────────────────

    private async Task RunPlaybackLoopAsync(CancellationToken ct)
    {
        await Task.Yield();

        while (!ct.IsCancellationRequested)
        {
            // Get or create the TCS for the next expected segment index.
            // If the segment was already enqueued (TCS pre-completed), this
            // returns immediately. If not yet enqueued, we await here until
            // EnqueueSegment signals it — no spin, no signal loss.
            TaskCompletionSource<bool> readyTcs;
            lock (_queueLock)
            {
                if (!_segmentReady.TryGetValue(_nextExpectedIndex, out readyTcs!))
                {
                    readyTcs = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    _segmentReady[_nextExpectedIndex] = readyTcs;
                }
            }

            try { await readyTcs.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { break; }

            Task<PcmAudio?>? nextTask;
            lock (_queueLock)
            {
                _synthTasks.TryGetValue(_nextExpectedIndex, out nextTask);
            }

            if (nextTask == null) continue; // shouldn't happen but guard anyway

            // Await synthesis — natural buffer-underrun wait.
            // While this waits, all later synthesis tasks are already running.
            PcmAudio? audio;
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PC] Awaiting segment {_nextExpectedIndex}, tasks in map: {string.Join(",", _synthTasks.Keys.OrderBy(k=>k))}");

                audio = await nextTask;
            }
            catch (OperationCanceledException) { AppServices.ClearOperationStatus(); break; }
            catch (Exception ex) when (IsCancellationIoException(ex, ct))
            {
                AppServices.ClearOperationStatus(); break;
            }
            catch (Exception ex)
            {
                AppServices.ClearOperationStatus();
                System.Diagnostics.Debug.WriteLine(
                    $"[PlaybackCoordinator] Synthesis error segment {_nextExpectedIndex}: {ex.Message}");
                lock (_queueLock) { _synthTasks.Remove(_nextExpectedIndex); _segmentReady.Remove(_nextExpectedIndex); _nextExpectedIndex++; }
                continue;
            }

            lock (_queueLock)
            {
                _synthTasks.Remove(_nextExpectedIndex);
                _segmentReady.Remove(_nextExpectedIndex);
                _nextExpectedIndex++;
            }

            if (audio == null) continue;

            // Play segment N. While playing, synthesis of segment N+1 is already running.
            try
            {
                AppServices.SetOperationStatus("Playing audio…");
                int segIdx = _nextExpectedIndex - 1;
                System.Diagnostics.Debug.WriteLine(
                    $"[PC] Play start seg={segIdx} samples={audio?.Samples.Length} pending={_synthTasks.Count}");
                await _player.PlayAsync(audio, ct);
                AppServices.ClearOperationStatus();
                System.Diagnostics.Debug.WriteLine($"[PC] Play done seg={segIdx}");
            }
            catch (OperationCanceledException) { AppServices.ClearOperationStatus(); break; }
            catch (Exception ex) when (IsCancellationIoException(ex, ct))
            {
                AppServices.ClearOperationStatus(); break;
            }
            catch (Exception ex)
            {
                AppServices.ClearOperationStatus();
                System.Diagnostics.Debug.WriteLine($"[PlaybackCoordinator] Playback error: {ex.Message}");
            }
        }
    }

    // ── Synthesis ─────────────────────────────────────────────────────────────

    private async Task<PcmAudio?> SynthesizeSegmentAsync(AssembledSegment segment, CancellationToken ct)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PC] Synth start seg={segment.SegmentIndex} slot={segment.Slot} provider={_provider.ProviderId}");
        // Use slot+seq aware suppression. Including SegmentIndex ensures two
        // segments with identical text at different positions in the same dialog
        // (e.g. "You flip to the next section." appearing as seq=5 and seq=7)
        // are never suppressed by each other.
        var slotKey = $"{segment.Slot}:{segment.SegmentIndex}";
        if (_recentSpeechSuppressor.ShouldSuppress(segment.Text, slotKey))
        {
            System.Diagnostics.Debug.WriteLine($"[PC] Suppressed seg={segment.SegmentIndex} slot={slotKey} (recent repeat)");
            return null;
        }

        // Bespoke sample only applies to NPC voice slots — never narrator.
        // Narrator segments share the same NpcId as the NPC dialog but should
        // always use the narrator voice profile, not the NPC's bespoke sample.
        bool applyBespoke = !string.IsNullOrWhiteSpace(segment.BespokeSampleId)
                            && segment.Slot.Group != Protocol.AccentGroup.Narrator;

        if (!string.IsNullOrWhiteSpace(segment.BespokeSampleId) && !applyBespoke)
            System.Diagnostics.Debug.WriteLine(
                $"[PC] Bespoke ignored for narrator seg={segment.SegmentIndex}");
        else if (applyBespoke)
            System.Diagnostics.Debug.WriteLine(
                $"[PC] Bespoke applied seg={segment.SegmentIndex} sample={segment.BespokeSampleId}");

        var voiceId = applyBespoke
            ? segment.BespokeSampleId!   // bespoke sample IS the voice identity
            : _provider.ResolveVoiceId(segment.Slot);
        var profile = _provider.ResolveProfile(segment.Slot);

        // Cache key includes slot string as namespace prefix so two different slots
        // that happen to share the same sample (e.g. Narrator and Tortollan both
        // defaulting to am_adam) never share cache entries and play the wrong voice.
        // Bespoke entries also include the sample ID to distinguish from the slot default.
        var effectiveVoiceId = applyBespoke
            ? $"{slotKey}:{voiceId}+bespoke:{segment.BespokeSampleId}"
            : $"{slotKey}:{voiceId}";

        var cached = await _cache.TryGetDecodedAsync(segment.Text, effectiveVoiceId, _provider.ProviderId, "", ct);
        if (cached != null)
        {
            System.Diagnostics.Debug.WriteLine($"[PC] Cache HIT seg={segment.SegmentIndex} voice={effectiveVoiceId}");
            return DspFilterChain.Apply(cached, profile?.Dsp);
        }
        System.Diagnostics.Debug.WriteLine($"[PC] Cache MISS seg={segment.SegmentIndex} voice={effectiveVoiceId}");

        if (_provider is RemoteTtsProvider remoteProvider)
        {
            var oggBytes = await remoteProvider.SynthesizeOggAsync(
                segment.Text, segment.Slot, ct,
                applyBespoke ? segment.BespokeSampleId    : null,
                applyBespoke ? segment.BespokeExaggeration : null,
                applyBespoke ? segment.BespokeCfgWeight   : null);

            System.Diagnostics.Debug.WriteLine(
                $"[PC] Remote synth complete seg={segment.SegmentIndex} bytes={oggBytes.Length}");
            await _cache.StoreOggAsync(oggBytes, segment.Text, effectiveVoiceId, _provider.ProviderId, "", ct);
            var decoded = await _cache.TryGetDecodedAsync(segment.Text, effectiveVoiceId, _provider.ProviderId, "", ct);
            if (decoded == null)
                throw new InvalidOperationException("Remote audio cached but could not be decoded.");

            System.Diagnostics.Debug.WriteLine(
                $"[PC] Synth done seg={segment.SegmentIndex} samples={decoded.Samples.Length} dsp={profile?.Dsp?.IsNeutral == false}");
            return DspFilterChain.Apply(decoded, profile?.Dsp);
        }

        // Local provider — synthesize and concatenate all phrase chunks
        var sw         = System.Diagnostics.Stopwatch.StartNew();
        var chunkTexts = TextChunkingPolicy.GetChunkTexts(segment.Text, _provider, profile, AppServices.Settings);
        var chunks     = new List<PcmAudio>();

        await foreach (var (audio, phraseIndex, phraseCount) in
            _provider.SynthesizePhraseStreamAsync(segment.Text, segment.Slot, _tempDirectory, ct))
        {
            if (phraseIndex == 0) { sw.Stop(); LastSynthesisLatency = sw.Elapsed; }
            var phraseText = GetPhraseText(segment.Text, phraseIndex, phraseCount, chunkTexts);
            await _cache.StoreAsync(audio, phraseText, effectiveVoiceId, _provider.ProviderId, "", ct);
            chunks.Add(DspFilterChain.Apply(audio, profile?.Dsp));
        }

        return ConcatenatePcm(chunks);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PcmAudio ConcatenatePcm(List<PcmAudio> chunks)
    {
        if (chunks.Count == 0) return new PcmAudio(Array.Empty<float>(), 24000, 1);
        if (chunks.Count == 1) return chunks[0];
        var first        = chunks[0];
        int totalSamples = chunks.Sum(c => c.Samples.Length);
        var merged       = new float[totalSamples];
        int offset       = 0;
        foreach (var chunk in chunks)
        {
            Array.Copy(chunk.Samples, 0, merged, offset, chunk.Samples.Length);
            offset += chunk.Samples.Length;
        }
        return new PcmAudio(merged, first.SampleRate, first.Channels);
    }

    private static string GetPhraseText(string fullText, int index, int phraseCount, IReadOnlyList<string> phrases)
    {
        if (phraseCount == 1) return fullText;
        return index < phrases.Count ? phrases[index] : fullText;
    }

    /// <summary>
    /// Returns true when an IOException is caused by CancellationToken firing
    /// mid-TLS-read. Windows surfaces this as SocketException(995) rather than
    /// OperationCanceledException.
    /// </summary>
    private static bool IsCancellationIoException(Exception ex, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested) return false;
        var inner = ex;
        while (inner != null)
        {
            if (inner is SocketException se &&
                se.SocketErrorCode == SocketError.OperationAborted) return true;
            if (inner is IOException ioe &&
                ioe.InnerException is SocketException se2 &&
                se2.SocketErrorCode == SocketError.OperationAborted) return true;
            inner = inner.InnerException;
        }
        return false;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelCurrentSession();
        _sessionCts?.Dispose();
    }
}
