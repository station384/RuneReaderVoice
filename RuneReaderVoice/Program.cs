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
        var npcPeopleCatalogStore = new NpcPeopleCatalogStore(db);
        npcPeopleCatalogStore.SeedFromLegacyCatalogAsync().GetAwaiter().GetResult();
        var providerSlotProfileStore = new ProviderSlotProfileStore(db);
        providerSlotProfileStore.SeedFromSettingsAsync(settings).GetAwaiter().GetResult();
        settings.PerProviderVoiceAssignments = new();
        settings.PerProviderVoiceProfiles = new();
        settings.PerProviderSampleProfiles = new();
        var npcPeopleCatalogService = new NpcPeopleCatalogService(npcPeopleCatalogStore);
        npcPeopleCatalogService.InitializeAsync().GetAwaiter().GetResult();

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
        var pendingExpandedSegments = new List<AssembledSegment>();

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
                DialogSegmentCount  = seg.DialogSegmentCount,
                NpcId               = seg.NpcId,
                PlayerName          = seg.PlayerName,
                PlayerRealm         = seg.PlayerRealm,
                PlayerClass         = seg.PlayerClass,
                BespokeSampleId     = seg.BespokeSampleId,
                BespokeExaggeration = seg.BespokeExaggeration,
                BespokeCfgWeight    = seg.BespokeCfgWeight,
                UseNpcIdAsSeed      = seg.UseNpcIdAsSeed,
            };
            var processed = activeProvider.SupportsInlinePronunciationHints
                ? AppServices.PronunciationProcessor.Process(shapedSegment)
                : shapedSegment;

            AppServices.LastProcessedText = processed.Text ?? string.Empty;
            AppServices.LastTextSpoken    = processed.Text ?? string.Empty;

            foreach (var chunk in ExpandPlayerNameSplit(processed, pendingExpandedSegments.Count))
                pendingExpandedSegments.Add(chunk);

            // The assembler's DialogSegmentCount reflects the original audible segment count
            // before player-name expansion. For playback, especially WaitForFullText mode,
            // the coordinator must use the post-split playback count instead.
            if (seg.SegmentIndex != seg.DialogSegmentCount - 1)
                return;

            var finalPlaybackCount = pendingExpandedSegments.Count;
            for (var i = 0; i < pendingExpandedSegments.Count; i++)
            {
                var chunk = pendingExpandedSegments[i];
                coordinator.EnqueueSegment(CloneSegment(
                    chunk,
                    chunk.Text,
                    i,
                    chunk.BatchId,
                    chunk.BatchSegmentId,
                    chunk.PrimeFromBatchSegmentId,
                    chunk.BatchSegments,
                    finalPlaybackCount));
            }

            pendingExpandedSegments.Clear();
        };

        assembler.OnSessionReset += id => { pendingExpandedSegments.Clear(); coordinator.OnSessionReset(id); };

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
            npcOverrides, npcSync, npcPeopleCatalogService, providerSlotProfileStore,
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
        var strategy = "containing_sentence";
        int expandedSegmentIndex = startExpandedSegmentIndex;
        if (mode != "split" && mode != "generic" && mode != "actual")
        {
            Console.WriteLine($"[PlayerSplit] bypass seg={startExpandedSegmentIndex} reason=mode mode={mode}");
            yield return CloneSegment(segment, segment.Text, expandedSegmentIndex);
            yield break;
        }

        var splitTarget = ResolvePlayerSplitTarget(segment, mode);
        if (string.IsNullOrWhiteSpace(segment.Text) || string.IsNullOrWhiteSpace(splitTarget))
        {
            Console.WriteLine($"[PlayerSplit] bypass seg={startExpandedSegmentIndex} reason=missing-text-or-target mode={mode} target='{splitTarget ?? string.Empty}' textLen={segment.Text?.Length ?? 0}");
            yield return CloneSegment(segment, segment.Text, expandedSegmentIndex);
            yield break;
        }

        Console.WriteLine($"[PlayerSplit] evaluate seg={startExpandedSegmentIndex} strategy={strategy} mode={mode} target='{splitTarget}' words={CountWords(segment.Text)} text='{Preview(segment.Text)}'");
        var parts = SplitAroundPlayerName(segment.Text, splitTarget!, strategy);
        if (parts == null || parts.Count == 0)
        {
            Console.WriteLine($"[PlayerSplit] no-split seg={startExpandedSegmentIndex} strategy={strategy} mode={mode} target='{splitTarget}'");
            yield return CloneSegment(segment, segment.Text, expandedSegmentIndex);
            yield break;
        }

        Console.WriteLine($"[PlayerSplit] split seg={startExpandedSegmentIndex} strategy={strategy} parts={parts.Count}");
        for (var i = 0; i < parts.Count; i++)
            Console.WriteLine($"[PlayerSplit] part[{i}] words={CountWords(parts[i])} text='{Preview(parts[i])}'");

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
            Console.WriteLine($"[PlayerSplit] remote-batch batchId={batchId} plans={batchPlans.Count} strategy={strategy}");
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
        Slot = segment.Slot,
        DialogId = segment.DialogId,
        SegmentIndex = idx,
        DialogSegmentCount = dialogSegmentCount ?? segment.DialogSegmentCount,
        NpcId = segment.NpcId,
        PlayerName = segment.PlayerName,
        PlayerRealm = segment.PlayerRealm,
        PlayerClass = segment.PlayerClass,
        BatchId = batchId,
        BatchSegmentId = batchSegmentId,
        PrimeFromBatchSegmentId = primeFromBatchSegmentId,
        BatchSegments = batchSegments,
        BespokeSampleId = segment.BespokeSampleId,
        BespokeExaggeration = segment.BespokeExaggeration,
        BespokeCfgWeight = segment.BespokeCfgWeight,
        UseNpcIdAsSeed = segment.UseNpcIdAsSeed,
    };

    private static string? ResolvePlayerSplitTarget(AssembledSegment segment, string mode)
    {
        mode = (mode ?? "generic").Trim().ToLowerInvariant();

        if (mode == "actual" || mode == "split")
        {
            var actualName = segment.PlayerName;
            if (string.IsNullOrWhiteSpace(actualName))
                return null;

            if (AppServices.Settings.PlayerNameAppendRealm && !string.IsNullOrWhiteSpace(segment.PlayerRealm))
                actualName = $"{actualName} of {segment.PlayerRealm}";

            return actualName;
        }

        if (mode != "generic")
            return null;

        // Maintainer note:
        // All replacement modes now use the same sentence-based cache-preserving split flow,
        // not just the actual player name. Cache-friendly titles (Hero / Champion /
        // Player Class Name), actual player names, and optional realm suffixes all fragment
        // cache identity, so they all need to be isolated into their own segment when present.
        var preset = (AppServices.Settings.PlayerNameReplacementPreset ?? "hero").Trim().ToLowerInvariant();
        var replacement = preset switch
        {
            "champion" => "Champion",
            "class" => string.IsNullOrWhiteSpace(segment.PlayerClass) ? "Hero" : segment.PlayerClass!,
            _ => "Hero",
        };

        if (AppServices.Settings.PlayerNameAppendRealm && !string.IsNullOrWhiteSpace(segment.PlayerRealm))
            replacement = $"{replacement} of {segment.PlayerRealm}";

        return replacement;
    }

    private const int MinimumPlayerNameSentenceWords = 3;

    private static List<string>? SplitAroundPlayerName(string text, string playerName, string strategy)
    {
        var escaped = Regex.Escape(playerName);
        var pattern = $@"(?<![\p{{L}}\p{{N}}_'-]){escaped}(?![\p{{L}}\p{{N}}_'-])";
        var matches = Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Console.WriteLine($"[PlayerSplit] matches={matches.Count} player='{playerName}' strategy={strategy} text='{Preview(text)}'");
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
            Console.WriteLine($"[PlayerSplit] strategy=name_only sentenceStart={start} sentenceEnd={end} words={sentenceWords} sentence='{Preview(sentence)}'");
            if (sentenceWords < MinimumPlayerNameSentenceWords)
            {
                ExpandToTwoSentences(text, ref start, ref end);
                Console.WriteLine($"[PlayerSplit] strategy=name_only expanded_to_two_sentences start={start} end={end} words={CountWords(text[start..end])} text='{Preview(text[start..end])}'");
            }
        }
        else if (strategy == "containing_paragraph")
        {
            start = FindParagraphStart(text, nameStart);
            end = FindParagraphEnd(text, nameEnd);
            Console.WriteLine($"[PlayerSplit] strategy=containing_paragraph start={start} end={end}");
        }
        else
        {
            start = FindSentenceStart(text, nameStart);
            end = FindSentenceEnd(text, nameEnd);

            var sentence = text[start..end];
            var sentenceWords = CountWords(sentence);
            Console.WriteLine($"[PlayerSplit] strategy=containing_sentence start={start} end={end} words={sentenceWords} sentence='{Preview(sentence)}'");
            if (sentenceWords < MinimumPlayerNameSentenceWords)
            {
                ExpandToTwoSentences(text, ref start, ref end);
                Console.WriteLine($"[PlayerSplit] expanded_to_two_sentences start={start} end={end} words={CountWords(text[start..end])} text='{Preview(text[start..end])}'");
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
        System.Diagnostics.Debug.WriteLine($"[PlayerSplit] {message}");
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
