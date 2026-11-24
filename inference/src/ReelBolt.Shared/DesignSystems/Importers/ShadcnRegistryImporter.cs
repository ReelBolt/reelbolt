using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.DesignSystems.Merge;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads a shadcn/ui registry item (or a registry collection with an <c>items</c> array) and turns
/// its <c>cssVars</c> into a canonical <see cref="DesignDocument"/> by building the equivalent CSS
/// custom-property text and delegating to <see cref="CssThemeImporter"/>. Component source carried
/// by <c>files[].content</c> (or <c>css</c>/<c>tailwind</c>) is never copied into any output.
///
/// <para>
/// Pure and total: the input is inert data, nothing is evaluated, and every failure becomes an
/// <see cref="ImportDiagnostic"/> rather than an exception.
/// </para>
/// </summary>
public sealed class ShadcnRegistryImporter : IDesignImporter
{
    /// <inheritdoc />
    public string Id => "shadcn-registry";

    private const int MaxValueLength = 200;

    private static readonly Regex VarNamePattern = new(
        @"^[a-z0-9][a-z0-9-]{0,63}$",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ThemeItemTypes = new(StringComparer.Ordinal)
    {
        "registry:theme",
        "registry:style",
        "registry:base",
    };

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head) => LooksLikeRegistryItem(path, head);

    /// <summary>
    /// True when the path ends in <c>.json</c> and the head carries a shadcn registry marker: a
    /// <c>"$schema"</c> naming <c>registry-item.json</c>/<c>/registry.json</c>, or a
    /// <c>registry:theme</c>/<c>registry:style</c> type.
    /// </summary>
    public static bool LooksLikeRegistryItem(string path, ReadOnlySpan<byte> head)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        if (head.Length == 0)
            return false;

        int limit = Math.Min(head.Length, ImportLimits.HeadBytes);
        string text = Encoding.UTF8.GetString(head[..limit]);
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        bool schemaMarker = text.Contains("\"$schema\"", StringComparison.Ordinal)
            && (text.Contains("registry-item.json", StringComparison.Ordinal)
                || text.Contains("/registry.json", StringComparison.Ordinal));

        bool typeMarker = text.Contains("registry:theme", StringComparison.Ordinal)
            || text.Contains("registry:style", StringComparison.Ordinal);

        return schemaMarker || typeMarker;
    }

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files)
    {
        var diagnostics = new List<ImportDiagnostic>();
        var sources = new List<ImportSourceRef>();
        var designs = new List<JsonObject>();

        if (files is not null)
        {
            foreach (ImportFile? file in files)
            {
                if (file is null)
                    continue;

                try
                {
                    ImportOne(file, designs, sources, diagnostics);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(Error("IMPORT_INTERNAL", file.Path ?? string.Empty, exception.GetType().Name));
                }
            }
        }

        JsonObject? design = designs.Count switch
        {
            0 => null,
            1 => designs[0],
            _ => DesignDocumentMerger.Merge(null, designs),
        };

        if (design is null)
            diagnostics.Add(Warn("NO_TOKENS_RECOGNIZED", string.Empty, "no shadcn registry cssVars were recognized"));

        return new DesignImportResult(design, null, sources, diagnostics);
    }

    private static void ImportOne(
        ImportFile file,
        List<JsonObject> designs,
        List<ImportSourceRef> sources,
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

            var items = new List<JsonObject>();
            if (root.ContainsKey("cssVars") || root.ContainsKey("type"))
                items.Add(root);
            if (root["items"] is JsonArray array)
            {
                foreach (JsonNode? node in array)
                {
                    if (node is JsonObject item)
                        items.Add(item);
                }
            }

            bool sawCssVars = false;
            int droppedCode = 0;
            var css = new StringBuilder();

            foreach (JsonObject item in items)
            {
                if (item["cssVars"] is JsonObject cssVars)
                {
                    sawCssVars = true;
                    AppendVars(css, ":root", cssVars["light"] as JsonObject, path, diagnostics);
                    AppendVars(css, ".dark", cssVars["dark"] as JsonObject, path, diagnostics);
                    AppendVars(css, "@theme inline", cssVars["theme"] as JsonObject, path, diagnostics);
                }

                if (ContainsCode(item))
                    droppedCode++;
            }

            if (droppedCode > 0)
            {
                diagnostics.Add(Info(
                    "DROPPED_CODE",
                    path,
                    $"{droppedCode} registry items contain source code and were ignored"));
            }

            if (!sawCssVars)
                return;

            byte[] bytes = Encoding.UTF8.GetBytes(css.ToString());
            string sha = Sha256(file.Content.Span);
            var synthetic = new ImportFile(path, bytes, sha, file.ProjectFileId);
            DesignImportResult cssResult = new CssThemeImporter().Import([synthetic]);

            if (cssResult.Design is null)
                return;

            foreach (DesignTokenRef token in DesignDocument.Tokens(cssResult.Design))
            {
                if (token.Token["$extensions"] is JsonObject extensions
                    && extensions["com.reelbolt"] is JsonObject reelbolt
                    && reelbolt["prov"] is JsonObject provenance)
                {
                    provenance["method"] = "imported";
                    provenance["importer"] = "shadcn-registry";
                    provenance["loc"] = path;
                }
            }

            diagnostics.AddRange(cssResult.Diagnostics);
            designs.Add(cssResult.Design);
            sources.Add(new ImportSourceRef(path, "shadcn-registry", sha, file.ProjectFileId));
        }
    }

    private static void AppendVars(
        StringBuilder css,
        string selector,
        JsonObject? vars,
        string path,
        List<ImportDiagnostic> diagnostics)
    {
        if (vars is null)
            return;

        var body = new StringBuilder();
        foreach (KeyValuePair<string, JsonNode?> property in vars)
        {
            string name = property.Key;
            if (!VarNamePattern.IsMatch(name))
            {
                diagnostics.Add(Warn("UNSUPPORTED_VALUE", path, $"cssVars variable '{name}' was skipped"));
                continue;
            }

            if (property.Value is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            {
                diagnostics.Add(Warn("UNSUPPORTED_VALUE", path, $"cssVars variable '{name}' was skipped"));
                continue;
            }

            if (!IsSafeValue(value.GetValue<string>()))
            {
                diagnostics.Add(Warn("UNSUPPORTED_VALUE", path, $"cssVars variable '{name}' was skipped"));
                continue;
            }

            body.Append("--").Append(name).Append(':').Append(value.GetValue<string>()).Append(';');
        }

        if (body.Length == 0)
            return;

        css.Append(selector).Append('{').Append(body).Append('}');
    }

    private static bool IsSafeValue(string value)
    {
        if (value.Length > MaxValueLength)
            return false;

        foreach (char c in value)
        {
            if (c is '{' or '}' or ';' or '\\' or '<' or '>' or '@' or '\n' or '\r')
                return false;
        }

        return !value.Contains("/*", StringComparison.Ordinal)
            && !value.Contains("*/", StringComparison.Ordinal)
            && !value.Contains("url(", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("expression(", StringComparison.OrdinalIgnoreCase)
            && !value.Contains("javascript:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsCode(JsonObject item)
    {
        string? type = item["type"] is JsonValue value && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

        bool themeLike = type is not null && ThemeItemTypes.Contains(type);
        bool hasFiles = item["files"] is JsonArray;

        return !themeLike || hasFiles;
    }

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

    private static ImportDiagnostic Info(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Info, code, path, message);
}
