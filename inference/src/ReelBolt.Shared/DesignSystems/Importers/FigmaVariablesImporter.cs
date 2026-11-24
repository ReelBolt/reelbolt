using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads the body of Figma's REST <c>GET /v1/files/{key}/variables/local</c> into a canonical
/// <see cref="DesignDocument"/>. Pure and total: the input is inert data, no file is executed,
/// nothing is fetched here and every surprise becomes an <see cref="ImportDiagnostic"/>.
///
/// <para>
/// Deliberately NOT registered in <see cref="DesignImportRegistry"/>: the Figma import is a one-shot
/// API call whose access token is never stored, so the route hands the fetched body straight to
/// <see cref="Import"/> and the token never reaches this type.
/// </para>
///
/// <para>
/// Mapping (SPEC 16.4 figma-variables): names <c>a/b/c</c> become dot paths, the collection's default
/// mode becomes the base token, and the other modes named <c>dark</c>/<c>light</c> become
/// <c>modes.dark</c>/<c>modes.light</c> overrides (an unrecognised mode name is dropped with an
/// Info). COLOR becomes <c>color.*</c> converted through sRGB, FLOAT becomes <c>number.*</c>,
/// STRING is skipped with an Info and BOOLEAN is ignored; VARIABLE_ALIAS values become
/// <c>{target.path}</c> aliases. Every token carries confidence 0.4 — the "low" band — because a
/// variables file states names and values, not intent.
/// </para>
/// </summary>
public sealed class FigmaVariablesImporter : IDesignImporter
{
    /// <summary>The stable importer identifier used in provenance and in <see cref="ImportSourceRef"/>.</summary>
    public string Id => "figma-variables";

    private const double Confidence = 0.4;
    private const int MaxTokens = 1500;
    private const int MaxPathLength = 160;
    private const int MaxDepth = 6;

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head)
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

        return text.Contains("\"valuesByMode\"", StringComparison.Ordinal)
            || text.Contains("\"variableCollections\"", StringComparison.Ordinal)
            || (text.Contains("\"variables\"", StringComparison.Ordinal)
                && text.Contains("\"resolvedType\"", StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files)
    {
        var design = DesignDocument.New();
        var sources = new List<ImportSourceRef>();
        var diagnostics = new List<ImportDiagnostic>();
        var usedPaths = new HashSet<string>(StringComparer.Ordinal);
        int emitted = 0;

        try
        {
            if (files is not null)
            {
                foreach (ImportFile? file in files)
                {
                    if (file is null)
                        continue;

                    string path = file.Path ?? string.Empty;
                    sources.Add(new ImportSourceRef(path, Id, Sha256(file.Content.Span), file.ProjectFileId));

                    if (file.Content.Length > ImportLimits.JsonMaxBytes)
                    {
                        diagnostics.Add(Warn(
                            "IMPORT_TOO_LARGE",
                            path,
                            $"file is {file.Content.Length} bytes, over the {ImportLimits.JsonMaxBytes} byte limit"));
                        continue;
                    }

                    JsonObject? root = ParseObject(file.Content, path, diagnostics);
                    if (root is null)
                        continue;

                    JsonObject meta = root["meta"] as JsonObject ?? root;
                    emitted += ImportMeta(meta, design, usedPaths, diagnostics, path);
                }
            }
        }
        catch (Exception exception)
        {
            diagnostics.Add(Error("IMPORT_INTERNAL", string.Empty, exception.GetType().Name));
        }

        bool produced = emitted > 0;
        if (!produced)
            diagnostics.Add(Warn("NO_TOKENS_RECOGNIZED", string.Empty, "no Figma variable could be imported"));

        return new DesignImportResult(produced ? design : null, null, sources, diagnostics);
    }

    private static int ImportMeta(
        JsonObject meta,
        JsonObject design,
        HashSet<string> usedPaths,
        List<ImportDiagnostic> diagnostics,
        string loc)
    {
        JsonObject? variables = meta["variables"] as JsonObject;
        if (variables is null)
        {
            diagnostics.Add(Warn("NO_VARIABLES", loc, "the response has no meta.variables object"));
            return 0;
        }

        JsonObject? collections = meta["variableCollections"] as JsonObject;

        // First pass: the canonical path of every importable variable, keyed by its Figma id, so an
        // alias can point at a token no matter which order the two appear in.
        var pathsById = new Dictionary<string, string>(StringComparer.Ordinal);
        var typesById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> property in variables)
        {
            if (property.Value is not JsonObject variable)
                continue;

            string? canonicalType = CanonicalType(StringProp(variable, "resolvedType"));
            if (canonicalType is null)
                continue;

            string name = StringProp(variable, "name") ?? property.Key;
            string rawPath = canonicalType + "." + PathFromName(name);
            if (rawPath.Length > MaxPathLength || rawPath.Split('.').Length > MaxDepth)
            {
                diagnostics.Add(Warn("UNSUPPORTED_VALUE", name, "variable path is too deep or too long"));
                continue;
            }

            pathsById[property.Key] = DesignKeys.Unique(usedPaths, rawPath);
            typesById[property.Key] = canonicalType;
        }

        int emitted = 0;
        foreach (KeyValuePair<string, JsonNode?> property in variables)
        {
            if (property.Value is not JsonObject variable)
                continue;

            string resolvedType = StringProp(variable, "resolvedType") ?? string.Empty;
            string name = StringProp(variable, "name") ?? property.Key;

            if (resolvedType == "BOOLEAN")
                continue;

            if (!pathsById.TryGetValue(property.Key, out string? path)
                || !typesById.TryGetValue(property.Key, out string? canonicalType))
            {
                diagnostics.Add(Info("VARIABLE_SKIPPED", name, $"resolvedType '{resolvedType}' is not imported"));
                continue;
            }

            if (variable["valuesByMode"] is not JsonObject values || values.Count == 0)
            {
                diagnostics.Add(Info("VARIABLE_SKIPPED", name, "variable has no valuesByMode"));
                continue;
            }

            string? collectionId = StringProp(variable, "variableCollectionId");
            string? defaultModeId = DefaultModeId(collections, collectionId, values);
            if (defaultModeId is null
                || !values.TryGetPropertyValue(defaultModeId, out JsonNode? defaultValue))
            {
                diagnostics.Add(Info("VARIABLE_SKIPPED", name, "variable has no value in its default mode"));
                continue;
            }

            JsonNode? converted = ConvertValue(defaultValue, canonicalType, pathsById, diagnostics, name);
            if (converted is null)
                continue;

            if (emitted >= MaxTokens)
            {
                diagnostics.Add(Warn("TRUNCATED", name, $"more than {MaxTokens} tokens; the rest is ignored"));
                break;
            }

            DesignDocument.SetToken(design, path, canonicalType, converted, Provenance(loc), Confidence);
            emitted++;

            foreach (KeyValuePair<string, JsonNode?> mode in values)
            {
                if (mode.Key == defaultModeId)
                    continue;

                string? modeName = ModeName(collections, collectionId, mode.Key);
                string? target = CanonicalMode(modeName);
                if (target is null)
                {
                    diagnostics.Add(Info(
                        "MODE_SKIPPED",
                        name,
                        $"mode '{DesignKeys.Clip(modeName ?? mode.Key)}' is not dark or light and was dropped"));
                    continue;
                }

                JsonNode? modeValue = ConvertValue(mode.Value, canonicalType, pathsById, diagnostics, name);
                if (modeValue is null)
                    continue;

                JsonObject modes = EnsureObject(design, "modes");
                JsonObject bucket = EnsureObject(modes, target);
                bucket[path] = modeValue;
            }
        }

        return emitted;
    }

    private static JsonNode? ConvertValue(
        JsonNode? raw,
        string canonicalType,
        Dictionary<string, string> pathsById,
        List<ImportDiagnostic> diagnostics,
        string variableName)
    {
        if (raw is null)
            return null;

        if (IsAlias(raw, out string? targetId))
        {
            if (targetId is not null && pathsById.TryGetValue(targetId, out string? targetPath))
                return JsonValue.Create("{" + targetPath + "}");

            diagnostics.Add(Info("ALIAS_UNRESOLVED", variableName, $"alias target '{targetId}' is not imported"));
            return null;
        }

        return canonicalType == "color"
            ? ConvertColor(raw, diagnostics, variableName)
            : ConvertNumber(raw, diagnostics, variableName);
    }

    private static JsonNode? ConvertColor(JsonNode? raw, List<ImportDiagnostic> diagnostics, string variableName)
    {
        if (raw is not JsonObject color)
        {
            diagnostics.Add(Warn("UNSUPPORTED_VALUE", variableName, "a COLOR value must be an object"));
            return null;
        }

        if (!TryComponent(color["r"], out double r)
            || !TryComponent(color["g"], out double g)
            || !TryComponent(color["b"], out double b))
        {
            diagnostics.Add(Warn("UNSUPPORTED_VALUE", variableName, "a COLOR value must carry r, g and b"));
            return null;
        }

        var hex = new StringBuilder(9);
        hex.Append('#');
        hex.Append(Byte(r).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        hex.Append(Byte(g).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        hex.Append(Byte(b).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));

        if (TryComponent(color["a"], out double a) && a < 1)
            hex.Append(Byte(a).ToString("x2", System.Globalization.CultureInfo.InvariantCulture));

        return new JsonObject { ["hex"] = hex.ToString(), ["colorSpace"] = "srgb" };
    }

    private static JsonNode? ConvertNumber(JsonNode? raw, List<ImportDiagnostic> diagnostics, string variableName)
    {
        if (!CanonicalJson.TryGetNumber(raw, out double number) || !double.IsFinite(number) || Math.Abs(number) > 1e6)
        {
            diagnostics.Add(Warn("UNSUPPORTED_VALUE", variableName, "a FLOAT value must be finite and within +/-1000000"));
            return null;
        }

        return JsonValue.Create(number);
    }

    private static bool IsAlias(JsonNode? raw, out string? id)
    {
        id = null;
        if (raw is not JsonObject alias)
            return false;

        if (!string.Equals(StringProp(alias, "type"), "VARIABLE_ALIAS", StringComparison.Ordinal))
            return false;

        id = StringProp(alias, "id");
        return true;
    }

    /// <summary>True for a finite component; the value is clamped into <c>[0,1]</c> by the callers.</summary>
    private static bool TryComponent(JsonNode? node, out double value)
    {
        value = 0;
        return node is not null && CanonicalJson.TryGetNumber(node, out value) && double.IsFinite(value);
    }

    private static byte Byte(double component)
    {
        double clamped = Math.Clamp(component, 0, 1);
        return (byte)Math.Round(clamped * 255, MidpointRounding.AwayFromZero);
    }

    private static string? CanonicalType(string? resolvedType) => resolvedType switch
    {
        "COLOR" => "color",
        "FLOAT" => "number",
        _ => null,
    };

    private static string? CanonicalMode(string? modeName)
    {
        if (modeName is null)
            return null;
        if (modeName.Equals("dark", StringComparison.OrdinalIgnoreCase))
            return "dark";
        if (modeName.Equals("light", StringComparison.OrdinalIgnoreCase))
            return "light";
        return null;
    }

    private static string? DefaultModeId(JsonObject? collections, string? collectionId, JsonObject values)
    {
        if (collections is not null
            && collectionId is not null
            && collections[collectionId] is JsonObject collection)
        {
            string? declared = StringProp(collection, "defaultModeId");
            if (declared is not null && values.ContainsKey(declared))
                return declared;

            if (collection["modes"] is JsonArray modes)
            {
                foreach (JsonNode? item in modes)
                {
                    if (item is JsonObject mode
                        && StringProp(mode, "modeId") is { } modeId
                        && values.ContainsKey(modeId))
                    {
                        return modeId;
                    }
                }
            }
        }

        foreach (KeyValuePair<string, JsonNode?> value in values)
            return value.Key;

        return null;
    }

    private static string? ModeName(JsonObject? collections, string? collectionId, string modeId)
    {
        if (collections is null
            || collectionId is null
            || collections[collectionId] is not JsonObject collection
            || collection["modes"] is not JsonArray modes)
        {
            return null;
        }

        foreach (JsonNode? item in modes)
        {
            if (item is JsonObject mode
                && string.Equals(StringProp(mode, "modeId"), modeId, StringComparison.Ordinal))
            {
                return StringProp(mode, "name");
            }
        }

        return null;
    }

    private static string PathFromName(string name)
    {
        var segments = new List<string>();
        foreach (string segment in name.Split('/'))
        {
            if (segment.Length == 0)
                continue;

            segments.Add(DesignKeys.Slug(segment));
        }

        return segments.Count == 0 ? "x" : string.Join('.', segments);
    }

    private static JsonObject? ParseObject(ReadOnlyMemory<byte> content, string path, List<ImportDiagnostic> diagnostics)
    {
        if (content.Length == 0)
        {
            diagnostics.Add(Warn("NO_TOKENS_RECOGNIZED", path, "input is empty"));
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(
                content,
                new JsonDocumentOptions
                {
                    MaxDepth = ImportLimits.JsonMaxDepth,
                    CommentHandling = JsonCommentHandling.Disallow,
                    AllowTrailingCommas = false,
                });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Warn("NO_TOKENS_RECOGNIZED", path, "the JSON root must be an object"));
                return null;
            }

            return JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject;
        }
        catch (JsonException exception)
        {
            diagnostics.Add(Warn("NO_TOKENS_RECOGNIZED", path, "input is not valid JSON: " + exception.Message));
            return null;
        }
    }

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent.TryGetPropertyValue(key, out JsonNode? node) && node is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static string? StringProp(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out JsonNode? node)
            && node is JsonValue value
            && value.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static TokenProvenance Provenance(string loc) => new("imported", "figma-variables", null, loc);

    private static ImportDiagnostic Error(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Error, code, path, message);

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);

    private static ImportDiagnostic Info(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Info, code, path, message);
}
