using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tests for <see cref="ChromaQuadTracker"/> — the deterministic chroma-plate quad tracker behind
/// tracked screen inserts (docs/video-editing.md "Tracked screen inserts (Phase 5)"). All fixtures
/// are synthetic in-memory RGB24 frames with a known solid-color rectangle drawn at known
/// coordinates, so every assertion checks the tracker against ground truth it can be held to
/// exactly — no real footage involved (matching this feature's validation plan).
/// </summary>
public class ChromaQuadTrackerTests
{
    private const int W = 160;
    private const int H = 90;
    private const double Fps = 10.0;

    private static readonly ChromaQuadTracker.Options DefaultOptions = new(
        ColorName: "green", MinTrackSeconds: 1.0);

    /// <summary>One RGB24 frame: neutral gray background with a filled axis-aligned rectangle of the given color.</summary>
    private static byte[] MakeFrame(int rectX, int rectY, int rectW, int rectH, (byte R, byte G, byte B)? color = null)
    {
        (byte r, byte g, byte b) = color ?? ((byte)30, (byte)220, (byte)40);
        var frame = new byte[W * H * 3];
        for (int i = 0; i < W * H; i++)
        {
            frame[i * 3] = 100;
            frame[i * 3 + 1] = 100;
            frame[i * 3 + 2] = 100;
        }

        for (int y = Math.Max(0, rectY); y < Math.Min(H, rectY + rectH); y++)
        {
            for (int x = Math.Max(0, rectX); x < Math.Min(W, rectX + rectW); x++)
            {
                int p = (y * W + x) * 3;
                frame[p] = r;
                frame[p + 1] = g;
                frame[p + 2] = b;
            }
        }

        return frame;
    }

    private static byte[] Concat(IEnumerable<byte[]> frames)
    {
        List<byte[]> list = frames.ToList();
        var buffer = new byte[list.Count * W * H * 3];
        for (int i = 0; i < list.Count; i++)
            Buffer.BlockCopy(list[i], 0, buffer, i * W * H * 3, W * H * 3);
        return buffer;
    }

    [Fact]
    public void Static_green_rectangle_is_tracked_with_accurate_corners_and_high_confidence()
    {
        // 20 frames (2s at 10fps) of a fixed 40x30 rectangle at (60, 30).
        byte[] pixels = Concat(Enumerable.Range(0, 20).Select(_ => MakeFrame(60, 30, 40, 30)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(pixels, 20, W, H, Fps, DefaultOptions);

        tracks.Should().HaveCount(1);
        VideoInsertRegionTrack track = tracks[0];
        track.Id.Should().Be("r0");
        track.StartSec.Should().BeApproximately(0.0, 1e-9);
        track.EndSec.Should().BeApproximately(2.0, 1e-9);
        track.Confidence.Should().BeGreaterThan(0.75, "every frame detects a clean rectangular plate");
        track.MotionClass.Should().Be("Static");
        track.ColorName.Should().Be("green");
        track.MeanAreaRatio.Should().BeApproximately(40.0 * 30 / (W * H), 0.01);
        track.MeanAspectRatio.Should().BeApproximately(40.0 / 30, 0.15);

        // Corner ground truth (normalized pixel centers): TL=(60.5/160, 30.5/90), BR=(99.5/160, 59.5/90).
        VideoInsertQuadKeyframe k = track.Keyframes[track.Keyframes.Count / 2];
        k.X0.Should().BeApproximately(60.5 / W, 0.02);
        k.Y0.Should().BeApproximately(30.5 / H, 0.02);
        k.X3.Should().BeApproximately(99.5 / W, 0.02);
        k.Y3.Should().BeApproximately(59.5 / H, 0.02);
        // Corner ORDER matters (ffmpeg perspective order): TR right of TL, BL below TL.
        k.X1.Should().BeGreaterThan(k.X0);
        k.Y2.Should().BeGreaterThan(k.Y0);
    }

    [Fact]
    public void Moving_rectangle_produces_monotonically_advancing_corners_and_a_motion_class()
    {
        // Rectangle slides right by 2px per frame over 30 frames (3s): 60px total travel.
        byte[] pixels = Concat(Enumerable.Range(0, 30).Select(i => MakeFrame(10 + i * 2, 30, 40, 30)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(pixels, 30, W, H, Fps, DefaultOptions);

        tracks.Should().HaveCount(1);
        VideoInsertRegionTrack track = tracks[0];
        track.MotionClass.Should().BeOneOf("Slow", "Moving");

        // Smoothed corner x must advance monotonically (allowing tiny numerical slack).
        for (int i = 1; i < track.Keyframes.Count; i++)
            track.Keyframes[i].X0.Should().BeGreaterThanOrEqualTo(track.Keyframes[i - 1].X0 - 1e-9);

        // Total tracked travel ~ 58/160 (extreme-point corners at the moved positions).
        (track.Keyframes[^1].X0 - track.Keyframes[0].X0).Should().BeApproximately(58.0 / W, 0.06);
    }

    [Fact]
    public void No_plate_produces_no_tracks()
    {
        byte[] pixels = Concat(Enumerable.Range(0, 20).Select(_ => MakeFrame(0, 0, 0, 0)));

        ChromaQuadTracker.Track(pixels, 20, W, H, Fps, DefaultOptions).Should().BeEmpty();
    }

    [Fact]
    public void Plate_smaller_than_MinAreaRatio_is_ignored()
    {
        // 2x2 = 4px of 14400 = 0.03% < default 0.4%.
        byte[] pixels = Concat(Enumerable.Range(0, 20).Select(_ => MakeFrame(60, 30, 2, 2)));

        ChromaQuadTracker.Track(pixels, 20, W, H, Fps, DefaultOptions).Should().BeEmpty();
    }

    [Fact]
    public void Track_shorter_than_MinTrackSeconds_is_dropped()
    {
        // Plate present for only 5 frames (0.5s at 10fps) of a 20-frame clip.
        byte[] pixels = Concat(Enumerable.Range(0, 20).Select(i =>
            i is >= 5 and < 10 ? MakeFrame(60, 30, 40, 30) : MakeFrame(0, 0, 0, 0)));

        ChromaQuadTracker.Track(pixels, 20, W, H, Fps, DefaultOptions).Should().BeEmpty();
    }

    [Fact]
    public void Two_temporally_separated_plates_become_two_tracks_in_chronological_order()
    {
        byte[] pixels = Concat(Enumerable.Range(0, 50).Select(i =>
            i < 15 ? MakeFrame(20, 20, 40, 30)
            : i < 30 ? MakeFrame(0, 0, 0, 0)
            : MakeFrame(90, 40, 40, 30)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(pixels, 50, W, H, Fps, DefaultOptions);

        tracks.Should().HaveCount(2);
        tracks[0].Id.Should().Be("r0");
        tracks[1].Id.Should().Be("r1");
        tracks[0].StartSec.Should().BeLessThan(tracks[1].StartSec);
    }

    [Fact]
    public void Short_detection_dropout_is_bridged_by_interpolation_not_split_into_two_tracks()
    {
        // Two dropout frames (<= MaxGapFrames=3) in the middle of an otherwise continuous plate.
        byte[] pixels = Concat(Enumerable.Range(0, 20).Select(i =>
            i is 9 or 10 ? MakeFrame(0, 0, 0, 0) : MakeFrame(60, 30, 40, 30)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(pixels, 20, W, H, Fps, DefaultOptions);

        tracks.Should().HaveCount(1, "a dropout within MaxGapFrames must be bridged, not split");
        tracks[0].EndSec.Should().BeApproximately(2.0, 1e-9);
        // The bridged frames exist in the keyframe list (interpolated), keeping it temporally dense.
        tracks[0].Keyframes.Count.Should().Be(20);
    }

    [Fact]
    public void Strongly_non_quadrilateral_blob_is_rejected_by_the_fill_ratio_gate()
    {
        // A thin L-shape: its extreme-point quad covers far more area than the component itself.
        byte[] frame = MakeFrame(20, 20, 60, 4);
        byte[] vertical = MakeFrame(20, 20, 4, 50);
        for (int i = 0; i < frame.Length; i += 3)
        {
            if (vertical[i + 1] == 220)
            {
                frame[i] = 30;
                frame[i + 1] = 220;
                frame[i + 2] = 40;
            }
        }

        ChromaQuadTracker.FrameQuad? quad = ChromaQuadTracker.DetectQuad(frame, 0, W, H, 0, DefaultOptions);

        quad.Should().BeNull("an L-shaped component must not be fitted as a quad plate");
    }

    [Fact]
    public void Blue_plate_is_tracked_when_ColorName_is_blue_and_ignored_under_green()
    {
        byte[] pixels = Concat(Enumerable.Range(0, 20).Select(_ =>
            MakeFrame(60, 30, 40, 30, color: ((byte)30, (byte)40, (byte)230))));

        ChromaQuadTracker.Track(pixels, 20, W, H, Fps, DefaultOptions).Should().BeEmpty();

        IReadOnlyList<VideoInsertRegionTrack> blueTracks = ChromaQuadTracker.Track(
            pixels, 20, W, H, Fps, DefaultOptions with { ColorName = "blue" });
        blueTracks.Should().HaveCount(1);
        blueTracks[0].ColorName.Should().Be("blue");
    }

    [Theory]
    [InlineData("green", "green")]
    [InlineData("Blue", "blue")]
    [InlineData("MAGENTA", "magenta")]
    [InlineData("chartreuse", "green")]
    [InlineData(null, "green")]
    public void NormalizeColorName_defaults_unknown_values_to_green(string? raw, string expected)
    {
        ChromaQuadTracker.NormalizeColorName(raw).Should().Be(expected);
    }

    // ---------------------------------------------------------------------
    // Multi-plate ("multipoint") tracking and silhouette curvature — see
    // docs/video-editing.md "Non-planar surfaces and multi-plate compositing".
    // ---------------------------------------------------------------------

    /// <summary>One RGB24 frame with TWO green rectangles — the simultaneous-plate case.</summary>
    private static byte[] MakeTwoPlateFrame(
        int aX, int aY, int aW, int aH, int bX, int bY, int bW, int bH)
    {
        byte[] frame = MakeFrame(aX, aY, aW, aH);
        for (int y = Math.Max(0, bY); y < Math.Min(H, bY + bH); y++)
        {
            for (int x = Math.Max(0, bX); x < Math.Min(W, bX + bW); x++)
            {
                int p = (y * W + x) * 3;
                frame[p] = 30;
                frame[p + 1] = 220;
                frame[p + 2] = 40;
            }
        }

        return frame;
    }

    [Fact]
    public void Two_SIMULTANEOUS_plates_collapse_to_one_track_at_the_default_cap_of_one()
    {
        // The documented v1 limitation, pinned so the default stays byte-identical: with
        // MaxPlatesPerFrame at 1 only the LARGER component is seen, every frame.
        byte[] pixels = Concat(Enumerable.Range(0, 25).Select(_ =>
            MakeTwoPlateFrame(15, 25, 44, 34, 100, 30, 36, 28)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(pixels, 25, W, H, Fps, DefaultOptions);

        tracks.Should().HaveCount(1);
        tracks[0].Keyframes[0].X0.Should().BeApproximately(15.5 / W, 0.03, "only the larger plate is tracked");
    }

    [Fact]
    public void Two_SIMULTANEOUS_plates_become_two_concurrent_tracks_when_the_cap_is_raised()
    {
        byte[] pixels = Concat(Enumerable.Range(0, 25).Select(_ =>
            MakeTwoPlateFrame(15, 25, 44, 34, 100, 30, 36, 28)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            pixels, 25, W, H, Fps, DefaultOptions with { MaxPlatesPerFrame = 4 });

        tracks.Should().HaveCount(2, "both plates clear the area and fill gates in every frame");
        tracks.Select(t => t.Id).Should().ContainInOrder("r0", "r1");

        // Both must span the WHOLE clip — the point of multi-plate tracking is that neither
        // plate's track is interrupted by the other's presence.
        tracks.Should().AllSatisfy(t =>
        {
            t.StartSec.Should().BeApproximately(0.0, 1e-9);
            t.EndSec.Should().BeApproximately(2.5, 1e-9);
            t.Confidence.Should().BeGreaterThan(0.75);
        });

        // Ordering is by first-frame centroid, so r0 is the left-hand plate.
        tracks[0].Keyframes[0].X0.Should().BeLessThan(tracks[1].Keyframes[0].X0);
    }

    [Fact]
    public void Two_plates_moving_in_opposite_directions_keep_their_own_identities()
    {
        // Association is by centroid proximity, so two plates that CROSS in x but never collide
        // must not swap tracks halfway through.
        byte[] pixels = Concat(Enumerable.Range(0, 30).Select(i =>
            MakeTwoPlateFrame(10 + i * 2, 10, 36, 28, 110 - i * 2, 50, 36, 28)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            pixels, 30, W, H, Fps, DefaultOptions with { MaxPlatesPerFrame = 4 });

        tracks.Should().HaveCount(2);
        tracks.Should().AllSatisfy(t => t.Keyframes.Should().HaveCount(30, "neither track drops a frame"));

        // The track that starts on the left ends on the right, and vice versa — proving the
        // identities followed the motion rather than being re-sorted every frame by position.
        VideoInsertRegionTrack leftStarter = tracks.OrderBy(t => t.Keyframes[0].X0).First();
        VideoInsertRegionTrack rightStarter = tracks.OrderBy(t => t.Keyframes[0].X0).Last();
        leftStarter.Keyframes[^1].X0.Should().BeGreaterThan(leftStarter.Keyframes[0].X0);
        rightStarter.Keyframes[^1].X0.Should().BeLessThan(rightStarter.Keyframes[0].X0);
    }

    [Fact]
    public void Curvature_is_not_measured_unless_it_is_asked_for()
    {
        byte[] pixels = Concat(Enumerable.Range(0, 20).Select(_ => MakeFrame(60, 30, 40, 30)));

        VideoInsertRegionTrack track = ChromaQuadTracker.Track(pixels, 20, W, H, Fps, DefaultOptions)[0];

        track.MeanCurvature.Should().Be(0);
        track.Keyframes.Should().AllSatisfy(k => k.Bows.Should().BeNull(
            "an unmeasured plate must serialize with no bows at all, so old and new artifacts read alike"));
    }

    [Fact]
    public void A_flat_plate_measures_flat_even_with_curvature_measurement_on()
    {
        byte[] pixels = Concat(Enumerable.Range(0, 20).Select(_ => MakeFrame(50, 20, 60, 50)));

        VideoInsertRegionTrack track = ChromaQuadTracker.Track(
            pixels, 20, W, H, Fps, DefaultOptions with { MeasureCurvature = true })[0];

        track.MeanCurvature.Should().BeLessThan(0.01,
            "a rectangle must stay below the threshold at which the compile step switches to a mesh");
    }

    [Fact]
    public void A_curved_plate_is_measured_as_curved_end_to_end()
    {
        // A green plate whose top and bottom edges bow inward — the silhouette a screen bent
        // about a vertical axis casts. Built by insetting each column by a parabolic amount.
        byte[] MakeCurvedFrame()
        {
            var frame = new byte[W * H * 3];
            for (int i = 0; i < W * H; i++)
            {
                frame[i * 3] = 100;
                frame[i * 3 + 1] = 100;
                frame[i * 3 + 2] = 100;
            }

            const int x0 = 40, x1 = 120, y0 = 20, y1 = 70;
            for (int x = x0; x <= x1; x++)
            {
                double t = (x - x0) / (double)(x1 - x0);
                int inset = (int)Math.Round(7 * 4 * t * (1 - t));
                for (int y = y0 + inset; y <= y1 - inset; y++)
                {
                    int p = (y * W + x) * 3;
                    frame[p] = 30;
                    frame[p + 1] = 220;
                    frame[p + 2] = 40;
                }
            }

            return frame;
        }

        byte[] pixels = Concat(Enumerable.Range(0, 20).Select(_ => MakeCurvedFrame()));

        VideoInsertRegionTrack track = ChromaQuadTracker.Track(
            pixels, 20, W, H, Fps, DefaultOptions with { MeasureCurvature = true })[0];

        track.MeanCurvature.Should().BeGreaterThan(0.05, "a 7px bow across a 50px-tall plate is ~14%");
        track.Keyframes.Should().AllSatisfy(k =>
        {
            k.Bows.Should().NotBeNull();
            k.Bows!.TopBow.Should().BeGreaterThan(0.05);
            k.Bows.BottomBow.Should().BeGreaterThan(0.05);
            Math.Abs(k.Bows.LeftBow).Should().BeLessThan(0.03, "the side edges of this plate are straight");
        });
    }

    // ---------------------------------------------------------------------
    // Surviving occlusion — see docs/video-editing.md "Surviving occlusion".
    // ---------------------------------------------------------------------

    /// <summary>A frame with the plate at a fixed place, optionally with a bite taken out of its left side.</summary>
    private static byte[] MakeOccludedFrame(int biteWidth)
    {
        byte[] frame = MakeFrame(40, 20, 60, 50);
        for (int y = 20; y < 70; y++)
        {
            for (int x = 40; x < 40 + biteWidth; x++)
            {
                int p = (y * W + x) * 3;
                frame[p] = 190;      // an opaque, distinctly non-green occluder
                frame[p + 1] = 150;
                frame[p + 2] = 130;
            }
        }

        return frame;
    }

    [Fact]
    public void A_plate_bitten_into_by_an_occluder_keeps_its_full_tracked_geometry()
    {
        // The failure this exists for: the detector still succeeds during an occlusion, it just
        // finds the REMAINING fragment, and fitting a quad to that collapses the tracked geometry
        // onto it. Composited, the insert shrinks and snaps back as the occluder passes.
        byte[] pixels = Concat(Enumerable.Range(0, 40).Select(i =>
            i is >= 18 and < 26 ? MakeOccludedFrame(22) : MakeOccludedFrame(0)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(pixels, 40, W, H, Fps, DefaultOptions);

        tracks.Should().HaveCount(1, "an occlusion must not split the track");
        VideoInsertRegionTrack track = tracks[0];
        track.StartSec.Should().BeApproximately(0.0, 1e-9);
        track.EndSec.Should().BeApproximately(4.0, 1e-9);

        // The left edge must not move: every keyframe should still describe the whole plate.
        double unoccludedLeft = track.Keyframes[2].X0;
        track.Keyframes.Should().AllSatisfy(k => k.X0.Should().BeApproximately(unoccludedLeft, 0.02,
            "the tracked quad must hold its geometry through the occlusion, not follow the fragment"));
    }

    [Fact]
    public void A_plate_that_genuinely_shrinks_is_followed_rather_than_treated_as_occluded()
    {
        // The other side of the same test: occlusion is an abrupt one-sided collapse, while a
        // plate moving away from camera shrinks gradually. The rolling baseline has to follow the
        // second without being fooled by the first, or a plate could never move in depth.
        byte[] pixels = Concat(Enumerable.Range(0, 40).Select(i =>
            MakeFrame(40, 20, 60 - i, 50 - (int)(i * 0.8))));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(pixels, 40, W, H, Fps, DefaultOptions);

        tracks.Should().HaveCount(1);
        VideoInsertRegionTrack track = tracks[0];
        track.EndSec.Should().BeApproximately(4.0, 1e-9, "a shrinking plate is still the same plate");
        (track.Keyframes[^1].X3 - track.Keyframes[^1].X0)
            .Should().BeLessThan(track.Keyframes[0].X3 - track.Keyframes[0].X0,
                "and the tracked quad must actually follow it inward");
    }

    [Fact]
    public void An_occlusion_that_never_lifts_is_eventually_accepted_as_the_new_truth()
    {
        // Holding geometry forever would be its own bug: past MaxOccludedFrames the smaller
        // reading is more likely to be what the plate now is than a very long occlusion.
        byte[] pixels = Concat(Enumerable.Range(0, 60).Select(i =>
            i < 10 ? MakeOccludedFrame(0) : MakeOccludedFrame(22)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            pixels, 60, W, H, Fps, DefaultOptions with { MaxOccludedFrames = 8 });

        tracks.Should().NotBeEmpty();
        tracks.Last().Keyframes[^1].X0.Should().BeGreaterThan(tracks[0].Keyframes[0].X0,
            "once the occlusion outlasts the hold, the narrower plate is accepted");
    }

    [Fact]
    public void MaxRegions_keeps_the_longest_tracks_re_sorted_chronologically()
    {
        // Three plates: 1.2s, 3s, 1.5s — MaxRegions=2 keeps the 3s and 1.5s ones, in time order.
        byte[] pixels = Concat(Enumerable.Range(0, 80).Select(i =>
            i < 12 ? MakeFrame(20, 20, 30, 20)
            : i is >= 20 and < 50 ? MakeFrame(60, 30, 30, 20)
            : i is >= 60 and < 75 ? MakeFrame(100, 50, 30, 20)
            : MakeFrame(0, 0, 0, 0)));

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            pixels, 80, W, H, Fps, DefaultOptions with { MaxRegions = 2 });

        tracks.Should().HaveCount(2);
        tracks[0].StartSec.Should().BeApproximately(2.0, 1e-9);
        tracks[1].StartSec.Should().BeApproximately(6.0, 1e-9);
        tracks.Select(t => t.Id).Should().ContainInOrder("r0", "r1");
    }

    // -----------------------------------------------------------------------
    // Regression: source aspect ratio (bug 1)
    //
    // The grid's sample dimensions are NOT the source's dimensions. Measuring physical distances
    // in grid space reported a portrait phone screen (true plate aspect ~0.47) as 1.46 — landscape
    // — to the motion-graphics agent, off by the grid/source aspect mismatch of (1920/1080)/(180/320)
    // = 3.16x. Two independent fixes: the grid now preserves the source aspect by default, and
    // QuadAspect takes the source's real dimensions explicitly.
    // -----------------------------------------------------------------------

    /// <summary>A grid of arbitrary dimensions with a filled rectangle — lets a test build a grid whose shape deliberately disagrees with the source's.</summary>
    private static byte[] MakeFrameOn(int gw, int gh, int rectX, int rectY, int rectW, int rectH)
    {
        var frame = new byte[gw * gh * 3];
        for (int i = 0; i < gw * gh; i++)
        {
            frame[i * 3] = 100;
            frame[i * 3 + 1] = 100;
            frame[i * 3 + 2] = 100;
        }

        for (int y = Math.Max(0, rectY); y < Math.Min(gh, rectY + rectH); y++)
        {
            for (int x = Math.Max(0, rectX); x < Math.Min(gw, rectX + rectW); x++)
            {
                int p = (y * gw + x) * 3;
                frame[p] = 30;
                frame[p + 1] = 220;
                frame[p + 2] = 40;
            }
        }

        return frame;
    }

    private static byte[] Repeat(byte[] frame, int count)
    {
        var buffer = new byte[frame.Length * count];
        for (int i = 0; i < count; i++)
            Buffer.BlockCopy(frame, 0, buffer, i * frame.Length, frame.Length);
        return buffer;
    }

    [Fact]
    public void Portrait_source_reports_the_plate_s_true_aspect_not_the_grid_s()
    {
        // A 1080x1920 portrait source. The plate covers half the frame's width and half its
        // height, so its TRUE aspect is (0.5 * 1080) / (0.5 * 1920) = 0.5625.
        const int Gw = 202, Gh = 360;   // what DeriveGridSize produces for 1080x1920
        byte[] pixels = Repeat(MakeFrameOn(Gw, Gh, Gw / 4, Gh / 4, Gw / 2, Gh / 2), 20);

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            pixels, 20, Gw, Gh, Fps, DefaultOptions, sourceWidth: 1080, sourceHeight: 1920);

        tracks.Should().HaveCount(1);
        tracks[0].MeanAspectRatio.Should().BeApproximately(1080.0 / 2 / (1920.0 / 2), 0.05,
            "the plate is half the frame in each dimension, so it inherits the source's own 9:16 aspect");
        tracks[0].MeanAspectRatio.Should().BeLessThan(1.0, "a portrait plate must never be reported as landscape");
    }

    [Fact]
    public void Landscape_source_reports_the_plate_s_true_aspect_not_the_grid_s()
    {
        // A 3840x2160 landscape source, whose plate is again half-by-half: true aspect 16/9.
        const int Gw = 320, Gh = 180;
        byte[] pixels = Repeat(MakeFrameOn(Gw, Gh, Gw / 4, Gh / 4, Gw / 2, Gh / 2), 20);

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            pixels, 20, Gw, Gh, Fps, DefaultOptions, sourceWidth: 3840, sourceHeight: 2160);

        tracks.Should().HaveCount(1);
        tracks[0].MeanAspectRatio.Should().BeApproximately(3840.0 / 2160, 0.15);
        tracks[0].MeanAspectRatio.Should().BeGreaterThan(1.0);
    }

    [Fact]
    public void A_grid_that_disagrees_with_the_source_still_reports_the_true_aspect()
    {
        // Defense in depth: a workflow author pins the OLD fixed 320x180 grid on a 1080x1920
        // portrait source. Grid space is squashed 10.7x vertically, so a grid-space measurement
        // reports 1.46 (landscape) for a plate that is really 0.56 (portrait) — the exact number
        // handed to MotionGraphicsPlanner in tonight's run. Supplying the source's real dimensions
        // is what keeps the answer right regardless of the grid.
        const int Gw = 320, Gh = 180;
        byte[] pixels = Repeat(MakeFrameOn(Gw, Gh, Gw / 4, Gh / 4, Gw / 2, Gh / 2), 20);

        IReadOnlyList<VideoInsertRegionTrack> gridSpace = ChromaQuadTracker.Track(
            pixels, 20, Gw, Gh, Fps, DefaultOptions);
        IReadOnlyList<VideoInsertRegionTrack> sourceSpace = ChromaQuadTracker.Track(
            pixels, 20, Gw, Gh, Fps, DefaultOptions, sourceWidth: 1080, sourceHeight: 1920);

        gridSpace.Should().HaveCount(1);
        sourceSpace.Should().HaveCount(1);
        gridSpace[0].MeanAspectRatio.Should().BeGreaterThan(1.0, "grid-space measurement is the bug being guarded against");
        sourceSpace[0].MeanAspectRatio.Should().BeApproximately(0.5625, 0.05);
    }

    [Theory]
    // 16:9 at the 480-wide auto base (was 320 — see ChromaQuadTracker.AutoGridBaseWidth).
    [InlineData(1920, 1080, 480, 270)]
    [InlineData(3840, 2160, 480, 270)]
    // Portrait hits the height ceiling and derives the width from it — 202x360 is precisely the
    // manual override that cut residual green fringe from 0.773% to 0.090% on real footage.
    [InlineData(1080, 1920, 202, 360)]
    [InlineData(720, 1280, 202, 360)]
    // Square hits the height ceiling; ultra-wide stays inside the clamps.
    [InlineData(1000, 1000, 360, 360)]
    [InlineData(3840, 1080, 480, 135)]
    public void DeriveGridSize_preserves_the_source_aspect_within_the_clamps(
        int sourceWidth, int sourceHeight, int expectedWidth, int expectedHeight)
    {
        (int w, int h) = ChromaQuadTracker.DeriveGridSize(sourceWidth, sourceHeight, 0, 0);

        w.Should().Be(expectedWidth);
        h.Should().Be(expectedHeight);
        (w / (double)h).Should().BeApproximately(sourceWidth / (double)sourceHeight, 0.02 * (sourceWidth / (double)sourceHeight));
    }

    [Fact]
    public void DeriveGridSize_honours_an_explicit_override_and_clamps_it()
    {
        ChromaQuadTracker.DeriveGridSize(1080, 1920, 256, 144).Should().Be((256, 144));
        ChromaQuadTracker.DeriveGridSize(1080, 1920, 4096, 4096).Should().Be((640, 360));
        ChromaQuadTracker.DeriveGridSize(1080, 1920, 1, 1).Should().Be((64, 36));
        // Only a COMPLETE positive pair counts as an override; a half-set pair falls back to auto.
        ChromaQuadTracker.DeriveGridSize(1080, 1920, 256, 0).Should().Be((202, 360));
    }

    [Fact]
    public void DeriveGridSize_falls_back_to_16_9_when_the_source_could_not_be_probed()
    {
        ChromaQuadTracker.DeriveGridSize(0, 0, 0, 0).Should().Be((480, 270));
    }

    // -----------------------------------------------------------------------
    // Regression: confidence is blind to under-segmentation (bug 2)
    //
    // On real footage a dim, unevenly-lit plate was detected at 66% of its true extent and still
    // reported confidence 0.9996 — MinInsertConfidence could not catch it, because every term in
    // the score was computed against what the strict mask happened to find. Two changes: the mask
    // now uses dual-threshold hysteresis so the dim part of a plate is recovered at all, and the
    // score gained a term (EdgeContrast) that sees an edge cut through the plate's own interior.
    // -----------------------------------------------------------------------

    /// <summary>
    /// A filled quad rotated by <paramref name="degrees"/> with an optional linear brightness
    /// gradient across it — the synthetic stand-in for a real screen lit unevenly.
    /// <paramref name="dimFactor"/> is the brightness at the dim edge, 1.0 at the bright one, and
    /// <paramref name="peakGreen"/> is the target channel's value at the bright edge.
    ///
    /// <para>
    /// The defaults model the REAL failing plate rather than an idealized bright one: a 4096x2160
    /// laptop panel whose green channel measured 28..88 across the SAME physical screen. That
    /// range straddles the strict chroma floor of 60, which is precisely why a flat threshold
    /// sliced the plate in half. A synthetic plate at g=210 cannot reproduce the failure at all —
    /// almost none of it falls below 60 no matter how steep the gradient.
    /// </para>
    /// </summary>
    private static byte[] MakeUnevenQuadFrame(
        int gw, int gh, double cx, double cy, double side, double degrees, double dimFactor, byte peakGreen = 210)
    {
        var frame = new byte[gw * gh * 3];
        for (int i = 0; i < gw * gh; i++)
        {
            frame[i * 3] = 90;
            frame[i * 3 + 1] = 90;
            frame[i * 3 + 2] = 95;
        }

        double th = degrees * Math.PI / 180.0;
        double ct = Math.Cos(th), st = Math.Sin(th);
        for (int y = 0; y < gh; y++)
        {
            for (int x = 0; x < gw; x++)
            {
                double dx = x - cx, dy = y - cy;
                double u = dx * ct + dy * st;
                double v = -dx * st + dy * ct;
                if (Math.Abs(u) > side / 2 || Math.Abs(v) > side / 2)
                    continue;

                double t = (u + side / 2) / side;               // 0 at the dim edge, 1 at the bright one
                double level = dimFactor + (1.0 - dimFactor) * t;
                int p = (y * gw + x) * 3;
                frame[p] = (byte)(peakGreen * 0.22 * level);
                frame[p + 1] = (byte)(peakGreen * level);
                frame[p + 2] = (byte)(peakGreen * 0.33 * level);
            }
        }

        return frame;
    }

    [Fact]
    public void Unevenly_lit_misaligned_plate_is_detected_in_full_and_scored_below_a_clean_plate()
    {
        // The real failure class: a dim plate whose own brightness gradient straddles the strict
        // chroma floor, and which is NOT axis-aligned (so the extreme-point fit has no easy time
        // of it either). Green channel runs 40..90 across the plate — the same shape as the real
        // laptop panel's measured 28..88 — so roughly 40% of it sits below the old flat g>=60 cut.
        // The control is the identical plate lit evenly at the same peak brightness.
        const int Gw = 320, Gh = 180;
        byte[] uneven = Repeat(MakeUnevenQuadFrame(Gw, Gh, 160, 90, 100, 17, 0.45, peakGreen: 90), 20);
        byte[] clean = Repeat(MakeUnevenQuadFrame(Gw, Gh, 160, 90, 100, 17, 1.0, peakGreen: 90), 20);

        IReadOnlyList<VideoInsertRegionTrack> unevenTracks = ChromaQuadTracker.Track(uneven, 20, Gw, Gh, Fps, DefaultOptions);
        IReadOnlyList<VideoInsertRegionTrack> cleanTracks = ChromaQuadTracker.Track(clean, 20, Gw, Gh, Fps, DefaultOptions);

        unevenTracks.Should().HaveCount(1);
        cleanTracks.Should().HaveCount(1);

        // (a) The plate is now detected at its TRUE extent — the whole 100x100 quad, not the
        //     brighter fragment of it. MeanAreaRatio is the direct measure of how much got found.
        double trueAreaRatio = 100.0 * 100 / (Gw * Gh);
        unevenTracks[0].MeanAreaRatio.Should().BeApproximately(trueAreaRatio, trueAreaRatio * 0.1,
            "hysteresis growth recovers the dim half of the plate instead of cutting it off");
        unevenTracks[0].MeanAreaRatio.Should().BeApproximately(cleanTracks[0].MeanAreaRatio, trueAreaRatio * 0.1,
            "an unevenly-lit plate and an evenly-lit one of the same size must measure the same size");

        // (b) Confidence says whether the fit is RIGHT, not how hard it was to get: the dim plate,
        //     recovered in full, is not docked for needing growth (it used to be — via the old
        //     chroma-margin term — however correct the fit). The same plate with growth pinned
        //     OFF stops short inside its own dim half, and THAT fit is what must score low: its
        //     cut edge has plate on both sides, which the edge-contrast term sees.
        unevenTracks[0].Confidence.Should().BeGreaterThan(0.7,
            "a correctly recovered dim plate is a correct fit");

        IReadOnlyList<VideoInsertRegionTrack> underGrown = ChromaQuadTracker.Track(
            uneven, 20, Gw, Gh, Fps, DefaultOptions with { GrowExcessFraction = 1.5 });
        underGrown.Should().HaveCount(1);
        underGrown[0].MeanAreaRatio.Should().BeLessThan(trueAreaRatio * 0.9, "the strict core alone misses the dim half");
        underGrown[0].Confidence.Should().BeLessThan(unevenTracks[0].Confidence - 0.1,
            "an edge cut through the middle of the plate has no contrast across it");
    }

    [Fact]
    public void A_45_degree_plate_is_tracked_as_a_full_quad_at_high_confidence()
    {
        // A 45-degree plate used to collapse the extreme-point fit: min(x+y) and max(x-y) became
        // the SAME pixel, so the "quad" was a triangle roughly half the component's area (fill
        // ~2.0), and the only defence was to score it low (the old Math.Min(fillRatio, 1.0) had
        // scored it PERFECT). The corners are now taken in the plate's own measured frame
        // (ChromaQuadOrientation), so the same plate is a quad bounding its whole component.
        const int Gw = 320, Gh = 180;
        byte[] rotated = Repeat(MakeUnevenQuadFrame(Gw, Gh, 160, 90, 100, 45, 1.0), 20);

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(rotated, 20, Gw, Gh, Fps, DefaultOptions);

        tracks.Should().ContainSingle();
        tracks[0].Confidence.Should().BeGreaterThan(0.85, "a quad on the plate's own axes bounds the whole component");
        tracks[0].MeanAreaRatio.Should().BeApproximately(100.0 * 100 / (Gw * Gh), 0.004, "the component is the full 100x100 plate");
    }

    [Fact]
    public void Hysteresis_growth_cannot_jump_to_a_disconnected_green_region()
    {
        // The safety property that makes a relaxed second threshold acceptable on a scene that is
        // itself green-ish: growth is 4-connected to the high-confidence seed, so a separate dim
        // green blob elsewhere in the frame is never absorbed into the plate's component.
        const int Gw = 320, Gh = 180;
        var frame = MakeUnevenQuadFrame(Gw, Gh, 100, 90, 60, 0, 1.0);

        // A dim green blob far from the plate, bright enough for the grow mask but not the core.
        for (int y = 40; y < 80; y++)
        {
            for (int x = 240; x < 300; x++)
            {
                int p = (y * Gw + x) * 3;
                frame[p] = 8;
                frame[p + 1] = 40;
                frame[p + 2] = 10;
            }
        }

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(Repeat(frame, 20), 20, Gw, Gh, Fps, DefaultOptions);

        tracks.Should().HaveCount(1);
        VideoInsertQuadKeyframe k = tracks[0].Keyframes[tracks[0].Keyframes.Count / 2];
        // The plate spans x in [70, 130]; the disconnected blob starts at x=240 (0.75 normalized).
        // If growth had leaked, the right-hand corners would be dragged well past the plate.
        k.X1.Should().BeLessThan(0.45, "TR must stay on the plate, not stretch toward the disconnected blob");
        k.X3.Should().BeLessThan(0.45, "BR must stay on the plate, not stretch toward the disconnected blob");
        tracks[0].MeanAreaRatio.Should().BeApproximately(60.0 * 60 / (Gw * Gh), 0.01);
    }

    // -----------------------------------------------------------------------
    // Regression: hysteresis growth flooded a bright plate into a lawn behind it
    //
    // The handheld phone clip (greenscreen-phone.mp4): plate (0, 230, 0), lawn (70, 93, 26). The
    // lawn passes the permissive grow mask, and where the coarse grid thinned the bezel away the
    // plate grew into the grass — corners jumped onto the lawn on about half the frames, the
    // insert swung around a barely-moving phone at confidence 0.71, and the leaked segmentation
    // left the chroma-key solver nothing to agree with, so no silhouette matte was applied.
    // -----------------------------------------------------------------------

    /// <summary>A saturated plate sitting directly on a dull-green "lawn" band with no bezel between them, the lawn's top edge flickering frame to frame like real foliage.</summary>
    private static byte[] MakePlateOnLawnClip(int gw, int gh, int frames)
    {
        var clip = new byte[gw * gh * 3 * frames];
        for (int f = 0; f < frames; f++)
        {
            int lawnTop = 120 + (f % 3) * 4;
            for (int y = 0; y < gh; y++)
            {
                for (int x = 0; x < gw; x++)
                {
                    int p = f * gw * gh * 3 + (y * gw + x) * 3;
                    bool plate = x >= 120 && x < 200 && y >= 40 && y < 120;
                    bool lawn = y >= lawnTop;
                    (byte r, byte g, byte b) = plate ? ((byte)0, (byte)230, (byte)0)
                        : lawn ? ((byte)70, (byte)93, (byte)26)
                        : ((byte)150, (byte)150, (byte)155);
                    clip[p] = r;
                    clip[p + 1] = g;
                    clip[p + 2] = b;
                }
            }
        }

        return clip;
    }

    [Fact]
    public void Growth_stays_on_a_saturated_plate_that_touches_dull_green_scenery()
    {
        const int Gw = 320, Gh = 180, Frames = 24;
        byte[] clip = MakePlateOnLawnClip(Gw, Gh, Frames);

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(clip, Frames, Gw, Gh, Fps, DefaultOptions);

        tracks.Should().HaveCount(1);
        VideoInsertRegionTrack t = tracks[0];
        t.MeanAreaRatio.Should().BeApproximately(80.0 * 80 / (Gw * Gh), 0.01, "the lawn is not plate");
        foreach (VideoInsertQuadKeyframe k in t.Keyframes)
        {
            k.Y2.Should().BeLessThan(0.70, "BL must stay on the plate's bottom edge, not drop into the lawn");
            k.Y3.Should().BeLessThan(0.70, "BR must stay on the plate's bottom edge, not drop into the lawn");
        }

        t.Confidence.Should().BeGreaterThan(0.6);
        t.Tuning.Should().NotBeNull("the growth bar is searched per source unless pinned");
        t.Tuning!.Parameters[ChromaTrackerTuner.GrowExcessFractionKey].Should().BeGreaterThan(0.2);
        t.Tuning.Candidates.Should().HaveCount(ChromaTrackerTuner.GrowCandidates.Length);
    }

    [Fact]
    public void Unrestricted_growth_into_scenery_is_scored_below_the_insert_floor()
    {
        // What the old tracker did, reproduced by pinning the purity bar to zero: the plate floods
        // into the flickering lawn. Confidence must now say so — the far edges sit in grass on
        // both sides and jump frame to frame — instead of reporting a composite-worthy score.
        const int Gw = 320, Gh = 180, Frames = 24;
        byte[] clip = MakePlateOnLawnClip(Gw, Gh, Frames);

        // The rigid-pose repair is off here: it would put the quad back on the plate (next test), and
        // this one is about how a fit that DID leak is scored.
        IReadOnlyList<VideoInsertRegionTrack> leaked = ChromaQuadTracker.Track(
            clip, Frames, Gw, Gh, Fps, DefaultOptions with { GrowExcessFraction = 0.0, SolveRigidPose = false });

        // Either refused outright (the fill gate) or scored under the compile step's floor —
        // never offered as a usable region.
        leaked.Should().OnlyContain(t => t.Confidence < 0.5, "VideoCompileStepConfig.MinInsertConfidence defaults to 0.5");
    }

    [Fact]
    public void A_detection_that_leaked_into_scenery_is_put_back_on_the_plate_by_the_rigid_pose()
    {
        // The same flood, with the repair on: the plate's own edges are where its colour ends, the
        // lawn is a tenth as green, and the rigid model is fitted to those edges, not to the
        // component. What is offered is the plate, so it may be offered.
        const int Gw = 320, Gh = 180, Frames = 24;
        byte[] clip = MakePlateOnLawnClip(Gw, Gh, Frames);

        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(
            clip, Frames, Gw, Gh, Fps, DefaultOptions with { GrowExcessFraction = 0.0 });

        tracks.Should().Contain(t => t.Confidence >= 0.5, "the repaired plate must be offered, or the loop below proves nothing");
        foreach (VideoInsertRegionTrack t in tracks.Where(t => t.Confidence >= 0.5))
        {
            foreach (VideoInsertQuadKeyframe k in t.Keyframes)
            {
                // The plate is x 120..200, y 40..120 on the grid (edges at 119.5, 199.5, 39.5, 119.5).
                (k.X0 * Gw - 0.5).Should().BeApproximately(119.5, 1.5);
                (k.Y0 * Gh - 0.5).Should().BeApproximately(39.5, 1.5);
                (k.X3 * Gw - 0.5).Should().BeApproximately(199.5, 1.5);
                (k.Y3 * Gh - 0.5).Should().BeApproximately(119.5, 1.5, "the bottom edge is the plate's, not the lawn's");
            }
        }
    }

    [Fact]
    public void Motion_class_reads_grid_jitter_on_a_still_plate_as_static()
    {
        // One-grid-pixel corner flicker at 12 fps summed frame to frame "travels" twice the
        // Moving bar; a phone held almost still was offered to the planner as Moving.
        const int Gw = 320, Gh = 180, Frames = 24;
        var frames = new List<byte[]>();
        for (int f = 0; f < Frames; f++)
            frames.Add(MakeUnevenQuadFrame(Gw, Gh, 160 + (f % 2), 90, 80, 0, 1.0));

        byte[] clip = frames.SelectMany(b => b).ToArray();
        IReadOnlyList<VideoInsertRegionTrack> tracks = ChromaQuadTracker.Track(clip, Frames, Gw, Gh, 12.0, DefaultOptions);

        tracks.Should().HaveCount(1);
        tracks[0].MotionClass.Should().Be("Static");
    }
}
