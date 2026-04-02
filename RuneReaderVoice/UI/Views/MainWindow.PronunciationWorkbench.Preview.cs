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
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RuneReaderVoice.TTS.Pronunciation;

namespace RuneReaderVoice.UI.Views;
// MainWindow.PronunciationWorkbench.Preview.cs
// Preview text handling for the pronunciation workbench.
public partial class MainWindow
{
    private void UpdatePronunciationPreview()
    {
        var original = PronTestSentence.Text ?? string.Empty;
        var processed = PronunciationWorkbenchHelper.BuildPreview(
            original,
            PronTargetText.Text ?? string.Empty,
            PronPhonemeText.Text ?? string.Empty);

        PronOriginalPreview.Text = string.IsNullOrWhiteSpace(original) ? "—" : original;
        PronProcessedPreview.Text = string.IsNullOrWhiteSpace(processed) ? "—" : processed;
    }

    private async void OnPronunciationCopyPreviewClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard == null)
                return;

            await topLevel.Clipboard.SetTextAsync(PronProcessedPreview.Text ?? string.Empty);
            SessionStatus.Text = "Pronunciation preview copied to clipboard.";
        }
        catch (Exception ex)
        {
            SessionStatus.Text = $"Clipboard copy failed: {ex.Message}";
        }
    }

    private void OnPronunciationClearClicked(object? sender, RoutedEventArgs e)
    {
        PronTestSentence.Text = string.Empty;
        PronTargetText.Text = string.Empty;
        PronPhonemeText.Text = string.Empty;
        PronRuleNotes.Text = string.Empty;

        PronSymbolTitle.Text = "Select a sound symbol";
        PronSymbolDescription.Text = "Descriptions and examples appear here.";
        PronSymbolExample.Text = string.Empty;
        PronRuleStatus.Text = "Rules save into the local SQLite database.";

        SavePronunciationWorkbenchState();
        UpdatePronunciationPreview();
    }
}