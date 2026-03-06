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
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;
using Windows.Media.SpeechSynthesis;

namespace RuneReaderVoice.TTS.Providers;

[SupportedOSPlatform("windows10.0.10240.0")]
public sealed class WinRtTtsProvider : ITtsProvider
{
    // One synthesizer instance per accent group slot for concurrent synthesis.
    // In practice we synthesize sequentially, so one instance is fine for now.
    private readonly SpeechSynthesizer _synth = new();
    private bool _disposed;

    // Voice assignments: VoiceSlot → WinRT VoiceInformation
    // Populated by the settings layer via SetVoice().
    private readonly Dictionary<VoiceSlot, VoiceInformation> _voiceAssignments = new();

    public string ProviderId   => "winrt";
    public string DisplayName  => "Windows Speech (WinRT)";
    public bool IsAvailable    => true; // always available on Windows build
    public bool RequiresFullText => false; // WinRT handles short segments fine

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

    /// <summary>Assigns a WinRT voice to a specific slot.</summary>
    public void SetVoice(VoiceSlot slot, string voiceId)
    {
        var voice = SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == voiceId);
        if (voice != null)
            _voiceAssignments[slot] = voice;
    }

    public async Task<string> SynthesizeToFileAsync(
        string text,
        VoiceSlot slot,
        string outputPath,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Apply the assigned voice for this slot
        if (_voiceAssignments.TryGetValue(slot, out var voice))
            _synth.Voice = voice;
        // else: use the synthesizer's default voice

        var stream = await _synth.SynthesizeTextToStreamAsync(text);

        ct.ThrowIfCancellationRequested();

        // Ensure .wav extension
        var wavPath = Path.ChangeExtension(outputPath, ".wav");
        await using var fileStream = File.Create(wavPath);
        await stream.AsStream().CopyToAsync(fileStream, ct);

        return wavPath;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _synth.Dispose();
    }
}
#endif
