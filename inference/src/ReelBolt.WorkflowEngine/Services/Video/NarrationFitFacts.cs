using System;
using System.Collections.Generic;
using System.Linq;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// One narration line's PICTURE-WINDOW fit: how much output time is left after the line begins in
/// the shot it starts in, plus every following shot that starts no line of its own, and whether the
/// line's own spoken duration overflows it.
///
/// <para>
/// Every seconds value is the same 3-decimal rounding its siblings on the compile's voiceover line
/// node use (<see cref="NarrationFitFacts.Measure"/>), so the node never disagrees with itself
/// about a number it reports twice.
/// </para>
/// </summary>
/// <param name="WindowStartSec">Start of the containing output placement, rounded.</param>
/// <param name="WindowEndSec">End of the containing output placement, rounded.</param>
/// <param name="WindowSec"><c>WindowEndSec - outputStartSec</c> — the picture time left from the
/// line's own start to the end of the shot it starts in. Negative for a line that starts before
/// its placement (never produced by <see cref="NarrationFitFacts.Measure"/>'s own call site, which
/// only ever passes a placement known to contain the start).</param>
/// <param name="OverflowSec"><c>max(0, playDurationSec - WindowSec)</c>, rounded, and zero within
/// <see cref="NarrationFitFacts.OverflowToleranceSec"/>. Zero means the
/// line fits.</param>
/// <param name="Fits"><c>OverflowSec &lt;= 0</c>.</param>
/// <param name="CarriedShotCount">How many following kept shots the window was extended across
/// because none of them starts a line of its own (see <see cref="NarrationFitFacts.CarryWindowEnd"/>).
/// Zero means the window is the starting shot alone.</param>
public readonly record struct NarrationLineFit(
    double WindowStartSec,
    double WindowEndSec,
    double WindowSec,
    double OverflowSec,
    bool Fits,
    int CarriedShotCount = 0);

/// <summary>
/// The node-level roll-up of every measurable line's <see cref="NarrationLineFit"/>.
/// <see cref="Applicable"/> is true only when at least one line's window was actually found —
/// a partial measurement reports the lines it could not place rather than silently averaging
/// them away.
/// </summary>
/// <param name="MeasuredLineCount">How many lines got a window.</param>
/// <param name="OverflowLineCount">How many of those overflow it.</param>
/// <param name="OverflowTotalSec">Sum of every measured line's overflow, rounded.</param>
/// <param name="MaxOverflowSec">The worst single overflow, rounded — the number a review loop
/// thresholds against.</param>
/// <param name="UnknownWindowLineIds">Ids of the resolved lines whose containing placement could
/// not be found. This list is empty BY CONSTRUCTION, not in practice: the second a caller looks up
/// comes from <see cref="OutputTimeline.MapToOutputSec"/>, which returns
/// <c>placement.OutputStartSec + (sourceSec - span.SnappedStart)</c> for a span with
/// <c>SnappedStart &lt;= sourceSec &lt; SnappedEnd</c>, while <see cref="OutputTimeline.Build"/>
/// sets that same placement's <c>OutputEndSec</c> to
/// <c>OutputStartSec + (SnappedEnd - SnappedStart)</c> — so the value it returns is always inside
/// its own placement and the lookup cannot fail for a value <c>MapToOutputSec</c> produced. The
/// defensive arm in the executor (and this list with it) remains for a caller that supplies a
/// second from somewhere else; a non-empty list still means those lines are NOT part of the
/// numbers above.</param>
public sealed record NarrationFitSummary(
    int MeasuredLineCount,
    int OverflowLineCount,
    double OverflowTotalSec,
    double MaxOverflowSec,
    IReadOnlyList<string> UnknownWindowLineIds)
{
    /// <summary>True when this summary measured at least one line.</summary>
    public bool Applicable => MeasuredLineCount > 0;
}

/// <summary>
/// The pure, deterministic picture-window fit computation behind the compile step's additive
/// <c>voiceover.fit</c> facts.
///
/// <para>
/// <b>Why the window is the CONTAINING output placement and not the next line.</b> A narration
/// line anchored to a shot has to fit inside THAT SHOT's output window. Measuring against
/// <c>[start, nextLineStart)</c> would report "fits" for a line that runs straight past the cut,
/// which is exactly the defect these facts exist to surface to <c>VideoReviewAgent</c>.
/// <c>OutputTimeline.Placements</c> is the only thing that knows where a span landed once seam
/// overlaps are accounted for, which is why the lookup lives on <see cref="OutputTimeline"/> and
/// this type only turns the answer into numbers.
/// </para>
///
/// <para>
/// <b>Deliberately free of everything else.</b> No model call, no ffmpeg invocation, no I/O, no
/// clock and no configuration: one line's fit is a total function of the placement it landed in,
/// the output second it starts at, and the output seconds it plays for. That makes it exhaustively
/// unit-testable without a scratch space, a bucket, or a media probe, and it keeps the seam for a
/// future word-count solver (which can contribute richer facts) from having to change this shape.
/// </para>
/// </summary>
public static class NarrationFitFacts
{
    /// <summary>
    /// The rounding every seconds value on the voiceover node uses — <c>Math.Round(value, 3)</c>,
    /// i.e. the framework's own midpoint rule, matching the line node's existing
    /// <c>outputStartSec</c>/<c>playDurationSec</c> keys exactly.
    /// </summary>
    public const int Decimals = 3;

    /// <summary>
    /// Measures one line against the output placement its start landed in.
    /// </summary>
    /// <param name="placement">The placement whose <c>[OutputStartSec, OutputEndSec)</c> contains
    /// <paramref name="outputStartSec"/>. Callers obtain it from
    /// <see cref="OutputTimeline.TryGetContainingPlacement"/>; a line whose window cannot be found
    /// is reported as unknown and simply not measured.</param>
    /// <param name="outputStartSec">The line's start on the OUTPUT timeline.</param>
    /// <param name="playDurationSec">How long the line plays for.</param>
    public static NarrationLineFit Measure(
        TimelinePlacement placement, double outputStartSec, double playDurationSec)
    {
        ArgumentNullException.ThrowIfNull(placement);

        double windowStartSec = Round(placement.OutputStartSec);
        double windowEndSec = Round(placement.OutputEndSec);
        double windowSec = Round(windowEndSec - Round(outputStartSec));
        double overflowSec = Overflow(playDurationSec, windowSec);

        return new NarrationLineFit(windowStartSec, windowEndSec, windowSec, overflowSec, overflowSec <= 0);
    }

    /// <summary>
    /// Measures one line against a window that may run past its starting placement: the window
    /// ends at <paramref name="windowEndSec"/> (from <see cref="CarryWindowEnd"/>) and spans
    /// <paramref name="carriedShotCount"/> following shots.
    /// </summary>
    public static NarrationLineFit Measure(
        TimelinePlacement placement, double outputStartSec, double playDurationSec,
        double windowEndSec, int carriedShotCount)
    {
        ArgumentNullException.ThrowIfNull(placement);

        double windowStartSec = Round(placement.OutputStartSec);
        double windowEnd = Round(Math.Max(placement.OutputEndSec, windowEndSec));
        double windowSec = Round(windowEnd - Round(outputStartSec));
        double overflowSec = Overflow(playDurationSec, windowSec);

        return new NarrationLineFit(windowStartSec, windowEnd, windowSec, overflowSec, overflowSec <= 0,
            Math.Max(0, carriedShotCount));
    }

    /// <summary>
    /// Where a line's picture window ends when it may carry over cuts: from the placement it starts
    /// in, the window extends across each FOLLOWING kept shot that starts no line of its own, and
    /// stops at the first shot that does (or at the end of the program). A beat-cut reel holds a
    /// shot for under two seconds, so a single sentence routinely spans two or three of them; only a
    /// line that runs into the NEXT line's shot is talking over another line's picture.
    /// </summary>
    /// <param name="placements">The output timeline's placements, in output order.</param>
    /// <param name="containing">The placement the line starts in.</param>
    /// <param name="lineStartSec">The line's own RAW output start.</param>
    /// <param name="otherLineStartsSec">The RAW output starts of every OTHER line.</param>
    public static (double WindowEndSec, int CarriedShotCount) CarryWindowEnd(
        IReadOnlyList<TimelinePlacement> placements, TimelinePlacement containing,
        double lineStartSec, IReadOnlyCollection<double> otherLineStartsSec)
    {
        ArgumentNullException.ThrowIfNull(placements);
        ArgumentNullException.ThrowIfNull(containing);
        ArgumentNullException.ThrowIfNull(otherLineStartsSec);

        int index = -1;
        for (int i = 0; i < placements.Count; i++)
        {
            if (placements[i] == containing)
            {
                index = i;
                break;
            }
        }

        double end = containing.OutputEndSec;
        if (index < 0)
            return (end, 0);

        // Another line starting later in the SAME shot already claims the picture after it.
        if (otherLineStartsSec.Any(s => s > lineStartSec && s < containing.OutputEndSec))
            return (end, 0);

        int carried = 0;
        for (int i = index + 1; i < placements.Count; i++)
        {
            TimelinePlacement next = placements[i];
            if (otherLineStartsSec.Any(s => s >= next.OutputStartSec && s < next.OutputEndSec))
                break;
            end = Math.Max(end, next.OutputEndSec);
            carried++;
        }

        return (end, carried);
    }

    /// <summary>
    /// Rolls the measured lines up. Unknown-window ids are de-duplicated and blank ids dropped, so
    /// a caller that accumulates them per line cannot produce a list an operator has to clean up.
    /// </summary>
    public static NarrationFitSummary Summarize(
        IEnumerable<NarrationLineFit>? measuredFits,
        IEnumerable<string>? unknownWindowLineIds = null)
    {
        List<NarrationLineFit> fits = measuredFits?.ToList() ?? [];
        List<string> unknown = unknownWindowLineIds?
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList() ?? [];

        return new NarrationFitSummary(
            MeasuredLineCount: fits.Count,
            OverflowLineCount: fits.Count(f => !f.Fits),
            OverflowTotalSec: Round(fits.Sum(f => f.OverflowSec)),
            MaxOverflowSec: fits.Count == 0 ? 0 : Round(fits.Max(f => f.OverflowSec)),
            UnknownWindowLineIds: unknown);
    }

    /// <summary>
    /// An overrun this short is a WAV's trailing breath, not a word over the next shot: a 3.019 s
    /// opener over a 3.0 s shot capped a reel's review at 4.
    /// </summary>
    public const double OverflowToleranceSec = 0.1;

    private static double Overflow(double playDurationSec, double windowSec)
    {
        double over = playDurationSec - windowSec;
        return over <= OverflowToleranceSec ? 0 : Round(over);
    }

    /// <summary>Rounds a seconds value the way every sibling key on the voiceover node does.</summary>
    public static double Round(double value) => Math.Round(value, Decimals);
}
