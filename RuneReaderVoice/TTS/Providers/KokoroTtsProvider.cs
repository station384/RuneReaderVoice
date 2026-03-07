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


// KokoroTtsProvider.cs
// TTS synthesis via KokoroSharp (local ONNX, cross-platform).
// NuGet: KokoroSharp.CPU  (or KokoroSharp.GPU.Windows for CUDA)
//
// Model (~320 MB) downloads automatically on first use.
// Subscribe to OnModelDownloading / OnModelReady for UI feedback.
//
// Voice IDs are Kokoro voice pack names (e.g. "af_heart", "bm_george").
// Voice mixing: assign a blend spec like "mix:af_heart:0.7|bm_george:0.3"
// as the voice ID for any slot. The provider parses and resolves the mix.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed class KokoroTtsProvider : ITtsProvider
{
    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Fires when the ONNX model begins loading/downloading. Arg = status message.</summary>
    public event Action<string>? OnModelDownloading;

    /// <summary>Fires when the model is fully loaded and ready to synthesize.</summary>
    public event Action? OnModelReady;

    // ── Complete voice list (hexgrad/Kokoro-82M VOICES.md) ───────────────────

    public static readonly VoiceInfo[] KnownVoices =
    {
        // ── American English ──────────────────────────────────────────────
        new() { VoiceId = "af_heart",      Name = "Heart ★ (AF)",      Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_bella",      Name = "Bella ★ (AF)",      Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_nicole",     Name = "Nicole (AF)",        Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_sarah",      Name = "Sarah (AF)",         Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_aoede",      Name = "Aoede (AF)",         Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_kore",       Name = "Kore (AF)",          Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_alloy",      Name = "Alloy (AF)",         Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_nova",       Name = "Nova (AF)",          Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_sky",        Name = "Sky (AF)",           Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_jessica",    Name = "Jessica (AF)",       Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "af_river",      Name = "River (AF)",         Language = "en-US", Gender = Gender.Female },
        new() { VoiceId = "am_michael",    Name = "Michael (AM)",       Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_fenrir",     Name = "Fenrir (AM)",        Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_puck",       Name = "Puck (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_echo",       Name = "Echo (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_eric",       Name = "Eric (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_liam",       Name = "Liam (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_onyx",       Name = "Onyx (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_adam",       Name = "Adam (AM)",          Language = "en-US", Gender = Gender.Male   },
        new() { VoiceId = "am_santa",      Name = "Santa (AM)",         Language = "en-US", Gender = Gender.Male   },
        // ── British English ───────────────────────────────────────────────
        new() { VoiceId = "bf_emma",       Name = "Emma (BF)",          Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bf_isabella",   Name = "Isabella (BF)",      Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bf_alice",      Name = "Alice (BF)",         Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bf_lily",       Name = "Lily (BF)",          Language = "en-GB", Gender = Gender.Female },
        new() { VoiceId = "bm_george",     Name = "George (BM)",        Language = "en-GB", Gender = Gender.Male   },
        new() { VoiceId = "bm_fable",      Name = "Fable (BM)",         Language = "en-GB", Gender = Gender.Male   },
        new() { VoiceId = "bm_daniel",     Name = "Daniel (BM)",        Language = "en-GB", Gender = Gender.Male   },
        new() { VoiceId = "bm_lewis",      Name = "Lewis (BM)",         Language = "en-GB", Gender = Gender.Male   },
        // ── Spanish ───────────────────────────────────────────────────────
        new() { VoiceId = "ef_dora",       Name = "Dora (EF)",          Language = "es",    Gender = Gender.Female },
        new() { VoiceId = "em_alex",       Name = "Alex (EM)",          Language = "es",    Gender = Gender.Male   },
        new() { VoiceId = "em_santa",      Name = "Santa (EM)",         Language = "es",    Gender = Gender.Male   },
        // ── French ────────────────────────────────────────────────────────
        new() { VoiceId = "ff_siwis",      Name = "Siwis (FF)",         Language = "fr",    Gender = Gender.Female },
        // ── Hindi ─────────────────────────────────────────────────────────
        new() { VoiceId = "hf_alpha",      Name = "Alpha (HF)",         Language = "hi",    Gender = Gender.Female },
        new() { VoiceId = "hf_beta",       Name = "Beta (HF)",          Language = "hi",    Gender = Gender.Female },
        new() { VoiceId = "hm_omega",      Name = "Omega (HM)",         Language = "hi",    Gender = Gender.Male   },
        new() { VoiceId = "hm_psi",        Name = "Psi (HM)",           Language = "hi",    Gender = Gender.Male   },
        // ── Italian ───────────────────────────────────────────────────────
        new() { VoiceId = "if_sara",       Name = "Sara (IF)",          Language = "it",    Gender = Gender.Female },
        new() { VoiceId = "im_nicola",     Name = "Nicola (IM)",        Language = "it",    Gender = Gender.Male   },
        // ── Brazilian Portuguese ──────────────────────────────────────────
        new() { VoiceId = "pf_dora",       Name = "Dora (PF)",          Language = "pt-BR", Gender = Gender.Female },
        new() { VoiceId = "pm_alex",       Name = "Alex (PM)",          Language = "pt-BR", Gender = Gender.Male   },
        new() { VoiceId = "pm_santa",      Name = "Santa (PM)",         Language = "pt-BR", Gender = Gender.Male   },
        // ── Japanese ──────────────────────────────────────────────────────
        new() { VoiceId = "jf_alpha",      Name = "Alpha (JF)",         Language = "ja",    Gender = Gender.Female },
        new() { VoiceId = "jf_gongitsune", Name = "Gongitsune (JF)",    Language = "ja",    Gender = Gender.Female },
        new() { VoiceId = "jf_nezumi",     Name = "Nezumi (JF)",        Language = "ja",    Gender = Gender.Female },
        new() { VoiceId = "jf_tebukuro",   Name = "Tebukuro (JF)",      Language = "ja",    Gender = Gender.Female },
        new() { VoiceId = "jm_kumo",       Name = "Kumo (JM)",          Language = "ja",    Gender = Gender.Male   },
        // ── Mandarin Chinese ──────────────────────────────────────────────
        new() { VoiceId = "zf_xiaobei",    Name = "Xiaobei (ZF)",       Language = "zh",    Gender = Gender.Female },
        new() { VoiceId = "zf_xiaoni",     Name = "Xiaoni (ZF)",        Language = "zh",    Gender = Gender.Female },
        new() { VoiceId = "zf_xiaoxiao",   Name = "Xiaoxiao (ZF)",      Language = "zh",    Gender = Gender.Female },
        new() { VoiceId = "zf_xiaoyi",     Name = "Xiaoyi (ZF)",        Language = "zh",    Gender = Gender.Female },
        new() { VoiceId = "zm_yunjian",    Name = "Yunjian (ZM)",       Language = "zh",    Gender = Gender.Male   },
        new() { VoiceId = "zm_yunxi",      Name = "Yunxi (ZM)",         Language = "zh",    Gender = Gender.Male   },
        new() { VoiceId = "zm_yunxia",     Name = "Yunxia (ZM)",        Language = "zh",    Gender = Gender.Male   },
        new() { VoiceId = "zm_yunyang",    Name = "Yunyang (ZM)",       Language = "zh",    Gender = Gender.Male   },
    };

    // Blend spec prefix — voice IDs starting with this are parsed as mixes.
    // Format: "mix:af_heart:0.7|bm_george:0.3"
    public const string MixPrefix     = "mix:";
    public const string DefaultVoiceId = "af_heart";

    // ── State ─────────────────────────────────────────────────────────────────

    private KokoroTTS? _tts;
    private bool       _initialized;
    private bool       _initializing;
    private bool       _disposed;
    private readonly object _initLock = new();

    private readonly Dictionary<VoiceSlot, string>    _voiceAssignments = new();
    private readonly Dictionary<string, KokoroVoice>  _voiceCache       = new();

    // ── ITtsProvider ──────────────────────────────────────────────────────────

    public string ProviderId      => "kokoro";
    public string DisplayName     => "Kokoro (Local ONNX)";
    public bool   IsAvailable     => true;
    public bool   RequiresFullText => true;

    public IReadOnlyList<VoiceInfo> GetAvailableVoices() => KnownVoices;

    public void SetVoice(VoiceSlot slot, string voiceId)
    {
        _voiceAssignments[slot] = voiceId;
        _voiceCache.Remove(voiceId);
    }

    public async Task<string> SynthesizeToFileAsync(
        string text, VoiceSlot slot, string outputPath, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureInitializedAsync(ct);
        ct.ThrowIfCancellationRequested();

        var voice   = GetVoiceForSlot(slot);
        var wavPath = Path.ChangeExtension(outputPath, ".wav");

        // SpeakFast() is void -- it drives its own internal playback and returns no PCM.
        // To capture raw samples we use EnqueueJob with an OnComplete callback per segment.
        var pcm = await Task.Run(() => InferToPcm(text, voice), ct);

        ct.ThrowIfCancellationRequested();
        await File.WriteAllBytesAsync(wavPath, PcmToWav(pcm, 24000), ct);
        return wavPath;
    }

    // Runs synchronous inference on a thread-pool thread.
    // Tokenises -> segments -> enqueues all jobs -> blocks until the last OnComplete fires.
    private float[] InferToPcm(string text, KokoroVoice voice)
    {
        // 1. Text -> tokens
        int[] tokens = Tokenizer.Tokenize(text);

        // 2. Split into <=510-token segments so the model never overruns its context window
        List<int[]> segments = SegmentationSystem.SplitToSegments(
            tokens, new DefaultSegmentationConfig());

        // 3. Collect PCM chunks via per-segment OnComplete callbacks
        // Chunks arrive in completion order; for short NPC dialog ordering is fine.
        // For strict ordering, use a segment index key instead.
        var chunks    = new List<float[]>(segments.Count);
        int remaining = segments.Count;
        using var done = new ManualResetEventSlim(false);

        foreach (var seg in segments)
        {
            var segRef = seg;
            _tts!.EnqueueJob(KokoroJob.Create(segRef, voice, speed: 1f,  OnComplete: chunk =>
            {
                lock (chunks) chunks.Add(chunk);
                if (Interlocked.Decrement(ref remaining) == 0)
                    done.Set();
            }));
        }

        // 4. Block until all segments complete
        done.Wait();

        // 5. Concatenate
        int totalLen = chunks.Sum(c => c.Length);
        var result   = new float[totalLen];
        int offset   = 0;
        foreach (var chunk in chunks) { chunk.CopyTo(result, offset); offset += chunk.Length; }
        return result;
    }

    // ── Model init with download feedback ────────────────────────────────────

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        bool shouldInit;
        lock (_initLock)
        {
            shouldInit = !_initialized && !_initializing;
            if (shouldInit) _initializing = true;
        }

        if (shouldInit)
        {
            OnModelDownloading?.Invoke(
                "Kokoro: loading model — first run downloads ~320 MB, please wait…");

            try
            {
                await Task.Run(() =>
                {
                    var x = new SessionOptions();
                    x.AppendExecutionProvider_CPU();
                    x.EnableCpuMemArena = true;
                    x.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
                    x.ExecutionMode = ExecutionMode.ORT_PARALLEL;
                    
                    
                    _tts = KokoroTTS.LoadModel(sessionOptions: x);
                }, ct);
                lock (_initLock) { _initialized = true; _initializing = false; }
                OnModelReady?.Invoke();
            }
            catch
            {
                lock (_initLock) _initializing = false;
                throw;
            }
        }
        else
        {
            // Another thread is initializing — poll until ready
            while (!_initialized)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(100, ct);
            }
        }
    }

    // ── Voice resolution ──────────────────────────────────────────────────────

    public string ResolveVoiceId(VoiceSlot slot)
        => _voiceAssignments.TryGetValue(slot, out var id) ? id : DefaultVoiceId;

    private KokoroVoice GetVoiceForSlot(VoiceSlot slot)
    {
        var id = _voiceAssignments.TryGetValue(slot, out var v) ? v : DefaultVoiceId;

        if (!_voiceCache.TryGetValue(id, out var voice))
        {
            voice = id.StartsWith(MixPrefix)
                ? ResolveMix(id[MixPrefix.Length..])
                : KokoroVoiceManager.GetVoice(id);
            _voiceCache[id] = voice;
        }

        return voice;
    }

    /// <summary>
    /// Parses a blend spec and returns a mixed KokoroVoice.
    /// Format: "af_heart:0.7|bm_george:0.3"
    /// Weights do not need to sum to 1 — KokoroSharp normalises internally.
    /// </summary>
    public static KokoroVoice ResolveMix(string spec)
    {
        var parts      = spec.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var components = new List<(KokoroVoice, float)>();

        foreach (var part in parts)
        {
            var colon = part.LastIndexOf(':');
            string voiceId;
            float  weight;

            if (colon < 0)
            {
                voiceId = part;
                weight  = 1f;
            }
            else
            {
                voiceId = part[..colon];
                weight  = float.TryParse(
                    part[(colon + 1)..],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var w) ? w : 1f;
            }

            components.Add((KokoroVoiceManager.GetVoice(voiceId), weight));
        }

        if (components.Count == 1) return components[0].Item1;

        return KokoroVoiceManager.Mix(components.ToArray());
    }

    // ── PCM float[] → 16-bit mono WAV ────────────────────────────────────────

    private static byte[] PcmToWav(float[] pcm, int sampleRate)
    {
        int byteCount = pcm.Length * 2;
        using var ms     = new MemoryStream(44 + byteCount);
        using var writer = new BinaryWriter(ms);

        writer.Write("RIFF"u8);  writer.Write(36 + byteCount);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);  writer.Write(16);
        writer.Write((short)1);  writer.Write((short)1);   // PCM, mono
        writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2);  writer.Write((short)16);  // block align, bits
        writer.Write("data"u8);  writer.Write(byteCount);

        foreach (var s in pcm)
            writer.Write((short)Math.Clamp(s * 32767f, short.MinValue, short.MaxValue));

        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tts?.Dispose();
    }
}