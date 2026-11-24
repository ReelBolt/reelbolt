using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// ElevenLabs, called directly (<see cref="InferenceProviderKind.ElevenLabs"/>): <c>POST /v1/music</c> and
/// <c>POST /v1/sound-generation</c> on <c>https://api.elevenlabs.io</c> with the <c>xi-api-key</c> header. Plain
/// <see cref="HttpClient"/>; both answer the raw audio bytes synchronously.
/// </summary>
/// <remarks>
/// <para>
/// <b>Wire facts</b> (research/audio-vendors.md B, the vendor's OpenAPI read 2026-10-17): <c>output_format</c> is a
/// QUERY parameter, not a body field; <c>model_id</c> is ALWAYS sent, because the API's own default is the
/// deprecated <c>music_v1</c>; <c>prompt</c> and <c>composition_plan</c> are mutually exclusive, and
/// <c>music_length_ms</c>/<c>force_instrumental</c> apply to a prompt only. No answer carries a duration, so the
/// engine probes the file.
/// </para>
/// <para>
/// <b>Errors</b> are classified (<see cref="AudioGenerationException"/>): 401/403 configuration, 402 vendor quota,
/// 400/422 (including <c>bad_prompt</c> and <c>bad_composition_plan</c>, whose suggested rewrite is carried in
/// <see cref="AudioGenerationException.Suggestion"/>) user input, 429 (<c>concurrent_limit_exceeded</c>,
/// <c>rate_limit_exceeded</c>, legacy <c>too_many_concurrent_requests</c>/<c>system_busy</c>) and 5xx retryable.
/// </para>
/// <para>
/// <b>Key.</b> The stored key is sent verbatim, except the literal admin-typed sentinel <c>env:</c>, which reads
/// <c>ELEVENLABS_API_KEY</c>. A blank key, or the sentinel with no variable set, THROWS: nothing falls back to the
/// ambient environment.
/// </para>
/// </remarks>
public sealed class ElevenLabsAudioGenerationClient : IAudioGenerationClient
{
    /// <summary>The vendor host. A provider row's endpoint overrides it (the docs mention regional servers).</summary>
    public const string DefaultEndpoint = "https://api.elevenlabs.io";

    /// <summary>The explicit, admin-typed "read the key from the environment" sentinel.</summary>
    public const string EnvironmentSentinel = "env:";

    /// <summary>The one variable the sentinel reads (the vendor's own documented name).</summary>
    public const string EnvironmentVariable = "ELEVENLABS_API_KEY";

    /// <summary>Hard ceiling on one answer (five minutes of 192 kbps MP3 is about 7 MB).</summary>
    public const long MaxAudioBytes = 64L * 1024 * 1024;

    private const string ModelsPath = "/v1/models";
    private const int MaxVendorMessageChars = 400;
    private static readonly string[] RateLimitWords =
        ["rate_limit_exceeded", "concurrent_limit_exceeded", "too_many_concurrent_requests", "system_busy"];

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    /// <param name="httpClient">The transport; its base address is the row's endpoint or <see cref="DefaultEndpoint"/>.</param>
    /// <param name="storedKey">The saved key, or <see cref="EnvironmentSentinel"/>.</param>
    /// <param name="environmentLookup">A test seam for the sentinel; production passes none.</param>
    public ElevenLabsAudioGenerationClient(HttpClient httpClient, string storedKey, Func<string, string?>? environmentLookup = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = ResolveApiKey(storedKey, environmentLookup);
    }

    /// <summary>The key to send: the stored value, or <c>ELEVENLABS_API_KEY</c> for the <c>env:</c> sentinel.</summary>
    /// <exception cref="ArgumentException">Blank, or the sentinel with no value. Never echoes the key.</exception>
    public static string ResolveApiKey(string? storedKey, Func<string, string?>? environmentLookup = null)
    {
        if (string.IsNullOrWhiteSpace(storedKey))
        {
            throw new ArgumentException(
                "An ElevenLabs key is required. Save the key itself, or the literal 'env:' to read " + EnvironmentVariable +
                ". No environment variable is read for a blank key.", nameof(storedKey));
        }

        string trimmed = storedKey.Trim();
        if (!string.Equals(trimmed, EnvironmentSentinel, StringComparison.OrdinalIgnoreCase)) return trimmed;

        string? fromEnvironment = (environmentLookup ?? Environment.GetEnvironmentVariable)(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(fromEnvironment))
        {
            throw new ArgumentException(
                "The stored key is the 'env:' sentinel, but " + EnvironmentVariable + " is not set.", nameof(storedKey));
        }

        return fromEnvironment.Trim();
    }

    private static AudioModelCatalog Catalog => AudioModelCatalog.Instance;

    /// <inheritdoc />
    public AudioGenerationCapabilities Describe(string model)
    {
        string id = model?.Trim() ?? string.Empty;
        AudioEndpoint? endpoint = id.Length == 0 ? null : Catalog.Find(InferenceProviderKind.ElevenLabs, id);
        return endpoint?.Capabilities ?? throw new NotSupportedException(
            "'" + id + "' is not an ElevenLabs model this platform has a read schema and a price for, so it cannot be " +
            "priced and will not be used. Music: " + Catalog.IdsFor(InferenceProviderKind.ElevenLabs, AudioGenerationKind.Music) +
            "; sound effects: " + Catalog.IdsFor(InferenceProviderKind.ElevenLabs, AudioGenerationKind.SoundEffect) + ".");
    }

    /// <inheritdoc />
    /// <remarks>QA-P1-17: this client's one call is also its result (no submit/poll split), so it never invokes
    /// <paramref name="onSubmitted"/> — there is no window between a submit and a download to protect.</remarks>
    public async Task<AudioGenerationResult> GenerateAsync(AudioGenerationRequest request, CancellationToken ct,
        Func<string, Task>? onSubmitted = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        AudioEndpoint endpoint = Catalog.Require(InferenceProviderKind.ElevenLabs, request.Kind, request.Model);
        (Dictionary<string, object?> body, string? format) = AudioRequestBody.Build(endpoint, request);
        body["model_id"] = endpoint.Id;
        string outputFormat = format ?? AudioOutputFormats.Default;

        using HttpRequestMessage message = new(HttpMethod.Post,
            endpoint.Path + "?output_format=" + Uri.EscapeDataString(outputFormat));
        message.Headers.TryAddWithoutValidation("xi-api-key", _apiKey);
        message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
        {
            throw new AudioGenerationException(AudioGenerationErrorKind.Retryable,
                "ElevenLabs could not be reached: " + Bounded(ex.Message), inner: ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string text = await BoundedBody.ReadErrorAsync(response.Content, ct);
                throw Classify((int)response.StatusCode, text);
            }

            byte[] bytes = await AudioPayload.ReadAsync(response, MaxAudioBytes, "the ElevenLabs answer", ct);
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            {
                // The success body is documented as raw audio with an audio/* type; a 200 without one is not audio.
                throw new AudioGenerationException(AudioGenerationErrorKind.VendorFailed,
                    "ElevenLabs answered HTTP 200 with content type '" + mediaType + "' instead of audio.", 200);
            }

            (string mime, string extension) = AudioRequestBody.FileTypeOf(endpoint, outputFormat);
            string? vendorId = Header(response, "song-id") ?? Header(response, "request-id");
            return new AudioGenerationResult(bytes, mime, extension, DurationSec: null, vendorId);
        }
    }

    /// <summary>
    /// No-cost provider test: one <c>GET /v1/models</c> (free, generates nothing). 2xx proves the host and key;
    /// 401/403 is a rejected key.
    /// </summary>
    public async Task<AudioGenerationProbeResult> ProbeAsync(CancellationToken ct)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, ModelsPath);
            request.Headers.TryAddWithoutValidation("xi-api-key", _apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            int status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                return new AudioGenerationProbeResult(true, false, "GET /v1/models answered HTTP " + status + ".");
            }

            string text = await BoundedBody.ReadErrorAsync(response.Content, ct);
            return status is 401 or 403
                ? new AudioGenerationProbeResult(false, true, "ElevenLabs rejected the key (HTTP " + status + "): " + Bounded(text))
                : new AudioGenerationProbeResult(false, false, "ElevenLabs answered HTTP " + status + ": " + Bounded(text));
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

    // ------------------------------------------------------------------ errors

    /// <summary>
    /// Reads the vendor's error envelope — <c>{"detail": {type, code, message, status, data}}</c>, or the
    /// validation shape <c>{"detail": [{loc, msg, type}]}</c>, or a bare string — and classifies it.
    /// </summary>
    private AudioGenerationException Classify(int status, string text)
    {
        string? vendorStatus = null, code = null, detailMessage = null, suggestion = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("detail", out JsonElement detail))
            {
                switch (detail.ValueKind)
                {
                    case JsonValueKind.Object:
                        vendorStatus = FalQueueClient.ReadString(detail, "status");
                        code = FalQueueClient.ReadString(detail, "code") ?? FalQueueClient.ReadString(detail, "type");
                        detailMessage = FalQueueClient.ReadString(detail, "message");
                        if (detail.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
                        {
                            if (data.TryGetProperty("prompt_suggestion", out JsonElement prompt) && prompt.ValueKind == JsonValueKind.String)
                                suggestion = prompt.GetString();
                            else if (data.TryGetProperty("composition_plan_suggestion", out JsonElement plan)
                                     && plan.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                                suggestion = plan.GetRawText();
                        }

                        break;

                    case JsonValueKind.Array:
                        detailMessage = string.Join("; ", detail.EnumerateArray().Select(item =>
                        {
                            string loc = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("loc", out JsonElement l)
                                         && l.ValueKind == JsonValueKind.Array
                                ? string.Join(".", l.EnumerateArray().Select(p => p.ToString()))
                                : string.Empty;
                            string msg = FalQueueClient.ReadString(item, "msg") ?? item.ToString();
                            return loc.Length == 0 ? msg : loc + ": " + msg;
                        }));
                        break;

                    case JsonValueKind.String:
                        detailMessage = detail.GetString();
                        break;
                }
            }
        }
        catch (JsonException)
        {
            detailMessage = text;
        }

        bool contentRefusal = vendorStatus is "bad_prompt" or "bad_composition_plan";
        bool rateLimited = status == 429
                           || RateLimitWords.Contains(code, StringComparer.Ordinal)
                           || RateLimitWords.Contains(vendorStatus, StringComparer.Ordinal);

        AudioGenerationErrorKind kind = contentRefusal ? AudioGenerationErrorKind.UserInput
            : rateLimited ? AudioGenerationErrorKind.Retryable
            : AudioGenerationException.Classify(status);

        string label = string.Join("/", new[] { vendorStatus, code }.Where(s => !string.IsNullOrEmpty(s)).Distinct());
        string message = "ElevenLabs answered HTTP " + status + (label.Length > 0 ? " (" + label + ")" : string.Empty) + ": " +
                         (string.IsNullOrWhiteSpace(detailMessage) ? "no detail" : detailMessage) +
                         (suggestion is null ? string.Empty : ". The vendor suggests: " + suggestion);
        return new AudioGenerationException(kind, Bounded(message), status, suggestion);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault() : null;

    private string Bounded(string text)
    {
        text = text.Replace(_apiKey, "***", StringComparison.Ordinal).ReplaceLineEndings(" ");
        return text.Length <= MaxVendorMessageChars ? text : text[..MaxVendorMessageChars] + "…";
    }
}
