// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

// TTS/Dsp/DspFilterChain.cs
//
// Post-synthesis DSP pipeline. Called by PlaybackCoordinator after synthesis,
// before StoreAsync. Receives PcmAudio, returns new PcmAudio. Input is not mutated.
//
// NWaves usage — verified against source (NWaves-master):
//
//   Effects (all inherit AudioEffect : WetDryMixer, IOnlineFilter)
//     PitchShiftEffect(double shift, int windowSize=1024, int hopSize=128, TsmAlgorithm tsm=...)
//       — shift is a RATIO (not semitones). 1.0=no change, 2.0=octave up.
//       — Use TsmAlgorithm.Wsola for voice: time-domain, no hollow phase artifacts.
//       — PhaseVocoderPhaseLocking (default) sounds hollow on speech — avoid.
//     ChorusEffect(int sr, float[] lfoFrequencies, float[] widths)
//       — widths are max delays in SECONDS. Internally: array of VibratoEffect.
//       — Wet/Dry on the instance control output mix.
//     VibratoEffect(int sr, float lfoFrequency=1, float width=0.003f)
//       — width in SECONDS. Set Wet=1, Dry=0 for pure pitch wobble.
//     PhaserEffect(int sr, float lfoFrequency=1, float minFrequency=300, float maxFrequency=3000, float q=0.5f)
//     FlangerEffect(int sr, float lfoFrequency=1, float width=0.003f, float depth=0.5f, float feedback=0)
//     AutowahEffect(int sr, float minFrequency=30, float maxFrequency=2000, float q=0.5f)
//     EchoEffect(int sr, float delay, float feedback=0.5f)
//     TremoloEffect(int sr, float depth=0.5f, float frequency=10, float tremoloIndex=0.5f)
//     DistortionEffect(DistortionMode mode, float inputGain=12dB, float outputGain=-12dB)
//     TubeDistortionEffect(float inputGain=20dB, float outputGain=-12dB, float q=-0.2f, float dist=5)
//     BitCrusherEffect(int bitDepth)
//     RobotEffect(int hopSize, int fftSize=0)
//     WhisperEffect(int hopSize, int fftSize=0)
//
//   Filters (BiQuad — normalized frequency = Hz / sampleRate, range [0..0.5])
//     HighPassFilter(double frequency, double q=1)
//     LowShelfFilter(double frequency, double q=1, double gain=1.0)   — gain in dB
//     PeakFilter(double frequency, double q=1, double gain=1.0)       — gain in dB
//     HighShelfFilter(double frequency, double q=1, double gain=1.0)  — gain in dB
//
//   Operations
//     Operation.TimeStretch(DiscreteSignal, double stretch, TsmAlgorithm)
//       — stretch > 1 = longer/slower, stretch < 1 = shorter/faster
//       — Wsola auto-tunes parameters to signal.SamplingRate
//     DynamicsProcessor(DynamicsMode.Compressor, int sr, float threshold, float ratio, ...)
//
//   Hand-rolled (not in NWaves):
//     Reverb   — Freeverb (Jezar, public domain): 8 Schroeder combs + 4 allpass
//     Exciter  — high-pass copy → soft-clip → mix back in
//
// Thread safety: DspFilterChain is static. NWaves effects are instantiated per-call.
// All effects that hold state (delay lines, LFO phase) are created fresh each call —
// this is correct because we process entire offline PcmAudio buffers, not streams.

using System;
using NWaves.Effects;
using NWaves.Filters.Base;
using NWaves.Filters.BiQuad;
using NWaves.Operations;
using NWaves.Operations.Tsm;
using NWaves.Signals;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.TTS.Dsp;

public static class DspFilterChain
{
    /// <summary>
    /// Applies the DSP chain described in <paramref name="profile"/> to <paramref name="input"/>.
    /// Returns a new PcmAudio. Input is not mutated.
    /// Returns <paramref name="input"/> unchanged if profile is null, disabled, or neutral.
    /// </summary>
    public static PcmAudio Apply(PcmAudio input, DspProfile? profile)
    {
        if (profile is null || !profile.Enabled || profile.IsNeutral)
            return input;

        var signal = new DiscreteSignal(input.SampleRate, (float[])input.Samples.Clone());

        // ── 1: Compressor ─────────────────────────────────────────────────────
        // Tame uneven TTS dynamics before any other processing.
        if (profile.CompressorThresholdDb < 0f)
        {
            var comp = new DynamicsProcessor(
                DynamicsMode.Compressor,
                signal.SamplingRate,
                threshold:   profile.CompressorThresholdDb,
                ratio:       profile.CompressorRatio,
                makeupGain:  0f,
                attack:      0.005f,
                release:     0.08f);
            signal = comp.ApplyTo(signal);
        }

        // ── 2: Pitch shift ────────────────────────────────────────────────────
        // PitchShiftEffect with TsmAlgorithm.Wsola: time-domain overlap-add.
        // Avoids the hollow/phasy artifacts of the phase vocoder — critical for
        // natural-sounding voice. Shift ratio = 2^(semitones/12).
        // Window 1024 / hop 256 is a good balance of quality vs. speed for speech.
        if (profile.PitchSemitones != 0f)
        {
            double ratio  = Math.Pow(2.0, profile.PitchSemitones / 12.0);
            var    effect = new PitchShiftEffect(ratio,
                                                 windowSize: 1024,
                                                 hopSize:    256,
                                                 tsm:        TsmAlgorithm.Wsola);
            signal = effect.ApplyTo(signal);
        }

        // ── 3: Tempo stretch (WSOLA) ──────────────────────────────────────────
        // TempoPercent: positive = faster (shorter). stretch = 1 / (1 + pct/100).
        // WSOLA is pitch-preserving and auto-adapts parameters to sampling rate.
        if (profile.TempoPercent != 0f)
        {
            double stretch = 1.0 / (1.0 + profile.TempoPercent / 100.0);
            stretch        = Math.Clamp(stretch, 0.4, 2.5);
            signal         = Operation.TimeStretch(signal, stretch, TsmAlgorithm.Wsola);
        }

        // ── 4–7: EQ (BiQuad) ─────────────────────────────────────────────────
        // Normalized frequency = Hz / SamplingRate (range [0..0.5])

        if (profile.HighPassHz > 0f)
        {
            var f = new HighPassFilter(profile.HighPassHz / signal.SamplingRate);
            signal = f.ApplyTo(signal);
        }

        if (profile.LowShelfDb != 0f)
        {
            var f = new LowShelfFilter(200.0 / signal.SamplingRate, q: 1.0, gain: profile.LowShelfDb);
            signal = f.ApplyTo(signal);
        }

        if (profile.MidGainDb != 0f)
        {
            var f = new PeakFilter(
                profile.MidFrequencyHz / signal.SamplingRate,
                q:    1.0,
                gain: profile.MidGainDb);
            signal = f.ApplyTo(signal);
        }

        if (profile.HighShelfDb != 0f)
        {
            var f = new HighShelfFilter(5000.0 / signal.SamplingRate, q: 1.0, gain: profile.HighShelfDb);
            signal = f.ApplyTo(signal);
        }

        // ── 8: Harmonic exciter ───────────────────────────────────────────────
        // High-pass the signal (extract highs), soft-clip to generate harmonics,
        // mix a fraction back into the main signal.
        if (profile.ExciterAmount > 0f)
        {
            var hpf       = new HighPassFilter(3000.0 / signal.SamplingRate);
            var highs     = hpf.ApplyTo(signal).Samples;
            var src       = signal.Samples;
            var result    = new float[src.Length];
            float mix     = profile.ExciterAmount * 0.3f;
            for (int i = 0; i < src.Length && i < highs.Length; i++)
                result[i] = src[i] + mix * SoftClip(highs[i] * 3f);
            signal = new DiscreteSignal(signal.SamplingRate, result);
        }

        // ── 9: Distortion ─────────────────────────────────────────────────────
        // TubeDistortion takes precedence over DistortionMode.
        if (profile.TubeDistortion)
        {
            var fx = new TubeDistortionEffect(
                inputGain:  profile.DistortionInputGainDb,
                outputGain: profile.DistortionOutputGainDb,
                q:          profile.TubeDistortionQ,
                dist:       profile.TubeDistortionDist);
            fx.Wet = 1f; fx.Dry = 0f;
            signal = fx.ApplyTo(signal);
        }
        else if (profile.DistortionMode.HasValue)
        {
            var fx = new DistortionEffect(
                profile.DistortionMode.Value,
                inputGain:  profile.DistortionInputGainDb,
                outputGain: profile.DistortionOutputGainDb);
            fx.Wet = 1f; fx.Dry = 0f;
            signal = fx.ApplyTo(signal);
        }

        // ── 10: BitCrusher ────────────────────────────────────────────────────
        if (profile.BitCrushDepth > 0)
        {
            var fx = new BitCrusherEffect(profile.BitCrushDepth);
            fx.Wet = 1f; fx.Dry = 0f;
            signal = fx.ApplyTo(signal);
        }

        // ── 11: Chorus ────────────────────────────────────────────────────────
        // Two voices with slightly offset LFO rates for natural detuning.
        // widths are max delays in seconds.
        if (profile.ChorusWet > 0f)
        {
            float w     = profile.ChorusWidth;
            float r     = profile.ChorusRateHz;
            var   lfoHz = new float[] { r, r * 1.15f };         // slight rate spread between voices
            var   widths = new float[] { w, w * 0.85f };        // slight width spread
            var   fx    = new ChorusEffect(signal.SamplingRate, lfoHz, widths);
            fx.Wet = profile.ChorusWet;
            fx.Dry = 1f - profile.ChorusWet;
            signal = fx.ApplyTo(signal);
        }

        // ── 12: Vibrato ───────────────────────────────────────────────────────
        // Pure pitch wobble — Wet=1, Dry=0 for ghost/unsettling quality.
        if (profile.VibratoWidth > 0f)
        {
            var fx = new VibratoEffect(
                signal.SamplingRate,
                lfoFrequency: profile.VibratoRateHz,
                width:        profile.VibratoWidth);
            fx.Wet = 1f; fx.Dry = 0f;
            signal = fx.ApplyTo(signal);
        }

        // ── 13: Phaser ────────────────────────────────────────────────────────
        if (profile.PhaserWet > 0f)
        {
            var fx = new PhaserEffect(
                signal.SamplingRate,
                lfoFrequency: profile.PhaserRateHz,
                minFrequency: profile.PhaserMinHz,
                maxFrequency: profile.PhaserMaxHz,
                q:            0.5f);
            fx.Wet = profile.PhaserWet;
            fx.Dry = 1f - profile.PhaserWet;
            signal = fx.ApplyTo(signal);
        }

        // ── 14: Flanger ───────────────────────────────────────────────────────
        if (profile.FlangerWet > 0f)
        {
            var fx = new FlangerEffect(
                signal.SamplingRate,
                lfoFrequency: profile.FlangerRateHz,
                width:        0.003f,
                depth:        profile.FlangerWet,
                feedback:     profile.FlangerFeedback);
            fx.Wet = profile.FlangerWet;
            fx.Dry = 1f - profile.FlangerWet;
            signal = fx.ApplyTo(signal);
        }

        // ── 15: AutoWah ───────────────────────────────────────────────────────
        // Envelope-following wah — reacts to voice dynamics, no fixed LFO.
        if (profile.AutoWahWet > 0f)
        {
            var fx = new AutowahEffect(
                signal.SamplingRate,
                minFrequency: profile.AutoWahMinHz,
                maxFrequency: profile.AutoWahMaxHz,
                q:            profile.AutoWahQ,
                attackTime:   0.005f,
                releaseTime:  0.05f);
            fx.Wet = profile.AutoWahWet;
            fx.Dry = 1f - profile.AutoWahWet;
            signal = fx.ApplyTo(signal);
        }

        // ── 16: Echo ──────────────────────────────────────────────────────────
        if (profile.EchoDelaySeconds > 0f)
        {
            var fx = new EchoEffect(
                signal.SamplingRate,
                delay:    profile.EchoDelaySeconds,
                feedback: profile.EchoFeedback);
            fx.Wet = profile.EchoWet;
            fx.Dry = 1f - profile.EchoWet;
            signal = fx.ApplyTo(signal);
        }

        // ── 17: Reverb (Freeverb — hand-rolled, not in NWaves) ────────────────
        if (profile.ReverbWet > 0f)
        {
            var reverbed = Freeverb(
                signal.Samples,
                signal.SamplingRate,
                profile.ReverbRoomSize,
                profile.ReverbDamping,
                profile.ReverbWet);
            signal = new DiscreteSignal(signal.SamplingRate, reverbed);
        }

        // ── 18: Tremolo ───────────────────────────────────────────────────────
        // TremoloIndex of 0.5 with depth of 1.0 gives full amplitude swing.
        if (profile.TremoloDepth > 0f)
        {
            var fx = new TremoloEffect(
                signal.SamplingRate,
                depth:         profile.TremoloDepth,
                frequency:     profile.TremoloRateHz,
                tremoloIndex:  profile.TremoloDepth * 0.5f);
            fx.Wet = 1f; fx.Dry = 0f;
            signal = fx.ApplyTo(signal);
        }

        // ── 19: Robot ─────────────────────────────────────────────────────────
        // fftSize must be a power of 2 and > hopSize.
        // Derive as next power of 2 above hopSize*2 (min 256); pass explicitly
        // so the 8*hopSize default doesn't bite non-power-of-2 hop values.
        if (profile.RobotHopSize > 0)
        {
            int hop = Math.Max(1, profile.RobotHopSize);
            int fft = NextPow2(Math.Max(hop * 2, 256));
            var fx  = new RobotEffect(hop, fft);
            signal  = fx.ApplyTo(signal);
        }

        // ── 20: Whisper ───────────────────────────────────────────────────────
        // Same fftSize derivation as Robot.
        if (profile.WhisperHopSize > 0)
        {
            int hop = Math.Max(1, profile.WhisperHopSize);
            int fft = NextPow2(Math.Max(hop * 2, 256));
            var fx  = new WhisperEffect(hop, fft);
            signal  = fx.ApplyTo(signal);
        }

        // ── 21: Peak normalize ────────────────────────────────────────────────
        // NWaves NormalizeMax divides by peak absolute value (only scales down if > 1).
        // We only normalize if peak > 1 to avoid boosting already-quiet signals.
        {
            float peak = 0f;
            foreach (var s in signal.Samples) { var a = MathF.Abs(s); if (a > peak) peak = a; }
            if (peak > 1.0f) signal.Amplify(1.0f / peak);
        }

        return new PcmAudio(signal.Samples, signal.SamplingRate);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns the smallest power of 2 that is >= n.</summary>
    private static int NextPow2(int n)
    {
        if (n <= 1) return 1;
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>
    /// Cubic soft-clip from DAFX book (Zoelzer p.118, SoftClipping mode).
    /// Used only for the harmonic exciter's distorted high-pass copy.
    /// </summary>
    private static float SoftClip(float x)
    {
        const float lower = 1f / 3f;
        const float upper = 2f / 3f;
        if      (x >  upper) return  1f;
        else if (x >  lower) return  1f - (2f - 3f * x) * (2f - 3f * x) / 3f;
        else if (x < -upper) return -1f;
        else if (x < -lower) return -1f + (2f + 3f * x) * (2f + 3f * x) / 3f;
        else                 return  2f * x;
    }

    // ── Freeverb ──────────────────────────────────────────────────────────────
    // Jezar at Dreampoint's Freeverb design (public domain).
    // 8 parallel Schroeder comb filters with damping, followed by 4 series allpass filters.
    // Delay lengths tuned for 44.1 kHz and scaled to actual sample rate.

    private static float[] Freeverb(
        float[] input, int sr, float roomSize, float damping, float wet)
    {
        int[] combBase = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        int[] apBase   = { 556, 441, 341, 225 };

        float scale    = sr / 44100f;
        float feedback = Math.Clamp(roomSize * 0.28f + 0.7f, 0.70f, 0.98f);
        float dry      = 1f - wet;
        const float reverbGain = 0.015f;   // scale reverb output to sane level

        // Allocate comb filter state
        int nc = combBase.Length;
        var combBuf  = new float[nc][];
        var combPtr  = new int[nc];
        var combFilt = new float[nc];
        for (int c = 0; c < nc; c++)
            combBuf[c] = new float[Math.Max(1, (int)(combBase[c] * scale))];

        // Allocate allpass filter state
        int na = apBase.Length;
        var apBuf = new float[na][];
        var apPtr = new int[na];
        for (int a = 0; a < na; a++)
            apBuf[a] = new float[Math.Max(1, (int)(apBase[a] * scale))];

        var output = new float[input.Length];

        for (int n = 0; n < input.Length; n++)
        {
            float x   = input[n];
            float rev = 0f;

            // 8 parallel comb filters (Schroeder) with damping low-pass
            for (int c = 0; c < nc; c++)
            {
                int   len = combBuf[c].Length;
                int   ptr = combPtr[c];
                float buf = combBuf[c][ptr];
                // One-pole LP on the feedback path (damping)
                combFilt[c]      = buf * (1f - damping) + combFilt[c] * damping;
                combBuf[c][ptr]  = x + combFilt[c] * feedback;
                combPtr[c]       = (ptr + 1) % len;
                rev             += buf;
            }

            // 4 series allpass filters
            for (int a = 0; a < na; a++)
            {
                int   len = apBuf[a].Length;
                int   ptr = apPtr[a];
                float buf = apBuf[a][ptr];
                apBuf[a][ptr] = rev + buf * 0.5f;
                apPtr[a]      = (ptr + 1) % len;
                rev           = buf - rev;
            }

            output[n] = x * dry + rev * wet * reverbGain;
        }

        return output;
    }
}
