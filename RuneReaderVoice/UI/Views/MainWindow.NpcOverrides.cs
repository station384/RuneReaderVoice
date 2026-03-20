// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

// MainWindow.NpcOverrides.cs
// "Last NPC" panel (updates after each dialog) and NPC Overrides CRUD tab.

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

    /// <summary>
    /// Populates the bespoke sample dropdown in the Last NPC panel.
    /// Only visible when the active provider is a RemoteTtsProvider that
    /// supports voice matching. Shows "(no bespoke sample)" as first item.
    /// </summary>
    private void PopulateLastNpcSampleDropdown()
    {
        LastNpcSampleDropdown.Items.Clear();
        LastNpcSampleDropdown.Items.Add(new ComboBoxItem
        {
            Content = "(no bespoke sample — use race default)",
            Tag     = (string?)null,
        });

        if (AppServices.Provider is TTS.Providers.RemoteTtsProvider remoteProvider)
        {
            var voices = remoteProvider.GetAvailableVoices();
            foreach (var v in voices.OrderBy(v => v.VoiceId))
            {
                LastNpcSampleDropdown.Items.Add(new ComboBoxItem
                {
                    Content = v.VoiceId,
                    Tag     = v.VoiceId,
                });
            }

            LastNpcSamplePanel.IsVisible = voices.Count > 0;
        }
        else
        {
            LastNpcSamplePanel.IsVisible = false;
        }

        LastNpcSampleDropdown.SelectedIndex = 0;
    }

    // ── Last NPC panel ────────────────────────────────────────────────────────

    private void OnSegmentCompletedForNpcPanel(Session.AssembledSegment seg)
    {
        // NpcId=0 means narrator text / book — no NPC to assign.
        if (seg.NpcId == 0)
            return;

        _lastNpcId = seg.NpcId;
        LastNpcIdLabel.Text = $"NPC ID: {seg.NpcId}";
        LastNpcPanel.IsVisible = true;

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

        var raceId         = (int)(item.Tag ?? 0);
        var notes          = LastNpcNotesBox.Text?.Trim();
        var bespokeSampleId = GetSelectedLastNpcSampleId();

        _ = Task.Run(async () =>
        {
            await AppServices.NpcOverrides.UpsertAsync(
                _lastNpcId, raceId, notes,
                bespokeSampleId: bespokeSampleId);

            AppServices.Assembler.ApplyRaceOverride(
                _lastNpcId, raceId,
                bespokeSampleId: bespokeSampleId);

            Dispatcher.UIThread.Post(() =>
            {
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
    private static void PopulateNpcOverrideRaceDropdown(ComboBox dropdown)
    {
        dropdown.Items.Clear();

        var entries = new System.Collections.Generic.List<(int raceId, string label)>();

        var playerIds   = RaceAccentMapping.PlayerRaceIds;
        var creatureIds = RaceAccentMapping.CreatureTypeIds;

        // One row per unique race: use the Male (or Narrator) catalog entry as
        // the representative label; gender is resolved at runtime from NPC data.
        foreach (var item in NpcVoiceSlotCatalog.All)
        {
            if (item.Slot.Gender == Gender.Female) continue;

            var group     = item.Slot.Group;
            var raceName  = item.NpcLabel
                .Replace(" / Male", "")
                .Replace(" NPC / Male", " NPC");
            var label     = $"{raceName} — {item.AccentLabel}";

            if (playerIds.TryGetValue(group, out int pid))
            {
                entries.Add((pid, label));
            }
            else if (creatureIds.TryGetValue(group, out int cid))
            {
                entries.Add((cid, label));
            }
        }

        // Sort: player races (low IDs) first, then creature types (0x50+).
        entries.Sort((a, b) => a.raceId.CompareTo(b.raceId));

        foreach (var (raceId, label) in entries)
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
    /// Selects the bespoke sample dropdown entry matching the given sample ID.
    /// Selects the "(no bespoke sample)" entry when sampleId is null or not found.
    /// </summary>
    private void SelectLastNpcSampleDropdown(string? sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId))
        {
            LastNpcSampleDropdown.SelectedIndex = 0;
            return;
        }

        foreach (var item in LastNpcSampleDropdown.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, sampleId, StringComparison.OrdinalIgnoreCase))
            {
                LastNpcSampleDropdown.SelectedItem = item;
                return;
            }
        }

        LastNpcSampleDropdown.SelectedIndex = 0;
    }

    /// <summary>
    /// Returns the selected bespoke sample ID, or null if "(no bespoke sample)" is selected.
    /// </summary>
    private string? GetSelectedLastNpcSampleId()
    {
        if (LastNpcSampleDropdown.SelectedItem is ComboBoxItem item)
            return item.Tag as string;
        return null;
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
}
