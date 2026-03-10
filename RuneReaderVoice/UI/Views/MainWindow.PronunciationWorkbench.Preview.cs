using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RuneReaderVoice.TTS.Pronunciation;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private void UpdatePronunciationPreview()
    {
        var original = PronTestSentence.Text ?? string.Empty;
        var processed = PronunciationWorkbenchHelper.BuildPreview(
            original,
            PronTargetText.Text ?? string.Empty,
            PronPhonemeText.Text ?? string.Empty);

        PronOriginalPreview.Text = string.IsNullOrWhiteSpace(original) ? "—" : original;
        PronProcessedPreview.Text = string.IsNullOrWhiteSpace(processed) ? "—" : processed;
    }

    private async void OnPronunciationCopyPreviewClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
                return;

            await topLevel.Clipboard.SetTextAsync(PronProcessedPreview.Text ?? string.Empty);
            SessionStatus.Text = "Pronunciation preview copied to clipboard.";
        }
        catch (Exception ex)
        {
            SessionStatus.Text = $"Clipboard copy failed: {ex.Message}";
        }
    }

    private void OnPronunciationClearClicked(object? sender, RoutedEventArgs e)
    {
        PronTestSentence.Text = string.Empty;
        PronTargetText.Text = string.Empty;
        PronPhonemeText.Text = string.Empty;
        PronRuleNotes.Text = string.Empty;

        PronSymbolTitle.Text = "Select a sound symbol";
        PronSymbolDescription.Text = "Descriptions and examples appear here.";
        PronSymbolExample.Text = string.Empty;
        PronRuleStatus.Text = "Rules save into the portable config folder.";

        SavePronunciationWorkbenchState();
        UpdatePronunciationPreview();
    }
}