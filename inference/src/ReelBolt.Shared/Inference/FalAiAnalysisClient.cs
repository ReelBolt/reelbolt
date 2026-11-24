using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// fal.ai — subject segmentation and depth estimation over the queue API at <c>queue.fal.run</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every field below was read from the endpoint's own OpenAPI schema, fetched on 2026-10-10 from
/// <c>https://fal.ai/api/openapi/queue/openapi.json?endpoint_id={id}</c>, plus the queue contract at
/// <c>fal.ai/docs/documentation/model-apis/inference/queue.md</c>.</b> Nothing here is inferred from
/// a sibling endpoint or remembered from training data — which matters, because sending a field an
/// endpoint does not define does not fail loudly at the vendor. It is ignored, and the caller gets a
/// plausible result for a request nobody made. That is why <see cref="Describe"/> exists and why an
/// unrecognised model throws rather than being passed through.
/// </para>
/// <para>
/// <b>The queue contract, once for every endpoint.</b> <c>POST https://queue.fal.run/{model-id}</c>
/// with <c>Authorization: Key &lt;FAL_KEY&gt;</c> and the endpoint's own input schema as the JSON
/// body, answering a <c>QueueStatus</c> that carries <c>request_id</c>, <c>status_url</c> and
/// <c>response_url</c>; then <c>GET</c> the status URL until it says <c>COMPLETED</c>; then <c>GET</c>
/// the response URL, which answers in that endpoint's output schema. The status vocabulary is
/// exactly <c>IN_QUEUE</c>, <c>IN_PROGRESS</c> and <c>COMPLETED</c> — an unrecognised value is
/// surfaced loudly rather than treated as "still working", because polling forever on an unknown
/// state burns the budget and then reports a timeout, hiding the one fact that explains it.
/// </para>
/// <para>
/// <b>Billing.</b> fal charges per successful output and not for server errors or queue time, and its
/// pricing note states that a model with no fixed per-output price "fall[s] back to per-second
/// billing based on the GPU machine type". None of the five endpoints below appears in fal's
/// published per-output price table, so that fallback is what this kind is priced from — see the
/// derivation and its stated assumption in the pre-catalog rate-card seed (ported once as price rows).
/// </para>
/// </remarks>
public class FalAiAnalysisClient : IImageAnalysisClient
{
    /// <summary>The public queue host. A provider row's endpoint overrides it.</summary>
    public const string DefaultEndpoint = FalQueueClient.DefaultEndpoint;

    /// <summary>Text-prompted subject segmentation, where <c>prompt</c> is required.</summary>
    public const string EvfSam = "fal-ai/evf-sam";

    /// <summary>Promptable segmentation seeded by boxes or points.</summary>
    public const string Sam2Image = "fal-ai/sam2/image";

    /// <summary>Background removal and matting, with a sub-model enum.</summary>
    public const string BirefnetV2 = "fal-ai/birefnet/v2";

    /// <summary>Depth estimation.</summary>
    public const string Depth = "fal-ai/imageutils/depth";

    /// <summary>Diffusion-based depth estimation.</summary>
    public const string MarigoldDepth = "fal-ai/imageutils/marigold-depth";

    /// <summary>
    /// Segment Anything Model 3.1 on a still image: text, click and box prompts, several candidate
    /// masks, per-mask scores and boxes. <b>The newest SAM on fal, and the default segmenter.</b>
    /// </summary>
    public const string Sam31Image = "fal-ai/sam-3-1/image";

    /// <summary>
    /// Segment Anything Model 3 on a still image: the same input and output schema as
    /// <see cref="Sam31Image"/> at half its per-request price ($0.005 against $0.01). Its schema is the
    /// one transcribed verbatim in <c>docs/fal-ai-models.md</c> section 1.
    /// </summary>
    public const string Sam3Image = "fal-ai/sam-3/image";

    /// <summary>
    /// The <c>fal-ai/birefnet/v2</c> <c>model</c> enum, verbatim from its schema. Validated rather
    /// than passed through: the vendor SILENTLY IGNORES an unknown enum value, so a typo would
    /// otherwise produce a cut-out from a different network than the one asked for.
    /// </summary>
    public static readonly IReadOnlyList<string> BirefnetVariants =
    [
        "General Use (Light)",
        "General Use (Light 2K)",
        "General Use (Heavy)",
        "Matting",
        "Portrait",
        "General Use (Dynamic)"
    ];

    /// <summary>The default poll budget.</summary>
    public static readonly TimeSpan DefaultPollTimeout = FalQueueClient.DefaultPollTimeout;

    /// <summary>The wait between polls, doubling up to a cap.</summary>
    public static readonly TimeSpan DefaultPollInterval = FalQueueClient.DefaultPollInterval;

    /// <summary>
    /// The newest-first segmentation model order the editor uses when a request names no model.
    /// </summary>
    public const string DefaultSegmentationModel = Sam31Image;

    /// <summary>The depth endpoint used when a request names none.</summary>
    public const string DefaultDepthModel = Depth;

    /// <summary>
    /// What each endpoint accepts, transcribed from its schema. This table is the reason an unknown
    /// model throws: everything the client sends is keyed off it.
    /// </summary>
    private static readonly Dictionary<string, EndpointSchema> Schemas = new(StringComparer.Ordinal)
    {
        [EvfSam] = new(EvfSam, SupportsSegmentation: true, SupportsDepth: false, TextPrompt: true,
            BoxPrompts: false, SeparateMatte: false, MaskOnly: true,
            "fal-ai/evf-sam schema: prompt (string, REQUIRED), negative_prompt, expand_mask, image_url " +
            "(string, REQUIRED), semantic_type, mask_only, blur_mask, revert_mask, use_grounding_dino, " +
            "fill_holes; output { image: File }. One image output, so the matte is only obtainable via " +
            "mask_only, which REPLACES the cut-out rather than accompanying it."),

        [Sam2Image] = new(Sam2Image, SupportsSegmentation: true, SupportsDepth: false, TextPrompt: false,
            BoxPrompts: true, SeparateMatte: false, MaskOnly: false,
            "fal-ai/sam2/image schema: sync_mode, prompts (PointPrompt[]), apply_mask, output_format " +
            "(jpeg|png|webp), image_url (REQUIRED), box_prompts (BoxPrompt[]); output { image: Image }. " +
            "No text prompt and no mask_only. The schema does NOT state the coordinate space of " +
            "box_prompts or prompts."),

        [Sam31Image] = Sam3Schema(Sam31Image),
        [Sam3Image] = Sam3Schema(Sam3Image),

        [BirefnetV2] = new(BirefnetV2, SupportsSegmentation: true, SupportsDepth: false, TextPrompt: false,
            BoxPrompts: false, SeparateMatte: true, MaskOnly: true,
            "fal-ai/birefnet/v2 schema: model (enum, default General Use (Light)), operating_resolution " +
            "(1024x1024|2048x2048|2304x2304), sync_mode, refine_foreground, image_url (REQUIRED), " +
            "mask_only, output_mask, output_format (webp|png|gif); output { image: ImageFile, " +
            "mask_image: ImageFile? }. The only one of the three with a SEPARATE matte output."),

        [Depth] = new(Depth, SupportsSegmentation: false, SupportsDepth: true, TextPrompt: false,
            BoxPrompts: false, SeparateMatte: false, MaskOnly: false,
            "fal-ai/imageutils/depth schema: bg_th (number), image_url (REQUIRED), depth_and_normal " +
            "(boolean), a (number); output { image: Image } described as The depth map."),

        [MarigoldDepth] = new(MarigoldDepth, SupportsSegmentation: false, SupportsDepth: true, TextPrompt: false,
            BoxPrompts: false, SeparateMatte: false, MaskOnly: false,
            "fal-ai/imageutils/marigold-depth schema: processing_res (default 0 = input size), image_url " +
            "(REQUIRED), num_inference_steps (default 10), ensemble_size (default 10); output " +
            "{ image: Image } described as The depth map."),
    };

    private readonly FalQueueClient _queue;

    /// <param name="probeModel">
    /// The endpoint id <see cref="ProbeAsync(CancellationToken)"/> submits against, taken from the
    /// provider row so a connection test checks the model an admin actually typed rather than some
    /// other endpoint that happens to be reachable.
    /// </param>
    public FalAiAnalysisClient(
        HttpClient httpClient,
        string apiKey,
        HttpClient? downloadClient = null,
        TimeSpan? pollInterval = null,
        TimeSpan? pollTimeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? probeModel = null)
    {
        _queue = new FalQueueClient(httpClient, apiKey, downloadClient, pollInterval, pollTimeout, delay);
        _probeModel = probeModel;
    }

    private readonly string? _probeModel;

    /// <summary>Every endpoint id this client has a read schema for, for a picker or a test.</summary>
    public static IReadOnlyCollection<string> KnownModels => Schemas.Keys;

    public ImageAnalysisModelCapabilities Describe(string model)
    {
        if (!Schemas.TryGetValue(model ?? string.Empty, out EndpointSchema? schema))
        {
            return new ImageAnalysisModelCapabilities(model ?? string.Empty, false, false, false, false,
                "unverified: no schema has been read for this model id, so nothing about it is claimed. " +
                "Endpoints with a verified schema: " + string.Join(", ", Schemas.Keys) + ".");
        }

        return new ImageAnalysisModelCapabilities(model, schema.TextPrompt, schema.BoxPrompts,
            schema.SeparateMatte, schema.MaskOnly, schema.Basis, schema.PointPrompts, schema.MultipleMasks);
    }

    public async Task<SegmentationResult> SegmentAsync(SegmentationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // A fal.ai provider row sends an EMPTY model: "use the catalogue's default endpoint". An explicit
        // model is the endpoint id and is refused below when no schema has been read for it.
        if (string.IsNullOrWhiteSpace(request.Model)) request = request with { Model = DefaultSegmentationModel };

        RequireSchema(request.Model, segmentation: true);
        RequireSource(request.SourceImageUrl);

        Dictionary<string, object?> body = BuildSegmentationBody(request);
        FalQueueResult queued = await _queue.SubmitAndPollAsync(request.Model, body, ct);
        JsonElement output = queued.Output;

        if (IsSam3(request.Model))
        {
            return await ReadSam3Async(output, queued.RequestId, request, ct);
        }

        GeneratedImage cutout = await _queue.ReadImageFieldAsync(output, "image", ct);

        // The matte is absent BY SCHEMA, not by failure: only birefnet/v2 documents a second output
        // field, so for the other two this is genuinely not returned rather than missing.
        GeneratedImage? matte = null;
        if (output.TryGetProperty("mask_image", out JsonElement mask) && mask.ValueKind == JsonValueKind.Object)
        {
            matte = await _queue.ReadImageObjectAsync(mask, ct);
        }

        return new SegmentationResult(cutout, matte, queued.RequestId) { ResolvedModel = request.Model };
    }

    public async Task<DepthResult> EstimateDepthAsync(DepthRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) request = request with { Model = DefaultDepthModel };

        RequireSchema(request.Model, segmentation: false);
        RequireSource(request.SourceImageUrl);

        Dictionary<string, object?> body = new() { ["image_url"] = request.SourceImageUrl };

        if (request.Model == MarigoldDepth)
        {
            if (request.ProcessingResolution is { } res) body["processing_res"] = res;
            if (request.InferenceSteps is { } steps) body["num_inference_steps"] = steps;
            if (request.EnsembleSize is { } ensemble) body["ensemble_size"] = ensemble;
        }
        else if (request.ProcessingResolution is not null
                 || request.InferenceSteps is not null
                 || request.EnsembleSize is not null)
        {
            throw new ArgumentException(
                "'" + request.Model + "' does not document processing_res, num_inference_steps or " +
                "ensemble_size; only '" + MarigoldDepth + "' does. Sending them anyway would be silently " +
                "ignored and the caller would believe it had tuned something.", nameof(request));
        }

        FalQueueResult queued = await _queue.SubmitAndPollAsync(request.Model, body, ct);
        GeneratedImage depthMap = await _queue.ReadImageFieldAsync(queued.Output, "image", ct);
        return new DepthResult(depthMap, queued.RequestId) { ResolvedModel = request.Model };
    }

    // ------------------------------------------------------------------ probe

    /// <summary>
    /// No-cost connectivity probe: it submits the ROW'S OWN model id with an EMPTY body.
    /// </summary>
    /// <remarks>
    /// Every one of the five schemas marks <c>image_url</c> required, so the request cannot be
    /// dispatched to a runner, cannot generate and cannot bill. What the answer proves is stated
    /// rather than implied: a <c>404</c> means the model id is not an endpoint fal routes (the single
    /// most useful thing a provider test can catch); a <c>401</c>/<c>403</c>, or a body naming an
    /// authorization problem, means the key was rejected; and a validation failure that is neither
    /// proves the endpoint exists and the key got past authentication. Anything else — including a
    /// 2xx, which would mean the queue accepted a request that cannot run — is reported as not ok,
    /// because a probe must fail closed.
    /// </remarks>
    public Task<ImageAnalysisProbeResult> ProbeAsync(CancellationToken ct) =>
        ProbeAsync(_probeModel ?? KnownModels.First(), ct);

    /// <summary>Probes one specific endpoint id.</summary>
    public async Task<ImageAnalysisProbeResult> ProbeAsync(string model, CancellationToken ct)
    {
        FalProbeOutcome outcome = await _queue.ProbeAsync(model, ct);
        return new ImageAnalysisProbeResult(outcome.IsOk, outcome.Reason);
    }

    // ------------------------------------------------------------------ internals

    private static EndpointSchema Sam3Schema(string id) => new(id,
        SupportsSegmentation: true, SupportsDepth: false, TextPrompt: true, BoxPrompts: true,
        SeparateMatte: true, MaskOnly: false,
        id + " schema (fal OpenAPI, 2026-10-10): image_url (REQUIRED), prompt (string, DEFAULT \"wheel\" - " +
        "never sent unless the caller supplied one), text_prompt (DEPRECATED, never sent), point_prompts " +
        "[{x,y,label 0|1}], box_prompts [{x_min,y_min,x_max,y_max}] (object_id and " +
        "frame_index exist in the schema but are not forwarded) (coordinates are INTEGERS with NO stated space - passed through verbatim as source " +
        "pixels), apply_mask, output_format jpeg|png|webp, return_multiple_masks, max_masks 1..32, " +
        "include_scores, include_boxes, sync_mode (never sent); output { image: Image?, masks: Image[], " +
        "metadata: [{index, score, box}], scores, boxes } where boxes are NORMALIZED [cx, cy, w, h]. " +
        "Billed per REQUEST, not per mask. Mask polarity and whether apply_mask returns an RGBA cut-out " +
        "or a visualisation are not stated by the schema.",
        PointPrompts: true, MultipleMasks: true);

    private static bool IsSam3(string model) => model is Sam31Image or Sam3Image;

    /// <summary>Reads the SAM 3 output: the optional composited <c>image</c> plus the required <c>masks</c>.</summary>
    /// <remarks>
    /// <para>
    /// <b>What a layer is made of.</b> A mask is a matte, not a cut-out, and this service has no raster
    /// library to apply one. So each result is a (bitmap, matte) pair the BROWSER composites
    /// (<c>destination-in</c>): the matte is the vendor's mask. The bitmap of the FIRST mask is the
    /// vendor's own <c>image</c> output ("Primary segmented mask preview"), as the contract specifies;
    /// every other mask has no vendor image of its own, so its bitmap is the SOURCE picture itself,
    /// fetched once through the SSRF-guarded download client (it is the same URL the vendor just fetched,
    /// so it is reachable). <b>What <c>image</c> actually is with <c>apply_mask</c> true - an RGBA cut-out
    /// or a mask visualisation - is NOT stated by the schema</b>; if a live call shows a preview, the
    /// first mask should use the source bitmap too (one line below) and this is on the measure list.
    /// </para>
    /// </remarks>
    private async Task<SegmentationResult> ReadSam3Async(
        JsonElement output, string requestId, SegmentationRequest request, CancellationToken ct)
    {
        if (!output.TryGetProperty("masks", out JsonElement masksElement)
            || masksElement.ValueKind != JsonValueKind.Array
            || masksElement.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                "fal.ai returned a SAM 3 result with no masks, so nothing was segmented: " +
                ImagePayload.Bounded(output.GetRawText()));
        }

        // Per-mask metadata arrives three ways (metadata[], scores[], boxes[]) depending on which
        // include_* flags the vendor honoured; metadata is the one that names its mask by index, so it
        // wins and the two parallel arrays fill the gaps by position.
        Dictionary<int, (double? Score, IReadOnlyList<double>? Box)> meta = [];
        if (output.TryGetProperty("metadata", out JsonElement metadata) && metadata.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in metadata.EnumerateArray())
            {
                int? index = FalQueueClient.ReadInt(item, "index");
                if (index is null) continue;
                meta[index.Value] = (FalQueueClient.ReadDouble(item, "score"), ReadNumberArray(item, "box"));
            }
        }

        bool multiple = request.ReturnMultipleMasks == true;
        int take = multiple ? masksElement.GetArrayLength() : 1;

        GeneratedImage? vendorImage = null;
        if (output.TryGetProperty("image", out JsonElement imageElement) && imageElement.ValueKind == JsonValueKind.Object)
        {
            vendorImage = await _queue.ReadImageObjectAsync(imageElement, ct);
        }

        GeneratedImage? source = null;
        async Task<GeneratedImage> SourceAsync()
        {
            if (source is not null) return source;
            try
            {
                return source = await _queue.DownloadImageAsync(request.SourceImageUrl, ct);
            }
            catch (InvalidOperationException ex)
            {
                // Loud on purpose: the only alternative is to give mask 2..n the first mask's bitmap, which
                // would be the WRONG picture and look like a model failure. The source URL is the
                // presigned GET the caller minted, so this happens when its host is not public (a
                // self-host object store on a private name) - the SSRF guard that protects vendor URLs
                // applies to it too.
                throw new InvalidOperationException(
                    "Could not fetch the source picture to use as the bitmap of the additional masks (" +
                    ex.Message + "). Request a single mask, or serve the source from a public host.", ex);
            }
        }

        List<SegmentedMask> masks = [];
        int position = 0;
        foreach (JsonElement maskElement in masksElement.EnumerateArray())
        {
            if (position >= take) break;

            GeneratedImage matte = await _queue.ReadImageObjectAsync(maskElement, ct);
            double? score = null;
            IReadOnlyList<double>? box = null;
            if (meta.TryGetValue(position, out (double? Score, IReadOnlyList<double>? Box) known))
            {
                score = known.Score;
                box = known.Box;
            }

            score ??= ReadParallelNumber(output, "scores", position);
            box ??= ReadParallelBox(output, "boxes", position);

            GeneratedImage bitmap = position == 0 && vendorImage is not null ? vendorImage : await SourceAsync();
            masks.Add(new SegmentedMask(bitmap, matte, score, box));
            position++;
        }

        SegmentedMask first = masks[0];
        return new SegmentationResult(first.Cutout, first.Matte, requestId)
        {
            Masks = multiple ? masks : null,
            ResolvedModel = request.Model
        };
    }

    private static IReadOnlyList<double>? ReadNumberArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out JsonElement array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<double> numbers = [];
        foreach (JsonElement n in array.EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out double d)) return null;
            numbers.Add(d);
        }

        return numbers;
    }

    private static double? ReadParallelNumber(JsonElement output, string name, int position)
    {
        if (!output.TryGetProperty(name, out JsonElement array)
            || array.ValueKind != JsonValueKind.Array
            || position >= array.GetArrayLength())
        {
            return null;
        }

        JsonElement item = array[position];
        return item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out double d) ? d : null;
    }

    private static IReadOnlyList<double>? ReadParallelBox(JsonElement output, string name, int position)
    {
        if (!output.TryGetProperty(name, out JsonElement array)
            || array.ValueKind != JsonValueKind.Array
            || position >= array.GetArrayLength()
            || array[position].ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<double> numbers = [];
        foreach (JsonElement n in array[position].EnumerateArray())
        {
            if (n.ValueKind != JsonValueKind.Number || !n.TryGetDouble(out double d)) return null;
            numbers.Add(d);
        }

        return numbers;
    }

    private sealed record EndpointSchema(
        string Id,
        bool SupportsSegmentation,
        bool SupportsDepth,
        bool TextPrompt,
        bool BoxPrompts,
        bool SeparateMatte,
        bool MaskOnly,
        string Basis,
        bool PointPrompts = false,
        bool MultipleMasks = false);

    private static EndpointSchema RequireSchema(string model, bool segmentation)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model id is required.", nameof(model));
        }

        if (!Schemas.TryGetValue(model, out EndpointSchema? schema))
        {
            throw new NotSupportedException(
                "No schema has been read for the fal.ai endpoint '" + model + "', so this client will not " +
                "send it a request: a field the endpoint does not define is SILENTLY IGNORED by the " +
                "vendor, and the caller would get a plausible result for a request nobody made. " +
                "Endpoints with a verified schema: " + string.Join(", ", Schemas.Keys) + ".");
        }

        if (segmentation && !schema.SupportsSegmentation)
        {
            throw new NotSupportedException("'" + model + "' is not a segmentation endpoint.");
        }

        if (!segmentation && !schema.SupportsDepth)
        {
            throw new NotSupportedException("'" + model + "' is not a depth endpoint.");
        }

        return schema;
    }

    private static void RequireSource(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException(
                "A source image URL is required: every fal endpoint takes 'image_url' and the vendor " +
                "fetches it itself.", nameof(url));
        }
    }

    private static void BuildSam3Body(SegmentationRequest request, Dictionary<string, object?> body)
    {
        bool hasPrompt = !string.IsNullOrWhiteSpace(request.Prompt);
        bool hasPoints = request.PointPrompts is { Count: > 0 };
        bool hasBoxes = request.BoxPrompts is { Count: > 0 } || request.Boxes is { Count: > 0 };

        if (!hasPrompt && !hasPoints && !hasBoxes)
        {
            // Not an oversight: with nothing to segment the vendor would fall back to its schema
            // default prompt, the string "wheel", and the caller would receive a mask of a wheel.
            throw new ArgumentException(
                "'" + request.Model + "' needs something to segment: a Prompt, a PointPrompts click or a " +
                "BoxPrompts box. Its schema defaults 'prompt' to \"wheel\", which this client never lets " +
                "reach the vendor.", nameof(request));
        }

        if (request.MaskOnly is not null || request.MatteToo == false || request.SemanticParts is not null || request.Variant is not null)
        {
            throw new ArgumentException(
                "'" + request.Model + "' always returns its masks and documents no mask_only, semantic or " +
                "variant switch, so MaskOnly/MatteToo=false/SemanticParts/Variant cannot be honoured.", nameof(request));
        }

        if (!string.IsNullOrWhiteSpace(request.NegativePrompt))
        {
            throw new ArgumentException("'" + request.Model + "' documents no negative prompt.", nameof(request));
        }

        if (request.MaxMasks is not null)
        {
            if (request.ReturnMultipleMasks != true)
            {
                throw new ArgumentException(
                    "MaxMasks only applies when ReturnMultipleMasks is set: the vendor documents max_masks as " +
                    "'the maximum number of masks to return when return_multiple_masks is enabled'.", nameof(request));
            }

            if (request.MaxMasks is < 1 or > 32)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.MaxMasks,
                    "max_masks is documented as an integer from 1 to 32.");
            }
        }

        // Sent ONLY when the caller supplied one - never the schema default.
        if (hasPrompt) body["prompt"] = request.Prompt;

        // Integers, verbatim. The schema declares them as integers and states no coordinate space, so
        // no scaling or normalisation is invented here; the live-verification step measures it.
        if (hasPoints)
        {
            body["point_prompts"] = request.PointPrompts!.Select(p =>
            {
                if (p.Label is not (0 or 1))
                {
                    throw new ArgumentException("A point prompt's label is 1 (foreground) or 0 (background).", nameof(request));
                }

                return new Dictionary<string, object?> { ["x"] = p.X, ["y"] = p.Y, ["label"] = p.Label };
            }).ToArray();
        }

        if (hasBoxes)
        {
            IEnumerable<BoxPrompt> boxes = (request.BoxPrompts ?? [])
                .Concat((request.Boxes ?? []).Select(b => new BoxPrompt(
                    (int)Math.Round(b.XMin), (int)Math.Round(b.YMin), (int)Math.Round(b.XMax), (int)Math.Round(b.YMax))));
            body["box_prompts"] = boxes.Select(b =>
            {
                return new Dictionary<string, object?>
                {
                    ["x_min"] = b.XMin,
                    ["y_min"] = b.YMin,
                    ["x_max"] = b.XMax,
                    ["y_max"] = b.YMax
                };
            }).ToArray();
        }

        body["apply_mask"] = true;
        body["output_format"] = "png";
        body["include_scores"] = true;
        body["include_boxes"] = true;
        if (request.ReturnMultipleMasks == true)
        {
            body["return_multiple_masks"] = true;
            if (request.MaxMasks is { } maxMasks) body["max_masks"] = maxMasks;
        }
    }

    private static Dictionary<string, object?> BuildSegmentationBody(SegmentationRequest request)
    {
        Dictionary<string, object?> body = new() { ["image_url"] = request.SourceImageUrl };

        if (request.Model == EvfSam)
        {
            // 'prompt' is REQUIRED by this endpoint's schema, so an absent one is refused here rather
            // than sent as null and rejected by the vendor after a round trip.
            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                throw new ArgumentException(
                    "'" + EvfSam + "' segments from a text prompt and its schema marks 'prompt' required. " +
                    "Supply the subject to cut out, for example 'the person'.", nameof(request));
            }

            body["prompt"] = request.Prompt;
            if (!string.IsNullOrWhiteSpace(request.NegativePrompt)) body["negative_prompt"] = request.NegativePrompt;
            if (request.SemanticParts is { } semantic) body["semantic_type"] = semantic;
            if (request.MaskOnly is { } maskOnly) body["mask_only"] = maskOnly;
        }
        else if (request.Model == Sam2Image)
        {
            // SAM 2 is PROMPTABLE: with neither a box nor a point it has nothing to segment. Refusing
            // is the honest answer — the alternative is a request that either errors at the vendor or
            // returns a mask for something nobody asked about.
            if (request.Boxes is null || request.Boxes.Count == 0)
            {
                throw new ArgumentException(
                    "'" + Sam2Image + "' is promptable and documents no text prompt, so it needs at least " +
                    "one box in Boxes. Use '" + EvfSam + "' to segment from words instead.", nameof(request));
            }

            body["apply_mask"] = true;
            body["output_format"] = "png";
            body["box_prompts"] = request.Boxes.Select(b => new Dictionary<string, object?>
            {
                // Passed through verbatim: the schema does not state the coordinate space, and an
                // invented conversion would put every cut-out in the wrong place while looking like a
                // model failure.
                ["x_min"] = (int)Math.Round(b.XMin),
                ["y_min"] = (int)Math.Round(b.YMin),
                ["x_max"] = (int)Math.Round(b.XMax),
                ["y_max"] = (int)Math.Round(b.YMax),
                ["frame_index"] = 0
            }).ToArray();
        }
        else if (IsSam3(request.Model))
        {
            BuildSam3Body(request, body);
        }
        else if (request.Model == BirefnetV2)
        {
            body["output_format"] = "png";
            // The matte is asked for by default on the one endpoint that has a SEPARATE field for it:
            // it is documented, it is the artefact a layer wants to be able to show, and the endpoint
            // already computed it.
            body["output_mask"] = request.MatteToo ?? true;
            if (request.MaskOnly is { } birefnetMaskOnly) body["mask_only"] = birefnetMaskOnly;

            if (!string.IsNullOrWhiteSpace(request.Variant))
            {
                if (!BirefnetVariants.Contains(request.Variant))
                {
                    throw new ArgumentException(
                        "'" + request.Variant + "' is not one of the model variants '" + BirefnetV2 +
                        "' documents: " + string.Join(", ", BirefnetVariants) + ". The vendor SILENTLY " +
                        "IGNORES an unknown enum value, so a typo here would produce a cut-out from a " +
                        "different network than the one asked for.", nameof(request));
                }

                body["model"] = request.Variant;
            }
        }

        // Everything the endpoint does not document is refused rather than dropped, so a caller never
        // believes it tuned something that was ignored.
        EndpointSchema schema = Schemas[request.Model];

        if (!schema.TextPrompt
            && (!string.IsNullOrWhiteSpace(request.Prompt) || !string.IsNullOrWhiteSpace(request.NegativePrompt)))
        {
            throw new ArgumentException(
                "'" + request.Model + "' documents no text prompt, so Prompt/NegativePrompt cannot be " +
                "honoured. Use '" + EvfSam + "' to segment from words.", nameof(request));
        }

        if (!schema.BoxPrompts && request.Boxes is { Count: > 0 })
        {
            throw new ArgumentException("'" + request.Model + "' documents no box prompts.", nameof(request));
        }

        if (!IsSam3(request.Model)
            && (request.PointPrompts is { Count: > 0 }
                || request.BoxPrompts is { Count: > 0 }
                || request.ReturnMultipleMasks is not null
                || request.MaxMasks is not null))
        {
            throw new ArgumentException(
                "'" + request.Model + "' documents no point prompts, BoxPrompts or multiple-mask output; only '" +
                Sam31Image + "' and '" + Sam3Image + "' do. Use Boxes for '" + Sam2Image + "'.", nameof(request));
        }

        if (!schema.SeparateMatte && request.MatteToo is not null)
        {
            throw new ArgumentException(
                "'" + request.Model + "' documents no separate matte output, so MatteToo cannot be " +
                "honoured. Set MaskOnly instead if the matte itself is what you want.", nameof(request));
        }

        return body;
    }
}
