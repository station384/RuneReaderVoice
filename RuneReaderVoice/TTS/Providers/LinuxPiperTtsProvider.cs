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

#if LINUX
// LinuxPiperTtsProvider.cs
// TTS synthesis via Piper (by Rhasspy/Nabu Casa) on Linux.
// Piper is invoked as a subprocess: text is piped to stdin and WAV is read
// back from stdout so no temporary files are created.

using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed class LinuxPiperTtsProvider : ITtsProvider
{
    private readonly string _piperBinaryPath;
    private readonly string _modelDirectory;
    private readonly Dictionary<VoiceSlot, string> _modelAssignments = new();
    private bool _disposed;

    public string ProviderId   => "piper";
    public string DisplayName  => "Piper (Local ONNX)";
    public bool RequiresFullText => false;
    public bool SupportsInlinePronunciationHints => false;

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_piperBinaryPath) &&
        File.Exists(_piperBinaryPath);
    public VoiceProfile? ResolveProfile(VoiceSlot slot)
    {
        return null;
    }
    public LinuxPiperTtsProvider(string piperBinaryPath, string modelDirectory)
    {
        _piperBinaryPath = piperBinaryPath;
        _modelDirectory  = modelDirectory;
    }

    public string ResolveVoiceId(VoiceSlot slot)
        => _modelAssignments.TryGetValue(slot, out var modelPath) ? modelPath : string.Empty;

    public IReadOnlyList<VoiceInfo> GetAvailableVoices()
    {
        if (!Directory.Exists(_modelDirectory))
            return Array.Empty<VoiceInfo>();

        return Directory.GetFiles(_modelDirectory, "*.onnx")
            .Select(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return new VoiceInfo
                {
                    VoiceId  = path,
                    Name     = name,
                    Language = ExtractLanguage(name),
                    Gender   = Gender.Unknown,
                };
            })
            .ToList();
    }

    public void SetModel(VoiceSlot slot, string modelPath)
    {
        _modelAssignments[slot] = modelPath;
    }

    public async Task<PcmAudio> SynthesizeAsync(
        string text,
        VoiceSlot slot,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_modelAssignments.TryGetValue(slot, out var modelPath))
        {
            modelPath = _modelAssignments.Values.FirstOrDefault()
                ?? throw new InvalidOperationException("No Piper model assigned for slot: " + slot);
        }

        var psi = new ProcessStartInfo
        {
            FileName              = _piperBinaryPath,
            Arguments             = $"--model "{modelPath}"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute       = false,
            CreateNoWindow        = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Piper process.");

        await process.StandardInput.WriteAsync(text.AsMemory(), ct);
        await process.StandardInput.WriteLineAsync();
        process.StandardInput.Close();

        using var wavBuffer = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(wavBuffer, ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"Piper exited with code {process.ExitCode}: {err}");
        }

        wavBuffer.Position = 0;
        return ReadWavToPcm(wavBuffer);
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
        _disposed = true;
    }

    private static string ExtractLanguage(string modelName)
    {
        var hyphen = modelName.IndexOf('-');
        return hyphen > 0 ? modelName[..hyphen] : modelName;
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
            throw new InvalidDataException("Unsupported or incomplete WAV returned by Piper.");

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
