// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton
//
// RuneReaderVoice is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// RuneReaderVoice is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with RuneReaderVoice. If not, see <https://www.gnu.org/licenses/>.


// VoiceMixDialog.axaml.cs
// Inline dialog for building a Kokoro voice blend spec.
// Shows up to 4 voice+weight rows. Produces a "mix:id:w|id:w" string
// that KokoroTtsProvider.ResolveMix() understands.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public sealed class VoiceMixDialog : Window
{
    public string? ResultSpec { get; private set; }

    private readonly List<(ComboBox voiceCombo, NumericUpDown weightBox)> _rows = new();
    private readonly StackPanel _rowsPanel;
    private readonly IReadOnlyList<VoiceInfo> _voices;

    public VoiceMixDialog(IReadOnlyList<VoiceInfo> voices, string? existingSpec = null)
    {
        _voices = voices;

        Title           = "Voice Mix Editor";
        Width           = 440;
        SizeToContent   = SizeToContent.Height;
        Background      = SolidColorBrush.Parse("#1A1A2E");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize       = false;

        _rowsPanel = new StackPanel { Spacing = 6, Margin = new Avalonia.Thickness(12, 8, 12, 4) };

        var addButton = new Button
        {
            Content    = "+ Add Voice",
            Margin     = new Avalonia.Thickness(12, 4, 12, 0),
            Background = SolidColorBrush.Parse("#0F3460"),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        addButton.Click += (_, _) => { if (_rows.Count < 4) AddRow(); };

        var hint = new TextBlock
        {
            Text       = "Weights are relative — they don't need to sum to 1.",
            Foreground = SolidColorBrush.Parse("#888"),
            FontSize   = 10,
            Margin     = new Avalonia.Thickness(12, 4, 12, 8),
        };

        var okButton = new Button
        {
            Content    = "Apply Mix",
            Background = SolidColorBrush.Parse("#E94560"),
            Foreground = Brushes.White,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin     = new Avalonia.Thickness(12, 4, 12, 12),
        };
        okButton.Click += OnApply;

        Content = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text       = "Blend up to 4 voices. Drag weights to taste.",
                    Foreground = SolidColorBrush.Parse("#E0E0FF"),
                    Margin     = new Avalonia.Thickness(12, 12, 12, 6),
                },
                _rowsPanel,
                addButton,
                hint,
                okButton,
            }
        };

        // Pre-populate from existing spec if provided
        if (!string.IsNullOrWhiteSpace(existingSpec))
            ParseExisting(existingSpec);
        else
            AddRow(); // start with one row
    }

    private void AddRow(string voiceId = "", float weight = 1f)
    {
        var voiceCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Width = 220,
        };
        foreach (var v in _voices)
            voiceCombo.Items.Add(new ComboBoxItem { Content = v.Name, Tag = v.VoiceId });

        if (!string.IsNullOrEmpty(voiceId))
        {
            var match = voiceCombo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == voiceId);
            if (match != null) voiceCombo.SelectedItem = match;
        }
        else
            voiceCombo.SelectedIndex = _rows.Count < _voices.Count ? _rows.Count : 0;

        var weightBox = new NumericUpDown
        {
            Minimum   = 0.05m,
            Maximum   = 10m,
            Increment = 0.05m,
            Value     = (decimal)weight,
            Width     = 90,
            FormatString = "0.00",
        };

        var removeBtn = new Button
        {
            Content    = "✕",
            Width      = 28,
            Height     = 28,
            Background = SolidColorBrush.Parse("#333"),
            Foreground = Brushes.Gray,
            Padding    = new Avalonia.Thickness(0),
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Margin = new Avalonia.Thickness(0, 0, 0, 2),
        };

        Grid.SetColumn(voiceCombo, 0);
        Grid.SetColumn(weightBox,  1);
        Grid.SetColumn(removeBtn,  2);
        row.Children.Add(voiceCombo);
        row.Children.Add(weightBox);
        row.Children.Add(removeBtn);

        var entry = (voiceCombo, weightBox);
        _rows.Add(entry);
        _rowsPanel.Children.Add(row);

        removeBtn.Click += (_, _) =>
        {
            _rows.Remove(entry);
            _rowsPanel.Children.Remove(row);
        };
    }

    private void ParseExisting(string spec)
    {
        // Strip "mix:" prefix if present
        var raw = spec.StartsWith(KokoroTtsProvider.MixPrefix)
            ? spec[KokoroTtsProvider.MixPrefix.Length..]
            : spec;

        foreach (var part in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.LastIndexOf(':');
            if (colon < 0) { AddRow(part, 1f); continue; }
            var id = part[..colon];
            var w  = float.TryParse(part[(colon + 1)..],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var wf) ? wf : 1f;
            AddRow(id, w);
        }
    }

    private void OnApply(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var parts = _rows
            .Where(r => r.voiceCombo.SelectedItem is ComboBoxItem)
            .Select(r =>
            {
                var id = ((ComboBoxItem)r.voiceCombo.SelectedItem!).Tag!.ToString()!;
                var w  = (float)(r.weightBox.Value ?? 1m);
                return $"{id}:{w:F2}";
            })
            .ToList();

        if (parts.Count == 0) { Close(); return; }

        ResultSpec = parts.Count == 1
            ? parts[0]  // single voice — no mix prefix needed
            : KokoroTtsProvider.MixPrefix + string.Join("|", parts);

        Close();
    }
}