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
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Pronunciation;

namespace RuneReaderVoice.UI.Views;
// MainWindow.Pronunciation.cs
// Pronunciation tab wiring, rule persistence, and workbench actions.
public partial class MainWindow
{


    // private void PopulatePronunciationSymbolGroup(WrapPanel panel, string category)
    // {
    //     panel.Children.Clear();
    //     foreach (var symbol in PronunciationWorkbenchCatalog.GetByCategory(category))
    //     {
    //         var button = new Button
    //         {
    //             Content = symbol.ButtonLabel,
    //             Tag = symbol,
    //             MinWidth = 78,
    //             Height = 28,
    //             Margin = new Avalonia.Thickness(0, 0, 6, 6),
    //             Background = Avalonia.Media.SolidColorBrush.Parse("#0F3460"),
    //             Foreground = Avalonia.Media.Brushes.WhiteSmoke,
    //             FontSize = 13
    //         };
    //         Avalonia.Controls.ToolTip.SetTip(button, $"{symbol.Symbol} — {symbol.Name}\n{symbol.Description}\nExample: {symbol.Example}");
    //         button.Click += OnPronunciationSymbolClicked;
    //         panel.Children.Add(button);
    //     }
    // }
    //
    // private void OnPronunciationSymbolClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    // {
    //     if (sender is not Button { Tag: PronunciationSymbol symbol })
    //         return;
    //
    //     PronSymbolTitle.Text = $"{symbol.Symbol}  —  {symbol.Name} ({symbol.Category})";
    //     PronSymbolDescription.Text = symbol.Description;
    //     PronSymbolExample.Text = $"Example: {symbol.Example}";
    //
    //     var insertText = symbol.Symbol;
    //     var tb = PronPhonemeText;
    //     var existing = tb.Text ?? string.Empty;
    //     var caret = tb.CaretIndex;
    //     if (caret < 0 || caret > existing.Length)
    //         caret = existing.Length;
    //
    //     tb.Text = existing.Insert(caret, insertText);
    //     tb.CaretIndex = caret + insertText.Length;
    //     SavePronunciationWorkbenchState();
    //     UpdatePronunciationPreview();
    //     tb.Focus();
    // }
    //
    // private void OnPronunciationInputChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    // {
    //     if (_pronunciationUiInitializing)
    //         return;
    //
    //     SavePronunciationWorkbenchState();
    //     UpdatePronunciationPreview();
    // }
    //
    // private void SavePronunciationWorkbenchState()
    // {
    //     AppServices.Settings.PronunciationWorkbenchTestSentence = PronTestSentence.Text ?? string.Empty;
    //     AppServices.Settings.PronunciationWorkbenchTargetText = PronTargetText.Text ?? string.Empty;
    //     AppServices.Settings.PronunciationWorkbenchPhonemeText = PronPhonemeText.Text ?? string.Empty;
    //     AppServices.Settings.PronunciationWorkbenchAccentGroup = ResolveWorkbenchGroup().ToString();
    //     AppServices.Settings.PronunciationWorkbenchGender = ResolveWorkbenchGenderTag();
    //     VoiceSettingsManager.SaveSettings(AppServices.Settings);
    // }
    //
    // private void UpdatePronunciationPreview()
    // {
    //     var original = PronTestSentence.Text ?? string.Empty;
    //     var processed = PronunciationWorkbenchHelper.BuildPreview(
    //         original,
    //         PronTargetText.Text ?? string.Empty,
    //         PronPhonemeText.Text ?? string.Empty);
    //
    //     PronOriginalPreview.Text = string.IsNullOrWhiteSpace(original) ? "—" : original;
    //     PronProcessedPreview.Text = string.IsNullOrWhiteSpace(processed) ? "—" : processed;
    // }
    //
    // private AccentGroup ResolveWorkbenchGroup()
    // {
    //     if (PronAccentGroupSelector.SelectedItem is ComboBoxItem { Tag: AccentGroup group })
    //         return group;
    //     return AccentGroup.Narrator;
    // }
    //
    // private string ResolveWorkbenchGenderTag()
    //     => PronGenderSelector.SelectedItem is ComboBoxItem item
    //         ? item.Tag?.ToString() ?? "Male"
    //         : "Male";
    //
    // private VoiceSlot ResolveWorkbenchSlot()
    // {
    //     var group = ResolveWorkbenchGroup();
    //     var tag = ResolveWorkbenchGenderTag();
    //
    //     if (group == AccentGroup.Narrator || string.Equals(tag, "Narrator", StringComparison.OrdinalIgnoreCase))
    //         return VoiceSlot.Narrator;
    //
    //     var gender = string.Equals(tag, "Female", StringComparison.OrdinalIgnoreCase)
    //         ? Gender.Female
    //         : Gender.Male;
    //
    //     return new VoiceSlot(group, gender);
    // }
    //
    //
    //
    //
    //
    //
    //
    // private async void OnPronunciationCopyPreviewClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    // {
    //     try
    //     {
    //         var topLevel = TopLevel.GetTopLevel(this);
    //         if (topLevel?.Clipboard != null)
    //         {
    //             await topLevel.Clipboard.SetTextAsync(PronProcessedPreview.Text ?? string.Empty);
    //             SessionStatus.Text = "Pronunciation preview copied to clipboard.";
    //         }
    //     }
    //     catch (Exception ex)
    //     {
    //         SessionStatus.Text = $"Clipboard copy failed: {ex.Message}";
    //     }
    // }
    //
    // private void OnPronunciationClearClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    // {
    //     PronTestSentence.Text = string.Empty;
    //     PronTargetText.Text = string.Empty;
    //     PronPhonemeText.Text = string.Empty;
    //     PronRuleNotes.Text = string.Empty;
    //     PronSymbolTitle.Text = "Select a sound symbol";
    //     PronSymbolDescription.Text = "Descriptions and examples appear here.";
    //     PronSymbolExample.Text = string.Empty;
    //     PronRuleStatus.Text = "Rules save into the local SQLite database.";
    //     SavePronunciationWorkbenchState();
    //     UpdatePronunciationPreview();
    // }
    //
    // private void OnPronunciationRuleInputChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    // {
    //     if (_pronunciationUiInitializing)
    //         return;
    //
    //     UpdatePronunciationRuleUi();
    // }
    //
    // private void UpdatePronunciationRuleUi()
    // {
    //     var scope = ResolveRuleScopeTag();
    //     PronRuleAccentGroupSelector.IsEnabled = string.Equals(scope, "AccentGroup", StringComparison.OrdinalIgnoreCase);
    // }
    //
    // private string ResolveRuleScopeTag()
    //     => PronRuleScopeSelector.SelectedItem is ComboBoxItem item
    //         ? item.Tag?.ToString() ?? "Global"
    //         : "Global";
    //
    // private AccentGroup ResolveRuleAccentGroup()
    // {
    //     if (PronRuleAccentGroupSelector.SelectedItem is ComboBoxItem { Tag: AccentGroup group })
    //         return group;
    //     return ResolveWorkbenchGroup();
    // }
    //
    // private PronunciationRuleEntry BuildWorkbenchRuleEntry()
    // {
    //     var scope = ResolveRuleScopeTag();
    //     var accentGroup = string.Equals(scope, "AccentGroup", StringComparison.OrdinalIgnoreCase)
    //         ? ResolveRuleAccentGroup().ToString()
    //         : null;
    //
    //     return new PronunciationRuleEntry
    //     {
    //         MatchText = (PronTargetText.Text ?? string.Empty).Trim(),
    //         PhonemeText = (PronPhonemeText.Text ?? string.Empty).Trim(),
    //         Scope = scope,
    //         AccentGroup = accentGroup,
    //         WholeWord = PronRuleWholeWord.IsChecked ?? true,
    //         CaseSensitive = PronRuleCaseSensitive.IsChecked ?? false,
    //         Enabled = PronRuleEnabled.IsChecked ?? true,
    //         Priority = 100,
    //         Notes = PronRuleNotes.Text ?? string.Empty,
    //     };
    // }
    //
    // private async void OnPronunciationSaveRuleClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    // {
    //     try
    //     {
    //         var entry = BuildWorkbenchRuleEntry();
    //         if (string.IsNullOrWhiteSpace(entry.MatchText))
    //         {
    //             PronRuleStatus.Text = "Enter a word or phrase to replace before saving.";
    //             return;
    //         }
    //         if (string.IsNullOrWhiteSpace(entry.PhonemeText))
    //         {
    //             PronRuleStatus.Text = "Enter phoneme sounds before saving.";
    //             return;
    //         }
    //
    //         PronSaveRuleButton.IsEnabled = false;
    //         PronunciationRuleStore.UpsertRule(entry);
    //         AppServices.SetPronunciationProcessor(new DialoguePronunciationProcessor(
    //             WowPronunciationRules.CreateDefault().Concat(PronunciationRuleStore.LoadUserRules()).ToList()));
    //
    //         PronRuleStatus.Text = $"Saved rule to {PronunciationRuleStore.GetRulesFilePath()}";
    //         SessionStatus.Text = "Pronunciation rule saved.";
    //         await System.Threading.Tasks.Task.CompletedTask;
    //     }
    //     catch (Exception ex)
    //     {
    //         PronRuleStatus.Text = $"Save failed: {ex.Message}";
    //     }
    //     finally
    //     {
    //         PronSaveRuleButton.IsEnabled = true;
    //     }
    // }
    //
    // private void OnPronunciationOpenRulesFileClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    // {
    //     try
    //     {
    //         var path = PronunciationRuleStore.GetRulesFilePath();
    //         var dir = System.IO.Path.GetDirectoryName(path);
    //         if (!string.IsNullOrWhiteSpace(dir))
    //             System.IO.Directory.CreateDirectory(dir);
    //
    //         if (!System.IO.File.Exists(path))
    //             PronunciationRuleStore.SaveRuleFile(PronunciationRuleStore.LoadRuleFile());
    //
    //         Process.Start(new ProcessStartInfo
    //         {
    //             FileName = path,
    //             UseShellExecute = true
    //         });
    //
    //         PronRuleStatus.Text = $"Opened {path}";
    //     }
    //     catch (Exception ex)
    //     {
    //         PronRuleStatus.Text = $"Open failed: {ex.Message}";
    //     }
    // }
    //
    // private void OnPronunciationReloadRulesClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    // {
    //     try
    //     {
    //         AppServices.SetPronunciationProcessor(new DialoguePronunciationProcessor(
    //             WowPronunciationRules.CreateDefault().Concat(PronunciationRuleStore.LoadUserRules()).ToList()));
    //         UpdatePronunciationPreview();
    //         PronRuleStatus.Text = "Reloaded rules from config/pronunciation-rules.json.";
    //     }
    //     catch (Exception ex)
    //     {
    //         PronRuleStatus.Text = $"Reload failed: {ex.Message}";
    //     }
    // }
}