using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Pronunciation;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
    
    
    private void OnPronunciationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_pronunciationUiInitializing)
            return;

        SavePronunciationWorkbenchState();
        UpdatePronunciationPreview();
    }

    private void OnPronunciationTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_pronunciationUiInitializing)
            return;

        SavePronunciationWorkbenchState();
        UpdatePronunciationPreview();
    }

    private void SavePronunciationWorkbenchState()
    {
        AppServices.Settings.PronunciationWorkbenchTestSentence = PronTestSentence.Text ?? string.Empty;
        AppServices.Settings.PronunciationWorkbenchTargetText = PronTargetText.Text ?? string.Empty;
        AppServices.Settings.PronunciationWorkbenchPhonemeText = PronPhonemeText.Text ?? string.Empty;
        AppServices.Settings.PronunciationWorkbenchAccentGroup = ResolveWorkbenchGroup().ToString();
        AppServices.Settings.PronunciationWorkbenchGender = ResolveWorkbenchGenderTag();

        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private AccentGroup ResolveWorkbenchGroup()
    {
        if (PronAccentGroupSelector.SelectedItem is ComboBoxItem { Tag: AccentGroup group })
            return group;

        return AccentGroup.Narrator;
    }

    private string ResolveWorkbenchGenderTag()
    {
        return PronGenderSelector.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString() ?? "Male"
            : "Male";
    }

    private VoiceSlot ResolveWorkbenchSlot()
    {
        var group = ResolveWorkbenchGroup();
        var tag = ResolveWorkbenchGenderTag();

        if (group == AccentGroup.Narrator ||
            string.Equals(tag, "Narrator", StringComparison.OrdinalIgnoreCase))
        {
            return VoiceSlot.Narrator;
        }

        var gender = string.Equals(tag, "Female", StringComparison.OrdinalIgnoreCase)
            ? Gender.Female
            : Gender.Male;

        return new VoiceSlot(group, gender);
    }
}