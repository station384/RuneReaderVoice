// MainWindow.axaml.cs
// Code-behind for the main RuneReader Voice window.
// Binds to AppServices for live status, wires all control events.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using RuneReaderVoice;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer;
    private bool _capturing;

    public MainWindow()
    {
        InitializeComponent();
        PopulateProviderSelector();
        LoadSettingsIntoUI();
        PopulateAudioDevices();
        // Seed Kokoro defaults if it is the active provider and has no assignments yet
        if (AppServices.Settings.ActiveProvider == "kokoro")
            ApplyKokoroDefaults();
        PopulateVoiceGrid();
        PopulateVolumeTrimGrid();
        SetPlatformVisibility();

        // Status refresh timer — 500ms is plenty for UI feedback
        _statusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _statusTimer.Tick += OnStatusTick;
        _statusTimer.Start();

        // Subscribe to assembler events for live session status
        AppServices.Assembler.OnSessionReset    += id => Dispatcher.UIThread.Post(() =>
        {
            SessionStatus.Text = $"Dialog 0x{id:X4}  —  waiting";
            DiagDialog.Text    = $"0x{id:X4}";
        });
        AppServices.Assembler.OnSegmentComplete += seg => Dispatcher.UIThread.Post(() =>
        {
            SessionStatus.Text = $"Dialog 0x{seg.DialogId:X4}  seg {seg.SegmentIndex}  {seg.Slot}";
            DiagDialog.Text    = $"0x{seg.DialogId:X4}";
            DiagLastText.Text  = seg.Text;
        });

        // Wire Kokoro model download feedback if that provider is active
        if (AppServices.Provider is RuneReaderVoice.TTS.Providers.KokoroTtsProvider kokoro)
        {
            kokoro.OnModelDownloading += msg => Dispatcher.UIThread.Post(() =>
            {
                StatusBadge.Text       = "●  Kokoro: downloading model…";
                StatusBadge.Foreground = Avalonia.Media.Brushes.Orange;
                SessionStatus.Text     = msg;
            });
            kokoro.OnModelReady += () => Dispatcher.UIThread.Post(() =>
            {
                StatusBadge.Text       = "●  Kokoro: ready";
                StatusBadge.Foreground = Avalonia.Media.Brushes.LightGreen;
                SessionStatus.Text     = "Model loaded — ready to speak";
            });
        }

        // Restore saved window position
        var s = AppServices.Settings;
        Position = new Avalonia.PixelPoint((int)s.AppStartX, (int)s.AppStartY);

        // Save position whenever the window moves or closes
        PositionChanged += (_, _) =>
        {
            AppServices.Settings.AppStartX = Position.X;
            AppServices.Settings.AppStartY = Position.Y;
            VoiceSettingsManager.SaveSettings(AppServices.Settings);
        };
        Closing += (_, _) =>
        {
            AppServices.Settings.AppStartX = Position.X;
            AppServices.Settings.AppStartY = Position.Y;
            VoiceSettingsManager.SaveSettings(AppServices.Settings);
        };
    }

    // ── Status tick ───────────────────────────────────────────────────────────

    private void OnStatusTick(object? sender, EventArgs e)
    {
        // Capture status
        CaptureStatus.Text      = _capturing ? "Active" : "Stopped";
        CaptureStatus.Foreground = _capturing
            ? Avalonia.Media.Brushes.LightGreen
            : Avalonia.Media.Brushes.IndianRed;

        // Playback state
        bool playing = AppServices.Player.IsPlaying;
        PlaybackStatus.Text      = playing ? "Playing" : "Idle";
        PlaybackStatus.Foreground = playing
            ? Avalonia.Media.Brushes.LightSkyBlue
            : Avalonia.Media.SolidColorBrush.Parse("#4ECDC4");

        // Cache stats
        var cache = AppServices.Cache;
        int total = cache.HitCount + cache.MissCount;
        var hitRate = total > 0 ? $"{cache.HitCount * 100 / total}%" : "—";
        CacheStatus.Text = $"{cache.EntryCount} entries  {cache.TotalSizeBytes / 1024 / 1024} MB  hit {hitRate}";

        // Diagnostics
        var latency = AppServices.Coordinator.LastSynthesisLatency;
        DiagLatency.Text = latency.TotalMilliseconds > 0
            ? $"{latency.TotalMilliseconds:F0} ms"
            : "—";
        DiagHitRate.Text = hitRate;

        CacheStatsLabel.Text =
            $"Cache: {cache.EntryCount} entries, {cache.TotalSizeBytes / 1024 / 1024} MB";
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    private void OnStartStopClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_capturing)
        {
            _ = AppServices.Monitor.StopAsync();
            _capturing              = false;
            StartStopButton.Content = "Start Capture";
            StartStopButton.Background = Avalonia.Media.SolidColorBrush.Parse("#E94560");
        }
        else
        {
            AppServices.Monitor.Start();
            _capturing              = true;
            StartStopButton.Content = "Stop Capture";
            StartStopButton.Background = Avalonia.Media.SolidColorBrush.Parse("#2ECC71");
        }
    }

    // ── Provider ──────────────────────────────────────────────────────────────

    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProviderSelector.SelectedItem is not ComboBoxItem item || item.Tag is not string providerId)
            return;
        if (providerId == AppServices.Settings.ActiveProvider)
            return;

        // ── Swap the active provider ──────────────────────────────────────────
        // Stop any in-progress synthesis/playback first
        AppServices.Coordinator.OnSessionReset(0);

        // Dispose the old provider
        var oldProvider = AppServices.Provider;

        // Build the new provider
        ITtsProvider newProvider = providerId switch
        {
#if WINDOWS
            "winrt"  => new RuneReaderVoice.TTS.Providers.WinRtTtsProvider(),
#elif LINUX
            "piper"  => new RuneReaderVoice.TTS.Providers.LinuxPiperTtsProvider(
                             AppServices.Settings.PiperBinaryPath,
                             AppServices.Settings.PiperModelDirectory),
#endif
            "kokoro" => new RuneReaderVoice.TTS.Providers.KokoroTtsProvider(),
            _         => oldProvider, // unsupported — leave unchanged
        };

        if (newProvider == oldProvider) return; // nothing changed

        // Update settings BEFORE restoring voice assignments (VoiceAssignments property
        // reads ActiveProvider to scope the dict)
        AppServices.Settings.ActiveProvider = providerId;

        // Restore saved voice assignments for this provider into the new provider object
#if WINDOWS
        if (newProvider is RuneReaderVoice.TTS.Providers.WinRtTtsProvider winRt)
            foreach (var (key, voiceId) in AppServices.Settings.VoiceAssignments)
                if (RuneReaderVoice.Protocol.VoiceSlot.TryParse(key, out var slot))
                    winRt.SetVoice(slot, voiceId);
#endif
        if (newProvider is RuneReaderVoice.TTS.Providers.KokoroTtsProvider kokoro)
        {
            foreach (var (key, voiceId) in AppServices.Settings.VoiceAssignments)
                if (RuneReaderVoice.Protocol.VoiceSlot.TryParse(key, out var slot))
                    kokoro.SetVoice(slot, voiceId);

            kokoro.EnablePhraseChunking = AppServices.Settings.EnablePhraseChunking;

            // Wire download feedback for the new Kokoro instance
            kokoro.OnModelDownloading += msg => Dispatcher.UIThread.Post(() =>
            {
                StatusBadge.Text       = "●  Kokoro: downloading model…";
                StatusBadge.Foreground = Avalonia.Media.Brushes.Orange;
                SessionStatus.Text     = msg;
            });
            kokoro.OnModelReady += () => Dispatcher.UIThread.Post(() =>
            {
                StatusBadge.Text       = "●  Kokoro: ready";
                StatusBadge.Foreground = Avalonia.Media.Brushes.LightGreen;
                SessionStatus.Text     = "Model loaded — ready to speak";
            });
        }

        // Hot-swap in AppServices and the coordinator
        AppServices.SwapProvider(newProvider);
        oldProvider.Dispose();

        // No full cache clear needed on provider switch — each provider has its own
        // ProviderId component in the cache key, so entries are naturally isolated.

        VoiceSettingsManager.SaveSettings(AppServices.Settings);

        // Seed defaults for Kokoro if this is the first time it has been selected
        if (providerId == "kokoro")
            ApplyKokoroDefaults();

        // Repopulate the voice grid for the new provider
        PopulateVoiceGrid();
    }

    // ── Basic settings ────────────────────────────────────────────────────────

    private void OnVolumeChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        AppServices.Player.Volume = (float)(e.NewValue / 100.0);
        AppServices.Settings.Volume = AppServices.Player.Volume;
    }

    private void OnSpeedChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        float speed = (float)(e.NewValue / 100.0);
        AppServices.Player.Speed = speed;
        AppServices.Settings.PlaybackSpeed = speed;
        SpeedLabel.Text = $"{speed:F2}×";
    }

    private void OnPlaybackModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PlaybackModeSelector.SelectedItem is ComboBoxItem item && item.Tag is string mode)
        {
            AppServices.Settings.PlaybackMode = mode;
            AppServices.Coordinator.Mode = mode == "StreamOnFirstChunk" 
                ? PlaybackMode.StreamOnFirstChunk 
                : PlaybackMode.WaitForFullText;
            // Hot-swap the coordinator's mode

        }
    }

    private void OnPhraseChunkingChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var enabled = PhraseChunking.IsChecked ?? true;
        AppServices.Settings.EnablePhraseChunking = enabled;
        if (AppServices.Provider is RuneReaderVoice.TTS.Providers.KokoroTtsProvider kokoro)
            kokoro.EnablePhraseChunking = enabled;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnAudioDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AudioDeviceSelector.SelectedItem is ComboBoxItem item)
        {
            var deviceId = item.Tag?.ToString();
            AppServices.Player.SetOutputDevice(deviceId);
            AppServices.Settings.AudioDeviceId = deviceId;
        }
    }

    // ── Advanced settings ─────────────────────────────────────────────────────

    private void OnCaptureIntervalChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        var ms = (int)(e.NewValue ?? 5);
        AppServices.Monitor.CaptureIntervalMs = ms;
        AppServices.Settings.CaptureIntervalMs = ms;
    }

    private void OnRescanIntervalChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        var ms = (int)(e.NewValue ?? 5000);
        AppServices.Monitor.ReScanIntervalMs = ms;
        AppServices.Settings.ReScanIntervalMs = ms;
    }

    private void OnCompressionToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AppServices.Settings.CompressionEnabled = CompressionEnabled.IsChecked ?? true;
        // Note: existing cache entries retain their current format.
        // New entries will use the updated setting.
    }

    private void OnOggQualityChanged(object? sender, Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        AppServices.Settings.OggQuality = (int)e.NewValue;
    }

    private void OnCacheSizeLimitChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        AppServices.Settings.CacheSizeLimitBytes = (long)(e.NewValue ?? 500) * 1024 * 1024;
    }

    private void OnSilenceTrimToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AppServices.Settings.SilenceTrimEnabled = SilenceTrim.IsChecked ?? true;
    }

    private async void OnClearCacheClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await AppServices.Cache.ClearAsync();
    }

    private void OnPiperBinaryPathChanged(object? sender, TextChangedEventArgs e)
    {
        AppServices.Settings.PiperBinaryPath = PiperBinaryPath.Text ?? "";
    }

    private void OnPiperModelDirChanged(object? sender, TextChangedEventArgs e)
    {
        AppServices.Settings.PiperModelDirectory = PiperModelDir.Text ?? "";
        RefreshPiperModelList();
    }

    // ── Population helpers ────────────────────────────────────────────────────

    private void LoadSettingsIntoUI()
    {
        var s = AppServices.Settings;
        VolumeSlider.Value  = s.Volume * 100;
        SpeedSlider.Value   = s.PlaybackSpeed * 100;
        SpeedLabel.Text     = $"{s.PlaybackSpeed:F2}×";
        CaptureInterval.Value = s.CaptureIntervalMs;
        RescanInterval.Value  = s.ReScanIntervalMs;
        CompressionEnabled.IsChecked = s.CompressionEnabled;
        OggQualitySlider.Value = s.OggQuality;
        CacheSizeLimit.Value   = s.CacheSizeLimitBytes / 1024 / 1024;
        SilenceTrim.IsChecked  = s.SilenceTrimEnabled;
        EnableGreeting.IsChecked = s.EnableQuestGreeting;
        EnableDetail.IsChecked   = s.EnableQuestDetail;
        EnableProgress.IsChecked = s.EnableQuestProgress;
        EnableReward.IsChecked   = s.EnableQuestReward;
        EnableBooks.IsChecked    = s.EnableBooks;
        PhraseChunking.IsChecked = s.EnablePhraseChunking;
        PiperBinaryPath.Text     = s.PiperBinaryPath;
        PiperModelDir.Text       = s.PiperModelDirectory;
      
        var playbackMatch = PlaybackModeSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == s.PlaybackMode);
        if (playbackMatch != null)
            PlaybackModeSelector.SelectedItem = playbackMatch;
        else
            PlaybackModeSelector.SelectedIndex = 0;
        
    }

    private void PopulateAudioDevices()
    {
        AudioDeviceSelector.Items.Clear();
        var devices = AppServices.Player.GetOutputDevices();
        foreach (var d in devices)
        {
            AudioDeviceSelector.Items.Add(new ComboBoxItem
            {
                Content = d.DeviceName,
                Tag     = d.DeviceId,
            });
        }
        if (AudioDeviceSelector.Items.Count > 0)
            AudioDeviceSelector.SelectedIndex = 0;
    }

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
                // Synthesize directly — bypass the session queue so it doesn't
                // interfere with any live dialog that might be playing.
                var outPath = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"rrv_preview_{System.Guid.NewGuid():N}.wav");

                await AppServices.Provider.SynthesizeToFileAsync(
                    previewText, slot, outPath, default);

                // Play on current audio device, respecting volume
                await AppServices.Player.PlayAsync(outPath, default);
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

    private void PopulateProviderSelector()
    {
        ProviderSelector.Items.Clear();

#if WINDOWS
        ProviderSelector.Items.Add(new ComboBoxItem { Content = "Windows Speech (WinRT)", Tag = "winrt" });
#elif LINUX
        ProviderSelector.Items.Add(new ComboBoxItem { Content = "Piper (Local ONNX)", Tag = "piper" });
#endif
        ProviderSelector.Items.Add(new ComboBoxItem { Content = "Kokoro (Local ONNX)", Tag = "kokoro" });
        ProviderSelector.Items.Add(new ComboBoxItem { Content = "Cloud TTS — coming soon", Tag = "cloud" });

        // Restore saved selection
        var saved = AppServices.Settings.ActiveProvider;
        var match = ProviderSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == saved);
        if (match != null)
            ProviderSelector.SelectedItem = match;
        else
            ProviderSelector.SelectedIndex = 0;
    }
    

    
    
    private void PopulateVolumeTrimGrid()
    {
        VolumeTrimGrid.Children.Clear();
        foreach (AccentGroup group in Enum.GetValues<AccentGroup>())
        {
            var key = group == AccentGroup.Narrator
                ? VoiceSlot.Narrator.ToString()
                : $"{group}/Male"; // use same key pattern as voice slot

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("2*,*,Auto") };
            AddGridLabel(row, group.ToString(), 0, Avalonia.Media.Brushes.LightGray);

            var slider = new Slider
            {
                Minimum = -12, Maximum = 12,
                Value = AppServices.Settings.VolumeTrimDb.TryGetValue(key, out var db) ? db : 0,
                Width = 100,
            };
            var label = new TextBlock
            {
                Text = $"{slider.Value:+0.0;-0.0;0.0} dB",
                Foreground = Avalonia.Media.Brushes.LightGray,
                FontSize = 10,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Width = 52,
            };

            slider.ValueChanged += (_, e) =>
            {
                label.Text = $"{e.NewValue:+0.0;-0.0;0.0} dB";
                AppServices.Settings.VolumeTrimDb[key] = (float)e.NewValue;
            };

            Grid.SetColumn(slider, 1);
            Grid.SetColumn(label,  2);
            row.Children.Add(slider);
            row.Children.Add(label);
            VolumeTrimGrid.Children.Add(row);
        }
    }

    private static void AddGridLabel(Grid grid, string text, int column,
        Avalonia.Media.IBrush brush)
    {
        var tb = new TextBlock
        {
            Text       = text,
            Foreground = brush,
            FontSize   = 11,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    private void SetPlatformVisibility()
    {
#if LINUX
        PiperSettings.IsVisible = true;
        RefreshPiperModelList();
#else
        PiperSettings.IsVisible = false;
#endif
    }

    private void RefreshPiperModelList()
    {
#if LINUX
        PiperModelList.Items.Clear();
        var dir = AppServices.Settings.PiperModelDirectory;
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir, "*.onnx"))
        {
            var fi   = new System.IO.FileInfo(f);
            var name = Path.GetFileNameWithoutExtension(f);
            PiperModelList.Items.Add($"{name}  ({fi.Length / 1024 / 1024} MB)");
        }
#endif
    }

    protected override void OnClosing(Avalonia.Controls.WindowClosingEventArgs e)
    {
        _statusTimer.Stop();
        // Save settings on window close
        _ = VoiceSettingsManager.SaveSettingsAsync(AppServices.Settings);
        base.OnClosing(e);
    }
}