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
using RuneReaderVoice.Diagnostics;

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
    // For remote provider: backed by TaskCompletionSource, completed when batch result arrives.
    // For local provider: backed by direct async synthesis task as before.
    private readonly Dictionary<int, Task<PcmAudio?>>                       _synthTasks    = new();
    private readonly Dictionary<int, TaskCompletionSource<PcmAudio?>>       _synthTcs      = new();
    private readonly Dictionary<int, AssembledSegment>                       _segmentMap    = new();
    private readonly Dictionary<int, AssembledSegment>                       _pendingSegments = new();
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
    /// Called once per assembled segment (all segments arrive in a burst for the same dialog).
    /// For remote providers: segments are collected until the full dialog count is known, then
    /// a single batch POST is submitted. Each segment's Task is backed by a TCS completed when
    /// its batch result arrives — eliminating per-segment HTTP round-trips and lock overhead.
    /// For local providers: fires synthesis immediately as before.
    /// </summary>
    public void EnqueueSegment(AssembledSegment segment)
    {
        RrvDebug.PlaybackDebug(
            $"[PC] Enqueued segment {segment.SegmentIndex}: \"{segment.Text.Substring(0, Math.Min(40, segment.Text.Length))}\"");
        lock (_queueLock)
        {
            var ct = _sessionCts?.Token ?? CancellationToken.None;
            _segmentMap[segment.SegmentIndex] = segment;
            _expectedDialogSegments = Math.Max(_expectedDialogSegments, segment.DialogSegmentCount);

            if (_provider is RemoteTtsProvider)
            {
                // Remote path: collect segments, submit as one batch when all have arrived.
                var tcs = new TaskCompletionSource<PcmAudio?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _synthTcs[segment.SegmentIndex]        = tcs;
                _synthTasks[segment.SegmentIndex]      = tcs.Task;
                _pendingSegments[segment.SegmentIndex] = segment;

                // When all segments for this dialog have arrived, submit the batch.
                // Fallback: if DialogSegmentCount is 0/unknown, treat as single-segment dialog.
                bool allArrived = segment.DialogSegmentCount > 0
                    ? _pendingSegments.Count == segment.DialogSegmentCount
                    : true;  // single unknown-count segment — submit immediately

                if (allArrived)
                {
                    var allSegments = _pendingSegments.Values
                        .OrderBy(s => s.SegmentIndex)
                        .ToList();
                    _pendingSegments.Clear();
                    _ = Task.Run(() => SubmitDialogBatchAndFillAsync(allSegments, ct), ct);
                }
            }
            else
            {
                // Local provider: synthesize immediately as before.
                var synthTask = SynthesizeSegmentAsync(segment, ct);
                _synthTasks[segment.SegmentIndex] = synthTask;
            }

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
        RrvDebug.PlaybackDebug(
            $"[PC] Session reset — cancelling {_synthTasks.Count} pending task(s)");
        _player.Stop();
        _sessionCts?.Cancel();
        lock (_queueLock)
        {
            _synthTasks.Clear();
            _synthTcs.Clear();
            _segmentMap.Clear();
            _pendingSegments.Clear();
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

                RrvDebug.PlaybackDebug(
                    $"[PC] Awaiting segment {_nextExpectedIndex}, tasks in map: {string.Join(",", _synthTasks.Keys.OrderBy(k=>k))}");

                audio = await nextTask;
            }
            catch (OperationCanceledException) { AppServices.ClearPlaybackActivity(); break; }
            catch (Exception ex) when (IsCancellationIoException(ex, ct))
            {
                AppServices.ClearPlaybackActivity(); break;
            }
            catch (Exception ex)
            {
                AppServices.ClearPlaybackActivity();
                RrvDebug.PlaybackDebug(
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
                    AppServices.SetPlaybackActivity(MainActivityKind.Playing, "Playing audio…");
                    var mergedBatchAudio = ConcatenatePcm(batchAudios);
                    RrvDebug.PlaybackDebug($"[PC] Play batch merged start segs={startSeg}-{endSeg} items={batchAudios.Count} samples={mergedBatchAudio.Samples.Length} pending={_synthTasks.Count}");
                    await _player.PlayAsync(mergedBatchAudio, ct);
                    AppServices.ClearPlaybackActivity();
                    RrvDebug.PlaybackDebug($"[PC] Play batch merged done segs={startSeg}-{endSeg}");
                }
                catch (OperationCanceledException) { AppServices.ClearPlaybackActivity(); break; }
                catch (Exception ex) when (IsCancellationIoException(ex, ct))
                {
                    AppServices.ClearPlaybackActivity(); break;
                }
                catch (Exception ex)
                {
                    AppServices.ClearPlaybackActivity();
                    RrvDebug.PlaybackDebug($"[PlaybackCoordinator] Batch playback error: {ex.Message}");
                }

                continue;
            }

            // Play segment N. While playing, synthesis of segment N+1 is already running.
            try
            {
                AppServices.SetPlaybackActivity(MainActivityKind.Playing, "Playing audio…");
                int segIdx = _nextExpectedIndex - 1;
                RuneReaderVoice.AppServices.RecordAudioStart(segIdx);
                RrvDebug.PlaybackDebug(
                    $"[PC] Play start seg={segIdx} samples={audio?.Samples.Length} pending={_synthTasks.Count}");
                if (audio != null)
                {
                    await _player.PlayAsync(audio, ct);
                }
                AppServices.ClearPlaybackActivity();
                RrvDebug.PlaybackDebug($"[PC] Play done seg={segIdx}");
            }
            catch (OperationCanceledException) { AppServices.ClearPlaybackActivity(); break; }
            catch (Exception ex) when (IsCancellationIoException(ex, ct))
            {
                AppServices.ClearPlaybackActivity(); break;
            }
            catch (Exception ex)
            {
                AppServices.ClearPlaybackActivity();
                RrvDebug.PlaybackDebug($"[PlaybackCoordinator] Playback error: {ex.Message}");
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

            bool applyBespoke = !string.IsNullOrWhiteSpace(segment.BespokeSampleId)
                                && !segment.IsNarratorSegment;
            bool suppressStoredSeed = !segment.IsNarratorSegment
                                      && segment.NpcId > 0
                                      && !segment.UseNpcIdAsSeed
                                      && !applyBespoke;
            var created = remoteProvider.SubmitSplitBatchAsync(
                segment.BatchSegments,
                segment.Slot,
                ct,
                segment.BespokeSampleId,
                segment.BespokeExaggeration,
                segment.BespokeCfgWeight,
                segment.BatchId,
                null,
                segment.UseNpcIdAsSeed && segment.NpcId > 0 ? segment.NpcId : null,
                suppressStoredSeed);
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
        RrvDebug.PlaybackDebug($"[PC] Remote batch synth complete seg={segment.SegmentIndex} batchId={batch.BatchId} batchSeg={segment.BatchSegmentId} progressKey={response.ProgressKey} cacheKey={response.CacheKey} bytes={oggBytes.Length}");
        var audio = await RemoteTtsProvider.DecodeOggAsync(oggBytes, ct);

        bool applyBespoke = !string.IsNullOrWhiteSpace(segment.BespokeSampleId)
                            && !segment.IsNarratorSegment;
        var playbackProfile = applyBespoke
            ? remoteProvider.ResolveSampleProfile(segment.BespokeSampleId!, segment.Slot)
            : _provider.ResolveProfile(segment.Slot);

        return DspFilterChain.Apply(audio, playbackProfile?.Dsp);
    }

    // ── Dialog batch submit ───────────────────────────────────────────────────

    /// <summary>
    /// Submits all segments for one dialog as a single batch POST, then fetches
    /// each result and completes the corresponding TCS so the playback loop can
    /// proceed as results arrive.
    ///
    /// Prime-chain logic: each segment is primed from the immediately prior segment
    /// of the same voice slot. Narrator and NPC slots maintain independent chains —
    /// a narrator interjection does not reset the NPC's prime context.
    /// </summary>
    private async Task SubmitDialogBatchAndFillAsync(
        List<AssembledSegment> segments,
        CancellationToken ct)
    {
        if (!(_provider is RemoteTtsProvider remoteProvider))
            return;

        try
        {
            // Build slot-aware prime chain: last segment_id per slot.
            var lastSegmentIdPerSlot = new Dictionary<string, string>(StringComparer.Ordinal);
            var plans = new List<BatchSegmentPlan>(segments.Count);

            foreach (var seg in segments)
            {
                var slotKey  = seg.Slot.ToString();
                var segId    = $"d_{seg.SegmentIndex}";
                string? primeFrom = null;
                lastSegmentIdPerSlot.TryGetValue(slotKey, out primeFrom);
                lastSegmentIdPerSlot[slotKey] = segId;

                plans.Add(new BatchSegmentPlan
                {
                    SegmentId          = segId,
                    Text               = seg.Text ?? string.Empty,
                    PrimeFromSegmentId = primeFrom,
                });
            }

            // All segments in this dialog share the same slot/voice — use first segment's
            // bespoke/seed settings. If slots differ (narrator + NPC) the batch uses the
            // slot resolved per-segment inside SubmitSplitBatchAsync via the plan list.
            // We use the first segment's slot as the primary slot for profile resolution;
            // the server handles per-segment cache keys independently.
            var firstSeg       = segments[0];
            var batchId        = Guid.NewGuid().ToString("N");
            bool applyBespoke  = !string.IsNullOrWhiteSpace(firstSeg.BespokeSampleId)
                                 && !firstSeg.IsNarratorSegment;
            bool suppressSeed  = !firstSeg.IsNarratorSegment
                                 && firstSeg.NpcId > 0
                                 && !firstSeg.UseNpcIdAsSeed
                                 && !applyBespoke;

            RrvDebug.PlaybackDebug(
                $"[PC] Dialog batch submit batchId={batchId} segments={segments.Count}");

            // SubmitSplitBatchAsync builds per-segment requests honoring each segment's
            // text, cache key, and prime chain. The slot passed here is used only for
            // profile resolution when a per-segment slot is not overridden.
            //
            // IMPORTANT: we need per-segment slot support. SubmitSplitBatchAsync takes
            // a single slot — for mixed-slot dialogs (narrator + NPC) we group by slot
            // and submit one batch per slot group, then merge results.
            var slotGroups = segments
                .GroupBy(s => s.Slot.ToString(), StringComparer.Ordinal)
                .ToList();

            // Map segment_id -> (progressKey, cacheKey) across all slot batches
            var allSegmentResponses = new Dictionary<string, V2BatchSegmentResponse>(StringComparer.Ordinal);

            foreach (var group in slotGroups)
            {
                var groupSegments = group.OrderBy(s => s.SegmentIndex).ToList();
                var groupPlans    = plans
                    .Where(p => groupSegments.Any(s => $"d_{s.SegmentIndex}" == p.SegmentId))
                    .ToList();

                var groupFirstSeg   = groupSegments[0];
                bool groupBespoke   = !string.IsNullOrWhiteSpace(groupFirstSeg.BespokeSampleId)
                                      && !groupFirstSeg.IsNarratorSegment;
                bool groupSuppSeed  = !groupFirstSeg.IsNarratorSegment
                                      && groupFirstSeg.NpcId > 0
                                      && !groupFirstSeg.UseNpcIdAsSeed
                                      && !groupBespoke;

                var resolution = await remoteProvider.SubmitSplitBatchAsync(
                    groupPlans,
                    groupFirstSeg.Slot,
                    ct,
                    groupBespoke ? groupFirstSeg.BespokeSampleId    : null,
                    groupBespoke ? groupFirstSeg.BespokeExaggeration : null,
                    groupBespoke ? groupFirstSeg.BespokeCfgWeight    : null,
                    batchId,
                    null,
                    groupFirstSeg.UseNpcIdAsSeed && groupFirstSeg.NpcId > 0 ? groupFirstSeg.NpcId : null,
                    groupSuppSeed);

                foreach (var kvp in resolution.Segments)
                    allSegmentResponses[kvp.Key] = kvp.Value;
            }

            // Fetch results and complete TCS for each segment as they arrive.
            // Fetch all in parallel — server processes them sequentially on the worker lock
            // but we don't want the client serializing the fetches.
            var fetchTasks = segments.Select(async seg =>
            {
                var segId = $"d_{seg.SegmentIndex}";
                try
                {
                    if (!allSegmentResponses.TryGetValue(segId, out var response))
                        throw new InvalidOperationException(
                            $"Batch response missing segment '{segId}' for seg={seg.SegmentIndex}");

                    var oggBytes = await remoteProvider.FetchBatchSegmentResultAsync(
                        batchId, response.ProgressKey, response.CacheKey, ct);

                    RrvDebug.PlaybackDebug(
                        $"[PC] Dialog batch result seg={seg.SegmentIndex} progressKey={response.ProgressKey} bytes={oggBytes.Length}");

                    var decoded = await RemoteTtsProvider.DecodeOggAsync(oggBytes, ct);

                    bool segBespoke = !string.IsNullOrWhiteSpace(seg.BespokeSampleId)
                                      && !seg.IsNarratorSegment;
                    var playbackProfile = segBespoke
                        ? remoteProvider.ResolveSampleProfile(seg.BespokeSampleId!, seg.Slot)
                        : _provider.ResolveProfile(seg.Slot);

                    var audio = DspFilterChain.Apply(decoded, playbackProfile?.Dsp);

                    // Store in local client cache so repeat encounters are instant
                    var cacheText = remoteProvider.NormalizeSubmittedTextForCache(seg.Text ?? string.Empty);
                    bool segApplyBespoke = !string.IsNullOrWhiteSpace(seg.BespokeSampleId) && !seg.IsNarratorSegment;
                    var effectiveVoiceId = BuildEffectiveVoiceId(seg, remoteProvider, segApplyBespoke);
                    await _cache.StoreOggAsync(oggBytes, cacheText, effectiveVoiceId, _provider.ProviderId, string.Empty, ct);

                    lock (_queueLock)
                    {
                        if (_synthTcs.TryGetValue(seg.SegmentIndex, out var tcs))
                            tcs.TrySetResult(audio);
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_queueLock)
                    {
                        if (_synthTcs.TryGetValue(seg.SegmentIndex, out var tcs))
                            tcs.TrySetCanceled(ct);
                    }
                }
                catch (Exception ex)
                {
                    RrvDebug.PlaybackDebug(
                        $"[PC] Dialog batch fetch error seg={seg.SegmentIndex}: {ex.Message}");
                    lock (_queueLock)
                    {
                        if (_synthTcs.TryGetValue(seg.SegmentIndex, out var tcs))
                            tcs.TrySetException(ex);
                    }
                }
            }).ToArray();

            await Task.WhenAll(fetchTasks);
        }
        catch (OperationCanceledException)
        {
            // Cancel all pending TCS for this dialog
            lock (_queueLock)
            {
                foreach (var seg in segments)
                {
                    if (_synthTcs.TryGetValue(seg.SegmentIndex, out var tcs))
                        tcs.TrySetCanceled(ct);
                }
            }
        }
        catch (Exception ex)
        {
            RrvDebug.PlaybackDebug($"[PC] Dialog batch submit error: {ex.Message}");
            lock (_queueLock)
            {
                foreach (var seg in segments)
                {
                    if (_synthTcs.TryGetValue(seg.SegmentIndex, out var tcs))
                        tcs.TrySetException(ex);
                }
            }
        }
    }

    private string BuildEffectiveVoiceId(AssembledSegment seg, RemoteTtsProvider remoteProvider, bool applyBespoke)
    {
        var cacheSlotKey = seg.Slot.ToString();
        bool forcedNpcSeed = seg.UseNpcIdAsSeed && seg.NpcId > 0;
        bool suppressSeed  = !seg.IsNarratorSegment && seg.NpcId > 0 && !seg.UseNpcIdAsSeed && !applyBespoke;

        var profile = remoteProvider.ResolveEffectiveSynthesisProfile(
            seg.Slot,
            applyBespoke ? seg.BespokeSampleId    : null,
            applyBespoke ? seg.BespokeExaggeration : null,
            applyBespoke ? seg.BespokeCfgWeight    : null,
            forcedNpcSeed ? seg.NpcId : null,
            suppressSeed);

        var voiceId = applyBespoke
            ? $"sample:{profile.BuildIdentityKey()}"
            : profile.BuildIdentityKey();

        return applyBespoke
            ? $"{cacheSlotKey}:{voiceId}+bespoke:{seg.BespokeSampleId}"
            : $"{cacheSlotKey}:{voiceId}";
    }

    // ── Synthesis ─────────────────────────────────────────────────────────────

    private async Task<PcmAudio?> SynthesizeSegmentAsync(AssembledSegment segment, CancellationToken ct)
    {
        RuneReaderVoice.AppServices.RecordTtsStart(segment);
        RrvDebug.PlaybackDebug(
            $"[PC] Synth start seg={segment.SegmentIndex} slot={segment.Slot} provider={_provider.ProviderId}");
        // Suppressor key includes SegmentIndex so two segments with identical text
        // at different positions in the same dialog (e.g. "You flip to the next
        // section." at seq=5 and seq=7) are never suppressed by each other.
        var suppressorKey = $"{segment.Slot}:{segment.SegmentIndex}";
        if (_recentSpeechSuppressor.ShouldSuppress(segment.Text, suppressorKey))
        {
            RrvDebug.PlaybackDebug($"[PC] Suppressed seg={segment.SegmentIndex} slot={suppressorKey} (recent repeat)");
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
                            && !segment.IsNarratorSegment;
        int? forcedNpcSeed = segment.UseNpcIdAsSeed && segment.NpcId > 0 ? segment.NpcId : null;

        // Race/default slot seeds are suppressed for normal NPC playback unless
        // explicitly using NPC ID as seed. Bespoke sample profiles are different:
        // their stored seed is part of the sample-specific voice identity and must
        // be honored so Default Voice preview and in-game playback match.
        bool suppressStoredSeed = !segment.IsNarratorSegment
                                  && segment.NpcId > 0
                                  && !segment.UseNpcIdAsSeed
                                  && !applyBespoke;

        if (_provider is RemoteTtsProvider remoteProvider &&
            !string.IsNullOrWhiteSpace(segment.BatchId) &&
            segment.BatchSegments != null && segment.BatchSegments.Count > 1 &&
            !string.IsNullOrWhiteSpace(segment.BatchSegmentId))
        {
            RrvDebug.PlaybackDebug($"[PC] Using remote batch seg={segment.SegmentIndex} batchId={segment.BatchId} batchSeg={segment.BatchSegmentId} primeFrom={segment.PrimeFromBatchSegmentId ?? "-"}");
            return await SynthesizeBatchSegmentAsync(segment, remoteProvider, ct);
        }

        if (!string.IsNullOrWhiteSpace(segment.BespokeSampleId) && !applyBespoke)
            RrvDebug.PlaybackDebug(
                $"[PC] Bespoke ignored for narrator seg={segment.SegmentIndex} slot={segment.Slot} sample={segment.BespokeSampleId}");
        else if (applyBespoke)
            RrvDebug.PlaybackDebug(
                $"[PC] Bespoke applied seg={segment.SegmentIndex} sample={segment.BespokeSampleId} slot={segment.Slot} narratorFlag={segment.IsNarratorSegment}");

        var profile = _provider is RemoteTtsProvider remoteProfileProvider
            ? remoteProfileProvider.ResolveEffectiveSynthesisProfile(
                segment.Slot,
                applyBespoke ? segment.BespokeSampleId : null,
                applyBespoke ? segment.BespokeExaggeration : null,
                applyBespoke ? segment.BespokeCfgWeight : null,
                forcedNpcSeed,
                suppressStoredSeed)
            : _provider.ResolveProfile(segment.Slot);

        var voiceId = applyBespoke
            ? $"sample:{profile?.BuildIdentityKey() ?? segment.BespokeSampleId!}"
            : (_provider is RemoteTtsProvider && profile != null
                ? profile.BuildIdentityKey()
                : _provider.ResolveVoiceId(segment.Slot));

        // Cache key includes slot string as namespace prefix so two different slots
        // that happen to share the same sample (e.g. Narrator and Tortollan both
        // defaulting to am_adam) never share cache entries and play the wrong voice.
        // Bespoke entries also include the sample ID to distinguish from the slot default.
        var effectiveVoiceId = applyBespoke
            ? $"{cacheSlotKey}:{voiceId}+bespoke:{segment.BespokeSampleId}"
            : $"{cacheSlotKey}:{voiceId}";

        if (ShouldForceBookPhraseChunking(segment))
        {
            var forcedBookAudio = await TrySynthesizeForcedBookPhraseChunksAsync(
                segment,
                profile,
                effectiveVoiceId,
                applyBespoke,
                forcedNpcSeed,
                suppressStoredSeed,
                ct);

            if (forcedBookAudio != null)
                return forcedBookAudio;
        }

        var cacheText = _provider is RemoteTtsProvider remoteCacheProvider
            ? remoteCacheProvider.NormalizeSubmittedTextForCache(segment.Text)
            : segment.Text;

        // Diagnostics must reflect the exact text used for cache identity / provider
        // submission at playback time. Do this here, not in Program before cache
        // lookup, so UI cannot accidentally show pre-pipeline/source text.
        RuneReaderVoice.AppServices.RecordDialogSegmentServerText(segment, cacheText ?? string.Empty);

        var cacheKey = TtsAudioCache.ComputeKey(cacheText, effectiveVoiceId, _provider.ProviderId, "");
        DebugCacheTrace(
            phase: "Lookup",
            segmentIndex: segment.SegmentIndex,
            providerId: _provider.ProviderId,
            slotKey: cacheSlotKey,
            voiceId: effectiveVoiceId,
            cacheText: cacheText,
            cacheKey: cacheKey,
            originalText: segment.Text);

        var cached = await _cache.TryGetDecodedAsync(cacheText, effectiveVoiceId, _provider.ProviderId, "", ct);
        if (cached != null)
        {
            RuneReaderVoice.AppServices.RecordCacheState(segment, hit: true);
            RrvDebug.PlaybackDebug($"[PC] Cache HIT seg={segment.SegmentIndex} slot={cacheSlotKey} voice={effectiveVoiceId} words={Regex.Matches(segment.Text ?? string.Empty, @"\b[\p{L}\p{N}']+\b", RegexOptions.CultureInvariant).Count} text='{PreviewSegment(segment.Text)}'");
            DebugCacheTrace(
                phase: "Hit",
                segmentIndex: segment.SegmentIndex,
                providerId: _provider.ProviderId,
                slotKey: cacheSlotKey,
                voiceId: effectiveVoiceId,
                cacheText: cacheText,
                cacheKey: cacheKey,
                originalText: segment.Text);
            return DspFilterChain.Apply(cached, profile?.Dsp);
        }
        RuneReaderVoice.AppServices.RecordCacheState(segment, hit: false);
        RrvDebug.PlaybackDebug($"[PC] Cache MISS seg={segment.SegmentIndex} slot={cacheSlotKey} voice={effectiveVoiceId} words={Regex.Matches(segment.Text ?? string.Empty, @"\b[\p{L}\p{N}']+\b", RegexOptions.CultureInvariant).Count} text='{PreviewSegment(segment.Text)}'");
        DebugCacheTrace(
            phase: "Miss",
            segmentIndex: segment.SegmentIndex,
            providerId: _provider.ProviderId,
            slotKey: cacheSlotKey,
            voiceId: effectiveVoiceId,
            cacheText: cacheText,
            cacheKey: cacheKey,
            originalText: segment.Text);

        if (_provider is RemoteTtsProvider remoteProviderSingle)
        {
            var segmentText = segment.Text ?? string.Empty;
            var oggBytes = await remoteProviderSingle.SynthesizeOggAsync(
                segmentText, segment.Slot, ct,
                applyBespoke ? segment.BespokeSampleId    : null,
                applyBespoke ? segment.BespokeExaggeration : null,
                applyBespoke ? segment.BespokeCfgWeight   : null,
                null,
                null,
                forcedNpcSeed,
                suppressStoredSeed);

            RrvDebug.PlaybackDebug(
                $"[PC] Remote synth complete seg={segment.SegmentIndex} bytes={oggBytes.Length}");
            await _cache.StoreOggAsync(oggBytes, cacheText, effectiveVoiceId, _provider.ProviderId, string.Empty, ct);
            var decoded = await _cache.TryGetDecodedAsync(cacheText, effectiveVoiceId, _provider.ProviderId, "", ct);
            if (decoded == null)
                throw new InvalidOperationException("Remote audio cached but could not be decoded.");

            RrvDebug.PlaybackDebug(
                $"[PC] Synth done seg={segment.SegmentIndex} samples={decoded.Samples.Length} dsp={profile?.Dsp?.IsNeutral == false}");
            return DspFilterChain.Apply(decoded, profile?.Dsp);
        }

        // Local provider — synthesize and concatenate all phrase chunks
        var sw         = System.Diagnostics.Stopwatch.StartNew();
        var chunkTexts = TextChunkingPolicy.GetChunkTexts(segment.Text ?? string.Empty, _provider, profile, AppServices.Settings);
        var chunks     = new List<PcmAudio>();

        if (segment.Text == null) return ConcatenatePcm(chunks);
        
        await foreach (var (audio, phraseIndex, phraseCount) in
                       _provider.SynthesizePhraseStreamAsync(segment.Text, segment.Slot, _tempDirectory, ct))
        {
            if (phraseIndex == 0)
            {
                sw.Stop();
                LastSynthesisLatency = sw.Elapsed;
            }

            var phraseText = GetPhraseText(segment.Text, phraseIndex, phraseCount, chunkTexts);
            await _cache.StoreAsync(audio, phraseText, effectiveVoiceId, _provider.ProviderId, "", ct);
            chunks.Add(DspFilterChain.Apply(audio, profile?.Dsp));
        }

        return ConcatenatePcm(chunks);
    }

    private bool ShouldForceBookPhraseChunking(AssembledSegment segment)
        => AppServices.Settings.ForceBookPhraseChunking && IsSyntheticBookNpcId(segment.NpcId);

    private async Task<PcmAudio?> TrySynthesizeForcedBookPhraseChunksAsync(
        AssembledSegment segment,
        VoiceProfile? profile,
        string effectiveVoiceId,
        bool applyBespoke,
        int? forcedNpcSeed,
        bool suppressStoredSeed,
        CancellationToken ct)
    {
        var text = segment.Text ?? string.Empty;
        var phrases = TextChunkingPolicy.GetChunkTexts(text, _provider.ProviderId, profile, enabled: true);
        if (phrases.Count <= 1)
            return null;

        RrvDebug.PlaybackDebug(
            $"[PC] Force book phrase chunking seg={segment.SegmentIndex} npc={segment.NpcId} chunks={phrases.Count} provider={_provider.ProviderId}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var chunks = new List<PcmAudio>(phrases.Count);
        for (int i = 0; i < phrases.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var phrase = phrases[i];
            var phraseCacheText = _provider is RemoteTtsProvider remoteCacheProvider
                ? remoteCacheProvider.NormalizeSubmittedTextForCache(phrase)
                : phrase;

            var cached = await _cache.TryGetDecodedAsync(phraseCacheText, effectiveVoiceId, _provider.ProviderId, "", ct);
            if (cached != null)
            {
                chunks.Add(DspFilterChain.Apply(cached, profile?.Dsp));
                continue;
            }

            PcmAudio decoded;
            if (_provider is RemoteTtsProvider remoteProvider)
            {
                var oggBytes = await remoteProvider.SynthesizeOggAsync(
                    phrase,
                    segment.Slot,
                    ct,
                    applyBespoke ? segment.BespokeSampleId    : null,
                    applyBespoke ? segment.BespokeExaggeration : null,
                    applyBespoke ? segment.BespokeCfgWeight    : null,
                    null,
                    null,
                    forcedNpcSeed,
                    suppressStoredSeed);

                await _cache.StoreOggAsync(oggBytes, phraseCacheText, effectiveVoiceId, _provider.ProviderId, string.Empty, ct);
                decoded = await _cache.TryGetDecodedAsync(phraseCacheText, effectiveVoiceId, _provider.ProviderId, "", ct)
                          ?? throw new InvalidOperationException("Forced book chunk cached but could not be decoded.");
            }
            else
            {
                decoded = await _provider.SynthesizeAsync(phrase, segment.Slot, ct);
                await _cache.StoreAsync(decoded, phraseCacheText, effectiveVoiceId, _provider.ProviderId, "", ct);
            }

            if (i == 0)
            {
                sw.Stop();
                LastSynthesisLatency = sw.Elapsed;
            }

            chunks.Add(DspFilterChain.Apply(decoded, profile?.Dsp));
        }

        return ConcatenatePcm(chunks);
    }

    private static bool IsSyntheticBookNpcId(int npcId)
        => npcId >= 0xF00000 && npcId <= 0xFFFFFF;


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
                AppServices.SetPlaybackActivity(MainActivityKind.Waiting, "Waiting for full text…");
                RrvDebug.PlaybackDebug($"[PC] WaitForFullText holding playback until segs {firstNeeded}-{_expectedDialogSegments - 1} ({tasksToAwait.Length} segment(s)) are synthesized");
                await Task.WhenAll(tasksToAwait);
                AppServices.ClearPlaybackActivity();
                RrvDebug.PlaybackDebug("[PC] WaitForFullText released playback");
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
    [System.Diagnostics.Conditional("DEBUG")]
    private static void DebugCacheTrace(
        string phase,
        int segmentIndex,
        string providerId,
        string slotKey,
        string voiceId,
        string? cacheText,
        string cacheKey,
        string? originalText)
    {
        RrvDebug.CacheDebug($@"phase={phase} seg={segmentIndex}
provider={providerId}
slot={slotKey}
key={cacheKey}
voice={voiceId}
cacheText={cacheText ?? string.Empty}
originalText={originalText ?? string.Empty}
");
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
