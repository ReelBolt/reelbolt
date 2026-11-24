using System.Globalization;
using System.Text.RegularExpressions;
using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.WorkflowEngine.Agents.Tools;

/// <summary>
/// A chroma plate the run's analysis tracked, with the key the compile will use to find it (T-206). The compile
/// keys on dominance relative to <see cref="Dominance"/> when that is known (the
/// <c>ScreenInsertFilterBuilder</c> matte), else on <c>colorkey</c> distance within <see cref="Similarity"/>.
/// </summary>
internal sealed record PlateKey(string RegionId, string ColorName, int R, int G, int B, double? Similarity, int? Dominance);

/// <summary>
/// T-206: advisory findings for a sandbox source write — never a refusal. Two Author footguns turned into tool
/// output: a CSS font family that is neither installed, in the run's design system nor declared from a staged
/// file (it silently falls back to a default face at render time), and a colour that the compile would key as
/// the chroma plate (it turns into a hole in the composited insert). Only what the write INTRODUCES is reported:
/// a family or colour already in the file before the write is not repeated. Pure and bounded.
/// </summary>
internal static class AuthoringAdvisories
{
    /// <summary>Most findings one write reports; the rest are counted.</summary>
    internal const int MaxFindings = 8;

    /// <summary>Sources a composition loads styles from; anything else (JSON, images, markdown) is not scanned.</summary>
    private static readonly HashSet<string> ScannedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".css"
    };

    /// <summary>CSS generic and global keywords: always resolvable.</summary>
    private static readonly HashSet<string> GenericFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "serif", "sans-serif", "monospace", "cursive", "fantasy", "system-ui", "emoji", "math", "fangsong",
        "ui-serif", "ui-sans-serif", "ui-monospace", "ui-rounded", "inherit", "initial", "unset", "revert", "revert-layer"
    };

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private const RegexOptions Options = RegexOptions.CultureInvariant;

    private static readonly Regex JsFontFamily = new(@"fontFamily\s*:\s*(['""`])(?<v>[^'""`\r\n]{1,200})\1", Options, Timeout);
    private static readonly Regex CssFontFamily = new(@"(?<![\w-])font-family\s*:\s*(?<v>[^;{}\r\n]{1,200})", Options | RegexOptions.IgnoreCase, Timeout);

    /// <summary>Families a project declares itself: <c>@font-face</c>, <c>new FontFace('X', …)</c>, <c>loadFont({family: 'X'})</c>.</summary>
    private static readonly Regex[] Declarations =
    [
        new(@"@font-face\s*\{[^}]*?font-family\s*:\s*(['""]?)(?<v>[^;'""}\r\n]{1,100})\1", Options | RegexOptions.IgnoreCase, Timeout),
        new(@"new\s+FontFace\(\s*(['""`])(?<v>[^'""`\r\n]{1,100})\1", Options, Timeout),
        new(@"(?<![\w-])family\s*:\s*(['""`])(?<v>[^'""`\r\n]{1,100})\1", Options, Timeout)
    ];

    private static readonly Regex HexColour = new(@"#(?<v>[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})(?![0-9a-zA-Z_])", Options, Timeout);
    private static readonly Regex RgbColour = new(@"rgba?\(\s*(?<r>\d{1,3})\s*[, ]\s*(?<g>\d{1,3})\s*[, ]\s*(?<b>\d{1,3})", Options | RegexOptions.IgnoreCase, Timeout);

    /// <summary>The CSS names that are themselves plate colours (green, blue and magenta families).</summary>
    private static readonly Dictionary<string, (int R, int G, int B)> NamedColours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lime"] = (0, 255, 0),
        ["green"] = (0, 128, 0),
        ["chartreuse"] = (127, 255, 0),
        ["lawngreen"] = (124, 252, 0),
        ["springgreen"] = (0, 255, 127),
        ["limegreen"] = (50, 205, 50),
        ["forestgreen"] = (34, 139, 34),
        ["seagreen"] = (46, 139, 87),
        ["mediumseagreen"] = (60, 179, 113),
        ["blue"] = (0, 0, 255),
        ["mediumblue"] = (0, 0, 205),
        ["magenta"] = (255, 0, 255),
        ["fuchsia"] = (255, 0, 255)
    };

    private static readonly Regex NamedColour = new(
        @"(?:(['""`])(?<v>" + string.Join("|", NamedColours.Keys) + @")\1)|(?::\s*(?<v>" + string.Join("|", NamedColours.Keys) + @")\s*[;}!])",
        Options | RegexOptions.IgnoreCase, Timeout);

    /// <summary>True when <paramref name="path"/> is a source this scanner reads.</summary>
    internal static bool IsScanned(string path) => ScannedExtensions.Contains(Path.GetExtension(path ?? string.Empty));

    /// <summary>True when <paramref name="text"/> has any colour literal at all (the cheap gate before the run's plates are read).</summary>
    internal static bool HasColourLiteral(string text)
    {
        try
        {
            return HexColour.IsMatch(text) || RgbColour.IsMatch(text) || NamedColour.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Families a project's sources declare from their own (staged) files.</summary>
    internal static IEnumerable<string> DeclaredFamilies(string text)
    {
        foreach (Regex declaration in Declarations)
        {
            foreach (Match m in SafeMatches(declaration, text))
                yield return m.Groups["v"].Value.Trim();
        }
    }

    /// <summary>
    /// The advisory lines for one write of <paramref name="after"/> over <paramref name="before"/> (null for a new
    /// file). <paramref name="knownFamilies"/> are the installed and design-system families plus any the project
    /// already declared; <paramref name="plates"/> are the run's chroma plates (empty = no colour check).
    /// </summary>
    internal static IReadOnlyList<string> Scan(
        string path, string? before, string after, IEnumerable<string> knownFamilies, IReadOnlyList<PlateKey> plates)
    {
        if (!IsScanned(path) || string.IsNullOrEmpty(after))
            return [];

        List<string> findings = [];
        try
        {
            ScanFonts(path, before, after, knownFamilies, findings);
            if (plates.Count > 0 && findings.Count <= MaxFindings)
                ScanColours(path, before, after, plates, findings);
        }
        catch (RegexMatchTimeoutException)
        {
            // Advisory only: a pathological file gets no advice rather than a slow tool.
        }

        // Each scan stops at MaxFindings + 1 (enough to know there are more), so a write with thousands of
        // literals costs no more than one with nine: the rest are neither built nor counted.
        return findings.Count <= MaxFindings
            ? findings
            : [.. findings.Take(MaxFindings), $"... and more warning(s) like these (only the first {MaxFindings} are listed)."];
    }

    private static void ScanFonts(string path, string? before, string after, IEnumerable<string> knownFamilies, List<string> findings)
    {
        HashSet<string> known = new(knownFamilies, StringComparer.OrdinalIgnoreCase);
        known.UnionWith(DeclaredFamilies(after));
        HashSet<string> already = new(StringComparer.OrdinalIgnoreCase);
        if (before is not null)
        {
            foreach ((string family, _) in UsedFamilies(before))
                already.Add(family);
        }

        HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string family, int index) in UsedFamilies(after))
        {
            if (findings.Count > MaxFindings)
                return;
            if (known.Contains(family) || already.Contains(family) || !reported.Add(family))
                continue;

            findings.Add(
                $"Font warning: {path} line {LineOf(after, index)} uses the font family '{Bound(family)}', which is not installed in the sandbox, " +
                $"not in the design system and not declared from a staged file, so the render falls back to a default face. " +
                $"Use one of {SandboxFonts.FamilyList}, or call StageFont (source: \"google\" for a Google Fonts family, or a project font file) and paste the loadFont snippet it returns.");
        }
    }

    /// <summary>
    /// The families a style asks for BEFORE the first one that resolves: a stack whose first family is known is
    /// fine; an unknown family ahead of it is the one that silently does not load.
    /// </summary>
    private static IEnumerable<(string Family, int Index)> UsedFamilies(string text)
    {
        foreach (Regex pattern in new[] { JsFontFamily, CssFontFamily })
        {
            foreach (Match m in SafeMatches(pattern, text))
            {
                string value = m.Groups["v"].Value;
                if (value.Contains("${", StringComparison.Ordinal) || value.Contains("var(", StringComparison.OrdinalIgnoreCase))
                    continue; // not a literal

                foreach (string raw in value.Split(','))
                {
                    string family = raw.Trim().Trim('\'', '"', '`').Trim();
                    if (family.Length == 0 || family.StartsWith('{') || family.Contains('(') || family.Contains('$'))
                        break;
                    if (GenericFamilies.Contains(family) || SandboxFonts.Find(family) is not null)
                        break; // the stack resolves here; anything after it is a fallback
                    yield return (family, m.Index);
                    break; // report only the first family of the stack
                }
            }
        }
    }

    private static void ScanColours(string path, string? before, string after, IReadOnlyList<PlateKey> plates, List<string> findings)
    {
        HashSet<(int, int, int)> already = before is null ? [] : [.. Colours(before).Select(c => (c.R, c.G, c.B))];
        HashSet<(int, int, int)> reported = [];
        foreach ((string literal, int r, int g, int b, int index) in Colours(after))
        {
            if (findings.Count > MaxFindings)
                return;
            if (already.Contains((r, g, b)) || !reported.Add((r, g, b)))
                continue;

            PlateKey? plate = plates.FirstOrDefault(p => Keys(p, r, g, b));
            if (plate is null)
                continue;

            findings.Add(
                $"Chroma warning: {path} line {LineOf(after, index)} introduces the colour {literal}, which is within the key of the " +
                $"{plate.ColorName} screen ({plate.RegionId}) this run tracks: wherever it sits on the screen content it is keyed out " +
                "and shows as a hole in the insert. Pick a colour away from the plate colour.");
        }
    }

    private static IEnumerable<(string Literal, int R, int G, int B, int Index)> Colours(string text)
    {
        foreach (Match m in SafeMatches(HexColour, text))
        {
            string hex = m.Groups["v"].Value;
            if (hex.Length is 3 or 4)
                hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
            int r = int.Parse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int g = int.Parse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            int b = int.Parse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            yield return (m.Value, r, g, b, m.Index);
        }

        foreach (Match m in SafeMatches(RgbColour, text))
        {
            int r = Math.Min(255, int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture));
            int g = Math.Min(255, int.Parse(m.Groups["g"].Value, CultureInfo.InvariantCulture));
            int b = Math.Min(255, int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture));
            yield return (m.Value + ")", r, g, b, m.Index);
        }

        foreach (Match m in SafeMatches(NamedColour, text))
        {
            string name = m.Groups["v"].Value;
            (int r, int g, int b) = NamedColours[name];
            yield return (name, r, g, b, m.Groups["v"].Index);
        }
    }

    /// <summary>
    /// Would the compile's matte treat this colour as plate? Mirrors <c>ScreenInsertFilterBuilder</c>: with a
    /// measured plate dominance, the plate term (dominance from 35% of the plate's level) or the dark-plate term
    /// (purity of at least 0.5 above a small floor); with only a <c>colorkey</c>, RGB distance within its
    /// similarity; with no key at all, the plate term against a nominal plate level.
    /// </summary>
    internal static bool Keys(PlateKey plate, int r, int g, int b)
    {
        (int target, int rival) = plate.ColorName.ToLowerInvariant() switch
        {
            "blue" => (b, Math.Max(r, g)),
            "magenta" => (Math.Min(r, b), g),
            _ => (g, Math.Max(r, b))
        };
        int dominance = target - rival;

        if (plate.Dominance is int level && level >= 24)
            return dominance >= 0.35 * level || (target >= 10 && dominance > 0 && dominance >= 0.5 * target);

        if (plate.Similarity is double similarity && similarity > 0)
        {
            double dr = r - plate.R, dg = g - plate.G, db = b - plate.B;
            return Math.Sqrt((dr * dr + dg * dg + db * db) / (3.0 * 255 * 255)) < similarity;
        }

        return dominance >= 0.35 * 200 || (target >= 24 && dominance > 0 && dominance >= 0.5 * target);
    }

    /// <summary>Distinct colour literals, none of them the design system's, that make a file worth a palette reminder.</summary>
    internal const int MinForeignColours = 4;

    private static readonly Regex RgbaColour = new(
        @"rgba?\(\s*(?<r>\d{1,3})\s*[, ]\s*(?<g>\d{1,3})\s*[, ]\s*(?<b>\d{1,3})(?:\s*[,/]\s*(?<a>[0-9.]+%?))?", Options | RegexOptions.IgnoreCase, Timeout);

    private static readonly Regex HslColour = new(
        @"hsla?\(\s*(?<h>-?[0-9.]+)(?:deg)?\s*[, ]\s*(?<s>[0-9.]+)%\s*[, ]\s*(?<l>[0-9.]+)%(?:\s*[,/]\s*(?<a>[0-9.]+%?))?", Options | RegexOptions.IgnoreCase, Timeout);

    /// <summary>
    /// T-006: a palette reminder (never a refusal). When the run pins a design system with colour tokens
    /// (<paramref name="palette"/>: normalised <c>rrggbb</c> to token name) and the written source holds at least
    /// <see cref="MinForeignColours"/> distinct colour literals of which none is a design-system colour (exact
    /// match after normalisation; near-black, near-white and fully transparent colours are neutral and ignored),
    /// returns a <c>design_system_colors_unused</c> warning naming a few palette colours. Null otherwise.
    /// </summary>
    internal static string? DesignSystemColourAdvice(string path, string after, IReadOnlyDictionary<string, string> palette)
    {
        if (palette.Count == 0 || !IsScanned(path) || string.IsNullOrEmpty(after))
            return null;

        HashSet<string> distinct = [];
        try
        {
            foreach (string hex in LiteralColours(after))
            {
                if (palette.ContainsKey(hex))
                    return null; // the file already uses the design system
                distinct.Add(hex);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (distinct.Count < MinForeignColours)
            return null;

        string names = string.Join(", ", palette.Take(4).Select(p => $"{Bound(p.Value)} (#{p.Key})"));
        return $"design_system_colors_unused: {path} uses {distinct.Count} colours and none is a colour of the project's design system. " +
               $"Take colours from the design system (ReadDesignSystem tokens), for example {names}, unless the brief asks for something else.";
    }

    /// <summary>Normalised <c>rrggbb</c> of every non-neutral colour literal in <paramref name="text"/>, bounded.</summary>
    private static IEnumerable<string> LiteralColours(string text)
    {
        int budget = 400;
        foreach (Match m in HexColour.Matches(text))
        {
            if (budget-- <= 0)
                yield break;
            string v = m.Groups["v"].Value.ToLowerInvariant();
            if (v.Length is 3 or 4)
                v = string.Concat(v.Select(c => new string(c, 2)));
            if (v.Length == 8)
            {
                if (v.EndsWith("00", StringComparison.Ordinal))
                    continue;
                v = v[..6];
            }

            if (!IsNeutral(Convert.ToInt32(v[..2], 16), Convert.ToInt32(v[2..4], 16), Convert.ToInt32(v[4..6], 16)))
                yield return v;
        }

        foreach (Match m in RgbaColour.Matches(text))
        {
            if (budget-- <= 0)
                yield break;
            if (m.Groups["a"].Success && double.TryParse(m.Groups["a"].Value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && a <= 0)
                continue;
            int r = int.Parse(m.Groups["r"].Value, CultureInfo.InvariantCulture), g = int.Parse(m.Groups["g"].Value, CultureInfo.InvariantCulture), b = int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
            if (r > 255 || g > 255 || b > 255 || IsNeutral(r, g, b))
                continue;
            yield return $"{r:x2}{g:x2}{b:x2}";
        }

        foreach (Match m in HslColour.Matches(text))
        {
            if (budget-- <= 0)
                yield break;
            if (m.Groups["a"].Success && double.TryParse(m.Groups["a"].Value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double a) && a <= 0)
                continue;
            double h = (double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture) % 360 + 360) % 360;
            double sat = Math.Clamp(double.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) / 100, 0, 1);
            double l = Math.Clamp(double.Parse(m.Groups["l"].Value, CultureInfo.InvariantCulture) / 100, 0, 1);
            double c = (1 - Math.Abs(2 * l - 1)) * sat;
            double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
            double mm = l - c / 2;
            (double r1, double g1, double b1) = (int)(h / 60) switch
            {
                0 => (c, x, 0d),
                1 => (x, c, 0d),
                2 => (0d, c, x),
                3 => (0d, x, c),
                4 => (x, 0d, c),
                _ => (c, 0d, x)
            };
            int r = (int)Math.Round((r1 + mm) * 255), g = (int)Math.Round((g1 + mm) * 255), b = (int)Math.Round((b1 + mm) * 255);
            if (!IsNeutral(r, g, b))
                yield return $"{r:x2}{g:x2}{b:x2}";
        }
    }

    private static bool IsNeutral(int r, int g, int b) =>
        (r <= 24 && g <= 24 && b <= 24) || (r >= 232 && g >= 232 && b >= 232);

    private static IEnumerable<Match> SafeMatches(Regex regex, string text) => regex.Matches(text);

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
                line++;
        }

        return line;
    }

    private static string Bound(string value)
    {
        string clean = new(value.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= 60 ? clean : clean[..60] + "...";
    }
}
