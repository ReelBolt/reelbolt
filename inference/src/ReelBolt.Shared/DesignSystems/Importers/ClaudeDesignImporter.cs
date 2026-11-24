using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.DesignSystems.Brand;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// Reads a Claude Design export (a manifest, a set of token stylesheets, a <c>SKILL.md</c> and the
/// component sources) into a canonical <see cref="DesignDocument"/>. PURE: the caller supplies the
/// bytes, no file is ever executed, no URL is fetched, and every failure is a bounded
/// <see cref="ImportDiagnostic"/> rather than an exception.
///
/// <para>
/// The token half delegates to <see cref="CssThemeImporter"/> through its options seam: the
/// manifest's <c>themes</c> name the modes and <see cref="MapPath"/> is the data-driven mapper from
/// the export's custom-property names onto the paths the views read. The reader keys off the
/// format only — the manifest's fields, the selector shapes, the token name suffixes — never off a
/// customer name.
/// </para>
/// </summary>
public sealed class ClaudeDesignImporter : IDesignImporter
{
    /// <summary>The importer identifier.</summary>
    public const string ImporterId = "claude-design";

    /// <summary>The manifest file name that identifies an export.</summary>
    public const string ManifestFileName = "_ds_manifest.json";

    /// <summary>Info: the manifest's own counts, once it was read.</summary>
    public const string ManifestDiagnostic = "CLAUDE_DESIGN_MANIFEST";

    /// <summary>Warn: the manifest could not be read, so the importer falls back.</summary>
    public const string ManifestUnreadableDiagnostic = "CLAUDE_DESIGN_MANIFEST_UNREADABLE";

    /// <summary>Info: the mode names the manifest's themes produced.</summary>
    public const string ModesDiagnostic = "CLAUDE_DESIGN_MODES";

    /// <summary>Info: an entry of the export that is never read (scripts, markup, screenshots).</summary>
    public const string FileSkippedDiagnostic = "CLAUDE_DESIGN_FILE_SKIPPED";

    /// <summary>Warn: a <c>globalCssPaths</c> entry is not in the supplied files.</summary>
    public const string GlobalCssMissingDiagnostic = "GLOBAL_CSS_MISSING";

    /// <summary>Info: how many binary assets were selected and why the rest were skipped.</summary>
    public const string AssetsDiagnostic = "CLAUDE_DESIGN_ASSETS";

    /// <summary>Info: an image asset whose file name did not name a role and had no default slot left.</summary>
    public const string AssetRoleUnknownDiagnostic = "ASSET_ROLE_UNKNOWN";

    /// <summary>Warn: an image asset over the archive's per-entry byte limit.</summary>
    public const string AssetTooLargeDiagnostic = "ASSET_TOO_LARGE";

    /// <summary>Warn: an asset's declared SHA-256 does not match the bytes it carries.</summary>
    public const string AssetDigestMismatchDiagnostic = "ASSET_DIGEST_MISMATCH";

    /// <summary>Warn: an asset's bytes are not the image type its MIME type declares.</summary>
    public const string AssetTypeMismatchDiagnostic = "ASSET_TYPE_MISMATCH";

    /// <summary>Warn: the sanitizer refused an SVG, so it is never selected or stored.</summary>
    public const string SvgRefusedDiagnostic = "SVG_REFUSED";

    /// <summary>Info: an SVG was rebuilt from the allowlist and became an asset.</summary>
    public const string SvgSanitizedDiagnostic = "SVG_SANITIZED";
    public const string LogoNameToneMismatchDiagnostic = "LOGO_NAME_TONE_MISMATCH";

    /// <summary>Info: how many manifest components reached the product map.</summary>
    public const string ComponentsDiagnostic = "CLAUDE_DESIGN_COMPONENTS";

    /// <summary>Info: how many prose/guideline reference documents were kept and skipped.</summary>
    public const string ReferenceDiagnostic = "CLAUDE_DESIGN_REFERENCES";

    /// <summary>Warn: a reference document is over the per-document byte cap.</summary>
    public const string ReferenceTooLargeDiagnostic = "REFERENCE_TOO_LARGE";

    /// <summary>Warn: the reference set is over its document or total-byte cap.</summary>
    public const string ReferenceCapDiagnostic = "REFERENCE_CAP";

    /// <summary>Info: a credential was replaced in a reference document's text.</summary>
    public const string SecretRedactedDiagnostic = "SECRET_REDACTED";

    /// <summary>Info: the font families, local files and unfetched declarations one import found.</summary>
    public const string FontsDiagnostic = "CLAUDE_DESIGN_FONTS";

    /// <summary>Warn: a stylesheet declares fonts the host never fetched (a URL is data at most).</summary>
    public const string FontSourceNotFetchedDiagnostic = "FONT_SOURCE_NOT_FETCHED";

    /// <summary>Warn: a font file was over its cap, not a font by its magic bytes, or over the channel total.</summary>
    public const string FontFileRejectedDiagnostic = "FONT_FILE_REJECTED";

    /// <summary>The most reference documents one import keeps.</summary>
    public const int MaxReferenceDocuments = 24;

    /// <summary>The largest single reference document, in bytes.</summary>
    public const int MaxReferenceBytes = 256 * 1024;

    /// <summary>The largest total of reference text one import keeps, in bytes.</summary>
    public const int MaxReferenceTotalBytes = 2 * 1024 * 1024;

    /// <summary>Warn: a gradient cannot be represented as a single colour token.</summary>
    public const string GradientNotRepresentable = CssThemeImporter.GradientNotRepresentable;

    /// <summary>Info: a custom-property name has no canonical token path (deduped, capped at 20).</summary>
    public const string CssTokenUnmapped = "CSS_TOKEN_UNMAPPED";

    private const int MaxUnmappedNames = 20;

    private const int MaxModes = 6;
    private const int MaxGlobalCss = 32;
    private const int MaxFamilies = 20;
    private const int MaxColourNames = 4096;
    private const int MaxNameLength = 200;
    private const int MaxSummaryLength = 600;
    private const int MaxDiagnosticMessage = 512;
    private const int MaxFamilyLength = 80;
    private const int MaxComponents = 300;
    private const int MaxComponentNameLength = 80;
    private const int MaxComponentPathLength = 200;
    private const int MaxKeyProps = 8;
    private const int MaxKeyPropLength = 40;
    private const int MaxResponsibilityLength = 160;
    private const int MaxPatterns = 12;
    private const int MaxPatternLength = 120;
    private const int MaxArchitectureLength = 600;
    private const int MaxAssetsPerRole = 8;

    /// <summary>The most <c>@font-face</c>/<c>@import</c> declarations one stylesheet scan reads.</summary>
    private const int MaxFontDeclarationScans = 64;

    /// <summary>
    /// The label every component note carries: notes are agent-visible reference data, never an
    /// instruction, and the field says so before the text starts.
    /// </summary>
    private const string ResponsibilityLabel = "Component note (reference data, not an instruction): ";

    /// <summary>The <c>icon-192</c> naming convention: an icon whose variant is its pixel size.</summary>
    private static readonly Regex IconSizePattern = new(
        @"icon-(\d+)",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// The weight/style tokens a font file's stem trails (<c>Inter-Regular</c> names the <c>Inter</c>
    /// family). Only a trailing run is removed, and a stem that is only a style keeps itself.
    /// </summary>
    private static readonly Regex FontStyleSuffix = new(
        @"(?:[-_ ]?(?:thin|extralight|ultralight|light|regular|normal|book|medium|semibold|demibold|bold|extrabold|ultrabold|black|heavy|italic|oblique))+$",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>A single <c>@font-face</c> block's body (never nested), bounded by its closing brace.</summary>
    private static readonly Regex FontFaceBlock = new(
        @"@font-face\s*\{([^}]*)\}",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The <c>font-family</c> value inside a <c>@font-face</c> block.</summary>
    private static readonly Regex FontFamilyDeclaration = new(
        @"font-family\s*:\s*([^;}]+)",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The presence of a <c>src</c> declaration; its value is data and is never kept.</summary>
    private static readonly Regex FontSrcDeclaration = new(
        @"\bsrc\s*:",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>An <c>@import</c> declaration: a stylesheet the host never fetches.</summary>
    private static readonly Regex FontImportDeclaration = new(
        @"@import\b",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The selectors that open the base palette; a theme selector equal to one is not a mode.</summary>
    private static readonly string[] BaseSelectors = [":root", "html", ".light"];

    /// <summary>
    /// The canonical colour names, matched exactly after the vendor prefix is stripped (so
    /// <c>lines</c> and <c>table-lines</c> can never share <c>color.border</c>).
    /// </summary>
    private static readonly (string Name, string Path)[] ColourNames =
    [
        ("brand-violet-light", "color.brand.violet-light"),
        ("text-secondary", "color.text.muted"),
        ("brand-violet", "color.brand.violet"),
        ("text-primary", "color.text"),
        ("background", "color.background"),
        ("secondary", "color.brand.secondary"),
        ("tertiary", "color.brand.accent"),
        ("primary", "color.brand.primary"),
        ("surface", "color.surface"),
        ("success", "color.semantic.success"),
        ("warning", "color.semantic.warning"),
        ("error", "color.semantic.error"),
        ("info", "color.semantic.info"),
        ("lines", "color.border"),
    ];

    /// <inheritdoc />
    public string Id => ImporterId;

    /// <inheritdoc />
    public bool CanRead(string path, ReadOnlySpan<byte> head) =>
        !string.IsNullOrEmpty(path)
        && string.Equals(Basename(Normalize(path)), ManifestFileName, StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public DesignImportResult Import(IReadOnlyList<ImportFile> files) => ImportBundle(files, []).Import;

    /// <summary>
    /// Reads the export and returns the design document, the product map and the binary assets the
    /// reader kept. Asset selection and the component inventory are bounded, deterministic and
    /// generic: the file names and the manifest drive them, never a customer name.
    /// </summary>
    public ClaudeDesignBundle ImportBundle(
        IReadOnlyList<ImportFile> files, IReadOnlyList<ImportedAsset> assets)
    {
        IReadOnlyList<ImportFile> entries = files ?? [];
        (DesignImportResult import, IReadOnlyList<ImportedAsset> selected) =
            ImportDesign(entries, assets ?? []);

        // Prose is collected independently of the manifest: an unreadable manifest must not lose the
        // documentation a reader could still understand. The reference diagnostics join the import's
        // own, so the one redaction pass below covers them too.
        (IReadOnlyList<ImportedReferenceDocument> references, IReadOnlyList<ImportDiagnostic> referenceDiagnostics) =
            CollectReferences(entries);

        IReadOnlyList<ImportDiagnostic> diagnostics = import.Diagnostics is { Count: > 0 }
            ? [.. import.Diagnostics, .. referenceDiagnostics]
            : referenceDiagnostics;
        var merged = new DesignImportResult(import.Design, import.ProductMap, import.Sources, diagnostics);

        // One redaction pass over everything the reader produced — design, product map,
        // diagnostics and sources — exactly as the registry path does. Reference text is redacted
        // where it is collected, before it can reach this result.
        return new ClaudeDesignBundle(DesignImportRegistry.RedactResult(merged), selected)
        {
            ReferenceDocuments = references,
        };
    }

    private static (DesignImportResult Import, IReadOnlyList<ImportedAsset> Assets) ImportDesign(
        IReadOnlyList<ImportFile> files, IReadOnlyList<ImportedAsset> assets)
    {
        var diagnostics = new List<ImportDiagnostic>();
        var sources = new List<ImportSourceRef>();

        try
        {
            ImportFile? manifest = FindManifest(files);
            if (manifest is null)
            {
                IReadOnlyList<ImportedAsset> noAssets =
                    SelectAssets(files, assets, string.Empty, PublisherNamesFrom(files), diagnostics);
                return (new DesignImportResult(null, null, sources, diagnostics), noAssets);
            }

            ManifestData? data = ReadManifest(manifest, diagnostics);
            if (data is null)
            {
                IReadOnlyList<ImportedAsset> noAssets =
                    SelectAssets(files, assets, string.Empty, PublisherNamesFrom(files), diagnostics);
                return (new DesignImportResult(null, null, sources, diagnostics), noAssets);
            }

            sources.Add(Source(manifest));
            (string? skillName, string? skillSummary) = ReadSkill(files, sources, diagnostics);
            string? name = skillName ?? DesignText.Sanitize(data.Namespace, MaxNameLength);
            string? summary = ReadReadmeSummary(files, sources, diagnostics) ?? skillSummary;

            IReadOnlyList<ImportedAsset> selectedAssets =
                SelectAssets(files, assets, ProductSlug(data.Namespace, skillName), PublisherNamesFrom(files), diagnostics);

            List<ImportFile> cssFiles = ResolveCss(data.GlobalCssPaths, files, diagnostics);
            string? colourPrefix = DominantColourPrefix(data.ColourNames);
            var unmapped = new HashSet<string>(StringComparer.Ordinal);
            var unmappedOrder = new List<string>();
            var options = new CssImportOptions(
                data.ThemeSelectors,
                (bare, type) => MapPath(bare, type, colourPrefix, unmapped, unmappedOrder),
                TypeFor);
            DesignImportResult css = new CssThemeImporter().Import(cssFiles, options);
            JsonObject design = css.Design ?? DesignDocument.New();
            sources.AddRange(css.Sources);
            diagnostics.AddRange(css.Diagnostics);

            foreach (string unmappedName in unmappedOrder)
            {
                diagnostics.Add(Info(
                    CssTokenUnmapped,
                    manifest.Path,
                    $"'{DesignKeys.Clip(unmappedName)}' has no canonical token path and was not placed")
                    with
                { Axis = DesignImportAxis.DerivedTokens });
            }

            if (name is not null)
                design["name"] = name;

            if (summary is not null)
                design["voice"] = new JsonObject { ["summary"] = summary };

            AddFonts(design, selectedAssets, files, diagnostics);

            diagnostics.Add(Info(
                ManifestDiagnostic,
                manifest.Path,
                $"{data.ComponentCount} components, {data.TokenCount} tokens, {data.CardCount} preview cards, " +
                $"{data.StartingPointCount} starting points, {data.ThemeSelectors.Count} theme(s), " +
                $"{cssFiles.Count} global stylesheet(s)"));

            if (data.ThemeSelectors.Count > 0)
            {
                diagnostics.Add(Info(
                    ModesDiagnostic,
                    manifest.Path,
                    string.Join(", ", data.ThemeSelectors.Values.Distinct(StringComparer.Ordinal)))
                    with
                { Axis = DesignImportAxis.Modes });
            }

            ProductMapDocument productMap =
                BuildProductMap(data, manifest, files, cssFiles.Count, diagnostics);

            return (
                new DesignImportResult(design, productMap, sources, diagnostics),
                selectedAssets);
        }
        catch (Exception exception)
        {
            // Never throws: an unanticipated shape is a diagnostic, not an exception.
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Error, "IMPORT_INTERNAL", string.Empty, exception.GetType().Name));
            return (
                new DesignImportResult(null, null, sources, diagnostics),
                Array.Empty<ImportedAsset>());
        }
    }

    /// <summary>The manifest entry at any depth; the reference collector reports the paths it skips.</summary>
    private static ImportFile? FindManifest(IReadOnlyList<ImportFile> files)
    {
        ImportFile? manifest = null;
        foreach (ImportFile? file in files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
                continue;

            string path = Normalize(file.Path);
            if (manifest is null
                && string.Equals(Basename(path), ManifestFileName, StringComparison.OrdinalIgnoreCase))
            {
                manifest = file;
            }
        }

        return manifest;
    }

    private static ManifestData? ReadManifest(ImportFile manifest, List<ImportDiagnostic> diagnostics)
    {
        string path = Normalize(manifest.Path);
        if (manifest.Content.Length > ImportLimits.JsonMaxBytes)
        {
            diagnostics.Add(Unreadable(path, $"manifest is larger than the {ImportLimits.JsonMaxBytes} byte limit"));
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                manifest.Content, new JsonDocumentOptions { MaxDepth = ImportLimits.JsonMaxDepth });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Unreadable(path, "manifest root is not an object"));
                return null;
            }

            if (CountNodes(root, ImportLimits.JsonMaxNodes) > ImportLimits.JsonMaxNodes)
            {
                diagnostics.Add(Unreadable(path, $"manifest has more than {ImportLimits.JsonMaxNodes} nodes"));
                return null;
            }

            string? ns = root.TryGetProperty("namespace", out JsonElement namespaceElement)
                && namespaceElement.ValueKind == JsonValueKind.String
                ? namespaceElement.GetString()
                : null;

            var selectors = new Dictionary<string, string>(StringComparer.Ordinal);
            var modes = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("themes", out JsonElement themes) && themes.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement theme in themes.EnumerateArray())
                {
                    if (theme.ValueKind != JsonValueKind.Object
                        || !theme.TryGetProperty("selector", out JsonElement selectorElement)
                        || selectorElement.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    string selector = NormalizeSelector(selectorElement.GetString());
                    if (selector.Length == 0 || IsBaseSelector(selector))
                        continue;

                    string? label = theme.TryGetProperty("label", out JsonElement labelElement)
                        && labelElement.ValueKind == JsonValueKind.String
                        ? labelElement.GetString()
                        : null;
                    string mode = SlugMode(label ?? selector);
                    if (modes.Count >= MaxModes && !modes.Contains(mode))
                        continue;

                    modes.Add(mode);
                    selectors.TryAdd(selector, mode);
                }
            }

            var globalCss = new List<string>();
            if (root.TryGetProperty("globalCssPaths", out JsonElement globalCssElement)
                && globalCssElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in globalCssElement.EnumerateArray())
                {
                    if (globalCss.Count >= MaxGlobalCss)
                        break;

                    if (entry.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(entry.GetString()))
                        globalCss.Add(Normalize(entry.GetString()!));
                }
            }

            var colourNames = new List<string>();
            if (root.TryGetProperty("tokens", out JsonElement tokensElement)
                && tokensElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement token in tokensElement.EnumerateArray())
                {
                    if (colourNames.Count >= MaxColourNames)
                        break;

                    if (token.ValueKind != JsonValueKind.Object
                        || !token.TryGetProperty("kind", out JsonElement kind)
                        || kind.ValueKind != JsonValueKind.String
                        || !string.Equals(kind.GetString(), "color", StringComparison.OrdinalIgnoreCase)
                        || !token.TryGetProperty("name", out JsonElement tokenName)
                        || tokenName.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }

                    if (tokenName.GetString() is { Length: > 0 } name)
                        colourNames.Add(name);
                }
            }

            var components = new List<ManifestComponent>();
            if (root.TryGetProperty("components", out JsonElement componentsElement)
                && componentsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in componentsElement.EnumerateArray())
                {
                    if (components.Count >= MaxComponents)
                        break;

                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("name", out JsonElement componentName)
                        || componentName.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(componentName.GetString()))
                    {
                        continue;
                    }

                    string? sourcePath = entry.TryGetProperty("sourcePath", out JsonElement pathElement)
                        && pathElement.ValueKind == JsonValueKind.String
                        ? pathElement.GetString()
                        : null;
                    components.Add(new ManifestComponent(componentName.GetString()!, sourcePath));
                }
            }

            JsonArray? cards = null;
            if (root.TryGetProperty("cards", out JsonElement cardsElement)
                && cardsElement.ValueKind == JsonValueKind.Array)
            {
                cards = new JsonArray();
                foreach (JsonElement entry in cardsElement.EnumerateArray())
                {
                    // One past the cap is enough for the reader to report the overflow.
                    if (cards.Count > PreviewCardReader.MaxCards)
                        break;

                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    try
                    {
                        if (JsonNode.Parse(entry.GetRawText()) is JsonObject card)
                        {
                            cards.Add(card);
                        }
                    }
                    catch (JsonException)
                    {
                        // A card that cannot be cloned is not a card.
                    }
                }

                if (cards.Count == 0)
                    cards = null;
            }

            List<string> cardGroups = ReadStringField(root, "cards", "group");
            List<string> startingPointSections = ReadStringField(root, "startingPoints", "section");

            return new ManifestData(
                ns,
                CountArray(root, "components"),
                CountArray(root, "tokens"),
                CountArray(root, "cards"),
                CountArray(root, "startingPoints"),
                colourNames,
                selectors,
                globalCss,
                components,
                cards,
                cardGroups,
                startingPointSections);
        }
        catch (JsonException)
        {
            diagnostics.Add(Unreadable(path, "manifest is not valid JSON within the depth limit"));
            return null;
        }
        catch (Exception exception)
        {
            diagnostics.Add(Unreadable(path, "manifest could not be read: " + exception.GetType().Name));
            return null;
        }
    }

    /// <summary>
    /// The <c>SKILL.md</c> front matter: its <c>name</c> and <c>description</c>. Prose below the
    /// front matter is documentation, not design data, and is never read.
    /// </summary>
    private static (string? Name, string? Summary) ReadSkill(
        IReadOnlyList<ImportFile> files, List<ImportSourceRef> sources, List<ImportDiagnostic> diagnostics)
    {
        foreach (ImportFile? file in files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
                continue;

            if (!string.Equals(Basename(Normalize(file.Path)), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                continue;

            if (file.Content.Length > ImportLimits.MarkdownMaxBytes)
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    "TRUNCATED",
                    DesignKeys.Clip(file.Path),
                    $"SKILL.md is over the {ImportLimits.MarkdownMaxBytes} byte limit and was not read"));
                return (null, null);
            }

            sources.Add(Source(file));
            string text = Encoding.UTF8.GetString(file.Content.Span);
            if (!TryReadFrontMatter(text, out string? name, out string? description))
                return (null, null);

            return (DesignText.Sanitize(name, MaxNameLength), DesignText.Sanitize(description, MaxSummaryLength));
        }

        return (null, null);
    }

    private static bool TryReadFrontMatter(string text, out string? name, out string? description)
    {
        name = null;
        description = null;

        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
            return false;

        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (string.Equals(line.Trim(), "---", StringComparison.Ordinal))
                return true;

            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;

            string key = line[..colon].Trim();
            string value = Unquote(line[(colon + 1)..].Trim());
            if (string.Equals(key, "name", StringComparison.Ordinal))
                name = value;
            else if (string.Equals(key, "description", StringComparison.Ordinal))
                description = value;
        }

        return false;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }

    /// <summary>
    /// <c>voice.summary</c> from the export's <c>readme.md</c>: its first prose paragraph, which
    /// describes the brand rather than instructing a model.
    /// </summary>
    private static string? ReadReadmeSummary(
        IReadOnlyList<ImportFile> files, List<ImportSourceRef> sources, List<ImportDiagnostic> diagnostics)
    {
        foreach (ImportFile? file in files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
                continue;

            if (!string.Equals(Basename(Normalize(file.Path)), "readme.md", StringComparison.OrdinalIgnoreCase))
                continue;

            if (file.Content.Length > ImportLimits.MarkdownMaxBytes)
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    "TRUNCATED",
                    DesignKeys.Clip(file.Path),
                    $"readme.md is over the {ImportLimits.MarkdownMaxBytes} byte limit and was not read"));
                return null;
            }

            sources.Add(Source(file));
            string text = Encoding.UTF8.GetString(file.Content.Span);
            return DesignText.Sanitize(FirstProseParagraph(text), MaxSummaryLength);
        }

        return null;
    }

    /// <summary>
    /// The first contiguous non-empty paragraph of a Markdown document, skipping a leading front
    /// matter, headings, fences and blank lines. Returns null when there is no such paragraph.
    /// </summary>
    private static string? FirstProseParagraph(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int index = 0;

        while (index < lines.Length && lines[index].Trim().Length == 0)
            index++;

        if (index < lines.Length && string.Equals(lines[index].Trim(), "---", StringComparison.Ordinal))
        {
            index++;
            while (index < lines.Length && !string.Equals(lines[index].Trim(), "---", StringComparison.Ordinal))
                index++;
            index++;
        }

        var paragraph = new StringBuilder();
        bool fenced = false;

        for (; index < lines.Length; index++)
        {
            string trimmed = lines[index].Trim();

            if (trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (paragraph.Length > 0)
                    break;

                fenced = !fenced;
                continue;
            }

            if (fenced)
                continue;

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                if (paragraph.Length > 0)
                    break;

                continue;
            }

            if (paragraph.Length > 0)
                paragraph.Append(' ');
            paragraph.Append(trimmed);
        }

        return paragraph.Length == 0 ? null : paragraph.ToString();
    }

    private static List<string> ReadStringField(JsonElement root, string arrayName, string propertyName)
    {
        var values = new List<string>();
        if (!root.TryGetProperty(arrayName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return values;

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (values.Count >= MaxPatterns * 4)
                break;

            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty(propertyName, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text)
            {
                values.Add(text);
            }
        }

        return values;
    }

    /// <summary>
    /// The binary assets this reader keeps: images under an <c>assets/</c> segment, sized under the
    /// archive's per-entry limit, mapped to a role and variant from their file name. The order is
    /// the path order and the caps are per role and in total, so the same input always yields the
    /// same list.
    /// </summary>
    private static IReadOnlyList<ImportedAsset> SelectAssets(
        IReadOnlyList<ImportFile> files,
        IReadOnlyList<ImportedAsset> assets,
        string productSlug,
        IReadOnlyList<string> publisherNames,
        List<ImportDiagnostic> diagnostics)
    {
        var selected = new List<ImportedAsset>();
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int skipped = 0;
        bool logoDefault = false;

        void Skip(string reason)
        {
            skipped++;
            reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        }

        // The binary channel: raster logos and icons, plus the root preview thumbnail. The SVG
        // channel is separate (below) because a sanitized vector is rebuilt, never trusted.
        foreach (ImportedAsset asset in assets
                     .OfType<ImportedAsset>()
                     .OrderBy(static asset => Normalize(asset.Path), StringComparer.Ordinal))
        {
            string path = Normalize(asset.Path);
            if (!seenPaths.Add(path))
            {
                Skip("duplicate path");
                continue;
            }

            // The exact root `.thumbnail` is the preview card, not a brand asset under `assets/`.
            bool isThumbnail = string.Equals(path, ".thumbnail", StringComparison.Ordinal);
            bool isFont = string.Equals(asset.Role, "font", StringComparison.Ordinal);
            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            bool underAssets = segments.Any(static segment =>
                string.Equals(segment, "assets", StringComparison.OrdinalIgnoreCase));
            bool underFonts = segments.Any(static segment =>
                string.Equals(segment, "fonts", StringComparison.OrdinalIgnoreCase));

            // A font rides the same bounded channel under `fonts/` or `assets/`; every other binary
            // asset is a brand image under `assets/` (the exact root thumbnail is the preview card).
            if (isFont ? !underAssets && !underFonts : !isThumbnail && !underAssets)
            {
                Skip(isFont ? "not under fonts or assets" : "not under assets");
                continue;
            }

            string mime = (asset.MimeType ?? string.Empty).Trim().ToLowerInvariant();
            if (isFont)
            {
                int fontCount = selected.Count(static kept =>
                    string.Equals(kept.Role, "font", StringComparison.Ordinal));
                long fontBytes = selected
                    .Where(static kept => string.Equals(kept.Role, "font", StringComparison.Ordinal))
                    .Sum(static kept => (long)kept.Content.Length);

                if (asset.Content.Length > ArchiveFontLimits.MaxEntryBytes
                    || fontCount >= ArchiveFontLimits.MaxEntries
                    || fontBytes + asset.Content.Length > ArchiveFontLimits.MaxTotalBytes)
                {
                    Skip("font over cap");
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        FontFileRejectedDiagnostic,
                        DesignKeys.Clip(asset.Path),
                        ClipMessage(
                            $"font is {asset.Content.Length} bytes and would bring the channel to " +
                            $"{fontBytes + asset.Content.Length} bytes; the limits are " +
                            $"{ArchiveFontLimits.MaxEntryBytes} per entry, {ArchiveFontLimits.MaxEntries} entries, " +
                            $"{ArchiveFontLimits.MaxTotalBytes} total"))
                    { Axis = DesignImportAxis.Fonts });
                    continue;
                }

                // The declared MIME type is caller input too: the magic bytes name the kind.
                string? fontMime = DeriveFontMime(asset.Content.Span);
                if (fontMime is null || (mime.Length > 0 && !string.Equals(fontMime, mime, StringComparison.Ordinal)))
                {
                    Skip("not a font");
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        FontFileRejectedDiagnostic,
                        DesignKeys.Clip(asset.Path),
                        ClipMessage(
                            $"declared type is {mime}; the first bytes are " +
                            $"{(fontMime ?? "not a supported font")}"))
                    { Axis = DesignImportAxis.Fonts });
                    continue;
                }

                // Recompute the digest rather than trusting the caller's claim.
                string fontDigest = Convert.ToHexStringLower(SHA256.HashData(asset.Content.Span));
                string declaredFontDigest = (asset.Sha256 ?? string.Empty).Trim();
                if (declaredFontDigest.Length > 0
                    && !string.Equals(declaredFontDigest, fontDigest, StringComparison.OrdinalIgnoreCase))
                {
                    Skip("digest mismatch");
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        AssetDigestMismatchDiagnostic,
                        DesignKeys.Clip(asset.Path),
                        "declared SHA-256 does not match the content")
                    { Axis = DesignImportAxis.Fonts });
                    continue;
                }

                string? family = FontFamilySlug(Basename(path));
                if (family is null)
                {
                    Skip("unnamed font family");
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        FontFileRejectedDiagnostic,
                        DesignKeys.Clip(asset.Path),
                        "the file name does not name a usable font family")
                    { Axis = DesignImportAxis.Fonts });
                    continue;
                }

                selected.Add(asset with
                {
                    Role = "font",
                    Variant = family,
                    MimeType = fontMime,
                    Sha256 = fontDigest,
                });
                continue;
            }

            if (mime is not ("image/png" or "image/jpeg" or "image/webp"))
            {
                Skip("unsupported type");
                continue;
            }

            if (asset.Content.Length > ArchiveAssetLimits.MaxEntryBytes)
            {
                Skip("too large");
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    AssetTooLargeDiagnostic,
                    DesignKeys.Clip(asset.Path),
                    ClipMessage(
                        $"asset is {asset.Content.Length} bytes; the limit is {ArchiveAssetLimits.MaxEntryBytes}")));
                continue;
            }

            // The declared MIME type is caller input too: it must agree with the magic bytes.
            string? derivedMime = DeriveImageMime(asset.Content.Span);
            if (derivedMime is null || !string.Equals(derivedMime, mime, StringComparison.Ordinal))
            {
                Skip("type mismatch");
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    AssetTypeMismatchDiagnostic,
                    DesignKeys.Clip(asset.Path),
                    ClipMessage(
                        $"declared type is {mime}; the first bytes are {(derivedMime ?? "not a supported image")}")));
                continue;
            }

            // Recompute the digest rather than trusting the caller's claim.
            string digest = Convert.ToHexStringLower(SHA256.HashData(asset.Content.Span));
            string declaredDigest = (asset.Sha256 ?? string.Empty).Trim();
            if (declaredDigest.Length > 0
                && !string.Equals(declaredDigest, digest, StringComparison.OrdinalIgnoreCase))
            {
                Skip("digest mismatch");
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    AssetDigestMismatchDiagnostic,
                    DesignKeys.Clip(asset.Path),
                    "declared SHA-256 does not match the content"));
                continue;
            }

            // Fonts have their own channel cap above; this one bounds brand images.
            if (selected.Count(static kept =>
                    !string.Equals(kept.Role, "font", StringComparison.Ordinal)) >= ArchiveAssetLimits.MaxEntries)
            {
                Skip("entry cap");
                continue;
            }

            string basename = Basename(path);
            string? role;
            string? variant;
            if (isThumbnail)
            {
                role = "preview";
                variant = "thumbnail";
            }
            else
            {
                (role, variant) = InferRole(basename);

                if (role is null)
                {
                    if (logoDefault)
                    {
                        Skip("unknown role");
                        diagnostics.Add(Info(
                            AssetRoleUnknownDiagnostic,
                            asset.Path,
                            "no role could be inferred from the file name and the logo default is taken"));
                        continue;
                    }

                    role = "logo";
                    variant = "default";
                }

            }

            if (selected.Count(kept =>
                    string.Equals(kept.Role, role, StringComparison.Ordinal)) >= MaxAssetsPerRole)
            {
                Skip("role cap");
                continue;
            }

            ImportedAsset kept = asset with
            {
                Role = role,
                Variant = variant,
                MimeType = derivedMime,
                Sha256 = digest,
            };
            if (role == "logo" && !isThumbnail)
                kept = DescribeLogo(kept, basename, productSlug, publisherNames, diagnostics);

            selected.Add(kept);
            if (role == "logo" && kept.Variant == "default")
                logoDefault = true;
        }

        // The text channel: an `.svg` entry is sanitized and rebuilt, then carried like a raster.
        foreach (ImportFile file in files
                     .OfType<ImportFile>()
                     .OrderBy(static file => Normalize(file.Path), StringComparer.Ordinal))
        {
            string path = Normalize(file.Path);
            if (!path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seenPaths.Add(path))
            {
                Skip("duplicate path");
                continue;
            }

            string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (!segments.Any(static segment =>
                    string.Equals(segment, "assets", StringComparison.OrdinalIgnoreCase)))
            {
                Skip("not under assets");
                continue;
            }

            // The transport marker is the extension; the sanitizer is the authority on content. A
            // document that only looks like markup (a DTD, a prolog) is still handed to it so a
            // hostile file is refused with SVG_REFUSED rather than skipped as "not an svg".
            SvgSanitizeResult sanitized = SvgSanitizer.Sanitize(file.Content.Span);
            if (!sanitized.Ok)
            {
                Skip("unsafe svg");
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    SvgRefusedDiagnostic,
                    DesignKeys.Clip(file.Path),
                    ClipMessage(
                        $"{(sanitized.Code ?? SvgSanitizer.InvalidCode)}: " +
                        $"{(sanitized.Message ?? "the SVG could not be sanitized")}"))
                { Axis = DesignImportAxis.Assets });
                continue;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(sanitized.Sanitized!);
            string basename = Basename(path);
            (string? role, string? variant) = InferRole(basename);
            role ??= "logo";

            if (selected.Count(kept =>
                    string.Equals(kept.Role, role, StringComparison.Ordinal)) >= MaxAssetsPerRole)
            {
                Skip("role cap");
                continue;
            }

            var vectorAsset = new ImportedAsset(
                role,
                variant,
                DesignText.Sanitize(basename, MaxNameLength) ?? "asset.svg",
                path,
                "image/svg+xml",
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                bytes);
            if (role == "logo")
                vectorAsset = DescribeLogo(vectorAsset, basename, productSlug, publisherNames, diagnostics);

            selected.Add(vectorAsset);

            diagnostics.Add(Info(
                SvgSanitizedDiagnostic,
                DesignKeys.Clip(file.Path),
                ClipMessage(
                    $"{sanitized.ElementsDropped} element(s) and {sanitized.AttributesDropped} attribute(s) dropped"))
                with
            { Axis = DesignImportAxis.Assets });
        }

        ResolveLogoBase(selected);

        string reasonText = reasons.Count == 0
            ? "nothing skipped"
            : string.Join(", ", reasons
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => $"{entry.Value} {entry.Key}"));
        diagnostics.Add(Info(
            AssetsDiagnostic,
            string.Empty,
            $"{selected.Count} asset(s) selected; {skipped} skipped ({reasonText})")
            with
        { Axis = DesignImportAxis.Assets });

        return selected;
    }

    /// <summary>
    /// Classifies a logo (whose it is, its form) and measures its ink tone on the stored bytes. A publisher
    /// mark gets the variant <c>publisher</c>; a product or unknown logo keeps its name-derived variant.
    /// The tone comes only from measurement; when nothing can be measured the file-name light/dark token is
    /// the fallback, and a measured tone that contradicts that token is an Info diagnostic.
    /// </summary>
    private static ImportedAsset DescribeLogo(
        ImportedAsset asset,
        string basename,
        string productSlug,
        IReadOnlyList<string> publisherNames,
        List<ImportDiagnostic> diagnostics)
    {
        (string subject, string form, string? hint) =
            BrandLogoClassifier.Classify(basename, productSlug, publisherNames);
        LogoTone? measured = BrandLogoProfiler.Profile(asset.Content.Span, asset.MimeType);

        string? tone = null;
        double? luminance = null;
        IReadOnlyList<string>? readableOn = null;
        if (measured is not null)
        {
            tone = measured.Tone;
            luminance = measured.Luminance;
            var readable = new List<string>(2);
            if (measured.ReadableOnLight) readable.Add("light");
            if (measured.ReadableOnDark) readable.Add("dark");
            readableOn = readable;

            if ((hint == "light" && tone == LogoTone.Dark) || (hint == "dark" && tone == LogoTone.Light))
            {
                diagnostics.Add(Info(
                    LogoNameToneMismatchDiagnostic,
                    DesignKeys.Clip(asset.Path),
                    ClipMessage($"the file name says '{hint}' but the logo measures as {tone} ink; the measurement is kept"))
                    with
                { Axis = DesignImportAxis.Assets });
            }
        }
        else if (hint is not null)
        {
            // Name fallback: a -light token reads as light ink (for a dark background), -dark as dark ink.
            tone = hint;
            readableOn = hint == "light" ? ["dark"] : ["light"];
        }

        return asset with
        {
            Variant = subject == BrandLogoClassifier.SubjectPublisher ? "publisher" : asset.Variant,
            Subject = subject,
            Form = form,
            Tone = tone,
            ReadableOn = readableOn,
            Luminance = luminance,
        };
    }

    /// <summary>
    /// Owner names the export's own prose mentions (readme.md and SKILL.md), by generic patterns only.
    /// </summary>
    private static IReadOnlyList<string> PublisherNamesFrom(IReadOnlyList<ImportFile> files)
    {
        var names = new List<string>();
        foreach (ImportFile? file in files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path) || file.Content.Length > ImportLimits.MarkdownMaxBytes)
                continue;

            string basename = Basename(Normalize(file.Path));
            if (!string.Equals(basename, "readme.md", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(basename, "skill.md", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (string name in BrandLogoNames.PublisherNamesFrom(Encoding.UTF8.GetString(file.Content.Span)))
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase))
                    names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// The role and variant a file name names, from the same conventions for rasters and vectors.
    /// A <c>null</c> role means the name was roleless; a <c>default</c> logo variant is the base slot.
    /// </summary>
    private static (string? Role, string? Variant) InferRole(string basename)
    {
        string lower = basename.ToLowerInvariant();

        if (lower.Contains("maskable", StringComparison.Ordinal))
            return ("icon", "maskable");
        if (lower.Contains("apple-touch", StringComparison.Ordinal))
            return ("icon", "apple-touch");
        if (lower.Contains("favicon", StringComparison.Ordinal))
            return ("icon", "favicon");
        if (IconSizePattern.Match(lower) is { Success: true } size)
            return ("icon", size.Groups[1].Value);
        if (lower.Contains("app-icon", StringComparison.Ordinal)
            && lower.Contains("rounded", StringComparison.Ordinal))
            return ("icon", "rounded");
        if (lower.Contains("app-icon", StringComparison.Ordinal))
            return ("icon", "default");
        if (lower.Contains("wordmark", StringComparison.Ordinal))
            return ("logo", "wordmark");
        if (lower.Contains("logo", StringComparison.Ordinal)
            && lower.Contains("light", StringComparison.Ordinal))
            return ("logo", "light");
        if (lower.Contains("logo", StringComparison.Ordinal)
            && lower.Contains("dark", StringComparison.Ordinal))
            return ("logo", "dark");
        if (lower.Contains("logo", StringComparison.Ordinal))
            return ("logo", "default");

        return (null, null);
    }

    /// <summary>
    /// Applies the base-mark rule to the selected logo assets. A roleless vector takes the base slot
    /// when no raster logo competes for it, or when a raster logo's stem equals the vector's stem
    /// after one trailing <c>-light|-dark|-icon|-mark|-vector</c> token is removed (the same mark in
    /// two formats). The raster is kept as a variant when the SVG is promoted.
    /// </summary>
    private static void ResolveLogoBase(List<ImportedAsset> selected)
    {
        // A publisher or unknown logo is never the base slot while a product logo exists.
        bool productLogoExists = selected.Any(static asset =>
            asset.Role == "logo" && asset.Subject == BrandLogoClassifier.SubjectProduct);
        if (productLogoExists)
        {
            for (int demote = 0; demote < selected.Count; demote++)
            {
                ImportedAsset candidate = selected[demote];
                if (candidate.Role == "logo"
                    && candidate.Variant == "default"
                    && candidate.Subject is not (null or BrandLogoClassifier.SubjectProduct))
                {
                    selected[demote] = candidate with
                    {
                        Variant = NextVariant(selected, "logo", candidate.Subject!),
                    };
                }
            }
        }

        bool rasterLogoExists = selected.Any(asset =>
            asset.Role == "logo" && !IsVector(asset));

        for (int index = 0; index < selected.Count; index++)
        {
            ImportedAsset vector = selected[index];
            if (vector.Role != "logo" || !IsVector(vector))
                continue;

            string? assigned = vector.Variant;
            if (assigned is not (null or "default"))
                continue;
            if (productLogoExists && vector.Subject != BrandLogoClassifier.SubjectProduct)
                continue; // an explicitly named variant (light/dark/wordmark/publisher) is not a base

            bool defaultTakenByAnother = selected.Where((_, i) => i != index).Any(asset =>
                asset.Role == "logo" && asset.Variant == "default");

            ImportedAsset? matchingRaster = null;
            if (rasterLogoExists && StripVariantToken(Stem(vector.Path)) is { } stripped)
            {
                // The token is what proves the two files are the same mark exported twice: a bare
                // stem names no mark on its own, so it cannot promote the raster it collides with.
                matchingRaster = selected.FirstOrDefault(asset =>
                    asset.Role == "logo"
                    && !IsVector(asset)
                    && string.Equals(Stem(asset.Path), stripped, StringComparison.Ordinal));
            }

            bool promote = rasterLogoExists
                ? matchingRaster is not null
                : !defaultTakenByAnother;

            if (!promote)
            {
                selected[index] = vector with { Variant = NextVariant(selected, "logo", "vector") };
                continue;
            }

            if (matchingRaster is not null && matchingRaster.Variant == "default")
            {
                for (int rasterIndex = 0; rasterIndex < selected.Count; rasterIndex++)
                {
                    if (!ReferenceEquals(selected[rasterIndex], matchingRaster))
                        continue;

                    selected[rasterIndex] = matchingRaster with
                    {
                        Variant = NextVariant(selected, "logo", "raster"),
                    };
                    break;
                }
            }

            selected[index] = vector with { Variant = "default" };
        }
    }

    private static bool IsVector(ImportedAsset asset) =>
        string.Equals(asset.MimeType, "image/svg+xml", StringComparison.Ordinal);

    /// <summary>The file stem, lowercased and without its extension, used by the base-mark rule.</summary>
    private static string Stem(string path) =>
        System.IO.Path.GetFileNameWithoutExtension(Normalize(path)).ToLowerInvariant();

    private static readonly string[] VariantTokens =
        ["-light", "-dark", "-icon", "-mark", "-vector"];

    /// <summary>
    /// Removes one trailing variant token; a stem without one is <c>null</c> because it carries no
    /// evidence that the vector and a raster are the same mark in two formats.
    /// </summary>
    private static string? StripVariantToken(string stem)
    {
        foreach (string token in VariantTokens)
        {
            if (stem.EndsWith(token, StringComparison.Ordinal))
                return stem[..^token.Length];
        }

        return null;
    }

    /// <summary>The first free <paramref name="stem"/> / <c>stem-2</c> / … variant of a role.</summary>
    private static string NextVariant(List<ImportedAsset> selected, string role, string stem)
    {
        string candidate = stem;
        int suffix = 2;
        while (selected.Any(asset =>
                   string.Equals(asset.Role, role, StringComparison.Ordinal)
                   && string.Equals(asset.Variant, candidate, StringComparison.Ordinal)))
        {
            candidate = $"{stem}-{suffix++}";
        }

        return candidate;
    }

    /// <summary>
    /// The product slug the publisher rule keys off: the manifest namespace before
    /// <c>DesignSystem</c>, else the <c>SKILL.md</c> name's hyphen tokens minus the generic words.
    /// Normalised to <c>[a-z0-9]</c>; empty when neither source names a product.
    /// </summary>
    private static string ProductSlug(string? ns, string? skillName)
    {
        if (!string.IsNullOrWhiteSpace(ns))
        {
            string text = ns.Trim();
            int marker = text.IndexOf("DesignSystem", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
                text = text[..marker];

            string slug = NormalizeLetters(text);
            if (slug.Length > 0)
                return slug;
        }

        string fromName = BrandLogoNames.ProductSlugFrom(skillName);
        if (fromName.Length > 0)
            return fromName;

        return string.Empty;
    }

    private static string NormalizeLetters(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// One <see cref="ProductMapComponent"/> per manifest entry. Props come from the sibling
    /// <c>.d.ts</c>, the responsibility from the first prose line of the sibling
    /// <c>.prompt.md</c>; a component with neither still appears with empty values.
    /// </summary>
    private static ProductMapDocument BuildProductMap(
        ManifestData data,
        ImportFile manifest,
        IReadOnlyList<ImportFile> files,
        int globalStylesheetCount,
        List<ImportDiagnostic> diagnostics)
    {
        var byPath = new Dictionary<string, ImportFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ImportFile? file in files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
                continue;
            byPath[Normalize(file.Path)] = file;
        }

        var components = new List<ProductMapComponent>();
        var componentSources = new List<string>();
        var sourceSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var textCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (ManifestComponent item in data.Components)
        {
            if (components.Count >= MaxComponents)
                break;

            string? componentName = DesignText.Sanitize(item.Name, MaxComponentNameLength);
            if (componentName is null)
                continue;

            string sourcePath = Normalize(item.SourcePath ?? string.Empty);
            string path = DesignText.Sanitize(sourcePath, MaxComponentPathLength) ?? string.Empty;
            bool core = sourcePath.Contains("components/core/", StringComparison.OrdinalIgnoreCase);

            (string? dtsPath, string? promptPath) = SiblingPaths(sourcePath);
            ImportFile? dts = dtsPath is not null && byPath.TryGetValue(dtsPath, out ImportFile? dtsFile)
                ? dtsFile
                : null;
            ImportFile? prompt = promptPath is not null
                && byPath.TryGetValue(promptPath, out ImportFile? promptFile)
                ? promptFile
                : null;

            string? dtsText = ReadSiblingText(dts, textCache, diagnostics);
            string? promptText = ReadSiblingText(prompt, textCache, diagnostics);

            IReadOnlyList<string> keyProps = dtsText is null
                ? []
                : ReadKeyProps(dtsText, componentName);
            string responsibility = promptText is null
                ? string.Empty
                : ReadResponsibility(promptText);

            components.Add(new ProductMapComponent(
                componentName, path, responsibility, keyProps, core, "static"));

            if (dts is not null && sourceSeen.Add(Normalize(dts.Path)))
                componentSources.Add(Normalize(dts.Path));
            if (prompt is not null && sourceSeen.Add(Normalize(prompt.Path)))
                componentSources.Add(Normalize(prompt.Path));
        }

        string namespaceText =
            DesignText.Sanitize(data.Namespace, MaxComponentNameLength) ?? "unknown";
        string architecture =
            $"Claude Design export \"{namespaceText}\": {data.ComponentCount} components, " +
            $"{data.CardCount} preview cards, {data.StartingPointCount} starting points, " +
            $"{data.ThemeSelectors.Count} themes, {globalStylesheetCount} global stylesheets.";
        if (architecture.Length > MaxArchitectureLength)
            architecture = architecture[..(MaxArchitectureLength - 3)] + "...";

        var patterns = new List<string>();
        var patternSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddPattern(string? value)
        {
            if (patterns.Count >= MaxPatterns || string.IsNullOrWhiteSpace(value))
                return;

            string? cleaned = StyleNotesSanitizer.Sanitize(value);
            if (cleaned is null)
                return;

            string pattern = CapText(cleaned, MaxPatternLength);
            if (pattern.Length > 0 && patternSeen.Add(pattern))
                patterns.Add(pattern);
        }

        foreach (string group in data.CardGroups)
            AddPattern(group);
        foreach (string section in data.StartingPointSections)
            AddPattern(section);

        var framework = new ProductMapFramework(
            "other", string.Empty, "javascript", ["css"], "unknown", "design-system");
        var dependencies = new ProductMapDependencies([], [], []);
        var provenance = new Dictionary<string, ProductMapProvenance>(StringComparer.Ordinal)
        {
            ["overview"] = new("static", [Normalize(manifest.Path)]),
            ["components"] = new("static", componentSources),
        };

        diagnostics.Add(Info(
            ComponentsDiagnostic, manifest.Path, $"{components.Count} component(s)")
            with
        { Axis = DesignImportAxis.Components });

        // Preview cards join the manifest's card entries with the markup a card names. The reader
        // filters to markup files and never executes or fetches anything.
        IReadOnlyList<ProductMapPreviewCard> previewCards =
            PreviewCardReader.Read(files, data.Cards, diagnostics);

        return new ProductMapDocument(
            ProductMapDocument.SchemaId,
            null,
            framework,
            architecture,
            null,
            [],
            [],
            [],
            components,
            dependencies,
            patterns,
            provenance,
            previewCards);
    }

    private static (string? Dts, string? Prompt) SiblingPaths(string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath))
            return (null, null);

        int slash = sourcePath.LastIndexOf('/');
        int dot = sourcePath.LastIndexOf('.');
        string stem = dot > slash ? sourcePath[..dot] : sourcePath;
        return (stem + ".d.ts", stem + ".prompt.md");
    }

    /// <summary>
    /// The property names of <c>interface &lt;Name&gt;Props</c> / <c>type &lt;Name&gt;Props</c>,
    /// in declaration order, bounded to eight. Comments and nested braces are ignored.
    /// </summary>
    private static IReadOnlyList<string> ReadKeyProps(string text, string componentName)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string declaration = componentName + "Props";
        int start = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Contains("interface " + declaration, StringComparison.Ordinal)
                || trimmed.Contains("type " + declaration, StringComparison.Ordinal))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
            return [];

        var props = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int depth = 1;

        for (int i = start + 1; i < lines.Length && props.Count < MaxKeyProps; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
                continue;

            if (depth <= 1 && trimmed.StartsWith('}'))
                break;

            if (!trimmed.StartsWith("//", StringComparison.Ordinal)
                && !trimmed.StartsWith("/*", StringComparison.Ordinal)
                && !trimmed.StartsWith('*'))
            {
                int colon = trimmed.IndexOf(':');
                if (colon > 0)
                {
                    string candidate = trimmed[..colon].Trim().TrimEnd('?').Trim();
                    if (IsIdentifier(candidate) && seen.Add(candidate))
                    {
                        string? prop = DesignText.Sanitize(candidate, MaxKeyPropLength);
                        if (prop is not null)
                            props.Add(prop);
                    }
                }
            }

            depth += CountChar(trimmed, '{') - CountChar(trimmed, '}');
            if (depth <= 0)
                break;
        }

        return props;
    }

    private static string ReadResponsibility(string text)
    {
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0
                || trimmed.StartsWith('#')
                || trimmed.StartsWith("```", StringComparison.Ordinal)
                || string.Equals(trimmed, "---", StringComparison.Ordinal))
            {
                continue;
            }

            // The note is agent-visible reference data: clean it of markup, cap the whole field at
            // its limit, and keep the "not an instruction" label in front of the text.
            string? note = StyleNotesSanitizer.Sanitize(trimmed);
            if (note is null)
                return string.Empty;

            int room = MaxResponsibilityLength - ResponsibilityLabel.Length;
            if (room <= 0)
                return string.Empty;

            note = CapText(note, room);
            return note.Length == 0 ? string.Empty : ResponsibilityLabel + note;
        }

        return string.Empty;
    }

    /// <summary>
    /// The decoded text of one sibling file, decoded once per path and bounded by the markdown
    /// limit. <c>null</c> means "not read": absent, or over the limit (one Warn per path, even when
    /// the manifest names the same component repeatedly).
    /// </summary>
    private static string? ReadSiblingText(
        ImportFile? file, Dictionary<string, string?> cache, List<ImportDiagnostic> diagnostics)
    {
        if (file is null)
            return null;

        string path = Normalize(file.Path);
        if (cache.TryGetValue(path, out string? cached))
            return cached;

        string? text = null;
        if (file.Content.Length > ImportLimits.MarkdownMaxBytes)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Warn,
                "TRUNCATED",
                DesignKeys.Clip(file.Path),
                $"file is over the {ImportLimits.MarkdownMaxBytes} byte limit and was not read"));
        }
        else
        {
            text = Encoding.UTF8.GetString(file.Content.Span);
        }

        cache[path] = text;
        return text;
    }

    /// <summary>The image MIME the magic bytes identify, or null when the bytes are none of the three.</summary>
    private static string? DeriveImageMime(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 8
            && content[0] == 0x89 && content[1] == 0x50 && content[2] == 0x4E && content[3] == 0x47
            && content[4] == 0x0D && content[5] == 0x0A && content[6] == 0x1A && content[7] == 0x0A)
        {
            return "image/png";
        }

        if (content.Length >= 3
            && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (content.Length >= 12
            && content[0] == 'R' && content[1] == 'I' && content[2] == 'F' && content[3] == 'F'
            && content[8] == 'W' && content[9] == 'E' && content[10] == 'B' && content[11] == 'P')
        {
            return "image/webp";
        }

        return null;
    }

    /// <summary>The font MIME the magic bytes identify, or null when the bytes are none of the four.</summary>
    private static string? DeriveFontMime(ReadOnlySpan<byte> content)
    {
        if (content.Length >= 4
            && content[0] == 0x77 && content[1] == 0x4F && content[2] == 0x46 && content[3] == 0x32)
        {
            return "font/woff2";
        }

        if (content.Length >= 4
            && content[0] == 0x77 && content[1] == 0x4F && content[2] == 0x46 && content[3] == 0x46)
        {
            return "font/woff";
        }

        if (content.Length >= 4
            && content[0] == 0x4F && content[1] == 0x54 && content[2] == 0x54 && content[3] == 0x4F)
        {
            return "font/otf";
        }

        if (content.Length >= 4
            && content[0] == 0x00 && content[1] == 0x01 && content[2] == 0x00 && content[3] == 0x00)
        {
            return "font/ttf";
        }

        return null;
    }

    /// <summary>
    /// The family a font file's stem names, with a trailing weight/style run removed:
    /// <c>Inter-Regular.woff2</c> → <c>Inter</c>, <c>Roboto-BoldItalic</c> → <c>Roboto</c>. The result
    /// is sanitized and must be a plausible family, else null (the file is refused).
    /// </summary>
    private static string? FontFamilySlug(string basename)
    {
        string stem = basename;
        int dot = stem.LastIndexOf('.');
        if (dot > 0)
            stem = stem[..dot];

        string family = FontStyleSuffix.Replace(stem, string.Empty).Trim(' ', '-', '_');
        if (family.Length == 0)
            family = stem;

        string? sanitized = DesignText.Sanitize(family, MaxFamilyLength);
        return sanitized is not null && DesignText.IsSafeFontFamily(sanitized) ? sanitized : null;
    }

    private static string CapText(string text, int max)
    {
        if (text.Length <= max)
            return text;

        int cut = max;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            cut--;

        return text[..cut].TrimEnd();
    }

    private static bool IsIdentifier(string value)
    {
        if (value.Length == 0)
            return false;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            bool letter = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
            bool digit = c is >= '0' and <= '9';
            if (!(letter || digit || c is '_' or '$'))
                return false;
            if (i == 0 && digit)
                return false;
        }

        return true;
    }

    private static int CountChar(string text, char value)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c == value)
                count++;
        }

        return count;
    }

    /// <summary>The manifest's stylesheets, or every <c>tokens/</c> stylesheet when it names none.</summary>
    private static List<ImportFile> ResolveCss(
        IReadOnlyList<string> requested, IReadOnlyList<ImportFile> files, List<ImportDiagnostic> diagnostics)
    {
        var byPath = new Dictionary<string, ImportFile>(StringComparer.OrdinalIgnoreCase);
        foreach (ImportFile? file in files)
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
                continue;

            byPath[Normalize(file.Path)] = file;
        }

        var result = new List<ImportFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (requested.Count > 0)
        {
            foreach (string path in requested)
            {
                if (result.Count >= MaxGlobalCss)
                    break;

                if (byPath.TryGetValue(path, out ImportFile? file))
                {
                    if (seen.Add(path))
                        result.Add(file);
                }
                else
                {
                    diagnostics.Add(new ImportDiagnostic(
                        ImportDiagnosticSeverity.Warn,
                        GlobalCssMissingDiagnostic,
                        DesignKeys.Clip(path),
                        $"manifest names '{DesignKeys.Clip(path)}', which is not in the archive"));
                }
            }

            return result;
        }

        foreach (ImportFile? file in files)
        {
            if (result.Count >= MaxGlobalCss)
                break;
            if (file is null || string.IsNullOrEmpty(file.Path))
                continue;

            string path = Normalize(file.Path);
            if (path.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && IsUnderTokens(path) && seen.Add(path))
                result.Add(file);
        }

        return result;
    }

    /// <summary>
    /// The data-driven path mapper: the export's custom-property names onto the canonical dot paths.
    /// A colour name is matched exactly after the manifest's dominant vendor prefix is stripped, so
    /// no two values collide on one path; an unmapped token keeps its own slugged path. An empty
    /// list means "skip".
    /// </summary>
    private static IReadOnlyList<string> MapPath(
        string bare,
        string type,
        string? colourPrefix,
        HashSet<string>? unmapped = null,
        List<string>? unmappedOrder = null)
    {
        if (string.IsNullOrEmpty(bare))
            return [];

        string name = bare.ToLowerInvariant();

        switch (type)
        {
            case "color":
                string stripped = name;
                if (colourPrefix is not null
                    && name.StartsWith(colourPrefix + "-", StringComparison.Ordinal))
                {
                    stripped = name[(colourPrefix.Length + 1)..];
                }

                foreach ((string canonical, string path) in ColourNames)
                {
                    if (string.Equals(stripped, canonical, StringComparison.Ordinal))
                        return [path];
                }

                return ["color." + DesignKeys.Slug(stripped)];

            case "fontFamily":
                if (string.Equals(name, "font-sans", StringComparison.Ordinal))
                    return ["typography.family.primary", "typography.family.body"];

                if (string.Equals(name, "font-mono", StringComparison.Ordinal))
                    return ["typography.family.mono"];

                if (name.StartsWith("font-", StringComparison.Ordinal))
                    return ["typography.family." + DesignKeys.Slug(name[5..])];

                return Unmapped(name, unmapped, unmappedOrder);
        }

        if (name.StartsWith("type-", StringComparison.Ordinal))
        {
            string rest = name[5..];
            if (rest.EndsWith("-transform", StringComparison.Ordinal))
                return ["typography.transform." + Segments(rest[..^10])];
            if (rest.EndsWith("-size", StringComparison.Ordinal))
                return ["typography.size." + Segments(rest[..^5])];
            if (rest.EndsWith("-weight", StringComparison.Ordinal))
                return ["typography.weight." + Segments(rest[..^7])];
            if (rest.EndsWith("-lh", StringComparison.Ordinal))
                return ["typography.line-height." + Segments(rest[..^3])];
            if (rest.EndsWith("-ls", StringComparison.Ordinal))
                return ["typography.letter-spacing." + Segments(rest[..^3])];

            return Unmapped(name, unmapped, unmappedOrder);
        }

        if (name.StartsWith("space-", StringComparison.Ordinal))
            return ["spacing." + Segments(name[6..])];
        if (name.StartsWith("radius-", StringComparison.Ordinal))
            return ["radius." + Segments(name[7..])];
        if (name.StartsWith("shadow-", StringComparison.Ordinal))
            return ["shadow." + Segments(name[7..])];
        if (name.StartsWith("dur-", StringComparison.Ordinal))
            return ["motion.duration." + Segments(name[4..])];
        if (name.StartsWith("ease-", StringComparison.Ordinal))
            return ["motion.easing." + Segments(name[5..])];
        if (name.StartsWith("z-", StringComparison.Ordinal))
            return ["z." + DesignKeys.Slug(name[2..])];
        if (name is "appbar-height" or "content-cap" or "touch-target" or "fab-size"
            || name.StartsWith("drawer-width", StringComparison.Ordinal))
        {
            return ["spacing." + Segments(name)];
        }

        return Unmapped(name, unmapped, unmappedOrder);
    }

    /// <summary>
    /// Records a deduped, capped unmapped name and answers the empty path list, so the caller can
    /// return it directly without losing the diagnostic.
    /// </summary>
    private static IReadOnlyList<string> Unmapped(
        string name,
        HashSet<string>? unmapped,
        List<string>? unmappedOrder)
    {
        if (unmapped is not null
            && unmappedOrder is not null
            && unmappedOrder.Count < MaxUnmappedNames
            && unmapped.Add(name))
        {
            unmappedOrder.Add(name);
        }

        return [];
    }

    /// <summary>
    /// The type the CSS value cannot reveal by itself: a bare <c>400</c> weight, a bare
    /// <c>1.167</c> line height, a z-index integer and a text-transform keyword.
    /// </summary>
    private static string? TypeFor(string bare)
    {
        if (bare.EndsWith("-weight", StringComparison.Ordinal))
            return "fontWeight";
        if (bare.EndsWith("-lh", StringComparison.Ordinal))
            return "number";
        if (bare.StartsWith("z-", StringComparison.Ordinal))
            return "number";
        if (bare.StartsWith("type-", StringComparison.Ordinal)
            && bare.EndsWith("-transform", StringComparison.Ordinal))
        {
            return "keyword";
        }

        return null;
    }

    /// <summary>
    /// <c>fonts.licence</c> is "unknown" for every requested family (the export ships names, not
    /// files); <c>fonts.resolved</c> only names a bundled face, so the validator accepts it. The
    /// families come from the typography tokens, the stylesheets' <c>@font-face</c> declarations
    /// (whose <c>src</c> is never kept) and the local font files that arrived on the binary channel.
    /// One <c>FONT_SOURCE_NOT_FETCHED</c> reports the declarations the host did not fetch.
    /// </summary>
    private static void AddFonts(
        JsonObject design,
        IReadOnlyList<ImportedAsset> selectedAssets,
        IReadOnlyList<ImportFile> files,
        List<ImportDiagnostic> diagnostics)
    {
        var families = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (design["typography"] is JsonObject typography && typography["family"] is JsonObject familyGroup)
            CollectFamilies(familyGroup, families, seen);

        (List<string> declared, int notFetched) = ScanFontDeclarations(files);
        foreach (string family in declared)
        {
            if (families.Count >= MaxFamilies)
                break;

            if (seen.Add(family))
                families.Add(family);
        }

        int localFonts = 0;
        foreach (ImportedAsset asset in selectedAssets
                     .Where(static asset => string.Equals(asset.Role, "font", StringComparison.Ordinal))
                     .OrderBy(static asset => asset.Variant, StringComparer.Ordinal))
        {
            localFonts++;
            string? family = DesignText.Sanitize(asset.Variant, MaxFamilyLength);
            if (family is null || !DesignText.IsSafeFontFamily(family) || families.Count >= MaxFamilies)
                continue;

            if (seen.Add(family))
                families.Add(family);
        }

        if (families.Count > 0)
        {
            var licence = new JsonObject();
            var resolved = new JsonObject();
            foreach (string family in families)
            {
                licence[family] = "unknown";
                (string font, _) = BrandFontMatcher.NearestSandboxFont([family]);
                if (DesignSystemFonts.BundledNames.Contains(font))
                    resolved[family] = font;
            }

            design["fonts"] = new JsonObject
            {
                ["licence"] = licence,
                ["resolved"] = resolved,
            };
        }

        if (notFetched > 0)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Warn,
                FontSourceNotFetchedDiagnostic,
                string.Empty,
                ClipMessage(
                    $"{notFetched} font source declaration(s) were recorded but not fetched; " +
                    "a URL is data and is never requested"))
            { Axis = DesignImportAxis.Fonts });
        }

        diagnostics.Add(Info(
            FontsDiagnostic,
            string.Empty,
            $"{families.Count} font family(ies); {localFonts} local font file(s); {notFetched} declaration(s) not fetched")
            with
        { Axis = DesignImportAxis.Fonts });
    }

    /// <summary>
    /// Walks the CSS files' <c>@font-face</c> blocks and <c>@import</c> declarations. Only the family
    /// name and the presence of a <c>src</c> leave this method: the declaration's text is data and a
    /// URL from it never enters the document. The scan is bounded by
    /// <see cref="MaxFontDeclarationScans"/> and by <see cref="ImportLimits.CssMaxBytes"/> per file.
    /// </summary>
    private static (List<string> Families, int NotFetched) ScanFontDeclarations(IReadOnlyList<ImportFile> files)
    {
        var families = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int notFetched = 0;
        int scanned = 0;

        foreach (ImportFile file in files
                     .OrderBy(static file => Normalize(file.Path), StringComparer.Ordinal))
        {
            if (!Normalize(file.Path).EndsWith(".css", StringComparison.OrdinalIgnoreCase))
                continue;

            if (file.Content.Length > ImportLimits.CssMaxBytes)
                continue;

            string text = Encoding.UTF8.GetString(file.Content.Span);

            for (Match block = FontFaceBlock.Match(text);
                 block.Success && scanned < MaxFontDeclarationScans;
                 block = block.NextMatch())
            {
                scanned++;
                string body = block.Groups[1].Value;
                if (FontSrcDeclaration.IsMatch(body))
                    notFetched++;

                Match declaration = FontFamilyDeclaration.Match(body);
                if (!declaration.Success)
                    continue;

                string raw = declaration.Groups[1].Value.Trim().Trim('\'', '"').Trim();
                int comma = raw.IndexOf(',');
                if (comma >= 0)
                    raw = raw[..comma].Trim();

                string? family = DesignText.Sanitize(raw, MaxFamilyLength);
                if (family is null || !DesignText.IsSafeFontFamily(family)
                    || families.Count >= MaxFamilies || !seen.Add(family))
                {
                    continue;
                }

                families.Add(family);
            }

            int imports = 0;
            for (Match import = FontImportDeclaration.Match(text);
                 import.Success && scanned < MaxFontDeclarationScans && imports < MaxFontDeclarationScans;
                 import = import.NextMatch())
            {
                imports++;
                scanned++;
                notFetched++;
            }
        }

        return (families, notFetched);
    }

    private static void CollectFamilies(JsonNode? node, List<string> families, HashSet<string> seen)
    {
        if (families.Count >= MaxFamilies)
            return;

        switch (node)
        {
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    if (families.Count >= MaxFamilies)
                        return;

                    if (item is JsonValue value && value.GetValueKind() == JsonValueKind.String)
                    {
                        string? family = DesignText.Sanitize(value.GetValue<string>(), MaxFamilyLength);
                        if (family is not null && DesignText.IsSafeFontFamily(family) && seen.Add(family))
                            families.Add(family);
                    }
                }

                break;

            case JsonObject obj:
                if (obj.TryGetPropertyValue("$value", out JsonNode? valueNode))
                    CollectFamilies(valueNode, families, seen);

                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    if (!property.Key.StartsWith('$'))
                        CollectFamilies(property.Value, families, seen);
                }

                break;
        }
    }

    private static int CountArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.Array
            ? element.GetArrayLength()
            : 0;

    private static int CountNodes(JsonElement root, int max)
    {
        int count = 0;
        var stack = new Stack<JsonElement>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            JsonElement element = stack.Pop();
            if (++count > max)
                return count;

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                    stack.Push(property.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                    stack.Push(item);
            }
        }

        return count;
    }

    private static bool IsBaseSelector(string selector)
    {
        foreach (string candidate in BaseSelectors)
        {
            if (string.Equals(selector, candidate, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsUnderTokens(string path)
    {
        string[] segments = path.Split('/');
        return segments.Length >= 2 && string.Equals(segments[^2], "tokens", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSelector(string? selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return string.Empty;

        // Quotes are irrelevant to a selector's identity, so `[data-theme="midnight"]` and
        // `[data-theme=midnight]` name the same mode.
        string normalized = selector.Replace("\"", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal);
        string[] parts = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", parts).ToLowerInvariant();
    }

    /// <summary>
    /// The vendor prefix the export's colour tokens share, or null when they share none. Each
    /// colour-kind token name's first <c>-</c> segment is counted; the most common wins only when it
    /// covers at least 60% of the colours and is 1..16 characters. Prefixing is what keeps
    /// <c>lines</c> and <c>table-lines</c> on different paths.
    /// </summary>
    private static string? DominantColourPrefix(IReadOnlyList<string> colourNames)
    {
        if (colourNames.Count == 0)
            return null;

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string raw in colourNames)
        {
            string name = raw.Trim();
            if (name.StartsWith("--", StringComparison.Ordinal))
                name = name[2..];

            int dash = name.IndexOf('-');
            if (dash <= 0)
                continue;

            string prefix = name[..dash].ToLowerInvariant();
            if (prefix.Length is < 1 or > 16)
                continue;

            counts[prefix] = counts.GetValueOrDefault(prefix) + 1;
        }

        if (counts.Count == 0)
            return null;

        KeyValuePair<string, int> best = counts
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .First();

        return best.Value * 100 >= colourNames.Count * 60 ? best.Key : null;
    }

    /// <summary>A label onto the mode-name form (<c>^[a-z][a-z0-9-]{0,23}$</c>).</summary>
    private static string SlugMode(string? label)
    {
        string slug = DesignKeys.Slug(label);
        int start = 0;
        while (start < slug.Length && (slug[start] < 'a' || slug[start] > 'z'))
            start++;

        slug = slug[start..].Replace('_', '-');
        if (slug.Length > 24)
            slug = slug[..24];
        slug = slug.TrimEnd('-');

        return slug.Length == 0 ? "mode" : slug;
    }

    private static string Segments(string raw)
    {
        string[] parts = raw.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "x";

        var builder = new StringBuilder();
        foreach (string part in parts)
        {
            string key = DesignKeys.Slug(part);
            if (key.Length == 0)
                continue;
            if (builder.Length > 0)
                builder.Append('.');
            builder.Append(key);
        }

        return builder.Length == 0 ? "x" : builder.ToString();
    }

    /// <summary>
    /// The prose, guidelines, UI-kit notes and lint config this export carries, in ordinal path order.
    /// Every document is bounded by a per-file byte cap and a total cap, its text is redacted, and
    /// markup is reduced to plain text by <see cref="MarkupText"/> — never executed or fetched.
    /// </summary>
    private static (IReadOnlyList<ImportedReferenceDocument> Documents, IReadOnlyList<ImportDiagnostic> Diagnostics)
        CollectReferences(IReadOnlyList<ImportFile> files)
    {
        var diagnostics = new List<ImportDiagnostic>();
        var documents = new List<ImportedReferenceDocument>();
        int skipped = 0;
        long totalBytes = 0;
        bool capped = false;

        IEnumerable<ImportFile> ordered = files
            .Where(static file => file is not null && !string.IsNullOrEmpty(file.Path))
            .OrderBy(static file => Normalize(file.Path), StringComparer.Ordinal);

        foreach (ImportFile file in ordered)
        {
            string path = Normalize(file.Path);

            if (ExclusionReason(path) is { } reason)
            {
                diagnostics.Add(Info(FileSkippedDiagnostic, path, "not read: " + reason)
                    with
                { Axis = DesignImportReportBuilder.AxisForReason(reason) });
                skipped++;
                continue;
            }

            if (!TryClassifyReference(path, out string? name, out string? mime))
                continue;

            if (documents.Count >= MaxReferenceDocuments)
            {
                capped = true;
                break;
            }

            if (file.Content.Length > MaxReferenceBytes)
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    ReferenceTooLargeDiagnostic,
                    DesignKeys.Clip(path),
                    ClipMessage($"reference is {file.Content.Length} bytes; the limit is {MaxReferenceBytes}"))
                { Axis = DesignImportAxis.ReferenceDocuments });
                skipped++;
                continue;
            }

            string text = IsMarkupReference(path)
                ? MarkupText.ToText(Encoding.UTF8.GetString(file.Content.Span))
                : Encoding.UTF8.GetString(file.Content.Span);

            (text, int redactions) = SecretRedactor.Redact(text);
            if (redactions > 0)
                diagnostics.Add(Info(SecretRedactedDiagnostic, path, $"{redactions} secret(s) redacted")
                    with
                { Axis = DesignImportAxis.ReferenceDocuments });

            int bytes = Encoding.UTF8.GetByteCount(text);
            if (totalBytes + bytes > MaxReferenceTotalBytes)
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    ReferenceCapDiagnostic,
                    DesignKeys.Clip(path),
                    ClipMessage($"the reference total would exceed {MaxReferenceTotalBytes} bytes"))
                { Axis = DesignImportAxis.ReferenceDocuments });
                skipped++;
                continue;
            }

            totalBytes += bytes;
            documents.Add(new ImportedReferenceDocument(
                name!,
                path,
                mime!,
                text,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))));
        }

        if (capped)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Info,
                ReferenceCapDiagnostic,
                string.Empty,
                ClipMessage($"more than {MaxReferenceDocuments} reference documents; the rest were not read"))
            { Axis = DesignImportAxis.ReferenceDocuments });
        }

        diagnostics.Add(Info(
            ReferenceDiagnostic,
            string.Empty,
            $"{documents.Count} reference document(s) kept; {skipped} skipped")
            with
        { Axis = DesignImportAxis.ReferenceDocuments });

        return (documents, diagnostics);
    }

    /// <summary>
    /// The exclusion reason for a path this reader never opens, or null. One bounded diagnostic is
    /// emitted per excluded path, so a client scratch file, a compiled bundle, a screenshot and an
    /// unexecuted component are each accounted for rather than silently ignored.
    /// </summary>
    private static string? ExclusionReason(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string name = segments.Length == 0 ? path : segments[^1];

        if (segments.Any(static segment => string.Equals(segment, "uploads", StringComparison.OrdinalIgnoreCase)))
            return "CLIENT_SCRATCH";

        if (string.Equals(name, "_ds_bundle.js", StringComparison.OrdinalIgnoreCase))
            return "COMPILED_BUNDLE";

        if (segments.Any(static segment => string.Equals(segment, "reference", StringComparison.OrdinalIgnoreCase))
            && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            return "SCREENSHOT";
        }

        if (segments.Any(static segment => string.Equals(segment, "ui_kits", StringComparison.OrdinalIgnoreCase))
            && name.EndsWith(".jsx", StringComparison.OrdinalIgnoreCase))
        {
            return "NOT_EXECUTED";
        }

        return null;
    }

    /// <summary>
    /// The stable name and MIME type of a reference path per the path table, or <c>false</c> when the
    /// path is not a reference. More specific rules (ui_kits, reference, components) run before the
    /// generic bare-name rules.
    /// </summary>
    private static bool TryClassifyReference(string path, out string? name, out string? mime)
    {
        name = null;
        mime = null;

        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        string basename = segments[^1];
        string[] directories = segments[..^1];
        bool Under(string directory) =>
            directories.Any(segment => string.Equals(segment, directory, StringComparison.OrdinalIgnoreCase));

        if (Under("ui_kits") && string.Equals(basename, "README.md", StringComparison.OrdinalIgnoreCase))
        {
            name = "ui_kits/" + basename;
            mime = "text/markdown";
            return true;
        }

        if (Under("reference")
            && (basename.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || basename.EndsWith(".css", StringComparison.OrdinalIgnoreCase)))
        {
            name = "reference/" + basename;
            mime = basename.EndsWith(".css", StringComparison.OrdinalIgnoreCase) ? "text/css" : "text/markdown";
            return true;
        }

        if (string.Equals(basename, "readme.md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(basename, "SKILL.md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(basename, "github.md", StringComparison.OrdinalIgnoreCase))
        {
            name = basename;
            mime = "text/markdown";
            return true;
        }

        if (string.Equals(basename, "_adherence.oxlintrc.json", StringComparison.OrdinalIgnoreCase))
        {
            name = basename;
            mime = "text/plain";
            return true;
        }

        if (Under("components") && basename.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase))
        {
            name = "components/prompts/" + basename;
            mime = "text/markdown";
            return true;
        }

        if (IsMarkupReference(path))
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(basename);
            string directory = directories.Length == 0 ? string.Empty : string.Join('/', directories) + "/";
            name = directory + stem + ".txt";
            mime = "text/plain";
            return true;
        }

        return false;
    }

    /// <summary>True for a guideline document or the root thumbnail that is reduced to plain text.</summary>
    private static bool IsMarkupReference(string path)
    {
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        string basename = segments[^1];
        bool html = basename.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || basename.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)
            || basename.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase);

        if (!html)
            return false;

        return string.Equals(basename, "thumbnail.html", StringComparison.OrdinalIgnoreCase)
            || segments[..^1].Any(static segment =>
                string.Equals(segment, "guidelines", StringComparison.OrdinalIgnoreCase));
    }

    private static ImportSourceRef Source(ImportFile file) =>
        new(
            Normalize(file.Path),
            ImporterId,
            Convert.ToHexStringLower(SHA256.HashData(file.Content.Span)),
            file.ProjectFileId);

    private static ImportDiagnostic Info(string code, string path, string message) =>
        new(ImportDiagnosticSeverity.Info, code, DesignKeys.Clip(path), ClipMessage(message));

    private static ImportDiagnostic Unreadable(string path, string message) =>
        new(ImportDiagnosticSeverity.Warn, ManifestUnreadableDiagnostic, DesignKeys.Clip(path), ClipMessage(message));

    private static string ClipMessage(string message) =>
        message.Length <= MaxDiagnosticMessage ? message : message[..(MaxDiagnosticMessage - 3)] + "...";

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string Basename(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private sealed record ManifestData(
        string? Namespace,
        int ComponentCount,
        int TokenCount,
        int CardCount,
        int StartingPointCount,
        IReadOnlyList<string> ColourNames,
        IReadOnlyDictionary<string, string> ThemeSelectors,
        IReadOnlyList<string> GlobalCssPaths,
        IReadOnlyList<ManifestComponent> Components,
        JsonArray? Cards,
        IReadOnlyList<string> CardGroups,
        IReadOnlyList<string> StartingPointSections);

    private sealed record ManifestComponent(string Name, string? SourcePath);
}

/// <summary>The design document, the binary assets and the reference documents a Claude Design export produced.</summary>
public sealed record ClaudeDesignBundle(
    DesignImportResult Import,
    IReadOnlyList<ImportedAsset> Assets)
{
    /// <summary>The prose/guideline reference documents read from the export, in ordinal path order.</summary>
    public IReadOnlyList<ImportedReferenceDocument> ReferenceDocuments { get; init; } = [];
}
