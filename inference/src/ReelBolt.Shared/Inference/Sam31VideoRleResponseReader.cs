using System.Text.Json;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Maps a <c>fal-ai/sam-3-1/video-rle</c> output document into the platform's
/// <see cref="VideoSegmentationResult"/>, decoding every mask and refusing anything the vendor's own
/// schema does not support.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape, read from the real responses (T-002's replay pack, 2026-10-18).</b>
/// <c>{ rle: string[], objects: {object_id, rle, box}[][], metadata: {index, score, box}[], boxes:
/// number[][], scores: number[]? }</c>. <c>rle[i]</c> duplicates the first object's mask;
/// <c>objects[i][j].box</c> is the PER-OBJECT box; <c>boxes[i]</c> and <c>metadata[i].box</c> are the
/// UNION box over every object on that frame. The document states no frame width, height, frame count,
/// cost or per-object score, which is why the client must be given the geometry by its caller and why
/// <see cref="VideoSegmentationResult.InputFrames"/> is derived from the number of frame entries (the
/// vendor returns one per input frame).
/// </para>
/// <para>
/// <b>Boxes are CENTRE-anchored</b> <c>[cx, cy, w, h]</c>, normalised, not the corner-anchored
/// <c>[x, y, w, h]</c> the record is named after: measured on all three fixtures, the centre reading
/// matches the mask's own centroid to a median 3–6% of the diagonal, while reading the first two numbers
/// as a corner puts the box a half-width/height away (median ~50% of the frame width) — the same
/// finding T-002 recorded. <see cref="VideoSegmentationObjectBox"/> is corner-anchored by its own field
/// names, so this reader converts (<c>X = cx - w/2</c>, <c>Y = cy - h/2</c>) and clamps a hairline
/// overshoot to the frame edge.
/// </para>
/// <para>
/// <b>Box agreement is a DIAGNOSTIC, never a refusal (D-05).</b> The decoded mask is still compared with
/// the object's own vendor box grown by <see cref="BoxGrowPixels"/> px, and the worst agreement over the
/// document is reported on <see cref="VideoSegmentationResult.Diagnostics"/>. It cannot be a gate: the
/// vendor's boxes are centre-anchored, approximate metadata, and on an object that touches the frame edge
/// the box is clipped, so an ordinary, correct answer falls short of it — F1/FACE (2026-10-18) decodes to
/// 84.83% at frame 132 object 2 while its masks are exactly the vendor's. <see cref="MinBoxAgreementPercent"/>
/// survives as the reference value the observed minimum is measured against, not as a floor.
/// </para>
/// <para>
/// <b>Object slots are bounded structurally, not by the request.</b> The vendor does not treat
/// <c>max_num_objects</c> as a hard cap on the slots it returns: F2/FACE answered a request for 4 with
/// 6–10 slots on every frame, and those extra slots are ordinary output, not a refusal. What is refused,
/// before a single run is decoded, is work this reader cannot bound: more than
/// <see cref="MaxObjectSlots"/> slots on one frame, or a frames × total-slots product over
/// <see cref="MaxFrameSlotWork"/>.
/// </para>
/// </remarks>
public static class Sam31VideoRleResponseReader
{
    /// <summary>Pixels of slack added around the vendor's box before counting mask pixels.</summary>
    public const int BoxGrowPixels = 2;

    /// <summary>
    /// The reference value the reported <see cref="VideoSegmentationDiagnostics.BoxAgreementMin"/> is read
    /// against — NOT a floor. It was a refusal until T-015: the vendor's boxes are approximate metadata and
    /// are clipped on an edge-touching object, so a valid answer (F1/FACE, 84.83%) sat below it. Measured on
    /// the committed R1/R10 fixtures the worst frame is 98.82%, which is why the old floor looked safe.
    /// </summary>
    public const double MinBoxAgreementPercent = 95.0;

    /// <summary>
    /// The hard ceiling on object slots in ONE frame. <c>max_num_objects</c> is only what the caller asked
    /// for: F2/FACE answered a request for 4 with 6–10 slots on all 141 frames, so the request's number is
    /// not a refusal (T-015). This ceiling is structural — nothing legitimate has been measured anywhere
    /// near it, and it is what keeps the per-frame decode work bounded.
    /// </summary>
    public const int MaxObjectSlots = 32;

    /// <summary>
    /// The bound on total decode work: the SUM of object slots over the whole document, because a slot is
    /// one run and a run is decoded once. <see cref="MaxObjectSlots"/> bounds each frame and this bounds
    /// the document. It is deliberately not a <c>frames × slots</c> product: that counts one slot once per
    /// frame, and refused an ordinary 825-frame answer holding about one slot per frame at 735 900
    /// "frame-slots" for 892 real runs (2026-10-19).
    /// </summary>
    public const long MaxFrameSlotWork = 250_000;

    /// <summary>
    /// The absolute ceiling on the frame entries this reader will look at — 600 s
    /// (<c>SegmentationLimits.MaxSeconds</c>) at 30 fps (the rate the segmentation credit hold prices,
    /// <c>SegmentationCreditPricing.AssumedMaxFps</c>), i.e. the largest frame count any configured
    /// segmentation cap can buy. It is only the bound when the caller's probe could not state the clip's
    /// own frame count; when it could, that count bounds it instead, within <see cref="FrameToleranceFraction"/>.
    /// </summary>
    /// <remarks>
    /// <b>Why a bound at all.</b> The frame count is the outer factor of this reader's cost — every frame
    /// is a second full pass over <see cref="VideoSegmentationMask"/> pixels per object — and the vendor
    /// chooses it. <c>Sam31VideoRleDecoder</c> refuses a malformed run, but a document of a million
    /// well-formed frames decodes for hours. Refusing here, before the first run is decoded, turns an
    /// OOM or a hung worker into one <c>SEGMENTATION_BAD_RESPONSE</c>.
    /// </remarks>
    public const int MaxFrames = 18_000;

    /// <summary>
    /// The band, as a fraction of the probe's count, inside which a vendor answer's frame count is
    /// accepted: 5% rounded UP (a 20-frame clip tolerates one frame either way, a 100-frame clip five).
    /// Inside the band a short answer is padded with the last mask and a long one truncated; outside it
    /// the document is refused. See <see cref="FrameTolerance"/>.
    /// </summary>
    public const double FrameToleranceFraction = 0.05;

    /// <summary>The absolute frame-count tolerance for a probe that measured <paramref name="probedFrames"/>.</summary>
    public static int FrameTolerance(int probedFrames) =>
        (int)Math.Ceiling(Math.Max(0, probedFrames) * FrameToleranceFraction);

    /// <summary>
    /// Reads and validates one output document.
    /// </summary>
    /// <remarks>
    /// <b>The frame count is a precondition with a 5% tolerance; the slot counts are work bounds.</b> A
    /// document whose count is within <see cref="FrameTolerance"/> of the caller's probe is accepted — a
    /// short answer padded at the tail with its last mask, a long one truncated to the probe's count —
    /// and anything outside the band is refused BEFORE any run is decoded. Object slots are NOT
    /// compared with the request: the vendor returns more slots than <c>max_num_objects</c> asked for
    /// (measured, T-015), so the request's number cannot refuse an answer. What is refused, also before the
    /// first run, is work that cannot be bounded — <see cref="MaxObjectSlots"/> on one frame and
    /// <see cref="MaxFrameSlotWork"/> over the whole document.
    /// </remarks>
    /// <param name="output">The vendor's result document, as fetched from its <c>response_url</c>.</param>
    /// <param name="model">The endpoint id that answered, echoed onto the result.</param>
    /// <param name="width">Frame width the RLE index is over, supplied by the caller's probe.</param>
    /// <param name="height">Frame height.</param>
    /// <param name="expectedFrames">
    /// The frame count the caller's own probe measured for the clip, or null when it could not say (then
    /// <see cref="MaxFrames"/> bounds the document). A document within <see cref="FrameTolerance"/> of it
    /// is accepted (padded when short, truncated when long); one outside the band is refused.
    /// </param>
    /// <param name="maxObjects">
    /// The <c>max_num_objects</c> the request sent. It is NOT a cap on the answer — the vendor ignores it
    /// as one (F2/FACE returned 6–10 slots for a request of 4) — so it only has to be positive and is
    /// named in a slot-ceiling refusal.
    /// </param>
    /// <param name="providerFingerprint">Provider id + kind + model, no key material; null when the caller has none.</param>
    /// <exception cref="VideoSegmentationBadResponseException">The document is not the documented shape.</exception>
    public static VideoSegmentationResult Read(
        JsonElement output,
        string model,
        int width,
        int height,
        int? expectedFrames,
        int maxObjects,
        string? providerFingerprint = null)
    {
        if (width <= 0 || height <= 0)
        {
            throw Bad("the caller supplied a " + width + "x" + height + " frame, which cannot index a mask");
        }

        if (maxObjects < 1)
        {
            throw Bad("the caller supplied max_num_objects " + maxObjects + ", which cannot describe a mask");
        }

        if (output.ValueKind != JsonValueKind.Object
            || !output.TryGetProperty("objects", out JsonElement objectsElement)
            || objectsElement.ValueKind != JsonValueKind.Array)
        {
            throw Bad("the response has no 'objects' array: " + Bounded(output));
        }

        int frameCount = objectsElement.GetArrayLength();

        JsonElement? topLevelRle = null;
        if (output.TryGetProperty("rle", out JsonElement rleElement))
        {
            if (rleElement.ValueKind != JsonValueKind.Array)
            {
                throw Bad("the response's 'rle' is not an array: " + Bounded(output));
            }

            if (rleElement.GetArrayLength() != frameCount)
            {
                throw Bad("the response's 'rle' has " + rleElement.GetArrayLength() + " entries but 'objects' has " +
                          frameCount + "; the two parallel arrays describe the same frames");
            }

            topLevelRle = rleElement;
        }

        if (frameCount == 0)
        {
            throw Bad("the response returned zero frames; a segmentation of a video always has at least one");
        }

        // ---- The frame count is a PRECONDITION, with a bounded tolerance. ----
        // The vendor's answer is not frame-exact: the live batch of 2026-10-18 answered a 144-frame 6 s
        // clip with 141 entries, and the first live run (2026-10-19) answered an 827-frame clip with 825.
        // A short answer is padded at the TAIL by holding the last real mask; a long one is truncated to
        // the probe's count. Nothing is resampled, interpolated or re-indexed. Beyond the band it is still
        // refused, before a single run is decoded.
        int decodedFrames = frameCount;
        int framesPadded = 0;
        int framesOverrunClamped = 0;
        int maxOverrunClamped = 0;
        int framesTruncated = 0;
        if (expectedFrames is { } probedFrames)
        {
            int tolerance = FrameTolerance(probedFrames);
            if (Math.Abs(frameCount - probedFrames) > tolerance)
            {
                throw Bad("the vendor returned " + frameCount + " frame entries for a clip the probe measured at " +
                          probedFrames + " frames, outside the " + tolerance + "-frame tolerance (5% of the probe, " +
                          "rounded up). A short answer is padded and a long one truncated inside that band; beyond " +
                          "it the document is refused rather than silently resampled.");
            }

            if (frameCount < probedFrames)
            {
                framesPadded = probedFrames - frameCount;
            }
            else if (frameCount > probedFrames)
            {
                framesTruncated = frameCount - probedFrames;
                decodedFrames = probedFrames;
            }
        }

        if (frameCount > MaxFrames)
        {
            throw Bad("the response returned " + frameCount + " frame entries, over this reader's " + MaxFrames +
                      "-frame ceiling (600 s at 30 fps, the most any configured segmentation cap can buy). The " +
                      "document was refused before any of its runs were decoded");
        }

        int metadataCount = -1;
        if (output.TryGetProperty("metadata", out JsonElement metadataElement))
        {
            if (metadataElement.ValueKind != JsonValueKind.Array)
            {
                throw Bad("the response's 'metadata' is not an array: " + Bounded(output));
            }

            metadataCount = metadataElement.GetArrayLength();
            if (metadataCount != frameCount)
            {
                throw Bad("the response's 'metadata' has " + metadataCount + " entries but 'objects' has " +
                          frameCount + "; a truncated metadata array means frame indexes cannot be trusted");
            }
        }

        if (output.TryGetProperty("boxes", out JsonElement unionBoxes)
            && unionBoxes.ValueKind == JsonValueKind.Array
            && unionBoxes.GetArrayLength() != frameCount)
        {
            throw Bad("the response's 'boxes' has " + unionBoxes.GetArrayLength() + " entries but 'objects' has " +
                      frameCount);
        }

        // ---- Object slots are WORK, not a promise: the vendor does not treat max_num_objects as a cap on
        //      the slots it returns (F2/FACE answered a request for 4 with 6-10 slots on every frame), so
        //      the request's number cannot refuse an answer. This pass counts the slots of every frame
        //      before a single run is decoded, so the two bounds that DO hold cost nothing to enforce. ----
        int[] slotsPerFrame = new int[frameCount];
        int observedMaxObjectSlots = 0;
        long totalObjectSlots = 0;
        int slotFrameIndex = 0;
        foreach (JsonElement frameElement in objectsElement.EnumerateArray())
        {
            if (frameElement.ValueKind != JsonValueKind.Array)
            {
                throw Bad("frame " + slotFrameIndex + " is not an array of objects: " + Bounded(frameElement));
            }

            int slots = frameElement.GetArrayLength();
            if (slots > MaxObjectSlots)
            {
                throw Bad("frame " + slotFrameIndex + " carries " + slots + " object slots, over this reader's " +
                          MaxObjectSlots + "-slot ceiling on one frame (the request asked for " + maxObjects +
                          "; slots above that number are accepted, because the vendor does not treat " +
                          "max_num_objects as a cap, but the ceiling bounds the decode work)");
            }

            slotsPerFrame[slotFrameIndex] = slots;
            totalObjectSlots += slots;
            if (slots > observedMaxObjectSlots) observedMaxObjectSlots = slots;
            slotFrameIndex++;
        }

        // A slot is one decoded run, so the document's work is the SUM of its slots. A frames x total-slots
        // product counts each slot once per frame and refused an ordinary 825-frame answer (892 runs,
        // 735 900 by that arithmetic) before this was corrected on 2026-10-19.
        if (totalObjectSlots > MaxFrameSlotWork)
        {
            throw Bad("the response has " + frameCount + " frames carrying " + totalObjectSlots +
                      " object slots in total, over this reader's " + MaxFrameSlotWork +
                      "-slot document work bound; the document was refused before any of its runs were decoded");
        }

        List<VideoSegmentationFrame> frames = new(frameCount);
        double? boxAgreementMin = null;
        int? boxAgreementFrame = null;
        int? boxAgreementObject = null;
        int index = 0;
        foreach (JsonElement frameElement in objectsElement.EnumerateArray())
        {
            // A long answer was truncated to the probe's count above: its extra trailing entries are
            // dropped without being decoded.
            if (index >= decodedFrames)
            {
                break;
            }

            if (topLevelRle is { } rleArray)
            {
                JsonElement mirror = rleArray[index];
                if (mirror.ValueKind != JsonValueKind.String)
                {
                    throw Bad("frame " + index + "'s top-level rle is not a string: " + Bounded(mirror));
                }
            }

            if (metadataCount >= 0)
            {
                JsonElement meta = metadataElement.GetArrayLength() > index ? metadataElement[index] : default;
                if (meta.ValueKind == JsonValueKind.Object
                    && meta.TryGetProperty("index", out JsonElement metaIndex)
                    && metaIndex.ValueKind == JsonValueKind.Number
                    && metaIndex.TryGetInt32(out int declared)
                    && declared != index)
                {
                    throw Bad("frame " + index + "'s metadata declares index " + declared +
                              "; the frames are in a different order than their indexes, so a mask would be " +
                              "attached to the wrong moment of the clip");
                }
            }

            List<VideoSegmentationObjectRle> objects = [];
            List<VideoSegmentationObjectBox>? boxes = null;
            HashSet<int> seenIds = [];
            // The slot count came from the pre-pass, which already refused a frame over the ceiling.
            int objectsOnFrame = slotsPerFrame[index];

            foreach (JsonElement objectElement in frameElement.EnumerateArray())
            {
                if (objectElement.ValueKind != JsonValueKind.Object)
                {
                    throw Bad("frame " + index + " holds a non-object entry: " + Bounded(objectElement));
                }

                if (!objectElement.TryGetProperty("object_id", out JsonElement idElement)
                    || idElement.ValueKind != JsonValueKind.Number
                    || !idElement.TryGetInt32(out int objectId))
                {
                    throw Bad("frame " + index + " has an object with no integer object_id: " + Bounded(objectElement));
                }

                if (!seenIds.Add(objectId))
                {
                    throw Bad("frame " + index + " repeats object_id " + objectId + "; object slots must be unique " +
                              "within a frame");
                }

                if (!objectElement.TryGetProperty("rle", out JsonElement maskElement)
                    || maskElement.ValueKind != JsonValueKind.String)
                {
                    throw Bad("frame " + index + " object " + objectId + " has no string rle: " + Bounded(objectElement));
                }

                string rle = maskElement.GetString() ?? string.Empty;
                if (rle.Length == 0)
                {
                    throw Bad("frame " + index + " object " + objectId + " carries an empty mask; the vendor's " +
                              "empty frames omit the object entirely, so an object entry with no mask is a shape " +
                              "this reader cannot represent");
                }

                VideoSegmentationMask mask = Sam31VideoRleDecoder.Decode(rle, objectId, width, height);
                if (mask.OverrunPixelsClamped > 0)
                {
                    framesOverrunClamped++;
                    maxOverrunClamped = Math.Max(maxOverrunClamped, mask.OverrunPixelsClamped);
                }

                // The top-level array is the UNION mask of every object on the frame: on a single-object
                // frame it is that object's mask byte for byte (measured on all 120 R1/tabby frames), but on
                // the 59 two-object frames of R1/turn-a it matches NEITHER object (it is the union, and
                // boxes[i] is the union box). Only the single-object case is checkable as an exact copy, so
                // only that case is compared; the per-object masks are what this reader maps.
                if (topLevelRle is { } mirrorArray && objectsOnFrame == 1)
                {
                    string? mirror = mirrorArray[index].GetString();
                    if (!string.Equals(mirror, rle, StringComparison.Ordinal))
                    {
                        throw Bad("frame " + index + "'s top-level rle does not match its only object's mask; " +
                                  "the vendor's two copies of the same mask disagree, so neither can be trusted");
                    }
                }

                if (objectElement.TryGetProperty("box", out JsonElement boxElement))
                {
                    VideoSegmentationObjectBox box = ReadBox(boxElement, index, objectId);

                    // Recorded, never refused (D-05): the box is approximate, centre-anchored metadata and
                    // is clipped when the object touches the frame edge, so a correct mask can sit well
                    // outside it. The worst object over the document is what the analyze path reports.
                    double inside = InsideFraction(mask, box);
                    if (boxAgreementMin is not { } agreement || inside < agreement)
                    {
                        boxAgreementMin = inside;
                        boxAgreementFrame = index;
                        boxAgreementObject = objectId;
                    }

                    (boxes ??= []).Add(box);
                }

                objects.Add(new VideoSegmentationObjectRle(objectId, rle));
            }

            objects.Sort((left, right) => left.ObjectId.CompareTo(right.ObjectId));
            if (boxes is not null)
            {
                boxes.Sort((left, right) => left.ObjectId.CompareTo(right.ObjectId));
            }

            frames.Add(new VideoSegmentationFrame(index, objects, boxes));
            index++;
        }

        // A short answer is padded at the TAIL with the last real mask — no resample, no interpolation,
        // and the padded frames take the probe's own missing indexes.
        for (int padIndex = frames.Count; padIndex < decodedFrames + framesPadded; padIndex++)
        {
            VideoSegmentationFrame last = frames[^1];
            frames.Add(new VideoSegmentationFrame(padIndex, last.Objects, last.Boxes));
        }

        return new VideoSegmentationResult(
            model,
            width,
            height,
            frames,
            providerFingerprint,
            CostUsd: null,
            InputFrames: frameCount,
            Diagnostics: new VideoSegmentationDiagnostics(
                observedMaxObjectSlots,
                totalObjectSlots,
                boxAgreementMin * 100.0,
                boxAgreementFrame,
                boxAgreementObject,
                framesPadded,
                framesTruncated,
                framesOverrunClamped,
                maxOverrunClamped));
    }

    /// <summary>Reads and converts one centre-anchored vendor box to the record's corner-anchored fields.</summary>
    private static VideoSegmentationObjectBox ReadBox(JsonElement element, int frameIndex, int objectId)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 4)
        {
            throw Bad("frame " + frameIndex + " object " + objectId + " has a box that is not four numbers: " +
                      Bounded(element));
        }

        double[] values = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (element[i].ValueKind != JsonValueKind.Number || !element[i].TryGetDouble(out values[i])
                || double.IsNaN(values[i]) || double.IsInfinity(values[i]))
            {
                throw Bad("frame " + frameIndex + " object " + objectId + " box has a non-finite member: " +
                          Bounded(element));
            }
        }

        double centreX = values[0], centreY = values[1], boxWidth = values[2], boxHeight = values[3];
        if (boxWidth <= 0 || boxHeight <= 0 || boxWidth > 1.01 || boxHeight > 1.01
            || centreX < -0.01 || centreX > 1.01 || centreY < -0.01 || centreY > 1.01)
        {
            throw Bad("frame " + frameIndex + " object " + objectId + " box [" + string.Join(", ", values) +
                      "] is not a normalised [cx, cy, w, h] inside the frame");
        }

        double x = Math.Clamp(centreX - boxWidth / 2.0, 0.0, 1.0);
        double y = Math.Clamp(centreY - boxHeight / 2.0, 0.0, 1.0);
        double w = Math.Min(boxWidth, 1.0 - x);
        double h = Math.Min(boxHeight, 1.0 - y);
        return new VideoSegmentationObjectBox(objectId, x, y, w, h);
    }

    /// <summary>Fraction of the mask's pixels that fall inside the box grown by two pixels.</summary>
    private static double InsideFraction(VideoSegmentationMask mask, VideoSegmentationObjectBox box)
    {
        double left = box.X * mask.Width - BoxGrowPixels;
        double top = box.Y * mask.Height - BoxGrowPixels;
        double right = (box.X + box.Width) * mask.Width + BoxGrowPixels;
        double bottom = (box.Y + box.Height) * mask.Height + BoxGrowPixels;

        int inside = 0;
        for (int y = 0; y < mask.Height; y++)
        {
            ReadOnlySpan<byte> row = mask.Pixels.AsSpan(y * mask.Width, mask.Width);
            if (y < top || y > bottom) continue;
            for (int x = 0; x < mask.Width; x++)
            {
                if (row[x] == Sam31VideoRleDecoder.ObjectValue && x >= left && x <= right) inside++;
            }
        }

        return mask.Area == 0 ? 0 : (double)inside / mask.Area;
    }

    private static string Bounded(JsonElement element)
    {
        string text = element.ValueKind == JsonValueKind.Undefined ? "(absent)" : element.GetRawText();
        return text.Length <= 400 ? text : text[..400] + "…";
    }

    private static VideoSegmentationBadResponseException Bad(string detail) =>
        new(VideoSegmentationBadResponseException.FailureCode + ": " + detail);
}
