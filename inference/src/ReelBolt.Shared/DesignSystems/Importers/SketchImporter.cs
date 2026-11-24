using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads the shared colour swatches and the layer text styles out of a Sketch archive's
/// <c>document.json</c>. Pages, layer trees and <c>meta.json</c> are never read: only the file
/// named <c>document.json</c> is offered, and only its <c>sharedSwatches</c>, legacy
/// <c>assets.colorAssets</c> and <c>layerTextStyles</c> libraries are inspected.
///
/// <para>
/// The real <c>document.json</c> shape (see <c>handover/BLOCKED-sketch.md</c>) is the documented
/// contract only, so this reader is deliberately tolerant: <c>sharedSwatches</c> and
/// <c>layerTextStyles</c> may be an array or an object carrying an <c>objects</c> array, a swatch
/// colour may sit directly under <c>color</c> or under a modern <c>value.fill(s)[].color</c>, and
/// keys are matched case-insensitively with the separator-insensitive folding the other importers
/// use. Colours become <c>color.sketch.{slug(name)}</c> (bytes <c>round(v*255)</c> from the 0..1
/// channels) and text styles <c>typography.sketch.{slug(name)}</c> (the PostScript font name's
/// family is guessed by stripping its <c>-Bold</c>-style suffix and sanitized).
/// </para>
///
/// <para>
/// Pure and total: the input is inert data, nothing is evaluated, no regular expression runs, and
/// every failure becomes an <see cref="ImportDiagnostic"/> rather than an exception. Only the path
/// is inspected to claim a file, so an oversized <c>document.json</c> is refused by
/// <see cref="ImportLimits.JsonMaxBytes"/> rather than parsed (defence in depth behind the import
/// worker's own <c>ZipInlineFileMaxBytes</c> cap).
/// </para>
/// </summary>
public sealed class SketchImporter : IDesignImporter
{
    /// <summary>The importer identifier recorded on the sources it reads and in token provenance.</summary>
    public const string ImporterId = "sketch";

    /// <summary>The Info code for a document that carries no colour/typography library object.</summary>
    public const string NoLibraryDiagnostic = "SKETCH_NO_LIBRARY";

    /// <summary>The Warn code for a library entry that cannot be placed (no name, unusable value).</summary>
    public const string EntrySkippedDiagnostic = "SKETCH_ENTRY_SKIPPED";

    /// <summary>The Warn code for a candidate file that is not valid JSON within the caps.</summary>
    public const string InvalidJsonDiagnostic = "IMPORT_INVALID_JSON";

    /// <summary>The confidence a tolerated (schema-guessed) Sketch token carries.</summary>
    public const double Confidence = 0.6;

    /// <inheritdoc />
    public string Id => ImporterId;

    private const int MaxPathLength = 160;
    private const int MaxLocLength = 200;
    private const int MaxSkipDiagnostics = 100;

    /// <summary>
    /// True when <paramref name="path"/> names the one file a Sketch archive's tokens live in. Only
    /// the file name is inspected, so a <c>document.json</c> whose swatches sit past the head window
    /// is still claimed.
    /// </summary>
    public static bool LooksLikeDocument(string? path) =>
        !string.IsNullOrEmpty(path)
        && System.IO.Path.GetFileName(path).Equals("document.json", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head) => LooksLikeDocument(path);

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files)
    {
        var root = DesignDocument.New();
        var diagnostics = new List<ImportDiagnostic>();
        var sources = new List<ImportSourceRef>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        int tokenCount = 0;

        foreach (ImportFile? file in files ?? [])
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
                continue;

            // Pages and meta.json are never read, whatever the caller offered.
            if (!LooksLikeDocument(file.Path))
                continue;

            JsonNode? parsed = Parse(file, diagnostics);
            if (parsed is null)
                continue;

            var scan = new Scan(root, used, diagnostics, file.Path);
            scan.Read(parsed);
            tokenCount += scan.TokenCount;

            if (scan.TokenCount == 0)
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Info,
                    NoLibraryDiagnostic,
                    file.Path,
                    "no Sketch colour or text-style library object"));
                continue;
            }

            sources.Add(new ImportSourceRef(
                file.Path,
                ImporterId,
                Sha256Hex(file.Content.Span),
                file.ProjectFileId));
        }

        if (tokenCount == 0)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Warn,
                NoLibraryDiagnostic,
                string.Empty,
                "no Sketch colour or text-style library object was recognized"));
        }

        return new DesignImportResult(tokenCount > 0 ? root : null, null, sources, diagnostics);
    }

    private static JsonNode? Parse(ImportFile file, List<ImportDiagnostic> diagnostics)
    {
        if (file.Content.Length == 0)
        {
            diagnostics.Add(Warn(InvalidJsonDiagnostic, file.Path, "file is empty"));
            return null;
        }

        if (file.Content.Length > ImportLimits.JsonMaxBytes)
        {
            diagnostics.Add(Warn(
                InvalidJsonDiagnostic,
                file.Path,
                $"file is {file.Content.Length} bytes, over the {ImportLimits.JsonMaxBytes} byte limit"));
            return null;
        }

        try
        {
            return JsonNode.Parse(
                file.Content.Span,
                nodeOptions: null,
                documentOptions: new JsonDocumentOptions { MaxDepth = ImportLimits.JsonMaxDepth });
        }
        catch (JsonException)
        {
            diagnostics.Add(Warn(InvalidJsonDiagnostic, file.Path, "not valid JSON"));
            return null;
        }
    }

    /// <summary>
    /// Strips the PostScript style segment from a font name: <c>Inter-Bold</c> and
    /// <c>HelveticaNeue-CondensedBold</c> both guess <c>Inter</c> / <c>HelveticaNeue</c>. A name
    /// with no hyphen (e.g. <c>ArialMT</c>) is used as it stands. Sanitized with
    /// <see cref="DesignText.Sanitize"/>; null when nothing usable is left.
    /// </summary>
    public static string? GuessFamily(string? postScriptName)
    {
        string? clean = DesignText.Sanitize(postScriptName, 80);
        if (clean is null)
            return null;

        int hyphen = clean.IndexOf('-');
        string family = (hyphen > 0 ? clean[..hyphen] : clean).Trim();
        return family.Length == 0 ? null : DesignText.Sanitize(family, 80);
    }

    /// <summary>
    /// One walk over one parsed <c>document.json</c>: writes every swatch under
    /// <c>color.sketch</c> and every text style under <c>typography.sketch</c>.
    /// </summary>
    private sealed class Scan
    {
        private readonly JsonObject _root;
        private readonly HashSet<string> _used;
        private readonly List<ImportDiagnostic> _diagnostics;
        private readonly string _path;
        private int _skips;

        public Scan(JsonObject root, HashSet<string> used, List<ImportDiagnostic> diagnostics, string path)
        {
            _root = root;
            _used = used;
            _diagnostics = diagnostics;
            _path = path;
        }

        public int TokenCount { get; private set; }

        public void Read(JsonNode? document)
        {
            if (document is not JsonObject root)
                return;

            foreach (JsonNode? entry in Entries(root, "sharedSwatches"))
                WriteSwatch(entry);

            if (TryGet(root, "assets", out JsonNode? assets) && assets is JsonObject assetsObject)
            {
                foreach (JsonNode? entry in Entries(assetsObject, "colorAssets"))
                    WriteSwatch(entry);
            }

            foreach (JsonNode? entry in Entries(root, "layerTextStyles"))
                WriteTextStyle(entry);
        }

        private void WriteSwatch(JsonNode? entry)
        {
            if (entry is not JsonObject obj)
                return;

            string? name = StringOf(obj, "name") ?? StringOf(obj, "id");
            if (string.IsNullOrWhiteSpace(name))
            {
                Skip(null, "swatch has no name");
                return;
            }

            JsonNode? normalized = NormalizeColor(ColorObject(obj));
            if (normalized is null)
            {
                Skip(name, "swatch carries no usable sRGB colour");
                return;
            }

            string dotPath = "color.sketch." + DesignKeys.Slug(name);
            if (dotPath.Length > MaxPathLength)
            {
                Skip(name, "token path is too long");
                return;
            }

            Emit("color", DesignKeys.Unique(_used, dotPath), normalized);
        }

        private void WriteTextStyle(JsonNode? entry)
        {
            if (entry is not JsonObject obj)
                return;

            string? name = StringOf(obj, "name") ?? StringOf(obj, "id");
            if (string.IsNullOrWhiteSpace(name))
            {
                Skip(null, "text style has no name");
                return;
            }

            JsonObject? attributes = FontAttributes(obj);
            if (attributes is null)
            {
                Skip(name, "text style carries no font attribute");
                return;
            }

            string? family = GuessFamily(StringOf(attributes, "name"));
            if (family is null)
            {
                Skip(name, "text style carries no usable font name");
                return;
            }

            var members = new JsonObject { ["fontFamily"] = family };
            if (TryGet(attributes, "size", out JsonNode? size) && size is not null)
                members["fontSize"] = size.DeepClone();

            JsonNode? normalized = TokenNormalizer.NormalizeTypography(members, out _);
            if (normalized is null)
            {
                Skip(name, "text style carries no recognizable sub-values");
                return;
            }

            string dotPath = "typography.sketch." + DesignKeys.Slug(name);
            if (dotPath.Length > MaxPathLength)
            {
                Skip(name, "token path is too long");
                return;
            }

            Emit("typography", DesignKeys.Unique(_used, dotPath), normalized);
        }

        private static JsonNode? NormalizeColor(JsonObject? color)
        {
            if (color is null)
                return null;

            if (!TryChannel(color, "red", out double red)
                || !TryChannel(color, "green", out double green)
                || !TryChannel(color, "blue", out double blue))
            {
                return null;
            }

            var shape = new JsonObject
            {
                ["colorSpace"] = "srgb",
                ["components"] = new JsonArray(
                    JsonValue.Create(red),
                    JsonValue.Create(green),
                    JsonValue.Create(blue)),
            };

            return TokenNormalizer.NormalizeColor(shape, out _);
        }

        /// <summary>
        /// The colour object of a swatch: the documented <c>color</c>, a <c>value</c> that is itself the
        /// colour (the public Sketch file format), or the modern <c>value.fill[].color</c> /
        /// <c>value.fills[].color</c> shape.
        /// </summary>
        private static JsonObject? ColorObject(JsonObject entry)
        {
            if (TryGet(entry, "color", out JsonNode? direct) && direct is JsonObject directColor)
                return directColor;

            if (TryGet(entry, "value", out JsonNode? value) && value is JsonObject style)
            {
                // The public Sketch file format: Swatch.value IS the Color ({ _class: "color", red, green, blue }).
                if (TryGet(style, "red", out _) && TryGet(style, "green", out _) && TryGet(style, "blue", out _))
                    return style;

                if (FirstFill(style) is JsonObject fill
                    && TryGet(fill, "color", out JsonNode? fillColor)
                    && fillColor is JsonObject fillColorObject)
                {
                    return fillColorObject;
                }

                if (TryGet(style, "color", out JsonNode? styleColor) && styleColor is JsonObject styleColorObject)
                    return styleColorObject;
            }

            return null;
        }

        private static JsonObject? FirstFill(JsonObject style)
        {
            if (TryGet(style, "fill", out JsonNode? fill) && fill is JsonObject fillObject)
                return fillObject;

            if (TryGet(style, "fills", out JsonNode? fills) && fills is JsonArray array)
            {
                foreach (JsonNode? item in array)
                {
                    if (item is JsonObject entry)
                        return entry;
                }
            }

            return null;
        }

        private static bool TryChannel(JsonObject color, string channel, out double value)
        {
            value = 0;
            if (TryGet(color, channel, out JsonNode? node)
                || TryGet(color, channel[..1], out node))
            {
                return TryNumber(node, out value);
            }

            return false;
        }

        private static bool TryNumber(JsonNode? node, out double value)
        {
            value = 0;
            if (node is JsonValue jsonValue)
            {
                if (jsonValue.GetValueKind() == JsonValueKind.Number)
                {
                    value = jsonValue.GetValue<double>();
                    return double.IsFinite(value);
                }

                if (jsonValue.GetValueKind() == JsonValueKind.String
                    && double.TryParse(jsonValue.GetValue<string>().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return double.IsFinite(value);
                }
            }

            return false;
        }

        private static JsonObject? FontAttributes(JsonObject entry)
        {
            if (TryGet(entry, "value", out JsonNode? value) && value is JsonObject style
                && TryGet(style, "textStyle", out JsonNode? textStyle) && textStyle is JsonObject textStyleObject
                && TryGet(textStyleObject, "encodedAttributes", out JsonNode? encoded) && encoded is JsonObject encodedObject
                && TryGet(encodedObject, "MSAttributedStringFontAttribute", out JsonNode? font) && font is JsonObject fontObject)
            {
                if (TryGet(fontObject, "attributes", out JsonNode? attributes) && attributes is JsonObject attributesObject)
                    return attributesObject;

                return fontObject;
            }

            return null;
        }

        private static IEnumerable<JsonNode?> Entries(JsonObject owner, string key)
        {
            if (!TryGet(owner, key, out JsonNode? node) || node is null)
                yield break;

            if (node is JsonArray array)
            {
                foreach (JsonNode? item in array)
                    yield return item;

                yield break;
            }

            if (node is JsonObject obj
                && TryGet(obj, "objects", out JsonNode? objects)
                && objects is JsonArray entries)
            {
                foreach (JsonNode? item in entries)
                    yield return item;
            }
        }

        private void Emit(string type, string dotPath, JsonNode value)
        {
            DesignDocument.SetToken(
                _root,
                dotPath,
                type,
                value,
                new TokenProvenance("imported", ImporterId, null, Loc(_path)),
                Confidence);
            TokenCount++;
        }

        private void Skip(string? where, string message)
        {
            if (_skips++ >= MaxSkipDiagnostics)
                return;

            _diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Warn,
                EntrySkippedDiagnostic,
                _path,
                (string.IsNullOrEmpty(where) ? string.Empty : where + ": ") + message));
        }
    }

    private static bool TryGet(JsonObject obj, string key, out JsonNode? value)
    {
        string wanted = Normalize(key);
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            if (string.Equals(Normalize(property.Key), wanted, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string? StringOf(JsonObject obj, string key) =>
        TryGet(obj, key, out JsonNode? value)
            && value is JsonValue jsonValue
            && jsonValue.GetValueKind() == JsonValueKind.String
                ? jsonValue.GetValue<string>()
                : null;

    /// <summary>Lowercases and strips separators so <c>sharedSwatches</c>, <c>shared-swatches</c>
    /// and <c>shared_swatches</c> compare equal.</summary>
    private static string Normalize(string key) => key.Replace("-", string.Empty).Replace("_", string.Empty);

    private static string Loc(string path) => path.Length > MaxLocLength ? path[..MaxLocLength] : path;

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);
}
