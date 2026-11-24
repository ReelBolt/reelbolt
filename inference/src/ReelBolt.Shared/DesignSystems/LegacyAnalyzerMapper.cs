using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.DesignSystems.Color;
using ReelBolt.Shared.DesignSystems.Importers;
using CodeDir = ReelBolt.Shared.Data.OutputSchemas.DirectoryInfo;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Maps the five legacy analyzer outputs onto the typed product map and the dynamic design
/// document. The mapping is deliberately lossy and total: every invalid item is dropped silently,
/// nothing throws, and a null input yields an empty document rather than a null one.
/// </summary>
public static class LegacyAnalyzerMapper
{
    private static readonly Regex TrailingVersion = new(
        @"\s*v?@?\s*\d+(\.\d+)*$",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    private static readonly (string Keyword, string Name)[] FrameworkKeywords =
    [
        ("next", "next"),
        ("remix", "remix"),
        ("nuxt", "nuxt"),
        ("svelte", "svelte"),
        ("angular", "angular"),
        ("astro", "astro"),
        ("vue", "vue"),
        ("vite", "vite"),
        ("react", "react"),
        ("express", "express"),
    ];

    /// <summary>
    /// Builds a product map from whichever analyzer outputs are present. Missing inputs leave their
    /// sections empty; the result is never null.
    /// </summary>
    public static ProductMapDocument ProductMapFrom(
        CodeStructureOutput? cs,
        DependencyAnalysisOutput? dep,
        ComponentInventoryOutput? comp,
        RouteAndApiOutput? ra)
    {
        (string frameworkName, string frameworkVersion) = ParseFramework(cs?.Framework);

        List<ProductMapDirectory> directories = [];
        foreach (CodeDir directory in (cs?.Directories ?? new List<CodeDir>()).Take(20))
        {
            if (directory is null)
                continue;
            directories.Add(new ProductMapDirectory(directory.Path ?? "", directory.Purpose ?? ""));
        }

        List<ProductMapEntryPoint> entryPoints = [];
        foreach (string entryPoint in (cs?.EntryPoints ?? new List<string>()).Take(30))
        {
            if (entryPoint is null)
                continue;
            entryPoints.Add(new ProductMapEntryPoint(entryPoint, ""));
        }

        List<ProductMapDependency> runtime = [];
        if (dep?.Dependencies is { } dependencies)
        {
            foreach (Dependency dependency in dependencies)
            {
                if (dependency is not null && dependency.IsCore)
                    runtime.Add(new ProductMapDependency(dependency.Name ?? "", dependency.Version ?? ""));
            }

            foreach (Dependency dependency in dependencies)
            {
                if (dependency is not null && !dependency.IsCore)
                    runtime.Add(new ProductMapDependency(dependency.Name ?? "", dependency.Version ?? ""));
            }
        }

        List<ProductMapDependency> dev = [];
        if (dep?.DevDependencies is { } devDependencies)
        {
            foreach (string name in devDependencies)
            {
                if (name is not null)
                    dev.Add(new ProductMapDependency(name, ""));
            }
        }

        List<ProductMapComponent> components = [];
        if (comp?.Components is { } inventory)
        {
            foreach (ComponentInfo component in inventory.Take(300))
            {
                if (component is null)
                    continue;

                List<string> keyProps = [];
                if (component.Props is { } props)
                {
                    foreach (PropInfo prop in props)
                    {
                        if (prop is not null && prop.Required)
                            keyProps.Add(prop.Name ?? "");
                    }

                    foreach (PropInfo prop in props)
                    {
                        if (prop is not null && !prop.Required)
                            keyProps.Add(prop.Name ?? "");
                    }
                }

                if (keyProps.Count > 8)
                    keyProps = keyProps.Take(8).ToList();

                components.Add(new ProductMapComponent(
                    component.Name ?? "",
                    component.FilePath ?? "",
                    component.Responsibility ?? "",
                    keyProps,
                    false,
                    "llm"));
            }
        }

        List<string> patterns = [];
        if (comp?.CommonPatterns is { } commonPatterns)
        {
            foreach (string pattern in commonPatterns.Take(12))
            {
                if (pattern is not null)
                    patterns.Add(pattern);
            }
        }

        List<ProductMapRoute> routes = [];
        if (ra?.ClientRoutes is { } clientRoutes)
        {
            foreach (RouteInfo route in clientRoutes.Take(300))
            {
                if (route is null)
                    continue;
                routes.Add(new ProductMapRoute(route.Path ?? "", "", "page", null, null, route.RequiresAuth, "llm"));
            }
        }

        if (ra?.ApiEndpoints is { } apiEndpoints)
        {
            foreach (ApiEndpoint endpoint in apiEndpoints.Take(300))
            {
                if (endpoint is null)
                    continue;
                routes.Add(new ProductMapRoute(endpoint.Path ?? "", "", "api", endpoint.Method, null, false, "llm"));
            }
        }

        var provenance = new Dictionary<string, ProductMapProvenance>(StringComparer.Ordinal);
        if (cs is not null)
        {
            provenance["framework"] = new ProductMapProvenance("llm", []);
            provenance["entryPoints"] = new ProductMapProvenance("llm", []);
        }

        if (dep is not null)
            provenance["dependencies"] = new ProductMapProvenance("llm", []);
        if (comp is not null)
            provenance["components"] = new ProductMapProvenance("llm", []);
        if (ra is not null)
            provenance["routes"] = new ProductMapProvenance("llm", []);

        return new ProductMapDocument(
            ProductMapDocument.SchemaId,
            null,
            new ProductMapFramework(
                frameworkName,
                frameworkVersion,
                "",
                [],
                dep?.PackageManager ?? "",
                cs?.ProjectType ?? ""),
            cs?.OverallArchitecture ?? "",
            ra?.RoutingStrategy ?? "",
            directories,
            entryPoints,
            routes,
            components,
            new ProductMapDependencies(runtime, dev, []),
            patterns,
            provenance);
    }

    /// <summary>
    /// Builds a design token tree from the style extractor's output, or null when there was none.
    /// The tree is not validated here; the design document validator owns that.
    /// </summary>
    public static JsonObject? DesignFrom(StyleAndThemeOutput? style) => DesignFrom(style, null);

    /// <summary>
    /// As <see cref="DesignFrom(StyleAndThemeOutput?)"/>, and every non-empty value that cannot be turned
    /// into a canonical token (a gradient or prose as a colour, an unsafe font, an unreadable size) is
    /// omitted with a WARN diagnostic appended to <paramref name="diagnostics"/> instead of vanishing
    /// silently. The value in the message is clipped and redacted.
    /// </summary>
    public static JsonObject? DesignFrom(StyleAndThemeOutput? style, List<ImportDiagnostic>? diagnostics)
    {
        if (style is null)
            return null;

        var root = new JsonObject { ["schema"] = DesignDocument.Schema };

        var color = new JsonObject();
        var brand = new JsonObject();
        AddColor(brand, "primary", style.Colors?.Primary, "color.brand.primary", diagnostics);
        AddColor(brand, "secondary", style.Colors?.Secondary, "color.brand.secondary", diagnostics);
        if (brand.Count > 0)
            color["brand"] = brand;

        AddColor(color, "background", style.Colors?.Background, "color.background", diagnostics);
        AddColor(color, "text", style.Colors?.Text, "color.text", diagnostics);

        var extra = new JsonObject();
        if (style.Colors?.Additional is { } additional)
        {
            foreach (KeyValuePair<string, string> entry in additional)
            {
                if (TryKey(entry.Key, "color.extra", diagnostics, out string key))
                    AddColor(extra, key, entry.Value, "color.extra." + key, diagnostics);
            }
        }

        if (extra.Count > 0)
            color["extra"] = extra;
        if (color.Count > 0)
            root["color"] = color;

        var typography = new JsonObject();
        var family = new JsonObject();
        AddFont(family, "primary", style.Typography?.PrimaryFont, "typography.family.primary", diagnostics);
        AddFont(family, "secondary", style.Typography?.SecondaryFont, "typography.family.secondary", diagnostics);
        if (family.Count > 0)
            typography["family"] = family;

        var size = new JsonObject();
        if (style.Typography?.FontSizes is { } fontSizes)
        {
            foreach (KeyValuePair<string, string> entry in fontSizes)
            {
                if (TryKey(entry.Key, "typography.size", diagnostics, out string key))
                    AddDimension(size, key, entry.Value, null, "typography.size." + key, diagnostics);
            }
        }

        if (size.Count > 0)
            typography["size"] = size;
        if (typography.Count > 0)
            root["typography"] = typography;

        var spacing = new JsonObject();
        if (style.Spacing?.Scale is { } scale)
        {
            foreach (KeyValuePair<string, string> entry in scale)
            {
                if (TryKey(entry.Key, "spacing", diagnostics, out string key))
                    AddDimension(spacing, key, entry.Value, style.Spacing?.Unit, "spacing." + key, diagnostics);
            }
        }

        if (spacing.Count > 0)
            root["spacing"] = spacing;

        return root;
    }

    private static (string Name, string Version) ParseFramework(string? raw)
    {
        string text = (raw ?? "").Trim().ToLowerInvariant();
        string version = "";

        Match match = TrailingVersion.Match(text);
        if (match.Success)
        {
            version = match.Value.Trim();
            text = text[..match.Index].Trim();
        }

        if (version.Length > 30)
            version = version[..30];

        foreach ((string keyword, string name) in FrameworkKeywords)
        {
            if (text.Contains(keyword, StringComparison.Ordinal))
                return (name, version);
        }

        return ("other", version);
    }

    private static void AddColor(JsonObject parent, string key, string? css, string path, List<ImportDiagnostic>? diagnostics)
    {
        if (string.IsNullOrEmpty(css))
            return;

        if (!ColorConvert.TryToSrgbHex(css, out string hex, out _))
        {
            Drop(diagnostics, "COLOR_UNPARSEABLE", path, "colour", css);
            return;
        }

        parent[key] = new JsonObject
        {
            ["$type"] = "color",
            // colorSpace is part of the canonical colour form (DS_V09); without it every colour token is rejected.
            ["$value"] = new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" },
            ["$extensions"] = Extensions(),
        };
    }

    private static void AddFont(JsonObject parent, string key, string? family, string path, List<ImportDiagnostic>? diagnostics)
    {
        if (!DesignText.IsSafeFontFamily(family ?? ""))
        {
            if (!string.IsNullOrWhiteSpace(family))
                Drop(diagnostics, "FONT_FAMILY_UNPARSEABLE", path, "font family", family);
            return;
        }

        parent[key] = new JsonObject
        {
            ["$type"] = "fontFamily",
            ["$value"] = new JsonArray(family!),
            ["$extensions"] = Extensions(),
        };
    }

    private static void AddDimension(
        JsonObject parent, string key, string? raw, string? fallbackUnit, string path, List<ImportDiagnostic>? diagnostics)
    {
        if (string.IsNullOrEmpty(raw))
            return;

        if (!TryParseDimension(raw, fallbackUnit, out double value, out string unit)
            || Math.Abs(value) > TokenNormalizer.MaxDimension)
        {
            Drop(diagnostics, "DIMENSION_UNPARSEABLE", path, "dimension", raw);
            return;
        }

        parent[key] = new JsonObject
        {
            ["$type"] = "dimension",
            ["$value"] = new JsonObject { ["value"] = value, ["unit"] = unit },
            ["$extensions"] = Extensions(),
        };
    }

    // The dropped value is never echoed (it is model text); the path is clipped and redacted.
    private static void Drop(List<ImportDiagnostic>? diagnostics, string code, string path, string what, string value) =>
        diagnostics?.Add(new ImportDiagnostic(
            ImportDiagnosticSeverity.Warn,
            code,
            DesignTokenSanitizer.ClipPath(path),
            $"{what} dropped, not a recognised value (length {value.Length})"));

    /// <summary>
    /// A model-chosen dictionary key as a valid token key: lower-cased, anything outside [a-z0-9_-] turned
    /// into '-', at most 48 characters. A key that looks like a secret, or leaves nothing, drops the entry.
    /// </summary>
    private static bool TryKey(string raw, string parentPath, List<ImportDiagnostic>? diagnostics, out string key)
    {
        key = "";
        string candidate = raw ?? "";
        if (candidate.Length <= 200 && SecretRedactor.Redact(candidate).count == 0)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in candidate.ToLowerInvariant())
            {
                bool ok = c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-';
                sb.Append(ok ? c : '-');
            }

            string slug = sb.ToString().Trim('-', '_');
            if (slug.Length > 48)
                slug = slug[..48].TrimEnd('-', '_');
            if (slug.Length > 0 && SecretRedactor.Redact(slug).count == 0)
            {
                key = slug;
                return true;
            }
        }

        diagnostics?.Add(new ImportDiagnostic(
            ImportDiagnosticSeverity.Warn,
            "TOKEN_KEY_DROPPED",
            DesignTokenSanitizer.ClipPath(parentPath),
            $"entry dropped, its name is not a usable token key (length {candidate.Length})"));
        return false;
    }

    private static bool TryParseDimension(string raw, string? fallbackUnit, out double value, out string unit)
    {
        value = 0;
        unit = "px";

        string text = raw.Trim().ToLowerInvariant();
        if (text.Length == 0)
            return false;

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double bare)
            && double.IsFinite(bare))
        {
            value = bare;
            unit = NormalizeUnit(fallbackUnit);
            return true;
        }

        foreach (string suffix in new[] { "rem", "px", "em", "%" })
        {
            if (!text.EndsWith(suffix, StringComparison.Ordinal))
                continue;

            string number = text[..^suffix.Length];
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && double.IsFinite(parsed))
            {
                value = parsed;
                unit = suffix;
                return true;
            }

            return false;
        }

        return false;
    }

    private static string NormalizeUnit(string? unit) =>
        unit is "px" or "rem" or "em" or "%" ? unit : "px";

    private static JsonObject Extensions() => new()
    {
        ["com.reelbolt"] = new JsonObject
        {
            ["prov"] = new JsonObject { ["method"] = "llm" },
            ["conf"] = 0.5,
            ["locked"] = false,
        },
    };
}
