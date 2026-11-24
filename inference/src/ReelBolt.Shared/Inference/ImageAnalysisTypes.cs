namespace ReelBolt.Shared.Inference;

/// <summary>
/// A subject to cut out, addressed the way the chosen endpoint addresses it: by a phrase, by a box,
/// or by neither (a matting model cuts out whatever the foreground is).
/// </summary>
/// <param name="Model">The vendor's endpoint id, e.g. <c>fal-ai/evf-sam</c>. The client dispatches
/// on it, so an id whose schema we have not read is REFUSED rather than guessed at.</param>
/// <param name="SourceImageUrl">
/// The image to cut from. A URL because that is what every endpoint we integrate documents
/// (<c>image_url</c>), and because the vendor fetches it itself.
/// </param>
/// <param name="Prompt">
/// What to cut out, in words: <c>"the person"</c>, <c>"the dog"</c>. Only the endpoints that
/// document a text prompt accept it; asking one that does not is an
/// <see cref="ArgumentException"/>, never a silently ignored field.
/// </param>
/// <param name="NegativePrompt">What to exclude, where the endpoint documents one.</param>
/// <param name="SemanticParts">
/// Ask for semantic-level segmentation (body parts, background, several objects) where the endpoint
/// documents it. <c>null</c> means "do not send the field at all", which is not the same as
/// <c>false</c> on an endpoint that documents no such field.
/// </param>
/// <param name="Boxes">Bounding boxes, where the endpoint documents box prompts. See
/// <see cref="NormalizedBox"/> for the coordinate-space caveat, which is real.</param>
/// <param name="MaskOnly">
/// Return the binary matte INSTEAD of the composited cut-out. Both endpoints that document it say
/// the same thing; it is not a request for "the mask as well" — that is
/// <see cref="SegmentationRequest.MatteToo"/> on the one endpoint that has it.
/// </param>
/// <param name="MatteToo">
/// Also return the binary matte, on the endpoint that documents a second output field for it.
/// <c>null</c> means leave the endpoint's own default alone.
/// </param>
/// <param name="Variant">
/// A sub-model on the same endpoint, where the endpoint documents one. <c>fal-ai/birefnet/v2</c>
/// carries a <c>model</c> enum whose members are training variants of the same network — notably
/// <c>"Matting"</c>, which is the one tuned for soft alpha edges such as hair, against
/// <c>"General Use (Light)"</c>, fal's own recommendation for most cases. Validated against that
/// documented enum, so a typo is refused instead of being silently ignored by the vendor.
/// </param>
/// <param name="PointPrompts">
/// Click prompts for an interactive segmenter (SAM family): points on the SOURCE IMAGE, in source
/// pixels, each labelled include (1) or exclude (0). <c>null</c>/empty means none. A client whose
/// endpoint documents no point prompt must refuse a non-empty list (<see cref="ArgumentException"/>)
/// rather than drop it. See <see cref="PointPrompt"/> for the coordinate-space caveat.
/// </param>
/// <param name="BoxPrompts">
/// Drag-a-box prompts, in source pixels (<see cref="BoxPrompt"/>). Distinct from the older
/// <paramref name="Boxes"/>, which carries a caller-measured, possibly normalised grid; the editor
/// always works in source pixels, so it uses this and leaves <paramref name="Boxes"/> alone.
/// </param>
/// <param name="ReturnMultipleMasks">
/// Ask the endpoint for every mask it can produce for the prompts, where it documents that, instead of
/// the single best one. <c>null</c> means "do not send the field". The result then carries them all in
/// <see cref="SegmentationResult.Masks"/>.
/// </param>
/// <param name="MaxMasks">
/// The most masks the caller will use. A client forwards it only where the endpoint documents a count
/// and otherwise truncates its own result to it; <c>null</c> leaves the endpoint's default alone.
/// </param>
public sealed record SegmentationRequest(
    string Model,
    string SourceImageUrl,
    string? Prompt = null,
    string? NegativePrompt = null,
    bool? SemanticParts = null,
    IReadOnlyList<NormalizedBox>? Boxes = null,
    bool? MaskOnly = null,
    bool? MatteToo = null,
    string? Variant = null,
    IReadOnlyList<PointPrompt>? PointPrompts = null,
    IReadOnlyList<BoxPrompt>? BoxPrompts = null,
    bool? ReturnMultipleMasks = null,
    int? MaxMasks = null);

/// <summary>
/// One click on the source image for an interactive segmenter.
/// </summary>
/// <param name="X">Horizontal position in SOURCE-IMAGE PIXELS.</param>
/// <param name="Y">Vertical position in SOURCE-IMAGE PIXELS.</param>
/// <param name="Label"><c>1</c> = this point is on the subject (include), <c>0</c> = it is not (exclude).</param>
/// <remarks>
/// <b>The coordinate space is the editor's statement, not the vendor's.</b> fal's schema for the SAM
/// endpoints declares these as integers and says nothing about pixels versus a normalised grid, so a
/// client passes them through verbatim and the live-verification step (docs/design/thumbnail-editor.md
/// section 8) measures what the endpoint actually expects. Nothing here converts them.
/// </remarks>
public sealed record PointPrompt(int X, int Y, int Label);

/// <summary>A drag-a-box prompt in SOURCE-IMAGE PIXELS; same coordinate-space caveat as <see cref="PointPrompt"/>.</summary>
public sealed record BoxPrompt(int XMin, int YMin, int XMax, int YMax);

/// <summary>
/// A bounding box on the source image.
/// </summary>
/// <remarks>
/// <b>The coordinate space is deliberately NOT asserted here, because the vendor does not state
/// one.</b> fal's OpenAPI schema for <c>fal-ai/sam2/image</c> declares <c>box_prompts</c> as four
/// integers (<c>x_min</c>, <c>y_min</c>, <c>x_max</c>, <c>y_max</c>) and <c>prompts</c> as
/// <c>x</c>/<c>y</c>/<c>label</c>, and says nothing about whether those are pixels or a normalised
/// grid. This type therefore carries whatever the caller measured and passes it through unchanged;
/// it is named <c>Normalized</c> for the convention our own <c>IObjectLocator</c> uses (a 0–1000
/// grid), and a caller feeding it from there must confirm the endpoint's expectation against a real
/// call before relying on it. An invented conversion here would put every cut-out in the wrong
/// place and look like a model failure.
/// </remarks>
public sealed record NormalizedBox(double XMin, double YMin, double XMax, double YMax);

/// <summary>
/// The result of a segmentation call: the subject, cut out.
/// </summary>
/// <param name="Cutout">
/// The subject on its own, with an alpha channel. This is the artefact a LAYER is made of, and it is
/// the reason this capability exists: the bytes here are what let a composite show a person and not
/// a rectangle of photograph. When the call returned several masks this is the FIRST one.
/// </param>
/// <param name="Matte">
/// The binary matte, when the endpoint returned one separately. <c>null</c> is a normal answer, not
/// a failure: <c>fal-ai/evf-sam</c> documents a single <c>image</c> output and no second field.
/// </param>
/// <param name="VendorRequestId">The vendor's request id, for a support ticket.</param>
/// <remarks>
/// <b>Several masks, additively.</b> A request with
/// <see cref="SegmentationRequest.ReturnMultipleMasks"/> can yield more than one cut-out.
/// <see cref="Masks"/> then lists every one in order (its first element mirrors
/// <see cref="Cutout"/>/<see cref="Matte"/>), and <see cref="AllMasks"/> is the single accessor a
/// consumer should use: it yields the multi-mask list when set and otherwise a one-element list of
/// <c>(Cutout, Matte)</c>, so a client that never sets <see cref="Masks"/> (every client written
/// before this member existed, and the three single-output fal endpoints) behaves exactly as before.
/// </remarks>
public sealed record SegmentationResult(GeneratedImage Cutout, GeneratedImage? Matte, string? VendorRequestId = null)
{
    /// <summary>
    /// Every mask the call produced, in order, when it produced more than one; <c>null</c> otherwise.
    /// Set by the client; consumers read <see cref="AllMasks"/> instead.
    /// </summary>
    public IReadOnlyList<SegmentedMask>? Masks { get; init; }

    /// <summary>
    /// The endpoint id that actually ran. A fal.ai row sends no model, so the client resolves the
    /// catalogue default; metering bills THIS, not the row's (empty) model name. Additive.
    /// </summary>
    public string? ResolvedModel { get; init; }

    /// <summary>
    /// Every mask the call produced: <see cref="Masks"/> when a client supplied it and it is not
    /// empty, else the single <see cref="Cutout"/>/<see cref="Matte"/> pair. Never empty.
    /// </summary>
    public IReadOnlyList<SegmentedMask> AllMasks =>
        Masks is { Count: > 0 } masks ? masks : [new SegmentedMask(Cutout, Matte)];
}

/// <summary>One cut-out and its optional separate binary matte, as one mask of a multi-mask result.</summary>
/// <param name="Cutout">The layer bitmap for this mask.</param>
/// <param name="Matte">The binary mask the layer is shown through.</param>
/// <param name="Score">The vendor's confidence for this mask when it was asked for, else <c>null</c>. Additive.</param>
/// <param name="BoxCxCyWh">
/// The vendor's box for this mask - <b>normalized centre-x, centre-y, width, height</b>, a different
/// convention from the pixel-space box prompts that went in - or <c>null</c>. Additive.
/// </param>
public sealed record SegmentedMask(
    GeneratedImage Cutout,
    GeneratedImage? Matte,
    double? Score = null,
    IReadOnlyList<double>? BoxCxCyWh = null);

/// <summary>A depth estimation request.</summary>
/// <param name="Model">The vendor's endpoint id, e.g. <c>fal-ai/imageutils/depth</c>.</param>
/// <param name="SourceImageUrl">The image to estimate depth for.</param>
/// <param name="ProcessingResolution">
/// Where the endpoint documents one. <c>null</c> means leave its default alone; on
/// <c>fal-ai/imageutils/marigold-depth</c> that default is documented as <c>0</c>, meaning "use the
/// size of the input image".
/// </param>
/// <param name="InferenceSteps">Denoising steps, where documented. More is slower and more accurate.</param>
/// <param name="EnsembleSize">Predictions to average, where documented.</param>
public sealed record DepthRequest(
    string Model,
    string SourceImageUrl,
    int? ProcessingResolution = null,
    int? InferenceSteps = null,
    int? EnsembleSize = null);

/// <summary>
/// A depth map: a greyscale image whose value is distance, not a mesh and not a point cloud.
/// </summary>
/// <remarks>
/// The output is an image because that is what both endpoints we integrate document returning
/// (<c>image: Image</c>, described as "The depth map"). Turning it into geometry is the caller's
/// job and is deliberately not faked here.
/// </remarks>
public sealed record DepthResult(GeneratedImage DepthMap, string? VendorRequestId = null)
{
    /// <summary>The endpoint id that actually ran (see <see cref="SegmentationResult.ResolvedModel"/>). Additive.</summary>
    public string? ResolvedModel { get; init; }
}

/// <summary>The outcome of a no-cost connectivity probe.</summary>
public sealed record ImageAnalysisProbeResult(bool IsOk, string? Reason);

/// <summary>
/// What one analysis endpoint can do, read from its own OpenAPI schema.
/// </summary>
/// <param name="TakesTextPrompt">The endpoint documents a natural-language subject prompt.</param>
/// <param name="TakesBoxPrompts">The endpoint documents bounding boxes.</param>
/// <param name="CanReturnMatte">The endpoint documents returning the binary matte separately.</param>
/// <param name="OutputIsMatteWhenMaskOnly">
/// The endpoint documents <c>mask_only</c>, where the SINGLE image output becomes the matte. This is
/// the flag that tells a caller whether a mask can be obtained at all.
/// </param>
/// <param name="Basis">Where every one of those facts was read, so a reviewer can check them.</param>
/// <param name="TakesPointPrompts">The endpoint documents click (point) prompts. Additive.</param>
/// <param name="CanReturnMultipleMasks">The endpoint documents returning several candidate masks. Additive.</param>
public sealed record ImageAnalysisModelCapabilities(
    string Model,
    bool TakesTextPrompt,
    bool TakesBoxPrompts,
    bool CanReturnMatte,
    bool OutputIsMatteWhenMaskOnly,
    string Basis,
    bool TakesPointPrompts = false,
    bool CanReturnMultipleMasks = false);
