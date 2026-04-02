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
using Avalonia.Controls;
using Avalonia.Interactivity;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Pronunciation;

namespace RuneReaderVoice.UI.Views;
// MainWindow.PronunciationWorkbench.State.cs
// Persisted state helpers for the pronunciation workbench.
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