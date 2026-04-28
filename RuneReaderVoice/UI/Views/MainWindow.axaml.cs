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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RuneReaderVoice;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Pronunciation;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;
// MainWindow.axaml.cs
// Code-behind for the main RuneReader Voice window.
// Binds to AppServices for live status, wires all control events.
public partial class MainWindow : Window
{
    private readonly DispatcherTimer _statusTimer;
    private bool _capturing;
    private bool _pronunciationUiInitializing;
    private bool _uiInitializing;

    public MainWindow()
    {
        InitializeComponent();
        PopulateProviderSelector();
        _uiInitializing = true;
        LoadSettingsIntoUI();
        _uiInitializing = false;
        WireExpanderStateSaving();
        PopulateAudioDevices();
        PopulateVoiceGrid();
        InitRaceEditorUi();
        _ = PopulateSampleDefaultsGridAsync();
        PopulateVolumeTrimGrid();
        SetPlatformVisibility();
        PopulatePronunciationWorkbench();
        PopulateTextSwapWorkbench();



        InitNpcOverridesUI();
        HookNpcSyncEvents();
        InitUpdatePanel();

        // Warm the voice cache in the background so the NPC sample dropdown
        // is populated as soon as possible — without requiring the user to
        // visit the Voices tab first.
        _ = Task.Run(async () =>
        {
            try
            {
                if (AppServices.Provider is TTS.Providers.RemoteTtsProvider remote)
                {
                    System.Diagnostics.Debug.WriteLine("[MainWindow] Background voice cache warmup starting");
                    await remote.RefreshVoiceSourcesAsync(System.Threading.CancellationToken.None);
                    System.Diagnostics.Debug.WriteLine(
                        $"[MainWindow] Voice cache warmed: {remote.GetAvailableVoices().Count} voices");
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        PopulateLastNpcSampleDropdown();
                        _ = PopulateSampleDefaultsGridAsync();
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Voice cache warmup failed: {ex.Message}");
            }
        });

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
            PlayerStatus.Text  = string.IsNullOrWhiteSpace(AppServices.CurrentPlayerName) ? "—" : AppServices.CurrentPlayerName;
        });
        AppServices.Assembler.OnSegmentComplete += seg => Dispatcher.UIThread.Post(() =>
        {
            SessionStatus.Text = $"Dialog 0x{seg.DialogId:X4}  seg {seg.SegmentIndex}  {GetDisplaySlotLabel(seg.Slot)}";
            DiagDialog.Text    = $"0x{seg.DialogId:X4}";
            PlayerStatus.Text  = string.IsNullOrWhiteSpace(AppServices.CurrentPlayerName) ? "—" : AppServices.CurrentPlayerName;
        });

        HookProviderStatusCallbacks(AppServices.Provider);
        AppServices.MainActivityChanged += OnMainActivityChanged;

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
        CaptureStatus.Text       = _capturing ? "Active" : "Stopped";
        CaptureStatus.Foreground = _capturing
            ? Avalonia.Media.Brushes.LightGreen
            : Avalonia.Media.Brushes.IndianRed;

        // Playback state
        var resolved = AppServices.GetResolvedMainActivity();
        if (resolved.IsActive)
        {
            PlaybackStatus.Text = string.IsNullOrWhiteSpace(resolved.Detail)
                ? resolved.Headline
                : $"{resolved.Headline} • {resolved.Detail}";
            PlaybackStatus.Foreground = resolved.Kind switch
            {
                MainActivityKind.Playing => Avalonia.Media.Brushes.LightSkyBlue,
                MainActivityKind.Capturing => Avalonia.Media.Brushes.LightGreen,
                MainActivityKind.UpdateAvailable => Avalonia.Media.Brushes.Orange,
                _ => Avalonia.Media.Brushes.Gold,
            };
        }
        else
        {
            bool playing = AppServices.Player.IsPlaying;
            PlaybackStatus.Text = playing ? "Playing" : "Idle";
            PlaybackStatus.Foreground = playing
                ? Avalonia.Media.Brushes.LightSkyBlue
                : Avalonia.Media.SolidColorBrush.Parse("#4ECDC4");
        }

        // Cache stats
        var cache = AppServices.Cache;
        int total = cache.HitCount + cache.MissCount;
        var hitRate = total > 0 ? $"{cache.HitCount * 100 / total}%" : "—";
        CacheStatus.Text = $"{cache.EntryCount} entries  {cache.TotalSizeBytes / 1024 / 1024} MB  hit {hitRate}";
        PlayerStatus.Text = string.IsNullOrWhiteSpace(AppServices.CurrentPlayerName) ? "—" : AppServices.CurrentPlayerName;

        // Diagnostics
        var latency = AppServices.Coordinator.LastSynthesisLatency;
        DiagLatency.Text = latency.TotalMilliseconds > 0
            ? $"{latency.TotalMilliseconds:F0} ms"
            : "—";

        DiagLastDecodedText.Text = string.IsNullOrEmpty(AppServices.LastDecodedText) ? "—" : AppServices.LastDecodedText;
        DiagProcessedText.Text   = string.IsNullOrEmpty(AppServices.LastProcessedText) ? "—" : AppServices.LastProcessedText;
        DiagTextSpoken.Text      = string.IsNullOrEmpty(AppServices.LastTextSpoken) ? "—" : AppServices.LastTextSpoken;
        DiagHitRate.Text = hitRate;

        CacheStatsLabel.Text =
            $"Cache: {cache.EntryCount} entries, {cache.TotalSizeBytes / 1024 / 1024} MB";
    }


    private void OnMainActivityChanged(MainActivityState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (state.IsActive)
            {
                PlaybackStatus.Text = string.IsNullOrWhiteSpace(state.Detail)
                    ? state.Headline
                    : $"{state.Headline} • {state.Detail}";
                PlaybackStatus.Foreground = state.Kind == MainActivityKind.Playing
                    ? Avalonia.Media.Brushes.LightSkyBlue
                    : Avalonia.Media.Brushes.Gold;
            }
            else
            {
                PlaybackStatus.Text = AppServices.Player.IsPlaying ? "Playing" : "Idle";
                PlaybackStatus.Foreground = AppServices.Player.IsPlaying
                    ? Avalonia.Media.Brushes.LightSkyBlue
                    : Avalonia.Media.SolidColorBrush.Parse("#4ECDC4");
            }
        });
    }

    // ── Start / Stop ──────────────────────────────────────────────────────────

    private void OnStartStopClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_capturing)
        {
            _ = AppServices.Monitor.StopAsync();
            _capturing              = false;
            AppServices.ClearCaptureActivity();
            StartStopButton.Content = "Start";
            StartStopButton.Background = Avalonia.Media.SolidColorBrush.Parse("#E94560");
        }
        else
        {
            AppServices.Monitor.Start();
            _capturing              = true;
            AppServices.SetCaptureActivity("Monitoring screen…");
            StartStopButton.Content = "Stop";
            StartStopButton.Background = Avalonia.Media.SolidColorBrush.Parse("#2ECC71");
        }
    }


    private void UpdatePlayerNameReplacementUi()
    {
        var mode = AppServices.Settings.PlayerNameMode ?? "generic";
        bool useGeneric = string.Equals(mode, "generic", StringComparison.OrdinalIgnoreCase);
        bool useActual = string.Equals(mode, "actual", StringComparison.OrdinalIgnoreCase);

        PlayerNamePresetSelector.IsEnabled = useGeneric;
        PlayerNameAppendRealmCheck.IsEnabled = useGeneric || useActual;
        PlayerNameEnableTitleCheck.IsEnabled = true;
    }

    private static string GetDisplaySlotLabel(VoiceSlot slot)
        => AppServices.NpcPeopleCatalog?.GetSlotLabel(slot)
           ?? slot.ToString();

    // ── Population helpers ────────────────────────────────────────────────────

    private void LoadSettingsIntoUI()
    {
        var s = AppServices.Settings;
        VolumeSlider.Value  = s.Volume * 100;
        VolumeLabel.Text    = $"{(int)(s.Volume * 100)}%";
        SpeedSlider.Value   = s.PlaybackSpeed * 100;
        SpeedLabel.Text     = $"{s.PlaybackSpeed:F2}×";
        CaptureInterval.Value = Math.Clamp(s.CaptureIntervalMs, 4, 100);
        RescanInterval.Value  = s.ReScanIntervalMs;
        RepeatSuppressionEnabled.IsChecked = s.RepeatSuppressionEnabled;
        RepeatSuppressionWindow.Value = s.RepeatSuppressionWindowSeconds;
        var playerNameMode = (s.PlayerNameMode ?? "generic").Trim().ToLowerInvariant();
        if (playerNameMode == "split")
            playerNameMode = "actual";

        foreach (var item in PlayerNameModeSelector.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), playerNameMode, StringComparison.OrdinalIgnoreCase))
            {
                PlayerNameModeSelector.SelectedItem = item;
                break;
            }
        }
        foreach (var item in PlayerNamePresetSelector.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), s.PlayerNameReplacementPreset, StringComparison.OrdinalIgnoreCase))
            {
                PlayerNamePresetSelector.SelectedItem = item;
                break;
            }
        }
        PlayerNameAppendRealmCheck.IsChecked = s.PlayerNameAppendRealm;
        PlayerNameEnableTitleCheck.IsChecked = s.PlayerNameEnableTitle;
        UpdatePlayerNameReplacementUi();
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
        RemoteServerUrl.Text     = s.RemoteServerUrl;
        RemoteApiKey.Text        = s.RemoteApiKey;
        ContributeKeyBox.Text    = s.ContributeKey;
        AdminKeyBox.Text         = s.AdminKey;
        ContributeByDefaultCheck.IsChecked = s.ContributeByDefault;
        FirstLoadCompleteCheck.IsChecked   = s.FirstLoadComplete;
        UpdateRemoteProvidersStatus();
        UpdateProviderSensitiveUi();

        var playbackMatch = PlaybackModeSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == s.PlaybackMode);
        if (playbackMatch != null)
            PlaybackModeSelector.SelectedItem = playbackMatch;
        else
            PlaybackModeSelector.SelectedIndex = 0;

        // Restore expander states (all default to collapsed if not in settings)
        RestoreExpanders(s);
    }

    // ── Expander state persistence ────────────────────────────────────────────

    private void RestoreExpanders(VoiceUserSettings s)
    {
        ExpanderSettings.IsExpanded      = s.GetExpanderState(nameof(ExpanderSettings));
        ExpanderProvider.IsExpanded      = s.GetExpanderState(nameof(ExpanderProvider));
        ExpanderPlayback.IsExpanded      = s.GetExpanderState(nameof(ExpanderPlayback));
        ExpanderDialogSources.IsExpanded = s.GetExpanderState(nameof(ExpanderDialogSources));
        ExpanderCapture.IsExpanded       = s.GetExpanderState(nameof(ExpanderCapture));
        ExpanderAdvPlayback.IsExpanded   = s.GetExpanderState(nameof(ExpanderAdvPlayback));
        ExpanderCache.IsExpanded         = s.GetExpanderState(nameof(ExpanderCache));
        ExpanderAudio.IsExpanded         = s.GetExpanderState(nameof(ExpanderAudio));
        ExpanderDiagnostics.IsExpanded   = s.GetExpanderState(nameof(ExpanderDiagnostics));
        ExpanderHotkey.IsExpanded        = s.GetExpanderState(nameof(ExpanderHotkey));
    }

    private void WireExpanderStateSaving()
    {
        WireExpander(ExpanderSettings,      nameof(ExpanderSettings));
        WireExpander(ExpanderProvider,      nameof(ExpanderProvider));
        WireExpander(ExpanderPlayback,      nameof(ExpanderPlayback));
        WireExpander(ExpanderDialogSources, nameof(ExpanderDialogSources));
        WireExpander(ExpanderCapture,       nameof(ExpanderCapture));
        WireExpander(ExpanderAdvPlayback,   nameof(ExpanderAdvPlayback));
        WireExpander(ExpanderCache,         nameof(ExpanderCache));
        WireExpander(ExpanderAudio,         nameof(ExpanderAudio));
        WireExpander(ExpanderDiagnostics,   nameof(ExpanderDiagnostics));
        WireExpander(ExpanderHotkey,        nameof(ExpanderHotkey));
    }

    private void WireExpander(Expander expander, string name)
    {
        expander.PropertyChanged += (_, e) =>
        {
            if (e.Property == Expander.IsExpandedProperty)
            {
                AppServices.Settings.SetExpanderState(name, expander.IsExpanded);
                VoiceSettingsManager.SaveSettings(AppServices.Settings);

                // When the main settings expander opens or closes, update the
                // TabControl height cap and, if not maximized, snap window height.
                // if (expander == ExpanderSettings)
                //     UpdateSettingsPanelHeight();
            }
        };
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

        foreach (var descriptor in AppServices.ProviderRegistry.All())
        {
            ProviderSelector.Items.Add(new ComboBoxItem
            {
                Content = descriptor.DisplayName,
                Tag = descriptor.ClientProviderId,
            });
        }

        var saved = AppServices.Settings.ActiveProvider;
        var match = ProviderSelector.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == saved);
        if (match != null)
            ProviderSelector.SelectedItem = match;
        else if (ProviderSelector.Items.Count > 0)
            ProviderSelector.SelectedIndex = 0;
    }

    private void UpdateRemoteProvidersStatus()
    {
        var remoteCount = AppServices.ProviderRegistry.All().Count(p => p.TransportKind == ProviderTransportKind.Remote);
        RemoteProvidersStatus.Text = remoteCount > 0
            ? $"Loaded {remoteCount} remote provider(s)."
            : "Remote providers not loaded.";
    }

    private void PopulateVolumeTrimGrid()
    {
        VolumeTrimGrid.Children.Clear();
        foreach (AccentGroup group in Enum.GetValues<AccentGroup>())
        {
            var key = group == AccentGroup.Narrator
                ? VoiceSlot.Narrator.ToString()
                : $"{group}/Male";

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


    private void HookNpcSyncEvents()
    {
        if (AppServices.NpcSync == null)
            return;

        AppServices.NpcSync.NpcRecordsMerged -= OnNpcRecordsMergedFromSync;
        AppServices.NpcSync.NpcRecordsMerged += OnNpcRecordsMergedFromSync;
    }

    private void OnNpcRecordsMergedFromSync(int mergedCount)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                RefreshNpcOverridesGrid();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NpcPanel] Refresh after sync failed: {ex.Message}");
            }
        });
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
        if (AppServices.NpcSync != null)
            AppServices.NpcSync.NpcRecordsMerged -= OnNpcRecordsMergedFromSync;
        _statusTimer.Stop();
        _ = VoiceSettingsManager.SaveSettingsAsync(AppServices.Settings);
        base.OnClosing(e);
    }

    private void Control_OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        SizeToContent = SizeToContent.Height;
    }


    private void ExpanderSettings_OnCollapsed(object? sender, RoutedEventArgs e)
    {

        PrimaryDisplayWindow.CanResize = false;
    }

    private void ExpanderSettings_OnExpanded(object? sender, RoutedEventArgs e)
    {

        PrimaryDisplayWindow.CanResize = true;
    }
}