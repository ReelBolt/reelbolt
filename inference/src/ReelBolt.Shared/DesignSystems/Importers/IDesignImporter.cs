using System.Text.Json.Nodes;

namespace ReelBolt.Shared.DesignSystems.Importers;

/// <summary>
/// One file offered to an importer: its logical path, its bytes, the caller's (untrusted) SHA-256
/// and the <c>project_files</c> row it came from, when any.
/// </summary>
public sealed record ImportFile(string Path, ReadOnlyMemory<byte> Content, string Sha256, Guid? ProjectFileId);

/// <summary>One file an importer actually consumed, with the digest it recomputed from the bytes.</summary>
public sealed record ImportSourceRef(string Path, string Importer, string Sha256, Guid? ProjectFileId);

/// <summary>
/// One thing an importer noticed while reading its input. <see cref="Axis"/> names the design axis
/// the notice belongs to; when it is absent the report builder maps the code with
/// <see cref="DesignImportReportBuilder.AxisForCode"/>.
/// </summary>
public sealed record ImportDiagnostic(ImportDiagnosticSeverity Severity, string Code, string Path, string Message)
{
    /// <summary>The axis this notice belongs to, or null when the construction site did not tag it.</summary>
    public DesignImportAxis? Axis { get; init; }
}

/// <summary>How serious an <see cref="ImportDiagnostic"/> is. Append-only.</summary>
public enum ImportDiagnosticSeverity
{
    Info,
    Warn,
    Error,
}

/// <summary>The outcome of one importer run: a design document, an optional product map, and the facts found on the way.</summary>
public sealed record DesignImportResult(
    JsonObject? Design,
    ProductMapDocument? ProductMap,
    IReadOnlyList<ImportSourceRef> Sources,
    IReadOnlyList<ImportDiagnostic> Diagnostics);

/// <summary>
/// A pure, never-throwing reader of one design-source dialect. <see cref="Import"/> performs no IO:
/// the caller supplies the bytes.
/// </summary>
public interface IDesignImporter
{
    /// <summary>The stable importer identifier, e.g. <c>dtcg</c>.</summary>
    string Id { get; }

    /// <summary>True when <paramref name="path"/> and the first <see cref="ImportLimits.HeadBytes"/> bytes look like this dialect.</summary>
    bool CanRead(string path, ReadOnlySpan<byte> head);

    /// <summary>PURE; never throws. Reads the supplied files and returns whatever could be understood.</summary>
    DesignImportResult Import(IReadOnlyList<ImportFile> files);
}

/// <summary>The numeric caps of the importer contract.</summary>
public static class ImportLimits
{
    /// <summary>The largest JSON source accepted, in bytes.</summary>
    public const int JsonMaxBytes = 2 * 1024 * 1024;

    /// <summary>The largest CSS source accepted, in bytes.</summary>
    public const int CssMaxBytes = 2 * 1024 * 1024;

    /// <summary>The largest Markdown source accepted, in bytes.</summary>
    public const int MarkdownMaxBytes = 512 * 1024;

    /// <summary>The largest SVG source accepted as inert text, in bytes.</summary>
    public const int SvgMaxBytes = 512 * 1024;

    /// <summary>The largest HTML/XHTML source accepted as inert text, in bytes.</summary>
    public const int MarkupMaxBytes = 256 * 1024;

    /// <summary>The deepest JSON nesting accepted by a parser.</summary>
    public const int JsonMaxDepth = 32;

    /// <summary>The largest number of JSON nodes accepted.</summary>
    public const int JsonMaxNodes = 200000;

    /// <summary>The number of leading bytes an importer may inspect to answer <see cref="IDesignImporter.CanRead"/>.</summary>
    public const int HeadBytes = 4096;
}
