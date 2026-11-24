using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tracked-insert precision and reflections (docs/video-editing.md "Sub-pixel corners at the
/// source's frame rate" and "Reflections"): the edge-line corner fit, the source-aligned sample
/// rate, trimming a weak fragment off a track's end, the dominance matte and the reflection
/// transfer — plus, where ffmpeg is installed, a real run of the matte and despill filters,
/// because the bug that left a green rim round every insert lived in ffmpeg's format negotiation
/// and no filter STRING could show it.
/// </summary>
public class InsertTrackingPrecisionTests
{
    // -----------------------------------------------------------------------
    // ChromaEdgeLineFitter
    // -----------------------------------------------------------------------

    /// <summary>
    /// A dominance field of an anti-aliased, rotated rectangle with rounded corners — the shape of
    /// a phone screen — rendered by 8x8 supersampling, so every edge pixel carries its true partial
    /// coverage. Returns the field and the rectangle's true (virtual, unrounded) corners in
    /// TL, TR, BR, BL order, at pixel-centre coordinates.
    /// </summary>
    private static (float[] Field, (double X, double Y)[] Corners) RenderPlate(
        int w, int h, double cx, double cy, double halfW, double halfH, double angleDeg, double radius,
        Func<double, double>? bowTopBottom = null)
    {
        double a = angleDeg * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        var field = new float[w * h];
        const int ss = 8;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int inside = 0;
                for (int sy = 0; sy < ss; sy++)
                {
                    for (int sx = 0; sx < ss; sx++)
                    {
                        double px = x - 0.5 + (sx + 0.5) / ss - cx, py = y - 0.5 + (sy + 0.5) / ss - cy;
                        double u = px * cos + py * sin, v = -px * sin + py * cos;
                        double hh = halfH - (bowTopBottom?.Invoke(u / halfW) ?? 0);
                        double qx = Math.Abs(u) - (halfW - radius), qy = Math.Abs(v) - (hh - radius);
                        double outside = Math.Sqrt(Math.Pow(Math.Max(qx, 0), 2) + Math.Pow(Math.Max(qy, 0), 2))
                            + Math.Min(Math.Max(qx, qy), 0) - radius;
                        if (outside <= 0)
                            inside++;
                    }
                }

                field[y * w + x] = 200f * inside / (ss * ss);
            }
        }

        (double X, double Y) P(double u, double v) => (cx + u * cos - v * sin, cy + u * sin + v * cos);
        return (field, [P(-halfW, -halfH), P(halfW, -halfH), P(halfW, halfH), P(-halfW, halfH)]);
    }

    private static (double X, double Y)[] ExtremePoints(float[] field, int w, int h)
    {
        (double, double) tl = default, tr = default, br = default, bl = default;
        int minS = int.MaxValue, maxS = int.MinValue, minD = int.MaxValue, maxD = int.MinValue;
        for (int i = 0; i < field.Length; i++)
        {
            if (field[i] < 100)
                continue;
            int x = i % w, y = i / w;
            if (x + y < minS) { minS = x + y; tl = (x, y); }
            if (x + y > maxS) { maxS = x + y; br = (x, y); }
            if (x - y > maxD) { maxD = x - y; tr = (x, y); }
            if (x - y < minD) { minD = x - y; bl = (x, y); }
        }

        return [tl, tr, br, bl];
    }

    private static double MaxError((double X, double Y)[] a, (double X, double Y)[] b) =>
        a.Zip(b, (p, q) => Math.Sqrt((p.X - q.X) * (p.X - q.X) + (p.Y - q.Y) * (p.Y - q.Y))).Max();

    [Theory]
    [InlineData(80.3, 45.6, 0)]
    [InlineData(81.7, 44.2, 4)]
    [InlineData(79.1, 46.9, -7)]
    public void Line_fit_recovers_the_true_corners_of_a_rounded_anti_aliased_plate_to_a_fraction_of_a_pixel(
        double cx, double cy, double angle)
    {
        const int W = 160, H = 90;
        (float[] field, (double X, double Y)[] truth) = RenderPlate(W, H, cx, cy, 18.4, 32.3, angle, radius: 3.5);
        (double X, double Y)[] extreme = ExtremePoints(field, W, H);

        ChromaEdgeLineFitter.Result? fit = ChromaEdgeLineFitter.Fit(
            field, W, H, 200, extreme[0], extreme[1], extreme[2], extreme[3]);

        fit.Should().NotBeNull();
        MaxError(fit!.Value.Corners, truth).Should().BeLessThan(0.35,
            "every edge is located from its whole intensity ramp, not from one quantized pixel");
        MaxError(extreme, truth).Should().BeGreaterThan(1.5,
            "the extreme pixel sits on the corner rounding, well inside the true corner — the bias the fit removes");
        fit.Value.MaxBowFraction.Should().BeLessThan(ChromaQuadTracker.FlatEdgeBowFraction, "the plate is flat");
    }

    [Fact]
    public void Line_fit_reports_a_bowed_plate_as_curved()
    {
        const int W = 160, H = 90;
        // Top and bottom edges pulled inward by up to 5px in the middle (a screen bent about a vertical axis).
        (float[] field, _) = RenderPlate(W, H, 80.2, 45.4, 30, 30, 0, radius: 2, bowTopBottom: t => 5 * (1 - t * t));
        (double X, double Y)[] extreme = ExtremePoints(field, W, H);

        ChromaEdgeLineFitter.Result? fit = ChromaEdgeLineFitter.Fit(
            field, W, H, 200, extreme[0], extreme[1], extreme[2], extreme[3]);

        fit.Should().NotBeNull();
        fit!.Value.MaxBowFraction.Should().BeGreaterThan(ChromaQuadTracker.FlatEdgeBowFraction,
            "a flat-plate verdict would skip the profiler and lose the mesh warp this plate needs");
    }

    [Fact]
    public void Line_fit_declines_a_plate_too_small_to_measure()
    {
        const int W = 64, H = 36;
        (float[] field, _) = RenderPlate(W, H, 32, 18, 1.2, 1.2, 0, radius: 0);
        (double X, double Y)[] extreme = ExtremePoints(field, W, H);

        ChromaEdgeLineFitter.Fit(field, W, H, 200, extreme[0], extreme[1], extreme[2], extreme[3])
            .Should().BeNull("the caller then keeps the extreme-point corners, the pre-existing behaviour");
    }

    [Fact]
    public void Line_fit_ignores_a_bite_out_of_one_edge()
    {
        const int W = 160, H = 90;
        (float[] field, (double X, double Y)[] truth) = RenderPlate(W, H, 80.4, 45.3, 20, 30, 0, radius: 3);
        // A reflection or a fingertip removing a stretch of the left edge.
        for (int y = 40; y < 50; y++)
            for (int x = 55; x < 66; x++)
                field[y * W + x] = 0;
        (double X, double Y)[] extreme = ExtremePoints(field, W, H);

        ChromaEdgeLineFitter.Result? fit = ChromaEdgeLineFitter.Fit(
            field, W, H, 200, extreme[0], extreme[1], extreme[2], extreme[3]);

        fit.Should().NotBeNull();
        MaxError(fit!.Value.Corners, truth).Should().BeLessThan(0.5, "probes starting inside a hole are skipped and the rest outvote it");
    }

    // -----------------------------------------------------------------------
    // Sample rate
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, 24.0, 6.6, 24.0)]          // the source's own rate
    [InlineData(0, 60.0, 6.6, 30.0)]          // capped at 30 by an exact divisor
    [InlineData(0, 30000.0 / 1001, 10, 30000.0 / 1001)]
    [InlineData(0, 25.0, 10, 25.0)]
    [InlineData(0, 0.0, 10, 10.0)]            // unprobed: the historical default
    [InlineData(12.5, 24.0, 6.6, 12.5)]       // an explicit author rate is honoured
    [InlineData(99, 24.0, 6.6, 30.0)]         // ...clamped
    public void Sample_rate_follows_the_source(double configured, double sourceFps, double duration, double expected)
    {
        ChromaQuadTracker.ResolveSampleFps(configured, sourceFps, duration, 480, 270, 3000)
            .Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void A_long_clip_drops_to_an_exact_divisor_of_the_source_rate_rather_than_an_arbitrary_one()
    {
        // 480x270 RGB is ~389 KB a frame, so the byte budget holds ~1726 frames: 120 s fits at
        // 12 fps (1440 frames) but not at 24 (2880).
        double fps = ChromaQuadTracker.ResolveSampleFps(0, 24, 120, 480, 270, 20_000);

        fps.Should().Be(12);
        (fps * 120).Should().BeLessThanOrEqualTo(ChromaQuadTracker.SampleFrameCap(480, 270, 20_000));
    }

    [Fact]
    public void Frame_count_knobs_keep_their_meaning_in_seconds()
    {
        ChromaQuadTracker.ScaleFrames(3, 24 / ChromaQuadTracker.ReferenceSampleFps).Should().Be(7);
        ChromaQuadTracker.ScaleFrames(3, 1.0).Should().Be(3);
        ChromaQuadTracker.ScaleFrames(3, 0.5).Should().Be(3, "never scaled below the rate they were tuned at");
        ChromaQuadTracker.ScaleFrames(0, 3.0).Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // Trimming a weak fragment off a track's end
    // -----------------------------------------------------------------------

    private const int GW = 160, GH = 90;

    private static byte[] PlateFrame(int x0, int y0, int w, int h)
    {
        var frame = new byte[GW * GH * 3];
        for (int i = 0; i < GW * GH; i++)
        {
            frame[i * 3] = 150;
            frame[i * 3 + 1] = 150;
            frame[i * 3 + 2] = 160;
        }

        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                int p = (y * GW + x) * 3;
                frame[p] = 10;
                frame[p + 1] = 210;
                frame[p + 2] = 15;
            }
        }

        return frame;
    }

    private static byte[] Blank() => PlateFrame(0, 0, 0, 0);

    [Fact]
    public void A_sliver_seen_before_a_gap_is_not_bridged_onto_the_plate()
    {
        // Two frames of a thin sliver (the screen seen edge-on), three frames of nothing (glare),
        // then the plate square to camera — the project's phone clip in miniature.
        var frames = new List<byte[]> { PlateFrame(76, 20, 6, 14), PlateFrame(76, 20, 6, 14) };
        frames.AddRange(Enumerable.Range(0, 3).Select(_ => Blank()));
        frames.AddRange(Enumerable.Range(0, 20).Select(_ => PlateFrame(60, 15, 40, 60)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            frames.SelectMany(f => f).ToArray(), frames.Count, GW, GH, 10,
            new ChromaQuadTracker.Options(MinTrackSeconds: 0.5));

        tracks.Should().ContainSingle();
        tracks[0].StartSec.Should().BeApproximately(0.5, 1e-9,
            "the sliver and the interpolated bridge from it are dropped; the insert opens on the first clean frame");
    }

    [Fact]
    public void A_plate_that_grows_without_a_gap_keeps_every_frame()
    {
        // Walking toward camera: small at first, never lost.
        var frames = Enumerable.Range(0, 20)
            .Select(i => PlateFrame(70 - i, 30 - i, 10 + 2 * i, 14 + 2 * i))
            .ToList();

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            frames.SelectMany(f => f).ToArray(), frames.Count, GW, GH, 10,
            new ChromaQuadTracker.Options(MinTrackSeconds: 0.5));

        tracks.Should().ContainSingle();
        tracks[0].StartSec.Should().Be(0);
    }

    [Fact]
    public void A_detected_track_carries_the_plate_s_own_dominance_for_the_matte()
    {
        var frames = Enumerable.Range(0, 20).Select(_ => PlateFrame(60, 15, 40, 60)).ToList();

        VideoInsertRegionTrack track = ChromaQuadTracker.Track(
            frames.SelectMany(f => f).ToArray(), frames.Count, GW, GH, 10,
            new ChromaQuadTracker.Options(SolveChromaKey: true))[0];

        track.ChromaKey.Should().NotBeNull();
        track.ChromaKey!.PlateDominance.Should().Be(210 - 15);
    }

    // -----------------------------------------------------------------------
    // Filter strings
    // -----------------------------------------------------------------------

    private static ResolvedScreenInsert Insert(VideoInsertChromaKey? matte, bool reflections) =>
        new("r0", "/scratch/a.mp4", 1.0, 4.0,
            [new InsertQuadFrame(0, 100, 100, 300, 100, 100, 500, 300, 500)],
            24, 1, Matte: matte, TransferReflections: reflections);

    private static readonly VideoInsertChromaKey ColorKeyOnly = new(0, 202, 0, 0.39, 0, 0.95);
    private static readonly VideoInsertChromaKey WithDominance = ColorKeyOnly with { PlateDominance = 201 };

    [Fact]
    public void A_key_with_a_measured_plate_level_is_matted_on_dominance_not_colorkey()
    {
        string graph = ScreenInsertFilterBuilder.BuildFilterChain("[base]", [Insert(WithDominance, false)], 1344, 768, _ => 1);

        graph.Should().NotContain("colorkey=");
        graph.Should().Contain("lut2=c0='255*max(");
        graph.Should().Contain("(x-70.35)/60.3", "the plate ramp runs 35%..65% of the plate's own level of 201");
    }

    [Fact]
    public void A_key_from_an_older_artifact_keeps_its_colorkey_matte()
    {
        string graph = ScreenInsertFilterBuilder.BuildFilterChain("[base]", [Insert(ColorKeyOnly, false)], 1344, 768, _ => 1);

        graph.Should().Contain("colorkey=0x00CA00:0.39:0");
        graph.Should().NotContain("lut2=");
    }

    [Fact]
    public void Reflections_are_transferred_only_when_asked_for_and_only_with_a_matte()
    {
        string with = ScreenInsertFilterBuilder.BuildFilterChain("[base]", [Insert(WithDominance, true)], 1344, 768, _ => 1);
        string without = ScreenInsertFilterBuilder.BuildFilterChain("[base]", [Insert(WithDominance, false)], 1344, 768, _ => 1);
        string unmatted = ScreenInsertFilterBuilder.BuildFilterChain("[base]", [Insert(null, true)], 1344, 768, _ => 1);

        with.Should().Contain("blend=all_mode=screen").And.Contain("[rfa0][rfm0]overlay=").And.EndWith("[vout]");
        with.Should().Contain("split=4[mtbase][mtsrc0][mtsrc1][mtsrc2]", "a third copy of the base feeds the reflection layer");
        without.Should().NotContain("all_mode=screen");
        unmatted.Should().NotContain("all_mode=screen");
    }

    [Fact]
    public void Every_blend_branch_that_needs_rgb_semantics_is_pinned_to_planar_rgb()
    {
        string graph = ScreenInsertFilterBuilder.BuildFilterChain("[base]", [Insert(WithDominance, true)], 1344, 768, _ => 1);

        graph.Should().Contain("[mtsrc1]format=gbrp,split=4[ds0a]", "the despill's darken/lighten are per-RGB-channel min/max only in RGB");
        graph.Should().Contain("[mtsrc0]format=gbrp,split=3[mt0cr]");
        graph.Should().Contain("[mtsrc2]format=gbrp,split=3[rl0cr]");
        graph.Should().Contain("format=gbrp,split=2[rfa0][rfb0]");
    }

    [Fact]
    public void Long_keyframe_series_switch_to_the_balanced_tree_and_short_ones_stay_byte_identical()
    {
        var shortKeys = Enumerable.Range(0, ScreenInsertFilterBuilder.LinearChainMaxKeyframes).Select(i => ((long)i, i * 0.5)).ToList();
        var longKeys = Enumerable.Range(0, ScreenInsertFilterBuilder.LinearChainMaxKeyframes + 1).Select(i => ((long)i, i * 0.5)).ToList();

        ScreenInsertFilterBuilder.BuildInterpolationExpr(shortKeys).Should().Be(ScreenInsertFilterBuilder.BuildPiecewiseExpr(shortKeys));
        ScreenInsertFilterBuilder.BuildInterpolationExpr(longKeys).Should().Be(ScreenInsertFilterBuilder.BuildBalancedPiecewiseExpr(longKeys));
    }

    // -----------------------------------------------------------------------
    // Real ffmpeg
    // -----------------------------------------------------------------------

    /// <summary>One RGB frame through a filtergraph built by <paramref name="build"/>; returns the output pixels.</summary>
    private static byte[] RunFfmpeg(byte[] rgb, int w, int h, Func<string, List<string>, string> build)
    {
        string dir = Path.Combine(Path.GetTempPath(), "insert-precision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = Path.Combine(dir, "in.rgb"), output = Path.Combine(dir, "out.rgb"), script = Path.Combine(dir, "graph.txt");
            File.WriteAllBytes(input, rgb);
            var segments = new List<string>();
            string outLabel = build("[0:v]", segments);
            File.WriteAllText(script, string.Join(";", segments) + $";{outLabel}format=rgb24[o]");

            var psi = new ProcessStartInfo("ffmpeg")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (string a in new[]
                     {
                         "-v", "error", "-y", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{w}x{h}", "-i", input,
                         "-filter_complex_script", script, "-map", "[o]", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", output
                     })
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0, err);
            return File.ReadAllBytes(output);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A 4-pixel-wide strip of test colours, each 8x8.</summary>
    private static byte[] Swatches(params (byte R, byte G, byte B)[] colours)
    {
        int w = colours.Length * 8, h = 8;
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (byte r, byte g, byte b) = colours[x / 8];
                int i = (y * w + x) * 3;
                rgb[i] = r;
                rgb[i + 1] = g;
                rgb[i + 2] = b;
            }
        }

        return rgb;
    }

    [FfmpegFact]
    public void Dominance_matte_keys_plate_dark_plate_and_reflections_and_rejects_scenery()
    {
        (byte, byte, byte)[] colours =
        [
            (0, 201, 0),     // clean plate
            (0, 25, 0),      // the anti-aliased edge into the bezel: dark but pure
            (150, 215, 160), // a reflection streak across the plate
            (70, 93, 26),    // the potted plant behind the subject
            (230, 230, 230), // white wall
            (170, 190, 220), // pale blue shirt
            (5, 5, 5)        // the bezel itself
        ];
        byte[] rgb = Swatches(colours);

        byte[] matte = RunFfmpeg(rgb, colours.Length * 8, 8,
            (input, segs) => ScreenInsertFilterBuilder.AppendDominanceMatte(segs, input, WithDominance, 201, "m"));

        byte Value(int swatch) => matte[(4 * colours.Length * 8 + swatch * 8 + 4) * 3];
        Value(0).Should().Be(255);
        Value(1).Should().BeGreaterThan(200, "a dark edge pixel that is still pure plate colour must be covered, or it shows as a green rim");
        Value(2).Should().BeGreaterThan(200, "a reflection is still the plate");
        Value(3).Should().BeLessThan(16,
            "dull scenery green must not be painted over — it sits at the very foot of the reflection ramp (~5%), " +
            "which is the price of keeping the dimmest real reflection streaks (green ~121 against the plant's 93)");
        Value(4).Should().Be(0);
        Value(5).Should().Be(0);
        Value(6).Should().Be(0);
    }

    [FfmpegFact]
    public void Reflection_layer_is_black_on_a_clean_plate_of_any_purity_and_keeps_white_glare()
    {
        (byte, byte, byte)[] colours = [(0, 201, 0), (0, 171, 30), (150, 215, 160)];
        byte[] layer = RunFfmpeg(Swatches(colours), colours.Length * 8, 8,
            (input, segs) => ScreenInsertFilterBuilder.AppendReflectionLayer(segs, input, "r"));

        (byte, byte, byte) Pixel(int swatch)
        {
            int i = (4 * colours.Length * 8 + swatch * 8 + 4) * 3;
            return (layer[i], layer[i + 1], layer[i + 2]);
        }

        Pixel(0).Should().Be(((byte)0, (byte)0, (byte)0));
        Pixel(1).Should().Be(((byte)0, (byte)0, (byte)0), "an impure plate's blue must not be mistaken for a reflection — it tinted black content teal");
        Pixel(2).Should().Be(((byte)142, (byte)142, (byte)142), "glare is white light: min(R,G,B) less the noise floor, in every channel");
    }
}

/// <summary>Skips at discovery time unless an <c>ffmpeg</c> binary is on PATH.</summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (!OnPath("ffmpeg"))
            Skip = "Requires an ffmpeg binary on PATH.";
    }

    private static bool OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, name)) || File.Exists(Path.Combine(dir, name + ".exe")));
}
