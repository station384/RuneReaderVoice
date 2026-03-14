using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Pronunciation;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private void OnPronunciationRuleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pronunciationUiInitializing)
            return;

        UpdatePronunciationRuleUi();
    }

    private void OnPronunciationRuleTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_pronunciationUiInitializing)
            return;

        UpdatePronunciationRuleUi();
    }

    private void OnPronunciationRuleClickChanged(object? sender, RoutedEventArgs e)
    {
        if (_pronunciationUiInitializing)
            return;

        UpdatePronunciationRuleUi();
    }

    private void UpdatePronunciationRuleUi()
    {
        var scope = ResolveRuleScopeTag();
        PronRuleAccentGroupSelector.IsEnabled =
            string.Equals(scope, "AccentGroup", StringComparison.OrdinalIgnoreCase);
    }

    private string ResolveRuleScopeTag()
    {
        return PronRuleScopeSelector.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? "Global"
            : "Global";
    }

    private AccentGroup ResolveRuleAccentGroup()
    {
        if (PronRuleAccentGroupSelector.SelectedItem is ComboBoxItem { Tag: AccentGroup group })
            return group;

        return ResolveWorkbenchGroup();
    }

    private PronunciationRuleEntry BuildWorkbenchRuleEntry()
    {
        var scope = ResolveRuleScopeTag();
        var accentGroup = string.Equals(scope, "AccentGroup", StringComparison.OrdinalIgnoreCase)
            ? ResolveRuleAccentGroup().ToString()
            : null;

        return new PronunciationRuleEntry
        {
            MatchText     = (PronTargetText.Text ?? string.Empty).Trim(),
            PhonemeText   = (PronPhonemeText.Text ?? string.Empty).Trim(),
            Scope         = scope,
            AccentGroup   = accentGroup,
            WholeWord     = PronRuleWholeWord.IsChecked ?? true,
            CaseSensitive = PronRuleCaseSensitive.IsChecked ?? false,
            Enabled       = PronRuleEnabled.IsChecked ?? true,
            Priority      = 100,
            Notes         = PronRuleNotes.Text ?? string.Empty
        };
    }

    private async void OnPronunciationSaveRuleClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var entry = BuildWorkbenchRuleEntry();
            if (!ValidateRuleEntry(entry))
                return;

            PronSaveRuleButton.IsEnabled = false;

            await AppServices.PronunciationRules.UpsertRuleAsync(entry);
            await ReloadPronunciationProcessorAsync();

            PronRuleStatus.Text = "Saved pronunciation rule.";
            SessionStatus.Text  = "Pronunciation rule saved.";
        }
        catch (Exception ex)
        {
            PronRuleStatus.Text = $"Save failed: {ex.Message}";
        }
        finally
        {
            PronSaveRuleButton.IsEnabled = true;
        }
    }

    private bool ValidateRuleEntry(PronunciationRuleEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.MatchText))
        {
            PronRuleStatus.Text = "Enter a word or phrase to replace before saving.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(entry.PhonemeText))
        {
            PronRuleStatus.Text = "Enter phoneme sounds before saving.";
            return false;
        }

        return true;
    }

    private async void ReloadPronunciationProcessorAsync(object? sender, RoutedEventArgs e)
        => await ReloadPronunciationProcessorAsync();

    private async Task ReloadPronunciationProcessorAsync()
    {
        var userRules = await AppServices.PronunciationRules.LoadUserRulesAsync();
        AppServices.SetPronunciationProcessor(
            new DialoguePronunciationProcessor(
                WowPronunciationRules.CreateDefault()
                    .Concat(userRules)
                    .ToList()));
    }

    private async void OnPronunciationReloadRulesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            await ReloadPronunciationProcessorAsync();
            UpdatePronunciationPreview();
            PronRuleStatus.Text = "Reloaded pronunciation rules from database.";
        }
        catch (Exception ex)
        {
            PronRuleStatus.Text = $"Reload failed: {ex.Message}";
        }
    }

    // ── Import / Export ───────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonPronExportOptions = new() { WriteIndented = true };

    private async void OnPronunciationExportRulesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var entries = await AppServices.PronunciationRules.GetAllEntriesAsync();
            var file = new PronunciationRuleFile { Rules = entries };
            var json = JsonSerializer.Serialize(file, _jsonPronExportOptions);

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var path = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = "Export Pronunciation Rules",
                SuggestedFileName = "pronunciation-rules.json",
                DefaultExtension  = "json",
                FileTypeChoices   = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });

            if (path == null) return;

            await File.WriteAllTextAsync(path.Path.LocalPath, json);
            PronRuleStatus.Text = $"Exported {entries.Count} rules.";
        }
        catch (Exception ex)
        {
            PronRuleStatus.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void OnPronunciationImportRulesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title          = "Import Pronunciation Rules",
                AllowMultiple  = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });

            if (files.Count == 0) return;

            var json = await File.ReadAllTextAsync(files[0].Path.LocalPath);
            var file = JsonSerializer.Deserialize<PronunciationRuleFile>(json);
            if (file?.Rules == null || file.Rules.Count == 0)
            {
                PronRuleStatus.Text = "No rules found in file.";
                return;
            }

            foreach (var entry in file.Rules)
                await AppServices.PronunciationRules.UpsertRuleAsync(entry);

            await ReloadPronunciationProcessorAsync();
            UpdatePronunciationPreview();
            PronRuleStatus.Text = $"Imported {file.Rules.Count} rules.";
        }
        catch (Exception ex)
        {
            PronRuleStatus.Text = $"Import failed: {ex.Message}";
        }
    }

    // ── Removed: OnPronunciationOpenRulesFileClicked (rules now stored in DB) ─
}
