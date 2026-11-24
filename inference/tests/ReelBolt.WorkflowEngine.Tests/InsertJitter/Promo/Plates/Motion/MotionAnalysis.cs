#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo.Plates.Motion;

/// <summary>One film's references and tracks, everything per frame in plate source px.</summary>
internal sealed record RefSet(
    string Name, int N,
    IReadOnlyList<(string Label, Quad?[] Track)> Tracks,
    PlateMeasurement Chroma, PlateMeasurement Luma,
    IReadOnlyList<RingPair> Ring, Quad?[] RingPos, double?[][] RingVel,
    Quad?[] Geometry);

internal static class MotionAnalysis
{
    public const double Out = PlateScorer.OutPerSrc;
    private static readonly double Root2 = Math.Sqrt(2);

    public static string F(double v, string f = "0.000") => double.IsNaN(v) || double.IsInfinity(v) ? "-" : v.ToString(f, CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------------------------------------- building a RefSet

    public static double?[][] RingVelocity(IReadOnlyList<RingPair> pairs, Quad?[] geometry)
    {
        var o = new double?[8][];
        for (int k = 0; k < 8; k++)
            o[k] = new double?[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
        {
            if (!pairs[i].Ok || i >= geometry.Length || geometry[i] is not { } g)
                continue;
            Quad p = RingMotion.PredictCorners(g, pairs[i].H);
            for (int c = 0; c < 4; c++)
            {
                o[2 * c][i] = p.Corner(c).X - g.Corner(c).X;
                o[2 * c + 1][i] = p.Corner(c).Y - g.Corner(c).Y;
            }
        }

        return o;
    }

    /// <summary>The ring chained from the plate's own start position; a failed pair breaks the chain (null at that frame, a fresh chain anchored on the geometry after it, so a spectrum window never straddles two chains).</summary>
    public static Quad?[] RingPositions(IReadOnlyList<RingPair> pairs, Quad?[] geometry)
    {
        int n = pairs.Count + 1;
        var o = new Quad?[n];
        Quad? cur = geometry[0];
        o[0] = cur;
        for (int i = 0; i < pairs.Count; i++)
        {
            if (pairs[i].Ok && cur is { } c)
            {
                cur = RingMotion.PredictCorners(c, pairs[i].H) with { T = (i + 1) / PromoInputs.Fps };
                o[i + 1] = cur;
            }
            else
            {
                o[i + 1] = null;
                cur = i + 1 < geometry.Length ? geometry[i + 1] : null;
            }
        }

        return o;
    }

    public static PlateMeasurement WindowMeasurement(PlateMeasurement m, int from, int to)
    {
        var quads = new Quad?[m.N];
        var edges = new PromoEdge[m.N][];
        PromoEdge Invalid() => new(false, "window", (0, 0), (0, 0), double.NaN, double.NaN, double.NaN, 0, 0);
        for (int i = 0; i < m.N; i++)
        {
            bool inside = i >= from && i <= to;
            quads[i] = inside ? m.Quad[i] : null;
            edges[i] = inside ? m.Edges[i] : [Invalid(), Invalid(), Invalid(), Invalid()];
        }

        return new PlateMeasurement(m.N, quads, edges);
    }

    public static double?[][] Mask(double?[][] v, int from, int to)
    {
        var o = new double?[v.Length][];
        for (int k = 0; k < v.Length; k++)
        {
            o[k] = new double?[v[k].Length];
            for (int i = 0; i < v[k].Length; i++)
                o[k][i] = i >= from && i + 1 <= to ? v[k][i] : null;
        }

        return o;
    }

    /// <summary>Velocity channels restricted to frames where a mask channel also has a velocity (so every channel is scored on the same samples).</summary>
    public static double?[][] And(double?[][] v, double?[][] other)
    {
        var o = new double?[v.Length][];
        for (int k = 0; k < v.Length; k++)
        {
            o[k] = new double?[v[k].Length];
            for (int i = 0; i < v[k].Length; i++)
                o[k][i] = i < other[k].Length && other[k][i] is not null ? v[k][i] : null;
        }

        return o;
    }

    // ---------------------------------------------------------------------------------------------- the report

    public static readonly (double Lo, double Hi, string Name)[] Bands = [(0.5, 2, "0.5-2 Hz"), (2, 4, "2-4 Hz"), (4, 8, "4-8 Hz"), (8, 12.01, "8-12 Hz"), (2, 12.01, ">2 Hz")];

    public static string Render(RefSet r, (string Name, int From, int To)[] windows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"## {r.Name}");
        sb.AppendLine();
        sb.AppendLine($"Frames {r.N}; chroma meter valid {r.Chroma.Valid}, luma meter valid {r.Luma.Valid}, ring pairs ok {r.Ring.Count(p => p.Ok)}/{r.Ring.Count} (median ring pixels {r.Ring.Select(p => p.Pixels).OrderBy(x => x).ElementAt(r.Ring.Count / 2)}). Output px = source px x {F(Out, "0.0000")}; slip = frame-to-frame change of the track-minus-reference difference (2-D, RMS over corners / pooled edge points).");
        sb.AppendLine();
        foreach ((string wname, int from, int to) in windows)
        {
            sb.AppendLine($"### {r.Name}, {wname} (frames {from}-{to})");
            sb.AppendLine();
            sb.AppendLine(ScoreTable(r, from, to));
            sb.AppendLine(FloorTable(r, from, to));
            sb.AppendLine(SpectrumTables(r, from, to));
            sb.AppendLine(RemovalTables(r, from, to));
        }

        return sb.ToString();
    }

    private static string Row(params string[] cells) => "| " + string.Join(" | ", cells) + " |";

    private static string O(double? srcPx) => srcPx is null ? "-" : F(srcPx.Value * Out);

    public static string ScoreTable(RefSet r, int from, int to)
    {
        PlateMeasurement ch = WindowMeasurement(r.Chroma, from, to), lu = WindowMeasurement(r.Luma, from, to);
        var sb = new StringBuilder();
        sb.AppendLine("Tracks scored against each independent reference, output px. `quad` = corners (frames where the reference quad exists), `edges` = four edges pooled (normal offsets at 4 points per edge); SlipRms / HfRms as the T-011 report; `ring` = corner velocities of the track against the scene ring's homography (velocity domain, no drift), `ring hf` = the same after removing the 13-frame mean (>2 Hz).");
        sb.AppendLine();
        sb.AppendLine(Row("track", "chroma quad slip", "chroma edges slip", "chroma edges hf", "luma quad slip", "luma edges slip", "luma edges hf", "ring quad slip", "ring hf slip", "frames chroma/luma/ring"));
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        double?[][] ringV = Mask(r.RingVel, from, to);
        foreach ((string label, Quad?[] track) in r.Tracks)
        {
            IReadOnlyList<Quad> t = track.Select(q => q ?? default).ToList();
            TrackScore sc = PlateScorer.Score(label, t, ch);
            TrackScore sl = PlateScorer.Score(label, t, lu);
            double?[][] tv = Mask(Chan.Velocity(track), from, to);
            PairStats ring = PairStats.Of(tv, ringV);
            PairStats ringHf = PairStats.Of(Chan.HighPass(And(tv, ringV), 13), Chan.HighPass(And(ringV, tv), 13));
            sb.AppendLine(Row(
                label,
                O(sc.Quad.Row.Stats?.SlipRms), O(sc.EdgesPooled.Row.Stats?.SlipRms), O(sc.EdgesPooled.Row.Stats?.HfRms),
                O(sl.Quad.Row.Stats?.SlipRms), O(sl.EdgesPooled.Row.Stats?.SlipRms), O(sl.EdgesPooled.Row.Stats?.HfRms),
                F(ring.RmsDiff * Root2 * Out), F(ringHf.RmsDiff * Root2 * Out),
                $"{sc.Frames}/{sl.Frames}/{ring.N / 8}"));
        }

        return sb.ToString();
    }

    /// <summary>The references' own floors on the window: leave-one-out residual of each measured quad and the size of each reference's motion.</summary>
    public static string FloorTable(RefSet r, int from, int to)
    {
        var sb = new StringBuilder();
        Quad?[] c = Chan.Window(Chan.Of(r.Chroma), from, to), l = Chan.Window(Chan.Of(r.Luma), from, to);
        (double rc, _, int fc) = PromoScore.ResidualFloor(c);
        (double rl, _, int fl) = PromoScore.ResidualFloor(l);
        double?[][] cv = Mask(Chan.Velocity(c), from, to), lv = Mask(Chan.Velocity(l), from, to), rv = Mask(r.RingVel, from, to);
        PairStats cl = PairStats.Of(cv, lv), cr = PairStats.Of(cv, rv), lr = PairStats.Of(lv, rv);
        sb.AppendLine("References against each other (velocity domain, per-frame change of the 4 corners, output px, 2-D RMS): size of each, size of their difference, regression slope of the first on the second.");
        sb.AppendLine();
        sb.AppendLine(Row("pair", "frames", "rms A", "rms B", "rms A-B", "slope A on B", "corr"));
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach ((string n, PairStats s) in new[] { ("chroma vs luma", cl), ("chroma vs ring", cr), ("luma vs ring", lr) })
            sb.AppendLine(Row(n, (s.N / 8).ToString(CultureInfo.InvariantCulture), F(s.RmsA * Root2 * Out), F(s.RmsB * Root2 * Out), F(s.RmsDiff * Root2 * Out), F(s.SlopeAOnB, "0.00"), F(s.Corr, "0.00")));
        sb.AppendLine();
        sb.AppendLine($"No-truth leave-one-out residual of the measured quad (src px): chroma {F(rc)} ({fc} frames), luma {F(rl)} ({fl} frames).");
        sb.AppendLine();
        return sb.ToString();
    }

    public static string SpectrumTables(RefSet r, int from, int to)
    {
        var sb = new StringBuilder();
        Quad?[] c = Chan.Window(Chan.Of(r.Chroma), from, to), l = Chan.Window(Chan.Of(r.Luma), from, to), g = Chan.Window(r.RingPos, from, to);
        // common frames: where every reference exists
        Quad?[] all = Chan.Both(Chan.Both(c, l), g);
        Quad?[] cm = Chan.Both(c, all), lm = Chan.Both(l, all), gm = Chan.Both(g, all);
        sb.AppendLine("Position spectra of each channel's motion (output px 2-D RMS per corner (per-coordinate x 1.414) in the band; 2 s Welch segments over frames where chroma meter, luma meter and ring all exist). Coherence = band-averaged magnitude-squared coherence with the named channel.");
        sb.AppendLine();
        var channels = new List<(string Name, Quad?[] Q)> { ("chroma meter", cm), ("luma meter", lm), ("ring", gm) };
        foreach ((string label, Quad?[] track) in r.Tracks)
            channels.Add((label, Chan.Both(track, all)));
        sb.AppendLine(Row(["channel", .. Bands.Select(b => b.Name + " rms"), "coh w/ luma >2 Hz", "coh w/ ring >2 Hz", "coh w/ chroma >2 Hz"]));
        sb.AppendLine("|---|" + string.Concat(Bands.Select(_ => "---:|")) + "---:|---:|---:|");
        double?[][] cc = Chan.Coords(cm), lc = Chan.Coords(lm), gc = Chan.Coords(gm);
        foreach ((string name, Quad?[] q) in channels)
        {
            double?[][] xc = Chan.Coords(q);
            CrossSpectrum? self = CrossSpectrum.Compute(xc, xc);
            if (self is null)
            {
                sb.AppendLine(Row([name, .. Bands.Select(_ => "-"), "-", "-", "-"]));
                continue;
            }

            CrossSpectrum? withL = CrossSpectrum.Compute(xc, lc), withR = CrossSpectrum.Compute(xc, gc), withC = CrossSpectrum.Compute(xc, cc);
            sb.AppendLine(Row([
                name, .. Bands.Select(b => F(Math.Sqrt(2 * self.Power(self.Sxx, b.Lo, b.Hi)) * Out)),
                name == "luma meter" ? "-" : F(withL?.Coherence(2, 12.01) ?? double.NaN, "0.00"),
                name == "ring" ? "-" : F(withR?.Coherence(2, 12.01) ?? double.NaN, "0.00"),
                name == "chroma meter" ? "-" : F(withC?.Coherence(2, 12.01) ?? double.NaN, "0.00")]));
        }

        sb.AppendLine();
        sb.AppendLine("Real content of each channel's >2 Hz motion, read through the references: `g` = slope of the channel on the reference's >2 Hz motion (regression), `real` = |g| x rms of the reference, `rest` = what is left (output px 2-D RMS per corner (per-coordinate x 1.414)). The ring is the cleaner reference where it has texture (Film B); luma elsewhere.");
        sb.AppendLine();
        sb.AppendLine(Row("channel", "rms >2 Hz", "g on luma", "real via luma", "rest", "g on ring", "real via ring", "rest"));
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach ((string name, Quad?[] q) in channels)
        {
            double?[][] xc = Chan.Coords(q);
            CrossSpectrum? self = CrossSpectrum.Compute(xc, xc), xl = CrossSpectrum.Compute(xc, lc), xr = CrossSpectrum.Compute(xc, gc);
            if (self is null || xl is null || xr is null)
                continue;
            double tot = Math.Sqrt(2 * self.Power(self.Sxx, 2, 12.01)) * Out;
            double gL = xl.Slope(2, 12.01), gR = xr.Slope(2, 12.01);
            double realL = Math.Abs(gL) * Math.Sqrt(2 * xl.Power(xl.Syy, 2, 12.01)) * Out, realR = Math.Abs(gR) * Math.Sqrt(2 * xr.Power(xr.Syy, 2, 12.01)) * Out;
            sb.AppendLine(Row(name, F(tot), F(gL, "0.00"), F(realL), F(Math.Sqrt(Math.Max(0, tot * tot - realL * realL))), F(gR, "0.00"), F(realR), F(Math.Sqrt(Math.Max(0, tot * tot - realR * realR)))));
        }

        sb.AppendLine();
        sb.AppendLine(ErrorTable(r, all, cm, lm, gm));
        sb.AppendLine(ThreeCorner(cm, lm, gm));
        return sb.ToString();
    }

    /// <summary>2-D corner RMS (source px) of the track-minus-reference difference in the band [lo, hi) Hz, over the frames where both exist; NaN when there is no overlap.</summary>
    public static double BandError(Quad?[] track, Quad?[] reference, double lo, double hi)
    {
        double?[][] e = Chan.Coords(Chan.Minus(track, reference));
        CrossSpectrum? s = CrossSpectrum.Compute(e, e);
        return s is null ? double.NaN : Math.Sqrt(2 * s.Power(s.Sxx, lo, hi));
    }

    /// <summary>Each track's error against each reference, per band: what a tracker is judged by when the reference is not made of the tracker's own samples.</summary>
    private static string ErrorTable(RefSet r, Quad?[] all, Quad?[] chroma, Quad?[] luma, Quad?[] ring)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Band error of each track against each reference (2-D corner RMS of track minus reference in the band, output px; same frames as above). A reference's own noise is in every row of its block: compare rows, not rows to zero. The chroma meter reads the tracker's own chroma samples, luma and ring do not.");
        sb.AppendLine();
        sb.AppendLine(Row(["reference", "track", .. Bands.Select(b => b.Name)]));
        sb.AppendLine("|---|---|" + string.Concat(Bands.Select(_ => "---:|")));
        foreach ((string rname, Quad?[] reference) in new[] { ("chroma meter", chroma), ("luma meter", luma), ("ring", ring) })
        {
            foreach ((string label, Quad?[] track) in r.Tracks)
                sb.AppendLine(Row([rname, label, .. Bands.Select(b => F(BandError(Chan.Both(track, all), reference, b.Lo, b.Hi) * Out))]));
        }

        return sb.ToString();
    }

    /// <summary>Three-cornered hat: three channels each = shared truth + independent noise. S_truth = Sxy Sxz / Syz; noise_i = S_ii - S_truth.</summary>
    public static string ThreeCorner(Quad?[] chroma, Quad?[] luma, Quad?[] ring)
    {
        double?[][] a = Chan.Coords(chroma), b = Chan.Coords(luma), c = Chan.Coords(ring);
        CrossSpectrum? ab = CrossSpectrum.Compute(a, b), ac = CrossSpectrum.Compute(a, c), bc = CrossSpectrum.Compute(b, c);
        CrossSpectrum? aa = CrossSpectrum.Compute(a, a), bb = CrossSpectrum.Compute(b, b), cc = CrossSpectrum.Compute(c, c);
        var sb = new StringBuilder();
        if (ab is null || ac is null || bc is null || aa is null || bb is null || cc is null)
        {
            sb.AppendLine("Three-cornered hat: not enough frames where all three channels exist.");
            return sb.ToString();
        }

        sb.AppendLine("Three-cornered hat on the three references (chroma meter, luma meter, ring each = shared plate motion + noise independent of the other two; output px 2-D RMS per corner (per-coordinate x 1.414) in the band). `shared` = the part all three see, `X noise` = what only X sees.");
        sb.AppendLine();
        sb.AppendLine(Row("band", "shared", "chroma noise", "luma noise", "ring noise", "chroma total", "luma total", "ring total"));
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach ((double lo, double hi, string name) in Bands)
        {
            double sab = ab.SharedPower(lo, hi), sac = ac.SharedPower(lo, hi), sbc = bc.SharedPower(lo, hi);
            double saa = aa.Power(aa.Sxx, lo, hi), sbb = bb.Power(bb.Sxx, lo, hi), scc = cc.Power(cc.Sxx, lo, hi);
            double shared = sbc != 0 ? sab * sac / sbc : double.NaN;
            double S(double v) => double.IsNaN(v) ? double.NaN : Math.Sqrt(Math.Max(0, 2 * v)) * Out;
            sb.AppendLine(Row(name, F(S(shared)), F(S(saa - shared)), F(S(sbb - shared)), F(S(scc - shared)), F(S(saa)), F(S(sbb)), F(S(scc))));
        }

        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>What F2 removed (decode-only minus decode+smoother; shipped-decode minus smoother-only) against what the independent references see beyond the smoothed track.</summary>
    public static string RemovalTables(RefSet r, int from, int to)
    {
        var sb = new StringBuilder();
        Quad?[] c = Chan.Window(Chan.Of(r.Chroma), from, to), l = Chan.Window(Chan.Of(r.Luma), from, to), g = Chan.Window(r.RingPos, from, to);
        Quad?[] T(string label) => r.Tracks.First(t => t.Label == label).Track;
        sb.AppendLine("What F2 removed, tested against the references. `removed` = (track without the smoother) minus (same decode with the smoother). `residual` = reference minus the smoothed track. If the removed part is REAL motion, the independent reference's residual contains it (slope near 1, coherence well above its noise floor); if it is noise of the chroma/codec path, the luma and ring residuals do not (slope near 0). The chroma meter reads the same chroma samples as the tracker, so its slope is the confound: it is expected to be high either way.");
        sb.AppendLine();
        sb.AppendLine(Row("pair", "band", "rms removed", "slope chroma resid", "coh", "slope luma resid", "coh", "slope ring resid", "coh", "rms luma resid", "rms ring resid"));
        sb.AppendLine("|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach ((string name, string without, string with) in new[] { ("F1: decode-only minus F1+F2", "f1-decode-only", "f1f2"), ("shipped minus F2-only", "shipped", "f2-smoother-only") })
        {
            Quad?[] tw = T(without), ts = T(with);
            Quad?[] common = Chan.Both(Chan.Both(Chan.Both(c, l), g), Chan.Both(tw, ts));
            Quad?[] rem = Chan.Minus(Chan.Both(tw, common), Chan.Both(ts, common));
            Quad?[] rc = Chan.Minus(Chan.Both(c, common), Chan.Both(ts, common));
            Quad?[] rl = Chan.Minus(Chan.Both(l, common), Chan.Both(ts, common));
            Quad?[] rg = Chan.Minus(Chan.Both(g, common), Chan.Both(ts, common));
            double?[][] remC = Chan.Coords(rem);
            CrossSpectrum? cr = CrossSpectrum.Compute(remC, Chan.Coords(rc)), lr = CrossSpectrum.Compute(remC, Chan.Coords(rl)), gr = CrossSpectrum.Compute(remC, Chan.Coords(rg));
            CrossSpectrum? rr = CrossSpectrum.Compute(remC, remC);
            foreach ((double lo, double hi, string bname) in Bands.Where(b => b.Name != "0.5-2 Hz"))
            {
                if (cr is null || lr is null || gr is null || rr is null)
                {
                    sb.AppendLine(Row(name, bname, "-", "-", "-", "-", "-", "-", "-", "-", "-"));
                    continue;
                }

                // CrossSpectrum(x = removed, y = residual): slope of removed on residual is not what we want; we want residual on removed = Re Sxy / Sxx.
                double SlopeResOnRem(CrossSpectrum s) => s.Power(s.Sxx, lo, hi) > 0 ? s.SharedPower(lo, hi) / s.Power(s.Sxx, lo, hi) : double.NaN;
                sb.AppendLine(Row(name, bname,
                    F(Math.Sqrt(2 * rr.Power(rr.Sxx, lo, hi)) * Out),
                    F(SlopeResOnRem(cr), "0.00"), F(cr.Coherence(lo, hi), "0.00"),
                    F(SlopeResOnRem(lr), "0.00"), F(lr.Coherence(lo, hi), "0.00"),
                    F(SlopeResOnRem(gr), "0.00"), F(gr.Coherence(lo, hi), "0.00"),
                    F(Math.Sqrt(2 * lr.Power(lr.Syy, lo, hi)) * Out), F(Math.Sqrt(2 * gr.Power(gr.Syy, lo, hi)) * Out)));
            }
        }

        sb.AppendLine();
        return sb.ToString();
    }
}
