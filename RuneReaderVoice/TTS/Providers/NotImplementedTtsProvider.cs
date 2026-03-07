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

// NotImplementedTtsProvider.cs
// Placeholder provider for Phase 3 AI voice backends (ONNX, cloud, etc.).
// Exists so the DI wiring compiles and the UI can show the option as "coming soon".

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed class NotImplementedTtsProvider : ITtsProvider
{
    private readonly string _providerId;
    private readonly string _displayName;

    public NotImplementedTtsProvider(string providerId, string displayName)
    {
        _providerId  = providerId;
        _displayName = displayName;
    }

    public string ProviderId    => _providerId;
    public string DisplayName   => _displayName;
    public bool IsAvailable     => false;
    public bool RequiresFullText => true;

    public string ResolveVoiceId(VoiceSlot slot) => string.Empty;

    public IReadOnlyList<VoiceInfo> GetAvailableVoices() => Array.Empty<VoiceInfo>();

    public Task<string> SynthesizeToFileAsync(
        string text, VoiceSlot slot, string outputPath, CancellationToken ct)
        => throw new NotImplementedException($"{_displayName} is not yet implemented.");

    public void Dispose() { }
}