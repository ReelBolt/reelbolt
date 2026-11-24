using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Services.Video.Tracking;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Markerless planar tracking (docs/video-editing.md "Tracking objects") against synthetic clips
/// whose ground truth is known exactly: a textured patch moving over a DIFFERENTLY textured,
/// independently moving background — the tracker must follow the patch, not the scene.
/// </summary>
public class PlanarTrackerTests
{
    private const int W = 320, H = 180;

    /// <summary>Deterministic value noise in 0..255, smooth enough for gradients to exist.</summary>
    private static double Noise(int seed, double x, double y)
    {
        static double Hash(int s, int ix, int iy)
        {
            unchecked
            {
                int h = s * 374761393 + ix * 668265263 + iy * 2147483647;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0;
            }
        }

        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        double a = Hash(seed, x0, y0), b = Hash(seed, x0 + 1, y0), c = Hash(seed, x0, y0 + 1), d = Hash(seed, x0 + 1, y0 + 1);
        return 255 * ((a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy);
    }

    /// <summary>
    /// A 60x30 patch whose top-left moves along (40 + 3f, 70 + sin(f/5)*6) and slowly grows,
    /// over a background panning the other way. Returns the frames and the ground-truth corners.
    /// </summary>
    private static (GrayFrames Frames, (double X, double Y)[][] Truth) Clip(int frames)
    {
        var data = new byte[W * H * frames];
        var truth = new (double X, double Y)[frames][];
        for (int f = 0; f < frames; f++)
        {
            double px = 40 + 3 * f, py = 70 + Math.Sin(f / 5.0) * 6, scale = 1 + 0.004 * f;
            double pw = 60 * scale, ph = 30 * scale;
            truth[f] = [(px, py), (px + pw, py), (px + pw, py + ph), (px, py + ph)];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    double v;
                    if (x >= px && x < px + pw && y >= py && y < py + ph)
                    {
                        double u = (x - px) / scale, w = (y - py) / scale;
                        v = Noise(7, u / 3.0, w / 3.0);
                    }
                    else
                    {
                        v = 0.5 * Noise(99, (x + 2.0 * f) / 6.0, y / 6.0);
                    }

                    data[f * W * H + y * W + x] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        }

        return (new GrayFrames(data, W, H, frames, 15), truth);
    }

    private static double MaxCornerError((double X, double Y)[] a, (double X, double Y)[] b) =>
        Enumerable.Range(0, 4).Max(i => Math.Sqrt(Math.Pow(a[i].X - b[i].X, 2) + Math.Pow(a[i].Y - b[i].Y, 2)));

    [Fact]
    public void Follows_a_moving_growing_patch_over_a_counter_moving_background()
    {
        (GrayFrames frames, var truth) = Clip(30);

        List<TrackedQuad> track = PlanarTracker.Track(frames, 0, truth[0], new PlanarTrackerOptions());

        track.Should().HaveCount(30, "the patch never leaves the frame or gets occluded");
        foreach (TrackedQuad q in track)
            MaxCornerError(q.Corners, truth[q.FrameIndex]).Should().BeLessThan(2.5, $"frame {q.FrameIndex} must stay on the patch");
    }

    [Fact]
    public void Tracks_backward_from_a_mid_clip_seed()
    {
        (GrayFrames frames, var truth) = Clip(30);

        List<TrackedQuad> track = PlanarTracker.Track(frames, 15, truth[15], new PlanarTrackerOptions());

        track.First().FrameIndex.Should().Be(0);
        track.Last().FrameIndex.Should().Be(29);
        MaxCornerError(track.First().Corners, truth[0]).Should().BeLessThan(2.5);
    }

    [Fact]
    public void Ends_the_track_when_the_target_disappears_instead_of_sliding_onto_the_background()
    {
        (GrayFrames frames, var truth) = Clip(30);
        // Blank the patch out from frame 20 on: the tracker must stop, not keep reporting a quad.
        var data = new byte[W * H * 30];
        for (int f = 0; f < 30; f++)
        {
            GrayImage img = frames.Image(f);
            for (int i = 0; i < W * H; i++)
                data[f * W * H + i] = (byte)img.Pixels[i];
            if (f >= 20)
            {
                foreach (int y in Enumerable.Range(0, H))
                    foreach (int x in Enumerable.Range(0, W))
                        if (x >= truth[f][0].X - 2 && x <= truth[f][1].X + 2 && y >= truth[f][0].Y - 2 && y <= truth[f][2].Y + 2)
                            data[f * W * H + y * W + x] = 40;
            }
        }

        List<TrackedQuad> track = PlanarTracker.Track(new GrayFrames(data, W, H, 30, 15), 0, truth[0], new PlanarTrackerOptions());

        track.Last().FrameIndex.Should().BeLessThan(20 + new PlanarTrackerOptions().MaxLostFrames + 1);
    }

    [Fact]
    public void Tuner_scores_every_candidate_and_reports_the_winner_in_words()
    {
        (GrayFrames frames, var truth) = Clip(24);

        (PlanarTrackerOptions options, var tuning) = PlanarTrackerTuner.Tune(frames, 0, truth[0], new PlanarTrackerOptions());

        tuning.Candidates.Should().HaveCount(
            PlanarTrackerTuner.Models.Length * PlanarTrackerTuner.WindowRadii.Length * PlanarTrackerTuner.PyramidDepths.Length);
        tuning.Score.Should().BeGreaterThan(0.5, "a clean textured patch round-trips almost exactly");
        tuning.Summary.Should().Contain(options.Model.ToString().ToLowerInvariant());
    }

    [Fact]
    public void Homography_fit_recovers_an_exact_perspective_map()
    {
        var truth = new Transform2D(1.1, 0.05, 10, -0.03, 0.95, 4, 0.0004, -0.0002, 1);
        var src = new List<(double X, double Y)> { (0, 0), (100, 0), (100, 50), (0, 50), (50, 25), (20, 40) };
        var dst = src.Select(p => truth.Apply(p.X, p.Y)).ToList();

        Transform2D? fit = Geometry.Fit(src, dst, TrackMotionModel.Homography);

        fit.Should().NotBeNull();
        foreach ((double X, double Y) p in src)
        {
            (double ex, double ey) = truth.Apply(p.X, p.Y);
            (double ax, double ay) = fit!.Value.Apply(p.X, p.Y);
            Math.Abs(ex - ax).Should().BeLessThan(1e-6);
            Math.Abs(ey - ay).Should().BeLessThan(1e-6);
        }
    }

    [Fact]
    public void Ransac_ignores_outliers()
    {
        var truth = new Transform2D(1, 0, 12, 0, 1, -5, 0, 0, 1);
        var src = Enumerable.Range(0, 40).Select(i => ((double)(i % 8) * 10, (double)(i / 8) * 10)).ToList();
        var dst = src.Select((p, i) => i % 5 == 0 ? (p.Item1 + 40, p.Item2 - 30) : truth.Apply(p.Item1, p.Item2)).ToList();

        var result = Geometry.EstimateRansac(src, dst, TrackMotionModel.Similarity, 1.0, 10);

        result.Should().NotBeNull();
        result!.Value.InlierCount.Should().Be(32);
        (double x, double y) = result.Value.Transform.Apply(0, 0);
        x.Should().BeApproximately(12, 1e-6);
        y.Should().BeApproximately(-5, 1e-6);
    }

    [Fact]
    public void Inverse_undoes_a_transform()
    {
        var h = new Transform2D(1.2, 0.1, 5, -0.2, 0.9, 3, 0.001, 0.0005, 1);
        Transform2D inv = PlanarTracker.Invert(h)!.Value;
        (double x, double y) = h.Apply(30, 40);
        (double bx, double by) = inv.Apply(x, y);
        bx.Should().BeApproximately(30, 1e-6);
        by.Should().BeApproximately(40, 1e-6);
    }
}
