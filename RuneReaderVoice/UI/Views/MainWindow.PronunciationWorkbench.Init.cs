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
using System.Linq;
using Avalonia.Controls;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Pronunciation;

namespace RuneReaderVoice.UI.Views;
// MainWindow.PronunciationWorkbench.Init.cs
// Pronunciation workbench initialization and persisted UI state restore.
public partial class MainWindow
{
    private void PopulatePronunciationWorkbench()
    {
        _pronunciationUiInitializing = true;

        PopulateWorkbenchRaces();
        PopulateWorkbenchGender();
        PopulateWorkbenchInputs();
        PopulateRuleEditors();
        PopulatePronunciationSymbolCatalog();

        _pronunciationUiInitializing = false;
        UpdatePronunciationPreview();
        UpdatePronunciationRuleUi();
        _ = ReloadPronunciationRuleListAsync();
    }

    private void PopulateWorkbenchRaces()
    {
        PronRaceSelector.Items.Clear();

        foreach (var row in AppServices.NpcPeopleCatalog.GetEnabledRows())
        {
            PronRaceSelector.Items.Add(new ComboBoxItem
            {
                Content = row.DisplayName,
                Tag = row.Id
            });
        }

        var savedCatalogId = VoiceSlot.NormalizeCatalogId(AppServices.Settings.PronunciationWorkbenchCatalogId);
        if (string.IsNullOrWhiteSpace(savedCatalogId))
            savedCatalogId = "troll";

        var catalogItem = PronRaceSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), savedCatalogId, StringComparison.OrdinalIgnoreCase));

        if (catalogItem != null)
            PronRaceSelector.SelectedItem = catalogItem;
        else
            PronRaceSelector.SelectedIndex = 0;
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

        PronRuleRaceSelector.Items.Clear();

        foreach (var row in AppServices.NpcPeopleCatalog.GetEnabledRows())
        {
            PronRuleRaceSelector.Items.Add(new ComboBoxItem
            {
                Content = row.DisplayName,
                Tag = row.Id
            });
        }

        var selectedWorkbenchCatalogId = ResolveWorkbenchCatalogId();

        var defaultRuleCatalogItem = PronRuleRaceSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals(i.Tag?.ToString(), selectedWorkbenchCatalogId, StringComparison.OrdinalIgnoreCase));

        PronRuleRaceSelector.SelectedItem =
            defaultRuleCatalogItem ??
            PronRuleRaceSelector.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void PopulatePronunciationSymbolCatalog()
    {
        PopulatePronunciationSymbolGroup(PronStressTimingGrid, PronunciationWorkbenchCatalog.StressTimingCategory);
        PopulatePronunciationSymbolGroup(PronDiphthongGrid, PronunciationWorkbenchCatalog.DiphthongCategory);
        PopulatePronunciationSymbolGroup(PronVowelGrid, PronunciationWorkbenchCatalog.VowelCategory);
        PopulatePronunciationSymbolGroup(PronConsonantGrid, PronunciationWorkbenchCatalog.ConsonantCategory);
    }
}