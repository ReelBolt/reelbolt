using System.Globalization;

namespace ReelBolt.Shared.Inference;

/// <summary>Which pixel index a run's <c>start</c> token counts from.</summary>
/// <remarks>
/// The vendor documents neither the base nor the grid. <b>Measured on the real
/// <c>fal-ai/sam-3-1/video-rle</c> responses (T-002's replay pack, 2026-10-18): 0-based.</b>
/// Decoding the R1/tabby clip 0-based reproduces T-002's pinned decoded-mask SHA-256
/// <c>51d10d82a2a105976432607b52a5f720acd72dde73461251671709a7bdff39c1</c> byte for byte; the 1-based
/// reading produces <c>15b6351470a99d9a744b9c9ec2d3c743e399724f17ceb1c7ab9f30c6ab4a722d</c>, which is not
/// the committed matte. The parameter exists so a future endpoint can state a different base rather than
/// because the base is in doubt here.
/// </remarks>
public enum RleStartBase
{
    /// <summary>The first pixel of the frame is index 0 (the measured behaviour of SAM 3.1 video-rle).</summary>
    Zero = 0,

    /// <summary>The first pixel of the frame is index 1 (not what this endpoint does).</summary>
    One = 1
}

/// <summary>
/// A response the platform cannot represent: a malformed run list, a run outside the stated frame, or an
/// output document that is not the documented shape.
/// Carries the plan's <c>SEGMENTATION_BAD_RESPONSE</c> code so the analyze step (T-004) can fail closed
/// with a stable, specific refusal instead of an unhandled exception.
/// </summary>
public sealed class VideoSegmentationBadResponseException : Exception
{
    /// <summary>The stable failure code the step reports for every shape/decode refusal.</summary>
    public const string FailureCode = "SEGMENTATION_BAD_RESPONSE";

    public VideoSegmentationBadResponseException(string message) : base(message) { }

    /// <summary>Shorthand for <see cref="FailureCode"/>.</summary>
    public string Code => FailureCode;
}

/// <summary>
/// One decoded frame mask: a row-major <see cref="byte"/> buffer with <see cref="ObjectValue"/> (255)
/// where the object is and 0 elsewhere, plus the area and bounding box the decoder measured.
/// </summary>
/// <param name="ObjectId">The vendor's object slot.</param>
/// <param name="Width">Frame width the buffer is indexed over.</param>
/// <param name="Height">Frame height.</param>
/// <param name="Pixels">The mask, row-major, one byte per pixel; the decoder owned this buffer before returning it.</param>
/// <param name="Area">Number of non-zero pixels; 0 for a legitimately empty frame.</param>
/// <param name="X0">Left-most set pixel, or -1 when <see cref="IsEmpty"/>.</param>
/// <param name="Y0">Top-most set pixel, or -1 when <see cref="IsEmpty"/>.</param>
/// <param name="X1">Right-most set pixel, or -1 when <see cref="IsEmpty"/>.</param>
/// <param name="Y1">Bottom-most set pixel, or -1 when <see cref="IsEmpty"/>.</param>
public sealed record VideoSegmentationMask(
    int ObjectId,
    int Width,
    int Height,
    byte[] Pixels,
    int Area,
    int X0,
    int Y0,
    int X1,
    int Y1)
{
    /// <summary>True when the frame carries no object pixels at all.</summary>
    public bool IsEmpty => Area == 0;

    /// <summary>
    /// Pixels by which the vendor's final run overshot the frame end and was clamped to it (0 when none).
    /// A reported diagnostic, never a refusal, up to <see cref="Sam31VideoRleDecoder.MaxTolerableOverrunPixels"/>.
    /// </summary>
    public int OverrunPixelsClamped { get; init; }
}

/// <summary>
/// Decodes one frame of <c>fal-ai/sam-3-1/video-rle</c>'s <c>"start length start length …"</c> string.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout, measured rather than assumed.</b> Each pair is (start, length) over a ROW-MAJOR index
/// of a <c>width x height</c> frame; a run is <see cref="ObjectValue"/> (255) and everything else is 0.
/// The base is 0 (see <see cref="RleStartBase"/>). A run that reaches the frame's right edge and
/// continues on the next row is <b>legal and common</b> in the real data — 20,517 of the 73,985 runs on
/// R1/tabby cross a row boundary, on 76 of its 120 frames, because the model's mask touches the right
/// edge and a linear row-major run continues at column 0 of the next row. Rejecting a crossing run (as
/// an earlier draft of the plan proposed) would reject most of the real clip, so this decoder does not:
/// it validates that every run lies INSIDE the frame and lets it cross rows. The assumption that the
/// width is right is measured one layer up, where the decoded mask is compared with the vendor's own
/// box and the agreement is REPORTED (<see cref="Sam31VideoRleResponseReader"/>): a wrong width shears
/// the mask out of that box, but the box is approximate metadata, so the number is a diagnostic (D-05),
/// never a refusal.
/// </para>
/// <para>
/// <b>Allocation.</b> Exactly one <c>byte[]</c> of <c>width x height</c> per call. Tokens are parsed
/// from spans; no <c>string.Split</c>, no per-pixel object.
/// </para>
/// <para>
/// <b>Fail closed.</b> Anything this cannot represent throws
/// <see cref="VideoSegmentationBadResponseException"/>: a non-integer or negative token, an odd token
/// count, a zero/negative length, a start that does not advance past the previous run's end (unsorted
/// or overlapping), a run that ends outside the frame, a 1-based start of 0, or a frame geometry that
/// overflows. An empty string is a legitimately empty frame, not an error.
/// </para>
/// </remarks>
public static class Sam31VideoRleDecoder
{
    /// <summary>The value a set pixel carries, matching the vendor's own 255.</summary>
    public const byte ObjectValue = 255;

    /// <summary>
    /// How many pixels past the frame end a run may reach and still be clamped rather than refused.
    /// Evidence: the one production failure (run 85444334, 2026-10-19) was a 1080x1920 frame whose last run
    /// ended at 2,073,601 against 2,073,600 pixels, i.e. 1 pixel over, in an otherwise correct response
    /// (an off-by-one in the vendor's run end). 32 is a generous margin over that single observation (about
    /// 0.0015% of a 1080p frame, under one row of any frame at least 32 px wide) yet far below what a wrong
    /// width or start base produces, which shifts runs by at least a whole row. A run that STARTS outside the
    /// frame, a negative index, overlap and malformed tokens are never tolerated.
    /// </summary>
    public const int MaxTolerableOverrunPixels = 32;

    /// <summary>
    /// Decodes <paramref name="rle"/> into a <paramref name="width"/> x <paramref name="height"/> mask.
    /// </summary>
    /// <param name="rle">The vendor's run list; null or whitespace means "no object on this frame".</param>
    /// <param name="objectId">The vendor's object slot, copied onto the result.</param>
    /// <param name="width">Frame width the row-major index is over (the vendor's, never re-derived).</param>
    /// <param name="height">Frame height.</param>
    /// <param name="startBase">Which pixel the run starts count from; measured 0 for this endpoint.</param>
    /// <exception cref="VideoSegmentationBadResponseException">The run list is not representable.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The frame geometry is not positive.</exception>
    public static VideoSegmentationMask Decode(
        string? rle,
        int objectId,
        int width,
        int height,
        RleStartBase startBase = RleStartBase.Zero)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Frame width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Frame height must be positive.");

        long pixelCount = (long)width * height;
        if (pixelCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width),
                width + "x" + height + " is " + pixelCount + " pixels, which no single mask buffer can hold.");
        }

        byte[] pixels = new byte[pixelCount];
        if (string.IsNullOrWhiteSpace(rle))
        {
            return new VideoSegmentationMask(objectId, width, height, pixels, 0, -1, -1, -1, -1);
        }

        ReadOnlySpan<char> remaining = rle.AsSpan();
        int previousEnd = 0;
        int area = 0;
        int overrun = 0;

        while (true)
        {
            SkipWhitespace(ref remaining);
            if (remaining.IsEmpty) break;

            int tokenStart = TokenPosition(rle, remaining);
            int start = ReadInt(ref remaining, "start", tokenStart);
            SkipWhitespace(ref remaining);
            int length = ReadInt(ref remaining, "length", tokenStart);

            if (length <= 0)
            {
                throw Bad("run " + tokenStart + " has a non-positive length " + length +
                          "; every run must cover at least one pixel");
            }

            long index = (long)start - (int)startBase;
            if (startBase == RleStartBase.One && start == 0)
            {
                throw Bad("run " + tokenStart + " starts at 0, but the 1-based reading of this run list " +
                          "addresses pixels only from 1; refusing to guess which pixel was meant");
            }

            long end = index + length;
            if (index >= 0 && index < pixelCount && end > pixelCount && end - pixelCount <= MaxTolerableOverrunPixels)
            {
                overrun = (int)(end - pixelCount);
                length -= overrun;
                end = pixelCount;
            }

            if (index < 0 || end > pixelCount)
            {
                throw Bad("run " + tokenStart + " (" + start + " + " + length + " = " + end + ") falls outside " +
                          "the " + width + "x" + height + " frame it is supposedly indexed over (" + pixelCount +
                          " pixels); the observed run end " + end + " exceeds the expected frame size, which " +
                          "means the width assumption (" + width + ") or the start base (" + (int)startBase +
                          ") is wrong");
            }

            if (index < previousEnd)
            {
                throw Bad("run " + tokenStart + " starts at " + start + " (pixel " + index + ") before the " +
                          "previous run ended at " + previousEnd + "; the vendor's run list must be sorted and " +
                          "non-overlapping");
            }

            previousEnd = (int)end;
            area = checked(area + length);

            pixels.AsSpan((int)index, length).Fill(ObjectValue);
        }

        if (area == 0)
        {
            throw Bad("the run list contains no pixels; an empty frame must be the empty string, not " +
                      "zero-length runs");
        }

        // One exact pass for the bounding box: a run may cross rows, so the box is not derivable from
        // the run endpoints alone, and T-004's artifact consumes the box.
        (int x0, int y0, int x1, int y1, _) = MeasureBox(pixels, width, height);

        return new VideoSegmentationMask(objectId, width, height, pixels, area, x0, y0, x1, y1)
        {
            OverrunPixelsClamped = overrun,
        };
    }

    /// <summary>
    /// The frame's measurement: how much of it the object covers (0..1) and the bounding box of the
    /// covered pixels, normalised (<c>[x, y, w, h]</c> fractions of the frame). All-zero when nothing
    /// is covered. This is what the analyze path stores on a track and what the per-frame box
    /// cross-check compares against the vendor's own boxes.
    /// </summary>
    /// <remarks>
    /// Lives here, beside <see cref="Decode"/>, so the vendor's format is read in exactly ONE place:
    /// a second parser would be a second opinion on the same bytes, which is the divergence this
    /// decoder's whole existence is meant to prevent.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="mask"/> is shorter than the frame.</exception>
    public static SegmentationMaskMeasurement Measure(ReadOnlySpan<byte> mask, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return default;
        }

        (int minX, int minY, int maxX, int maxY, int covered) = MeasureBox(mask, width, height);
        if (covered == 0)
        {
            return default;
        }

        double w = maxX - minX + 1;
        double h = maxY - minY + 1;
        return new SegmentationMaskMeasurement(
            covered / (width * (double)height),
            minX / (double)width,
            minY / (double)height,
            w / width,
            h / height,
            covered);
    }

    /// <summary>
    /// Which vendor object slot "the object" is, for one prompt, out of the candidates the model
    /// returned. <paramref name="seed"/> is the normalised box an author drew (an
    /// <c>ObjectTrackTarget.Region</c>'s bounding box), or <c>null</c> when the prompt alone seeded the
    /// call. With a seed, the candidate whose FIRST NON-EMPTY box best matches it wins (a click/box is
    /// the customer saying "this one, not that one"); without one, the candidate covering the most
    /// pixels across the clip wins, which is what "the speedometer" means when the model returns several
    /// instances of the prompted class.
    /// <para>
    /// Ties resolve to the LOWEST object id, so a repeated run over the same answer always selects the
    /// same slot (the artifact and the content cache both depend on that being deterministic).
    /// </para>
    /// </summary>
    public static int? ChooseObjectId(IEnumerable<SegmentationObjectCandidate> candidates, SegmentationBox? seed = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        SegmentationObjectCandidate? best = null;
        double bestScore = double.NegativeInfinity;

        foreach (SegmentationObjectCandidate candidate in candidates.OrderBy(c => c.ObjectId))
        {
            double score = seed is { } s
                ? (candidate.FirstBox is { } box ? box.MatchScore(s) : double.NegativeInfinity)
                : candidate.CoveredPixels;

            // A slot that never covered a single pixel is not an answer, whichever way it was seeded:
            // choosing it would emit a track whose mask is black on every frame.
            if (double.IsNegativeInfinity(score) || score <= 0)
            {
                continue;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best?.ObjectId;
    }

    /// <summary>One object slot's totals over the whole clip, for <see cref="ChooseObjectId"/>.</summary>
    /// <param name="ObjectId">The vendor's slot id, as it appears on every frame.</param>
    /// <param name="CoveredPixels">Summed covered pixels over the clip (0 when the slot was never found).</param>
    /// <param name="FirstBox">The slot's first NON-EMPTY normalised box, or null when it never covered anything.</param>
    public sealed record SegmentationObjectCandidate(int ObjectId, long CoveredPixels, SegmentationBox? FirstBox);

    /// <summary>A normalised <c>[x, y, w, h]</c> box (fractions of the frame, 0..1).</summary>
    public readonly record struct SegmentationBox(double X, double Y, double W, double H)
    {
        /// <summary>
        /// How well this box matches <paramref name="seed"/>: 1.0 for an exact match, 0.0 when they do not
        /// overlap, and the intersection-over-union otherwise, with the degenerate cases (either box
        /// empty) answering 0 rather than dividing by zero.
        /// </summary>
        public double MatchScore(SegmentationBox seed)
        {
            double x0 = Math.Max(X, seed.X);
            double y0 = Math.Max(Y, seed.Y);
            double x1 = Math.Min(X + W, seed.X + seed.W);
            double y1 = Math.Min(Y + H, seed.Y + seed.H);
            double iw = Math.Max(0, x1 - x0);
            double ih = Math.Max(0, y1 - y0);
            double intersection = iw * ih;
            double union = (W * H) + (seed.W * seed.H) - intersection;
            return union <= 0 ? 0 : intersection / union;
        }
    }

    /// <summary>One frame's decoded measurement (see <see cref="Measure"/>).</summary>
    /// <param name="Coverage">Fraction of the frame the object covers (0..1).</param>
    /// <param name="X">Normalised left edge of the covered pixels.</param>
    /// <param name="Y">Normalised top edge.</param>
    /// <param name="W">Normalised width of the covered pixels.</param>
    /// <param name="H">Normalised height.</param>
    /// <param name="CoveredPixels">Covered pixel count (0 = nothing found on this frame).</param>
    public readonly record struct SegmentationMaskMeasurement(
        double Coverage, double X, double Y, double W, double H, int CoveredPixels);

    private static (int X0, int Y0, int X1, int Y1, int Covered) MeasureBox(
        ReadOnlySpan<byte> mask, int width, int height)
    {
        if (mask.Length < (long)width * height)
        {
            throw new ArgumentException(
                "A " + mask.Length + "-byte buffer cannot hold a " + width + "x" + height + " frame.",
                nameof(mask));
        }

        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1, covered = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = mask.Slice(y * width, width);
            for (int x = 0; x < row.Length; x++)
            {
                if (row[x] == 0) continue;
                covered++;
                if (x < x0) x0 = x;
                if (x > x1) x1 = x;
                if (y < y0) y0 = y;
                y1 = y;
            }
        }

        return (x0, y0, x1, y1, covered);
    }

    private static void SkipWhitespace(ref ReadOnlySpan<char> span)
    {
        int i = 0;
        while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
        span = span[i..];
    }

    private static int TokenPosition(string original, ReadOnlySpan<char> remaining)
    {
        // Diagnostic only: the token's offset in the original string, so a refusal names a position a
        // human can find in the vendor's own answer.
        return original.Length - remaining.Length;
    }

    private static int ReadInt(ref ReadOnlySpan<char> span, string kind, int tokenPosition)
    {
        int i = 0;
        while (i < span.Length && !char.IsWhiteSpace(span[i])) i++;
        if (i == 0)
        {
            throw Bad("the run list ends where a " + kind + " token was expected (token " + tokenPosition + ")");
        }

        ReadOnlySpan<char> token = span[..i];
        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            throw Bad("token '" + token.ToString() + "' at " + tokenPosition + " is not a non-negative " + kind +
                      "; the vendor's run list is 'start length start length …' in decimal");
        }

        span = span[i..];
        return value;
    }

    private static VideoSegmentationBadResponseException Bad(string detail) =>
        new(VideoSegmentationBadResponseException.FailureCode + ": " + detail);
}
