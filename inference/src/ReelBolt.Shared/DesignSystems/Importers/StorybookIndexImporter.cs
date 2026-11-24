using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads a Storybook <c>index.json</c> (Storybook 7+ <c>entries</c>) or <c>stories.json</c>
/// (Storybook 6 <c>stories</c>) and turns the component names and story paths into a
/// <see cref="ProductMapDocument"/>. Only the index JSON is ever read: no story file is opened and
/// no story source is copied.
///
/// <para>
/// Pure and total: the input is inert data, nothing is evaluated, and every failure becomes an
/// <see cref="ImportDiagnostic"/> rather than an exception.
/// </para>
/// </summary>
public sealed class StorybookIndexImporter : IDesignImporter
{
    /// <inheritdoc />
    public string Id => "storybook-index";

    private const int MaxComponents = 300;

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string name = Path.GetFileName(path);
        if (!name.Equals("index.json", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("stories.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (head.Length == 0)
            return false;

        int limit = Math.Min(head.Length, ImportLimits.HeadBytes);
        string text = Encoding.UTF8.GetString(head[..limit]);

        return text.Contains("\"entries\"", StringComparison.Ordinal)
            || text.Contains("\"stories\"", StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files)
    {
        var diagnostics = new List<ImportDiagnostic>();
        var entries = new List<RawEntry>();
        var contributing = new List<ImportSourceRef>();
        int skipped = 0;

        if (files is not null)
        {
            foreach (ImportFile? file in files)
            {
                if (file is null)
                    continue;

                try
                {
                    ImportOne(file, entries, contributing, ref skipped, diagnostics);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(Error("IMPORT_INTERNAL", file.Path ?? string.Empty, exception.GetType().Name));
                }
            }
        }

        if (skipped > 0)
        {
            diagnostics.Add(Warn(
                "UNSUPPORTED_VALUE",
                string.Empty,
                $"{skipped} story entries had an unusable path and were skipped"));
        }

        entries.Sort(static (left, right) =>
        {
            int byPath = string.CompareOrdinal(left.Path, right.Path);
            return byPath != 0 ? byPath : string.CompareOrdinal(left.Name, right.Name);
        });

        var components = new List<ProductMapComponent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (RawEntry entry in entries)
        {
            string key = entry.Name + "\u0000" + entry.Path;
            if (!seen.Add(key))
                continue;

            components.Add(new ProductMapComponent(entry.Name, entry.Path, entry.Responsibility, [], false, "static"));
        }

        if (components.Count > MaxComponents)
        {
            diagnostics.Add(Warn(
                "TRUNCATED",
                string.Empty,
                $"index has {components.Count} components, keeping the first {MaxComponents}"));
            components.RemoveRange(MaxComponents, components.Count - MaxComponents);
        }

        if (components.Count == 0)
        {
            diagnostics.Add(Warn("NO_TOKENS_RECOGNIZED", string.Empty, "no Storybook components were recognized"));
            return new DesignImportResult(null, null, [], diagnostics);
        }

        var framework = new ProductMapFramework("other", string.Empty, "other", [], "unknown", "component library");
        var dependencies = new ProductMapDependencies([], [], []);
        var provenance = new Dictionary<string, ProductMapProvenance>(StringComparer.Ordinal)
        {
            ["components"] = new ProductMapProvenance("static", []),
        };

        var map = new ProductMapDocument(
            ProductMapDocument.SchemaId,
            null,
            framework,
            null,
            null,
            [],
            [],
            [],
            components,
            dependencies,
            [],
            provenance);

        return new DesignImportResult(null, map, contributing, diagnostics);
    }

    private static void ImportOne(
        ImportFile file,
        List<RawEntry> entries,
        List<ImportSourceRef> contributing,
        ref int skipped,
        List<ImportDiagnostic> diagnostics)
    {
        string path = file.Path ?? string.Empty;

        if (file.Content.Length > ImportLimits.JsonMaxBytes)
        {
            diagnostics.Add(Error(
                "IMPORT_TOO_LARGE",
                path,
                $"file is {file.Content.Length} bytes, over the {ImportLimits.JsonMaxBytes} byte limit"));
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(
                file.Content,
                new JsonDocumentOptions
                {
                    MaxDepth = ImportLimits.JsonMaxDepth,
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                });
        }
        catch (JsonException exception)
        {
            diagnostics.Add(Error("IMPORT_TOO_LARGE", path, "input is not valid JSON within the limits: " + exception.Message));
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Error("IMPORT_TOO_LARGE", path, "the JSON root must be an object"));
                return;
            }

            int nodes = CountNodes(document.RootElement);
            if (nodes > ImportLimits.JsonMaxNodes)
            {
                diagnostics.Add(Error(
                    "IMPORT_TOO_LARGE",
                    path,
                    $"document has {nodes} nodes, over the {ImportLimits.JsonMaxNodes} limit"));
                return;
            }

            JsonObject? root = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject;
            if (root is null)
                return;

            JsonObject? index = root["entries"] as JsonObject ?? root["stories"] as JsonObject;
            if (index is null)
                return;

            int before = entries.Count;
            foreach (KeyValuePair<string, JsonNode?> property in index)
            {
                if (property.Value is not JsonObject entry)
                    continue;

                string? title = StringField(entry, "title") ?? StringField(entry, "kind");
                string? importPath = StringField(entry, "importPath");
                if (title is null || importPath is null)
                    continue;

                int slash = title.LastIndexOf('/');
                string last = slash >= 0 ? title[(slash + 1)..] : title;
                string prefix = slash >= 0 ? title[..slash] : string.Empty;

                string? name = DesignText.Sanitize(last, 80);
                if (name is null)
                    continue;
                string responsibility = DesignText.Sanitize(prefix, 160) ?? string.Empty;

                string rawPath = importPath.StartsWith("./", StringComparison.Ordinal)
                    ? importPath[2..]
                    : importPath;
                if (IsUnsafePath(rawPath))
                {
                    skipped++;
                    continue;
                }

                string? cleanedPath = DesignText.Sanitize(rawPath, 200);
                if (cleanedPath is null)
                    continue;

                entries.Add(new RawEntry(name, responsibility, cleanedPath));
            }

            if (entries.Count > before)
                contributing.Add(new ImportSourceRef(path, "storybook-index", Sha256(file.Content.Span), file.ProjectFileId));
        }
    }

    private static string? StringField(JsonObject node, string key) =>
        node[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static bool IsUnsafePath(string path) =>
        path.Length == 0
        || path.StartsWith('/')
        || path.Contains("..", StringComparison.Ordinal)
        || path.Contains('\\')
        || path.Contains(':');

    private static int CountNodes(JsonElement element)
    {
        int count = 1;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    count += CountNodes(property.Value) + 1;
                    if (count > ImportLimits.JsonMaxNodes)
                        return count;
                }

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    count += CountNodes(item);
                    if (count > ImportLimits.JsonMaxNodes)
                        return count;
                }

                break;
        }

        return count;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ImportDiagnostic Error(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Error, code, path, message);

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);

    private sealed record RawEntry(string Name, string Responsibility, string Path);
}
