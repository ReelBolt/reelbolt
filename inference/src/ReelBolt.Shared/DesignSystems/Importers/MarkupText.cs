using System.Globalization;
using System.Net;
using System.Text;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reduces inert HTML/XHTML markup to plain text. This is a bounded state machine, never a regex:
/// outside a tag characters are copied, a <c>script</c>/<c>style</c>/<c>head</c> body is skipped to
/// its matching close tag (or EOF), any other tag is skipped to <c>&gt;</c>, entities are decoded,
/// control/bidi characters are removed, whitespace is collapsed and the result is capped.
///
/// <para>
/// The method never executes or interprets markup and never throws; an unclosed container is skipped
/// to EOF, so a hostile document can never leak the body it failed to close.
/// </para>
/// </summary>
public static class MarkupText
{
    /// <summary>The most input characters ever scanned; the rest is ignored.</summary>
    public const int MaxInputChars = 256 * 1024;

    /// <summary>The most output characters returned.</summary>
    public const int MaxOutputChars = 32 * 1024;

    /// <summary>
    /// Strips script/style/head bodies and tags, decodes entities, removes control/bidi characters,
    /// collapses whitespace and caps the result at <paramref name="maxChars"/>.
    /// </summary>
    public static string ToText(string markup, int maxChars = MaxOutputChars)
    {
        if (string.IsNullOrEmpty(markup) || maxChars <= 0)
            return string.Empty;

        int length = Math.Min(markup.Length, MaxInputChars);
        var builder = new StringBuilder(Math.Min(length, MaxOutputChars));

        int index = 0;
        while (index < length)
        {
            if (markup[index] != '<')
            {
                builder.Append(markup[index]);
                index++;
                continue;
            }

            // A comment body is not prose; skip it whole, otherwise the first '>' inside it would
            // leave the rest of the comment (and its text) behind.
            if (index + 3 < length
                && markup[index + 1] == '!'
                && markup[index + 2] == '-'
                && markup[index + 3] == '-')
            {
                int commentEnd = markup.IndexOf("-->", index + 4, StringComparison.Ordinal);
                index = commentEnd < 0 ? length : commentEnd + 3;
                continue;
            }

            if (index + 1 >= length)
            {
                builder.Append('<');
                index++;
                continue;
            }

            char next = markup[index + 1];
            if (next is '!' or '?')
            {
                int declarationEnd = markup.IndexOf('>', index + 1);
                index = declarationEnd < 0 ? length : declarationEnd + 1;
                continue;
            }

            bool closing = next == '/';
            int nameStart = closing ? index + 2 : index + 1;
            int nameEnd = nameStart;
            while (nameEnd < length && IsAsciiLetter(markup[nameEnd]))
                nameEnd++;

            if (nameEnd == nameStart)
            {
                // A '<' that starts no tag name is text (e.g. "a < b").
                builder.Append('<');
                index++;
                continue;
            }

            bool boundary = nameEnd >= length
                || markup[nameEnd] is '>' or '/'
                || char.IsWhiteSpace(markup[nameEnd]);

            if (!closing && boundary && IsSkippedContainer(markup, nameStart, nameEnd))
            {
                index = SkipContainer(markup, nameStart, nameEnd, length);
                continue;
            }

            int tagEnd = markup.IndexOf('>', nameEnd);
            index = tagEnd < 0 ? length : tagEnd + 1;
        }

        string decoded = WebUtility.HtmlDecode(builder.ToString());
        string cleaned = RemoveControlAndFormat(decoded);
        string collapsed = CollapseWhitespace(cleaned);
        return Cap(collapsed, maxChars);
    }

    private static bool IsSkippedContainer(string markup, int nameStart, int nameEnd)
    {
        ReadOnlySpan<char> name = markup.AsSpan(nameStart, nameEnd - nameStart);
        return name.Equals("script", StringComparison.OrdinalIgnoreCase)
            || name.Equals("style", StringComparison.OrdinalIgnoreCase)
            || name.Equals("head", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Skips a container's body through its matching close tag. An unclosed container is skipped to
    /// EOF, so the body can never leak.
    /// </summary>
    private static int SkipContainer(string markup, int nameStart, int nameEnd, int length)
    {
        string closeTag = "</" + markup[nameStart..nameEnd];
        int close = markup.IndexOf(closeTag, nameEnd, StringComparison.OrdinalIgnoreCase);
        if (close < 0)
            return length;

        int tagEnd = markup.IndexOf('>', close);
        return tagEnd < 0 ? length : tagEnd + 1;
    }

    private static string RemoveControlAndFormat(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))
                continue;

            if (char.GetUnicodeCategory(c) == UnicodeCategory.Format)
                continue;

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool pendingSpace = false;

        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (builder.Length > 0)
                    pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Cap(string text, int max)
    {
        if (text.Length <= max)
            return text;

        int cut = max;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            cut--;

        return text[..cut];
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
}
