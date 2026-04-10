// SPDX-License-Identifier: GPL-3.0-only
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3 of the License.
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
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.TTS.Providers;
using RuneReaderVoice.TTS.Cache;
using RuneReaderVoice.TTS.Audio;
using RuneReaderVoice.TTS.Dsp;
using RuneReaderVoice.Session;

namespace RuneReaderVoice.TTS;
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
    private readonly Dictionary<int, AssembledSegment> _segmentMap = new();
    private readonly Dictionary<string, Task<RemoteTtsProvider.RemoteBatchResolution>> _remoteBatchTasks = new();
    private int            _nextExpectedIndex;
    private int            _expectedDialogSegments;
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly object        _queueLock   = new();



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
            _segmentMap[segment.SegmentIndex] = segment;
            _expectedDialogSegments = Math.Max(_expectedDialogSegments, segment.DialogSegmentCount);
            _queueSignal.Release();
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
            _segmentMap.Clear();
            _remoteBatchTasks.Clear();
            _nextExpectedIndex = 0;
            _expectedDialogSegments = 0;
        }

        while (_queueSignal.CurrentCount > 0)
            _queueSignal.Wait(0);

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
            try { await _queueSignal.WaitAsync(ct); }
            catch (OperationCanceledException) { break; }

            Task<PcmAudio?>? nextTask;
            lock (_queueLock)
            {
                if (!_synthTasks.TryGetValue(_nextExpectedIndex, out nextTask))
                    continue;
            }

            // Await synthesis — natural buffer-underrun wait.
            // While this waits, all later synthesis tasks are already running.
            PcmAudio? audio;
            try
            {
                if (_mode == PlaybackMode.WaitForFullText)
                    await WaitForAllDialogSegmentsAsync(ct);

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
                lock (_queueLock) { _synthTasks.Remove(_nextExpectedIndex); _nextExpectedIndex++; }
                continue;
            }

            AssembledSegment? playedSegment;
            lock (_queueLock)
            {
                _segmentMap.TryGetValue(_nextExpectedIndex, out playedSegment);
                _synthTasks.Remove(_nextExpectedIndex);
                _segmentMap.Remove(_nextExpectedIndex);
                _nextExpectedIndex++;
            }

            if (audio == null) continue;

            // When WaitForFullText is enabled, keep remote batch subsegments inside one
            // active player session so the buffer never drains to Idle between pieces.
            if (_mode == PlaybackMode.WaitForFullText &&
                playedSegment != null &&
                !string.IsNullOrWhiteSpace(playedSegment.BatchId) &&
                playedSegment.BatchSegments != null && playedSegment.BatchSegments.Count > 1)
            {
                var batchAudios = new List<PcmAudio> { audio };
                int startSeg = _nextExpectedIndex - 1;
                int endSeg = startSeg;

                while (true)
                {
                    AssembledSegment? nextBatchSeg;
                    Task<PcmAudio?>? nextBatchTask;
                    lock (_queueLock)
                    {
                        if (!_segmentMap.TryGetValue(_nextExpectedIndex, out nextBatchSeg) ||
                            !string.Equals(nextBatchSeg.BatchId, playedSegment.BatchId, StringComparison.Ordinal) ||
                            !_synthTasks.TryGetValue(_nextExpectedIndex, out nextBatchTask))
                        {
                            break;
                        }
                    }

                    var nextAudio = await nextBatchTask;
                    lock (_queueLock)
                    {
                        _synthTasks.Remove(_nextExpectedIndex);
                        _segmentMap.Remove(_nextExpectedIndex);
                        _nextExpectedIndex++;
                    }

                    if (nextAudio != null)
                        batchAudios.Add(nextAudio);
                    endSeg++;
                }

                try
                {
                    AppServices.SetOperationStatus("Playing audio…");
                    var mergedBatchAudio = ConcatenatePcm(batchAudios);
                    System.Diagnostics.Debug.WriteLine($"[PC] Play batch merged start segs={startSeg}-{endSeg} items={batchAudios.Count} samples={mergedBatchAudio.Samples.Length} pending={_synthTasks.Count}");
                    await _player.PlayAsync(mergedBatchAudio, ct);
                    AppServices.ClearOperationStatus();
                    System.Diagnostics.Debug.WriteLine($"[PC] Play batch merged done segs={startSeg}-{endSeg}");
                }
                catch (OperationCanceledException) { AppServices.ClearOperationStatus(); break; }
                catch (Exception ex) when (IsCancellationIoException(ex, ct))
                {
                    AppServices.ClearOperationStatus(); break;
                }
                catch (Exception ex)
                {
                    AppServices.ClearOperationStatus();
                    System.Diagnostics.Debug.WriteLine($"[PlaybackCoordinator] Batch playback error: {ex.Message}");
                }

                continue;
            }

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

    private Task<RemoteTtsProvider.RemoteBatchResolution> GetOrCreateRemoteBatchTask(
        AssembledSegment segment,
        RemoteTtsProvider remoteProvider,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(segment.BatchId) || segment.BatchSegments == null || segment.BatchSegments.Count == 0)
            throw new InvalidOperationException("Segment is missing remote batch metadata.");

        lock (_queueLock)
        {
            if (_remoteBatchTasks.TryGetValue(segment.BatchId, out var existing))
                return existing;

            var created = remoteProvider.SubmitSplitBatchAsync(
                segment.BatchSegments,
                segment.Slot,
                ct,
                segment.BespokeSampleId,
                segment.BespokeExaggeration,
                segment.BespokeCfgWeight,
                segment.BatchId);
            _remoteBatchTasks[segment.BatchId] = created;
            return created;
        }
    }

    private async Task<PcmAudio?> SynthesizeBatchSegmentAsync(
        AssembledSegment segment,
        RemoteTtsProvider remoteProvider,
        CancellationToken ct)
    {
        var batchTask = GetOrCreateRemoteBatchTask(segment, remoteProvider, ct);
        var batch = await batchTask;
        if (string.IsNullOrWhiteSpace(segment.BatchSegmentId) || !batch.Segments.TryGetValue(segment.BatchSegmentId, out var response))
            throw new InvalidOperationException($"Remote batch response missing segment '{segment.BatchSegmentId ?? "<null>"}'.");

        var oggBytes = await remoteProvider.FetchBatchSegmentResultAsync(batch.BatchId, response.ProgressKey, response.CacheKey, ct);
        System.Diagnostics.Debug.WriteLine($"[PC] Remote batch synth complete seg={segment.SegmentIndex} batchId={batch.BatchId} batchSeg={segment.BatchSegmentId} progressKey={response.ProgressKey} cacheKey={response.CacheKey} bytes={oggBytes.Length}");
        var audio = await RemoteTtsProvider.DecodeOggAsync(oggBytes, ct);
        return DspFilterChain.Apply(audio, _provider.ResolveProfile(segment.Slot)?.Dsp);
    }

    // ── Synthesis ─────────────────────────────────────────────────────────────

    private async Task<PcmAudio?> SynthesizeSegmentAsync(AssembledSegment segment, CancellationToken ct)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[PC] Synth start seg={segment.SegmentIndex} slot={segment.Slot} provider={_provider.ProviderId}");
        // Suppressor key includes SegmentIndex so two segments with identical text
        // at different positions in the same dialog (e.g. "You flip to the next
        // section." at seq=5 and seq=7) are never suppressed by each other.
        var suppressorKey = $"{segment.Slot}:{segment.SegmentIndex}";
        if (_recentSpeechSuppressor.ShouldSuppress(segment.Text, suppressorKey))
        {
            System.Diagnostics.Debug.WriteLine($"[PC] Suppressed seg={segment.SegmentIndex} slot={suppressorKey} (recent repeat)");
            return null;
        }

        // Cache key does NOT include SegmentIndex. The same text spoken by the
        // same voice at any position in any dialog should share a cache entry.
        // SegmentIndex in the cache key caused:
        //   (a) cache misses when text shaping changed segment boundaries
        //   (b) stale Human-slot audio surfacing because the old key was never hit
        // The slot string (e.g. "BloodElf/Male") already namespaces the key —
        // two different races or genders with the same sample never collide.
        var cacheSlotKey = $"{segment.Slot}";

        // Bespoke sample only applies to NPC voice slots — never narrator.
        // Narrator segments share the same NpcId as the NPC dialog but should
        // always use the narrator voice profile, not the NPC's bespoke sample.
        bool applyBespoke = !string.IsNullOrWhiteSpace(segment.BespokeSampleId)
                            && segment.Slot.Group != Protocol.AccentGroup.Narrator;

        if (_provider is RemoteTtsProvider remoteProvider &&
            !string.IsNullOrWhiteSpace(segment.BatchId) &&
            segment.BatchSegments != null && segment.BatchSegments.Count > 1 &&
            !string.IsNullOrWhiteSpace(segment.BatchSegmentId))
        {
            System.Diagnostics.Debug.WriteLine($"[PC] Using remote batch seg={segment.SegmentIndex} batchId={segment.BatchId} batchSeg={segment.BatchSegmentId} primeFrom={segment.PrimeFromBatchSegmentId ?? "-"}");
            return await SynthesizeBatchSegmentAsync(segment, remoteProvider, ct);
        }

        if (!string.IsNullOrWhiteSpace(segment.BespokeSampleId) && !applyBespoke)
            System.Diagnostics.Debug.WriteLine(
                $"[PC] Bespoke ignored for narrator seg={segment.SegmentIndex}");
        else if (applyBespoke)
            System.Diagnostics.Debug.WriteLine(
                $"[PC] Bespoke applied seg={segment.SegmentIndex} sample={segment.BespokeSampleId}");

        var profile = applyBespoke && _provider is RemoteTtsProvider remoteProviderForSample
            ? remoteProviderForSample.ResolveSampleProfile(segment.BespokeSampleId!, segment.Slot)
            : _provider.ResolveProfile(segment.Slot);

        var voiceId = applyBespoke
            ? $"sample:{profile?.BuildIdentityKey() ?? segment.BespokeSampleId!}"
            : _provider.ResolveVoiceId(segment.Slot);

        // Cache key includes slot string as namespace prefix so two different slots
        // that happen to share the same sample (e.g. Narrator and Tortollan both
        // defaulting to am_adam) never share cache entries and play the wrong voice.
        // Bespoke entries also include the sample ID to distinguish from the slot default.
        var effectiveVoiceId = applyBespoke
            ? $"{cacheSlotKey}:{voiceId}+bespoke:{segment.BespokeSampleId}"
            : $"{cacheSlotKey}:{voiceId}";

        var cached = await _cache.TryGetDecodedAsync(segment.Text, effectiveVoiceId, _provider.ProviderId, "", ct);
        if (cached != null)
        {
            System.Diagnostics.Debug.WriteLine($"[PC] Cache HIT seg={segment.SegmentIndex} slot={cacheSlotKey} voice={effectiveVoiceId} words={Regex.Matches(segment.Text ?? string.Empty, @"\b[\p{L}\p{N}']+\b", RegexOptions.CultureInvariant).Count} text='{PreviewSegment(segment.Text)}'");
            return DspFilterChain.Apply(cached, profile?.Dsp);
        }
        System.Diagnostics.Debug.WriteLine($"[PC] Cache MISS seg={segment.SegmentIndex} slot={cacheSlotKey} voice={effectiveVoiceId} words={Regex.Matches(segment.Text ?? string.Empty, @"\b[\p{L}\p{N}']+\b", RegexOptions.CultureInvariant).Count} text='{PreviewSegment(segment.Text)}'");

        if (_provider is RemoteTtsProvider remoteProviderSingle)
        {
            var oggBytes = await remoteProviderSingle.SynthesizeOggAsync(
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


    private async Task WaitForAllDialogSegmentsAsync(CancellationToken ct)
    {
        while (true)
        {
            Task<PcmAudio?>[]? tasksToAwait = null;
            int firstNeeded = 0;
            int remainingNeeded = 0;
            lock (_queueLock)
            {
                firstNeeded = _nextExpectedIndex;
                remainingNeeded = _expectedDialogSegments - _nextExpectedIndex;
                if (remainingNeeded > 0 && _synthTasks.Count >= remainingNeeded)
                {
                    bool haveAll = true;
                    for (int i = firstNeeded; i < _expectedDialogSegments; i++)
                    {
                        if (!_synthTasks.ContainsKey(i))
                        {
                            haveAll = false;
                            break;
                        }
                    }

                    if (haveAll)
                        tasksToAwait = Enumerable.Range(firstNeeded, remainingNeeded).Select(i => _synthTasks[i]).ToArray();
                }
            }

            if (tasksToAwait != null)
            {
                AppServices.SetOperationStatus("Waiting for full text…");
                System.Diagnostics.Debug.WriteLine($"[PC] WaitForFullText holding playback until segs {firstNeeded}-{_expectedDialogSegments - 1} ({tasksToAwait.Length} segment(s)) are synthesized");
                await Task.WhenAll(tasksToAwait);
                AppServices.ClearOperationStatus();
                System.Diagnostics.Debug.WriteLine("[PC] WaitForFullText released playback");
                return;
            }

            await Task.Delay(10, ct);
        }
    }

    private static async IAsyncEnumerable<PcmAudio> ToAsyncEnumerable(IEnumerable<PcmAudio> audios)
    {
        foreach (var audio in audios)
        {
            yield return audio;
            await Task.Yield();
        }
    }

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
        _queueSignal.Dispose();
    }
    private static string PreviewSegment(string? text, int max = 100)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "<empty>";

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length <= max)
            return normalized;

        return normalized[..max] + "...";
    }
}
