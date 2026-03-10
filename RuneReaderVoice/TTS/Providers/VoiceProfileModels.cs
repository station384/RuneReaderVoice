using System;
using System.Collections.Generic;
using System.Linq;

namespace RuneReaderVoice.TTS.Providers;

public sealed class VoiceProfile
{
    public string VoiceId { get; set; } = string.Empty;
    public string LangCode { get; set; } = string.Empty;
    public float SpeechRate { get; set; } = 1.0f;

    public VoiceProfile Clone() => new()
    {
        VoiceId = VoiceId,
        LangCode = LangCode,
        SpeechRate = SpeechRate
    };

    public string BuildIdentityKey() => $"{VoiceId}|{LangCode}|{SpeechRate:0.00}";
}

public sealed class EspeakLanguageOption
{
    public string Code { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsPinned { get; init; }

    public override string ToString() => DisplayName;
}

public static class VoiceProfileDefaults
{
    public static string GetDefaultLangCodeForVoice(string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
            return "en-us";

        if (voiceId.StartsWith(KokoroTtsProvider.MixPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var body = voiceId[KokoroTtsProvider.MixPrefix.Length..];
            var first = body.Split('|', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(first))
            {
                var colon = first.IndexOf(':');
                var firstVoice = colon > 0 ? first[..colon] : first;
                return GetDefaultLangCodeForVoice(firstVoice);
            }
        }

        if (voiceId.StartsWith("bf_", StringComparison.OrdinalIgnoreCase) ||
            voiceId.StartsWith("bm_", StringComparison.OrdinalIgnoreCase))
            return "en-gb";

        return "en-us";
    }

    public static VoiceProfile Create(string voiceId) => new()
    {
        VoiceId = voiceId,
        LangCode = GetDefaultLangCodeForVoice(voiceId),
        SpeechRate = 1.0f
    };
}

public static class EspeakLanguageCatalog
{
    public static IReadOnlyList<EspeakLanguageOption> All { get; } = Build();

    private static IReadOnlyList<EspeakLanguageOption> Build()
    {
        var list = new List<EspeakLanguageOption>
        {
            Add("en-us", "English (America)", true),
            Add("en-gb", "English (Great Britain)", true),
            Add("en-gb-scotland", "English (Scotland)", true),
            Add("en-gb-x-rp", "English (Received Pronunciation)", true),
            Add("en-029", "English (Caribbean)", true),
            Add("en-us-nyc", "English (America, New York City)", true),

            Add("af", "Afrikaans"),
            Add("am", "Amharic"),
            Add("an", "Aragonese"),
            Add("ar", "Arabic"),
            Add("as", "Assamese"),
            Add("az", "Azerbaijani"),
            Add("ba", "Bashkir"),
            Add("be", "Belarusian"),
            Add("bg", "Bulgarian"),
            Add("bn", "Bengali"),
            Add("bpy", "Bishnupriya Manipuri"),
            Add("bs", "Bosnian"),
            Add("ca", "Catalan"),
            Add("chr-US-Qaaa-x-west", "Cherokee"),
            Add("cmn", "Chinese (Mandarin, Latin as English)"),
            Add("cmn-latn-pinyin", "Chinese (Mandarin, Latin as Pinyin)"),
            Add("cs", "Czech"),
            Add("cv", "Chuvash"),
            Add("cy", "Welsh"),
            Add("da", "Danish"),
            Add("de", "German"),
            Add("el", "Greek"),
            Add("en-gb-x-gbclan", "English (Lancaster)"),
            Add("en-gb-x-gbcwmd", "English (West Midlands)"),
            Add("eo", "Esperanto"),
            Add("es", "Spanish (Spain)"),
            Add("es-419", "Spanish (Latin America)"),
            Add("et", "Estonian"),
            Add("eu", "Basque"),
            Add("fa", "Persian"),
            Add("fa-latn", "Persian (Pinglish)"),
            Add("fi", "Finnish"),
            Add("fr-be", "French (Belgium)"),
            Add("fr-ch", "French (Switzerland)"),
            Add("fr-fr", "French (France)"),
            Add("ga", "Gaelic (Irish)"),
            Add("gd", "Gaelic (Scottish)"),
            Add("gn", "Guarani"),
            Add("grc", "Greek (Ancient)"),
            Add("gu", "Gujarati"),
            Add("hak", "Hakka Chinese"),
            Add("haw", "Hawaiian"),
            Add("he", "Hebrew"),
            Add("hi", "Hindi"),
            Add("hr", "Croatian"),
            Add("ht", "Haitian Creole"),
            Add("hu", "Hungarian"),
            Add("hy", "Armenian (East Armenia)"),
            Add("hyw", "Armenian (West Armenia)"),
            Add("ia", "Interlingua"),
            Add("id", "Indonesian"),
            Add("io", "Ido"),
            Add("is", "Icelandic"),
            Add("it", "Italian"),
            Add("ja", "Japanese"),
            Add("jbo", "Lojban"),
            Add("ka", "Georgian"),
            Add("kk", "Kazakh"),
            Add("kl", "Greenlandic"),
            Add("kn", "Kannada"),
            Add("ko", "Korean"),
            Add("kok", "Konkani"),
            Add("ku", "Kurdish"),
            Add("ky", "Kyrgyz"),
            Add("la", "Latin"),
            Add("lb", "Luxembourgish"),
            Add("lfn", "Lingua Franca Nova"),
            Add("lt", "Lithuanian"),
            Add("ltg", "Latgalian"),
            Add("lv", "Latvian"),
            Add("mi", "Māori"),
            Add("mk", "Macedonian"),
            Add("ml", "Malayalam"),
            Add("mr", "Marathi"),
            Add("ms", "Malay"),
            Add("mt", "Maltese"),
            Add("my", "Myanmar (Burmese)"),
            Add("nb", "Norwegian Bokmål"),
            Add("nci", "Nahuatl (Classical)"),
            Add("ne", "Nepali"),
            Add("nl", "Dutch"),
            Add("nog", "Nogai"),
            Add("om", "Oromo"),
            Add("or", "Oriya"),
            Add("pa", "Punjabi"),
            Add("pap", "Papiamento"),
            Add("piqd", "Klingon"),
            Add("pl", "Polish"),
            Add("pt", "Portuguese (Portugal)"),
            Add("pt-br", "Portuguese (Brazil)"),
            Add("py", "Pyash"),
            Add("qdb", "Lang Belta"),
            Add("qu", "Quechua"),
            Add("quc", "K'iche'"),
            Add("qya", "Quenya"),
            Add("ro", "Romanian"),
            Add("ru", "Russian"),
            Add("ru-lv", "Russian (Latvia)"),
            Add("sd", "Sindhi"),
            Add("shn", "Shan (Tai Yai)"),
            Add("si", "Sinhala"),
            Add("sjn", "Sindarin"),
            Add("sk", "Slovak"),
            Add("sl", "Slovenian"),
            Add("smj", "Lule Saami"),
            Add("sq", "Albanian"),
            Add("sr", "Serbian"),
            Add("sv", "Swedish"),
            Add("sw", "Swahili"),
            Add("ta", "Tamil"),
            Add("te", "Telugu"),
            Add("th", "Thai"),
            Add("tk", "Turkmen"),
            Add("tn", "Setswana"),
            Add("tr", "Turkish"),
            Add("tt", "Tatar"),
            Add("ug", "Uyghur"),
            Add("uk", "Ukrainian"),
            Add("ur", "Urdu"),
            Add("uz", "Uzbek"),
            Add("vi", "Vietnamese (Northern)"),
            Add("vi-vn-x-central", "Vietnamese (Central)"),
            Add("vi-vn-x-south", "Vietnamese (Southern)"),
            Add("yue", "Chinese (Cantonese)"),
            Add("yue-latn-jyutping", "Chinese (Cantonese, Latin as Jyutping)")
        };

        var pinned = list.Where(x => x.IsPinned);
        var rest = list.Where(x => !x.IsPinned)
                       .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase);

        return pinned.Concat(rest).ToList();
    }

    private static EspeakLanguageOption Add(string code, string displayName, bool pinned = false)
        => new() { Code = code, DisplayName = displayName, IsPinned = pinned };
}