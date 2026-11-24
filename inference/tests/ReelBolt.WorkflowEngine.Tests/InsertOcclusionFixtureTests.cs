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
/// The tracked insert on the two committed cat clips (a monitor on a desk, a cat walking in front
/// of it): the cat covers the screen's lower corners and its bottom edge for most of each clip. The
/// monitor does not move, so every emitted corner must stay where it is; before the rigid-pose
/// repair the lower corners rode up to 168 px on the cat's back in one clip, and the other was
/// tracked for 27 of its 158 frames. See docs/video-editing.md "Occluded corners and edges".
/// </summary>
public class InsertOcclusionFixtureTests
{
    private const int Width = 768, Height = 1344;
    private const double Fps = 24;

    private static string Clip(string name) => Path.Combine(
        AppContext.BaseDirectory, "TestData", "prompted-region-effects", "sam31-rle", "clips", name);

    private static IReadOnlyList<VideoInsertRegionTrack> Track(string clip, bool rigid, out int frames)
    {
        var cfg = new VideoAnalyzeStepConfig(1, new VideoSourceRef(VideoSourceKind.ProjectFile));
        (int gw, int gh) = ChromaQuadTracker.DeriveGridSize(Width, Height, cfg.InsertGridWidth, cfg.InsertGridHeight);
        string raw = Path.Combine(Path.GetTempPath(), "insert-occlusion-" + Guid.NewGuid().ToString("N") + ".rgb");
        byte[] grid;
        try
        {
            var psi = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true };
            foreach (string a in FfmpegArgvBuilder.BuildGridSampleArgs(Clip(clip), raw, Fps, gw, gh, 0, GridChroma.FullChroma))
                psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            p.ExitCode.Should().Be(0, err);
            grid = File.ReadAllBytes(raw);
        }
        finally
        {
            File.Delete(raw);
        }

        frames = grid.Length / (gw * gh * 3);
        var options = new ChromaQuadTracker.Options(
            ColorName: cfg.InsertRegionColor,
            MinAreaRatio: cfg.MinInsertRegionAreaRatio,
            MinTrackSeconds: cfg.MinInsertRegionSeconds,
            MaxRegions: cfg.MaxInsertRegions,
            MaxPlatesPerFrame: cfg.MaxInsertPlatesPerFrame,
            MeasureCurvature: cfg.MeasureInsertCurvature,
            SolveChromaKey: cfg.SolveInsertChromaKey,
            SolveRigidPose: rigid);
        return ChromaQuadTracker.Track(grid, frames, gw, gh, Fps, options, Width, Height);
    }

    /// <summary>The furthest any corner strays from its own median position over the track, in source pixels.</summary>
    private static double WorstWander(VideoInsertRegionTrack t)
    {
        double worst = 0;
        Func<VideoInsertQuadKeyframe, (double X, double Y)>[] corners =
            [k => (k.X0, k.Y0), k => (k.X1, k.Y1), k => (k.X2, k.Y2), k => (k.X3, k.Y3)];
        foreach (Func<VideoInsertQuadKeyframe, (double X, double Y)> corner in corners)
        {
            List<double> xs = [.. t.Keyframes.Select(k => corner(k).X * Width).OrderBy(v => v)];
            List<double> ys = [.. t.Keyframes.Select(k => corner(k).Y * Height).OrderBy(v => v)];
            double mx = xs[xs.Count / 2], my = ys[ys.Count / 2];
            foreach (VideoInsertQuadKeyframe k in t.Keyframes)
                worst = Math.Max(worst, Math.Sqrt(Math.Pow(corner(k).X * Width - mx, 2) + Math.Pow(corner(k).Y * Height - my, 2)));
        }

        return worst;
    }

    [FfmpegTheory]
    [InlineData("cat-tabby.mp4")]
    [InlineData("cat-turn-a.mp4")]
    public void The_screen_stays_put_while_the_cat_walks_in_front_of_it(string clip)
    {
        IReadOnlyList<VideoInsertRegionTrack> tracks = Track(clip, rigid: true, out int frames);

        tracks.Should().HaveCount(1, "it is one screen, however often something covers it");
        VideoInsertRegionTrack t = tracks[0];
        t.StartSec.Should().BeLessThan(1.5 / Fps);
        t.EndSec.Should().BeGreaterThan((frames - 1.5) / Fps, "the screen is in view for the whole clip");
        t.Confidence.Should().BeGreaterThan(0.8, "a track repaired from the plate's own edges is not priced by the frames where the cat hid them");
        WorstWander(t).Should().BeLessThan(10, "the monitor does not move");
    }

    [FfmpegFact]
    public void Without_the_rigid_pose_the_lower_corners_follow_the_cat()
    {
        // What the repair is for, pinned so the fixture keeps biting: the same clip, the same tracker,
        // the repair switched off.
        IReadOnlyList<VideoInsertRegionTrack> tracks = Track("cat-tabby.mp4", rigid: false, out _);

        tracks.Should().NotBeEmpty();
        WorstWander(tracks[0]).Should().BeGreaterThan(60);
    }
}
