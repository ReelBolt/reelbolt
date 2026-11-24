using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// fal.ai hosted voice isolation over the shared queue contract (<see cref="FalQueueClient"/>), for the endpoints in
/// <see cref="AudioIsolationModelCatalog"/> (default <c>fal-ai/elevenlabs/audio-isolation</c>).
/// </summary>
/// <remarks>
/// One call validates the request against the catalogue, submits, polls the vendor's own <c>status_url</c> to
/// <c>COMPLETED</c>, fetches the <c>response_url</c> and downloads each stem through the SSRF-guarded client with no
/// Authorization header. The source is a presigned GET URL the caller minted; it is sent to the vendor and never
/// logged or put in an error message. Nothing here has been run against the live queue: every test replays
/// hand-written fixtures, and <c>docs/fal-ai-models.md</c> lists what must be measured with a real key.
/// </remarks>
public sealed class FalAiAudioIsolationClient : IAudioIsolationClient
{
    /// <summary>The public queue host. A provider row's endpoint overrides it.</summary>
    public const string DefaultEndpoint = FalQueueClient.DefaultEndpoint;

    /// <summary>Hard ceiling on one downloaded stem (30 minutes of 16-bit mono 44.1 kHz WAV is about 160 MB).</summary>
    public const long MaxDownloadBytes = 256L * 1024 * 1024;

    /// <summary>Poll budget: a 30-minute source is the longest job the platform requests.</summary>
    public static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromMinutes(10);

    private const int MaxVendorMessageChars = 300;
    private const int MaxMetadataChars = 2000;
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(4);

    private readonly HttpClient _downloadClient;
    private readonly FalQueueClient _queue;
    private readonly string _apiKey;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="httpClient">The queue transport (base address = the row's endpoint or <see cref="DefaultEndpoint"/>).</param>
    /// <param name="apiKey">The fal key. Blank THROWS: there is no ambient fallback.</param>
    /// <param name="downloadClient">The SSRF-guarded client for result URLs.</param>
    public FalAiAudioIsolationClient(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException(
                "A fal.ai key is required for audio isolation; a blank key is refused rather than read from the environment.",
                nameof(apiKey));
        }

        _apiKey = apiKey.Trim();
        _downloadClient = downloadClient ?? httpClient;
        _pollInterval = pollInterval ?? FalQueueClient.DefaultPollInterval;
        _pollTimeout = pollTimeout ?? DefaultPollTimeout;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _queue = new FalQueueClient(httpClient, _apiKey, _downloadClient, _pollInterval, _pollTimeout, _delay);
    }

    private static AudioIsolationModelCatalog Catalog => AudioIsolationModelCatalog.Instance;

    /// <inheritdoc />
    public AudioIsolationEndpoint Describe(string model) =>
        Catalog.Resolve(model) ?? throw new NotSupportedException(
            "'" + (model?.Trim() ?? string.Empty) + "' is not a fal.ai audio-isolation endpoint this platform has a read " +
            "schema and a price for, so it cannot be priced and will not be used. Known: " + Catalog.Ids + ".");

    /// <inheritdoc />
    public async Task<AudioIsolationResult> IsolateAsync(AudioIsolationRequest request, CancellationToken ct,
        Func<string, Task>? onSubmitted = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        AudioIsolationEndpoint endpoint = Validate(request);

        Dictionary<string, object?> body = new() { [endpoint.AudioField] = request.SourceUrl.Trim() };

        FalSubmission submission;
        try
        {
            submission = await _queue.SubmitAsync(endpoint.Id, body, ct);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            int? status = ex.StatusCode is { } code ? (int)code : null;
            throw new AudioIsolationException(
                status is { } s ? AudioIsolationException.Classify(s) : AudioIsolationErrorKind.Retryable,
                Bounded(ex.Message), status);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested)
        {
            // Accepted but untrackable: the job may run and bill, so never a definite rejection.
            throw new AudioIsolationException(AudioIsolationErrorKind.VendorFailed, Bounded(ex.Message));
        }

        if (onSubmitted is not null && !string.IsNullOrEmpty(submission.RequestId))
            await onSubmitted(submission.RequestId);

        JsonElement output = await WaitForOutputAsync(endpoint, submission, ct);
        string voiceUrl = ReadStemUrl(output, endpoint.VoiceOutputField, endpoint, submission.RequestId);
        (byte[] voice, string mime, string extension) = await DownloadAsync(voiceUrl, ct);

        byte[]? background = null;
        if (request.WantBackground == true)
        {
            string backgroundUrl = ReadStemUrl(output, endpoint.BackgroundOutputField!, endpoint, submission.RequestId);
            (background, _, _) = await DownloadAsync(backgroundUrl, ct);
        }

        return new AudioIsolationResult(voice, mime, extension, background, request.SourceDurationSec, endpoint.Id,
            MetadataOf(output, endpoint), string.IsNullOrEmpty(submission.RequestId) ? null : submission.RequestId);
    }

    private static AudioIsolationEndpoint Validate(AudioIsolationRequest request)
    {
        AudioIsolationEndpoint? endpoint = Catalog.Resolve(request.Model);
        if (endpoint is null)
        {
            throw new AudioIsolationException(AudioIsolationErrorKind.RequestInvalid,
                "'" + request.Model.Trim() + "' is not a catalogued audio-isolation endpoint. Known: " + Catalog.Ids + ".");
        }

        if (!Uri.TryCreate(request.SourceUrl?.Trim(), UriKind.Absolute, out Uri? uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new AudioIsolationException(AudioIsolationErrorKind.RequestInvalid,
                "The source must be an absolute https URL (a presigned GET) without embedded credentials.");
        }

        if (!(request.SourceDurationSec > 0) || double.IsInfinity(request.SourceDurationSec))
        {
            throw new AudioIsolationException(AudioIsolationErrorKind.RequestInvalid,
                "The source duration must be a positive number of seconds.");
        }

        if (request.WantBackground == true && !endpoint.HasBackgroundStem)
        {
            throw new AudioIsolationException(AudioIsolationErrorKind.RequestInvalid,
                "'" + endpoint.Id + "' returns no background stem, so one cannot be requested.");
        }

        return endpoint;
    }

    private async Task<JsonElement> WaitForOutputAsync(AudioIsolationEndpoint endpoint, FalSubmission submission, CancellationToken ct)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _pollTimeout;
        TimeSpan interval = _pollInterval;
        string label = "job " + (string.IsNullOrEmpty(submission.RequestId) ? "(no id)" : submission.RequestId);

        while (true)
        {
            FalQueueStatus status;
            try
            {
                status = await _queue.ReadStatusAsync(submission.StatusUrl, label, ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or JsonException
                                           or HttpRequestException && !ct.IsCancellationRequested)
            {
                throw new AudioIsolationException(AudioIsolationErrorKind.VendorFailed,
                    "fal.ai accepted '" + endpoint.Id + "' (" + label + ") but its status could not be read: " +
                    Bounded(ex.Message) + ". The job may still run and bill, so it is not retried.");
            }

            if (status.Status == "COMPLETED")
            {
                if (!string.IsNullOrWhiteSpace(status.Error))
                {
                    throw new AudioIsolationException(AudioIsolationErrorKind.VendorFailed,
                        "fal.ai ran '" + endpoint.Id + "' (" + label + ") and it failed: " +
                        Bounded(FalQueueClient.CleanVendorText(status.Error)));
                }

                break;
            }

            // IN_QUEUE and IN_PROGRESS (and any other non-terminal word) keep polling until the budget runs out.
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AudioIsolationException(AudioIsolationErrorKind.VendorFailed,
                    "fal.ai did not finish '" + endpoint.Id + "' (" + label + ") within " + _pollTimeout + ". The job may " +
                    "still complete and bill, so it is not retried.");
            }

            await _delay(interval, ct);
            interval = interval + interval > MaxPollInterval ? MaxPollInterval : interval + interval;
        }

        FalResultFetch fetch = await _queue.TryFetchResultAsync(submission.ResponseUrl, label, ct);
        if (fetch.Output is { } output) return output;

        bool refused = fetch.StatusCode is 400 or 422;
        string detail = refused ? Bounded(FalQueueClient.VendorValidationDetail(fetch.Body)) : string.Empty;
        throw new AudioIsolationException(
            refused ? AudioIsolationErrorKind.RequestInvalid
                : fetch.StatusCode is 429 or >= 500 ? AudioIsolationErrorKind.Retryable
                : fetch.StatusCode == 402 ? AudioIsolationErrorKind.VendorQuota
                : AudioIsolationErrorKind.VendorFailed,
            "fal.ai answered HTTP " + fetch.StatusCode + " fetching the result of '" + endpoint.Id + "'" +
            (detail.Length > 0 ? ": " + detail : "."),
            fetch.StatusCode);
    }

    private static string ReadStemUrl(JsonElement output, string field, AudioIsolationEndpoint endpoint, string requestId)
    {
        if (output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty(field, out JsonElement stem)
            && stem.ValueKind == JsonValueKind.Object
            && FalQueueClient.ReadString(stem, "url") is { Length: > 0 } url)
        {
            return url;
        }

        throw new AudioIsolationException(AudioIsolationErrorKind.VendorFailed,
            "fal.ai completed '" + endpoint.Id + "' (" + requestId + ") but returned no '" + field + ".url'. " +
            "A 200 carrying no audio is not a success.");
    }

    /// <summary>
    /// A fresh node (never re-parented) for <paramref name="element"/>, with every string that carries a URL or a
    /// SigV4 token replaced by <c>&lt;url&gt;</c>, recursively.
    /// </summary>
    private static JsonNode? Sanitized(JsonElement element, int depth = 0)
    {
        if (depth > 8) return JsonValue.Create("<deep>");
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                string text = element.GetString() ?? string.Empty;
                return JsonValue.Create(FalQueueClient.StripUrls(text) == text ? text : "<url>");
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
    /// The output with every URL stripped, bounded; what the vendor said besides the stems. Provenance only: it
    /// can NEVER fail the call (the job has already billed), so any problem yields null.
    /// </summary>
    private static string? MetadataOf(JsonElement output, AudioIsolationEndpoint endpoint)
    {
        try
        {
            if (output.ValueKind != JsonValueKind.Object) return null;
            JsonObject copy = [];
            foreach (JsonProperty property in output.EnumerateObject())
            {
                if (property.Name == endpoint.VoiceOutputField || property.Name == endpoint.BackgroundOutputField)
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
    /// Downloads one stem. The URL comes out of a vendor response, so it is untrusted: https only, no credentials, no
    /// loopback/private/link-local literal host (and the guarded client re-checks the address it dials); the answer
    /// must be an audio or binary type and at most <see cref="MaxDownloadBytes"/>. No Authorization header.
    /// </summary>
    private async Task<(byte[] Bytes, string Mime, string Extension)> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            GeneratedClipUrlGuard.EnsureAllowed(url);
        }
        catch (InvalidOperationException ex)
        {
            throw new AudioIsolationException(AudioIsolationErrorKind.VendorFailed,
                "fal.ai returned an audio URL that is not safe to fetch: " + Bounded(ex.Message));
        }

        using HttpResponseMessage response = await _downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AudioIsolationException(AudioIsolationErrorKind.VendorFailed,
                "Downloading the isolated audio answered HTTP " + (int)response.StatusCode + ".", (int)response.StatusCode);
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        byte[] bytes;
        try
        {
            bytes = await AudioPayload.ReadAsync(response, MaxDownloadBytes, "the isolated audio", ct);
        }
        catch (AudioGenerationException ex)
        {
            throw new AudioIsolationException(AudioIsolationErrorKind.VendorFailed, Bounded(ex.Message), ex.VendorStatus);
        }

        string mime = mediaType is { Length: > 0 } m && m.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? m : "audio/mpeg";
        return (bytes, mime, ExtensionOf(mime, url));
    }

    private static string ExtensionOf(string mime, string url) => mime.ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
        "audio/ogg" or "audio/opus" => ".ogg",
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/mp4" or "audio/m4a" or "audio/x-m4a" or "audio/aac" => ".m4a",
        _ => Uri.TryCreate(url, UriKind.Absolute, out Uri? u) && Path.GetExtension(u.AbsolutePath) is { Length: > 1 and <= 5 } e ? e.ToLowerInvariant() : ".mp3",
    };

    private string Bounded(string text)
    {
        // The customer's presigned source URL may be echoed by fal in any refusal or failure text: mask every URL
        // (and scheme-less X-Amz- tokens) BEFORE the length cap so a cut can never leave a signature behind.
        text = FalQueueClient.StripUrls(text).Replace(_apiKey, "***", StringComparison.Ordinal);
        return text.Length <= MaxVendorMessageChars ? text : text[..MaxVendorMessageChars] + "…";
    }
}
