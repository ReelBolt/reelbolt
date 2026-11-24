using System.Text;
using System.Text.Json;
using ReelBolt.Shared.Archives;

namespace ReelBolt.ImportWorker;

/// <summary>
/// Turns one accepted zip entry into a public kind plus an inline text copy, or a drop reason.
/// Classification is magic-byte first; the file extension can only make the decision stricter
/// (drop an otherwise textual file), never talk a binary into looking textual.
/// </summary>
public static class ZipFileClassifier
{
    private const string DroppedHtml = "DROPPED_HTML";
    private const string DroppedScript = "DROPPED_SCRIPT";
    private const string DroppedBinary = "DROPPED_BINARY";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> HtmlExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".html", ".htm", ".xhtml" };

    private static readonly HashSet<string> ScriptExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".mts", ".cts",
            ".php", ".sh", ".bat", ".cmd", ".ps1", ".py", ".rb", ".pl", ".vbs",
        };

    public static (string? Kind, string? DropReason) Classify(string path, ReadOnlySpan<byte> content)
    {
        int headLength = Math.Min(content.Length, 4096);
        MagicKind magic = MagicSniffer.Sniff(content[..headLength]);

        switch (magic)
        {
            case MagicKind.Png:
            case MagicKind.Jpeg:
            case MagicKind.WebP:
                return ("image", null);
            case MagicKind.Woff:
            case MagicKind.Woff2:
            case MagicKind.TrueType:
            case MagicKind.OpenType:
                return ("font", null);
            case MagicKind.Svg:
            case MagicKind.Html:
                // SVG and markup travel only as strict-UTF-8 text; a non-UTF-8 SVG stays a binary drop.
                return IsStrictUtf8(content)
                    ? (magic == MagicKind.Svg ? "svg" : "markup", null)
                    : (null, DroppedBinary);
            case MagicKind.Xml:
                // An HTML/XHTML file that opens with an XML declaration is still markup when its root
                // element is <html>; any other XML (scripts included) stays a dropped HTML/binary entry.
                if (IsHtmlExtension(path) && IsStrictUtf8(content) && IsHtmlRoot(StrictUtf8.GetString(content)))
                    return ("markup", null);
                return (null, DroppedHtml);
            case MagicKind.Text:
                break;
            default:
                return (null, DroppedBinary);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            return (null, DroppedBinary);
        }

        // Decided on the name without trailing blanks/invisible characters, so "a.js<NBSP>" is still a script.
        string trimmedPath = ArchivePathRules.TrimInvisibleEnd(path);
        string extension = Path.GetExtension(trimmedPath);
        if (HtmlExtensions.Contains(extension))
            return ("markup", null);

        // A shebang marks a script whatever the name says, a `.d.ts` included.
        if (HasShebang(text))
            return (null, DroppedScript);

        // A declaration file is type information, not executable code: the Claude Design reader needs the
        // interface names, and the content is only ever read as inert text. Every other .ts/.js/.tsx stays dropped.
        if (trimmedPath.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
            return ("text", null);

        if (ScriptExtensions.Contains(extension))
            return (null, DroppedScript);

        if (IsJson(text))
            return ("json", null);
        if (string.Equals(extension, ".css", StringComparison.OrdinalIgnoreCase))
            return ("css", null);
        return ("text", null);
    }

    /// <summary>
    /// The MIME type of a binary entry the worker accepts, from its magic bytes only (the name never decides):
    /// <c>image/png</c>, <c>image/jpeg</c>, <c>image/webp</c>, <c>font/woff</c>, <c>font/woff2</c>,
    /// <c>font/ttf</c> or <c>font/otf</c>, else null. Used by the API to label the bytes it received, without
    /// decoding them.
    /// </summary>
    public static string? MediaType(string path, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(path);

        int headLength = Math.Min(content.Length, 4096);
        return MagicSniffer.Sniff(content[..headLength]) switch
        {
            MagicKind.Png => "image/png",
            MagicKind.Jpeg => "image/jpeg",
            MagicKind.WebP => "image/webp",
            MagicKind.Woff => "font/woff",
            MagicKind.Woff2 => "font/woff2",
            MagicKind.TrueType => "font/ttf",
            MagicKind.OpenType => "font/otf",
            _ => null,
        };
    }

    /// <summary>True for <c>tailwind.config.{js,cjs,mjs,ts,...}</c>, wherever it sits in the archive.</summary>
    public static bool IsTailwindConfig(string path)
        => Path.GetFileName(path).StartsWith("tailwind.config", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the entry's name carries an HTML extension (after invisible trailing characters).</summary>
    private static bool IsHtmlExtension(string path)
        => HtmlExtensions.Contains(Path.GetExtension(ArchivePathRules.TrimInvisibleEnd(path)));

    /// <summary>
    /// True when the first element of an XML document is an <c>html</c> root: it skips a BOM,
    /// whitespace, the <c>&lt;?xml ...?&gt;</c> declaration, comments and a doctype, then reads the
    /// element name. Pure text work: nothing is parsed as a document and no entity is resolved.
    /// </summary>
    private static bool IsHtmlRoot(string text)
    {
        int index = text.Length > 0 && text[0] == '﻿' ? 1 : 0;

        while (true)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;

            if (index >= text.Length || text[index] != '<')
                return false;

            if (StartsWith(text, index, "<?"))
            {
                int end = text.IndexOf("?>", index + 2, StringComparison.Ordinal);
                if (end < 0)
                    return false;
                index = end + 2;
                continue;
            }

            if (StartsWith(text, index, "<!--"))
            {
                int end = text.IndexOf("-->", index + 4, StringComparison.Ordinal);
                if (end < 0)
                    return false;
                index = end + 3;
                continue;
            }

            if (StartsWithIgnoreCase(text, index, "<!doctype"))
            {
                int end = text.IndexOf('>', index + 9);
                if (end < 0)
                    return false;
                index = end + 1;
                continue;
            }

            if (!StartsWithIgnoreCase(text, index, "<html"))
                return false;

            int after = index + 5;
            return after >= text.Length
                || text[after] is '>' or '/' or ' ' or '\t' or '\r' or '\n';
        }
    }

    private static bool StartsWith(string text, int index, string value) =>
        text.Length - index >= value.Length
        && string.CompareOrdinal(text, index, value, 0, value.Length) == 0;

    private static bool StartsWithIgnoreCase(string text, int index, string value) =>
        text.Length - index >= value.Length
        && string.Compare(text, index, value, 0, value.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static bool HasShebang(string text)
    {
        int start = text.Length > 0 && text[0] == '\uFEFF' ? 1 : 0;
        return text.Length >= start + 2 && text[start] == '#' && text[start + 1] == '!';
    }

    private static bool IsStrictUtf8(ReadOnlySpan<byte> content)
    {
        try
        {
            StrictUtf8.GetCharCount(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsJson(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 32 });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
