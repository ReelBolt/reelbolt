using System.Text.Json.Serialization;

namespace ReelBolt.ImportWorker;

/// <summary>
/// One accepted zip entry, always carrying its hash and size; <see cref="Text"/> or <see cref="Base64"/> is filled
/// only when inlined (never both, and never for a dropped entry).
/// </summary>
public sealed record WorkerFileDto(
    string Path,
    string Sha256,
    long Size,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Base64 = null);

/// <summary>One zip entry the reader or the classifier refused, with the exact reason code.</summary>
public sealed record WorkerDroppedDto(string Path, string Reason);

/// <summary>Counts for the accepted entries only: how many files and how many bytes were returned.</summary>
public sealed record WorkerStatsDto(int Entries, long Bytes);

/// <summary>The <c>POST /v1/extract-zip</c> body. On an abort, <see cref="Files"/> is empty and <see cref="AbortReason"/> is set.</summary>
public sealed record ExtractZipResponse(
    IReadOnlyList<WorkerFileDto> Files,
    IReadOnlyList<WorkerDroppedDto> Dropped,
    WorkerStatsDto Stats,
    bool Aborted,
    string? AbortReason);

/// <summary>The <c>POST /v1/pdf-text</c> success body.</summary>
/// <param name="TotalPages">The document's page count, or null when the worker could not tell.</param>
/// <param name="PagesRead">How many pages the extraction covered (at most the page limit); null when the total is unknown.</param>
public sealed record PdfTextResponse(string Text, bool Truncated, int? TotalPages = null, int? PagesRead = null);

/// <summary>A minimal error body for endpoints that return one.</summary>
public sealed record ErrorResponse(string Error);

/// <summary>What a handler decided: an HTTP status and an optional body (null writes an empty body).</summary>
public sealed record WorkerResult(int Status, object? Body);
