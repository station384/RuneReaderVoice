using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace RuneReaderVoice.TTS;

internal static class HtmlRenderedTextExtractor
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "html", "body", "div", "p", "section", "article", "header", "footer", "aside", "main", "nav",
        "table", "thead", "tbody", "tfoot", "tr", "ul", "ol", "li", "blockquote", "pre",
        "h1", "h2", "h3", "h4", "h5", "h6"
    };

    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "noscript", "head", "meta", "link"
    };

    public static bool LooksLikeHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(
            text,
            @"</?(html|body|div|span|p|br|h[1-6]|table|tr|td|ul|ol|li|blockquote)\b|<[^>]+\s+(class|style|align|href|src)\s*=",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string ExtractRenderedText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var doc = new HtmlDocument
        {
            OptionFixNestedTags = true,
            OptionAutoCloseOnEnd = true
        };
        doc.LoadHtml(html);

        var sb = new StringBuilder(1024);
        Walk(doc.DocumentNode, sb);

        var text = sb.ToString();
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"[ \t\f\v]+", " ");
        text = Regex.Replace(text, @" *\n *", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    public static string ExtractFromMixedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        if (!TrySplitEmbeddedHtml(text, out var prefix, out var html))
            return ExtractRenderedText(text);

        var rendered = ExtractRenderedText(html);
        if (string.IsNullOrWhiteSpace(prefix))
            return rendered;
        if (string.IsNullOrWhiteSpace(rendered))
            return prefix.Trim();

        var prefixTrimmed = prefix.Trim();
        var renderedTrimmed = RemoveDuplicateLeadingLine(prefixTrimmed, rendered);
        return string.Concat(prefixTrimmed, "\n\n", renderedTrimmed.Trim());
    }

    private static bool TrySplitEmbeddedHtml(string text, out string prefix, out string html)
    {
        var match = Regex.Match(
            text,
            @"<(html|body|div|p|br|h[1-6]|img|table|tr|td|ul|ol|li|blockquote)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!match.Success)
        {
            prefix = string.Empty;
            html = text;
            return false;
        }

        prefix = text[..match.Index].Trim();
        html = text[match.Index..];
        return true;
    }

    private static string RemoveDuplicateLeadingLine(string prefix, string rendered)
    {
        var prefixLastLine = GetLastNonEmptyLine(prefix);
        var renderedFirstLine = GetFirstNonEmptyLine(rendered);

        if (string.IsNullOrWhiteSpace(prefixLastLine) || string.IsNullOrWhiteSpace(renderedFirstLine))
            return rendered;

        if (!string.Equals(prefixLastLine.Trim(), renderedFirstLine.Trim(), StringComparison.OrdinalIgnoreCase))
            return rendered;

        var normalized = rendered.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        bool removed = false;
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (!removed && string.Equals(line.Trim(), renderedFirstLine.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
                continue;
            }
            kept.Add(line);
        }

        return string.Join("\n", kept).Trim();
    }

    private static string GetFirstNonEmptyLine(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        foreach (var line in normalized.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
                return line;
        }
        return string.Empty;
    }

    private static string GetLastNonEmptyLine(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
                return lines[i];
        }
        return string.Empty;
    }

    private static void Walk(HtmlNode node, StringBuilder sb)
    {
        if (node.NodeType == HtmlNodeType.Comment)
            return;

        if (node.NodeType == HtmlNodeType.Document)
        {
            foreach (var child in node.ChildNodes)
                Walk(child, sb);
            return;
        }

        if (node.NodeType == HtmlNodeType.Text)
        {
            var text = WebUtility.HtmlDecode(node.InnerText);
            if (!string.IsNullOrWhiteSpace(text))
                AppendText(sb, text);
            return;
        }

        if (node.NodeType != HtmlNodeType.Element)
            return;

        var name = node.Name;
        if (SkipTags.Contains(name))
            return;

        if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            AppendNewline(sb, 1);
            return;
        }

        var isBlock = BlockTags.Contains(name);
        if (isBlock)
            AppendNewline(sb, RequiredBreaksBefore(name));

        if (name.Equals("li", StringComparison.OrdinalIgnoreCase))
            AppendText(sb, "• ");

        foreach (var child in node.ChildNodes)
            Walk(child, sb);

        if (isBlock)
            AppendNewline(sb, RequiredBreaksAfter(name));
    }

    private static void AppendText(StringBuilder sb, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        text = Regex.Replace(text, @"\s+", " ");
        if (text.Length == 0)
            return;

        if (sb.Length > 0)
        {
            var last = sb[^1];
            if (last != '\n' && last != ' ' && !char.IsPunctuation(last) && !char.IsPunctuation(text[0]))
                sb.Append(' ');
        }

        sb.Append(text);
    }

    private static void AppendNewline(StringBuilder sb, int count)
    {
        if (count <= 0)
            return;

        int trailing = 0;
        for (int i = sb.Length - 1; i >= 0 && sb[i] == '\n'; i--)
            trailing++;

        for (int i = 0; i < count - trailing; i++)
            sb.Append('\n');
    }

    private static int RequiredBreaksBefore(string tag) => tag switch
    {
        "p" or "div" or "section" or "article" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "ul" or "ol" or "table" => 2,
        "li" or "tr" => 1,
        _ => 1
    };

    private static int RequiredBreaksAfter(string tag) => tag switch
    {
        "p" or "div" or "section" or "article" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "ul" or "ol" or "table" => 2,
        "li" or "tr" => 1,
        _ => 0
    };
}
