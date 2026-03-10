using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
            MatchText = (PronTargetText.Text ?? string.Empty).Trim(),
            PhonemeText = (PronPhonemeText.Text ?? string.Empty).Trim(),
            Scope = scope,
            AccentGroup = accentGroup,
            WholeWord = PronRuleWholeWord.IsChecked ?? true,
            CaseSensitive = PronRuleCaseSensitive.IsChecked ?? false,
            Enabled = PronRuleEnabled.IsChecked ?? true,
            Priority = 100,
            Notes = PronRuleNotes.Text ?? string.Empty
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

            PronunciationRuleStore.UpsertRule(entry);
            ReloadPronunciationProcessor();

            PronRuleStatus.Text = $"Saved rule to {PronunciationRuleStore.GetRulesFilePath()}";
            SessionStatus.Text = "Pronunciation rule saved.";

            await System.Threading.Tasks.Task.CompletedTask;
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

    private void ReloadPronunciationProcessor()
    {
        AppServices.SetPronunciationProcessor(
            new DialoguePronunciationProcessor(
                WowPronunciationRules.CreateDefault()
                    .Concat(PronunciationRuleStore.LoadUserRules())
                    .ToList()));
    }

    private void OnPronunciationOpenRulesFileClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = PronunciationRuleStore.GetRulesFilePath();
            var dir = System.IO.Path.GetDirectoryName(path);

            if (!string.IsNullOrWhiteSpace(dir))
                System.IO.Directory.CreateDirectory(dir);

            if (!System.IO.File.Exists(path))
                PronunciationRuleStore.SaveRuleFile(PronunciationRuleStore.LoadRuleFile());

            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });

            PronRuleStatus.Text = $"Opened {path}";
        }
        catch (Exception ex)
        {
            PronRuleStatus.Text = $"Open failed: {ex.Message}";
        }
    }

    private void OnPronunciationReloadRulesClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            ReloadPronunciationProcessor();
            UpdatePronunciationPreview();
            PronRuleStatus.Text = "Reloaded rules from config/pronunciation-rules.json.";
        }
        catch (Exception ex)
        {
            PronRuleStatus.Text = $"Reload failed: {ex.Message}";
        }
    }
}