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
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Controls;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Dsp;
using RuneReaderVoice.TTS.Providers;
namespace RuneReaderVoice.UI.Views;
// MainWindow.PronunciationWorkbench.Audio.cs
// Speak and preview actions for the pronunciation workbench.
public partial class MainWindow
{
    private async Task<PcmAudio> GetOrCreateAudioAsync(string text, VoiceSlot slot)
    {
        var voiceId = AppServices.Provider.ResolveVoiceId(slot);
        var profile = AppServices.Provider.ResolveProfile(slot);
        // Include slot in cache key — same as PlaybackCoordinator — so two slots
        // that share the same underlying voice ID never collide in cache.
        var effectiveVoiceId = $"{slot}:{voiceId}";
        var cachedAudio = await AppServices.Cache.TryGetDecodedAsync(
            text,
            effectiveVoiceId,
            AppServices.Provider.ProviderId,
            "",
            default);

        if (cachedAudio != null)
            return DspFilterChain.Apply(cachedAudio, profile?.Dsp);

        if (AppServices.Provider is RemoteTtsProvider remote)
        {
            var oggBytes = await remote.SynthesizeOggAsync(text, slot, default);
            await AppServices.Cache.StoreOggAsync(
                oggBytes,
                text,
                effectiveVoiceId,
                AppServices.Provider.ProviderId,
                "",
                default);

            var decoded = await AppServices.Cache.TryGetDecodedAsync(
                text,
                effectiveVoiceId,
                AppServices.Provider.ProviderId,
                "",
                default);

            if (decoded == null)
                throw new InvalidOperationException("Remote audio was cached but could not be decoded.");

            return DspFilterChain.Apply(decoded, profile?.Dsp);
        }

        var rawAudio = await AppServices.Provider.SynthesizeAsync(text, slot, default);

        await AppServices.Cache.StoreAsync(
            rawAudio,
            text,
            effectiveVoiceId,
            AppServices.Provider.ProviderId,
            "",
            default);

        return DspFilterChain.Apply(rawAudio, profile?.Dsp);
    }

    private async Task SpeakWorkbenchTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SessionStatus.Text = "Pronunciation workbench: enter some test text first.";
            return;
        }

        var slot = ResolveWorkbenchSlot();
        var audio = await GetOrCreateAudioAsync(text, slot);

        await AppServices.Player.PlayAsync(audio, default);
    }

    private async void OnPronunciationSpeakOriginalClicked(object? sender, RoutedEventArgs e)
    {
        await SpeakFromButtonAsync(
            PronSpeakOriginalButton,
            PronTestSentence.Text ?? string.Empty);
    }

    private async void OnPronunciationSpeakProcessedClicked(object? sender, RoutedEventArgs e)
    {
        await SpeakFromButtonAsync(
            PronSpeakProcessedButton,
            PronProcessedPreview.Text ?? string.Empty);
    }

    private async Task SpeakFromButtonAsync(Button button, string text)
    {
        button.IsEnabled = false;

        try
        {
            await SpeakWorkbenchTextAsync(text);
        }
        catch (Exception ex)
        {
            SessionStatus.Text = $"Pronunciation preview failed: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }
}