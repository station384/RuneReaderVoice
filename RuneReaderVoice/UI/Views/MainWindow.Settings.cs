using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS.Providers;

namespace RuneReaderVoice.UI.Views;

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

        if (AppServices.Provider is KokoroTtsProvider kokoro)
            kokoro.EnablePhraseChunking = enabled;

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

        AppServices.Settings.CaptureIntervalMs = (int)e.NewValue.Value;
        AppServices.Monitor.CaptureIntervalMs = (int)e.NewValue.Value;
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

        ITtsProvider newProvider = AppServices.Settings.ActiveProvider switch
        {
#if WINDOWS
            "winrt" => new WinRtTtsProvider(),
#elif LINUX
            "piper" => new LinuxPiperTtsProvider(
                AppServices.Settings.PiperBinaryPath,
                AppServices.Settings.PiperModelDirectory),
#endif
            "kokoro" => new KokoroTtsProvider(),
            "cloud" => new NotImplementedTtsProvider("cloud", "Cloud TTS"),
            _ => AppServices.Provider
        };

#if WINDOWS
        if (newProvider is WinRtTtsProvider winRt)
        {
            foreach (var (key, profile) in AppServices.Settings.VoiceProfiles)
                if (VoiceSlot.TryParse(key, out var slot))
                    winRt.SetVoice(slot, profile.VoiceId);
        }
#elif LINUX
        if (newProvider is LinuxPiperTtsProvider piper)
        {
            foreach (var (key, profile) in AppServices.Settings.VoiceProfiles)
                if (VoiceSlot.TryParse(key, out var slot))
                    piper.SetModel(slot, profile.VoiceId);
        }
#endif

        if (newProvider is KokoroTtsProvider kokoro)
        {
            foreach (var (key, profile) in AppServices.Settings.VoiceProfiles)
                if (VoiceSlot.TryParse(key, out var slot))
                    kokoro.SetVoiceProfile(slot, profile);

            kokoro.EnablePhraseChunking = AppServices.Settings.EnablePhraseChunking;

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

        AppServices.SwapProvider(newProvider);
        PopulateVoiceGrid();
    }
}