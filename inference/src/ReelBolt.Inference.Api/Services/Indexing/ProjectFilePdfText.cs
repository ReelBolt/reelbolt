using System.Globalization;
using System.Text;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Indexing;

/// <summary>
/// Who is asking for a PDF's text. An <see cref="Interactive"/> read (the assistant, a person waiting) gives up with
/// <see cref="ProjectPdfTextFailure.Busy"/> after <see cref="ProjectPdfTextCache.GateTimeout"/>; a <see cref="Background"/> read
/// (summarizer, indexer: nothing re-queues them after a Busy) waits for an extraction slot, bounded only by its own cancellation.
/// </summary>
public enum PdfReadPurpose
{
    Interactive,
    Background,
}

/// <summary>Why a project PDF's text could not be read. <see cref="None"/> means it was read.</summary>
public enum ProjectPdfTextFailure
{
    None,
    TooLarge,
    NotPdf,
    NoText,
    Unreadable,
    Unavailable,
    Busy,
    BadResponse,

    /// <summary>The operator switched this path off (<c>Import:Pdf:ProjectText:Enabled</c>=false); the worker is never called.</summary>
    Disabled,
}

/// <summary>
/// One answer of <see cref="IProjectFilePdfText"/>: the text (empty on failure) or a plain message naming the
/// reason and what to do. <see cref="Message"/> is safe to show to a user or hand to a model.
/// </summary>
public sealed record ProjectPdfText(ProjectPdfTextFailure Failure, string Text, bool Truncated, string? Message, int? PagesRead = null, int? TotalPages = null)
{
    /// <summary>True only when the worker reported that pages after the limit were left out.</summary>
    public bool PagesCut => PagesRead is { } read && TotalPages is { } total && read < total;

    public bool Ok => Failure == ProjectPdfTextFailure.None;

    /// <summary>A stable machine code for tool JSON, e.g. <c>PDF_NO_TEXT</c>.</summary>
    public string Code => Failure switch
    {
        ProjectPdfTextFailure.TooLarge => "PDF_TOO_LARGE",
        ProjectPdfTextFailure.NotPdf => "NOT_A_PDF",
        ProjectPdfTextFailure.NoText => "PDF_NO_TEXT",
        ProjectPdfTextFailure.Unreadable => "PDF_UNREADABLE",
        ProjectPdfTextFailure.Unavailable or ProjectPdfTextFailure.Disabled => "PDF_READER_UNAVAILABLE",
        ProjectPdfTextFailure.Busy => "PDF_READER_BUSY",
        ProjectPdfTextFailure.BadResponse => "IMPORT_WORKER_BAD_RESPONSE",
        _ => string.Empty,
    };

    /// <summary>True for a failure that a later attempt may cure (the worker, not the document).</summary>
    public bool Transient => Failure is ProjectPdfTextFailure.Unavailable or ProjectPdfTextFailure.Busy or ProjectPdfTextFailure.BadResponse;
}

/// <summary>
/// The text of a project PDF. The PDF bytes are parsed only inside the import-worker (the API never links a PDF
/// library); this is the one place that sends a project file there and the one place that turns the answer into text
/// the assistant, the summarizer and the indexer may use. The text is untrusted file content.
/// </summary>
public interface IProjectFilePdfText
{
    Task<ProjectPdfText> ReadAsync(ProjectFile file, PdfReadPurpose purpose, CancellationToken ct);
}

/// <summary>The in-process, size-bounded cache of extracted PDF text: one extraction per PDF version per process.</summary>
public sealed class ProjectPdfTextCache
{
    /// <summary>Total characters kept across all entries.</summary>
    public const long MaxCachedChars = 32L * 1024 * 1024;

    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    internal MemoryCache Entries { get; } = new(new MemoryCacheOptions { SizeLimit = MaxCachedChars });

    /// <summary>Lets at most two extractions run at once (the worker's own slots are bounded too); a reader of a PDF another reader just extracted finds it in the cache after waiting.</summary>
    internal SemaphoreSlim Gate { get; } = new(2, 2);

    /// <summary>The longest an interactive reader waits for a free extraction slot before answering Busy (background readers wait without a limit).</summary>
    public TimeSpan GateTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public static class ProjectFilePdf
{
    /// <summary>The largest PDF sent to the worker, the same cap as the brand-PDF route.</summary>
    public const long MaxPdfBytes = 20L * 1024 * 1024;

    /// <summary>The most extracted characters any reader of a PDF gets (indexer and summarizer cap on their own byte limits too).</summary>
    public const int MaxTextChars = 200 * 1024;

    /// <summary>True when the stored MIME type or file name says PDF (the bytes are checked separately).</summary>
    public static bool IsPdf(string? mimeType, string? fileName)
    {
        string mime = (mimeType ?? string.Empty).Split(';')[0].Trim();
        if (mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            return true;

        bool genericMime = mime.Length == 0 || mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
        return genericMime && string.Equals(Path.GetExtension(fileName ?? string.Empty), ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Says pages were left out, with the worker's own counts; empty when nothing was dropped or the worker did not say.</summary>
    public static string PageNote(ProjectPdfText pdf)
    {
        if (pdf.PagesCut)
            return $"Only the first {pdf.PagesRead} of {pdf.TotalPages} pages of this PDF were read; text from later pages is not included.";

        // No page counts (an older worker, or counts dropped as implausible) but the worker or the text cap says text was cut.
        return pdf.PagesRead is null && pdf.TotalPages is null && pdf.Truncated
            ? "Only part of this document was read; the rest is not included."
            : string.Empty;
    }

    public static string Message(ProjectPdfTextFailure failure) => failure switch
    {
        ProjectPdfTextFailure.TooLarge =>
            "This PDF is larger than 20 MB, which is the most ReelBolt reads text from. Split it or export a smaller PDF and upload that.",
        ProjectPdfTextFailure.NotPdf =>
            "This file is named like a PDF but its content is not a PDF, so no text could be read. Upload the real PDF or a text version.",
        ProjectPdfTextFailure.NoText =>
            "This PDF has no selectable text (it looks like a scan or an image-only PDF) and ReelBolt does not do OCR. Upload a text-based export, or paste the text into a text file.",
        ProjectPdfTextFailure.Unreadable =>
            "ReelBolt could not read text from this PDF (it may be damaged or password-protected). Upload a fresh export of it.",
        ProjectPdfTextFailure.Disabled =>
            "PDF reading is switched off on this workspace's server, so no text was read from this PDF. Ask your administrator to turn it on, or upload a text version of the document.",
        ProjectPdfTextFailure.Unavailable =>
            "PDF text reading is not available right now because the document reader could not be reached. Try again in a few minutes.",
        ProjectPdfTextFailure.Busy =>
            "The PDF reader is busy. Try again in a minute.",
        ProjectPdfTextFailure.BadResponse =>
            "The PDF reader gave an answer ReelBolt could not accept, so no text was read. Try again later.",
        _ => string.Empty,
    };

    /// <summary>Drops control characters (keeps tab and newlines), so extracted text is plain readable text.</summary>
    public static string Clean(string text)
    {
        // Line breaks first: a CR-only PDF would otherwise lose them, a CRLF pair counts once.
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        StringBuilder sb = new(Math.Min(text.Length, MaxTextChars));
        foreach (char c in text)
        {
            if (sb.Length >= MaxTextChars)
                break;
            // Control characters (C0, DEL, C1) and Format characters (bidi controls, zero-width space, BOM, soft hyphen) are
            // dropped; ZWNJ and ZWJ stay because they are part of Persian and Indic words and emoji sequences.
            if (c is '\n' or '\t' || (c >= ' ' && c != '\u007f' && !(c >= '\u0080' && c <= '\u009f')
                && (c is '\u200C' or '\u200D' || char.GetUnicodeCategory(c) != UnicodeCategory.Format)))
                sb.Append(c);
        }

        return sb.ToString().Trim();
    }
}

public sealed class ProjectFilePdfText : IProjectFilePdfText
{
    private readonly IFileStorageService _storage;
    private readonly ImportWorkerClient _worker;
    private readonly ProjectPdfTextCache _cache;
    private readonly IConfiguration? _configuration;

    public ProjectFilePdfText(IFileStorageService storage, ImportWorkerClient worker, ProjectPdfTextCache cache, IConfiguration? configuration = null)
    {
        _configuration = configuration;
        _storage = storage;
        _worker = worker;
        _cache = cache;
    }

    public async Task<ProjectPdfText> ReadAsync(ProjectFile file, PdfReadPurpose purpose, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);

        // Operator off-switch: nothing is read, cached or sent to the worker.
        if (!(_configuration?.GetValue<bool?>("Import:Pdf:ProjectText:Enabled") ?? true))
            return Fail(ProjectPdfTextFailure.Disabled);

        if (file.SizeBytes > ProjectFilePdf.MaxPdfBytes)
            return Fail(ProjectPdfTextFailure.TooLarge);

        // A PDF version is its id, size and upload time (an overwrite replaces the bytes and the upload time).
        string key = $"{file.Id:N}:{file.SizeBytes}:{file.UploadedAt.Ticks}";
        if (_cache.Entries.TryGetValue(key, out ProjectPdfText? hit) && hit is not null)
            return hit;

        if (!_worker.IsConfigured)
            return Fail(ProjectPdfTextFailure.Unavailable);

        if (purpose == PdfReadPurpose.Background)
            await _cache.Gate.WaitAsync(ct);
        else if (!await _cache.Gate.WaitAsync(_cache.GateTimeout, ct))
            return Fail(ProjectPdfTextFailure.Busy);

        try
        {
            if (_cache.Entries.TryGetValue(key, out hit) && hit is not null)
                return hit;

            byte[] bytes;
            await using (Stream stream = await _storage.DownloadAsync(file.ProjectId, file.StorageKey, ct))
            {
                using MemoryStream buffer = new();
                byte[] chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + read > ProjectFilePdf.MaxPdfBytes)
                        return Fail(ProjectPdfTextFailure.TooLarge);
                    buffer.Write(chunk, 0, read);
                }

                bytes = buffer.ToArray();
            }

            if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F' || bytes[4] != '-')
                return Remember(key, Fail(ProjectPdfTextFailure.NotPdf));

            using MemoryStream pdf = new(bytes, writable: false);
            ImportWorkerPdfTextResult worker = await _worker.ExtractPdfTextAsync(pdf, ct);
            ProjectPdfText result = worker.Status switch
            {
                ImportWorkerStatus.Ok => FromText(worker),
                ImportWorkerStatus.TooLarge => Fail(ProjectPdfTextFailure.TooLarge),
                ImportWorkerStatus.NotPdf => Fail(ProjectPdfTextFailure.NotPdf),
                ImportWorkerStatus.Unreadable => Fail(ProjectPdfTextFailure.Unreadable),
                ImportWorkerStatus.Busy => Fail(ProjectPdfTextFailure.Busy),
                ImportWorkerStatus.Unavailable => Fail(ProjectPdfTextFailure.Unavailable),
                _ => Fail(ProjectPdfTextFailure.BadResponse),
            };

            // Only answers about the document are kept; a worker hiccup is retried by the next reader.
            return result.Transient ? result : Remember(key, result);
        }
        finally
        {
            _cache.Gate.Release();
        }
    }

    private static ProjectPdfText FromText(ImportWorkerPdfTextResult worker)
    {
        string text = ProjectFilePdf.Clean(worker.Text ?? string.Empty);
        if (text.Length == 0)
            return Fail(ProjectPdfTextFailure.NoText);

        return new ProjectPdfText(ProjectPdfTextFailure.None, text, worker.Truncated || worker.Text!.Length > ProjectFilePdf.MaxTextChars, null, worker.PagesRead, worker.TotalPages);
    }

    private static ProjectPdfText Fail(ProjectPdfTextFailure failure) =>
        new(failure, string.Empty, false, ProjectFilePdf.Message(failure));

    private ProjectPdfText Remember(string key, ProjectPdfText value)
    {
        _cache.Entries.Set(key, value, new MemoryCacheEntryOptions
        {
            Size = Math.Max(1, value.Text.Length),
            AbsoluteExpirationRelativeToNow = ProjectPdfTextCache.Lifetime,
        });
        return value;
    }
}
