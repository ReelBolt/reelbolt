using System.Globalization;
using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// fal.ai — image generation, instruction editing, mask inpainting and the thumbnail editor's
/// expand / background / relight / upscale operations, over the same queue API as
/// <see cref="FalAiAnalysisClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One client, one catalogue, many endpoints.</b> fal exposes one queue contract over every model it
/// hosts, so this kind is ONE <see cref="IImageClient"/> and the endpoint is chosen per call by
/// <c>request.Model</c> - which the editor takes from <see cref="Catalog"/> (one default per job,
/// overridable). Each editor tool resolves a capability of its own (<see cref="ImageJobCapabilities"/>), and a
/// row of that capability sends its own model when it is a catalogued endpoint for the job, else an empty one
/// that this client reads as the job's default - which is also how a pre-split <c>ImageEdit</c> row still
/// serves every tool. The queue plumbing (submit, poll, download, probe) is <see cref="FalQueueClient"/>, shared with
/// the analysis client so the two cannot drift apart on a queue detail.
/// </para>
/// <para>
/// <b>Nothing is guessed.</b> Each <see cref="FalImageEndpoint"/> was transcribed from the endpoint's
/// own OpenAPI schema (fetched 2026-10-10; <c>docs/fal-ai-models.md</c> has every field with its URL).
/// A field the schema does not declare is REFUSED, an enum is validated BEFORE it is sent (fal silently
/// ignores an unknown value and returns a plausible image for a request nobody made), a required field
/// is required, and the vendor's own schema defaults are not re-sent. Outputs are parsed by the
/// documented field names and the images are downloaded and returned as bytes - never as a URL.
/// </para>
/// <para>
/// <b>Masks go by URL.</b> fal takes a mask only as a URL (<c>mask_url</c>), and this service holds no
/// upload path of its own, so <see cref="ImageInpaintRequest.MaskUrl"/> is the carrier; bytes alone throw
/// a sentence saying so. <b>Mask polarity is checked, not assumed</b>: see <see cref="FalMaskPolarity"/>.
/// </para>
/// </remarks>
public sealed class FalAiImageClient : IImageClient, IBillingModelKeyed
{
    /// <summary>The public queue host. A provider row's endpoint overrides it.</summary>
    public const string DefaultEndpoint = FalQueueClient.DefaultEndpoint;

    /// <summary>The endpoint catalogue: one default per job plus the documented alternatives.</summary>
    public static FalImageCatalog Catalog => FalImageCatalog.Instance;

    private readonly FalQueueClient _queue;

    /// <summary>The same queue, whose submits carry <c>X-Fal-Store-IO: 0</c>: used for a person's pictures only (avatars phase 3).</summary>
    private readonly FalQueueClient _privateQueue;
    private readonly string? _probeModel;
    private readonly InferenceProviderCapability? _probeCapability;

    /// <param name="probeModel">
    /// The endpoint id <see cref="ProbeAsync"/> submits against, taken from the provider row so a
    /// connection test checks the model an admin typed rather than some other reachable endpoint.
    /// </param>
    /// <param name="probeCapability">
    /// The row's capability. When it is one of the editor's job capabilities the probe also checks that
    /// <paramref name="probeModel"/> serves THAT job (<see cref="ImageJobCapabilities.FalModelProblem"/>),
    /// and a blank model is probed as the catalogue's default for the job.
    /// </param>
    public FalAiImageClient(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? probeModel = null,
        InferenceProviderCapability? probeCapability = null)
    {
        _queue = new FalQueueClient(httpClient, apiKey, downloadClient, pollInterval, pollTimeout, delay);
        _privateQueue = new FalQueueClient(httpClient, apiKey, downloadClient, pollInterval, pollTimeout, delay,
            FalAiVideoSegmentationClient.StoreIoHeaders);
        _probeModel = probeModel;
        _probeCapability = probeCapability;
    }

    // ------------------------------------------------------------------ IImageClient

    public Task<GeneratedImageSet> GenerateAsync(ImageGenerationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        FalImageEndpoint endpoint = Require(request.Model, "generation", FalImageJob.Generate, FalImageJob.Generate);

        if (!string.IsNullOrWhiteSpace(request.ReferenceImageUrl))
        {
            throw new ArgumentException(
                "'" + endpoint.Id + "' is a text-to-image route and documents no subject-reference input. " +
                "Use an instruction-edit endpoint (EditAsync) to start from an existing picture.", nameof(request));
        }

        Input input = new(null, null, request.Prompt, null, request.Seed, request.Count, request.AspectRatio,
            request.Width, request.Height, request.Params);
        return RunAsync(endpoint, input, ct);
    }

    public Task<GeneratedImageSet> EditAsync(ImageEditRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        FalImageEndpoint endpoint = Require(request.Model, "instruction edit", FalImageJob.Instruct, FalImageJob.Instruct);
        Input input = new(request.SourceImageUrl, null, request.Prompt, null, request.Seed, 1, request.AspectRatio,
            null, null, request.Params, ReferenceUrls: request.ReferenceImageUrls);
        // A person's pictures (an avatar's still) ask the vendor not to keep the inputs; every other edit is unchanged.
        return RunAsync(endpoint, input, ct, request.NoVendorRetention ? _privateQueue : null);
    }

    public Task<GeneratedImageSet> InpaintAsync(ImageInpaintRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The editor's erase carries no instruction of its own and sends the neutral prompt "background"
        // (EditImageOperationsController); with an empty model that is what tells erase from fill.
        bool neutralPrompt = string.IsNullOrWhiteSpace(request.Prompt)
            || string.Equals(request.Prompt.Trim(), NeutralErasePrompt, StringComparison.OrdinalIgnoreCase);
        FalImageEndpoint endpoint = Require(
            request.Model, "mask inpaint", neutralPrompt ? FalImageJob.Erase : FalImageJob.Fill,
            FalImageJob.Erase, FalImageJob.Fill);

        if (string.IsNullOrWhiteSpace(request.MaskUrl))
        {
            throw request.MaskBytes is { Length: > 0 }
                ? new InvalidOperationException(
                    "fal.ai takes a mask only as a URL ('" + endpoint.MaskUrlField + "'), and this service has no " +
                    "upload path of its own, so the mask bytes cannot be sent. Set ImageInpaintRequest.MaskUrl " +
                    "to a presigned GET for the mask the browser uploaded.")
                : new ArgumentException("A mask is required: set ImageInpaintRequest.MaskUrl.", nameof(request));
        }

        switch (endpoint.MaskPolarity)
        {
            case FalMaskPolarity.WhitePreserves when !request.MaskMeansPreserve:
                throw new NotSupportedException(
                    "'" + endpoint.Id + "' states its mask as \"Black edits, white preserves\" - the OPPOSITE of the " +
                    "editor's white = act-on mask - and the server has no raster library to invert one. Supply an " +
                    "already-inverted mask and set ImageInpaintRequest.MaskMeansPreserve, or use '" +
                    Catalog.DefaultFor(endpoint.Job == FalImageJob.Erase ? FalImageJob.Erase : FalImageJob.Fill).Id + "'.");
            case FalMaskPolarity.WhiteActs or FalMaskPolarity.NotStated when request.MaskMeansPreserve:
                throw new NotSupportedException(
                    "'" + endpoint.Id + "' reads white as the area to act on" +
                    (endpoint.MaskPolarity == FalMaskPolarity.NotStated ? " (assumed: its schema does not say)" : "") +
                    ", so an inverted (white = preserve) mask would act on exactly the wrong pixels.");
        }

        // An erase endpoint documents no prompt: the neutral sentinel is dropped, anything else is a real
        // instruction it cannot honour and is refused by BuildBody rather than discarded.
        string? prompt = endpoint.PromptField is null && neutralPrompt ? null : request.Prompt;
        Input input = new(request.SourceImageUrl, request.MaskUrl, prompt, null, request.Seed, 1, null,
            null, null, request.Params);
        return RunAsync(endpoint, input, ct);
    }

    public Task<GeneratedImageSet> TransformAsync(ImageTransformRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        FalImageJob job = FalImageCatalog.JobFor(request.Operation);
        FalImageEndpoint endpoint = Require(request.Model, request.Operation.ToString(), job, job);
        if (request.Expand is not null && endpoint.ExpandBy == FalExpandGeometry.None)
        {
            throw new ArgumentException("'" + endpoint.Id + "' is not an expand endpoint, so it takes no expand geometry.", nameof(request));
        }

        Input input = new(request.ImageUrl, null, request.Prompt, request.NegativePrompt, request.Seed, 1, null,
            null, null, request.Params, request.Expand);
        return RunAsync(endpoint, input, ct);
    }

    /// <summary>
    /// No-cost provider test: submits the ROW'S OWN model id with an empty body, which every catalogued
    /// endpoint rejects on validation (each requires an image or a prompt) - see
    /// <see cref="FalQueueClient.ProbeAsync"/> for exactly what the answer proves. A model id outside the
    /// catalogue fails the test without a request, because this client would refuse to use it anyway.
    /// </summary>
    public async Task<ImageProbeResult> ProbeAsync(CancellationToken ct)
    {
        string? model = string.IsNullOrWhiteSpace(_probeModel)
            ? _probeCapability is { } blankCapability ? ImageJobCapabilities.FalDefaultFor(blankCapability)?.Id : null
            : _probeModel.Trim();

        if (string.IsNullOrWhiteSpace(model))
        {
            return new ImageProbeResult(false, "No model id is configured on this provider row.");
        }

        if (Catalog.Find(model) is null)
        {
            return new ImageProbeResult(false,
                "'" + model + "' is not an endpoint this client has a read schema for, so it would refuse to " +
                "send it a request. Use one of: " + string.Join(", ", Catalog.All.Select(e => e.Id)) + ".");
        }

        // A row named for one job pointing at another job's endpoint would test green and then never be
        // used for that model (the editor sends the catalogue default for a mismatched one), so the test
        // says so instead.
        if (_probeCapability is { } capability && ImageJobCapabilities.FalModelProblem(capability, model) is { } problem)
        {
            return new ImageProbeResult(false, problem);
        }

        FalProbeOutcome outcome = await _queue.ProbeAsync(model, ct);
        return new ImageProbeResult(outcome.IsOk, outcome.Reason);
    }

    public ImageModelCapabilities Describe(string model)
    {
        FalImageEndpoint? endpoint = Catalog.Find(model);
        if (endpoint is null)
        {
            return new ImageModelCapabilities(model ?? string.Empty, false, false, 1,
                GeneratedPromptSanitizer.DefaultMaxPromptLength,
                "unverified: no schema has been read for this model id, so nothing about it is claimed. " +
                "Endpoints with a verified schema: " + string.Join(", ", Catalog.All.Select(e => e.Id)) + ".");
        }

        Dictionary<string, IReadOnlyList<string>> options = [];
        foreach (FalImageField field in endpoint.Fields.Where(f => f.Values is not null))
        {
            options[field.Name] = field.Values!;
        }

        if (endpoint.SizePresets is not null) options[endpoint.SizeField!] = endpoint.SizePresets;

        return new ImageModelCapabilities(
            endpoint.Id,
            SupportsInstructionEdit: endpoint.Job == FalImageJob.Instruct,
            SupportsMaskInpaint: endpoint.MaskUrlField is not null,
            MaxImagesPerRequest: endpoint.CountMax,
            MaxPromptLength: endpoint.PromptMaxLength ?? GeneratedPromptSanitizer.DefaultMaxPromptLength,
            Basis: "fal OpenAPI schema for " + endpoint.Id + " (read 2026-10-10), job " + endpoint.Job +
                   (endpoint.IsDefault ? " (default)" : endpoint.IsPreset ? " (preset)" : endpoint.IsOlderGeneration ? " (older generation)" : "") +
                   "; mask polarity " + endpoint.MaskPolarity + "; source " + endpoint.SourceUrl,
            Options: options.Count == 0 ? null : options)
        {
            TakesPrompt = endpoint.PromptField is not null,
            Expand = endpoint.ExpandBy == FalExpandGeometry.None
                ? null
                : new ImageExpandCapabilities(
                    endpoint.ExpandBy == FalExpandGeometry.Margins ? "margins" : "canvas",
                    endpoint.MaxMarginPx,
                    endpoint.MaxOutputPixels,
                    endpoint.MaxInputPixels,
                    endpoint.LimitsBasis)
        };
    }

    // ------------------------------------------------------------------ IBillingModelKeyed

    public string BillingModelKey(string model, IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        FalImageEndpoint? endpoint = Catalog.Find(model);
        if (endpoint?.BillingTierField is not { } tierField) return model;

        FalImageField field = endpoint.FieldNamed(tierField)!;
        string? value = null;
        if (parameters is not null)
        {
            foreach ((string key, JsonElement element) in parameters)
            {
                if ((key == field.Name || key == field.EditorName) && element.ValueKind == JsonValueKind.String)
                {
                    value = element.GetString();
                }
            }
        }

        return model + "#" + (value ?? field.VendorDefault);
    }

    /// <summary>
    /// An endpoint the catalogue marks <see cref="FalImageEndpoint.PricedPerMegapixel"/>, called without extra reference
    /// pictures (whose megapixels the output does not show and some endpoints may bill).
    /// </summary>
    public bool PricesByOutputMegapixels(string model, bool editsASource, int referenceImages) =>
        Metering.OutputSizePricing.PricesByOutputMegapixels(InferenceProviderKind.FalAi, model, editsASource, referenceImages);

    /// <inheritdoc />
    public long? MaxOutputPixels(string model) => Metering.OutputSizePricing.MaxOutputPixels(InferenceProviderKind.FalAi, model);

    // ------------------------------------------------------------------ the request

    private sealed record Input(
        string? SourceUrl,
        string? MaskUrl,
        string? Prompt,
        string? NegativePrompt,
        int? Seed,
        int Count,
        string? AspectRatio,
        int? Width,
        int? Height,
        IReadOnlyDictionary<string, JsonElement>? Params,
        ImageExpandGeometry? Expand = null,
        IReadOnlyList<string>? ReferenceUrls = null);

    /// <summary>The prompt the editor sends for an erase, which carries no instruction of its own.</summary>
    public const string NeutralErasePrompt = "background";

    /// <summary>
    /// Resolves the endpoint. An EMPTY model means "the catalogue's default for this operation" - a fal.ai
    /// provider row sends an empty model on every request, because the row's own model is only the
    /// connection test's target and the endpoint is a property of the operation. An explicit model is the
    /// endpoint id and is refused when no schema has been read for it or when it serves another job.
    /// </summary>
    private static FalImageEndpoint Require(string? model, string what, FalImageJob defaultJob, params FalImageJob[] jobs)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return Catalog.DefaultFor(defaultJob);
        }

        FalImageEndpoint? endpoint = Catalog.Find(model);
        if (endpoint is null)
        {
            throw new NotSupportedException(
                "No schema has been read for the fal.ai endpoint '" + model + "', so this client will not send it a " +
                "request: a field the endpoint does not define is SILENTLY IGNORED by the vendor, and the caller " +
                "would get a plausible result for a request nobody made. Endpoints with a verified schema: " +
                string.Join(", ", Catalog.All.Select(e => e.Id)) + ".");
        }

        if (!jobs.Contains(endpoint.Job))
        {
            throw new NotSupportedException(
                "'" + model + "' is a " + endpoint.Job + " endpoint and cannot serve " + what + ". Endpoints for it: " +
                string.Join(", ", Catalog.All.Where(e => jobs.Contains(e.Job)).Select(e => e.Id)) + ".");
        }

        return endpoint;
    }

    private async Task<GeneratedImageSet> RunAsync(FalImageEndpoint endpoint, Input input, CancellationToken ct, FalQueueClient? queue = null)
    {
        Dictionary<string, object?> body = BuildBody(endpoint, input);
        FalQueueResult queued = await (queue ?? _queue).SubmitAndPollAsync(endpoint.Id, body, ct);

        List<GeneratedImage> images = [];
        if (endpoint.Output == FalOutputShape.SingleImage)
        {
            images.Add(await _queue.ReadImageFieldAsync(queued.Output, "image", ct));
        }
        else
        {
            if (!queued.Output.TryGetProperty("images", out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "fal.ai returned a result with no 'images' array for '" + endpoint.Id + "': " +
                    ImagePayload.Bounded(queued.Output.GetRawText()));
            }

            foreach (JsonElement item in array.EnumerateArray())
            {
                images.Add(await _queue.ReadImageObjectAsync(item, ct));
            }
        }

        if (images.Count == 0)
        {
            throw new InvalidOperationException(
                "fal.ai completed '" + endpoint.Id + "' but returned no image (request " + queued.RequestId +
                "). A 200 carrying no image is not a success.");
        }

        return new GeneratedImageSet(images, string.IsNullOrEmpty(queued.RequestId) ? null : queued.RequestId)
        {
            ResolvedModel = endpoint.Id
        };
    }

    /// <summary>
    /// Builds the request body from the typed inputs and the free parameters, refusing anything the
    /// endpoint's schema does not declare. Internal so the tests can pin exact bodies without a transport.
    /// </summary>
    private static Dictionary<string, object?> BuildBody(FalImageEndpoint ep, Input input)
    {
        Dictionary<string, object?> body = new(StringComparer.Ordinal);

        // ---- the picture
        bool hasSource = !string.IsNullOrWhiteSpace(input.SourceUrl);
        if (ep.ImageUrlField is not null || ep.ImageUrlsField is not null)
        {
            if (!hasSource)
            {
                throw new ArgumentException("A source image URL is required: '" + ep.Id + "' takes '" +
                    (ep.ImageUrlField ?? ep.ImageUrlsField) + "' and the vendor fetches it itself.");
            }

            if (ep.ImageUrlField is not null)
            {
                body[ep.ImageUrlField] = input.SourceUrl;

                // One-image endpoints have no second slot to put a reference picture in, and dropping it
                // would run a DIFFERENT edit from the one the user asked for, at their expense. Named in
                // the refusal: the endpoints whose schema documents an image ARRAY are the ones that can.
                if (input.ReferenceUrls is { Count: > 0 })
                {
                    FalImageEndpoint[] alternatives = [.. Catalog.All.Where(e =>
                        e.ImageUrlsField is not null && e.Job == ep.Job && e.Id != ep.Id)];

                    throw new NotSupportedException(
                        "'" + ep.Id + "' takes one image ('" + ep.ImageUrlField + "') and documents no reference " +
                        "picture input, so it cannot edit against a second picture. Use an endpoint that takes " +
                        "an image array" + (alternatives.Length == 0
                            ? "."
                            : ": " + string.Join(", ", alternatives.Select(e => e.Id)) + "."));
                }
            }
            else
            {
                // The source is always FIRST and the references follow IN THE CALLER'S ORDER: every
                // documented prompt reads the array positionally ("the second image"), so neither the
                // picture being edited nor the order a user attached things in may shift.
                body[ep.ImageUrlsField!] = input.ReferenceUrls is { Count: > 0 } references
                    ? [input.SourceUrl, .. references]
                    : new[] { input.SourceUrl };
            }
        }
        else if (hasSource)
        {
            throw new ArgumentException("'" + ep.Id + "' is a text-to-image route and takes no source image.");
        }

        // ---- the mask
        if (ep.MaskUrlField is not null)
        {
            if (string.IsNullOrWhiteSpace(input.MaskUrl))
            {
                throw new ArgumentException("'" + ep.Id + "' requires a mask URL ('" + ep.MaskUrlField + "').");
            }

            body[ep.MaskUrlField] = input.MaskUrl;
        }
        else if (!string.IsNullOrWhiteSpace(input.MaskUrl))
        {
            throw new ArgumentException("'" + ep.Id + "' documents no mask.");
        }

        // ---- the prompt
        bool hasPrompt = !string.IsNullOrWhiteSpace(input.Prompt);
        if (ep.PromptField is null)
        {
            if (hasPrompt)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' documents no prompt, so one cannot be honoured. Leave it blank (the editor sends " +
                    "none for this operation) or choose an endpoint that takes one.");
            }
        }
        else if (!hasPrompt)
        {
            if (ep.PromptRequired)
            {
                throw new ArgumentException("A prompt is required: '" + ep.Id + "' takes '" + ep.PromptField + "'" +
                    (ep.Id.EndsWith("youtube-thumbnails", StringComparison.Ordinal)
                        ? ", whose schema default (\"Generate youtube thumbnails\") would otherwise be printed onto the thumbnail."
                        : "."));
            }
        }
        else
        {
            int length = input.Prompt!.Length;
            if (ep.PromptMinLength is { } min && length < min || ep.PromptMaxLength is { } max && length > max)
            {
                throw new ArgumentException("The prompt for '" + ep.Id + "' must be " + (ep.PromptMinLength ?? 0) +
                    " to " + (ep.PromptMaxLength?.ToString() ?? "unbounded") + " characters; it is " + length + ".");
            }

            body[ep.PromptField] = input.Prompt;
        }

        // ---- negative prompt
        if (!string.IsNullOrWhiteSpace(input.NegativePrompt))
        {
            if (ep.NegativePromptField is null)
            {
                throw new ArgumentException("'" + ep.Id + "' documents no negative prompt.");
            }

            body[ep.NegativePromptField] = input.NegativePrompt;
        }

        // ---- seed
        if (input.Seed is { } seed)
        {
            if (ep.SeedField is null)
            {
                throw new ArgumentException("'" + ep.Id + "' documents no seed, so one cannot be honoured.");
            }

            if (ep.SeedMin is { } lo && seed < lo || ep.SeedMax is { } hi && seed > hi)
            {
                throw new ArgumentOutOfRangeException(nameof(input), seed,
                    "'" + ep.Id + "' documents a seed from " + (ep.SeedMin ?? long.MinValue) + " to " + (ep.SeedMax ?? long.MaxValue) + ".");
            }

            body[ep.SeedField] = seed;
        }

        // ---- count
        if (input.Count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.Count, "Count must be at least 1.");
        }

        if (input.Count > 1)
        {
            if (ep.CountField is null)
            {
                throw new ArgumentOutOfRangeException(nameof(input), input.Count,
                    "'" + ep.Id + "' returns one image per request and documents no image count.");
            }

            if (input.Count > ep.CountMax)
            {
                throw new ArgumentOutOfRangeException(nameof(input), input.Count,
                    "'" + ep.Id + "' documents at most " + ep.CountMax + " images per request.");
            }

            body[ep.CountField] = input.Count;
        }

        // ---- size and aspect ratio
        BuildShape(ep, input, body);

        // ---- expand geometry
        BuildExpand(ep, input, body);

        // ---- free parameters
        if (input.Params is not null)
        {
            foreach ((string key, JsonElement value) in input.Params)
            {
                FalImageField field = ep.FieldNamed(key) ?? throw new ArgumentException(
                    "'" + key + "' is not a field this client forwards to '" + ep.Id + "'. Allowed: " +
                    string.Join(", ", ep.Fields.Select(f => f.EditorName is null ? f.Name : f.Name + " (or " + f.EditorName + ")")) +
                    ". A field the schema does not declare would be silently ignored by the vendor, so it is refused.");

                if (body.ContainsKey(field.Name))
                {
                    throw new ArgumentException("'" + field.Name + "' was supplied twice for '" + ep.Id +
                        "' (once through a typed member and once in Params, or under both of its names).");
                }

                object converted = Convert(ep, field, value);
                body[field.Name] = field.WrapIn is null
                    ? converted
                    : new Dictionary<string, object?> { [field.WrapIn] = converted };
            }
        }

        foreach (FalImageField withDefault in ep.Fields.Where(f => f.SendByDefault is not null))
        {
            if (!body.ContainsKey(withDefault.Name)) body[withDefault.Name] = withDefault.SendByDefault;
        }

        // ---- PNG, so a layer is not JPEG-degraded and re-compressed on every operation
        if (ep.OutputFormatField is not null && !body.ContainsKey(ep.OutputFormatField))
        {
            body[ep.OutputFormatField] = ep.OutputFormatValue;
        }

        foreach (FalImageField required in ep.Fields.Where(f => f.Required))
        {
            if (!body.ContainsKey(required.Name))
            {
                throw new ArgumentException("'" + required.Name + "' is required by '" + ep.Id + "'. Allowed: " +
                    (required.Values is null ? required.Type.ToString() : string.Join(", ", required.Values)) + ".");
            }
        }

        if (ep.Rule?.Invoke(body) is { } refusal)
        {
            throw new ArgumentException(refusal + " (" + ep.Id + ")");
        }

        return body;
    }

    /// <summary>
    /// Writes a planned expand (<see cref="ExpandPlanner"/>) into the endpoint's own geometry fields: per-side
    /// margins for a margins endpoint, <c>canvas_size</c> / <c>original_image_size</c> /
    /// <c>original_image_location</c> (as <c>[width, height]</c> and <c>[x, y]</c>) for a canvas one. Every
    /// documented limit is checked here as well, so a request the vendor would refuse after a queue wait is
    /// refused before it is sent.
    /// </summary>
    private static void BuildExpand(FalImageEndpoint ep, Input input, Dictionary<string, object?> body)
    {
        if (ep.ExpandBy == FalExpandGeometry.None) return;

        if (input.Expand is not { } g)
        {
            if (ep.ExpandBy == FalExpandGeometry.Canvas)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' takes the finished canvas and where the picture goes on it, so an expand on it " +
                    "needs a planned geometry (ImageTransformRequest.Expand), not per-side margins.");
            }

            return; // a margins endpoint called the old way: margins come through Params, checked as fields
        }

        if (g.ImageWidth < 1 || g.ImageHeight < 1 || g.Left < 0 || g.Top < 0 || g.Right < 0 || g.Bottom < 0)
        {
            throw new ArgumentException("The expand geometry is not a canvas around the picture (" + g + ").");
        }

        long canvas = (long)g.CanvasWidth * g.CanvasHeight;
        if (ep.MaxOutputPixels is { } maxOut && (ep.MaxOutputPixelsExclusive ? canvas >= maxOut : canvas > maxOut))
        {
            throw new ArgumentException(
                "'" + ep.Id + "' makes pictures of " + (ep.MaxOutputPixelsExclusive ? "less than " : "at most ") + maxOut +
                " pixels; " + g.CanvasWidth + "x" + g.CanvasHeight + " is " + canvas + ".");
        }

        if (ep.ExpandBy == FalExpandGeometry.Margins)
        {
            if (ep.MaxInputPixels is { } maxIn && (long)g.ImageWidth * g.ImageHeight > maxIn)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' takes pictures of at most " + maxIn + " pixels as they are; " + g.ImageWidth + "x" +
                    g.ImageHeight + " is larger, so send a smaller copy.");
            }

            (string Field, int Value)[] sides =
                [("expand_top", g.Top), ("expand_right", g.Right), ("expand_bottom", g.Bottom), ("expand_left", g.Left)];
            foreach ((string field, int value) in sides)
            {
                if (input.Params is not null && input.Params.Keys.Any(k => ep.FieldNamed(k)?.Name == field))
                {
                    throw new ArgumentException(
                        "'" + field + "' was supplied in Params as well as by the planned geometry for '" + ep.Id + "'.");
                }

                if (value == 0) continue;
                CheckRange(ep.FieldNamed(field)!, value);
                body[field] = (long)value;
            }

            return;
        }

        body[ep.CanvasSizeField!] = new[] { g.CanvasWidth, g.CanvasHeight };
        body[ep.OriginalImageSizeField!] = new[] { g.ImageWidth, g.ImageHeight };
        body[ep.OriginalImageLocationField!] = new[] { g.ImageX, g.ImageY };
    }

    private static void BuildShape(FalImageEndpoint ep, Input input, Dictionary<string, object?> body)
    {
        bool hasWidth = input.Width is not null;
        bool hasHeight = input.Height is not null;
        if (hasWidth != hasHeight)
        {
            throw new ArgumentException("Width and Height must be supplied together.");
        }

        bool hasAspect = !string.IsNullOrWhiteSpace(input.AspectRatio);
        if (hasWidth)
        {
            if (ep.SizeField is null)
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' documents no pixel size" +
                    (ep.AspectRatioField is null ? "." : ", only an aspect ratio; pass AspectRatio instead."));
            }

            if (hasAspect)
            {
                throw new ArgumentException("Give a size as Width/Height OR as an AspectRatio preset, not both.");
            }

            int width = input.Width!.Value;
            int height = input.Height!.Value;
            if (width is < 1 or > 14142 || height is < 1 or > 14142)
            {
                throw new ArgumentOutOfRangeException(nameof(input), width + "x" + height,
                    "fal documents image_size.width/height as above 0 and at most 14142.");
            }

            if (ep.SupportedSizes is not null && !ep.SupportedSizes.Contains(width + "x" + height, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "'" + ep.Id + "' only renders these explicit sizes: " + string.Join(", ", ep.SupportedSizes) +
                    ". Got " + width + "x" + height + ". (1280x720 is the YouTube thumbnail size and is supported.)");
            }

            body[ep.SizeField] = new Dictionary<string, object?> { ["width"] = width, ["height"] = height };
            return;
        }

        if (!hasAspect) return;

        string ratio = input.AspectRatio!;
        if (ep.SizeField is not null)
        {
            if (ep.SizePresets is null || !ep.SizePresets.Contains(ratio, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    "'" + ratio + "' is not a size preset '" + ep.Id + "' documents: " +
                    string.Join(", ", ep.SizePresets ?? []) + ". Pass Width and Height for an explicit size.");
            }

            body[ep.SizeField] = ratio;
            return;
        }

        if (ep.AspectRatioField is null)
        {
            throw new ArgumentException("'" + ep.Id + "' documents no aspect ratio or size.");
        }

        FalImageField aspect = ep.FieldNamed(ep.AspectRatioField)!;
        if (aspect.Values is not null && !aspect.Values.Contains(ratio, StringComparer.Ordinal))
        {
            throw new ArgumentException(
                "'" + ratio + "' is not an aspect ratio '" + ep.Id + "' documents: " + string.Join(", ", aspect.Values) +
                ". The vendor SILENTLY IGNORES an unknown enum value.");
        }

        body[aspect.Name] = ratio;
    }

    private static object Convert(FalImageEndpoint ep, FalImageField field, JsonElement value)
    {
        switch (field.Type)
        {
            case FalFieldType.String:
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new ArgumentException("'" + field.Name + "' for '" + ep.Id + "' must be a string.");
                }

                string text = value.GetString()!;
                if (field.Values is not null && !field.Values.Contains(text, StringComparer.Ordinal))
                {
                    throw new ArgumentException(
                        "'" + text + "' is not a value '" + ep.Id + "' documents for '" + field.Name + "': " +
                        string.Join(", ", field.Values) + ". The vendor SILENTLY IGNORES an unknown enum value, so " +
                        "a typo would produce an image from the default setting." +
                        (field.Note is null ? "" : " " + field.Note));
                }

                return text;

            case FalFieldType.Integer:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long whole))
                {
                    throw new ArgumentException("'" + field.Name + "' for '" + ep.Id + "' must be an integer.");
                }

                CheckRange(field, whole);
                return whole;

            case FalFieldType.Number:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number))
                {
                    throw new ArgumentException("'" + field.Name + "' for '" + ep.Id + "' must be a number.");
                }

                CheckRange(field, number);
                return number;

            case FalFieldType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ArgumentException("'" + field.Name + "' for '" + ep.Id + "' must be true or false.");
                }

                return value.GetBoolean();

            default:
                throw new NotSupportedException("Unhandled field type " + field.Type);
        }
    }

    /// <summary>
    /// Refuses a value outside the schema's bounds in a sentence an editor user can read ("The upscale factor can
    /// be at most 2."). The label is the field's own name in words, so one builder serves every bounded field.
    /// </summary>
    private static void CheckRange(FalImageField field, double value)
    {
        string label = "The " + field.Name.Replace('_', ' ');
        if (field.Max is { } max && value > max)
        {
            throw new ArgumentOutOfRangeException(paramName: null, message: label + " can be at most " + max.ToString(CultureInfo.InvariantCulture) + ".");
        }

        if (field.Min is { } min && value < min)
        {
            throw new ArgumentOutOfRangeException(paramName: null, message: label + " can be at least " + min.ToString(CultureInfo.InvariantCulture) + ".");
        }
    }
}
