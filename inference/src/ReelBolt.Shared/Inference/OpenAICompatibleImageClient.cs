using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Image generation and editing over the OpenAI Images shape: <c>POST {base}/images/generations</c>
/// and <c>POST {base}/images/edits</c>. Covers OpenAI itself and any endpoint that speaks the same
/// contract.
/// </summary>
/// <remarks>
/// <para>
/// <b>Read from OpenAI's own API reference on 2026-10-09</b>
/// (<c>developers.openai.com/api/reference/resources/images</c>). The facts that shape this client:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <c>POST /images/generations</c> returns <c>ImagesResponse</c>:
/// <c>{ created, data: [{ b64_json?, url?, revised_prompt? }], size?, quality?, output_format?, background?, usage? }</c>.
/// <c>b64_json</c> is "returned by default for GPT image models"; <c>url</c> is "unsupported for GPT
/// image models" and exists for older ones. Both are handled, b64 first.
/// </description></item>
/// <item><description>
/// <c>POST /images/edits</c> takes a JSON body with <c>images</c> (an array of
/// <c>{ file_id?, image_url? }</c>, "a fully qualified URL or base64-encoded data URL", up to 16 for
/// GPT image models), <c>prompt</c>, and an optional <c>mask</c> of the same shape.
/// <b>This is the current contract, not the legacy multipart one</b>: the reference marks
/// <c>/images/variations</c> as "retired and no longer available" and describes edits in JSON, so
/// this client sends JSON. A gateway that only implements the older multipart form will answer 4xx;
/// that is a visible failure rather than a silent one, and it is the honest reading of the vendor
/// documentation rather than a hedge against every compatible server.
/// </description></item>
/// <item><description>
/// <b>The mask is prompt GUIDANCE, not a mask.</b> OpenAI's image-generation guide says the mask
/// indicates "where the model should focus" and is "not pixel-exact" — a materially weaker promise
/// than a Fill-style inpaint, where black is preserved and white is regenerated. This client sends
/// it because it is the only mask-shaped control OpenAI documents, and
/// <see cref="Describe"/> says so rather than claiming parity with FLUX.1 Fill.
/// </description></item>
/// <item><description>
/// <c>usage</c> carries token counts "for gpt-image-1 only". They are read but not metered here:
/// the capability meters images, and pricing a token-priced model per image would be a guess. See
/// <c>ModelPriceCatalog</c>, where the OpenAI image models are listed as unpriced with that reason.
/// </description></item>
/// </list>
/// <para>
/// <b>Credential rule.</b> A blank key sends NO <c>Authorization</c> header and reads nothing from
/// the environment — the same rule <see cref="OpenAICompatibleSpeechSynthesisClient"/> follows, so a
/// zero-cost local endpoint (a bundled image server) works and a hosted endpoint 401s loudly instead
/// of spending the host's <c>OPENAI_API_KEY</c>.
/// </para>
/// </remarks>
public class OpenAICompatibleImageClient : IImageClient
{
    /// <summary>The model name every probe sends; deliberately one no endpoint can have.</summary>
    private const string ProbeModel = "__reelbolt_probe__";

    /// <summary>
    /// The prompt cap this client enforces. OpenAI does not document a character cap on the images
    /// endpoints (its limits are token-based and per-model), so this is the shared default from
    /// <see cref="GeneratedPromptSanitizer.DefaultMaxPromptLength"/> rather than a number invented
    /// for this class.
    /// </summary>
    public const int MaxPromptLength = GeneratedPromptSanitizer.DefaultMaxPromptLength;

    private readonly HttpClient _httpClient;
    private readonly HttpClient _downloadClient;

    public OpenAICompatibleImageClient(HttpClient httpClient, HttpClient? downloadClient = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _downloadClient = downloadClient ?? httpClient;
    }

    public ImageModelCapabilities Describe(string model) => new(
        Model: model,
        SupportsInstructionEdit: true,
        SupportsMaskInpaint: true,
        MaxImagesPerRequest: 16,
        MaxPromptLength: MaxPromptLength,
        Basis: "OpenAI Images API reference (developers.openai.com/api/reference/resources/images), read " +
               "2026-10-09: POST /images/generations and POST /images/edits; edits takes a JSON body with " +
               "images[] of {file_id|image_url} and an optional mask of the same shape; b64_json is the " +
               "default return for GPT image models and url is unsupported for them; the image-generation " +
               "guide states the mask is prompt guidance and is NOT pixel-exact, which is why mask support " +
               "is reported as supported-with-that-caveat rather than as a Fill-style inpaint.");

    public async Task<GeneratedImageSet> GenerateAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Model, request.Prompt, request.Count);

        Dictionary<string, object?> body = new()
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["n"] = request.Count
        };

        // The Images API takes an explicit WIDTHxHEIGHT size, not a ratio: "1024x1024",
        // "1024x1536" or "1536x1024" are the documented named values, and any other WIDTHxHEIGHT
        // string is accepted for the models that support it. A ratio therefore contributes nothing
        // here — inventing a mapping from "16:9" to a size would be guessing at a resolution the
        // caller never asked for — so size is sent only when the caller gave both dimensions.
        if (request.Width is { } w && request.Height is { } h) body["size"] = $"{w}x{h}";

        HttpResponseMessage response = await PostJsonAsync("images/generations", body, ct);
        return await ReadImageResponseAsync(response, ct);
    }

    public async Task<GeneratedImageSet> EditAsync(ImageEditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request.Model, request.Prompt, count: 1);

        if (string.IsNullOrWhiteSpace(request.SourceImageUrl))
        {
            throw new ArgumentException("A source image URL is required.", nameof(request));
        }

        Dictionary<string, object?> body = new()
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["images"] = new[] { new { image_url = request.SourceImageUrl } }
        };

        HttpResponseMessage response = await PostJsonAsync("images/edits", body, ct);
        return await ReadImageResponseAsync(response, ct);
    }

    public async Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.MaskBytes);
        Validate(request.Model, request.Prompt, count: 1);

        if (string.IsNullOrWhiteSpace(request.SourceImageUrl))
        {
            throw new ArgumentException("A source image URL is required.", nameof(request));
        }

        if (request.MaskBytes.Length == 0)
        {
            throw new ArgumentException("The mask is empty.", nameof(request));
        }

        // The mask goes out as a data URL because that is the only shape the reference documents for
        // a mask that is not already uploaded through the Files API ("a fully qualified URL or
        // base64-encoded data URL"), and uploading one would add a second resource with its own
        // lifetime to clean up.
        string maskDataUrl = $"data:{request.MaskContentType};base64,{Convert.ToBase64String(request.MaskBytes)}";

        Dictionary<string, object?> body = new()
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["images"] = new[] { new { image_url = request.SourceImageUrl } },
            ["mask"] = new { image_url = maskDataUrl }
        };

        HttpResponseMessage response = await PostJsonAsync("images/edits", body, ct);
        return await ReadImageResponseAsync(response, ct);
    }

    public Task<GeneratedImageSet> TransformAsync(ImageTransformRequest request, CancellationToken ct) =>
        throw new NotSupportedException(
            $"Operation {request.Operation} is not available on this provider kind. The OpenAI Images API documents generations and edits only (developers.openai.com/api/reference/resources/images): no outpainting, background-replacement, relighting or upscaling operation exists to call.");

    public async Task<ImageProbeResult> ProbeAsync(CancellationToken ct)
    {
        // The probe asks for a model that cannot exist, so it can never generate and never bill.
        // What its answer proves is narrower than the other two clients' and is stated as such: an
        // endpoint that answers "no such model" has demonstrably been reached, and if the answer is
        // not an authentication error then the credential was accepted. A 401/403, or any body that
        // names an authorization problem, is reported as a key problem. Anything else — including a
        // 2xx, which would mean the endpoint accepted a nonsense model — is not ok.
        Dictionary<string, object?> body = new()
        {
            ["model"] = ProbeModel,
            ["prompt"] = "reelbolt connectivity probe",
            ["n"] = 1
        };

        try
        {
            using HttpRequestMessage request = BuildJsonRequest("images/generations", body);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, ct);
            string text = await response.Content.ReadAsStringAsync(ct);
            int status = (int)response.StatusCode;

            if (status is 401 or 403)
            {
                return new ImageProbeResult(false,
                    $"The endpoint rejected the credential (HTTP {status}): {ImagePayload.Bounded(text)}");
            }

            if (response.IsSuccessStatusCode)
            {
                return new ImageProbeResult(false,
                    "The endpoint accepted a request for a deliberately nonexistent model, which proves " +
                    "nothing about whether real requests will work. Verify the model name by hand.");
            }

            if (LooksLikeAnAuthError(text))
            {
                return new ImageProbeResult(false,
                    $"The endpoint's HTTP {status} names an authorization problem: {ImagePayload.Bounded(text)}");
            }

            if (status is 400 or 404 or 422)
            {
                return new ImageProbeResult(true, null);
            }

            return new ImageProbeResult(false,
                $"The endpoint answered HTTP {status}, which does not establish that the key was accepted: " +
                ImagePayload.Bounded(text));
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
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("A model name is required.", nameof(model));
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("A prompt is required.", nameof(prompt));
        if (prompt.Length > MaxPromptLength)
        {
            throw new ArgumentException(
                $"The prompt is {prompt.Length} characters, over this client's {MaxPromptLength}-character cap.",
                nameof(prompt));
        }

        if (count < 1) throw new ArgumentOutOfRangeException(nameof(count), count, "At least one image is required.");
    }

    /// <summary>
    /// A 4xx body that talks about the credential, whatever the status was. Some gateways answer 400
    /// rather than 401 for a missing key, and reading that as "the model was wrong, so the key must
    /// be fine" is exactly the false positive a probe must not produce.
    /// </summary>
    private static bool LooksLikeAnAuthError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        string lowered = body.ToLowerInvariant();
        return lowered.Contains("unauthor")
            || lowered.Contains("invalid_api_key")
            || lowered.Contains("invalid api key")
            || lowered.Contains("incorrect api key")
            || lowered.Contains("authentication")
            || lowered.Contains("no api key");
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string relativePath, Dictionary<string, object?> body, CancellationToken ct)
    {
        HttpResponseMessage response = await _httpClient.SendAsync(BuildJsonRequest(relativePath, body), ct);
        if (!response.IsSuccessStatusCode)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            int status = (int)response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException(
                $"The image endpoint returned HTTP {status}: {ImagePayload.Bounded(text)}");
        }

        return response;
    }

    private HttpRequestMessage BuildJsonRequest(string relativePath, Dictionary<string, object?> body)
    {
        string baseAddress = _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? "https://api.openai.com/v1";
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(baseAddress + "/" + relativePath.TrimStart('/')))
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<GeneratedImageSet> ReadImageResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            string text = await response.Content.ReadAsStringAsync(ct);
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;

            List<GeneratedImage> images = [];
            if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in data.EnumerateArray())
                {
                    string? revised = entry.TryGetProperty("revised_prompt", out JsonElement rp) && rp.ValueKind == JsonValueKind.String
                        ? rp.GetString()
                        : null;

                    if (entry.TryGetProperty("b64_json", out JsonElement b64) && b64.ValueKind == JsonValueKind.String
                        && b64.GetString() is { Length: > 0 } encoded)
                    {
                        images.Add(ImagePayload.FromBase64(encoded) with { RevisedPrompt = revised });
                        continue;
                    }

                    if (entry.TryGetProperty("url", out JsonElement url) && url.ValueKind == JsonValueKind.String
                        && url.GetString() is { Length: > 0 } link)
                    {
                        GeneratedImage downloaded = await ImagePayload.DownloadAsync(_downloadClient, link, ct);
                        images.Add(downloaded with { RevisedPrompt = revised });
                    }
                }
            }

            if (images.Count == 0)
            {
                throw new InvalidOperationException(
                    "The image endpoint reported success but returned no image. This is a failure, not an " +
                    "empty result: the request may still have been billed.");
            }

            string? created = root.TryGetProperty("created", out JsonElement createdElement) && createdElement.ValueKind == JsonValueKind.Number
                ? createdElement.GetRawText()
                : null;

            return new GeneratedImageSet(images, created);
        }
    }
}
