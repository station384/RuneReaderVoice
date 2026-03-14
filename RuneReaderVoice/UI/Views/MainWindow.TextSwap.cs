using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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

        _ = ReloadTextSwapRuleListAsync();

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

    private async Task ReloadTextSwapProcessorAsync()
    {
        var userRules = await AppServices.TextSwapRules.LoadUserRulesAsync();
        AppServices.SetTextSwapProcessor(
            new DialogueTextSwapProcessor(
                DefaultTextSwapRules.CreateDefault()
                    .Concat(userRules)
                    .ToList()));
    }

    private async Task ReloadTextSwapRuleListAsync()
    {
        TextSwapRuleList.Items.Clear();

        var entries = await AppServices.TextSwapRules.GetAllEntriesAsync();
        foreach (var rule in entries.OrderByDescending(r => r.Priority).ThenBy(r => r.FindText))
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

    private async Task<DialogueTextSwapProcessor> BuildEffectiveTextSwapProcessorAsync()
    {
        var entries = await AppServices.TextSwapRules.GetAllEntriesAsync();
        var workingEntry = BuildTextSwapRuleEntry(includeReplaceTextWhenDisabled: false);

        var workingHasFindText = !string.IsNullOrEmpty(workingEntry.FindText);

        if (workingHasFindText)
        {
            entries.RemoveAll(r =>
                string.Equals(r.FindText, workingEntry.FindText, StringComparison.OrdinalIgnoreCase) &&
                r.WholeWord == workingEntry.WholeWord &&
                r.CaseSensitive == workingEntry.CaseSensitive);

            if (workingEntry.Enabled)
                entries.Add(workingEntry);
        }

        var effectiveRules = DefaultTextSwapRules.CreateDefault()
            .Concat(entries
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
        _ = UpdateTextSwapPreviewAsync();
    }

    private async Task UpdateTextSwapPreviewAsync()
    {
        var original = TextSwapOriginalText.Text ?? string.Empty;
        var effectiveProcessor = await BuildEffectiveTextSwapProcessorAsync();
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
            FindText        = TextSwapFindText.Text ?? string.Empty,
            ReplaceWithCrLf = TextSwapReplaceWithCrLf.IsChecked ?? false,
            ReplaceText     = (TextSwapReplaceWithCrLf.IsChecked ?? false) && !includeReplaceTextWhenDisabled
                ? string.Empty
                : (TextSwapReplaceText.Text ?? string.Empty),
            WholeWord       = TextSwapWholeWord.IsChecked ?? false,
            CaseSensitive   = TextSwapCaseSensitive.IsChecked ?? false,
            Enabled         = TextSwapEnabled.IsChecked ?? true,
            Priority        = (int)(TextSwapPriority.Value ?? 100),
            Notes           = TextSwapRuleNotes.Text ?? string.Empty,
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
            await AppServices.TextSwapRules.UpsertRuleAsync(entry);
            await ReloadTextSwapProcessorAsync();
            await ReloadTextSwapRuleListAsync();
            UpdateTextSwapPreview();
            TextSwapRuleStatus.Text = "Saved text swap rule.";
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

    private async void OnTextSwapDeleteRuleClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var entry = BuildTextSwapRuleEntry();
            if (!ValidateTextSwapRuleEntry(entry))
                return;

            await AppServices.TextSwapRules.DeleteRuleAsync(entry);
            await ReloadTextSwapProcessorAsync();
            await ReloadTextSwapRuleListAsync();
            UpdateTextSwapPreview();
            TextSwapRuleStatus.Text = "Deleted matching text swap rule.";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Delete failed: {ex.Message}";
        }
    }

    private async void OnTextSwapReloadRulesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ReloadTextSwapProcessorAsync();
            await ReloadTextSwapRuleListAsync();
            UpdateTextSwapPreview();
            TextSwapRuleStatus.Text = "Reloaded text swap rules from database.";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Reload failed: {ex.Message}";
        }
    }

    // ── Import / Export ───────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonExportOptions = new() { WriteIndented = true };

    private async void OnTextSwapExportRulesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var entries = await AppServices.TextSwapRules.GetAllEntriesAsync();
            var file = new TextSwapRuleFile { Rules = entries };
            var json = JsonSerializer.Serialize(file, _jsonExportOptions);

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var path = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title               = "Export Text Swap Rules",
                SuggestedFileName   = "text-swap-rules.json",
                DefaultExtension    = "json",
                FileTypeChoices     = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });

            if (path == null) return;

            await File.WriteAllTextAsync(path.Path.LocalPath, json);
            TextSwapRuleStatus.Text = $"Exported {entries.Count} rules.";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void OnTextSwapImportRulesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title           = "Import Text Swap Rules",
                AllowMultiple   = false,
                FileTypeFilter  = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });

            if (files.Count == 0) return;

            var json = await File.ReadAllTextAsync(files[0].Path.LocalPath);
            var file = JsonSerializer.Deserialize<TextSwapRuleFile>(json);
            if (file?.Rules == null || file.Rules.Count == 0)
            {
                TextSwapRuleStatus.Text = "No rules found in file.";
                return;
            }

            foreach (var entry in file.Rules)
                await AppServices.TextSwapRules.UpsertRuleAsync(entry);

            await ReloadTextSwapProcessorAsync();
            await ReloadTextSwapRuleListAsync();
            UpdateTextSwapPreview();
            TextSwapRuleStatus.Text = $"Imported {file.Rules.Count} rules.";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Import failed: {ex.Message}";
        }
    }

    // ── Removed: OnTextSwapOpenRulesFileClicked (rules now stored in DB) ──────

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
        => await SpeakTextSwapPreviewAsync(TextSwapOriginalPreview.Text ?? string.Empty, TextSwapSpeakOriginalButton);

    private async void OnTextSwapSpeakProcessedClicked(object? sender, RoutedEventArgs e)
        => await SpeakTextSwapPreviewAsync(TextSwapProcessedPreview.Text ?? string.Empty, TextSwapSpeakProcessedButton);

    private async void OnTextSwapSpeakFinalClicked(object? sender, RoutedEventArgs e)
        => await SpeakTextSwapPreviewAsync(TextSwapFinalPreview.Text ?? string.Empty, TextSwapSpeakFinalButton);
}
