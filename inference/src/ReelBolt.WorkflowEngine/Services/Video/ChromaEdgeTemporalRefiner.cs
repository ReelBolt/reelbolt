using FrameQuad = ReelBolt.WorkflowEngine.Services.Video.ChromaQuadTracker.FrameQuad;

namespace ReelBolt.WorkflowEngine.Services.Video;

/// <summary>
/// Re-locates each tracked plate's four edges from the NEIGHBOURING frame's edges, and keeps,
/// per edge, whichever line better looks like the plate's own boundary. See docs/video-editing.md
/// "Tracking quality on the customer clips".
///
/// <para>
/// <b>Why.</b> The per-frame detector seeds its edge-line fit from the plate's extreme-point
/// corners, and a corner that something covers is not where the plate's corner is. On the
/// customer's finger-swipe clip a fingertip over the screen's lower-right corner moved that
/// corner up to 40 px along the finger, the right edge's line was fitted between the true
/// top-right corner and the fingertip, and a green wedge opened down the right of the insert for
/// two thirds of a second. On the tablet clip a straight, green-tinted window reflection on the
/// glass did the same: the component stops at the reflection, the coarse corner sits on it, and
/// the "right edge" ran diagonally along the reflection's boundary. The occlusion test in track
/// assembly cannot see either — the plate's core area falls only a few percent while a finger
/// enters, and a reflection costs no area at all.
/// </para>
/// <para>
/// <b>How.</b> A plate moves little between consecutive frames, so the previous frame's edge is a
/// far better seed than this frame's own corners. Each edge is fitted again with
/// <see cref="ChromaEdgeLineFitter.FitEdgeLine"/> along the previous frame's edge — probes that
/// start on the finger or on the reflection are skipped as holes, and the robust fit ignores the
/// minority that cross the wrong boundary — and both candidates are scored by
/// <see cref="ChromaEdgeLineFitter.EdgeContrastScore"/> (plate inside, bezel outside). The
/// re-fitted line replaces the detected one only when it scores clearly better
/// (<see cref="PreferenceMargin"/>), so a clean detection is left exactly as it was. Corners are
/// where the chosen lines meet. The pass runs forward and then backward, so a track whose FIRST
/// frames are the corrupted ones is repaired from the clean frames after them. On a frame bridged
/// across an occlusion, the detection the occlusion test set aside (<c>FrameQuad.SetAside</c>) is
/// offered too — its uncovered edges are real. No candidate may move an edge net INWARD past
/// <see cref="InwardToleranceFraction"/> of the plate: an occluder only removes plate area, so a
/// line further in is the occluder's boundary — the collapse the occlusion hold exists to stop.
/// </para>
/// <para>
/// Applies to flat plates only: a curved plate's edges are not lines, and its mesh warp is built
/// from the profiler's measurements, which this would contradict.
/// </para>
/// </summary>
internal static class ChromaEdgeTemporalRefiner
{
    /// <summary>
    /// How much better (in <see cref="ChromaEdgeLineFitter.EdgeContrastScore"/> units, 0..1) the
    /// re-fitted line must score before it replaces the detected one. A hysteresis, not a
    /// tolerance: on a clean frame both lines are the same edge and score within a few hundredths
    /// of each other, and the detection must win those ties so a clean track is untouched.
    /// </summary>
    internal const double PreferenceMargin = 0.05;

    /// <summary>
    /// Furthest a refined corner may move from the detected one, as a fraction of the plate's
    /// diagonal. The corruption this repairs moved a corner by up to ~6% of the diagonal (the
    /// fingertip) and ~21% (the tablet's window reflection, which drags the top-right corner a
    /// third of the way along the top edge); a much larger move means two lines met far from the
    /// plate, not a repair.
    /// </summary>
    internal const double MaxCornerMoveFraction = 0.35;

    /// <summary>
    /// Median per-frame curvature under which a whole track counts as flat — the compile step's
    /// own <c>MinInsertCurvature</c> default, below which it pins the plate flat regardless.
    /// </summary>
    internal const double FlatTrackCurvature = 0.01;

    internal static List<FrameQuad> Refine(
        List<FrameQuad> run, byte[] pixelData, int width, int height, string color, Action<string>? log = null)
    {
        if (run.Count < 2 || width < 4 || height < 4)
            return run;

        double level = ChromaQuadTracker.Median(run.Where(q => !q.Interpolated && q.PlateDominance > 1).Select(q => q.PlateDominance).ToList());
        if (!(level > 1))
            return run;

        // A plate that reads flat over the track as a whole is flat on every frame: a frame whose
        // own profile read a bow (motion blur on a whip, a hand on the edge) is measurement
        // noise, and the compile step will pin the whole track flat anyway (MinInsertCurvature).
        bool flatTrack = ChromaQuadTracker.Median(run.Select(q => q.Bows.MaxMagnitude).ToList()) < FlatTrackCurvature;

        var dominance = new float[width * height];
        List<FrameQuad> forward = Pass(run, pixelData, width, height, color, level, dominance, flatTrack, reverse: false, log);
        return Pass(forward, pixelData, width, height, color, level, dominance, flatTrack, reverse: true, log);
    }

    private static List<FrameQuad> Pass(
        List<FrameQuad> run, byte[] pixelData, int width, int height, string color, double level,
        float[] dominance, bool flatTrack, bool reverse, Action<string>? log)
    {
        var result = new List<FrameQuad>(run);
        int frameSize = width * height * 3;
        FrameQuad? prior = null;
        for (int n = 0; n < result.Count; n++)
        {
            int i = reverse ? result.Count - 1 - n : n;
            FrameQuad q = result[i];
            long offset = (long)q.FrameIndex * frameSize;
            if (prior is null || (!flatTrack && (!q.Bows.IsFlat || !prior.Bows.IsFlat)) || offset < 0 || offset + frameSize > pixelData.Length)
            {
                prior = q;
                continue;
            }

            for (int p = 0; p < width * height; p++)
            {
                int b0 = (int)offset + p * 3;
                dominance[p] = ChromaEdgeLineFitter.Dominance(color, pixelData[b0], pixelData[b0 + 1], pixelData[b0 + 2]);
            }

            FrameQuad refined = RefineFrame(q, prior, dominance, width, height, level, log is null ? null : m => log($"{(reverse ? "bwd" : "fwd")} f{q.FrameIndex} {m}"));

            result[i] = refined;
            prior = refined;
        }

        return result;
    }

    /// <summary>One frame's edges re-located against <paramref name="prior"/>'s; <paramref name="q"/> itself when nothing scored better.</summary>
    internal static FrameQuad RefineFrame(FrameQuad q, FrameQuad prior, float[] dominance, int width, int height, double level, Action<string>? log = null)
    {
        (double X, double Y)[] det = Cycle(q, width, height);
        (double X, double Y)[] pri = Cycle(prior, width, height);
        (double dcx, double dcy) = Centre(det);
        (double pcx, double pcy) = Centre(pri);

        double shorter = Math.Min(Math.Min(Dist(pri[0], pri[1]), Dist(pri[2], pri[3])), Math.Min(Dist(pri[1], pri[2]), Dist(pri[3], pri[0])));
        if (shorter < 3)
            return q;
        double reach = ChromaEdgeLineFitter.ReachFor(shorter);

        // A bridged sample may carry the detection the occlusion test set aside: its uncovered
        // edges are real measurements, offered as further candidates (and as further seeds).
        (double X, double Y)[]? aside = q.SetAside is { } setAside ? Cycle(setAside, width, height) : null;
        (double acx, double acy) = aside is null ? (0, 0) : Centre(aside);

        var lines = new (double Px, double Py, double Dx, double Dy)[4];
        bool changed = false;
        for (int k = 0; k < 4; k++)
        {
            var detLine = ChromaEdgeLineFitter.LineThrough(det[k], det[(k + 1) % 4]);
            lines[k] = detLine;
            double detScore = ChromaEdgeLineFitter.EdgeContrastScore(dominance, width, height, level, detLine, det[k], det[(k + 1) % 4], dcx, dcy);
            double best = detScore + PreferenceMargin;

            void Consider((double Px, double Py, double Dx, double Dy)? candidate, (double X, double Y) a, (double X, double Y) b, double cx, double cy, string what)
            {
                if (candidate is null)
                    return;

                // Never net-inward: something in front of a plate can only REMOVE plate area, so a
                // line that would pull this edge inward overall is an occluder's boundary, not the
                // plate's — the very collapse the occlusion hold in track assembly exists to stop.
                // Net, not everywhere: the corruption this repairs TILTS an edge (a fingertip drags
                // one end in and, through the corner fit, pushes the other end out), so the right
                // line is inward of the corrupted one at one end and further outward at the other.
                double inward = InwardShift(candidate.Value, det[k], det[(k + 1) % 4], dcx, dcy);
                double across = (Dist(det[(k + 1) % 4], det[(k + 2) % 4]) + Dist(det[(k + 3) % 4], det[k])) / 2;
                if (inward > Math.Max(MinInwardTolerancePx, InwardToleranceFraction * across))
                {
                    log?.Invoke($"edge {k}: {what} refused, {inward:0.0} px inward on average");
                    return;
                }

                double score = ChromaEdgeLineFitter.EdgeContrastScore(dominance, width, height, level, candidate.Value, a, b, cx, cy);
                log?.Invoke($"edge {k}: det {detScore:0.000} {what} {score:0.000}");
                if (score > best)
                {
                    best = score;
                    lines[k] = candidate.Value;
                    changed = true;
                }
            }

            Consider(ChromaEdgeLineFitter.FitEdgeLine(dominance, width, height, level, pri[k], pri[(k + 1) % 4], pcx, pcy, reach),
                pri[k], pri[(k + 1) % 4], pcx, pcy, "fit");
            if (aside is not null)
            {
                Consider(ChromaEdgeLineFitter.LineThrough(aside[k], aside[(k + 1) % 4]), aside[k], aside[(k + 1) % 4], acx, acy, "aside");
                Consider(ChromaEdgeLineFitter.FitEdgeLine(dominance, width, height, level, aside[k], aside[(k + 1) % 4], acx, acy, reach),
                    aside[k], aside[(k + 1) % 4], acx, acy, "asidefit");
            }
        }

        if (!changed)
            return q;

        var corners = new (double X, double Y)[4];
        double diagonal = Math.Max(Dist(det[0], det[2]), Dist(det[1], det[3]));
        for (int k = 0; k < 4; k++)
        {
            (double X, double Y)? c = ChromaEdgeLineFitter.IntersectLines(lines[(k + 3) % 4], lines[k]);
            if (c is null || Dist(c.Value, det[k]) > MaxCornerMoveFraction * diagonal)
                return q;
            corners[k] = c.Value;
        }

        if (!ChromaEdgeLineFitter.IsConvexQuad(corners))
            return q;

        // Back to normalized pixel-centre coordinates; FrameQuad stores TL, TR, BL, BR.
        double NX(double x) => (x + 0.5) / width;
        double NY(double y) => (y + 0.5) / height;
        (double, double, double, double, double, double, double, double) surface = q.Surface == default
            ? default
            : (NX(corners[0].X), NY(corners[0].Y), NX(corners[1].X), NY(corners[1].Y),
               NX(corners[3].X), NY(corners[3].Y), NX(corners[2].X), NY(corners[2].Y));
        return q with
        {
            X0 = NX(corners[0].X),
            Y0 = NY(corners[0].Y),
            X1 = NX(corners[1].X),
            Y1 = NY(corners[1].Y),
            X2 = NX(corners[3].X),
            Y2 = NY(corners[3].Y),
            X3 = NX(corners[2].X),
            Y3 = NY(corners[2].Y),
            Surface = surface,
            // Lines describe a flat plate; whatever bow this frame's profile read is superseded.
            Bows = InsertPlateBows.Flat,
            // A bridged sample that has just been MEASURED is a measurement now: it must anchor
            // the outlier pass rather than be re-interpolated by it.
            Interpolated = false
        };
    }

    /// <summary>
    /// How far a candidate edge may sit INSIDE, on average, the edge it would replace, as a
    /// fraction of the plate's extent across that edge (never under
    /// <see cref="MinInwardTolerancePx"/>). Relative to the plate, like every tolerance here. The
    /// two cases it separates are far apart: the corrections it must let through moved an edge
    /// inward by 2-3% of the plate (a corner fit pushed out past the plate by a fingertip on the
    /// swipe clip), the collapse it must refuse is an occluder's boundary a third of the way in.
    /// </summary>
    internal const double InwardToleranceFraction = 0.05;

    /// <summary>Floor of the inward tolerance, in grid pixels — the edge fit's own noise.</summary>
    internal const double MinInwardTolerancePx = 1.0;

    /// <summary>
    /// How far, on average along the edge <paramref name="a"/>→<paramref name="b"/>, the
    /// <paramref name="candidate"/> line sits inward of it (towards the plate's centre
    /// (<paramref name="cx"/>, <paramref name="cy"/>)) — measured along the edge's normal at five
    /// points from 10% to 90% of it; negative when it lies further out. Proportional to the plate
    /// area the replacement would remove.
    /// </summary>
    internal static double InwardShift(
        (double Px, double Py, double Dx, double Dy) candidate, (double X, double Y) a, (double X, double Y) b, double cx, double cy)
    {
        var edge = ChromaEdgeLineFitter.LineThrough(a, b);
        double nx = -edge.Dy, ny = edge.Dx;
        if ((cx - a.X) * nx + (cy - a.Y) * ny > 0)
        {
            nx = -nx;
            ny = -ny;
        }

        double sum = 0;
        double[] stations = [0.1, 0.3, 0.5, 0.7, 0.9];
        foreach (double t in stations)
        {
            (double X, double Y) p = (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            (double X, double Y)? hit = ChromaEdgeLineFitter.IntersectLines(candidate, (p.X, p.Y, nx, ny));
            if (hit is null)
                return double.PositiveInfinity; // parallel to the normal: not a candidate for this edge at all
            double outward = (hit.Value.X - p.X) * nx + (hit.Value.Y - p.Y) * ny;
            sum -= outward;
        }

        return sum / stations.Length;
    }

    /// <summary>Corners in grid pixels, in cycle order TL, TR, BR, BL.</summary>
    private static (double X, double Y)[] Cycle(FrameQuad q, int width, int height) =>
    [
        (q.X0 * width - 0.5, q.Y0 * height - 0.5),
        (q.X1 * width - 0.5, q.Y1 * height - 0.5),
        (q.X3 * width - 0.5, q.Y3 * height - 0.5),
        (q.X2 * width - 0.5, q.Y2 * height - 0.5)
    ];

    private static (double X, double Y) Centre((double X, double Y)[] c) =>
        ((c[0].X + c[1].X + c[2].X + c[3].X) / 4, (c[0].Y + c[1].Y + c[2].Y + c[3].Y) / 4);

    private static double Dist((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
