using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
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
        TextSwapReplaceText.Text = s.TextSwapWorkbenchReplaceText;
        TextSwapWholeWord.IsChecked = s.TextSwapWorkbenchWholeWord;
        TextSwapCaseSensitive.IsChecked = s.TextSwapWorkbenchCaseSensitive;
        TextSwapRuleNotes.Text = s.TextSwapWorkbenchNotes;
        TextSwapEnabled.IsChecked = true;
        TextSwapPriority.Value = 100;

        ReloadTextSwapRuleList();

        _textSwapUiInitializing = false;
        UpdateTextSwapPreview();
    }

    private void SaveTextSwapWorkbenchState()
    {
        var s = AppServices.Settings;
        s.TextSwapWorkbenchOriginalText = TextSwapOriginalText.Text ?? string.Empty;
        s.TextSwapWorkbenchFindText = TextSwapFindText.Text ?? string.Empty;
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

        return $"{rule.FindText} → {rule.ReplaceText}  [{flags}]";
    }

    private void UpdateTextSwapPreview()
    {
        var original = TextSwapOriginalText.Text ?? string.Empty;
        var fullShaped = string.IsNullOrWhiteSpace(TextSwapFindText.Text)
            ? AppServices.TextSwapProcessor.Process(original)
            : TextSwapWorkbenchHelper.BuildPreview(
                original,
                TextSwapFindText.Text ?? string.Empty,
                TextSwapReplaceText.Text ?? string.Empty,
                TextSwapWholeWord.IsChecked ?? false,
                TextSwapCaseSensitive.IsChecked ?? false);

        var finalText = AppServices.Provider.SupportsInlinePronunciationHints
            ? AppServices.PronunciationProcessor.Process(new Session.AssembledSegment
            {
                Text = fullShaped,
                Slot = Protocol.VoiceSlot.Narrator,
                DialogId = 0,
                SegmentIndex = 0,
                NpcId = 0,
            }).Text
            : fullShaped;

        TextSwapOriginalPreview.Text = string.IsNullOrWhiteSpace(original) ? "—" : original;
        TextSwapProcessedPreview.Text = string.IsNullOrWhiteSpace(fullShaped) ? "—" : fullShaped;
        TextSwapFinalPreview.Text = string.IsNullOrWhiteSpace(finalText) ? "—" : finalText;
    }

    private void OnTextSwapTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_textSwapUiInitializing)
            return;

        SaveTextSwapWorkbenchState();
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
        TextSwapWholeWord.IsChecked = entry.WholeWord;
        TextSwapCaseSensitive.IsChecked = entry.CaseSensitive;
        TextSwapEnabled.IsChecked = entry.Enabled;
        TextSwapPriority.Value = entry.Priority;
        TextSwapRuleNotes.Text = entry.Notes;
        _textSwapUiInitializing = false;

        SaveTextSwapWorkbenchState();
        UpdateTextSwapPreview();
    }

    private TextSwapRuleEntry BuildTextSwapRuleEntry()
        => new()
        {
            FindText = (TextSwapFindText.Text ?? string.Empty).Trim(),
            ReplaceText = TextSwapReplaceText.Text ?? string.Empty,
            WholeWord = TextSwapWholeWord.IsChecked ?? false,
            CaseSensitive = TextSwapCaseSensitive.IsChecked ?? false,
            Enabled = TextSwapEnabled.IsChecked ?? true,
            Priority = (int)(TextSwapPriority.Value ?? 100),
            Notes = TextSwapRuleNotes.Text ?? string.Empty,
        };

    private bool ValidateTextSwapRuleEntry(TextSwapRuleEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.FindText))
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
            await System.Threading.Tasks.Task.CompletedTask;
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
        TextSwapWholeWord.IsChecked = false;
        TextSwapCaseSensitive.IsChecked = false;
        TextSwapEnabled.IsChecked = true;
        TextSwapPriority.Value = 100;
        TextSwapRuleNotes.Text = string.Empty;
        TextSwapRuleList.SelectedItem = null;
        _textSwapUiInitializing = false;

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

    private async System.Threading.Tasks.Task CopyTextSwapPreviewAsync()
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
}