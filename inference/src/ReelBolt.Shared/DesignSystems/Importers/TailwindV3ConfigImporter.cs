using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Color;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads a Tailwind v3 <c>tailwind.config.{js,cjs,mjs,ts}</c> file as inert text and maps its
/// <c>theme</c> / <c>theme.extend</c> keys into the canonical design document. Nothing in the input
/// is ever executed: the config is understood only through <see cref="JsLiteralParser"/>, and any
/// value outside that restricted subset is skipped and reported. Pure and never throwing.
/// </summary>
public sealed class TailwindV3ConfigImporter : IDesignImporter
{
    private const double Confidence = 0.95;
    private const int MaxLocLength = 200;
    private const int MaxKeyLength = 48;

    /// <inheritdoc />
    public string Id => "tailwind-v3";

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string lower = path.ToLowerInvariant();
        if (lower.EndsWith(".css", StringComparison.Ordinal)
            || lower.EndsWith(".json", StringComparison.Ordinal)
            || lower.EndsWith(".md", StringComparison.Ordinal))
        {
            return false;
        }

        int separator = Math.Max(lower.LastIndexOf('/'), lower.LastIndexOf('\\'));
        string segment = separator >= 0 ? lower[(separator + 1)..] : lower;
        if (!segment.StartsWith("tailwind.config", StringComparison.Ordinal))
            return false;

        if (head.Length == 0)
            return false;

        int limit = Math.Min(head.Length, ImportLimits.HeadBytes);
        for (int i = 0; i < limit; i++)
        {
            if (head[i] == 0)
                return false;
        }

        return true;
    }

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files)
    {
        var design = DesignDocument.New();
        var sources = new List<ImportSourceRef>();
        var diagnostics = new List<ImportDiagnostic>();
        var dynamicPaths = new HashSet<string>(StringComparer.Ordinal);

        if (files is not null)
        {
            foreach (ImportFile? file in files)
            {
                if (file is null)
                    continue;

                try
                {
                    ReadOnlySpan<byte> content = file.Content.Span;
                    ReadOnlySpan<byte> head = content[..Math.Min(ImportLimits.HeadBytes, content.Length)];
                    if (!CanRead(file.Path, head))
                        continue;

                    sources.Add(new ImportSourceRef(file.Path, Id, Sha256(content), file.ProjectFileId));

                    if (content.Length > JsLiteralParser.MaxBytes)
                    {
                        diagnostics.Add(new ImportDiagnostic(
                            ImportDiagnosticSeverity.Error,
                            "IMPORT_TOO_LARGE",
                            file.Path,
                            $"tailwind-v3: {content.Length} bytes exceeds {JsLiteralParser.MaxBytes}"));
                        continue;
                    }

                    string text = Encoding.UTF8.GetString(content);
                    JsLiteralParseResult parsed = JsLiteralParser.Parse(text);
                    AddParserDiagnostics(diagnostics, file.Path, parsed.Issues, dynamicPaths);

                    if (parsed.Root is not JsonObject config)
                        continue;

                    new Mapper(design, file.Path, diagnostics).Map(config);
                }
                catch (Exception exception)
                {
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Error,
                        "IMPORT_INTERNAL",
                        file.Path ?? string.Empty,
                        exception.GetType().Name));
                }
            }
        }

        return new DesignImportResult(design, null, sources, diagnostics);
    }

    private static void AddParserDiagnostics(
        List<ImportDiagnostic> diagnostics,
        string file,
        IReadOnlyList<JsLiteralIssue> issues,
        HashSet<string> dynamicPaths)
    {
        if (issues is null)
            return;

        foreach (JsLiteralIssue issue in issues)
        {
            switch (issue.Code)
            {
                case "TAILWIND_DYNAMIC_VALUE":
                    if (dynamicPaths.Add(file + "\u0000" + issue.Path))
                    {
                        diagnostics.Add(new ImportDiagnostic(
                            ImportDiagnosticSeverity.Warn,
                            issue.Code,
                            file,
                            $"tailwind-v3: dynamic value at '{DesignKeys.Clip(issue.Path)}'"));
                    }

                    break;

                case "TOO_DEEP":
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        issue.Code,
                        file,
                        "tailwind-v3: config nesting exceeded the depth cap"));
                    break;

                case "TRUNCATED":
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        issue.Code,
                        file,
                        "tailwind-v3: config exceeded the token budget"));
                    break;

                case "PARSE_FAILED":
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Error,
                        issue.Code,
                        file,
                        "tailwind-v3: config could not be parsed"));
                    break;

                default:
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        issue.Code,
                        file,
                        "tailwind-v3: " + issue.Code));
                    break;
            }
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void SetTokenPreservingChildren(
        JsonObject design,
        string path,
        string type,
        JsonNode? value,
        TokenProvenance provenance,
        double confidence,
        string? original)
    {
        // A token and a group may share a path (Tailwind's bare `--spacing` beside `--spacing-4`).
        // DesignDocument.SetToken replaces the leaf, so keep any existing children alive.
        var children = new List<KeyValuePair<string, JsonNode?>>();
        JsonObject? existing = DesignDocument.FindToken(design, path);
        if (existing is not null)
        {
            foreach (KeyValuePair<string, JsonNode?> property in existing)
            {
                if (property.Key.Length > 0 && property.Key[0] != '$')
                    children.Add(new KeyValuePair<string, JsonNode?>(property.Key, property.Value?.DeepClone()));
            }
        }

        DesignDocument.SetToken(design, path, type, value, provenance, confidence, original: original);

        if (children.Count == 0)
            return;

        JsonObject? token = DesignDocument.FindToken(design, path);
        if (token is null)
            return;

        foreach (KeyValuePair<string, JsonNode?> child in children)
            token[child.Key] = child.Value;
    }

    /// <summary>Maps one config's <c>theme</c> and <c>theme.extend</c> into the design document.</summary>
    private sealed class Mapper
    {
        private readonly JsonObject _design;
        private readonly string _file;
        private readonly List<ImportDiagnostic> _diagnostics;
        private readonly Dictionary<string, SegmentMap> _segments = new(StringComparer.Ordinal);
        private readonly TokenProvenance _provenance;

        public Mapper(JsonObject design, string file, List<ImportDiagnostic> diagnostics)
        {
            _design = design;
            _file = file;
            _diagnostics = diagnostics;

            string loc = file;
            if (loc.Length > MaxLocLength)
                loc = loc[(loc.Length - MaxLocLength)..];

            _provenance = new TokenProvenance("static", "tailwind-v3", null, loc);
        }

        public void Map(JsonObject config)
        {
            if (config["theme"] is not JsonObject theme)
                return;

            ApplyThemeKeys(theme);

            if (theme["extend"] is JsonObject extend)
                ApplyThemeKeys(extend);
        }

        private void ApplyThemeKeys(JsonObject theme)
        {
            foreach (KeyValuePair<string, JsonNode?> property in theme)
            {
                switch (property.Key)
                {
                    case "colors" when property.Value is JsonObject colors:
                        MapColors(colors, "color");
                        break;

                    case "fontFamily" when property.Value is JsonObject families:
                        MapFontFamily(families);
                        break;

                    case "fontSize" when property.Value is JsonObject sizes:
                        MapFontSize(sizes);
                        break;

                    case "spacing" when property.Value is JsonObject spacing:
                        MapSpacing(spacing);
                        break;

                    case "borderRadius" when property.Value is JsonObject radii:
                        MapBorderRadius(radii);
                        break;

                    case "boxShadow" when property.Value is JsonObject shadows:
                        MapBoxShadow(shadows);
                        break;

                    case "transitionDuration" when property.Value is JsonObject durations:
                        MapTransitionDuration(durations);
                        break;

                    case "transitionTimingFunction" when property.Value is JsonObject easings:
                        MapTransitionTimingFunction(easings);
                        break;

                    case "screens":
                        break;
                }
            }
        }

        private void MapColors(JsonObject colors, string path)
        {
            foreach (KeyValuePair<string, JsonNode?> property in colors)
            {
                if (property.Key == "DEFAULT")
                {
                    if (TryColor(property.Value, out JsonNode? color))
                        Set(path, "color", color);
                    else
                        Warn(path, "colors.DEFAULT is not a colour");

                    continue;
                }

                string key = Segment(path, property.Key);
                if (key.Length == 0)
                    continue;

                string childPath = path + "." + key;

                if (property.Value is JsonObject nested)
                {
                    MapColors(nested, childPath);
                    continue;
                }

                if (TryColor(property.Value, out JsonNode? value))
                    Set(childPath, "color", value);
                else
                    Warn(childPath, "not a colour");
            }
        }

        private void MapFontFamily(JsonObject families)
        {
            const string parent = "typography.family";

            foreach (KeyValuePair<string, JsonNode?> property in families)
            {
                string key = Segment(parent, property.Key);
                if (key.Length == 0)
                    continue;

                string path = parent + "." + key;
                JsonNode? raw = TruncateFontStack(property.Value);
                JsonNode? value = TokenNormalizer.NormalizeFontFamily(raw, out ImportDiagnostic? diagnostic);
                if (value is null)
                {
                    Warn(path, diagnostic?.Message ?? "invalid font family");
                    continue;
                }

                Set(path, "fontFamily", value);
            }
        }

        private void MapFontSize(JsonObject sizes)
        {
            const string parent = "typography.size";

            foreach (KeyValuePair<string, JsonNode?> property in sizes)
            {
                string key = Segment(parent, property.Key);
                if (key.Length == 0)
                    continue;

                string sizePath = parent + "." + key;
                string lineHeightPath = "typography.lineHeight." + key;
                JsonNode? sizeRaw = property.Value;
                JsonNode? lineHeightRaw = null;

                if (property.Value is JsonArray array && array.Count > 0)
                {
                    sizeRaw = array[0];
                    if (array.Count > 1
                        && array[1] is JsonObject options
                        && options.TryGetPropertyValue("lineHeight", out JsonNode? lineHeight)
                        && lineHeight is not null)
                    {
                        lineHeightRaw = lineHeight;
                    }
                }

                JsonNode? size = TokenNormalizer.NormalizeDimension(sizeRaw, out ImportDiagnostic? diagnostic);
                if (size is null)
                    Warn(sizePath, diagnostic?.Message ?? "invalid font size");
                else
                    Set(sizePath, "dimension", size);

                if (lineHeightRaw is not null)
                    EmitLineHeight(lineHeightPath, lineHeightRaw);
            }
        }

        private void MapSpacing(JsonObject spacing) => MapDimensions(spacing, "spacing");

        private void MapBorderRadius(JsonObject radii) => MapDimensions(radii, "radius");

        private void MapDimensions(JsonObject values, string parent)
        {
            foreach (KeyValuePair<string, JsonNode?> property in values)
            {
                string key = Segment(parent, property.Key);
                if (key.Length == 0)
                    continue;

                string path = parent + "." + key;
                JsonNode? value = TokenNormalizer.NormalizeDimension(property.Value, out ImportDiagnostic? diagnostic);
                if (value is null)
                {
                    Warn(path, diagnostic?.Message ?? "invalid dimension");
                    continue;
                }

                Set(path, "dimension", value);
            }
        }

        private void MapBoxShadow(JsonObject shadows)
        {
            const string parent = "shadow";

            foreach (KeyValuePair<string, JsonNode?> property in shadows)
            {
                string key = Segment(parent, property.Key);
                if (key.Length == 0)
                    continue;

                string path = parent + "." + key;
                JsonNode? value = TokenNormalizer.NormalizeShadow(property.Value, out ImportDiagnostic? diagnostic);
                if (value is null)
                {
                    Warn(path, diagnostic?.Message ?? "invalid shadow");
                    continue;
                }

                Set(path, "shadow", value);
            }
        }

        private void MapTransitionDuration(JsonObject durations)
        {
            const string parent = "motion.duration";

            foreach (KeyValuePair<string, JsonNode?> property in durations)
            {
                string key = Segment(parent, property.Key);
                if (key.Length == 0)
                    continue;

                string path = parent + "." + key;
                JsonNode? value = TokenNormalizer.NormalizeDuration(property.Value, out ImportDiagnostic? diagnostic);
                if (value is null)
                {
                    Warn(path, diagnostic?.Message ?? "invalid duration");
                    continue;
                }

                Set(path, "duration", value);
            }
        }

        private void MapTransitionTimingFunction(JsonObject easings)
        {
            const string parent = "motion.easing";

            foreach (KeyValuePair<string, JsonNode?> property in easings)
            {
                string key = Segment(parent, property.Key);
                if (key.Length == 0)
                    continue;

                string path = parent + "." + key;
                JsonNode? curve = BuildCubicBezier(property.Value);
                if (curve is null)
                {
                    Warn(path, "easing is not a cubic-bezier or a known keyword");
                    continue;
                }

                JsonNode? value = TokenNormalizer.NormalizeCubicBezier(curve, out ImportDiagnostic? diagnostic);
                if (value is null)
                {
                    Warn(path, diagnostic?.Message ?? "invalid cubic bezier");
                    continue;
                }

                Set(path, "cubicBezier", value);
            }
        }

        private void EmitLineHeight(string path, JsonNode? raw)
        {
            if (IsUnitless(raw))
            {
                JsonNode? number = TokenNormalizer.NormalizeNumber(raw, out ImportDiagnostic? numberDiagnostic);
                if (number is null)
                {
                    Warn(path, numberDiagnostic?.Message ?? "invalid line height");
                    return;
                }

                Set(path, "number", number);
                return;
            }

            JsonNode? dimension = TokenNormalizer.NormalizeDimension(raw, out ImportDiagnostic? dimensionDiagnostic);
            if (dimension is null)
            {
                Warn(path, dimensionDiagnostic?.Message ?? "invalid line height");
                return;
            }

            Set(path, "dimension", dimension);
        }

        private void Set(string path, string type, JsonNode? value) =>
            SetTokenPreservingChildren(_design, path, type, value, _provenance, Confidence, original: null);

        private void Warn(string path, string message) =>
            _diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Warn,
                "INVALID_VALUE",
                _file,
                $"tailwind-v3 {DesignKeys.Clip(path)}: {message}"));

        /// <summary>
        /// Normalizes one theme key. Collisions under the same parent get a <c>-2</c>, <c>-3</c> suffix
        /// so two raw keys never overwrite each other.
        /// </summary>
        private string Segment(string parent, string raw)
        {
            if (!_segments.TryGetValue(parent, out SegmentMap? segments))
            {
                segments = new SegmentMap();
                _segments[parent] = segments;
            }

            if (segments.ByRaw.TryGetValue(raw, out string? existing))
                return existing;

            string candidate = NormalizeKey(raw);
            if (candidate.Length == 0)
            {
                segments.ByRaw[raw] = string.Empty;
                return string.Empty;
            }

            string unique = candidate;
            if (!segments.Used.Add(unique))
            {
                // Resume from the last suffix handed out for this candidate so n collisions cost O(n), not O(n^2).
                segments.NextSuffix.TryGetValue(candidate, out int suffix);
                if (suffix < 2)
                    suffix = 2;

                do
                {
                    unique = candidate + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                    suffix++;
                }
                while (!segments.Used.Add(unique));

                segments.NextSuffix[candidate] = suffix;
            }

            segments.ByRaw[raw] = unique;
            return unique;
        }

        private sealed class SegmentMap
        {
            public Dictionary<string, string> ByRaw { get; } = new(StringComparer.Ordinal);

            public HashSet<string> Used { get; } = new(StringComparer.Ordinal);

            public Dictionary<string, int> NextSuffix { get; } = new(StringComparer.Ordinal);
        }

        private static string NormalizeKey(string raw)
        {
            var builder = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                char lower = char.ToLowerInvariant(c);
                builder.Append(IsKeyCharacter(lower) ? lower : '-');
            }

            int start = 0;
            while (start < builder.Length && !IsKeyStart(builder[start]))
                start++;

            if (start >= builder.Length)
                return string.Empty;

            string result = builder.ToString(start, builder.Length - start);
            return result.Length > MaxKeyLength ? result[..MaxKeyLength] : result;
        }

        private static bool IsKeyCharacter(char c) =>
            (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c is '_' or '-';

        private static bool IsKeyStart(char c) => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

        private static bool TryColor(JsonNode? raw, out JsonNode? value)
        {
            value = null;
            if (raw is JsonValue jsonValue
                && jsonValue.GetValueKind() == JsonValueKind.String
                && ColorConvert.TryToSrgbHex(jsonValue.GetValue<string>(), out string hex, out _))
            {
                value = new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" };
                return true;
            }

            return false;
        }

        private static JsonNode? TruncateFontStack(JsonNode? raw)
        {
            if (raw is not JsonArray array || array.Count <= 5)
                return raw;

            var result = new JsonArray();
            for (int i = 0; i < 5; i++)
                result.Add(array[i]?.DeepClone());

            return result;
        }

        private static bool IsUnitless(JsonNode? raw)
        {
            if (raw is not JsonValue value)
                return false;

            if (value.GetValueKind() == JsonValueKind.Number)
                return true;

            if (value.GetValueKind() != JsonValueKind.String)
                return false;

            string text = value.GetValue<string>().Trim();
            if (text.Length == 0 || text.EndsWith('%'))
                return false;

            foreach (char c in text)
            {
                if (char.IsLetter(c))
                    return false;
            }

            return true;
        }

        private static JsonNode? BuildCubicBezier(JsonNode? raw)
        {
            if (raw is not JsonValue value || value.GetValueKind() != JsonValueKind.String)
                return null;

            string text = value.GetValue<string>().Trim();
            double[]? numbers = text.ToLowerInvariant() switch
            {
                "linear" => [0.0, 0.0, 1.0, 1.0],
                "ease" => [0.25, 0.1, 0.25, 1.0],
                "ease-in" => [0.42, 0.0, 1.0, 1.0],
                "ease-out" => [0.0, 0.0, 0.58, 1.0],
                "ease-in-out" => [0.42, 0.0, 0.58, 1.0],
                _ => ParseCubicBezier(text),
            };

            if (numbers is null)
                return null;

            var result = new JsonArray();
            foreach (double number in numbers)
                result.Add(JsonValue.Create(number));

            return result;
        }

        private static double[]? ParseCubicBezier(string text)
        {
            if (!text.StartsWith("cubic-bezier", StringComparison.OrdinalIgnoreCase))
                return null;

            int open = text.IndexOf('(');
            int close = text.LastIndexOf(')');
            if (open < 0 || close <= open)
                return null;

            string[] parts = text[(open + 1)..close].Split(',');
            if (parts.Length != 4)
                return null;

            var numbers = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])
                    || !double.IsFinite(numbers[i]))
                {
                    return null;
                }
            }

            return numbers;
        }
    }
}
