// MainWindow.axaml.cs
// Code-behind for the main RuneReader Voice window.
// Binds to AppServices for live status, wires all control events.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using RuneReaderVoice;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Pronunciation;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer;
    private bool _capturing;
    private bool _pronunciationUiInitializing;

    public MainWindow()
    {
        InitializeComponent();
        PopulateProviderSelector();
        LoadSettingsIntoUI();
        PopulateAudioDevices();


        PopulateVoiceGrid();
        PopulateVolumeTrimGrid();
        SetPlatformVisibility();
        PopulatePronunciationWorkbench();
        PopulateTextSwapWorkbench();

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



    // ── Population helpers ────────────────────────────────────────────────────

    private void LoadSettingsIntoUI()
    {
        var s = AppServices.Settings;
        VolumeSlider.Value  = s.Volume * 100;
        SpeedSlider.Value   = s.PlaybackSpeed * 100;
        SpeedLabel.Text     = $"{s.PlaybackSpeed:F2}×";
        CaptureInterval.Value = s.CaptureIntervalMs;
        RescanInterval.Value  = s.ReScanIntervalMs;
        RepeatSuppressionEnabled.IsChecked = s.RepeatSuppressionEnabled;
        RepeatSuppressionWindow.Value = s.RepeatSuppressionWindowSeconds;
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