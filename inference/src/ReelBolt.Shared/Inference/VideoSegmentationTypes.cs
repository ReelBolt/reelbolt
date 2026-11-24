namespace ReelBolt.Shared.Inference;

/// <summary>
/// One "look here on this frame too" hint for <see cref="IVideoSegmentationClient"/>, in NORMALISED
/// frame coordinates (0..1 of width/height, the same convention every rectangle in the video
/// artifact uses) — the optional refinement of a text prompt, e.g. a click on the speedometer when
/// the clip holds two similar gauges.
/// </summary>
/// <param name="X">Normalised x of the click, 0..1.</param>
/// <param name="Y">Normalised y of the click, 0..1.</param>
/// <param name="FrameIndex">0-based frame the hint applies to (usually the first frame the object is visible on).</param>
/// <param name="ObjectId">Which vendor object slot this hint belongs to; null lets the vendor assign one.</param>
/// <param name="Label">Optional positive/negative label as the vendor's schema defines it; null sends none.</param>
public sealed record VideoSegmentationPointPrompt(
    double X,
    double Y,
    int FrameIndex,
    int? ObjectId = null,
    string? Label = null);

/// <summary>
/// One box hint for <see cref="IVideoSegmentationClient"/> — the same normalised convention and the
/// same role as <see cref="VideoSegmentationPointPrompt"/>, for a customer who drew a rectangle
/// instead of clicking. Produced from an <see cref="ObjectTrackTarget.Region"/> (a normalized corner
/// quad) by taking the quad's bounding box and its centre.
/// </summary>
/// <param name="X">Normalised left edge, 0..1.</param>
/// <param name="Y">Normalised top edge, 0..1.</param>
/// <param name="Width">Normalised width, 0..1 (x + width &lt;= 1).</param>
/// <param name="Height">Normalised height, 0..1.</param>
/// <param name="FrameIndex">0-based frame the box applies to.</param>
/// <param name="ObjectId">Which vendor object slot this box belongs to; null lets the vendor assign one.</param>
/// <param name="Label">Optional positive/negative label; null sends none.</param>
public sealed record VideoSegmentationBoxPrompt(
    double X,
    double Y,
    double Width,
    double Height,
    int FrameIndex,
    int? ObjectId = null,
    string? Label = null);

/// <summary>
/// One segmentation call: the source, the prompt, the optional hints and the vendor knobs. Built by
/// the analyze path (T-004) AFTER the egress-consent check and the presigned GET, so
/// <see cref="VideoUrl"/> is always a short-TTL URL the caller created — this record never carries a
/// storage key, a bucket name or a long-lived credential.
/// </summary>
/// <param name="Model">The endpoint id to call (e.g. <c>fal-ai/sam-3-1/video-rle</c>). An implementation refuses an id whose schema it has not read rather than sending undocumented fields.</param>
/// <param name="VideoUrl">A presigned GET for the source clip, TTL under 15 minutes and scoped to <c>projects/{projectId}/</c>. Never logged verbatim.</param>
/// <param name="Prompt">The SANITIZED, length-capped object description ("speedometer"). Never a timestamp, never a path, never shell text — and it never reaches an ffmpeg argv; the model consumes it, the renderer does not.</param>
/// <param name="PointPrompts">Optional click hints; null sends none.</param>
/// <param name="BoxPrompts">Optional box hints; null sends none.</param>
/// <param name="MaxObjects">Most object slots the vendor may return, sent verbatim after clamping to 1..16 (<see cref="Shared.Workflows.SegmentationLimits"/>).</param>
/// <param name="DetectionThreshold">The vendor's own confidence floor, sent verbatim after clamping to 0.01..1.</param>
/// <param name="Timeout">Wall-clock budget for the whole call including polling; the implementation applies it to its HTTP client, never the ambient default.</param>
public sealed record VideoSegmentationRequest(
    string Model,
    Uri VideoUrl,
    string Prompt,
    IReadOnlyList<VideoSegmentationPointPrompt>? PointPrompts = null,
    IReadOnlyList<VideoSegmentationBoxPrompt>? BoxPrompts = null,
    int MaxObjects = 4,
    double DetectionThreshold = 0.5,
    TimeSpan? Timeout = null);

/// <summary>
/// One tracked object's mask on one frame, still run-length encoded: <see cref="Rle"/> is the
/// vendor's <c>"start length start length …"</c> string over a ROW-MAJOR index of a
/// <see cref="VideoSegmentationResult.Width"/>-wide frame (each pair is a run start followed by a
/// run length, and the runs are the 255 pixels). Decoded by first-party code, never by this record.
/// <para>
/// The START BASE is the vendor's to state and it does not state it: the "0-based" reading elsewhere
/// in this contract is a HYPOTHESIS the decoder must confirm empirically against the probed frame
/// size and row boundaries — not an assumption it may inherit. A wrong base shifts every mask by one
/// pixel, which no downstream check would catch, so a run that does not fit the grid must fail the
/// response rather than be nudged into it.
/// </para>
/// </summary>
/// <param name="ObjectId">The vendor's object slot this run belongs to; the analyze path selects "the object" by this id, and <c>g{n}</c> track ids are assigned per slot.</param>
/// <param name="Rle">The run-length string, verbatim. Never parsed here — decoding is a separate, offline-tested unit.</param>
public sealed record VideoSegmentationObjectRle(int ObjectId, string Rle);

/// <summary>
/// One tracked object's box on one frame, NORMALISED (<c>[x, y, w, h]</c> as fractions of the frame),
/// as the vendor reported it. Carried for the view/editor and for QA's centroid cross-check; never
/// used to render an effect (the mask is).
/// </summary>
public sealed record VideoSegmentationObjectBox(int ObjectId, double X, double Y, double Width, double Height);

/// <summary>
/// One frame of a segmentation answer: the per-object RLE masks, the per-object boxes when the vendor
/// returned them, and the vendor's own scores when it had any.
/// </summary>
/// <param name="FrameIndex">0-based frame index in the SOURCE clip, ascending across the result.</param>
/// <param name="Objects">One entry per object the model tracked on this frame; empty when nothing matched.</param>
/// <param name="Boxes">The vendor's normalised boxes for this frame, or null when it returned none.</param>
/// <param name="Scores">The vendor's per-object confidences in <paramref name="Objects"/> order, or null when the vendor returned none (the sibling initiative's real runs did). Null is "not reported", never zero.</param>
public sealed record VideoSegmentationFrame(
    int FrameIndex,
    IReadOnlyList<VideoSegmentationObjectRle> Objects,
    IReadOnlyList<VideoSegmentationObjectBox>? Boxes = null,
    IReadOnlyList<double>? Scores = null);

/// <summary>
/// Facts about the SHAPE of a vendor answer that are recorded rather than refused: the object slots the
/// vendor actually used, and how closely the decoded masks agree with its own boxes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither number is a correctness measure (D-05).</b> <c>max_num_objects</c> is a request parameter
/// the vendor does not treat as a cap on the slots it returns: a real answer to <c>max_num_objects: 4</c>
/// carried 6–10 slots on every one of its 141 frames (F2/FACE, 2026-10-18). And the vendor's boxes are
/// centre-anchored, approximate METADATA, never the measure of a mask: on an object that touches the
/// frame edge the box is clipped, so a mask that decodes byte for byte still falls short of it — 84.83%
/// of the mask inside the box on F1/FACE. Both facts are reported here, so the analyze path can say what
/// it saw. The reader refuses only structural RLE errors and its two documented work bounds.
/// </para>
/// </remarks>
/// <param name="ObservedMaxObjectSlots">Most object slots the vendor used on any single frame; 0 when every frame is empty.</param>
/// <param name="TotalObjectSlots">Object slots across every frame — the inner factor of the decode work.</param>
/// <param name="BoxAgreementMin">Smallest percentage of a decoded mask that fell inside that object's own vendor box grown by two pixels, or null when the document carried no boxes.</param>
/// <param name="BoxAgreementFrame">Frame of the least-agreeing mask; null together with <paramref name="BoxAgreementMin"/>.</param>
/// <param name="BoxAgreementObject">Object slot of the least-agreeing mask; null together with <paramref name="BoxAgreementMin"/>.</param>
/// <param name="FramesPadded">
/// Trailing frames the vendor did not answer and the reader held the last real mask over. Zero when the
/// answer was not short. The padding is a copy of the last mask, never a resample or an interpolation.
/// </param>
/// <param name="FramesTruncated">
/// Trailing frame entries the vendor answered beyond the probe's count and the reader dropped. Zero when
/// the answer was not long. The dropped entries are never decoded.
/// </param>
/// <param name="FramesOverrunClamped">
/// Object masks whose final run overshot the frame end by at most <see cref="Sam31VideoRleDecoder.MaxTolerableOverrunPixels"/>
/// and was clamped to it. Zero when the vendor's runs were all in range.
/// </param>
/// <param name="MaxOverrunPixelsClamped">The largest such overshoot, in pixels.</param>
public sealed record VideoSegmentationDiagnostics(
    int ObservedMaxObjectSlots,
    long TotalObjectSlots,
    double? BoxAgreementMin = null,
    int? BoxAgreementFrame = null,
    int? BoxAgreementObject = null,
    int FramesPadded = 0,
    int FramesTruncated = 0,
    int FramesOverrunClamped = 0,
    int MaxOverrunPixelsClamped = 0);

/// <summary>
/// A whole segmentation answer, RAW and pre-decode. Validated for shape by the caller (T-003's
/// decoder + T-004's analyzer), which is where <c>SEGMENTATION_BAD_RESPONSE</c> is raised.
/// </summary>
/// <param name="Model">The endpoint id that answered, echoed from the request.</param>
/// <param name="Width">Frame width the RLE row-major index is over, as the vendor reported it — the decoder MUST use this, never a re-derived size.</param>
/// <param name="Height">Frame height the RLE index is over.</param>
/// <param name="Frames">Frames in ascending <see cref="VideoSegmentationFrame.FrameIndex"/> order.</param>
/// <param name="ProviderFingerprint">Provider id + kind + model (no key material) — stamped onto the artifact track and hashed into the step-cache key.</param>
/// <param name="CostUsd">What the vendor says the call cost, when it says; null is "not reported", never zero.</param>
/// <param name="InputFrames">Frames of video input the vendor billed for (round the caller's own count up to its 16-frame unit). Null when the vendor did not report it.</param>
/// <param name="Diagnostics">What the vendor's answer looked like, recorded for the analyze path to report; null when a caller built the result without them.</param>
public sealed record VideoSegmentationResult(
    string Model,
    int Width,
    int Height,
    IReadOnlyList<VideoSegmentationFrame> Frames,
    string? ProviderFingerprint = null,
    decimal? CostUsd = null,
    int? InputFrames = null,
    VideoSegmentationDiagnostics? Diagnostics = null);
