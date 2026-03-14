// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

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
    public async Task<PcmAudio> SynthesizeAsync(string text, VoiceSlot slot, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var profile = ResolveVoiceProfile(slot);
        var voice = GetVoiceForSlot(slot);
        var pcm = await Task.Run(() => InferAllPhrases(text, voice, profile), ct);

        ct.ThrowIfCancellationRequested();
        return new PcmAudio(pcm, 24000, 1);
    }

    public async IAsyncEnumerable<(PcmAudio audio, int phraseIndex, int phraseCount)> SynthesizePhraseStreamAsync(
        string text,
        VoiceSlot slot,
        string tempDirectory,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureInitializedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var profile = ResolveVoiceProfile(slot);
        var voice = GetVoiceForSlot(slot);
        // DisableChunking on the VoiceProfile takes per-slot precedence.
        // The provider-level EnablePhraseChunking is a global fallback for providers
        // that don't have per-slot profiles yet (e.g. WinRt).
        bool doChunk = EnablePhraseChunking && !profile.DisableChunking;
        var phrases = doChunk ? TextSplitter.Split(text) : new List<string> { text };
        int count = phrases.Count;
        var channel = Channel.CreateBounded<(int index, float[] pcm)>(count);

        int remaining = count;
        for (int i = 0; i < count; i++)
        {
            var phraseIndex = i;
            var tokens = Tokenizer.Tokenize(phrases[i], string.IsNullOrWhiteSpace(profile.LangCode) ? "en-us" : profile.LangCode);
            var dSegConfig = new DefaultSegmentationConfig
            {
                MaxFirstSegmentLength = 250,
                MinFirstSegmentLength = 10,
                MaxSecondSegmentLength = 250,
                MinFollowupSegmentsLength = 500
            };
            var subSegs = SegmentationSystem.SplitToSegments(tokens, dSegConfig);

            int subTotal = subSegs.Count;
            var subChunks = new float[subTotal][];
            int subRemaining = subTotal;

            for (int s = 0; s < subTotal; s++)
            {
                var segRef = subSegs[s];
                var slotIdx = s;
                _tts!.EnqueueJob(KokoroJob.Create(segRef, voice, speed: profile.SpeechRate, OnComplete: chunk =>
                {
                    subChunks[slotIdx] = chunk;
                    if (Interlocked.Decrement(ref subRemaining) == 0)
                    {
                        int len = subChunks.Sum(c => c.Length);
                        var pcm = new float[len];
                        int off = 0;
                        foreach (var c in subChunks)
                        {
                            c.CopyTo(pcm, off);
                            off += c.Length;
                        }

                        // Null out the individual sub-chunk arrays immediately after
                        // concatenation so the GC can reclaim them without waiting for
                        // the closure to go out of scope.
                        Array.Clear(subChunks, 0, subChunks.Length);

                        channel.Writer.TryWrite((phraseIndex, pcm));

                        if (Interlocked.Decrement(ref remaining) == 0)
                            channel.Writer.Complete();
                    }
                }));
            }
        }

        var pending = new Dictionary<int, float[]>();
        int nextExpected = 0;

        await foreach (var (index, pcm) in channel.Reader.ReadAllAsync(ct))
        {
            pending[index] = pcm;
            while (pending.TryGetValue(nextExpected, out var readyPcm))
            {
                pending.Remove(nextExpected);
                ct.ThrowIfCancellationRequested();
                yield return (new PcmAudio(readyPcm, 24000, 1), nextExpected, count);
                nextExpected++;
            }
        }
    }

    private float[] InferAllPhrases(string text, KokoroVoice voice, VoiceProfile profile)
    {
        var phrases = TextSplitter.Split(text);
        var allSegments = new List<int[]>();

        foreach (var phrase in phrases)
        {
            var tokens = Tokenizer.Tokenize(
                phrase,
                string.IsNullOrWhiteSpace(profile.LangCode) ? "en-us" : profile.LangCode);

            var dSegConfig = new DefaultSegmentationConfig
            {
                MaxFirstSegmentLength = 50,
                MinFirstSegmentLength = 10,
                MaxSecondSegmentLength = 150,
                MinFollowupSegmentsLength = 500
            };

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
            var kjob = KokoroJob.Create(segRef, voice, speed: profile.SpeechRate, OnComplete: chunk =>
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

        // Release individual chunk arrays immediately after concat.
        Array.Clear(chunks, 0, chunks.Length);

        return result;
    }
}