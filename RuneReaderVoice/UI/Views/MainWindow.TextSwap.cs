using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.Session;
using RuneReaderVoice.TTS.TextSwap;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private bool _textSwapUiInitializing;

    private void PopulateTextSwapWorkbench()
    {
        _textSwapUiInitializing = true;

        var s = AppServices.Settings;
        TextSwapOriginalText.Text = s.TextSwapWorkbenchOriginalText;
        TextSwapFindText.Text = s.TextSwapWorkbenchFindText;
        TextSwapReplaceWithCrLf.IsChecked = s.TextSwapWorkbenchReplaceWithCrLf;
        TextSwapReplaceText.Text = s.TextSwapWorkbenchReplaceText;
        TextSwapWholeWord.IsChecked = s.TextSwapWorkbenchWholeWord;
        TextSwapCaseSensitive.IsChecked = s.TextSwapWorkbenchCaseSensitive;
        TextSwapRuleNotes.Text = s.TextSwapWorkbenchNotes;
        TextSwapEnabled.IsChecked = true;
        TextSwapPriority.Value = 100;
        TextSwapReplaceText.IsEnabled = !(TextSwapReplaceWithCrLf.IsChecked ?? false);

        ReloadTextSwapRuleList();

        _textSwapUiInitializing = false;
        UpdateTextSwapPreview();
    }

    private void SaveTextSwapWorkbenchState()
    {
        var s = AppServices.Settings;
        s.TextSwapWorkbenchOriginalText = TextSwapOriginalText.Text ?? string.Empty;
        s.TextSwapWorkbenchFindText = TextSwapFindText.Text ?? string.Empty;
        s.TextSwapWorkbenchReplaceWithCrLf = TextSwapReplaceWithCrLf.IsChecked ?? false;
        s.TextSwapWorkbenchReplaceText = TextSwapReplaceText.Text ?? string.Empty;
        s.TextSwapWorkbenchWholeWord = TextSwapWholeWord.IsChecked ?? false;
        s.TextSwapWorkbenchCaseSensitive = TextSwapCaseSensitive.IsChecked ?? false;
        s.TextSwapWorkbenchNotes = TextSwapRuleNotes.Text ?? string.Empty;
        VoiceSettingsManager.SaveSettings(s);
    }

    private void ReloadTextSwapProcessor()
    {
        AppServices.SetTextSwapProcessor(
            new DialogueTextSwapProcessor(
                DefaultTextSwapRules.CreateDefault()
                    .Concat(TextSwapRuleStore.LoadUserRules())
                    .ToList()));
    }

    private void ReloadTextSwapRuleList()
    {
        TextSwapRuleList.Items.Clear();

        var file = TextSwapRuleStore.LoadRuleFile();
        foreach (var rule in file.Rules.OrderByDescending(r => r.Priority).ThenBy(r => r.FindText))
        {
            TextSwapRuleList.Items.Add(new ListBoxItem
            {
                Content = BuildTextSwapRuleSummary(rule),
                Tag = rule,
            });
        }
    }

    private static string BuildTextSwapRuleSummary(TextSwapRuleEntry rule)
    {
        var flags = string.Join(", ", new[]
        {
            rule.Enabled ? "Enabled" : "Disabled",
            rule.WholeWord ? "Whole word" : "Phrase",
            rule.CaseSensitive ? "Case sensitive" : "Ignore case",
            $"Priority {rule.Priority}"
        });

        var replaceDisplay = rule.ReplaceWithCrLf ? @"\r\n" : rule.ReplaceText;
        return $"{rule.FindText} → {replaceDisplay}  [{flags}]";
    }

    private DialogueTextSwapProcessor BuildEffectiveTextSwapProcessor()
    {
        var file = TextSwapRuleStore.LoadRuleFile();
        var workingEntry = BuildTextSwapRuleEntry(includeReplaceTextWhenDisabled: false);

        var workingHasFindText = !string.IsNullOrEmpty(workingEntry.FindText);

        if (workingHasFindText)
        {
            file.Rules.RemoveAll(r =>
                string.Equals(r.FindText, workingEntry.FindText, StringComparison.OrdinalIgnoreCase) &&
                r.WholeWord == workingEntry.WholeWord &&
                r.CaseSensitive == workingEntry.CaseSensitive);

            if (workingEntry.Enabled)
                file.Rules.Add(workingEntry);
        }

        var effectiveRules = DefaultTextSwapRules.CreateDefault()
            .Concat(file.Rules
                .Where(r => r.Enabled && !string.IsNullOrEmpty(r.FindText))
                .Select(r => r.ToRule()))
            .ToList();

        return new DialogueTextSwapProcessor(effectiveRules);
    }

    private string BuildFinalTtsPreviewText(string processedText)
    {
        if (!AppServices.Provider.SupportsInlinePronunciationHints)
            return processedText;

        return AppServices.PronunciationProcessor.Process(new AssembledSegment
        {
            Text = processedText,
            Slot = VoiceSlot.Narrator,
            DialogId = 0,
            SegmentIndex = 0,
            NpcId = 0,
        }).Text;
    }

    private void UpdateTextSwapPreview()
    {
        var original = TextSwapOriginalText.Text ?? string.Empty;
        var effectiveProcessor = BuildEffectiveTextSwapProcessor();
        var processed = effectiveProcessor.Process(original);
        var finalText = BuildFinalTtsPreviewText(processed);

        TextSwapOriginalPreview.Text = original;
        TextSwapProcessedPreview.Text = processed;
        TextSwapFinalPreview.Text = finalText;
    }

    private void OnTextSwapTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_textSwapUiInitializing)
            return;

        SaveTextSwapWorkbenchState();
        TextSwapReplaceText.IsEnabled = !(TextSwapReplaceWithCrLf.IsChecked ?? false);
        UpdateTextSwapPreview();
    }

    private void OnTextSwapClickChanged(object? sender, RoutedEventArgs e)
    {
        if (_textSwapUiInitializing)
            return;

        SaveTextSwapWorkbenchState();
        UpdateTextSwapPreview();
    }

    private void OnTextSwapPriorityChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (_textSwapUiInitializing)
            return;

        UpdateTextSwapPreview();
    }

    private void OnTextSwapRuleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_textSwapUiInitializing)
            return;

        if (TextSwapRuleList.SelectedItem is not ListBoxItem { Tag: TextSwapRuleEntry entry })
            return;

        _textSwapUiInitializing = true;
        TextSwapFindText.Text = entry.FindText;
        TextSwapReplaceText.Text = entry.ReplaceText;
        TextSwapReplaceWithCrLf.IsChecked = entry.ReplaceWithCrLf;
        TextSwapWholeWord.IsChecked = entry.WholeWord;
        TextSwapCaseSensitive.IsChecked = entry.CaseSensitive;
        TextSwapEnabled.IsChecked = entry.Enabled;
        TextSwapPriority.Value = entry.Priority;
        TextSwapRuleNotes.Text = entry.Notes;
        _textSwapUiInitializing = false;

        TextSwapReplaceText.IsEnabled = !(TextSwapReplaceWithCrLf.IsChecked ?? false);
        SaveTextSwapWorkbenchState();
        UpdateTextSwapPreview();
    }

    private TextSwapRuleEntry BuildTextSwapRuleEntry(bool includeReplaceTextWhenDisabled = true)
        => new()
        {
            FindText = TextSwapFindText.Text ?? string.Empty,
            ReplaceWithCrLf = TextSwapReplaceWithCrLf.IsChecked ?? false,
            ReplaceText = (TextSwapReplaceWithCrLf.IsChecked ?? false) && !includeReplaceTextWhenDisabled ? string.Empty : (TextSwapReplaceText.Text ?? string.Empty),
            WholeWord = TextSwapWholeWord.IsChecked ?? false,
            CaseSensitive = TextSwapCaseSensitive.IsChecked ?? false,
            Enabled = TextSwapEnabled.IsChecked ?? true,
            Priority = (int)(TextSwapPriority.Value ?? 100),
            Notes = TextSwapRuleNotes.Text ?? string.Empty,
        };

    private bool ValidateTextSwapRuleEntry(TextSwapRuleEntry entry)
    {
        if (entry.FindText.Length == 0)
        {
            TextSwapRuleStatus.Text = "Enter text to find before saving a rule.";
            return false;
        }

        return true;
    }

    private async void OnTextSwapSaveRuleClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var entry = BuildTextSwapRuleEntry();
            if (!ValidateTextSwapRuleEntry(entry))
                return;

            TextSwapSaveRuleButton.IsEnabled = false;
            TextSwapRuleStore.UpsertRule(entry);
            ReloadTextSwapProcessor();
            ReloadTextSwapRuleList();
            UpdateTextSwapPreview();
            TextSwapRuleStatus.Text = $"Saved rule to {TextSwapRuleStore.GetRulesFilePath()}";
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            TextSwapSaveRuleButton.IsEnabled = true;
        }
    }

    private void OnTextSwapNewRuleClicked(object? sender, RoutedEventArgs e)
    {
        _textSwapUiInitializing = true;
        TextSwapFindText.Text = string.Empty;
        TextSwapReplaceText.Text = string.Empty;
        TextSwapReplaceWithCrLf.IsChecked = false;
        TextSwapWholeWord.IsChecked = false;
        TextSwapCaseSensitive.IsChecked = false;
        TextSwapEnabled.IsChecked = true;
        TextSwapPriority.Value = 100;
        TextSwapRuleNotes.Text = string.Empty;
        TextSwapRuleList.SelectedItem = null;
        _textSwapUiInitializing = false;
        TextSwapReplaceText.IsEnabled = true;

        SaveTextSwapWorkbenchState();
        UpdateTextSwapPreview();
        TextSwapRuleStatus.Text = "Started a new text swap rule.";
    }

    private void OnTextSwapDeleteRuleClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var entry = BuildTextSwapRuleEntry();
            if (!ValidateTextSwapRuleEntry(entry))
                return;

            TextSwapRuleStore.DeleteRule(entry);
            ReloadTextSwapProcessor();
            ReloadTextSwapRuleList();
            UpdateTextSwapPreview();
            TextSwapRuleStatus.Text = "Deleted matching text swap rule.";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Delete failed: {ex.Message}";
        }
    }

    private void OnTextSwapReloadRulesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            ReloadTextSwapProcessor();
            ReloadTextSwapRuleList();
            UpdateTextSwapPreview();
            TextSwapRuleStatus.Text = "Reloaded rules from config/text-swap-rules.json.";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Reload failed: {ex.Message}";
        }
    }

    private void OnTextSwapOpenRulesFileClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = TextSwapRuleStore.GetRulesFilePath();
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
                System.IO.Directory.CreateDirectory(dir);

            if (!System.IO.File.Exists(path))
                TextSwapRuleStore.SaveRuleFile(TextSwapRuleStore.LoadRuleFile());

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });

            TextSwapRuleStatus.Text = $"Opened {path}";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Open failed: {ex.Message}";
        }
    }

    private void OnTextSwapCopyPreviewClicked(object? sender, RoutedEventArgs e)
    {
        _ = CopyTextSwapPreviewAsync();
    }

    private async Task CopyTextSwapPreviewAsync()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
                return;

            await topLevel.Clipboard.SetTextAsync(TextSwapFinalPreview.Text ?? string.Empty);
            TextSwapRuleStatus.Text = "Final TTS preview copied to clipboard.";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Clipboard copy failed: {ex.Message}";
        }
    }

    private async Task SpeakTextSwapPreviewAsync(string text, Button button)
    {
        if (string.IsNullOrEmpty(text))
        {
            TextSwapRuleStatus.Text = "There is no text to preview.";
            return;
        }

        button.IsEnabled = false;
        try
        {
            var audio = await GetOrCreateAudioAsync(text, VoiceSlot.Narrator);
            await AppServices.Player.PlayAsync(audio, default);
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Text shaping preview failed: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnTextSwapSpeakOriginalClicked(object? sender, RoutedEventArgs e)
    {
        await SpeakTextSwapPreviewAsync(TextSwapOriginalPreview.Text ?? string.Empty, TextSwapSpeakOriginalButton);
    }

    private async void OnTextSwapSpeakProcessedClicked(object? sender, RoutedEventArgs e)
    {
        await SpeakTextSwapPreviewAsync(TextSwapProcessedPreview.Text ?? string.Empty, TextSwapSpeakProcessedButton);
    }

    private async void OnTextSwapSpeakFinalClicked(object? sender, RoutedEventArgs e)
    {
        await SpeakTextSwapPreviewAsync(TextSwapFinalPreview.Text ?? string.Empty, TextSwapSpeakFinalButton);
    }
}