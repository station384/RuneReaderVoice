using System;
using System.Linq;
using System.Threading.Tasks;
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
            SessionStatus.Text = "Pronunciation rule saved.";
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
    {
        await ReloadPronunciationProcessorAsync();
    }

    private async Task ReloadPronunciationProcessorAsync()
    {
        var userRules = await AppServices.PronunciationRules.LoadUserRulesAsync();
        AppServices.SetPronunciationProcessor(
            new DialoguePronunciationProcessor(
                WowPronunciationRules.CreateDefault()
                    .Concat(userRules)
                    .ToList()));
    }

    private void OnPronunciationOpenRulesFileClicked(object? sender, RoutedEventArgs e)
    {
        PronRuleStatus.Text = "Pronunciation rules are now stored in runereader-voice.db.";
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
}
