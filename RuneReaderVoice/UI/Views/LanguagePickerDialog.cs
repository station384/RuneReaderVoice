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

// LanguagePickerDialog.cs
// Simple language and dialect picker used by the voice profile editor.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public sealed class LanguagePickerDialog : Window
{
    private readonly TextBox _searchBox;
    private readonly ListBox _listBox;
    private readonly TextBlock _detailName;
    private readonly TextBlock _detailCode;
    private readonly IReadOnlyList<EspeakLanguageOption> _all;

    public LanguagePickerDialog(string? initialCode)
    {
        _all = EspeakLanguageCatalog.All;

        Title = "Choose Dialect / Language";
        Width = 780;
        Height = 620;
        MinWidth = 680;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _searchBox = new TextBox { PlaceholderText = "Search languages or codes..." };
        _searchBox.TextChanged += (_, _) => RefreshList(_searchBox.Text ?? "");

        _detailName = new TextBlock { FontWeight = Avalonia.Media.FontWeight.SemiBold };
        _detailCode = new TextBlock();

        _listBox = new ListBox();
        _listBox.SelectionChanged += (_, _) =>
        {
            if (_listBox.SelectedItem is EspeakLanguageOption option)
            {
                _detailName.Text = option.DisplayName;
                _detailCode.Text = $"Code: {option.Code}";
            }
            else
            {
                _detailName.Text = string.Empty;
                _detailCode.Text = string.Empty;
            }
        };

        var selectButton = new Button { Content = "Select", Width = 90 };
        selectButton.Click += SelectButton_Click;

        var cancelButton = new Button { Content = "Cancel", Width = 90 };
        cancelButton.Click += (_, _) => Close(null);

        var leftBorder = new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(6),
            Child = _listBox
        };
        Grid.SetRow(leftBorder, 1);
        Grid.SetColumn(leftBorder, 0);

        var rightBorder = new Border
        {
            BorderThickness = new Avalonia.Thickness(1),
            Padding = new Avalonia.Thickness(10),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Selected", FontWeight = Avalonia.Media.FontWeight.Bold },
                    _detailName,
                    _detailCode
                }
            }
        };
        Grid.SetRow(rightBorder, 1);
        Grid.SetColumn(rightBorder, 1);

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, selectButton }
        };
        Grid.SetRow(buttonRow, 2);
        Grid.SetColumnSpan(buttonRow, 2);

        var root = new Grid
        {
            Margin = new Avalonia.Thickness(12),
            ColumnDefinitions = new ColumnDefinitions("2*,*"),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            ColumnSpacing = 12,
            RowSpacing = 10
        };

        root.Children.Add(_searchBox);
        root.Children.Add(leftBorder);
        root.Children.Add(rightBorder);
        root.Children.Add(buttonRow);

        Content = root;
        RefreshList(string.Empty);

        if (!string.IsNullOrWhiteSpace(initialCode))
        {
            var match = _all.FirstOrDefault(x => string.Equals(x.Code, initialCode, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                _listBox.SelectedItem = match;
        }
    }

    private void RefreshList(string search)
    {
        search = search.Trim();

        IEnumerable<EspeakLanguageOption> items = _all;
        if (!string.IsNullOrWhiteSpace(search))
        {
            items = items.Where(x =>
                x.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                x.Code.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        _listBox.ItemsSource = items.ToArray();
    }

    private void SelectButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(_listBox.SelectedItem as EspeakLanguageOption);
    }
}