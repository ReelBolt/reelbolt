using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// The tolerant, shared JSON token walker behind the vendor imports whose schema is unverified
/// (currently <c>v0.json</c>; shared with the planned Lovable and Penpot importers). It does not
/// assume a fixed schema: anywhere in the tree, an object under one of the recognized key names
/// (<c>colors|color|palette|typography|fonts|fontFamily|radius|radii|borderRadius|spacing|shadows</c>,
/// case-insensitive) is treated as a token container, and its leaf strings are converted with the
/// same <see cref="TokenNormalizer"/> the strict importers use. A leaf the walker cannot place is
/// skipped with an <see cref="ImportDiagnosticSeverity.Info"/> diagnostic, never an error. Pure and
/// total: it performs no IO and never throws.
/// </summary>
public static class TolerantTokenWalker
{
    /// <summary>The importer identifier recorded on the sources it reads.</summary>
    public const string ImporterId = "tolerant";

    /// <summary>The Info code for one leaf the walker could not place.</summary>
    public const string SkippedDiagnostic = "TOKEN_SKIPPED";

    /// <summary>The Info code emitted when a walked file yielded no token at all.</summary>
    public const string NothingRecognizedDiagnostic = "NO_TOKENS_RECOGNIZED";

    /// <summary>The Warn code emitted when a walked file is not valid JSON.</summary>
    public const string InvalidJsonDiagnostic = "IMPORT_INVALID_JSON";

    /// <summary>The confidence a tolerated (schema-guessed) token carries.</summary>
    public const double Confidence = 0.6;

    private const int MaxPathLength = 160;
    private const int MaxSkipDiagnostics = 100;

    /// <summary>The properties the walker searches the whole tree for, and the group each feeds.</summary>
    private static readonly Dictionary<string, Category> RecognizedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["colors"] = Category.Color,
        ["color"] = Category.Color,
        ["palette"] = Category.Color,
        ["typography"] = Category.Typography,
        ["fonts"] = Category.Typography,
        ["fontFamily"] = Category.Typography,
        ["radius"] = Category.Radius,
        ["radii"] = Category.Radius,
        ["borderRadius"] = Category.Radius,
        ["spacing"] = Category.Spacing,
        ["shadows"] = Category.Shadow,
    };

    /// <summary>Keys that name a font stack rather than one of its metrics.</summary>
    private static readonly HashSet<string> FamilyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "font", "fonts", "fontfamily", "font-family", "family", "typeface", "typefaces",
    };

    /// <summary>Keys inside a typography object that are metrics, never font stacks.</summary>
    private static readonly HashSet<string> MetricKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "fontsize", "font-size", "size", "fontweight", "font-weight", "weight",
        "lineheight", "line-height", "letterspacing", "letter-spacing",
        "fontstyle", "font-style", "texttransform", "text-transform",
    };

    /// <summary>The token category a recognized key feeds.</summary>
    private enum Category
    {
        Color,
        Typography,
        Radius,
        Spacing,
        Shadow,
    }

    /// <summary>
    /// Parses one UTF-8 JSON file and walks it into a fresh document, honouring the shared JSON caps.
    /// Returns null (with a diagnostic) for an empty, oversized or invalid file, or when no leaf
    /// could be placed.
    /// </summary>
    public static JsonObject? WalkFile(string source, ReadOnlyMemory<byte> content, List<ImportDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        source ??= string.Empty;

        if (content.Length == 0)
        {
            diagnostics.Add(Warn(InvalidJsonDiagnostic, source, "file is empty"));
            return null;
        }

        if (content.Length > ImportLimits.JsonMaxBytes)
        {
            diagnostics.Add(Warn(
                InvalidJsonDiagnostic,
                source,
                $"file is {content.Length} bytes, over the {ImportLimits.JsonMaxBytes} byte limit"));
            return null;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(
                content.Span,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions { MaxDepth = ImportLimits.JsonMaxDepth });
        }
        catch (JsonException)
        {
            diagnostics.Add(Warn(InvalidJsonDiagnostic, source, "not valid JSON"));
            return null;
        }

        var design = DesignDocument.New();
        int tokens = WalkInto(design, source, root, diagnostics);

        if (tokens == 0)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Info,
                NothingRecognizedDiagnostic,
                source,
                "no recognized token under any known key"));
            return null;
        }

        return design;
    }

    /// <summary>
    /// Walks an already-parsed root into <paramref name="design"/> and returns the number of tokens
    /// written. Shared with the vendor importers that parse their own JSON.
    /// </summary>
    public static int WalkInto(
        JsonObject design,
        string source,
        JsonNode? root,
        List<ImportDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        var walker = new Walker(design, source ?? string.Empty, diagnostics);
        walker.Search(root);
        return walker.TokenCount;
    }

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);

    /// <summary>One walk over one root: path tracking, conversions and skip diagnostics.</summary>
    private sealed class Walker
    {
        private readonly JsonObject _design;
        private readonly string _source;
        private readonly List<ImportDiagnostic>? _diagnostics;
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);
        private int _skips;

        public Walker(JsonObject design, string source, List<ImportDiagnostic>? diagnostics)
        {
            _design = design;
            _source = source;
            _diagnostics = diagnostics;
        }

        /// <summary>The number of tokens written so far.</summary>
        public int TokenCount { get; private set; }

        /// <summary>
        /// Searches the whole tree for a recognized key. Anything that is not one is traversed in
        /// place, so a token block nested inside a theme object is still found.
        /// </summary>
        public void Search(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (KeyValuePair<string, JsonNode?> property in obj)
                    {
                        if (RecognizedKeys.TryGetValue(property.Key, out Category category))
                            Write(category, property.Value, []);
                        else
                            Search(property.Value);
                    }

                    break;

                case JsonArray array:
                    foreach (JsonNode? item in array)
                        Search(item);
                    break;
            }
        }

        private void Write(Category category, JsonNode? node, List<string> segments)
        {
            if (TryEmit(category, node, segments))
                return;

            switch (node)
            {
                case JsonObject obj:
                    foreach (KeyValuePair<string, JsonNode?> property in obj)
                    {
                        if (property.Value is null)
                            continue;

                        if (category == Category.Typography && MetricKeys.Contains(property.Key))
                        {
                            Skip(WithSegment(segments, property.Key));
                        }
                        else if (category == Category.Typography && FamilyKeys.Contains(property.Key))
                        {
                            Write(category, property.Value, segments);
                        }
                        else
                        {
                            Write(category, property.Value, WithSegment(segments, property.Key));
                        }
                    }

                    break;

                case null:
                    break;

                default:
                    Skip(segments);
                    break;
            }
        }

        private bool TryEmit(Category category, JsonNode? node, List<string> segments)
        {
            if (node is null)
                return false;

            string type;
            JsonNode? normalized;

            switch (category)
            {
                case Category.Color:
                    type = "color";
                    normalized = TokenNormalizer.NormalizeColor(node, out _);
                    break;

                case Category.Typography:
                    type = "fontFamily";
                    normalized = TokenNormalizer.NormalizeFontFamily(node, out _);
                    break;

                case Category.Radius:
                case Category.Spacing:
                    type = "dimension";
                    normalized = TokenNormalizer.NormalizeDimension(node, out _);
                    break;

                case Category.Shadow:
                    type = "shadow";
                    normalized = TokenNormalizer.NormalizeShadow(node, out _);
                    break;

                default:
                    return false;
            }

            if (normalized is null)
                return false;

            Emit(type, category, normalized, segments);
            return true;
        }

        private void Emit(string type, Category category, JsonNode value, List<string> segments)
        {
            string group = category switch
            {
                Category.Color => "color",
                Category.Typography => "typography",
                Category.Radius => "radius",
                Category.Spacing => "spacing",
                Category.Shadow => "shadow",
                _ => "color",
            };

            var builder = new StringBuilder(group);
            if (category == Category.Typography)
                builder.Append(".family");

            if (segments.Count == 0)
            {
                builder.Append(".default");
            }
            else
            {
                foreach (string segment in segments)
                    builder.Append('.').Append(DesignKeys.Slug(segment));
            }

            string path = builder.ToString();
            if (path.Length > MaxPathLength)
            {
                Skip(segments);
                return;
            }

            path = DesignKeys.Unique(_used, path);
            DesignDocument.SetToken(
                _design,
                path,
                type,
                value,
                new TokenProvenance("imported", ImporterId, null, _source),
                Confidence);
            TokenCount++;
        }

        private void Skip(List<string> segments)
        {
            if (_skips++ >= MaxSkipDiagnostics)
                return;

            string suffix = segments.Count == 0 ? string.Empty : ": " + string.Join('.', segments);
            _diagnostics?.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Info,
                SkippedDiagnostic,
                _source,
                "leaf did not match a known token type" + suffix));
        }

        private static List<string> WithSegment(List<string> segments, string segment)
        {
            var next = new List<string>(segments) { segment };
            return next;
        }
    }
}
