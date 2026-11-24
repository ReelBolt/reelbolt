using System.Text.Json.Nodes;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Shared.DesignSystems;

/// <summary>
/// Maps a brand's requested font families onto the closed <see cref="CaptionFont"/> set the
/// workflow-engine image actually ships (see SPEC 14.2 and <c>CaptionFontCatalog</c>).
///
/// <para>
/// An exact, case-insensitive match against a catalog name (or its enum name) wins outright and
/// carries no warning — the brand asked for a face the image has. A family the catalog does not
/// name falls back to the nearest keyword group (SPEC 14.2), and a family that matches no keyword
/// falls back to Inter, the caption style's own default. Any non-exact outcome carries
/// <c>font_substituted:{requested}-&gt;{used}</c> so the substitution is visible in the step's
/// <c>meta.designSystem.warnings[]</c> rather than silent.
/// </para>
/// </summary>
public static class BrandFontMatcher
{
    /// <summary>The design-document font roles, in preference order — the same order the prompt view lists them.</summary>
    public static readonly IReadOnlyList<string> FontRoles = ["heading", "body", "mono", "primary", "secondary"];

    /// <summary>Serif keywords: a family naming one of these is drawn with an installed serif.</summary>
    private static readonly string[] SandboxSerifKeywords =
    [
        "times", "georgia", "garamond", "serif", "baskerville", "palatino", "book antiqua",
        "didot", "bodoni", "playfair", "cambria", "merriweather", "lora", "pt serif", "rockwell",
        "abril",
    ];

    /// <summary>
    /// The serif keywords that ask for a high-contrast display or heading serif, which DM Serif
    /// Display stands in for; every other serif is Playfair Display.
    /// </summary>
    private static readonly string[] SandboxDisplaySerifKeywords = ["display", "didot", "bodoni", "abril", "dm serif"];

    /// <summary>Mono keywords: a family naming one of these is drawn with JetBrains Mono.</summary>
    private static readonly string[] SandboxMonoKeywords =
    [
        "courier", "consolas", "monaco", "mono", "menlo", "inconsolata", "fira code", "fira mono",
        "source code", "jetbrains", "cascadia", "typewriter",
    ];

    /// <summary>
    /// Display keywords, in order: the first a family contains picks the installed display face
    /// (a heavy grotesque to Anton, a tall condensed face to Bebas Neue).
    /// </summary>
    private static readonly (string Keyword, string Family)[] SandboxDisplayKeywords =
    [
        ("anton", "Anton"),
        ("bebas", "Bebas Neue"),
        ("oswald", "Bebas Neue"),
        ("impact", "Bebas Neue"),
        ("league gothic", "Bebas Neue"),
        ("condensed", "Bebas Neue"),
        ("compressed", "Bebas Neue"),
    ];

    /// <summary>The face an unmatched (or sans) request is drawn with.</summary>
    private const string SandboxDefaultFont = "Inter";

    /// <summary>The caption font descriptors shipped in the image, exactly as <c>CaptionFontCatalog</c> names them.</summary>
    private static readonly (string Family, CaptionFont Font)[] Catalog =
    [
        ("Inter", CaptionFont.Inter),
        ("Montserrat", CaptionFont.Montserrat),
        ("Poppins", CaptionFont.Poppins),
        ("Bebas Neue", CaptionFont.BebasNeue),
        ("Anton", CaptionFont.Anton),
        ("DejaVu Sans", CaptionFont.DejaVuSans),
    ];

    /// <summary>The SPEC 14.2 keyword table. Order is the catalogue's: the first keyword a family contains wins.</summary>
    private static readonly (string Keyword, CaptionFont Font)[] Keywords =
    [
        ("inter", CaptionFont.Inter),
        ("roboto", CaptionFont.Inter),
        ("helvetica", CaptionFont.Inter),
        ("arial", CaptionFont.Inter),
        ("segoe", CaptionFont.Inter),
        ("system-ui", CaptionFont.Inter),
        ("-apple-system", CaptionFont.Inter),
        ("sf pro", CaptionFont.Inter),
        ("montserrat", CaptionFont.Montserrat),
        ("gotham", CaptionFont.Montserrat),
        ("proxima", CaptionFont.Montserrat),
        ("raleway", CaptionFont.Montserrat),
        ("josefin", CaptionFont.Montserrat),
        ("poppins", CaptionFont.Poppins),
        ("nunito", CaptionFont.Poppins),
        ("quicksand", CaptionFont.Poppins),
        ("dm sans", CaptionFont.Poppins),
        ("manrope", CaptionFont.Poppins),
        ("lexend", CaptionFont.Poppins),
        ("bebas", CaptionFont.BebasNeue),
        ("oswald", CaptionFont.BebasNeue),
        ("impact", CaptionFont.BebasNeue),
        ("league gothic", CaptionFont.BebasNeue),
        ("anton", CaptionFont.Anton),
    ];

    /// <summary>
    /// The thirteen font families the thumbnail editor offers (web <c>lib/editor/text-content.ts</c>,
    /// <c>EDITOR_FONTS</c>), in the order the picker lists them. This is the closed set
    /// <see cref="NearestEditorFont"/> maps a brand's heading onto.
    /// </summary>
    public static readonly IReadOnlyList<string> EditorFontFamilies =
    [
        "Chakra Petch",
        "JetBrains Mono",
        "Anton",
        "Bebas Neue",
        "Oswald",
        "Montserrat",
        "Poppins",
        "Inter",
        "Roboto Condensed",
        "Archivo Black",
        "Playfair Display",
        "Permanent Marker",
        "Bangers",
    ];

    /// <summary>
    /// The keyword groups <see cref="NearestEditorFont"/> falls back to, each naming one of the
    /// editor's families. It is deliberately richer than the caption table (SPEC 14.2): the editor
    /// offers thirteen faces, so a request for a mono maps to JetBrains Mono and a serif to Playfair
    /// Display rather than everything landing on Inter. Order is the table's: the first keyword a
    /// family contains wins.
    /// </summary>
    private static readonly (string Keyword, string Family)[] EditorKeywords =
    [
        ("chakra", "Chakra Petch"),
        ("jetbrains", "JetBrains Mono"),
        ("mono", "JetBrains Mono"),
        ("courier", "JetBrains Mono"),
        ("consolas", "JetBrains Mono"),
        ("menlo", "JetBrains Mono"),
        ("monaco", "JetBrains Mono"),
        ("inconsolata", "JetBrains Mono"),
        ("fira", "JetBrains Mono"),
        ("anton", "Anton"),
        ("bebas", "Bebas Neue"),
        ("league gothic", "Bebas Neue"),
        ("impact", "Bebas Neue"),
        ("oswald", "Oswald"),
        ("montserrat", "Montserrat"),
        ("gotham", "Montserrat"),
        ("proxima", "Montserrat"),
        ("raleway", "Montserrat"),
        ("josefin", "Montserrat"),
        ("poppins", "Poppins"),
        ("nunito", "Poppins"),
        ("quicksand", "Poppins"),
        ("dm sans", "Poppins"),
        ("manrope", "Poppins"),
        ("lexend", "Poppins"),
        ("inter", "Inter"),
        ("helvetica", "Inter"),
        ("arial", "Inter"),
        ("segoe", "Inter"),
        ("system-ui", "Inter"),
        ("-apple-system", "Inter"),
        ("sf pro", "Inter"),
        ("roboto", "Roboto Condensed"),
        ("archivo", "Archivo Black"),
        ("playfair", "Playfair Display"),
        ("georgia", "Playfair Display"),
        ("serif", "Playfair Display"),
        ("marker", "Permanent Marker"),
        ("comic", "Bangers"),
        ("bangers", "Bangers"),
        // The generic fallbacks come last, so "Comic Sans" reaches Bangers above.
        ("sans", "Inter"),
    ];

    /// <summary>
    /// The editor font nearest <paramref name="families"/>: the first family that exactly names one
    /// of <paramref name="editorFonts"/> wins (case-insensitive, quotes dropped), else the first
    /// family matching a keyword group whose family is offered, else null. Null means the brand
    /// asked for a face the editor does not have (SPEC 16.3) — the editor keeps its own default,
    /// which is why this never falls back to a face the brand did not ask for.
    /// </summary>
    public static string? NearestEditorFont(IReadOnlyList<string>? families, IReadOnlyList<string> editorFonts)
    {
        if (families is null || families.Count == 0 || editorFonts is null || editorFonts.Count == 0)
            return null;

        foreach (string family in families)
        {
            string candidate = Clean(family);
            if (candidate.Length == 0)
                continue;

            foreach (string editor in editorFonts)
            {
                if (string.Equals(candidate, editor, StringComparison.OrdinalIgnoreCase))
                    return editor;
            }
        }

        foreach (string family in families)
        {
            string candidate = Clean(family);
            if (candidate.Length == 0)
                continue;

            foreach ((string keyword, string target) in EditorKeywords)
            {
                if (!candidate.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? offered = editorFonts.FirstOrDefault(
                    editor => string.Equals(editor, target, StringComparison.OrdinalIgnoreCase));
                if (offered is not null)
                    return offered;
            }
        }

        return null;
    }

    /// <summary>
    /// The caption font nearest <paramref name="families"/>. The families are the requested stack,
    /// in preference order; each is tried for an exact catalog match first, then for a keyword, and
    /// an unmatched request falls back to Inter. Empty input is Inter with no warning (nothing was
    /// requested, so nothing was substituted).
    /// </summary>
    public static (CaptionFont Font, string? Warning) NearestCaptionFont(IReadOnlyList<string>? families)
    {
        if (families is null || families.Count == 0)
            return (CaptionFont.Inter, null);

        foreach (string family in families)
        {
            string candidate = Clean(family);
            if (candidate.Length == 0)
                continue;

            foreach ((string name, CaptionFont font) in Catalog)
            {
                if (string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(candidate, font.ToString(), StringComparison.OrdinalIgnoreCase))
                    return (font, null);
            }
        }

        string requested = FirstRequested(families);

        foreach (string family in families)
        {
            string candidate = Clean(family);
            if (candidate.Length == 0)
                continue;

            foreach ((string keyword, CaptionFont font) in Keywords)
            {
                if (candidate.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return (font, $"font_substituted:{requested}->{FamilyOf(font)}");
            }
        }

        return (CaptionFont.Inter, $"font_substituted:{requested}->Inter");
    }

    /// <summary>
    /// The installed sandbox font nearest <paramref name="families"/> (see <see cref="SandboxFonts"/>:
    /// the eleven bundled families plus Liberation Sans/Serif/Mono). The first non-empty requested
    /// family decides. A family that is installed is used as is, with no warning. Otherwise, in order:
    /// a family that contains an installed name ("Inter Tight") maps to it; a mono family to
    /// JetBrains Mono; a display family to Anton or Bebas Neue by keyword; a serif to DM Serif Display
    /// when it asks for a display/heading serif and to Playfair Display otherwise; anything else
    /// (sans included) to Inter. Every such substitution is reported as
    /// <c>font_substituted:requested-&gt;used</c> and surfaced in the graphic's result notes; an
    /// empty request is Inter with no warning.
    /// </summary>
    public static (string Font, string? Warning) NearestSandboxFont(IReadOnlyList<string>? families)
    {
        if (families is null || families.Count == 0)
            return (SandboxDefaultFont, null);

        foreach (string family in families)
        {
            string candidate = Clean(family);
            if (candidate.Length == 0)
                continue;

            if (SandboxFonts.Find(candidate) is { } installed)
                return (installed.Family, null);

            string nearest = NearestInstalledFamily(candidate);
            return (nearest, $"font_substituted:{candidate}->{nearest}");
        }

        return (SandboxDefaultFont, null);
    }

    private static string NearestInstalledFamily(string candidate)
    {
        // The longest installed name first, so the most specific one a variant names wins.
        SandboxFont? containing = SandboxFonts.Families
            .OrderByDescending(f => f.Family.Length)
            .FirstOrDefault(f => candidate.Contains(f.Family, StringComparison.OrdinalIgnoreCase));
        if (containing is not null)
            return containing.Family;

        if (Contains(candidate, SandboxMonoKeywords))
            return "JetBrains Mono";

        foreach ((string keyword, string display) in SandboxDisplayKeywords)
        {
            if (candidate.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return display;
        }

        // "sans-serif" names a serif keyword but asks for the sans face; the sans check wins.
        if (!candidate.Contains("sans", StringComparison.OrdinalIgnoreCase) && Contains(candidate, SandboxSerifKeywords))
            return Contains(candidate, SandboxDisplaySerifKeywords) ? "DM Serif Display" : "Playfair Display";

        return SandboxDefaultFont;
    }

    /// <summary>
    /// The requested families a design document declares, in <see cref="FontRoles"/> order, taking
    /// each role's whole <c>typography.family.&lt;role&gt;</c> stack. Missing roles and malformed
    /// arrays are skipped rather than failing the match; the result is empty when the document
    /// declares no family at all.
    /// </summary>
    public static IReadOnlyList<string> RequestedFamilies(JsonObject? design)
    {
        var families = new List<string>();
        if (design is null)
            return families;

        foreach (string role in FontRoles)
        {
            string path = $"typography.family.{role}";
            if (DesignDocument.FindToken(design, path) is null)
                continue;
            if (!DesignDocument.ResolveAlias(design, path, out JsonNode? value))
                continue;
            if (value is not JsonArray stack)
                continue;

            foreach (JsonNode? node in stack)
            {
                if (node is JsonValue json && json.TryGetValue(out string? family) && !string.IsNullOrWhiteSpace(family))
                    families.Add(Clean(family));
            }
        }

        return families;
    }

    /// <summary>The catalog family name the enum's captions are drawn with.</summary>
    public static string FamilyOf(CaptionFont font) =>
        Catalog.FirstOrDefault(entry => entry.Font == font).Family ?? font.ToString();

    private static string FirstRequested(IReadOnlyList<string> families)
    {
        foreach (string family in families)
        {
            string candidate = Clean(family);
            if (candidate.Length > 0)
                return candidate;
        }

        return string.Empty;
    }

    private static bool Contains(string candidate, string[] keywords) =>
        keywords.Any(keyword => candidate.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    /// <summary>Trims a requested family, dropping the single or double quotes a CSS-style stack entry may carry.</summary>
    private static string Clean(string? family)
    {
        string trimmed = family?.Trim() ?? string.Empty;
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1].Trim();
        }

        return trimmed;
    }
}
