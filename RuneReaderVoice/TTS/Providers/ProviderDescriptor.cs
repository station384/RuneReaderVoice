using System;
using System.Collections.Generic;

namespace RuneReaderVoice.TTS.Providers;

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
    public float? Default { get; init; }
    public float? Min { get; init; }
    public float? Max { get; init; }
    public string Description { get; init; } = string.Empty;
}
