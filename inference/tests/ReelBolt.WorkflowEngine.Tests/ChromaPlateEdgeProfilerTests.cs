using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for <see cref="ChromaPlateEdgeProfiler"/> — the measurement half of non-planar tracked
/// compositing (docs/video-editing.md "Non-planar surfaces and multi-plate compositing"). Every
/// fixture is a synthetic in-memory mask rasterized from an ANALYTICALLY known surface, so the
/// measured bow can be checked against the bow that was actually drawn.
/// </summary>
public class ChromaPlateEdgeProfilerTests
{
    private const int W = 320;
    private const int H = 180;

    /// <summary>A rectangle bent about a vertical (or horizontal) axis on a circular cylinder, perspective projected. u,v run -1..1.</summary>
    private static Func<double, double, (double X, double Y)> Cylinder(
        double cx = 160, double cy = 90, double a = 90, double b = 60,
        double theta = 0.6, double focal = 420, double sign = 1.0, bool horizontalAxis = false)
    {
        double r = (horizontalAxis ? b : a) / Math.Sin(theta);
        return (u, v) =>
        {
            double phi = (horizontalAxis ? v : u) * theta;
            double bent = r * Math.Sin(phi);
            double z3 = sign * r * (Math.Cos(phi) - Math.Cos(theta));
            double s = focal / (focal + z3);
            double x3 = horizontalAxis ? u * a : bent;
            double y3 = horizontalAxis ? bent : v * b;
            return (cx + x3 * s, cy + y3 * s);
        };
    }

    /// <summary>Forward-rasterizes a surface into a boolean mask, densely enough to leave no interior holes.</summary>
    private static bool[] Rasterize(Func<double, double, (double X, double Y)> surface, int samples = 1400)
    {
        var mask = new bool[W * H];
        for (int i = 0; i <= samples; i++)
        {
            for (int j = 0; j <= samples; j++)
            {
                (double x, double y) = surface(-1 + 2.0 * j / samples, -1 + 2.0 * i / samples);
                int px = (int)Math.Round(x), py = (int)Math.Round(y);
                if (px >= 0 && py >= 0 && px < W && py < H)
                    mask[py * W + px] = true;
            }
        }

        return mask;
    }

    /// <summary>The four extreme-point corners of a mask, exactly as <c>ChromaQuadTracker</c> fits them.</summary>
    private static ((double X, double Y) Tl, (double X, double Y) Tr, (double X, double Y) Bl, (double X, double Y) Br) Corners(bool[] mask)
    {
        (double x, double y) tl = default, tr = default, bl = default, br = default;
        int minSum = int.MaxValue, maxSum = int.MinValue, minDiff = int.MaxValue, maxDiff = int.MinValue;
        for (int p = 0; p < mask.Length; p++)
        {
            if (!mask[p])
                continue;

            int x = p % W, y = p / W, sum = x + y, diff = x - y;
            if (sum < minSum) { minSum = sum; tl = (x, y); }
            if (sum > maxSum) { maxSum = sum; br = (x, y); }
            if (diff > maxDiff) { maxDiff = diff; tr = (x, y); }
            if (diff < minDiff) { minDiff = diff; bl = (x, y); }
        }

        return (tl, tr, bl, br);
    }

    private static InsertPlateBows MeasureOf(Func<double, double, (double X, double Y)> surface)
    {
        bool[] mask = Rasterize(surface);
        var (tl, tr, bl, br) = Corners(mask);
        return ChromaPlateEdgeProfiler.Measure(mask, W, H, tl, tr, bl, br).Bows;
    }

    [Fact]
    public void A_flat_axis_aligned_plate_measures_as_flat()
    {
        var mask = new bool[W * H];
        for (int y = 40; y < 140; y++)
        {
            for (int x = 90; x < 230; x++)
                mask[y * W + x] = true;
        }

        InsertPlateBows bows = ChromaPlateEdgeProfiler.Measure(mask, W, H, (90, 40), (229, 40), (90, 139), (229, 139)).Bows;

        bows.MaxMagnitude.Should().BeLessThan(0.002,
            "a straight-edged rectangle has no bow to find — and must stay well clear of the 0.01 " +
            "default at which a plate is treated as curved, or every flat plate would silently mesh");
    }

    [Fact]
    public void A_flat_but_ROTATED_plate_measures_as_flat()
    {
        // The case real footage actually supplies (the laptop clip's plate sits at ~17 degrees):
        // a rotated flat quad must not read as curved just because it is not axis-aligned.
        Func<double, double, (double X, double Y)> rotated = (u, v) =>
        {
            const double angle = 0.30;
            double x = u * 95, y = v * 55;
            return (160 + x * Math.Cos(angle) - y * Math.Sin(angle), 90 + x * Math.Sin(angle) + y * Math.Cos(angle));
        };

        MeasureOf(rotated).MaxMagnitude.Should().BeLessThan(0.01,
            "the extreme-point corners of a rotated rectangle are its true corners, so the edges probe straight");
    }

    [Fact]
    public void A_vertical_axis_bend_is_measured_on_the_top_and_bottom_edges_only()
    {
        InsertPlateBows bows = MeasureOf(Cylinder(theta: 0.6));

        bows.Top.Bow.Should().BeGreaterThan(0.03, "the top edge of a plate bending away bows inward");
        bows.Bottom.Bow.Should().BeGreaterThan(0.03);
        Math.Abs(bows.Left.Bow).Should().BeLessThan(0.006, "the side edges of a vertical-axis bend stay straight");
        Math.Abs(bows.Right.Bow).Should().BeLessThan(0.006);
    }

    [Fact]
    public void A_horizontal_axis_bend_is_measured_on_the_side_edges_only()
    {
        InsertPlateBows bows = MeasureOf(Cylinder(theta: 0.6, horizontalAxis: true));

        // The threshold here is lower than the vertical-axis case above on purpose, and the
        // difference is the unit rather than the accuracy: a bow is a fraction of its own edge's
        // ACROSS-vector, and this fixture's side edges look across the plate's 180px width while
        // their sagitta is set by its 120px height. The same physical bend therefore reads as a
        // smaller fraction. Pixel-accuracy is asserted separately, in the sagitta test.
        bows.Left.Bow.Should().BeGreaterThan(0.018);
        bows.Right.Bow.Should().BeGreaterThan(0.018);
        Math.Abs(bows.Top.Bow).Should().BeLessThan(0.006, "a horizontal-axis bend leaves the top and bottom edges straight");
        Math.Abs(bows.Bottom.Bow).Should().BeLessThan(0.006);
    }

    [Fact]
    public void The_measured_bow_reproduces_the_drawn_sagitta_in_pixels()
    {
        // The number that matters: how far the true boundary's midpoint sits from the straight
        // chord. The profiler stores a fraction of the across-vector, so convert back and compare.
        Func<double, double, (double X, double Y)> surface = Cylinder(theta: 0.6);
        InsertPlateBows bows = MeasureOf(surface);

        (double tlx, double tly) = surface(-1, -1);
        (double trx, double @try) = surface(1, -1);
        (double midX, double midY) = surface(0, -1);
        double chordMidX = (tlx + trx) / 2, chordMidY = (tly + @try) / 2;
        double trueSagittaPx = Math.Sqrt(Math.Pow(midX - chordMidX, 2) + Math.Pow(midY - chordMidY, 2));

        // The across-vector at the top edge's midpoint spans the plate's height.
        (double botMidX, double botMidY) = surface(0, 1);
        double acrossPx = Math.Sqrt(Math.Pow(botMidX - midX, 2) + Math.Pow(botMidY - midY, 2));
        double measuredSagittaPx = bows.Top.Bow * acrossPx;

        trueSagittaPx.Should().BeGreaterThan(3, "the fixture must actually be curved for this to mean anything");
        measuredSagittaPx.Should().BeApproximately(trueSagittaPx, 1.5,
            "the fitted bow should recover the drawn sagitta to within the grid's own quantization");
    }

    [Fact]
    public void A_notch_in_one_edge_is_rejected_as_an_outlier_rather_than_bent_into_the_surface()
    {
        // The phone clip's plate has a camera notch cut out of its top edge. A free-form boundary
        // would follow it and the Coons blend would ripple it through the interior; the
        // two-parameter fit is structurally the wrong shape to express it, and the trimming pass
        // drops it.
        var mask = new bool[W * H];
        for (int y = 40; y < 140; y++)
        {
            for (int x = 90; x < 230; x++)
                mask[y * W + x] = true;
        }

        for (int y = 40; y < 52; y++)
        {
            for (int x = 150; x < 170; x++)
                mask[y * W + x] = false;
        }

        InsertPlateBows bows = ChromaPlateEdgeProfiler.Measure(mask, W, H, (90, 40), (229, 40), (90, 139), (229, 139)).Bows;

        bows.Top.Bow.Should().BeLessThan(0.02, "a 20px notch in a 140px edge must not read as a 12px bow");
    }

    [Fact]
    public void An_empty_or_undersized_mask_measures_flat_instead_of_throwing()
    {
        ChromaPlateEdgeProfiler.Measure(new bool[W * H], W, H, (10, 10), (100, 10), (10, 100), (100, 100))
            .Bows.Should().Be(InsertPlateBows.Flat);
        ChromaPlateEdgeProfiler.Measure(new bool[4], W, H, (10, 10), (100, 10), (10, 100), (100, 100))
            .Bows.Should().Be(InsertPlateBows.Flat);
        ChromaPlateEdgeProfiler.Measure(new bool[W * H], W, H, (0, 0), (1, 1), (2, 2), (3, 3))
            .Bows.Should().Be(InsertPlateBows.Flat);
    }

    [Fact]
    public void LeastSquares_recovers_the_coefficients_it_was_given()
    {
        var samples = new List<(double T, double Dev)>();
        for (int i = 1; i <= 20; i++)
        {
            double t = i / 21.0;
            samples.Add((t, 4 * t * (1 - t) * (0.07 + -0.03 * (2 * t - 1))));
        }

        InsertEdgeBow fit = ChromaPlateEdgeProfiler.LeastSquares(samples);

        fit.Bow.Should().BeApproximately(0.07, 1e-9);
        fit.Skew.Should().BeApproximately(-0.03, 1e-9);
    }

    [Fact]
    public void A_rounded_corner_rectangle_measures_FLAT_and_has_its_corners_pushed_outward()
    {
        // The real-footage regression this whole two-pass design exists for. Every phone and
        // laptop screen has rounded corners; the tracker's extreme-point fit lands on the
        // rounding's tangent point, inset from where the straight edges meet. Measured against
        // those inset corners, a perfectly flat plate looks bowed on all four edges — the
        // project's real phone clip read 0.027 against a 0.01 "this is curved" threshold.
        const int x0 = 80, y0 = 30, x1 = 240, y1 = 150, radius = 22;
        var mask = new bool[W * H];
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double dx = Math.Max(0, Math.Max(x0 + radius - x, x - (x1 - radius)));
                double dy = Math.Max(0, Math.Max(y0 + radius - y, y - (y1 - radius)));
                if (dx * dx + dy * dy <= radius * (double)radius)
                    mask[y * W + x] = true;
            }
        }

        var (tl, tr, bl, br) = Corners(mask);
        // The extreme-point fit really is inset: it lands on the rounding, not the true corner.
        tl.X.Should().BeGreaterThan(x0 + 3, "min(x+y) sits on the corner arc, not at the rectangle's corner");
        tl.Y.Should().BeGreaterThan(y0 + 3);

        PlateSurface surface = ChromaPlateEdgeProfiler.Measure(mask, W, H, tl, tr, bl, br);

        surface.Bows.MaxMagnitude.Should().BeLessThan(0.01,
            "corner rounding must be absorbed by the corner refinement, not reported as curvature");
        surface.Tl.X.Should().BeLessThan(tl.X, "the refined top-left corner moves back out toward the true corner");
        surface.Tl.Y.Should().BeLessThan(tl.Y);
        surface.Br.X.Should().BeGreaterThan(br.X);
    }

    [Fact]
    public void A_plate_whose_corner_fit_FAILED_reports_flat_rather_than_a_huge_bogus_bow()
    {
        // The project's real laptop clip: a strong trapezoid where min(x+y) lands partway down
        // the left edge instead of at the top-left corner, so the fitted quad cuts the whole
        // upper-left triangle off the plate. The silhouette genuinely sits far outside that
        // quad's top edge, and the first version of this profiler dutifully reported a 0.118
        // bow — which made the composite WORSE than the flat pin. The fixture below reproduces
        // the geometry by handing Measure a quad that is deliberately wrong in the same way.
        var mask = new bool[W * H];
        for (int y = 30; y <= 150; y++)
        {
            for (int x = 60; x <= 260; x++)
                mask[y * W + x] = true;
        }

        // "Top-left" placed well down the left edge, as the extreme-point fit does on a strong
        // trapezoid: 20px of a 120px-tall plate, the same ~0.15 of the across-vector the real
        // laptop clip produces.
        PlateSurface surface = ChromaPlateEdgeProfiler.MeasureWithDiagnostics(
            mask, W, H, (60, 50), (260, 30), (60, 150), (260, 150), out _, out double cornerCorrection);

        cornerCorrection.Should().BeGreaterThan(0.10,
            "the fixture must actually trip the corner-validity gate for this test to mean anything");
        surface.Bows.MaxMagnitude.Should().Be(0,
            "a corner correction far beyond what rounding could explain invalidates the whole measurement");
        surface.Tl.Should().Be((60, 50), "a rejected plate keeps the corners it came in with");
    }

    [Fact]
    public void A_bend_the_two_parameter_profile_cannot_describe_is_rejected_rather_than_approximated()
    {
        // A DOUBLE-humped edge: zero at both ends and in the middle, bulging inward at the
        // quarter points. Neither a symmetric bow nor a leaning one can describe it, and — unlike
        // the misplaced-corner case — its endpoints extrapolate to roughly zero, so the corner
        // gate has nothing to catch. The fit residual is the only thing standing between this
        // boundary and a confidently wrong curvature claim.
        var mask = new bool[W * H];
        for (int x = 70; x <= 250; x++)
        {
            double t = (x - 70) / 180.0;
            int inset = (int)Math.Round(16 * Math.Pow(Math.Sin(2 * Math.PI * t), 2));
            for (int y = 40 + inset; y <= 150; y++)
            {
                if (y >= 0 && y < H)
                    mask[y * W + x] = true;
            }
        }

        PlateSurface surface = ChromaPlateEdgeProfiler.MeasureWithDiagnostics(
            mask, W, H, (70, 40), (250, 40), (70, 150), (250, 150),
            out var residuals, out double cornerCorrection);

        surface.Bows.Top.Should().Be(InsertEdgeBow.Flat, "an unexplained boundary yields no curvature claim");

        // Two independent guards can reach that verdict — the residual says the profile does not
        // describe this boundary, the corner limit says the quad it was measured against cannot
        // be right. Which one fires is an implementation detail; that ONE of them did is not.
        (residuals.Top > 0.012 || cornerCorrection > 0.10).Should().BeTrue(
            $"the rejection must be explained by a diagnostic (residual {residuals.Top:F4}, corner {cornerCorrection:F4})");
    }

    [Fact]
    public void A_convex_bulge_is_measured_with_the_opposite_sign_to_a_concave_bend()
    {
        double concave = MeasureOf(Cylinder(theta: 0.6)).Top.Bow;
        double convex = MeasureOf(Cylinder(theta: 0.6, sign: -1.0)).Top.Bow;

        concave.Should().BeGreaterThan(0, "a plate curving away bows its top edge inward");
        convex.Should().BeLessThan(0, "a plate bulging toward the camera bows it outward");
    }
}
