namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// The closed set of font families the deployment bundles. It is the union of the caption-font
/// families in WorkflowEngine <c>AssSubtitleBuilder.cs</c> (<c>CaptionFontCatalog</c>) and the
/// editor-font families in web <c>lib/editor/text-content.ts</c> (<c>EDITOR_FONTS</c>): 14 names.
/// The design-document validator and the importers both read this one set, so a resolved font the
/// reader writes is always one the validator accepts.
/// </summary>
public static class DesignSystemFonts
{
    /// <summary>The bundled family names, compared case-insensitively.</summary>
    public static readonly IReadOnlySet<string> BundledNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Inter",
        "Montserrat",
        "Poppins",
        "Bebas Neue",
        "Anton",
        "DejaVu Sans",
        "Chakra Petch",
        "JetBrains Mono",
        "Oswald",
        "Roboto Condensed",
        "Archivo Black",
        "Playfair Display",
        "Permanent Marker",
        "Bangers",
    };
}
