using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.DesignSystems.Color;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// SPEC 16.4 brand-pdf: the pure reader over the text a brand PDF was reduced to. The PDF bytes
/// themselves are parsed only by the import-worker (P3-09); this type never sees or touches them.
///
/// <para>
/// Colours are found by a <c>#[0-9A-Fa-f]{6}</c> hex regex and by the <c>RGB: r g b</c> /
/// <c>R:0 G:0 B:0</c> channel forms. A colour near one of the words
/// <c>primary|secondary|accent|brand|background|text</c> is biased to that role; every other colour
/// becomes <c>color.extra.*</c>. Font names come from <c>(Typeface|Font|Typography): name</c> lines
/// and land under <c>typography.family.*</c> only when <see cref="DesignText.IsSafeFontFamily"/>
/// accepts the name. Tone words are never extracted: a PDF states hexes, not intent, so every token
/// is written at confidence 0.4 (the "low" band) with <c>prov.method=static importer=brand-pdf</c>.
/// </para>
///
/// <para>
/// Deliberately not registered in <see cref="DesignImportRegistry"/>: the registry runs over project
/// file bytes, and a PDF must reach this type only as text the worker extracted.
/// </para>
/// </summary>
public sealed class BrandPdfImporter : IDesignImporter
{
    /// <summary>The stable importer identifier used in provenance and in <see cref="ImportSourceRef"/>.</summary>
    public string Id => "brand-pdf";

    /// <summary>Every brand-PDF token is "low": the text states values, not intent.</summary>
    private const double Confidence = 0.4;

    /// <summary>The most tokens one run may emit.</summary>
    private const int MaxTokens = 200;

    /// <summary>How far either side of a colour a role word still counts as "near".</summary>
    private const int ProximityWindow = 80;

    private static readonly Regex HexPattern = new(
        @"#[0-9A-Fa-f]{6}\b",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    private static readonly Regex RgbPattern = new(
        @"\bRGB\s*[:=(]\s*(\d{1,3})\s*[,;/]?\s*(\d{1,3})\s*[,;/]?\s*(\d{1,3})\b",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ChannelPattern = new(
        @"\bR\s*[:=]\s*(\d{1,3})\s*[,;/]?\s*G\s*[:=]\s*(\d{1,3})\s*[,;/]?\s*B\s*[:=]\s*(\d{1,3})\b",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex FontPattern = new(
        @"(Typeface|Font|Typography)\s*[:\-]\s*([A-Za-z0-9 ]{2,40})",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex KeywordPattern = new(
        @"\b(primary|secondary|accent|brand|background|text)\b",
        RegexOptions.NonBacktracking | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    /// <remarks>Never true: this importer reads extracted text, never the PDF bytes a registry offers.</remarks>
    public bool CanRead(string path, ReadOnlySpan<byte> head) => false;

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files)
    {
        var design = DesignDocument.New();
        var sources = new List<ImportSourceRef>();
        var diagnostics = new List<ImportDiagnostic>();
        var usedPaths = new HashSet<string>(StringComparer.Ordinal);
        int emitted = 0;

        try
        {
            if (files is not null)
            {
                foreach (ImportFile? file in files)
                {
                    if (file is null)
                        continue;

                    string path = file.Path ?? string.Empty;
                    sources.Add(new ImportSourceRef(path, Id, Sha256(file.Content.Span), file.ProjectFileId));
                    emitted += Parse(Decode(file.Content), path, design, usedPaths, diagnostics);
                }
            }
        }
        catch (Exception exception)
        {
            // "Never throw" outranks reporting: a structural surprise the walker did not anticipate.
            diagnostics.Add(Error("IMPORT_INTERNAL", string.Empty, exception.GetType().Name));
        }

        if (emitted == 0)
            diagnostics.Add(Warn("NO_TOKENS_RECOGNIZED", string.Empty, "no colour or font candidate was found in the PDF text"));

        return new DesignImportResult(emitted > 0 ? design : null, null, sources, diagnostics);
    }

    /// <summary>
    /// The route's entry point: parses one already-extracted text body. <paramref name="path"/> is the
    /// uploaded file's name, recorded on the source ref only.
    /// </summary>
    public DesignImportResult ImportText(string? text, string path = "brand.pdf")
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text ?? string.Empty);
        return Import([new ImportFile(path, bytes, string.Empty, null)]);
    }

    private static int Parse(
        string text,
        string path,
        JsonObject design,
        HashSet<string> usedPaths,
        List<ImportDiagnostic> diagnostics)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        int emitted = 0;

        foreach (Match match in HexPattern.Matches(text))
        {
            if (emitted >= MaxTokens)
                break;

            if (ColorConvert.TryToSrgbHex(match.Value, out string hex, out _)
                && AddColor(design, usedPaths, text, match.Index, match.Length, hex, path))
            {
                emitted++;
            }
        }

        foreach (Match match in RgbPattern.Matches(text))
        {
            if (emitted >= MaxTokens)
                break;

            if (TryChannels(match, out int r, out int g, out int b)
                && ColorConvert.TryToSrgbHex($"rgb({r}, {g}, {b})", out string hex, out _)
                && AddColor(design, usedPaths, text, match.Index, match.Length, hex, path))
            {
                emitted++;
            }
        }

        foreach (Match match in ChannelPattern.Matches(text))
        {
            if (emitted >= MaxTokens)
                break;

            if (TryChannels(match, out int r, out int g, out int b)
                && ColorConvert.TryToSrgbHex($"rgb({r}, {g}, {b})", out string hex, out _)
                && AddColor(design, usedPaths, text, match.Index, match.Length, hex, path))
            {
                emitted++;
            }
        }

        foreach (Match match in FontPattern.Matches(text))
        {
            if (emitted >= MaxTokens)
                break;

            string name = match.Groups[2].Value.Trim();
            if (!DesignText.IsSafeFontFamily(name))
            {
                diagnostics.Add(Info("UNSAFE_FONT", path, "a font name was dropped because it is not a safe family name"));
                continue;
            }

            string tokenPath = DesignKeys.Unique(usedPaths, "typography.family." + DesignKeys.Slug(name));
            var families = new JsonArray();
            families.Add(name);
            DesignDocument.SetToken(
                design, tokenPath, "fontFamily", families,
                new TokenProvenance("static", "brand-pdf", null, path), Confidence);
            emitted++;
        }

        return emitted;
    }

    /// <summary>Writes one colour token, biased to a role by proximity or parked under <c>color.extra</c>.</summary>
    private static bool AddColor(
        JsonObject design,
        HashSet<string> usedPaths,
        string text,
        int index,
        int length,
        string hex,
        string path)
    {
        string? role = NearestRole(text, index, length);
        string basePath = role is null
            ? "color.extra." + DesignKeys.Slug(NearbyName(text, index))
            : "color." + role;

        var value = new JsonObject { ["hex"] = hex, ["colorSpace"] = "srgb" };
        DesignDocument.SetToken(
            design,
            DesignKeys.Unique(usedPaths, basePath),
            "color",
            value,
            new TokenProvenance("static", "brand-pdf", null, path),
            Confidence);
        return true;
    }

    /// <summary>The role word nearest the colour within <see cref="ProximityWindow"/> characters, or null.</summary>
    private static string? NearestRole(string text, int index, int length)
    {
        int start = Math.Max(0, index - ProximityWindow);
        int end = Math.Min(text.Length, index + length + ProximityWindow);
        if (end <= start)
            return null;

        string window = text[start..end];
        int center = index + (length / 2) - start;

        string? role = null;
        int best = int.MaxValue;
        foreach (Match keyword in KeywordPattern.Matches(window))
        {
            string? candidate = RoleFor(keyword.Value);
            if (candidate is null)
                continue;

            int distance = Math.Abs(keyword.Index + (keyword.Length / 2) - center);
            if (distance >= best)
                continue;

            best = distance;
            role = candidate;
        }

        return role;
    }

    private static string? RoleFor(string keyword) => keyword.ToLowerInvariant() switch
    {
        "primary" or "brand" => "primary",
        "secondary" => "secondary",
        "accent" => "accent",
        "background" => "background",
        "text" => "text",
        _ => null,
    };

    /// <summary>The nearest preceding word on the colour's own line, so an unlabelled swatch still has a name.</summary>
    private static string NearbyName(string text, int index)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1));
        string prefix = text[(lineStart + 1)..index];

        string[] parts = prefix.Split(
            [' ', '\t', '\r', '|', ':', ',', ';', '(', ')', '[', ']', '/', '\\', '-', '_', '='],
            StringSplitOptions.RemoveEmptyEntries);

        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i].Length >= 2 && char.IsLetter(parts[i][0]))
                return parts[i];
        }

        return "color";
    }

    private static bool TryChannels(Match match, out int r, out int g, out int b)
    {
        r = 0;
        g = 0;
        b = 0;

        return int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out r)
            && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out g)
            && int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out b)
            && r <= 255
            && g <= 255
            && b <= 255;
    }

    private static string Decode(ReadOnlyMemory<byte> content)
    {
        if (content.Length == 0)
            return string.Empty;

        string text = Encoding.UTF8.GetString(content.Span);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static ImportDiagnostic Error(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Error, code, path, message);

    private static ImportDiagnostic Warn(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, code, path, message);

    private static ImportDiagnostic Info(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Info, code, path, message);
}
