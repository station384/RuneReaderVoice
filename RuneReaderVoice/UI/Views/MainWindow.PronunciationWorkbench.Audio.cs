using System;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Controls;
using RuneReaderVoice.Protocol;
namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private async Task<string> GetOrCreateAudioPathAsync(string text, VoiceSlot slot)
    {
        var voiceId = AppServices.Provider.ResolveVoiceId(slot);

        var cachedPath = await AppServices.Cache.TryGetAsync(
            text,
            voiceId,
            AppServices.Provider.ProviderId);

        if (cachedPath != null)
            return cachedPath;

        var audio = await AppServices.Provider.SynthesizeAsync(text, slot, default);

        return await AppServices.Cache.StoreAsync(
            audio,
            text,
            voiceId,
            AppServices.Provider.ProviderId,
            default);
    }

    private async Task SpeakWorkbenchTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SessionStatus.Text = "Pronunciation workbench: enter some test text first.";
            return;
        }

        var slot = ResolveWorkbenchSlot();
        var audioPath = await GetOrCreateAudioPathAsync(text, slot);

        await AppServices.Player.PlayAsync(audioPath, default);
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