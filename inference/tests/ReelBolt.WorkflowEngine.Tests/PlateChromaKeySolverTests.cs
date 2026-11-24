using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for <see cref="PlateChromaKeySolver"/> — the grid search that picks each plate's own
/// <c>colorkey</c> parameters so the compile step can clip an insert to the plate's real
/// silhouette (docs/video-editing.md "Silhouette matting"). Fixtures are synthetic frames with a
/// known plate colour, a known occluder and a known blend rim, so the search can be held to a
/// result rather than to "it returned something".
/// </summary>
public class PlateChromaKeySolverTests
{
    private const int W = 96;
    private const int H = 72;

    private sealed record Scene(byte[] Pixels, List<int> Plate, bool[] Core, bool[] Grow);

    /// <summary>
    /// A frame with a rectangular plate of <paramref name="plate"/> on a <paramref name="background"/>
    /// field, optionally with an occluder rectangle punched through it and a one-pixel 50/50 blend
    /// rim around that occluder — the physical thing a real motion-blurred edge produces.
    /// </summary>
    private static Scene Build(
        (byte R, byte G, byte B) plate, (byte R, byte G, byte B) background,
        (int X, int Y, int W, int H)? occluder = null, (byte R, byte G, byte B)? occluderColour = null)
    {
        const int px0 = 20, py0 = 14, px1 = 76, py1 = 58;
        var pixels = new byte[W * H * 3];
        var core = new bool[W * H];
        var grow = new bool[W * H];
        var platePixels = new List<int>();
        (byte r, byte g, byte b) occ = occluderColour ?? ((byte)210, (byte)170, (byte)150);

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int p = y * W + x;
                bool onPlate = x >= px0 && x < px1 && y >= py0 && y < py1;
                (byte r, byte g, byte b) c = onPlate ? plate : background;

                if (onPlate && occluder is { } o)
                {
                    bool inside = x >= o.X && x < o.X + o.W && y >= o.Y && y < o.Y + o.H;
                    bool rim = !inside &&
                        x >= o.X - 1 && x < o.X + o.W + 1 && y >= o.Y - 1 && y < o.Y + o.H + 1;

                    if (inside)
                    {
                        c = occ;
                        onPlate = false;
                    }
                    else if (rim)
                    {
                        // Half occluder, half plate: real green light on a real object's edge.
                        c = ((byte)((occ.r + plate.R) / 2), (byte)((occ.g + plate.G) / 2), (byte)((occ.b + plate.B) / 2));
                        onPlate = false;
                    }
                }

                pixels[p * 3] = c.r;
                pixels[p * 3 + 1] = c.g;
                pixels[p * 3 + 2] = c.b;

                // The tracker's two thresholds, as the real detector computes them.
                core[p] = c.g >= 60 && c.g * 10 > c.r * 13 + 100 && c.g * 10 > c.b * 13 + 100;
                grow[p] = c.g >= 24 && c.g * 10 > c.r * 11 + 20 && c.g * 10 > c.b * 11 + 20;

                // The tracker's ground truth is its GROWN component, not "pixels I drew as
                // plate": an occluder's half-plate blend rim passes its thresholds and is
                // 4-connected to the plate, so the real detector includes it and so must this.
                if (grow[p])
                    platePixels.Add(p);
            }
        }

        return new Scene(pixels, platePixels, core, grow);
    }

    private static PlateChromaKeySolver.Sample ToSample(Scene s) => new(
        s.Pixels, 0, W, H, s.Plate, s.Core, s.Grow,
        [(20, 14), (75, 14), (20, 57), (75, 57)]);

    /// <summary>Applies the solved key exactly as ffmpeg's colorkey would, returning coverage (1 = the insert shows here).</summary>
    private static double CoverageAt(Scene scene, PlateChromaKey key, int x, int y)
    {
        double diff = PlateChromaKeySolver.Distance(scene.Pixels, (y * W + x) * 3, key.R, key.G, key.B);
        return PlateChromaKeySolver.Coverage(diff, key.Similarity, key.Blend);
    }

    [Fact]
    public void Solves_a_bright_saturated_plate()
    {
        Scene scene = Build(plate: (0, 230, 0), background: (120, 110, 105));

        PlateChromaKey key = PlateChromaKeySolver.Solve([ToSample(scene)]);

        key.Score.Should().BeGreaterThan(0.95);
        CoverageAt(scene, key, 48, 36).Should().BeApproximately(1, 1e-9, "the plate's centre must be covered");
        CoverageAt(scene, key, 5, 5).Should().BeApproximately(0, 1e-9, "the background must not be");
    }

    [Fact]
    public void Solves_a_DIM_desaturated_plate_that_no_single_fixed_tolerance_could_share()
    {
        // The reason the parameters are searched at all: the project's two real clips measure
        // RGB (0,232,0) and (31,63,10). A tolerance keying the second would key half a room in
        // the first. Both must solve, and to very different parameters.
        Scene bright = Build(plate: (0, 230, 0), background: (120, 110, 105));
        Scene dim = Build(plate: (31, 63, 10), background: (44, 42, 40));

        PlateChromaKey brightKey = PlateChromaKeySolver.Solve([ToSample(bright)]);
        PlateChromaKey dimKey = PlateChromaKeySolver.Solve([ToSample(dim)]);

        brightKey.Score.Should().BeGreaterThan(0.9);
        dimKey.Score.Should().BeGreaterThan(0.9);
        CoverageAt(dim, dimKey, 48, 36).Should().BeApproximately(1, 1e-9);
        CoverageAt(dim, dimKey, 5, 5).Should().BeApproximately(0, 1e-9);

        // The point is not that the two keys differ numerically but that NEITHER transfers: each
        // scene's solution misses the other's plate entirely, which is why this is searched per
        // plate rather than configured once.
        CoverageAt(dim, brightKey, 48, 36).Should().BeApproximately(0, 1e-9,
            "the bright plate's key does not see the dim plate at all");
        CoverageAt(bright, dimKey, 48, 36).Should().BeApproximately(0, 1e-9,
            "and the dim plate's key does not see the bright one");
    }

    [Fact]
    public void An_occluder_is_excluded_from_the_matte_so_it_can_pass_in_FRONT_of_the_insert()
    {
        Scene scene = Build(plate: (0, 230, 0), background: (120, 110, 105), occluder: (40, 28, 18, 16));

        PlateChromaKey key = PlateChromaKeySolver.Solve([ToSample(scene)]);

        CoverageAt(scene, key, 48, 36).Should().BeApproximately(0, 1e-9, "the occluder's interior must not be matted");
        CoverageAt(scene, key, 28, 36).Should().BeApproximately(1, 1e-9, "the plate beside it still must be");
    }

    [Fact]
    public void The_spill_term_prefers_a_key_that_absorbs_an_occluders_blend_rim()
    {
        // The rim pixels are half plate by construction, so they carry real plate colour. A key
        // chosen on agreement alone leaves them in the base and they show as a coloured halo
        // tracing the occluder; the spill term is what pushes the search wide enough to take them.
        Scene scene = Build(plate: (0, 230, 0), background: (120, 110, 105), occluder: (40, 28, 18, 16));

        PlateChromaKey key = PlateChromaKeySolver.Solve([ToSample(scene)]);

        // A rim pixel: one row above the occluder's top edge, inside the plate.
        CoverageAt(scene, key, 48, 27).Should().BeGreaterThan(0.5,
            "the half-plate blend rim should be absorbed by the matte rather than left showing");
        CoverageAt(scene, key, 48, 36).Should().BeApproximately(0, 1e-9,
            "…without the key growing far enough to swallow the occluder itself");
    }

    [Fact]
    public void A_frame_with_no_plate_yields_no_key_rather_than_a_guess()
    {
        Scene scene = Build(plate: (0, 230, 0), background: (120, 110, 105));
        var empty = new PlateChromaKeySolver.Sample(scene.Pixels, 0, W, H, [], scene.Core, scene.Grow);

        PlateChromaKeySolver.Solve([]).IsUsable.Should().BeFalse();
        PlateChromaKeySolver.Solve([empty]).IsUsable.Should().BeFalse();
    }

    [Fact]
    public void A_plate_indistinguishable_from_its_background_scores_below_the_usable_floor()
    {
        // Nothing separates these, so no key can reproduce the tracker's segmentation. The
        // solver must say so rather than return the least-bad guess, because downstream a key is
        // trusted to cut the composite.
        Scene scene = Build(plate: (90, 96, 92), background: (88, 94, 90));

        PlateChromaKeySolver.Solve([ToSample(scene)]).Score.Should().BeLessThan(PlateChromaKeySolver.MinUsableScore);
    }

    [Fact]
    public void Coverage_matches_ffmpeg_colorkey_including_the_blend_ramp()
    {
        // Verified against a real ffmpeg run over a synthesized distance ramp: alpha is 0 at or
        // below `similarity` and ramps to 1 by `similarity + blend`. A score computed against a
        // different curve than the one that will run would be worthless.
        PlateChromaKeySolver.Coverage(0.04, 0.05, 0).Should().Be(1, "inside the radius is fully keyed");
        PlateChromaKeySolver.Coverage(0.06, 0.05, 0).Should().Be(0, "outside it, with no ramp, is not keyed at all");
        PlateChromaKeySolver.Coverage(0.075, 0.05, 0.05).Should().BeApproximately(0.5, 1e-9, "halfway up the ramp");
        PlateChromaKeySolver.Coverage(0.20, 0.05, 0.05).Should().Be(0, "beyond the ramp");
    }

    [Fact]
    public void Distance_is_ffmpegs_own_normalized_metric()
    {
        var pixels = new byte[] { 40, 160, 30 };

        PlateChromaKeySolver.Distance(pixels, 0, 40, 160, 30).Should().Be(0);
        // A pure 30-unit green offset: sqrt(30^2/3)/255.
        PlateChromaKeySolver.Distance(pixels, 0, 40, 130, 30)
            .Should().BeApproximately(Math.Sqrt(900 / 3.0) / 255.0, 1e-12);
    }

    [Theory]
    [InlineData(48, 36, true)]    // centre
    [InlineData(21, 15, true)]    // just inside a corner
    [InlineData(5, 5, false)]     // outside
    [InlineData(90, 36, false)]   // beyond the right edge
    public void InsideQuad_tests_containment_in_the_tracked_corner_order(double x, double y, bool expected)
    {
        (double X, double Y)[] quad = [(20, 14), (75, 14), (20, 57), (75, 57)];

        PlateChromaKeySolver.InsideQuad(quad, x, y).Should().Be(expected);
    }

    [Fact]
    public void A_null_quad_admits_everything_so_a_caller_without_geometry_is_simply_unbounded()
    {
        PlateChromaKeySolver.InsideQuad(null, -500, 9999).Should().BeTrue();
    }
}
