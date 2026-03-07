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
// Piper is invoked as a subprocess: text is piped to stdin, WAV is read from stdout.
//
// Piper binary and model paths are user-configurable in settings.
// Model files (.onnx + .onnx.json) must be downloaded separately.
// The settings UI shows installed models and links to the download page.
//
// https://github.com/rhasspy/piper

using System.Diagnostics;
using System.Runtime.CompilerServices;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed class LinuxPiperTtsProvider : ITtsProvider
{
    private readonly string _piperBinaryPath;
    private readonly string _modelDirectory;

    // VoiceSlot → model file path (e.g. "/home/user/.local/share/piper/en_US-lessac-medium.onnx")
    private readonly Dictionary<VoiceSlot, string> _modelAssignments = new();

    private bool _disposed;

    public string ProviderId   => "piper";
    public string DisplayName  => "Piper (Local ONNX)";
    public bool RequiresFullText => false;

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_piperBinaryPath) &&
        File.Exists(_piperBinaryPath);

    public LinuxPiperTtsProvider(string piperBinaryPath, string modelDirectory)
    {
        _piperBinaryPath = piperBinaryPath;
        _modelDirectory  = modelDirectory;
    }

    public string ResolveVoiceId(VoiceSlot slot) => string.Empty;

    public IReadOnlyList<VoiceInfo> GetAvailableVoices()
    {
        // Enumerate .onnx files in the model directory.
        // Each .onnx file is a Piper voice model.
        if (!Directory.Exists(_modelDirectory))
            return Array.Empty<VoiceInfo>();

        return Directory.GetFiles(_modelDirectory, "*.onnx")
            .Select(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                // Piper model names are typically: lang_REGION-voice-quality
                // e.g. en_US-lessac-medium, en_GB-alan-medium
                return new VoiceInfo
                {
                    VoiceId  = path,
                    Name     = name,
                    Language = ExtractLanguage(name),
                    Gender   = Gender.Unknown, // Piper doesn't expose gender in model name
                };
            })
            .ToList();
    }

    /// <summary>Assigns a Piper model file to a voice slot.</summary>
    public void SetModel(VoiceSlot slot, string modelPath)
    {
        _modelAssignments[slot] = modelPath;
    }

    public async Task<string> SynthesizeToFileAsync(
        string text,
        VoiceSlot slot,
        string outputPath,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_modelAssignments.TryGetValue(slot, out var modelPath))
        {
            // Fall back to any assigned model
            modelPath = _modelAssignments.Values.FirstOrDefault()
                ?? throw new InvalidOperationException("No Piper model assigned for slot: " + slot);
        }

        var wavPath = Path.ChangeExtension(outputPath, ".wav");

        // piper --model <model> --output_file <wavPath>
        // Text is piped to stdin.
        var psi = new ProcessStartInfo
        {
            FileName               = _piperBinaryPath,
            Arguments              = $"--model \"{modelPath}\" --output_file \"{wavPath}\"",
            RedirectStandardInput  = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start Piper process.");

        await process.StandardInput.WriteAsync(text);
        process.StandardInput.Close();

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var err = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"Piper exited with code {process.ExitCode}: {err}");
        }

        return wavPath;
    }

    public async IAsyncEnumerable<(string wavPath, int phraseIndex, int phraseCount)>
        SynthesizePhraseStreamAsync(
            string text, VoiceSlot slot, string tempDirectory,
            [EnumeratorCancellation] CancellationToken ct)
    {
        var tmpPath = Path.Combine(tempDirectory, $"piper_{Guid.NewGuid():N}.wav");
        var wavPath = await SynthesizeToFileAsync(text, slot, tmpPath, ct);
        yield return (wavPath, 0, 1);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private static string ExtractLanguage(string modelName)
    {
        // "en_US-lessac-medium" → "en_US"
        var underscore = modelName.IndexOf('-');
        return underscore > 0 ? modelName[..underscore] : modelName;
    }
}
#endif