using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads tokens in the DTCG 2025.10 dialect (<c>$value</c>/<c>$type</c>) and the legacy Style
/// Dictionary dialect (<c>value</c>/<c>type</c>), converting both into a canonical
/// <see cref="DesignDocument"/>. Pure and total: it never touches IO, never throws and reports
/// hostile input as an <see cref="ImportDiagnostic"/>.
/// </summary>
public sealed class DtcgImporter : IDesignImporter
{
    /// <summary>The importer identifier used in provenance and in <see cref="ImportSourceRef"/>.</summary>
    public string Id => "dtcg";

    private const double Confidence = 0.95;
    private const int MaxKeyLength = 48;
    private const int MaxLocLength = 200;
    private const int MaxTokens = 1500;
    private const int MaxTypeLength = 32;

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal)
    {
        "color",
        "dimension",
        "fontFamily",
        "fontWeight",
        "duration",
        "cubicBezier",
        "number",
        "shadow",
        "typography",
        "keyword",
    };

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head)
    {
        if (TokensStudioImporter.LooksLikeTokensStudio(path, head))
            return false;

        if (ShadcnRegistryImporter.LooksLikeRegistryItem(path, head))
            return false;

        if (string.IsNullOrEmpty(path) || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;

        if (head.Length == 0)
            return false;

        int limit = Math.Min(head.Length, ImportLimits.HeadBytes);
        ReadOnlySpan<byte> window = head[..limit];

        int start = 0;
        if (window.Length >= 3 && window[0] == 0xEF && window[1] == 0xBB && window[2] == 0xBF)
            start = 3;

        while (start < window.Length && IsWhitespace(window[start]))
            start++;

        if (start >= window.Length || (window[start] != (byte)'{' && window[start] != (byte)'['))
            return false;

        try
        {
            using var document = JsonDocument.Parse(window.ToArray());
            return HasMarkers(document.RootElement);
        }
        catch (JsonException)
        {
            bool dtcg = ContainsAscii(window, "\"$value\""u8) && ContainsAscii(window, "\"$type\""u8);
            bool legacy = ContainsAscii(window, "\"value\""u8) && ContainsAscii(window, "\"type\""u8);
            return dtcg || legacy;
        }
    }

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files)
    {
        var design = DesignDocument.New();
        var sources = new List<ImportSourceRef>();
        var diagnostics = new List<ImportDiagnostic>();
        var usedPaths = new HashSet<string>(StringComparer.Ordinal);

        if (files is not null)
        {
            foreach (ImportFile? file in files)
            {
                if (file is null)
                    continue;

                try
                {
                    ImportOne(design, file, sources, diagnostics, usedPaths);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(Error("IMPORT_INTERNAL", file.Path, exception.GetType().Name));
                }
            }
        }

        return new DesignImportResult(design, null, sources, diagnostics);
    }

    private static void ImportOne(
        JsonObject design,
        ImportFile file,
        List<ImportSourceRef> sources,
        List<ImportDiagnostic> diagnostics,
        HashSet<string> usedPaths)
    {
        string path = file.Path ?? string.Empty;
        sources.Add(new ImportSourceRef(path, "dtcg", Sha256(file.Content.Span), file.ProjectFileId));

        if (file.Content.Length > ImportLimits.JsonMaxBytes)
        {
            diagnostics.Add(Error("IMPORT_TOO_LARGE", path, $"file is {file.Content.Length} bytes, over the {ImportLimits.JsonMaxBytes} byte limit"));
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
            if (ExceedsDepth(file.Content.Span, ImportLimits.JsonMaxDepth))
                diagnostics.Add(Error("TOO_DEEP", path, $"JSON nesting is deeper than {ImportLimits.JsonMaxDepth} levels"));
            else
                diagnostics.Add(Error("JSON_INVALID", path, "input is not valid JSON: " + DesignKeys.Clip(exception.Message)));
            return;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Error("JSON_INVALID", path, "the JSON root must be an object"));
                return;
            }

            int nodes = CountNodes(document.RootElement);
            if (nodes > ImportLimits.JsonMaxNodes)
            {
                diagnostics.Add(Error("IMPORT_TOO_LARGE", path, $"document has {nodes} nodes, over the {ImportLimits.JsonMaxNodes} limit"));
                return;
            }

            if (document.RootElement.TryGetProperty("resolutionOrder", out _))
                diagnostics.Add(Warn("RESOLVER_UNSUPPORTED", path, "DTCG resolver files are not interpreted"));

            string loc = path.Length > MaxLocLength ? path[..MaxLocLength] : path;
            Walk(document.RootElement, string.Empty, GroupType(document.RootElement, null), design, usedPaths, diagnostics, loc);
        }
    }

    private static bool Walk(
        JsonElement group,
        string parentPath,
        string? inheritedType,
        JsonObject design,
        HashSet<string> usedPaths,
        List<ImportDiagnostic> diagnostics,
        string loc)
    {
        string? groupType = GroupType(group, inheritedType);

        foreach (JsonProperty property in group.EnumerateObject())
        {
            string key = property.Name;
            if (key.Length > 0 && key[0] == '$')
                continue;
            if (key is "value" or "type")
                continue;

            JsonElement child = property.Value;
            if (child.ValueKind != JsonValueKind.Object)
                continue;

            string segment = NormalizeKey(key);
            string path = parentPath.Length == 0 ? segment : parentPath + "." + segment;

            bool hasDtcgValue = child.TryGetProperty("$value", out JsonElement dtcgValue);
            bool hasLegacyValue = child.TryGetProperty("value", out JsonElement legacyValue);
            bool isToken = hasDtcgValue
                || (hasLegacyValue && (child.TryGetProperty("type", out _) || groupType is not null));

            if (isToken)
            {
                if (usedPaths.Count >= MaxTokens)
                {
                    diagnostics.Add(Warn("TOKEN_CAP", path, $"more than {MaxTokens} tokens; the rest is ignored"));
                    return true;
                }

                Emit(path, TokenType(child, groupType), hasDtcgValue ? dtcgValue : legacyValue, design, usedPaths, diagnostics, loc);
            }
            else if (Walk(child, path, groupType, design, usedPaths, diagnostics, loc))
            {
                return true;
            }
        }

        return false;
    }

    private static void Emit(
        string path,
        string? type,
        JsonElement valueElement,
        JsonObject design,
        HashSet<string> usedPaths,
        List<ImportDiagnostic> diagnostics,
        string loc)
    {
        // Length first: hashing a megabyte-long inherited $type once per token is itself a CPU amplifier.
        if (type is null || type.Length > MaxTypeLength || !KnownTypes.Contains(type))
        {
            diagnostics.Add(Warn("UNKNOWN_TYPE", path, $"token has unsupported $type '{DesignKeys.Clip(type)}'"));
            return;
        }

        JsonNode? raw;
        try
        {
            raw = JsonNode.Parse(valueElement.GetRawText());
        }
        catch (JsonException)
        {
            diagnostics.Add(Warn("INVALID_VALUE", path, "token value is not valid JSON"));
            return;
        }

        if (raw is null)
        {
            diagnostics.Add(Warn("INVALID_VALUE", path, "token has no value"));
            return;
        }

        // Aliases are kept verbatim; resolving them is the validator's and the consumer's job.
        if (raw is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            string? text = value.GetValue<string>();
            if (DesignDocument.TryParseAlias(text, out _))
            {
                DesignDocument.SetToken(design, Dedup(path, usedPaths), type, JsonValue.Create(text), Provenance(loc), Confidence);
                return;
            }
        }

        JsonNode? normalized = TokenNormalizer.Normalize(type!, raw, out ImportDiagnostic? diagnostic);
        if (normalized is null)
        {
            diagnostics.Add(diagnostic is null
                ? Warn("INVALID_VALUE", path, "value could not be normalized")
                : diagnostic with { Path = path });
            return;
        }

        DesignDocument.SetToken(design, Dedup(path, usedPaths), type!, normalized, Provenance(loc), Confidence);
    }

    private static string? GroupType(JsonElement group, string? inherited)
    {
        if (group.ValueKind != JsonValueKind.Object)
            return inherited;

        if (group.TryGetProperty("$type", out JsonElement dtcgType) && dtcgType.ValueKind == JsonValueKind.String)
            return dtcgType.GetString();
        if (group.TryGetProperty("type", out JsonElement legacyType) && legacyType.ValueKind == JsonValueKind.String)
            return legacyType.GetString();

        return inherited;
    }

    private static string? TokenType(JsonElement token, string? inherited)
    {
        if (token.TryGetProperty("$type", out JsonElement dtcgType) && dtcgType.ValueKind == JsonValueKind.String)
            return dtcgType.GetString();
        if (token.TryGetProperty("type", out JsonElement legacyType) && legacyType.ValueKind == JsonValueKind.String)
            return legacyType.GetString();

        return inherited;
    }

    private static string NormalizeKey(string key)
    {
        var builder = new StringBuilder(key.Length);
        foreach (char c in key)
        {
            char lower = char.ToLowerInvariant(c);
            builder.Append(IsKeyCharacter(lower) ? lower : '-');
        }

        int start = 0;
        while (start < builder.Length && !IsKeyStart(builder[start]))
            start++;

        string normalized = builder.ToString(start, builder.Length - start);
        if (normalized.Length == 0)
            normalized = "token";
        if (normalized.Length > MaxKeyLength)
            normalized = normalized[..MaxKeyLength];

        return normalized;
    }

    /// <summary>True when bracket nesting (outside strings) goes deeper than <paramref name="maxDepth"/>.</summary>
    private static bool ExceedsDepth(ReadOnlySpan<byte> json, int maxDepth)
    {
        int depth = 0;
        bool inString = false;
        for (int i = 0; i < json.Length; i++)
        {
            byte b = json[i];
            if (inString)
            {
                if (b == (byte)'\\')
                    i++;
                else if (b == (byte)'"')
                    inString = false;

                continue;
            }

            if (b == (byte)'"')
                inString = true;
            else if (b is (byte)'{' or (byte)'[')
            {
                if (++depth > maxDepth)
                    return true;
            }
            else if (b is (byte)'}' or (byte)']')
                depth--;
        }

        return false;
    }

    private static string Dedup(string path, HashSet<string> usedPaths) => DesignKeys.Unique(usedPaths, path);

    private static TokenProvenance Provenance(string loc) => new("imported", "dtcg", null, loc);

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

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

    private static bool HasMarkers(JsonElement element)
    {
        bool hasDtcgValue = false;
        bool hasDtcgType = false;
        bool hasLegacyLeaf = false;
        ScanMarkers(element, ref hasDtcgValue, ref hasDtcgType, ref hasLegacyLeaf);
        return (hasDtcgValue && hasDtcgType) || hasLegacyLeaf;
    }

    private static void ScanMarkers(JsonElement element, ref bool hasDtcgValue, ref bool hasDtcgType, ref bool hasLegacyLeaf)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("$value", out _))
                    hasDtcgValue = true;
                if (element.TryGetProperty("$type", out _))
                    hasDtcgType = true;
                if (element.TryGetProperty("value", out _) && element.TryGetProperty("type", out _))
                    hasLegacyLeaf = true;

                if (hasDtcgValue && hasDtcgType)
                    return;

                foreach (JsonProperty property in element.EnumerateObject())
                    ScanMarkers(property.Value, ref hasDtcgValue, ref hasDtcgType, ref hasLegacyLeaf);

                break;

            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                    ScanMarkers(item, ref hasDtcgValue, ref hasDtcgType, ref hasLegacyLeaf);

                break;
        }
    }

    private static bool ContainsAscii(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
            return false;

        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return true;
        }

        return false;
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool IsKeyCharacter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '_' or '-';

    private static bool IsKeyStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

    private static ImportDiagnostic Error(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Error, code, path, message);

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);
}
