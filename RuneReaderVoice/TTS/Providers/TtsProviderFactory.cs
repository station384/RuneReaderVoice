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

// TtsProviderFactory.cs
// Creates the active provider from the current settings and provider catalog.

using System;
using System.Collections.Generic;
using System.Linq;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public static class TtsProviderFactory
{
    public static ProviderRegistry BuildRegistry(VoiceUserSettings settings)
    {
        var providers = new List<ProviderDescriptor>();

#if WINDOWS
        providers.Add(new ProviderDescriptor
        {
            ClientProviderId = "winrt",
            DisplayName = "Windows Speech (WinRT)",
            TransportKind = ProviderTransportKind.Local,
            RequiresFullText = false,
        });
#elif LINUX
        providers.Add(new ProviderDescriptor
        {
            ClientProviderId = "piper",
            DisplayName = "Piper (Local ONNX)",
            TransportKind = ProviderTransportKind.Local,
            RequiresFullText = false,
        });
#endif
        providers.Add(new ProviderDescriptor
        {
            ClientProviderId = "kokoro",
            DisplayName = "Kokoro (Local ONNX)",
            TransportKind = ProviderTransportKind.Local,
            SupportsBaseVoices = true,
            SupportsVoiceBlending = true,
            SupportsInlinePronunciationHints = true,
            RequiresFullText = true,
            VoiceSourceKind = RemoteVoiceSourceKind.Voices,
            Languages = new[] { "en" },
        });

        foreach (var remote in RemoteProviderCatalog.Load(settings))
            providers.Add(remote);

        return new ProviderRegistry(providers);
    }

    public static ITtsProvider CreateProvider(VoiceUserSettings settings, ProviderDescriptor descriptor)
    {
        if (descriptor.TransportKind == ProviderTransportKind.Remote)
            return new RemoteTtsProvider(settings, descriptor);

        return descriptor.ClientProviderId switch
        {
#if WINDOWS
            "winrt" => new WinRtTtsProvider(),
#elif LINUX
            "piper" => new LinuxPiperTtsProvider(settings.PiperBinaryPath, settings.PiperModelDirectory),
#endif
            "kokoro" => new KokoroTtsProvider(),
            "cloud" => new NotImplementedTtsProvider("cloud", "Cloud TTS"),
            _ => CreateDefaultProvider(settings),
        };
    }

    public static void ApplyStoredProfiles(VoiceUserSettings settings, ITtsProvider provider)
    {
#if WINDOWS
        if (provider is WinRtTtsProvider winRtProvider)
        {
            foreach (var (key, profile) in settings.VoiceProfiles)
                if (VoiceSlot.TryParse(key, out var slot))
                    winRtProvider.SetVoice(slot, profile.VoiceId);
        }
#elif LINUX
        if (provider is LinuxPiperTtsProvider piperProvider)
        {
            foreach (var (key, profile) in settings.VoiceProfiles)
                if (VoiceSlot.TryParse(key, out var slot))
                    piperProvider.SetModel(slot, profile.VoiceId);
        }
#endif

        if (provider is KokoroTtsProvider kokoroProvider)
        {
            foreach (var (key, profile) in settings.VoiceProfiles)
                if (VoiceSlot.TryParse(key, out var slot))
                    kokoroProvider.SetVoiceProfile(slot, profile);
            kokoroProvider.EnablePhraseChunking = settings.EnablePhraseChunking;
        }
    }

    private static ITtsProvider CreateDefaultProvider(VoiceUserSettings settings)
    {
#if WINDOWS
        return new WinRtTtsProvider();
#elif LINUX
        return new LinuxPiperTtsProvider(settings.PiperBinaryPath, settings.PiperModelDirectory);
#else
        return new NotImplementedTtsProvider("unsupported", "Unsupported Platform");
#endif
    }
}