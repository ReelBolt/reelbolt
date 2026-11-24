using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Color;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads Tokens Studio (Figma plugin) JSON exports — a single file whose top-level keys are sets, and
/// multi-file exports (one file per set plus <c>$themes.json</c>/<c>$metadata.json</c>) — and returns a
/// canonical <see cref="DesignDocument"/>.
///
/// <para>
/// Pure and total: the input is inert data, nothing is evaluated, no regular expression runs, and
/// every failure becomes an <see cref="ImportDiagnostic"/> rather than an exception. Only the path and
/// the first <see cref="ImportLimits.HeadBytes"/> bytes are inspected to claim a file.
/// </para>
///
/// <para>
/// Known limit: an export whose first 4096 bytes show none of the markers (<c>"$themes"</c>,
/// <c>"tokenSetOrder"</c>, or a Tokens-Studio-only <c>"type"</c> such as <c>sizing</c>,
/// <c>boxShadow</c> or <c>fontFamilies</c>) is not claimed here and falls through to the DTCG/legacy
/// importer, which reads the dialect as inert data too.
/// </para>
/// </summary>
public sealed class TokensStudioImporter : IDesignImporter
{
    /// <inheritdoc />
    public string Id => "tokens-studio";

    private const double Confidence = 0.9;
    private const int MaxDescriptionLength = 200;
    private const int MaxLocLength = 200;
    private const int MaxOrigLength = 64;
    private const int MaxPathLength = 160;
    private const int MaxDepth = 6;
    private const int MaxTokens = 1500;
    private const int MaxAliasDepth = 8;
    private const int MaxShadowEntries = 4;
    private const double MaxDimension = 10000;
    private const double MaxNumber = 1e6;

    private static readonly HashSet<string> TokensStudioOnlyTypes = new(StringComparer.Ordinal)
    {
        "sizing",
        "spacing",
        "borderRadius",
        "borderWidth",
        "opacity",
        "fontFamilies",
        "fontWeights",
        "fontSizes",
        "lineHeights",
        "letterSpacing",
        "paragraphSpacing",
        "textCase",
        "textDecoration",
        "boxShadow",
        "composition",
    };

    private static readonly HashSet<string> DroppedFirstSegments = new(StringComparer.Ordinal)
    {
        "color",
        "colors",
        "spacing",
        "space",
        "radius",
        "radii",
        "borderradius",
        "sizing",
        "size",
        "sizes",
        "fontfamilies",
        "fontfamily",
        "fonts",
        "font",
        "fontsizes",
        "fontweights",
        "lineheights",
        "letterspacing",
        "shadow",
        "shadows",
        "boxshadow",
        "typography",
        "opacity",
    };

    private static readonly Dictionary<string, Destination> Destinations = new(StringComparer.Ordinal)
    {
        ["color"] = new("color", "color"),
        ["spacing"] = new("dimension", "spacing"),
        ["sizing"] = new("dimension", "component.sizing"),
        ["borderRadius"] = new("dimension", "radius"),
        ["borderWidth"] = new("dimension", "component.border-width"),
        ["opacity"] = new("number", "component.opacity"),
        ["fontFamilies"] = new("fontFamily", "typography.family"),
        ["fontWeights"] = new("fontWeight", "typography.weight"),
        ["fontSizes"] = new("dimension", "typography.size"),
        ["lineHeights"] = new("number", "typography.line-height"),
        ["letterSpacing"] = new("dimension", "typography.letter-spacing"),
        ["boxShadow"] = new("shadow", "shadow"),
        ["typography"] = new("typography", "typography.style"),
        ["dimension"] = new("dimension", "component.dimension"),
        ["number"] = new("number", "component.number"),
    };

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head) => LooksLikeTokensStudio(path, head);

    /// <summary>
    /// True when the file name is <c>$themes.json</c>/<c>$metadata.json</c>, or when the head is a
    /// JSON object carrying a Tokens-Studio marker. Case-insensitive on the file name.
    /// </summary>
    public static bool LooksLikeTokensStudio(string path, ReadOnlySpan<byte> head)
    {
        if (!string.IsNullOrEmpty(path))
        {
            string name = Path.GetFileName(path);
            if (name.Equals("$themes.json", StringComparison.OrdinalIgnoreCase)
                || name.Equals("$metadata.json", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (head.Length == 0)
            return false;

        int limit = Math.Min(head.Length, ImportLimits.HeadBytes);
        string text = Encoding.UTF8.GetString(head[..limit]);
        if (text.Length > 0 && text[0] == '\uFEFF')
            text = text[1..];

        string trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{')
            return false;

        return text.Contains("\"$themes\"", StringComparison.Ordinal)
            || text.Contains("\"tokenSetOrder\"", StringComparison.Ordinal)
            || HasTokensStudioType(text);
    }

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files)
    {
        var context = new Context();

        try
        {
            if (files is not null)
            {
                var parsed = new List<ParsedFile>();
                int index = 0;

                foreach (ImportFile? file in files)
                {
                    if (file is null)
                        continue;

                    int fileIndex = index++;
                    string path = file.Path ?? string.Empty;
                    context.Files[fileIndex] = new FileRecord(path, file.ProjectFileId, string.Empty);

                    if (file.Content.Length > ImportLimits.JsonMaxBytes)
                    {
                        context.Diagnostics.Add(Error(
                            "IMPORT_TOO_LARGE",
                            path,
                            $"file is {file.Content.Length} bytes, over the {ImportLimits.JsonMaxBytes} byte limit"));
                        continue;
                    }

                    JsonObject? root = ParseObject(file, path, context.Diagnostics);
                    if (root is null)
                        continue;

                    string name = Path.GetFileName(path);
                    var parsedFile = new ParsedFile
                    {
                        Index = fileIndex,
                        Path = path,
                        Root = root,
                        IsThemes = name.Equals("$themes.json", StringComparison.OrdinalIgnoreCase),
                        IsMetadata = name.Equals("$metadata.json", StringComparison.OrdinalIgnoreCase),
                        HasThemes = root.ContainsKey("$themes"),
                        TokenSetOrder = ReadTokenSetOrder(root),
                        Sha256 = Sha256(file.Content.Span),
                    };

                    context.Files[fileIndex] = new FileRecord(path, file.ProjectFileId, parsedFile.Sha256);
                    parsed.Add(parsedFile);

                    if (parsedFile.IsThemes || parsedFile.HasThemes)
                    {
                        context.Diagnostics.Add(Info(
                            "THEME_IGNORED",
                            path,
                            "Tokens Studio themes are not interpreted"));
                    }
                }

                BuildSets(context, parsed);
            }
        }
        catch (Exception exception)
        {
            context.Diagnostics.Add(Error("IMPORT_TOO_LARGE", string.Empty, exception.GetType().Name));
        }

        List<RawToken> deferred = AssignPaths(context);
        EmitConcrete(context);
        EmitReferences(context, deferred);
        ApplyDarkModes(context);

        bool produced = context.TokenCount > 0;
        if (!produced)
        {
            context.Diagnostics.Add(Warn(
                "NO_TOKENS_RECOGNIZED",
                string.Empty,
                "no Tokens Studio tokens were recognized"));
        }

        var sources = new List<ImportSourceRef>(context.ContributingFiles.Count);
        foreach (int fileIndex in context.ContributingFiles)
        {
            FileRecord record = context.Files[fileIndex];
            sources.Add(new ImportSourceRef(record.Path, "tokens-studio", record.Sha256, record.ProjectFileId));
        }

        return new DesignImportResult(produced ? context.Root : null, null, sources, context.Diagnostics);
    }

    private static JsonObject? ParseObject(ImportFile file, string path, List<ImportDiagnostic> diagnostics)
    {
        if (file.Content.Length == 0)
        {
            diagnostics.Add(Error("IMPORT_TOO_LARGE", path, "input is empty"));
            return null;
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
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Error("IMPORT_TOO_LARGE", path, "the JSON root must be an object"));
                return null;
            }

            int nodes = CountNodes(document.RootElement);
            if (nodes > ImportLimits.JsonMaxNodes)
            {
                diagnostics.Add(Error(
                    "IMPORT_TOO_LARGE",
                    path,
                    $"document has {nodes} nodes, over the {ImportLimits.JsonMaxNodes} limit"));
                return null;
            }

            try
            {
                return JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject;
            }
            catch (JsonException exception)
            {
                diagnostics.Add(Error("IMPORT_TOO_LARGE", path, "document could not be read: " + exception.Message));
                return null;
            }
        }
    }

    private static void BuildSets(Context context, List<ParsedFile> parsed)
    {
        List<ParsedFile> tokenFiles = parsed.Where(file => !file.IsMetadata && !file.IsThemes).ToList();
        bool multiFile = parsed.Any(file => file.IsMetadata) || tokenFiles.Count > 1;

        List<string> order = parsed
            .Where(file => file.IsMetadata)
            .SelectMany(file => file.TokenSetOrder ?? [])
            .ToList();
        if (order.Count == 0)
        {
            order = parsed
                .Where(file => !file.IsMetadata && !file.IsThemes)
                .SelectMany(file => file.TokenSetOrder ?? [])
                .ToList();
        }

        var sets = new List<SetEntry>();
        if (multiFile)
        {
            foreach (ParsedFile file in tokenFiles)
            {
                string setName = Path.GetFileNameWithoutExtension(file.Path);
                sets.Add(new SetEntry(setName, file.Root, file.Index, Loc(file.Path)));
            }
        }
        else
        {
            foreach (ParsedFile file in tokenFiles)
            {
                foreach (KeyValuePair<string, JsonNode?> property in file.Root)
                {
                    if (property.Key.Length > 0 && property.Key[0] == '$')
                        continue;
                    if (property.Value is not JsonObject setContent)
                        continue;

                    sets.Add(new SetEntry(property.Key, setContent, file.Index, Loc(file.Path)));
                }
            }
        }

        // Index the sets by name once so ordering is linear in sets + order entries.
        var byName = new Dictionary<string, List<SetEntry>>(StringComparer.Ordinal);
        foreach (SetEntry set in sets)
        {
            if (!byName.TryGetValue(set.Name, out List<SetEntry>? sameName))
            {
                sameName = new List<SetEntry>();
                byName[set.Name] = sameName;
            }

            sameName.Add(set);
        }

        var ordered = new List<SetEntry>(sets.Count);
        var placed = new HashSet<SetEntry>(ReferenceEqualityComparer.Instance);
        foreach (string name in order)
        {
            if (!byName.TryGetValue(name, out List<SetEntry>? matches))
                continue;

            foreach (SetEntry set in matches)
            {
                if (placed.Add(set))
                    ordered.Add(set);
            }
        }

        foreach (SetEntry set in sets)
        {
            if (placed.Add(set))
                ordered.Add(set);
        }

        foreach (SetEntry set in ordered.Where(entry => !IsDark(entry.Name)))
            CollectSet(context, set);

        foreach (SetEntry set in ordered.Where(entry => IsDark(entry.Name)))
            CollectSet(context, set);
    }

    private static void CollectSet(Context context, SetEntry set)
    {
        WalkGroup(context, set.Content, new List<string>(), set, IsDark(set.Name));
    }

    private static void WalkGroup(Context context, JsonObject node, List<string> segments, SetEntry set, bool isDark)
    {
        foreach (KeyValuePair<string, JsonNode?> property in node)
        {
            string key = property.Key;
            if (key.Length > 0 && key[0] == '$')
                continue;
            if (key is "value" or "type")
                continue;
            if (property.Value is not JsonObject child)
                continue;

            if (child.ContainsKey("value") || child.ContainsKey("$value"))
            {
                AddToken(context, child, segments, key, set, isDark);
            }
            else
            {
                segments.Add(key);
                WalkGroup(context, child, segments, set, isDark);
                segments.RemoveAt(segments.Count - 1);
            }
        }
    }

    private static void AddToken(
        Context context,
        JsonObject leaf,
        List<string> parent,
        string key,
        SetEntry set,
        bool isDark)
    {
        string? tsType = StringProp(leaf, "type") ?? StringProp(leaf, "$type");
        var segments = new List<string>(parent) { key };
        string rawPath = string.Join('.', segments);

        if (tsType is null || !Destinations.TryGetValue(tsType, out Destination destination))
        {
            context.Diagnostics.Add(Warn("UNKNOWN_TYPE", rawPath, $"token has unsupported type '{tsType ?? "(none)"}'"));
            return;
        }

        List<string> relativeSegments = new(segments);
        if (relativeSegments.Count > 1 && DroppedFirstSegments.Contains(relativeSegments[0].ToLowerInvariant()))
            relativeSegments.RemoveAt(0);

        string relative = string.Join('.', relativeSegments.Select(DesignKeys.Slug));
        string canonicalPath = relative.Length == 0 ? destination.Path : destination.Path + "." + relative;

        if (canonicalPath.Length > MaxPathLength || canonicalPath.Split('.').Length > MaxDepth)
        {
            context.Diagnostics.Add(Warn("UNSUPPORTED_VALUE", rawPath, "token path is too deep or too long"));
            return;
        }

        var token = new RawToken
        {
            FileIndex = set.FileIndex,
            Set = set.Name,
            IsDark = isDark,
            Loc = set.Loc,
            RawPath = rawPath,
            TsType = tsType,
            Destination = destination.Path,
            CanonicalType = destination.CanonicalType,
            CanonicalPath = canonicalPath,
            RawValue = GetRawValue(leaf),
            Description = StringProp(leaf, "description") ?? StringProp(leaf, "$description"),
        };

        if (isDark)
            context.DarkTokens.Add(token);
        else
            context.BaseTokens.Add(token);
    }

    private static List<RawToken> AssignPaths(Context context)
    {
        var deferred = new List<RawToken>();

        foreach (RawToken token in context.BaseTokens)
        {
            string path = token.CanonicalPath;
            if (context.UsedPaths.Contains(path))
            {
                if (context.PathOwner.TryGetValue(path, out string? owner)
                    && string.Equals(owner, token.Set, StringComparison.Ordinal))
                {
                    path = DesignKeys.Unique(context.UsedPaths, path);
                }
            }
            else
            {
                context.UsedPaths.Add(path);
            }

            context.PathOwner[path] = token.Set;
            token.MappedPath = path;
            context.RawByPath[token.RawPath] = token;

            if (IsReference(token.RawValue, out _))
                deferred.Add(token);
        }

        foreach (RawToken token in context.DarkTokens)
        {
            token.MappedPath = token.CanonicalPath;
            context.RawByPath[token.RawPath] = token;
        }

        return deferred;
    }

    private static void EmitConcrete(Context context)
    {
        foreach (RawToken token in context.BaseTokens)
        {
            if (IsReference(token.RawValue, out _))
                continue;

            JsonNode? value = ConvertValue(context, token, token.RawValue, out string? original, out ImportDiagnostic? diagnostic);
            if (value is null)
            {
                context.Diagnostics.Add(WithPath(diagnostic, token.RawPath, "value could not be converted"));
                continue;
            }

            EmitToken(context, token, value, original);
        }
    }

    private static void EmitReferences(Context context, List<RawToken> deferred)
    {
        foreach (RawToken token in deferred)
        {
            if (!IsReference(token.RawValue, out string? referencePath) || referencePath is null)
                continue;

            if (TryResolveTarget(context, referencePath, out RawToken? target)
                && target is not null
                && target.Emitted
                && string.Equals(target.CanonicalType, token.CanonicalType, StringComparison.Ordinal))
            {
                EmitToken(context, token, JsonValue.Create("{" + target.MappedPath + "}"), null);
                continue;
            }

            if (TryResolveRaw(context, referencePath, out JsonNode? concrete) && concrete is not null)
            {
                JsonNode? value = ConvertValue(context, token, concrete, out string? original, out ImportDiagnostic? diagnostic);
                if (value is not null)
                {
                    EmitToken(context, token, value, original);
                    continue;
                }

                context.Diagnostics.Add(WithPath(diagnostic, token.RawPath, "reference could not be converted"));
                continue;
            }

            context.Diagnostics.Add(Warn("UNSUPPORTED_VALUE", token.RawPath, "reference could not be resolved"));
        }
    }

    private static void ApplyDarkModes(Context context)
    {
        foreach (RawToken token in context.DarkTokens)
        {
            if (!context.EmittedByPath.TryGetValue(token.MappedPath, out RawToken? baseToken))
            {
                context.Diagnostics.Add(Info("MODE_TOKEN_SKIPPED", token.RawPath, "dark token has no base token"));
                continue;
            }

            JsonNode? concrete = token.RawValue;
            if (IsReference(concrete, out string? referencePath) && referencePath is not null)
                concrete = ResolveRaw(context, referencePath);

            if (concrete is null)
            {
                context.Diagnostics.Add(Info("MODE_TOKEN_SKIPPED", token.RawPath, "dark token value could not be read"));
                continue;
            }

            JsonNode? value = ConvertValue(context, token, concrete, out _, out _);
            if (value is null)
            {
                context.Diagnostics.Add(Info("MODE_TOKEN_SKIPPED", token.RawPath, "dark token value is not valid for its type"));
                continue;
            }

            JsonObject modes = EnsureObject(context.Root, "modes");
            JsonObject dark = EnsureObject(modes, "dark");
            dark[token.MappedPath] = value.DeepClone();
            context.ModeCount++;
            context.ContributingFiles.Add(token.FileIndex);
        }
    }

    private static void EmitToken(Context context, RawToken token, JsonNode? value, string? original)
    {
        if (context.TokenCount >= MaxTokens)
        {
            if (!context.Truncated)
            {
                context.Diagnostics.Add(Warn("TRUNCATED", token.RawPath, $"more than {MaxTokens} tokens; the rest is ignored"));
                context.Truncated = true;
            }

            return;
        }

        DesignDocument.SetToken(
            context.Root,
            token.MappedPath,
            token.CanonicalType,
            value,
            new TokenProvenance("imported", "tokens-studio", null, token.Loc),
            Confidence,
            false,
            original);

        if (!string.IsNullOrEmpty(token.Description))
        {
            string? description = DesignText.Sanitize(token.Description, MaxDescriptionLength);
            JsonObject? stored = DesignDocument.FindToken(context.Root, token.MappedPath);
            if (description is not null && stored is not null)
                stored["$description"] = description;
        }

        token.Emitted = true;
        token.CanonicalValue = value?.DeepClone();
        context.TokenCount++;
        context.EmittedByPath[token.MappedPath] = token;
        context.ContributingFiles.Add(token.FileIndex);
    }

    private static JsonNode? ConvertValue(
        Context context,
        RawToken token,
        JsonNode? raw,
        out string? original,
        out ImportDiagnostic? diagnostic)
    {
        original = null;
        diagnostic = null;

        if (string.Equals(token.TsType, "lineHeights", StringComparison.Ordinal))
        {
            JsonNode? lineHeight = ConvertLineHeight(raw, out string canonicalType, out diagnostic);
            if (lineHeight is not null)
                token.CanonicalType = canonicalType;
            return lineHeight;
        }

        return ConvertForType(token.TsType, token.CanonicalType, raw, path => ResolveRaw(context, path), out original, out diagnostic);
    }

    private static JsonNode? ConvertForType(
        string tsType,
        string canonicalType,
        JsonNode? raw,
        Func<string, JsonNode?> resolve,
        out string? original,
        out ImportDiagnostic? diagnostic)
    {
        original = null;
        diagnostic = null;

        if (IsBadExpression(raw, out string? reason))
        {
            diagnostic = Warn("UNSUPPORTED_VALUE", reason!);
            return null;
        }

        if (string.Equals(tsType, "opacity", StringComparison.Ordinal))
            return ConvertOpacity(raw, out diagnostic);

        switch (canonicalType)
        {
            case "color":
                return ConvertColor(raw, out diagnostic, out original);
            case "dimension":
                return ConvertDimension(raw, out diagnostic);
            case "fontFamily":
                return ConvertFontFamily(raw, out diagnostic);
            case "fontWeight":
                return ConvertFontWeight(raw, out diagnostic);
            case "number":
                return ConvertNumber(raw, out diagnostic);
            case "shadow":
                return ConvertShadow(raw, resolve, out diagnostic);
            case "typography":
                return ConvertTypography(raw, resolve, out diagnostic);
            default:
                diagnostic = Warn("UNSUPPORTED_VALUE", $"type '{canonicalType}' is not supported");
                return null;
        }
    }

    private static JsonNode? ConvertColor(JsonNode? raw, out ImportDiagnostic? diagnostic, out string? original)
    {
        diagnostic = null;
        original = null;

        string? text = raw switch
        {
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            JsonObject obj when obj["hex"] is JsonValue hexNode && hexNode.GetValueKind() == JsonValueKind.String => hexNode.GetValue<string>(),
            _ => null,
        };

        if (text is null || !ColorConvert.TryToSrgbHex(text, out string hex, out string? fromFunction))
        {
            diagnostic = Warn("UNSUPPORTED_VALUE", "colour could not be converted to sRGB");
            return null;
        }

        if (fromFunction is not null)
            original = fromFunction.Length > MaxOrigLength ? fromFunction[..MaxOrigLength] : fromFunction;

        return new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" };
    }

    private static JsonNode? ConvertDimension(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is JsonObject canonical
            && canonical["value"] is JsonValue
            && canonical["unit"] is JsonValue unitValue
            && unitValue.GetValueKind() == JsonValueKind.String)
        {
            string unit = unitValue.GetValue<string>();
            if (CanonicalJson.TryGetNumber(canonical["value"], out double canonicalNumber)
                && IsUnit(unit)
                && IsDimension(canonicalNumber))
            {
                return Dimension(canonicalNumber, unit);
            }
        }

        if (raw is JsonValue value)
        {
            if (value.GetValueKind() == JsonValueKind.Number)
            {
                if (CanonicalJson.TryGetNumber(raw, out double number) && IsDimension(number))
                    return Dimension(number, "px");

                diagnostic = Warn("UNSUPPORTED_VALUE", "dimension must be finite and within +/-10000");
                return null;
            }

            if (value.GetValueKind() == JsonValueKind.String)
            {
                string text = value.GetValue<string>().Trim();
                string unit = "px";

                if (text.EndsWith("%", StringComparison.Ordinal))
                {
                    unit = "%";
                    text = text[..^1];
                }
                else if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                {
                    text = text[..^2];
                }
                else if (text.EndsWith("rem", StringComparison.OrdinalIgnoreCase))
                {
                    unit = "rem";
                    text = text[..^3];
                }
                else if (text.EndsWith("em", StringComparison.OrdinalIgnoreCase))
                {
                    unit = "em";
                    text = text[..^2];
                }

                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                    && IsDimension(parsed))
                {
                    return Dimension(parsed, unit);
                }

                diagnostic = Warn("UNSUPPORTED_VALUE", "dimension must be a number or a length");
                return null;
            }
        }

        diagnostic = Warn("UNSUPPORTED_VALUE", "dimension must be a number or a length");
        return null;
    }

    private static JsonNode? ConvertFontFamily(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;
        var families = new List<string>();

        if (raw is JsonValue value && value.GetValueKind() == JsonValueKind.String)
        {
            foreach (string part in value.GetValue<string>().Split(','))
                AddFamily(part, families);
        }
        else if (raw is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is not JsonValue entry || entry.GetValueKind() != JsonValueKind.String)
                {
                    diagnostic = Warn("UNSUPPORTED_VALUE", "font family entries must be strings");
                    return null;
                }

                AddFamily(entry.GetValue<string>(), families);
            }
        }
        else
        {
            diagnostic = Warn("UNSUPPORTED_VALUE", "font family must be a string or an array");
            return null;
        }

        if (families.Count is < 1 or > 5)
        {
            diagnostic = Warn("UNSUPPORTED_VALUE", "font family must hold between 1 and 5 safe names");
            return null;
        }

        var result = new JsonArray();
        foreach (string family in families)
            result.Add(family);

        return result;
    }

    private static void AddFamily(string? raw, List<string> families)
    {
        string? clean = DesignText.Sanitize(raw?.Trim().Trim('"', '\''), 80);
        if (clean is not null && DesignText.IsSafeFontFamily(clean))
            families.Add(clean);
    }

    private static JsonNode? ConvertFontWeight(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is JsonValue value)
        {
            if (value.GetValueKind() == JsonValueKind.String)
            {
                string text = value.GetValue<string>().Trim();
                int? keyword = text.ToLowerInvariant().Replace(" ", string.Empty).Replace("-", string.Empty) switch
                {
                    "thin" => 100,
                    "extralight" => 200,
                    "light" => 300,
                    "normal" or "regular" => 400,
                    "medium" => 500,
                    "semibold" => 600,
                    "bold" => 700,
                    "extrabold" => 800,
                    "black" or "heavy" => 900,
                    _ => null,
                };

                if (keyword is not null)
                    return JsonValue.Create(keyword.Value);

                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedText))
                {
                    diagnostic = Warn("UNSUPPORTED_VALUE", "font weight must be a keyword or a number");
                    return null;
                }

                return Weight(parsedText, out diagnostic);
            }

            if (value.GetValueKind() == JsonValueKind.Number)
            {
                if (!CanonicalJson.TryGetNumber(raw, out double number))
                {
                    diagnostic = Warn("UNSUPPORTED_VALUE", "font weight must be a finite number");
                    return null;
                }

                return Weight(number, out diagnostic);
            }
        }

        diagnostic = Warn("UNSUPPORTED_VALUE", "font weight must be a keyword or a number");
        return null;
    }

    private static JsonNode? Weight(double number, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (number < 1 || number > 1000 || number != Math.Floor(number) || !double.IsFinite(number))
        {
            diagnostic = Warn("UNSUPPORTED_VALUE", "font weight must be a whole number between 1 and 1000");
            return null;
        }

        return JsonValue.Create((int)number);
    }

    private static JsonNode? ConvertNumber(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is JsonValue value)
        {
            if (value.GetValueKind() == JsonValueKind.Number)
            {
                if (CanonicalJson.TryGetNumber(raw, out double number) && IsNumber(number))
                    return JsonValue.Create(number);
            }
            else if (value.GetValueKind() == JsonValueKind.String
                && double.TryParse(value.GetValue<string>().Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && IsNumber(parsed))
            {
                return JsonValue.Create(parsed);
            }
        }

        diagnostic = Warn("UNSUPPORTED_VALUE", "number must be finite and within +/-1000000");
        return null;
    }

    private static JsonNode? ConvertOpacity(JsonNode? raw, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is JsonValue value)
        {
            if (value.GetValueKind() == JsonValueKind.Number)
                return ConvertNumber(raw, out diagnostic);

            if (value.GetValueKind() == JsonValueKind.String)
            {
                string text = value.GetValue<string>().Trim();
                if (text.EndsWith("%", StringComparison.Ordinal)
                    && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                {
                    double ratio = percent / 100.0;
                    if (IsNumber(ratio))
                        return JsonValue.Create(ratio);
                }

                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain) && IsNumber(plain))
                    return JsonValue.Create(plain);
            }
        }

        diagnostic = Warn("UNSUPPORTED_VALUE", "opacity must be a number or a percentage");
        return null;
    }

    private static JsonNode? ConvertLineHeight(JsonNode? raw, out string canonicalType, out ImportDiagnostic? diagnostic)
    {
        canonicalType = "number";
        diagnostic = null;

        if (IsBadExpression(raw, out string? reason))
        {
            diagnostic = Warn("UNSUPPORTED_VALUE", reason!);
            return null;
        }

        if (raw is JsonObject obj && obj["unit"] is JsonValue)
        {
            canonicalType = "dimension";
            return ConvertDimension(raw, out diagnostic);
        }

        if (raw is JsonValue value)
        {
            if (value.GetValueKind() == JsonValueKind.Number)
                return ConvertNumber(raw, out diagnostic);

            if (value.GetValueKind() == JsonValueKind.String)
            {
                string text = value.GetValue<string>().Trim();

                if (text.EndsWith("px", StringComparison.OrdinalIgnoreCase)
                    || text.EndsWith("rem", StringComparison.OrdinalIgnoreCase)
                    || text.EndsWith("em", StringComparison.OrdinalIgnoreCase))
                {
                    canonicalType = "dimension";
                    return ConvertDimension(raw, out diagnostic);
                }

                if (text.EndsWith("%", StringComparison.Ordinal)
                    && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
                {
                    double ratio = percent / 100.0;
                    if (IsNumber(ratio))
                        return JsonValue.Create(ratio);
                }

                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && IsNumber(parsed))
                    return JsonValue.Create(parsed);
            }
        }

        diagnostic = Warn("UNSUPPORTED_VALUE", "line height must be a number, a percentage or a length");
        return null;
    }

    private static JsonNode? ConvertShadow(JsonNode? raw, Func<string, JsonNode?> resolve, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is JsonArray array)
        {
            if (array.Count is < 1 or > MaxShadowEntries)
            {
                diagnostic = Warn("UNSUPPORTED_VALUE", $"a shadow array must hold between 1 and {MaxShadowEntries} entries");
                return null;
            }

            var shadows = new JsonArray();
            foreach (JsonNode? item in array)
            {
                if (item is not JsonObject entry)
                {
                    diagnostic = Warn("UNSUPPORTED_VALUE", "shadow array entries must be objects");
                    return null;
                }

                JsonNode? normalized = ShadowEntry(entry, resolve, out diagnostic);
                if (normalized is null)
                    return null;

                shadows.Add(normalized);
            }

            return shadows;
        }

        if (raw is JsonObject single)
        {
            JsonNode? normalized = ShadowEntry(single, resolve, out diagnostic);
            return normalized is null ? null : new JsonArray { normalized };
        }

        diagnostic = Warn("UNSUPPORTED_VALUE", "shadow must be an object or an array");
        return null;
    }

    private static JsonNode? ShadowEntry(JsonObject entry, Func<string, JsonNode?> resolve, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        JsonNode? colorNode = entry["color"];
        if (IsReference(colorNode, out string? colorPath) && colorPath is not null)
            colorNode = resolve(colorPath);

        JsonNode? color = ConvertColor(colorNode, out ImportDiagnostic? colorDiagnostic, out _);
        if (color is null)
        {
            diagnostic = colorDiagnostic ?? Warn("UNSUPPORTED_VALUE", "shadow entry has no usable colour");
            return null;
        }

        var result = new JsonObject { ["color"] = color };
        foreach ((string source, string target) in ShadowDimensions)
        {
            if (!entry.TryGetPropertyValue(source, out JsonNode? dimensionNode) || dimensionNode is null)
                continue;

            if (IsReference(dimensionNode, out string? dimensionPath) && dimensionPath is not null)
                dimensionNode = resolve(dimensionPath);

            JsonNode? dimension = ConvertDimension(dimensionNode, out ImportDiagnostic? dimensionDiagnostic);
            if (dimension is null)
            {
                diagnostic = dimensionDiagnostic ?? Warn("UNSUPPORTED_VALUE", "shadow offset must be a length");
                return null;
            }

            result[target] = dimension;
        }

        if (entry["type"] is JsonValue typeValue
            && typeValue.GetValueKind() == JsonValueKind.String
            && typeValue.GetValue<string>().Equals("innerShadow", StringComparison.OrdinalIgnoreCase))
        {
            result["inset"] = true;
        }
        else if (entry["inset"] is JsonValue insetValue
            && (insetValue.GetValueKind() == JsonValueKind.True || insetValue.GetValueKind() == JsonValueKind.False))
        {
            result["inset"] = JsonValue.Create(insetValue.GetValue<bool>());
        }

        return result;
    }

    private static readonly (string Source, string Target)[] ShadowDimensions =
    [
        ("offsetX", "offsetX"),
        ("x", "offsetX"),
        ("offsetY", "offsetY"),
        ("y", "offsetY"),
        ("blur", "blur"),
        ("spread", "spread"),
    ];

    private static JsonNode? ConvertTypography(JsonNode? raw, Func<string, JsonNode?> resolve, out ImportDiagnostic? diagnostic)
    {
        diagnostic = null;

        if (raw is not JsonObject obj)
        {
            diagnostic = Warn("UNSUPPORTED_VALUE", "typography must be an object");
            return null;
        }

        var result = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> property in obj)
        {
            JsonNode? member = property.Value;
            if (IsReference(member, out string? memberPath) && memberPath is not null)
                member = resolve(memberPath);

            if (member is null)
                continue;

            JsonNode? converted = property.Key switch
            {
                "fontFamily" => ConvertFontFamily(member, out _),
                "fontWeight" => ConvertFontWeight(member, out _),
                "fontSize" => ConvertDimension(member, out _),
                "letterSpacing" => ConvertDimension(member, out _),
                "lineHeight" => ConvertLineHeight(member, out _, out _),
                _ => null,
            };

            if (converted is not null)
                result[property.Key] = converted;
        }

        if (result.Count == 0)
        {
            diagnostic = Warn("UNSUPPORTED_VALUE", "typography carries no recognizable members");
            return null;
        }

        return result;
    }

    private static bool IsBadExpression(JsonNode? raw, out string? reason)
    {
        reason = null;
        if (raw is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            return false;

        string text = value.GetValue<string>();
        if (text.Contains('{') || text.Contains('}'))
        {
            reason = "value is a math expression or a malformed reference";
            return true;
        }

        bool onlyMathCharacters = text.Length > 0;
        bool hasOperator = false;

        foreach (char c in text)
        {
            if ((c >= '0' && c <= '9') || char.IsWhiteSpace(c) || c is '.' or '(' or ')')
                continue;

            if (c is '+' or '-' or '*' or '/')
            {
                hasOperator = true;
                continue;
            }

            onlyMathCharacters = false;
            break;
        }

        if (onlyMathCharacters && hasOperator)
        {
            reason = "value is a math expression";
            return true;
        }

        return false;
    }

    private static bool IsReference(JsonNode? node, out string? path)
    {
        path = null;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
            return false;

        string text = value.GetValue<string>();
        if (text.Length < 3 || text[0] != '{' || text[^1] != '}')
            return false;

        string inner = text[1..^1];
        if (inner.Length == 0 || inner.Contains('{') || inner.Contains('}'))
            return false;

        path = inner;
        return true;
    }

    private static bool TryResolveTarget(Context context, string rawPath, out RawToken? target)
    {
        target = null;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string current = rawPath;

        for (int depth = 0; depth <= MaxAliasDepth; depth++)
        {
            if (!context.RawByPath.TryGetValue(current, out RawToken? token))
                return false;

            if (IsReference(token.RawValue, out string? next) && next is not null)
            {
                if (!visited.Add(current))
                    return false;

                current = next;
                continue;
            }

            target = token;
            return true;
        }

        return false;
    }

    private static bool TryResolveRaw(Context context, string rawPath, out JsonNode? value)
    {
        value = null;
        if (!TryResolveTarget(context, rawPath, out RawToken? target) || target is null)
            return false;

        value = target.RawValue;
        return value is not null;
    }

    private static JsonNode? ResolveRaw(Context context, string rawPath) =>
        TryResolveTarget(context, rawPath, out RawToken? target) ? target?.RawValue : null;

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent.TryGetPropertyValue(key, out JsonNode? node) && node is JsonObject existing)
            return existing;

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static bool HasTokensStudioType(string text)
    {
        int index = 0;
        while ((index = text.IndexOf("\"type\"", index, StringComparison.Ordinal)) >= 0)
        {
            int cursor = index + 6;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                cursor++;

            if (cursor >= text.Length || text[cursor] != ':')
            {
                index = cursor;
                continue;
            }

            cursor++;
            while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
                cursor++;

            if (cursor >= text.Length || text[cursor] != '"')
            {
                index = cursor;
                continue;
            }

            cursor++;
            int start = cursor;
            while (cursor < text.Length && text[cursor] != '"')
                cursor++;

            if (cursor <= text.Length)
            {
                string id = text[start..cursor];
                if (TokensStudioOnlyTypes.Contains(id))
                    return true;
            }

            index = cursor + 1;
        }

        return false;
    }

    private static List<string>? ReadTokenSetOrder(JsonObject root)
    {
        JsonArray? array = root["tokenSetOrder"] as JsonArray;
        if (array is null && root["$metadata"] is JsonObject metadata)
            array = metadata["tokenSetOrder"] as JsonArray;

        if (array is null)
            return null;

        var order = new List<string>();
        foreach (JsonNode? item in array)
        {
            if (item is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                order.Add(value.GetValue<string>());
        }

        return order.Count > 0 ? order : null;
    }

    private static bool IsDark(string name) => name.Contains("dark", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnit(string unit) => unit is "px" or "rem" or "em" or "%";

    private static bool IsDimension(double value) => double.IsFinite(value) && Math.Abs(value) <= MaxDimension;

    private static bool IsNumber(double value) => double.IsFinite(value) && Math.Abs(value) <= MaxNumber;

    private static string? StringProp(JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out JsonNode? node))
            return null;

        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    private static JsonNode? GetRawValue(JsonObject leaf) =>
        leaf.TryGetPropertyValue("$value", out JsonNode? dtcg) ? dtcg : leaf["value"];

    private static JsonObject Dimension(double value, string unit) =>
        new() { ["value"] = JsonValue.Create(value), ["unit"] = unit };

    private static string Loc(string path) => path.Length > MaxLocLength ? path[..MaxLocLength] : path;

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ImportDiagnostic WithPath(ImportDiagnostic? diagnostic, string path, string fallback)
    {
        ImportDiagnostic source = diagnostic ?? Warn("UNSUPPORTED_VALUE", fallback);
        return source with { Path = path };
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

    private static ImportDiagnostic Error(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Error, code, path, message);

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);

    private static ImportDiagnostic Warn(string code, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, string.Empty, message);

    private static ImportDiagnostic Info(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Info, code, path, message);

    private readonly record struct Destination(string CanonicalType, string Path);

    private sealed record SetEntry(string Name, JsonObject Content, int FileIndex, string Loc);

    private sealed record FileRecord(string Path, Guid? ProjectFileId, string Sha256);

    private sealed class ParsedFile
    {
        public int Index { get; init; }

        public string Path { get; init; } = string.Empty;

        public JsonObject Root { get; init; } = new();

        public bool IsThemes { get; init; }

        public bool IsMetadata { get; init; }

        public bool HasThemes { get; init; }

        public List<string>? TokenSetOrder { get; init; }

        public string Sha256 { get; init; } = string.Empty;
    }

    private sealed class RawToken
    {
        public int FileIndex { get; init; }

        public string Set { get; init; } = string.Empty;

        public bool IsDark { get; init; }

        public string Loc { get; init; } = string.Empty;

        public string RawPath { get; init; } = string.Empty;

        public string TsType { get; init; } = string.Empty;

        public string Destination { get; init; } = string.Empty;

        public string CanonicalType { get; set; } = string.Empty;

        public string CanonicalPath { get; init; } = string.Empty;

        public string MappedPath { get; set; } = string.Empty;

        public JsonNode? RawValue { get; init; }

        public string? Description { get; init; }

        public bool Emitted { get; set; }

        public JsonNode? CanonicalValue { get; set; }
    }

    private sealed class Context
    {
        public JsonObject Root { get; } = DesignDocument.New();

        public List<ImportDiagnostic> Diagnostics { get; } = new();

        public Dictionary<string, RawToken> RawByPath { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, RawToken> EmittedByPath { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> PathOwner { get; } = new(StringComparer.Ordinal);

        public Dictionary<int, FileRecord> Files { get; } = new();

        public SortedSet<int> ContributingFiles { get; } = new();

        public HashSet<string> UsedPaths { get; } = new(StringComparer.Ordinal);

        public List<RawToken> BaseTokens { get; } = new();

        public List<RawToken> DarkTokens { get; } = new();

        public int TokenCount { get; set; }

        public int ModeCount { get; set; }

        public bool Truncated { get; set; }
    }
}
