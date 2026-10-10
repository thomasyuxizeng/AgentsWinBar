using System.Net;
using System.Text.RegularExpressions;

namespace CodexBarWin.Helpers;

/// <summary>
/// Pure static sanitizer for untrusted provider-supplied incident and status update text.
/// Strips markdown and HTML formatting, decodes entities, collapses whitespace, and caps length on a word boundary.
/// </summary>
public static partial class IncidentTextSanitizer
{
    public const int DefaultMaxLength = 200;
    private const string Ellipsis = "...";

    [GeneratedRegex(@"<br\s*/?>|<p\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"(?m)^\s*#{0,6}\s*\*{0,2}(?:Summary|Description|Symptoms|Workaround|Title|Root Cause|Remediation and Prevention|Incident Report)\*{0,2}:?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionHeaderLineRegex();

    [GeneratedRegex(@"(?m)^\s*\*{0,2}(?:Summary|Description|Symptoms|Workaround|Title)\*{0,2}:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex InlineSectionPrefixRegex();

    [GeneratedRegex(@"```[\w-]*\r?\n?([\s\S]*?)```")]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]+\)")]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex MarkdownLinkRegex();

    [GeneratedRegex(@"`([^`]+)`")]
    private static partial Regex InlineCodeRegex();

    [GeneratedRegex(@"(?m)^\s*#{1,6}\s+")]
    private static partial Regex MarkdownHeaderRegex();

    [GeneratedRegex(@"(?m)^\s*([-*+]|\d+\.)\s+")]
    private static partial Regex MarkdownListRegex();

    [GeneratedRegex(@"(?m)^\s*>\s+")]
    private static partial Regex MarkdownBlockquoteRegex();

    [GeneratedRegex(@"(\*{1,3}|_{1,3})(.*?)\1")]
    private static partial Regex MarkdownEmphasisRegex();

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F]")]
    private static partial Regex ControlCharRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Sanitizes untrusted incident text by stripping markdown, HTML, control characters, and entities,
    /// collapsing whitespace, and capping the result to <paramref name="maxLength"/> characters with an ellipsis on a word boundary.
    /// Returns an empty string for null, empty, or whitespace-only inputs.
    /// </summary>
    public static string Sanitize(string? text, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (maxLength <= 0)
        {
            return string.Empty;
        }

        // 1. Strip control characters
        var cleaned = ControlCharRegex().Replace(text, " ");

        // 2. Replace HTML breaks/paragraphs with spaces, then strip remaining HTML tags
        cleaned = HtmlBreakRegex().Replace(cleaned, " ");
        cleaned = HtmlTagRegex().Replace(cleaned, " ");

        // 2. Decode HTML entities (&amp;, &#39;, &quot;, etc.)
        cleaned = WebUtility.HtmlDecode(cleaned);

        // 3. Strip fenced code blocks (extract content)
        cleaned = FencedCodeRegex().Replace(cleaned, "$1");

        // 4. Strip section headers (e.g. **Summary:**, **Description:**)
        cleaned = SectionHeaderLineRegex().Replace(cleaned, "");
        cleaned = InlineSectionPrefixRegex().Replace(cleaned, "");

        // 5. Strip Markdown headers, lists, and blockquotes
        cleaned = MarkdownHeaderRegex().Replace(cleaned, "");
        cleaned = MarkdownListRegex().Replace(cleaned, "");
        cleaned = MarkdownBlockquoteRegex().Replace(cleaned, "");

        // 6. Strip Markdown images and links (extract label/alt)
        cleaned = MarkdownImageRegex().Replace(cleaned, "$1");
        cleaned = MarkdownLinkRegex().Replace(cleaned, "$1");

        // 7. Strip inline code
        cleaned = InlineCodeRegex().Replace(cleaned, "$1");

        // 8. Strip emphasis (bold / italics) - run passes to catch nested like ***bold italic***
        cleaned = MarkdownEmphasisRegex().Replace(cleaned, "$2");
        cleaned = MarkdownEmphasisRegex().Replace(cleaned, "$2");

        // 9. Collapse consecutive whitespace and newlines, then trim
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim();

        if (cleaned.Length <= maxLength)
        {
            return cleaned;
        }

        // 10. Truncate on word boundary
        if (maxLength <= Ellipsis.Length)
        {
            return Ellipsis[..maxLength];
        }

        var target = maxLength - Ellipsis.Length;

        // Search backwards from target for whitespace
        var lastSpace = -1;
        for (var i = target; i >= 0; i--)
        {
            if (char.IsWhiteSpace(cleaned[i]))
            {
                lastSpace = i;
                break;
            }
        }

        string sub;
        if (lastSpace > 0)
        {
            sub = cleaned[..lastSpace].TrimEnd(' ', '\t', ',', '.', ';', ':', '!', '?', '-', '/', '\\', '"', '\'');
        }
        else
        {
            var cut = target;
            if (cut > 0 && char.IsHighSurrogate(cleaned[cut - 1]))
            {
                cut--;
            }
            sub = cleaned[..cut].TrimEnd();
        }

        return sub + Ellipsis;
    }
}
