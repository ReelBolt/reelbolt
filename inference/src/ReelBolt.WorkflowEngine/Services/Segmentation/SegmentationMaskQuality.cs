namespace ReelBolt.WorkflowEngine.Services.Segmentation;

/// <summary>How good a stored segmentation mask is, as the one self-check the platform runs on it (T-017, D-18).</summary>
public enum SegmentationMaskQuality
{
    /// <summary>Every gap is small enough to be a rounding error or a legitimate occlusion.</summary>
    Clean,

    /// <summary>The object is missing from more than a fifth of the clip, or from a single run longer than a second.</summary>
    Degraded,

    /// <summary>The object is missing from every frame — an effect on this mask would do nothing.</summary>
    Empty
}

/// <summary>
/// The numbers behind a <see cref="SegmentationMaskQuality"/>, all derived from facts the analyze path
/// already records. Immutable and prompt-free: it is safe to log and to replay.
/// </summary>
/// <param name="Quality">The verdict.</param>
/// <param name="FrameCount">Frames the mask holds.</param>
/// <param name="EmptyFrames">Frames whose mask is entirely empty.</param>
/// <param name="FirstNonEmptyFrame">First frame with an object; <c>null</c> when unknown or empty everywhere.</param>
/// <param name="LastNonEmptyFrame">Last frame with an object; <c>null</c> when unknown or empty everywhere.</param>
/// <param name="LeadingGapFrames">Empty frames before the object is first found (0 when empty everywhere).</param>
/// <param name="TrailingGapFrames">Empty frames after the object is last found (0 when empty everywhere).</param>
/// <param name="EmptyFraction">Empty frames as a fraction of the clip (0 when there are no frames).</param>
/// <param name="LongestGapSeconds">The longest single run of empty frames, in seconds.</param>
public sealed record SegmentationMaskAssessment(
    SegmentationMaskQuality Quality,
    int FrameCount,
    int EmptyFrames,
    int? FirstNonEmptyFrame,
    int? LastNonEmptyFrame,
    int LeadingGapFrames,
    int TrailingGapFrames,
    double EmptyFraction,
    double LongestGapSeconds);

/// <summary>
/// The one policy that decides whether a stored mask is worth warning about (initiative "prompted region
/// effects", takeover wave §12.2 / D-18). It exists because the analyze path MEASURED a leading gap
/// (<c>emptyFrames</c>/<c>longestGapFrames</c>), the compile copied it into the EDL, and nothing surfaced
/// it or acted on it — the only self-check that could have caught a head gap, <c>boxAgreementMin</c>, is a
/// BOX agreement over frames that carry an object, so a leading gap cannot lower it.
///
/// <para>
/// <b>Pure and total.</b> It never throws and never validates: a nonsense input (no frames, negative
/// counts) is clamped to <see cref="SegmentationMaskQuality.Clean"/> with zeroes, because this is a
/// self-check, not a gate. <see cref="WarnEmptyFraction"/> and <see cref="WarnLongestGapSeconds"/> are
/// stated constants so a reviewer can see the bar.
/// </para>
/// </summary>
public static class SegmentationMaskQualityPolicy
{
    /// <summary>More than this fraction of the clip's frames carrying no object is a degraded mask.</summary>
    public const double WarnEmptyFraction = 0.20;

    /// <summary>Any single run of empty frames longer than this reads as "the effect switched off".</summary>
    public const double WarnLongestGapSeconds = 1.0;

    /// <summary>
    /// Classifies a mask from the facts the analyze path recorded. <c>Empty</c> when the object is missing
    /// from every frame; <c>Degraded</c> when it is missing from more than <see cref="WarnEmptyFraction"/>
    /// of them OR from any single run longer than <see cref="WarnLongestGapSeconds"/>; <c>Clean</c> otherwise.
    /// </summary>
    public static SegmentationMaskAssessment Assess(
        int frameCount, int emptyFrames, int longestGapFrames, double fps,
        int? firstNonEmptyFrame, int? lastNonEmptyFrame)
    {
        if (frameCount <= 0)
        {
            return new SegmentationMaskAssessment(
                SegmentationMaskQuality.Clean, 0, 0, null, null, 0, 0, 0, 0);
        }

        emptyFrames = Math.Clamp(emptyFrames, 0, frameCount);
        longestGapFrames = Math.Clamp(longestGapFrames, 0, frameCount);

        bool emptyEverywhere = emptyFrames >= frameCount;
        int? first = emptyEverywhere ? null : InRange(firstNonEmptyFrame, frameCount);
        int? last = emptyEverywhere ? null : InRange(lastNonEmptyFrame, frameCount);

        // Nothing to lead or trail when the mask is empty everywhere: the "gap" is the whole clip, not a
        // gap. Otherwise the gap before the object is exactly where it was first found, and the gap after
        // it is the tail past where it was last found. An UNKNOWN first/last (an artifact or cache entry
        // written before these facts existed) reports 0 rather than a fabricated full-clip gap.
        int leading = !emptyEverywhere && first is { } firstFrame ? firstFrame : 0;
        int trailing = !emptyEverywhere && last is { } lastFrame ? frameCount - 1 - lastFrame : 0;

        double emptyFraction = emptyFrames / (double)frameCount;
        double longestGapSeconds = fps > 0 ? longestGapFrames / fps : 0;

        SegmentationMaskQuality quality =
            emptyEverywhere ? SegmentationMaskQuality.Empty
            : emptyFraction > WarnEmptyFraction || longestGapSeconds > WarnLongestGapSeconds
                ? SegmentationMaskQuality.Degraded
                : SegmentationMaskQuality.Clean;

        return new SegmentationMaskAssessment(
            quality, frameCount, emptyFrames, first, last, leading, trailing, emptyFraction, longestGapSeconds);
    }

    /// <summary>
    /// The fact sentence both call sites use: the frames, where the object was first and last found, and
    /// the longest run — plain, specific, and PROMPT-FREE, so it is safe to log (F-1). The caller adds its
    /// own subject (the prompt in the customer's step output, nothing in the log).
    /// </summary>
    public static string Describe(SegmentationMaskAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);

        int found = Math.Max(0, assessment.FrameCount - assessment.EmptyFrames);

        if (assessment.Quality == SegmentationMaskQuality.Empty)
        {
            return FormattableString.Invariant(
                $"found on none of {assessment.FrameCount} frames; the longest run with no object is {assessment.LongestGapSeconds:0.##} s");
        }

        if (assessment.FirstNonEmptyFrame is not { } first)
        {
            // The facts are unknown (an artifact or cache entry written before they existed): say what IS
            // known rather than inventing a first frame.
            return FormattableString.Invariant(
                $"found on {found} of {assessment.FrameCount} frames; the longest run with no object is {assessment.LongestGapSeconds:0.##} s");
        }

        string lastClause = assessment.LastNonEmptyFrame is { } last
            ? FormattableString.Invariant($" and the last was frame {last}")
            : string.Empty;

        return FormattableString.Invariant(
            $"found on {found} of {assessment.FrameCount} frames; the first was frame {first}{lastClause}; the longest run with no object is {assessment.LongestGapSeconds:0.##} s");
    }

    private static int? InRange(int? frame, int frameCount) =>
        frame is { } value && value >= 0 && value < frameCount ? value : null;
}
