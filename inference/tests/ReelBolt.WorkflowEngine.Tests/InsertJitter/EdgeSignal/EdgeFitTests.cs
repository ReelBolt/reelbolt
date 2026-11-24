#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.EdgeSignal;

/// <summary>Fast, always-on checks of the prototype edge fit (no ffmpeg, no clips).</summary>
public class EdgeFitTests
{
    private const int W = 120, H = 90;

    /// <summary>Coverage plane (0..1) of a convex quad, 8x8 supersampled; pixel centres at integers, pixel i covers [i-0.5, i+0.5).</summary>
    private static float[] Coverage((double X, double Y)[] quad)
    {
        var cov = new float[W * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < 8; sy++)
                {
                    for (int sx = 0; sx < 8; sx++)
                    {
                        double px = x - 0.5 + (sx + 0.5) / 8, py = y - 0.5 + (sy + 0.5) / 8;
                        if (Inside(quad, px, py))
                            inside++;
                    }
                }

                cov[y * W + x] = inside / 64f;
            }
        }

        return cov;
    }

    private static bool Inside((double X, double Y)[] q, double px, double py)
    {
        for (int k = 0; k < 4; k++)
        {
            var a = q[k];
            var b = q[(k + 1) % 4];
            if ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X) < 0)
                return false;
        }

        return true;
    }

    /// <summary>TL, TR, BR, BL of a slightly rotated rectangle whose corner positions depend on a sub-pixel phase.</summary>
    private static (double X, double Y)[] Plate(double phaseX, double phaseY, double degrees = 2.5)
    {
        double a = degrees * Math.PI / 180, c = Math.Cos(a), s = Math.Sin(a);
        double cx = 60 + phaseX, cy = 45 + phaseY, hw = 38, hh = 24;
        (double, double) P(double dx, double dy) => (cx + dx * c - dy * s, cy + dx * s + dy * c);
        return [P(-hw, -hh), P(hw, -hh), P(hw, hh), P(-hw, hh)];
    }

    private static (double X, double Y)[] Integer((double X, double Y)[] q) => q.Select(p => (Math.Round(p.X), Math.Round(p.Y))).ToArray();

    private static double MaxCornerError((double X, double Y)[] got, (double X, double Y)[] truth) =>
        got.Zip(truth, (g, t) => Math.Sqrt((g.X - t.X) * (g.X - t.X) + (g.Y - t.Y) * (g.Y - t.Y))).Max();

    [Fact]
    public void Chroma_mode_reproduces_the_production_fit_exactly()
    {
        foreach ((double px, double py) in new[] { (0.0, 0.0), (0.31, 0.17), (0.5, 0.5), (0.77, 0.93), (0.12, 0.64) })
        {
            var plate = Plate(px, py);
            float[] dom = Coverage(plate).Select(v => v * 190f).ToArray();
            var seeds = Integer(plate);
            var prod = ChromaEdgeLineFitter.Fit(dom, W, H, 190, seeds[0], seeds[1], seeds[2], seeds[3]);
            var mine = EdgeFit.Fit(new FloatPlaneField(dom, W, H, clampNegative: true), null, W, H, 190, seeds[0], seeds[1], seeds[2], seeds[3], new EdgeFitOptions());
            prod.Should().NotBeNull();
            mine.Should().NotBeNull();
            for (int k = 0; k < 4; k++)
            {
                mine!.Value.Corners[k].X.Should().BeApproximately(prod!.Value.Corners[k].X, 1e-9);
                mine.Value.Corners[k].Y.Should().BeApproximately(prod.Value.Corners[k].Y, 1e-9);
            }

            mine!.Value.MaxBowFraction.Should().BeApproximately(prod!.Value.MaxBowFraction, 1e-9);
        }
    }

    [Fact]
    public void Luma_integral_crossing_does_not_depend_on_the_pixel_phase()
    {
        // A vertical edge at x0 between luma 60 (left, inside) and 130 (right), box-filtered to the pixel grid.
        var o = new EdgeFitOptions(Crossing.LumaIntegral, Band: 2.0, MinContrast: 16);
        double worst = 0;
        for (int i = 0; i < 20; i++)
        {
            double x0 = 50.0 + i / 20.0;
            var plane = new float[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    double covRight = Math.Clamp(x + 0.5 - x0, 0, 1);
                    plane[y * W + x] = (float)(60 + 70 * covRight);
                }
            }

            double? s = EdgeFit.LumaCrossing(new FloatPlaneField(plane, W, H), 50.0, 40.0, 1, 0, 0.3, o);
            s.Should().NotBeNull();
            worst = Math.Max(worst, Math.Abs(50.0 + s!.Value - x0));
        }

        worst.Should().BeLessThan(0.02);
    }

    [Fact]
    public void A_probe_without_luma_contrast_keeps_the_chroma_crossing()
    {
        var plane = new float[W * H];
        for (int i = 0; i < plane.Length; i++)
            plane[i] = 100 + (i % W > 60 ? 6 : 0); // a 6-level step: under the 16-level threshold
        new FloatPlaneField(plane, W, H).Sample(61, 3).Should().BeGreaterThan(99);
        EdgeFit.LumaCrossing(new FloatPlaneField(plane, W, H), 60.0, 40.0, 1, 0, 0.0, new EdgeFitOptions(Crossing.LumaIntegral)).Should().BeNull();

        // Whole-plate version: luma flat over the plate and the background, so every probe falls back and the fit equals the chroma fit.
        var plate = Plate(0.3, 0.2);
        float[] dom = Coverage(plate).Select(v => v * 190f).ToArray();
        float[] flat = Coverage(plate).Select(v => 100f + 5f * v).ToArray();
        var seeds = Integer(plate);
        var tel = new EdgeTelemetry();
        var chroma = EdgeFit.Fit(new FloatPlaneField(dom, W, H, clampNegative: true), null, W, H, 190, seeds[0], seeds[1], seeds[2], seeds[3], new EdgeFitOptions());
        var guided = EdgeFit.Fit(new FloatPlaneField(dom, W, H, clampNegative: true), new FloatPlaneField(flat, W, H), W, H, 190, seeds[0], seeds[1], seeds[2], seeds[3],
            new EdgeFitOptions(Crossing.LumaIntegral, MinContrast: 16), tel);
        tel.LumaProbes.Should().Be(0);
        tel.Probes.Should().BeGreaterThan(100);
        for (int k = 0; k < 4; k++)
        {
            guided!.Value.Corners[k].X.Should().BeApproximately(chroma!.Value.Corners[k].X, 1e-9);
            guided.Value.Corners[k].Y.Should().BeApproximately(chroma.Value.Corners[k].Y, 1e-9);
        }
    }

    [Fact]
    public void Luma_placed_corners_beat_a_blocky_chroma_signal()
    {
        // Chroma at half resolution, replicated: dominance is the 2x2-block mean of the plate coverage (a staircase),
        // luma stays at full grid resolution.
        double chromaErr = 0, lumaErr = 0;
        int n = 0;
        for (int i = 0; i < 12; i++)
        {
            var plate = Plate(i / 12.0, (i * 5 % 12) / 12.0);
            float[] cov = Coverage(plate);
            var blocky = new float[W * H];
            for (int y = 0; y < H; y += 2)
            {
                for (int x = 0; x < W; x += 2)
                {
                    float m = (cov[y * W + x] + cov[y * W + x + 1] + cov[(y + 1) * W + x] + cov[(y + 1) * W + x + 1]) / 4;
                    blocky[y * W + x] = blocky[y * W + x + 1] = blocky[(y + 1) * W + x] = blocky[(y + 1) * W + x + 1] = m * 190f;
                }
            }

            float[] luma = cov.Select(v => 60f + 70f * v).ToArray();
            var seeds = Integer(plate);
            var dom = new FloatPlaneField(blocky, W, H, clampNegative: true);
            var c = EdgeFit.Fit(dom, null, W, H, 190, seeds[0], seeds[1], seeds[2], seeds[3], new EdgeFitOptions());
            var l = EdgeFit.Fit(dom, new FloatPlaneField(luma, W, H), W, H, 190, seeds[0], seeds[1], seeds[2], seeds[3], new EdgeFitOptions(Crossing.LumaIntegral));
            c.Should().NotBeNull();
            l.Should().NotBeNull();
            chromaErr += MaxCornerError(c!.Value.Corners, plate);
            lumaErr += MaxCornerError(l!.Value.Corners, plate);
            n++;
        }

        lumaErr /= n;
        chromaErr /= n;
        lumaErr.Should().BeLessThan(0.25, "full-resolution luma places the edge to a fraction of a pixel");
        lumaErr.Should().BeLessThan(chromaErr * 0.7, $"chroma-only mean worst-corner error was {chromaErr:0.000}");
    }

    [Fact]
    public void Refine_on_a_finer_plane_keeps_a_good_seed_and_corrects_a_biased_one()
    {
        var plate = Plate(0.4, 0.3);
        float[] cov = Coverage(plate);
        float[] luma = cov.Select(v => 60f + 70f * v).ToArray();
        var field = new FloatPlaneField(luma, W, H);
        var good = EdgeFit.Refine(field, W, H, plate[0], plate[1], plate[2], plate[3], new EdgeFitOptions(Crossing.LumaIntegral));
        good.Should().NotBeNull();
        MaxCornerError(good!.Value.Corners, plate).Should().BeLessThan(0.2);

        var biased = plate.Select(p => (p.X + 0.8, p.Y - 0.6)).ToArray();
        var fixedUp = EdgeFit.Refine(field, W, H, biased[0], biased[1], biased[2], biased[3], new EdgeFitOptions(Crossing.LumaIntegral));
        fixedUp.Should().NotBeNull();
        MaxCornerError(fixedUp!.Value.Corners, plate).Should().BeLessThan(0.25);
    }

    [Fact]
    public void Decode_variants_fill_every_placeholder_and_start_from_the_production_chain()
    {
        EdgeDecode.Get(EdgeDecode.BaseId).FilterFor(24, 480, 274).Should().Be("fps=24,scale=480:274:flags=area,format=rgb24");
        foreach (DecodeVariant v in EdgeDecode.All)
        {
            string f = v.FilterFor(23.976, 480, 274);
            f.Should().NotContain("{").And.NotContain("}");
            f.Should().StartWith("fps=23.976,");
            f.Should().EndWith("format=rgb24");
        }

        EdgeDecode.All.Select(v => v.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Candidate_catalogues_have_unique_ids()
    {
        foreach (IReadOnlyList<Candidate> list in new[] { EdgeSignalBenchmark.E1Candidates(), EdgeSignalBenchmark.E2Candidates(), EdgeSignalBenchmark.E3Candidates(), EdgeSignalBenchmark.E4Candidates(), EdgeSignalBenchmark.ComboCandidates() })
            list.Select(c => c.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Guarded_refine_rejects_a_seed_that_is_too_far_and_keeps_a_near_one()
    {
        var plate = Plate(0.4, 0.3);
        float[] luma = Coverage(plate).Select(v => 60f + 70f * v).ToArray();
        var field = new FloatPlaneField(luma, W, H);
        var guarded = new EdgeFitOptions(Crossing.LumaHalf, Band: 2.0, MaxRefineShift: 1.5, RequireSpread: true);

        var near = plate.Select(p => (p.X + 0.9, p.Y - 0.7)).ToArray();
        var fixedUp = EdgeFit.Refine(field, W, H, near[0], near[1], near[2], near[3], guarded);
        fixedUp.Should().NotBeNull();
        MaxCornerError(fixedUp!.Value.Corners, plate).Should().BeLessThan(0.3);

        // A correction larger than the guard allows rejects the whole re-fit: the seed was 0.9 px off, the guard here is 0.5 px.
        var tight = new EdgeFitOptions(Crossing.LumaHalf, Band: 2.0, MaxRefineShift: 0.5, RequireSpread: true);
        EdgeFit.Refine(field, W, H, near[0], near[1], near[2], near[3], tight).Should().BeNull();
    }

    [Fact]
    public void Guarded_refine_keeps_the_seed_line_when_the_surviving_probes_do_not_span_the_edge()
    {
        var plate = Plate(0.2, 0.1);
        float[] luma = Coverage(plate).Select(v => 60f + 70f * v).ToArray();
        // Wipe the luma step along the right 2/3 of the picture: only the left third of the top and bottom edges survives.
        for (int y = 0; y < H; y++)
        {
            for (int x = 60; x < W; x++)
                luma[y * W + x] = 100f;
        }

        var field = new FloatPlaneField(luma, W, H);
        var tel = new EdgeTelemetry();
        var guarded = new EdgeFitOptions(Crossing.LumaHalf, MinContrast: 16, RequireSpread: true);
        var result = EdgeFit.Refine(field, W, H, plate[0], plate[1], plate[2], plate[3], guarded, tel);
        tel.EdgesWithoutLuma.Should().BeGreaterThan(0);
        if (result is { } r)
            MaxCornerError(r.Corners, plate).Should().BeLessThan(0.5, "an edge without spread keeps the seed line, never a tilted one");
    }
}
