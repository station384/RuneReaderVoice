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
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuneReaderVoice.TTS.Providers;
// RemoteProviderCatalog.cs
// Logical provider list for server-backed providers and their client-facing IDs.
public static class RemoteProviderCatalog
{
    public static IReadOnlyList<ProviderDescriptor> Load(VoiceUserSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.RemoteProviderCatalogJson))
            return Array.Empty<ProviderDescriptor>();

        try
        {
            var payload = JsonSerializer.Deserialize<List<RemoteProviderInfoDto>>(settings.RemoteProviderCatalogJson)
                          ?? new List<RemoteProviderInfoDto>();
            return payload
                .Where(p => !string.IsNullOrWhiteSpace(p.ProviderId))
                .Select(ToDescriptor)
                .ToList();
        }
        catch
        {
            return Array.Empty<ProviderDescriptor>();
        }
    }

    public static string Serialize(IEnumerable<RemoteProviderInfoDto> providers)
        => JsonSerializer.Serialize(providers.ToList(), new JsonSerializerOptions { WriteIndented = false });

    public static ProviderDescriptor ToDescriptor(RemoteProviderInfoDto dto)
    {
        var voiceSourceKind = dto.SupportsBaseVoices
            ? RemoteVoiceSourceKind.Voices
            : dto.SupportsVoiceMatching
                ? RemoteVoiceSourceKind.Samples
                : RemoteVoiceSourceKind.None;

        return new ProviderDescriptor
        {
            ClientProviderId = $"remote:{dto.ProviderId}",
            DisplayName = $"Remote · {dto.DisplayName}",
            TransportKind = ProviderTransportKind.Remote,
            RemoteProviderId = dto.ProviderId,
            SupportsBaseVoices = dto.SupportsBaseVoices,
            SupportsVoiceMatching = dto.SupportsVoiceMatching,
            SupportsVoiceBlending = dto.SupportsVoiceBlending,
            SupportsInlinePronunciationHints = dto.SupportsInlinePronunciation,
            RequiresFullText = true,
            VoiceSourceKind = voiceSourceKind,
            Languages = dto.Languages ?? Array.Empty<string>(),
            Controls = (dto.Controls ?? new Dictionary<string, RemoteControlDescriptorDto>())
                .ToDictionary(k => k.Key,
                              v => new RemoteControlDescriptor
                              {
                                  Type = v.Value.Type ?? "float",
                                  Default = v.Value.Default,
                                  Min = v.Value.Min,
                                  Max = v.Value.Max,
                                  Description = v.Value.Description ?? string.Empty,
                              },
                              StringComparer.OrdinalIgnoreCase),
        };
    }
}

public sealed class RemoteProviderInfoDto
{
    [JsonPropertyName("provider_id")] public string ProviderId { get; set; } = string.Empty;
    [JsonPropertyName("display_name")] public string DisplayName { get; set; } = string.Empty;
    [JsonPropertyName("supports_base_voices")] public bool SupportsBaseVoices { get; set; }
    [JsonPropertyName("supports_voice_matching")] public bool SupportsVoiceMatching { get; set; }
    [JsonPropertyName("supports_voice_blending")] public bool SupportsVoiceBlending { get; set; }
    [JsonPropertyName("supports_inline_pronunciation")] public bool SupportsInlinePronunciation { get; set; }
    [JsonPropertyName("languages")] public string[]? Languages { get; set; }
    [JsonPropertyName("controls")] public Dictionary<string, RemoteControlDescriptorDto>? Controls { get; set; }
}

public sealed class RemoteControlDescriptorDto
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("default")] public float? Default { get; set; }
    [JsonPropertyName("min")] public float? Min { get; set; }
    [JsonPropertyName("max")] public float? Max { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
}