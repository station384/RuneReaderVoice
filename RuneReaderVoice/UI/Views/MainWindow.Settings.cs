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
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;
// MainWindow.Settings.cs
// Settings tab handlers, provider swapping, and capture/playback preferences.
public partial class MainWindow
{
    private void OnProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ProviderSelector.SelectedItem is not ComboBoxItem item)
            return;

        var tag = item.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(tag))
            return;

        if (AppServices.Settings.ActiveProvider == tag)
            return;

        AppServices.Settings.ActiveProvider = tag;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
        SwapActiveProvider();
    }


    private void OnRemoteServerUrlChanged(object? sender, TextChangedEventArgs e)
    {
        AppServices.Settings.RemoteServerUrl = RemoteServerUrl.Text ?? string.Empty;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnRemoteApiKeyChanged(object? sender, TextChangedEventArgs e)
    {
        AppServices.Settings.RemoteApiKey = RemoteApiKey.Text ?? string.Empty;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnContributeKeyChanged(object? sender, TextChangedEventArgs e)
    {
        AppServices.Settings.ContributeKey = ContributeKeyBox.Text ?? string.Empty;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnAdminKeyChanged(object? sender, TextChangedEventArgs e)
    {
        AppServices.Settings.AdminKey = AdminKeyBox.Text ?? string.Empty;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnContributeByDefaultChanged(object? sender, RoutedEventArgs e)
    {
        AppServices.Settings.ContributeByDefault = ContributeByDefaultCheck.IsChecked == true;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnFirstLoadCompleteChanged(object? sender, RoutedEventArgs e)
    {
        AppServices.Settings.FirstLoadComplete = FirstLoadCompleteCheck.IsChecked == true;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private async void OnRefreshRemoteProvidersClicked(object? sender, RoutedEventArgs e)
    {
        RemoteRefreshProvidersButton.IsEnabled = false;
        try
        {
            var client = new RemoteTtsClient(AppServices.Settings.RemoteServerUrl, AppServices.Settings.RemoteApiKey);
            var providers = await client.GetProvidersAsync(default);
            AppServices.Settings.RemoteProviderCatalogJson = RemoteProviderCatalog.Serialize(providers);
            VoiceSettingsManager.SaveSettings(AppServices.Settings);
            AppServices.ProviderRegistry = TtsProviderFactory.BuildRegistry(AppServices.Settings);
            PopulateProviderSelector();
            UpdateRemoteProvidersStatus();
            SessionStatus.Text = $"Loaded {providers.Count} remote provider(s).";
        }
        catch (Exception ex)
        {
            RemoteProvidersStatus.Text = "Remote refresh failed.";
            SessionStatus.Text = $"Remote provider refresh failed: {ex.Message}";
        }
        finally
        {
            RemoteRefreshProvidersButton.IsEnabled = true;
        }
    }
    private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var volume = (float)(e.NewValue / 100.0);
        AppServices.Settings.Volume = volume;
        AppServices.Player.Volume = volume;
        VolumeLabel.Text = $"{(int)e.NewValue}%";
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnSpeedChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var speed = (float)(e.NewValue / 100.0);
        AppServices.Settings.PlaybackSpeed = speed;
        AppServices.Player.Speed = speed;
        SpeedLabel.Text = $"{speed:F2}×";
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnPlaybackModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PlaybackModeSelector.SelectedItem is not ComboBoxItem item)
            return;

        var mode = item.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(mode))
            return;

        AppServices.Settings.PlaybackMode = mode;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnPhraseChunkingChanged(object? sender, RoutedEventArgs e)
    {
        var enabled = PhraseChunking.IsChecked == true;
        AppServices.Settings.EnablePhraseChunking = enabled;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnAudioDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (AudioDeviceSelector.SelectedItem is not ComboBoxItem item)
            return;

        var deviceId = item.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        AppServices.Settings.AudioDeviceId = deviceId;
        AppServices.Player.SetOutputDevice(deviceId);
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnCaptureIntervalChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!e.NewValue.HasValue) return;

        var clamped = Math.Clamp((int)e.NewValue.Value, 4, 100);
        if (CaptureInterval.Value != clamped)
            CaptureInterval.Value = clamped;
        AppServices.Settings.CaptureIntervalMs = clamped;
        AppServices.Monitor.CaptureIntervalMs = clamped;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnRescanIntervalChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!e.NewValue.HasValue) return;

        AppServices.Settings.ReScanIntervalMs = (int)e.NewValue.Value;
        AppServices.Monitor.ReScanIntervalMs = (int)e.NewValue.Value;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnRepeatSuppressionToggled(object? sender, RoutedEventArgs e)
    {
        var enabled = RepeatSuppressionEnabled.IsChecked == true;
        AppServices.Settings.RepeatSuppressionEnabled = enabled;
        AppServices.Coordinator.RecentSpeechSuppressor.Enabled = enabled;
        AppServices.Coordinator.RecentSpeechSuppressor.Window =
            System.TimeSpan.FromSeconds(System.Math.Max(0, AppServices.Settings.RepeatSuppressionWindowSeconds));
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnRepeatSuppressionWindowChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!e.NewValue.HasValue) return;

        var seconds = (int)e.NewValue.Value;
        AppServices.Settings.RepeatSuppressionWindowSeconds = seconds;
        AppServices.Coordinator.RecentSpeechSuppressor.Enabled = AppServices.Settings.RepeatSuppressionEnabled;
        AppServices.Coordinator.RecentSpeechSuppressor.Window =
            System.TimeSpan.FromSeconds(System.Math.Max(0, seconds));
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnCompressionToggled(object? sender, RoutedEventArgs e)
    {
        AppServices.Settings.CompressionEnabled = CompressionEnabled.IsChecked == true;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnOggQualityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        AppServices.Settings.OggQuality = (int)e.NewValue;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnCacheSizeLimitChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!e.NewValue.HasValue) return;

        AppServices.Settings.CacheSizeLimitBytes = (long)e.NewValue.Value * 1024L * 1024L;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnSilenceTrimToggled(object? sender, RoutedEventArgs e)
    {
        AppServices.Settings.SilenceTrimEnabled = SilenceTrim.IsChecked == true;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private async  void OnClearCacheClicked(object? sender, RoutedEventArgs e)
    {
        await AppServices.Cache.ClearAsync();
    }

    private void OnPiperBinaryPathChanged(object? sender, TextChangedEventArgs e)
    {
        AppServices.Settings.PiperBinaryPath = PiperBinaryPath.Text ?? "";
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnPiperModelDirChanged(object? sender, TextChangedEventArgs e)
    {
        AppServices.Settings.PiperModelDirectory = PiperModelDir.Text ?? "";
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void SwapActiveProvider()
    {
        var oldProvider = AppServices.Provider;
        if (oldProvider is System.IDisposable disposable)
            disposable.Dispose();

        var descriptor = AppServices.ProviderRegistry.Get(AppServices.Settings.ActiveProvider);
        if (descriptor == null)
        {
            SessionStatus.Text = $"Provider '{AppServices.Settings.ActiveProvider}' is not registered.";
            return;
        }

        var newProvider = TtsProviderFactory.CreateProvider(AppServices.Settings, descriptor);
        TtsProviderFactory.ApplyStoredProfiles(AppServices.Settings, newProvider);
        HookProviderStatusCallbacks(newProvider);

        AppServices.SwapProvider(newProvider);
        PopulateVoiceGrid();
        PopulateLastNpcSampleDropdown();
        _ = PopulateSampleDefaultsGridAsync();
    }

    private void HookProviderStatusCallbacks(ITtsProvider provider)
    {
        if (provider is KokoroTtsProvider kokoro)
        {
            kokoro.OnModelDownloading += msg => Dispatcher.UIThread.Post(() =>
            {
                StatusBadge.Text = "●  Kokoro: downloading model…";
                StatusBadge.Foreground = Avalonia.Media.Brushes.Orange;
                SessionStatus.Text = msg;
            });

            kokoro.OnModelReady += () => Dispatcher.UIThread.Post(() =>
            {
                StatusBadge.Text = "●  Kokoro: ready";
                StatusBadge.Foreground = Avalonia.Media.Brushes.LightGreen;
                SessionStatus.Text = "Model loaded — ready to speak";
            });
            return;
        }

        if (provider is RemoteTtsProvider)
        {
            StatusBadge.Text = "●  Remote provider";
            StatusBadge.Foreground = Avalonia.Media.Brushes.LightSkyBlue;
        }
    }
}