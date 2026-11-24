#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Tests.InsertJitter.RenderPath;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.InsertJitter.Promo;

/// <summary>Fast tests (no ffmpeg, no inputs) for the promo measurement tooling (T-007).</summary>
public class PromoTests
{
    private static Quad Skewed(double t = 0) => Quad.From(t, (210, 110), (1420, 118), (200, 700), (1430, 690));

    [Fact]
    public void Homography_maps_the_unit_square_corners_onto_the_quad_and_inverts()
    {
        Quad q = Skewed();
        Homography h = Homography.FromUnitSquare(q);
        (double X, double Y) p = h.Apply(1, 0);
        p.X.Should().BeApproximately(q.X1, 1e-9);
        p.Y.Should().BeApproximately(q.Y1, 1e-9);
        (double X, double Y) c = h.Apply(0.5, 0.5);
        (double u, double v) = h.Inverse().Apply(c.X, c.Y);
        u.Should().BeApproximately(0.5, 1e-9);
        v.Should().BeApproximately(0.5, 1e-9);
        // Same map as the bench's closed form.
        (double X, double Y) ref1 = q.ToMap().Map(0.3, 0.8);
        (double X, double Y) mine = h.Apply(0.3, 0.8);
        mine.X.Should().BeApproximately(ref1.X, 1e-7);
        mine.Y.Should().BeApproximately(ref1.Y, 1e-7);
    }

    [Fact]
    public void Edge_fit_recovers_an_anti_aliased_quad_to_a_few_hundredths_of_a_pixel()
    {
        const int w = 1920, h = 1080;
        Quad truth = Skewed();
        var cov = new float[w * h];
        EdgeMeter.Coverage(cov, w, h, truth);
        var field = cov.Select(c => 14f + 55f * c).ToArray();
        // Seed several pixels off in different directions.
        Quad seed = truth with { X0 = truth.X0 + 2.5, Y0 = truth.Y0 - 3, X3 = truth.X3 - 2, Y3 = truth.Y3 + 3.5 };
        PromoQuadFit fit = PromoEdgeFit.FitQuad(field, w, h, seed, PromoMeasure.ScreenOptions);
        fit.Quad.Should().NotBeNull();
        Quad q = fit.Quad!.Value;
        for (int c = 0; c < 4; c++)
        {
            q.Corner(c).X.Should().BeApproximately(truth.Corner(c).X, 0.1);
            q.Corner(c).Y.Should().BeApproximately(truth.Corner(c).Y, 0.1);
        }
    }

    [Fact]
    public void Edge_fit_refuses_an_edge_that_leaves_the_frame_and_one_without_contrast()
    {
        const int w = 1920, h = 1080;
        Quad truth = Quad.From(0, (100, -4), (1500, -4), (100, 700), (1500, 700));
        var cov = new float[w * h];
        EdgeMeter.Coverage(cov, w, h, truth);
        var field = cov.Select(c => 14f + 55f * c).ToArray();
        PromoQuadFit fit = PromoEdgeFit.FitQuad(field, w, h, truth, PromoMeasure.ScreenOptions);
        fit.Quad.Should().BeNull();
        fit.Edges[0].Valid.Should().BeFalse();
        fit.Edges[0].Reason.Should().Contain("outside");

        var flat = new float[w * h];
        Array.Fill(flat, 40f);
        PromoQuadFit none = PromoEdgeFit.FitQuad(flat, w, h, Skewed(), PromoMeasure.ScreenOptions);
        none.Edges.Should().OnlyContain(e => !e.Valid);
    }

    [Fact]
    public void Edge_fit_accepts_a_bowed_edge_and_reports_the_bow()
    {
        const int w = 1920, h = 1080;
        var field = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Top edge bowed by 3 px at the middle (parabola), screen below it.
                double top = 300 - 3 * (1 - Math.Pow((x - 960) / 700.0, 2));
                double cover = Math.Clamp(y + 0.5 - top + 0.5, 0, 1);
                field[y * w + x] = (float)(14 + 55 * cover);
            }
        }

        PromoEdge e = PromoEdgeFit.FitEdge(field, w, h, (260, 301), (1660, 301), (960, 700), PromoMeasure.ScreenOptions);
        e.Valid.Should().BeTrue(e.Reason);
        e.Bow.Should().BeApproximately(3.0, 0.5);
    }

    [Fact]
    public void Scalar_rows_follow_the_JitterMetrics_definitions()
    {
        // A pure alternation +-0.5 px has slip 1.0 px at every step, a constant 2 px offset must not count as slip.
        var series = new List<double[]?>();
        for (int i = 0; i < 60; i++)
        {
            double v = 2 + (i % 2 == 0 ? 0.5 : -0.5);
            series.Add([v, v, v, v]);
        }

        PromoScore.Row r = PromoScore.Scalar("alt", series);
        r.Stats!.SlipRms.Should().BeApproximately(1.0, 1e-9);
        r.OffsetMean.Should().BeApproximately(2.0, 1e-9);
        r.Stats.ErrMax.Should().BeApproximately(0.5, 1e-9);
        r.SlipMax.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Welch_puts_a_sinusoid_and_the_two_frame_staircase_in_their_bands()
    {
        const int n = 256;
        var slow = new double[]?[n];
        var stair = new double[]?[n];
        for (int i = 0; i < n; i++)
        {
            double a = 0.4 * Math.Sin(2 * Math.PI * i / 16.0);
            slow[i] = [a, a, a, a];
            double b = (i % 2 == 0 ? 0.4 : -0.4);
            stair[i] = [b, b, b, b];
        }

        PromoScore.Spectrum? sp = PromoScore.Welch([slow]);
        sp.Should().NotBeNull();
        int peak = Enumerable.Range(1, 32).OrderByDescending(k => sp!.Power[k]).First();
        sp!.PeriodOf(peak).Should().BeApproximately(16.0, 0.01);
        double total = PromoScore.Bands.Sum(b => PromoScore.BandPower(sp, b));
        (PromoScore.BandPower(sp, PromoScore.Bands[2]) / total).Should().BeGreaterThan(0.75);
        PromoScore.Spectrum? st = PromoScore.Welch([stair]);
        int top = Enumerable.Range(1, 32).OrderByDescending(k => st!.Power[k]).First();
        st!.PeriodOf(top).Should().BeApproximately(2.0, 0.01);
        (PromoScore.BandPower(st, PromoScore.Bands[6]) / PromoScore.Bands.Sum(b => PromoScore.BandPower(st, b))).Should().BeGreaterThan(0.5);
    }

    [Fact]
    public void Gap_and_measured_inside_track_have_the_expected_sign_and_size()
    {
        Quad track = Quad.From(0, (100, 100), (900, 100), (100, 500), (900, 500));
        // A measured top edge 2 px below the track's top edge (further inside).
        var measured = new PromoEdge(true, "ok", (100, 102), (900, 102), 0.1, 1, 50, 2, 100);
        PromoScore.MeasuredInsideTrack(track, 0, measured).Should().OnlyContain(v => Math.Abs(v - 2.0) < 1e-9);
        // A card edge 30 px below the screen edge.
        var screen = new PromoEdge(true, "ok", (100, 100), (900, 100), 0.1, 1, 50, 0, 100);
        PromoScore.GapInside(PromoScore.Points(Quad.From(0, (130, 130), (870, 130), (130, 470), (870, 470)), 0), screen, track, 0)
            .Should().OnlyContain(v => Math.Abs(v - 30.0) < 1e-9);
    }

    [Fact]
    public void Card_prediction_through_the_plate_quad_keeps_the_content_fractions()
    {
        Quad plate = Skewed();
        var card = new CardRect(0.05, 0.05, 0.95, 0.95, 0, 0, 1);
        Quad pred = PromoMeasure.PredictCard(plate, card);
        Homography inv = Homography.FromUnitSquare(plate).Inverse();
        (double u, double v) = inv.Apply(pred.X3, pred.Y3);
        u.Should().BeApproximately(0.95, 1e-9);
        v.Should().BeApproximately(0.95, 1e-9);
    }

    [Fact]
    public void Detrend_and_runs_helpers_behave()
    {
        double[] ramp = Enumerable.Range(0, 50).Select(i => 3 + 0.2 * i + 0.01 * i * i).ToArray();
        PromoSeries.DetrendPoly(ramp, 2).Should().OnlyContain(v => Math.Abs(v) < 1e-6);
        PromoSeries.Runs([true, true, false, true, false, false, true]).Should().Equal([(0, 2), (3, 4), (6, 7)]);
    }
}
