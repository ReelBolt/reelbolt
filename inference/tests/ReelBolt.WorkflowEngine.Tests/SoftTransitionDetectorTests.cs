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

/// <summary>VideoAnalyze <c>SceneDetection: Soft</c>: the lagged frame-difference detector for dissolves, whips and warps.</summary>
public class SoftTransitionDetectorTests
{
    private const double Fps = 25;

    private static byte[] Sequence(int count, Func<int, byte> level, Func<int, int, byte>? pixel = null)
    {
        byte[] data = new byte[count * SoftTransitionDetector.FrameBytes];
        for (int f = 0; f < count; f++)
        {
            for (int k = 0; k < SoftTransitionDetector.FrameBytes; k++)
                data[f * SoftTransitionDetector.FrameBytes + k] = pixel is null ? level(f) : pixel(f, k);
        }

        return data;
    }

    [Fact]
    public void A_hard_cut_is_one_boundary_centred_on_the_cut()
    {
        byte[] frames = Sequence(100, f => f < 50 ? (byte)40 : (byte)200);

        IReadOnlyList<SoftBoundary> found = SoftTransitionDetector.FindBoundaries(frames, Fps);

        found.Should().ContainSingle().Which.TimeSec.Should().BeApproximately(50 / Fps, 0.1);
    }

    [Fact]
    public void A_linear_dissolve_over_half_a_second_is_one_boundary_mid_dissolve()
    {
        // 12.5 frames of dissolve from frame 40 to 52.
        byte[] frames = Sequence(100, f => (byte)(f < 40 ? 40 : f > 52 ? 200 : 40 + (f - 40) * 160 / 12));

        IReadOnlyList<SoftBoundary> found = SoftTransitionDetector.FindBoundaries(frames, Fps);

        found.Should().ContainSingle().Which.TimeSec.Should().BeInRange(44 / Fps, 49 / Fps);
    }

    [Fact]
    public void A_whip_blur_the_scene_score_would_miss_is_found()
    {
        // Textured shot A, a few smeared frames (low-contrast), textured shot B with a different pattern.
        byte[] frames = Sequence(100, _ => 0, (f, k) =>
            f < 46 ? (byte)(k % 2 == 0 ? 30 : 220)
            : f < 50 ? (byte)(110 + (k % 3) * 4)
            : (byte)(k % 2 == 0 ? 220 : 30));

        IReadOnlyList<SoftBoundary> found = SoftTransitionDetector.FindBoundaries(frames, Fps);

        found.Should().ContainSingle().Which.TimeSec.Should().BeInRange(44 / Fps, 52 / Fps);
    }

    [Fact]
    public void A_static_shot_has_no_boundary()
    {
        SoftTransitionDetector.FindBoundaries(Sequence(100, _ => 90), Fps).Should().BeEmpty();
    }

    [Fact]
    public void Flicker_below_the_threshold_is_not_a_boundary()
    {
        // +-20 of 255 is a 0.078 difference, below 0.14.
        byte[] frames = Sequence(100, f => (byte)(f % 2 == 0 ? 100 : 120));

        SoftTransitionDetector.FindBoundaries(frames, Fps).Should().BeEmpty();
    }

    [Fact]
    public void Two_peaks_within_0_6_seconds_merge_and_keep_the_larger()
    {
        // 40 -> 120 at frame 40, 120 -> 255 at frame 48 (0.32 s later): one transition, the bigger step wins.
        byte[] frames = Sequence(100, f => f < 40 ? (byte)40 : f < 48 ? (byte)120 : (byte)255);

        SoftTransitionDetector.FindBoundaries(frames, Fps).Should().HaveCount(1);
    }

    [Fact]
    public void Too_short_a_sequence_gives_nothing()
    {
        SoftTransitionDetector.FindBoundaries(Sequence(2, f => (byte)(f * 200)), Fps).Should().BeEmpty();
    }

    [Fact]
    public void Merge_unions_with_the_hard_cuts_prefers_the_hard_one_and_respects_the_minimum_shot()
    {
        List<(double StartSec, double EndSec)> hard = [(0, 5), (5, 20)];
        SoftBoundary[] soft =
        [
            new(0.2, 0.5),   // inside the minimum shot at the start
            new(5.2, 0.4),   // within 0.3 s of the hard cut at 5
            new(8.0, 0.3),   // kept
            new(8.4, 0.3),   // within the minimum shot of the one at 8.0
            new(19.9, 0.3)   // inside the minimum shot at the end
        ];

        SceneDetectionResult merged = SoftTransitionDetector.Merge(hard, soft, 20, 0.5);

        merged.SoftBoundaries.Should().Equal(8.0);
        merged.Spans.Should().Equal((0, 5), (5, 8.0), (8.0, 20));
    }

    [Fact]
    public void Merge_without_soft_boundaries_returns_the_standard_spans_untouched()
    {
        List<(double StartSec, double EndSec)> hard = [(0, 5), (5, 20)];

        SceneDetectionResult merged = SoftTransitionDetector.Merge(hard, [], 20, 0.5);

        merged.Spans.Should().BeSameAs(hard);
        merged.SoftBoundaries.Should().BeEmpty();
    }

    [Fact]
    public void BuildArgs_decodes_once_at_the_source_rate_to_raw_thumbnails_without_network_protocols()
    {
        SoftTransitionDetector.BuildArgs("/scratch/in.mp4", 4, 30000, 1001, "/scratch/out.rgb").Should().Equal(
            "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
            "-protocol_whitelist", "file",
            "-threads", "4",
            "-i", "/scratch/in.mp4",
            "-an",
            "-vf", "fps=30000/1001,scale=24:42:flags=area,format=rgb24",
            "-frames:v", SoftTransitionDetector.MaxFrames.ToString(),
            "-fs", SoftTransitionDetector.MaxThumbBytes.ToString(),
            "-f", "rawvideo", "/scratch/out.rgb");
    }

    [Fact]
    public void The_argv_bounds_the_output_inside_ffmpeg_by_frames_and_by_bytes()
    {
        string[] args = SoftTransitionDetector.BuildArgs("/i.mp4", 2, 25, 1, "/o.rgb", SoftTransitionDetector.FrameCap(10, 25));
        int frames = int.Parse(args[Array.IndexOf(args, "-frames:v") + 1]);
        long bytes = long.Parse(args[Array.IndexOf(args, "-fs") + 1]);

        frames.Should().Be(252);
        bytes.Should().Be(SoftTransitionDetector.MaxThumbBytes);
        (frames * (long)SoftTransitionDetector.FrameBytes).Should().BeLessThanOrEqualTo(SoftTransitionDetector.MaxThumbBytes);
        SoftTransitionDetector.FrameCap(1e9, 120).Should().Be(SoftTransitionDetector.MaxFrames);
    }

    [Theory]
    [InlineData(1000, 1, 120, 1)]
    [InlineData(240000, 1001, 120, 1)]
    [InlineData(30000, 1001, 30000, 1001)]
    [InlineData(1, 5, 1, 1)]
    [InlineData(0, 0, 25, 1)]
    public void The_sampling_rate_is_clamped_between_1_and_120(int num, int den, int expectedNum, int expectedDen)
    {
        SoftTransitionDetector.SamplingFps(num, den).Should().Be((expectedNum, expectedDen));
    }

    private sealed class RecordingRunner(IVideoToolRunner inner, bool failAfterRun, bool timedOut = false, bool cancelAfterRun = false) : IVideoToolRunner
    {
        public string? OutputPath { get; private set; }
        public IReadOnlyList<string>? Args { get; private set; }

        public async Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        {
            Args = args;
            OutputPath = args[^1];
            VideoToolResult r = await inner.RunFfmpegAsync(args, timeout, ct);
            if (cancelAfterRun)
                throw new OperationCanceledException(ct);
            return failAfterRun || timedOut ? new VideoToolResult(1, "", "forced failure", timedOut) : r;
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
            RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            inner.RunFfprobeAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
            RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) =>
            RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority) =>
            RunFfmpegAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) =>
            inner.RunFfprobeAsync(args, timeout, ct);
    }

    private static async Task<(RecordingRunner Runner, Func<Task<SceneDetectionResult>> Detect, string Root)> Arrange(
        bool failAfterRun, double fps, bool timedOut = false, bool cancelAfterRun = false, CancellationToken token = default)
    {
        string root = Path.Combine(Path.GetTempPath(), "soft-clean", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        IOptions<VideoEditingOptions> options = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(root, "scratch"),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 120
        });
        IVideoToolRunner real = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
        string path = Path.Combine(root, "clip.mp4");
        VideoToolResult made = await real.RunFfmpegAsync(
            ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i",
             $"testsrc2=s=64x64:r={fps.ToString(System.Globalization.CultureInfo.InvariantCulture)}:d=2",
             "-c:v", "libx264", "-pix_fmt", "yuv420p", path],
            TimeSpan.FromMinutes(1), CancellationToken.None);
        made.Succeeded.Should().BeTrue(made.StdErr);
        RecordingRunner runner = new(real, failAfterRun, timedOut, cancelAfterRun);
        IShotDetector standard = new FixedShotDetector([(0, 2)]);
        IMediaWorkspace workspace = LocalMediaWorkspace.ForLocalFile(path, runner, NullLogger.Instance);
        return (runner, () => SceneCutFinder.DetectWithMethodsAsync(
            standard, workspace, "clip.mp4", SceneDetectionMode.Soft, 0.30, 2.0, 0.5, options.Value,
            token, ((int)fps, 1)), root);
    }

    [FfmpegToolsFact]
    public async Task The_thumbnail_file_is_gone_after_a_successful_pass()
    {
        (RecordingRunner runner, Func<Task<SceneDetectionResult>> detect, string root) = await Arrange(failAfterRun: false, fps: 25);
        try
        {
            await detect();
            runner.OutputPath.Should().NotBeNull();
            File.Exists(runner.OutputPath!).Should().BeFalse();
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [FfmpegToolsFact]
    public async Task The_thumbnail_file_is_gone_after_an_ffmpeg_failure()
    {
        (RecordingRunner runner, Func<Task<SceneDetectionResult>> detect, string root) = await Arrange(failAfterRun: true, fps: 25);
        try
        {
            // A failed soft pass never fails the step: the standard shots come back, with no soft boundary.
            SceneDetectionResult result = await detect();
            result.Spans.Should().Equal([(0.0, 2.0)]);
            result.SoftBoundaries.Should().BeEmpty();
            runner.OutputPath.Should().NotBeNull();
            File.Exists(runner.OutputPath!).Should().BeFalse();
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [FfmpegToolsFact]
    public async Task A_timed_out_pass_degrades_to_the_standard_shots_and_leaves_no_thumbnail()
    {
        (RecordingRunner runner, Func<Task<SceneDetectionResult>> detect, string root) = await Arrange(failAfterRun: false, fps: 25, timedOut: true);
        try
        {
            SceneDetectionResult result = await detect();
            result.Spans.Should().Equal([(0.0, 2.0)]);
            result.SoftBoundaries.Should().BeEmpty();
            File.Exists(runner.OutputPath!).Should().BeFalse();
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [FfmpegToolsFact]
    public async Task A_cancelled_pass_still_cancels_and_leaves_no_thumbnail()
    {
        using var cts = new CancellationTokenSource();
        (RecordingRunner runner, Func<Task<SceneDetectionResult>> detect, string root) = await Arrange(failAfterRun: false, fps: 25, cancelAfterRun: true, token: cts.Token);
        try
        {
            Func<Task> act = detect;
            await act.Should().ThrowAsync<OperationCanceledException>();
            runner.OutputPath.Should().NotBeNull();
            File.Exists(runner.OutputPath!).Should().BeFalse();
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [FfmpegToolsFact]
    public async Task A_high_frame_rate_source_is_sampled_at_120_and_writes_within_the_bound()
    {
        string root = Path.Combine(Path.GetTempPath(), "soft-hifps", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IOptions<VideoEditingOptions> options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(root, "scratch"),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 120
            });
            IVideoToolRunner real = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
            string outPath = Path.Combine(root, "out.rgb");
            // 4 s of a 1000 fps request: the argv clamps it to 120, so at most 480 + slack frames are written.
            string input = Path.Combine(root, "in.mp4");
            (await real.RunFfmpegAsync(
                ["-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=s=64x64:r=250:d=4",
                 "-c:v", "libx264", "-pix_fmt", "yuv420p", input], TimeSpan.FromMinutes(1), CancellationToken.None))
                .Succeeded.Should().BeTrue();
            (int num, int den) = SoftTransitionDetector.SamplingFps(1000, 1);
            VideoToolResult r = await real.RunFfmpegAsync(
                SoftTransitionDetector.BuildArgs(input, 2, num, den, outPath, SoftTransitionDetector.FrameCap(4, 120)),
                TimeSpan.FromMinutes(1), CancellationToken.None);
            r.Succeeded.Should().BeTrue(r.StdErr);
            long size = new FileInfo(outPath).Length;
            size.Should().BeLessThanOrEqualTo(SoftTransitionDetector.MaxThumbBytes);
            size.Should().BeLessThanOrEqualTo((4 * 120 + 2) * (long)SoftTransitionDetector.FrameBytes);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Standard_mode_returns_exactly_the_standard_detectors_spans_and_never_decodes_again()
    {
        List<(double StartSec, double EndSec)> spans = [(0, 5), (5, 20)];
        IShotDetector standard = new FixedShotDetector(spans);

        IReadOnlyList<(double StartSec, double EndSec)> result = await SceneCutFinder.DetectAsync(
            standard, null!, "x.mp4", SceneDetectionMode.Standard, 0.30, 20, 0.5, new VideoEditingOptions(), CancellationToken.None);

        result.Should().BeSameAs(spans);
    }

    private sealed class FixedShotDetector(IReadOnlyList<(double StartSec, double EndSec)> spans) : IShotDetector
    {
        public Task<IReadOnlyList<(double StartSec, double EndSec)>> DetectShotsAsync(
            IMediaWorkspace workspace, string virtualPath, double sceneThreshold, double totalDurationSec, CancellationToken ct) =>
            Task.FromResult(spans);

        public Task<IReadOnlyList<(double StartSec, double EndSec)>> DetectShotsAsync(
            string localFilePath, double sceneThreshold, double totalDurationSec, CancellationToken ct) =>
            Task.FromResult(spans);
    }

    [Fact]
    public void Soft_is_appended_after_Sensitive_so_stored_configs_keep_their_meaning()
    {
        ((int)SceneDetectionMode.Standard).Should().Be(0);
        ((int)SceneDetectionMode.Sensitive).Should().Be(1);
        ((int)SceneDetectionMode.Soft).Should().Be(2);
    }

    [FfmpegToolsFact]
    public async Task Real_ffmpeg_finds_a_dissolve_the_scene_score_misses_and_Standard_does_not()
    {
        string root = Path.Combine(Path.GetTempPath(), "soft-transition", Guid.NewGuid().ToString("N"));
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

            // A test pattern and its negative joined by a 0.5 s xfade dissolve centred at about 3.25 s: 8 s total.
            string path = Path.Combine(root, "dissolve.mp4");
            VideoToolResult made = await runner.RunFfmpegAsync(
                [
                    "-nostdin", "-hide_banner", "-y", "-loglevel", "error",
                    "-filter_complex",
                    "testsrc2=s=160x284:r=25:d=4[a];testsrc2=s=160x284:r=25:d=4,negate,format=yuv420p[b];" +
                    "[a]format=yuv420p[a2];[a2][b]xfade=transition=dissolve:duration=0.5:offset=3[v]",
                    "-map", "[v]", "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "12", path
                ],
                TimeSpan.FromMinutes(2), CancellationToken.None);
            made.Succeeded.Should().BeTrue(made.StdErr);

            IShotDetector standard = new FfmpegShotDetector(runner, options, NullLogger<FfmpegShotDetector>.Instance);
            IMediaWorkspace workspace = LocalMediaWorkspace.ForLocalFile(path, runner, NullLogger.Instance);

            SceneDetectionResult soft = await SceneCutFinder.DetectWithMethodsAsync(
                standard, workspace, "dissolve.mp4", SceneDetectionMode.Soft, 0.30, 7.0, 0.5, options.Value,
                CancellationToken.None, (25, 1));

            soft.SoftBoundaries.Should().ContainSingle().Which.Should().BeInRange(2.9, 3.6);
            soft.Spans.Should().HaveCount(2);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [FfmpegToolsFact]
    public async Task The_real_C3_source_has_all_seven_boundaries_when_DYN_C3_SOURCE_is_set()
    {
        string? source = Environment.GetEnvironmentVariable("DYN_C3_SOURCE");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return; // skipped: no real footage on this machine

        string root = Path.Combine(Path.GetTempPath(), "soft-c3", Guid.NewGuid().ToString("N"));
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
            IShotDetector standard = new FfmpegShotDetector(runner, options, NullLogger<FfmpegShotDetector>.Instance);
            IMediaWorkspace workspace = LocalMediaWorkspace.ForLocalFile(source, runner, NullLogger.Instance);

            SceneDetectionResult soft = await SceneCutFinder.DetectWithMethodsAsync(
                standard, workspace, Path.GetFileName(source), SceneDetectionMode.Soft, 0.30, 19.82, 0.5, options.Value,
                CancellationToken.None, null);

            // 10.9-11.6 is the logo warp: the lagged difference peaks at its end (11.76 measured), so 11.6 is pinned.
            // The 4.9 whip and the 4.94 hard cut are one boundary.
            double[] expected = [4.94, 7.9, 11.6, 13.25, 15.9, 17.6];
            List<double> all = soft.Spans.Skip(1).Select(s => s.StartSec).ToList();
            foreach (double t in expected)
                all.Should().Contain(b => Math.Abs(b - t) <= 0.25, $"a boundary near {t}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
