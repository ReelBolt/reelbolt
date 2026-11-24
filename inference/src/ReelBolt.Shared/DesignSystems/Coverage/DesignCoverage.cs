using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Coverage;

/// <summary>The design and product-map halves of one coverage assessment.</summary>
public sealed record CoverageReport(CoverageDesign Design, CoverageProductMap ProductMap);

/// <summary>What a design document covers.</summary>
public sealed record CoverageDesign(
    bool Color,
    bool Typography,
    bool Spacing,
    bool Radius,
    bool Shadow,
    bool Motion,
    bool Voice,
    bool Assets,
    int TokenCount,
    bool PrimaryColor,
    bool FallbackRecommended);

/// <summary>What a product map covers.</summary>
public sealed record CoverageProductMap(
    bool Framework,
    int Routes,
    int Components,
    double ResponsibilityFilledPct,
    bool FallbackRecommended);

/// <summary>
/// Measures how much of a design document and a product map is actually filled in, so a caller can
/// decide whether an extractor covered enough to skip a fallback. Pure: it reads JSON trees only and
/// never throws on a malformed one.
/// </summary>
public static class DesignCoverage
{
    private static readonly string[] TokenGroups =
    [
        "color", "typography", "spacing", "radius", "shadow", "motion", "component",
    ];

    /// <summary>Computes coverage for an optional design token tree and an optional product map.</summary>
    public static CoverageReport Compute(JsonObject? design, ProductMapDocument? map)
    {
        bool color = HasToken(design?["color"]);
        bool typography = HasToken(design?["typography"]);
        bool spacing = HasToken(design?["spacing"]);
        bool radius = HasToken(design?["radius"]);
        bool shadow = HasToken(design?["shadow"]);
        bool motion = HasToken(design?["motion"]);

        int tokenCount = 0;
        foreach (string group in TokenGroups)
            tokenCount += CountTokens(design?[group]);

        bool voice = HasNonEmptyObject(design, "voice");
        bool assets = HasNonEmptyObject(design, "assets");
        bool primaryColor = HasTokenAt(design, "color.primary")
            || HasTokenAt(design, "color.brand.primary")
            || HasTokenAt(design, "color.accent");

        bool designFallback = !(primaryColor && typography && tokenCount >= 8);

        var designReport = new CoverageDesign(
            color,
            typography,
            spacing,
            radius,
            shadow,
            motion,
            voice,
            assets,
            tokenCount,
            primaryColor,
            designFallback);

        return new CoverageReport(designReport, ComputeProduct(map));
    }

    private static CoverageProductMap ComputeProduct(ProductMapDocument? map)
    {
        if (map is null)
            return new CoverageProductMap(false, 0, 0, 0.0, true);

        string frameworkName = map.Framework?.Name ?? "";
        bool framework = !string.IsNullOrEmpty(frameworkName);
        int routes = map.Routes?.Count ?? 0;
        int components = map.Components?.Count ?? 0;
        double responsibilityFilledPct = 0.0;

        if (components > 0)
        {
            int filled = 0;
            foreach (ProductMapComponent? component in map.Components ?? [])
            {
                if (component is not null && !string.IsNullOrEmpty(component.Responsibility))
                    filled++;
            }

            responsibilityFilledPct = (double)filled / components;
        }

        bool fallback = components == 0
            || (routes == 0 && string.Equals(frameworkName, "other", StringComparison.Ordinal))
            || responsibilityFilledPct < 0.5;

        return new CoverageProductMap(framework, routes, components, responsibilityFilledPct, fallback);
    }

    private static bool HasNonEmptyObject(JsonObject? design, string key) =>
        design is not null
        && design.TryGetPropertyValue(key, out JsonNode? node)
        && node is JsonObject obj
        && obj.Count > 0;

    private static bool HasToken(JsonNode? group) => CountTokens(group) >= 1;

    private static bool HasTokenAt(JsonObject? root, string dottedPath)
    {
        JsonNode? current = root;
        foreach (string segment in dottedPath.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(segment, out JsonNode? next))
                return false;
            current = next;
        }

        return current is JsonObject token && token.ContainsKey("$value");
    }

    private static int CountTokens(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                int count = obj.ContainsKey("$value") ? 1 : 0;
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                    count += CountTokens(property.Value);
                return count;

            case JsonArray array:
                int arrayCount = 0;
                foreach (JsonNode? item in array)
                    arrayCount += CountTokens(item);
                return arrayCount;

            default:
                return 0;
        }
    }
}
