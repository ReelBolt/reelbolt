using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for <see cref="ScreenInsertMeshFilterBuilder"/> — the non-planar renderer (see
/// docs/video-editing.md "Non-planar surfaces and multi-plate compositing"). The filter-string
/// assertions pin the recipe that was validated against a real ffmpeg run; the geometry
/// assertions pin the property that actually makes the tiling invisible, which is that every
/// cell's warped CONTENT reaches its own mesh cell rather than stopping a border-pixel short.
/// </summary>
public class ScreenInsertMeshFilterBuilderTests
{
    private static (double X, double Y)[][] Grid(int rows, int cols, double x0, double y0, double w, double h, double bowPx = 0)
    {
        var grid = new (double X, double Y)[rows + 1][];
        for (int i = 0; i <= rows; i++)
        {
            grid[i] = new (double X, double Y)[cols + 1];
            double v = i / (double)rows;
            for (int j = 0; j <= cols; j++)
            {
                double u = j / (double)cols;
                // A vertical-axis bow: the top and bottom edges pull inward toward the middle.
                double bow = bowPx * 4 * u * (1 - u) * (1 - 2 * v) * -1;
                grid[i][j] = (x0 + u * w, y0 + v * h + bow);
            }
        }

        return grid;
    }

    private static ResolvedScreenInsert Insert(string regionId = "r0", double start = 2, double end = 5) =>
        new(regionId, $"/scratch/{regionId}.mp4", start, end,
            [new InsertQuadFrame(0, 100, 100, 400, 100, 100, 400, 400, 400)], 30, 1);

    private static InsertMeshPlan Mesh(int rows, int cols, int frames = 3, double bowPx = 12, double drift = 0) =>
        new(rows, cols, Enumerable.Range(0, frames)
            .Select(f => new InsertMeshFrame(f * 30, Grid(rows, cols, 200 + f * drift, 150, 420, 320, bowPx)))
            .ToList());

    private static string Build(ResolvedScreenInsert insert, InsertMeshPlan mesh)
    {
        var segments = new List<string>();
        ScreenInsertMeshFilterBuilder.AppendInsert(segments, "[vcut]", insert, mesh, 0, 1, "[vout]");
        return string.Join(";", segments);
    }

    [Fact]
    public void Emits_one_content_mask_alphamerge_and_overlay_per_cell_plus_a_single_split()
    {
        string chain = Build(Insert(), Mesh(2, 3));

        chain.Should().Contain("split=6", "one branch per cell, split once off the shared asset");
        CountOf(chain, "alphamerge").Should().Be(6);
        CountOf(chain, "overlay=").Should().Be(6);
        CountOf(chain, "perspective=").Should().Be(12, "each cell warps its content AND its mask identically");
        CountOf(chain, "color=c=white").Should().Be(6);
        chain.Should().EndWith("[vout]");
        chain.Should().Contain("[vcut][ma0c0]overlay", "the first cell chains off the caller's label");
    }

    [Fact]
    public void Every_cell_is_gated_to_the_inserts_own_window_and_conformed_to_the_encode_rate()
    {
        string chain = Build(Insert(start: 2.5, end: 7.25), Mesh(1, 2));

        CountOf(chain, "enable='between(t,2.5,7.25)'").Should().Be(2);
        chain.Should().Contain("[1:v]fps=30/1,format=yuv444p,setpts=PTS-STARTPTS+2.5/TB,split=2");
        chain.Should().Contain("eof_action=pass");
    }

    [Fact]
    public void Cells_crop_the_asset_by_floor_tiled_fractions_so_they_abut_exactly()
    {
        // Whatever the asset's real dimensions, cell j's right edge must be cell j+1's left edge:
        // floor(iw*(j+1)/n) appears as both, so there is no gap and no double-sampled column.
        string chain = Build(Insert(), Mesh(1, 3));

        chain.Should().Contain("x='floor(iw*0/3)'");
        chain.Should().Contain("x='floor(iw*1/3)'");
        chain.Should().Contain("x='floor(iw*2/3)'");
        chain.Should().Contain("w='floor(iw*1/3)-floor(iw*0/3)'");
        chain.Should().Contain("w='floor(iw*3/3)-floor(iw*2/3)'");
    }

    [Fact]
    public void A_cell_canvas_is_sized_to_that_cell_alone_not_to_the_whole_frame()
    {
        // This is what makes a dense mesh affordable, and what keeps each source cell being
        // resampled at roughly its own destination scale rather than blown up to frame size.
        InsertMeshPlan mesh = Mesh(2, 4);
        ScreenInsertMeshFilterBuilder.CellPlacement placement =
            ScreenInsertMeshFilterBuilder.PlaceCell(mesh, 0, 0, 2, 4);

        placement.CanvasWidth.Should().BeLessThan(200, "one cell of a 420px-wide plate split four ways");
        placement.CanvasHeight.Should().BeLessThan(200);
        placement.CanvasWidth.Should().BeGreaterThanOrEqualTo(ScreenInsertMeshFilterBuilder.MinCanvasPx);
        placement.OriginX.Should().BeLessThan(200, "the canvas starts at the cell's own bounding box, not at 0,0");
    }

    [Fact]
    public void A_cell_canvas_spans_the_cells_whole_travel_so_the_overlay_offset_can_stay_constant()
    {
        InsertMeshPlan moving = Mesh(1, 2, frames: 3, drift: 60);
        ScreenInsertMeshFilterBuilder.CellPlacement placement =
            ScreenInsertMeshFilterBuilder.PlaceCell(moving, 0, 0, 1, 2);

        InsertMeshPlan still = Mesh(1, 2, frames: 3, drift: 0);
        ScreenInsertMeshFilterBuilder.CellPlacement stillPlacement =
            ScreenInsertMeshFilterBuilder.PlaceCell(still, 0, 0, 1, 2);

        placement.CanvasWidth.Should().BeGreaterThan(stillPlacement.CanvasWidth + 100,
            "the canvas must cover the cell's whole travel — 120px of drift here");

        string chain = Build(Insert(), moving);
        CountOf(chain, "overlay=x=").Should().Be(2, "each cell still has exactly one, constant, overlay position");
    }

    [Fact]
    public void Each_cells_warped_CONTENT_reaches_its_own_mesh_corners_rather_than_stopping_a_border_pixel_short()
    {
        // The seam property, asserted geometrically instead of by eye. `perspective` is given
        // where the padded CANVAS corners must land; the content sits 1px inside that canvas, so
        // without the projective extrapolation it would land a border-pixel short on every side
        // and open a visible gap along every interior cell boundary.
        InsertMeshPlan mesh = Mesh(2, 3, frames: 1, bowPx: 14);

        for (int r = 0; r < 2; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                ScreenInsertMeshFilterBuilder.CellPlacement placement =
                    ScreenInsertMeshFilterBuilder.PlaceCell(mesh, r, c, 2, 3);
                (double X, double Y)[] meshCorners = ScreenInsertMeshFilterBuilder.CellCorners(mesh.Keyframes[0], r, c);
                (long _, (double X, double Y)[] canvasCorners) = placement.Frames[0];

                // The map perspective induces: the canvas's own corners go to canvasCorners.
                InsertSurfaceMesh.UnitSquareHomography g = InsertSurfaceMesh.UnitSquareHomography.Solve(
                    canvasCorners[0], canvasCorners[1], canvasCorners[2], canvasCorners[3])!;

                double cw = placement.CanvasWidth, ch = placement.CanvasHeight;
                (double U, double V)[] contentCornerUv =
                    [(1 / cw, 1 / ch), ((cw - 1) / cw, 1 / ch), (1 / cw, (ch - 1) / ch), ((cw - 1) / cw, (ch - 1) / ch)];

                double centroidX = meshCorners.Average(p => p.X), centroidY = meshCorners.Average(p => p.Y);
                for (int k = 0; k < 4; k++)
                {
                    (double gx, double gy) = g.Map(contentCornerUv[k].U, contentCornerUv[k].V);
                    gx += placement.OriginX;
                    gy += placement.OriginY;

                    double contentRadius = Math.Sqrt(Math.Pow(gx - centroidX, 2) + Math.Pow(gy - centroidY, 2));
                    double meshRadius = Math.Sqrt(Math.Pow(meshCorners[k].X - centroidX, 2) + Math.Pow(meshCorners[k].Y - centroidY, 2));

                    // Corner k touches two sides. A side WITH a neighbour is grown by the
                    // interior overlap that covers the warp's alpha ramp; a side on the mesh's
                    // outer boundary is not grown at all, because that boundary is the measured
                    // silhouette and must not creep outward.
                    bool left = k is 0 or 2, top = k is 0 or 1;
                    bool overlapped = (left ? c > 0 : c < 2) || (top ? r > 0 : r < 1);

                    if (overlapped)
                    {
                        contentRadius.Should().BeGreaterThan(meshRadius,
                            $"cell ({r},{c}) corner {k} abuts a neighbour, so it must overshoot its mesh point");
                        (contentRadius - meshRadius).Should().BeLessThan(6,
                            $"cell ({r},{c}) corner {k} must overlap by a couple of pixels, not run away");
                    }
                    else
                    {
                        contentRadius.Should().BeApproximately(meshRadius, 0.2,
                            $"cell ({r},{c}) corner {k} is on the mesh's outer boundary and must land exactly on the measured silhouette");
                    }
                }
            }
        }
    }

    [Fact]
    public void A_degenerate_cell_falls_back_to_its_raw_corners_instead_of_throwing()
    {
        (double X, double Y)[] collapsed = [(10, 10), (10, 10), (10, 10), (10, 10)];

        ScreenInsertMeshFilterBuilder.ExtrapolateToCanvas(collapsed, 40, 40).Should().Equal(collapsed);
    }

    [Fact]
    public void A_moving_cell_gets_per_frame_corner_expressions_and_a_still_one_gets_constants()
    {
        string still = Build(Insert(), Mesh(1, 1, frames: 3, drift: 0));
        still.Should().NotContain("if(lt(in,", "a cell whose corners never move collapses to plain constants");

        string moving = Build(Insert(), Mesh(1, 1, frames: 3, drift: 40));
        moving.Should().Contain("if(lt(in,", "a moving cell interpolates piecewise-linearly over the filter's `in`");
        moving.Should().Contain(":sense=destination:eval=frame");
    }

    [Fact]
    public void Numbers_are_culture_invariant()
    {
        string chain = Build(Insert(start: 1.5, end: 3.25), Mesh(1, 2));

        chain.Should().NotContain(",5", "a comma decimal separator would corrupt the filtergraph");
        chain.Should().Contain("1.5");
    }

    [Fact]
    public void A_mesh_insert_and_a_flat_insert_chain_together_through_the_planar_builder()
    {
        // The two flavours have to interleave: a compile can legitimately carry one curved plate
        // and one flat one, and the caller's input-index bookkeeping must not care which is which.
        ResolvedScreenInsert flat = Insert("r0");
        ResolvedScreenInsert curved = Insert("r1") with { Mesh = Mesh(1, 2) };

        string chain = ScreenInsertFilterBuilder.BuildFilterChain(
            "[vcut]", [flat, curved], 1920, 1080, i => 1 + i);

        chain.Should().Contain("[1:v]scale=300:300", "insert 0 keeps the flat recipe, prescaled to its 300 x 300 quad");
        chain.Should().Contain("[sia0]overlay=x=96:y=96", "the flat insert is laid back at the integer origin of its warp window");
        chain.Should().Contain("[si0]", "the flat insert hands off to the mesh insert through the shared label");
        chain.Should().Contain("[2:v]fps=30/1", "insert 1 takes the mesh recipe on the next input index");
        chain.Should().Contain("split=2");
        chain.Should().EndWith("[vout]");
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0, at = 0;
        while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }

        return count;
    }
}
