using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NVorbis;
using OggVorbisEncoder;
using RuneReaderVoice.Protocol;

namespace RuneReaderVoice.TTS.Providers;

public sealed class RemoteTtsProvider : ITtsProvider
{
    private readonly VoiceUserSettings _settings;
    private readonly ProviderDescriptor _descriptor;
    private readonly RemoteTtsClient _client;
    private IReadOnlyList<VoiceInfo>? _voiceCache;

    public RemoteTtsProvider(VoiceUserSettings settings, ProviderDescriptor descriptor)
    {
        _settings = settings;
        _descriptor = descriptor;
        _client = new RemoteTtsClient(settings.RemoteServerUrl, settings.RemoteApiKey);
    }

    public string ProviderId => _descriptor.ClientProviderId;
    public string DisplayName => _descriptor.DisplayName;
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_settings.RemoteServerUrl);
    public bool RequiresFullText => _descriptor.RequiresFullText;
    public bool SupportsInlinePronunciationHints => _descriptor.SupportsInlinePronunciationHints;

    public async IAsyncEnumerable<(PcmAudio audio, int phraseIndex, int phraseCount)> SynthesizePhraseStreamAsync(
        string text,
        VoiceSlot slot,
        string tempDirectory,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var profile = ResolveProfile(slot) ?? VoiceProfileDefaults.Create(string.Empty);
        var phrases = TextChunkingPolicy.GetChunkTexts(text, ProviderId, profile, _settings.EnablePhraseChunking && !profile.DisableChunking);
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

    public async Task<byte[]> SynthesizeOggAsync(string text, VoiceSlot slot, CancellationToken ct,
        string? bespokeSampleId = null, float? bespokeExaggeration = null, float? bespokeCfgWeight = null)
    {
        if (string.IsNullOrWhiteSpace(_descriptor.RemoteProviderId))
            throw new InvalidOperationException("Remote provider id is missing.");

        var profile = ResolveProfile(slot) ?? VoiceProfileDefaults.Create(string.Empty);
        var chunkingEnabled = _settings.EnablePhraseChunking && !profile.DisableChunking;
        var phrases = TextChunkingPolicy.GetChunkTexts(text, ProviderId, profile, chunkingEnabled);

        System.Diagnostics.Debug.WriteLine(
            $"[RemoteTTS] SynthesizeOgg provider={ProviderId} chunks={phrases.Count} chunking={chunkingEnabled} textLen={text.Length}");
        if (phrases.Count > 1)
        {
            for (int i = 0; i < phrases.Count; i++)
                System.Diagnostics.Debug.WriteLine(
                    $"[RemoteTTS]   chunk[{i}] len={phrases[i].Length}: '{phrases[i].Substring(0, Math.Min(50, phrases[i].Length))}'");
        }

        if (phrases.Count <= 1)
            return await SynthesizeOggCoreAsync(text, slot, profile, ct,
                bespokeSampleId, bespokeExaggeration, bespokeCfgWeight);

        var oggChunks = await RunLimitedAsync(phrases, 2,
            phrase => SynthesizeOggCoreAsync(phrase, slot, profile, ct,
                bespokeSampleId, bespokeExaggeration, bespokeCfgWeight), ct);

        var pcmChunks = new List<PcmAudio>(oggChunks.Length);
        foreach (var ogg in oggChunks)
            pcmChunks.Add(await DecodeOggAsync(ogg, ct));

        var combined = ConcatenatePcm(pcmChunks);
        return await EncodeOggAsync(combined, ct);
    }

    private async Task<byte[]> SynthesizeOggCoreAsync(string text, VoiceSlot slot, VoiceProfile profile,
        CancellationToken ct,
        string? bespokeSampleId = null, float? bespokeExaggeration = null, float? bespokeCfgWeight = null)
    {
        // Apply bespoke overrides on top of the resolved race-slot profile.
        // DSP is NOT touched here — it lives in profile.Dsp and is applied post-synthesis.
        if (!string.IsNullOrWhiteSpace(bespokeSampleId))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[RemoteTTS] Bespoke override: sample={bespokeSampleId} ex={bespokeExaggeration} cfg={bespokeCfgWeight}");
            profile = profile.Clone();
            profile.VoiceId = bespokeSampleId;
            if (bespokeExaggeration.HasValue) profile.Exaggeration = bespokeExaggeration;
            if (bespokeCfgWeight.HasValue)    profile.CfgWeight    = bespokeCfgWeight;
        }

        // Chatterbox-specific text cleanup — applied before sending to the model.
        // Other backends handle these patterns better and don't need this.
        var providerId = _descriptor.RemoteProviderId ?? string.Empty;
        if (providerId.Contains("chatterbox", StringComparison.OrdinalIgnoreCase))
        {
            var before = text;
            text = ChatterboxPreprocess(text);
            if (text != before)
                System.Diagnostics.Debug.WriteLine(
                    $"[RemoteTTS] Chatterbox preprocess changed text: '{before.Substring(0, Math.Min(60, before.Length))}' -> '{text.Substring(0, Math.Min(60, text.Length))}'");
        }

        System.Diagnostics.Debug.WriteLine(
            $"[RemoteTTS] Core request: provider={providerId} voice={profile.VoiceId} exag={profile.Exaggeration} cfg={profile.CfgWeight} len={text.Length}");
        var voiceSpec = BuildVoiceSpec(profile);
        var request = new RemoteSynthesizeRequest
        {
            ProviderId   = _descriptor.RemoteProviderId!,
            Text         = text,
            Voice        = voiceSpec,
            LangCode     = string.IsNullOrWhiteSpace(profile.LangCode) ? "en" : profile.LangCode,
            SpeechRate   = profile.SpeechRate <= 0f ? 1.0f : profile.SpeechRate,
            CfgWeight    = profile.CfgWeight,
            Exaggeration = profile.Exaggeration,
        };

        return await _client.SynthesizeAsync(request, ct);
    }

    /// <summary>
    /// Cleans text before sending to Chatterbox. Chatterbox is sensitive to:
    ///   - Inline angle-bracket annotations like &lt;A water stain...&gt; — confuses
    ///     the model's sequence tracking mid-generation
    ///   - Dash-interrupted sentences reconstructed across paragraph breaks
    ///   - Trailing/leading dashes left over from reconstruction
    /// </summary>
    private static string ChatterboxPreprocess(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // 1. Replace <annotation text> with a spoken cue or remove it.
        //    WoW uses these for flavor notes (water stains, torn pages, etc.)
        //    We replace with "..." so there is a natural pause rather than silence.
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"<[^>]{1,120}>",
            "... ",
            System.Text.RegularExpressions.RegexOptions.None);

        // 2. Clean up dash-reconstructed sentences: "ship-\n\n...-scout" becomes
        //    "ship. Scout" — split at the reconstruction point into two sentences.
        //    Pattern: word-dash at end of a chunk, dash-word at start of next.
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(\w)-\s*\.\.\.\s*-(\w)",
            "$1. $2",
            System.Text.RegularExpressions.RegexOptions.None);

        // 3. Strip any remaining leading/trailing dashes that touch whitespace
        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?<=\s)-+(?=\w)|(?<=\w)-+(?=\s)",
            " ");

        // 4. Collapse multiple spaces and trim
        text = System.Text.RegularExpressions.Regex.Replace(text, @"  +", " ").Trim();

        return text;
    }

    private async Task<(PcmAudio audio, int index)> SynthesizeChunkAsync(string text, VoiceSlot slot, int index, CancellationToken ct)
    {
        var oggBytes = await SynthesizeOggAsync(text, slot, ct);
        var audio = await DecodeOggAsync(oggBytes, ct);
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

    public async Task<IReadOnlyList<VoiceInfo>> RefreshVoiceSourcesAsync(CancellationToken ct)
    {
        _voiceCache = await _client.GetAvailableVoiceSourcesAsync(_descriptor, ct);
        return _voiceCache;
    }

    public IReadOnlyList<VoiceInfo> GetAvailableVoices()
    {
        // Never block the UI thread on remote I/O here.
        // Call RefreshVoiceSourcesAsync(...) from an async UI flow when a live fetch is needed.
        return _voiceCache ?? Array.Empty<VoiceInfo>();
    }

    public void Dispose()
    {
    }

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
            try
            {
                return await work(item).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToArray();

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }


    private VoiceProfile? GetDefaultSampleProfile(VoiceSlot slot)
    {
        var available = GetAvailableVoices();
        var preferredIds = BuildPreferredSampleIds(slot);

        if (available.Count > 0)
        {
            var guaranteed = available.Where(v => IsGuaranteedDefaultSample(v.VoiceId)).ToList();
            if (guaranteed.Count > 0)
            {
                foreach (var preferred in preferredIds)
                {
                    var exact = guaranteed.FirstOrDefault(v => string.Equals(v.VoiceId, preferred, StringComparison.OrdinalIgnoreCase));
                    if (exact != null)
                        return VoiceProfileDefaults.Create(exact.VoiceId);

                    var stem = RemoveRegionSuffix(preferred);
                    var partial = guaranteed.FirstOrDefault(v => v.VoiceId.StartsWith(stem, StringComparison.OrdinalIgnoreCase));
                    if (partial != null)
                        return VoiceProfileDefaults.Create(partial.VoiceId);
                }

                var families = preferredIds.Select(GetSampleFamilyPrefix).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var family in families)
                {
                    var familyMatch = guaranteed.FirstOrDefault(v => v.VoiceId.StartsWith(family!, StringComparison.OrdinalIgnoreCase));
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
            _ when stem.StartsWith("af_", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("am_", StringComparison.OrdinalIgnoreCase)
                => new[] { stem + "-en-us" },
            _ when stem.StartsWith("bf_", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("bm_", StringComparison.OrdinalIgnoreCase)
                => new[] { stem + "-en-gb" },
            _ when stem.StartsWith("jf_", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("jm_", StringComparison.OrdinalIgnoreCase) ||
                   stem.StartsWith("zf_", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("zm_", StringComparison.OrdinalIgnoreCase)
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

            // Non-playable NPC races
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
            _                               => slot.Gender == Gender.Female ? "af_bella" : "am_adam"
        };
    }

    private bool IsF5Provider()
        => (_descriptor.RemoteProviderId ?? string.Empty).Contains("f5", StringComparison.OrdinalIgnoreCase);

    private static bool IsGuaranteedDefaultSample(string? sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId))
            return false;

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
        if (string.IsNullOrWhiteSpace(sampleId))
            return string.Empty;

        foreach (var suffix in new[] { "-en-us", "-en-gb" })
        {
            if (sampleId.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return sampleId[..^suffix.Length];
        }

        return sampleId;
    }

    private static string? GetSampleFamilyPrefix(string sampleId)
    {
        if (string.IsNullOrWhiteSpace(sampleId) || sampleId.Length < 2)
            return null;
        return sampleId.Substring(0, 2);
    }

    private RemoteVoiceSpec BuildVoiceSpec(VoiceProfile profile)
    {
        if (_descriptor.SupportsVoiceBlending && !string.IsNullOrWhiteSpace(profile.VoiceId) &&
            profile.VoiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteVoiceSpec
            {
                Type = "blend",
                Blend = ParseBlend(profile.VoiceId),
            };
        }

        if (_descriptor.SupportsBaseVoices)
        {
            return new RemoteVoiceSpec
            {
                Type = "base",
                VoiceId = profile.VoiceId,
            };
        }

        if (_descriptor.SupportsVoiceMatching)
        {
            return new RemoteVoiceSpec
            {
                Type = "reference",
                SampleId = profile.VoiceId,
            };
        }

        throw new InvalidOperationException($"Remote provider '{DisplayName}' has no supported voice source mode.");
    }

    private static List<RemoteBlendSpec> ParseBlend(string blendSpec)
    {
        var result = new List<RemoteBlendSpec>();
        if (string.IsNullOrWhiteSpace(blendSpec))
            return result;

        var raw = blendSpec;
        if (raw.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[KokoroTtsProvider.MixPrefix.Length..];

        foreach (var part in raw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(':', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length != 2) continue;
            if (!float.TryParse(pieces[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var weight))
                continue;
            result.Add(new RemoteBlendSpec { VoiceId = pieces[0], Weight = weight });
        }

        return result;
    }

    private static PcmAudio ConcatenatePcm(IReadOnlyList<PcmAudio> chunks)
    {
        if (chunks.Count == 0)
            return new PcmAudio(Array.Empty<float>(), 24000, 1);

        var first = chunks[0];
        int sampleRate = first.SampleRate;
        int channels = first.Channels;

        if (chunks.Any(c => c.SampleRate != sampleRate || c.Channels != channels))
            throw new InvalidOperationException("Remote chunk synthesis returned incompatible audio formats.");

        int totalSamples = chunks.Sum(c => c.Samples.Length);
        var merged = new float[totalSamples];
        int offset = 0;
        foreach (var chunk in chunks)
        {
            Array.Copy(chunk.Samples, 0, merged, offset, chunk.Samples.Length);
            offset += chunk.Samples.Length;
        }

        return new PcmAudio(merged, sampleRate, channels);
    }

    private static async Task<byte[]> EncodeOggAsync(PcmAudio audio, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            int channels = Math.Max(1, audio.Channels);
            int sampleRate = audio.SampleRate;
            float quality = 0.8f;

            var info = VorbisInfo.InitVariableBitRate(channels, sampleRate, quality);
            var comments = new Comments();
            comments.AddTag("ENCODER", "RuneReaderVoice");

            var oggStream = new OggStream(new Random().Next());
            oggStream.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
            oggStream.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(comments));
            oggStream.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

            using var ms = new MemoryStream();
            var processingState = ProcessingState.Create(info);

            while (oggStream.PageOut(out OggPage page, true))
            {
                ms.Write(page.Header, 0, page.Header.Length);
                ms.Write(page.Body, 0, page.Body.Length);
            }

            var pcmChannels = Deinterleave(audio);
            const int chunkSize = 1024;
            int total = pcmChannels[0].Length;
            int offset = 0;
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
                        ms.Write(page.Body, 0, page.Body.Length);
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
                    ms.Write(page.Body, 0, page.Body.Length);
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

        int sampleCount = audio.Samples.Length / channels;
        var channelArrays = new float[channels][];
        for (int c = 0; c < channels; c++)
            channelArrays[c] = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            for (int c = 0; c < channels; c++)
                channelArrays[c][i] = audio.Samples[(i * channels) + c];
        }

        return channelArrays;
    }

    private static async Task<PcmAudio> DecodeOggAsync(byte[] oggBytes, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using var ms = new MemoryStream(oggBytes, writable: false);
            using var vorbis = new VorbisReader(ms, leaveOpen: false);
            vorbis.Initialize();

            var sampleRate = vorbis.SampleRate;
            var channels = vorbis.Channels;
            var samples = new List<float>(sampleRate * channels * 5);
            var readBuf = new float[4096];
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
