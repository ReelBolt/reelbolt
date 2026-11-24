using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// VideoAnalyze <c>SceneDetection: Sensitive</c> (docs/video-editing.md "Finding cuts in finished films"):
/// the luma-jump detector that finds a hard cut between two dark shots, which the scene score misses.
/// </summary>
public class SceneCutFinderTests
{
    private static List<LumaFrame> Frames(params (double Avg, double Low, double High)[] frames) =>
        frames.Select((f, i) => new LumaFrame(i * 0.04, f.Avg, f.Low, f.High)).ToList();

    private static IEnumerable<(double, double, double)> Run(int count, double avg) =>
        Enumerable.Range(0, count).Select(_ => (avg, avg - 7, avg + 7));

    [Fact]
    public void BuildArgs_writes_each_luma_statistic_to_its_own_session_file_and_reads_no_audio()
    {
        // One file per statistic: two metadata=print filters sharing a file= target overwrite each other,
        // and the log itself is a bounded tail (T-211 item 7).
        SceneCutFinder.BuildArgs("/scratch/in.mp4", 4, ["/scratch/yavg.txt", "/scratch/ylow.txt", "/scratch/yhigh.txt"])
            .Should().Equal(
                "-nostdin", "-hide_banner", "-y",
                "-loglevel", "info",
                "-protocol_whitelist", "file",
                "-format_whitelist", UserMediaInputFormats.Whitelist,
                "-threads", "4",
                "-i", "/scratch/in.mp4",
                "-an",
                "-vf",
                "signalstats," +
                "metadata=print:key=lavfi.signalstats.YAVG:file='/scratch/yavg.txt'," +
                "metadata=print:key=lavfi.signalstats.YLOW:file='/scratch/ylow.txt'," +
                "metadata=print:key=lavfi.signalstats.YHIGH:file='/scratch/yhigh.txt'",
                "-f", "null", "-");
    }

    [Fact]
    public void BuildArgs_refuses_a_statistics_file_list_that_does_not_match_the_three_keys()
    {
        Action build = () => SceneCutFinder.BuildArgs("/scratch/in.mp4", 4, ["/scratch/one.txt"]);

        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseFrames_joins_the_three_statistics_files_by_frame()
    {
        const string yavg =
            "frame:0    pts:0       pts_time:0\n" +
            "lavfi.signalstats.YAVG=30.8384\n" +
            "frame:1    pts:1024    pts_time:0.04\n" +
            "lavfi.signalstats.YAVG=31\n";
        const string ylow =
            "frame:0    pts:0       pts_time:0\n" +
            "lavfi.signalstats.YLOW=24\n" +
            "frame:1    pts:1024    pts_time:0.04\n" +
            "lavfi.signalstats.YLOW=25\n";
        const string yhigh =
            "frame:0    pts:0       pts_time:0\n" +
            "lavfi.signalstats.YHIGH=38\n" +
            "frame:1    pts:1024    pts_time:0.04\n" +
            "lavfi.signalstats.YHIGH=39\n" +
            "frame=    2 fps=0.0 q=-0.0 size=N/A\n";

        IReadOnlyList<LumaFrame> frames = SceneCutFinder.ParseFrames(yavg, ylow, yhigh);

        frames.Should().Equal(new LumaFrame(0, 30.8384, 24, 38), new LumaFrame(0.04, 31, 25, 39));
    }

    [Fact]
    public void ParseFrames_skips_a_frame_that_is_missing_from_one_of_the_three_files()
    {
        const string yavg = "frame:0 pts:0 pts_time:0\nlavfi.signalstats.YAVG=20\nframe:1 pts:1 pts_time:0.04\nlavfi.signalstats.YAVG=40\n";
        const string ylow = "frame:0 pts:0 pts_time:0\nlavfi.signalstats.YLOW=18\nframe:1 pts:1 pts_time:0.04\nlavfi.signalstats.YLOW=38\n";
        const string yhigh = "frame:0 pts:0 pts_time:0\nlavfi.signalstats.YHIGH=22\n";

        IReadOnlyList<LumaFrame> frames = SceneCutFinder.ParseFrames(yavg, ylow, yhigh);

        frames.Should().Equal(new LumaFrame(0, 20, 18, 22));
    }

    [Fact]
    public void FindCuts_finds_a_hard_cut_between_two_dark_levels()
    {
        List<LumaFrame> frames = Frames([.. Run(10, 30), .. Run(10, 46)]);

        SceneCutFinder.FindCuts(frames).Should().Equal(0.4);
    }

    [Fact]
    public void FindCuts_ignores_noise_inside_one_dark_shot()
    {
        List<LumaFrame> frames = Frames(Enumerable.Range(0, 30)
            .Select(i => (30 + (i % 2 == 0 ? 0.4 : -0.4), 23.0, 37.0)).ToArray());

        SceneCutFinder.FindCuts(frames).Should().BeEmpty();
    }

    [Fact]
    public void FindCuts_never_splits_on_a_fade_because_the_jump_is_spread_over_many_frames()
    {
        // 30 -> 90 over 10 frames with a narrow spread: every frame moves by 6 (a big normalised jump),
        // but none stands out from its neighbours.
        List<LumaFrame> frames = Frames([
            .. Enumerable.Repeat((30.0, 29.0, 31.0), 10),
            .. Enumerable.Range(1, 10).Select(i => (30.0 + 6 * i, 29.0 + 6 * i, 31.0 + 6 * i)),
            .. Enumerable.Repeat((90.0, 89.0, 91.0), 10)]);

        SceneCutFinder.FindCuts(frames).Should().BeEmpty();
    }

    [Fact]
    public void FindCuts_ignores_a_single_frame_flash_that_jumps_out_and_straight_back()
    {
        List<LumaFrame> frames = Frames([.. Run(10, 30), (50.0, 43.0, 57.0), .. Run(10, 30)]);

        SceneCutFinder.FindCuts(frames).Should().BeEmpty();
    }

    [Fact]
    public void FindCuts_leaves_bright_content_to_the_scene_detector()
    {
        List<LumaFrame> frames = Frames([.. Run(10, 150), .. Run(10, 190)]);

        SceneCutFinder.FindCuts(frames).Should().BeEmpty();
    }

    [Fact]
    public void FindCuts_normalises_by_the_frames_own_dynamic_range_so_a_wide_frame_needs_a_bigger_jump()
    {
        // Same 12-level jump: against a +-3 range it is a cut, against a +-60 range it is not.
        SceneCutFinder.FindCuts(Frames([.. Enumerable.Repeat((30.0, 27.0, 33.0), 8), .. Enumerable.Repeat((42.0, 39.0, 45.0), 8)]))
            .Should().HaveCount(1);
        SceneCutFinder.FindCuts(Frames([.. Enumerable.Repeat((30.0, 0.0, 90.0), 8), .. Enumerable.Repeat((42.0, 12.0, 100.0), 8)]))
            .Should().BeEmpty();
    }

    [Fact]
    public void Merge_adds_dark_cuts_and_keeps_every_scene_boundary()
    {
        List<(double StartSec, double EndSec)> scene = [(0, 4), (4, 10)];

        SceneCutFinder.Merge(scene, [2.0], 10, 0.5).Should().Equal((0, 2), (2, 4), (4, 10));
    }

    [Fact]
    public void Merge_drops_a_dark_cut_closer_than_the_minimum_shot_length_to_any_boundary()
    {
        List<(double StartSec, double EndSec)> scene = [(0, 4), (4, 10)];

        SceneCutFinder.Merge(scene, [0.2, 4.1, 9.9, 6.0, 6.2], 10, 0.5)
            .Should().Equal((0, 4), (4, 6), (6, 10));
    }

    [Fact]
    public void Merge_with_no_cuts_returns_the_scene_spans_unchanged()
    {
        List<(double StartSec, double EndSec)> scene = [(0, 4), (4, 10)];

        SceneCutFinder.Merge(scene, [], 10, 0.5).Should().Equal(scene);
    }

    // ---- real ffmpeg: the documented bug ----

    [FfmpegToolsFact]
    public async Task An_early_dark_cut_is_found_on_a_long_film_whose_frame_log_exceeds_the_captured_tail()
    {
        string root = Path.Combine(Path.GetTempPath(), "scene-cut-finder-long", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IOptions<VideoEditingOptions> options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(root, "scratch"),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 300
            });
            IVideoToolRunner runner = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);

            // 600 s at 10 fps = 6,000 frames. The old pass wrote ~400 characters of signalstats lines per
            // frame to stderr, and the runner keeps only the last 1,000,000 characters — the last ~2,400
            // frames — so frame 40, where this one hard cut is, was never parsed. Two flat dark levels and
            // nothing else moving, so the cut at 4 s is the only jump in the film.
            string path = Path.Combine(root, "long-dark.mp4");
            VideoToolResult made = await runner.RunFfmpegAsync(
                [
                    "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
                    "-filter_complex",
                    "color=c=0x121212:s=64x64:r=10:d=4[a];color=c=0x222222:s=64x64:r=10:d=596[b];[a][b]concat=n=2:v=1:a=0[v]",
                    "-map", "[v]", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "12", path
                ],
                TimeSpan.FromMinutes(2), CancellationToken.None);
            made.Succeeded.Should().BeTrue(made.StdErr);

            IShotDetector standard = new FfmpegShotDetector(runner, options, NullLogger<FfmpegShotDetector>.Instance);
            IMediaWorkspace workspace = LocalMediaWorkspace.ForLocalFile(path, runner, NullLogger.Instance);

            IReadOnlyList<(double StartSec, double EndSec)> sensitiveShots = await SceneCutFinder.DetectAsync(
                standard, workspace, "long-dark.mp4", SceneDetectionMode.Sensitive, 0.30, 600.0, 0.5, options.Value, CancellationToken.None);

            sensitiveShots.Should().HaveCount(2, "the dark-to-dark cut at 4 s is invisible to the scene score");
            sensitiveShots[0].EndSec.Should().BeApproximately(4.0, 0.05);
            sensitiveShots[1].StartSec.Should().BeApproximately(4.0, 0.05);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [FfmpegToolsFact]
    public async Task A_hard_cut_between_two_dark_noisy_shots_is_one_shot_in_Standard_and_two_in_Sensitive()
    {
        string root = Path.Combine(Path.GetTempPath(), "scene-cut-finder", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IOptions<VideoEditingOptions> options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(root, "scratch"),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 120
            });
            IVideoToolRunner runner = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);

            // Two dark shots of 2 s at different dark levels, each with temporal noise, joined by a hard cut at 2 s.
            string path = Path.Combine(root, "dark.mp4");
            VideoToolResult made = await runner.RunFfmpegAsync(
                [
                    "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
                    "-filter_complex",
                    "color=c=0x121212:s=160x120:r=25:d=2,noise=alls=10:allf=t[a];" +
                    "color=c=0x222222:s=160x120:r=25:d=2,noise=alls=10:allf=t[b];" +
                    "[a][b]concat=n=2:v=1:a=0[v]",
                    "-map", "[v]", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "12", path
                ],
                TimeSpan.FromMinutes(2), CancellationToken.None);
            made.Succeeded.Should().BeTrue(made.StdErr);

            IShotDetector standard = new FfmpegShotDetector(runner, options, NullLogger<FfmpegShotDetector>.Instance);
            IMediaWorkspace workspace = LocalMediaWorkspace.ForLocalFile(path, runner, NullLogger.Instance);

            IReadOnlyList<(double StartSec, double EndSec)> standardShots = await SceneCutFinder.DetectAsync(
                standard, workspace, "dark.mp4", SceneDetectionMode.Standard, 0.30, 4.0, 0.2, options.Value, CancellationToken.None);
            IReadOnlyList<(double StartSec, double EndSec)> sensitiveShots = await SceneCutFinder.DetectAsync(
                standard, workspace, "dark.mp4", SceneDetectionMode.Sensitive, 0.30, 4.0, 0.2, options.Value, CancellationToken.None);

            standardShots.Should().HaveCount(1, "the scene score of a dark-to-dark cut is far below 0.30: the original bug");
            sensitiveShots.Should().HaveCount(2);
            sensitiveShots[0].EndSec.Should().BeApproximately(2.0, 0.05);
            sensitiveShots[1].StartSec.Should().BeApproximately(2.0, 0.05);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
