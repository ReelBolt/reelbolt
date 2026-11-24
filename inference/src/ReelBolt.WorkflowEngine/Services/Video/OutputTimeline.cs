using System.Linq;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Where one resolved span landed on the OUTPUT timeline — <c>[OutputStartSec, OutputEndSec)</c>,
/// always exactly <c>SnappedEnd - SnappedStart</c> long (a seam's overlap only ever shifts a span's
/// START position backward into its predecessor's tail; it never shortens the span's own presented
/// duration — see <see cref="OutputTimeline.Build"/>).
/// </summary>
public sealed record TimelinePlacement(int SpanIndex, double OutputStartSec, double OutputEndSec);

/// <summary>
/// The compiled OUTPUT timeline a resolved cut list + its planned seam transitions produce —
/// generalizes the pre-transitions single source-to-output mapping
/// (<c>VideoCompileStepExecutor.MapSourceToOutputSec</c>/<c>MapSourceWindowToOutput</c>) to account
/// for overlapping crossfade seams, which make consecutive spans occupy overlapping output time.
/// When every <see cref="SeamPlan.OverlapSec"/> is 0 this produces IDENTICAL results to those two
/// pre-existing static methods — see docs/video-editing.md "Cut transitions".
/// </summary>
public sealed partial class OutputTimeline
{
    private readonly IReadOnlyList<VideoCompileStepExecutor.ResolvedSpan> _spans;

    public IReadOnlyList<TimelinePlacement> Placements { get; }

    public IReadOnlyList<SeamPlan> Seams { get; }

    public double TotalSec { get; }

    private OutputTimeline(
        IReadOnlyList<TimelinePlacement> placements,
        IReadOnlyList<SeamPlan> seams,
        double totalSec,
        IReadOnlyList<VideoCompileStepExecutor.ResolvedSpan> spans)
    {
        Placements = placements;
        Seams = seams;
        TotalSec = totalSec;
        _spans = spans;
    }

    /// <summary>
    /// Builds the output timeline: walks <paramref name="spans"/> in order, placing each one right
    /// after its predecessor except pulled backward by the preceding seam's own
    /// <see cref="SeamPlan.OverlapSec"/> (0 for every non-overlapping treatment, so this reduces to
    /// simple concatenation exactly as before this addition).
    /// </summary>
    internal static OutputTimeline Build(
        IReadOnlyList<VideoCompileStepExecutor.ResolvedSpan> spans, IReadOnlyList<SeamPlan> seams)
    {
        var placements = new List<TimelinePlacement>(spans.Count);
        double cursor = 0;
        for (int i = 0; i < spans.Count; i++)
        {
            if (i > 0)
                cursor -= seams[i - 1].OverlapSec;

            double duration = spans[i].OutputDurationSec;
            placements.Add(new TimelinePlacement(i, cursor, cursor + duration));
            cursor += duration;
        }

        return new OutputTimeline(placements, seams, cursor, spans);
    }

    /// <summary>
    /// A tracked region's output window, extended to the end of the kept segment it ends in when it
    /// stops at most <paramref name="maxGapSec"/> short of it. A track ends on its clip's last
    /// TRACKED frame, but the segment can be a frame longer: a clip kept to its end is held to its
    /// snapped timeline length by repeating its last frame (see
    /// <c>VideoCompileStepExecutor.SegmentLengthSuffix</c>), and that repeated frame showed the raw
    /// green plate because neither the insert nor its plate guard reached it. Windows ending well
    /// before their segment's end are returned unchanged.
    /// </summary>
    public (double Start, double End) ExtendToSegmentEnd((double Start, double End) window, double maxGapSec)
    {
        foreach (TimelinePlacement placement in Placements)
        {
            if (window.End > placement.OutputStartSec && window.End <= placement.OutputEndSec + 1e-9)
            {
                double gap = placement.OutputEndSec - window.End;
                return gap > 1e-9 && gap <= maxGapSec ? (window.Start, placement.OutputEndSec) : window;
            }
        }

        return window;
    }

    /// <summary>
    /// Maps a source-timeline second to its position in the compiled output timeline. Returns
    /// <c>null</c> when <paramref name="sourceSec"/> falls inside a cut region — including before
    /// the first kept span of this source or after its last.
    /// </summary>
    public double? MapToOutputSec(double sourceSec, int sourceIndex = 0)
    {
        foreach (TimelinePlacement placement in Placements)
        {
            VideoCompileStepExecutor.ResolvedSpan span = _spans[placement.SpanIndex];
            if (span.SourceIndex != sourceIndex)
                continue;

            // Not `return null`: in a multi-clip edit the same source can come back LATER in the
            // program with an EARLIER part of itself (clip A 10-15s, clip B, then clip A 2-5s).
            // Stopping at the first span that starts after the second would call 3s "cut away"
            // although the third span keeps it. For spans in source order this is identical.
            if (sourceSec < span.SnappedStart)
                continue;

            if (sourceSec < span.SnappedEnd)
                return placement.OutputStartSec + (sourceSec - span.SnappedStart) / span.Speed;
        }

        return null;
    }

    /// <summary>
    /// The placement whose <c>[OutputStartSec, OutputEndSec)</c> window contains
    /// <paramref name="outputSec"/>, whatever source it came from — "which kept shot is on screen
    /// at this output second?". Same half-open bounds and same earlier-placement-wins rule under
    /// an overlapping seam as the source-filtered overload; callers must pass the RAW second.
    /// </summary>
    public bool TryGetContainingPlacement(double outputSec, out TimelinePlacement placement)
    {
        foreach (TimelinePlacement candidate in Placements)
        {
            if (outputSec >= candidate.OutputStartSec && outputSec < candidate.OutputEndSec)
            {
                placement = candidate;
                return true;
            }
        }

        placement = null!;
        return false;
    }

    /// <summary>
    /// The placement whose <c>[OutputStartSec, OutputEndSec)</c> half-open window contains
    /// <paramref name="outputSec"/> on <paramref name="sourceIndex"/>'s own spans — i.e. "which
    /// kept shot's output window is this second inside?".
    ///
    /// <para>
    /// The end bound is EXCLUSIVE, so a second sitting exactly on a placement's end belongs to the
    /// following placement and not to the one it closes. Under an overlapping seam treatment the
    /// blend window belongs to two placements at once; the EARLIER one wins, because placements are
    /// walked in span order and the earlier span is the one being transitioned out of.
    /// </para>
    ///
    /// <para>
    /// Purely additive: <see cref="Build"/>, <see cref="MapToOutputSec"/>,
    /// <see cref="MapWindowToOutput"/>, <see cref="TransitionOverlapRanges"/> and
    /// <see cref="ClipAwayTransitionOverlaps"/> are untouched by its existence.
    /// </para>
    ///
    /// <para>
    /// <b>Callers must pass the RAW output second.</b> The bounds compared against below are the
    /// unrounded <see cref="TimelinePlacement.OutputStartSec"/>/<see cref="TimelinePlacement.OutputEndSec"/>,
    /// and the end bound is exclusive — so a value already rounded to a node's own 3 decimals can
    /// land exactly on the exclusive end and be reported as contained by nothing, even though the
    /// raw second it was rounded from is inside. Round for REPORTING, look up RAW.
    /// </para>
    /// </summary>
    /// <returns><c>false</c> (with <paramref name="placement"/> left null) when no placement of
    /// <paramref name="sourceIndex"/> contains <paramref name="outputSec"/>.</returns>
    public bool TryGetContainingPlacement(double outputSec, int sourceIndex, out TimelinePlacement placement)
    {
        foreach (TimelinePlacement candidate in Placements)
        {
            if (_spans[candidate.SpanIndex].SourceIndex != sourceIndex)
                continue;

            if (outputSec >= candidate.OutputStartSec && outputSec < candidate.OutputEndSec)
            {
                placement = candidate;
                return true;
            }
        }

        placement = null!;
        return false;
    }

    /// <summary>
    /// Output-time ranges where two spans are blended together by an overlapping seam treatment
    /// (SoftCut/Dissolve/DipToBlack/WhipBlur). Empty whenever every seam is a plain cut, which is
    /// every compile with the default <c>TransitionPolicy = Off</c>.
    ///
    /// <para>
    /// A span pulled backward by its preceding seam's <see cref="SeamPlan.OverlapSec"/> occupies
    /// the same output time as its predecessor's tail; during that window the frame is a blend, so
    /// it belongs to NEITHER source cleanly. Content that must be pinned to one specific source
    /// clip's geometry (a tracked screen insert) has to stay out of these windows.
    /// </para>
    /// </summary>
    public IEnumerable<(double Start, double End)> TransitionOverlapRanges()
    {
        for (int i = 1; i < Placements.Count; i++)
        {
            double overlap = i - 1 < Seams.Count ? Seams[i - 1].OverlapSec : 0;
            if (overlap > 0)
                yield return (Placements[i].OutputStartSec, Placements[i].OutputStartSec + overlap);
        }
    }

    /// <summary>
    /// Trims <c>[start, end)</c> down to its longest contiguous sub-range that avoids every
    /// <see cref="TransitionOverlapRanges"/> window, or <c>null</c> when nothing survives. A
    /// no-op (returns the input unchanged) when there are no overlapping seams at all.
    /// </summary>
    public (double Start, double End)? ClipAwayTransitionOverlaps(double start, double end)
    {
        if (end <= start)
            return null;

        List<(double Start, double End)> blocked = TransitionOverlapRanges()
            .Where(r => r.End > start && r.Start < end)
            .OrderBy(r => r.Start)
            .ToList();

        if (blocked.Count == 0)
            return (start, end);

        // Walk the gaps between blocked ranges, keeping the longest clean one.
        (double Start, double End)? best = null;
        double cursor = start;
        foreach ((double bs, double be) in blocked)
        {
            if (bs > cursor && (best is null || bs - cursor > best.Value.End - best.Value.Start))
                best = (cursor, bs);
            cursor = Math.Max(cursor, be);
        }

        if (end > cursor && (best is null || end - cursor > best.Value.End - best.Value.Start))
            best = (cursor, end);

        return best;
    }

    /// <summary>
    /// Intersects a source-timeline <c>[startSec, endSec)</c> window with the kept spans of
    /// <paramref name="sourceIndex"/> and maps the surviving (first-overlapping) portion to the
    /// output timeline — mirrors the pre-existing <c>MapSourceWindowToOutput</c> exactly.
    /// </summary>
    /// <summary>
    /// EVERY output stretch the source window <c>[startSec, endSec)</c> of
    /// <paramref name="sourceIndex"/> survives into, in output order — where
    /// <see cref="MapWindowToOutput"/> returns only the first. Censoring needs all of them: an
    /// object visible across a cut that keeps two pieces of its window must be obscured in both.
    /// </summary>
    /// <summary>
    /// Every kept stretch of the source window with the source seconds it covers: where
    /// <see cref="MapWindowToOutputAll"/> returns only the output side, this keeps the source side so a caller can
    /// tell which part of the window each stretch is.
    /// </summary>
    public IReadOnlyList<TranscriptCaptionLines.Portion> MapWindowToPortions(double startSec, double endSec, int sourceIndex = 0)
    {
        var portions = new List<TranscriptCaptionLines.Portion>();
        if (endSec <= startSec)
            return portions;

        foreach (TimelinePlacement placement in Placements)
        {
            VideoCompileStepExecutor.ResolvedSpan span = _spans[placement.SpanIndex];
            if (span.SourceIndex != sourceIndex)
                continue;

            double overlapStart = Math.Max(startSec, span.SnappedStart);
            double overlapEnd = Math.Min(endSec, span.SnappedEnd);
            if (overlapEnd > overlapStart)
                portions.Add(new TranscriptCaptionLines.Portion(
                    overlapStart, overlapEnd,
                    placement.OutputStartSec + (overlapStart - span.SnappedStart) / span.Speed,
                    placement.OutputStartSec + (overlapEnd - span.SnappedStart) / span.Speed));
        }

        return portions;
    }

    public IReadOnlyList<(double Start, double End)> MapWindowToOutputAll(double startSec, double endSec, int sourceIndex = 0)
    {
        var portions = new List<(double Start, double End)>();
        if (endSec <= startSec)
            return portions;

        foreach (TimelinePlacement placement in Placements)
        {
            VideoCompileStepExecutor.ResolvedSpan span = _spans[placement.SpanIndex];
            if (span.SourceIndex != sourceIndex)
                continue;

            double overlapStart = Math.Max(startSec, span.SnappedStart);
            double overlapEnd = Math.Min(endSec, span.SnappedEnd);
            if (overlapEnd > overlapStart)
                portions.Add((
                    placement.OutputStartSec + (overlapStart - span.SnappedStart) / span.Speed,
                    placement.OutputStartSec + (overlapEnd - span.SnappedStart) / span.Speed));
        }

        return portions;
    }

    public (double Start, double End)? MapWindowToOutput(double startSec, double endSec, int sourceIndex = 0)
    {
        if (endSec <= startSec)
            return null;

        foreach (TimelinePlacement placement in Placements)
        {
            VideoCompileStepExecutor.ResolvedSpan span = _spans[placement.SpanIndex];
            if (span.SourceIndex != sourceIndex)
                continue;

            double overlapStart = Math.Max(startSec, span.SnappedStart);
            double overlapEnd = Math.Min(endSec, span.SnappedEnd);

            if (overlapEnd > overlapStart)
            {
                double outStart = placement.OutputStartSec + (overlapStart - span.SnappedStart) / span.Speed;
                double outEnd = placement.OutputStartSec + (overlapEnd - span.SnappedStart) / span.Speed;
                return (outStart, outEnd);
            }
        }

        return null;
    }
}
