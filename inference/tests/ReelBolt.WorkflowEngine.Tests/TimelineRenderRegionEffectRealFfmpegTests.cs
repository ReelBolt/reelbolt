#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Magic blur: <c>MediaClip.regionEffects</c> rendered end to end through the REAL ffmpeg. The string tests in
/// <see cref="TimelineRenderRegionEffectTests"/> pin the graph text; these prove ffmpeg accepts it and that the
/// effect is confined to the mask. Inputs are synthesized by ffmpeg lavfi at test time: a noisy high-frequency
/// picture and a gray FFV1 mask video (white box on black). Skipped, visibly, where no ffmpeg is on PATH.
/// </summary>
public sealed class TimelineRenderRegionEffectRealFfmpegTests : IDisposable
{
    private const int W = 320;
    private const int H = 180;
    // The static mask box: x 80..199, y 45..134.
    private const int BoxX = 80, BoxY = 45, BoxW = 120, BoxH = 90;

    private static readonly Guid ProjectId = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Analysis = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly EditTimelineSettings Settings = new(W, H, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);
    private static readonly SegmentationMaskRef MaskRef = new(Analysis, "g1");

    private readonly string _work = Path.Combine(Path.GetTempPath(), "tl-re-real-" + Guid.NewGuid().ToString("N"));

    public TimelineRenderRegionEffectRealFfmpegTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── (a)+(b): every effect word, confined to / inverted against the mask ──

    [FfmpegFact]
    public async Task Blur_changes_only_inside_the_box()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(1);
        string plain = await RenderOk(Doc(Clip(asset, null)), assets, masks);
        string fx = await RenderOk(Doc(Clip(asset, [Fx("blur", 1)])), assets, masks);

        AssertDeliveryFacts(fx, 1.0);
        Inside(plain, fx).Should().BeGreaterThan(8, "the box is blurred");
        Outside(plain, fx).Should().BeLessThan(3, "the rest of the frame is untouched");
    }

    [FfmpegFact]
    public async Task Blur_inverted_changes_only_outside_the_box()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(1);
        string plain = await RenderOk(Doc(Clip(asset, null)), assets, masks);
        string fx = await RenderOk(Doc(Clip(asset, [Fx("blur", 1, invert: true)])), assets, masks);

        Outside(plain, fx).Should().BeGreaterThan(8, "invert blurs everything but the box");
        Inside(plain, fx).Should().BeLessThan(3, "the box is kept sharp");
    }

    [FfmpegFact]
    public async Task Pixelate_changes_only_inside_the_box()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(1);
        string plain = await RenderOk(Doc(Clip(asset, null)), assets, masks);
        string fx = await RenderOk(Doc(Clip(asset, [Fx("pixelate", 2)])), assets, masks);

        AssertDeliveryFacts(fx, 1.0);
        Inside(plain, fx).Should().BeGreaterThan(8);
        Outside(plain, fx).Should().BeLessThan(3);
    }

    [FfmpegFact]
    public async Task Highlight_brightens_the_box_and_dims_the_rest()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(1);
        string plain = await RenderOk(Doc(Clip(asset, null)), assets, masks);
        string fx = await RenderOk(Doc(Clip(asset, [Fx("highlight", 1)])), assets, masks);

        AssertDeliveryFacts(fx, 1.0);
        SignedLuma(plain, fx, InsideRect).Should().BeGreaterThan(4, "the box is lifted");
        SignedLuma(plain, fx, OutsideRects).Should().BeLessThan(-8, "highlight dims the surround by design");
    }

    [FfmpegFact]
    public async Task Outline_draws_a_ring_just_outside_the_box_and_leaves_the_rest_alone()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(1);
        string plain = await RenderOk(Doc(Clip(asset, null)), assets, masks);
        string fx = await RenderOk(Doc(Clip(asset, [Fx("outline", 2)])), assets, masks);

        AssertDeliveryFacts(fx, 1.0);
        // The ring hugs the silhouette, 1-2 px outside its left edge.
        MeanDiff(plain, fx, new Rect(BoxX - 4, BoxY + 10, 4, BoxH - 20)).Should().BeGreaterThan(20, "the ring is drawn beside the box edge");
        Inside(plain, fx).Should().BeLessThan(3, "the interior of the object is untouched");
        Outside(plain, fx).Should().BeLessThan(3, "far from the edge nothing changes");
    }

    [FfmpegFact]
    public async Task Spotlight_dims_outside_the_box_and_inverted_dims_inside()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(1);
        string plain = await RenderOk(Doc(Clip(asset, null)), assets, masks);
        string fx = await RenderOk(Doc(Clip(asset, [Fx("spotlight", 1)])), assets, masks);
        string inv = await RenderOk(Doc(Clip(asset, [Fx("spotlight", 1, invert: true)])), assets, masks);

        AssertDeliveryFacts(fx, 1.0);
        Outside(plain, fx).Should().BeGreaterThan(30, "the surround goes dark");
        Inside(plain, fx).Should().BeLessThan(3, "the subject stays lit");
        Inside(plain, inv).Should().BeGreaterThan(30, "inverted, the box goes dark");
        Outside(plain, inv).Should().BeLessThan(3);
    }

    [FfmpegFact]
    public async Task ColorPop_desaturates_outside_the_box_and_keeps_it_in_colour()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(1);
        string plain = await RenderOk(Doc(Clip(asset, null)), assets, masks);
        string fx = await RenderOk(Doc(Clip(asset, [Fx("colorpop", 1)])), assets, masks);

        AssertDeliveryFacts(fx, 1.0);
        Outside(plain, fx).Should().BeGreaterThan(8, "the surround loses its colour");
        Inside(plain, fx).Should().BeLessThan(3, "the subject keeps its colour");
        Chroma(fx, OutsideRects).Should().BeLessThan(6, "outside is grey");
        Chroma(fx, InsideRect).Should().BeGreaterThan(Chroma(fx, OutsideRects) + 10);
    }

    // ── (c) both a segmentation mask and a regionEffects entry ──

    [FfmpegFact]
    public async Task A_clip_with_both_a_segmentation_mask_and_a_region_effect_renders()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(1);
        // The clip's own matte keeps only the box (black elsewhere over the empty track); the effect pixelates it.
        MediaClip clip = Clip(asset, [Fx("pixelate", 2)], mask: SegMask());
        string plain = await RenderOk(Doc(Clip(asset, null, mask: SegMask())), assets, masks);
        string output = await RenderOk(Doc(clip), assets, masks);

        AssertDeliveryFacts(output, 1.0);
        Inside(plain, output).Should().BeGreaterThan(8, "the effect still reaches the matted box");
        MeanLuma(output, OutsideRects).Should().BeLessThan(10, "the clip's own matte still hides the rest");
    }

    // ── (d) across a transition leg ──

    [FfmpegFact]
    public async Task A_region_effect_across_a_transition_leg_renders()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(2);
        MediaClip from = Clip(asset, [Fx("blur", 1)], recordOut: 15);
        MediaClip to = Clip(asset, null, recordIn: 25, recordOut: 40, sourceIn: 15);
        Transition fade = new(Guid.NewGuid(), "x", 15, 25, "fade");

        string output = await RenderOk(Doc(from, fade, to), assets, masks);

        AssertDeliveryFacts(output, 40 / 30.0);
        MeanLuma(output, InsideRect, atSec: 20 / 30.0).Should().BeGreaterThan(5, "a frame mid-transition decodes with picture");
    }

    // ── (e) speed and reverse keep the box aligned ──

    [FfmpegFact]
    public async Task Speed_two_keeps_the_effect_on_the_moving_box()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(2, moving: true);
        // Output 1 s at 2x consumes 2 s of source; the box left edge is 20 + 100 t in source time.
        (int left1, int right1) = await ChangedColumns(asset, assets, masks, rate: 1.0, reverse: false, recordOut: 30, atSec: 0.5);
        (int left2, int right2) = await ChangedColumns(asset, assets, masks, rate: 2.0, reverse: false, recordOut: 30, atSec: 0.25);

        // 1x at u=0.5 is source time 0.5 (x 70..149); 2x at u=0.25 is source time 0.5 as well.
        left1.Should().BeInRange(70 - 12, 70 + 12);
        right1.Should().BeInRange(150 - 12, 150 + 12);
        left2.Should().BeInRange(left1 - 12, left1 + 12, "same source time, same place, whatever the speed");
        right2.Should().BeInRange(right1 - 12, right1 + 12);
        (int left3, _) = await ChangedColumns(asset, assets, masks, rate: 2.0, reverse: false, recordOut: 30, atSec: 0.75);
        left3.Should().BeGreaterThan(left2 + 60, "at 2x the box travels twice as fast over the output");
    }

    [FfmpegFact]
    public async Task Reverse_at_speed_two_keeps_the_effect_on_the_moving_box()
    {
        (AssetRef asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, Dictionary<AssetRef, LocalAsset> assets) = Stage(2, moving: true);
        // Reversed 2x: output u shows source time 2 - 2u. u=0.25 -> 1.5 s -> box left edge 170.
        (int left, int right) = await ChangedColumns(asset, assets, masks, rate: 2.0, reverse: true, recordOut: 30, atSec: 0.25);

        left.Should().BeInRange(170 - 14, 170 + 14);
        right.Should().BeInRange(250 - 14, 250 + 14);
    }

    // ── helpers: composition ──

    private async Task<(int Left, int Right)> ChangedColumns(AssetRef asset, Dictionary<AssetRef, LocalAsset> assets,
        Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks, double rate, bool reverse, long recordOut, double atSec)
    {
        string plain = await RenderOk(Doc(Clip(asset, null, rate: rate, reverse: reverse, recordOut: recordOut)), assets, masks);
        string fx = await RenderOk(Doc(Clip(asset, [Fx("pixelate", 2)], rate: rate, reverse: reverse, recordOut: recordOut)), assets, masks);
        AssertDeliveryFacts(fx, recordOut / 30.0);
        byte[] a = Frame(plain, atSec), b = Frame(fx, atSec);
        // A column "changed" when its mean abs diff over the box rows is clearly above the encode noise.
        List<int> cols = [];
        for (int x = 0; x < W; x++)
        {
            double sum = 0;
            int n = 0;
            for (int y = BoxY + 5; y < BoxY + BoxH - 5; y++)
            {
                int i = (y * W + x) * 3;
                sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
                n += 3;
            }

            if (sum / n > 4.5) cols.Add(x);
        }

        cols.Should().NotBeEmpty("the effect must touch the mask box");
        // The box is the longest run of changed columns (gaps of <= 3 allowed); lone noisy columns are not the box.
        (int Left, int Right) best = (cols[0], cols[0]), run = (cols[0], cols[0]);
        foreach (int c in cols.Skip(1))
        {
            run = c - run.Right <= 4 ? (run.Left, c) : (c, c);
            if (run.Right - run.Left > best.Right - best.Left) best = run;
        }

        return best;
    }

    private MediaClip Clip(AssetRef asset, IReadOnlyList<RegionClipEffect>? effects, ShapeMask? mask = null, double rate = 1.0, bool reverse = false,
        long recordIn = 0, long recordOut = 30, long sourceIn = 0) =>
        new(Guid.NewGuid(), "clip", recordIn, recordOut, asset, sourceIn, rate, true, null,
            null, null, null, reverse, false, 0, 0, null, mask, RegionEffects: effects);

    private static ShapeMask SegMask(bool invert = false) =>
        new(ShapeMask.SegmentationKind, Invert: invert, AnalysisStepResultId: Analysis, TrackIds: ["g1"], FeatherPx: 0);

    private static RegionClipEffect Fx(string word, double strength, bool invert = false) =>
        new(word, strength, SegMask(invert), true);

    private static EditTimeline Doc(MediaClip clip) => Doc(items: [clip]);

    private static EditTimeline Doc(MediaClip from, Transition transition, MediaClip to) => Doc(items: [from, transition, to]);

    private static EditTimeline Doc(Item[] items) =>
        new(Guid.NewGuid(), ProjectId, "doc", Settings,
            [new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items)],
            [], null, null, null);

    /// <summary>A noisy picture and a same-size gray FFV1 mask video, staged like production stages them.</summary>
    private (AssetRef Asset, Dictionary<SegmentationMaskRef, LocalSegmentationMask> Masks, Dictionary<AssetRef, LocalAsset> Assets) Stage(int seconds, bool moving = false)
    {
        string src = Path.Combine(_work, $"src-{Guid.NewGuid():N}.mp4");
        Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", moving
                ? $"nullsrc=s={W}x{H}:r=30:d={seconds},geq=lum='if(mod(floor(X/3)+floor(Y/3),2),215,40)':cb=128:cr=128" // hf everywhere: the box edge must show wherever it is
                : $"testsrc2=s={W}x{H}:r=30:d={seconds},noise=alls=15:allf=t+u",
            "-pix_fmt", "yuv420p", "-c:v", "libx264", "-crf", "8", "-g", "15", src]);

        string mask = Path.Combine(_work, $"mask-{Guid.NewGuid():N}.mkv");
        string graph = moving
            ? $"color=c=black:s={W}x{H}:r=30:d={seconds}[bg];color=c=white:s=80x{BoxH}:r=30:d={seconds}[bx];[bg][bx]overlay=x='20+t*100':y={BoxY}:eval=frame,format=gray"
            : $"color=c=black:s={W}x{H}:r=30:d={seconds},drawbox=x={BoxX}:y={BoxY}:w={BoxW}:h={BoxH}:color=white:t=fill,format=gray";
        Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", graph, "-frames:v", (seconds * 30).ToString(CultureInfo.InvariantCulture), "-c:v", "ffv1", mask]);

        AssetRef reference = new(AssetRefKind.ProjectFile, Guid.NewGuid(), null);
        Dictionary<AssetRef, LocalAsset> assets = new()
        {
            [reference] = new LocalAsset(src, $"src|{Guid.NewGuid():N}", new MediaProbeResult(seconds, 30, 1, W, H, "h264", null, null)),
        };
        Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks = new() { [MaskRef] = new LocalSegmentationMask(mask, $"mask|{Guid.NewGuid():N}") };
        return (reference, masks, assets);
    }

    private static FfmpegVideoToolRunner RealRunner() =>
        new(Options.Create(new VideoEditingOptions { ScratchPath = Path.GetTempPath(), MaxConcurrentJobs = 2, CompileTimeoutSeconds = 120 }),
            NullLogger<FfmpegVideoToolRunner>.Instance);

    private async Task<string> RenderOk(EditTimeline doc, Dictionary<AssetRef, LocalAsset> assets, Dictionary<SegmentationMaskRef, LocalSegmentationMask> masks)
    {
        string scratch = Path.Combine(_work, "scratch-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(_work, "out-" + Guid.NewGuid().ToString("N") + ".mp4");
        TimelineRenderEnvironment env = new(RealRunner(), scratch, "/fonts/missing.ttf", TimeSpan.FromMinutes(2), 2, NullRegionCache.Instance, ProjectId,
            SegmentationMasks: masks);
        TimelineRenderStepConfig config = new(doc.Id, Preset: TimelineRenderPreset.Master);
        await new TimelineRenderer(NullLogger.Instance).RenderAsync(doc, config, assets, output, env, CancellationToken.None);
        return output;
    }

    // ── helpers: measurement ──

    private readonly record struct Rect(int X, int Y, int W, int H);

    // Interiors/far-outside regions, kept clear of the box edge so encode bleed and feathering never count.
    private static readonly Rect InsideRect = new(BoxX + 12, BoxY + 12, BoxW - 24, BoxH - 24);
    private static readonly Rect[] OutsideRects =
    [
        new(0, 0, 50, H), new(W - 50, 0, 50, H), new(0, 0, W, 20), new(0, H - 20, W, 20),
    ];

    private static double Inside(string a, string b) => MeanDiff(a, b, InsideRect);

    private static double Outside(string a, string b) => OutsideRects.Average(r => MeanDiff(a, b, r));

    private static double MeanDiff(string a, string b, Rect r, double atSec = 0.5)
    {
        byte[] fa = Frame(a, atSec), fb = Frame(b, atSec);
        double sum = 0;
        for (int y = r.Y; y < r.Y + r.H; y++)
            for (int x = r.X; x < r.X + r.W; x++)
            {
                int i = (y * W + x) * 3;
                sum += Math.Abs(fa[i] - fb[i]) + Math.Abs(fa[i + 1] - fb[i + 1]) + Math.Abs(fa[i + 2] - fb[i + 2]);
            }

        return sum / (r.W * r.H * 3.0);
    }

    private static double SignedLuma(string plain, string fx, Rect r) => MeanLuma(fx, r) - MeanLuma(plain, r);

    private static double SignedLuma(string plain, string fx, Rect[] rs) => rs.Average(r => SignedLuma(plain, fx, r));

    private static double MeanLuma(string video, Rect[] rs) => rs.Average(r => MeanLuma(video, r));

    private static double MeanLuma(string video, Rect r, double atSec = 0.5)
    {
        byte[] f = Frame(video, atSec);
        double sum = 0;
        for (int y = r.Y; y < r.Y + r.H; y++)
            for (int x = r.X; x < r.X + r.W; x++)
            {
                int i = (y * W + x) * 3;
                sum += 0.299 * f[i] + 0.587 * f[i + 1] + 0.114 * f[i + 2];
            }

        return sum / (r.W * r.H);
    }

    /// <summary>Mean channel spread (max - min of R,G,B) over the rectangles: 0 is grey.</summary>
    private static double Chroma(string video, Rect r)
    {
        byte[] f = Frame(video, 0.5);
        double sum = 0;
        for (int y = r.Y; y < r.Y + r.H; y++)
            for (int x = r.X; x < r.X + r.W; x++)
            {
                int i = (y * W + x) * 3;
                sum += Math.Max(f[i], Math.Max(f[i + 1], f[i + 2])) - Math.Min(f[i], Math.Min(f[i + 1], f[i + 2]));
            }

        return sum / (r.W * r.H);
    }

    private static double Chroma(string video, Rect[] rs) => rs.Average(r => Chroma(video, r));

    private static byte[] Frame(string video, double atSec)
    {
        byte[] frame = RunBytes("ffmpeg", ["-hide_banner", "-loglevel", "error", "-ss", atSec.ToString("R", CultureInfo.InvariantCulture), "-i", video,
            "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]);
        frame.Length.Should().Be(W * H * 3);
        return frame;
    }

    /// <summary>The delivery exists, lasts <paramref name="seconds"/> and has the timeline's size.</summary>
    private static void AssertDeliveryFacts(string output, double seconds)
    {
        File.Exists(output).Should().BeTrue();
        string json = Run("ffprobe", ["-v", "error", "-select_streams", "v:0", "-show_entries", "format=duration:stream=width,height", "-of", "json", output]);
        using JsonDocument probe = JsonDocument.Parse(json);
        double duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
        duration.Should().BeApproximately(seconds, 0.1);
        JsonElement video = probe.RootElement.GetProperty("streams")[0];
        video.GetProperty("width").GetInt32().Should().Be(W);
        video.GetProperty("height").GetInt32().Should().Be(H);
    }

    private static string Run(string tool, IEnumerable<string> args) => System.Text.Encoding.UTF8.GetString(RunBytes(tool, args));

    private static byte[] RunBytes(string tool, IEnumerable<string> args)
    {
        ProcessStartInfo info = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        using MemoryStream stdout = new();
        Task copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        string stderr = process.StandardError.ReadToEnd();
        copy.Wait(60_000);
        process.WaitForExit(60_000);
        process.ExitCode.Should().Be(0, $"{tool} failed: {stderr}");
        return stdout.ToArray();
    }
}
