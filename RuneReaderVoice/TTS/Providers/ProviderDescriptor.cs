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
using System.Collections.Generic;

namespace RuneReaderVoice.TTS.Providers;
// ProviderDescriptor.cs
// Describes provider capabilities, UI labels, and provider-family behavior.
public enum ProviderTransportKind
{
    Local,
    Remote,
}

public enum RemoteVoiceSourceKind
{
    None,
    Voices,
    Samples,
}

public sealed class ProviderDescriptor
{
    public string ClientProviderId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public ProviderTransportKind TransportKind { get; init; }
    public string? RemoteProviderId { get; init; }
    public bool SupportsBaseVoices { get; init; }
    public bool SupportsVoiceMatching { get; init; }
    public bool SupportsVoiceBlending { get; init; }
    public bool SupportsInlinePronunciationHints { get; init; }
    public bool RequiresFullText { get; init; } = true;
    public RemoteVoiceSourceKind VoiceSourceKind { get; init; } = RemoteVoiceSourceKind.None;
    public IReadOnlyList<string> Languages { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, RemoteControlDescriptor> Controls { get; init; } =
        new Dictionary<string, RemoteControlDescriptor>(StringComparer.OrdinalIgnoreCase);
}

public sealed class RemoteControlDescriptor
{
    public string Type { get; init; } = "float";
    public string? Default { get; init; }   // string to handle both numeric and string defaults
    public float? Min { get; init; }
    public float? Max { get; init; }
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<string> Options { get; init; } = Array.Empty<string>();
}