using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// One archive-level skip reason and how many entries it removed. The worker's
/// <c>DROPPED_SCRIPT</c>/<c>DROPPED_HTML</c>/<c>DROPPED_BINARY</c> counts travel here, never the
/// entry names themselves.
/// </summary>
public sealed record ImportSkippedGroup(string Reason, int Count);

/// <summary>One code every diagnostic of which was grouped into a single bounded reason row.</summary>
public sealed record ImportDropGroup(
    string Code, ImportDiagnosticSeverity Severity, int Count, string Message);

/// <summary>One axis of one import: what was imported, what was dropped, and why.</summary>
public sealed record ImportAxisReport(
    DesignImportAxis Axis,
    DesignImportSupport Support,
    int Imported,
    int Dropped,
    IReadOnlyList<ImportDropGroup> Reasons);

/// <summary>
/// The axis-tagged answer to "what did this import actually give me, and what did it drop?".
/// A report is produced by <see cref="DesignImportReportBuilder"/> and attached to every import
/// response; the web client renders one row per axis.
/// </summary>
public sealed record DesignImportReport(
    string ImporterId, string? Vendor, IReadOnlyList<ImportAxisReport> Axes);

/// <summary>
/// Counts one import result into the ten <see cref="DesignImportAxis"/> rows. Pure: it reads the
/// document, product map, diagnostics and the caller's kept asset/reference/skip lists and never
/// throws. Diagnostics tagged with <see cref="ImportDiagnostic.Axis"/> are honoured; an untagged
/// diagnostic is mapped by a small code table and otherwise counts under <see cref="DesignImportAxis.Tokens"/>.
/// </summary>
public static class DesignImportReportBuilder
{
    /// <summary>The most distinct reason codes one axis reports; the rest are summarised away.</summary>
    public const int MaxReasonsPerAxis = 20;

    /// <summary>Maps a diagnostic code onto its axis when the construction site did not tag it.</summary>
    public static DesignImportAxis AxisForCode(string? code) => code switch
    {
        "SVG_REFUSED" or "SVG_SANITIZED"
            or "CLAUDE_DESIGN_ASSETS" or "ASSET_ROLE_UNKNOWN" or "ASSET_TOO_LARGE"
            or "ASSET_DIGEST_MISMATCH" or "ASSET_TYPE_MISMATCH" or "ASSET_QUARANTINED"
            or "ASSET_CAP" or "ASSETS_NOT_UPLOADED" or "LOGO_NAME_TONE_MISMATCH" => DesignImportAxis.Assets,

        "PREVIEW_CARDS" or "PREVIEW_CARD_CAP" or "BUNDLE_FILE_DROPPED"
            or "BUNDLE_MARKUP_NOT_READ" => DesignImportAxis.PreviewCards,

        "CLAUDE_DESIGN_REFERENCES" or "REFERENCE_TOO_LARGE" or "REFERENCE_CAP"
            or "SECRET_REDACTED" or "REFERENCES_NOT_UPLOADED" => DesignImportAxis.ReferenceDocuments,

        "CLAUDE_DESIGN_FONTS" or "FONT_SOURCE_NOT_FETCHED" or "FONT_FILE_REJECTED"
            => DesignImportAxis.Fonts,

        "CLAUDE_DESIGN_COMPONENTS" or "PRODUCT_MAP_NOT_CREATED" => DesignImportAxis.Components,
        "CLAUDE_DESIGN_MODES" => DesignImportAxis.Modes,

        "MIX_UNRESOLVED" or "GRADIENT_NOT_REPRESENTABLE" or "CSS_TOKEN_UNMAPPED"
            => DesignImportAxis.DerivedTokens,

        "UNSUPPORTED_VALUE" => DesignImportAxis.Tokens,

        _ => DesignImportAxis.Tokens,
    };

    /// <summary>
    /// Maps an archive skip reason onto the axis whose entries it removed. Scripts, HTML/markup,
    /// compiled bundles and scratch/screenshot material are unread support prose, so they belong to
    /// <see cref="DesignImportAxis.ReferenceDocuments"/>; images and SVG belong to Assets; an
    /// oversized Sketch/Penpot <c>document.json</c> is a token/component loss, not a reference.
    /// </summary>
    public static DesignImportAxis AxisForReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason))
            return DesignImportAxis.Tokens;

        bool Has(string token) => reason.Contains(token, StringComparison.OrdinalIgnoreCase);

        if (Has("FONT"))
            return DesignImportAxis.Fonts;
        if (Has("IMAGE") || Has("SVG") || Has("ASSET"))
            return DesignImportAxis.Assets;
        if (Has("DOCUMENT_TOO_LARGE"))
            return DesignImportAxis.Tokens;
        if (Has("SCREENSHOT") || Has("CLIENT_SCRATCH") || Has("SCRIPT") || Has("HTML")
            || Has("MARKUP") || Has("COMPILED_BUNDLE") || Has("NOT_EXECUTED")
            || Has("REFERENCE") || Has("PROSE") || Has("README") || Has("DOCUMENT"))
        {
            return DesignImportAxis.ReferenceDocuments;
        }

        if (Has("CARD") || Has("PREVIEW"))
            return DesignImportAxis.PreviewCards;
        if (Has("TOKEN") || Has("MIX") || Has("GRADIENT") || Has("CSS"))
            return DesignImportAxis.DerivedTokens;

        return DesignImportAxis.Tokens;
    }

    /// <summary>
    /// True when one diagnostic describes something the import lost. Every Warn/Error is a loss; an
    /// Info is normally a summary or a success note (<c>CLAUDE_DESIGN_ASSETS</c>, <c>SVG_SANITIZED</c>,
    /// <c>PREVIEW_CARDS</c>, …) and only counts when its code names a real removal: the known loss
    /// codes below, or a code ending in one of <see cref="InfoDropSuffixes"/>. This is the single
    /// predicate behind both <see cref="ImportAxisReport.Dropped"/> and its reason list.
    /// </summary>
    public static bool IsDropDiagnostic(ImportDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);

        if (diagnostic.Severity is ImportDiagnosticSeverity.Warn or ImportDiagnosticSeverity.Error)
            return true;

        string code = diagnostic.Code ?? string.Empty;
        if (code.Length == 0)
            return false;

        if (InfoDropCodes.Contains(code))
            return true;

        foreach (string suffix in InfoDropSuffixes)
        {
            if (code.EndsWith(suffix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static readonly HashSet<string> InfoDropCodes = new(StringComparer.Ordinal)
    {
        "CLAUDE_DESIGN_FILE_SKIPPED",
        "BUNDLE_MARKUP_NOT_READ",
        "CSS_TOKEN_UNMAPPED",
        "ALIAS_UNRESOLVED",
        "SD_INCLUDE_IGNORED",
        "SD_NON_JSON_SOURCE",
        "UNSAFE_FONT",
        "NO_TOKENS_RECOGNIZED",
        "SKETCH_NO_LIBRARY",
        "PENPOT_NO_LIBRARY",
    };

    private static readonly string[] InfoDropSuffixes =
    [
        "_SKIPPED", "_TOO_LARGE", "_CAP", "_DROPPED", "_REFUSED",
        "_QUARANTINED", "_NOT_UPLOADED", "_NOT_CREATED",
    ];

    /// <summary>
    /// Counts <paramref name="result"/> into ten axis rows. <paramref name="assets"/> is the reader's
    /// kept imported-asset list, <paramref name="references"/> its kept reference documents, and
    /// <paramref name="skipped"/> the archive-level drops reported by the worker.
    /// </summary>
    public static DesignImportReport Build(
        string importerId,
        string? vendor,
        DesignImportResult result,
        IReadOnlyList<ImportedAsset>? assets = null,
        IReadOnlyList<ImportedReferenceDocument>? references = null,
        IReadOnlyList<ImportSkippedGroup>? skipped = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        JsonObject? design = result.Design;
        IReadOnlyList<ImportDiagnostic> diagnostics = result.Diagnostics ?? [];
        IReadOnlyList<ImportDiagnostic> drops = diagnostics.Where(IsDropDiagnostic).ToList();

        var imported = new Dictionary<DesignImportAxis, int>
        {
            // The resolved derived destinations actually present in the document, never a diagnostic
            // count: CSS_TOKEN_UNMAPPED is a drop, and a clean resolution emits no diagnostic at all.
            [DesignImportAxis.DerivedTokens] = DerivedTokenCount(design),
            [DesignImportAxis.Tokens] = design is null ? 0 : DesignDocument.Tokens(design).Count(),
            [DesignImportAxis.Modes] = ObjectKeyCount(design, "modes"),
            [DesignImportAxis.Assets] = assets?.Count ?? 0,
            [DesignImportAxis.Fonts] = FontCount(design),
            [DesignImportAxis.Components] = result.ProductMap?.Components?.Count ?? 0,
            [DesignImportAxis.PreviewCards] = result.ProductMap?.PreviewCards?.Count ?? 0,
            [DesignImportAxis.Voice] = HasVoice(design) ? 1 : 0,
            [DesignImportAxis.Prose] = references is not null && references.Any(IsProse) ? 1 : 0,
            [DesignImportAxis.ReferenceDocuments] = references?.Count ?? 0,
        };

        Dictionary<DesignImportAxis, List<ImportDiagnostic>> byAxis = drops
            .GroupBy(AxisOf)
            .ToDictionary(group => group.Key, group => group.ToList());

        Dictionary<DesignImportAxis, List<ImportSkippedGroup>> skippedByAxis = (skipped ?? [])
            .GroupBy(group => AxisForReason(group.Reason))
            .ToDictionary(group => group.Key, group => group.ToList());

        var axes = new List<ImportAxisReport>();
        foreach (DesignImportAxis axis in Enum.GetValues<DesignImportAxis>())
        {
            int dropped = 0;
            var reasons = new List<ImportDropGroup>();

            if (byAxis.TryGetValue(axis, out List<ImportDiagnostic>? axisDiagnostics))
            {
                dropped += axisDiagnostics.Count;
                reasons.AddRange(GroupDiagnostics(axisDiagnostics));
            }

            if (skippedByAxis.TryGetValue(axis, out List<ImportSkippedGroup>? axisSkips))
            {
                dropped += axisSkips.Sum(group => group.Count);
                reasons.AddRange(axisSkips.Select(group => new ImportDropGroup(
                    group.Reason,
                    ImportDiagnosticSeverity.Info,
                    group.Count,
                    $"{group.Count} file(s) were skipped ({group.Reason})")));
            }

            IReadOnlyList<ImportDropGroup> capped = reasons
                .OrderByDescending(reason => reason.Severity)
                .ThenByDescending(reason => reason.Count)
                .ThenBy(reason => reason.Code, StringComparer.Ordinal)
                .Take(MaxReasonsPerAxis)
                .ToList();

            axes.Add(new ImportAxisReport(
                axis, SupportFor(importerId, axis), imported[axis], dropped, capped));
        }

        return new DesignImportReport(importerId, vendor, axes);
    }

    private static DesignImportAxis AxisOf(ImportDiagnostic diagnostic) =>
        diagnostic.Axis ?? AxisForCode(diagnostic.Code);

    private static DesignImportSupport SupportFor(string importerId, DesignImportAxis axis)
    {
        DesignImporterCapabilities? importer = DesignImportCapabilities.For(importerId);
        DesignImportAxisCapability? capability = importer?.Axes
            .FirstOrDefault(row => row.Axis == axis);
        return capability?.Support ?? DesignImportSupport.None;
    }

    private static IEnumerable<ImportDropGroup> GroupDiagnostics(
        IReadOnlyList<ImportDiagnostic> diagnostics) =>
        diagnostics
            .GroupBy(diagnostic => diagnostic.Code ?? string.Empty, StringComparer.Ordinal)
            .Select(group => new ImportDropGroup(
                group.Key,
                group.Max(diagnostic => diagnostic.Severity),
                group.Count(),
                group.Select(diagnostic => diagnostic.Message)
                    .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message)) ?? group.Key));

    private static int ObjectKeyCount(JsonObject? design, string section) =>
        design?[section] is JsonObject obj ? obj.Count : 0;

    /// <summary>
    /// The resolved derived destinations in the document: every token in the <c>z</c> group, every
    /// token whose <c>$type</c> is <c>keyword</c>, and every shadow token whose value is the empty
    /// array (<c>none</c>). A resolved <c>color-mix()</c> becomes an ordinary colour token and is not
    /// distinguishable after the fact, so it is not counted here.
    /// </summary>
    private static int DerivedTokenCount(JsonObject? design)
    {
        if (design is null)
            return 0;

        int count = 0;
        foreach (DesignTokenRef token in DesignDocument.Tokens(design))
        {
            if (token.Path.StartsWith("z.", StringComparison.Ordinal)
                || string.Equals(token.Type, "keyword", StringComparison.Ordinal)
                || (string.Equals(token.Type, "shadow", StringComparison.Ordinal)
                    && token.Value is JsonArray array && array.Count == 0))
            {
                count++;
            }
        }

        return count;
    }

    private static int FontCount(JsonObject? design)
    {
        if (design?["fonts"] is not JsonObject fonts)
            return 0;

        int licence = (fonts["licence"] as JsonObject)?.Count ?? 0;
        int files = (fonts["files"] as JsonObject)?.Count ?? 0;
        return licence + files;
    }

    private static bool HasVoice(JsonObject? design)
    {
        if (design?["voice"] is not JsonObject voice)
            return false;

        return IsNonEmpty(voice["summary"]) || IsNonEmpty(voice["do"]) || IsNonEmpty(voice["dont"]);
    }

    private static bool IsNonEmpty(JsonNode? node) => node switch
    {
        JsonArray array => array.Count > 0,
        JsonValue value => value.TryGetValue(out string? text) && !string.IsNullOrWhiteSpace(text),
        _ => false,
    };

    private static bool IsProse(ImportedReferenceDocument reference) =>
        reference.MimeType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
        || reference.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        || reference.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
}
