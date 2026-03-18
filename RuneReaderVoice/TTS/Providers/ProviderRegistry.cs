using System;
using System.Collections.Generic;
using System.Linq;

namespace RuneReaderVoice.TTS.Providers;

public sealed class ProviderRegistry
{
    private readonly Dictionary<string, ProviderDescriptor> _providers =
        new(StringComparer.OrdinalIgnoreCase);

    public ProviderRegistry(IEnumerable<ProviderDescriptor> providers)
    {
        foreach (var provider in providers)
            _providers[provider.ClientProviderId] = provider;
    }

    public IReadOnlyList<ProviderDescriptor> All() => _providers.Values.OrderBy(p => p.DisplayName).ToList();

    public ProviderDescriptor? Get(string providerId)
        => _providers.TryGetValue(providerId, out var descriptor) ? descriptor : null;

    public bool Contains(string providerId) => _providers.ContainsKey(providerId);
}
