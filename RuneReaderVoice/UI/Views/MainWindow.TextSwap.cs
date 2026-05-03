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
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.Session;
using RuneReaderVoice.TTS.TextSwap;

namespace RuneReaderVoice.UI.Views;
// MainWindow.TextSwap.cs
// Text shaping tab wiring, preview updates, and rule persistence.
public partial class MainWindow
{
    private bool _textSwapUiInitializing;
    private int _textSwapPageNumber = 1;
    private int _textSwapPageSize = 25;
    private int _textSwapRuleListReloadSerial;

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
        TextSwapUseRegex.IsChecked = s.TextSwapWorkbenchUseRegex;
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
        s.TextSwapWorkbenchUseRegex = TextSwapUseRegex.IsChecked ?? false;
        s.TextSwapWorkbenchNotes = TextSwapRuleNotes.Text ?? string.Empty;
        VoiceSettingsManager.SaveSettings(s);
    }

    private async Task ReloadTextSwapProcessorAsync()
    {
        var userRules = await AppServices.TextSwapRules.LoadUserRulesAsync();
        AppServices.SetTextSwapProcessor(
            new DialogueTextSwapProcessor(userRules));
    }

    private async Task ReloadTextSwapRuleListAsync()
    {
        var reloadSerial = Interlocked.Increment(ref _textSwapRuleListReloadSerial);

        if (TextSwapPageSizeComboBox.SelectedItem == null)
            TextSwapPageSizeComboBox.SelectedIndex = 0;

        var page = await AppServices.TextSwapRules.QueryPageAsync(_textSwapPageNumber, _textSwapPageSize);
        var totalPages = Math.Max(1, (int)Math.Ceiling(page.TotalCount / (double)page.PageSize));
        if (_textSwapPageNumber > totalPages)
        {
            _textSwapPageNumber = totalPages;
            page = await AppServices.TextSwapRules.QueryPageAsync(_textSwapPageNumber, _textSwapPageSize);
        }

        if (reloadSerial != _textSwapRuleListReloadSerial)
            return;

        TextSwapRuleList.Items.Clear();
        foreach (var rule in page.Items)
        {
            TextSwapRuleList.Items.Add(new ListBoxItem
            {
                Content = BuildTextSwapRuleSummary(rule),
                Tag = rule,
            });
        }

        TextSwapPageInfoText.Text = $"Page {_textSwapPageNumber} / {Math.Max(1, totalPages)}  ({page.TotalCount} rules)";
        TextSwapPrevPageButton.IsEnabled = _textSwapPageNumber > 1;
        TextSwapNextPageButton.IsEnabled = _textSwapPageNumber < totalPages;
    }

    private static string BuildTextSwapRuleSummary(TextSwapRuleEntry rule)
    {
        var flags = string.Join(", ", new[]
        {
            rule.Enabled ? "Enabled" : "Disabled",
            rule.WholeWord ? "Whole word" : "Phrase",
            rule.CaseSensitive ? "Case sensitive" : "Ignore case",
            rule.UseRegex ? "Regex" : "Literal",
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
                r.CaseSensitive == workingEntry.CaseSensitive &&
                r.UseRegex == workingEntry.UseRegex);

            if (workingEntry.Enabled)
                entries.Add(workingEntry);
        }

        var effectiveRules = entries
            .Where(r => r.Enabled && !string.IsNullOrEmpty(r.FindText))
            .Select(r => r.ToRule())
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
        TextSwapUseRegex.IsChecked = entry.UseRegex;
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
            UseRegex        = TextSwapUseRegex.IsChecked ?? false,
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

        if (entry.UseRegex)
        {
            try
            {
                _ = new Regex(DialogueTextSwapProcessor.DecodeTextSwapEscapes(entry.FindText));
            }
            catch (Exception ex)
            {
                TextSwapRuleStatus.Text = $"Regex is invalid: {ex.Message}";
                return false;
            }
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
        TextSwapUseRegex.IsChecked = false;
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

    private async void OnTextSwapAddDefaultsClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            await AppServices.TextSwapRules.AddDefaultRulesAsync();
            await ReloadTextSwapProcessorAsync();
            await ReloadTextSwapRuleListAsync();
            UpdateTextSwapPreview();
            TextSwapRuleStatus.Text = "Added default text shaping rules to the database.";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Add defaults failed: {ex.Message}";
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

    private async void OnTextSwapPushToServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            TextSwapRuleStatus.Text = "No server URL configured.";
            return;
        }

        try
        {
            var entries = await AppServices.TextSwapRules.GetAllEntriesAsync();
            var file    = new TextSwapRuleFile { Rules = entries };
            var json    = System.Text.Json.JsonSerializer.Serialize(file,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var ok = await AppServices.NpcSync.PushDefaultsAsync("text-shaping", json);
            TextSwapRuleStatus.Text = ok ? "Text shaping rules pushed to server." : "Push failed — check server logs.";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Push failed: {ex.Message}";
        }
    }

    private async void OnTextSwapPullFromServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            TextSwapRuleStatus.Text = "No server URL configured.";
            return;
        }

        try
        {
            var ok = await AppServices.NpcSync.PullAndApplyDefaultsAsync("text-shaping");
            if (ok)
            {
                await ReloadTextSwapProcessorAsync();
                await ReloadTextSwapRuleListAsync();
            }
            TextSwapRuleStatus.Text = ok ? "Text shaping rules pulled from server." : "No text shaping rules on server.";
        }
        catch (Exception ex)
        {
            TextSwapRuleStatus.Text = $"Pull failed: {ex.Message}";
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
        => await SpeakTextSwapPreviewAsync(TextSwapOriginalPreview.Text ?? string.Empty, TextSwapSpeakOriginalButton);

    private async void OnTextSwapSpeakProcessedClicked(object? sender, RoutedEventArgs e)
        => await SpeakTextSwapPreviewAsync(TextSwapProcessedPreview.Text ?? string.Empty, TextSwapSpeakProcessedButton);

    private async void OnTextSwapSpeakFinalClicked(object? sender, RoutedEventArgs e)
        => await SpeakTextSwapPreviewAsync(TextSwapFinalPreview.Text ?? string.Empty, TextSwapSpeakFinalButton);

private async void OnTextSwapPrevPageClicked(object? sender, RoutedEventArgs e)
    {
        if (_textSwapPageNumber <= 1) return;
        _textSwapPageNumber--;
        await ReloadTextSwapRuleListAsync();
    }

    private async void OnTextSwapNextPageClicked(object? sender, RoutedEventArgs e)
    {
        _textSwapPageNumber++;
        await ReloadTextSwapRuleListAsync();
    }

    private async void OnTextSwapPageSizeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TextSwapPageSizeComboBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var pageSize))
        {
            _textSwapPageSize = pageSize;
            _textSwapPageNumber = 1;
            await ReloadTextSwapRuleListAsync();
        }
    }
}
