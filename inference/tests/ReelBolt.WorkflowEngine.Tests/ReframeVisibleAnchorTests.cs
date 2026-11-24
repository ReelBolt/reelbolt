#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="ReframeAnchor.Visible"/>: the keyframe centre names what the canvas shows, so the cover crop pans
/// with the zoompan window (<see cref="ReframeGeometry.Decompose"/>, <see cref="ReframeFilterBuilder.VisibleCoverCrop"/>).
/// </summary>
public class ReframeVisibleAnchorTests
{
    private const int SourceW = 1920, SourceH = 1080, CanvasW = 1080, CanvasH = 1920;

    private static VideoReframe Visible(params ReframeKeyframe[] keys) => new(0, null, null, keys, ReframeAnchor.Visible);

    private static VideoCompileStepExecutor.SourceCanvasFit CoverFit(IReadOnlyList<VideoReframe> reframes)
    {
        var fit = VideoCompileStepExecutor.SourceCanvasFit.For(
            new VideoAnalysisMedia(10, 30, 1, SourceW, SourceH), new VideoAnalysisMedia(10, 30, 1, CanvasW, CanvasH), cover: true);
        return fit with { Timeline = new ReframeFitTimeline(SourceW, SourceH, reframes, [], CanvasW, CanvasH) };
    }

    /// <summary>Canvas x of the source column at fraction <paramref name="p"/> of the frame.</summary>
    private static double CanvasX(VideoCompileStepExecutor.SourceCanvasFit fit, double p) => fit.OffsetX + p * fit.Width;

    [Theory]
    [InlineData(1.0, 0.5)]
    [InlineData(1.0, 0.3)]
    [InlineData(2.0, 0.5)]
    [InlineData(2.0, 0.3)]
    [InlineData(2.0, 0.8)]
    public void A_point_at_the_visible_centre_lands_on_the_canvas_centre(double zoom, double centre)
    {
        VideoReframe reframe = Visible(new ReframeKeyframe(0, zoom, centre));
        VideoCompileStepExecutor.SourceCanvasFit fit = CoverFit([reframe]).At(1);

        CanvasX(fit, centre).Should().BeApproximately(CanvasW / 2.0, 2.0 * zoom + 2);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void The_left_and_right_source_edges_are_reachable(double zoom)
    {
        double visible = (double)CanvasW / (SourceW * (CanvasH / (double)SourceH));
        double leftC = 0.5 * visible / zoom;
        double rightC = 1 - leftC;

        var left = CoverFit([Visible(new ReframeKeyframe(0, zoom, 0))]).At(1);
        var right = CoverFit([Visible(new ReframeKeyframe(0, zoom, 1))]).At(1);

        CanvasX(left, 0).Should().BeApproximately(0, 4);
        CanvasX(right, 1).Should().BeApproximately(CanvasW, 4);
        CanvasX(CoverFit([Visible(new ReframeKeyframe(0, zoom, leftC))]).At(1), 0).Should().BeApproximately(0, 4);
        CanvasX(CoverFit([Visible(new ReframeKeyframe(0, zoom, rightC))]).At(1), 1).Should().BeApproximately(CanvasW, 4);
    }

    [Fact]
    public void A_window_reframe_cannot_pan_at_zoom_one_and_its_geometry_is_unchanged()
    {
        VideoReframe window = new(0, null, null, [new ReframeKeyframe(0, 1, 0)]);
        var baseFit = CoverFit([]);
        CoverFit([window]).At(1).Should().Be(baseFit with { Timeline = null });

        VideoReframe zoomed = new(0, null, null, [new ReframeKeyframe(0, 2, 0.3, 0.6)]);
        var expected = ReframeGeometry.Apply(
            baseFit with { Timeline = null },
            ReframeGeometry.PixelWindow(new ReframeWindow(2, 0.3, 0.6), SourceW, SourceH), SourceW, SourceH);
        CoverFit([zoomed]).At(1).Should().Be(expected);
    }

    [Fact]
    public void Decompose_splits_the_visible_centre_into_window_and_strip()
    {
        // 16:9 into 9:16: V = 0.3164 on x. Zoom 2, visible centre 0.1: window clamps to 0.25, strip is c*z.
        ReframeStrip s = ReframeGeometry.Decompose(new ReframeWindow(2, 0.1, 0.5), 0.3164, 1);
        s.Window.Should().Be(new ReframeWindow(2, 0.25, 0.5));
        s.StripX.Should().BeApproximately(0.2, 1e-9);
        s.StripY.Should().Be(0.5);

        ReframeGeometry.Decompose(new ReframeWindow(1, 0, 0.5), 0.3164, 1).StripX.Should().BeApproximately(0.1582, 1e-9);
    }

    [Fact]
    public void The_crop_filter_pans_on_t_and_is_absent_for_window_or_contain()
    {
        VideoReframe visible = Visible(new ReframeKeyframe(0, 1, 0.2), new ReframeKeyframe(2, 1, 0.8));
        string? crop = ReframeFilterBuilder.VisibleCoverCrop([visible], 0, 4, SourceW, SourceH, CanvasW, CanvasH, null, null);

        crop.Should().StartWith("crop=1080:1920:'max(0,min(iw-ow,(").And.EndWith(")*iw-ow/2))':'(ih-oh)/2'");
        crop.Should().Contain("lt(t,2)").And.Contain("min(0,").And.Contain("max(0,");
        crop!.Count(c => c == '(').Should().Be(crop.Count(c => c == ')'));
        crop.Should().NotContain("it");

        ReframeFilterBuilder.VisibleCoverCrop([visible with { Anchor = ReframeAnchor.Window }], 0, 4, SourceW, SourceH, CanvasW, CanvasH, null, null).Should().BeNull();
        ReframeFilterBuilder.VisibleCoverCrop([visible], 0, 4, SourceW, SourceH, 1920, 1080, null, null).Should().BeNull();
        ReframeFilterBuilder.VisibleCoverCrop([visible with { ToSec = 2 }], 10, 12, SourceW, SourceH, CanvasW, CanvasH, null, null).Should().BeNull();
    }

    [Fact]
    public void A_config_without_anchor_deserializes_as_window_and_builds_the_same_stage()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        VideoReframe old = JsonSerializer.Deserialize<VideoReframe>(
            "{\"sourceIndex\":0,\"keyframes\":[{\"atSec\":0,\"zoom\":2,\"centerX\":0.3,\"centerY\":0.5}]}", options)!;

        old.Anchor.Should().Be(ReframeAnchor.Window);
        JsonSerializer.Serialize(old, options).Should().NotContain("anchor");
        JsonSerializer.Serialize(old with { Anchor = ReframeAnchor.Visible }, options).Should().Contain("\"anchor\":\"Visible\"");
        ReframeFilterBuilder.ZoompanStage([old], 0, 4, 1920, 1080, 30, 1)
            .Should().Be(ReframeFilterBuilder.ZoompanStage([old with { Anchor = ReframeAnchor.Window }], 0, 4, 1920, 1080, 30, 1));
        ReframeFilterBuilder.VisibleCoverCrop([old], 0, 4, 1920, 1080, 1080, 1920, null, null).Should().BeNull();
    }

    [Fact]
    public void Validation_refuses_an_unknown_anchor_keeps_visible_centres_and_needs_reencode()
    {
        VideoReframeValidation unknown = VideoReframe.Validate([Visible(new ReframeKeyframe(0)) with { Anchor = (ReframeAnchor)99 }]);
        unknown.ErrorCode.Should().Be(VideoReframeCodes.AnchorUnknown);

        VideoReframeValidation ok = VideoReframe.Validate([Visible(new ReframeKeyframe(0, 2, 0.1))]);
        ok.IsValid.Should().BeTrue();
        ok.Clamps.Should().BeEmpty();
        ok.Normalized[0].Keyframes[0].CenterX.Should().Be(0.1);
        ok.Normalized[0].Anchor.Should().Be(ReframeAnchor.Visible);

        VideoCompileStepConfig copy = new(1, new ExtractInputRef(ExtractInputSource.Previous), 1,
            Mode: VideoCompileMode.StreamCopy, Reframes: [Visible(new ReframeKeyframe(0))]);
        VideoReframe.CheckConfig(copy).ErrorCode.Should().Be(VideoReframeCodes.RequiresReencode);
    }

    // ------------------------------------------------------------------------------------------
    // Real ffmpeg: a Visible reframe panned to an edge shows that edge of the source.
    // ------------------------------------------------------------------------------------------

    [RealFfmpegFact]
    public void Real_ffmpeg_visible_reframe_reaches_the_source_edges_and_matches_the_geometry()
    {
        const int sw = 256, sh = 144, cw = 72, ch = 128, fps = 10;
        double visibleFrac = cw / (sw * (double)ch / sh);
        string work = Path.Combine(Path.GetTempPath(), "reelbolt-visible-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            foreach (double zoom in new[] { 1.0, 2.0 })
            {
                var reframe = new VideoReframe(0, null, null,
                [
                    new ReframeKeyframe(0, zoom, 0, 0.5),
                    new ReframeKeyframe(2, zoom, 1, 0.5)
                ], ReframeAnchor.Visible);
                string stage = ReframeFilterBuilder.ZoompanStage([reframe], 0, 2, sw, sh, fps, 1)!;
                string crop = ReframeFilterBuilder.VisibleCoverCrop([reframe], 0, 2, sw, sh, cw, ch, null, null)!;
                string raw = Path.Combine(work, $"z{zoom}.yuv");
                Run(work, "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi",
                    "-i", $"color=black:s={sw}x{sh}:r={fps}:d=2,format=yuv420p,geq=lum='X':cb=128:cr=128",
                    "-vf", $"trim=start=0:end=2,setpts=PTS-STARTPTS,{stage},scale={cw}:{ch}:force_original_aspect_ratio=increase,{crop},setsar=1,format=yuv420p",
                    "-f", "rawvideo", raw);

                byte[] bytes = File.ReadAllBytes(raw);
                int frameBytes = cw * ch * 3 / 2;
                int frames = bytes.Length / frameBytes;
                frames.Should().Be(2 * fps);
                int row = ch / 2;
                bytes[row * cw].Should().BeLessThan(4, $"zoom {zoom}: the first frame's left column is the source's left edge");
                ((int)bytes[(frames - 1) * frameBytes + row * cw + cw - 1]).Should().BeGreaterThan((int)(sw - 1 - 16 * zoom), $"zoom {zoom}: the last frame's right column nears the source's right edge");

                var fit = VideoCompileStepExecutor.SourceCanvasFit.For(
                    new VideoAnalysisMedia(10, 30, 1, sw, sh), new VideoAnalysisMedia(10, 30, 1, cw, ch), cover: true);
                fit = fit with { Timeline = new ReframeFitTimeline(sw, sh, [reframe], [], cw, ch) };
                for (int k = 0; k < frames; k++)
                {
                    var at = fit.At(k / (double)fps);
                    double expectedColumn = (0 - at.OffsetX) / at.Width * sw;
                    int actual = bytes[k * frameBytes + row * cw];
                    actual.Should().BeCloseTo((int)Math.Round(expectedColumn), 6, $"zoom {zoom} frame {k}: left column");
                }
            }

            visibleFrac.Should().BeLessThan(1);
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
