using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>The axes a design importer can populate. Append-only: the web matrix reads the names.</summary>
public enum DesignImportAxis
{
    Tokens,
    Modes,
    Assets,
    Fonts,
    Components,
    PreviewCards,
    Prose,
    Voice,
    DerivedTokens,
    ReferenceDocuments,
}

/// <summary>How far one importer reaches one axis. <c>Full</c> only where the read is end to end.</summary>
public enum DesignImportSupport
{
    Full,
    Partial,
    None,
}

/// <summary>One cell of the matrix: the axis, the support level and a short truthful note.</summary>
public sealed record DesignImportAxisCapability(
    DesignImportAxis Axis, DesignImportSupport Support, string Note);

/// <summary>One importer's row: its stable id, a display label and one capability per axis.</summary>
public sealed record DesignImporterCapabilities(
    string ImporterId, string Label, IReadOnlyList<DesignImportAxisCapability> Axes);

/// <summary>
/// The one central importer × axis table (T-008). It is static data, not a property on each
/// reader, and the drift test in <c>DesignImportCapabilitiesTests</c> fails the build when an
/// importer gains or loses a row. Support levels describe the post-initiative state truthfully:
/// <c>Full</c> only where an importer reaches the axis end to end, <c>Partial</c> where it reads
/// only part of it, <c>None</c> where it does not read it at all.
/// </summary>
public static class DesignImportCapabilities
{
    /// <summary>The versioned payload schema the route and the web client agree on.</summary>
    public const string Schema = "reelbolt.import-capabilities/1";

    private const DesignImportSupport Full = DesignImportSupport.Full;
    private const DesignImportSupport Partial = DesignImportSupport.Partial;
    private const DesignImportSupport None = DesignImportSupport.None;

    /// <summary>Every registered importer, each with exactly one row per <see cref="DesignImportAxis"/>.</summary>
    public static IReadOnlyList<DesignImporterCapabilities> Matrix { get; } =
    [
        Importer("dtcg", "DTCG tokens",
            Row(DesignImportAxis.Tokens, Full, "Reads DTCG $value/$type and legacy value/type tokens."),
            Row(DesignImportAxis.Modes, None, "DTCG has no mode or theme concept."),
            Row(DesignImportAxis.Assets, None, "Token files carry values only, never asset bytes."),
            Row(DesignImportAxis.Fonts, None, "Font families stay typography tokens; no font files or @font-face."),
            Row(DesignImportAxis.Components, None, "A token file carries no component metadata."),
            Row(DesignImportAxis.PreviewCards, None, "A token file carries no preview-card metadata."),
            Row(DesignImportAxis.Prose, None, "A token file carries no prose documents."),
            Row(DesignImportAxis.Voice, None, "A token file carries no voice guidance."),
            Row(DesignImportAxis.DerivedTokens, Partial, "The shared normalizer maps z, keyword and empty-shadow values; color-mix is CSS-only."),
            Row(DesignImportAxis.ReferenceDocuments, None, "A token file carries no reference documents.")),

        Importer("css-theme", "CSS custom properties",
            Row(DesignImportAxis.Tokens, Full, "Reads CSS custom properties and Tailwind v4 @theme blocks."),
            Row(DesignImportAxis.Modes, Partial, "Modes come only from selector-driven blocks such as .dark or [data-theme]."),
            Row(DesignImportAxis.Assets, None, "url() values are never fetched or imported as bytes."),
            Row(DesignImportAxis.Fonts, None, "font-family values become typography tokens; @font-face is not read."),
            Row(DesignImportAxis.Components, None, "CSS carries no component inventory."),
            Row(DesignImportAxis.PreviewCards, None, "CSS carries no preview cards."),
            Row(DesignImportAxis.Prose, None, "CSS carries no prose documents."),
            Row(DesignImportAxis.Voice, None, "CSS carries no voice guidance."),
            Row(DesignImportAxis.DerivedTokens, Full, "Resolves color-mix literals and gives z, keyword and empty-shadow real destinations."),
            Row(DesignImportAxis.ReferenceDocuments, None, "CSS carries no reference documents.")),

        Importer("design-md", "DESIGN.md",
            Row(DesignImportAxis.Tokens, Partial, "Only YAML front matter becomes tokens; colours and fonts written in prose or tables are not read."),
            Row(DesignImportAxis.Modes, None, "DESIGN.md has no mode concept."),
            Row(DesignImportAxis.Assets, None, "DESIGN.md carries no asset bytes."),
            Row(DesignImportAxis.Fonts, None, "Typefaces stay typography tokens; no font files are read."),
            Row(DesignImportAxis.Components, None, "Component bodies are skipped, not imported."),
            Row(DesignImportAxis.PreviewCards, None, "DESIGN.md carries no preview cards."),
            Row(DesignImportAxis.Prose, None, "Prose headings become voice; they are not kept as reference documents."),
            Row(DesignImportAxis.Voice, Full, "Headings under tone, do and don't become voice content."),
            Row(DesignImportAxis.DerivedTokens, Partial, "Front-matter values pass through the shared token normalizer."),
            Row(DesignImportAxis.ReferenceDocuments, None, "DESIGN.md is read as tokens and voice, not as reference documents.")),

        Importer("tokens-studio", "Tokens Studio",
            Row(DesignImportAxis.Tokens, Full, "Reads single-file and multi-file Tokens Studio exports."),
            Row(DesignImportAxis.Modes, Full, "$themes.json and token-set order become named modes."),
            Row(DesignImportAxis.Assets, None, "Token sets carry values only, never asset bytes."),
            Row(DesignImportAxis.Fonts, None, "Font-family and weight tokens stay typography; no font files."),
            Row(DesignImportAxis.Components, None, "Token sets carry no component metadata."),
            Row(DesignImportAxis.PreviewCards, None, "Token sets carry no preview cards."),
            Row(DesignImportAxis.Prose, None, "Token sets carry no prose documents."),
            Row(DesignImportAxis.Voice, None, "Token sets carry no voice guidance."),
            Row(DesignImportAxis.DerivedTokens, Partial, "The shared normalizer maps z, keyword and empty-shadow values."),
            Row(DesignImportAxis.ReferenceDocuments, None, "Token sets carry no reference documents.")),

        Importer("shadcn-registry", "shadcn/ui registry",
            Row(DesignImportAxis.Tokens, Full, "cssVars become CSS custom properties."),
            Row(DesignImportAxis.Modes, Partial, "Light and dark cssVars become mode overrides when the registry carries them."),
            Row(DesignImportAxis.Assets, None, "Component source and registry files are never copied."),
            Row(DesignImportAxis.Fonts, None, "The registry carries no font files."),
            Row(DesignImportAxis.Components, None, "Registry component source is never copied into the product map."),
            Row(DesignImportAxis.PreviewCards, None, "The registry carries no preview cards."),
            Row(DesignImportAxis.Prose, None, "The registry carries no prose documents."),
            Row(DesignImportAxis.Voice, None, "The registry carries no voice guidance."),
            Row(DesignImportAxis.DerivedTokens, Partial, "Values are read through the CSS reader's normalizer."),
            Row(DesignImportAxis.ReferenceDocuments, None, "The registry carries no reference documents.")),

        Importer("storybook-index", "Storybook index",
            Row(DesignImportAxis.Tokens, None, "The index carries story metadata, not token values."),
            Row(DesignImportAxis.Modes, None, "The index carries no mode values."),
            Row(DesignImportAxis.Assets, None, "No story asset or source file is opened."),
            Row(DesignImportAxis.Fonts, None, "The index carries no font data."),
            Row(DesignImportAxis.Components, Full, "Component names and story paths become product-map components."),
            Row(DesignImportAxis.PreviewCards, None, "Story entries are not preview-card captures."),
            Row(DesignImportAxis.Prose, None, "The index carries no prose documents."),
            Row(DesignImportAxis.Voice, None, "The index carries no voice guidance."),
            Row(DesignImportAxis.DerivedTokens, None, "The index carries no token values."),
            Row(DesignImportAxis.ReferenceDocuments, None, "The index carries no reference documents.")),

        Importer("style-dictionary", "Style Dictionary",
            Row(DesignImportAxis.Tokens, Full, "Config source globs feed the DTCG reader."),
            Row(DesignImportAxis.Modes, None, "A Style Dictionary config carries no mode values."),
            Row(DesignImportAxis.Assets, None, "A config names token files, not asset bytes."),
            Row(DesignImportAxis.Fonts, None, "Font families stay typography tokens; no font files."),
            Row(DesignImportAxis.Components, None, "A config carries no component inventory."),
            Row(DesignImportAxis.PreviewCards, None, "A config carries no preview cards."),
            Row(DesignImportAxis.Prose, None, "A config carries no prose documents."),
            Row(DesignImportAxis.Voice, None, "A config carries no voice guidance."),
            Row(DesignImportAxis.DerivedTokens, Partial, "Delegated token values pass through the shared normalizer."),
            Row(DesignImportAxis.ReferenceDocuments, None, "A config carries no reference documents.")),

        Importer("tailwind-v3", "Tailwind v3 config",
            Row(DesignImportAxis.Tokens, Full, "theme and theme.extend keys map to tokens."),
            Row(DesignImportAxis.Modes, None, "A Tailwind config carries no mode values."),
            Row(DesignImportAxis.Assets, None, "The config names no asset bytes."),
            Row(DesignImportAxis.Fonts, None, "Font families map to typography tokens; no font files."),
            Row(DesignImportAxis.Components, None, "The config carries no component inventory."),
            Row(DesignImportAxis.PreviewCards, None, "The config carries no preview cards."),
            Row(DesignImportAxis.Prose, None, "The config carries no prose documents."),
            Row(DesignImportAxis.Voice, None, "The config carries no voice guidance."),
            Row(DesignImportAxis.DerivedTokens, Partial, "Values pass through the shared token normalizer."),
            Row(DesignImportAxis.ReferenceDocuments, None, "The config carries no reference documents.")),

        Importer("figma-variables", "Figma variables",
            Row(DesignImportAxis.Tokens, Full, "Variables become tokens under their dot paths."),
            Row(DesignImportAxis.Modes, Full, "Collection modes become light and dark mode values."),
            Row(DesignImportAxis.Assets, None, "The Figma variable payload carries no asset bytes."),
            Row(DesignImportAxis.Fonts, None, "Font variables stay typography tokens; no font files."),
            Row(DesignImportAxis.Components, None, "The variable payload carries no component inventory."),
            Row(DesignImportAxis.PreviewCards, None, "The variable payload carries no preview cards."),
            Row(DesignImportAxis.Prose, None, "The variable payload carries no prose documents."),
            Row(DesignImportAxis.Voice, None, "The variable payload carries no voice guidance."),
            Row(DesignImportAxis.DerivedTokens, Partial, "Values pass through the shared token normalizer."),
            Row(DesignImportAxis.ReferenceDocuments, None, "The variable payload carries no reference documents.")),

        Importer("brand-pdf", "Brand PDF",
            Row(DesignImportAxis.Tokens, Partial, "Only hex colours and typeface names from the reduced PDF text."),
            Row(DesignImportAxis.Modes, None, "A brand PDF states no modes."),
            Row(DesignImportAxis.Assets, None, "PDF images are not extracted."),
            Row(DesignImportAxis.Fonts, Partial, "Typeface names become typography families; no font files."),
            Row(DesignImportAxis.Components, None, "A brand PDF carries no component inventory."),
            Row(DesignImportAxis.PreviewCards, None, "A brand PDF carries no preview cards."),
            Row(DesignImportAxis.Prose, None, "The PDF text is reduced to colours and typefaces, not kept as prose."),
            Row(DesignImportAxis.Voice, None, "Tone words are never inferred from a PDF."),
            Row(DesignImportAxis.DerivedTokens, None, "A brand PDF has no derived token values to resolve."),
            Row(DesignImportAxis.ReferenceDocuments, None, "The PDF text is not kept as a reference document.")),

        Importer("penpot", "Penpot",
            Row(DesignImportAxis.Tokens, Partial, "Colour and typography library entries only."),
            Row(DesignImportAxis.Modes, None, "The reader does not read Penpot themes."),
            Row(DesignImportAxis.Assets, None, "Page images are never read."),
            Row(DesignImportAxis.Fonts, Partial, "Typography entries carry font-family names; no font files."),
            Row(DesignImportAxis.Components, None, "The reader does not read Penpot components."),
            Row(DesignImportAxis.PreviewCards, None, "The reader does not read Penpot boards or cards."),
            Row(DesignImportAxis.Prose, None, "The reader does not read Penpot text content."),
            Row(DesignImportAxis.Voice, None, "Penpot carries no voice guidance."),
            Row(DesignImportAxis.DerivedTokens, None, "Penpot values are read as literals, not derived tokens."),
            Row(DesignImportAxis.ReferenceDocuments, None, "Penpot carries no reference documents.")),

        Importer("sketch", "Sketch",
            Row(DesignImportAxis.Tokens, Partial, "Shared colour swatches and layer text styles only."),
            Row(DesignImportAxis.Modes, None, "The reader does not read Sketch themes."),
            Row(DesignImportAxis.Assets, None, "Pages and layer trees are never read, so images are not imported."),
            Row(DesignImportAxis.Fonts, Partial, "Text styles carry PostScript font names; no font files."),
            Row(DesignImportAxis.Components, None, "Layer trees are never read, so there is no component inventory."),
            Row(DesignImportAxis.PreviewCards, None, "Pages are never read, so there are no preview cards."),
            Row(DesignImportAxis.Prose, None, "Only text-style metadata is read, not prose content."),
            Row(DesignImportAxis.Voice, None, "Sketch carries no voice guidance."),
            Row(DesignImportAxis.DerivedTokens, None, "Sketch values are read as literals, not derived tokens."),
            Row(DesignImportAxis.ReferenceDocuments, None, "Sketch carries no reference documents.")),

        Importer("claude-design", "Claude Design",
            Row(DesignImportAxis.Tokens, Full, "Token stylesheets and manifest themes become the design document."),
            Row(DesignImportAxis.Modes, Full, "The manifest's themes become named modes."),
            Row(DesignImportAxis.Assets, Full, "SVG marks and font files are imported as sanitized assets."),
            Row(DesignImportAxis.Fonts, Partial, "Font files when present, declarations otherwise; remote URLs are reported, never fetched."),
            Row(DesignImportAxis.Components, Full, "The export's component sources become product-map components."),
            Row(DesignImportAxis.PreviewCards, Full, "Manifest and markup cards become structured preview cards."),
            Row(DesignImportAxis.Prose, Partial, "Readme and guideline prose is kept, but not every passage becomes a reference document."),
            Row(DesignImportAxis.Voice, Partial, "The readme's opening paragraph becomes voice.summary; SKILL.md supplies only the name and description."),
            Row(DesignImportAxis.DerivedTokens, Partial, "CSS values resolve through the shared normalizer; some mixes and gradients stay unmappable."),
            Row(DesignImportAxis.ReferenceDocuments, Full, "Readme, guidelines, ui kits, source notes and lint config become reference documents.")),
    ];

    /// <summary>The row for <paramref name="importerId"/>, or <c>null</c> when the id is not registered.</summary>
    public static DesignImporterCapabilities? For(string importerId) =>
        string.IsNullOrEmpty(importerId)
            ? null
            : Matrix.FirstOrDefault(m => string.Equals(m.ImporterId, importerId, StringComparison.Ordinal));

    private static DesignImporterCapabilities Importer(
        string importerId, string label, params DesignImportAxisCapability[] axes) =>
        new(importerId, label, axes);

    private static DesignImportAxisCapability Row(
        DesignImportAxis axis, DesignImportSupport support, string note) =>
        new(axis, support, note);
}
