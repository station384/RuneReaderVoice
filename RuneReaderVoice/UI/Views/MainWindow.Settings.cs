using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

public partial class MainWindow
{
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
            "winrt" => new RuneReaderVoice.TTS.Providers.WinRtTtsProvider(),
#elif LINUX
            "piper"  => new RuneReaderVoice.TTS.Providers.LinuxPiperTtsProvider(
                             AppServices.Settings.PiperBinaryPath,
                             AppServices.Settings.PiperModelDirectory),
#endif
            "kokoro" => new RuneReaderVoice.TTS.Providers.KokoroTtsProvider(),
            _ => oldProvider, // unsupported — leave unchanged
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

    private void OnRepeatSuppressionToggled(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var enabled = RepeatSuppressionEnabled.IsChecked ?? true;
        AppServices.Settings.RepeatSuppressionEnabled = enabled;
        AppServices.Coordinator.RecentSpeechSuppressor.Enabled = enabled;
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
    }

    private void OnRepeatSuppressionWindowChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        var seconds = (int)(e.NewValue ?? 5);
        if (seconds < 0) seconds = 0;

        AppServices.Settings.RepeatSuppressionWindowSeconds = seconds;
        AppServices.Coordinator.RecentSpeechSuppressor.Window = TimeSpan.FromSeconds(seconds);
        VoiceSettingsManager.SaveSettings(AppServices.Settings);
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

}