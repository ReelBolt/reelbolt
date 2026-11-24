#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Keyframed reframing, per-source fits and the subject safe area through the whole compile
/// (docs/video-editing.md "Keyframed reframing"). Shares <see cref="CompileFixture"/> with the
/// output-format tests; the last test is a real 3 s encode probed by pixel.
/// </summary>
public partial class VideoCompileNarrationAndFormatTests
{
    private static ReframeKeyframe RKey(double at, double zoom, double cx = 0.5, double cy = 0.5, ReframeEasing easing = ReframeEasing.Linear) =>
        new(at, zoom, cx, cy, easing);

    private static string ErrorCode(StepExecutionResult result) =>
        JsonDocument.Parse(result.Output).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    /// <summary>Folds the run's scratch subtree out of an argv entry so a golden can pin the rest (its guids differ per run).</summary>
    private static string NormalizeScratch(string arg)
    {
        string scratchBase = Path.Combine(Path.GetTempPath(), "video-compile-narration-format-tests");
        return arg.StartsWith(scratchBase, StringComparison.Ordinal) ? "<scratch>/" + Path.GetFileName(arg) : arg;
    }

    [Fact]
    public async Task Without_reframes_the_single_source_compile_keeps_its_select_path_and_no_reframes_node()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0));
        fixture.Config = c => c with { OutputFormat = VideoOutputFormat.Vertical, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain("force_original_aspect_ratio=increase,crop=1080:1920,setsar=1[vout]");
        fixture.FilterGraph.Should().NotContain("zoompan");
        fixture.Output!.ContainsKey("reframes").Should().BeFalse();
        fixture.Edl!.ContainsKey("reframes").Should().BeFalse();
    }

    [Fact]
    public async Task A_reframed_single_source_compile_runs_the_zoompan_stage_on_the_spans_own_clock()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0));
        fixture.Config = c => c with
        {
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            Reframes = [new VideoReframe(0, null, 3, [RKey(0, 2, 0.5, 0.9, ReframeEasing.EaseOut), RKey(3, 1)])]
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        // The whole filtergraph AND the whole encode argv, exactly and in order: a changed or
        // reordered stage, flag or argument fails here, where the old substring Contains let both
        // slip through (QA-P1-18). Only the run's scratch paths are folded to a placeholder.
        fixture.FilterGraph.Should().Be(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,trim=start=0:end=10,setpts=PTS-STARTPTS,fps=30/1,zoompan=" +
            "z='if(lt(it,3),if(lt(it,3),2+(-1)*(it/3)*(2-(it/3)),1),1)':" +
            "x='2*floor(max(0,(0.5)*iw-iw/zoom/2)/2)':" +
            "y='2*floor(max(0,(if(lt(it,3),if(lt(it,3),0.75+(-0.25)*(it/3)*(2-(it/3)),0.5),0.5))*ih-ih/zoom/2)/2)':" +
            "d=1:s=1920x1080:fps=30/1,tpad=stop_mode=clone:stop_duration=10,trim=end=10[v0];" +
            "[0:a]atrim=start=0:end=10,asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo[a0];" +
            "[v0][a0]concat=n=1:v=1:a=1[vout][aout]");
        fixture.EncodeArgs!.Select(NormalizeScratch).Should().Equal(
            "-nostdin", "-hide_banner", "-y", "-loglevel", "error", "-progress", "pipe:1",
            "-protocol_whitelist", "file",
            "-format_whitelist", UserMediaInputFormats.Whitelist,
            "-i", "<scratch>/source-0.mp4",
            "-filter_complex_script", "<scratch>/filter_complex_multisource.txt",
            "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-crf", "20", "-preset", "veryfast",
            "-pix_fmt", "yuv420p", "-color_range", "tv", "-movflags", "+faststart",
            "-c:a", "aac",
            "<scratch>/edited.mp4");
        JsonObject reframe = fixture.Output!["reframes"]!["reframes"]!.AsArray().Single()!.AsObject();
        reframe["applied"]!.GetValue<bool>().Should().BeTrue();
        reframe["spans"]!.AsArray().Select(n => n!.GetValue<int>()).Should().Equal(0);
        reframe["clamped"]!.AsArray().Single()!["to"]!.GetValue<double>().Should().Be(0.75, "at zoom 2 the window centre can go no lower than 0.75");
        fixture.Edl!["reframes"]!["reframes"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Reframing_a_stream_copy_is_a_config_error()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0));
        fixture.Config = c => c with
        {
            Mode = VideoCompileMode.StreamCopy,
            AllowKeyframeSnapping = true,
            Reframes = [new VideoReframe(0, null, null, [RKey(0, 2)])]
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("REFRAME_REQUIRES_REENCODE");
        fixture.EncodeArgs.Should().BeNull();
    }

    [Theory]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public async Task The_compile_re_checks_the_reframe_cap_before_building_its_graph(int count, bool runs)
    {
        var fixture = new CompileFixture(SingleSourceArtifact(20.0));
        fixture.Config = c => c with
        {
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            Reframes = Enumerable.Range(0, count).Select(i => new VideoReframe(0, i, i + 1, [RKey(0, 2)])).ToList()
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        if (runs)
        {
            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            fixture.FilterGraph.Should().Contain("zoompan");
        }
        else
        {
            result.Status.Should().Be(StepStatus.Failed);
            ErrorCode(result).Should().Be("REFRAME_TOO_MANY_REFRAMES");
            fixture.EncodeArgs.Should().BeNull("the graph is never built");
        }
    }

    [Fact]
    public async Task A_reframe_of_a_clip_the_analysis_does_not_have_is_a_config_error()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0));
        fixture.Config = c => c with { Reframes = [new VideoReframe(4, null, null, [RKey(0, 2)])] };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("REFRAME_UNKNOWN_SOURCE");
    }

    [Fact]
    public async Task A_per_source_fit_pads_one_clip_while_the_others_are_cropped()
    {
        var fixture = new CompileFixture(ThreeSourceArtifact());
        fixture.Config = c => c with
        {
            OutputFormat = VideoOutputFormat.Vertical,
            OutputFit = VideoOutputFit.Crop,
            SourceFits = new Dictionary<int, VideoOutputFit> { [1] = VideoOutputFit.Pad },
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0", "s1"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=4.066667,trim=start=0:end=4,setpts=PTS-STARTPTS,scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30/1");
        fixture.FilterGraph.Should().Contain(
            "[1:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=5.066667,trim=start=0:end=5,setpts=PTS-STARTPTS,scale=1080:1920:force_original_aspect_ratio=decrease,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30/1");
        fixture.Output!["output"]!["sourceFits"]!["1"]!.GetValue<string>().Should().Be("Pad");
    }

    [Fact]
    public async Task Under_subject_a_shot_with_on_screen_text_is_padded_inside_the_safe_area()
    {
        VideoAnalysisArtifact artifact = SingleSourceArtifact(10.0);
        var text = new VideoShotCaption("s2", "end card", [], "", "", "", "Wide", "", ["Launch day"], [], "Unknown", "", "", "", []);
        artifact = artifact with
        {
            Shots =
            [
                new VideoAnalysisShot("s0", 0, 3, SourceIndex: 0, Subject: new VideoAnalysisRect(0.2, 0.3, 0.2, 0.4)),
                new VideoAnalysisShot("s1", 3, 6, SourceIndex: 0),
                new VideoAnalysisShot("s2", 6, 10, SourceIndex: 0, Caption: text)
            ],
            OfferedIds = ["s0", "s1", "s2"]
        };
        var fixture = new CompileFixture(artifact);
        fixture.Config = c => c with { OutputFormat = VideoOutputFormat.Vertical, OutputFit = VideoOutputFit.Subject, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0", "s2"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=3.066667,trim=start=0:end=3,setpts=PTS-STARTPTS,scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920:");
        fixture.FilterGraph.Should().Contain(
            "trim=start=6:end=10,setpts=PTS-STARTPTS,scale=972:1728:force_original_aspect_ratio=decrease,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30/1");
        JsonObject shot = fixture.Output!["reframes"]!["safeAreaShots"]!.AsArray().Single()!.AsObject();
        shot["shot"]!.GetValue<string>().Should().Be("s2");
        shot["reason"]!.GetValue<string>().Should().Be("on_screen_text");
        fixture.Edl!["reframes"]!["safeAreaShots"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public void Tracked_insert_corners_under_a_reframe_land_where_the_filter_puts_those_source_pixels()
    {
        // Property: every insert keyframe's corners, mapped through the time-varying fit, equal the
        // source point re-projected through the zoompan window at that keyframe's own moment.
        VideoAnalysisArtifact artifact = SingleSourceArtifact(10.0);
        var media = new VideoAnalysisMedia(10, 30, 1, 1920, 1080);
        List<VideoCompileStepExecutor.ResolvedSpan> spans =
            [new(0, 10, 0, 10, 0, 300, 0, ["x"])];
        VideoReframeValidation v = VideoReframe.Validate(
            [new VideoReframe(0, 1, 8, [RKey(0, 3, 0.3, 0.6, ReframeEasing.EaseInOut), RKey(4, 1.5, 0.6, 0.5, ReframeEasing.EaseIn), RKey(6, 1)])]);
        var config = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1, Reframes: v.Normalized);
        SourceFitPlan fits = SourceFitPlanner.Plan(config, artifact, [(0, 0.0, 10.0)], _ => media, media, fixedFormat: false);
        ReframePlan plan = ReframePlanner.Build(config, v.Normalized, v.Clamps, artifact, spans, _ => media, media, fits, _ => false);
        VideoCompileStepExecutor.SourceCanvasFit fit = VideoCompileStepExecutor.SourceCanvasFit.Identity(media) with
        {
            Timeline = plan.TimelineFor(0),
            ReframeBreakpoints = VideoCompileStepExecutor.ReframeBreakpointsFor(config, 0)
        };

        var keys = Enumerable.Range(0, 60)
            .Select(i => i * 0.15)
            .Select(t => new VideoInsertQuadKeyframe(t, 0.40, 0.40, 0.55, 0.42, 0.41, 0.60, 0.56, 0.62))
            .ToList();
        var track = new VideoInsertRegionTrack(
            Id: "r0", ShotId: "s0", StartSec: 0, EndSec: 9, Keyframes: keys, Confidence: 0.9, MeanAreaRatio: 0.03,
            MeanAspectRatio: 0.75, MotionClass: "static", ColorName: "green", SourceIndex: 0);

        IReadOnlyList<InsertQuadFrame> frames = VideoCompileStepExecutor.BuildInsertKeyframes(
            track, Timeline((0, 0, 10)), media, (0, 9), int.MaxValue, 0, fit);

        // The plate is the same quad at every tracked keyframe, so the expected corner is that quad
        // mapped through the reframe's window at the sample's OWN source moment.
        void ExpectAt(long frame, double sourceSec)
        {
            InsertQuadFrame f = frames.Single(x => x.FrameIndex == frame);
            ReframePixelWindow px = ReframeGeometry.PixelWindow(ReframeGeometry.WindowAt(v.Normalized, sourceSec), 1920, 1080);
            double ExpectedX(double nx) => (nx * 1920 - px.X) / px.Width * 1920;
            double ExpectedY(double ny) => (ny * 1080 - px.Y) / px.Height * 1080;
            f.X0.Should().BeApproximately(ExpectedX(0.40), 1e-6, $"frame {frame}");
            f.Y0.Should().BeApproximately(ExpectedY(0.40), 1e-6, $"frame {frame}");
            f.X3.Should().BeApproximately(ExpectedX(0.56), 1e-6, $"frame {frame}");
            f.Y3.Should().BeApproximately(ExpectedY(0.62), 1e-6, $"frame {frame}");
        }

        // Every tracked keyframe still has its own insert keyframe, at its own moment.
        foreach (VideoInsertQuadKeyframe k in keys)
            ExpectAt((long)Math.Round(k.TimeSec * 30), k.TimeSec);

        // And every reframe knot is sampled — AT the knot and one source frame before it, so the
        // Hold/cut lands on a perspective keyframe instead of being smeared (QA-P1-13).
        foreach (double knot in new[] { 1.0, 5.0, 7.0, 8.0 })
        {
            ExpectAt((long)Math.Round(knot * 30), knot);
            ExpectAt((long)Math.Round(knot * 30) - 1, knot - 1.0 / 30);
        }
    }
    [RealFfmpegFact]
    public async Task Real_ffmpeg_three_second_reframe_crops_the_first_and_last_frames_where_the_geometry_says()
    {
        // A 256x144 horizontal luma ramp (pixel value = column): a frame's first and last visible
        // columns read back as the left and right edge of the window zoompan kept.
        const int width = 256, height = 144, fps = 30;
        string work = Path.Combine(Path.GetTempPath(), "reelbolt-reframe-compile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string clip = Path.Combine(work, "clip.mp4");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                "-i", $"color=black:s={width}x{height}:r={fps}:d=3,format=yuv420p,geq=lum='X':cb=128:cr=128",
                "-an", "-c:v", "libx264", "-crf", "0", "-pix_fmt", "yuv420p", clip);
            var options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(work, "scratch"),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 60,
                CompileTimeoutSeconds = 120
            });
            var runner = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
            var reframes = new List<VideoReframe> { new(0, null, null, [RKey(0, 4, 0.3, 0.5, ReframeEasing.EaseOut), RKey(3, 1)]) };
            var fixture = new CompileFixture(SingleSourceArtifact(3.0, width, height), sourcesHaveAudio: false)
            {
                RealToolRunner = runner,
                RealProbe = new FfprobeMediaProbe(runner, options, NullLogger<FfprobeMediaProbe>.Instance),
                SourceFile = clip,
                OutputCopy = Path.Combine(work, "out.mp4")
            };
            fixture.Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0, Crf = 0, Reframes = reframes };

            StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            // The raw Y plane, no pixel-format conversion (a gray conversion would rescale tv-range luma).
            string raw = Path.Combine(work, "frames.yuv");
            RunArgv(work, "ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", fixture.OutputCopy!, "-pix_fmt", "yuv420p", "-f", "rawvideo", raw);
            byte[] bytes = File.ReadAllBytes(raw);
            int frameBytes = width * height * 3 / 2;
            int frames = bytes.Length / frameBytes;
            frames.Should().Be(3 * fps, "the reframe keeps every frame at the source rate");
            foreach (int k in new[] { 0, frames - 1 })
            {
                ReframePixelWindow expected = ReframeGeometry.PixelWindow(ReframeGeometry.WindowAt(reframes, k / (double)fps), width, height);
                int row = k * frameBytes + height / 2 * width;
                ((int)bytes[row]).Should().BeCloseTo(expected.X, 3, $"frame {k} left edge");
                ((int)bytes[row + width - 1]).Should().BeCloseTo(expected.X + expected.Width - 1, 3, $"frame {k} right edge");
            }

            ReframeGeometry.PixelWindow(ReframeGeometry.WindowAt(reframes, 0), width, height).Width.Should().Be(64, "frame 0 is the 4x crop");

            // The same reframe into a vertical canvas: the stage composes with the cover fit.
            var vertical = new CompileFixture(SingleSourceArtifact(3.0, width, height), sourcesHaveAudio: false)
            {
                RealToolRunner = runner,
                RealProbe = new FfprobeMediaProbe(runner, options, NullLogger<FfprobeMediaProbe>.Instance),
                SourceFile = clip,
                OutputCopy = Path.Combine(work, "vertical.mp4")
            };
            vertical.Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0, OutputFormat = VideoOutputFormat.Vertical, Reframes = reframes };
            (await vertical.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
            File.Exists(vertical.OutputCopy).Should().BeTrue();
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void RunArgv(string workingDirectory, string tool, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(tool) { RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workingDirectory };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{tool} failed: {stderr}");
    }

    // ==========================================================================================
    // QA-P1-13 — the insert/censor geometry must follow the SAME time-varying transform the reframe
    // applies to the picture, not only the tracker's own keyframes.
    // ==========================================================================================

    private static VideoCompileStepExecutor.SourceCanvasFit ReframedFit(
        VideoCompileStepConfig config, VideoReframeValidation validation, VideoAnalysisArtifact artifact,
        List<VideoCompileStepExecutor.ResolvedSpan> spans, VideoAnalysisMedia media, int sourceIndex)
    {
        SourceFitPlan fits = SourceFitPlanner.Plan(config, artifact, [(sourceIndex, 0.0, media.DurationSec)], _ => media, media, fixedFormat: false);
        ReframePlan plan = ReframePlanner.Build(config, validation.Normalized, validation.Clamps, artifact, spans, _ => media, media, fits, _ => false);
        return VideoCompileStepExecutor.SourceCanvasFit.Identity(media) with
        {
            Timeline = plan.TimelineFor(sourceIndex),
            ReframeBreakpoints = VideoCompileStepExecutor.ReframeBreakpointsFor(config, sourceIndex)
        };
    }

    /// <summary>A static plate the tracker sampled at <paramref name="times"/> (same quad at each).</summary>
    private static VideoInsertRegionTrack StaticTrack(params double[] times) =>
        new(Id: "r0", ShotId: "s0", StartSec: 0, EndSec: 9,
            Keyframes: times.Select(t => new VideoInsertQuadKeyframe(t, 0.40, 0.40, 0.55, 0.42, 0.41, 0.60, 0.56, 0.62)).ToList(),
            Confidence: 0.9, MeanAreaRatio: 0.03, MeanAspectRatio: 0.75, MotionClass: "static", ColorName: "green", SourceIndex: 0);

    /// <summary>The perspective filter's own linear interpolation between consecutive keyframes, at <paramref name="frame"/>.</summary>
    private static (double X0, double Y0) Interpolated(IReadOnlyList<InsertQuadFrame> frames, double frame)
    {
        if (frames.Count == 1 || frame <= frames[0].FrameIndex)
            return (frames[0].X0, frames[0].Y0);
        for (int i = 0; i < frames.Count - 1; i++)
        {
            InsertQuadFrame a = frames[i], b = frames[i + 1];
            if (frame < a.FrameIndex || frame > b.FrameIndex)
                continue;
            double u = (frame - a.FrameIndex) / (double)(b.FrameIndex - a.FrameIndex);
            return (a.X0 + (b.X0 - a.X0) * u, a.Y0 + (b.Y0 - a.Y0) * u);
        }

        return (frames[^1].X0, frames[^1].Y0);
    }

    [Fact]
    public void A_one_keyframe_track_under_a_reframe_is_not_left_behind()
    {
        VideoAnalysisArtifact artifact = SingleSourceArtifact(10.0);
        var media = new VideoAnalysisMedia(10, 30, 1, 1920, 1080);
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [new(0, 10, 0, 10, 0, 300, 0, ["x"])];
        VideoReframeValidation v = VideoReframe.Validate([new VideoReframe(0, 0, 8, [RKey(0, 2), RKey(4, 1)])]);
        var config = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1, Reframes: v.Normalized);
        VideoCompileStepExecutor.SourceCanvasFit fit = ReframedFit(config, v, artifact, spans, media, 0);

        // ONE tracked keyframe: a static plate the tracker sampled once.
        IReadOnlyList<InsertQuadFrame> frames = VideoCompileStepExecutor.BuildInsertKeyframes(
            StaticTrack(0), Timeline((0, 0, 10)), media, (0, 9), int.MaxValue, 0, fit);

        frames.Should().HaveCountGreaterThan(1, "a one-keyframe track must still follow the reframe, not stay fixed");
        frames[^1].X0.Should().NotBeApproximately(frames[0].X0, 1e-6, "the zoom-out moves the plate under the insert");

        // The property: at EVERY returned keyframe the corners are the plate mapped through the fit
        // at that keyframe's own moment — so the added reframe-knot samples are as correct as the
        // tracker's own.
        foreach (InsertQuadFrame f in frames)
        {
            double sourceSec = f.FrameIndex / 30.0;
            ReframePixelWindow px = ReframeGeometry.PixelWindow(ReframeGeometry.WindowAt(v.Normalized, sourceSec), 1920, 1080);
            double ExpectedX(double nx) => (nx * 1920 - px.X) / px.Width * 1920;
            f.X0.Should().BeApproximately(ExpectedX(0.40), 1e-6, $"frame {f.FrameIndex}");
        }
    }

    [Fact]
    public void A_hold_reframe_keeps_the_insert_on_its_plate_across_the_hold()
    {
        VideoAnalysisArtifact artifact = SingleSourceArtifact(10.0);
        var media = new VideoAnalysisMedia(10, 30, 1, 1920, 1080);
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [new(0, 10, 0, 10, 0, 300, 0, ["x"])];
        // Hold at 0 then a hard cut to the full frame at 4 s: the picture does not move for 4 s.
        VideoReframeValidation v = VideoReframe.Validate(
            [new VideoReframe(0, 0, 8, [RKey(0, 2, 0.5, 0.5, ReframeEasing.Hold), RKey(4, 1)])]);
        var config = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1, Reframes: v.Normalized);
        VideoCompileStepExecutor.SourceCanvasFit fit = ReframedFit(config, v, artifact, spans, media, 0);

        IReadOnlyList<InsertQuadFrame> frames = VideoCompileStepExecutor.BuildInsertKeyframes(
            StaticTrack(0, 8), Timeline((0, 0, 10)), media, (0, 9), int.MaxValue, 0, fit);

        // ffmpeg interpolates linearly between the perspective keyframes. Across the hold
        // ([0, 4 s) of the plate's own clock) that interpolation must be the hold's own geometry:
        // without a sample at the knot and one frame before it, the zoom-out is smeared over the
        // whole hold and the insert slides off the plate.
        (double holdX0, double holdY0) = Interpolated(frames, 0);
        for (double f = 0; f < 4 * 30; f += 5)
        {
            (double x0, double y0) = Interpolated(frames, f);
            x0.Should().BeApproximately(holdX0, 1e-6, $"frame {f} is inside the hold");
            y0.Should().BeApproximately(holdY0, 1e-6, $"frame {f} is inside the hold");
        }

        // And the cut itself is honoured: by the knot the plate is at the full-frame framing.
        (double afterX0, _) = Interpolated(frames, 4 * 30);
        afterX0.Should().BeApproximately(0.40 * 1920, 1e-6, "at 4 s the clip is shown whole, so the plate's own corner maps 1:1");
    }

    [Fact]
    public void The_overlay_band_is_mapped_at_the_moment_it_actually_begins_not_the_placement_start()
    {
        // The placement's own start sits BEFORE the kept span (the edit cut that stretch away), so
        // mapping at placement.StartSec would use a fit the picture never shows. QA-P1-13.
        List<VideoCompileStepExecutor.ResolvedSpan> spans = [new(4, 10, 4, 10, 120, 300, 0, ["x"])];
        OutputTimeline timeline = OutputTimeline.Build(spans, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(spans.Count));

        VideoCompileStepExecutor.OverlayFitSourceSec(2.0, 0.0, 0, timeline, spans)
            .Should().Be(4.0, "the band begins where the kept span does, not at the placement's cut-away start");
        VideoCompileStepExecutor.OverlayFitSourceSec(5.0, 1.0, 0, timeline, spans)
            .Should().Be(5.0, "a placement inside the span is mapped at its own start, unchanged");
    }
}
