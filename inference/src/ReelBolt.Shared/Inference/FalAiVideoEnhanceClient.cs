using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// The shared queue plumbing of the two hosted video-enhancement clients: validate against the catalogue, submit, poll
/// the vendor's own <c>status_url</c>, fetch the <c>response_url</c> and download the clip through the SSRF-guarded
/// client with no Authorization header. Submit and poll are SEPARATE so a job can outlive one poll and an engine
/// restart resumes from the persisted handle instead of submitting (and paying) again. The source is a presigned GET URL
/// the caller minted; it is sent to the vendor and never logged or put in an error message. Nothing here has been run
/// against the live queue: every test replays hand-written fixtures.
/// </summary>
internal sealed partial class FalVideoEnhanceJob
{
    /// <summary>Hard ceiling on one downloaded clip (declared and actually streamed): 512 MiB.</summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

    /// <summary>Default poll budget of one <see cref="PollAsync"/> call (<c>VideoEnhance:Hosted:PollTimeoutMinutes</c>).</summary>
    public static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromMinutes(VideoEnhanceCreditPricing.DefaultPollTimeoutMinutes);

    private const int MaxVendorMessageChars = 300;
    private const int MaxMetadataChars = 2000;
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(10);

    private readonly HttpClient _downloadClient;
    private readonly FalQueueClient _queue;
    private readonly string _apiKey;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly long _maxDownloadBytes;

    public FalVideoEnhanceJob(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient,
        TimeSpan? pollInterval,
        TimeSpan? pollTimeout,
        Func<TimeSpan, CancellationToken, Task>? delay,
        long maxDownloadBytes = MaxDownloadBytes)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _maxDownloadBytes = maxDownloadBytes;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException(
                "A fal.ai key is required for video enhancement; a blank key is refused rather than read from the environment.",
                nameof(apiKey));
        }

        _apiKey = apiKey.Trim();
        _downloadClient = downloadClient ?? httpClient;
        _pollInterval = pollInterval ?? FalQueueClient.DefaultPollInterval;
        _pollTimeout = pollTimeout ?? DefaultPollTimeout;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _queue = new FalQueueClient(httpClient, _apiKey, _downloadClient, _pollInterval, _pollTimeout, _delay);
    }

    public static VideoEnhanceEndpoint Describe(InferenceProviderCapability capability, string model) =>
        VideoEnhanceModelCatalog.Instance.Resolve(capability, model) ?? throw new NotSupportedException(
            "'" + (model?.Trim() ?? string.Empty) + "' is not a fal.ai " + capability + " endpoint this platform has a read " +
            "schema and a price for, so it cannot be priced and will not be used. Known: " +
            VideoEnhanceModelCatalog.Instance.IdsFor(capability) + ".");

    /// <summary>The fields common to both requests, checked before anything is sent.</summary>
    public static VideoEnhanceEndpoint ValidateSource(
        InferenceProviderCapability capability, string model, string sourceUrl, int width, int height, double fps,
        double durationSec, long frames, int maxSourceSeconds)
    {
        VideoEnhanceEndpoint? endpoint = VideoEnhanceModelCatalog.Instance.Resolve(capability, model);
        if (endpoint is null)
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "'" + model?.Trim() + "' is not a catalogued " + capability + " endpoint. Known: " +
                VideoEnhanceModelCatalog.Instance.IdsFor(capability) + ".");
        }

        if (!Uri.TryCreate(sourceUrl?.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "The source must be an absolute https URL (a presigned GET) without embedded credentials.");
        }

        if (width <= 0 || height <= 0 || frames <= 0 || !(fps > 0) || double.IsInfinity(fps))
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "The source width, height, frame rate and frame count must all be positive measured values.");
        }

        if (!VideoEnhanceCreditPricing.WithinCap(durationSec, maxSourceSeconds))
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "Hosted video enhancement takes a source of up to " + maxSourceSeconds + " seconds.");
        }

        return endpoint;
    }

    /// <summary>Refuses a body that carries a field the endpoint's schema reading does not allow.</summary>
    public static void RequireOnlyAllowedFields(VideoEnhanceEndpoint endpoint, IReadOnlyDictionary<string, object?> body)
    {
        foreach (string field in body.Keys)
        {
            if (!endpoint.AllowedFields.Contains(field))
            {
                throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                    "'" + endpoint.Id + "' does not declare a '" + field + "' field this platform may send.");
            }
        }
    }

    public async Task<VideoEnhanceJobHandle> SubmitAsync(
        VideoEnhanceEndpoint endpoint, Dictionary<string, object?> body, CancellationToken ct)
    {
        RequireOnlyAllowedFields(endpoint, body);
        try
        {
            FalSubmission submission = await _queue.SubmitAsync(endpoint.Id, body, ct);
            return new VideoEnhanceJobHandle(submission.RequestId, submission.StatusUrl, submission.ResponseUrl);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            int? status = ex.StatusCode is { } code ? (int)code : null;
            string message = Bounded(ex.Message);
            throw new VideoEnhanceException(
                IsRefusalText(message) ? VideoEnhanceErrorKind.Refused
                    : status is { } s ? VideoEnhanceException.Classify(s) : VideoEnhanceErrorKind.SubmitOutcomeUnknown,
                message, status);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // The HttpClient timeout fired while the caller did not cancel: the POST may already have queued (and will
            // bill) a job, so it is outcome-unknown and never resubmitted. Reported by type only.
            throw new VideoEnhanceException(VideoEnhanceErrorKind.SubmitOutcomeUnknown,
                "The submit to fal.ai timed out (" + ex.GetType().Name + "); the job may have been queued and will not be resubmitted.");
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested)
        {
            // Accepted but untrackable: the job may run and bill, so never a definite rejection.
            throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed, Bounded(ex.Message));
        }
        catch (JsonException ex) when (!ct.IsCancellationRequested)
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed, Bounded(ex.Message));
        }
    }

    public async Task<VideoEnhanceResult> PollAsync(
        InferenceProviderCapability capability, VideoEnhanceJobHandle handle, string model, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(handle);
        VideoEnhanceEndpoint endpoint = VideoEnhanceModelCatalog.Instance.Resolve(capability, model)
            ?? throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "'" + model?.Trim() + "' is not a catalogued " + capability + " endpoint.");

        JsonElement output = await WaitForOutputAsync(endpoint, handle, ct);
        string url = ReadClipUrl(output, endpoint, handle.RequestId);
        (byte[] clip, string mime, string extension) = await DownloadAsync(url, ct);
        return new VideoEnhanceResult(clip, mime, extension, ReadDuration(output), endpoint.Id, MetadataOf(output, endpoint),
            string.IsNullOrEmpty(handle.RequestId) ? null : handle.RequestId);
    }

    private async Task<JsonElement> WaitForOutputAsync(VideoEnhanceEndpoint endpoint, VideoEnhanceJobHandle handle, CancellationToken ct)
    {
        try
        {
            _queue.RequireQueueOrigin(handle.StatusUrl); // a hostile persisted handle is permanent, never a retry
        }
        catch (InvalidOperationException ex)
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed, Bounded(ex.Message));
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + _pollTimeout;
        TimeSpan interval = _pollInterval;
        string label = "job " + (string.IsNullOrEmpty(handle.RequestId) ? "(no id)" : handle.RequestId);

        while (true)
        {
            FalQueueStatus status;
            try
            {
                status = await _queue.ReadStatusAsync(handle.StatusUrl, label, ct);
            }
            catch (Exception ex) when (IsTransientPoll(ex) && !ct.IsCancellationRequested)
            {
                // A transport error, a 5xx or a garbled body on ONE poll is not the job failing: retry with the
                // bounded backoff until the deadline, then leave the handle resumable (StillRunning).
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    throw new VideoEnhanceException(VideoEnhanceErrorKind.StillRunning,
                        "fal.ai status of '" + endpoint.Id + "' (" + label + ") kept failing until the poll budget ran out: " +
                        Bounded(ex.Message) + ". The job may still complete and bill; keep the handle and poll again.");
                }

                await _delay(interval, ct);
                interval = interval + interval > MaxPollInterval ? MaxPollInterval : interval + interval;
                continue;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or JsonException or HttpRequestException
                                       && !ct.IsCancellationRequested)
            {
                throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed,
                    "fal.ai accepted '" + endpoint.Id + "' (" + label + ") but its status could not be read: " +
                    Bounded(ex.Message) + ". The job may still run and bill, so it is not retried.");
            }

            if (status.Status == "COMPLETED")
            {
                if (!string.IsNullOrWhiteSpace(status.Error))
                {
                    string error = Bounded(FalQueueClient.CleanVendorText(status.Error));
                    throw new VideoEnhanceException(
                        IsRefusalText(error) ? VideoEnhanceErrorKind.Refused : VideoEnhanceErrorKind.VendorFailed,
                        "fal.ai ran '" + endpoint.Id + "' (" + label + ") and it failed: " + error);
                }

                break;
            }

            // IN_QUEUE and IN_PROGRESS keep polling until the budget runs out.
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new VideoEnhanceException(VideoEnhanceErrorKind.StillRunning,
                    "fal.ai has not finished '" + endpoint.Id + "' (" + label + ") within " + _pollTimeout + ". The job may " +
                    "still complete and bill; keep the handle and poll it again, do not submit again.");
            }

            await _delay(interval, ct);
            interval = interval + interval > MaxPollInterval ? MaxPollInterval : interval + interval;
        }

        FalResultFetch fetch;
        try
        {
            fetch = await _queue.TryFetchResultAsync(handle.ResponseUrl, label, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or HttpRequestException && !ct.IsCancellationRequested)
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed,
                "The result of '" + endpoint.Id + "' (" + label + ") could not be read: " + Bounded(ex.Message));
        }

        if (fetch.Output is { } output) return output;

        bool invalid = fetch.StatusCode is 400 or 422;
        string detail = invalid ? Bounded(FalQueueClient.VendorValidationDetail(fetch.Body)) : string.Empty;
        throw new VideoEnhanceException(
            invalid && IsRefusalText(detail) ? VideoEnhanceErrorKind.Refused
                : invalid ? VideoEnhanceErrorKind.RequestInvalid
                : fetch.StatusCode is 429 or >= 500 ? VideoEnhanceErrorKind.VendorFailed // may have been billed: not retried
                : fetch.StatusCode == 402 ? VideoEnhanceErrorKind.VendorQuota
                : VideoEnhanceErrorKind.VendorFailed,
            "fal.ai answered HTTP " + fetch.StatusCode + " fetching the result of '" + endpoint.Id + "'" +
            (detail.Length > 0 ? ": " + detail : "."),
            fetch.StatusCode);
    }

    private static string ReadClipUrl(JsonElement output, VideoEnhanceEndpoint endpoint, string requestId)
    {
        if (output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty(endpoint.OutputField, out JsonElement clip)
            && clip.ValueKind == JsonValueKind.Object
            && FalQueueClient.ReadString(clip, "url") is { Length: > 0 } url)
        {
            return url;
        }

        throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed,
            "fal.ai completed '" + endpoint.Id + "' (" + requestId + ") but returned no '" + endpoint.OutputField + ".url'. " +
            "A 200 carrying no video is not a success.");
    }

    private static double? ReadDuration(JsonElement output) =>
        output.ValueKind == JsonValueKind.Object && FalQueueClient.ReadDouble(output, "duration") is { } d && d > 0 && !double.IsInfinity(d)
            ? d
            : null;

    /// <summary>
    /// Downloads the clip. The URL comes out of a vendor response, so it is untrusted: https only, no credentials, no
    /// loopback/private/link-local literal host (and the guarded client re-checks the address it dials); the answer must
    /// be a video or binary type and at most <see cref="MaxDownloadBytes"/>. No Authorization header.
    /// </summary>
    private async Task<(byte[] Bytes, string Mime, string Extension)> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            GeneratedClipUrlGuard.EnsureAllowed(url);
        }
        catch (InvalidOperationException ex)
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed,
                "fal.ai returned a video URL that is not safe to fetch: " + Bounded(ex.Message));
        }

        try
        {
            using HttpResponseMessage response = await _downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed,
                    "Downloading the enhanced clip answered HTTP " + (int)response.StatusCode + ".", (int)response.StatusCode);
            }

            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            // video/* only: a missing Content-Type or application/octet-stream is not accepted as a clip.
            if (mediaType is null || !mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed,
                    "Unexpected content type '" + FalQueueClient.CleanVendorText(mediaType ?? "(none)", 60) + "' for the enhanced clip.");
            }

            if (response.Content.Headers.ContentLength > _maxDownloadBytes)
            {
                throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed,
                    "The enhanced clip is " + response.Content.Headers.ContentLength + " bytes, over the " + _maxDownloadBytes + "-byte limit.");
            }

            Stream inner = await response.Content.ReadAsStreamAsync(ct);
            await using LimitedReadStream limited = new(inner, _maxDownloadBytes, inner);
            using MemoryStream buffer = new();
            try
            {
                await limited.CopyToAsync(buffer, ct);
            }
            catch (StreamLimitExceededException ex)
            {
                throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed, ex.Message, inner: ex);
            }

            if (buffer.Length == 0)
            {
                throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed, "The enhanced clip was empty.");
            }

            string mime = mediaType;
            return (buffer.ToArray(), mime, ExtensionOf(mime, url));
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            // Includes the guarded handler's refusal of a host that resolves to a private address.
            throw new VideoEnhanceException(VideoEnhanceErrorKind.VendorFailed,
                "The enhanced clip could not be downloaded: " + Bounded(ex.Message));
        }
    }

    /// <summary>Transport errors, a 5xx/408/429 status answer and unparseable bodies are transient on a poll; a 4xx is permanent.</summary>
    private static bool IsTransientPoll(Exception ex)
    {
        if (ex is HttpRequestException or JsonException) return true;
        if (ex is not InvalidOperationException) return false;
        Match m = PollStatus().Match(ex.Message);
        return m.Success && int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) is 408 or 429 or >= 500;
    }

    [GeneratedRegex(@"returned HTTP (\d{3}) while polling")]
    private static partial Regex PollStatus();

    private static string ExtensionOf(string mime, string url) => mime.ToLowerInvariant() switch
    {
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        "video/webm" => ".webm",
        "video/x-matroska" => ".mkv",
        _ => Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && Path.GetExtension(u.AbsolutePath) is { Length: > 1 and <= 5 } e
            ? e.ToLowerInvariant()
            : ".mp4",
    };

    /// <summary>True when the vendor text says its safety checker or content policy refused the clip.</summary>
    private static bool IsRefusalText(string text) =>
        text.Contains("content_policy", StringComparison.OrdinalIgnoreCase)
        || text.Contains("content policy", StringComparison.OrdinalIgnoreCase)
        || text.Contains("nsfw", StringComparison.OrdinalIgnoreCase)
        || text.Contains("safety checker", StringComparison.OrdinalIgnoreCase)
        || text.Contains("safety_checker", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A fresh node for <paramref name="element"/>, with every string that carries a URL or a SigV4 token replaced by
    /// <c>&lt;url&gt;</c>, recursively.
    /// </summary>
    private static JsonNode? Sanitized(JsonElement element, int depth = 0)
    {
        if (depth > 8) return JsonValue.Create("<deep>");
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                string text = element.GetString() ?? string.Empty;
                return JsonValue.Create(UrlMask().Replace(text, "<url>") == text ? text : "<url>");
            case JsonValueKind.Object:
                JsonObject obj = [];
                foreach (JsonProperty p in element.EnumerateObject()) obj[p.Name] = Sanitized(p.Value, depth + 1);
                return obj;
            case JsonValueKind.Array:
                JsonArray array = [];
                foreach (JsonElement item in element.EnumerateArray()) array.Add(Sanitized(item, depth + 1));
                return array;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return JsonNode.Parse(element.GetRawText());
            default:
                return null;
        }
    }

    /// <summary>
    /// The output with every URL stripped, bounded: what the vendor said besides the clip. Provenance only; it can NEVER
    /// fail the call (the job has already billed), so any problem yields null.
    /// </summary>
    private static string? MetadataOf(JsonElement output, VideoEnhanceEndpoint endpoint)
    {
        try
        {
            if (output.ValueKind != JsonValueKind.Object) return null;
            JsonObject copy = [];
            foreach (JsonProperty property in output.EnumerateObject())
            {
                if (property.Name == endpoint.OutputField)
                {
                    if (property.Value.ValueKind == JsonValueKind.Object)
                    {
                        JsonObject file = [];
                        foreach (JsonProperty p in property.Value.EnumerateObject())
                        {
                            if (p.Name.Equals("url", StringComparison.OrdinalIgnoreCase)) continue;
                            file[p.Name] = Sanitized(p.Value);
                        }

                        if (file.Count > 0) copy[property.Name] = file;
                    }

                    continue;
                }

                copy[property.Name] = Sanitized(property.Value);
            }

            if (copy.Count == 0) return null;
            string json = copy.ToJsonString();
            return json.Length <= MaxMetadataChars ? json : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The customer's presigned source URL may be echoed by fal in any refusal or failure text: mask every URL (and
    /// scheme-less X-Amz- tokens) BEFORE the length cap so a cut can never leave a signature behind.
    /// </summary>
    private string Bounded(string? text)
    {
        string masked = UrlMask().Replace(text ?? string.Empty, "<url>").Replace(_apiKey, "***", StringComparison.Ordinal);
        return masked.Length <= MaxVendorMessageChars ? masked : masked[..MaxVendorMessageChars] + "…";
    }

    [GeneratedRegex(@"(?i)\b(?:https?|s3|gs|ftp):(?:\\*/){2}\S+|\S*X-Amz-\S*")]
    private static partial Regex UrlMask();
}

/// <summary>
/// fal.ai hosted video upscaling over the shared queue contract, for the endpoints in <see cref="VideoEnhanceModelCatalog"/>
/// (default <c>fal-ai/topaz/upscale/video</c>, alternative <c>fal-ai/bytedance-upscaler/upscale/video</c>).
/// </summary>
public sealed class FalAiVideoUpscaleClient : IVideoUpscaleClient
{
    /// <summary>The public queue host. A provider row's endpoint overrides it.</summary>
    public const string DefaultEndpoint = FalQueueClient.DefaultEndpoint;

    private readonly FalVideoEnhanceJob _job;
    private readonly int _maxSourceSeconds;

    /// <param name="httpClient">The queue transport (base address = the row's endpoint or <see cref="DefaultEndpoint"/>).</param>
    /// <param name="apiKey">The fal key. Blank THROWS: there is no ambient fallback.</param>
    /// <param name="downloadClient">The SSRF-guarded client for result URLs.</param>
    /// <param name="pollTimeout">The poll budget of one <see cref="PollAsync"/> (default 30 minutes).</param>
    /// <param name="maxSourceSeconds">The per-request source length cap (default 120).</param>
    public FalAiVideoUpscaleClient(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxSourceSeconds = VideoEnhanceCreditPricing.DefaultMaxSourceSeconds,
        long maxDownloadBytes = FalVideoEnhanceJob.MaxDownloadBytes)
    {
        _job = new FalVideoEnhanceJob(httpClient, apiKey, downloadClient, pollInterval, pollTimeout, delay, maxDownloadBytes);
        _maxSourceSeconds = maxSourceSeconds;
    }

    /// <inheritdoc />
    public VideoEnhanceEndpoint Describe(string model) =>
        FalVideoEnhanceJob.Describe(InferenceProviderCapability.VideoUpscale, model);

    /// <inheritdoc />
    public async Task<VideoEnhanceJobHandle> SubmitAsync(VideoUpscaleRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        VideoEnhanceEndpoint endpoint = FalVideoEnhanceJob.ValidateSource(
            InferenceProviderCapability.VideoUpscale, request.Model, request.SourceUrl, request.SourceWidth,
            request.SourceHeight, request.SourceFps, request.SourceDurationSec, request.SourceFrames, _maxSourceSeconds);

        if (!(request.Factor >= endpoint.MinFactor) || request.Factor > endpoint.MaxFactor)
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "'" + endpoint.Id + "' takes a scale factor from " + endpoint.MinFactor + " to " + endpoint.MaxFactor + ".");
        }

        // An output tier the catalogue has no price for is unofferable (bytedance has no 720p tier): fail closed here,
        // before a job is submitted that could not be priced at settlement.
        int outputShortSide = VideoEnhanceCreditPricing.OutputShortSide(request.SourceWidth, request.SourceHeight, request.Factor);
        if (VideoEnhanceCreditPricing.UpscaleTierFor(endpoint, outputShortSide, request.SourceFps) is null)
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "'" + endpoint.Id + "' has no price for a " + outputShortSide + "p output, so it is not offered for it.");
        }

        return await _job.SubmitAsync(endpoint, BuildBody(endpoint, request), ct);
    }

    internal static Dictionary<string, object?> BuildBody(VideoEnhanceEndpoint endpoint, VideoUpscaleRequest request)
    {
        Dictionary<string, object?> body = new() { [endpoint.VideoField] = request.SourceUrl.Trim() };
        if (endpoint.Id == VideoEnhanceModelCatalog.BytedanceId)
        {
            body["scale_ratio"] = request.Factor;
            // bytedance FORCES a target frame rate (24..120, default 30). We send the source rate rounded and clamped;
            // the local conform restores the exact fps, so this only needs to keep frame timing close.
            body["target_fps"] = VideoEnhanceCreditPricing.BytedanceTargetFps(request.SourceFps);
            body["enhancement_tier"] = "standard";
        }
        else
        {
            // Topaz: Proteus, H.264 out, a factor computed by the caller. NEVER target_fps (it turns interpolation on).
            body["upscale_factor"] = request.Factor;
            body["model"] = "Proteus";
            body["H264_output"] = true;
        }

        return body;
    }

    /// <inheritdoc />
    public Task<VideoEnhanceResult> PollAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct) =>
        _job.PollAsync(InferenceProviderCapability.VideoUpscale, handle, model, ct);

    /// <inheritdoc />
    public Task<VideoEnhanceResult> ResumeAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct) =>
        PollAsync(handle, model, ct);
}

/// <summary>
/// fal.ai hosted frame interpolation over the shared queue contract (default <c>fal-ai/rife/video</c>, alternative
/// <c>fal-ai/film/video</c>).
/// </summary>
public sealed class FalAiFrameInterpolationClient : IFrameInterpolationClient
{
    /// <summary>The public queue host. A provider row's endpoint overrides it.</summary>
    public const string DefaultEndpoint = FalQueueClient.DefaultEndpoint;

    private readonly FalVideoEnhanceJob _job;
    private readonly int _maxSourceSeconds;

    public FalAiFrameInterpolationClient(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxSourceSeconds = VideoEnhanceCreditPricing.DefaultMaxSourceSeconds,
        long maxDownloadBytes = FalVideoEnhanceJob.MaxDownloadBytes)
    {
        _job = new FalVideoEnhanceJob(httpClient, apiKey, downloadClient, pollInterval, pollTimeout, delay, maxDownloadBytes);
        _maxSourceSeconds = maxSourceSeconds;
    }

    /// <inheritdoc />
    public VideoEnhanceEndpoint Describe(string model) =>
        FalVideoEnhanceJob.Describe(InferenceProviderCapability.FrameInterpolation, model);

    /// <inheritdoc />
    public async Task<VideoEnhanceJobHandle> SubmitAsync(FrameInterpolationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        VideoEnhanceEndpoint endpoint = FalVideoEnhanceJob.ValidateSource(
            InferenceProviderCapability.FrameInterpolation, request.Model, request.SourceUrl, request.SourceWidth,
            request.SourceHeight, request.SourceFps, request.SourceDurationSec, request.SourceFrames, _maxSourceSeconds);

        if (!endpoint.HostedMultipliers.Contains(request.Multiplier))
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "The hosted path offers a frame multiplier of " + string.Join(" or ", endpoint.HostedMultipliers) + " only.");
        }

        return await _job.SubmitAsync(endpoint, BuildBody(endpoint, request), ct);
    }

    internal static Dictionary<string, object?> BuildBody(VideoEnhanceEndpoint endpoint, FrameInterpolationRequest request)
    {
        Dictionary<string, object?> body = new()
        {
            [endpoint.VideoField] = request.SourceUrl.Trim(),
            // num_frames is the count of IN-BETWEEN frames: 1 doubles the rate, 3 quadruples it.
            ["num_frames"] = request.Multiplier - 1,
            ["use_calculated_fps"] = true,
            ["use_scene_detection"] = true,
            ["loop"] = false,
        };
        if (endpoint.Id == VideoEnhanceModelCatalog.FilmId)
        {
            // Never sync_mode: true (it would return a data URI instead of a URL we can fetch through the guard).
            body["enable_safety_checker"] = true;
        }

        return body;
    }

    /// <inheritdoc />
    public Task<VideoEnhanceResult> PollAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct) =>
        _job.PollAsync(InferenceProviderCapability.FrameInterpolation, handle, model, ct);

    /// <inheritdoc />
    public Task<VideoEnhanceResult> ResumeAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct) =>
        PollAsync(handle, model, ct);
}

/// <summary>
/// fal.ai hosted video outpainting over the shared queue contract (default <c>fal-ai/wan-vace-14b/outpainting</c>,
/// alternative <c>fal-ai/luma-dream-machine/ray-2/reframe</c>). The platform computes the canvas from a target
/// aspect or four margins and refuses locally anything the chosen endpoint cannot express or price.
/// </summary>
public sealed class FalAiVideoOutpaintClient : IVideoOutpaintClient
{
    /// <summary>The public queue host. A provider row's endpoint overrides it.</summary>
    public const string DefaultEndpoint = FalQueueClient.DefaultEndpoint;

    private readonly FalVideoEnhanceJob _job;
    private readonly int _maxSourceSeconds;

    public FalAiVideoOutpaintClient(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxSourceSeconds = VideoEnhanceCreditPricing.DefaultMaxSourceSeconds)
    {
        _job = new FalVideoEnhanceJob(httpClient, apiKey, downloadClient, pollInterval, pollTimeout, delay);
        _maxSourceSeconds = maxSourceSeconds;
    }

    /// <inheritdoc />
    public VideoEnhanceEndpoint Describe(string model) =>
        FalVideoEnhanceJob.Describe(InferenceProviderCapability.VideoOutpaint, model);

    /// <inheritdoc />
    public async Task<VideoEnhanceJobHandle> SubmitAsync(VideoOutpaintRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        VideoEnhanceEndpoint endpoint = FalVideoEnhanceJob.ValidateSource(
            InferenceProviderCapability.VideoOutpaint, request.Model, request.SourceUrl, request.SourceWidth,
            request.SourceHeight, request.SourceFps, request.SourceDurationSec, request.SourceFrames, _maxSourceSeconds);

        if (endpoint.PromptRequired && string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "'" + endpoint.Id + "' requires a prompt, and a blank one is refused before submitting.");
        }

        VideoOutpaintCanvas? canvas = request.TargetAspect is { Length: > 0 } aspect
            ? VideoOutpaintGeometry.ForAspect(request.SourceWidth, request.SourceHeight, aspect)
            : VideoOutpaintGeometry.ForMargins(
                request.SourceWidth, request.SourceHeight,
                request.MarginLeft, request.MarginRight, request.MarginTop, request.MarginBottom);

        if (canvas is null)
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "The requested canvas is outside what '" + endpoint.Id + "' can express: a supported target aspect, or " +
                "margins of at most " + VideoOutpaintGeometry.MaxMargin + " per side.");
        }

        if (canvas is { Left: 0, Right: 0, Top: 0, Bottom: 0 })
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "The requested canvas adds no margin, so there is nothing to outpaint.");
        }

        if (!VideoEnhanceCreditPricing.AtLeastMinFrames(endpoint, request.SourceFrames))
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "'" + endpoint.Id + "' needs at least " + endpoint.MinOutputFrames + " frames, so a shorter source is refused before submitting.");
        }

        if (!VideoEnhanceCreditPricing.WithinFrameCap(endpoint, request.SourceFrames))
        {
            double? capSeconds = VideoEnhanceCreditPricing.FrameCapSeconds(endpoint, request.SourceFps);
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "'" + endpoint.Id + "' takes at most " + endpoint.MaxOutputFrames + " frames" +
                (capSeconds is { } seconds ? " (" + seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " s at " +
                    request.SourceFps.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " fps)" : string.Empty) +
                ".");
        }

        if (endpoint.Id == VideoEnhanceModelCatalog.LumaReframeId)
        {
            if (request.TargetAspect is not { Length: > 0 } lumaAspect || !VideoOutpaintGeometry.LumaSupports(lumaAspect))
            {
                throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                    "'" + endpoint.Id + "' takes one of its aspect_ratio values and no per-side margins, so this canvas is " +
                    "not offered on it.");
            }
        }
        else if (VideoEnhanceCreditPricing.OutpaintRateFor(endpoint, Math.Min(canvas.Width, canvas.Height)) is null)
        {
            throw new VideoEnhanceException(VideoEnhanceErrorKind.RequestInvalid,
                "'" + endpoint.Id + "' has no price for a " + Math.Min(canvas.Width, canvas.Height) +
                "p output, so it is not offered for it.");
        }

        try
        {
            return await _job.SubmitAsync(endpoint, BuildBody(endpoint, request, canvas), ct);
        }
        catch (Exception ex) when ((ex is OperationCanceledException or TimeoutException) && !ct.IsCancellationRequested)
        {
            // An HTTP timeout is not a rejection: the queue may have accepted and may bill the job. Never a definite failure and never
            // re-submitted (the caller keeps the reservation charged); the message names nothing of the request.
            throw new VideoEnhanceException(VideoEnhanceErrorKind.SubmitOutcomeUnknown,
                "The submit to the hosted service timed out, so it is not known whether the job was queued.");
        }
    }

    internal static Dictionary<string, object?> BuildBody(
        VideoEnhanceEndpoint endpoint, VideoOutpaintRequest request, VideoOutpaintCanvas canvas)
    {
        Dictionary<string, object?> body = new() { [endpoint.VideoField] = request.SourceUrl.Trim() };

        if (endpoint.Id == VideoEnhanceModelCatalog.LumaReframeId)
        {
            body["aspect_ratio"] = request.TargetAspect!.Trim();
            if (!string.IsNullOrWhiteSpace(request.Prompt)) body["prompt"] = request.Prompt;
            // The schema declares enable_safety_checker (read 2026-11-03); ask for it, like the Wan body does.
            body["enable_safety_checker"] = true;
            return body;
        }

        // Wan VACE: the model keeps the input's frames and rate, then expands each ENABLED side by expand_ratio. A side
        // whose margin is zero is switched off (expand_* = false) rather than expanded by zero.
        body["prompt"] = request.Prompt;
        body["expand_left"] = canvas.ExpandLeft;
        body["expand_right"] = canvas.ExpandRight;
        body["expand_top"] = canvas.ExpandTop;
        body["expand_bottom"] = canvas.ExpandBottom;
        body["expand_ratio"] = canvas.ExpandRatio;
        body["match_input_num_frames"] = true;
        body["match_input_frames_per_second"] = true;
        body["resolution"] = VideoEnhanceCreditPricing.OutpaintRateFor(endpoint, Math.Min(canvas.Width, canvas.Height))!.Value.Variant;
        // The endpoint defaults this to false; the platform always asks for the safety checker (and moderation runs first).
        body["enable_safety_checker"] = true;
        return body;
    }

    /// <inheritdoc />
    public Task<VideoEnhanceResult> PollAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct) =>
        _job.PollAsync(InferenceProviderCapability.VideoOutpaint, handle, model, ct);

    /// <inheritdoc />
    public Task<VideoEnhanceResult> ResumeAsync(VideoEnhanceJobHandle handle, string model, CancellationToken ct) =>
        PollAsync(handle, model, ct);
}
