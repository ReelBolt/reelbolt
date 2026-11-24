using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems.Merge;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// The vendor labels <see cref="BundleImporter"/> recognizes from an archive's layout. Append-only.
/// </summary>
public static class BundleVendors
{
    /// <summary>A Claude / design-system export: a top-level name or directory containing <c>claude</c>
    /// or <c>design-system</c>, or a <c>DESIGN.md</c> beside an <c>index.html</c>.</summary>
    public const string ClaudeDesign = "claude-design";

    /// <summary>A v0 export: a <c>v0.json</c> or a <c>SKILL.md</c>.</summary>
    public const string V0 = "v0";

    /// <summary>A Lovable export: a <c>.lovable/design-system.json</c>.</summary>
    public const string Lovable = "lovable";

    /// <summary>A Penpot export: a <c>manifest.json</c> naming <c>penpot</c> (or a root manifest beside a
    /// <c>files/</c> tree), or an entry under a <c>.penpot</c>-like name.</summary>
    public const string Penpot = "penpot";

    /// <summary>A Sketch export: a <c>document.json</c> beside a <c>meta.json</c> (a <c>pages/</c>
    /// tree is expected in a real archive but is never read, so it is not required to label one).</summary>
    public const string Sketch = "sketch";
}

/// <summary>
/// One bundle import run: the registered importers' result plus the layout hint and the
/// <c>package.json</c> dependency names the run read (never its scripts).
/// </summary>
public sealed record BundleImportResult(
    DesignImportResult Import,
    string? Vendor,
    IReadOnlySet<string> Dependencies,
    IReadOnlyList<ImportedAsset> Assets)
{
    /// <summary>The reference documents a complete Claude Design reader carried, in ordinal path order.</summary>
    public IReadOnlyList<ImportedReferenceDocument> ReferenceDocuments { get; init; } = [];
}

/// <summary>
/// SPEC 16.4 <c>bundle</c>: the archive entry point. Every allowed text file of an extracted archive
/// is offered to every registered importer, whatever they produced is merged with the standard
/// precedence, and the archive's layout is labelled with a vendor hint. PURE: it never executes a
/// file, never touches IO, and an entry it will not read (a script, markup or a binary name) is
/// dropped with a diagnostic rather than handed to an importer.
///
/// <para>
/// This importer is never a member of <see cref="DesignImportRegistry.All"/>: it delegates to the
/// registry, so listing it would recurse. The controller routes an extracted zip to it explicitly
/// and <see cref="CanRead"/> answers true for any bundle.
/// </para>
/// </summary>
public sealed class BundleImporter : IDesignImporter
{
    /// <summary>The importer identifier, also the fallback provenance source id.</summary>
    public const string ImporterId = "bundle";

    /// <summary>Diagnostic codes this importer emits.</summary>
    public const string VendorDiagnostic = "BUNDLE_VENDOR";

    /// <summary>The diagnostic code recording that only a <c>package.json</c>'s dependencies were read.</summary>
    public const string PackageDiagnostic = "BUNDLE_PACKAGE";

    /// <summary>The diagnostic code for an entry refused by name because it is not an allowed text file.</summary>
    public const string DroppedDiagnostic = "BUNDLE_FILE_DROPPED";

    /// <summary>The Info code for a <c>tailwind.config.*</c> entry the archive carried but that was not read.</summary>
    public const string TailwindConfigNotReadDiagnostic = "TAILWIND_CONFIG_NOT_READ_FROM_ZIP";

    /// <summary>
    /// The Info code for an SVG (or other markup) entry no vendor reader consumed. Vector markup is
    /// admitted to the Claude reader only; everywhere else it is reported rather than silently ignored.
    /// </summary>
    public const string MarkupNotReadDiagnostic = "BUNDLE_MARKUP_NOT_READ";

    /// <summary>The suite-wide "nothing recognized" code, emitted when the bundle yielded no document.</summary>
    public const string NothingRecognized = "IMPORT_NOTHING_RECOGNIZED";

    /// <summary>The dependency fields read from a <c>package.json</c>, in the order they are merged.</summary>
    private static readonly string[] DependencyFields =
    [
        "dependencies",
        "devDependencies",
        "peerDependencies",
        "optionalDependencies",
    ];

    /// <summary>
    /// Extensions that are never read as data: scripts and executables. This is defense in depth; the
    /// worker's own classifier already drops them and the route only builds <see cref="ImportFile"/>s
    /// from inlined <c>json</c>/<c>css</c>/<c>text</c>/<c>svg</c>/<c>markup</c> entries.
    /// A <c>tailwind.config.*</c> is refused like any other script: the worker never inlines it, so a
    /// bundle cannot read it, and it is reported with <see cref="TailwindConfigNotReadDiagnostic"/>
    /// instead. An <c>.svg</c> or an HTML/XHTML entry is NOT refused: the Claude reader sanitizes the
    /// vector and reduces the markup to a plain-text reference digest, and a bundle no vendor reader
    /// owns reports it with <see cref="MarkupNotReadDiagnostic"/>.
    /// </summary>
    private static readonly HashSet<string> RefusedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".mts", ".cts",
        ".php", ".sh", ".bat", ".cmd", ".ps1", ".py", ".rb", ".pl", ".vbs",
        ".exe", ".dll", ".so", ".dylib", ".node", ".wasm",
    };

    /// <summary>The cap on dependency names read from one <c>package.json</c>.</summary>
    private const int MaxDependencies = 5000;

    private const int MaxDependencyNameLength = 256;

    public string Id => ImporterId;

    /// <summary>True for any zip bundle; the controller routes an extracted archive here explicitly.</summary>
    public bool CanRead(string path, ReadOnlySpan<byte> head) => true;

    /// <summary>Runs <see cref="ImportBundle"/> and returns its import result.</summary>
    public DesignImportResult Import(IReadOnlyList<ImportFile> files) => ImportBundle(files).Import;

    /// <summary>
    /// Runs every registered importer whose <see cref="IDesignImporter.CanRead"/> accepts an allowed
    /// text file, merges their documents with the standard precedence, adds a fallback provenance
    /// source per file, and labels the layout with a vendor hint. Never throws. A Claude Design
    /// archive (a <c>_ds_manifest.json</c>) is instead read in full by
    /// <see cref="ClaudeDesignImporter"/>, which also selects the binary <paramref name="assets"/>.
    /// </summary>
    public BundleImportResult ImportBundle(
        IReadOnlyList<ImportFile>? files,
        IReadOnlyList<ImportedAsset>? assets = null)
    {
        var diagnostics = new List<ImportDiagnostic>();
        var allowed = new List<ImportFile>();

        // The whole archive layout, refused entries included: index.html is never parsed or handed to
        // an importer, but its presence beside DESIGN.md is one of the claude-design layout hints.
        var layout = new List<string>();
        ImportFile? packageJson = null;
        bool penpotManifest = false;

        foreach (ImportFile? file in files ?? [])
        {
            if (file is null || string.IsNullOrEmpty(file.Path))
                continue;

            layout.Add(file.Path);

            if (IsTailwindConfig(file.Path))
            {
                // The worker drops every script and only reports this one by name. Reading it here would be
                // possible (the parser is inert) but would claim a capability an archive does not have, so the
                // honest outcome is to say it was seen and not read.
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Info,
                    TailwindConfigNotReadDiagnostic,
                    file.Path,
                    "a tailwind config is JavaScript and is never read from an archive; export the theme as CSS variables or DTCG JSON to import it")
                { Axis = DesignImportAxis.Tokens });
                continue;
            }

            if (!IsAllowedTextFile(file.Path))
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn,
                    DroppedDiagnostic,
                    file.Path,
                    "entry is not an allowed text file")
                { Axis = DesignImportAxis.PreviewCards });
                continue;
            }

            if (string.Equals(System.IO.Path.GetFileName(file.Path), "package.json", StringComparison.OrdinalIgnoreCase))
            {
                packageJson = file;
                continue;
            }

            if (IsPenpotManifest(file))
                penpotManifest = true;

            allowed.Add(file);
        }

        IReadOnlySet<string> dependencies = ReadPackageDependencies(packageJson, diagnostics);

        // The vendor hint is decided before any reader runs: a complete Claude Design archive is
        // owned by ClaudeDesignImporter, and the generic registry must not also read the same CSS
        // (it would misclassify the type sizes as spacing tokens).
        string? vendor = DetectVendor(layout, packageJson?.Path, dependencies, penpotManifest);

        DesignImportResult registry;
        IReadOnlyList<ImportedAsset> importedAssets = Array.Empty<ImportedAsset>();
        IReadOnlyList<ImportedReferenceDocument> referenceDocuments = Array.Empty<ImportedReferenceDocument>();
        bool readerOwnsArchive = false;
        if (vendor == BundleVendors.ClaudeDesign && HasClaudeManifest(allowed))
        {
            ClaudeDesignBundle claude = new ClaudeDesignImporter().ImportBundle(allowed, assets ?? []);
            // References are carried whatever the manifest produced: prose survives an unreadable manifest.
            referenceDocuments = claude.ReferenceDocuments;
            if (claude.Import.Design is not null)
            {
                registry = claude.Import;
                importedAssets = claude.Assets;
                readerOwnsArchive = true;
            }
            else
            {
                // An unreadable manifest falls back to the tolerant path; the reader's own diagnostic
                // (CLAUDE_DESIGN_MANIFEST_UNREADABLE) says why the complete import produced nothing.
                registry = allowed.Count > 0
                    ? DesignImportRegistry.ImportAll(allowed)
                    : new DesignImportResult(null, null, Array.Empty<ImportSourceRef>(), Array.Empty<ImportDiagnostic>());
                if (claude.Import.Diagnostics is { Count: > 0 })
                    diagnostics.AddRange(claude.Import.Diagnostics);
            }
        }
        else
        {
            registry = allowed.Count > 0
                ? DesignImportRegistry.ImportAll(allowed)
                : new DesignImportResult(null, null, Array.Empty<ImportSourceRef>(), Array.Empty<ImportDiagnostic>());
        }

        if (registry.Diagnostics is { Count: > 0 })
            diagnostics.AddRange(registry.Diagnostics);

        // Vector markup is consumed only by the complete Claude reader (which sanitizes it). Any
        // archive it does not own was admitted past IsAllowedTextFile but never read, so say so.
        if (!readerOwnsArchive)
        {
            foreach (ImportFile svg in allowed.Where(static file =>
                         file.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)))
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Info,
                    MarkupNotReadDiagnostic,
                    svg.Path,
                    "SVG markup was carried but no vendor reader consumed it")
                { Axis = DesignImportAxis.PreviewCards });
            }
        }

        // HTML/XHTML is inert text admitted for the Claude reader's guideline and thumbnail digests.
        // An entry no reader consumed (every non-Claude archive, and a Claude archive's unclassified
        // markup) is reported rather than silently ignored.
        var consumedMarkup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ImportedReferenceDocument reference in referenceDocuments)
            consumedMarkup.Add(NormalizePath(reference.Path));

        foreach (ImportFile markup in allowed.Where(static file => IsMarkupEntry(file.Path)))
        {
            if (consumedMarkup.Contains(NormalizePath(markup.Path)))
                continue;

            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Info,
                MarkupNotReadDiagnostic,
                markup.Path,
                "HTML markup was carried as inert text but no vendor reader consumed it")
            { Axis = DesignImportAxis.PreviewCards });
        }

        if (vendor is not null)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Info,
                VendorDiagnostic,
                string.Empty,
                vendor));
        }

        // v0's two named files fall outside every registered dialect's CanRead: v0.json carries no
        // DTCG/legacy marker and SKILL.md is not DESIGN.md. When the layout says v0, the shared
        // tolerant walker reads v0.json and the design-md prose rules read SKILL.md, then both
        // merge with whatever the registry produced.
        JsonObject? v0Design = null;
        IReadOnlyList<ImportSourceRef> v0Sources = Array.Empty<ImportSourceRef>();
        if (vendor == BundleVendors.V0)
            (v0Design, v0Sources) = ImportV0(allowed, registry.Sources, diagnostics);

        // Lovable's marker file carries no DTCG/legacy marker of its own, so the registered
        // dialects leave `.lovable/design-system.json` to the shared tolerant walker; `src/index.css`
        // in the same archive is claimed by css-theme above (`tailwind.config.ts` is reported, not read).
        JsonObject? lovableDesign = null;
        IReadOnlyList<ImportSourceRef> lovableSources = Array.Empty<ImportSourceRef>();
        if (vendor == BundleVendors.Lovable)
            (lovableDesign, lovableSources) = ImportLovable(allowed, registry.Sources, diagnostics);

        // Penpot's marker is its manifest.json (or a `.penpot` name); its JSON documents carry no DTCG/legacy marker of
        // their own, so the registered dialects leave them to `PenpotImporter`, which reads the
        // colour/typography library objects out of whatever JSON document holds them. The plain
        // DTCG export path stays with `dtcg` (it is the documented, tested route).
        JsonObject? penpotDesign = null;
        IReadOnlyList<ImportSourceRef> penpotSources = Array.Empty<ImportSourceRef>();
        if (vendor == BundleVendors.Penpot)
            (penpotDesign, penpotSources) = ImportPenpot(allowed, registry.Sources, diagnostics);

        // Sketch's `document.json` carries no DTCG/legacy marker of its own, so the registered
        // dialects leave it to `SketchImporter`, which reads only the swatch and text-style
        // libraries. Pages, layer trees and `meta.json` are never read.
        JsonObject? sketchDesign = null;
        IReadOnlyList<ImportSourceRef> sketchSources = Array.Empty<ImportSourceRef>();
        if (vendor == BundleVendors.Sketch)
            (sketchDesign, sketchSources) = ImportSketch(allowed, diagnostics);

        JsonObject? design = registry.Design;
        if (v0Design is not null)
        {
            design = design is null
                ? v0Design
                : DesignDocumentMerger.Merge(null, [design, v0Design]);
        }

        if (lovableDesign is not null)
        {
            design = design is null
                ? lovableDesign
                : DesignDocumentMerger.Merge(null, [design, lovableDesign]);
        }

        if (penpotDesign is not null)
        {
            design = design is null
                ? penpotDesign
                : DesignDocumentMerger.Merge(null, [design, penpotDesign]);
        }

        if (sketchDesign is not null)
        {
            design = design is null
                ? sketchDesign
                : DesignDocumentMerger.Merge(null, [design, sketchDesign]);
        }

        if (design is null && !diagnostics.Any(d => d.Code == NothingRecognized))
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Error,
                NothingRecognized,
                string.Empty,
                "no registered importer recognized any input file"));
        }

        // The complete Claude Design reader is authoritative about what it read: no unread file is
        // added to its provenance. Every other path keeps the fallback merge.
        IReadOnlyList<ImportSourceRef> sources = readerOwnsArchive
            ? registry.Sources
            : MergeSources(
                allowed,
                packageJson,
                [.. registry.Sources, .. v0Sources, .. lovableSources, .. penpotSources, .. sketchSources]);

        var import = new DesignImportResult(design, registry.ProductMap, sources, diagnostics);
        return new BundleImportResult(import, vendor, dependencies, importedAssets)
        {
            ReferenceDocuments = referenceDocuments,
        };
    }

    /// <summary>
    /// The vendor hint for a bundle's layout, or null when nothing distinctive is present. The
    /// first match wins, most specific layout first.
    /// </summary>
    public static string? DetectVendor(
        IEnumerable<string> paths,
        string? packageJsonPath,
        IReadOnlySet<string>? dependencies = null,
        bool penpotManifest = false)
    {
        var normalized = new List<string>();
        foreach (string? path in paths ?? [])
        {
            if (!string.IsNullOrEmpty(path))
                normalized.Add(path.Replace('\\', '/').TrimStart('/'));
        }

        if (packageJsonPath is not null)
            normalized.Add(packageJsonPath.Replace('\\', '/').TrimStart('/'));

        bool HasName(string name) =>
            normalized.Any(p => string.Equals(System.IO.Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));

        bool HasTopLevelContaining(string needle) => normalized.Any(p =>
        {
            string first = p.Split('/', 2)[0];
            return first.Contains(needle, StringComparison.OrdinalIgnoreCase);
        });

        // True for any path under a directory of that name (the file itself may be deeper still).
        bool HasDirectory(string name) => normalized.Any(p =>
        {
            string[] segments = p.Split('/');
            return segments.Length >= 2
                && segments[..^1].Any(segment => string.Equals(segment, name, StringComparison.OrdinalIgnoreCase));
        });

        // lovable: the exact marker file (or anything under the marker directory).
        if (normalized.Any(p =>
                p.Contains(".lovable/", StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(".lovable", StringComparison.OrdinalIgnoreCase)))
        {
            return BundleVendors.Lovable;
        }

        // sketch: the document.json + meta.json pair. A pages/ tree is expected in a real archive but
        // is never read, so its absence does not stop the label.
        if (HasName("document.json")
            && HasName("meta.json"))
        {
            return BundleVendors.Sketch;
        }

        // penpot: a manifest.json that names penpot (a real .penpot export is a zip of JSON with a root
        // manifest and a files/ tree, and no entry named *.penpot), a root manifest beside files/, or any
        // .penpot entry or directory.
        if (penpotManifest
            || (normalized.Contains("manifest.json", StringComparer.OrdinalIgnoreCase)
                && normalized.Any(p => p.StartsWith("files/", StringComparison.OrdinalIgnoreCase)))
            || normalized.Any(p => p.Contains(".penpot", StringComparison.OrdinalIgnoreCase)))
        {
            return BundleVendors.Penpot;
        }

        // claude-design (real export layout): the complete reader's manifest anywhere, or a SKILL.md
        // beside a tokens/ tree. Both are above the v0 SKILL.md rule, which would otherwise claim the
        // same file.
        if (HasName(ClaudeDesignImporter.ManifestFileName)
            || (HasName("SKILL.md") && HasDirectory("tokens")))
        {
            return BundleVendors.ClaudeDesign;
        }

        // v0: the two named export files.
        if (HasName("v0.json") || HasName("SKILL.md"))
            return BundleVendors.V0;

        // claude-design: a top-level name/dir, the DESIGN.md + index.html pair, or a dependency name.
        bool dependencySignal = dependencies is not null && dependencies.Any(d =>
            d.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || d.Contains("design-system", StringComparison.OrdinalIgnoreCase));

        if (HasTopLevelContaining("claude")
            || HasTopLevelContaining("design-system")
            || (HasName("DESIGN.md") && HasName("index.html"))
            || dependencySignal)
        {
            return BundleVendors.ClaudeDesign;
        }

        return null;
    }

    /// <summary>
    /// The tolerant v0 readers: <c>v0.json</c> through <see cref="TolerantTokenWalker"/> and
    /// <c>SKILL.md</c> through the design-md prose rules, both merged into one document. A file the
    /// registry already attributed is left to the registry, so no token is written twice.
    /// </summary>
    private static (JsonObject? Design, IReadOnlyList<ImportSourceRef> Sources) ImportV0(
        IReadOnlyList<ImportFile> allowed,
        IReadOnlyList<ImportSourceRef> registrySources,
        List<ImportDiagnostic> diagnostics)
    {
        var designs = new List<JsonObject>();
        var sources = new List<ImportSourceRef>();
        var skillImporter = new DesignMdImporter();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImportSourceRef source in registrySources)
            seen.Add(source.Path);

        foreach (ImportFile file in allowed)
        {
            if (!seen.Add(file.Path))
                continue;

            string name = System.IO.Path.GetFileName(file.Path);
            if (name.Equals("v0.json", StringComparison.OrdinalIgnoreCase))
            {
                JsonObject? walked = TolerantTokenWalker.WalkFile(file.Path, file.Content, diagnostics);
                if (walked is null)
                    continue;

                designs.Add(walked);
                sources.Add(new ImportSourceRef(
                    file.Path,
                    TolerantTokenWalker.ImporterId,
                    Sha256Hex(file.Content.Span),
                    file.ProjectFileId));
            }
            else if (name.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                DesignImportResult prose = skillImporter.Import([file]);
                if (prose.Diagnostics is { Count: > 0 })
                    diagnostics.AddRange(prose.Diagnostics);

                if (prose.Design is not null)
                {
                    designs.Add(prose.Design);
                    sources.AddRange(prose.Sources);
                }
            }
        }

        JsonObject? design = designs.Count == 0 ? null : DesignDocumentMerger.Merge(null, designs);
        return (design, sources);
    }

    /// <summary>
    /// The tolerant Lovable reader: <c>.lovable/design-system.json</c> through
    /// <see cref="TolerantTokenWalker"/>. A file a registered dialect already attributed is left to
    /// the registry, so no token is written twice. The layout's <c>src/index.css</c> already flows
    /// through css-theme unchanged; a <c>tailwind.config.ts</c> is never read (see
    /// <see cref="TailwindConfigNotReadDiagnostic"/>).
    /// </summary>
    private static (JsonObject? Design, IReadOnlyList<ImportSourceRef> Sources) ImportLovable(
        IReadOnlyList<ImportFile> allowed,
        IReadOnlyList<ImportSourceRef> registrySources,
        List<ImportDiagnostic> diagnostics)
    {
        var designs = new List<JsonObject>();
        var sources = new List<ImportSourceRef>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImportSourceRef source in registrySources)
            seen.Add(source.Path);

        foreach (ImportFile file in allowed)
        {
            if (!IsLovableDesignSystem(file.Path) || !seen.Add(file.Path))
                continue;

            JsonObject? walked = TolerantTokenWalker.WalkFile(file.Path, file.Content, diagnostics);
            if (walked is null)
                continue;

            designs.Add(walked);
            sources.Add(new ImportSourceRef(
                file.Path,
                TolerantTokenWalker.ImporterId,
                Sha256Hex(file.Content.Span),
                file.ProjectFileId));
        }

        JsonObject? design = designs.Count == 0 ? null : DesignDocumentMerger.Merge(null, designs);
        return (design, sources);
    }

    /// <summary>
    /// The tolerant Penpot reader: every allowed JSON document a registered dialect has not already
    /// attributed is offered to <see cref="PenpotImporter"/> through its own
    /// <see cref="IDesignImporter.CanRead"/>, and whatever library objects it finds are merged into
    /// one document. A `.penpot` entry's non-JSON parts (page blobs, binaries) fall outside its
    /// <c>CanRead</c> and are ignored, and the plain DTCG export path stays with <c>dtcg</c>.
    /// </summary>
    private static (JsonObject? Design, IReadOnlyList<ImportSourceRef> Sources) ImportPenpot(
        IReadOnlyList<ImportFile> allowed,
        IReadOnlyList<ImportSourceRef> registrySources,
        List<ImportDiagnostic> diagnostics)
    {
        var importer = new PenpotImporter();
        var candidates = new List<ImportFile>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImportSourceRef source in registrySources)
            seen.Add(source.Path);

        foreach (ImportFile file in allowed)
        {
            if (!seen.Add(file.Path))
                continue;

            ReadOnlySpan<byte> content = file.Content.Span;
            ReadOnlySpan<byte> head = content[..Math.Min(ImportLimits.HeadBytes, content.Length)];
            if (importer.CanRead(file.Path, head))
                candidates.Add(file);
        }

        if (candidates.Count == 0)
            return (null, Array.Empty<ImportSourceRef>());

        DesignImportResult result;
        try
        {
            result = importer.Import(candidates);
        }
        catch (Exception exception)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Error,
                "IMPORT_INTERNAL",
                string.Empty,
                exception.GetType().Name));
            return (null, Array.Empty<ImportSourceRef>());
        }

        if (result.Diagnostics is { Count: > 0 })
            diagnostics.AddRange(result.Diagnostics);

        return (result.Design, result.Sources);
    }

    /// <summary>
    /// The tolerant Sketch reader: the single <c>document.json</c> entry is offered to
    /// <see cref="SketchImporter"/>, whose own <c>CanRead</c> claims that file name only. Pages,
    /// layer trees and <c>meta.json</c> are never handed over, so they are never read.
    /// </summary>
    private static (JsonObject? Design, IReadOnlyList<ImportSourceRef> Sources) ImportSketch(
        IReadOnlyList<ImportFile> allowed,
        List<ImportDiagnostic> diagnostics)
    {
        var importer = new SketchImporter();
        var candidates = new List<ImportFile>();

        foreach (ImportFile file in allowed)
        {
            if (SketchImporter.LooksLikeDocument(file.Path))
                candidates.Add(file);
        }

        if (candidates.Count == 0)
            return (null, Array.Empty<ImportSourceRef>());

        DesignImportResult result;
        try
        {
            result = importer.Import(candidates);
        }
        catch (Exception exception)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Error,
                "IMPORT_INTERNAL",
                string.Empty,
                exception.GetType().Name));
            return (null, Array.Empty<ImportSourceRef>());
        }

        if (result.Diagnostics is { Count: > 0 })
            diagnostics.AddRange(result.Diagnostics);

        return (result.Design, result.Sources);
    }

    /// <summary>True when a <c>_ds_manifest.json</c> is among the allowed files, at any directory depth.</summary>
    private static bool HasClaudeManifest(IReadOnlyList<ImportFile> allowed) =>
        allowed.Any(file => string.Equals(
            System.IO.Path.GetFileName(file.Path.Replace('\\', '/')),
            ClaudeDesignImporter.ManifestFileName,
            StringComparison.OrdinalIgnoreCase));

    /// <summary>True for an HTML/XHTML entry, the markup kind the worker carries as inert text.</summary>
    private static bool IsMarkupEntry(string path)
    {
        string extension = System.IO.Path.GetExtension(path.Replace('\\', '/'));
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xhtml", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>True for the Lovable marker file <c>.lovable/design-system.json</c>, at any directory
    /// depth and with either slash.</summary>
    private static bool IsLovableDesignSystem(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string[] segments = path.Replace('\\', '/').Trim('/').Split('/');
        return segments.Length >= 2
            && segments[^1].Equals("design-system.json", StringComparison.OrdinalIgnoreCase)
            && segments[^2].Equals(".lovable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="path"/> names a text file the bundle may read. Scripts and markup
    /// are refused by extension and traversal segments are refused outright; everything else is
    /// allowed, so an extension-less text file still reaches the importers.
    /// </summary>
    public static bool IsAllowedTextFile(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':'))
            return false;

        foreach (string segment in normalized.Split('/'))
        {
            if (segment is ".." or ".")
                return false;
        }

        // A declaration file is inert data (the component-props channel), not a script. Every other
        // .ts/.js/.jsx/.html/.svg stays refused.
        if (normalized.EndsWith(".d.ts", StringComparison.OrdinalIgnoreCase))
            return true;

        return !RefusedExtensions.Contains(System.IO.Path.GetExtension(normalized));
    }

    /// <summary>True for a <c>tailwind.config.*</c> file, wherever it sits in the archive.</summary>
    private static bool IsTailwindConfig(string path) =>
        System.IO.Path.GetFileName(path.Replace('\\', '/')).StartsWith("tailwind.config", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for the root <c>manifest.json</c> of a Penpot export, recognised by its content: the head of
    /// the document names <c>penpot</c> (<c>"type": "penpot/export-files"</c>). Only the first
    /// <see cref="ImportLimits.HeadBytes"/> bytes are looked at, and nothing is parsed.
    /// </summary>
    private static bool IsPenpotManifest(ImportFile file)
    {
        string normalized = file.Path.Replace('\\', '/').TrimStart('/');
        if (!string.Equals(normalized, "manifest.json", StringComparison.OrdinalIgnoreCase))
            return false;

        int limit = Math.Min(file.Content.Length, ImportLimits.HeadBytes);
        return System.Text.Encoding.UTF8.GetString(file.Content.Span[..limit])
            .Contains("penpot", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads only the dependency-name keys of a <c>package.json</c>. Scripts, <c>bin</c>, config and
    /// every other field are never read, let alone executed. A malformed file yields no names and a
    /// warning.
    /// </summary>
    private static IReadOnlySet<string> ReadPackageDependencies(ImportFile? packageJson, List<ImportDiagnostic> diagnostics)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (packageJson is null)
            return names;

        try
        {
            using JsonDocument document = JsonDocument.Parse(
                packageJson.Content, new JsonDocumentOptions { MaxDepth = 32 });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new ImportDiagnostic(
                    ImportDiagnosticSeverity.Warn, PackageDiagnostic, packageJson.Path, "package.json is not an object"));
                return names;
            }

            foreach (string field in DependencyFields)
            {
                if (!document.RootElement.TryGetProperty(field, out JsonElement dependencies)
                    || dependencies.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (JsonProperty dependency in dependencies.EnumerateObject())
                {
                    if (names.Count >= MaxDependencies)
                        break;

                    if (dependency.Name.Length is > 0 and <= MaxDependencyNameLength)
                        names.Add(dependency.Name);
                }
            }

            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Info,
                PackageDiagnostic,
                packageJson.Path,
                $"read {names.Count} dependency name(s)"));
        }
        catch (JsonException)
        {
            diagnostics.Add(new ImportDiagnostic(
                ImportDiagnosticSeverity.Warn, PackageDiagnostic, packageJson.Path, "package.json is not valid JSON"));
        }

        return names;
    }

    /// <summary>
    /// Provenance for every file the bundle read: the registry's own source refs kept, and any file
    /// the registry did not attribute (including <c>package.json</c>) attributed to the bundle
    /// itself. One source per path.
    /// </summary>
    private static IReadOnlyList<ImportSourceRef> MergeSources(
        IReadOnlyList<ImportFile> allowed,
        ImportFile? packageJson,
        IReadOnlyList<ImportSourceRef>? registrySources)
    {
        var sources = new List<ImportSourceRef>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ImportSourceRef source in registrySources ?? [])
        {
            if (seen.Add(source.Path))
                sources.Add(source);
        }

        foreach (ImportFile file in allowed)
        {
            if (seen.Add(file.Path))
                sources.Add(new ImportSourceRef(file.Path, ImporterId, Sha256Hex(file.Content.Span), file.ProjectFileId));
        }

        if (packageJson is not null && seen.Add(packageJson.Path))
        {
            sources.Add(new ImportSourceRef(
                packageJson.Path, ImporterId, Sha256Hex(packageJson.Content.Span), packageJson.ProjectFileId));
        }

        return sources;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
