// SPDX-License-Identifier: GPL-3.0-or-later
//
// This file is part of RuneReaderVoice.
// Copyright (C) 2026 Michael Sutton

// UI/Views/DspEffectPickerDialog.cs
// Modal dialog that lets the user pick a DSP effect to add to the chain.
// Displays effects grouped by category as a grid of buttons.
// Returns the chosen DspEffectKind on close, or null if cancelled.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public sealed class DspEffectPickerDialog : Window
{
    public DspEffectKind? ChosenEffect { get; private set; }

    public DspEffectPickerDialog()
    {
        Title           = "Add Effect";
        Width           = 480;
        SizeToContent   = SizeToContent.Height;
        CanResize       = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background      = SolidColorBrush.Parse("#1A1A2E");

        var root = new StackPanel { Spacing = 12, Margin = new Thickness(16) };

        root.Children.Add(new TextBlock
        {
            Text       = "Choose an effect to add to the chain:",
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.White,
            Margin     = new Thickness(0, 0, 0, 4),
        });

        // Group effects by category, build a section per category
        var allKinds = Enum.GetValues<DspEffectKind>();
        var byCategory = allKinds
            .GroupBy(DspEffectItem.Category)
            .OrderBy(g => CategoryOrder(g.Key));

        foreach (var group in byCategory)
        {
            root.Children.Add(new TextBlock
            {
                Text       = group.Key,
                FontSize   = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = SolidColorBrush.Parse("#888"),
                Margin     = new Thickness(0, 4, 0, 2),
            });

            var wrap = new WrapPanel { Orientation = Orientation.Horizontal, ItemWidth = 140 };
            foreach (var kind in group)
            {
                var k    = kind; // capture
                var btn  = new Button
                {
                    Content             = DspEffectItem.DisplayName(k),
                    Height              = 36,
                    Margin              = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Background          = SolidColorBrush.Parse("#16213E"),
                    Foreground          = Brushes.White,
                    FontSize            = 12,
                };
                ToolTip.SetTip(btn, DspEffectItem.Description(k));
                btn.Click += (_, _) =>
                {
                    ChosenEffect = k;
                    Close();
                };
                wrap.Children.Add(btn);
            }
            root.Children.Add(wrap);
        }

        // Cancel button
        var cancelBtn = new Button
        {
            Content             = "Cancel",
            Width               = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 8, 0, 0),
        };
        cancelBtn.Click += (_, _) => Close();
        root.Children.Add(cancelBtn);

        Content = new ScrollViewer { Content = root, MaxHeight = 600 };
    }

    private static int CategoryOrder(string cat) => cat switch
    {
        "Dynamics"    => 0,
        "Pitch & Time"=> 1,
        "Tone"        => 2,
        "Distortion"  => 3,
        "Modulation"  => 4,
        "Space"       => 5,
        "Character"   => 6,
        _             => 99,
    };
}
