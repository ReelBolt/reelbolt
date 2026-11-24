using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// MiniMax image client over <c>POST /v1/image_generation</c> on <c>api.minimax.io</c>, model
/// <c>image-01</c>, plain <see cref="HttpClient"/> + <c>System.Text.Json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every field below was read from MiniMax's own documentation on 2026-10-09</b>:
/// the OpenAPI document behind
/// <c>platform.minimax.io/docs/api-reference/image-generation-t2i</c> (request schema
/// <c>ImageGenerationReq</c>, response schema <c>ImageGenerationResp</c>, error codes on
/// <c>BaseResp.status_code</c>) and the guide at
/// <c>platform.minimax.io/docs/guides/image-generation</c> (the <c>subject_reference</c> example).
/// Nothing here is inferred from a sibling API.
/// </para>
/// <para>
/// <b>What MiniMax documents:</b> <c>model</c> (the only enum value is <c>image-01</c>),
/// <c>prompt</c> (max 1500 characters), <c>aspect_ratio</c> (eight values, <c>1:1</c> default),
/// <c>width</c>/<c>height</c> (512-2048, divisible by 8, only for <c>image-01</c>, and
/// <c>aspect_ratio</c> wins if both are sent), <c>response_format</c> (<c>url</c> or <c>base64</c>,
/// default <c>url</c>, whose links "expire in 24 hours"), <c>seed</c>, <c>n</c> (1-9, default 1),
/// <c>prompt_optimizer</c>, and <c>subject_reference</c> — an array of
/// <c>{type: "character", image_file: "&lt;url&gt;"}</c> where "<b>only a single reference image is
/// supported per request</b>".
/// </para>
/// <para>
/// <b>What MiniMax does NOT document, and this client therefore refuses:</b> any mask parameter.
/// There is no <c>mask</c> field on <c>ImageGenerationReq</c>, so <see cref="InpaintAsync"/> throws
/// rather than sending a mask MiniMax would ignore — an ignored mask would silently return an edit
/// of the whole image and the caller would have no way to tell.
/// </para>
/// </remarks>
public class MiniMaxImageClient : IImageClient
{
    /// <summary>The one model MiniMax documents for this endpoint.</summary>
    public const string Image01 = "image-01";

    /// <summary>
    /// The model name every probe sends. Deliberately not a real model: the probe's whole design is
    /// that the request cannot succeed, so it cannot generate, so it cannot cost anything.
    /// </summary>
    private const string ProbeModel = "__reelbolt_probe__";

    /// <summary>MiniMax's documented prompt cap for <c>image-01</c>, in characters.</summary>
    public const int MaxPromptLength = 1500;

    /// <summary>MiniMax's documented <c>n</c> range.</summary>
    public const int MaxImagesPerRequest = 9;

    private const string GenerationPath = "/v1/image_generation";

    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// MiniMax's documented <c>base_resp.status_code</c> values that mean the key was NOT accepted.
    /// 1004 is "Account authentication failed, please check if the API Key is correct" and 2049 is
    /// "Invalid API Key"; 1008 is "Insufficient account balance", which is a credential problem of a
    /// different kind and is reported as its own sentence so an operator tops up rather than rotating
    /// a working key.
    /// </summary>
    private static readonly Dictionary<int, string> StatusCodeMessages = new()
    {
        [0] = "Request successful",
        [1002] = "rate limit triggered",
        [1004] = "account authentication failed (check the API key)",
        [1008] = "insufficient account balance",
        [1026] = "sensitive content detected in the prompt",
        [2013] = "invalid input parameters",
        [2049] = "invalid API key"
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly HttpClient _downloadClient;

    public MiniMaxImageClient(HttpClient httpClient, string apiKey, HttpClient? downloadClient = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _downloadClient = downloadClient ?? httpClient;
    }

    public ImageModelCapabilities Describe(string model) => new(
        Model: model,
        SupportsInstructionEdit: true,
        SupportsMaskInpaint: false,
        MaxImagesPerRequest: MaxImagesPerRequest,
        MaxPromptLength: MaxPromptLength,
        Basis: "MiniMax image generation API reference (platform.minimax.io/docs/api-reference/" +
               "image-generation-t2i, OpenAPI schema ImageGenerationReq) and the Image Generation guide " +
               "(platform.minimax.io/docs/guides/image-generation), read 2026-10-09: model image-01, prompt " +
               "max 1500 characters, n 1-9, aspect_ratio with eight values, response_format url|base64 with " +
               "url links expiring in 24 hours, and subject_reference (type=character, image_file=url), of " +
               "which only ONE reference image is supported per request. No mask parameter is documented, " +
               "so mask inpainting is reported unsupported rather than assumed.");

    public Task<GeneratedImageSet> GenerateAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Model, request.Prompt, request.Count);

        Dictionary<string, object?> body = new()
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.Count,
            // Always url, never base64: the response envelope stays small and the bytes are fetched
            // once through the guarded download client either way. The 24-hour expiry MiniMax
            // documents is irrelevant here because the fetch happens inside this call.
            ["response_format"] = "url"
        };

        ApplySize(body, request.AspectRatio, request.Width, request.Height);
        if (request.Seed is { } seed) body["seed"] = seed;
        if (!string.IsNullOrWhiteSpace(request.ReferenceImageUrl))
        {
            body["subject_reference"] = new[] { new { type = "character", image_file = request.ReferenceImageUrl } };
        }

        return SendAsync(body, ct);
    }

    public Task<GeneratedImageSet> EditAsync(ImageEditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Model, request.Prompt, count: 1);

        if (string.IsNullOrWhiteSpace(request.SourceImageUrl))
        {
            throw new ArgumentException(
                "MiniMax's image-to-image path is subject_reference, which takes a URL the vendor " +
                "fetches itself, so a source image URL is required.", nameof(request));
        }

        Dictionary<string, object?> body = new()
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = 1,
            ["response_format"] = "url",
            ["subject_reference"] = new[] { new { type = "character", image_file = request.SourceImageUrl } }
        };

        ApplySize(body, request.AspectRatio, width: null, height: null);
        if (request.Seed is { } seed) body["seed"] = seed;

        return SendAsync(body, ct);
    }

    public Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest request, CancellationToken ct) =>
        throw new NotSupportedException(
            "MiniMax does not document mask-based inpainting: the image generation API reference " +
            "(platform.minimax.io/docs/api-reference/image-generation-t2i) defines no mask field on " +
            "ImageGenerationReq, and the only image-to-image path it documents is subject_reference, " +
            "which preserves a subject's characteristics rather than regenerating a masked region. " +
            "Configure an ImageEdit provider of kind 'BlackForestLabs' (FLUX.1 Fill) or " +
            "'OpenAICompatible' for mask inpainting.");

    public Task<GeneratedImageSet> TransformAsync(ImageTransformRequest request, CancellationToken ct) =>
        throw new NotSupportedException(
            $"Operation {request.Operation} is not available on this provider kind. MiniMax documents no outpainting, background-replacement, relighting or upscaling endpoint on its image API (platform.minimax.io/docs/api-reference/image-generation-t2i lists text-to-image with subject_reference only).");

    public async Task<ImageProbeResult> ProbeAsync(CancellationToken ct)
    {
        // The probe sends an UNKNOWN model name. MiniMax validates the model before it generates, so
        // this request cannot produce (or bill for) an image, and its answer is conclusive either
        // way: base_resp.status_code 2013 ("invalid input parameters") proves the endpoint is
        // MiniMax's image API and that the caller's key got past authentication, while 1004/2049
        // proves it did not. The key-before-validation ordering is the same one
        // MiniMaxVideoGenerationClient's own probe comment records as observed on 2026-10-04.
        Dictionary<string, object?> body = new()
        {
            ["model"] = ProbeModel,
            ["prompt"] = "reelbolt connectivity probe",
            ["n"] = 1
        };

        try
        {
            using HttpRequestMessage httpRequest = BuildRequest(body);
            using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, ct);
            string text = await response.Content.ReadAsStringAsync(ct);

            if (!TryReadStatusCode(text, out int statusCode, out string? statusMessage))
            {
                return new ImageProbeResult(false,
                    $"Provider returned HTTP {(int)response.StatusCode} with a body that is not MiniMax's " +
                    $"image envelope: {ImagePayload.Bounded(text)}");
            }

            if (statusCode == 2013)
            {
                return new ImageProbeResult(true, null);
            }

            string meaning = StatusCodeMessages.TryGetValue(statusCode, out string? known) ? known : "unrecognised status code";
            string detail = string.IsNullOrWhiteSpace(statusMessage) ? string.Empty : $" ({ImagePayload.Bounded(statusMessage, 120)})";
            return new ImageProbeResult(false,
                $"Provider answered status_code {statusCode}: {meaning}{detail}.");
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

    private static void Validate(string model, string prompt, int count)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model name is required.", nameof(model));
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            throw new ArgumentException("A prompt is required.", nameof(prompt));
        }

        if (prompt.Length > MaxPromptLength)
        {
            throw new ArgumentException(
                $"The prompt is {prompt.Length} characters; MiniMax documents a {MaxPromptLength}-character " +
                "limit for image-01 and silently truncating it would send a prompt nobody wrote.",
                nameof(prompt));
        }

        if (count is < 1 or > MaxImagesPerRequest)
        {
            throw new ArgumentOutOfRangeException(
                nameof(count), count,
                $"MiniMax documents n between 1 and {MaxImagesPerRequest}.");
        }
    }

    private static void ApplySize(Dictionary<string, object?> body, string? aspectRatio, int? width, int? height)
    {
        if (!string.IsNullOrWhiteSpace(aspectRatio)) body["aspect_ratio"] = aspectRatio;

        // width/height are only sent as a PAIR. MiniMax documents them as ineffective unless both are
        // set, so sending one alone would be a request that silently means something else.
        if (width is { } w && height is { } h)
        {
            body["width"] = w;
            body["height"] = h;
        }
    }

    private async Task<GeneratedImageSet> SendAsync(Dictionary<string, object?> body, CancellationToken ct)
    {
        using HttpRequestMessage httpRequest = BuildRequest(body);
        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, ct);
        string text = await response.Content.ReadAsStringAsync(ct);

        if (!TryReadStatusCode(text, out int statusCode, out string? statusMessage))
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"MiniMax returned HTTP {(int)response.StatusCode} with a body that is not its image " +
                    $"envelope: {ImagePayload.Bounded(text)}");
            }

            throw new InvalidOperationException(
                $"MiniMax returned HTTP {(int)response.StatusCode} with no base_resp: {ImagePayload.Bounded(text)}");
        }

        if (statusCode != 0)
        {
            string meaning = StatusCodeMessages.TryGetValue(statusCode, out string? known) ? known : "unrecognised status code";
            string detail = string.IsNullOrWhiteSpace(statusMessage) ? string.Empty : $" ({ImagePayload.Bounded(statusMessage, 120)})";
            throw new InvalidOperationException($"MiniMax image generation failed: status_code {statusCode} — {meaning}{detail}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"MiniMax returned HTTP {(int)response.StatusCode} with a success status_code.");
        }

        using JsonDocument document = JsonDocument.Parse(text);
        JsonElement root = document.RootElement;
        string? requestId = root.TryGetProperty("id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;

        List<GeneratedImage> images = [];
        if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("image_urls", out JsonElement urls) && urls.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement url in urls.EnumerateArray())
                {
                    string? value = url.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        images.Add(await ImagePayload.DownloadAsync(_downloadClient, value, ct));
                    }
                }
            }
            else if (data.TryGetProperty("image_base64", out JsonElement base64) && base64.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in base64.EnumerateArray())
                {
                    string? value = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) images.Add(ImagePayload.FromBase64(value));
                }
            }
        }

        if (images.Count == 0)
        {
            throw new InvalidOperationException(
                "MiniMax reported success but returned no image. This is a failure, not an empty result: " +
                "the request may still have been billed.");
        }

        return new GeneratedImageSet(images, requestId);
    }

    private HttpRequestMessage BuildRequest(Dictionary<string, object?> body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, GenerationPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        ImagePayload.SetBearer(request, _apiKey);
        return request;
    }

    /// <summary>
    /// Reads <c>base_resp.status_code</c>. MiniMax answers HTTP 200 with a non-zero status code for
    /// most application errors, so the HTTP status alone is not the answer to "did this work".
    /// </summary>
    private static bool TryReadStatusCode(string text, out int statusCode, out string? statusMessage)
    {
        statusCode = 0;
        statusMessage = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("base_resp", out JsonElement baseResp)
                || baseResp.ValueKind != JsonValueKind.Object
                || !baseResp.TryGetProperty("status_code", out JsonElement code))
            {
                return false;
            }

            // MiniMax's own example serialises status_code as a number; the metadata block in the same
            // example serialises counts as strings. Accept both rather than failing on the quirk.
            statusCode = code.ValueKind switch
            {
                JsonValueKind.Number when code.TryGetInt32(out int numeric) => numeric,
                JsonValueKind.String when int.TryParse(code.GetString(), out int parsed) => parsed,
                _ => 0
            };

            if (baseResp.TryGetProperty("status_msg", out JsonElement message) && message.ValueKind == JsonValueKind.String)
            {
                statusMessage = message.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
