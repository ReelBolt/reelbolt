using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads the Penpot library objects out of the JSON documents of a <c>.penpot</c> archive (a zip of
/// JSON, v3), or of the plain DTCG token export beside it.
///
/// <para>
/// The real v3 archive layout is unverified offline (see <c>handover/BLOCKED-penpot.md</c>), so this
/// reader is deliberately tolerant: anywhere in the tree an array under <c>colors</c>/<c>tokens</c>/
/// <c>tokensLib</c> (case-insensitive) is searched for colour-library entries
/// (<c>{ "name", "color", "path" }</c>), and an array under <c>typography</c>/<c>tokens</c>/
/// <c>tokensLib</c> for typography entries (<c>{ "name", "font-family", "font-size", "font-weight",
/// "line-height", "letter-spacing" }</c>). Keys are matched tolerantly, so kebab-case and camelCase
/// (and the underscore/snake variants JSON serializers emit) all read. Colours become
/// <c>color.penpot.{slug(path/name)}</c> and typographies <c>typography.penpot.{slug(path/name)}</c>.
/// </para>
///
/// <para>
/// Pure and total: the input is inert data, nothing is evaluated, no regular expression runs, and
/// every failure becomes an <see cref="ImportDiagnostic"/> rather than an exception. Only the path
/// and the first <see cref="ImportLimits.HeadBytes"/> bytes are inspected to claim a file. Documents
/// that carry no colour or typography library object are left to whichever other importer claims
/// them; the plain Penpot DTCG export is read by the <c>dtcg</c> importer instead.
/// </para>
/// </summary>
public sealed class PenpotImporter : IDesignImporter
{
    /// <summary>The importer identifier recorded on the sources it reads and in token provenance.</summary>
    public const string ImporterId = "penpot";

    /// <summary>The Info code for a document that carries no colour/typography library object.</summary>
    public const string NoLibraryDiagnostic = "PENPOT_NO_LIBRARY";

    /// <summary>The Warn code for a library entry that cannot be placed (no name, unusable value).</summary>
    public const string EntrySkippedDiagnostic = "PENPOT_ENTRY_SKIPPED";

    /// <summary>The Warn code for a candidate file that is not valid JSON within the caps.</summary>
    public const string InvalidJsonDiagnostic = "IMPORT_INVALID_JSON";

    /// <summary>The confidence a tolerated (schema-guessed) Penpot token carries.</summary>
    public const double Confidence = 0.6;

    /// <inheritdoc />
    public string Id => ImporterId;

    private const int MaxPathLength = 160;
    private const int MaxLocLength = 200;
    private const int MaxSkipDiagnostics = 100;
    private const int MaxScanNodes = 200000;

    /// <summary>Keys that name a colour-library array, or a container of one.</summary>
    private static readonly HashSet<string> ColorKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "colors",
        "colour",
        "color",
        "palette",
        "tokens",
        "tokenslib",
    };

    /// <summary>Keys that name a typography-library array, or a container of one.</summary>
    private static readonly HashSet<string> TypographyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "typography",
        "typographies",
        "textstyles",
        "fonts",
        "tokens",
        "tokenslib",
    };

    /// <summary>The entry keys that carry a colour value, most specific first.</summary>
    private static readonly string[] ColorValueKeys = ["color", "colour", "fillColor", "value"];

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head) => LooksLikePenpot(path, head);

    /// <summary>
    /// True when <paramref name="path"/> names a JSON file whose leading bytes carry one of the
    /// Penpot library keys (<c>colors</c>/<c>tokens</c>/<c>tokensLib</c>). Case-insensitive on the
    /// file name. A plain DTCG export is not claimed here: <see cref="DtcgImporter"/> reads it.
    /// </summary>
    public static bool LooksLikePenpot(string path, ReadOnlySpan<byte> head)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        if (head.Length == 0)
            return false;

        int limit = Math.Min(head.Length, ImportLimits.HeadBytes);
        string text = Encoding.UTF8.GetString(head[..limit]);
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        string trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return false;

        return ContainsKey(text, "colors")
            || ContainsKey(text, "tokens")
            || ContainsKey(text, "tokensLib")
            || ContainsKey(text, "typography")
            || ContainsKey(text, "typographies");
    }

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

            if (!file.Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            if (IsDollarFile(file.Path))
                continue;

            JsonNode? parsed = Parse(file, diagnostics);
            if (parsed is null)
                continue;

            var scan = new Scan(root, used, diagnostics, file.Path);
            scan.Search(parsed);
            tokenCount += scan.TokenCount;

            if (scan.TokenCount == 0)
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Info,
                    NoLibraryDiagnostic,
                    file.Path,
                    "no Penpot colour or typography library object"));
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
                "no Penpot colour or typography library object was recognized"));
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

    /// <summary>True for <c>$themes.json</c>/<c>$metadata.json</c>, which Tokens Studio owns.</summary>
    private static bool IsDollarFile(string path) =>
        System.IO.Path.GetFileName(path).StartsWith('$');

    /// <summary>
    /// One walk over one parsed document: finds the colour/typography arrays at any depth and writes
    /// each entry under the <c>penpot</c> namespace.
    /// </summary>
    private sealed class Scan
    {
        private readonly JsonObject _root;
        private readonly HashSet<string> _used;
        private readonly List<ImportDiagnostic> _diagnostics;
        private readonly string _path;
        private int _skips;
        private int _nodes;

        public Scan(JsonObject root, HashSet<string> used, List<ImportDiagnostic> diagnostics, string path)
        {
            _root = root;
            _used = used;
            _diagnostics = diagnostics;
            _path = path;
        }

        public int TokenCount { get; private set; }

        public void Search(JsonNode? node) => Search(node, 0);

        private void Search(JsonNode? node, int depth)
        {
            if (node is null || depth > ImportLimits.JsonMaxDepth || _nodes++ > MaxScanNodes)
                return;

            switch (node)
            {
                case JsonObject obj:
                    foreach (KeyValuePair<string, JsonNode?> property in obj)
                    {
                        if (property.Value is JsonArray array && IsLibraryKey(property.Key))
                        {
                            foreach (JsonNode? entry in array)
                                WriteEntry(entry);
                        }
                        else if (property.Value is JsonObject map && IsMapLibraryKey(property.Key))
                        {
                            // A Penpot file document keys its libraries by id: { "colors": { "<uuid>": { ... } } }.
                            foreach (KeyValuePair<string, JsonNode?> entry in map)
                                WriteEntry(entry.Value);
                        }
                        else
                        {
                            Search(property.Value, depth + 1);
                        }
                    }

                    break;

                case JsonArray array:
                    foreach (JsonNode? item in array)
                        Search(item, depth + 1);
                    break;
            }
        }

        private void WriteEntry(JsonNode? entry)
        {
            if (entry is not JsonObject obj)
                return;

            string? name = StringOf(obj, "name") ?? StringOf(obj, "id");
            string? path = StringOf(obj, "path") ?? StringOf(obj, "group");

            // An entry is a colour when it carries a colour value under any known key; otherwise a
            // typography when it carries a font metric. The library key only disambiguates.
            if (HasAnyKey(obj, ColorValueKeys))
            {
                WriteColor(obj, path, name);
                return;
            }

            if (HasAnyKey(obj, "font-family", "fontFamily", "font-size", "fontSize", "line-height", "lineHeight"))
            {
                WriteTypography(obj, path, name);
            }
        }

        private void WriteColor(JsonObject obj, string? path, string? name)
        {
            JsonNode? raw = null;
            foreach (string key in ColorValueKeys)
            {
                if (TryGet(obj, key, out JsonNode? value) && value is not null)
                {
                    raw = value;
                    break;
                }
            }

            JsonNode? normalized = TokenNormalizer.NormalizeColor(raw, out _);
            if (normalized is null)
            {
                Skip(name ?? path, "colour is not a supported sRGB value");
                return;
            }

            string dotPath = BuildPath("color.penpot", path, name);
            if (dotPath.Length > MaxPathLength)
            {
                Skip(name ?? path, "token path is too long");
                return;
            }

            Emit("color", DesignKeys.Unique(_used, dotPath), normalized);
        }

        private void WriteTypography(JsonObject obj, string? path, string? name)
        {
            var members = new JsonObject();
            AddMember(members, obj, "fontFamily", "font-family", "fontfamily");
            AddMember(members, obj, "fontSize", "font-size", "fontsize");
            AddMember(members, obj, "fontWeight", "font-weight", "fontweight");
            AddMember(members, obj, "lineHeight", "line-height", "lineheight");
            AddMember(members, obj, "letterSpacing", "letter-spacing", "letterspacing");

            JsonNode? normalized = TokenNormalizer.NormalizeTypography(members, out _);
            if (normalized is null)
            {
                Skip(name ?? path, "typography carries no recognizable sub-values");
                return;
            }

            string dotPath = BuildPath("typography.penpot", path, name);
            if (dotPath.Length > MaxPathLength)
            {
                Skip(name ?? path, "token path is too long");
                return;
            }

            Emit("typography", DesignKeys.Unique(_used, dotPath), normalized);
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

        private static string BuildPath(string prefix, string? path, string? name)
        {
            string source = !string.IsNullOrWhiteSpace(path) ? path! : name ?? string.Empty;

            // A real Penpot library entry carries the GROUP in `path` and the leaf in `name`
            // ({ "path": "Brand", "name": "Primary" }). When the name is a single segment that the path does
            // not already end with, the group and the name together are the token path; otherwise the path
            // alone is (the shape the earlier fixtures use: path "brand/primary", name "Brand / Primary").
            if (!string.IsNullOrWhiteSpace(path)
                && !string.IsNullOrWhiteSpace(name)
                && name!.IndexOfAny(['/', '\\', '.']) < 0)
            {
                string leaf = DesignKeys.Slug(name.Trim());
                string last = DesignKeys.Slug(path!.Split(['/', '\\', '.'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? string.Empty);
                if (leaf.Length > 0 && leaf != last)
                    source = path + "/" + name;
            }

            var segments = new List<string>();
            foreach (string raw in source.Split(['/', '\\', '.'], StringSplitOptions.RemoveEmptyEntries))
            {
                string slug = DesignKeys.Slug(raw.Trim());
                if (slug.Length > 0)
                    segments.Add(slug);
            }

            if (segments.Count == 0)
                segments.Add("default");

            return prefix + "." + string.Join('.', segments);
        }

        private static void AddMember(JsonObject target, JsonObject source, string canonical, params string[] aliases)
        {
            foreach (string alias in aliases)
            {
                if (!TryGet(source, alias, out JsonNode? value) || value is null)
                    continue;

                target[canonical] = value.DeepClone();
                return;
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

        private static bool HasAnyKey(JsonObject obj, params string[] keys)
        {
            foreach (string key in keys)
            {
                if (TryGet(obj, key, out _))
                    return true;
            }

            return false;
        }

        /// <summary>The library keys whose value may be an id-keyed map. <c>tokens</c> is excluded: a DTCG
        /// <c>tokens</c> object is a token tree, not a library.</summary>
        private static bool IsMapLibraryKey(string key) =>
            key.Equals("colors", StringComparison.OrdinalIgnoreCase)
            || key.Equals("typographies", StringComparison.OrdinalIgnoreCase)
            || key.Equals("typography", StringComparison.OrdinalIgnoreCase);

        private static bool IsLibraryKey(string key) => ColorKeys.Contains(key) || TypographyKeys.Contains(key);

        /// <summary>Lowercases and strips separators so <c>font-family</c>, <c>fontFamily</c> and
        /// <c>font_family</c> compare equal.</summary>
        private static string Normalize(string key) => key.Replace("-", string.Empty).Replace("_", string.Empty);
    }

    private static bool ContainsKey(string text, string key) =>
        text.Contains("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);

    private static string Loc(string path) => path.Length > MaxLocLength ? path[..MaxLocLength] : path;

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);
}
