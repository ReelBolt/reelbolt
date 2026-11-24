namespace ReelBolt.Shared.Inference;

/// <summary>
/// A still-image generation request: text, optionally steered by an aspect ratio or an explicit
/// pixel size and optionally seeded by ONE reference image whose subject should be preserved.
/// </summary>
/// <param name="Model">The vendor's model name. Never a free-form string on the wire path: the
/// client passes it through verbatim and the vendor rejects an unknown one, which is the only
/// honest behaviour — we do not maintain a per-vendor model allowlist that would go stale.</param>
/// <param name="Prompt">The text prompt. Already through <see cref="GeneratedPromptSanitizer"/>.</param>
/// <param name="AspectRatio">
/// A vendor-shaped ratio string (<c>"16:9"</c>, <c>"1:1"</c>) where the vendor documents one.
/// Deliberately a string rather than an enum: the vocabularies differ between vendors (MiniMax
/// publishes eight ratios, OpenAI publishes none and uses explicit sizes instead) and folding them
/// into one enum would either lose options or invent them.
/// </param>
/// <param name="Width">Explicit width in pixels, where the vendor documents one.</param>
/// <param name="Height">Explicit height in pixels, where the vendor documents one.</param>
/// <param name="Count">
/// How many images to return. Clamped per vendor by <see cref="ImageModelCapabilities"/>, never
/// silently truncated: a caller asking for more than the vendor allows gets a thrown
/// <see cref="ArgumentOutOfRangeException"/> rather than a different number of images than it
/// budgeted for.
/// </param>
/// <param name="ReferenceImageUrl">
/// A publicly reachable URL of a source image whose subject should be preserved. MiniMax documents
/// this as <c>subject_reference</c> (one reference image per request) and it must be a URL — the
/// vendor fetches it, so an internal or presigned-to-a-private-host URL will simply fail at
/// MiniMax. Documents nothing about <c>null</c>: no reference means pure text-to-image.
/// </param>
/// <param name="Seed">A vendor seed where documented, for reproducibility.</param>
/// <param name="Params">
/// Endpoint-specific fields exactly as the chosen endpoint's own schema names them (for example a fal
/// <c>quality</c> or <c>resolution</c> tier). A client forwards only fields its schema documents and
/// refuses the rest, so a typo cannot be silently dropped by the vendor. Additive: the clients that
/// predate it ignore it by never being handed one.
/// </param>
public sealed record ImageGenerationRequest(
    string Model,
    string Prompt,
    string? AspectRatio = null,
    int? Width = null,
    int? Height = null,
    int Count = 1,
    string? ReferenceImageUrl = null,
    int? Seed = null,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Params = null)
{
    /// <summary>
    /// T-013 rework 3: the output size the caller expected, in pixels, when it sized a charge from it. The meter never
    /// bills a measured output above <see cref="Metering.OutputSizePricing.ExpectedSizeClampFactor"/> times this. Additive.
    /// </summary>
    public long? ExpectedOutputPixels { get; init; }
}

/// <summary>
/// An instruction edit: an existing image plus a sentence describing the change. The source is a
/// URL because that is the shape every vendor we integrate documents for the instruction path —
/// MiniMax takes <c>subject_reference[].image_file</c>, BFL takes <c>input_image</c>, and OpenAI's
/// edits endpoint takes <c>images[].image_url</c>, which accepts "a fully qualified URL". A client
/// that needs bytes (BFL's mask-based Fill route) fetches the URL through the SSRF-guarded download
/// client rather than being handed the bytes by the caller, so there is exactly one egress point.
/// </summary>
/// <param name="Params">Endpoint-specific fields, as for <see cref="ImageGenerationRequest.Params"/>.</param>
public sealed record ImageEditRequest(
    string Model,
    string Prompt,
    string SourceImageUrl,
    string? AspectRatio = null,
    int? Seed = null,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Params = null,
    /// <summary>
    /// FURTHER pictures the edit is made against, in the caller's order: "replace the face in this picture
    /// with the face from the second one". They are appended to the endpoint's image array after the
    /// picture being edited, which is always FIRST, and are only sent to an endpoint whose schema
    /// documents an array — an endpoint that takes a single image refuses them rather than silently
    /// starting the edit from a different picture.
    /// </summary>
    IReadOnlyList<string>? ReferenceImageUrls = null)
{
    /// <summary>
    /// T-013 (F5): the pixel size of each of <see cref="ReferenceImageUrls"/>, in the same order, measured by the caller
    /// before the call. A per-megapixel endpoint bills input megapixels the output does not show, so the meter adds
    /// each reference's megapixels to the output's. Null (or a count that does not match) = not measured. Additive.
    /// </summary>
    public IReadOnlyList<long>? ReferenceImagePixels { get; init; }

    /// <summary>
    /// T-013 rework 3: the output size the caller expected, in pixels, when it sized a charge from it. The meter never
    /// bills a measured output above <see cref="Metering.OutputSizePricing.ExpectedSizeClampFactor"/> times this. Additive.
    /// </summary>
    public long? ExpectedOutputPixels { get; init; }

    /// <summary>
    /// The pictures are a person's (an avatar's references, docs/design/avatars.md phase 3): a client whose vendor
    /// documents a "do not keep the inputs" switch sends it (fal: <c>X-Fal-Store-IO: 0</c>). Default false keeps every
    /// existing edit byte-identical. Additive.
    /// </summary>
    public bool NoVendorRetention { get; init; }
}

/// <summary>
/// A mask-based inpaint: the source image plus a mask. <b>Black areas are preserved and white areas
/// are regenerated</b> — that is BFL's documented convention for FLUX.1 Fill ("black areas will be
/// preserved while white areas will be inpainted") and it is the convention this record states,
/// because a mask is otherwise just an image with no defined meaning.
/// </summary>
/// <param name="SourceImageUrl">The image the masked region is cut out of.</param>
/// <param name="MaskBytes">The mask image itself, supplied as bytes rather than a URL: the caller
/// (the editor) has just produced it, it is not uploaded anywhere yet, and round-tripping it
/// through object storage to get a URL the vendor can fetch would be a second place to get the
/// lifetime and access policy wrong.</param>
/// <param name="MaskContentType">The mask's media type; PNG is the only one BFL documents.</param>
/// <param name="MaskUrl">
/// A URL the vendor can fetch the mask from. fal.ai takes masks ONLY by URL (<c>mask_url</c>), and the
/// server holds no upload path of its own to turn <paramref name="MaskBytes"/> into one, so the caller
/// (which has just stored the browser's mask and can mint a presigned GET) supplies it; a fal endpoint
/// handed bytes and no URL throws rather than guessing. <paramref name="MaskBytes"/> may then be empty.
/// </param>
/// <param name="MaskMeansPreserve">
/// <c>false</c> (the house convention): white pixels are the area to act on. <c>true</c>: the mask
/// supplied is already INVERTED, white = keep. Exists because one fal endpoint
/// (<c>ideogram/v4.5/edit</c>: "Black edits, white preserves") states the opposite polarity and the
/// server has no raster library to invert a mask — such an endpoint is refused unless this is
/// <c>true</c>, and every white-acts endpoint is refused when it is, so a mask is never silently
/// sent the wrong way round.
/// </param>
/// <param name="Params">Endpoint-specific fields, as for <see cref="ImageGenerationRequest.Params"/>.</param>
public sealed record ImageInpaintRequest(
    string Model,
    string Prompt,
    string SourceImageUrl,
    byte[] MaskBytes,
    string MaskContentType = "image/png",
    int? Seed = null,
    string? MaskUrl = null,
    bool MaskMeansPreserve = false,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Params = null)
{
    /// <summary>
    /// T-013 rework 3: the output size the caller expected, in pixels, when it sized a charge from it. The meter never
    /// bills a measured output above <see cref="Metering.OutputSizePricing.ExpectedSizeClampFactor"/> times this. Additive.
    /// </summary>
    public long? ExpectedOutputPixels { get; init; }
}

/// <summary>
/// One image, already materialised as bytes.
/// </summary>
/// <remarks>
/// <b>Always bytes, never a URL the caller has to fetch later.</b> Every vendor we integrate hands
/// back a link that is either short-lived or unusable from a browser: MiniMax's
/// <c>response_format: url</c> links "expire in 24 hours", and BFL's <c>result.sample</c> URLs are
/// valid for <b>10 minutes</b> and are explicitly not CORS-enabled — their own documentation says
/// "we recommend downloading the image and re-serving it from your own infrastructure". Returning
/// the link and hoping a later caller fetches it in time is the bug this type exists to prevent,
/// so the client downloads and the caller receives pixels. <see cref="VendorUrl"/> is kept only for
/// diagnostics and is never a delivery mechanism.
/// </remarks>
public sealed record GeneratedImage(
    byte[] Bytes,
    string ContentType,
    string? RevisedPrompt = null,
    string? VendorUrl = null)
{
    /// <summary>The image's pixel size, or <c>null</c> when the vendor did not state one.</summary>
    public int? Width { get; init; }

    /// <summary>The image's pixel size, or <c>null</c> when the vendor did not state one.</summary>
    public int? Height { get; init; }
}

/// <summary>Every image one call produced, plus the vendor's own request id when it gave one.</summary>
/// <param name="Images">In the order the vendor returned them. An empty list is a failure the caller
/// must treat as one — a 200 carrying no image is not a success.</param>
/// <param name="VendorRequestId">
/// The vendor's trace id (MiniMax's <c>id</c>, BFL's <c>id</c>). Carried because it is the only
/// thing a support ticket can be opened with.
/// </param>
public sealed record GeneratedImageSet(IReadOnlyList<GeneratedImage> Images, string? VendorRequestId = null)
{
    /// <summary>
    /// The model that actually produced the images, set by a client that chooses the endpoint per call
    /// (fal.ai: a row sends an empty model and the client applies the catalogue's default for the
    /// operation). Metering bills THIS model - not the provider row's - and provenance should record it.
    /// <c>null</c> for clients whose model is simply the request's. Additive.
    /// </summary>
    public string? ResolvedModel { get; init; }
}

/// <summary>
/// The result of a no-cost connectivity probe.
/// </summary>
/// <param name="IsOk">
/// <c>true</c> only on an answer that PROVES the endpoint is the vendor's image API and the key was
/// accepted. A network error, a timeout, a 5xx, a 408/429 and any unrecognised body are all
/// <c>false</c> — see each client's <c>ProbeAsync</c> for what it sends and why that answer is
/// conclusive.
/// </param>
/// <param name="Reason">A sentence an admin can act on, or <c>null</c> when <see cref="IsOk"/>.</param>
public sealed record ImageProbeResult(bool IsOk, string? Reason);

/// <summary>
/// What a specific image model can do, read from the vendor's own documentation.
/// </summary>
/// <param name="Model">The model this describes.</param>
/// <param name="SupportsInstructionEdit">Instruction edit (an image plus a sentence) is documented.</param>
/// <param name="SupportsMaskInpaint">Mask-based inpainting is documented.</param>
/// <param name="MaxImagesPerRequest">
/// The vendor's documented maximum for <see cref="ImageGenerationRequest.Count"/>, or <c>1</c> when
/// it documents a single image per call.
/// </param>
/// <param name="MaxPromptLength">
/// The vendor's documented prompt cap in characters, or
/// <see cref="GeneratedPromptSanitizer.DefaultMaxPromptLength"/> when it documents none — a
/// positive default, never <c>0</c>, for the reason that type's own remarks give.
/// </param>
/// <param name="Basis">
/// Where every one of these facts was read. Load-bearing: an unverified capability is reported as
/// unsupported rather than guessed, and this string is what lets a reviewer check that.
/// </param>
/// <param name="Options">
/// The closed vocabularies the model documents, by field name (for example fal's relighting
/// <c>lighting_style</c> or an upscaler's <c>model</c>), so a UI can render a dropdown from the
/// client instead of keeping a second copy of the enum. <c>null</c> when the model documents none.
/// </param>
public sealed record ImageModelCapabilities(
    string Model,
    bool SupportsInstructionEdit,
    bool SupportsMaskInpaint,
    int MaxImagesPerRequest,
    int MaxPromptLength,
    string Basis,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Options = null)
{
    /// <summary>
    /// Whether the model documents a prompt at all, or <c>null</c> when that is not established. A UI shows
    /// no prompt field for <c>false</c>, because a client refuses a prompt the model cannot honour.
    /// </summary>
    public bool? TakesPrompt { get; init; }

    /// <summary>What an expand model takes and how far it can grow a picture, or <c>null</c> for any other model.</summary>
    public ImageExpandCapabilities? Expand { get; init; }
}

/// <summary>What an expand (outpaint) model takes, read from its schema and its vendor's documented limits.</summary>
/// <param name="Geometry">
/// <c>"margins"</c> (pixels per side, the input kept at its own size) or <c>"canvas"</c> (the finished canvas
/// plus where the original goes on it, the vendor scaling the input itself).
/// </param>
/// <param name="MaxMarginPx">The per-side cap of a margins model, or <c>null</c>.</param>
/// <param name="MaxOutputPixels">The largest finished picture, in pixels, or <c>null</c> when none is documented.</param>
/// <param name="MaxInputPixels">The largest input taken as it is, in pixels, or <c>null</c> when none is documented.</param>
/// <param name="Basis">Where the limits were read.</param>
public sealed record ImageExpandCapabilities(
    string Geometry,
    int? MaxMarginPx,
    long? MaxOutputPixels,
    long? MaxInputPixels,
    string? Basis);

/// <summary>
/// Where the original picture goes on an expanded canvas, in the pixels of the FINISHED picture the request
/// asks for: the canvas is <paramref name="CanvasWidth"/> x <paramref name="CanvasHeight"/> and the original
/// (scaled to <paramref name="ImageWidth"/> x <paramref name="ImageHeight"/>) has its top-left corner at
/// (<paramref name="ImageX"/>, <paramref name="ImageY"/>). Built by <see cref="ExpandPlanner"/>.
/// </summary>
/// <remarks>
/// A margins endpoint keeps the input at the size it was sent, so for one the image size IS the size of the
/// picture sent and the margins are what is left around it. A canvas endpoint scales the input to the image
/// size itself.
/// </remarks>
public sealed record ImageExpandGeometry(int CanvasWidth, int CanvasHeight, int ImageX, int ImageY, int ImageWidth, int ImageHeight)
{
    /// <summary>Pixels added to the left.</summary>
    public int Left => ImageX;

    /// <summary>Pixels added at the top.</summary>
    public int Top => ImageY;

    /// <summary>Pixels added to the right.</summary>
    public int Right => CanvasWidth - ImageX - ImageWidth;

    /// <summary>Pixels added at the bottom.</summary>
    public int Bottom => CanvasHeight - ImageY - ImageHeight;
}

/// <summary>
/// The whole-image transformations the thumbnail editor offers beyond generation, instruction
/// editing and mask inpainting (docs/design/thumbnail-editor.md §5). <b>Append-only</b>: persisted
/// by name in a layer's provenance JSON, so a member is never renamed or reordered.
/// </summary>
public enum ImageOperation
{
    /// <summary>Outpaint the picture to larger bounds.</summary>
    Expand,
    /// <summary>Keep the foreground, regenerate the background from a prompt.</summary>
    ReplaceBackground,
    /// <summary>Change the lighting of the picture from a prompt or parameters.</summary>
    Relight,
    /// <summary>Raise the resolution.</summary>
    Upscale,
}

/// <summary>
/// One <see cref="ImageOperation"/> on one picture the vendor can fetch at <paramref name="ImageUrl"/>
/// (a presigned GET the caller minted). <paramref name="Params"/> carries the operation-specific
/// fields exactly as the chosen endpoint's own schema names them (e.g. <c>factor</c>,
/// <c>top</c>/<c>right</c>/<c>bottom</c>/<c>left</c>); a client forwards only the fields the
/// endpoint documents and refuses the rest, so a typo cannot be silently dropped by the vendor.
/// <paramref name="Expand"/> is the planned geometry of an expand (<see cref="ExpandPlanner"/>); when it is
/// set the client writes the endpoint's own geometry fields from it and refuses margins in
/// <paramref name="Params"/> as well.
/// </summary>
public sealed record ImageTransformRequest(
    ImageOperation Operation,
    string Model,
    string ImageUrl,
    string? Prompt = null,
    string? NegativePrompt = null,
    IReadOnlyDictionary<string, System.Text.Json.JsonElement>? Params = null,
    int? Seed = null,
    ImageExpandGeometry? Expand = null)
{
    /// <summary>
    /// T-013 rework 3: the output size the caller expected, in pixels, when it sized a charge from it. The meter never
    /// bills a measured output above <see cref="Metering.OutputSizePricing.ExpectedSizeClampFactor"/> times this. Additive.
    /// </summary>
    public long? ExpectedOutputPixels { get; init; }
}
