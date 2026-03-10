using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
   private void PopulateVoiceGrid()
    {
        VoiceGrid.Children.Clear();
        var voices = AppServices.Provider.GetAvailableVoices();

        // Header row
        var headerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2*,3*,3*"),
            Margin = new Avalonia.Thickness(0, 0, 0, 4)
        };
        AddGridLabel(headerGrid, "Accent Group", 0, Avalonia.Media.Brushes.Gray);
        AddGridLabel(headerGrid, "Male Voice",   1, Avalonia.Media.Brushes.Gray);
        AddGridLabel(headerGrid, "Female Voice", 2, Avalonia.Media.Brushes.Gray);
        VoiceGrid.Children.Add(headerGrid);

        // One row per accent group
        foreach (AccentGroup group in Enum.GetValues<AccentGroup>())
        {
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("2*,3*,3*"),
                Margin = new Avalonia.Thickness(0, 2, 0, 2)
            };

            AddGridLabel(row, group.ToString(), 0, Avalonia.Media.Brushes.LightGray);

            if (group == AccentGroup.Narrator)
            {
                var control = BuildVoiceControl(voices, new VoiceSlot(group, Gender.Unknown));
                Grid.SetColumn(control, 1);
                Grid.SetColumnSpan(control, 2);
                row.Children.Add(control);
            }
            else
            {
                var maleControl   = BuildVoiceControl(voices, new VoiceSlot(group, Gender.Male));
                var femaleControl = BuildVoiceControl(voices, new VoiceSlot(group, Gender.Female));
                Grid.SetColumn(maleControl,   1);
                Grid.SetColumn(femaleControl, 2);
                row.Children.Add(maleControl);
                row.Children.Add(femaleControl);
            }

            VoiceGrid.Children.Add(row);
        }
    }

    private Control BuildVoiceControl(IReadOnlyList<VoiceInfo> voices, VoiceSlot slot)
    {
        var combo = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };

        // For Kokoro, add all known voices. For others, add what the provider returned.
        foreach (var v in voices)
            combo.Items.Add(new ComboBoxItem { Content = v.Name, Tag = v.VoiceId });

        var key         = slot.ToString();
        bool initializing = true;

        // Restore saved assignment — may be a blend spec for Kokoro
        if (AppServices.Settings.VoiceAssignments.TryGetValue(key, out var savedId))
        {
            if (savedId.StartsWith(RuneReaderVoice.TTS.Providers.KokoroTtsProvider.MixPrefix))
            {
                // Blend spec — show as a synthetic item in the combo
                var mixItem = new ComboBoxItem { Content = "⚗ Custom Mix", Tag = savedId };
                combo.Items.Add(mixItem);
                combo.SelectedItem = mixItem;
            }
            else
            {
                var match = combo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag?.ToString() == savedId);
                if (match != null) combo.SelectedItem = match;
            }
        }
        else if (combo.Items.Count > 0)
            combo.SelectedIndex = 0;

        initializing = false;

        void ApplyVoiceId(string voiceId)
        {
            AppServices.Settings.VoiceAssignments[key] = voiceId;
            VoiceSettingsManager.SaveSettings(AppServices.Settings);
#if WINDOWS
            if (AppServices.Provider is RuneReaderVoice.TTS.Providers.WinRtTtsProvider winRt)
                winRt.SetVoice(slot, voiceId);
#endif
            if (AppServices.Provider is RuneReaderVoice.TTS.Providers.KokoroTtsProvider kokoro)
                kokoro.SetVoice(slot, voiceId);
            // No cache clear needed — cache is keyed on resolved voice ID,
            // so the old slot's entries become naturally unreachable (LRU-evicted).
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (initializing) return;
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string voiceId)
                ApplyVoiceId(voiceId);
        };

        // ── Preview button (all providers) ───────────────────────────────────────
        var previewBtn = new Button
        {
            Content   = "▶",
            Width     = 26,
            Height    = 26,
            FontSize  = 11,
            Padding   = new Avalonia.Thickness(0),
            Background = Avalonia.Media.SolidColorBrush.Parse("#1A472A"),
            Foreground = Avalonia.Media.Brushes.LightGreen,
            Margin    = new Avalonia.Thickness(3, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Avalonia.Controls.ToolTip.SetTip(previewBtn, "Preview this voice");

        previewBtn.Click += async (_, _) =>
        {
            // Friendly name for the spoken line (strip UI decoration like ★)
            var voiceName = combo.SelectedItem is ComboBoxItem selItem
                ? selItem.Content?.ToString() ?? slot.ToString()
                : slot.ToString();
            // Strip leading stars/markers that are just UI decoration
            voiceName = voiceName.TrimStart('★', '☆', ' ');

            var previewText = $"Testing voice {voiceName}. Merry had a happy hamburger, and Anduin is wishy washy.";

            previewBtn.IsEnabled = false;
            previewBtn.Content   = "…";

            try
            {
                var audioPath = await GetOrCreateAudioPathAsync(previewText, slot);

                // Play on current audio device, respecting volume
                await AppServices.Player.PlayAsync(audioPath, default);
            }
            catch (Exception ex)
            {
                SessionStatus.Text = $"Preview failed: {ex.Message}";
            }
            finally
            {
                previewBtn.IsEnabled = true;
                previewBtn.Content   = "▶";
            }
        };

        // ── Mix button (Kokoro only) ───────────────────────────────────────────
        var panel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        panel.Children.Add(combo);
        panel.Children.Add(previewBtn);

        combo.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;

        if (AppServices.Provider is not RuneReaderVoice.TTS.Providers.KokoroTtsProvider)
            return panel;

        var mixBtn = new Button
        {
            Content    = "⚗",
            Width      = 28,
            Height     = 28,
            FontSize   = 13,
            Padding    = new Avalonia.Thickness(0),
            Background = Avalonia.Media.SolidColorBrush.Parse("#0F3460"),
            Foreground = Avalonia.Media.Brushes.LightBlue,
            Margin     = new Avalonia.Thickness(3, 0, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Avalonia.Controls.ToolTip.SetTip(mixBtn, "Edit voice blend");

        mixBtn.Click += async (_, _) =>
        {
            var currentSpec = AppServices.Settings.VoiceAssignments.TryGetValue(key, out var s) ? s : null;
            var dialog      = new VoiceMixDialog(
                RuneReaderVoice.TTS.Providers.KokoroTtsProvider.KnownVoices, currentSpec);

            await dialog.ShowDialog(this);

            if (dialog.ResultSpec == null) return;

            var spec = dialog.ResultSpec;

            // Update or add the "⚗ Custom Mix" item in the combo
            var existingMix = combo.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString()?.StartsWith(
                    RuneReaderVoice.TTS.Providers.KokoroTtsProvider.MixPrefix) == true);

            initializing = true;
            if (spec.StartsWith(RuneReaderVoice.TTS.Providers.KokoroTtsProvider.MixPrefix))
            {
                if (existingMix != null)
                    existingMix.Tag = spec;
                else
                {
                    var mixItem = new ComboBoxItem { Content = "⚗ Custom Mix", Tag = spec };
                    combo.Items.Add(mixItem);
                    combo.SelectedItem = mixItem;
                }
            }
            else if (existingMix != null)
                combo.Items.Remove(existingMix);
            initializing = false;

            ApplyVoiceId(spec);
        };

        panel.Children.Add(mixBtn);
        return panel;
    }

    // Legacy alias used by Narrator (single control) and NPC (male/female) rows
    private ComboBox BuildVoiceCombo(IReadOnlyList<VoiceInfo> voices, VoiceSlot slot)
    {
        // Return just the combo for backward-compat layout — Mix button handled separately
        var control = BuildVoiceControl(voices, slot);
        if (control is ComboBox cb) return cb;
        if (control is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is ComboBox spCb)
            return spCb;
        return new ComboBox();
    }

    /// <summary>
    /// Seeds per-slot voice assignments for Kokoro the first time it is selected.
    /// Only writes slots that have no existing assignment.
    /// </summary>
    private static void ApplyKokoroDefaults()
    {
        var assignments = AppServices.Settings.VoiceAssignments; // scoped to "kokoro"

        void SetDefault(VoiceSlot slot, string voiceId)
        {
            var key = slot.ToString();
            if (!assignments.ContainsKey(key))
                assignments[key] = voiceId;
        }

        // Narrator: 20% Adam / 80% Lewis blend
        SetDefault(new VoiceSlot(AccentGroup.Narrator, Gender.Unknown),
            "mix:am_adam:0.2|bm_lewis:0.8");

        // NeutralAmerican explicit pair
        SetDefault(new VoiceSlot(AccentGroup.NeutralAmerican, Gender.Male),   "am_michael");
        SetDefault(new VoiceSlot(AccentGroup.NeutralAmerican, Gender.Female), "af_sarah");

        // All remaining groups default to Echo (M) / Nova (F)
        foreach (AccentGroup group in Enum.GetValues<AccentGroup>())
        {
            if (group == AccentGroup.Narrator || group == AccentGroup.NeutralAmerican)
                continue;
            SetDefault(new VoiceSlot(group, Gender.Male),   "am_echo");
            SetDefault(new VoiceSlot(group, Gender.Female), "af_nova");
        }

        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }
}