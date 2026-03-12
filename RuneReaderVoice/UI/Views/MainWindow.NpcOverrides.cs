// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

// MainWindow.NpcOverrides.cs
// "Last NPC" panel (updates after each dialog) and NPC Overrides CRUD tab.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
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
        RefreshNpcOverridesGrid();
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

                LastNpcSaveButton.IsEnabled  = true;
                LastNpcClearButton.IsEnabled = existing != null;
            });
        });
    }

    private void OnLastNpcSaveClicked(object? sender, RoutedEventArgs e)
    {
        if (_lastNpcId == 0) return;
        if (LastNpcRaceDropdown.SelectedItem is not ComboBoxItem item) return;

        var raceId = (int)(item.Tag ?? 0);
        var notes  = LastNpcNotesBox.Text?.Trim();

        _ = Task.Run(async () =>
        {
            await AppServices.NpcOverrides.UpsertAsync(_lastNpcId, raceId, notes);
            AppServices.Assembler.ApplyRaceOverride(_lastNpcId, raceId);

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

        // Pre-fill the Last NPC panel with this entry and show it.
        _lastNpcId = entry.NpcId;
        LastNpcIdLabel.Text    = $"NPC ID: {entry.NpcId}";
        LastNpcNotesBox.Text   = entry.Notes ?? string.Empty;
        LastNpcPanel.IsVisible = true;
        SelectDropdownByRaceId(LastNpcRaceDropdown, entry.RaceId);
        LastNpcClearButton.IsEnabled = true;

        // Scroll / focus — navigate to the panel tab if tabs are in use.
        // For now just make it visible; the panel is always on the main window.
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
    private static void PopulateNpcOverrideRaceDropdown(ComboBox dropdown)
    {
        dropdown.Items.Clear();

        // Build a representative list: one entry per distinct (raceId, accentGroup) pair.
        // We use a small fixed list so the user sees friendly WoW race names.
        var options = new (int raceId, string label)[]
        {
            (1,    "Human — Neutral American"),
            (2,    "Orc — Neutral American"),
            (3,    "Dwarf — Scottish"),
            (4,    "Night Elf — Neutral American"),
            (5,    "Undead — American Raspy"),
            (6,    "Tauren — Deep Resonant"),
            (7,    "Gnome — Playful / Squeaky"),
            (8,    "Troll — Caribbean"),
            (9,    "Goblin — New York"),
            (10,   "Blood Elf — British Haughty"),
            (11,   "Draenei — Eastern European"),
            (13,   "Pandaren — East Asian"),
            (22,   "Worgen — British Rugged"),
            (24,   "Nightborne — French"),
            (27,   "Highmountain Tauren — Deep Resonant"),
            (28,   "Lightforged Draenei — Eastern European"),
            (29,   "Void Elf — British Haughty"),
            (30,   "Dark Iron Dwarf — Scottish"),
            (31,   "Zandalari Troll — Regal Tribal"),
            (32,   "Kul Tiran — British Rugged"),
            (35,   "Vulpera — Scrappy"),
            (36,   "Mag'har Orc — Neutral American"),
            (37,   "Mechagnome — Playful / Squeaky"),
            (0x50, "Humanoid NPC — Neutral American"),
            (0x51, "Beast — Narrator"),
            (0x52, "Dragonkin — Deep Resonant"),
            (0x53, "Undead creature — American Raspy"),
            (0x54, "Demon — American Raspy"),
            (0x55, "Elemental — Deep Resonant"),
            (0x56, "Giant — Deep Resonant"),
            (0x57, "Mechanical — Playful / Squeaky"),
            (0x58, "Aberration — Narrator"),
        };

        foreach (var (raceId, label) in options)
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
    
    private void OnNpcOverridesExportClicked(object? sender, RoutedEventArgs e)
    {
        // TODO: serialize GetAllAsync() to JSON and open a SaveFileDialog
    }

    private void OnNpcOverridesImportClicked(object? sender, RoutedEventArgs e)
    {
        // TODO: open OpenFileDialog, deserialize, call UpsertAsync per entry
    }

    private void OnNpcOverridesRefreshClicked(object? sender, RoutedEventArgs e)
        => RefreshNpcOverridesGrid();
}
