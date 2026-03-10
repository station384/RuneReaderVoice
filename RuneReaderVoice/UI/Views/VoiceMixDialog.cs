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

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public sealed class VoiceMixDialog : Window
{
    public string? ResultSpec { get; private set; }

    private sealed class MixRow
    {
        public required ComboBox VoiceCombo { get; init; }
        public required Slider PercentSlider { get; init; }
        public required TextBlock PercentLabel { get; init; }
        public required Grid RowGrid { get; init; }
    }

    private readonly List<MixRow> _rows = new();
    private readonly StackPanel _rowsPanel;
    private readonly IReadOnlyList<VoiceInfo> _voices;
    private readonly TextBlock _totalLabel;

    public VoiceMixDialog(IReadOnlyList<VoiceInfo> voices, string? existingSpec = null)
    {
        _voices = voices;

        Title = "Voice Mix Editor";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        Background = SolidColorBrush.Parse("#1A1A2E");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CanResize = false;

        _rowsPanel = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(12, 8, 12, 4)
        };

        var addButton = new Button
        {
            Content = "+ Add Voice",
            Margin = new Thickness(12, 4, 12, 0),
            Background = SolidColorBrush.Parse("#0F3460"),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        addButton.Click += (_, _) =>
        {
            if (_rows.Count < 4)
            {
                AddRow();
                RebalanceEvenly();
                RefreshTotals();
            }
        };

        _totalLabel = new TextBlock
        {
            Text = "Total: 100%",
            Foreground = SolidColorBrush.Parse("#B8B8D8"),
            FontSize = 11,
            Margin = new Thickness(12, 2, 12, 2),
        };

        var hint = new TextBlock
        {
            Text = "Adjust each voice as a percentage of the final mix.",
            Foreground = SolidColorBrush.Parse("#888"),
            FontSize = 10,
            Margin = new Thickness(12, 2, 12, 8),
        };

        var okButton = new Button
        {
            Content = "Apply Mix",
            Background = SolidColorBrush.Parse("#E94560"),
            Foreground = Brushes.White,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(12, 4, 12, 12),
        };
        okButton.Click += OnApply;

        Content = new StackPanel
        {
            Children =
            {
                new TextBlock
                {
                    Text = "Blend up to 4 voices.",
                    Foreground = SolidColorBrush.Parse("#E0E0FF"),
                    Margin = new Thickness(12, 12, 12, 6),
                    FontSize = 14,
                    FontWeight = FontWeight.SemiBold
                },
                _rowsPanel,
                addButton,
                _totalLabel,
                hint,
                okButton,
            }
        };

        if (!string.IsNullOrWhiteSpace(existingSpec))
            ParseExisting(existingSpec);
        else
            AddRow(percent: 100);

        RefreshTotals();
    }

    private void AddRow(string voiceId = "", double percent = 25)
    {
        var voiceCombo = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 240
        };

        foreach (var v in _voices)
            voiceCombo.Items.Add(new ComboBoxItem { Content = v.Name, Tag = v.VoiceId });

        if (!string.IsNullOrEmpty(voiceId))
        {
            var match = voiceCombo.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == voiceId);

            if (match != null)
                voiceCombo.SelectedItem = match;
        }
        else
        {
            voiceCombo.SelectedIndex = _rows.Count < _voices.Count ? _rows.Count : 0;
        }

        var percentSlider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = percent,
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var percentLabel = new TextBlock
        {
            Text = $"{Math.Round(percent)}%",
            Width = 44,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Foreground = Brushes.White
        };

        var removeBtn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            Background = SolidColorBrush.Parse("#333"),
            Foreground = Brushes.Gray,
            Padding = new Thickness(0),
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
            Margin = new Thickness(0, 0, 0, 2),
            ColumnSpacing = 8
        };

        Grid.SetColumn(voiceCombo, 0);
        Grid.SetColumn(percentSlider, 1);
        Grid.SetColumn(percentLabel, 2);
        Grid.SetColumn(removeBtn, 3);

        row.Children.Add(voiceCombo);
        row.Children.Add(percentSlider);
        row.Children.Add(percentLabel);
        row.Children.Add(removeBtn);

        var entry = new MixRow
        {
            VoiceCombo = voiceCombo,
            PercentSlider = percentSlider,
            PercentLabel = percentLabel,
            RowGrid = row
        };

        percentSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty)
            {
                entry.PercentLabel.Text = $"{Math.Round(percentSlider.Value)}%";
                RefreshTotals();
            }
        };

        removeBtn.Click += (_, _) =>
        {
            _rows.Remove(entry);
            _rowsPanel.Children.Remove(row);

            if (_rows.Count == 1)
                _rows[0].PercentSlider.Value = 100;
            else if (_rows.Count > 1)
                RebalanceEvenly();

            RefreshTotals();
        };

        _rows.Add(entry);
        _rowsPanel.Children.Add(row);
    }

    private void RefreshTotals()
    {
        var total = _rows.Sum(r => r.PercentSlider.Value);
        _totalLabel.Text = $"Total: {Math.Round(total)}%";

        if (Math.Abs(total - 100) < 0.5)
            _totalLabel.Foreground = SolidColorBrush.Parse("#9FE870");
        else
            _totalLabel.Foreground = SolidColorBrush.Parse("#FFD166");
    }

    private void RebalanceEvenly()
    {
        if (_rows.Count == 0)
            return;

        var even = 100.0 / _rows.Count;

        for (int i = 0; i < _rows.Count; i++)
            _rows[i].PercentSlider.Value = even;

        RefreshTotals();
    }

    private void ParseExisting(string spec)
    {
        var raw = spec.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.Ordinal)
            ? spec[KokoroTtsProvider.MixPrefix.Length..]
            : spec;

        var parsed = new List<(string id, float weight)>();

        foreach (var part in raw.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.LastIndexOf(':');
            if (colon < 0)
            {
                parsed.Add((part, 1f));
                continue;
            }

            var id = part[..colon];
            var w = float.TryParse(
                part[(colon + 1)..],
                System.Globalization.NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var wf)
                ? wf
                : 1f;

            parsed.Add((id, w));
        }

        if (parsed.Count == 0)
        {
            AddRow(percent: 100);
            return;
        }

        var totalWeight = parsed.Sum(x => x.weight);
        if (totalWeight <= 0f)
            totalWeight = parsed.Count;

        foreach (var item in parsed)
        {
            var percent = (item.weight / totalWeight) * 100.0;
            AddRow(item.id, percent);
        }

        RefreshTotals();
    }

    private void OnApply(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = _rows
            .Where(r => r.VoiceCombo.SelectedItem is ComboBoxItem)
            .Select(r => new
            {
                Id = ((ComboBoxItem)r.VoiceCombo.SelectedItem!).Tag!.ToString()!,
                Percent = Math.Max(0, r.PercentSlider.Value)
            })
            .ToList();

        if (selected.Count == 0)
        {
            Close();
            return;
        }

        if (selected.Count == 1)
        {
            ResultSpec = selected[0].Id;
            Close();
            return;
        }

        var total = selected.Sum(x => x.Percent);
        if (total <= 0)
            total = selected.Count;

        var parts = selected
            .Select(x =>
            {
                var normalized = x.Percent / total;
                return $"{x.Id}:{normalized.ToString("0.####", CultureInfo.InvariantCulture)}";
            })
            .ToList();

        ResultSpec = KokoroTtsProvider.MixPrefix + string.Join("|", parts);
        Close();
    }
}