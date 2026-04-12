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
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using RuneReaderVoice.Data;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.UI.Views;
// MainWindow.NpcOverrides.cs
// "Last NPC" panel (updates after each dialog) and NPC Overrides CRUD tab.
public partial class MainWindow
{
    // ── State ─────────────────────────────────────────────────────────────────

    // NpcId of the NPC shown in the Last NPC panel. 0 = no NPC (narrator/book).
    private int _lastNpcId;

    // ── Initialization ────────────────────────────────────────────────────────

    private void InitNpcOverridesUI()
    {
        // Wire the assembler segment event to update the Last NPC panel.
        AppServices.Assembler.OnSegmentComplete += seg =>
            Dispatcher.UIThread.Post(() => OnSegmentCompletedForNpcPanel(seg));

        PopulateNpcOverrideRaceDropdown(LastNpcRaceDropdown);
        PopulateLastNpcSampleDropdown();
        RefreshNpcOverridesGrid();
    }

    // Known variant suffixes — must match server-side variant names.
    // These appear after a dash in the sample ID e.g. M_WWZ_10-slow.
    private static readonly string[] _variantSuffixes =
        { "slow", "fast", "quiet", "loud", "breathy" };

    /// <summary>
    /// Returns the base sample ID by stripping known variant suffixes.
    /// "M_WWZ_10-slow" → "M_WWZ_10", "M_WWZ_10" → "M_WWZ_10"
    /// </summary>
    private static string GetBaseSampleId(string sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId)) return sampleId;
        foreach (var suffix in _variantSuffixes)
        {
            if (sampleId.EndsWith($"-{suffix}", StringComparison.OrdinalIgnoreCase))
                return sampleId[..^(suffix.Length + 1)];
        }
        return sampleId;
    }

    /// <summary>
    /// Returns the variant suffix part of a sample ID, or null if it is a base sample.
    /// "M_WWZ_10-slow" → "slow", "M_WWZ_10" → null
    /// </summary>
    private static string? GetVariantSuffix(string sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId)) return null;
        foreach (var suffix in _variantSuffixes)
        {
            if (sampleId.EndsWith($"-{suffix}", StringComparison.OrdinalIgnoreCase))
                return suffix;
        }
        return null;
    }

    private bool ShouldShowLastNpcSamplePanel()
    {
        var descriptor = AppServices.ProviderRegistry.Get(AppServices.Provider.ProviderId);
        if (descriptor == null)
            return false;

        if (descriptor.TransportKind != TTS.Providers.ProviderTransportKind.Remote)
            return false;

        if (descriptor.SupportsVoiceMatching)
            return true;

        var remoteId = descriptor.RemoteProviderId ?? string.Empty;
        return remoteId.Contains("chatterbox", StringComparison.OrdinalIgnoreCase)
            || remoteId.Contains("f5", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Populates the two-level sample picker (base sample + variant dropdowns).
    /// Dropdown 1: distinct base sample IDs.
    /// Dropdown 2: variants available for the selected base.
    /// Only visible when provider supports voice matching.
    /// </summary>
    private void PopulateLastNpcSampleDropdown()
    {
        LastNpcSampleDropdown.SelectionChanged -= OnBaseSampleSelectionChanged;
        LastNpcSampleDropdown.Items.Clear();
        LastNpcVariantDropdown.Items.Clear();

        LastNpcSampleDropdown.Items.Add(new ComboBoxItem
        {
            Content = "(race default)",
            Tag     = (string?)null,
        });

        var showPanel = ShouldShowLastNpcSamplePanel();

        if (AppServices.Provider is TTS.Providers.RemoteTtsProvider remoteProvider)
        {
            var voices = remoteProvider.GetAvailableVoices();
            System.Diagnostics.Debug.WriteLine(
                $"[NpcPanel] PopulateSampleDropdown: provider={AppServices.Provider.ProviderId} voiceCount={voices.Count} showPanel={showPanel}");

            // Separate base samples from variants
            var baseSamples = SelectionRecencyHelper.SortByVoiceRecency(
                    voices.Where(v => GetVariantSuffix(v.VoiceId) == null),
                    AppServices.Settings,
                    AppServices.Provider.ProviderId,
                    v => v.VoiceId,
                    v => v.VoiceId,
                    bespokeOnly: true)
                .ToList();

            foreach (var v in baseSamples)
            {
                LastNpcSampleDropdown.Items.Add(new ComboBoxItem
                {
                    Content = SelectionRecencyHelper.GetVoiceDisplayLabel(v.VoiceId),
                    Tag     = v.VoiceId,
                });
            }

            LastNpcSamplePanel.IsVisible = showPanel;
            System.Diagnostics.Debug.WriteLine(
                $"[NpcPanel] SamplePanel.IsVisible={LastNpcSamplePanel.IsVisible}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine(
                $"[NpcPanel] PopulateSampleDropdown: provider={AppServices.Provider?.ProviderId} is NOT RemoteTtsProvider — hiding panel");
            LastNpcSamplePanel.IsVisible = false;
        }

        LastNpcSampleDropdown.SelectedIndex = 0;
        LastNpcSampleDropdown.SelectionChanged += OnBaseSampleSelectionChanged;
        PopulateVariantDropdown(null);
    }

    /// <summary>
    /// Repopulates the variant dropdown for the currently selected base sample.
    /// </summary>
    private void PopulateVariantDropdown(string? baseSampleId)
    {
        LastNpcVariantDropdown.SelectionChanged -= OnVariantSelectionChanged;
        LastNpcVariantDropdown.Items.Clear();

        LastNpcVariantDropdown.Items.Add(new ComboBoxItem
        {
            Content = "(default)",
            Tag     = baseSampleId,  // base ID = no variant
        });

        if (!string.IsNullOrWhiteSpace(baseSampleId) &&
            AppServices.Provider is TTS.Providers.RemoteTtsProvider remoteProvider)
        {
            var allVoices = remoteProvider.GetAvailableVoices();
            var variants  = SelectionRecencyHelper.SortByVoiceRecency(
                    allVoices.Where(v => GetBaseSampleId(v.VoiceId) == baseSampleId
                                      && GetVariantSuffix(v.VoiceId) != null),
                    AppServices.Settings,
                    AppServices.Provider.ProviderId,
                    v => v.VoiceId,
                    v => v.VoiceId)
                .ToList();

            foreach (var v in variants)
            {
                var suffix = GetVariantSuffix(v.VoiceId) ?? v.VoiceId;
                LastNpcVariantDropdown.Items.Add(new ComboBoxItem
                {
                    Content = suffix,
                    Tag     = v.VoiceId,   // full ID including variant suffix
                });
            }

            LastNpcVariantDropdown.IsVisible = variants.Count > 0;
        }
        else
        {
            LastNpcVariantDropdown.IsVisible = false;
        }

        LastNpcVariantDropdown.SelectedIndex = 0;
        LastNpcVariantDropdown.SelectionChanged += OnVariantSelectionChanged;
    }

    private void OnBaseSampleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var baseSampleId = (LastNpcSampleDropdown.SelectedItem as ComboBoxItem)?.Tag as string;
        PopulateVariantDropdown(baseSampleId);
    }

    private void OnVariantSelectionChanged(object? sender, SelectionChangedEventArgs e) { }

    // ── Last NPC panel ────────────────────────────────────────────────────────

    private void OnSegmentCompletedForNpcPanel(Session.AssembledSegment seg)
    {
        // NpcId=0 means narrator text / book — no NPC to assign.
        if (seg.NpcId == 0)
            return;

        _lastNpcId = seg.NpcId;
        LastNpcIdLabel.Text = $"NPC ID: {seg.NpcId}";
        LastNpcPanel.IsVisible = true;

        // Refresh sample dropdown — voice list may have loaded since init
        PopulateLastNpcSampleDropdown();

        // Pre-fill existing override if one exists, otherwise clear.
        LoadExistingOverrideIntoPanel(seg.NpcId);
    }

    private void LoadExistingOverrideIntoPanel(int npcId)
    {
        _ = Task.Run(async () =>
        {
            var existing = await AppServices.NpcOverrides.GetOverrideAsync(npcId);
            Dispatcher.UIThread.Post(() =>
            {
                LastNpcNotesBox.Text = existing?.Notes ?? string.Empty;

                if (existing != null)
                    SelectDropdownByRaceId(LastNpcRaceDropdown, existing.RaceId);
                else
                    LastNpcRaceDropdown.SelectedIndex = 0;

                // Bespoke sample — select existing or reset to "(no bespoke sample)"
                SelectLastNpcSampleDropdown(existing?.BespokeSampleId);

                LastNpcSaveButton.IsEnabled  = true;
                LastNpcClearButton.IsEnabled = existing != null;
            });
        });
    }

    private void OnLastNpcSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_lastNpcId == 0) return;
        if (LastNpcRaceDropdown.SelectedItem is not ComboBoxItem item) return;

        var raceId          = (int)(item.Tag ?? 0);
        var notes           = LastNpcNotesBox.Text?.Trim();
        var bespokeSampleId = GetSelectedLastNpcSampleId();
        SelectionRecencyHelper.BumpRace(AppServices.Settings, raceId);
        SelectionRecencyHelper.BumpVoice(AppServices.Settings, AppServices.Provider.ProviderId, bespokeSampleId);
        VoiceSettingsManager.SaveSettings(AppServices.Settings);

        _ = Task.Run(async () =>
        {
            await AppServices.NpcOverrides.UpsertAsync(
                _lastNpcId, raceId, notes,
                bespokeSampleId: bespokeSampleId);

            AppServices.Assembler.ApplyRaceOverride(
                _lastNpcId, raceId,
                bespokeSampleId: bespokeSampleId);

            // Contribute to server if enabled
            if (AppServices.Settings.ContributeByDefault)
            {
                var entry = await AppServices.NpcOverrides.GetOverrideAsync(_lastNpcId);
                if (entry != null)
                    AppServices.NpcSync.ContributeIfEnabled(entry);
            }

            Dispatcher.UIThread.Post(() =>
            {
                PopulateNpcOverrideRaceDropdown(LastNpcRaceDropdown);
                SelectDropdownByRaceId(LastNpcRaceDropdown, raceId);

                PopulateLastNpcSampleDropdown();
                SelectLastNpcSampleDropdown(bespokeSampleId);

                LastNpcClearButton.IsEnabled = true;
                RefreshNpcOverridesGrid();
            });
        });
    }

    private void OnLastNpcClearClicked(object? sender, RoutedEventArgs e)
    {
        if (_lastNpcId == 0) return;

        _ = Task.Run(async () =>
        {
            await AppServices.NpcOverrides.DeleteAsync(_lastNpcId);
            AppServices.Assembler.RemoveRaceOverride(_lastNpcId);

            Dispatcher.UIThread.Post(() =>
            {
                LastNpcRaceDropdown.SelectedIndex = 0;
                LastNpcNotesBox.Text = string.Empty;
                LastNpcClearButton.IsEnabled = false;
                RefreshNpcOverridesGrid();
            });
        });
    }

    // ── NPC Overrides CRUD tab ────────────────────────────────────────────────

    private void RefreshNpcOverridesGrid()
    {
        _ = Task.Run(async () =>
        {
            var entries = await AppServices.NpcOverrides.GetAllAsync();
            Dispatcher.UIThread.Post(() => RenderNpcOverridesGrid(entries));
        });
    }

    private void RenderNpcOverridesGrid(IReadOnlyList<NpcRaceOverride> entries)
    {
        NpcOverridesGrid.Children.Clear();
        NpcOverridesGrid.RowDefinitions.Clear();

        // Header row
        NpcOverridesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddOverrideHeaderRow();

        if (entries.Count == 0)
        {
            NpcOverridesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var empty = new TextBlock
            {
                Text       = "No overrides saved yet. Play a dialog and use the Last NPC panel to add one.",
                Foreground = Brushes.Gray,
                Margin     = new Avalonia.Thickness(4, 8, 4, 4),
            };
            Grid.SetRow(empty, 1);
            Grid.SetColumnSpan(empty, 5);
            NpcOverridesGrid.Children.Add(empty);
            return;
        }

        int row = 1;
        foreach (var entry in entries)
        {
            NpcOverridesGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddOverrideDataRow(entry, row++);
        }
    }

    private void AddOverrideHeaderRow()
    {
        var headers = new[] { "NPC ID", "Notes", "Race / Accent", "Source", "", "" };
        for (int col = 0; col < headers.Length; col++)
        {
            var tb = new TextBlock
            {
                Text       = headers[col],
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.LightGray,
                Margin     = new Avalonia.Thickness(4, 2, 4, 4),
            };
            Grid.SetRow(tb, 0);
            Grid.SetColumn(tb, col);
            NpcOverridesGrid.Children.Add(tb);
        }
    }

    private void AddOverrideDataRow(NpcRaceOverride entry, int rowIndex)
    {
        var isLocal = !entry.IsReadOnly;

        // NPC ID
        AddCell(entry.NpcId.ToString(), rowIndex, 0);

        // Notes (editable inline for local entries)
        if (isLocal)
        {
            var notesBox = new TextBox
            {
                Text              = entry.Notes ?? string.Empty,
                Watermark         = "optional label",
                FontSize          = 11,
                Margin            = new Avalonia.Thickness(4, 1, 4, 1),
                Background        = Brushes.Transparent,
                BorderBrush       = Brushes.DimGray,
            };
            notesBox.LostFocus += (_, _) =>
            {
                var notes = notesBox.Text?.Trim();
                _ = Task.Run(async () =>
                    await AppServices.NpcOverrides.UpsertAsync(entry.NpcId, entry.RaceId, notes));
            };
            Grid.SetRow(notesBox, rowIndex);
            Grid.SetColumn(notesBox, 1);
            NpcOverridesGrid.Children.Add(notesBox);
        }
        else
        {
            AddCell(entry.Notes ?? "—", rowIndex, 1, Brushes.Gray);
        }

        // Race dropdown (editable for local entries)
        if (isLocal)
        {
            var dropdown = new ComboBox { FontSize = 11, Margin = new Avalonia.Thickness(4, 1, 4, 1) };
            PopulateNpcOverrideRaceDropdown(dropdown);
            SelectDropdownByRaceId(dropdown, entry.RaceId);

            dropdown.SelectionChanged += (_, _) =>
            {
                if (dropdown.SelectedItem is not ComboBoxItem item) return;
                var raceId = (int)(item.Tag ?? 0);
                SelectionRecencyHelper.BumpRace(AppServices.Settings, raceId);
                VoiceSettingsManager.SaveSettings(AppServices.Settings);
                _ = Task.Run(async () =>
                {
                    await AppServices.NpcOverrides.UpsertAsync(entry.NpcId, raceId, entry.Notes);
                    AppServices.Assembler.ApplyRaceOverride(entry.NpcId, raceId);
                });
            };

            Grid.SetRow(dropdown, rowIndex);
            Grid.SetColumn(dropdown, 2);
            NpcOverridesGrid.Children.Add(dropdown);
        }
        else
        {
            AddCell($"{entry.AccentGroup}  (race {entry.RaceId})", rowIndex, 2, Brushes.Gray);
        }

        // Source badge
        var sourceBrush = entry.Source switch
        {
            NpcOverrideSource.Confirmed    => Brushes.Gold,
            NpcOverrideSource.CrowdSourced => Brushes.SkyBlue,
            _                              => Brushes.DimGray,
        };
        AddCell(entry.Source.ToString(), rowIndex, 3, sourceBrush, fontSize: 10);

        // Edit button — jump to Last NPC panel pre-filled with this entry
        if (isLocal)
        {
            var editBtn = new Button
            {
                Content = "Edit",
                FontSize = 10,
                Padding = new Avalonia.Thickness(6, 2, 6, 2),
                Margin  = new Avalonia.Thickness(2, 1, 2, 1),
                Tag     = entry,
            };
            editBtn.Click += OnNpcOverrideEditClicked;
            Grid.SetRow(editBtn, rowIndex);
            Grid.SetColumn(editBtn, 4);
            NpcOverridesGrid.Children.Add(editBtn);

            // Delete button
            var delBtn = new Button
            {
                Content    = "Delete",
                FontSize   = 10,
                Padding    = new Avalonia.Thickness(6, 2, 6, 2),
                Margin     = new Avalonia.Thickness(2, 1, 2, 1),
                Background = SolidColorBrush.Parse("#7B2D2D"),
                Tag        = entry,
            };
            delBtn.Click += OnNpcOverrideDeleteClicked;
            Grid.SetRow(delBtn, rowIndex);
            Grid.SetColumn(delBtn, 5);
            NpcOverridesGrid.Children.Add(delBtn);
        }
        else
        {
            // Read-only: show "Override" button to create a local shadow entry
            var overrideBtn = new Button
            {
                Content  = "Override locally",
                FontSize = 10,
                Padding  = new Avalonia.Thickness(6, 2, 6, 2),
                Margin   = new Avalonia.Thickness(2, 1, 2, 1),
                Tag      = entry,
            };
            overrideBtn.Click += OnNpcOverrideLocalOverrideClicked;
            Grid.SetRow(overrideBtn, rowIndex);
            Grid.SetColumn(overrideBtn, 4);
            Grid.SetColumnSpan(overrideBtn, 2);
            NpcOverridesGrid.Children.Add(overrideBtn);
        }
    }

    private void OnNpcOverrideEditClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NpcRaceOverride entry) return;

        _lastNpcId = entry.NpcId;
        LastNpcIdLabel.Text    = $"NPC ID: {entry.NpcId}";
        LastNpcNotesBox.Text   = entry.Notes ?? string.Empty;
        LastNpcPanel.IsVisible = true;
        SelectDropdownByRaceId(LastNpcRaceDropdown, entry.RaceId);
        PopulateLastNpcSampleDropdown();
        SelectLastNpcSampleDropdown(entry.BespokeSampleId);
        LastNpcClearButton.IsEnabled = true;
    }

    private void OnNpcOverrideDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NpcRaceOverride entry) return;

        _ = Task.Run(async () =>
        {
            await AppServices.NpcOverrides.DeleteAsync(entry.NpcId);
            AppServices.Assembler.RemoveRaceOverride(entry.NpcId);
            Dispatcher.UIThread.Post(RefreshNpcOverridesGrid);
        });
    }

    private void OnNpcOverrideLocalOverrideClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not NpcRaceOverride entry) return;

        // Create a local entry copying the server entry's values,
        // then let the user edit it in the Last NPC panel.
        _ = Task.Run(async () =>
        {
            await AppServices.NpcOverrides.UpsertAsync(entry.NpcId, entry.RaceId, entry.Notes);
            AppServices.Assembler.ApplyRaceOverride(entry.NpcId, entry.RaceId);
            Dispatcher.UIThread.Post(() =>
            {
                RefreshNpcOverridesGrid();
                OnNpcOverrideEditClicked(sender, e); // open panel
            });
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Populates a ComboBox with all race ID → accent group options.
    /// Uses the player race map entries, sorted by accent group name.
    /// </summary>
    /// <summary>
    /// Populates a ComboBox with one entry per race / creature-type ID.
    /// Derived from NpcVoiceSlotCatalog + RaceAccentMapping so it stays in
    /// sync automatically whenever new races are added to either source.
    ///
    /// Label: "{Race name} — {AccentLabel}"  (male entry used as representative).
    /// Tag  : integer raceId stored in NpcOverride records.
    /// </summary>
    private void PopulateNpcOverrideRaceDropdown(ComboBox dropdown)
    {
        dropdown.Items.Clear();

        var playerIds   = RaceAccentMapping.PlayerRaceIds;
        var creatureIds = RaceAccentMapping.CreatureTypeIds;
        var npcOnlyIds  = RaceAccentMapping.NpcOnlyRaceIds;

        var playerEntries   = new System.Collections.Generic.List<(int raceId, string label)>();
        var creatureEntries = new System.Collections.Generic.List<(int raceId, string label)>();
        var npcOnlyEntries  = new System.Collections.Generic.List<(int raceId, string label)>();

        // One row per unique race/group: use the Male (or Narrator) catalog entry as
        // the representative label; gender is resolved at runtime from NPC data.
        foreach (var item in NpcVoiceSlotCatalog.All)
        {
            if (item.Slot.Gender == Gender.Female) continue;

            var group    = item.Slot.Group;
            var raceName = item.NpcLabel
                .Replace(" / Male", "")
                .Replace(" NPC / Male", " NPC");
            var label    = $"{raceName} — {item.AccentLabel}";

            if (playerIds.TryGetValue(group, out int pid))
                playerEntries.Add((pid, label));
            else if (creatureIds.TryGetValue(group, out int cid))
                creatureEntries.Add((cid, label));
            else if (npcOnlyIds.TryGetValue(group, out int nid))
                npcOnlyEntries.Add((nid, label));
            // Narrator slot has no race ID — intentionally excluded
        }

        var sortedEntries = SelectionRecencyHelper.SortRaces(
            playerEntries.Concat(creatureEntries).Concat(npcOnlyEntries),
            AppServices.Settings);

        foreach (var (raceId, label) in sortedEntries)
        {
            dropdown.Items.Add(new ComboBoxItem
            {
                Content = label,
                Tag     = raceId,
            });
        }

        dropdown.SelectedIndex = 0;
    }

    private static void SelectDropdownByRaceId(ComboBox dropdown, int raceId)
    {
        foreach (var item in dropdown.Items.OfType<ComboBoxItem>())
        {
            if ((int)(item.Tag ?? -1) == raceId)
            {
                dropdown.SelectedItem = item;
                return;
            }
        }
        dropdown.SelectedIndex = 0;
    }

    /// <summary>
    /// Selects the correct base sample and variant for the given full sample ID.
    /// "M_WWZ_10-slow" → selects "M_WWZ_10" in base dropdown, "slow" in variant dropdown.
    /// </summary>
    private void SelectLastNpcSampleDropdown(string? sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId))
        {
            LastNpcSampleDropdown.SelectedIndex = 0;
            PopulateVariantDropdown(null);
            return;
        }

        var baseId = GetBaseSampleId(sampleId);

        // Select base
        foreach (var item in LastNpcSampleDropdown.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, baseId, StringComparison.OrdinalIgnoreCase))
            {
                LastNpcSampleDropdown.SelectedItem = item;
                break;
            }
        }

        // Repopulate variants for this base, then select the right variant
        PopulateVariantDropdown(baseId);

        if (!string.Equals(baseId, sampleId, StringComparison.OrdinalIgnoreCase))
        {
            // Has a variant — find and select it
            foreach (var item in LastNpcVariantDropdown.Items.OfType<ComboBoxItem>())
            {
                if (string.Equals(item.Tag as string, sampleId, StringComparison.OrdinalIgnoreCase))
                {
                    LastNpcVariantDropdown.SelectedItem = item;
                    return;
                }
            }
        }

        LastNpcVariantDropdown.SelectedIndex = 0;
    }

    /// <summary>
    /// Returns the full selected sample ID (including variant suffix if any),
    /// or null if "(race default)" is selected.
    /// Reads from the variant dropdown — its Tag holds the full ID.
    /// </summary>
    private string? GetSelectedLastNpcSampleId()
    {
        // Base dropdown has null tag for "(race default)"
        var baseItem = LastNpcSampleDropdown.SelectedItem as ComboBoxItem;
        if (baseItem?.Tag as string == null)
            return null;

        // Variant dropdown tag holds the full ID (base or base+variant)
        if (LastNpcVariantDropdown.SelectedItem is ComboBoxItem varItem)
            return varItem.Tag as string;

        return baseItem.Tag as string;
    }

    private void AddCell(string text, int row, int col,
        IBrush? foreground = null, double fontSize = 11)
    {
        var tb = new TextBlock
        {
            Text       = text,
            FontSize   = fontSize,
            Foreground = foreground ?? Brushes.WhiteSmoke,
            Margin     = new Avalonia.Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(tb, row);
        Grid.SetColumn(tb, col);
        NpcOverridesGrid.Children.Add(tb);
    }
    
    // ── Import / Export ───────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _jsonNpcOptions = new() { WriteIndented = true };

    /// <summary>
    /// DTO for JSON export/import. Includes bespoke sample fields so round-trips
    /// preserve per-NPC voice assignments.
    /// </summary>
    private sealed class NpcOverrideExportEntry
    {
        public int     NpcId               { get; set; }
        public int     RaceId              { get; set; }
        public string? Notes               { get; set; }
        public string? BespokeSampleId     { get; set; }
        public float?  BespokeExaggeration { get; set; }
        public float?  BespokeCfgWeight    { get; set; }
    }

    private sealed class NpcOverrideExportFile
    {
        public string                       Version { get; set; } = "1";
        public List<NpcOverrideExportEntry> Entries { get; set; } = new();
    }

    private async void OnNpcOverridesExportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var entries = await AppServices.NpcOverrides.GetAllAsync();
            var localEntries = entries.Where(x => x.Source == NpcOverrideSource.Local).ToList();

            var file = new NpcOverrideExportFile
            {
                Entries = localEntries.Select(x => new NpcOverrideExportEntry
                {
                    NpcId               = x.NpcId,
                    RaceId              = x.RaceId,
                    Notes               = x.Notes,
                    BespokeSampleId     = x.BespokeSampleId,
                    BespokeExaggeration = x.BespokeExaggeration,
                    BespokeCfgWeight    = x.BespokeCfgWeight,
                }).ToList(),
            };

            var json = JsonSerializer.Serialize(file, _jsonNpcOptions);

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var path = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title             = "Export NPC Voice Overrides",
                SuggestedFileName = "npc-voice-overrides.json",
                DefaultExtension  = "json",
                FileTypeChoices   = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });

            if (path == null) return;

            await File.WriteAllTextAsync(path.Path.LocalPath, json);
            NpcOverridesStatus.Text = $"Exported {localEntries.Count} override(s).";
        }
        catch (Exception ex)
        {
            NpcOverridesStatus.Text = $"Export failed: {ex.Message}";
        }
    }

    private async void OnNpcOverridesImportClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title          = "Import NPC Voice Overrides",
                AllowMultiple  = false,
                FileTypeFilter = new[] { new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } } },
            });

            if (files.Count == 0) return;

            var json = await File.ReadAllTextAsync(files[0].Path.LocalPath);
            var file = JsonSerializer.Deserialize<NpcOverrideExportFile>(json);

            if (file?.Entries == null || file.Entries.Count == 0)
            {
                NpcOverridesStatus.Text = "No entries found in file.";
                return;
            }

            int count = 0;
            foreach (var entry in file.Entries)
            {
                await AppServices.NpcOverrides.UpsertAsync(
                    entry.NpcId, entry.RaceId, entry.Notes,
                    bespokeSampleId:     entry.BespokeSampleId,
                    bespokeExaggeration: entry.BespokeExaggeration,
                    bespokeCfgWeight:    entry.BespokeCfgWeight);

                AppServices.Assembler.ApplyRaceOverride(
                    entry.NpcId, entry.RaceId,
                    bespokeSampleId:     entry.BespokeSampleId,
                    bespokeExaggeration: entry.BespokeExaggeration,
                    bespokeCfgWeight:    entry.BespokeCfgWeight);

                count++;
            }

            RefreshNpcOverridesGrid();
            NpcOverridesStatus.Text = $"Imported {count} override(s).";
        }
        catch (Exception ex)
        {
            NpcOverridesStatus.Text = $"Import failed: {ex.Message}";
        }
    }

    private void OnNpcOverridesRefreshClicked(object? sender, RoutedEventArgs e)
        => RefreshNpcOverridesGrid();

    private async void OnNpcOverridesPullFromServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            NpcOverridesStatus.Text = "No server URL configured.";
            return;
        }

        NpcOverridesStatus.Text = "Pulling from server…";
        try
        {
            int merged = await AppServices.NpcSync.PollNpcOverridesAsync(sinceTs: 0.0);
            RefreshNpcOverridesGrid();
            NpcOverridesStatus.Text = merged > 0
                ? $"Pulled {merged} override(s) from server."
                : "No new overrides on server.";
        }
        catch (Exception ex)
        {
            NpcOverridesStatus.Text = $"Pull failed: {ex.Message}";
        }
    }

    private async void OnNpcOverridesPushToServerClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AppServices.Settings.RemoteServerUrl))
        {
            NpcOverridesStatus.Text = "No server URL configured.";
            return;
        }

        NpcOverridesStatus.Text = "Pushing to server…";
        try
        {
            var entries = await AppServices.NpcOverrides.GetAllAsync();
            var local   = entries.Where(x => x.Source == NpcOverrideSource.Local).ToList();

            int ok = 0;
            foreach (var entry in local)
            {
                if (await AppServices.NpcSync.ContributeOneAsync(entry))
                    ok++;
            }

            NpcOverridesStatus.Text = $"Pushed {ok}/{local.Count} override(s) to server.";
        }
        catch (Exception ex)
        {
            NpcOverridesStatus.Text = $"Push failed: {ex.Message}";
        }
    }
}