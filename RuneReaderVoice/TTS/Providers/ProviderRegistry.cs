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

namespace RuneReaderVoice.TTS.Providers;
// ProviderRegistry.cs
// Static catalog of local and remote TTS providers exposed by the client.
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