using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Shared.Archives;

namespace ReelBolt.Inference.Api.Services.Archives;

/// <summary>What the import-worker said about a zip sent to <c>/v1/unpack-zip</c>.</summary>
public enum ArchiveUnpackStatus
{
    /// <summary>A validated manifest is available and the files can be read.</summary>
    Ok,

    /// <summary>The worker refused the archive as a whole (a validated abort reason, zero files).</summary>
    Aborted,

    /// <summary>413: the zip is over the worker's archive cap.</summary>
    TooLarge,

    /// <summary>415: the body is not a zip.</summary>
    NotZip,

    /// <summary>503: the worker refused rather than queued; see <see cref="ArchiveUnpackSession.RetryAfterSeconds"/>.</summary>
    Busy,

    /// <summary>The worker could not be reached, timed out, or answered an unexpected status.</summary>
    Unavailable,

    /// <summary>The worker's answer broke a bound the API enforces; see <see cref="ArchiveUnpackSession.Violation"/>.</summary>
    BadResponse,
}

/// <summary>One file the manifest promises: a safe relative path, its exact size and lowercase hex SHA-256.</summary>
public sealed record UnpackManifestEntry(string Path, long Size, string Sha256);

/// <summary>One skipped path for display. Untrusted text (bounded to 1024 chars): never use it as a path.</summary>
public sealed record UnpackSkippedSample(string Path, string Reason);

/// <summary>The validated manifest. <see cref="SkippedCounts"/> maps a fixed reason code (e.g. EXCLUDED_VCS, NESTED_ARCHIVE) to a count.</summary>
public sealed record UnpackManifest(
    IReadOnlyList<UnpackManifestEntry> Files,
    IReadOnlyDictionary<string, int> SkippedCounts,
    IReadOnlyList<UnpackSkippedSample> SkippedSamples,
    bool Aborted,
    string? AbortReason);

/// <summary>Thrown by <see cref="ArchiveUnpackSession.ReadFilesAsync"/> when the worker's byte stream breaks the manifest. The message is a fixed code.</summary>
public sealed class ArchiveUnpackViolationException : Exception
{
    public ArchiveUnpackViolationException(string violation)
        : base(violation) => Violation = violation;

    public string Violation { get; }
}

/// <summary>
/// One <c>/v1/unpack-zip</c> exchange. <see cref="Status"/> tells whether files can be read; when
/// <see cref="ArchiveUnpackStatus.Ok"/>, <see cref="ReadFilesAsync"/> streams them once, each verified against its
/// manifest digest before it is yielded. Dispose to release the connection.
/// </summary>
public sealed class ArchiveUnpackSession : IDisposable
{
    private readonly HttpResponseMessage? _response;
    private readonly Stream? _body;
    private readonly CancellationTokenSource? _timeout;
    private readonly TimeSpan _readBudget;
    private bool _consumed;

    internal ArchiveUnpackSession(ArchiveUnpackStatus status, int retryAfterSeconds = 0, string? violation = null)
    {
        Status = status;
        RetryAfterSeconds = retryAfterSeconds;
        Violation = violation;
    }

    internal ArchiveUnpackSession(
        ArchiveUnpackStatus status,
        UnpackManifest manifest,
        HttpResponseMessage? response,
        Stream? body,
        CancellationTokenSource? timeout,
        TimeSpan? readBudget = null)
    {
        _readBudget = readBudget ?? ArchiveUnpackClient.Timeout;
        Status = status;
        Manifest = manifest;
        _response = response;
        _body = body;
        _timeout = timeout;
    }

    public ArchiveUnpackStatus Status { get; private set; }

    /// <summary>The validated manifest when <see cref="Status"/> is Ok or Aborted; otherwise null.</summary>
    public UnpackManifest? Manifest { get; }

    /// <summary>The worker's Retry-After (whole seconds, 1..60) when Busy.</summary>
    public int RetryAfterSeconds { get; }

    /// <summary>A fixed code naming the broken bound when BadResponse (never content).</summary>
    public string? Violation { get; private set; }

    /// <summary>
    /// Streams every manifest file once, in manifest order. Each entry's bytes are read exactly, the SHA-256 is
    /// checked BEFORE the entry is yielded, and trailing bytes after the last file are refused. A break throws
    /// <see cref="ArchiveUnpackViolationException"/> and flips <see cref="Status"/> to BadResponse; the caller must
    /// discard everything it already took from this session. The yielded stream is the caller's to dispose.
    /// </summary>
    public async IAsyncEnumerable<(UnpackManifestEntry Entry, Stream Content)> ReadFilesAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (Status != ArchiveUnpackStatus.Ok || Manifest is null || _body is null)
            throw new InvalidOperationException("Session has no readable files.");
        if (_consumed)
            throw new InvalidOperationException("Session files can be read once.");
        _consumed = true;

        foreach (UnpackManifestEntry entry in Manifest.Files)
        {
            byte[] content = new byte[entry.Size];
            try
            {
                // The budget bounds the worker round trip only, never the caller's storage writes between two reads.
                using CancellationTokenSource budget = ReadBudget(ct);
                await _body.ReadExactlyAsync(content, budget.Token);
            }
            catch (EndOfStreamException)
            {
                throw Fail("body_short");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                throw Fail("body_read_failed");
            }

            string digest = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            if (!string.Equals(digest, entry.Sha256, StringComparison.Ordinal))
                throw Fail("digest_mismatch");

            yield return (entry, new MemoryStream(content, writable: false));
        }

        int extra;
        try
        {
            using CancellationTokenSource budget = ReadBudget(ct);
            extra = await _body.ReadAsync(new byte[1], budget.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            throw Fail("body_read_failed");
        }

        if (extra > 0)
            throw Fail("body_trailing");
    }

    private CancellationTokenSource ReadBudget(CancellationToken ct)
    {
        CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_readBudget);
        return budget;
    }

    public void Dispose()
    {
        _body?.Dispose();
        _response?.Dispose();
        _timeout?.Dispose();
    }

    private ArchiveUnpackViolationException Fail(string violation)
    {
        Status = ArchiveUnpackStatus.BadResponse;
        Violation = violation;
        return new ArchiveUnpackViolationException(violation);
    }
}

/// <summary>
/// The API's client for the import-worker's <c>POST /v1/unpack-zip</c>. The worker is a separate process and its
/// answer is UNTRUSTED: the framing, the manifest and every byte are validated here. Reuses the <c>ImportWorker</c>
/// named HttpClient (base URL) and the <c>Import:Worker:Token</c> bearer. Nothing here logs a path or content.
/// </summary>
public sealed class ArchiveUnpackClient
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>How long one worker round trip (the opening exchange, or one body read) may take; storage writes never count.</summary>
    public TimeSpan RoundTripBudget { get; set; } = Timeout;

    private static readonly Regex CodePattern = new(@"^[A-Z][A-Z0-9_]{0,39}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HexPattern = new(@"^[0-9a-f]{64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int MaxCountKeys = 64;
    private const int MaxSamplePathLength = ArchivePathRules.MaxPathLength;

    private readonly HttpClient _http;
    private readonly string? _token;

    public ArchiveUnpackClient(HttpClient http, IConfiguration? configuration = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _token = configuration?["Import:Worker:Token"];
    }

    /// <summary>True when the bearer and the worker address are both present.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_token) && _http.BaseAddress is not null;

    /// <summary>
    /// Sends <paramref name="zip"/> (exactly <paramref name="length"/> bytes) to <c>/v1/unpack-zip</c> and validates the
    /// framing and manifest. Never throws for a worker-side failure; only the caller's own cancellation is re-thrown.
    /// The returned session must be disposed.
    /// </summary>
    public async Task<ArchiveUnpackSession> OpenAsync(Stream zip, long length, CancellationToken ct)
    {
        if (zip is null || !zip.CanRead || length < 0)
            return new ArchiveUnpackSession(ArchiveUnpackStatus.Unavailable);

        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RoundTripBudget);
        HttpResponseMessage? response = null;
        bool handedOver = false;

        try
        {
            using StreamContent content = new(zip);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            content.Headers.ContentLength = length;

            using HttpRequestMessage request = new(HttpMethod.Post, "/v1/unpack-zip") { Content = content };
            if (!string.IsNullOrEmpty(_token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);

            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            switch ((int)response.StatusCode)
            {
                case 200:
                    break;
                case 503:
                    return new ArchiveUnpackSession(ArchiveUnpackStatus.Busy, RetryAfter(response));
                case 413:
                    return new ArchiveUnpackSession(ArchiveUnpackStatus.TooLarge);
                case 415:
                    return new ArchiveUnpackSession(ArchiveUnpackStatus.NotZip);
                default:
                    return new ArchiveUnpackSession(ArchiveUnpackStatus.Unavailable);
            }

            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, UnpackLimits.ContentType, StringComparison.OrdinalIgnoreCase))
                return Bad("content_type");

            Stream body = await response.Content.ReadAsStreamAsync(timeout.Token);
            (UnpackManifest? manifest, string? violation) = await ReadManifestAsync(body, response.Content.Headers.ContentLength, timeout.Token);
            if (manifest is null)
                return Bad(violation ?? "manifest_invalid");

            ArchiveUnpackSession session = new(
                manifest.Aborted ? ArchiveUnpackStatus.Aborted : ArchiveUnpackStatus.Ok,
                manifest,
                response,
                body,
                timeout,
                RoundTripBudget);
            handedOver = true;
            // The opening round trip is over; each later body read gets its own budget (see ReadFilesAsync).
            timeout.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
            return session;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ArchiveUnpackSession(ArchiveUnpackStatus.Unavailable);
        }
        finally
        {
            if (!handedOver)
            {
                response?.Dispose();
                timeout.Dispose();
            }
        }

        static ArchiveUnpackSession Bad(string violation) => new(ArchiveUnpackStatus.BadResponse, violation: violation);
    }

    private static int RetryAfter(HttpResponseMessage response)
    {
        double? seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds;
        return seconds is { } value && value >= 1 ? (int)Math.Min(value, 60) : 5;
    }

    private static async Task<(UnpackManifest? Manifest, string? Violation)> ReadManifestAsync(
        Stream body, long? declaredLength, CancellationToken ct)
    {
        byte[] head = new byte[8];
        try
        {
            await body.ReadExactlyAsync(head, ct);
        }
        catch (EndOfStreamException)
        {
            return (null, "frame_short");
        }

        if (!head.AsSpan(0, 4).SequenceEqual(UnpackLimits.Magic))
            return (null, "bad_magic");

        uint manifestLength = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(4, 4));
        if (manifestLength == 0 || manifestLength > UnpackLimits.MaxManifestBytes)
            return (null, "manifest_length");

        byte[] manifestBytes = new byte[manifestLength];
        try
        {
            await body.ReadExactlyAsync(manifestBytes, ct);
        }
        catch (EndOfStreamException)
        {
            return (null, "manifest_short");
        }

        (UnpackManifest? manifest, string? violation) = ParseManifest(manifestBytes);
        if (manifest is null)
            return (null, violation);

        if (declaredLength is { } declared)
        {
            long expected = 8L + manifestLength + manifest.Files.Sum(file => file.Size);
            if (declared != expected)
                return (null, "length_mismatch");
        }

        return (manifest, null);
    }

    /// <summary>Validates the manifest JSON against every bound; exposed for tests of crafted worker answers.</summary>
    internal static (UnpackManifest? Manifest, string? Violation) ParseManifest(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return (null, "manifest_not_json");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, "manifest_not_object");

            if (!root.TryGetProperty("aborted", out JsonElement abortedNode)
                || abortedNode.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return (null, "aborted_invalid");
            }

            bool aborted = abortedNode.ValueKind == JsonValueKind.True;

            string? abortReason = null;
            if (root.TryGetProperty("abortReason", out JsonElement reasonNode) && reasonNode.ValueKind != JsonValueKind.Null)
            {
                if (reasonNode.ValueKind != JsonValueKind.String || !CodePattern.IsMatch(reasonNode.GetString() ?? string.Empty))
                    return (null, "abort_reason_invalid");
                abortReason = reasonNode.GetString();
            }

            if (aborted != (abortReason is not null))
                return (null, "abort_reason_mismatch");

            if (!root.TryGetProperty("files", out JsonElement filesNode) || filesNode.ValueKind != JsonValueKind.Array)
                return (null, "files_invalid");

            int count = filesNode.GetArrayLength();
            if (count > UnpackLimits.MaxIncludedEntries)
                return (null, "too_many_files");
            if (aborted && count > 0)
                return (null, "aborted_with_files");

            List<UnpackManifestEntry> files = new(count);
            HashSet<string> fileKeys = new(StringComparer.Ordinal);
            HashSet<string> folderKeys = new(StringComparer.Ordinal);
            long total = 0;

            foreach (JsonElement node in filesNode.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object
                    || !node.TryGetProperty("path", out JsonElement pathNode) || pathNode.ValueKind != JsonValueKind.String
                    || !node.TryGetProperty("size", out JsonElement sizeNode) || sizeNode.ValueKind != JsonValueKind.Number
                    || !node.TryGetProperty("sha256", out JsonElement shaNode) || shaNode.ValueKind != JsonValueKind.String)
                {
                    return (null, "file_shape");
                }

                string path = pathNode.GetString() ?? string.Empty;
                if (!ImportWorkerClient.IsSafeFilePath(path, UnpackLimits.MaxDepth))
                    return (null, "file_path");

                if (!sizeNode.TryGetInt64(out long size) || size < 0 || size > UnpackLimits.MaxEntryBytes)
                    return (null, "file_size");

                total += size;
                if (total > UnpackLimits.MaxTotalBytes)
                    return (null, "total_size");

                string sha = shaNode.GetString() ?? string.Empty;
                if (!HexPattern.IsMatch(sha))
                    return (null, "file_digest");

                if (!ArchivePathRules.TryGetDedupeKey(path, out string key))
                    return (null, "file_path");

                if (!fileKeys.Add(key))
                    return (null, "file_duplicate");

                int slash = key.IndexOf('/');
                while (slash >= 0)
                {
                    folderKeys.Add(key[..slash]);
                    slash = key.IndexOf('/', slash + 1);
                }

                files.Add(new UnpackManifestEntry(path, size, sha));
            }

            // A path that is also a folder prefix of another cannot exist on a filesystem.
            foreach (string key in fileKeys)
            {
                if (folderKeys.Contains(key))
                    return (null, "file_folder_clash");
            }

            if (!root.TryGetProperty("skipped", out JsonElement skippedNode) || skippedNode.ValueKind != JsonValueKind.Object)
                return (null, "skipped_invalid");

            Dictionary<string, int> counts = new(StringComparer.Ordinal);
            if (skippedNode.TryGetProperty("counts", out JsonElement countsNode))
            {
                if (countsNode.ValueKind != JsonValueKind.Object)
                    return (null, "counts_invalid");

                foreach (JsonProperty property in countsNode.EnumerateObject())
                {
                    if (counts.Count >= MaxCountKeys
                        || !CodePattern.IsMatch(property.Name)
                        || property.Value.ValueKind != JsonValueKind.Number
                        || !property.Value.TryGetInt32(out int n)
                        || n < 0)
                    {
                        return (null, "counts_invalid");
                    }

                    counts[property.Name] = n;
                }
            }

            List<UnpackSkippedSample> samples = new();
            if (skippedNode.TryGetProperty("samples", out JsonElement samplesNode))
            {
                if (samplesNode.ValueKind != JsonValueKind.Array || samplesNode.GetArrayLength() > UnpackLimits.MaxSkippedSamples)
                    return (null, "samples_invalid");

                foreach (JsonElement sample in samplesNode.EnumerateArray())
                {
                    if (sample.ValueKind != JsonValueKind.Object
                        || !sample.TryGetProperty("path", out JsonElement samplePath) || samplePath.ValueKind != JsonValueKind.String
                        || !sample.TryGetProperty("reason", out JsonElement sampleReason) || sampleReason.ValueKind != JsonValueKind.String)
                    {
                        return (null, "samples_invalid");
                    }

                    string text = samplePath.GetString() ?? string.Empty;
                    string reason = sampleReason.GetString() ?? string.Empty;
                    if (text.Length > MaxSamplePathLength || !CodePattern.IsMatch(reason))
                        return (null, "samples_invalid");

                    samples.Add(new UnpackSkippedSample(text, reason));
                }
            }

            return (new UnpackManifest(files, counts, samples, aborted, abortReason), null);
        }
    }
}

/// <summary>DI registration for <see cref="ArchiveUnpackClient"/>; call next to the <c>ImportWorkerClient</c> registration.</summary>
public static class ArchiveUnpackClientRegistration
{
    public static IServiceCollection AddArchiveUnpackClient(
        this IServiceCollection services)
    {
        services.AddScoped(sp => new ArchiveUnpackClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(ImportWorkerClient.HttpClientName),
            sp.GetRequiredService<IConfiguration>()));
        return services;
    }
}
