using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using RuneReaderVoice;
using RuneReaderVoice.Data;
using RuneReaderVoice.Platform;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Cache;
using RuneReaderVoice.TTS.Providers;
using RuneReaderVoice.TTS.Audio;
using RuneReaderVoice.Session;
using RuneReaderVoice.TTS.Pronunciation;
using RuneReaderVoice.TTS.TextSwap;


namespace RuneReaderVoice;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
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
            foreach (var (key, profile) in settings.VoiceProfiles)
                if (VoiceSlot.TryParse(key, out var slot))
                    winRtProvider.SetVoice(slot, profile.VoiceId);
        }
    #elif LINUX
        if (provider is LinuxPiperTtsProvider piperProvider)
        {
            foreach (var (key, profile) in settings.VoiceProfiles)
                if (VoiceSlot.TryParse(key, out var slot))
                    piperProvider.SetModel(slot, profile.VoiceId);
        }
    #endif

        if (provider is KokoroTtsProvider kokoroProvider)
        {
            foreach (var (key, profile) in settings.VoiceProfiles)
                if (VoiceSlot.TryParse(key, out var slot))
                    kokoroProvider.SetVoiceProfile(slot, profile);
            kokoroProvider.EnablePhraseChunking = settings.EnablePhraseChunking;
        }

        // ── Unified SQLite DB ─────────────────────────────────────────────────
        var dbPath = Path.Combine(VoiceSettingsManager.GetConfigDirectory(), "runereader-voice.db");
        var db = new RvrDb(dbPath);
        await db.InitializeAsync();

        var pronunciationRules = new PronunciationRuleStore(db);
        var textSwapRules      = new TextSwapRuleStore(db);

        // ── Audio cache ───────────────────────────────────────────────────────
        var cacheDir = !string.IsNullOrWhiteSpace(settings.CacheDirectoryOverride)
            ? settings.CacheDirectoryOverride
            : VoiceSettingsManager.GetDefaultCacheDirectory();

        var cache = new TtsAudioCache(
            cacheDir,
            db,
            maxSizeBytes:       settings.CacheSizeLimitBytes,
            compressionEnabled: settings.CompressionEnabled,
            oggQuality:         settings.OggQuality,
            silenceTrimEnabled: settings.SilenceTrimEnabled);

        IAudioPlayer player = CreateAudioPlayer();
        player.Volume = settings.Volume;
        player.Speed  = settings.PlaybackSpeed;
        if (settings.AudioDeviceId != null)
            player.SetOutputDevice(settings.AudioDeviceId);

        // ── NPC race override DB ──────────────────────────────────────────────
        var npcOverrides = new NpcRaceOverrideDb(db);
        await npcOverrides.InitializeAsync();
        var assembler = new TtsSessionAssembler(npcOverrides);
        await assembler.LoadOverridesAsync();

        var textSwapProcessor      = await BuildTextSwapProcessorAsync(textSwapRules);
        var pronunciationProcessor = await BuildPronunciationProcessorAsync(pronunciationRules);

        var tempDir = Path.Combine(Path.GetTempPath(), "RuneReaderVoice");
        var playbackMode = settings.PlaybackMode == "StreamOnFirstChunk"
            ? PlaybackMode.StreamOnFirstChunk
            : PlaybackMode.WaitForFullText;

        var recentSpeechSuppressor = new RecentSpeechSuppressor
        {
            Enabled = settings.RepeatSuppressionEnabled,
            Window  = TimeSpan.FromSeconds(Math.Max(0, settings.RepeatSuppressionWindowSeconds))
        };

        var coordinator = new PlaybackCoordinator(
            provider, cache, player, playbackMode, tempDir, recentSpeechSuppressor);

        coordinator.StartSession();

        assembler.OnSegmentComplete += seg =>
        {
            AppServices.LastDecodedText = seg.Text ?? string.Empty;
            AppServices.LastRuntimeSlot = seg.Slot;
            var activeProvider = AppServices.Provider;
            var shapedText = AppServices.TextSwapProcessor.Process(seg.Text);
            var shapedSegment = new AssembledSegment
            {
                Text         = shapedText,
                Slot         = seg.Slot,
                DialogId     = seg.DialogId,
                SegmentIndex = seg.SegmentIndex,
                NpcId        = seg.NpcId,
            };
            var processed = activeProvider.SupportsInlinePronunciationHints
                ? AppServices.PronunciationProcessor.Process(shapedSegment)
                : shapedSegment;

            AppServices.LastProcessedText = processed.Text ?? string.Empty;
            AppServices.LastTextSpoken    = processed.Text ?? string.Empty;

            coordinator.EnqueueSegment(processed);
        };

        assembler.OnSessionReset += coordinator.OnSessionReset;

        var monitor = new RvBarcodeMonitor(platform.ScreenCapture);
        monitor.TrySetInitialLockedRegion(settings.LastBarcodeRegion);
        monitor.CaptureIntervalMs     = settings.CaptureIntervalMs;
        monitor.ReScanIntervalMs      = settings.ReScanIntervalMs;
        monitor.SourceGoneThresholdMs = settings.SourceGoneThresholdMs;

        monitor.OnPacketDecoded += assembler.Feed;
        monitor.OnSourceGone += () =>
        {
            assembler.SignalSourceGone();
            coordinator.OnSourceGone();
        };
        monitor.OnLockedRegionChanged += rect =>
        {
            settings.LastBarcodeRegion = new SavedBarcodeRegion
            {
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height,
                ScreenWidth = platform.ScreenCapture.ScreenWidth,
                ScreenHeight = platform.ScreenCapture.ScreenHeight,
            };
            _ = VoiceSettingsManager.SaveSettingsAsync(settings);
        };

        platform.ScreenCapture.OnFullScreenUpdated += monitor.ProcessFrame;
        platform.ScreenCapture.OnRegionUpdated     += monitor.ProcessFrameRegion;

        platform.Hotkeys.EscPressed += coordinator.HandleEscPressed;
        platform.Hotkeys.Start();

        AppServices.Initialize(
            settings, platform, provider, cache, player,
            assembler, coordinator, monitor, pronunciationProcessor, textSwapProcessor,
            npcOverrides, db, pronunciationRules, textSwapRules);

        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(
                args,
                Avalonia.Controls.ShutdownMode.OnMainWindowClose);
    }

    private static async Task<DialogueTextSwapProcessor> BuildTextSwapProcessorAsync(TextSwapRuleStore store)
    {
        var userRules = await store.LoadUserRulesAsync();
        var rules = DefaultTextSwapRules.CreateDefault()
            .Concat(userRules)
            .ToList();

        return new DialogueTextSwapProcessor(rules);
    }

    private static async Task<DialoguePronunciationProcessor> BuildPronunciationProcessorAsync(PronunciationRuleStore store)
    {
        var userRules = await store.LoadUserRulesAsync();
        var rules = WowPronunciationRules.CreateDefault()
            .Concat(userRules)
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
        return new WasapiStreamAudioPlayer();
    #elif LINUX
        return new GstAudioPlayer();
    #else
        throw new PlatformNotSupportedException("No audio player for this platform.");
    #endif
    }
}
