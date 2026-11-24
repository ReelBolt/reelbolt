#nullable enable
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
/// <see cref="ReframeFilterBuilder"/>: the exact filter strings of keyframed reframing and the
/// subject safe-area pad, plus one real-ffmpeg check that the <c>zoompan</c> stage crops exactly
/// the window <see cref="ReframeGeometry"/> says it does, frame by frame.
/// </summary>
public class ReframeFilterBuilderTests
{
    private static ReframeKeyframe Key(double at, double zoom, double cx = 0.5, double cy = 0.5, ReframeEasing easing = ReframeEasing.Linear) =>
        new(at, zoom, cx, cy, easing);

    [Fact]
    public void A_span_no_reframe_touches_gets_no_stage()
    {
        var later = new VideoReframe(0, 10, null, [Key(0, 2)]);
        var earlier = new VideoReframe(0, 0, 3, [Key(0, 2)]);

        ReframeFilterBuilder.ZoompanStage([later], 0, 4, 1920, 1080, 30, 1).Should().BeNull();
        ReframeFilterBuilder.ZoompanStage([earlier], 3, 6, 1920, 1080, 30, 1).Should().BeNull("ToSec is exclusive");
        ReframeFilterBuilder.ZoompanStage([], 0, 4, 1920, 1080, 30, 1).Should().BeNull();
    }

    [Fact]
    public void A_reframe_over_a_whole_span_is_one_frame_accurate_zoompan_stage()
    {
        var reframe = new VideoReframe(0, null, null, [Key(0, 2, 0.4, 0.5, ReframeEasing.EaseOut), Key(2, 1)]);

        string? stage = ReframeFilterBuilder.ZoompanStage([reframe], 0, 4, 1920, 1080, 30, 1);

        stage.Should().Be(
            "fps=30/1,zoompan=" +
            "z='if(lt(it,2),2+(-1)*(it/2)*(2-(it/2)),1)':" +
            "x='2*floor(max(0,(if(lt(it,2),0.4+(0.1)*(it/2)*(2-(it/2)),0.5))*iw-iw/zoom/2)/2)':" +
            "y='2*floor(max(0,(0.5)*ih-ih/zoom/2)/2)':" +
            "d=1:s=1920x1080:fps=30/1");
    }

    [Fact]
    public void A_span_that_starts_inside_a_reframe_runs_its_curve_on_the_spans_own_clock_and_ends_whole()
    {
        // Source 3..5 s of a reframe over 2..4.5 s: local keyframes at -1 s and 1 s, whole after 1.5 s.
        var reframe = new VideoReframe(0, 2, 4.5, [Key(0, 3), Key(2, 1)]);

        string? stage = ReframeFilterBuilder.ZoompanStage([reframe], 3, 5, 1920, 1080, 25, 1);

        stage.Should().StartWith("fps=25/1,zoompan=z='if(lt(it,1.5),if(lt(it,1),3+(-2)*((it+1)/2),1),1)':");
    }

    [Fact]
    public void A_reframe_that_starts_inside_the_span_is_whole_until_it_starts()
    {
        var reframe = new VideoReframe(0, 1, null, [Key(0, 2)]);

        ReframeFilterBuilder.ZoompanStage([reframe], 0, 4, 1920, 1080, 30, 1)
            .Should().StartWith("fps=30/1,zoompan=z='if(lt(it,1),1,2)':");
    }

    [Fact]
    public void A_hold_is_a_constant_until_the_next_keyframe()
    {
        var reframe = new VideoReframe(0, null, null, [Key(0, 2, easing: ReframeEasing.Hold), Key(1, 4)]);

        ReframeFilterBuilder.ZoompanStage([reframe], 0, 3, 1920, 1080, 30, 1)
            .Should().StartWith("fps=30/1,zoompan=z='if(lt(it,1),2,4)':");
    }

    [Theory]
    [InlineData(ReframeEasing.Linear, "1+(1)*(it/2)")]
    [InlineData(ReframeEasing.EaseIn, "1+(1)*(it/2)*(it/2)")]
    [InlineData(ReframeEasing.EaseInOut, "1+(1)*(it/2)*(it/2)*(3-2*(it/2))")]
    public void Each_easing_is_written_as_its_curve(ReframeEasing easing, string segment)
    {
        var reframe = new VideoReframe(0, null, null, [Key(0, 1, easing: easing), Key(2, 2)]);

        ReframeFilterBuilder.ZoompanStage([reframe], 0, 3, 1920, 1080, 30, 1)
            .Should().StartWith($"fps=30/1,zoompan=z='if(lt(it,2),{segment},2)':");
    }

    [Fact]
    public void The_safe_pad_fits_the_clip_inside_the_margin_on_an_even_box()
    {
        ReframeFilterBuilder.SafeBox(1080, 1920, 0.05).Should().Be((972, 1728));
        ReframeFilterBuilder.SafeBox(1081, 1921, 0.0).Should().Be((1080, 1920));
        ReframeFilterBuilder.SafePadSuffix(1080, 1920, 0.05, 30, 1).Should().Be(
            ",scale=972:1728:force_original_aspect_ratio=decrease,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30/1");
    }

    [Fact]
    public void A_reframed_span_puts_the_stage_before_its_canvas_fit()
    {
        IReadOnlyList<string>? parts = ReframeFilterBuilder.SpanVideoChain(new ReframeSpanChain(
            InputIndex: 1, SpanIndex: 2, Start: "3", End: "5", Stage: "fps=30/1,zoompan=z='2':d=1:s=1920x1080:fps=30/1",
            NormalizeSuffix: ",scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30/1",
            SafePad: false, SafeMargin: 0, Blur: false, CanvasWidth: 1080, CanvasHeight: 1920, FpsNum: 30, FpsDen: 1,
            LengthSuffix: ",tpad=stop_mode=clone:stop_duration=2,trim=end=2", FadeSuffix: ""));

        parts.Should().Equal(
            "[1:v]trim=start=3:end=5,setpts=PTS-STARTPTS,fps=30/1,zoompan=z='2':d=1:s=1920x1080:fps=30/1" +
            ",scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30/1" +
            ",tpad=stop_mode=clone:stop_duration=2,trim=end=2[v2]");
    }

    [Fact]
    public void A_text_bearing_span_is_padded_inside_the_safe_area_instead_of_cropped()
    {
        IReadOnlyList<string>? parts = ReframeFilterBuilder.SpanVideoChain(new ReframeSpanChain(
            0, 0, "0", "4", Stage: null,
            NormalizeSuffix: ",scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30/1",
            SafePad: true, SafeMargin: 0.05, Blur: false, 1080, 1920, 30, 1, LengthSuffix: "", FadeSuffix: ""));

        parts.Should().Equal(
            "[0:v]trim=start=0:end=4,setpts=PTS-STARTPTS" +
            ",scale=972:1728:force_original_aspect_ratio=decrease,pad=1080:1920:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30/1[v0]");
    }

    [Fact]
    public void A_blurred_fill_keeps_its_split_graph_with_the_stage_and_the_safe_box()
    {
        IReadOnlyList<string>? parts = ReframeFilterBuilder.SpanVideoChain(new ReframeSpanChain(
            0, 1, "0", "4", Stage: "fps=30/1,zoompan=z='2':d=1:s=1920x1080:fps=30/1", NormalizeSuffix: "",
            SafePad: true, SafeMargin: 0.05, Blur: true, 1080, 1920, 30, 1, LengthSuffix: "", FadeSuffix: ""));

        parts.Should().Equal(
            "[0:v]trim=start=0:end=4,setpts=PTS-STARTPTS,fps=30/1,zoompan=z='2':d=1:s=1920x1080:fps=30/1,fps=30/1,split[vfg1][vbg1]",
            "[vbg1]scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,gblur=sigma=40,eq=brightness=-0.08[vbgb1]",
            "[vfg1]scale=972:1728:force_original_aspect_ratio=decrease[vfgs1]",
            "[vbgb1][vfgs1]overlay=(W-w)/2:(H-h)/2,setsar=1[v1]");
    }

    [Fact]
    public void Nothing_to_do_leaves_the_span_to_the_existing_branch()
    {
        ReframeFilterBuilder.SpanVideoChain(new ReframeSpanChain(
            0, 0, "0", "4", null, "", false, 0, false, 1080, 1920, 30, 1, "", "")).Should().BeNull();
    }

    // ------------------------------------------------------------------------------------------
    // Real ffmpeg: the crop window the stage takes, frame by frame, is ReframeGeometry's.
    // ------------------------------------------------------------------------------------------

    [RealFfmpegFact]
    public void Real_ffmpeg_zoompan_stage_crops_the_window_the_geometry_predicts_on_every_frame()
    {
        const int size = 256, fps = 10;
        var reframe = new VideoReframe(0, 0.5, null,
        [
            Key(0, 4, 0.3, 0.7, ReframeEasing.EaseOut),
            Key(1, 2.2, 0.6, 0.4, ReframeEasing.EaseInOut),
            Key(2, 1.3, 0.5, 0.5)
        ]);
        // The span: source 0.3..2.9 s, so the reframe starts 0.2 s in and runs past the span end.
        const double spanStart = 0.3, spanEnd = 2.9;
        string stage = ReframeFilterBuilder.ZoompanStage([reframe], spanStart, spanEnd, size, size, fps, 1)!;

        string work = Path.Combine(Path.GetTempPath(), "reelbolt-reframe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (bool horizontal in new[] { true, false })
            {
                string raw = Path.Combine(work, horizontal ? "x.yuv" : "y.yuv");
                string ramp = horizontal ? "X" : "Y";
                Run(work, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                    "-i", $"color=black:s={size}x{size}:r={fps}:d=3,format=yuv420p,geq=lum='{ramp}':cb=128:cr=128",
                    "-vf", $"trim=start={spanStart}:end={spanEnd},setpts=PTS-STARTPTS,{stage},format=yuv420p",
                    "-f", "rawvideo", raw);

                byte[] bytes = File.ReadAllBytes(raw);
                int frameBytes = size * size * 3 / 2;
                int frames = bytes.Length / frameBytes;
                frames.Should().Be((int)Math.Round((spanEnd - spanStart) * fps), "the stage keeps the frame rate");
                for (int k = 0; k < frames; k++)
                {
                    ReframePixelWindow expected = ReframeGeometry.PixelWindow(
                        ReframeGeometry.WindowAt([reframe], spanStart + k / (double)fps), size, size);
                    int first = horizontal ? bytes[k * frameBytes + 128 * size] : bytes[k * frameBytes + 128];
                    int last = horizontal ? bytes[k * frameBytes + 128 * size + size - 1] : bytes[k * frameBytes + (size - 1) * size + 128];
                    int from = horizontal ? expected.X : expected.Y;
                    int extent = horizontal ? expected.Width : expected.Height;
                    first.Should().BeCloseTo(from, 1, $"frame {k} first {(horizontal ? "column" : "row")}");
                    last.Should().BeCloseTo(from + extent - 1, 1, $"frame {k} last {(horizontal ? "column" : "row")}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void Run(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = workingDirectory };
        foreach (string arg in args)
            start.ArgumentList.Add(arg);
        using Process process = Process.Start(start)!;
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed: {stderr}");
    }
}
