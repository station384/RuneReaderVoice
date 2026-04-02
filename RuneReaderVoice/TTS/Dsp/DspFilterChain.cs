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
using NWaves.Effects;
using NWaves.Filters.Base;
using NWaves.Filters.BiQuad;
using NWaves.Operations;
using NWaves.Operations.Tsm;
using NWaves.Signals;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.TTS.Dsp;
// TTS/Dsp/DspFilterChain.cs
//
// Post-synthesis DSP pipeline. Iterates DspProfile.Effects in user-defined order.
// Each DspEffectItem is applied only when Enabled=true.
// All NWaves effects are instantiated fresh per call (correct for offline buffers).
public static class DspFilterChain
{
    public static PcmAudio Apply(PcmAudio input, DspProfile? profile)
    {
        if (profile is null || !profile.Enabled || profile.IsNeutral)
            return input;

        var signal = new DiscreteSignal(input.SampleRate, (float[])input.Samples.Clone());

        foreach (var effect in profile.Effects)
        {
            if (!effect.Enabled) continue;
            signal = ApplyEffect(signal, effect);
        }

        return new PcmAudio(signal.Samples, signal.SamplingRate);
    }

    private static DiscreteSignal ApplyEffect(DiscreteSignal signal, DspEffectItem item)
    {
        switch (item.Kind)
        {
            // -- Dynamics --------------------------------------------------

            case DspEffectKind.EvenOut:
            {
                float threshold = item.Get("threshold", -18f);
                float ratio     = item.Get("ratio",      4f);
                if (threshold < 0f)
                {
                    var comp = new DynamicsProcessor(
                        DynamicsMode.Compressor,
                        signal.SamplingRate,
                        threshold:  threshold,
                        ratio:      ratio,
                        makeupGain: 0f,
                        attack:     0.005f,
                        release:    0.08f);
                    signal = comp.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Level:
            {
                float peak = 0f;
                foreach (var s in signal.Samples) { var a = MathF.Abs(s); if (a > peak) peak = a; }
                if (peak > 1.0f) signal.Amplify(1.0f / peak);
                break;
            }

            // -- Pitch / Time ----------------------------------------------

            case DspEffectKind.Pitch:
            {
                float semitones = item.Get("semitones", 0f);
                if (semitones != 0f)
                {
                    double ratio  = Math.Pow(2.0, semitones / 12.0);
                    var    effect = new PitchShiftEffect(ratio, windowSize: 1024, hopSize: 256,
                                                         tsm: TsmAlgorithm.Wsola);
                    signal = effect.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Speed:
            {
                float percent = item.Get("percent", 0f);
                if (percent != 0f)
                {
                    double stretch = 1.0 / (1.0 + percent / 100.0);
                    stretch = Math.Clamp(stretch, 0.4, 2.5);
                    signal  = Operation.TimeStretch(signal, stretch, TsmAlgorithm.Wsola);
                }
                break;
            }

            // -- Tone (EQ) -------------------------------------------------

            case DspEffectKind.RumbleRemover:
            {
                float hz = item.Get("hz", 100f);
                if (hz > 0f)
                {
                    var f = new HighPassFilter(hz / signal.SamplingRate);
                    signal = f.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Bass:
            {
                float db = item.Get("db", 0f);
                if (db != 0f)
                {
                    var f = new LowShelfFilter(200.0 / signal.SamplingRate, q: 1.0, gain: db);
                    signal = f.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Presence:
            {
                float db = item.Get("db",  0f);
                float hz = item.Get("hz",  1000f);
                if (db != 0f)
                {
                    var f = new PeakFilter(hz / signal.SamplingRate, q: 1.0, gain: db);
                    signal = f.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Brightness:
            {
                float db = item.Get("db", 0f);
                if (db != 0f)
                {
                    var f = new HighShelfFilter(5000.0 / signal.SamplingRate, q: 1.0, gain: db);
                    signal = f.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Air:
            {
                float amount = item.Get("amount", 0f);
                if (amount > 0f)
                {
                    var hpf    = new HighPassFilter(3000.0 / signal.SamplingRate);
                    var highs  = hpf.ApplyTo(signal).Samples;
                    var src    = signal.Samples;
                    var result = new float[src.Length];
                    float mix  = amount * 0.3f;
                    for (int i = 0; i < src.Length && i < highs.Length; i++)
                        result[i] = src[i] + mix * SoftClip(highs[i] * 3f);
                    signal = new DiscreteSignal(signal.SamplingRate, result);
                }
                break;
            }

            // -- Distortion ------------------------------------------------

            case DspEffectKind.Grit:
            {
                int   mode  = (int)item.Get("mode",  1f);
                float inDb  = item.Get("inDb",  12f);
                float outDb = item.Get("outDb", -12f);
                var distMode = mode switch
                {
                    1 => (DistortionMode?)DistortionMode.SoftClipping,
                    2 => DistortionMode.HardClipping,
                    3 => DistortionMode.Exponential,
                    4 => DistortionMode.FullWaveRectify,
                    5 => DistortionMode.HalfWaveRectify,
                    _ => (DistortionMode?)null,
                };
                if (distMode.HasValue)
                {
                    var fx = new DistortionEffect(distMode.Value, inputGain: inDb, outputGain: outDb);
                    fx.Wet = 1f; fx.Dry = 0f;
                    signal = fx.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.WarmthGrit:
            {
                float drive = item.Get("drive", 5f);
                float q     = item.Get("q",    -0.2f);
                var fx = new TubeDistortionEffect(inputGain: 20f, outputGain: -12f, q: q, dist: drive);
                fx.Wet = 1f; fx.Dry = 0f;
                signal = fx.ApplyTo(signal);
                break;
            }

            case DspEffectKind.LoFi:
            {
                int bits = (int)item.Get("bits", 8f);
                if (bits > 0)
                {
                    var fx = new BitCrusherEffect(bits);
                    fx.Wet = 1f; fx.Dry = 0f;
                    signal = fx.ApplyTo(signal);
                }
                break;
            }

            // -- Modulation ------------------------------------------------

            case DspEffectKind.Thickness:
            {
                float wet   = item.Get("wet",   0.5f);
                float rate  = item.Get("rate",  1.5f);
                float width = item.Get("width", 0.02f);
                if (wet > 0f)
                {
                    var lfoHz  = new float[] { rate, rate * 1.15f };
                    var widths = new float[] { width, width * 0.85f };
                    var fx     = new ChorusEffect(signal.SamplingRate, lfoHz, widths);
                    fx.Wet = wet; fx.Dry = 1f - wet;
                    signal = fx.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Wobble:
            {
                float width = item.Get("width", 0.005f);
                float rate  = item.Get("rate",  3f);
                if (width > 0f)
                {
                    var fx = new VibratoEffect(signal.SamplingRate, lfoFrequency: rate, width: width);
                    fx.Wet = 1f; fx.Dry = 0f;
                    signal = fx.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Swirl:
            {
                float wet   = item.Get("wet",   0.5f);
                float rate  = item.Get("rate",  1f);
                float minHz = item.Get("minHz", 300f);
                float maxHz = item.Get("maxHz", 3000f);
                if (wet > 0f)
                {
                    var fx = new PhaserEffect(signal.SamplingRate,
                        lfoFrequency: rate, minFrequency: minHz, maxFrequency: maxHz, q: 0.5f);
                    fx.Wet = wet; fx.Dry = 1f - wet;
                    signal = fx.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Jet:
            {
                float wet      = item.Get("wet",      0.5f);
                float rate     = item.Get("rate",     1f);
                float feedback = item.Get("feedback", 0.5f);
                if (wet > 0f)
                {
                    var fx = new FlangerEffect(signal.SamplingRate,
                        lfoFrequency: rate, width: 0.003f, depth: wet, feedback: feedback);
                    fx.Wet = wet; fx.Dry = 1f - wet;
                    signal = fx.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Wah:
            {
                float wet   = item.Get("wet",   0.5f);
                float minHz = item.Get("minHz", 300f);
                float maxHz = item.Get("maxHz", 3000f);
                if (wet > 0f)
                {
                    var fx = new AutowahEffect(signal.SamplingRate,
                        minFrequency: minHz, maxFrequency: maxHz, q: 0.5f,
                        attackTime: 0.005f, releaseTime: 0.05f);
                    fx.Wet = wet; fx.Dry = 1f - wet;
                    signal = fx.ApplyTo(signal);
                }
                break;
            }

            case DspEffectKind.Tremor:
            {
                float depth = item.Get("depth", 0.5f);
                float rate  = item.Get("rate",  4f);
                if (depth > 0f)
                {
                    var fx = new TremoloEffect(signal.SamplingRate,
                        depth: depth, frequency: rate, tremoloIndex: depth * 0.5f);
                    fx.Wet = 1f; fx.Dry = 0f;
                    signal = fx.ApplyTo(signal);
                }
                break;
            }

            // -- Space -----------------------------------------------------

            case DspEffectKind.Room:
            {
                float wet      = item.Get("wet",      0.3f);
                float roomSize = item.Get("roomSize", 0.5f);
                float damping  = item.Get("damping",  0.5f);
                if (wet > 0f)
                {
                    var reverbed = Freeverb(signal.Samples, signal.SamplingRate,
                                            roomSize, damping, wet);
                    signal = new DiscreteSignal(signal.SamplingRate, reverbed);
                }
                break;
            }

            case DspEffectKind.Echo:
            {
                float delay    = item.Get("delay",    0.3f);
                float feedback = item.Get("feedback", 0.4f);
                float wet      = item.Get("wet",      0.5f);
                if (delay > 0f && wet > 0f)
                {
                    var fx = new EchoEffect(signal.SamplingRate, delay: delay, feedback: feedback);
                    fx.Wet = wet; fx.Dry = 1f - wet;
                    signal = fx.ApplyTo(signal);
                }
                break;
            }

            // -- Character -------------------------------------------------

            case DspEffectKind.Robot:
            {
                int strength = (int)item.Get("strength", 2f);
                int hop = strength switch { 0 => 64, 1 => 128, 3 => 512, _ => 256 };
                int fft = NextPow2(Math.Max(hop * 2, 256));
                var fx  = new RobotEffect(hop, fft);
                signal  = fx.ApplyTo(signal);
                break;
            }

            case DspEffectKind.Whisper:
            {
                int strength = (int)item.Get("strength", 1f);
                int hop = strength switch { 0 => 64, 1 => 128, 3 => 512, _ => 256 };
                int fft = NextPow2(Math.Max(hop * 2, 256));
                var fx  = new WhisperEffect(hop, fft);
                signal  = fx.ApplyTo(signal);
                break;
            }
        }

        return signal;
    }

    // -- Helpers -----------------------------------------------------------

    private static int NextPow2(int n)
    {
        if (n <= 1) return 1;
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

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

    private static float[] Freeverb(float[] input, int sr, float roomSize, float damping, float wet)
    {
        int[] combBase = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
        int[] apBase   = { 556, 441, 341, 225 };

        float scale    = sr / 44100f;
        float feedback = Math.Clamp(roomSize * 0.28f + 0.7f, 0.70f, 0.98f);
        float dry      = 1f - wet;
        const float reverbGain = 0.015f;

        int nc = combBase.Length;
        var combBuf  = new float[nc][];
        var combPtr  = new int[nc];
        var combFilt = new float[nc];
        for (int c = 0; c < nc; c++)
            combBuf[c] = new float[Math.Max(1, (int)(combBase[c] * scale))];

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

            for (int c = 0; c < nc; c++)
            {
                int   len = combBuf[c].Length;
                int   ptr = combPtr[c];
                float buf = combBuf[c][ptr];
                combFilt[c]      = buf * (1f - damping) + combFilt[c] * damping;
                combBuf[c][ptr]  = x + combFilt[c] * feedback;
                combPtr[c]       = (ptr + 1) % len;
                rev             += buf;
            }

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