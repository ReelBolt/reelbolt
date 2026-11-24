#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Production run 5d420a0f ("IG reel: effect demos", step 6): a 1280x720 cafe clip rendered to 1080x1920 with
/// OutputFit=Crop and a Strength 2, FeatherPx 24 blur on "human face". These pin the two halves of that report:
/// (1) the mask is warped by exactly the picture's own geometry on the static (no reframe, no safe pad) Crop and
/// Pad paths, for an OFF-CENTRE object that MOVES (the older geometry tests use a still box, which cannot see a
/// time offset); (2) an anonymising effect covers the whole object: a feather centred on the object's edge left the
/// outer band of the face (profile, nose, eyes) partly un-blurred, so the mask is now grown before it is feathered.
/// </summary>
public sealed class RegionMaskCropGeometryTests : IDisposable
{
    // The production geometry, at a tenth of the size (same aspect ratios, same cover factor 2.667).
    private const int SourceWidth = 128;
    private const int SourceHeight = 72;
    private const int CanvasWidth = 108;
    private const int CanvasHeight = 192;
    private const int Fps = 10;
    private const double DurationSec = 2.0;

    private static readonly VideoAnalysisMedia Source = new(DurationSec, Fps, 1, SourceWidth, SourceHeight);
    private static readonly VideoAnalysisMedia Canvas = new(DurationSec, Fps, 1, CanvasWidth, CanvasHeight);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "region-mask-crop", Guid.NewGuid().ToString("N"));

    public RegionMaskCropGeometryTests() => Directory.CreateDirectory(_root);

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

    // ---- pure geometry (no ffmpeg) ----

    [Fact]
    public void A_1280x720_clip_cropped_to_1080x1920_puts_an_off_centre_object_where_the_cover_crop_does()
    {
        var source = new VideoAnalysisMedia(10.28, 25, 1, 1280, 720);
        var canvas = new VideoAnalysisMedia(10.28, 25, 1, 1080, 1920);

        // Cover: scale 1920/720 = 2.6667 -> 3413.3 x 1920, centred crop: 1166.7 px cut on the left.
        VideoCompileStepExecutor.SourceCanvasFit fit = VideoCompileStepExecutor.SourceCanvasFit.For(source, canvas, cover: true);
        fit.Width.Should().BeApproximately(3413.33, 0.5);
        fit.Height.Should().BeApproximately(1920, 0.5);
        fit.OffsetX.Should().BeApproximately(-1166.67, 0.5);
        fit.OffsetY.Should().BeApproximately(0, 0.5);

        // A face right of centre, high in the frame: source x 700..780, y 150..250.
        VideoAnalysisRect face = new(700 / 1280.0, 150 / 720.0, 80 / 1280.0, 100 / 720.0);
        VideoAnalysisRect onCanvas = VideoCompileStepExecutor.MapRectThroughFit(face, fit, canvas)!;
        (onCanvas.X * 1080).Should().BeApproximately(700 * 1920 / 720.0 - 1166.67, 1);
        (onCanvas.Y * 1920).Should().BeApproximately(400, 1);
        (onCanvas.W * 1080).Should().BeApproximately(213.3, 1);
        (onCanvas.H * 1920).Should().BeApproximately(266.7, 1);

        // The filter the picture gets and the one its mask gets are the same string (the mask is conformed with
        // the picture's own BuildCanvasFitFilter, never a re-derivation), and it is the centred cover crop.
        string? picture = VideoCompileStepExecutor.BuildCanvasFitFilter(source, canvas, cover: true);
        picture.Should().Be("scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1");
    }

    [Fact]
    public void Anonymising_effects_grow_the_mask_before_feathering_and_the_others_do_not()
    {
        RegionEffectFilterBuilder.Anonymises(RegionEffect.Blur).Should().BeTrue();
        RegionEffectFilterBuilder.Anonymises(RegionEffect.Pixelate).Should().BeTrue();
        RegionEffectFilterBuilder.Anonymises(RegionEffect.Highlight).Should().BeFalse();
        RegionEffectFilterBuilder.Anonymises(RegionEffect.ColorPop).Should().BeFalse();

        RegionEffectFilterBuilder.BuildMaskHelperChain("[in]", "[out]", invert: false, featherPx: 24, opacity: 1, coverObject: true)
            .Should().Be("[in]gblur=sigma=24,lut=y='if(gte(val,6),255,0)',gblur=sigma=24[out]");
        // No feather: the hard edge already covers the object. Inverted: the soft edge belongs to the rest of the picture.
        RegionEffectFilterBuilder.BuildMaskHelperChain("[in]", "[out]", invert: false, featherPx: 0, opacity: 1, coverObject: true)
            .Should().Be("[in]null[out]");
        RegionEffectFilterBuilder.BuildMaskHelperChain("[in]", "[out]", invert: true, featherPx: 24, opacity: 1, coverObject: true)
            .Should().Be("[in]negate,gblur=sigma=24[out]");
        // The default keeps every existing caller (the editor's renderer) byte-identical.
        RegionEffectFilterBuilder.BuildMaskHelperChain("[in]", "[out]", invert: false, featherPx: 24, opacity: 1)
            .Should().Be("[in]gblur=sigma=24[out]");
    }

    // ---- real ffmpeg ----

    [FfmpegToolsFact]
    public void Under_a_static_cover_crop_the_mask_of_a_moving_off_centre_object_lands_on_it() =>
        AssertMaskMatchesPicture(cover: true);

    [FfmpegToolsFact]
    public void Under_a_static_pad_the_mask_of_a_moving_off_centre_object_lands_on_it() =>
        AssertMaskMatchesPicture(cover: false);

    [FfmpegToolsFact]
    public void A_feathered_blur_at_strength_2_leaves_no_detail_on_any_part_of_the_object()
    {
        const int w = 216, h = 384;
        // "Face": a fine checker (eyes, nose and mouth stand-in) inside an ellipse; the mask is that ellipse.
        string picture = $"color=c=black:s={w}x{h}:r=10:d=1,format=gray,geq=lum='if(mod(floor(X/3)+floor(Y/3),2),200,50)',format=yuv420p";
        string mask = $"color=c=black:s={w}x{h}:r=10:d=1,format=gray,geq=lum='if(lte(pow((X-110)/50,2)+pow((Y-190)/65,2),1),255,0)'";
        const double feather = 12, objectSize = 100;

        byte[] covered = RenderEffect(picture, mask, w, h, feather, objectSize, coverObject: true);
        byte[] centred = RenderEffect(picture, mask, w, h, feather, objectSize, coverObject: false);

        // The outer band of the object (its last 2 feather sigmas): where the profile, the eyes and the nose sit.
        bool InBand(int x, int y)
        {
            double r = Math.Sqrt(Math.Pow((x - 110) / 50.0, 2) + Math.Pow((y - 190) / 65.0, 2));
            return r <= 1 && r >= 1 - (2 * feather / 50.0);
        }

        double Detail(byte[] frame) => Enumerable.Range(0, w * h)
            .Where(i => InBand(i % w, i / w))
            .Average(i => Math.Abs(frame[i] - 125.0));

        // The checker is 75 grey levels either side of 125; a blur that makes it unreadable leaves almost none.
        Detail(centred).Should().BeGreaterThan(12, "the old centred feather let the sharp picture through at the object's edge");
        Detail(covered).Should().BeLessThan(8, "every part of the object must be blurred past reading");
    }

    // ---- helpers ----

    private void AssertMaskMatchesPicture(bool cover)
    {
        const double windowStart = 0.5;
        string? conform = VideoCompileStepExecutor.BuildCanvasFitFilter(Source, Canvas, cover);
        conform.Should().NotBeNull();

        string box = BoxVideo();
        byte[] picture = Render(box, $"[0:v]format=yuv420p,{conform},fps={Fps}/1,format=gray[out]", "picture");
        byte[] mask = Render(box, RegionEffectFilterBuilder.BuildMaskStream(
            0, conform, $"{Fps}/1", DurationSec, CanvasWidth, CanvasHeight,
            [new RegionMaskWindow(windowStart, DurationSec, windowStart, DurationSec)], "[out]"), "mask");

        CountOn(picture).Should().BeGreaterThan(100, "the moving box must be visible for the comparison to mean anything");
        Mismatch(picture, mask, windowStart).Should().BeLessThan(0.10, "the mask must sit on the object, frame by frame, as it moves");

        // And the comparison does catch a mask warped by any other geometry (the other fit).
        string other = VideoCompileStepExecutor.BuildCanvasFitFilter(Source, Canvas, !cover)!;
        byte[] misplaced = Render(box, RegionEffectFilterBuilder.BuildMaskStream(
            0, other, $"{Fps}/1", DurationSec, CanvasWidth, CanvasHeight,
            [new RegionMaskWindow(windowStart, DurationSec, windowStart, DurationSec)], "[out]"), "misplaced");
        Mismatch(picture, misplaced, windowStart).Should().BeGreaterThan(0.3);
    }

    private static int CountOn(byte[] frames) => frames.Count(v => v > 127);

    private static double Mismatch(byte[] picture, byte[] mask, double windowStart)
    {
        int frameSize = CanvasWidth * CanvasHeight;
        int frames = Math.Min(picture.Length, mask.Length) / frameSize;
        int differ = 0, either = 0;
        for (int f = (int)Math.Ceiling(windowStart * Fps) + 1; f < frames - 1; f++)
        {
            for (int i = f * frameSize; i < (f + 1) * frameSize; i++)
            {
                bool p = picture[i] > 127, m = mask[i] > 127;
                if (p || m) either++;
                if (p != m) differ++;
            }
        }

        return either == 0 ? 1 : differ / (double)either;
    }

    /// <summary>A white box moving right and down, off-centre but inside the centre cover crop of 128x72 -> 108x192.</summary>
    private string BoxVideo()
    {
        string box = Path.Combine(_root, "box.mkv");
        if (!File.Exists(box))
        {
            // drawbox has no clock (its `t` is the thickness), so the moving box is an overlay, which does.
            string d = DurationSec.ToString(CultureInfo.InvariantCulture);
            Run("-filter_complex",
                $"color=c=black:s={SourceWidth}x{SourceHeight}:r={Fps}:d={d}[bg];color=c=white:s=24x24:r={Fps}:d={d}[fg];" +
                "[bg][fg]overlay=x='52+4*t':y='8+10*t',format=yuv420p[out]",
                "-map", "[out]", "-c:v", "ffv1", box);
        }

        return box;
    }

    private byte[] RenderEffect(string picture, string mask, int w, int h, double feather, double objectSize, bool coverObject)
    {
        string graph =
            $"{picture}[pic];{mask}[msk];" +
            RegionEffectFilterBuilder.BuildMaskHelperChain("[msk]", "[m]", invert: false, feather, 1, coverObject) + ";" +
            RegionEffectFilterBuilder.BuildEffectChain("[pic]", "[m]", "[fx]", 0, RegionEffect.Blur, w, h, 2, "10/1", 1, objectSize) +
            ";[fx]format=gray[out]";
        string output = Path.Combine(_root, $"fx-{Guid.NewGuid():N}.gray");
        Run("-filter_complex", graph, "-map", "[out]", "-frames:v", "1", "-pix_fmt", "gray", "-f", "rawvideo", output);
        return File.ReadAllBytes(output);
    }

    private byte[] Render(string input, string graph, string name)
    {
        string output = Path.Combine(_root, $"{name}-{Guid.NewGuid():N}.gray");
        Run("-i", input, "-filter_complex", graph, "-map", "[out]",
            "-frames:v", (Fps * DurationSec).ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", "gray", "-f", "rawvideo", output);
        return File.ReadAllBytes(output);
    }

    private static void Run(params string[] arguments)
    {
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        info.ArgumentList.Add("-v");
        info.ArgumentList.Add("error");
        info.ArgumentList.Add("-y");
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("could not start ffmpeg");
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, because: $"ffmpeg {string.Join(' ', arguments)} said: {stderr}");
    }
}
