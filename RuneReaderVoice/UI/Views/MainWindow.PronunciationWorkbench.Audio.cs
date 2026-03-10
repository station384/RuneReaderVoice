using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Interactivity;
using Avalonia.Controls;
namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private async Task SpeakWorkbenchTextAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SessionStatus.Text = "Pronunciation workbench: enter some test text first.";
            return;
        }

        var slot = ResolveWorkbenchSlot();
        var outPath = Path.Combine(
            Path.GetTempPath(),
            $"rrv_pron_preview_{Guid.NewGuid():N}.wav");

        await AppServices.Provider.SynthesizeToFileAsync(text, slot, outPath, default);
        await AppServices.Player.PlayAsync(outPath, default);
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