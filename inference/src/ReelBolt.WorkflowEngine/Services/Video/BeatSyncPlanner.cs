namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>One cut of a beat-synced program: where it would have fallen and where it falls now.</summary>
/// <param name="Segment">0-based index of the kept span the cut ends.</param>
/// <param name="OutputEndSec">The cut's program time before snapping.</param>
/// <param name="SnappedEndSec">The cut's program time after snapping.</param>
/// <param name="Grid"><c>drop</c>, <c>bar</c>, <c>beat</c>, or <c>none</c> (the span was kept whole: no grid line fit).</param>
public sealed record BeatSyncCut(int Segment, double OutputEndSec, double SnappedEndSec, string Grid)
{
    /// <summary>How much of the span's tail was cut to land on the grid; negative when the cut moved later,
    /// into footage a target-length fit had trimmed (<see cref="BeatSyncPlanner.Plan"/>'s <c>maxDurations</c>).</summary>
    public double TrimmedSec => Math.Round(OutputEndSec - SnappedEndSec, 4);
}

/// <summary>
/// Snaps every cut of a program to the music's beat grid (see docs/video-editing.md "Cutting to the
/// beat"). Pure — durations in, durations out; the caller owns the spans.
/// </summary>
public static class BeatSyncPlanner
{
    /// <summary>
    /// Walks the kept spans in program order. Each span ends, in program time, at the running cursor
    /// plus its length; that cut moves EARLIER to the last grid line (bars, or beats with
    /// <paramref name="useBars"/> false) at or before it that still leaves the span at least one
    /// beat long, and at least <paramref name="minDurations"/>[i] long — the narration spoken over
    /// that span — only the span's TAIL is ever trimmed, never extended past its own footage. With
    /// bars and no bar line fitting, a beat line is tried; with neither, the span is kept whole and
    /// the next cut re-aligns from wherever it ends. The drop: when <paramref name="dropSec"/> is at
    /// least two bars in (a real intro) and falls inside a span at least a beat after that span's
    /// start, and no more than one bar before that span's end, that span is cut exactly on the drop, so the
    /// next shot lands with it; a drop further back is skipped (one snap never removes more than a bar).
    /// <para>
    /// <paramref name="maxDurations"/> (null = the rule above, unchanged) is the footage each span
    /// really has: the length the editor gave it before a target-length fit shortened its tail. When
    /// it is given, a cut may land anywhere in <c>[floor, cursor + maxDurations[i]]</c> — still inside
    /// the span's own footage, never past it — on the grid line NEAREST the cut's unsnapped program
    /// time (the running sum of <paramref name="durations"/>), instead of the last line before it.
    /// Each cut then re-aims at where the fitted program put it, so snapping no longer loses up to a
    /// grid step per cut and the fitted length survives within about one grid step. A span whose
    /// <paramref name="maxDurations"/> equals its duration can only move earlier, exactly as before.
    /// </para>
    /// </summary>
    public static (List<double> Durations, List<BeatSyncCut> Cuts, bool DropAligned) Plan(
        IReadOnlyList<double> durations,
        IReadOnlyList<double> beats,
        IReadOnlyList<double> bars,
        double beatSec,
        double? dropSec,
        bool useBars,
        IReadOnlyList<double>? minDurations = null,
        IReadOnlyList<double>? maxDurations = null)
    {
        var outDurations = new List<double>(durations.Count);
        var cuts = new List<BeatSyncCut>(durations.Count);
        const double eps = 1e-6;
        double cursor = 0;
        bool dropPending = dropSec is { } d0 && bars.Count > 1 && d0 >= 2 * 4 * beatSec - eps;
        bool dropAligned = false;
        double idealEnd = 0;
        // The most one snap may remove from a span's tail: a bar. The drop is only cut on when it is
        // that close to where the span would have ended; a drop further back would throw away the
        // rest of the film (run ec066f71: one 19.8 s segment cut to 6.9 s on the drop).
        double maxTrim = bars.Count > 1 ? bars[1] - bars[0] : 4 * beatSec;

        for (int i = 0; i < durations.Count; i++)
        {
            double end = cursor + durations[i];
            idealEnd += durations[i];
            // The latest a cut may land: the span's own footage. Equal to `end` without maxDurations.
            double reach = maxDurations is { } maxes && i < maxes.Count
                ? cursor + Math.Max(durations[i], maxes[i])
                : end;
            double? target = null;
            string grid = "none";
            // The span may never be cut shorter than the narration spoken over it (minDurations):
            // a snapped cut that drops the end of a line onto the next shot is worse than an
            // off-beat one.
            double floor = cursor + Math.Max(beatSec, minDurations is { } mins && i < mins.Count ? mins[i] : 0);

            if (dropPending && dropSec is { } drop && drop >= floor - eps && drop <= end + eps && end - drop <= maxTrim + eps)
            {
                target = Math.Min(drop, end);
                grid = "drop";
                dropAligned = true;
                dropPending = false;
            }
            else
            {
                if (useBars)
                {
                    target = maxDurations is null ? LastLine(bars, floor, end) : NearestLine(bars, floor, reach, idealEnd);
                    if (target is not null)
                        grid = "bar";
                }

                if (target is null)
                {
                    target = maxDurations is null ? LastLine(beats, floor, end) : NearestLine(beats, floor, reach, idealEnd);
                    if (target is not null)
                        grid = "beat";
                }

                // The drop fell inside this span but too close to its start to cut on: it is gone.
                if (dropPending && dropSec is { } passed && passed <= end + eps)
                    dropPending = false;
            }

            double snapped = target ?? end;
            outDurations.Add(snapped - cursor);
            cuts.Add(new BeatSyncCut(i, Math.Round(end, 4), Math.Round(snapped, 4), grid));
            cursor = snapped;
        }

        return (outDurations, cuts, dropAligned);
    }

    /// <summary>The last grid line in <c>[floor, end]</c>, or null.</summary>
    private static double? LastLine(IReadOnlyList<double> lines, double floor, double end)
    {
        const double eps = 1e-6;
        double? best = null;
        foreach (double line in lines)
        {
            if (line > end + eps)
                break;
            if (line >= floor - eps)
                best = line;
        }

        return best;
    }

    /// <summary>
    /// The grid line in <c>[floor, reach]</c> nearest <paramref name="aim"/> (the earlier one on a tie), or null.
    /// </summary>
    private static double? NearestLine(IReadOnlyList<double> lines, double floor, double reach, double aim)
    {
        const double eps = 1e-6;
        double? best = null;
        foreach (double line in lines)
        {
            if (line > reach + eps)
                break;
            if (line < floor - eps)
                continue;
            if (best is null || Math.Abs(line - aim) < Math.Abs(best.Value - aim) - eps)
                best = line;
        }

        return best;
    }

    /// <summary>Largest distance, in milliseconds, from a rendered cut time to the nearest grid line.</summary>
    public static double MaxOffsetMs(IReadOnlyList<double> cutTimes, IReadOnlyList<double> lines) =>
        lines.Count == 0 ? 0 : Math.Round(cutTimes.Max(c => lines.Min(l => Math.Abs(l - c))) * 1000.0, 1);

    /// <summary>
    /// The grid lines of a track looped to cover <paramref name="untilSec"/> of program: the track's
    /// own lines, then the same lines shifted by its length for every loop (background music loops to
    /// fit the program). With no length, the lines continue at the track's own spacing.
    /// </summary>
    public static List<double> LoopedGrid(IReadOnlyList<double> lines, double trackSec, double untilSec)
    {
        var all = new List<double>();
        if (lines.Count == 0)
            return all;
        if (trackSec <= 0)
        {
            all.AddRange(lines.Where(l => l <= untilSec));
            return all;
        }

        for (double offset = 0; offset <= untilSec; offset += trackSec)
        {
            foreach (double l in lines)
            {
                double t = l + offset;
                if (l < trackSec && t <= untilSec)
                    all.Add(Math.Round(t, 4));
            }
        }

        return all;
    }
}
