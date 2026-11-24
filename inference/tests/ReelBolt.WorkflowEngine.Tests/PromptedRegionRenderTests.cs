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
/// The render half of the region-effects bar (plan §7), on a synthetic clip with exact ground
/// truth generated here rather than committed: a moving white box on a noisy test pattern, plus a
/// mask video whose box IS that box. Real ffmpeg, so <see cref="FfmpegToolsFactAttribute"/> skips it
/// on a host without the binary; the full fixture-based bar (dashboard clip, reframe, temporal
/// stability) is T-009's, on T-002's instrument.
///
/// <para>
/// What this file holds down: <b>isolation is exact</b> — outside the mask the composite is the
/// input picture, pixel for pixel, for every effect that is supposed to change only the object —
/// which is the property the alpha compositing exists for.
/// </para>
/// </summary>
public class PromptedRegionRenderTests : IDisposable
{
    private const int Width = 128;
    private const int Height = 72;
    private const int Fps = 10;
    private const double DurationSec = 1.0;
    private const int Frames = (int)(Fps * DurationSec);

    // The ground-truth object: a box drawn at x=40..76, y=20..50 on the canvas.
    private const int BoxX = 40;
    private const int BoxY = 20;
    private const int BoxW = 36;
    private const int BoxH = 30;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "prompted-region-render", Guid.NewGuid().ToString("N"));

    public PromptedRegionRenderTests() => Directory.CreateDirectory(_root);

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

    [FfmpegToolsFact]
    public void Blur_changes_the_object_and_leaves_every_pixel_outside_the_mask_alone()
    {
        string baseYuv = RenderBase();
        byte[] baseBytes = File.ReadAllBytes(baseYuv);

        byte[] rendered = Render(RegionEffect.Blur);
        IsOutsideMaskIdentical(baseBytes, rendered, BoxX, BoxY, BoxW, BoxH).Should().BeTrue(
            because: "the mask's alpha is 0 outside the object, so the composite must be the input picture");
        MeanAbsoluteDifferenceInside(baseBytes, rendered, BoxX, BoxY, BoxW, BoxH).Should().BeGreaterThan(1.0);
    }

    [FfmpegToolsFact]
    public void Pixelate_changes_the_object_and_nothing_else()
    {
        string baseYuv = RenderBase();
        byte[] baseBytes = File.ReadAllBytes(baseYuv);

        byte[] rendered = Render(RegionEffect.Pixelate);

        IsOutsideMaskIdentical(baseBytes, rendered, BoxX, BoxY, BoxW, BoxH).Should().BeTrue();
        // The block is 2% of the short side, so on this 72-px-tall clip it is clamped to 2 px and a
        // mean luma delta of ~0.9 is what a 2 px mosaic can do; the point here is that the object
        // changed and nothing outside it did.
        MeanAbsoluteDifferenceInside(baseBytes, rendered, BoxX, BoxY, BoxW, BoxH).Should().BeGreaterThan(0.25);
    }

    [FfmpegToolsFact]
    public void ColorPop_desaturates_only_the_complement_of_the_mask()
    {
        string baseYuv = RenderBase();
        byte[] baseBytes = File.ReadAllBytes(baseYuv);

        byte[] rendered = Render(RegionEffect.ColorPop);

        // Outside: luma may shift with the chroma, but the object keeps its own colour exactly.
        IsInsideMaskIdentical(baseBytes, rendered, BoxX, BoxY, BoxW, BoxH).Should().BeTrue(
            because: "the object keeps colour, so its own pixels are the ones left untouched");
        ChromaOutsideChanged(baseBytes, rendered, BoxX, BoxY, BoxW, BoxH).Should().BeTrue();
    }

    [FfmpegToolsFact]
    public void Invert_swaps_which_side_of_the_mask_changes()
    {
        string baseYuv = RenderBase();
        byte[] baseBytes = File.ReadAllBytes(baseYuv);

        byte[] rendered = Render(RegionEffect.Blur, invert: true);

        IsInsideMaskIdentical(baseBytes, rendered, BoxX, BoxY, BoxW, BoxH).Should().BeTrue(
            because: "an inverted mask applies the effect to everything EXCEPT the object");
        IsOutsideMaskIdentical(baseBytes, rendered, BoxX, BoxY, BoxW, BoxH).Should().BeFalse();
    }

    [FfmpegToolsFact]
    public void A_frame_whose_mask_is_empty_is_byte_identical_to_the_un_effected_frame()
    {
        // Frame 3 of the mask is entirely black — the object is hidden, the "occlusion" case.
        string maskPath = WriteMask(blankFrames: new[] { 3 });
        string baseYuv = RenderBase();
        byte[] baseBytes = File.ReadAllBytes(baseYuv);

        byte[] rendered = Render(RegionEffect.Blur, maskPath: maskPath);

        int frameSize = Width * Height * 3 / 2;
        int offset = 3 * frameSize;
        rendered.Skip(offset).Take(Width * Height).Should().Equal(
            baseBytes.Skip(offset).Take(Width * Height),
            because: "no mask means no effect, and the composite copies the picture through");
        IsOutsideMaskIdentical(baseBytes, rendered, BoxX, BoxY, BoxW, BoxH).Should().BeTrue();
    }

    [FfmpegToolsFact]
    public void A_mask_from_a_larger_source_lands_on_the_object_after_the_pictures_own_conform()
    {
        // The mask is at 2x the canvas, exactly as a 4K source's mask is at 4K while the canvas is
        // 1080p: the SAME conform filter the picture gets must put the effect back on the object.
        string maskPath = WriteMask(scale: 2);
        string baseYuv = RenderBase();
        byte[] baseBytes = File.ReadAllBytes(baseYuv);

        byte[] rendered = Render(
            RegionEffect.Blur,
            maskPath: maskPath,
            conform: $"scale={Width}:{Height}:flags=neighbor");

        IsOutsideMaskIdentical(baseBytes, rendered, BoxX, BoxY, BoxW, BoxH).Should().BeTrue(
            because: "a correctly conformed mask must not smear the effect outside the object either");
        (double cx, double cy) = ChangedCentroid(baseBytes, rendered);
        cx.Should().BeApproximately(BoxX + BoxW / 2.0, 2.0);
        cy.Should().BeApproximately(BoxY + BoxH / 2.0, 2.0);
    }

    [FfmpegToolsFact]
    public void The_same_plan_renders_the_same_bytes_twice()
    {
        byte[] first = Render(RegionEffect.Spotlight);
        byte[] second = Render(RegionEffect.Spotlight);

        first.Should().Equal(second, because: "rendering is deterministic: identical config, identical bytes");
    }

    // ---------------------------------------------------------------------------------------
    // Fixture
    // ---------------------------------------------------------------------------------------

    private string BaseVideo => Path.Combine(_root, "base.mkv");

    private string RenderBase()
    {
        if (!File.Exists(BaseVideo))
        {
            Run("ffmpeg", "-v", "error", "-f", "lavfi", "-i", $"testsrc2=s={Width}x{Height}:r={Fps}:d={DurationSec}",
                "-pix_fmt", "yuv420p", BaseVideo);
        }

        string yuv = Path.Combine(_root, "base.yuv");
        if (!File.Exists(yuv))
            Run("ffmpeg", "-v", "error", "-i", BaseVideo, "-pix_fmt", "yuv420p", "-f", "rawvideo", yuv);

        return yuv;
    }

    /// <summary>A mask video at the canvas size (or <paramref name="scale"/>x it) holding the ground-truth box, white on black.</summary>
    private string WriteMask(int scale = 1, IReadOnlyList<int>? blankFrames = null)
    {
        string path = Path.Combine(_root, $"mask-{scale}-{string.Join('_', blankFrames ?? Array.Empty<int>())}.mkv");
        int w = Width * scale, h = Height * scale;
        int x = BoxX * scale, y = BoxY * scale, bw = BoxW * scale, bh = BoxH * scale;

        // The box is on every frame except the blanked ones, where the mask stays pure black — the
        // "object is hidden" case the occlusion check needs.
        string box = blankFrames is { Count: > 0 }
            ? $",drawbox=x={x}:y={y}:w={bw}:h={bh}:color=white:t=fill:enable='{string.Join("*", blankFrames.Select(f => $"not(eq(n,{f}))"))}'"
            : $",drawbox=x={x}:y={y}:w={bw}:h={bh}:color=white:t=fill";

        // -y and -nostdin: the same mask is written twice by the determinism test, and ffmpeg must never stop to ask
        Run("ffmpeg", "-nostdin", "-y", "-v", "error", "-f", "lavfi", "-i", $"color=c=black:s={w}x{h}:r={Fps}:d={DurationSec}",
            "-vf", $"format=yuv420p{box}", "-pix_fmt", "yuv420p", path);
        return path;
    }

    private byte[] Render(
        RegionEffect effect, bool invert = false, string? maskPath = null, string? conform = null, double strength = 1.0)
    {
        RenderBase();
        string mask = maskPath ?? WriteMask();
        string fps = $"{Fps}/1";
        IReadOnlyList<RegionMaskWindow> windows = new[] { new RegionMaskWindow(0, DurationSec, 0, DurationSec) };

        string graph =
            RegionEffectFilterBuilder.BuildMaskStream(1, conform, fps, DurationSec, Width, Height, windows, "[rm]") + ";" +
            RegionEffectFilterBuilder.BuildMaskHelperChain("[rm]", "[m]", invert, 0, 1) + ";" +
            "[0:v]format=yuv420p[base];" +
            RegionEffectFilterBuilder.BuildEffectChain("[base]", "[m]", "[out]", 0, effect, Width, Height, strength, fps, DurationSec);

        string outPath = Path.Combine(_root, $"out-{effect}-{invert}-{Guid.NewGuid():N}.yuv");
        Run("ffmpeg", "-v", "error", "-i", BaseVideo, "-i", mask, "-filter_complex", graph,
            "-map", "[out]", "-pix_fmt", "yuv420p", "-f", "rawvideo", outPath);
        return File.ReadAllBytes(outPath);
    }

    private static int FrameSize => Width * Height * 3 / 2;

    private static bool IsOutsideMaskIdentical(byte[] baseBytes, byte[] rendered, int x, int y, int w, int h)
    {
        for (int frame = 0; frame < Frames; frame++)
        {
            int luma = frame * FrameSize;
            for (int py = 0; py < Height; py++)
            {
                for (int px = 0; px < Width; px++)
                {
                    if (px >= x && px < x + w && py >= y && py < y + h)
                        continue;

                    if (baseBytes[luma + py * Width + px] != rendered[luma + py * Width + px])
                        return false;
                }
            }
        }

        return true;
    }

    private static bool IsInsideMaskIdentical(byte[] baseBytes, byte[] rendered, int x, int y, int w, int h)
    {
        for (int frame = 0; frame < Frames; frame++)
        {
            int luma = frame * FrameSize;
            for (int py = y + 2; py < y + h - 2; py++)
            {
                for (int px = x + 2; px < x + w - 2; px++)
                {
                    if (baseBytes[luma + py * Width + px] != rendered[luma + py * Width + px])
                        return false;
                }
            }
        }

        return true;
    }

    private static double MeanAbsoluteDifferenceInside(byte[] baseBytes, byte[] rendered, int x, int y, int w, int h)
    {
        double total = 0;
        int count = 0;
        for (int frame = 0; frame < Frames; frame++)
        {
            int luma = frame * FrameSize;
            for (int py = y + 2; py < y + h - 2; py++)
            {
                for (int px = x + 2; px < x + w - 2; px++)
                {
                    total += Math.Abs(baseBytes[luma + py * Width + px] - rendered[luma + py * Width + px]);
                    count++;
                }
            }
        }

        return total / Math.Max(1, count);
    }

    private static bool ChromaOutsideChanged(byte[] baseBytes, byte[] rendered, int x, int y, int w, int h)
    {
        int frame = 0;
        int u = frame * FrameSize + Width * Height;
        for (int py = 0; py < Height / 2; py++)
        {
            for (int px = 0; px < Width / 2; px++)
            {
                bool inside = px * 2 >= x && px * 2 < x + w && py * 2 >= y && py * 2 < y + h;
                if (!inside && baseBytes[u + py * (Width / 2) + px] != rendered[u + py * (Width / 2) + px])
                    return true;
            }
        }

        return false;
    }

    private static (double X, double Y) ChangedCentroid(byte[] baseBytes, byte[] rendered)
    {
        double sumX = 0, sumY = 0;
        int count = 0;
        for (int py = 0; py < Height; py++)
        {
            for (int px = 0; px < Width; px++)
            {
                if (baseBytes[py * Width + px] == rendered[py * Width + px])
                    continue;

                sumX += px;
                sumY += py;
                count++;
            }
        }

        return count == 0 ? (double.NaN, double.NaN) : (sumX / count, sumY / count);
    }

    /// <summary>
    /// A union of more than two masks must be a graph ffmpeg can PARSE. <c>blend</c> takes exactly two
    /// inputs and has no <c>inputs</c> option (ffmpeg rejects it at graph parse), so the pairwise fold is
    /// the only valid shape; <c>RegionEffectResolutionTests</c> pins that the builder emits it.
    /// </summary>
    [FfmpegToolsFact]
    public void A_three_way_mask_union_is_a_graph_ffmpeg_can_parse()
    {
        Run("ffmpeg", "-v", "error",
            "-f", "lavfi", "-i", $"color=c=black:s={Width}x{Height}:r={Fps}:d={DurationSec}",
            "-f", "lavfi", "-i", $"color=c=white:s={Width}x{Height}:r={Fps}:d={DurationSec}",
            "-f", "lavfi", "-i", $"color=c=black:s={Width}x{Height}:r={Fps}:d={DurationSec}",
            "-filter_complex",
            "[0]format=gray[m0];[1]format=gray[m1];[2]format=gray[m2];" +
            "[m0][m1]blend=all_mode=lighten[u1];[u1][m2]blend=all_mode=lighten[u2]",
            "-map", "[u2]", "-frames:v", "1", "-f", "null", "-");
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
