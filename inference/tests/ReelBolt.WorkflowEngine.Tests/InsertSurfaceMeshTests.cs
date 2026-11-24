using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for <see cref="InsertSurfaceMesh"/> — the geometry behind non-planar tracked compositing
/// (docs/video-editing.md "Non-planar surfaces and multi-plate compositing"). Every fixture is an
/// ANALYTICALLY known surface (a rectangle bent about an axis on a circular cylinder, perspective
/// projected), so the mesh can be scored against ground truth it can be held to exactly rather
/// than against itself.
/// </summary>
public class InsertSurfaceMeshTests
{
    /// <summary>
    /// The ground-truth surface every curvature test measures against: a rectangle of half-extent
    /// (a, b) bent about a vertical axis through <paramref name="theta"/> radians of arc, then
    /// perspective projected at focal length <paramref name="focal"/>. u,v run -1..1.
    /// </summary>
    private static Func<double, double, (double X, double Y)> Cylinder(
        double cx = 640, double cy = 360, double a = 300, double b = 230,
        double theta = 0.6, double focal = 1400, double sign = 1.0)
    {
        double r = a / Math.Sin(theta);
        return (u, v) =>
        {
            double phi = u * theta;
            double x3 = r * Math.Sin(phi);
            double z3 = sign * r * (Math.Cos(phi) - Math.Cos(theta));
            double s = focal / (focal + z3);
            return (cx + x3 * s, cy + v * b * s);
        };
    }

    /// <summary>Perpendicular deviation of the true boundary from the projective edge, fitted to the 4t(1-t) sagitta basis — i.e. what a perfect profiler would measure.</summary>
    private static InsertPlateBows MeasureExactBows(Func<double, double, (double X, double Y)> surface)
    {
        InsertSurfaceMesh.UnitSquareHomography h = Homography(surface);

        return new InsertPlateBows(Fit("top"), Fit("right"), Fit("bottom"), Fit("left"));

        InsertEdgeBow Fit(string edge)
        {
            double num = 0, den = 0;
            for (int i = 1; i <= 40; i++)
            {
                double t = i / 41.0;
                (double X, double Y) truth, near, far;
                switch (edge)
                {
                    case "top": truth = surface(-1 + 2 * t, -1); near = h.Map(t, 0); far = h.Map(t, 1); break;
                    case "bottom": truth = surface(-1 + 2 * t, 1); near = h.Map(t, 1); far = h.Map(t, 0); break;
                    case "left": truth = surface(-1, -1 + 2 * t); near = h.Map(0, t); far = h.Map(1, t); break;
                    default: truth = surface(1, -1 + 2 * t); near = h.Map(1, t); far = h.Map(0, t); break;
                }

                double ax = far.X - near.X, ay = far.Y - near.Y;
                double lenSq = ax * ax + ay * ay;
                double dev = ((truth.X - near.X) * ax + (truth.Y - near.Y) * ay) / lenSq;
                double basis = 4 * t * (1 - t);
                num += basis * dev;
                den += basis * basis;
            }

            return new InsertEdgeBow(den > 0 ? num / den : 0, 0);
        }
    }

    private static InsertSurfaceMesh.UnitSquareHomography Homography(Func<double, double, (double X, double Y)> surface) =>
        InsertSurfaceMesh.UnitSquareHomography.Solve(
            surface(-1, -1), surface(1, -1), surface(-1, 1), surface(1, 1))!;

    [Fact]
    public void Homography_maps_the_unit_square_corners_onto_the_given_quad()
    {
        InsertSurfaceMesh.UnitSquareHomography h = InsertSurfaceMesh.UnitSquareHomography.Solve(
            (100, 50), (500, 80), (120, 400), (540, 380))!;

        h.Map(0, 0).X.Should().BeApproximately(100, 1e-9);
        h.Map(0, 0).Y.Should().BeApproximately(50, 1e-9);
        h.Map(1, 0).X.Should().BeApproximately(500, 1e-9);
        h.Map(0, 1).Y.Should().BeApproximately(400, 1e-9);
        h.Map(1, 1).X.Should().BeApproximately(540, 1e-9);
        h.Map(1, 1).Y.Should().BeApproximately(380, 1e-9);
    }

    [Fact]
    public void Degenerate_quad_returns_null_rather_than_throwing()
    {
        // All four corners collinear — no projective map exists.
        InsertSurfaceMesh.UnitSquareHomography.Solve((0, 0), (10, 10), (20, 20), (30, 30)).Should().BeNull();
        InsertSurfaceMesh.BuildControlPoints((0, 0), (10, 10), (20, 20), (30, 30), InsertPlateBows.Flat, 3, 3)
            .Should().BeNull();
    }

    [Fact]
    public void Flat_bows_put_every_control_point_exactly_on_the_corner_homography()
    {
        // THE load-bearing invariant: a mesh over a flat plate is the same warp as one big
        // perspective, at any density. If this drifts, a flat plate's output silently changes.
        (double X, double Y) tl = (100, 50), tr = (500, 80), bl = (120, 400), br = (540, 380);
        InsertSurfaceMesh.UnitSquareHomography h = InsertSurfaceMesh.UnitSquareHomography.Solve(tl, tr, bl, br)!;

        foreach ((int rows, int cols) in new[] { (1, 1), (2, 3), (5, 7) })
        {
            (double X, double Y)[][] grid = InsertSurfaceMesh.BuildControlPoints(tl, tr, bl, br, InsertPlateBows.Flat, rows, cols)!;

            grid.Should().HaveCount(rows + 1);
            for (int i = 0; i <= rows; i++)
            {
                grid[i].Should().HaveCount(cols + 1);
                for (int j = 0; j <= cols; j++)
                {
                    (double x, double y) = h.Map(j / (double)cols, i / (double)rows);
                    grid[i][j].X.Should().BeApproximately(x, 1e-9);
                    grid[i][j].Y.Should().BeApproximately(y, 1e-9);
                }
            }
        }
    }

    [Fact]
    public void Mesh_reconstructs_a_known_cylindrical_surface_far_better_than_a_flat_quad()
    {
        Func<double, double, (double X, double Y)> surface = Cylinder();
        InsertSurfaceMesh.UnitSquareHomography h = Homography(surface);
        InsertPlateBows bows = MeasureExactBows(surface);

        double worstFlat = 0, worstMesh = 0;
        for (int i = 0; i <= 24; i++)
        {
            for (int j = 0; j <= 24; j++)
            {
                double u = j / 24.0, v = i / 24.0;
                (double tx, double ty) = surface(-1 + 2 * u, -1 + 2 * v);
                (double fx, double fy) = h.Map(u, v);
                (double mx, double my) = InsertSurfaceMesh.MapPoint(h, bows, u, v);
                worstFlat = Math.Max(worstFlat, Math.Sqrt(Math.Pow(tx - fx, 2) + Math.Pow(ty - fy, 2)));
                worstMesh = Math.Max(worstMesh, Math.Sqrt(Math.Pow(tx - mx, 2) + Math.Pow(ty - my, 2)));
            }
        }

        worstFlat.Should().BeGreaterThan(13, "a flat quad misses this bend by roughly its sagitta");
        worstMesh.Should().BeLessThan(1.0, "the Coons mesh built from the measured boundary tracks the true surface to sub-pixel");
    }

    [Fact]
    public void Mesh_interpolates_the_measured_boundary_exactly_on_all_four_edges()
    {
        // The property the whole feature rests on: whatever the interior does, the composited
        // content covers exactly the silhouette that was measured. That is what kills green fringe.
        Func<double, double, (double X, double Y)> surface = Cylinder(theta: 0.9, focal: 700);
        InsertSurfaceMesh.UnitSquareHomography h = Homography(surface);
        InsertPlateBows bows = MeasureExactBows(surface);

        for (int i = 0; i <= 20; i++)
        {
            double t = i / 20.0;
            foreach ((double u, double v, string edge) in new[] { (t, 0.0, "top"), (t, 1.0, "bottom"), (0.0, t, "left"), (1.0, t, "right") })
            {
                (double mx, double my) = InsertSurfaceMesh.MapPoint(h, bows, u, v);
                (double nx, double ny) = h.Map(u, v);
                double expected = edge switch
                {
                    "top" => bows.Top.Evaluate(t),
                    "bottom" => bows.Bottom.Evaluate(t),
                    "left" => bows.Left.Evaluate(t),
                    _ => bows.Right.Evaluate(t)
                };

                (double fx, double fy) = edge switch
                {
                    "top" => (h.Map(t, 1).X - h.Map(t, 0).X, h.Map(t, 1).Y - h.Map(t, 0).Y),
                    "bottom" => (h.Map(t, 0).X - h.Map(t, 1).X, h.Map(t, 0).Y - h.Map(t, 1).Y),
                    "left" => (h.Map(1, t).X - h.Map(0, t).X, h.Map(1, t).Y - h.Map(0, t).Y),
                    _ => (h.Map(0, t).X - h.Map(1, t).X, h.Map(0, t).Y - h.Map(1, t).Y)
                };

                mx.Should().BeApproximately(nx + expected * fx, 1e-9, $"{edge} edge at t={t}");
                my.Should().BeApproximately(ny + expected * fy, 1e-9, $"{edge} edge at t={t}");
            }
        }
    }

    [Fact]
    public void Corners_are_pinned_no_matter_how_strong_the_bows()
    {
        (double X, double Y) tl = (100, 50), tr = (500, 80), bl = (120, 400), br = (540, 380);
        var wild = new InsertPlateBows(
            new InsertEdgeBow(0.2, -0.15), new InsertEdgeBow(-0.18, 0.1),
            new InsertEdgeBow(0.12, 0.2), new InsertEdgeBow(-0.2, -0.05));

        (double X, double Y)[][] grid = InsertSurfaceMesh.BuildControlPoints(tl, tr, bl, br, wild, 4, 6)!;

        grid[0][0].Should().Be(tl);
        grid[0][6].X.Should().BeApproximately(tr.X, 1e-9);
        grid[4][0].Y.Should().BeApproximately(bl.Y, 1e-9);
        grid[4][6].X.Should().BeApproximately(br.X, 1e-9);
    }

    [Theory]
    // A bow of peak s over n cells leaves a chord error of s/n^2, so n = ceil(sqrt(s/tol)).
    [InlineData(0.0, 0.0, 1, 1)]           // flat -> one cell, i.e. the planar path
    [InlineData(0.03, 0.0, 1, 4)]          // 0.03 * 460px = 13.8px of bow across the width -> 5 columns
    [InlineData(0.0, 0.03, 4, 1)]          // the same bow on the side edges -> rows instead
    [InlineData(0.03, 0.03, 4, 4)]         // both -> a 2D mesh
    public void Density_follows_the_measured_bow_on_the_axis_that_resolves_it(
        double horizontalBow, double verticalBow, int minRows, int minCols)
    {
        var bows = new InsertPlateBows(
            new InsertEdgeBow(horizontalBow, 0), new InsertEdgeBow(verticalBow, 0),
            new InsertEdgeBow(horizontalBow, 0), new InsertEdgeBow(verticalBow, 0));

        (int rows, int cols) = InsertSurfaceMesh.ChooseDensity(bows, acrossWidthPx: 600, acrossHeightPx: 460, tolerancePx: 0.6, maxCells: 64);

        rows.Should().BeGreaterThanOrEqualTo(minRows);
        cols.Should().BeGreaterThanOrEqualTo(minCols);
        if (horizontalBow == 0)
            cols.Should().Be(1, "no top/bottom bow means columns resolve nothing");
        if (verticalBow == 0)
            rows.Should().Be(1, "no left/right bow means rows resolve nothing");
    }

    [Fact]
    public void Density_respects_the_cell_ceiling()
    {
        var bows = new InsertPlateBows(
            new InsertEdgeBow(0.2, 0), new InsertEdgeBow(0.2, 0),
            new InsertEdgeBow(0.2, 0), new InsertEdgeBow(0.2, 0));

        (int rows, int cols) = InsertSurfaceMesh.ChooseDensity(bows, 1920, 1080, tolerancePx: 0.1, maxCells: 12);

        (rows * cols).Should().BeLessThanOrEqualTo(12);
        rows.Should().BeGreaterThan(1);
        cols.Should().BeGreaterThan(1);
    }

    [Fact]
    public void A_denser_mesh_of_the_same_surface_converges_on_it()
    {
        // Each cell's straight edges chord the true curve, so the worst error must fall roughly
        // as 1/n^2 — the relationship ChooseDensity's formula is derived from.
        Func<double, double, (double X, double Y)> surface = Cylinder(theta: 0.9, focal: 700);
        InsertSurfaceMesh.UnitSquareHomography h = Homography(surface);
        InsertPlateBows bows = MeasureExactBows(surface);

        double ErrorAt(int cols)
        {
            (double X, double Y)[][] grid = InsertSurfaceMesh.BuildControlPoints(
                surface(-1, -1), surface(1, -1), surface(-1, 1), surface(1, 1), bows, 1, cols)!;

            // Worst gap between the straight top-edge chords of the mesh and the modelled curve.
            double worst = 0;
            for (int j = 0; j < cols; j++)
            {
                (double ax, double ay) = grid[0][j];
                (double bx, double by) = grid[0][j + 1];
                for (int k = 1; k < 8; k++)
                {
                    double localT = k / 8.0;
                    double u = (j + localT) / cols;
                    (double cx, double cy) = InsertSurfaceMesh.MapPoint(h, bows, u, 0);
                    worst = Math.Max(worst, Math.Sqrt(
                        Math.Pow(cx - (ax + (bx - ax) * localT), 2) + Math.Pow(cy - (ay + (by - ay) * localT), 2)));
                }
            }

            return worst;
        }

        double e2 = ErrorAt(2), e4 = ErrorAt(4), e8 = ErrorAt(8);
        e4.Should().BeLessThan(e2 / 3, "doubling the columns should cut the chord error roughly fourfold");
        e8.Should().BeLessThan(e4 / 3);
    }
}
