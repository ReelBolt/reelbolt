using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Color;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>The overlay colour choice for a palette: the chosen opaque font colour, the box colour, and a warning code when nothing qualified.</summary>
/// <param name="FontColor">The first candidate token whose contrast against black is at least <see cref="BrandOverlayColors.MinContrastAgainstBlack"/>, or null when none qualifies.</param>
/// <param name="BoxColor">The box colour paired with a chosen font colour — always <see cref="BrandOverlayColors.BoxColor"/>.</param>
/// <param name="Warning">Non-null only when no candidate qualified: <see cref="BrandOverlayColors.LowContrastWarning"/>.</param>
public sealed record BrandOverlayPick(string? FontColor, string BoxColor, string? Warning);

/// <summary>
/// Picks the brand-aware overlay colour for a pinned design document (see SPEC 14.2). Pure: it
/// reads only the document it is handed, never IO or the run. The candidate tokens are tried in the
/// order SPEC 14.2 fixes, and the first one whose WCAG contrast against black clears 7.0 wins —
/// a dark navy that reads as "on brand" is unusable as text on a dark overlay, so it is skipped.
/// </summary>
public static class BrandOverlayColors
{
    /// <summary>The overlay box colour the brand path pairs with a chosen font colour; inside the executor's existing allowlist.</summary>
    public const string BoxColor = "black@0.6";

    /// <summary>Recorded when the palette held no colour that reads on a dark overlay.</summary>
    public const string LowContrastWarning = "brand_color_low_contrast";

    /// <summary>The minimum WCAG contrast ratio against black a candidate must clear.</summary>
    public const double MinContrastAgainstBlack = 7.0;

    /// <summary>The candidate token paths, in the priority order SPEC 14.2 fixes.</summary>
    public static readonly IReadOnlyList<string> CandidatePaths =
    [
        "color.brand.primary",
        "color.brand.secondary",
        "color.accent",
        "color.text",
    ];

    /// <summary>
    /// The first candidate token in <see cref="CandidatePaths"/> whose resolved colour has a WCAG
    /// contrast ratio of at least 7.0 against black. When none qualifies, returns a null colour and
    /// <see cref="LowContrastWarning"/> so the caller can keep the configured values and record it.
    /// </summary>
    public static BrandOverlayPick Pick(JsonObject? palette)
    {
        if (palette is not null)
        {
            foreach (string path in CandidatePaths)
            {
                if (!TryColorAt(palette, path, out string hex))
                    continue;

                if (ColorContrast.Ratio(hex, "#000000") >= MinContrastAgainstBlack)
                    return new BrandOverlayPick(hex, BoxColor, null);
            }
        }

        return new BrandOverlayPick(null, BoxColor, LowContrastWarning);
    }

    /// <summary>Resolves a token path to an opaque lowercase <c>#rrggbb</c>, dropping any alpha.</summary>
    private static bool TryColorAt(JsonObject palette, string path, out string hex)
    {
        hex = string.Empty;

        if (DesignDocument.FindToken(palette, path) is null)
            return false;
        if (!DesignDocument.ResolveAlias(palette, path, out JsonNode? value))
            return false;

        // Canonical colour tokens are {"hex": "...", "colorSpace": "srgb"}; a bare string is tolerated for legacy documents.
        string? raw = value switch
        {
            JsonObject color => color["hex"] is JsonValue hexValue && hexValue.TryGetValue(out string? hexText) ? hexText : null,
            JsonValue json when json.TryGetValue(out string? text) => text,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(raw))
            return false;
        if (!ColorConvert.TryToSrgbHex(raw, out string srgb, out _))
            return false;

        hex = srgb.Length > 7 ? srgb[..7] : srgb;
        return true;
    }
}
