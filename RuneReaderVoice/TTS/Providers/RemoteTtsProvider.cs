using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NVorbis;
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
        yield return (await SynthesizeAsync(text, slot, ct), 0, 1);
    }

    public async Task<PcmAudio> SynthesizeAsync(string text, VoiceSlot slot, CancellationToken ct)
    {
        var oggBytes = await SynthesizeOggAsync(text, slot, ct);
        return await DecodeOggAsync(oggBytes, ct);
    }

    public async Task<byte[]> SynthesizeOggAsync(string text, VoiceSlot slot, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_descriptor.RemoteProviderId))
            throw new InvalidOperationException("Remote provider id is missing.");

        var profile = ResolveProfile(slot) ?? VoiceProfileDefaults.Create(string.Empty);
        var voiceSpec = BuildVoiceSpec(profile);
        var request = new RemoteSynthesizeRequest
        {
            ProviderId = _descriptor.RemoteProviderId,
            Text = text,
            Voice = voiceSpec,
            LangCode = string.IsNullOrWhiteSpace(profile.LangCode) ? "en" : profile.LangCode,
            SpeechRate = profile.SpeechRate <= 0f ? 1.0f : profile.SpeechRate,
            CfgWeight = profile.CfgWeight,
            Exaggeration = profile.Exaggeration,
        };

        return await _client.SynthesizeAsync(request, ct);
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
