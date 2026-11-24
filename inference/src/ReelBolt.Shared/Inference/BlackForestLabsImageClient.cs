using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Black Forest Labs (FLUX) image client. Asynchronous by design: a submission returns
/// <c>{ id, polling_url }</c> and the result is polled from that exact URL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from BFL's own documentation on 2026-10-09.</b> The submit/poll contract is
/// <c>docs.bfl.ml/quick_start/generating_images</c>; the edit fields are
/// <c>docs.bfl.ml/flux_2/flux2_image_editing</c>; the inpaint contract is
/// <c>docs.bfl.ml/flux_1_fill</c>; the prices are <c>docs.bfl.ml/quick_start/pricing</c>. The facts
/// that shape this client:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Authentication is an <c>x-key</c> header</b>, not <c>Authorization: Bearer</c>.
/// </description></item>
/// <item><description>
/// <b>The model is the URL PATH</b>: <c>POST https://api.bfl.ai/v1/flux-2-pro-preview</c>. That is
/// why <c>ModelName</c> on a BFL provider row is the endpoint slug (<c>flux-2-pro</c>,
/// <c>flux-kontext-pro</c>, <c>flux-pro-1.0-fill</c>) rather than a body field.
/// </description></item>
/// <item><description>
/// <b>Submission answers <c>{ id, polling_url }</c> and the result is polled from the returned
/// <c>polling_url</c></b> — which the documentation insists on for the global endpoint
/// ("you must use the <c>polling_url</c> returned in the response"), because the global host routes
/// across clusters and a hand-built URL would poll the wrong one.
/// </description></item>
/// <item><description>
/// Poll answers carry <c>status</c> and, when ready, <c>result.sample</c> — "a signed URL for
/// retrieval" that is "only valid for 10 minutes" and is <b>not CORS-enabled</b>, served from
/// <c>delivery.*.bfl.ai</c>. BFL's own advice is to "download the image and re-serve it from your
/// own infrastructure", which is exactly what this client does: <see cref="GeneratedImage.Bytes"/>
/// is always populated and no caller ever sees the signed URL as a delivery mechanism.
/// </description></item>
/// <item><description>
/// Editing takes <c>input_image</c> (and <c>input_image_2</c> … up to ten references) as a URL.
/// Inpainting is a DIFFERENT model and a different body: <c>flux-pro-1.0-fill</c> takes
/// <c>image</c> and <c>mask</c> as BASE64-ENCODED image data (not URLs), with the documented
/// convention that "black areas will be preserved while white areas will be inpainted".
/// </description></item>
/// <item><description>
/// Limits stated by BFL: 24 active tasks per account (6 for <c>flux-kontext-max</c>), answered with
/// <c>429</c>; <c>402</c> when credits are exhausted.
/// </description></item>
/// </list>
/// <para>
/// <b>Data handling, stated because it is a product decision and not a technical one.</b> BFL's FLUX
/// API Service Terms grant BFL a perpetual, irrevocable, sublicensable licence over Inputs and
/// Outputs and state that it may use them to train its models. The owner accepted that with
/// disclosure; the disclosure lives in <c>site/lib/provider-data-handling.ts</c> and drives the
/// Terms and Privacy sub-processor sections. This class does not add a control it cannot enforce,
/// and this comment does not claim one.
/// </para>
/// </remarks>
public class BlackForestLabsImageClient : IImageClient, IBillingModelKeyed
{
    /// <summary>BFL's public API host. A provider row's endpoint overrides it.</summary>
    public const string DefaultEndpoint = "https://api.bfl.ai";

    /// <summary>
    /// The model slug for mask-based inpainting. BFL's Fill model takes a mask; the FLUX.2 and
    /// Kontext models do not.
    /// </summary>
    public const string FillModel = "flux-pro-1.0-fill";

    /// <summary>
    /// The models whose <c>Describe</c> reports mask inpainting, and the reason the list is explicit
    /// rather than a name heuristic: only the Fill model's page documents <c>image</c> + <c>mask</c>,
    /// and a heuristic like "contains 'fill'" would claim the capability for a future model that does
    /// not have it.
    /// </summary>
    private static readonly HashSet<string> MaskCapableModels = new(StringComparer.OrdinalIgnoreCase)
    {
        FillModel
    };

    /// <summary>The model name every probe asks for; deliberately not a real FLUX slug.</summary>
    private const string ProbeModel = "__reelbolt_probe__";

    /// <summary>How long the poll loop will wait for a job before giving up.</summary>
    public static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromMinutes(3);

    /// <summary>The wait between polls, doubling up to a cap.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromSeconds(4);

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly HttpClient _downloadClient;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _pollTimeout;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public BlackForestLabsImageClient(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _downloadClient = downloadClient ?? httpClient;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _pollTimeout = pollTimeout ?? DefaultPollTimeout;

        // Injectable so a test can drive the poll loop deterministically instead of sleeping; the
        // shipped default is the real clock.
        _delay = delay ?? ((duration, ct) => Task.Delay(duration, ct));
    }

    public ImageModelCapabilities Describe(string model) => new(
        Model: model,
        SupportsInstructionEdit: true,
        SupportsMaskInpaint: MaskCapableModels.Contains(model),
        MaxImagesPerRequest: 1,
        MaxPromptLength: GeneratedPromptSanitizer.DefaultMaxPromptLength,
        Basis: "Black Forest Labs documentation, read 2026-10-09: docs.bfl.ml/quick_start/generating_images " +
               "(submit returns {id, polling_url}; poll status Ready/Error/Failed; result.sample is a signed URL " +
               "valid 10 minutes and not CORS-enabled; 24 active tasks, 429; 402 when out of credits), " +
               "docs.bfl.ml/flux_2/flux2_image_editing (input_image as a URL), and docs.bfl.ml/flux_1_fill " +
               "(flux-pro-1.0-fill takes base64 'image' and 'mask'; black is preserved, white is inpainted). " +
               "BFL documents no maximum images per request — batch is a separate concept — so this reports 1 " +
               "rather than guessing a batch size.");

    // ------------------------------------------------------------------ IBillingModelKeyed

    /// <summary>BFL prices by model and size, never by a tier parameter, so the model is the key.</summary>
    public string BillingModelKey(string model, IReadOnlyDictionary<string, JsonElement>? parameters) => model;

    /// <summary>FLUX.2 text-to-image only (<see cref="Metering.OutputSizePricing.PricesByOutputMegapixels"/>).</summary>
    public bool PricesByOutputMegapixels(string model, bool editsASource, int referenceImages) =>
        Metering.OutputSizePricing.PricesByOutputMegapixels(InferenceProviderKind.BlackForestLabs, model, editsASource, referenceImages);

    /// <inheritdoc />
    public long? MaxOutputPixels(string model) =>
        Metering.OutputSizePricing.MaxOutputPixels(InferenceProviderKind.BlackForestLabs, model);

    public async Task<GeneratedImageSet> GenerateAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Model, request.Prompt);

        Dictionary<string, object?> body = new() { ["prompt"] = request.Prompt };
        if (request.Width is { } w) body["width"] = w;
        if (request.Height is { } h) body["height"] = h;

        // A text-to-image call under the Fill model is legal (BFL documents it: prompt without a
        // mask extends the canvas), so no special case is needed here. A reference image, when
        // given, is the same field the edit path uses.
        if (!string.IsNullOrWhiteSpace(request.ReferenceImageUrl)) body["input_image"] = request.ReferenceImageUrl;

        if (request.Count != 1)
        {
            throw new ArgumentOutOfRangeException(
                "Count", request.Count,
                "BFL's submission endpoint produces one image per request; batching is a separate API " +
                "concept it documents on its pricing page, not an 'n' field.");
        }

        return await SubmitAndPollAsync(request.Model, body, ct);
    }

    public async Task<GeneratedImageSet> EditAsync(ImageEditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Model, request.Prompt);
        RequireSource(request.SourceImageUrl);

        if (MaskCapableModels.Contains(request.Model))
        {
            // The Fill model takes the source as BASE64 'image', not as 'input_image'. Sending the
            // edit field here would be silently ignored by the vendor, so the wrong model is
            // refused instead of producing a plain text-to-image result the caller did not ask for.
            throw new NotSupportedException(
                $"'{request.Model}' is BFL's inpainting endpoint and takes 'image' plus 'mask' as base64, " +
                "not an 'input_image' instruction edit. Use InpaintAsync for it, or configure a " +
                "non-Fill model for instruction editing.");
        }

        Dictionary<string, object?> body = new()
        {
            ["prompt"] = request.Prompt,
            ["input_image"] = request.SourceImageUrl
        };

        return await SubmitAndPollAsync(request.Model, body, ct);
    }

    public async Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MaskBytes);
        Validate(request.Model, request.Prompt);
        RequireSource(request.SourceImageUrl);

        if (!MaskCapableModels.Contains(request.Model))
        {
            throw new NotSupportedException(
                $"BFL does not document mask inpainting for '{request.Model}'. The only BFL model whose " +
                $"documentation defines 'image' + 'mask' is '{FillModel}' (docs.bfl.ml/flux_1_fill), so " +
                "configure that model on the provider row to inpaint.");
        }

        if (request.MaskBytes.Length == 0)
        {
            throw new ArgumentException("The mask is empty.", nameof(request));
        }

        // The source has to become bytes: the Fill body takes base64, not a URL. It is fetched
        // through the guarded download client rather than accepted inline, so the engine has exactly
        // one place where it dereferences a caller-supplied URL.
        GeneratedImage source = await ImagePayload.DownloadAsync(_downloadClient, request.SourceImageUrl, ct);

        Dictionary<string, object?> body = new()
        {
            ["prompt"] = request.Prompt,
            ["image"] = Convert.ToBase64String(source.Bytes),
            ["mask"] = Convert.ToBase64String(request.MaskBytes),
            ["output_format"] = request.MaskContentType.Contains("png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg"
        };

        return await SubmitAndPollAsync(request.Model, body, ct);
    }

    public Task<GeneratedImageSet> TransformAsync(ImageTransformRequest request, CancellationToken ct) =>
        throw new NotSupportedException(
            $"Operation {request.Operation} is not available on this provider kind. BFL documents text-to-image, Kontext editing and Fill inpainting (docs.bfl.ml); it publishes no dedicated outpainting, background-replacement, relighting or upscaling endpoint, so these operations are refused rather than approximated through Fill.");

    public async Task<ImageProbeResult> ProbeAsync(CancellationToken ct)
    {
        // The probe POSTs an empty body to a model slug no account can have. It cannot generate
        // anything, so it cannot cost anything. What the answer proves is stated honestly: a
        // 401/403, or a 4xx whose body names an authorization problem, is a credential failure; a
        // validation failure (400/404/422) that is NOT an authorization error proves the endpoint
        // was reached and the request was processed rather than refused at the door. Anything else
        // — including a 2xx, which would mean BFL accepted a nonsense slug — is reported as not ok.
        string baseAddress = BaseAddress();
        HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{baseAddress}/v1/{Uri.EscapeDataString(ProbeModel)}"))
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("x-key", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using (request)
            using (HttpResponseMessage response = await _httpClient.SendAsync(request, ct))
            {
                string text = await response.Content.ReadAsStringAsync(ct);
                int status = (int)response.StatusCode;

                if (status is 401 or 403)
                {
                    return new ImageProbeResult(false,
                        $"Black Forest Labs rejected the credential (HTTP {status}): {ImagePayload.Bounded(text)}");
                }

                if (response.IsSuccessStatusCode)
                {
                    return new ImageProbeResult(false,
                        "Black Forest Labs accepted a request for a deliberately nonexistent model, which " +
                        "proves nothing about whether real requests will work. Verify the model slug by hand.");
                }

                if (LooksLikeAnAuthError(text))
                {
                    return new ImageProbeResult(false,
                        $"Black Forest Labs answered HTTP {status} with an authorization problem: {ImagePayload.Bounded(text)}");
                }

                if (status is 400 or 404 or 422)
                {
                    return new ImageProbeResult(true, null);
                }

                string note = status == 429
                    ? " (BFL limits accounts to 24 active tasks and answers 429 when that is reached)"
                    : status == 402
                        ? " (402 is BFL's out-of-credits answer)"
                        : string.Empty;
                return new ImageProbeResult(false,
                    $"Black Forest Labs answered HTTP {status}{note}, which does not establish that the key " +
                    $"was accepted: {ImagePayload.Bounded(text)}");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ImageProbeResult(false, $"Provider test failed: {ex.GetType().Name}: {ImagePayload.Bounded(ex.Message)}");
        }
    }

    // ------------------------------------------------------------------ internals

    private string BaseAddress() => _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? DefaultEndpoint;

    private static void Validate(string model, string prompt)
    {
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model slug is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("A prompt is required.", nameof(prompt));
    }

    private static void RequireSource(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException(
                "A source image URL is required: BFL takes 'input_image' as a URL the vendor fetches itself.",
                nameof(url));
        }
    }

    private static bool LooksLikeAnAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        string lowered = body.ToLowerInvariant();
        return lowered.Contains("unauthor")
            || lowered.Contains("invalid api key")
            || lowered.Contains("invalid_api_key")
            || lowered.Contains("authentication")
            || lowered.Contains("x-key")
            || lowered.Contains("api key");
    }

    /// <summary>
    /// Submits, then polls the returned <c>polling_url</c> until the job settles or the poll budget
    /// runs out.
    /// </summary>
    /// <remarks>
    /// A poll budget that expires is a THROW, not a success with no image: the job may still be
    /// running and may still be billed. The same reasoning as
    /// <c>MiniMaxVideoGenerationClient</c>'s retry-is-the-re-poll note — the caller sees a failure
    /// and a retry re-polls rather than a silent empty result.
    /// </remarks>
    private async Task<GeneratedImageSet> SubmitAndPollAsync(string model, Dictionary<string, object?> body, CancellationToken ct)
    {
        string baseAddress = BaseAddress();
        using HttpRequestMessage submit = new(HttpMethod.Post, new Uri($"{baseAddress}/v1/{Uri.EscapeDataString(model)}"))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        submit.Headers.TryAddWithoutValidation("x-key", _apiKey);
        submit.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        string requestId;
        string pollingUrl;
        using (HttpResponseMessage submitResponse = await _httpClient.SendAsync(submit, ct))
        {
            string text = await submitResponse.Content.ReadAsStringAsync(ct);
            if (!submitResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Black Forest Labs returned HTTP {(int)submitResponse.StatusCode} submitting '{model}': " +
                    ImagePayload.Bounded(text));
            }

            using JsonDocument document = JsonDocument.Parse(text);
            requestId = document.RootElement.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() ?? string.Empty
                : string.Empty;
            pollingUrl = document.RootElement.TryGetProperty("polling_url", out JsonElement poll) && poll.ValueKind == JsonValueKind.String
                ? poll.GetString() ?? string.Empty
                : string.Empty;
        }

        if (string.IsNullOrWhiteSpace(pollingUrl))
        {
            throw new InvalidOperationException(
                "Black Forest Labs accepted the submission but returned no polling_url, so the result cannot " +
                $"be retrieved. Request id: {(string.IsNullOrEmpty(requestId) ? "(none)" : requestId)}.");
        }

        // The polling URL is absolute and vendor-supplied. It is NOT run through the SSRF guard's
        // syntactic check the way a download URL is: BFL's global endpoint explicitly routes across
        // clusters, and its own documentation says the returned polling_url is the only correct one
        // to use. The guard still applies to the RESULT URL, which is where the bytes come from.
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _pollTimeout;
        TimeSpan interval = _pollInterval;

        while (true)
        {
            using HttpRequestMessage poll = new(HttpMethod.Get, pollingUrl);
            poll.Headers.TryAddWithoutValidation("x-key", _apiKey);
            poll.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage pollResponse = await _httpClient.SendAsync(poll, ct);
            string text = await pollResponse.Content.ReadAsStringAsync(ct);

            if (!pollResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Black Forest Labs returned HTTP {(int)pollResponse.StatusCode} while polling: " +
                    ImagePayload.Bounded(text));
            }

            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            string status = root.TryGetProperty("status", out JsonElement statusElement) && statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString() ?? string.Empty
                : string.Empty;

            switch (status)
            {
                case "Ready":
                    {
                        if (!root.TryGetProperty("result", out JsonElement result)
                            || !result.TryGetProperty("sample", out JsonElement sample)
                            || sample.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(sample.GetString()))
                        {
                            throw new InvalidOperationException(
                                "Black Forest Labs reported the job Ready but returned no result.sample URL.");
                        }

                        GeneratedImage image = await ImagePayload.DownloadAsync(_downloadClient, sample.GetString()!, ct);
                        return new GeneratedImageSet([image], string.IsNullOrEmpty(requestId) ? null : requestId);
                    }

                case "Error":
                case "Failed":
                case "Task not found":
                case "Content Moderated":
                case "Request Moderated":
                    throw new InvalidOperationException(
                        $"Black Forest Labs failed the job (status '{status}'): {ImagePayload.Bounded(text)}");

                case "Pending":
                case "Queued":
                case "Processing":
                case "Generating":
                    break;

                default:
                    // A status we do not recognise is surfaced loudly rather than treated as pending:
                    // polling forever on an unknown state would burn the poll budget and then report a
                    // timeout, hiding the one fact that explains it.
                    throw new NotSupportedException(
                        $"Black Forest Labs returned an unrecognised job status '{status}': {ImagePayload.Bounded(text)}");
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Black Forest Labs did not finish job {(string.IsNullOrEmpty(requestId) ? "(no id)" : requestId)} " +
                    $"within {_pollTimeout}. It may still be running and may still be billed; retrying re-polls it.");
            }

            await _delay(interval, ct);
            interval = interval + interval > MaxPollInterval ? MaxPollInterval : interval + interval;
        }
    }
}
