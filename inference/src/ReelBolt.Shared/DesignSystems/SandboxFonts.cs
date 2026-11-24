namespace ReelBolt.Shared.DesignSystems;

/// <summary>One font family installed in the sandbox-runtime image, by its CSS <c>font-family</c> name.</summary>
/// <param name="Family">The family name exactly as <c>fc-list</c> reports it and CSS <c>font-family</c> selects it.</param>
/// <param name="Category"><c>sans</c>, <c>serif</c>, <c>mono</c> or <c>display</c>.</param>
/// <param name="Bundled">
/// True for a family ReelBolt bundles from <c>sandbox/fonts/</c> (listed in <c>sandbox/fonts/fonts.json</c>);
/// false for the Liberation faces the image gets from the <c>fonts-liberation</c> package.
/// </param>
public sealed record SandboxFont(string Family, string Category, bool Bundled);

/// <summary>
/// The fonts the sandbox-runtime image installs (<c>/usr/share/fonts/truetype/reelbolt/</c> plus
/// <c>fonts-liberation</c>; Noto Color Emoji is installed too but is not a text face). The sandbox has
/// no internet, so these are the only families a composition can draw text with: a font loaded from
/// the network (<c>@remotion/google-fonts</c>, a CDN stylesheet, a remote <c>@font-face</c>) is
/// refused before any render. A test pins the bundled half to <c>sandbox/fonts/fonts.json</c>.
/// </summary>
public static class SandboxFonts
{
    /// <summary>Every installed text family: the bundled ones in manifest order, then Liberation Sans/Serif/Mono.</summary>
    public static readonly IReadOnlyList<SandboxFont> Families =
    [
        new("Inter", "sans", true),
        new("Montserrat", "sans", true),
        new("Poppins", "sans", true),
        new("DM Sans", "sans", true),
        new("Space Grotesk", "sans", true),
        new("Chakra Petch", "sans", true),
        new("Bebas Neue", "display", true),
        new("Anton", "display", true),
        new("Playfair Display", "serif", true),
        new("DM Serif Display", "serif", true),
        new("JetBrains Mono", "mono", true),
        new("Roboto", "sans", true),
        new("Open Sans", "sans", true),
        new("Oswald", "sans", true),
        new("Raleway", "sans", true),
        new("Source Sans 3", "sans", true),
        new("Nunito", "sans", true),
        new("Work Sans", "sans", true),
        new("Rubik", "sans", true),
        new("Barlow", "sans", true),
        new("Barlow Condensed", "sans", true),
        new("Archivo", "sans", true),
        new("Manrope", "sans", true),
        new("Outfit", "sans", true),
        new("Plus Jakarta Sans", "sans", true),
        new("IBM Plex Sans", "sans", true),
        new("Lora", "serif", true),
        new("Libre Baskerville", "serif", true),
        new("Cormorant Garamond", "serif", true),
        new("EB Garamond", "serif", true),
        new("IBM Plex Mono", "mono", true),
        new("Fira Code", "mono", true),
        new("Roboto Mono", "mono", true),
        new("Space Mono", "mono", true),
        new("Abril Fatface", "display", true),
        new("Lobster", "display", true),
        new("Pacifico", "display", true),
        new("Caveat", "display", true),
        new("Dancing Script", "display", true),
        new("Archivo Black", "display", true),
        new("Alfa Slab One", "display", true),
        new("Liberation Sans", "sans", false),
        new("Liberation Serif", "serif", false),
        new("Liberation Mono", "mono", false),
    ];

    /// <summary>The installed family names, comma-separated, for model-facing text.</summary>
    public static string FamilyList { get; } = string.Join(", ", Families.Select(f => f.Family));

    /// <summary>The installed family <paramref name="family"/> names exactly (case-insensitive), or null.</summary>
    public static SandboxFont? Find(string? family) =>
        string.IsNullOrWhiteSpace(family)
            ? null
            : Families.FirstOrDefault(f => string.Equals(f.Family, family.Trim(), StringComparison.OrdinalIgnoreCase));
}
