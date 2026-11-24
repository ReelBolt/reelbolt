using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using ReelBolt.Shared.Archives;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.DesignSystems.Importers;

namespace ReelBolt.Inference.Api.Services.DesignSystems;

/// <summary>What the import-worker said about a PDF body.</summary>
public enum ImportWorkerStatus
{
    /// <summary>The worker extracted text.</summary>
    Ok,

    /// <summary>The worker could not be reached, timed out, or refused the connection.</summary>
    Unavailable,

    /// <summary>The worker answered 413: the body is over its PDF cap.</summary>
    TooLarge,

    /// <summary>The worker answered 415: the body does not start with <c>%PDF-</c>.</summary>
    NotPdf,

    /// <summary>The worker answered 415: the body does not start with <c>PK\x03\x04</c>.</summary>
    NotZip,

    /// <summary>The worker answered 422: the bytes are a PDF but the text could not be extracted.</summary>
    Unreadable,

    /// <summary>The worker answered 503 (WORKER_BUSY): it refused rather than queued; retry later.</summary>
    Busy,

    /// <summary>
    /// The worker answered 200 but the body broke a bound the API enforces on worker output (a hostile or
    /// buggy worker): a bad path, size, digest, kind, text, count, or a truncated/oversized body.
    /// </summary>
    BadResponse,

    /// <summary>Any other answer, including an unparseable success body.</summary>
    Failed,
}

/// <summary>
/// One worker answer: the extracted text plus whether the worker capped it. <see cref="TotalPages"/> and
/// <see cref="PagesRead"/> are null when the worker did not report (or reported an impossible) page count.
/// </summary>
public sealed record ImportWorkerPdfTextResult(ImportWorkerStatus Status, string Text, bool Truncated, int RetryAfterSeconds = 0, int? TotalPages = null, int? PagesRead = null);

/// <summary>
/// One entry the worker accepted from an archive, with its public kind and, when inlined, its text or its image
/// bytes. <see cref="Bytes"/> is set only for a magic-verified image entry under an <c>assets</c> segment; the API
/// never parses it, it only re-checks its size and digest.
/// </summary>
public sealed record ZipEntry(string Path, string Sha256, long Size, string Kind, string? Text, byte[]? Bytes = null);

/// <summary>One entry the worker's reader or classifier refused, with the exact reason code.</summary>
public sealed record ZipEntryRefusal(string Path, string Reason);

/// <summary>
/// The worker's whole <c>POST /v1/extract-zip</c> answer. When <see cref="Aborted"/> is true the
/// worker refused the archive as a whole and <see cref="Files"/> is empty; <see cref="AbortReason"/>
/// carries the reader's reason. The zip bytes are parsed only inside the worker's
/// <c>SafeZipReader</c> and never reach this process.
/// </summary>
public sealed record ZipBundle(
    IReadOnlyList<ZipEntry> Files,
    IReadOnlyList<ZipEntryRefusal> Dropped,
    int Entries,
    long Bytes,
    bool Aborted,
    string? AbortReason);

/// <summary>One <c>extract-zip</c> answer: the status plus the parsed bundle when the worker answered 200.</summary>
/// <param name="Status">The classified answer.</param>
/// <param name="Bundle">The validated bundle when <paramref name="Status"/> is <see cref="ImportWorkerStatus.Ok"/>.</param>
/// <param name="RetryAfterSeconds">The worker's Retry-After when <see cref="ImportWorkerStatus.Busy"/>.</param>
/// <param name="Violation">A fixed code naming the broken bound when <see cref="ImportWorkerStatus.BadResponse"/> (never content).</param>
/// <param name="ObservedEntries">How many file entries the bad answer carried (counts only, for the log line).</param>
public sealed record ImportWorkerZipResult(
    ImportWorkerStatus Status,
    ZipBundle? Bundle = null,
    int RetryAfterSeconds = 0,
    string? Violation = null,
    int ObservedEntries = 0);

/// <summary>
/// SPEC 16.5: the Inference API's only client for the <c>import-worker</c>. PDF bytes travel to the
/// worker's <c>POST /v1/pdf-text</c> and nowhere else; the worker is the only place they are parsed.
/// The named <see cref="System.Net.Http.HttpClient"/> is <c>"ImportWorker"</c> (120 s timeout), and the
/// shared bearer comes from <c>Import:Worker:Token</c> and is never logged, echoed or persisted.
/// </summary>
public sealed class ImportWorkerClient
{
    /// <summary>The named <see cref="System.Net.Http.HttpClient"/> this client dials.</summary>
    public const string HttpClientName = "ImportWorker";

    /// <summary>The one timeout: bytes in, text out.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>A bounded read of the worker's answer (its own text cap is 200 KiB).</summary>
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// A bounded read of an <c>extract-zip</c> answer. The worker may inline up to 24 MiB of text
    /// and JSON escaping can inflate it, so the cap is generous while still bounded.
    /// </summary>
    private const int MaxZipResponseBytes = 64 * 1024 * 1024;

    /// <summary>The reader's own bounds (<c>SafeZipLimits</c> defaults); the worker's answer may not exceed them.</summary>
    private const int MaxEntries = 2000;
    private const int MaxDepth = 12;
    private const int MaxPathLength = ArchivePathRules.MaxPathLength; // the worker refuses longer names too
    private const long MaxEntryBytes = 8L * 1024 * 1024;
    private const long MaxTotalTextChars = 64L * 1024 * 1024;

    private static readonly JsonSerializerOptions CaseInsensitiveJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string? _token;

    /// <summary>
    /// True when both the bearer (<c>Import:Worker:Token</c>) and the worker address (the named client's base
    /// address, itself built from <c>Import:Worker:BaseUrl</c>) are present. Routes answer 503 before
    /// reading anything when this is false.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_token) && _http.BaseAddress is not null;

    public ImportWorkerClient(HttpClient http, IConfiguration? configuration = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _token = configuration?["Import:Worker:Token"];
    }

    /// <summary>
    /// Sends <paramref name="pdf"/> to <c>/v1/pdf-text</c>. Never throws for a worker-side failure:
    /// an unreachable or timed-out worker is <see cref="ImportWorkerStatus.Unavailable"/>, an
    /// unexpected HTTP status is <see cref="ImportWorkerStatus.Failed"/>. Only the caller's own
    /// cancellation is re-thrown.
    /// </summary>
    public async Task<ImportWorkerPdfTextResult> ExtractPdfTextAsync(Stream pdf, CancellationToken ct)
    {
        if (pdf is null || !pdf.CanRead)
            return new ImportWorkerPdfTextResult(ImportWorkerStatus.Failed, string.Empty, false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            using var content = new StreamContent(pdf);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/pdf-text") { Content = content };
            if (!string.IsNullOrEmpty(_token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            return (int)response.StatusCode switch
            {
                200 => Parse(await ReadBoundedAsync(response.Content, timeout.Token) ?? string.Empty),
                503 => new ImportWorkerPdfTextResult(ImportWorkerStatus.Busy, string.Empty, false, RetryAfter(response)),
                413 => new ImportWorkerPdfTextResult(ImportWorkerStatus.TooLarge, string.Empty, false),
                415 => new ImportWorkerPdfTextResult(ImportWorkerStatus.NotPdf, string.Empty, false),
                422 => new ImportWorkerPdfTextResult(ImportWorkerStatus.Unreadable, string.Empty, false),
                _ => new ImportWorkerPdfTextResult(ImportWorkerStatus.Failed, string.Empty, false),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // HttpRequestException, a timeout, a relative URI with no base address, a malformed
            // response: all "the worker did not answer", which the route turns into a 503.
            return new ImportWorkerPdfTextResult(ImportWorkerStatus.Unavailable, string.Empty, false);
        }
    }

    /// <summary>
    /// Sends <paramref name="zip"/> to <c>/v1/extract-zip</c>. The archive is parsed only inside the
    /// worker and is never stored; every answer is the worker's own classification. Like the PDF
    /// call, an unreachable or timed-out worker is <see cref="ImportWorkerStatus.Unavailable"/> and
    /// only the caller's own cancellation is re-thrown.
    /// </summary>
    public async Task<ImportWorkerZipResult> ExtractZipAsync(Stream zip, CancellationToken ct)
    {
        if (zip is null || !zip.CanRead)
            return new ImportWorkerZipResult(ImportWorkerStatus.Failed);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            using var content = new StreamContent(zip);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/extract-zip") { Content = content };
            if (!string.IsNullOrEmpty(_token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            return (int)response.StatusCode switch
            {
                200 => ParseZip(await ReadBoundedAsync(response.Content, timeout.Token, MaxZipResponseBytes)),
                503 => new ImportWorkerZipResult(ImportWorkerStatus.Busy, RetryAfterSeconds: RetryAfter(response)),
                413 => new ImportWorkerZipResult(ImportWorkerStatus.TooLarge),
                415 => new ImportWorkerZipResult(ImportWorkerStatus.NotZip),
                _ => new ImportWorkerZipResult(ImportWorkerStatus.Failed),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ImportWorkerZipResult(ImportWorkerStatus.Unavailable);
        }
    }

    /// <summary>The worker's <c>Retry-After</c> in whole seconds (delta form), 5 when absent or unusable, capped at 60.</summary>
    private static int RetryAfter(HttpResponseMessage response)
    {
        double? seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds;
        return seconds is { } value && value >= 1 ? (int)Math.Min(value, 60) : 5;
    }

    private static ImportWorkerZipResult Bad(string violation, int entries = 0) =>
        new(ImportWorkerStatus.BadResponse, Violation: violation, ObservedEntries: entries);

    /// <summary>
    /// Parses and VALIDATES an <c>extract-zip</c> answer. The worker is a separate process, so its output is
    /// untrusted: every entry is held to the same path rules as <c>SafeZipReader</c> plus size, digest, kind
    /// and text bounds. Any violation fails the whole answer (<see cref="ImportWorkerStatus.BadResponse"/>);
    /// the violation is a fixed code, never content.
    /// </summary>
    private static ImportWorkerZipResult ParseZip(string? body)
    {
        if (string.IsNullOrEmpty(body))
            return Bad("body_missing_or_oversized");

        ZipResponseDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ZipResponseDto>(body, CaseInsensitiveJson);
        }
        catch (JsonException)
        {
            return Bad("body_not_json");
        }

        if (dto is null)
            return Bad("body_not_object");

        IReadOnlyList<ZipFileDto> rawFiles = dto.Files ?? [];
        IReadOnlyList<ZipDroppedDto> rawDropped = dto.Dropped ?? [];
        if (rawFiles.Count > MaxEntries || rawDropped.Count > MaxEntries)
            return Bad("too_many_entries", rawFiles.Count);

        var files = new List<ZipEntry>(rawFiles.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalText = 0;
        int inlinedAssets = 0;
        long inlinedAssetBytes = 0;
        int inlinedFonts = 0;
        long inlinedFontBytes = 0;
        foreach (ZipFileDto file in rawFiles)
        {
            if (file is null)
                return Bad("entry_null", rawFiles.Count);
            if (!IsSafeFilePath(file.Path, MaxDepth))
                return Bad("path", rawFiles.Count);

            // Defence in depth only: the worker is the NFC authority. The API runs with invariant globalization
            // (no ICU), so it does no normalisation and only rejects ordinal-ignore-case duplicates.
            if (!seen.Add(file.Path))
                return Bad("path_duplicate", rawFiles.Count);
            if (file.Size < 0 || file.Size > MaxEntryBytes)
                return Bad("size", rawFiles.Count);
            if (!IsSha256Hex(file.Sha256))
                return Bad("sha256", rawFiles.Count);
            if (file.Kind is not ("json" or "css" or "text" or "svg" or "markup" or "image" or "font"))
                return Bad("kind", rawFiles.Count);

            if (file.Text is not null)
            {
                if (file.Kind == "image")
                    return Bad("text_on_image", rawFiles.Count);

                // SVG and markup travel as text under their own caps; the worker inlines them only that way.
                long textCap = file.Kind switch
                {
                    "svg" => ImportLimits.SvgMaxBytes,
                    "markup" => ImportLimits.MarkupMaxBytes,
                    _ => MaxEntryBytes,
                };

                // UTF-8 decoding never yields more chars than bytes, so a longer text lies about its size.
                if (file.Text.Length > file.Size || file.Text.Length > textCap)
                    return Bad("text_length", rawFiles.Count);

                totalText += file.Text.Length;
                if (totalText > MaxTotalTextChars)
                    return Bad("text_total", rawFiles.Count);
            }

            // The text-only kinds never carry bytes: Base64 on an SVG/markup entry would be a widened magic check.
            if (file.Base64 is not null && file.Kind is "svg" or "markup")
                return Bad("asset_shape", rawFiles.Count);

            byte[]? bytes = null;
            if (file.Kind == "font")
            {
                // A font is bytes the answer claims are a font: shape, size, digest and the font caps are all re-checked
                // here. The codes are fixed; neither a path nor content enters the log.
                if (file.Base64 is null || !IsFontPath(file.Path))
                    return Bad("font_shape", rawFiles.Count);

                if (file.Base64.Length > (ArchiveFontLimits.MaxEntryBytes / 3 + 1) * 4)
                    return Bad("font_size", rawFiles.Count);

                try
                {
                    bytes = Convert.FromBase64String(file.Base64);
                }
                catch (FormatException)
                {
                    return Bad("font_shape", rawFiles.Count);
                }

                if (bytes.Length != file.Size)
                    return Bad("font_shape", rawFiles.Count);
                if (bytes.Length > ArchiveFontLimits.MaxEntryBytes)
                    return Bad("font_size", rawFiles.Count);
                if (!string.Equals(
                        Convert.ToHexString(SHA256.HashData(bytes)),
                        file.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Bad("font_digest", rawFiles.Count);
                }

                inlinedFonts++;
                inlinedFontBytes += bytes.Length;
                if (inlinedFonts > ArchiveFontLimits.MaxEntries || inlinedFontBytes > ArchiveFontLimits.MaxTotalBytes)
                    return Bad("font_limit", rawFiles.Count);
            }
            else if (file.Base64 is not null)
            {
                // The binary channel is narrower than the text one: an image, under an `assets` segment (or the exact
                // `.thumbnail` basename), whose bytes really are what the answer says.
                if (file.Kind != "image" || !IsAssetPath(file.Path))
                    return Bad("asset_shape", rawFiles.Count);

                // Base64 cannot be shorter than four thirds of the payload, so this refuses an over-cap entry
                // before allocating its bytes; whitespace padding is rejected as a malformed asset.
                if (file.Base64.Length > (ArchiveAssetLimits.MaxEntryBytes / 3 + 1) * 4)
                    return Bad("asset_size", rawFiles.Count);

                try
                {
                    bytes = Convert.FromBase64String(file.Base64);
                }
                catch (FormatException)
                {
                    return Bad("asset_shape", rawFiles.Count);
                }

                if (bytes.Length != file.Size)
                    return Bad("asset_shape", rawFiles.Count);
                if (bytes.Length > ArchiveAssetLimits.MaxEntryBytes)
                    return Bad("asset_size", rawFiles.Count);
                if (!string.Equals(
                        Convert.ToHexString(SHA256.HashData(bytes)),
                        file.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Bad("asset_digest", rawFiles.Count);
                }

                inlinedAssets++;
                inlinedAssetBytes += bytes.Length;
                if (inlinedAssets > ArchiveAssetLimits.MaxEntries || inlinedAssetBytes > ArchiveAssetLimits.MaxTotalBytes)
                    return Bad("asset_limit", rawFiles.Count);
            }

            files.Add(new ZipEntry(file.Path, file.Sha256!.ToLowerInvariant(), file.Size, file.Kind, file.Text, bytes));
        }

        var dropped = new List<ZipEntryRefusal>(rawDropped.Count);
        foreach (ZipDroppedDto drop in rawDropped)
        {
            // A refusal legitimately names the hostile path it refused, so only its shape is bounded.
            if (drop is null || !IsBoundedLabel(drop.Path, 1024, allowAny: true) || !IsBoundedLabel(drop.Reason, 64, allowAny: false))
                return Bad("dropped", rawFiles.Count);

            dropped.Add(new ZipEntryRefusal(drop.Path, drop.Reason!));
        }

        var bundle = new ZipBundle(
            files,
            dropped,
            files.Count,
            files.Sum(f => f.Size),
            dto.Aborted,
            dto.AbortReason is { Length: <= 64 } reason ? reason : null);

        return new ImportWorkerZipResult(ImportWorkerStatus.Ok, bundle);
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64)
            return false;

        foreach (char c in value)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// A short label: non-empty, bounded, no control, format/hidden or line/paragraph-separator runes and no
    /// unpaired surrogate; <paramref name="allowAny"/> false also requires A-Z and underscore. The worker is
    /// untrusted and <c>IMPORT_REJECTED</c> echoes these labels, so invisible runes are refused like controls.
    /// </summary>
    private static bool IsBoundedLabel(string? value, int max, bool allowAny)
    {
        if (string.IsNullOrEmpty(value) || value.Length > max)
            return false;

        foreach (Rune rune in value.EnumerateRunes())
        {
            int scalar = rune.Value;

            // C0, DEL and C1 controls.
            if (scalar < 0x20 || scalar is 0x7F or (>= 0x80 and <= 0x9F))
                return false;

            // A lone surrogate enumerates as U+FFFD; the rest are bidi, zero-width, tags and selectors.
            if (scalar == 0xFFFD || DesignText.IsHiddenRune(rune))
                return false;

            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                return false;

            if (!allowAny && !(scalar is >= 'A' and <= 'Z' || scalar == '_'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// True when any segment is named <c>assets</c> (case-insensitive) or the exact basename is <c>.thumbnail</c>
    /// (the export's preview WEBP): the places image bytes may be inlined from.
    /// </summary>
    private static bool IsAssetPath(string path)
    {
        if (string.Equals(Path.GetFileName(path), ".thumbnail", StringComparison.Ordinal))
            return true;

        foreach (string segment in path.Split('/'))
        {
            if (string.Equals(segment, "assets", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>True when any segment is named <c>fonts</c> or <c>assets</c> (case-insensitive): where font bytes may be inlined from.</summary>
    private static bool IsFontPath(string path)
    {
        foreach (string segment in path.Split('/'))
        {
            if (string.Equals(segment, "fonts", StringComparison.OrdinalIgnoreCase)
                || string.Equals(segment, "assets", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The shape <c>SafeZipReader</c> guarantees for an accepted entry: Unicode (no control/format/bidi characters), forward slashes only,
    /// no absolute path, no <c>.</c>/<c>..</c>/empty segment, no colon, no trailing dot or space, bounded depth.
    /// </summary>
    internal static bool IsSafeFilePath(string? path, int maxDepth)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxPathLength)
            return false;

        // The same character rules the worker applies (Unicode names allowed; control, format, bidi, zero-width,
        // unpaired surrogates, backslash, colon, traversal and reserved names refused).
        if (ArchivePathRules.Validate(path, maxDepth) is not null)
            return false;

        if (path.StartsWith('/'))
            return false;

        string[] segments = path.Split('/');
        if (segments.Length > maxDepth)
            return false;

        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment.Length > 255 || segment is "." or "..")
                return false;
            if (segment.EndsWith('.') || segment.EndsWith(' '))
                return false;
        }

        return true;
    }

    private sealed record ZipStatsDto(int Entries, long Bytes);

    private sealed record ZipFileDto(string Path, string? Sha256, long Size, string? Kind, string? Text, string? Base64 = null);

    private sealed record ZipDroppedDto(string Path, string? Reason);

    private sealed record ZipResponseDto(
        IReadOnlyList<ZipFileDto>? Files,
        IReadOnlyList<ZipDroppedDto>? Dropped,
        ZipStatsDto? Stats,
        bool Aborted,
        string? AbortReason);

    private static ImportWorkerPdfTextResult Parse(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            string text = root.TryGetProperty("text", out JsonElement textNode)
                && textNode.ValueKind == JsonValueKind.String
                ? textNode.GetString() ?? string.Empty
                : string.Empty;

            bool truncated = root.TryGetProperty("truncated", out JsonElement truncatedNode)
                && truncatedNode.ValueKind == JsonValueKind.True;

            // Both counts or neither; an impossible pair (negative, or more read than exist) is dropped, not trusted.
            int? totalPages = root.TryGetProperty("totalPages", out JsonElement totalNode) && totalNode.ValueKind == JsonValueKind.Number && totalNode.TryGetInt32(out int total) && total >= 0 ? total : null;
            int? pagesRead = root.TryGetProperty("pagesRead", out JsonElement readNode) && readNode.ValueKind == JsonValueKind.Number && readNode.TryGetInt32(out int pages) && pages >= 0 ? pages : null;
            if (totalPages is null || pagesRead is null || pagesRead > totalPages)
                totalPages = pagesRead = null;

            return new ImportWorkerPdfTextResult(ImportWorkerStatus.Ok, text, truncated, 0, totalPages, pagesRead);
        }
        catch (JsonException)
        {
            return new ImportWorkerPdfTextResult(ImportWorkerStatus.Failed, string.Empty, false);
        }
    }

    private static async Task<string?> ReadBoundedAsync(HttpContent content, CancellationToken ct, int maxBytes = MaxResponseBytes)
    {
        await using Stream stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int total = 0;

        while (total <= maxBytes)
        {
            int want = Math.Min(chunk.Length, maxBytes + 1 - total);
            int read = await stream.ReadAsync(chunk.AsMemory(0, want), ct);
            if (read <= 0)
                break;

            buffer.Write(chunk, 0, read);
            total += read;
        }

        return total > maxBytes ? null : System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
