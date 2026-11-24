using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression tests for the customer-clip tracking fixes (docs/video-editing.md "Tracking quality
/// on the customer clips"): edges re-located from the neighbouring frame when a finger covers a
/// corner, perspective's one-based <c>in</c>, plates left visible with nothing composited into
/// them (uncovered spans and plate guards), and the smeared overscan that pins the content's whole
/// frame to the plate.
/// </summary>
public class InsertTrackingQualityTests
{
    private const int GW = 320, GH = 180;

    /// <summary>
    /// A green plate with a black bezel on a grey background, its top-left at (x, y), plus — when
    /// <paramref name="finger"/> — a skin-coloured finger crossing it diagonally from the right and
    /// covering its lower-right corner, the way the customer's swipe clip does.
    /// </summary>
    private static byte[] PhoneFrame(double x, double y, double w, double h, bool finger)
    {
        var frame = new byte[GW * GH * 3];
        for (int py = 0; py < GH; py++)
        {
            for (int px = 0; px < GW; px++)
            {
                (byte r, byte g, byte b) = (120, 122, 125);
                bool inBezel = px >= x - 4 && px < x + w + 4 && py >= y - 4 && py < y + h + 4;
                bool inPlate = px >= x && px < x + w && py >= y && py < y + h;
                if (inBezel)
                    (r, g, b) = (12, 12, 14);
                if (inPlate)
                    (r, g, b) = (0, 200, 0);

                // The finger: a 14 px thick band along the line from (x + w + 30, y + h - 40) to
                // (x + w - 22, y + h + 6), i.e. across the lower-right corner.
                if (finger)
                {
                    double ax = x + w + 30, ay = y + h - 40, bx = x + w - 22, by = y + h + 6;
                    double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
                    double t = Math.Clamp(((px - ax) * vx + (py - ay) * vy) / len2, 0, 1);
                    double dx = px - (ax + t * vx), dy = py - (ay + t * vy);
                    if (dx * dx + dy * dy <= 7 * 7)
                        (r, g, b) = (214, 168, 140);
                }

                int i = (py * GW + px) * 3;
                frame[i] = r;
                frame[i + 1] = g;
                frame[i + 2] = b;
            }
        }

        return frame;
    }

    private static (double X, double Y) BottomRight(VideoInsertQuadKeyframe k) => (k.X3 * GW - 0.5, k.Y3 * GH - 0.5);

    private static IReadOnlyList<VideoInsertRegionTrack> TrackSwipe(bool refine, out Func<int, (double X, double Y)> truthBottomRight)
    {
        // A phone drifting right by half a pixel a frame; the finger is over its corner on frames 12-23.
        const int frames = 36;
        const double w = 70, h = 130;
        var pixels = new List<byte>();
        for (int f = 0; f < frames; f++)
            pixels.AddRange(PhoneFrame(100 + 0.5 * f, 25, w, h, finger: f is >= 12 and < 24));

        truthBottomRight = f => (100 + 0.5 * f + w - 0.5, 25 + h - 0.5);
        // The rigid-pose repair is off: these two tests are about the neighbour-seeded edge refit, which
        // is what still runs on a track the rigid model declines (ChromaPlatePoseTrackerTests has the repair's own).
        return ChromaQuadTracker.Track(
            pixels.ToArray(), frames, GW, GH, 12,
            new ChromaQuadTracker.Options(GrowExcessFraction: 0.5, MinTrackSeconds: 0.5, RefineEdgesFromNeighbours: refine, SolveRigidPose: false));
    }

    [Fact]
    public void A_finger_over_the_corner_does_not_drag_the_tracked_corner_onto_the_finger()
    {
        VideoInsertRegionTrack track = TrackSwipe(refine: true, out var truth).Should().ContainSingle().Subject;
        double worst = track.Keyframes
            .Select(k => (Frame: (int)Math.Round(k.TimeSec * 12), BR: BottomRight(k)))
            .Where(x => x.Frame is >= 12 and < 24)
            .Max(x => Math.Sqrt(Math.Pow(x.BR.X - truth(x.Frame).X, 2) + Math.Pow(x.BR.Y - truth(x.Frame).Y, 2)));

        worst.Should().BeLessThan(2.0,
            "the covered corner is where the plate's right and bottom edges meet, and both edges are still visible away from the finger");
    }

    [Fact]
    public void Without_neighbour_refinement_the_finger_drags_the_corner_which_is_the_failure_being_guarded()
    {
        VideoInsertRegionTrack track = TrackSwipe(refine: false, out var truth).Should().ContainSingle().Subject;
        double worst = track.Keyframes
            .Select(k => (Frame: (int)Math.Round(k.TimeSec * 12), BR: BottomRight(k)))
            .Where(x => x.Frame is >= 12 and < 24)
            .Max(x => Math.Sqrt(Math.Pow(x.BR.X - truth(x.Frame).X, 2) + Math.Pow(x.BR.Y - truth(x.Frame).Y, 2)));

        worst.Should().BeGreaterThan(4.0, "this synthetic finger must reproduce the corruption, or the test above proves nothing");
    }

    [Fact]
    public void A_plate_visible_before_its_track_starts_is_recorded_as_an_uncovered_span_with_its_box()
    {
        // Frames 0-5: a sliver of plate too small to track (a phone turning toward the camera);
        // frames 6-29: the full plate.
        const int frames = 30;
        var pixels = new List<byte>();
        for (int f = 0; f < frames; f++)
            pixels.AddRange(f < 6 ? PhoneFrame(150, 60, 5, 30, finger: false) : PhoneFrame(120, 30, 70, 120, finger: false));

        VideoInsertRegionTrack track = ChromaQuadTracker.Track(
                pixels.ToArray(), frames, GW, GH, 12, new ChromaQuadTracker.Options(GrowExcessFraction: 0.5, MinTrackSeconds: 0.5))
            .Should().ContainSingle().Subject;

        track.StartSec.Should().BeApproximately(0.5, 1e-6);
        VideoInsertPlateSpan span = track.UncoveredPlateSpans.Should().ContainSingle().Subject;
        span.StartSec.Should().Be(0);
        span.EndSec.Should().BeApproximately(0.5, 1e-6);
        (span.X * GW).Should().BeApproximately(150, 0.5);
        (span.Y * GH).Should().BeApproximately(60, 0.5);
        (span.W * GW).Should().BeApproximately(5, 0.5);
        (span.H * GH).Should().BeApproximately(30, 0.5);
    }

    private static VideoInsertRegionTrack PlateTrack(string id, double start, double end, VideoInsertChromaKey? key, double confidence = 0.9,
        IReadOnlyList<VideoInsertPlateSpan>? uncovered = null) =>
        new(id, null, start, end,
            Keyframes: Enumerable.Range(0, (int)Math.Round((end - start) * 10))
                .Select(i => new VideoInsertQuadKeyframe(start + i * 0.1, 0.4, 0.3, 0.6, 0.3, 0.4, 0.7, 0.6, 0.7))
                .ToList(),
            Confidence: confidence, MeanAreaRatio: 0.08, MeanAspectRatio: 0.6, MotionClass: "Static", ColorName: "green",
            ChromaKey: key, UncoveredPlateSpans: uncovered);

    private static readonly VideoInsertChromaKey Key = new(0, 200, 0, 0.3, 0, 0.95, PlateDominance: 200);

    private static (OutputTimeline Timeline, VideoAnalysisMedia Media) SingleSource(double duration)
    {
        var spans = new List<VideoCompileStepExecutor.ResolvedSpan> { new(0, duration, 0, duration, 0, (long)(duration * 24)) };
        return (OutputTimeline.Build(spans, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count)),
            new VideoAnalysisMedia(duration, 24, 1, 1280, 720));
    }

    [Fact]
    public void Plate_guards_cover_a_dropped_track_and_the_parts_of_a_track_outside_its_insert_and_never_the_insert_itself()
    {
        (OutputTimeline timeline, VideoAnalysisMedia media) = SingleSource(10);
        VideoInsertRegionTrack filled = PlateTrack("r0", 0, 5, Key, uncovered: [new VideoInsertPlateSpan(5, 5.5, 0.45, 0.35, 0.1, 0.3)]);
        VideoInsertRegionTrack dropped = PlateTrack("r1", 6, 9, Key);
        var insert = new ResolvedScreenInsert("r0", "content.mp4", 1, 4,
            [new InsertQuadFrame(0, 1, 1, 2, 1, 1, 2, 2, 2)], 24, 1, Matte: Key);

        InsertPlateGuardPlanner.Plan plan = InsertPlateGuardPlanner.Build(
            [filled, dropped], timeline, media, [insert], _ => VideoCompileStepExecutor.SourceCanvasFit.Identity(media),
            matteEnabled: true, minInsertConfidence: 0.5, maxKeyframes: 100);

        plan.Guards.Select(g => (g.RegionId, g.OutputStartSec, g.OutputEndSec)).Should().BeEquivalentTo(new[]
        {
            ("r0", 0.0, 1.0), ("r0", 4.0, 5.0), ("r0", 5.0, 5.5), ("r1", 6.0, 9.0)
        });
        plan.Guards.Should().OnlyContain(g => g.GuardFillColor == InsertPlateGuardPlanner.FillColor && g.Matte == Key);
        plan.Report.Should().HaveCount(4);
        plan.Report.Select(r => r!["code"]!.GetValue<string>()).Should().OnlyContain(c => c == "insert_untracked_plate_visible");

        // The uncovered span's guard is the box the plate was seen in, grown by HeldOverscan.
        ResolvedScreenInsert boxGuard = plan.Guards.Single(g => g.OutputStartSec == 5.0);
        double grow = 1 + InsertPlateGuardPlanner.HeldOverscan;
        boxGuard.Keyframes[0].X0.Should().BeApproximately((0.5 - 0.05 * grow) * 1280, 1e-6);
        boxGuard.Keyframes[0].Y3.Should().BeApproximately((0.5 + 0.15 * grow) * 720, 1e-6);
    }

    [Fact]
    public void Unfilled_plates_become_one_plain_language_compile_warning()
    {
        InsertPlateGuardPlanner.CompileWarning(new System.Text.Json.Nodes.JsonObject()).Should().BeNull();

        var node = new System.Text.Json.Nodes.JsonObject
        {
            ["unfilledPlates"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["outputStartSec"] = 1.0, ["outputEndSec"] = 2.5, ["neutralized"] = true },
                new System.Text.Json.Nodes.JsonObject { ["outputStartSec"] = 4.0, ["outputEndSec"] = 4.5, ["neutralized"] = true })
        };
        System.Text.Json.Nodes.JsonObject warning = InsertPlateGuardPlanner.CompileWarning(node)!;
        warning["code"]!.GetValue<string>().Should().Be("insert_untracked_plate_visible");
        warning["message"]!.GetValue<string>().Should().Contain("2.0 seconds").And.Contain("dark screen");

        // One frame at 24 fps rounds to "0.0" at one decimal — said as "a moment", never "0.0 seconds".
        var oneFrame = new System.Text.Json.Nodes.JsonObject
        {
            ["unfilledPlates"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["outputStartSec"] = 6.583, ["outputEndSec"] = 6.625, ["neutralized"] = true })
        };
        InsertPlateGuardPlanner.CompileWarning(oneFrame)!["message"]!.GetValue<string>()
            .Should().Contain("visible for a moment").And.NotContain("0.0 seconds");
    }

    [Fact]
    public void A_plate_without_a_key_or_far_under_the_confidence_floor_is_reported_but_not_darkened()
    {
        (OutputTimeline timeline, VideoAnalysisMedia media) = SingleSource(10);
        InsertPlateGuardPlanner.Plan plan = InsertPlateGuardPlanner.Build(
            [PlateTrack("r0", 0, 2, key: null), PlateTrack("r1", 3, 5, Key, confidence: 0.1)], timeline, media, [],
            _ => VideoCompileStepExecutor.SourceCanvasFit.Identity(media), matteEnabled: true, minInsertConfidence: 0.5, maxKeyframes: 100);

        plan.Guards.Should().BeEmpty();
        plan.Report.Select(r => r!["notNeutralizedReason"]!.GetValue<string>())
            .Should().BeEquivalentTo(new[] { "no_plate_key", "confidence_too_low_to_guard" });
    }

    [Fact]
    public void Smeared_overscan_warps_the_content_onto_the_plate_without_a_rim_and_masks_with_the_expanded_quad()
    {
        var plate = new InsertQuadFrame[] { new(0, 400, 200, 800, 200, 400, 600, 800, 600) };
        var expanded = new InsertQuadFrame[] { new(0, 380, 180, 820, 180, 380, 620, 820, 620) };
        var insert = new ResolvedScreenInsert("r0", "c.mp4", 0, 2, expanded, 24, 1, Matte: Key, ContentKeyframes: plate);

        string chain = ScreenInsertFilterBuilder.BuildFilterChain("[base]", [insert], 1280, 720, _ => 1);

        string content = chain.Split(';').Single(s => s.StartsWith("[1:v]", StringComparison.Ordinal));
        content.Should().NotContain(":1:1:black", "a black rim would be multiplied into the alpha edge instead of the content's own edge colour");
        // Prescaled to the plate (400 x 400) and smeared over the rest of the warp window (448 x 448: the expanded quad's bounding box
        // plus a 4 px margin, at canvas origin 376,176), so the overscan band carries the content's own edge colour.
        content.Should().Contain("scale=400:400:").And.Contain("pad=448:448:24:24:black").And.Contain("fillborders=").And.Contain("mode=smear");
        content.Should().Contain("x0='0'", "the plate (400..800) sits exactly where the content region does in the window (24 px in from each edge), so the window's corners map to themselves");
        content.Should().Contain("x1='448'");
        // The mask follows the EXPANDED quad (440 x 440), prescaled to it.
        string mask = chain.Split(';').Single(s => s.StartsWith("color=c=white", StringComparison.Ordinal) && s.EndsWith("[sim0]", StringComparison.Ordinal));
        mask.Should().Contain("size=440x440");
    }

    [FfmpegFact]
    public void Perspective_in_variable_is_one_based_so_keyframe_zero_must_be_written_at_in_one()
    {
        // Keyframe 0 puts the left edge at x=100, keyframe 1 at x=200. Rendered, frame 0 must
        // show x=100: written zero-based it showed x=150 — every insert one frame ahead.
        var keys = new List<InsertQuadFrame>
        {
            new(0, 100, 50, 500, 50, 100, 250, 500, 250),
            new(1, 200, 50, 500, 50, 200, 250, 500, 250)
        };
        string perspective = ScreenInsertFilterBuilder.BuildPerspectiveFilter(keys);

        byte[] frames = Ffmpeg(
            ["-f", "lavfi", "-i", "color=c=white:s=598x298:r=24:d=0.2"],
            $"[0:v]format=gray,pad=600:300:1:1:black,{perspective}[o]", 600 * 300, 2);

        int LeftEdge(int frame)
        {
            int row = frame * 600 * 300 + 150 * 600;
            for (int x = 0; x < 600; x++)
            {
                if (frames[row + x] > 128)
                    return x;
            }

            return -1;
        }

        LeftEdge(0).Should().BeInRange(99, 102);
        LeftEdge(1).Should().BeInRange(199, 202);
    }

    [FfmpegFact]
    public void Smeared_overscan_shows_the_contents_own_edge_at_the_plates_edge_where_the_pad_showed_black()
    {
        // A grey frame with a green plate at x 200..440, y 100..260 (480x360 canvas); the tracked
        // quad is the plate, the warp target 10% larger. The content is a solid red card.
        const int W = 480, H = 360;
        var basePixels = new byte[W * H * 3];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                bool plate = x is >= 200 and < 440 && y is >= 100 and < 260;
                int i = (y * W + x) * 3;
                (basePixels[i], basePixels[i + 1], basePixels[i + 2]) = plate ? ((byte)0, (byte)200, (byte)0) : ((byte)110, (byte)110, (byte)110);
            }
        }

        // The tracked quad sits 3 px inside the plate's left edge, as a coarse grid leaves it.
        var plateQuad = new InsertQuadFrame[] { new(0, 203, 100, 440, 100, 203, 260, 440, 260) };
        var expanded = new InsertQuadFrame[] { new(0, 191.15, 92, 451.85, 92, 191.15, 268, 451.85, 268) };
        byte[] Render(ResolvedScreenInsert insert) =>
            FfmpegWithBase(basePixels, W, H, ScreenInsertFilterBuilder.BuildFilterChain("[0:v]", [insert], W, H, _ => 1, "[o]"));

        byte[] smeared = Render(new ResolvedScreenInsert("r0", "c", 0, 1, expanded, 24, 1, Matte: Key, ContentKeyframes: plateQuad));
        byte[] padded = Render(new ResolvedScreenInsert("r0", "c", 0, 1, expanded, 24, 1, Matte: Key,
            ContentPrefilter: InsertContentFitFilter.OverscanPad(0.1)));

        // Pixel x=201, y=180: plate, but outside the tracked quad — the band overscan exists for.
        (byte R, byte G, byte B) Pixel(byte[] img, int x, int y) => (img[(y * W + x) * 3], img[(y * W + x) * 3 + 1], img[(y * W + x) * 3 + 2]);
        Pixel(smeared, 201, 180).R.Should().BeGreaterThan(150, "the content's own left edge is smeared over the band");
        Pixel(padded, 201, 180).R.Should().BeLessThan(60, "the pad filled the band with black");
        Pixel(smeared, 201, 180).G.Should().BeLessThan(60, "no raw plate either way");
    }

    private static byte[] FfmpegWithBase(byte[] rgb, int w, int h, string chain)
    {
        string dir = Path.Combine(Path.GetTempPath(), "insert-quality-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string input = Path.Combine(dir, "base.rgb");
            File.WriteAllBytes(input, rgb);
            return Ffmpeg(
                ["-f", "rawvideo", "-pix_fmt", "rgb24", "-s", $"{w}x{h}", "-r", "24", "-i", input,
                 "-f", "lavfi", "-i", "color=c=red:s=320x240:r=24:d=2"],
                chain + ";[o]format=rgb24[f]", w * h * 3, 1, mapLabel: "[f]");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] Ffmpeg(IEnumerable<string> inputs, string graph, int frameBytes, int frameCount, string mapLabel = "[o]")
    {
        string dir = Path.Combine(Path.GetTempPath(), "insert-quality-ff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string script = Path.Combine(dir, "graph.txt"), output = Path.Combine(dir, "out.raw");
            File.WriteAllText(script, graph);
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string a in new[] { "-v", "error", "-y" }.Concat(inputs).Concat(new[]
                     {
                         "-filter_complex_script", script, "-map", mapLabel, "-frames:v", frameCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                         "-f", "rawvideo", output
                     }))
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0, err);
            byte[] bytes = File.ReadAllBytes(output);
            bytes.Length.Should().BeGreaterThanOrEqualTo(frameBytes * frameCount, err);
            return bytes;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
