using System;
using System.Linq;
using Avalonia.Controls;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Pronunciation;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    private void PopulatePronunciationWorkbench()
    {
        _pronunciationUiInitializing = true;

        PopulateWorkbenchAccentGroups();
        PopulateWorkbenchGender();
        PopulateWorkbenchInputs();
        PopulateRuleEditors();
        PopulatePronunciationSymbolCatalog();

        _pronunciationUiInitializing = false;
        UpdatePronunciationPreview();
        UpdatePronunciationRuleUi();
        _ = ReloadPronunciationRuleListAsync();
    }

    private void PopulateWorkbenchAccentGroups()
    {
        PronAccentGroupSelector.Items.Clear();

        foreach (AccentGroup group in Enum.GetValues<AccentGroup>())
        {
            PronAccentGroupSelector.Items.Add(new ComboBoxItem
            {
                Content = group.ToString(),
                Tag = group
            });
        }

        var savedGroup = Enum.TryParse<AccentGroup>(
            AppServices.Settings.PronunciationWorkbenchAccentGroup,
            out var parsedGroup)
            ? parsedGroup
            : AccentGroup.Troll;

        var groupItem = PronAccentGroupSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag is AccentGroup g && g == savedGroup);

        if (groupItem != null)
            PronAccentGroupSelector.SelectedItem = groupItem;
        else
            PronAccentGroupSelector.SelectedIndex = 0;
    }

    private void PopulateWorkbenchGender()
    {
        var savedGender = AppServices.Settings.PronunciationWorkbenchGender;

        var genderItem = PronGenderSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i =>
                string.Equals(i.Tag?.ToString(), savedGender, StringComparison.OrdinalIgnoreCase));

        if (genderItem != null)
            PronGenderSelector.SelectedItem = genderItem;
        else
            PronGenderSelector.SelectedIndex = 0;
    }

    private void PopulateWorkbenchInputs()
    {
        PronTestSentence.Text = AppServices.Settings.PronunciationWorkbenchTestSentence;
        PronTargetText.Text = AppServices.Settings.PronunciationWorkbenchTargetText;
        PronPhonemeText.Text = AppServices.Settings.PronunciationWorkbenchPhonemeText;
    }

    private void PopulateRuleEditors()
    {
        PronRuleScopeSelector.SelectedIndex = 0;

        PronRuleAccentGroupSelector.Items.Clear();

        foreach (AccentGroup group in Enum.GetValues<AccentGroup>())
        {
            PronRuleAccentGroupSelector.Items.Add(new ComboBoxItem
            {
                Content = group.ToString(),
                Tag = group
            });
        }

        var selectedWorkbenchGroup = ResolveWorkbenchGroup();

        var defaultRuleGroupItem = PronRuleAccentGroupSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag is AccentGroup g && g == selectedWorkbenchGroup);

        PronRuleAccentGroupSelector.SelectedItem =
            defaultRuleGroupItem ??
            PronRuleAccentGroupSelector.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void PopulatePronunciationSymbolCatalog()
    {
        PopulatePronunciationSymbolGroup(PronStressTimingGrid, PronunciationWorkbenchCatalog.StressTimingCategory);
        PopulatePronunciationSymbolGroup(PronDiphthongGrid, PronunciationWorkbenchCatalog.DiphthongCategory);
        PopulatePronunciationSymbolGroup(PronVowelGrid, PronunciationWorkbenchCatalog.VowelCategory);
        PopulatePronunciationSymbolGroup(PronConsonantGrid, PronunciationWorkbenchCatalog.ConsonantCategory);
    }
}