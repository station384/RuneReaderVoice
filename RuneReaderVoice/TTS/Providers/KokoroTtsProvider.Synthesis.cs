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
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed partial class KokoroTtsProvider
{
    public async Task<PcmAudio> SynthesizeAsync(
        string text, VoiceSlot slot, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var voice = GetVoiceForSlot(slot);
        var pcm = await Task.Run(() => InferAllPhrases(text, voice), ct);

        ct.ThrowIfCancellationRequested();
        return new PcmAudio(pcm, 24000, 1);
    }

    /// <summary>
    /// Streams synthesized audio phrase by phrase.
    /// All phrase jobs are enqueued immediately so the ONNX engine works on them
    /// concurrently. Each phrase PCM buffer is yielded as soon as it completes,
    /// allowing the coordinator to start caching and playing phrase 0 while phrase 1 synthesizes.
    /// </summary>
    public async IAsyncEnumerable<(PcmAudio audio, int phraseIndex, int phraseCount)>
        SynthesizePhraseStreamAsync(
            string text,
            VoiceSlot slot,
            string tempDirectory,
            [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var voice = GetVoiceForSlot(slot);
        var phrases = EnablePhraseChunking
            ? TextSplitter.Split(text)
            : new System.Collections.Generic.List<string> { text };
        int count = phrases.Count;

        // Channel carries (phraseIndex, pcm) in completion order.
        // Bounded to count so writers never block.
        var channel = Channel.CreateBounded<(int index, float[] pcm)>(count);

        // Enqueue all phrase jobs immediately — ONNX processes them in parallel
        // on its internal thread pool. OnComplete writes into the channel.
        int remaining = count;
        for (int i = 0; i < count; i++)
        {
            var phraseIndex = i;
            var tokens = Tokenizer.Tokenize(phrases[i], "en-uk");
            //var tokens  = Tokenizer.Tokenize(phrases[i]);
            var dSegConfig = new DefaultSegmentationConfig();
            dSegConfig.MaxFirstSegmentLength = 250;
            dSegConfig.MinFirstSegmentLength = 10;
            dSegConfig.MaxSecondSegmentLength = 250;

            dSegConfig.MinFollowupSegmentsLength = 500;
            var subSegs = SegmentationSystem.SplitToSegments(
                tokens, dSegConfig);

            // For multi-segment phrases (>510 tokens) we collect sub-segments
            // and write to channel only when all sub-segments for this phrase complete.
            int subTotal = subSegs.Count;
            var subChunks = new float[subTotal][];
            int subRemaining = subTotal;

            for (int s = 0; s < subTotal; s++)
            {
                var segRef = subSegs[s];
                var slotIdx = s;
                _tts!.EnqueueJob(KokoroJob.Create(segRef, voice, speed: 1f, OnComplete: chunk =>
                {
                    subChunks[slotIdx] = chunk;
                    if (Interlocked.Decrement(ref subRemaining) == 0)
                    {
                        // All sub-segments for this phrase done — concatenate and write
                        int len = subChunks.Sum(c => c.Length);
                        var pcm = new float[len];
                        int off = 0;
                        foreach (var c in subChunks)
                        {
                            c.CopyTo(pcm, off);
                            off += c.Length;
                        }

                        channel.Writer.TryWrite((phraseIndex, pcm));

                        if (Interlocked.Decrement(ref remaining) == 0)
                            channel.Writer.Complete();
                    }
                }));
            }
        }

        // Collect completed phrases and yield them in ORDER.
        // Results may arrive out of order (phrase 1 finishes before phrase 0),
        // so we buffer out-of-order arrivals and yield in strict sequence.
        var pending = new Dictionary<int, float[]>();
        int nextExpected = 0;

        await foreach (var (index, pcm) in channel.Reader.ReadAllAsync(ct))
        {
            pending[index] = pcm;

            // Yield as many sequential phrases as are ready
            while (pending.TryGetValue(nextExpected, out var readyPcm))
            {
                pending.Remove(nextExpected);

                ct.ThrowIfCancellationRequested();
                yield return (new PcmAudio(readyPcm, 24000, 1), nextExpected, count);
                nextExpected++;
            }
        }
    }

    // ── PCM inference ─────────────────────────────────────────────────────────

    // Full-text blocking inference used by SynthesizeAsync.
    private float[] InferAllPhrases(string text, KokoroVoice voice)
    {
        var phrases = TextSplitter.Split(text);
        var allSegments = new List<int[]>();
        foreach (var phrase in phrases)
        {
            var tokens = Tokenizer.Tokenize(phrase, "en-gb-x-rp");
            var dSegConfig = new DefaultSegmentationConfig();
            dSegConfig.MaxFirstSegmentLength = 250;
            dSegConfig.MinFirstSegmentLength = 10;
            dSegConfig.MaxSecondSegmentLength = 250;
            dSegConfig.MinFollowupSegmentsLength = 500;

            var subSegs = SegmentationSystem.SplitToSegments(tokens, dSegConfig);
            allSegments.AddRange(subSegs);
        }

        int total = allSegments.Count;
        if (total == 0) return Array.Empty<float>();

        var chunks = new float[total][];
        int remaining = total;
        using var done = new ManualResetEventSlim(false);

        for (int i = 0; i < total; i++)
        {
            var segRef = allSegments[i];
            var slotIndex = i;
            var kjob = KokoroJob.Create(segRef, voice, speed: 1f, OnComplete: chunk =>
            {
                chunks[slotIndex] = chunk;
                if (Interlocked.Decrement(ref remaining) == 0)
                    done.Set();
            });

            _tts!.EnqueueJob(kjob);
        }

        done.Wait();

        int totalLen = chunks.Sum(c => c.Length);
        var result = new float[totalLen];
        int offset = 0;
        foreach (var c in chunks)
        {
            c.CopyTo(result, offset);
            offset += c.Length;
        }

        return result;
    }
}