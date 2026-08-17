using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PoMode.Client.Services;

/// <summary>
/// Renders the small Markdown subset the copilot actually produces: paragraphs, line breaks, bold,
/// italic and inline code (spec §5 asks for a two-sentence explanation rendered as Markdown).
///
/// Deliberately not a full Markdown library. The input is language-model output — untrusted text that
/// ends up inside a <c>MarkupString</c> — so this escapes everything first and only then converts a
/// fixed whitelist of inline markers back into tags. Nothing in the source can become an element or an
/// attribute. A wider syntax surface would mean a wider injection surface for no user benefit.
/// </summary>
public static partial class MiniMarkdown
{
    [GeneratedRegex(@"\*\*(?<text>.+?)\*\*", RegexOptions.Singleline)]
    private static partial Regex Bold();

    [GeneratedRegex(@"(?<!\*)\*(?<text>[^*]+?)\*(?!\*)", RegexOptions.Singleline)]
    private static partial Regex Italic();

    [GeneratedRegex(@"`(?<text>[^`]+?)`", RegexOptions.Singleline)]
    private static partial Regex Code();

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "";
        }

        var builder = new StringBuilder();
        var paragraphs = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length == 0)
            {
                continue;
            }
            builder.Append("<p>").Append(RenderInline(paragraph)).Append("</p>");
        }
        return builder.ToString();
    }

    private static string RenderInline(string paragraph)
    {
        // Escape first, always. Every conversion below inserts only literal tags of its own.
        var escaped = WebUtility.HtmlEncode(paragraph);

        // Code spans come first so their contents keep the escaping and are never re-marked as
        // emphasis — otherwise `*x*` inside code would silently become italic.
        escaped = Code().Replace(escaped, match => $"<code>{match.Groups["text"].Value}</code>");
        escaped = Bold().Replace(escaped, match => $"<strong>{match.Groups["text"].Value}</strong>");
        escaped = Italic().Replace(escaped, match => $"<em>{match.Groups["text"].Value}</em>");

        return escaped.Replace("\n", "<br />", StringComparison.Ordinal);
    }
}
