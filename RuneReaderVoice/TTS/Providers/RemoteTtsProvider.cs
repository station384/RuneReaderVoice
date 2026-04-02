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
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using NVorbis;
using OggVorbisEncoder;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

// RemoteTtsProvider.cs
// ITtsProvider implementation that renders through the RuneReader Voice server.
public sealed class RemoteTtsProvider : ITtsProvider
{
    private readonly VoiceUserSettings _settings;
    private readonly ProviderDescriptor _descriptor;
    private readonly RemoteTtsClient _client;
    private IReadOnlyList<VoiceInfo>? _voiceCache;

    public RemoteTtsProvider(VoiceUserSettings settings, ProviderDescriptor descriptor)
    {
        _settings   = settings;
        _descriptor = descriptor;
        _client     = new RemoteTtsClient(settings.RemoteServerUrl, settings.RemoteApiKey);
    }

    public string ProviderId                      => _descriptor.ClientProviderId;
    public string DisplayName                     => _descriptor.DisplayName;
    public bool   IsAvailable                     => !string.IsNullOrWhiteSpace(_settings.RemoteServerUrl);
    public bool   RequiresFullText                => _descriptor.RequiresFullText;
    public bool   SupportsInlinePronunciationHints => _descriptor.SupportsInlinePronunciationHints;

    // ── Phrase stream ─────────────────────────────────────────────────────────

    public async IAsyncEnumerable<(PcmAudio audio, int phraseIndex, int phraseCount)>
        SynthesizePhraseStreamAsync(
            string text,
            VoiceSlot slot,
            string tempDirectory,
            [EnumeratorCancellation] CancellationToken ct)
    {
        var profile = ResolveProfile(slot) ?? VoiceProfileDefaults.Create(string.Empty);
        var phrases = TextChunkingPolicy.GetChunkTexts(
            text, ProviderId, profile,
            _settings.EnablePhraseChunking && !profile.DisableChunking);
        int count = phrases.Count;
        if (count <= 1)
        {
            yield return (await SynthesizeAsync(text, slot, ct), 0, 1);
            yield break;
        }

        var results = await RunLimitedAsync(
            phrases.Select((phrase, index) => (phrase, index)),
            2,
            item => SynthesizeChunkAsync(item.phrase, slot, item.index, ct),
            ct);

        foreach (var result in results.OrderBy(r => r.index))
            yield return (result.audio, result.index, count);
    }

    public async Task<PcmAudio> SynthesizeAsync(string text, VoiceSlot slot, CancellationToken ct)
    {
        var oggBytes = await SynthesizeOggAsync(text, slot, ct);
        return await DecodeOggAsync(oggBytes, ct);
    }

    // ── OGG synthesis (public surface used by PlaybackCoordinator) ────────────

    /// <summary>
    /// Synthesize text to OGG bytes via the v2 async server endpoint.
    /// batchId/batchTotal tag this request as part of a batch for SSE progress reporting.
    /// </summary>
    public async Task<byte[]> SynthesizeOggAsync(
        string text, VoiceSlot slot, CancellationToken ct,
        string? bespokeSampleId     = null,
        float?  bespokeExaggeration = null,
        float?  bespokeCfgWeight    = null,
        string? batchId             = null,
        int?    batchTotal          = null)
    {
        if (string.IsNullOrWhiteSpace(_descriptor.RemoteProviderId))
            throw new InvalidOperationException("Remote provider id is missing.");

        var profile = ResolveProfile(slot) ?? VoiceProfileDefaults.Create(string.Empty);
        var chunkingEnabled = _settings.EnablePhraseChunking && !profile.DisableChunking;
        var phrases = TextChunkingPolicy.GetChunkTexts(text, ProviderId, profile, chunkingEnabled);

        System.Diagnostics.Debug.WriteLine(
            $"[RemoteTTS] SynthesizeOgg provider={ProviderId} chunks={phrases.Count} chunking={chunkingEnabled} textLen={text.Length}");

        // Generate a batch ID here — one per full text request regardless of chunk count.
        // All chunks submit under the same batchId so the server can report
        // aggregate progress: "3/6 chunks complete".
        // If a batchId was passed in by the caller (PlaybackCoordinator), use that.
        // Otherwise generate one here so the preview path also gets batch tracking.
        var effectiveBatchId    = batchId    ?? System.Guid.NewGuid().ToString("N");
        var effectiveBatchTotal = batchTotal ?? phrases.Count;

        // Start batch SSE monitor — linked to a CTS so we can cancel it if synthesis fails
        using var monitorCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => _MonitorBatchProgress(effectiveBatchId, monitorCts.Token), monitorCts.Token);

        try
        {
            if (phrases.Count <= 1)
                return await SynthesizeOggCoreAsync(text, slot, profile, ct,
                    bespokeSampleId, bespokeExaggeration, bespokeCfgWeight,
                    effectiveBatchId, effectiveBatchTotal);

            // Multi-chunk pipeline:
            //   Phase 1: Submit ALL chunks immediately (fast, ~5ms each, no concurrency limit)
            //   Phase 2: Fetch results as they complete (each awaits its own SSE)
            // This means chunk N+1's result can be fetched while chunk N is still playing,
            // rather than waiting for all chunks before returning any.

            // Phase 1 — submit all
            var submitTasks = phrases
                .Select(phrase => SubmitOggCoreAsync(phrase, slot, profile, ct,
                    bespokeSampleId, bespokeExaggeration, bespokeCfgWeight,
                    effectiveBatchId, effectiveBatchTotal))
                .ToArray();
            var submitted = await Task.WhenAll(submitTasks);

            // Phase 2 — fetch in parallel (each chunk awaits its own SSE independently)
            var fetchTasks = submitted
                .Select(s => FetchOggResultAsync(s.submitted, ct))
                .ToArray();
            var oggChunks = await Task.WhenAll(fetchTasks);

            var pcmChunks = new List<PcmAudio>(oggChunks.Length);
            foreach (var ogg in oggChunks)
                pcmChunks.Add(await DecodeOggAsync(ogg, ct));

            var combined = ConcatenatePcm(pcmChunks);
            return await EncodeOggAsync(combined, ct);
        }
        catch
        {
            monitorCts.Cancel();
            AppServices.ClearOperationStatus();
            throw;
        }
        finally
        {
            monitorCts.Cancel();
        }
    }

    private async Task _MonitorBatchProgress(string batchId, CancellationToken ct)
    {
        // Retry until the server registers the batch (first chunk POST has landed)
        bool gotEvents = false;
        for (int attempt = 0; attempt < 15 && !ct.IsCancellationRequested; attempt++)
        {
            if (attempt > 0)
            {
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
            }

            await foreach (var evt in _client.GetBatchProgressAsync(batchId, ct))
            {
                gotEvents = true;
                if (ct.IsCancellationRequested) break;

                if (string.Equals(evt.Status, "complete", StringComparison.OrdinalIgnoreCase))
                {
                    AppServices.ClearOperationStatus();
                    break;
                }

                var statusText = evt.Total > 1
                    ? $"Generating voice... ({evt.Completed}/{evt.Total})"
                    : "Generating voice...";
                AppServices.SetOperationStatus(statusText);
            }

            if (gotEvents) break;
        }
    }

    // ── Core synthesis (v2 API) ───────────────────────────────────────────────

    /// <summary>
    /// Phase 1 of 2: submit a synthesis job to the server and return immediately
    /// with the submit response (progress_key, cached flag).
    /// Does NOT wait for synthesis to complete.
    /// </summary>
    private async Task<(V2SubmitResponse submitted, VoiceProfile profile)> SubmitOggCoreAsync(
        string text, VoiceSlot slot, VoiceProfile profile,
        CancellationToken ct,
        string? bespokeSampleId     = null,
        float?  bespokeExaggeration = null,
        float?  bespokeCfgWeight    = null,
        string? batchId             = null,
        int?    batchTotal          = null)
    {
        if (!string.IsNullOrWhiteSpace(bespokeSampleId))
        {
            profile = ResolveSampleProfile(bespokeSampleId, slot);
            if (bespokeExaggeration.HasValue) profile.Exaggeration = bespokeExaggeration;
            if (bespokeCfgWeight.HasValue)    profile.CfgWeight    = bespokeCfgWeight;
        }

        var providerId = _descriptor.RemoteProviderId ?? string.Empty;
        if (providerId.Contains("chatterbox", StringComparison.OrdinalIgnoreCase))
            text = ChatterboxPreprocess(text);

        var voiceSpec = BuildVoiceSpec(profile);
        var v2Request = new RemoteSynthesizeV2Request
        {
            ProviderId        = _descriptor.RemoteProviderId!,
            Text              = text,
            Voice             = voiceSpec,
            LangCode          = string.IsNullOrWhiteSpace(profile.LangCode) ? "en" : profile.LangCode,
            SpeechRate        = profile.SpeechRate <= 0f ? 1.0f : profile.SpeechRate,
            CfgWeight         = profile.CfgWeight,
            Exaggeration      = profile.Exaggeration,
            BatchId           = batchId,
            BatchTotal        = batchTotal,
            CfgStrength       = profile.CfgStrength,
            NfeStep           = profile.NfeStep,
            CrossFadeDuration = profile.CrossFadeDuration,
            SwaysamplingCoef  = profile.SwaysamplingCoef,
            VoiceContext      = slot.ToString(),   // discriminates narrator vs NPC slots sharing same sample
        };

        var submitted = await _client.SynthesizeV2Async(v2Request, ct);
        System.Diagnostics.Debug.WriteLine(
            $"[RemoteTTS] v2 submitted: key={submitted.ProgressKey} cached={submitted.Cached}");
        return (submitted, profile);
    }

    /// <summary>
    /// Phase 2 of 2: wait for a submitted job to complete and fetch the OGG bytes.
    /// </summary>
    private async Task<byte[]> FetchOggResultAsync(V2SubmitResponse submitted, CancellationToken ct)
    {
        if (submitted.Cached)
        {
            var cached = await _client.GetV2ResultAsync(submitted.ProgressKey, ct);
            if (cached != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RemoteTTS] v2 cache hit: key={submitted.ProgressKey} bytes={cached.Length}");
                return cached;
            }
        }

        await _client.WaitForJobAsync(submitted.ProgressKey, ct);
        ct.ThrowIfCancellationRequested();

        var result = await _client.GetV2ResultAsync(submitted.ProgressKey, ct);
        if (result != null)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[RemoteTTS] v2 result fetched: key={submitted.ProgressKey} bytes={result.Length}");
            return result;
        }

        throw new InvalidOperationException(
            $"v2 synthesis completed but result unavailable for key={submitted.ProgressKey}");
    }

    private async Task<byte[]> SynthesizeOggCoreAsync(
        string text, VoiceSlot slot, VoiceProfile profile,
        CancellationToken ct,
        string? bespokeSampleId     = null,
        float?  bespokeExaggeration = null,
        float?  bespokeCfgWeight    = null,
        string? batchId             = null,
        int?    batchTotal          = null)
    {
        // Convenience wrapper — submit then fetch.
        var (submitted, _) = await SubmitOggCoreAsync(text, slot, profile, ct,
            bespokeSampleId, bespokeExaggeration, bespokeCfgWeight,
            batchId, batchTotal);
        return await FetchOggResultAsync(submitted, ct);
    }

    // ── Chatterbox text cleanup ───────────────────────────────────────────────

    /// <summary>
    /// Cleans text before sending to Chatterbox. Chatterbox is sensitive to:
    ///   - Inline angle-bracket annotations like &lt;A water stain...&gt;
    ///   - Dash-interrupted sentences reconstructed across paragraph breaks
    ///   - Trailing/leading dashes left over from reconstruction
    /// </summary>
    private static string ChatterboxPreprocess(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"<([^>]{1,120})>", "$1",
            System.Text.RegularExpressions.RegexOptions.None);

        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(\w)-\s*\.\.\.\s*-(\w)", "$1. $2",
            System.Text.RegularExpressions.RegexOptions.None);

        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"(?<=\s)-+(?=\w)|(?<=\w)-+(?=\s)", " ");

        text = System.Text.RegularExpressions.Regex.Replace(text, @"  +", " ").Trim();

        return text;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private async Task<(PcmAudio audio, int index)> SynthesizeChunkAsync(
        string text, VoiceSlot slot, int index, CancellationToken ct)
    {
        var oggBytes = await SynthesizeOggAsync(text, slot, ct);
        var audio    = await DecodeOggAsync(oggBytes, ct);
        return (audio, index);
    }

    public string ResolveVoiceId(VoiceSlot slot)
    {
        var profile = ResolveProfile(slot);
        return profile?.BuildIdentityKey() ?? string.Empty;
    }

    public VoiceProfile? ResolveProfile(VoiceSlot slot)
    {
        if (_settings.PerProviderVoiceProfiles.TryGetValue(ProviderId, out var dict) &&
            dict.TryGetValue(slot.ToString(), out var profile) &&
            profile != null)
        {
            return profile;
        }

        if (_descriptor.VoiceSourceKind == RemoteVoiceSourceKind.Samples)
            return GetDefaultSampleProfile(slot);

        return VoiceProfileDefaults.Create(GetAvailableVoices().FirstOrDefault()?.VoiceId ?? string.Empty);
    }

    public VoiceProfile ResolveSampleProfile(string sampleId, VoiceSlot? fallbackSlot = null)
    {
        if (_settings.PerProviderSampleProfiles.TryGetValue(ProviderId, out var dict) &&
            dict.TryGetValue(sampleId, out var stored) &&
            stored != null)
        {
            var cloned = stored.Clone();
            if (string.IsNullOrWhiteSpace(cloned.VoiceId))
                cloned.VoiceId = sampleId;
            return cloned;
        }

        var fallback = fallbackSlot.HasValue ? ResolveProfile(fallbackSlot.Value)?.Clone() : null;
        fallback ??= VoiceProfileDefaults.Create(sampleId);
        fallback.VoiceId = sampleId;
        if (string.IsNullOrWhiteSpace(fallback.LangCode))
            fallback.LangCode = VoiceProfileDefaults.GetDefaultLangCodeForVoice(sampleId);
        return fallback;
    }

    public async Task<IReadOnlyList<VoiceInfo>> RefreshVoiceSourcesAsync(CancellationToken ct)
    {
        _voiceCache = await _client.GetAvailableVoiceSourcesAsync(_descriptor, ct);
        return _voiceCache;
    }

    public IReadOnlyList<VoiceInfo> GetAvailableVoices()
        => _voiceCache ?? Array.Empty<VoiceInfo>();

    public void Dispose() { }

    // ── Concurrency helper ────────────────────────────────────────────────────

    private static async Task<T[]> RunLimitedAsync<TIn, T>(
        IEnumerable<TIn> items,
        int maxConcurrency,
        Func<TIn, Task<T>> work,
        CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            try   { return await work(item).ConfigureAwait(false); }
            finally { semaphore.Release(); }
        }).ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ── Default sample resolution ─────────────────────────────────────────────

    private VoiceProfile? GetDefaultSampleProfile(VoiceSlot slot)
    {
        var available    = GetAvailableVoices();
        var preferredIds = BuildPreferredSampleIds(slot);

        if (available.Count > 0)
        {
            var guaranteed = available.Where(v => IsGuaranteedDefaultSample(v.VoiceId)).ToList();
            if (guaranteed.Count > 0)
            {
                foreach (var preferred in preferredIds)
                {
                    var exact = guaranteed.FirstOrDefault(v =>
                        string.Equals(v.VoiceId, preferred, StringComparison.OrdinalIgnoreCase));
                    if (exact != null)
                        return VoiceProfileDefaults.Create(exact.VoiceId);

                    var stem    = RemoveRegionSuffix(preferred);
                    var partial = guaranteed.FirstOrDefault(v =>
                        v.VoiceId.StartsWith(stem, StringComparison.OrdinalIgnoreCase));
                    if (partial != null)
                        return VoiceProfileDefaults.Create(partial.VoiceId);
                }

                var families = preferredIds
                    .Select(GetSampleFamilyPrefix)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var family in families)
                {
                    var familyMatch = guaranteed.FirstOrDefault(v =>
                        v.VoiceId.StartsWith(family!, StringComparison.OrdinalIgnoreCase));
                    if (familyMatch != null)
                        return VoiceProfileDefaults.Create(familyMatch.VoiceId);
                }

                return VoiceProfileDefaults.Create(guaranteed.First().VoiceId);
            }
        }

        return VoiceProfileDefaults.Create(preferredIds.FirstOrDefault() ?? string.Empty);
    }

    private string[] BuildPreferredSampleIds(VoiceSlot slot)
    {
        if (IsF5Provider())
        {
            return slot.Gender == Gender.Female
                ? new[] { "zf_xiaobei-en-us", "zf_xiaoxiao-en-us", "zf_xiaoyi-en-us", "zf_xiaoni-en-us" }
                : new[] { "zm_yunjian-en-us", "zm_yunyang-en-us", "zm_yunxi-en-us", "zm_yunxia-en-us" };
        }

        var stem = GetPreferredSampleStem(slot);
        return ExpandSampleStemToPreferredIds(stem);
    }

    private static string[] ExpandSampleStemToPreferredIds(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
            return Array.Empty<string>();

        return stem switch
        {
            "af_heart" => new[] { "af-heart-en-us" },
            _ when stem.StartsWith("af_", StringComparison.OrdinalIgnoreCase) ||
                   stem.StartsWith("am_", StringComparison.OrdinalIgnoreCase)
                => new[] { stem + "-en-us" },
            _ when stem.StartsWith("bf_", StringComparison.OrdinalIgnoreCase) ||
                   stem.StartsWith("bm_", StringComparison.OrdinalIgnoreCase)
                => new[] { stem + "-en-gb" },
            _ when stem.StartsWith("jf_", StringComparison.OrdinalIgnoreCase) ||
                   stem.StartsWith("jm_", StringComparison.OrdinalIgnoreCase) ||
                   stem.StartsWith("zf_", StringComparison.OrdinalIgnoreCase) ||
                   stem.StartsWith("zm_", StringComparison.OrdinalIgnoreCase)
                => new[] { stem + "-en-us", stem + "-en-gb" },
            _ => new[] { stem },
        };
    }

    private static string GetPreferredSampleStem(VoiceSlot slot)
    {
        if (slot.Group == AccentGroup.Narrator)
            return "am_adam";

        bool f = slot.Gender == Gender.Female;
        return slot.Group switch
        {
            AccentGroup.Human               => f ? "af_sarah"    : "am_michael",
            AccentGroup.NightElf            => f ? "bf_alice"    : "bm_george",
            AccentGroup.Dwarf               => f ? "bf_alice"    : "bm_george",
            AccentGroup.DarkIronDwarf       => f ? "bf_alice"    : "bm_george",
            AccentGroup.Gnome               => f ? "af_nova"     : "am_puck",
            AccentGroup.Mechagnome          => f ? "af_nova"     : "am_puck",
            AccentGroup.Draenei             => f ? "bf_isabella" : "bm_lewis",
            AccentGroup.LightforgedDraenei  => f ? "bf_isabella" : "bm_george",
            AccentGroup.Worgen              => f ? "bf_emma"     : "bm_daniel",
            AccentGroup.KulTiran            => f ? "bf_emma"     : "bm_george",
            AccentGroup.BloodElf            => f ? "bf_isabella" : "bm_lewis",
            AccentGroup.VoidElf             => f ? "bf_isabella" : "bm_lewis",
            AccentGroup.Orc                 => f ? "af_alloy"    : "am_fenrir",
            AccentGroup.MagharOrc           => f ? "af_alloy"    : "am_fenrir",
            AccentGroup.Undead              => f ? "af_alloy"    : "am_onyx",
            AccentGroup.Tauren              => f ? "af_bella"    : "am_fenrir",
            AccentGroup.HighmountainTauren  => f ? "af_bella"    : "am_fenrir",
            AccentGroup.Troll               => f ? "af_aoede"    : "am_echo",
            AccentGroup.ZandalariTroll      => f ? "bf_alice"    : "am_adam",
            AccentGroup.Goblin              => f ? "af_nova"     : "am_eric",
            AccentGroup.Nightborne          => f ? "bf_alice"    : "bm_fable",
            AccentGroup.Vulpera             => f ? "af_sky"      : "am_puck",
            AccentGroup.Pandaren            => f ? "jf_alpha"    : "jm_kumo",
            AccentGroup.Earthen             => f ? "bf_emma"     : "am_adam",
            AccentGroup.Haranir             => f ? "af_bella"    : "am_adam",
            AccentGroup.Dracthyr            => f ? "bf_isabella" : "bm_lewis",
            AccentGroup.Dragonkin           => f ? "bf_isabella" : "am_onyx",
            AccentGroup.Elemental           => f ? "af_alloy"    : "am_onyx",
            AccentGroup.Giant               => f ? "af_kore"     : "am_fenrir",
            AccentGroup.Mechanical          => f ? "af_nova"     : "am_puck",
            AccentGroup.Amani               => f ? "af_aoede"    : "am_echo",
            AccentGroup.Arathi              => f ? "bf_alice"    : "bm_george",
            AccentGroup.Broken              => f ? "bf_isabella" : "bm_lewis",
            AccentGroup.Centaur             => f ? "af_bella"    : "am_fenrir",
            AccentGroup.DarkTroll           => f ? "af_aoede"    : "am_fenrir",
            AccentGroup.Dredger             => f ? "af_alloy"    : "am_onyx",
            AccentGroup.Dryad               => f ? "bf_alice"    : "bf_alice",
            AccentGroup.Faerie              => f ? "af_nova"     : "af_sky",
            AccentGroup.Fungarian           => f ? "af_nova"     : "am_puck",
            AccentGroup.Grummle             => f ? "af_sky"      : "am_puck",
            AccentGroup.Hobgoblin           => f ? "af_nova"     : "am_eric",
            AccentGroup.Kyrian              => f ? "bf_alice"    : "bm_george",
            AccentGroup.Nerubian            => f ? "af_alloy"    : "am_onyx",
            AccentGroup.Refti               => f ? "af_alloy"    : "am_onyx",
            AccentGroup.Revantusk           => f ? "af_aoede"    : "am_echo",
            AccentGroup.Rutaani             => f ? "af_jessica"  : "am_fenrir",
            AccentGroup.Shadowpine          => f ? "af_aoede"    : "am_echo",
            AccentGroup.Titan               => f ? "af_kore"     : "am_onyx",
            AccentGroup.Tortollan           => f ? "af_bella"    : "am_adam",
            AccentGroup.Tuskarr             => f ? "af_bella"    : "am_adam",
            AccentGroup.Venthyr             => f ? "bf_isabella" : "bm_fable",
            AccentGroup.ZulAman             => f ? "af_aoede"    : "am_echo",
            _                               => slot.Gender == Gender.Female ? "af_bella" : "am_adam",
        };
    }

    private bool IsF5Provider()
        => (_descriptor.RemoteProviderId ?? string.Empty)
            .Contains("f5", StringComparison.OrdinalIgnoreCase);

    private static bool IsGuaranteedDefaultSample(string? sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId)) return false;
        return sampleId.StartsWith("af", StringComparison.OrdinalIgnoreCase) ||
               sampleId.StartsWith("am", StringComparison.OrdinalIgnoreCase) ||
               sampleId.StartsWith("bf", StringComparison.OrdinalIgnoreCase) ||
               sampleId.StartsWith("bm", StringComparison.OrdinalIgnoreCase) ||
               sampleId.StartsWith("jf", StringComparison.OrdinalIgnoreCase) ||
               sampleId.StartsWith("jm", StringComparison.OrdinalIgnoreCase) ||
               sampleId.StartsWith("zf", StringComparison.OrdinalIgnoreCase) ||
               sampleId.StartsWith("zm", StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveRegionSuffix(string sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId)) return string.Empty;
        foreach (var suffix in new[] { "-en-us", "-en-gb" })
        {
            if (sampleId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return sampleId[..^suffix.Length];
        }
        return sampleId;
    }

    private static string? GetSampleFamilyPrefix(string sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId) || sampleId.Length < 2) return null;
        return sampleId.Substring(0, 2);
    }

    // ── Voice spec builder ────────────────────────────────────────────────────

    private RemoteVoiceSpec BuildVoiceSpec(VoiceProfile profile)
    {
        if (_descriptor.SupportsVoiceBlending &&
            !string.IsNullOrWhiteSpace(profile.VoiceId) &&
            profile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteVoiceSpec { Type = "blend", Blend = ParseBlend(profile.VoiceId) };
        }

        if (_descriptor.SupportsBaseVoices)
            return new RemoteVoiceSpec { Type = "base", VoiceId = profile.VoiceId };

        if (_descriptor.SupportsVoiceMatching)
            return new RemoteVoiceSpec { Type = "reference", SampleId = profile.VoiceId };

        throw new InvalidOperationException(
            $"Remote provider '{DisplayName}' has no supported voice source mode.");
    }

    private static List<RemoteBlendSpec> ParseBlend(string blendSpec)
    {
        var result = new List<RemoteBlendSpec>();
        if (string.IsNullOrWhiteSpace(blendSpec)) return result;

        var raw = blendSpec;
        if (raw.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[KokoroTtsProvider.MixPrefix.Length..];

        foreach (var part in raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length != 2) continue;
            if (!float.TryParse(pieces[1],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var weight))
                continue;
            result.Add(new RemoteBlendSpec { VoiceId = pieces[0], Weight = weight });
        }

        return result;
    }

    // ── PCM / OGG helpers ─────────────────────────────────────────────────────

    private static PcmAudio ConcatenatePcm(IReadOnlyList<PcmAudio> chunks)
    {
        if (chunks.Count == 0)
            return new PcmAudio(Array.Empty<float>(), 24000, 1);

        int targetSampleRate = chunks.Max(c => c.SampleRate);
        int targetChannels   = chunks.Max(c => Math.Max(1, c.Channels));

        var normalized = chunks
            .Select(chunk => NormalizePcm(chunk, targetSampleRate, targetChannels))
            .ToList();

        if (chunks.Any(c => c.SampleRate != targetSampleRate || Math.Max(1, c.Channels) != targetChannels))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[RemoteTTS] Normalized mixed chunk formats to {targetSampleRate} Hz / {targetChannels} ch before concatenation.");
        }

        int totalSamples = normalized.Sum(c => c.Samples.Length);
        var merged       = new float[totalSamples];
        int offset       = 0;
        foreach (var chunk in normalized)
        {
            Array.Copy(chunk.Samples, 0, merged, offset, chunk.Samples.Length);
            offset += chunk.Samples.Length;
        }

        return new PcmAudio(merged, targetSampleRate, targetChannels);
    }

    private static PcmAudio NormalizePcm(PcmAudio audio, int targetSampleRate, int targetChannels)
    {
        var working = audio;

        if (Math.Max(1, working.Channels) != targetChannels)
            working = ConvertChannels(working, targetChannels);

        if (working.SampleRate != targetSampleRate)
            working = ResampleLinear(working, targetSampleRate);

        return working;
    }

    private static PcmAudio ConvertChannels(PcmAudio audio, int targetChannels)
    {
        int sourceChannels = Math.Max(1, audio.Channels);
        targetChannels = Math.Max(1, targetChannels);

        if (sourceChannels == targetChannels)
            return audio;

        int frameCount = audio.Samples.Length / sourceChannels;
        var converted = new float[frameCount * targetChannels];

        if (sourceChannels == 1 && targetChannels == 2)
        {
            for (int i = 0; i < frameCount; i++)
            {
                var s = audio.Samples[i];
                int o = i * 2;
                converted[o] = s;
                converted[o + 1] = s;
            }
            return new PcmAudio(converted, audio.SampleRate, 2);
        }

        if (sourceChannels == 2 && targetChannels == 1)
        {
            for (int i = 0; i < frameCount; i++)
            {
                int o = i * 2;
                converted[i] = (audio.Samples[o] + audio.Samples[o + 1]) * 0.5f;
            }
            return new PcmAudio(converted, audio.SampleRate, 1);
        }

        for (int frame = 0; frame < frameCount; frame++)
        {
            int srcBase = frame * sourceChannels;
            int dstBase = frame * targetChannels;
            for (int ch = 0; ch < targetChannels; ch++)
            {
                int srcCh = Math.Min(ch, sourceChannels - 1);
                converted[dstBase + ch] = audio.Samples[srcBase + srcCh];
            }
        }

        return new PcmAudio(converted, audio.SampleRate, targetChannels);
    }

    private static PcmAudio ResampleLinear(PcmAudio audio, int targetSampleRate)
    {
        if (audio.SampleRate == targetSampleRate)
            return audio;

        int channels = Math.Max(1, audio.Channels);
        int sourceFrames = audio.Samples.Length / channels;
        if (sourceFrames <= 1)
            return new PcmAudio(audio.Samples.ToArray(), targetSampleRate, channels);

        int targetFrames = Math.Max(1, (int)Math.Round(sourceFrames * (targetSampleRate / (double)audio.SampleRate)));
        var resampled = new float[targetFrames * channels];
        double ratio = audio.SampleRate / (double)targetSampleRate;

        for (int frame = 0; frame < targetFrames; frame++)
        {
            double srcPos = frame * ratio;
            int srcIndex0 = Math.Min((int)srcPos, sourceFrames - 1);
            int srcIndex1 = Math.Min(srcIndex0 + 1, sourceFrames - 1);
            float frac = (float)(srcPos - srcIndex0);

            int dstBase = frame * channels;
            int srcBase0 = srcIndex0 * channels;
            int srcBase1 = srcIndex1 * channels;
            for (int ch = 0; ch < channels; ch++)
            {
                float s0 = audio.Samples[srcBase0 + ch];
                float s1 = audio.Samples[srcBase1 + ch];
                resampled[dstBase + ch] = s0 + ((s1 - s0) * frac);
            }
        }

        return new PcmAudio(resampled, targetSampleRate, channels);
    }

    private static async Task<byte[]> EncodeOggAsync(PcmAudio audio, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            int   channels   = Math.Max(1, audio.Channels);
            int   sampleRate = audio.SampleRate;
            float quality    = 0.8f;

            var info     = VorbisInfo.InitVariableBitRate(channels, sampleRate, quality);
            var comments = new Comments();
            comments.AddTag("ENCODER", "RuneReaderVoice");

            var oggStream = new OggStream(new Random().Next());
            oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
            oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
            oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

            using var ms              = new MemoryStream();
            var       processingState = ProcessingState.Create(info);

            while (oggStream.PageOut(out OggPage page, true))
            {
                ms.Write(page.Header, 0, page.Header.Length);
                ms.Write(page.Body,   0, page.Body.Length);
            }

            var        pcmChannels = Deinterleave(audio);
            const int  chunkSize   = 1024;
            int        total       = pcmChannels[0].Length;
            int        offset      = 0;
            while (offset < total)
            {
                ct.ThrowIfCancellationRequested();
                int count = Math.Min(chunkSize, total - offset);
                var chunk = new float[channels][];
                for (int c = 0; c < channels; c++)
                {
                    chunk[c] = new float[count];
                    Array.Copy(pcmChannels[c], offset, chunk[c], 0, count);
                }

                processingState.WriteData(chunk, count);
                offset += count;

                while (processingState.PacketOut(out OggPacket packet))
                {
                    oggStream.PacketIn(packet);
                    while (oggStream.PageOut(out OggPage page, false))
                    {
                        ms.Write(page.Header, 0, page.Header.Length);
                        ms.Write(page.Body,   0, page.Body.Length);
                    }
                }
            }

            processingState.WriteEndOfStream();
            while (processingState.PacketOut(out OggPacket packet))
            {
                oggStream.PacketIn(packet);
                while (oggStream.PageOut(out OggPage page, true))
                {
                    ms.Write(page.Header, 0, page.Header.Length);
                    ms.Write(page.Body,   0, page.Body.Length);
                }
            }

            return ms.ToArray();
        }, ct);
    }

    private static float[][] Deinterleave(PcmAudio audio)
    {
        int channels = Math.Max(1, audio.Channels);
        if (audio.Samples.Length == 0)
        {
            var empty = new float[channels][];
            for (int c = 0; c < channels; c++) empty[c] = Array.Empty<float>();
            return empty;
        }

        int sampleCount    = audio.Samples.Length / channels;
        var channelArrays  = new float[channels][];
        for (int c = 0; c < channels; c++)
            channelArrays[c] = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
            for (int c = 0; c < channels; c++)
                channelArrays[c][i] = audio.Samples[(i * channels) + c];

        return channelArrays;
    }

    private static async Task<PcmAudio> DecodeOggAsync(byte[] oggBytes, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var ms     = new MemoryStream(oggBytes, writable: false);
            using var vorbis = new VorbisReader(ms, leaveOpen: false);
            vorbis.Initialize();

            var sampleRate = vorbis.SampleRate;
            var channels   = vorbis.Channels;
            var samples    = new List<float>(sampleRate * channels * 5);
            var readBuf    = new float[4096];
            int read;
            while ((read = vorbis.ReadSamples(readBuf)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                for (var i = 0; i < read; i++)
                    samples.Add(readBuf[i]);
            }

            return new PcmAudio(samples.ToArray(), sampleRate, channels);
        }, ct);
    }
}