using System;
using System.IO;
using System.Linq;
using Avalonia;
using RuneReaderVoice;
using RuneReaderVoice.Platform;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Cache;
using RuneReaderVoice.TTS.Providers;
using RuneReaderVoice.TTS.Audio;
using RuneReaderVoice.Session;
using RuneReaderVoice.TTS.Pronunciation;

namespace RuneReaderVoice;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // ── Load settings ─────────────────────────────────────────────────────
        var settings = VoiceSettingsManager.LoadSettings();

        // ── Platform services ────────────────────────────────────────────────
        var platform = VoicePlatformFactory.Create();

        // ── TTS Provider ─────────────────────────────────────────────────────
        ITtsProvider provider = settings.ActiveProvider switch
        {
    #if WINDOWS
            "winrt"   => new WinRtTtsProvider(),
    #elif LINUX
            "piper"   => new LinuxPiperTtsProvider(
                             settings.PiperBinaryPath,
                             settings.PiperModelDirectory),
    #endif
            "kokoro"  => new KokoroTtsProvider(),
            "cloud"   => new NotImplementedTtsProvider("cloud", "Cloud TTS"),
            _         => CreateDefaultProvider(settings),
        };

    #if WINDOWS
        if (provider is WinRtTtsProvider winRtProvider)
        {
            foreach (var (key, voiceId) in settings.VoiceAssignments)
                if (VoiceSlot.TryParse(key, out var slot))
                    winRtProvider.SetVoice(slot, voiceId);
        }
    #endif

        if (provider is KokoroTtsProvider kokoroProvider)
        {
            foreach (var (key, voiceId) in settings.VoiceAssignments)
                if (VoiceSlot.TryParse(key, out var slot))
                    kokoroProvider.SetVoice(slot, voiceId);
            kokoroProvider.EnablePhraseChunking = settings.EnablePhraseChunking;
        }

        var cacheDir = !string.IsNullOrWhiteSpace(settings.CacheDirectoryOverride)
            ? settings.CacheDirectoryOverride
            : VoiceSettingsManager.GetDefaultCacheDirectory();

        var cache = new TtsAudioCache(
            cacheDir,
            maxSizeBytes:       settings.CacheSizeLimitBytes,
            compressionEnabled: settings.CompressionEnabled,
            oggQuality:         settings.OggQuality,
            silenceTrimEnabled: settings.SilenceTrimEnabled);

        IAudioPlayer player = CreateAudioPlayer();
        player.Volume = settings.Volume;
        player.Speed  = settings.PlaybackSpeed;
        if (settings.AudioDeviceId != null)
            player.SetOutputDevice(settings.AudioDeviceId);

        var assembler = new TtsSessionAssembler();
        var pronunciationProcessor = BuildPronunciationProcessor();

        var tempDir = Path.Combine(Path.GetTempPath(), "RuneReaderVoice");
        var playbackMode = settings.PlaybackMode == "StreamOnFirstChunk"
            ? PlaybackMode.StreamOnFirstChunk
            : PlaybackMode.WaitForFullText;

        var coordinator = new PlaybackCoordinator(
            provider, cache, player, playbackMode, tempDir);

        coordinator.StartSession();

        assembler.OnSegmentComplete += seg =>
        {
            var processed = AppServices.PronunciationProcessor.Process(seg);
            coordinator.EnqueueSegment(processed);
        };

        assembler.OnSessionReset += coordinator.OnSessionReset;

        var monitor = new RvBarcodeMonitor(platform.ScreenCapture);
        monitor.CaptureIntervalMs     = settings.CaptureIntervalMs;
        monitor.ReScanIntervalMs      = settings.ReScanIntervalMs;
        monitor.SourceGoneThresholdMs = settings.SourceGoneThresholdMs;

        monitor.OnPacketDecoded += assembler.Feed;
        monitor.OnSourceGone += () =>
        {
            assembler.SignalSourceGone();
            coordinator.OnSourceGone();
        };

        platform.ScreenCapture.OnFullScreenUpdated += monitor.ProcessFrame;
        platform.ScreenCapture.OnRegionUpdated     += monitor.ProcessFrame;

        platform.Hotkeys.EscPressed += coordinator.HandleEscPressed;
        platform.Hotkeys.Start();

        AppServices.Initialize(
            settings, platform, provider, cache, player,
            assembler, coordinator, monitor, pronunciationProcessor);

        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(
                args,
                Avalonia.Controls.ShutdownMode.OnMainWindowClose);
    }

    private static DialoguePronunciationProcessor BuildPronunciationProcessor()
    {
        var rules = WowPronunciationRules.CreateDefault()
            .Concat(PronunciationRuleStore.LoadUserRules())
            .ToList();

        return new DialoguePronunciationProcessor(rules);
    }

    private static ITtsProvider CreateDefaultProvider(VoiceUserSettings settings)
    {
    #if WINDOWS
        return new WinRtTtsProvider();
    #elif LINUX
        return new LinuxPiperTtsProvider(settings.PiperBinaryPath, settings.PiperModelDirectory);
    #else
        return new NotImplementedTtsProvider("none", "No provider available");
    #endif
    }

    private static IAudioPlayer CreateAudioPlayer()
    {
    #if WINDOWS
        return new WinRtAudioPlayer();
    #elif LINUX
        return new GstAudioPlayer();
    #else
        throw new PlatformNotSupportedException("No audio player for this platform.");
    #endif
    }
}