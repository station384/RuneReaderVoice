using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using RuneReaderVoice;
using RuneReaderVoice.Data;
using RuneReaderVoice.Platform;
using RuneReaderVoice.Protocol;
using RuneReaderVoice.Sync;
using RuneReaderVoice.TTS;
using RuneReaderVoice.TTS.Cache;
using RuneReaderVoice.TTS.Providers;
using RuneReaderVoice.TTS.Audio;
using RuneReaderVoice.Session;
using RuneReaderVoice.TTS.Pronunciation;
using RuneReaderVoice.TTS.TextSwap;
using Rect = OpenCvSharp.Rect;


namespace RuneReaderVoice;

internal static class Program
{

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
    [STAThread]
    public static int Main(string[] args)
    {
        // Suppress unobserved Task exceptions from HttpClient's internal connection
        // pool keep-alive machinery. When Caddy closes an idle connection the pool's
        // background read throws IOException(SocketException 995) as an unobserved
        // Task — this handler catches it before it prints to debug output.
        // Real synthesis/network errors are caught in our own try/catch blocks.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            var ex = e.Exception?.InnerException ?? e.Exception;
            if (ex is System.IO.IOException || ex is System.Net.Sockets.SocketException)
            {
                e.SetObserved(); // suppress — expected from idle connection recycling
                return;
            }
            System.Diagnostics.Debug.WriteLine(
                $"[UnobservedTask] {ex?.GetType().Name}: {ex?.Message}");
            e.SetObserved();
        };

        // ── Load settings ─────────────────────────────────────────────────────
        var settings = VoiceSettingsManager.LoadSettings();

        // ── Platform services ────────────────────────────────────────────────
        var platform = VoicePlatformFactory.Create();

        // ── Provider registry / TTS Provider ────────────────────────────────
        var providerRegistry = TtsProviderFactory.BuildRegistry(settings);
        var activeDescriptor = providerRegistry.Get(settings.ActiveProvider)
                              ?? providerRegistry.All().FirstOrDefault()
                              ?? throw new InvalidOperationException("No TTS providers are registered.");

        if (!providerRegistry.Contains(settings.ActiveProvider))
            settings.ActiveProvider = activeDescriptor.ClientProviderId;

        ITtsProvider provider = TtsProviderFactory.CreateProvider(settings, activeDescriptor);
        TtsProviderFactory.ApplyStoredProfiles(settings, provider);

        // ── Unified SQLite DB ─────────────────────────────────────────────────
        var dbPath = Path.Combine(VoiceSettingsManager.GetConfigDirectory(), "runereader-voice.db");
        var dbExisted = File.Exists(dbPath);
        var db = new RvrDb(dbPath);
        db.InitializeAsync().GetAwaiter().GetResult();

        var pronunciationRules = new PronunciationRuleStore(db);
        var textSwapRules      = new TextSwapRuleStore(db);

        if (!dbExisted)
            textSwapRules.AddDefaultRulesAsync().GetAwaiter().GetResult();

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
        npcOverrides.InitializeAsync().GetAwaiter().GetResult();

        var assembler = new TtsSessionAssembler(npcOverrides);
        assembler.LoadOverridesAsync().GetAwaiter().GetResult();

        // ── Community sync service ────────────────────────────────────────────
        NpcSyncService npcSync;
        if (!string.IsNullOrWhiteSpace(settings.RemoteServerUrl))
        {
            var syncClient = new ServerDefaultsClient(
                settings.RemoteServerUrl,
                settings.ContributeKey,
                settings.AdminKey);
            var assemblerBridge = new TtsSessionAssemblerBridge(assembler, npcOverrides);
            npcSync = new NpcSyncService(
                settings, npcOverrides, pronunciationRules, textSwapRules,
                syncClient, assemblerBridge);
            npcSync.StartAsync().GetAwaiter().GetResult();
        }
        else
        {
            // No server configured — create a no-op stub so AppServices is never null
            npcSync = NpcSyncService.CreateNoOp(
                settings, npcOverrides, pronunciationRules, textSwapRules);
        }

        var textSwapProcessor      = BuildTextSwapProcessorAsync(textSwapRules).GetAwaiter().GetResult();;
        var pronunciationProcessor = BuildPronunciationProcessorAsync(pronunciationRules).GetAwaiter().GetResult();;

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
                Text                = shapedText,
                Slot                = seg.Slot,
                DialogId            = seg.DialogId,
                SegmentIndex        = seg.SegmentIndex,
                NpcId               = seg.NpcId,
                BespokeSampleId     = seg.BespokeSampleId,
                BespokeExaggeration = seg.BespokeExaggeration,
                BespokeCfgWeight    = seg.BespokeCfgWeight,
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
           // platform.ScreenCapture.CaptureRegion = new Rect(settings.LastBarcodeRegion.X, settings.LastBarcodeRegion.Y, settings.LastBarcodeRegion.Width, settings.LastBarcodeRegion.Height);
                
        };

        platform.ScreenCapture.OnFullScreenUpdated += monitor.ProcessFrame;
        platform.ScreenCapture.OnRegionUpdated     += monitor.ProcessFrameRegion;

        platform.Hotkeys.EscPressed += coordinator.HandleEscPressed;
        platform.Hotkeys.Start();

        AppServices.Initialize(
            settings, platform, provider, cache, player,
            assembler, coordinator, monitor, pronunciationProcessor, textSwapProcessor,
            npcOverrides, npcSync, db, pronunciationRules, textSwapRules, providerRegistry);

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
        return new DialogueTextSwapProcessor(userRules);
    }

    private static async Task<DialoguePronunciationProcessor> BuildPronunciationProcessorAsync(PronunciationRuleStore store)
    {
        var userRules = await store.LoadUserRulesAsync();
        var rules = WowPronunciationRules.CreateDefault()
            .Concat(userRules)
            .ToList();

        return new DialoguePronunciationProcessor(rules);
    }

}
