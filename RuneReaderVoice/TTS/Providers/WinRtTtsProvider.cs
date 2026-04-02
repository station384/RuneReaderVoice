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

#if WINDOWS
// WinRtTtsProvider.cs
// TTS synthesis via Windows.Media.SpeechSynthesis (WinRT).
// Uses the modern Cortana/Narrator voices on Windows 10/11.
// NOT the legacy SAPI5 COM interface — that only exposes old voices.
//
// WinRT interop requires the project to target net8.0-windows or use
// the CsWinRT interop layer. Add this to the csproj if needed:
//   <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;
using Windows.Media.SpeechSynthesis;

namespace RuneReaderVoice.TTS.Providers;

[SupportedOSPlatform("windows10.0.10240.0")]
public sealed class WinRtTtsProvider : ITtsProvider
{
    private readonly SpeechSynthesizer _synth = new();
    private bool _disposed;

    private readonly Dictionary<VoiceSlot, VoiceInformation> _voiceAssignments = new();
    private readonly Dictionary<VoiceSlot, string> _voiceIds = new();

    public string ProviderId   => "winrt";
    public string DisplayName  => "Windows Speech (WinRT)";
    public bool IsAvailable    => true;
    public bool SupportsInlinePronunciationHints => false;
    public bool RequiresFullText => false;

    public string ResolveVoiceId(VoiceSlot slot)
        => _voiceIds.TryGetValue(slot, out var id) ? id : string.Empty;

    public VoiceProfile? ResolveProfile(VoiceSlot slot)
    {
        return null;
    }

    public IReadOnlyList<VoiceInfo> GetAvailableVoices()
    {
        return SpeechSynthesizer.AllVoices
            .Select(v => new VoiceInfo
            {
                VoiceId  = v.Id,
                Name     = v.DisplayName,
                Language = v.Language,
                Gender   = v.Gender == VoiceGender.Female ? Gender.Female : Gender.Male,
            })
            .ToList();
    }

    public void SetVoice(VoiceSlot slot, string voiceId)
    {
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voiceId);
        if (voice != null)
        {
            _voiceAssignments[slot] = voice;
            _voiceIds[slot] = voiceId;
        }
    }

    public async Task<PcmAudio> SynthesizeAsync(
        string text,
        VoiceSlot slot,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_voiceAssignments.TryGetValue(slot, out var voice))
            _synth.Voice = voice;

        var stream = await _synth.SynthesizeTextToStreamAsync(text);
        ct.ThrowIfCancellationRequested();

        await using var wavStream = stream.AsStream();
        using var ms = new MemoryStream();
        await wavStream.CopyToAsync(ms, ct);
        ms.Position = 0;

        return ReadWavToPcm(ms);
    }

    public async IAsyncEnumerable<(PcmAudio audio, int phraseIndex, int phraseCount)>
        SynthesizePhraseStreamAsync(
            string text, VoiceSlot slot, string tempDirectory,
            [EnumeratorCancellation] CancellationToken ct)
    {
        var audio = await SynthesizeAsync(text, slot, ct);
        yield return (audio, 0, 1);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _synth.Dispose();
    }

    private static PcmAudio ReadWavToPcm(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        var riff = new string(reader.ReadChars(4));
        if (riff != "RIFF") throw new InvalidDataException("Invalid WAV: missing RIFF header.");

        reader.ReadInt32();
        var wave = new string(reader.ReadChars(4));
        if (wave != "WAVE") throw new InvalidDataException("Invalid WAV: missing WAVE header.");

        int sampleRate = 0;
        int channels = 0;
        int bitsPerSample = 0;
        byte[]? dataBytes = null;

        while (stream.Position <= stream.Length - 8)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadInt32();

            if (chunkId == "fmt ")
            {
                var audioFormat = reader.ReadInt16();
                channels = reader.ReadInt16();
                sampleRate = reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt16();
                bitsPerSample = reader.ReadInt16();

                if (chunkSize > 16)
                    reader.ReadBytes(chunkSize - 16);

                if (audioFormat != 1)
                    throw new InvalidDataException($"Unsupported WAV format: {audioFormat}.");
            }
            else if (chunkId == "data")
            {
                dataBytes = reader.ReadBytes(chunkSize);
            }
            else
            {
                reader.ReadBytes(chunkSize);
            }

            if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
                reader.ReadByte();

            if (sampleRate > 0 && channels > 0 && bitsPerSample > 0 && dataBytes != null)
                break;
        }

        if (dataBytes == null || sampleRate <= 0 || channels <= 0 || bitsPerSample != 16)
            throw new InvalidDataException("Unsupported or incomplete WAV returned by WinRT TTS.");

        var sampleCount = dataBytes.Length / 2;
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            int byteIndex = i * 2;
            short sample = (short)(dataBytes[byteIndex] | (dataBytes[byteIndex + 1] << 8));
            samples[i] = sample / 32768f;
        }

        return new PcmAudio(samples, sampleRate, channels);
    }
}
#endif