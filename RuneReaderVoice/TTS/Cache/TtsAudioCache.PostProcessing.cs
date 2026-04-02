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
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OggVorbisEncoder;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.TTS.Cache;

public sealed partial class TtsAudioCache
{
    // ── Post-processing ───────────────────────────────────────────────────────

    /// <summary>
    /// Trims leading and trailing silence from PCM.
    /// TODO: implement proper sample-based trimming.
    /// Currently a pass-through to preserve existing behavior.
    /// </summary>
    private static PcmAudio TrimSilence(PcmAudio audio)
    {
        return audio;
    }

    /// <summary>
    /// Transcodes interleaved float PCM to OGG/Vorbis using OggVorbisEncoder.
    /// Pure managed, cross-platform, no native dependencies.
    /// </summary>
    private async Task TranscodeToOggAsync(PcmAudio audio, string oggPath, CancellationToken ct)
    {
        var pcmChannels = Deinterleave(audio);
        int sampleRate = audio.SampleRate;

        ct.ThrowIfCancellationRequested();

        // Encode on thread-pool — CPU-bound
        await Task.Run(() =>
        {
            float quality = Math.Clamp(_oggQuality / 10f, 0f, 1f);
            int channels = pcmChannels.Length;

            var info = VorbisInfo.InitVariableBitRate(channels, sampleRate, quality);
            var comments = new OggVorbisEncoder.Comments();
            comments.AddTag("ENCODER", "RuneReaderVoice");

            var serial = new Random().Next();
            var oggStream = new OggStream(serial);

            // Write the three Vorbis header packets
            oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
            oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
            oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

            using var outFile = File.Create(oggPath);
            var processingState = ProcessingState.Create(info);

            // Flush headers to file
            while (oggStream.PageOut(out OggPage page, true))
            {
                outFile.Write(page.Header, 0, page.Header.Length);
                outFile.Write(page.Body, 0, page.Body.Length);
            }

            // Feed PCM in chunks of 1024 samples
            const int chunkSize = 1024;
            int totalSamples = pcmChannels[0].Length;
            int offset = 0;

            while (offset < totalSamples)
            {
                int count = Math.Min(chunkSize, totalSamples - offset);

                // Build a float[][] slice for this chunk
                var chunk = new float[channels][];
                for (int c = 0; c < channels; c++)
                {
                    chunk[c] = new float[count];
                    Array.Copy(pcmChannels[c], offset, chunk[c], 0, count);
                }

                processingState.WriteData(chunk, count);
                offset += count;

                while (processingState.PacketOut(out OggPacket packet))
                {
                    oggStream.PacketIn(packet);
                    while (oggStream.PageOut(out OggPage page, false))
                    {
                        outFile.Write(page.Header, 0, page.Header.Length);
                        outFile.Write(page.Body, 0, page.Body.Length);
                    }
                }
            }

            // Signal end of stream and flush remaining pages
            processingState.WriteEndOfStream();
            while (processingState.PacketOut(out OggPacket packet))
            {
                oggStream.PacketIn(packet);
                while (oggStream.PageOut(out OggPage page, true))
                {
                    outFile.Write(page.Header, 0, page.Header.Length);
                    outFile.Write(page.Body, 0, page.Body.Length);
                }
            }
        }, ct);
    }

    private static float[][] Deinterleave(PcmAudio audio)
    {
        int channels = Math.Max(1, audio.Channels);
        if (audio.Samples.Length == 0)
        {
            var empty = new float[channels][];
            for (int c = 0; c < channels; c++) empty[c] = Array.Empty<float>();
            return empty;
        }

        int sampleCount = audio.Samples.Length / channels;
        var channelArrays = new float[channels][];
        for (int c = 0; c < channels; c++)
            channelArrays[c] = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            for (int c = 0; c < channels; c++)
                channelArrays[c][i] = audio.Samples[(i * channels) + c];
        }

        return channelArrays;
    }

    private static async Task WritePcmAsWavAsync(PcmAudio audio, string wavPath, CancellationToken ct)
    {
        int channels = Math.Max(1, audio.Channels);
        int bitsPerSample = 16;
        int blockAlign = channels * (bitsPerSample / 8);
        int byteRate = audio.SampleRate * blockAlign;
        int dataLength = audio.Samples.Length * 2;

        await using var fs = File.Create(wavPath);
        using var writer = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(audio.SampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        var pcm16 = new byte[dataLength];
        for (int i = 0; i < audio.Samples.Length; i++)
        {
            short sample = FloatToPcm16(audio.Samples[i]);
            pcm16[i * 2] = (byte)(sample & 0xFF);
            pcm16[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        await fs.WriteAsync(pcm16, ct);
        await fs.FlushAsync(ct);
    }

    private static short FloatToPcm16(float sample)
    {
        sample = Math.Clamp(sample, -1f, 1f);
        return (short)Math.Round(sample * short.MaxValue);
    }
}