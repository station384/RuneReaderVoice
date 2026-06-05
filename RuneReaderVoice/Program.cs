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
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Velopack;
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
using RuneReaderVoice.Diagnostics;


namespace RuneReaderVoice;
// Program.cs
// Application entry point and service bootstrap for RuneReaderVoice. Duhhhhhh.......
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
        // Velopack MUST be the very first call in Main.
        // It handles applying any pending update staged on the previous run,
        // then returns so normal startup continues. If this process was launched
        // by the updater itself (not the user), it exits here cleanly.
        VelopackApp.Build().Run();
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

        // ── Unified SQLite DB ─────────────────────────────────────────────────
        var dbPath = Path.Combine(VoiceSettingsManager.GetConfigDirectory(), "runereader-voice.db");
        var dbExisted = File.Exists(dbPath);
        var db = new RvrDb(dbPath);
        db.InitializeAsync().GetAwaiter().GetResult();

        var pronunciationRules = new PronunciationRuleStore(db);
        var textSwapRules      = new TextSwapRuleStore(db);
        var npcPeopleCatalogStore = new NpcPeopleCatalogStore(db);
        npcPeopleCatalogStore.SeedFromLegacyCatalogAsync().GetAwaiter().GetResult();
        var providerSlotProfileStore = new ProviderSlotProfileStore(db);
        var npcPeopleCatalogService = new NpcPeopleCatalogService(npcPeopleCatalogStore);
        npcPeopleCatalogService.InitializeAsync().GetAwaiter().GetResult();

        if (!dbExisted)
            textSwapRules.AddDefaultRulesAsync().GetAwaiter().GetResult();

        ITtsProvider provider = TtsProviderFactory.CreateProvider(settings, activeDescriptor);
        TtsProviderFactory.ApplyStoredProfiles(settings, providerSlotProfileStore, provider);

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
                npcPeopleCatalogService, syncClient, assemblerBridge);
            npcSync.StartAsync();
        }
        else
        {
            // No server configured — create a no-op stub so AppServices is never null
            npcSync = NpcSyncService.CreateNoOp(
                settings, npcOverrides, pronunciationRules, textSwapRules, npcPeopleCatalogService);
        }

        var textSwapProcessor      = BuildTextSwapProcessorAsync(textSwapRules).GetAwaiter().GetResult();;
        var pronunciationProcessor = BuildPronunciationProcessorAsync(pronunciationRules).GetAwaiter().GetResult();;
        var textNormalizer         = new TextNormalizer();

        // ── Update service ────────────────────────────────────────────────────
        var updater = new UpdateService();

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
        var pendingExpandedSegments = new List<AssembledSegment>();

        assembler.OnSegmentComplete += seg =>
        {
            LogTextPipeline("00-onsegment-input", seg, seg.Text);

            // Diagnostics Raw must reflect the decoded/source segment before assembler
            // speech shaping such as synthetic periods and dialogue quotes. The audio
            // pipeline, however, must continue from seg.Text so those assembler-level
            // TTS improvements are preserved.
            var rawSourceText = HtmlTextStripper.Strip(!string.IsNullOrWhiteSpace(seg.SourceText) ? seg.SourceText! : seg.Text);
            var pipelineInputText = HtmlTextStripper.Strip(seg.Text);

            LogTextPipeline("01-html-stripped-source", seg, rawSourceText);
            LogTextPipeline("01b-html-stripped-pipeline", seg, pipelineInputText);
            AppServices.LastDecodedText = rawSourceText;
            AppServices.LastRuntimeSlot = seg.Slot;
            AppServices.RecordDialogSegmentRaw(seg, rawSourceText);
            var activeProvider = AppServices.Provider;

            // Normalize before text shaping. Text shaping may intentionally add
            // punctuation/spacing for TTS pacing (for example comma pauses), and
            // that can damage machine-readable numeric tokens such as "10,000"
            // before Humanizer gets a chance to convert them to words.
            var normalizedText = AppServices.TextNormalizer.Normalize(pipelineInputText, AppServices.Settings);
            LogTextPipeline("02-normalized", seg, normalizedText);
            var shapedText = AppServices.TextSwapProcessor.Process(normalizedText);
            LogTextPipeline("03-text-shaped", seg, shapedText);

            var shapedSegment = new AssembledSegment
            {
                Text                = shapedText,
                SourceText          = seg.SourceText,
                Slot                = seg.Slot,
                DialogId            = seg.DialogId,
                SegmentIndex        = seg.SegmentIndex,
                DialogSegmentCount  = seg.DialogSegmentCount,
                NpcId               = seg.NpcId,
                NpcName             = seg.NpcName,
                PlayerName          = seg.PlayerName,
                PlayerRealm         = seg.PlayerRealm,
                PlayerClass         = seg.PlayerClass,
                PlayerTitle         = seg.PlayerTitle,
                BespokeSampleId     = seg.BespokeSampleId,
                BespokeExaggeration = seg.BespokeExaggeration,
                BespokeCfgWeight    = seg.BespokeCfgWeight,
                UseNpcIdAsSeed      = seg.UseNpcIdAsSeed,
                SkipNarratorMarkerExpansion = seg.SkipNarratorMarkerExpansion,
                IsNarratorSegment   = seg.IsNarratorSegment,
            };
            var processed = activeProvider.SupportsInlinePronunciationHints
                ? AppServices.PronunciationProcessor.Process(shapedSegment)
                : shapedSegment;
            LogTextPipeline(activeProvider.SupportsInlinePronunciationHints ? "04-pronunciation" : "04-pronunciation-skipped", processed, processed.Text);

            AppServices.LastProcessedText = processed.Text ?? string.Empty;
            AppServices.LastTextSpoken    = processed.Text ?? string.Empty;
            AppServices.RecordDialogSegmentProcessed(processed, processed.Text ?? string.Empty);

            foreach (var chunk in ExpandPlayerNameSplit(processed, pendingExpandedSegments.Count))
            {
                LogTextPipeline("05-after-player-split", chunk, chunk.Text);
                pendingExpandedSegments.Add(chunk);
            }

            // The assembler's DialogSegmentCount reflects the original audible segment count
            // before player-name expansion. For playback, especially WaitForFullText mode,
            // the coordinator must use the post-split playback count instead.
            if (seg.SegmentIndex != seg.DialogSegmentCount - 1)
                return;

            var finalPlaybackCount = pendingExpandedSegments.Count;
            for (var i = 0; i < pendingExpandedSegments.Count; i++)
            {
                var chunk = pendingExpandedSegments[i];
                var finalChunk = CloneSegment(
                    chunk,
                    chunk.Text ?? string.Empty,
                    i,
                    chunk.BatchId,
                    chunk.BatchSegmentId,
                    chunk.PrimeFromBatchSegmentId,
                    chunk.BatchSegments,
                    finalPlaybackCount);
                LogTextPipeline("06-enqueue-final", finalChunk, finalChunk.Text);
                coordinator.EnqueueSegment(finalChunk);
            }

            pendingExpandedSegments.Clear();
        };

        assembler.OnSessionReset += id =>
        {
            pendingExpandedSegments.Clear();
            AppServices.ResetDialogDiagnostics(id);
            coordinator.OnSessionReset(id);
        };

        var monitor = new RvBarcodeMonitor(platform.ScreenCapture);
        // Migrate old one-Code39-region setting into GUID side-channel.
        settings.LastRrvbGuidBarcodeRegion ??= settings.LastCode39BarcodeRegion;

        monitor.TrySetInitialLockedRegion(settings.LastBarcodeRegion);
        monitor.TrySetInitialLockedRrvbGuidRegion(settings.LastRrvbGuidBarcodeRegion);
        monitor.TrySetInitialLockedRrvbNameRegion(settings.LastRrvbNameBarcodeRegion);
        monitor.CaptureIntervalMs     = settings.CaptureIntervalMs;
        monitor.ReScanIntervalMs      = settings.ReScanIntervalMs;
        monitor.SourceGoneThresholdMs = settings.SourceGoneThresholdMs;

        monitor.OnPacketDecoded += assembler.Feed;
        monitor.OnRrvbGuidDecoded += guid =>
        {
            AppServices.CurrentRrvbGuid = guid;
            RrvDebug.RrvbDebug($"Current GUID side-channel = {guid}");
        };
        monitor.OnRrvbNameDecoded += name =>
        {
            AppServices.CurrentRrvbName = name;
            RrvDebug.RrvbDebug($"Current NPC name side-channel = {name}");
        };
        monitor.OnRrvbIdentityLost += () =>
        {
            AppServices.CurrentRrvbGuid = string.Empty;
            AppServices.CurrentRrvbName = string.Empty;
            RrvDebug.RrvbDebug("Current RRVB side-channel cleared");
        };
        monitor.OnSourceGone += () =>
        {
            // Side-channel metadata belongs to the visible QR dialog. Clear it
            // when the source disappears so a debounced RRVB value from the
            // previous NPC cannot leak into the next cold dialog.
            AppServices.CurrentRrvbGuid = string.Empty;
            AppServices.CurrentRrvbName = string.Empty;
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
            VoiceSettingsManager.MarkDirty();
           // platform.ScreenCapture.CaptureRegion = new Rect(settings.LastBarcodeRegion.X, settings.LastBarcodeRegion.Y, settings.LastBarcodeRegion.Width, settings.LastBarcodeRegion.Height);
                
        };

        monitor.OnLockedRrvbGuidRegionChanged += rect =>
        {
            settings.LastRrvbGuidBarcodeRegion = new SavedBarcodeRegion
            {
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height,
                ScreenWidth = platform.ScreenCapture.ScreenWidth,
                ScreenHeight = platform.ScreenCapture.ScreenHeight,
            };
            settings.LastCode39BarcodeRegion = settings.LastRrvbGuidBarcodeRegion;
            VoiceSettingsManager.MarkDirty();
        };

        monitor.OnLockedRrvbNameRegionChanged += rect =>
        {
            settings.LastRrvbNameBarcodeRegion = new SavedBarcodeRegion
            {
                X = rect.X,
                Y = rect.Y,
                Width = rect.Width,
                Height = rect.Height,
                ScreenWidth = platform.ScreenCapture.ScreenWidth,
                ScreenHeight = platform.ScreenCapture.ScreenHeight,
            };
            VoiceSettingsManager.MarkDirty();
        };

        platform.ScreenCapture.OnFullScreenUpdated += monitor.ProcessFrame;
        platform.ScreenCapture.OnRegionUpdated     += monitor.ProcessQrFrameRegion;
        platform.ScreenCapture.OnRrvbRegionUpdated += monitor.ProcessRrvbFrameRegion;

        platform.Hotkeys.EscPressed += coordinator.HandleEscPressed;
        platform.Hotkeys.Start();

        AppServices.Initialize(
            settings, platform, provider, cache, player,
            assembler, coordinator, monitor, pronunciationProcessor, textSwapProcessor, textNormalizer,
            npcOverrides, npcSync, updater, npcPeopleCatalogService, providerSlotProfileStore,
            db, pronunciationRules, textSwapRules, providerRegistry);

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

    private static IEnumerable<AssembledSegment> ExpandPlayerNameSplit(AssembledSegment segment, int startExpandedSegmentIndex)
    {
        var mode = (AppServices.Settings.PlayerNameMode ?? "generic").Trim().ToLowerInvariant();

        // Player-name replacement is intentionally isolated at paragraph level.
        // Earlier sentence/name-level splitting improved cache reuse but made the
        // substituted name/title sound pasted in. Keeping the whole paragraph
        // preserves local prosody while still avoiding regeneration of unrelated
        // paragraphs in the same dialog.
        var strategy = "containing_paragraph";
        int expandedSegmentIndex = startExpandedSegmentIndex;
        if (mode != "split" && mode != "generic" && mode != "actual")
        {
            RrvDebug.PlayerSplitDebug($"bypass seg={startExpandedSegmentIndex} reason=mode mode={mode}");
            yield return CloneSegment(segment, segment.Text ?? string.Empty, expandedSegmentIndex);
            yield break;
        }

        var splitTarget = ResolvePlayerSplitTarget(segment, mode);
        if (string.IsNullOrWhiteSpace(segment.Text) || string.IsNullOrWhiteSpace(splitTarget))
        {
            RrvDebug.PlayerSplitDebug($"bypass seg={startExpandedSegmentIndex} reason=missing-text-or-target mode={mode} target='{splitTarget ?? string.Empty}' textLen={segment.Text?.Length ?? 0}");
            yield return CloneSegment(segment, segment.Text ?? string.Empty, expandedSegmentIndex);
            yield break;
        }

        RrvDebug.PlayerSplitDebug($"evaluate seg={startExpandedSegmentIndex} strategy={strategy} mode={mode} target='{splitTarget}' words={CountWords(segment.Text)} text='{Preview(segment.Text)}'");
        LogTextPipeline("player-split-evaluate", segment, segment.Text, $"strategy={strategy} mode={mode} target={splitTarget}");
        var parts = SplitAroundPlayerName(segment.Text, splitTarget!, strategy);
        if (parts == null || parts.Count == 0)
        {
            RrvDebug.PlayerSplitDebug($"no-split seg={startExpandedSegmentIndex} strategy={strategy} mode={mode} target='{splitTarget}'");
            yield return CloneSegment(segment, segment.Text ?? string.Empty, expandedSegmentIndex);
            yield break;
        }

        RrvDebug.PlayerSplitDebug($"split seg={startExpandedSegmentIndex} strategy={strategy} parts={parts.Count}");
        for (var i = 0; i < parts.Count; i++)
        {
            RrvDebug.PlayerSplitDebug($"part[{i}] words={CountWords(parts[i])} text='{Preview(parts[i])}'");
            LogTextPipeline($"player-split-part[{i}]", segment, parts[i], $"strategy={strategy} mode={mode} target={splitTarget}");
        }

        var useRemoteBatch = AppServices.Provider is RemoteTtsProvider && parts.Count > 1;
        var batchId = useRemoteBatch ? Guid.NewGuid().ToString("N") : null;
        List<BatchSegmentPlan>? batchPlans = null;
        if (useRemoteBatch)
        {
            batchPlans = new List<BatchSegmentPlan>(parts.Count);
            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                var segmentId = $"seg_{i}";

                // Maintain explicit continuity across the client-requested batch chain.
                //
                // Why this exists:
                // - The server's normal internal sentence splitting for large Chatterbox text
                //   is correct and should remain untouched.
                // - This client batch path is different: it can split in the middle of a
                //   sentence (for example around player-name replacement), so continuity must
                //   be carried explicitly from one returned batch item to the next.
                // - We therefore chain every batch item to the immediately prior batch item,
                //   rather than using the older special-case rule that only primed the exact
                //   player-name segment.
                //
                // Maintainer note:
                // If future testing shows the narrator should remain atomic, make that decision
                // in the higher-level batch planner. This low-level split batch is one voice
                // stream and should always submit explicit continuity references.
                string? primeFrom = batchPlans.Count > 0 ? batchPlans[^1].SegmentId : null;

                batchPlans.Add(new BatchSegmentPlan
                {
                    SegmentId = segmentId,
                    Text = part,
                    PrimeFromSegmentId = primeFrom,
                });
            }
            RrvDebug.PlayerSplitDebug($"remote-batch batchId={batchId} plans={batchPlans.Count} strategy={strategy}");
        }

        var planIndex = 0;
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var plan = batchPlans != null && planIndex < batchPlans.Count ? batchPlans[planIndex] : null;
            yield return CloneSegment(
                segment,
                part,
                expandedSegmentIndex++,
                batchId,
                plan?.SegmentId,
                plan?.PrimeFromSegmentId,
                batchPlans);
            planIndex++;
        }
    }

    private static AssembledSegment CloneSegment(
        AssembledSegment segment,
        string text,
        int idx,
        string? batchId = null,
        string? batchSegmentId = null,
        string? primeFromBatchSegmentId = null,
        IReadOnlyList<BatchSegmentPlan>? batchSegments = null,
        int? dialogSegmentCount = null) => new()
    {
        Text = text,
        SourceText = segment.SourceText,
        Slot = segment.Slot,
        DialogId = segment.DialogId,
        SegmentIndex = idx,
        DialogSegmentCount = dialogSegmentCount ?? segment.DialogSegmentCount,
        NpcId = segment.NpcId,
        NpcName = segment.NpcName,
        PlayerName = segment.PlayerName,
        PlayerRealm = segment.PlayerRealm,
        PlayerClass = segment.PlayerClass,
        PlayerTitle = segment.PlayerTitle,
        BatchId = batchId,
        BatchSegmentId = batchSegmentId,
        PrimeFromBatchSegmentId = primeFromBatchSegmentId,
        BatchSegments = batchSegments,
        BespokeSampleId = segment.BespokeSampleId,
        BespokeMatchedByNpcName = segment.BespokeMatchedByNpcName,
        MissingBespokeSampleId = segment.MissingBespokeSampleId,
        BespokeExaggeration = segment.BespokeExaggeration,
        BespokeCfgWeight = segment.BespokeCfgWeight,
        UseNpcIdAsSeed = segment.UseNpcIdAsSeed,
        SkipNarratorMarkerExpansion = segment.SkipNarratorMarkerExpansion,
        IsNarratorSegment = segment.IsNarratorSegment,
    };

    private static string BuildPlayerNameWithOptionalRealm(AssembledSegment segment)
    {
        var actualName = segment.PlayerName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(actualName))
            return string.Empty;

        if (AppServices.Settings.PlayerNameAppendRealm && !string.IsNullOrWhiteSpace(segment.PlayerRealm))
            return $"{actualName} of {segment.PlayerRealm}";

        return actualName;
    }

    private static string ResolvePlayerTitleReplacement(AssembledSegment segment)
    {
        var title = segment.PlayerTitle?.Trim() ?? string.Empty;
        var playerName = BuildPlayerNameWithOptionalRealm(segment);

        if (!AppServices.Settings.PlayerNameEnableTitle)
            return string.IsNullOrWhiteSpace(playerName) ? "Hero" : playerName;

        if (string.IsNullOrWhiteSpace(title))
            return string.IsNullOrWhiteSpace(playerName) ? "Hero" : playerName;

        if (!string.IsNullOrWhiteSpace(playerName) && title.Contains("%s", StringComparison.OrdinalIgnoreCase))
            return Regex.Replace(title, "%s", m => playerName, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return title;
    }

    private static string? ResolvePlayerSplitTarget(AssembledSegment segment, string mode)
    {
        mode = (mode ?? "generic").Trim().ToLowerInvariant();

        if (mode == "actual" || mode == "split")
        {
            var actualName = BuildPlayerNameWithOptionalRealm(segment);
            if (string.IsNullOrWhiteSpace(actualName))
                return null;

            return AppServices.Settings.PlayerNameEnableTitle
                ? ResolvePlayerTitleReplacement(segment)
                : actualName;
        }

        if (mode != "generic")
            return null;

        // Maintainer note:
        // All replacement modes now use the same paragraph-based cache-preserving split flow,
        // not just the actual player name. Cache-friendly titles (Hero / Champion /
        // Player Class Name), actual player names, and optional realm suffixes all fragment
        // cache identity, so the full paragraph containing the replacement is isolated when present.
        var preset = (AppServices.Settings.PlayerNameReplacementPreset ?? "hero").Trim().ToLowerInvariant();
        var replacement = preset switch
        {
            "champion" => "Champion",
            "class" => string.IsNullOrWhiteSpace(segment.PlayerClass) ? "Hero" : segment.PlayerClass!,
            "title" => ResolvePlayerTitleReplacement(segment),
            _ => "Hero",
        };

        if (preset != "title" && AppServices.Settings.PlayerNameAppendRealm && !string.IsNullOrWhiteSpace(segment.PlayerRealm))
            replacement = $"{replacement} of {segment.PlayerRealm}";

        return replacement;
    }

    private const int MinimumPlayerNameSentenceWords = 3;

    private static List<string>? SplitAroundPlayerName(string text, string playerName, string strategy)
    {
        var escaped = Regex.Escape(playerName);
        var pattern = $@"(?<![\p{{L}}\p{{N}}_'-]){escaped}(?![\p{{L}}\p{{N}}_'-])";
        var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        RrvDebug.PlayerSplitDebug($"matches={matches.Count} player='{playerName}' strategy={strategy} text='{Preview(text)}'");
        if (matches.Count != 1) return null;

        var match = matches[0];
        int nameStart = match.Index;
        int nameEnd = match.Index + match.Length;
        strategy = (strategy ?? "containing_sentence").Trim().ToLowerInvariant();
        if (strategy == "surrounding_words")
            strategy = "containing_sentence";

        int start;
        int end;

        if (strategy == "name_only")
        {
            // Maintainer note:
            // "name_only" started as a tiny bridge fragment around the player name,
            // but that proved unstable for Chatterbox-family models even after T3
            // continuation tuning. Small fragments like "missive for Earwig from"
            // are not sentence-like enough to synthesize reliably as standalone units.
            // So this special mode now expands to the full containing sentence instead.
            // If the sentence is too short, expand to two sentences using the same
            // rules as the general containing_sentence strategy.
            start = FindSentenceStart(text, nameStart);
            end = FindSentenceEnd(text, nameEnd);

            var sentence = text[start..end];
            var sentenceWords = CountWords(sentence);
            RrvDebug.PlayerSplitDebug($"strategy=name_only sentenceStart={start} sentenceEnd={end} words={sentenceWords} sentence='{Preview(sentence)}'");
            if (sentenceWords < MinimumPlayerNameSentenceWords)
            {
                ExpandToTwoSentences(text, ref start, ref end);
                RrvDebug.PlayerSplitDebug($"strategy=name_only expanded_to_two_sentences start={start} end={end} words={CountWords(text[start..end])} text='{Preview(text[start..end])}'");
            }
        }
        else if (strategy == "containing_paragraph")
        {
            start = FindParagraphStart(text, nameStart);
            end = FindParagraphEnd(text, nameEnd);
            RrvDebug.PlayerSplitDebug($"strategy=containing_paragraph start={start} end={end}");
        }
        else
        {
            start = FindSentenceStart(text, nameStart);
            end = FindSentenceEnd(text, nameEnd);

            var sentence = text[start..end];
            var sentenceWords = CountWords(sentence);
            RrvDebug.PlayerSplitDebug($"strategy=containing_sentence start={start} end={end} words={sentenceWords} sentence='{Preview(sentence)}'");
            if (sentenceWords < MinimumPlayerNameSentenceWords)
            {
                ExpandToTwoSentences(text, ref start, ref end);
                RrvDebug.PlayerSplitDebug($"expanded_to_two_sentences start={start} end={end} words={CountWords(text[start..end])} text='{Preview(text[start..end])}'");
            }
        }

        var before = text[..start];
        var middle = text[start..end];
        var after = text[end..];

        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(before)) parts.Add(before);
        if (!string.IsNullOrWhiteSpace(middle)) parts.Add(middle);
        if (!string.IsNullOrWhiteSpace(after)) parts.Add(after);

        if (parts.Count <= 1)
            return null;

        return parts;
    }


    private static void LogTextPipeline(string stage, AssembledSegment segment, string? text, string? extra = null)
    {
        var safe = VisibleText(text);
        var line =
            $"[TextPipeline] {stage} dialog=0x{segment.DialogId:X} seg={segment.SegmentIndex}/{segment.DialogSegmentCount} " +
            $"slot={segment.Slot} npc={segment.NpcId} narrator={segment.IsNarratorSegment} batch={segment.BatchId ?? "-"}/{segment.BatchSegmentId ?? "-"} " +
            $"player='{segment.PlayerName ?? string.Empty}' title='{segment.PlayerTitle ?? string.Empty}' realm='{segment.PlayerRealm ?? string.Empty}' " +
            $"len={text?.Length ?? 0} words={CountWords(text ?? string.Empty)}" +
            (string.IsNullOrWhiteSpace(extra) ? string.Empty : $" {extra}") +
            $" text=<<<{safe}>>>";
        RrvDebug.TextPipelineDebug(line);
    }

    private static string VisibleText(string? text)
    {
        if (text == null)
            return string.Empty;

        return text
            .Replace("\r\n", "␊", StringComparison.Ordinal)
            .Replace("\n", "␊", StringComparison.Ordinal)
            .Replace("\r", "␍", StringComparison.Ordinal)
            .Replace("\t", "␉", StringComparison.Ordinal);
    }

    private static string Preview(string? text, int max = 120)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        return normalized.Length <= max ? normalized : normalized[..max] + "...";
    }

    private static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return Regex.Matches(text, @"\b[\p{L}\p{N}']+\b", RegexOptions.CultureInvariant).Count;
    }


    private static void ExpandToTwoSentences(string text, ref int start, ref int end)
    {
        var hasPrevious = start > 0;
        var hasNext = end < text.Length;

        if (hasPrevious)
        {
            start = FindSentenceStart(text, Math.Max(0, start - 1));
            return;
        }

        if (hasNext)
        {
            end = FindSentenceEnd(text, end);
        }
    }

    private static int FindParagraphStart(string text, int start)
    {
        var split = text.LastIndexOf("\n\n", Math.Max(0, start - 1), StringComparison.Ordinal);
        return split >= 0 ? split + 2 : 0;
    }

    private static int FindParagraphEnd(string text, int end)
    {
        var split = text.IndexOf("\n\n", end, StringComparison.Ordinal);
        return split >= 0 ? split : text.Length;
    }

    private static int FindSentenceStart(string text, int start)
    {
        for (int i = start - 1; i >= 0; i--)
            if (text[i] == '.' || text[i] == '!' || text[i] == '?' || text[i] == '\n' || text[i] == '\r')
                return i + 1;
        return 0;
    }

    private static int FindSentenceEnd(string text, int end)
    {
        for (int i = end; i < text.Length; i++)
            if (text[i] == '.' || text[i] == '!' || text[i] == '?' || text[i] == '\n' || text[i] == '\r')
                return i + 1;
        return text.Length;
    }

    private static void LogPlayerSplit(string message)
    {
        RrvDebug.PlayerSplitDebug($"{message}");
    }

    private static string PreviewText(string? text, int max = 80)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "<empty>";

        var normalized = Regex.Replace(text, @"\s+", " ").Trim();
        if (normalized.Length <= max)
            return normalized;

        return normalized[..max] + "...";
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '\'';
}
