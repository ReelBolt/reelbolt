using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// fal.ai music and sound effects over the shared queue contract (<see cref="FalQueueClient"/>): ElevenLabs Music
/// (<c>fal-ai/elevenlabs/music</c>), ElevenLabs SFX v2 (<c>fal-ai/elevenlabs/sound-effects/v2</c>, the SFX default)
/// and MMAudio (<c>fal-ai/mmaudio-v2/text-to-audio</c>), each from <see cref="AudioModelCatalog"/>.
/// </summary>
/// <remarks>
/// <para>
/// An audio job is short (seconds of inference), so one call submits, polls the vendor's own <c>status_url</c> to
/// <c>COMPLETED</c>, fetches the <c>response_url</c> and downloads the file through the SSRF-guarded download client
/// with no Authorization header (the key must never reach a CDN host). Field validation happens before the submit
/// (<see cref="AudioRequestBody"/>); a COMPLETED job carrying <c>error</c> is a failed job, never a download.
/// </para>
/// <para>
/// No output document carries a duration (research/audio-vendors.md A0), so <see cref="AudioGenerationResult.DurationSec"/>
/// is null and the engine probes the file. Nothing here has been run against the live queue: every test replays the
/// recorded mocks.
/// </para>
/// </remarks>
public sealed class FalAiAudioGenerationClient : IAudioGenerationClient
{
    /// <summary>The public queue host. A provider row's endpoint overrides it.</summary>
    public const string DefaultEndpoint = FalQueueClient.DefaultEndpoint;

    /// <summary>Hard ceiling on one downloaded file (five minutes of 192 kbps MP3 is about 7 MB).</summary>
    public const long MaxDownloadBytes = 64L * 1024 * 1024;

    /// <summary>The poll budget: five minutes of music is the longest job the catalogue requests.</summary>
    public static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(4);
    private const int MaxVendorMessageChars = 300;

    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadClient;
    private readonly FalQueueClient _queue;
    private readonly string _apiKey;
    private readonly string? _probeModel;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    /// <param name="httpClient">The queue transport (base address = the row's endpoint or <see cref="DefaultEndpoint"/>).</param>
    /// <param name="apiKey">The fal key, sent as <c>Authorization: Key</c>. Blank THROWS: there is no ambient fallback.</param>
    /// <param name="downloadClient">The SSRF-guarded client for result URLs (<see cref="GeneratedClipUrlGuard.CreateDownloadClient"/>).</param>
    /// <param name="probeModel">The row's model, whose app route the connection test reads.</param>
    public FalAiAudioGenerationClient(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? probeModel = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException(
                "A fal.ai key is required for audio generation; a blank key is refused rather than read from the environment.",
                nameof(apiKey));
        }

        _apiKey = apiKey.Trim();
        _downloadClient = downloadClient ?? httpClient;
        _pollInterval = pollInterval ?? FalQueueClient.DefaultPollInterval;
        _pollTimeout = pollTimeout ?? DefaultPollTimeout;
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _queue = new FalQueueClient(httpClient, _apiKey, _downloadClient, _pollInterval, _pollTimeout, _delay);
        _probeModel = probeModel;
    }

    private static AudioModelCatalog Catalog => AudioModelCatalog.Instance;

    /// <inheritdoc />
    public AudioGenerationCapabilities Describe(string model)
    {
        string id = model?.Trim() ?? string.Empty;
        AudioEndpoint? endpoint = id.Length == 0 ? null : Catalog.Find(InferenceProviderKind.FalAi, id);
        return endpoint?.Capabilities ?? throw new NotSupportedException(
            "'" + id + "' is not a fal.ai audio endpoint this platform has a read schema and a price for, so it cannot be " +
            "priced and will not be used. Music: " + Catalog.IdsFor(InferenceProviderKind.FalAi, AudioGenerationKind.Music) +
            "; sound effects: " + Catalog.IdsFor(InferenceProviderKind.FalAi, AudioGenerationKind.SoundEffect) + ".");
    }

    /// <inheritdoc />
    public async Task<AudioGenerationResult> GenerateAsync(AudioGenerationRequest request, CancellationToken ct,
        Func<string, Task>? onSubmitted = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        AudioEndpoint endpoint = Catalog.Require(InferenceProviderKind.FalAi, request.Kind, request.Model);
        (Dictionary<string, object?> body, string? format) = AudioRequestBody.Build(endpoint, request);
        if (format is not null) body["output_format"] = format;

        FalSubmission submission;
        try
        {
            submission = await _queue.SubmitAsync(endpoint.Id, body, ct);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            int? status = ex.StatusCode is { } code ? (int)code : null;
            throw new AudioGenerationException(
                status is { } s ? AudioGenerationException.Classify(s) : AudioGenerationErrorKind.Retryable,
                Bounded(ex.Message), status, inner: ex);
        }
        catch (InvalidOperationException ex) when (!ct.IsCancellationRequested)
        {
            // Accepted but untrackable (no tracking URLs, or URLs outside the queue origin: S-50). The job may
            // run and bill, so this is never classified as a definite rejection.
            throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed, Bounded(ex.Message), inner: ex);
        }

        // QA-P1-17: the request id is known here, BEFORE the poll and the result download; report it now so a crash
        // in that window still leaves it recorded. The caller persists it on the ledger row.
        if (onSubmitted is not null && !string.IsNullOrEmpty(submission.RequestId))
            await onSubmitted(submission.RequestId);

        JsonElement output = await WaitForOutputAsync(endpoint, submission, ct);
        string url = ReadAudioUrl(output, submission.RequestId);
        byte[] bytes = await DownloadAsync(url, ct);
        (string mime, string extension) = AudioRequestBody.FileTypeOf(endpoint, format);

        return new AudioGenerationResult(bytes, mime, extension, DurationSec: null,
            string.IsNullOrEmpty(submission.RequestId) ? null : submission.RequestId);
    }

    /// <summary>
    /// No-cost provider test: ONE authenticated <c>GET</c> of the status of a random request id on the row's app
    /// route (<c>{base}/{owner}/{app}/requests/{uuid}/status</c>). It submits nothing, so nothing can run or bill.
    /// A <c>404</c> (no such request) proves the route exists and the key got past authentication; a 401/403 is a
    /// rejected key. What fal answers for an unknown id on a valid key is on the must-measure list.
    /// </summary>
    public async Task<AudioGenerationProbeResult> ProbeAsync(CancellationToken ct)
    {
        string id = string.IsNullOrWhiteSpace(_probeModel)
            ? Catalog.DefaultFor(InferenceProviderKind.FalAi, AudioGenerationKind.Music).Id
            : _probeModel.Trim();
        if (Catalog.Find(InferenceProviderKind.FalAi, id) is null)
        {
            return new AudioGenerationProbeResult(false, false,
                "'" + id + "' is not a fal.ai audio endpoint this client has a read schema for, so it would refuse to " +
                "send it a request.");
        }

        string[] segments = id.Split('/');
        string app = segments[0] + "/" + segments[1];
        string baseAddress = _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? DefaultEndpoint;
        string url = baseAddress + "/" + app + "/requests/" + Guid.NewGuid().ToString("D") + "/status";

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Authorization", "Key " + _apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            string text = await BoundedBody.ReadErrorAsync(response.Content, ct);
            int status = (int)response.StatusCode;

            if (status is 401 or 403)
            {
                return new AudioGenerationProbeResult(false, true,
                    "fal.ai rejected the credential (HTTP " + status + "): " + Bounded(text));
            }

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            {
                return new AudioGenerationProbeResult(true, false,
                    "fal.ai answered HTTP " + status + " for a random request id on '" + app + "'; nothing was submitted.");
            }

            return new AudioGenerationProbeResult(false, false,
                "fal.ai answered HTTP " + status + ", which does not establish that the key works: " + Bounded(text));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AudioGenerationProbeResult(false, false,
                "Provider test failed: " + ex.GetType().Name + ": " + Bounded(ex.Message));
        }
    }

    // ------------------------------------------------------------------ the queue

    private async Task<JsonElement> WaitForOutputAsync(AudioEndpoint endpoint, FalSubmission submission, CancellationToken ct)
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
                throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
                    "fal.ai accepted '" + endpoint.Id + "' (" + label + ") but its status could not be read: " +
                    Bounded(ex.Message) + ". The job may still run and bill, so it is not retried.", inner: ex);
            }

            if (status.Status == "COMPLETED")
            {
                if (!string.IsNullOrWhiteSpace(status.Error))
                {
                    throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
                        "fal.ai ran '" + endpoint.Id + "' (" + label + ") and it failed: " +
                        Bounded(FalQueueClient.CleanVendorText(status.Error)));
                }

                break;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
                    "fal.ai did not finish '" + endpoint.Id + "' (" + label + ") within " + _pollTimeout + ". The job may " +
                    "still complete and bill, so it is not retried.");
            }

            await _delay(interval, ct);
            interval = interval + interval > MaxPollInterval ? MaxPollInterval : interval + interval;
        }

        FalResultFetch fetch = await _queue.TryFetchResultAsync(submission.ResponseUrl, label, ct);
        if (fetch.Output is { } output) return output;

        // 400/422 on the result route is the queue validating the input when the job ran: nothing was generated.
        // S-50: never the raw body; on a validation refusal only the vendor's own sentence, cleaned and capped.
        bool refused = fetch.StatusCode is 400 or 422;
        string detail = refused ? Bounded(FalQueueClient.VendorMessage(fetch.Body)) : string.Empty;
        throw new AudioGenerationException(
            refused ? AudioGenerationErrorKind.UserInput : AudioGenerationErrorKind.VendorFailed,
            "fal.ai answered HTTP " + fetch.StatusCode + " fetching the result of '" + endpoint.Id + "'" +
            (detail.Length > 0 ? ": " + detail : "."),
            fetch.StatusCode);
    }

    private string ReadAudioUrl(JsonElement output, string requestId)
    {
        if (output.ValueKind == JsonValueKind.Object
            && output.TryGetProperty("audio", out JsonElement audio)
            && audio.ValueKind == JsonValueKind.Object
            && FalQueueClient.ReadString(audio, "url") is { Length: > 0 } url)
        {
            return url;
        }

        throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
            "fal.ai completed '" + requestId + "' but returned no 'audio.url'. A 200 carrying no audio is not a success.");
    }

    /// <summary>
    /// Downloads the result. The URL comes out of a vendor response, so it is untrusted: https only, no credentials,
    /// no loopback/private/link-local literal host (and the guarded client re-checks the address it dials); the
    /// answer must be an audio or binary type and at most <see cref="MaxDownloadBytes"/>. No Authorization header.
    /// </summary>
    private async Task<byte[]> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            GeneratedClipUrlGuard.EnsureAllowed(url);
        }
        catch (InvalidOperationException ex)
        {
            throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
                "fal.ai returned an audio URL that is not safe to fetch: " + ex.Message, inner: ex);
        }

        using HttpResponseMessage response = await _downloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
                "Downloading the generated audio answered HTTP " + (int)response.StatusCode + ".", (int)response.StatusCode);
        }

        return await AudioPayload.ReadAsync(response, MaxDownloadBytes, "the generated audio", ct);
    }

    private string Bounded(string text)
    {
        text = text.Replace(_apiKey, "***", StringComparison.Ordinal);
        return text.Length <= MaxVendorMessageChars ? text : text[..MaxVendorMessageChars] + "…";
    }
}

/// <summary>Reads an audio response body with a content-type check and a hard size cap.</summary>
internal static class AudioPayload
{
    /// <summary>True for a content type an audio file may arrive with (or none at all).</summary>
    public static bool IsAudioType(string? mediaType) =>
        mediaType is null
        || mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
        || mediaType.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase);

    /// <exception cref="AudioGenerationException">Not audio, over the cap, or empty (<see cref="AudioGenerationErrorKind.VendorFailed"/>).</exception>
    public static async Task<byte[]> ReadAsync(HttpResponseMessage response, long maxBytes, string what, CancellationToken ct)
    {
        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!IsAudioType(mediaType))
        {
            throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
                "Unexpected content type '" + mediaType + "' for " + what + ".", (int)response.StatusCode);
        }

        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
                what + " is " + response.Content.Headers.ContentLength + " bytes, over the " + maxBytes + "-byte limit.");
        }

        Stream inner = await response.Content.ReadAsStreamAsync(ct);
        await using LimitedReadStream limited = new(inner, maxBytes, inner);
        using MemoryStream buffer = new();
        try
        {
            await limited.CopyToAsync(buffer, ct);
        }
        catch (StreamLimitExceededException ex)
        {
            throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed, ex.Message, inner: ex);
        }

        if (buffer.Length == 0)
        {
            throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed, what + " was empty.");
        }

        return buffer.ToArray();
    }
}
