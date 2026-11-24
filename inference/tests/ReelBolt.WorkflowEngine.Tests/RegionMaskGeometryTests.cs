#nullable enable
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
/// A region-effect mask must land on the picture it belongs to (issue 5 of 2026-11-03: in a padded
/// 16:9 -> 9:16 render a blur sat beside the face). The mask was conformed with the clip's STATIC canvas
/// fit, while a span padded inside the subject safe area, or reframed, fits its picture per span. These
/// tests render the picture branch and the mask stream from the SAME box video and require the two to
/// agree pixel for pixel (bar the resampled edge), for a safe-padded span, a followed (visible-anchor,
/// cover) span and a contain-padded reframed span. Real ffmpeg; skipped on a host without it.
/// </summary>
public sealed class RegionMaskGeometryTests : IDisposable
{
    private const int SourceWidth = 128;
    private const int SourceHeight = 72;
    private const int CanvasWidth = 72;
    private const int CanvasHeight = 128;
    private const int Fps = 10;
    private const double DurationSec = 2.0;

    private static readonly VideoAnalysisMedia Source = new(DurationSec, Fps, 1, SourceWidth, SourceHeight);
    private static readonly VideoAnalysisMedia Canvas = new(DurationSec, Fps, 1, CanvasWidth, CanvasHeight);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "region-mask-geometry", Guid.NewGuid().ToString("N"));

    public RegionMaskGeometryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // best effort
        }
    }

    // ---- string level (no ffmpeg) ----

    [Fact]
    public void A_safe_padded_span_conforms_its_mask_through_the_safe_box_not_the_static_cover_crop()
    {
        (ReframePlan plan, SourceFitPlan fits, VideoCompileStepExecutor.ResolvedSpan span) =
            Plan(VideoOutputFit.Subject, reframes: []);
        plan.SafePads(span).Should().BeTrue("an uncaptioned shot with no located subject is text_unknown");

        string chain = MaskChain(plan, fits, span)!;
        string graph = RegionEffectFilterBuilder.BuildMaskStream(
            3, StaticConform(fits), $"{Fps}/1", DurationSec, CanvasWidth, CanvasHeight,
            [new RegionMaskWindow(0.5, 2.0, 0.5, 2.0, span.SnappedStart, chain)], "[mask]");

        chain.Should().StartWith("scale=64:114:force_original_aspect_ratio=decrease,pad=72:128:");
        graph.Should().StartWith("[3:v]format=yuv420p,null[rm0in0]");
        graph.Should().Contain("[rm0in0]trim=start=0:end=2,setpts=PTS-STARTPTS," + chain + ",fps=10/1,setsar=1,trim=start=0.5,setpts=PTS-STARTPTS+0.5/TB[rm0seg0]");
        graph.Should().NotContain("crop=72:128", "the static cover crop is not this span's geometry");
    }

    [Fact]
    public void A_span_with_neither_a_reframe_nor_a_pad_keeps_the_single_stream_graph()
    {
        (ReframePlan plan, SourceFitPlan fits, VideoCompileStepExecutor.ResolvedSpan span) =
            Plan(VideoOutputFit.Crop, reframes: []);

        MaskChain(plan, fits, span).Should().BeNull();
    }

    // ---- real ffmpeg: the mask lands on the picture ----

    [FfmpegToolsFact]
    public void Under_the_safe_area_pad_the_mask_lands_on_the_padded_picture()
    {
        (ReframePlan plan, SourceFitPlan fits, VideoCompileStepExecutor.ResolvedSpan span) =
            Plan(VideoOutputFit.Subject, reframes: []);

        AssertMaskMatchesPicture(plan, fits, span, windowStart: 0.5);
        // And the old graph (the clip's static cover crop) really is somewhere else: this test catches it.
        MismatchFraction(RenderPicture(plan, fits, span), RenderMask(fits, span, chain: null, windowStart: 0.5), 0.5)
            .Should().BeGreaterThan(0.2);
    }

    [FfmpegToolsFact]
    public void Under_a_followed_cover_crop_the_mask_pans_and_zooms_with_the_picture()
    {
        VideoReframe follow = new(0, 0, null,
            [new ReframeKeyframe(0, 1, 0.3, 0.5), new ReframeKeyframe(1.5, 2, 0.75, 0.4, ReframeEasing.EaseInOut)])
        { Anchor = ReframeAnchor.Visible };
        (ReframePlan plan, SourceFitPlan fits, VideoCompileStepExecutor.ResolvedSpan span) =
            Plan(VideoOutputFit.Crop, reframes: [follow]);

        AssertMaskMatchesPicture(plan, fits, span, windowStart: 0.5);
    }

    [FfmpegToolsFact]
    public void Under_a_contain_pad_with_a_window_reframe_the_mask_follows_the_zoom()
    {
        VideoReframe zoom = new(0, 0, null, [new ReframeKeyframe(0, 1, 0.5, 0.5), new ReframeKeyframe(2, 2.5, 0.7, 0.4)]);
        (ReframePlan plan, SourceFitPlan fits, VideoCompileStepExecutor.ResolvedSpan span) =
            Plan(VideoOutputFit.Pad, reframes: [zoom]);

        // Shown whole (contain) the box is small, so its resampled edge is a larger share: a looser bar, still far
        // below what a misplaced mask scores (the static-conform mismatch in the safe-pad test is above 20%).
        AssertMaskMatchesPicture(plan, fits, span, windowStart: 0.3, maxMismatch: 0.10);
    }

    // ---- helpers ----

    private static (ReframePlan Plan, SourceFitPlan Fits, VideoCompileStepExecutor.ResolvedSpan Span) Plan(
        VideoOutputFit fit, IReadOnlyList<VideoReframe> reframes)
    {
        var config = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1,
            OutputFormat: VideoOutputFormat.Vertical, OutputFit: fit, Reframes: reframes);
        var artifact = new VideoAnalysisArtifact(
            Version: 2, Media: Source, Shots: [new VideoAnalysisShot("s0", 0, DurationSec, SourceIndex: 0)],
            SilenceSpans: [], Segments: [], Words: [], OfferedIds: ["s0"],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Sources: [new VideoAnalysisSourceInfo(0, "projects/p/userFiles/clip.mp4", Source, false)]);
        var span = new VideoCompileStepExecutor.ResolvedSpan(0, DurationSec, 0, DurationSec, 0, (long)(DurationSec * Fps), 0, ["s0"]);
        SourceFitPlan fits = SourceFitPlanner.Plan(config, artifact, [(0, 0.0, DurationSec)], _ => Source, Canvas, fixedFormat: true);
        VideoReframeValidation v = VideoReframe.Validate(reframes);
        ReframePlan plan = ReframePlanner.Build(config, v.Normalized, v.Clamps, artifact, [span], _ => Source, Canvas, fits, _ => false);
        return (plan, fits, span);
    }

    private static string? MaskChain(ReframePlan plan, SourceFitPlan fits, VideoCompileStepExecutor.ResolvedSpan span) =>
        plan.MaskChainFor(span, CanvasWidth, CanvasHeight, Fps, 1, normalized: true, fits.CoverFit, fits.CropFocus.GetValueOrDefault(0));

    private static string? StaticConform(SourceFitPlan fits) =>
        VideoCompileStepExecutor.BuildCanvasFitFilter(Source, Canvas, fits.CoverFit, fits.CropFocus.GetValueOrDefault(0));

    private void AssertMaskMatchesPicture(
        ReframePlan plan, SourceFitPlan fits, VideoCompileStepExecutor.ResolvedSpan span, double windowStart, double maxMismatch = 0.06)
    {
        byte[] picture = RenderPicture(plan, fits, span);
        byte[] mask = RenderMask(fits, span, MaskChain(plan, fits, span), windowStart);
        CountOn(picture, windowStart).Should().BeGreaterThan(50, "the box must be visible for the comparison to mean anything");
        MismatchFraction(picture, mask, windowStart).Should().BeLessThan(maxMismatch,
            "the mask must sit on the object it was traced from (only the resampled edge may differ)");
    }

    /// <summary>The picture branch exactly as the segmented encoder builds it for this span.</summary>
    private byte[] RenderPicture(ReframePlan plan, SourceFitPlan fits, VideoCompileStepExecutor.ResolvedSpan span)
    {
        CropFocus? focus = fits.CropFocus.GetValueOrDefault(0);
        string normalize = fits.CoverFit && focus is not { Pad: true }
            ? $",scale={CanvasWidth}:{CanvasHeight}:force_original_aspect_ratio=increase,{plan.VisibleCoverCrop(span, CanvasWidth, CanvasHeight, focus) ?? VideoCompileStepExecutor.CoverCropFilter(CanvasWidth, CanvasHeight, focus)},setsar=1,fps={Fps}/1"
            : $",scale={CanvasWidth}:{CanvasHeight}:force_original_aspect_ratio=decrease,pad={CanvasWidth}:{CanvasHeight}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={Fps}/1";
        IReadOnlyList<string> chain = ReframeFilterBuilder.SpanVideoChain(new ReframeSpanChain(
            0, 0, "0", "2", plan.StageFor(span, Fps, 1), normalize, plan.SafePads(span), plan.SafeMargin, false,
            CanvasWidth, CanvasHeight, Fps, 1, "", ""))!;
        return Render(string.Join(";", chain) + ";[v0]format=gray[out]", "picture");
    }

    private byte[] RenderMask(SourceFitPlan fits, VideoCompileStepExecutor.ResolvedSpan span, string? chain, double windowStart)
    {
        string graph = RegionEffectFilterBuilder.BuildMaskStream(
            0, StaticConform(fits), $"{Fps}/1", DurationSec, CanvasWidth, CanvasHeight,
            [new RegionMaskWindow(windowStart, DurationSec, windowStart, DurationSec, chain is null ? null : span.SnappedStart, chain)],
            "[out]");
        return Render(graph, "mask");
    }

    private byte[] Render(string graph, string name)
    {
        string box = Path.Combine(_root, "box.mkv");
        if (!File.Exists(box))
        {
            // An off-centre white box, so a crop, a pad and a zoom each put it somewhere different; large enough
            // that the resampled edge stays a small share of it even when the clip is shown whole (contain).
            Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", $"color=c=black:s={SourceWidth}x{SourceHeight}:r={Fps}:d={DurationSec}",
                "-vf", "format=yuv420p,drawbox=x=70:y=14:w=40:h=40:color=white:t=fill", "-c:v", "ffv1", box);
        }

        string output = Path.Combine(_root, $"{name}-{Guid.NewGuid():N}.gray");
        Run("ffmpeg", "-v", "error", "-i", box, "-filter_complex", graph, "-map", "[out]",
            "-frames:v", (Fps * DurationSec).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-pix_fmt", "gray", "-f", "rawvideo", output);
        return File.ReadAllBytes(output);
    }

    private static int FrameSize => CanvasWidth * CanvasHeight;

    private static IEnumerable<int> WindowFrames(double windowStart, int frames) =>
        Enumerable.Range((int)Math.Ceiling(windowStart * Fps) + 1, Math.Max(0, frames - (int)Math.Ceiling(windowStart * Fps) - 2));

    private static int CountOn(byte[] frames, double windowStart) =>
        WindowFrames(windowStart, frames.Length / FrameSize)
            .Sum(f => Enumerable.Range(f * FrameSize, FrameSize).Count(i => frames[i] > 127));

    /// <summary>Pixels where one says "object" and the other does not, over the pixels either says "object", inside the window.</summary>
    private static double MismatchFraction(byte[] picture, byte[] mask, double windowStart)
    {
        int frames = Math.Min(picture.Length, mask.Length) / FrameSize;
        int differ = 0, either = 0;
        foreach (int f in WindowFrames(windowStart, frames))
        {
            for (int i = f * FrameSize; i < (f + 1) * FrameSize; i++)
            {
                bool p = picture[i] > 127, m = mask[i] > 127;
                if (p || m) either++;
                if (p != m) differ++;
            }
        }

        return either == 0 ? 1 : differ / (double)either;
    }

    private static void Run(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException($"could not start {fileName}");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, because: $"{fileName} {string.Join(' ', arguments)} said: {stderr}");
    }
}
