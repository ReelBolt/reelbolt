using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Turns an export's preview cards into structured, inert data: the manifest's card entries joined
/// with their markup files. A card's markup is reduced to plain text by <see cref="MarkupText"/>,
/// a script body never reaches the text, and only the basenames of relative images are kept. No
/// markup is executed, no URL is fetched, and every input is bounded.
/// </summary>
public static class PreviewCardReader
{
    /// <summary>The most preview cards one product map keeps.</summary>
    public const int MaxCards = 100;

    /// <summary>The most characters of a card's plain text.</summary>
    public const int MaxTextChars = 500;

    /// <summary>The most image basenames one card keeps.</summary>
    public const int MaxAssetsPerCard = 8;

    /// <summary>Info: how many preview cards this read produced.</summary>
    public const string CardsDiagnostic = "PREVIEW_CARDS";

    /// <summary>Info: a cap was reached, so some input was not carried.</summary>
    public const string CapDiagnostic = "PREVIEW_CARD_CAP";

    private const int MaxNameChars = 80;
    private const int MaxGroupChars = 80;
    private const int MaxSubtitleChars = 160;
    private const int MaxViewportChars = 20;
    private const int MaxPathChars = 200;
    private const int MaxAssetNameChars = 100;

    // Enough candidates to deduplicate before the per-card cap applies, without letting a hostile
    // document with thousands of <img> tags grow the list without bound.
    private const int MaxAssetCandidates = MaxAssetsPerCard * 8;

    private static readonly char[] PathSeparators = ['/', '\\'];
    private static readonly char[] QueryFragmentMarks = ['?', '#'];

    /// <summary>
    /// Reads <paramref name="manifestCards"/> in manifest order, then every markup file the manifest
    /// did not name, ordered by normalised path. Always returns at most <see cref="MaxCards"/> cards.
    /// </summary>
    public static IReadOnlyList<ProductMapPreviewCard> Read(
        IReadOnlyList<ImportFile> markupFiles,
        JsonArray? manifestCards,
        List<ImportDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var byPath = new Dictionary<string, ImportFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ImportFile? file in markupFiles ?? [])
        {
            if (file is null || string.IsNullOrEmpty(file.Path) || !IsMarkupPath(file.Path))
                continue;

            byPath[Normalize(file.Path)] = file;
        }

        var cards = new List<ProductMapPreviewCard>(Math.Min(MaxCards, byPath.Count + 16));
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool cardCapped = false;
        bool assetCapped = false;

        foreach (JsonObject entry in ManifestEntries(manifestCards))
        {
            if (cards.Count >= MaxCards)
            {
                cardCapped = true;
                break;
            }

            string manifestPath = Normalize(StringValue(entry, "path") ?? string.Empty);
            ImportFile? file = manifestPath.Length > 0 && byPath.TryGetValue(manifestPath, out ImportFile? match)
                ? match
                : null;

            if (file is not null)
                usedPaths.Add(manifestPath);

            cards.Add(BuildCard(file, manifestPath, entry, ref assetCapped));
        }

        foreach (KeyValuePair<string, ImportFile> pair in byPath
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (usedPaths.Contains(pair.Key))
                continue;

            if (cards.Count >= MaxCards)
            {
                cardCapped = true;
                break;
            }

            cards.Add(BuildCard(pair.Value, pair.Key, null, ref assetCapped));
        }

        if (cardCapped)
        {
            diagnostics.Add(Info(
                CapDiagnostic, string.Empty,
                $"more than {MaxCards} preview cards; the rest were not read"));
        }

        if (assetCapped)
        {
            diagnostics.Add(Info(
                CapDiagnostic, string.Empty,
                $"preview card assets are capped at {MaxAssetsPerCard} per card"));
        }

        diagnostics.Add(Info(CardsDiagnostic, string.Empty, $"{cards.Count} preview card(s) read"));
        return cards;
    }

    private static ProductMapPreviewCard BuildCard(
        ImportFile? file,
        string fallbackPath,
        JsonObject? manifest,
        ref bool assetCapped)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? text = null;
        bool runtimeRendered = false;
        var assets = new List<string>();

        if (file is not null)
        {
            string markup = Encoding.UTF8.GetString(file.Content.Span);
            header = ReadHeader(markup);

            (runtimeRendered, List<string> candidates, bool overflow) = ScanMarkup(markup);
            assetCapped |= overflow;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string candidate in candidates)
            {
                if (!seen.Add(candidate))
                    continue;
                if (assets.Count >= MaxAssetsPerCard)
                {
                    assetCapped = true;
                    break;
                }

                assets.Add(candidate);
            }

            string plain = MarkupText.ToText(markup, MaxTextChars);
            text = plain.Length == 0 ? null : plain;
        }

        string? HeaderOrManifest(string key)
        {
            if (header.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value;
            return manifest is null ? null : StringValue(manifest, key);
        }

        string? rawName = HeaderOrManifest("name")
            ?? (file is null ? null : Stem(file.Path));
        string name = DesignText.Sanitize(rawName, MaxNameChars) ?? "Preview card";
        string? group = DesignText.Sanitize(HeaderOrManifest("group"), MaxGroupChars);
        string? subtitle = DesignText.Sanitize(HeaderOrManifest("subtitle"), MaxSubtitleChars);
        string? viewport = DesignText.Sanitize(HeaderOrManifest("viewport"), MaxViewportChars);

        string rawPath = HeaderOrManifest("path") is { } headerPath
            ? Normalize(headerPath)
            : fallbackPath;
        string path = DesignText.Sanitize(rawPath, MaxPathChars) ?? rawPath;

        return new ProductMapPreviewCard(
            name, group, subtitle, viewport, path, text, assets, runtimeRendered);
    }

    /// <summary>The first HTML comment that carries an <c>@dsCard</c> header, parsed into attributes.</summary>
    private static Dictionary<string, string> ReadHeader(string markup)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int length = Math.Min(markup.Length, MarkupText.MaxInputChars);
        int index = 0;

        while (index < length)
        {
            int start = markup.IndexOf("<!--", index, length - index, StringComparison.Ordinal);
            if (start < 0)
                break;

            int close = markup.IndexOf("-->", start + 4, length - (start + 4), StringComparison.Ordinal);
            string body = close < 0
                ? markup[(start + 4)..length]
                : markup[(start + 4)..close];

            int marker = body.IndexOf("@dsCard", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                ParseAttributes(body[(marker + "@dsCard".Length)..], attributes);
                return attributes;
            }

            index = close < 0 ? length : close + 3;
        }

        return attributes;
    }

    /// <summary>
    /// Walks the markup's tags the way <see cref="MarkupText"/> does: comments and the bodies of
    /// <c>script</c>/<c>style</c>/<c>head</c> are skipped, a real <c>script</c> tag marks the card as
    /// runtime-rendered, and an <c>img</c> contributes its relative basename.
    /// </summary>
    private static (bool RuntimeRendered, List<string> Assets, bool Overflow) ScanMarkup(string markup)
    {
        int length = Math.Min(markup.Length, MarkupText.MaxInputChars);
        var assets = new List<string>();
        bool runtimeRendered = false;
        int validAssets = 0;
        int index = 0;

        while (index < length)
        {
            if (markup[index] != '<')
            {
                index++;
                continue;
            }

            if (index + 3 < length
                && markup[index + 1] == '!'
                && markup[index + 2] == '-'
                && markup[index + 3] == '-')
            {
                int commentEnd = markup.IndexOf("-->", index + 4, length - (index + 4), StringComparison.Ordinal);
                index = commentEnd < 0 ? length : commentEnd + 3;
                continue;
            }

            if (index + 1 >= length)
                break;

            char next = markup[index + 1];
            if (next is '!' or '?')
            {
                int declarationEnd = markup.IndexOf('>', index + 1);
                index = declarationEnd < 0 || declarationEnd >= length ? length : declarationEnd + 1;
                continue;
            }

            bool closing = next == '/';
            int nameStart = closing ? index + 2 : index + 1;
            int nameEnd = nameStart;
            while (nameEnd < length && IsAsciiLetter(markup[nameEnd]))
                nameEnd++;

            if (nameEnd == nameStart)
            {
                index++;
                continue;
            }

            bool boundary = nameEnd >= length
                || markup[nameEnd] is '>' or '/'
                || char.IsWhiteSpace(markup[nameEnd]);

            if (!boundary)
            {
                index = nameEnd;
                continue;
            }

            ReadOnlySpan<char> name = markup.AsSpan(nameStart, nameEnd - nameStart);
            int tagEnd = markup.IndexOf('>', nameEnd);
            if (tagEnd < 0 || tagEnd >= length)
            {
                index = length;
                break;
            }

            if (!closing)
            {
                if (name.Equals("script", StringComparison.OrdinalIgnoreCase))
                {
                    runtimeRendered = true;
                    index = SkipContainer(markup, nameStart, nameEnd, length);
                    continue;
                }

                if (name.Equals("style", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("head", StringComparison.OrdinalIgnoreCase))
                {
                    index = SkipContainer(markup, nameStart, nameEnd, length);
                    continue;
                }

                if (name.Equals("img", StringComparison.OrdinalIgnoreCase)
                    && SafeAssetBasename(ReadAttribute(markup, nameEnd, tagEnd, "src")) is { } asset)
                {
                    validAssets++;
                    if (assets.Count < MaxAssetCandidates)
                        assets.Add(asset);
                }
            }

            index = tagEnd + 1;
        }

        return (runtimeRendered, assets, validAssets > MaxAssetsPerCard);
    }

    private static int SkipContainer(string markup, int nameStart, int nameEnd, int length)
    {
        string closeTag = "</" + markup[nameStart..nameEnd];
        int close = markup.IndexOf(closeTag, nameEnd, length - nameEnd, StringComparison.OrdinalIgnoreCase);
        if (close < 0)
            return length;

        int tagEnd = markup.IndexOf('>', close);
        return tagEnd < 0 || tagEnd >= length ? length : tagEnd + 1;
    }

    private static string? ReadAttribute(string markup, int start, int end, string wanted)
    {
        int index = start;
        while (index < end)
        {
            while (index < end && char.IsWhiteSpace(markup[index]))
                index++;
            if (index >= end)
                break;

            int nameStart = index;
            while (index < end && IsAttributeChar(markup[index]))
                index++;
            if (index == nameStart)
            {
                index++;
                continue;
            }

            string name = markup[nameStart..index];

            while (index < end && char.IsWhiteSpace(markup[index]))
                index++;
            if (index >= end || markup[index] != '=')
                continue;

            index++;
            while (index < end && char.IsWhiteSpace(markup[index]))
                index++;
            if (index >= end)
                break;

            string value;
            if (markup[index] is '"' or '\'')
            {
                char quote = markup[index++];
                int valueStart = index;
                while (index < end && markup[index] != quote)
                    index++;
                value = markup[valueStart..index];
                if (index < end)
                    index++;
            }
            else
            {
                int valueStart = index;
                while (index < end && !char.IsWhiteSpace(markup[index]) && markup[index] != '>')
                    index++;
                value = markup[valueStart..index];
            }

            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    private static void ParseAttributes(string segment, Dictionary<string, string> attributes)
    {
        int index = 0;
        int length = segment.Length;

        while (index < length)
        {
            while (index < length && (char.IsWhiteSpace(segment[index]) || segment[index] == '/'))
                index++;
            if (index >= length)
                break;

            int nameStart = index;
            while (index < length && IsAttributeChar(segment[index]))
                index++;
            if (index == nameStart)
            {
                index++;
                continue;
            }

            string name = segment[nameStart..index];

            while (index < length && char.IsWhiteSpace(segment[index]))
                index++;
            if (index >= length || segment[index] != '=')
                continue;

            index++;
            while (index < length && char.IsWhiteSpace(segment[index]))
                index++;
            if (index >= length)
                break;

            string value;
            if (segment[index] is '"' or '\'')
            {
                char quote = segment[index++];
                int valueStart = index;
                while (index < length && segment[index] != quote)
                    index++;
                value = segment[valueStart..index];
                if (index < length)
                    index++;
            }
            else
            {
                int valueStart = index;
                while (index < length && !char.IsWhiteSpace(segment[index]) && segment[index] != '>')
                    index++;
                value = segment[valueStart..index];
            }

            value = WebUtility.HtmlDecode(value.Trim());
            if (!attributes.ContainsKey(name) || attributes[name].Length == 0)
                attributes[name] = value;
        }
    }

    /// <summary>
    /// The safe basename of a relative image reference, or null. A scheme, a host, a root-absolute
    /// path or any traversal in the basename is refused; only the name is ever returned.
    /// </summary>
    private static string? SafeAssetBasename(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string src = WebUtility.HtmlDecode(raw.Trim());
        if (src.Length == 0)
            return null;

        // Protocol-relative (//host/x), root-absolute (/x) and Windows-rooted (\x) name a host or root.
        if (src.StartsWith("//", StringComparison.Ordinal)
            || src.StartsWith('/')
            || src.StartsWith('\\'))
        {
            return null;
        }

        // A scheme (https:, data:, javascript:, …) before any path separator names a URL, never a file.
        int colon = src.IndexOf(':');
        int separator = src.IndexOfAny(PathSeparators);
        if (colon >= 0 && (separator < 0 || colon < separator))
            return null;

        int cut = src.LastIndexOfAny(PathSeparators);
        string basename = cut < 0 ? src : src[(cut + 1)..];

        int mark = basename.IndexOfAny(QueryFragmentMarks);
        if (mark >= 0)
            basename = basename[..mark];

        basename = basename.Trim();
        if (basename.Length == 0 || basename is "." or "..")
            return null;
        if (basename.Contains("..", StringComparison.Ordinal)
            || basename.Contains(':')
            || basename.Contains('/')
            || basename.Contains('\\'))
        {
            return null;
        }

        string? safe = DesignText.Sanitize(basename, MaxAssetNameChars);
        return safe is { Length: > 0 } ? safe : null;
    }

    private static IEnumerable<JsonObject> ManifestEntries(JsonArray? manifestCards)
    {
        if (manifestCards is null)
            yield break;

        foreach (JsonNode? node in manifestCards)
        {
            if (node is JsonObject entry)
                yield return entry;
        }
    }

    private static string? StringValue(JsonObject source, string key) =>
        source.TryGetPropertyValue(key, out JsonNode? node)
        && node is JsonValue value
        && value.TryGetValue(out string? text)
            ? text
            : null;

    private static bool IsMarkupPath(string path)
    {
        string normalised = Normalize(path);
        return normalised.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || normalised.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
            || normalised.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase);
    }

    private static string Stem(string path)
    {
        string normalised = Normalize(path);
        int slash = normalised.LastIndexOf('/');
        string basename = slash < 0 ? normalised : normalised[(slash + 1)..];
        int dot = basename.LastIndexOf('.');
        return dot > 0 ? basename[..dot] : basename;
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsAttributeChar(char c) =>
        c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or ':';

    private static ImportDiagnostic Info(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Info, code, path, message);

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
