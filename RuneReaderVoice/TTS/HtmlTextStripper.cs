using System.Net;
using System.Text.RegularExpressions;

namespace RuneReaderVoice.TTS;

public static class HtmlTextStripper
{
    private static readonly Regex BreakRegex = new(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlockOpenRegex = new(@"<\s*(p|div|li|tr|table|ul|ol|h[1-6])\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlockCloseRegex = new(@"<\s*/\s*(p|div|li|tr|table|ul|ol|h[1-6])\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WrapperTagRegex = new(@"<\s*/?\s*(html|body|span|font|b|strong|i|em|u|small|big|center)\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BlankLineRegex = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex SpaceRunRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public static string Strip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text ?? string.Empty;

        var value = text.Replace("\r\n", "\n");

        // Convert known HTML structural tags into readable separators first so
        // adjacent text does not collapse together.
        value = BreakRegex.Replace(value, "\n");
        value = BlockOpenRegex.Replace(value, "\n");
        value = BlockCloseRegex.Replace(value, "\n");

        // Remove only known wrapper/formatting tags. Do not use a blanket
        // <...> stripper because narrator markers and tokenized text can also
        // use angle brackets.
        value = WrapperTagRegex.Replace(value, string.Empty);

        value = WebUtility.HtmlDecode(value);
        value = value.Replace("\r\n", "\n");
        value = SpaceRunRegex.Replace(value, " ");
        value = Regex.Replace(value, @" *\n *", "\n");
        value = BlankLineRegex.Replace(value, "\n\n");
        return value.Trim();
    }
}
