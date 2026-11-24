using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>What one finished fal queue job answered: its output document and the vendor's request id.</summary>
internal sealed record FalQueueResult(JsonElement Output, string RequestId);

/// <summary>The classified outcome of a no-cost probe, before a caller wraps it in its own result type.</summary>
/// <param name="IsOk">The answer proves the endpoint is routed and the key was accepted.</param>
/// <param name="Reason">What was seen, already bounded and key-scrubbed; null when ok.</param>
/// <param name="AuthRejected">fal.ai rejected the credential (401/403, or a body naming an authorization problem).</param>
internal sealed record FalProbeOutcome(bool IsOk, string? Reason, bool AuthRejected = false);

/// <summary>
/// One result fetch, classified rather than thrown: the output document when the vendor answered 2xx,
/// otherwise the status and body that refused it.
/// </summary>
/// <param name="Output">The endpoint's own output document, or null when the fetch was not a 2xx.</param>
/// <param name="StatusCode">The HTTP status the vendor answered.</param>
/// <param name="Body">The raw body, for a caller that must decide whether a refusal was billed.</param>
internal sealed record FalResultFetch(JsonElement? Output, int StatusCode, string Body);

/// <summary>
/// What one submit answered: the vendor's request id and the two tracking URLs it returned.
/// </summary>
/// <remarks>
/// The URLs travel with the id, because fal scopes them to the <b>app</b> (<c>{owner}/{app}</c>) rather
/// than to the endpoint id: for <c>fal-ai/vidu/q4/image-to-video</c> the live status route is
/// <c>https://queue.fal.run/fal-ai/vidu/requests/{id}/status</c>, while the endpoint-shaped URL the
/// vendor's own documentation shows (<c>.../fal-ai/vidu/q4/image-to-video/requests/{id}/status</c>)
/// answers HTTP 405 (measured without a credential, 2026-10-16). A caller that must resume a job from a
/// persisted id alone — the <c>VideoGenerate</c> step's crash-resume path — therefore cannot rebuild
/// them, so the submission carries them.
/// </remarks>
internal sealed record FalSubmission(string RequestId, string StatusUrl, string ResponseUrl);

/// <summary>
/// One status read: the queue status word, and the vendor's own error sentence when the job failed.
/// </summary>
/// <param name="Status">The queue status: <c>IN_QUEUE</c>, <c>IN_PROGRESS</c> or <c>COMPLETED</c>.</param>
/// <param name="Error">
/// <c>error</c> from the status document, which the vendor documents as "a human-readable error message,
/// present only if the request failed (only when COMPLETED)" — i.e. a COMPLETED job that FAILED. Null on
/// every other answer, and null when the field is absent or not a string.
/// </param>
internal sealed record FalQueueStatus(string Status, string? Error);

/// <summary>
/// The fal.ai queue contract, once, for every client that talks to <c>queue.fal.run</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a class and not a base class.</b> <see cref="FalAiAnalysisClient"/> (segmentation and
/// depth) and <see cref="FalAiImageClient"/> (generation, editing, inpainting, transforms) share the
/// whole wire contract — the <c>Authorization: Key</c> header, the submit, the status vocabulary, the
/// poll loop, the result fetch, the SSRF-guarded download and the probe classification — and differ only
/// in the request bodies and the output field names. Composition keeps that contract in exactly one
/// place so the two cannot drift apart on a queue detail (the status vocabulary in particular, where an
/// unrecognised value must fail loudly rather than be polled forever).
/// </para>
/// <para>
/// <b>The contract</b> (fal.ai/docs/documentation/model-apis/inference/queue.md, read 2026-10-10):
/// <c>POST https://queue.fal.run/{model-id}</c> with <c>Authorization: Key &lt;FAL_KEY&gt;</c> answers a
/// <c>QueueStatus</c> carrying <c>request_id</c>, <c>status_url</c> and <c>response_url</c>; the status
/// URL moves <c>IN_QUEUE</c> to <c>IN_PROGRESS</c> to <c>COMPLETED</c>; the response URL answers in the
/// endpoint's own output schema. Billing is per successful output, never for queue time or a server
/// error.
/// </para>
/// </remarks>
internal sealed class FalQueueClient
{
    /// <summary>The public queue host. A provider row's endpoint overrides it.</summary>
    public const string DefaultEndpoint = "https://queue.fal.run";

    /// <summary>The default poll budget.</summary>
    public static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The wait between polls, doubling up to a cap.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ProbeFollowTimeout = TimeSpan.FromSeconds(20);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly HttpClient _downloadClient;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly IReadOnlyDictionary<string, string>? _submitHeaders;

    /// <param name="submitHeaders">
    /// Extra headers added to the SUBMIT request only, for an endpoint whose contract carries one. The
    /// video-segmentation arm sends <c>X-Fal-Store-IO: 0</c> (no vendor-side retention of the uploaded
    /// clip); image/analysis/video callers pass null and are unchanged.
    /// </param>
    public FalQueueClient(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient,
        TimeSpan? pollInterval,
        TimeSpan? pollTimeout,
        Func<TimeSpan, CancellationToken, Task>? delay,
        IReadOnlyDictionary<string, string>? submitHeaders = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _downloadClient = downloadClient ?? httpClient;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _pollTimeout = pollTimeout ?? DefaultPollTimeout;
        _submitHeaders = submitHeaders;
        _delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
    }

    /// <summary>Submits a job, polls it to <c>COMPLETED</c> and fetches its output document.</summary>
    /// <param name="maxResultBytes">
    /// The ceiling on the result document's body, or null for the framework's own. The video-segmentation
    /// arm is the only caller that names one (<see cref="FalAiVideoSegmentationClient.MaxResultBytes"/>):
    /// its document is the one whose size the vendor, not the request, chooses, and one entry per frame per
    /// object cannot be bounded by any other means until the body is read.
    /// </param>
    /// <remarks>
    /// The one entry point for an endpoint whose work finishes inside
    /// <see cref="DefaultPollTimeout"/> and whose caller has nowhere to persist a handle — the image and
    /// analysis arms. A caller that must survive a restart uses the three halves directly
    /// (<see cref="SubmitAsync"/>, <see cref="ReadStatusAsync"/>, <see cref="FetchResultAsync"/>), which
    /// is what the video arm does: it keeps the vendor's request id in its own ledger row and polls it
    /// after a crash instead of submitting the same work — and paying for it — a second time.
    /// </remarks>
    public async Task<FalQueueResult> SubmitAndPollAsync(
        string model, Dictionary<string, object?> body, CancellationToken ct, int? maxResultBytes = null)
    {
        FalSubmission submission = await SubmitAsync(model, body, ct);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + _pollTimeout;
        TimeSpan interval = _pollInterval;

        while (true)
        {
            FalQueueStatus status = await ReadStatusAsync(submission.StatusUrl, model, ct);
            if (status.Status == "COMPLETED") break;

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    "fal.ai did not finish '" + model + "' (" +
                    (string.IsNullOrEmpty(submission.RequestId) ? "no id" : submission.RequestId) + ") within " +
                    _pollTimeout + ". fal does not bill queue time or a job that never completes, so retrying " +
                    "is safe.");
            }

            await _delay(interval, ct);
            interval = interval + interval > MaxPollInterval ? MaxPollInterval : interval + interval;
        }

        JsonElement output = await FetchResultAsync(submission.ResponseUrl, model, ct, maxResultBytes);
        return new FalQueueResult(output, submission.RequestId);
    }

    /// <summary>
    /// The submit half of the contract, on its own: one <c>POST /{model}</c>, no polling.
    /// </summary>
    /// <remarks>
    /// Split out for the video arm, whose jobs outlive any in-process poll budget: the caller persists
    /// <see cref="FalSubmission.RequestId"/> (and the two URLs, which cannot be rebuilt — see
    /// <see cref="FalSubmission"/>) and resumes with <see cref="ReadStatusAsync"/> instead of
    /// submitting the same work again. A submission is what the vendor bills, so re-submitting on a
    /// timeout is a second charge for the same clip.
    /// </remarks>
    public async Task<FalSubmission> SubmitAsync(string model, Dictionary<string, object?> body, CancellationToken ct)
    {
        string text;
        using (HttpRequestMessage submit = BuildRequest(model, body))
        using (HttpResponseMessage submitted = await _httpClient.SendAsync(submit, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            // S-52: bounded, and headers-first so HttpClient does not buffer an unbounded body first.
            text = submitted.IsSuccessStatusCode
                ? await BoundedBody.ReadDocumentAsync(submitted.Content, ct)
                : await BoundedBody.ReadErrorAsync(submitted.Content, ct);
            if (!submitted.IsSuccessStatusCode)
            {
                // HttpRequestException, NOT InvalidOperationException, and carrying the status: a submit is
                // the one call that decides whether money was committed, and the VideoGenerate executor's
                // own contract reads exactly this — a 4xx (other than 408) is a DEFINITE rejection and its
                // reservation is released, while a 5xx or 408 may have been accepted and keeps it charged
                // (VideoGenerateStepExecutor's IsDefiniteRejection). The message is unchanged.
                throw new HttpRequestException(
                    "fal.ai returned HTTP " + (int)submitted.StatusCode + " submitting '" + model + "': " +
                    ImagePayload.Bounded(text),
                    inner: null,
                    statusCode: submitted.StatusCode);
            }
        }

        string requestId;
        string statusUrl;
        string responseUrl;
        using (JsonDocument document = JsonDocument.Parse(text))
        {
            JsonElement root = document.RootElement;
            requestId = ReadString(root, "request_id") ?? string.Empty;
            statusUrl = ReadString(root, "status_url") ?? string.Empty;
            responseUrl = ReadString(root, "response_url") ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(statusUrl) || string.IsNullOrWhiteSpace(responseUrl))
        {
            // The URLs are what the vendor documents as the ones to use, and they are app-scoped rather
            // than endpoint-shaped, so a missing one is a protocol change worth failing loudly on rather
            // than silently substituting a hand-built URL.
            throw new InvalidOperationException(
                "fal.ai accepted the submission for '" + model + "' but returned no status_url/response_url, " +
                "so the result cannot be retrieved. Request id: " +
                (string.IsNullOrEmpty(requestId) ? "(none)" : requestId) + ".");
        }

        // S-50/S-57: the tracking URLs are vendor-supplied, so they are pinned to the configured queue
        // origin before anything is persisted or polled. Failing here (rather than only at use) keeps a
        // hostile pair out of the video arm's persisted handle; ReadStatusAsync and TryFetchResultAsync
        // re-check, because a persisted handle is read back from the database.
        RequireQueueOrigin(statusUrl);
        RequireQueueOrigin(responseUrl);

        return new FalSubmission(requestId, statusUrl, responseUrl);
    }

    /// <summary>
    /// S-50/S-57: refuses any vendor-supplied tracking URL that is not on the configured queue origin.
    /// </summary>
    /// <remarks>
    /// fal's status and response URLs are app-scoped and cannot be rebuilt from the request id (see
    /// <see cref="FalSubmission"/>), so they are used as returned — but only when they are absolute, carry
    /// no credentials, and share the scheme, host and port of the base address the submit went to (the
    /// provider row's endpoint, or <see cref="DefaultEndpoint"/>), AND pass
    /// <see cref="GeneratedClipUrlGuard.EnsureAllowed"/> (https, public host, no private literal). A BYO
    /// row at an attacker host can therefore only ever make the engine GET its own host, never an internal
    /// address or a third party. The refusal names no URL and no body.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The URL is not on the configured queue origin.</exception>
    public void RequireQueueOrigin(string url)
    {
        Uri origin = new(BaseAddress());
        bool sameOrigin = Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && string.IsNullOrEmpty(uri.UserInfo)
            && string.Equals(uri.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase)
            && uri.Port == origin.Port;
        if (sameOrigin)
        {
            try
            {
                GeneratedClipUrlGuard.EnsureAllowed(url);
                return;
            }
            catch (InvalidOperationException)
            {
                // fall through to the one fixed refusal
            }
        }

        throw new InvalidOperationException(
            "fal.ai returned a job tracking URL outside the configured queue endpoint, so it was not followed. " +
            "The job may still run and bill.");
    }

    /// <summary>
    /// A vendor sentence made safe for a tenant-visible error: control characters flattened, whitespace
    /// collapsed and capped at <paramref name="max"/> characters. Never pass a raw response body.
    /// </summary>
    public static string CleanVendorText(string? text, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        StringBuilder cleaned = new(Math.Min(text.Length, max + 1));
        bool space = false;
        foreach (char c in text)
        {
            if (cleaned.Length > max) break;
            if (char.IsControl(c) || char.IsWhiteSpace(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format)
            {
                space = cleaned.Length > 0;
                continue;
            }

            if (space) cleaned.Append(' ');
            space = false;
            cleaned.Append(c);
        }

        return cleaned.Length <= max ? cleaned.ToString() : cleaned.ToString(0, max) + "…";
    }

    /// <summary>
    /// The vendor's own validation sentence out of a refusal body (<c>detail</c> as a string, the first
    /// <c>detail[].msg</c>, or <c>error</c>/<c>message</c>), cleaned and capped; empty when there is none.
    /// The body itself is never returned.
    /// </summary>
    public static string VendorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return string.Empty;
            if (root.TryGetProperty("detail", out JsonElement detail))
            {
                if (detail.ValueKind == JsonValueKind.String) return CleanVendorText(detail.GetString());
                if (detail.ValueKind == JsonValueKind.Array && detail.GetArrayLength() > 0)
                {
                    return CleanVendorText(ReadString(detail[0], "msg"));
                }
            }

            return CleanVendorText(ReadString(root, "error") ?? ReadString(root, "message"));
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Most validation entries a refusal summary names; fal sends one per bad field.</summary>
    internal const int MaxValidationEntries = 3;

    /// <summary>
    /// A diagnosable, log- and tenant-safe summary of a fal validation refusal: for each of the first
    /// <see cref="MaxValidationEntries"/> <c>detail[]</c> entries, the field path (<c>loc</c>, joined
    /// with dots), the error <c>type</c> and the cleaned <c>msg</c>, e.g.
    /// <c>body.video_url file_download_error (Failed to download the file. Please check if the URL is accessible)</c>.
    /// Falls back to <see cref="VendorMessage"/> for a body that is not a <c>detail[]</c> list.
    /// </summary>
    /// <remarks>
    /// Deliberately never reads an entry's <c>input</c>, <c>url</c> or <c>ctx</c>: fal echoes the value it
    /// refused there, and for a source field that value is our presigned URL (a bearer capability to
    /// the customer's clip). Any URL that still appears inside a sentence is replaced with
    /// <c>&lt;url&gt;</c> for the same reason.
    /// </remarks>
    public static string VendorValidationDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("detail", out JsonElement detail)
                && detail.ValueKind == JsonValueKind.Array
                && detail.GetArrayLength() > 0)
            {
                List<string> parts = [];
                foreach (JsonElement entry in detail.EnumerateArray().Take(MaxValidationEntries))
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    string field = string.Empty;
                    if (entry.TryGetProperty("loc", out JsonElement loc) && loc.ValueKind == JsonValueKind.Array)
                    {
                        // Strings and numbers only: any other element kind is skipped, never read
                        // (GetString on a bool or object would throw and hide the HTTP status).
                        field = string.Join('.', loc.EnumerateArray()
                            .Select(p => p.ValueKind switch
                            {
                                JsonValueKind.String => p.GetString() ?? string.Empty,
                                JsonValueKind.Number => p.GetRawText(),
                                _ => string.Empty
                            })
                            .Where(p => p.Length > 0));
                    }

                    string type = CleanVendorText(StripUrls(ReadString(entry, "type")), 60);
                    string msg = CleanVendorText(StripUrls(ReadString(entry, "msg")), 160);
                    string head = string.Join(' ', new[] { CleanVendorText(StripUrls(field), 80), type }.Where(s => s.Length > 0));
                    string part = msg.Length > 0 ? (head.Length > 0 ? head + " (" + msg + ")" : msg) : head;
                    if (part.Length > 0) parts.Add(part);
                }

                if (detail.GetArrayLength() > MaxValidationEntries)
                {
                    parts.Add("+" + (detail.GetArrayLength() - MaxValidationEntries) + " more");
                }

                if (parts.Count > 0) return string.Join("; ", parts);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return string.Empty;
        }

        return StripUrls(VendorMessage(body));
    }

    /// <summary>
    /// Masks every <c>scheme://</c> URL, and any other token carrying a SigV4 query parameter
    /// (<c>X-Amz-</c>, e.g. a scheme-less <c>host/path?X-Amz-Signature=...</c> echo), with <c>&lt;url&gt;</c>.
    /// </summary>
    internal static string StripUrls(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : System.Text.RegularExpressions.Regex.Replace(text, @"(?i)\b(?:https?|s3|gs|ftp):(?:\\*/){2}\S+|\S*X-Amz-\S*", "<url>");

    /// <summary>
    /// The poll half of the contract, on its own: one authenticated <c>GET</c> of a status URL.
    /// </summary>
    /// <remarks>
    /// The status vocabulary is enforced in exactly one place — here — and an unrecognised value fails
    /// loudly instead of being read as "still working", which would burn the caller's poll budget and
    /// then report a timeout, hiding the one fact that explains it. That matters more for the video arm
    /// than for images: a caller that cannot resume pays for a second clip.
    /// </remarks>
    /// <exception cref="NotSupportedException">The status is not <c>IN_QUEUE</c>, <c>IN_PROGRESS</c> or <c>COMPLETED</c>.</exception>
    public async Task<FalQueueStatus> ReadStatusAsync(string statusUrl, string model, CancellationToken ct)
    {
        RequireQueueOrigin(statusUrl);
        string text;
        using (HttpRequestMessage poll = new(HttpMethod.Get, statusUrl))
        {
            poll.Headers.TryAddWithoutValidation("Authorization", "Key " + _apiKey);
            poll.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage polled = await _httpClient.SendAsync(poll, HttpCompletionOption.ResponseHeadersRead, ct);
            text = polled.IsSuccessStatusCode
                ? await BoundedBody.ReadDocumentAsync(polled.Content, ct)
                : await BoundedBody.ReadErrorAsync(polled.Content, ct);
            if (!polled.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "fal.ai returned HTTP " + (int)polled.StatusCode + " while polling '" + model + "'.");
            }
        }

        string status;
        string? error;
        using (JsonDocument document = JsonDocument.Parse(text))
        {
            status = ReadString(document.RootElement, "status") ?? string.Empty;
            error = ReadString(document.RootElement, "error");
        }

        if (status is not ("IN_QUEUE" or "IN_PROGRESS" or "COMPLETED"))
        {
            throw new NotSupportedException(
                "fal.ai returned an unrecognised queue status '" + CleanVendorText(status, 40) + "' for '" + model +
                "'. Treating an unknown state as 'still working' would " +
                "burn the poll budget and then report a timeout, hiding the one fact that explains it.");
        }

        return new FalQueueStatus(status, error);
    }

    /// <summary>
    /// The fetch half of the contract, on its own: one authenticated <c>GET</c> of a result URL,
    /// answering the endpoint's own output document.
    /// </summary>
    public async Task<JsonElement> FetchResultAsync(
        string responseUrl, string model, CancellationToken ct, int? maxResultBytes = null)
    {
        FalResultFetch fetch = await TryFetchResultAsync(responseUrl, model, ct, maxResultBytes);
        if (fetch.Output is not { } output)
        {
            // The vendor's own field + type + sentence (never its echo of our input, which carries the
            // presigned source URL): without it a 422 "file_download_error" reads like any other 422.
            string detail = VendorValidationDetail(fetch.Body);
            throw new InvalidOperationException(
                "fal.ai returned HTTP " + fetch.StatusCode + " fetching the result of '" + model + "'" +
                (detail.Length > 0 ? ": " + detail + "." : "."));
        }

        return output;
    }

    /// <summary>
    /// The same fetch, unclassified-throwing: it hands back the status and body so a caller whose billing
    /// decision depends on WHY the vendor refused (the video arm: a validation refusal costs nothing and
    /// fails the take, a 5xx may have been billed and keeps it charged) can tell the two apart.
    /// </summary>
    public async Task<FalResultFetch> TryFetchResultAsync(
        string responseUrl, string model, CancellationToken ct, int? maxResultBytes = null)
    {
        RequireQueueOrigin(responseUrl);
        using HttpRequestMessage result = new(HttpMethod.Get, responseUrl);
        result.Headers.TryAddWithoutValidation("Authorization", "Key " + _apiKey);
        result.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using HttpResponseMessage fetched = await _httpClient.SendAsync(result, HttpCompletionOption.ResponseHeadersRead, ct);
        string bodyText = maxResultBytes is { } ceiling
            ? await ReadBoundedStringAsync(fetched.Content, ceiling, model, ct)
            : fetched.IsSuccessStatusCode
                ? await BoundedBody.ReadDocumentAsync(fetched.Content, ct)
                : await BoundedBody.ReadErrorAsync(fetched.Content, ct);
        if (!fetched.IsSuccessStatusCode)
        {
            return new FalResultFetch(null, (int)fetched.StatusCode, bodyText);
        }

        using JsonDocument output = JsonDocument.Parse(bodyText);
        return new FalResultFetch(output.RootElement.Clone(), (int)fetched.StatusCode, bodyText);
    }

    /// <summary>
    /// Reads a response body through a running byte counter, refusing the moment it passes
    /// <paramref name="limit"/> — the vendor chooses this document's size, and
    /// <see cref="HttpContent.ReadAsStringAsync"/> buffers it whole first.
    /// </summary>
    /// <remarks>
    /// The declaration is checked first when the vendor states one, so an oversized body is refused
    /// before any of it is read; the counter then covers a chunked or lying declaration. The bytes are
    /// decoded as UTF-8 at the end because the document is JSON and every caller parses it as a string.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The body is larger than <paramref name="limit"/>.</exception>
    private static async Task<string> ReadBoundedStringAsync(
        HttpContent content, int limit, string model, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > 0
            && content.Headers.ContentLength.Value > limit)
        {
            throw new InvalidOperationException(
                "fal.ai declared a " + content.Headers.ContentLength.Value + "-byte result body for '" + model +
                "', over the " + limit + "-byte ceiling this client accepts. The answer is refused unread.");
        }

        await using Stream body = await content.ReadAsStreamAsync(ct);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                throw new InvalidOperationException(
                    "fal.ai returned a result body for '" + model + "' that exceeds the " + limit +
                    "-byte ceiling this client accepts, so it was refused rather than buffered. A document " +
                    "that large is not an answer to a request whose max_num_objects and frame count were " +
                    "both bounded; retrying will not help until the vendor's answer is a documented one.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>
    /// No-cost connectivity probe the image and analysis arms call: it submits <paramref name="model"/>
    /// with an EMPTY body.
    /// </summary>
    /// <remarks>
    /// Every image and analysis endpoint this platform configures marks at least one input required
    /// (<c>image_url</c>, <c>image_urls</c> or <c>prompt</c>), so the request cannot be dispatched to a
    /// runner, cannot generate and — the vendor's own FAQ says server errors are never charged and a
    /// client error "may" be charged only if a runner already spent GPU time, which an empty body never
    /// reaches — cannot bill. <b>That premise is false for text-to-video</b>, which is why the video arm
    /// passes <see cref="UnparseableProbeBody"/> instead; see it for the two schemas that made the
    /// difference. What the answer proves is stated rather than implied: a <c>404</c> means the model
    /// id is not an endpoint fal routes; a <c>401</c>/<c>403</c>, or a body naming an authorization
    /// problem, means the key was rejected; a validation failure (<c>400</c>/<c>422</c>) that is
    /// neither proves the endpoint exists and the key got past authentication. <b>Measured with a real
    /// key: the queue accepts an empty body at submit (2xx, <c>IN_QUEUE</c>) and validates only when the
    /// job runs</b>, so a 2xx is followed to its result for up to 20 s: a 400/422 there is ok, a job that
    /// is still queued is ok (submit already authenticated and routed it), and a job that actually ran
    /// fails closed.
    /// </remarks>
    public Task<FalProbeOutcome> ProbeAsync(string model, CancellationToken ct) =>
        ProbeAsync(model, "{}", ct);

    /// <summary>
    /// The probe body the VIDEO arm sends: deliberately not valid JSON.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The empty body above is safe only for an endpoint that marks an input required. That premise is
    /// FALSE for text-to-video: <c>fal-ai/kling-video/v3/turbo/pro/text-to-video</c> and its
    /// <c>pro</c> sibling both declare <c>required: []</c> with every input optional and
    /// <c>generate_audio</c> defaulting to <c>true</c>, so an empty body is a valid request — a real 5 s
    /// clip, a real charge (each endpoint's own OpenAPI schema, read 2026-10-16).
    /// </para>
    /// <para>
    /// A body that is not JSON cannot satisfy ANY endpoint's required inputs, because there is no input
    /// document to validate: the runner cannot deserialise it, so it cannot generate and cannot bill.
    /// The vendor's own billing rule is per successful output, never for queue time or a server error.
    /// The image and analysis arms keep the empty body they were measured with, so nothing about their
    /// behaviour changes; this constant and the overload that takes it exist for the video arm alone.
    /// </para>
    /// </remarks>
    public const string UnparseableProbeBody = "{\"reelboltProbe\":";

    /// <summary>
    /// No-cost connectivity probe with the body the caller chooses. <see cref="UnparseableProbeBody"/> is
    /// what the video arm passes; <c>{}</c> is what the image and analysis arms keep passing.
    /// </summary>
    public async Task<FalProbeOutcome> ProbeAsync(string model, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return new FalProbeOutcome(false, "No model id is configured on this provider row.");
        }

        using HttpRequestMessage request = BuildRequest(model, body);
        try
        {
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            string text = await BoundedBody.ReadErrorAsync(response.Content, ct);
            int status = (int)response.StatusCode;

            if (status == 404)
            {
                return new FalProbeOutcome(false,
                    "fal.ai does not route '" + model + "', so this row's model id is wrong: " +
                    ImagePayload.Bounded(text));
            }

            if (status is 401 or 403)
            {
                return new FalProbeOutcome(false,
                    "fal.ai rejected the credential (HTTP " + status + "): " + ImagePayload.Bounded(text),
                    AuthRejected: true);
            }

            if (LooksLikeAnAuthError(text))
            {
                return new FalProbeOutcome(false,
                    "fal.ai answered HTTP " + status + " with an authorization problem: " + ImagePayload.Bounded(text),
                    AuthRejected: true);
            }

            if (response.IsSuccessStatusCode)
            {
                // Measured on a real key (2026-10-10): the queue accepts ANY body at submit time and
                // validates only when the job runs, so a 2xx here already proves the key passed
                // authentication and the model id is routed. Follow the job to its result to confirm the
                // empty body was rejected as a validation error rather than run.
                return await FollowProbeAsync(model, text, ct);
            }

            if (status is 400 or 422)
            {
                return new FalProbeOutcome(true, null);
            }

            return new FalProbeOutcome(false,
                "fal.ai answered HTTP " + status + ", which does not establish that the endpoint and key " +
                "are usable: " + ImagePayload.Bounded(text));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new FalProbeOutcome(false,
                "Provider test failed: " + ex.GetType().Name + ": " + ImagePayload.Bounded(ex.Message));
        }
    }

    private async Task<FalProbeOutcome> FollowProbeAsync(string model, string submitText, CancellationToken ct)
    {
        string? statusUrl;
        string? responseUrl;
        try
        {
            using JsonDocument document = JsonDocument.Parse(submitText);
            statusUrl = ReadString(document.RootElement, "status_url");
            responseUrl = ReadString(document.RootElement, "response_url");
        }
        catch (JsonException)
        {
            statusUrl = responseUrl = null;
        }

        if (string.IsNullOrWhiteSpace(statusUrl) || string.IsNullOrWhiteSpace(responseUrl))
        {
            return new FalProbeOutcome(false,
                "fal.ai accepted the probe for '" + model + "' but returned no status_url/response_url: " +
                ImagePayload.Bounded(submitText));
        }

        try
        {
            RequireQueueOrigin(statusUrl);
            RequireQueueOrigin(responseUrl);
        }
        catch (InvalidOperationException ex)
        {
            return new FalProbeOutcome(false, ex.Message);
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + ProbeFollowTimeout;
        TimeSpan interval = TimeSpan.FromMilliseconds(500);
        while (true)
        {
            using HttpRequestMessage poll = new(HttpMethod.Get, statusUrl);
            poll.Headers.TryAddWithoutValidation("Authorization", "Key " + _apiKey);
            using HttpResponseMessage polled = await _httpClient.SendAsync(poll, HttpCompletionOption.ResponseHeadersRead, ct);
            string text = await BoundedBody.ReadErrorAsync(polled.Content, ct);
            string? status = null;
            if (polled.IsSuccessStatusCode)
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(text);
                    status = ReadString(document.RootElement, "status");
                }
                catch (JsonException)
                {
                    // fall through: reported below as an unreadable status
                }
            }

            if (status == "COMPLETED") break;
            if (status is not ("IN_QUEUE" or "IN_PROGRESS"))
            {
                return new FalProbeOutcome(false,
                    "fal.ai accepted the probe but its status endpoint answered HTTP " + (int)polled.StatusCode +
                    (polled.IsSuccessStatusCode ? " with an unreadable status." : "."));
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                // Still queued: the submit already got past authentication and routing, which is what
                // this test exists to establish; fal does not bill a job that never runs.
                return new FalProbeOutcome(true, null);
            }

            await _delay(interval, ct);
            interval = interval + interval > MaxPollInterval ? MaxPollInterval : interval + interval;
        }

        using HttpRequestMessage result = new(HttpMethod.Get, responseUrl);
        result.Headers.TryAddWithoutValidation("Authorization", "Key " + _apiKey);
        using HttpResponseMessage fetched = await _httpClient.SendAsync(result, HttpCompletionOption.ResponseHeadersRead, ct);
        string body = await BoundedBody.ReadErrorAsync(fetched.Content, ct);
        int code = (int)fetched.StatusCode;
        if (code is 400 or 422)
        {
            return new FalProbeOutcome(true, null);
        }

        if (code is 401 or 403 || LooksLikeAnAuthError(body))
        {
            return new FalProbeOutcome(false,
                "fal.ai rejected the credential when the probe ran (HTTP " + code + ").",
                AuthRejected: true);
        }

        return new FalProbeOutcome(false,
            "fal.ai accepted the probe body and answered HTTP " + code + " instead of rejecting it as a " +
            "validation error, so its answer proves nothing about whether a real request will work.");
    }

    /// <summary>
    /// Downloads the image an output object points at. Dimensions the vendor stated win; when it stated
    /// none (several fal outputs carry no width/height) they are read from the image's own header, so
    /// a caller never has to guess the size of a layer it is about to store.
    /// </summary>
    public async Task<GeneratedImage> ReadImageObjectAsync(JsonElement image, CancellationToken ct)
    {
        string? url = ReadString(image, "url");
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException(
                "fal.ai returned an image object with no url: " + ImagePayload.Bounded(image.GetRawText()));
        }

        GeneratedImage downloaded = await ImagePayload.DownloadAsync(_downloadClient, url, ct);
        int? width = ReadInt(image, "width");
        int? height = ReadInt(image, "height");
        if ((width is null || height is null) && ImagePayload.TryReadDimensions(downloaded.Bytes, out int w, out int h))
        {
            width ??= w;
            height ??= h;
        }

        return downloaded with { Width = width, Height = height };
    }

    /// <summary>Downloads an image URL (the caller's own, e.g. the presigned source) through the SSRF-guarded client.</summary>
    public async Task<GeneratedImage> DownloadImageAsync(string url, CancellationToken ct)
    {
        GeneratedImage downloaded = await ImagePayload.DownloadAsync(_downloadClient, url, ct);
        return ImagePayload.TryReadDimensions(downloaded.Bytes, out int w, out int h)
            ? downloaded with { Width = w, Height = h }
            : downloaded;
    }

    /// <summary>Reads a named object field of the output as an image, or throws naming what came back.</summary>
    public async Task<GeneratedImage> ReadImageFieldAsync(JsonElement output, string field, CancellationToken ct)
    {
        if (!output.TryGetProperty(field, out JsonElement element) || element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "fal.ai returned a result with no '" + field + "' object: " + ImagePayload.Bounded(output.GetRawText()));
        }

        return await ReadImageObjectAsync(element, ct);
    }

    private HttpRequestMessage BuildRequest(string model, Dictionary<string, object?> body) =>
        BuildRequest(model, JsonSerializer.Serialize(body));

    /// <summary>
    /// The one authenticated request shape: <c>POST {base}/{model}</c> with <c>Authorization: Key</c>.
    /// The body is taken as text so a probe can send one that is deliberately not JSON.
    /// </summary>
    private HttpRequestMessage BuildRequest(string model, string body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(BaseAddress() + "/" + model.TrimStart('/')))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        // "Key <FAL_KEY>", verbatim from fal's own cURL example — NOT a bearer token.
        request.Headers.TryAddWithoutValidation("Authorization", "Key " + _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (_submitHeaders is not null)
        {
            foreach ((string name, string value) in _submitHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return request;
    }

    private string BaseAddress() => _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? DefaultEndpoint;

    public static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int? ReadInt(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
            ? parsed
            : null;

    public static double? ReadDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDouble(out double parsed)
            ? parsed
            : null;

    private static bool LooksLikeAnAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        string lowered = body.ToLowerInvariant();
        return lowered.Contains("unauthor")
            || lowered.Contains("forbidden")
            || lowered.Contains("invalid api key")
            || lowered.Contains("invalid key")
            || lowered.Contains("authentication");
    }
}
