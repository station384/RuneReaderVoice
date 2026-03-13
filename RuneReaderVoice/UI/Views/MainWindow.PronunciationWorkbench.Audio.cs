using System;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Controls;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Dsp;
using RuneReaderVoice.TTS.Providers;
namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private async Task<PcmAudio> GetOrCreateAudioAsync(string text, VoiceSlot slot)
    {
        var voiceId = AppServices.Provider.ResolveVoiceId(slot);
        var profile = AppServices.Provider.ResolveProfile(slot);
        var dspKey  = profile?.Dsp?.BuildCacheKey() ?? "";

        // Cache stores DSP-processed audio — play directly on hit, no second pass.
        var cachedAudio = await AppServices.Cache.TryGetDecodedAsync(
            text,
            voiceId,
            AppServices.Provider.ProviderId,
            dspKey,
            default);

        if (cachedAudio != null)
            return cachedAudio;

        var rawAudio = await AppServices.Provider.SynthesizeAsync(text, slot, default);
        var audio    = DspFilterChain.Apply(rawAudio, profile?.Dsp);

        await AppServices.Cache.StoreAsync(
            audio,
            text,
            voiceId,
            AppServices.Provider.ProviderId,
            dspKey,
            default);

        return audio;
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