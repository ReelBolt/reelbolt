#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// MediaClip.sourceRotation: an extra clockwise quarter turn of the (already autorotated) source,
/// applied before crop and fit. Argv units, the validator rule, the region cache key, and a real-ffmpeg
/// fixture on a display-matrix rotate=90 phone clip.
/// </summary>
public class SourceRotationTests
{
    private static readonly AssetRef Asset = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly EditTimelineSettings Settings = new(108, 192, new Rational(10, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static MediaClip Clip(int? rotation = null, ClipCrop? crop = null) =>
        new(Guid.Parse("11111111-1111-4111-8111-111111111111"), "c", 0, 5, Asset, Crop: crop, SourceRotation: rotation);

    private static EditTimeline Doc(MediaClip clip) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "doc", Settings,
            [new Track(Guid.Parse("22222222-0000-4000-8000-000000000000"), TrackKind.Video, "V1", null, 64, false, true, false, false, null, [clip])],
            [], null, null);

    private static ResolvedRenderTarget Target() =>
        TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);

    private static Dictionary<AssetRef, LocalAsset> Assets(string path, int w, int h) =>
        new() { [Asset] = new LocalAsset(path, "fp", new MediaProbeResult(1, 10, 1, w, h, "h264", null, null)) };

    private static RegionFilterGraphBuilder.RegionArgv Build(MediaClip clip, string path = "/tmp/a.mp4", string output = "/scratch/r.mp4", int w = 108, int h = 192) =>
        RegionFilterGraphBuilder.Build(SegmentPlanner.Plan(Doc(clip), null).Regions[0], Target(), Assets(path, w, h), output, "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 2);

    private static string Joined(RegionFilterGraphBuilder.RegionArgv argv) => string.Join(" ", argv.Args);

    [Theory]
    [InlineData(90, "transpose=1,scale=108:192")]
    [InlineData(270, "transpose=2,scale=108:192")]
    [InlineData(180, "hflip,vflip,scale=108:192")]
    public void The_quarter_turn_precedes_the_fit_on_the_P4_identity_path(int rotation, string expected) =>
        Joined(Build(Clip(rotation))).Should().Contain("fps=10/1," + expected);

    [Fact]
    public void A_zero_or_absent_rotation_is_byte_identical()
    {
        string baseline = Joined(Build(Clip()));
        Joined(Build(Clip(0))).Should().Be(baseline);
        baseline.Should().NotContain("transpose").And.NotContain("hflip");
    }

    [Fact]
    public void The_quarter_turn_precedes_the_crop_on_the_depth_path()
    {
        string joined = Joined(Build(Clip(90, new ClipCrop(Left: 0.1))));
        joined.Should().Contain("transpose=1,crop=iw*0.9:ih*1:iw*0.1:ih*0");
    }

    [Fact]
    public void Changing_the_rotation_changes_the_region_cache_key()
    {
        string Hash(MediaClip c) => RegionRecipeHasher.Hash(SegmentPlanner.Plan(Doc(c), null).Regions[0], Target(), Assets("/tmp/a.mp4", 108, 192), "font");
        string none = Hash(Clip()), r90 = Hash(Clip(90)), r270 = Hash(Clip(270));
        Hash(Clip(0)).Should().Be(none);
        new[] { none, r90, r270 }.Distinct().Should().HaveCount(3);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(-90)]
    [InlineData(360)]
    public void The_validator_names_the_clip_for_an_unsupported_rotation(int rotation)
    {
        EditTimeline doc = Doc(Clip(rotation));
        var result = EditTimelineValidator.Validate(doc, new EditTimelineValidationContext(
            doc.ProjectId, new Dictionary<AssetRef, long>(), new Dictionary<Guid, Guid> { [Asset.ProjectFileId!.Value] = doc.ProjectId }, new Dictionary<Guid, Guid>()));
        result.Violations.Should().Contain(v => v.Path.Contains("sourceRotation") && v.Path.Contains("items"));
    }

    [Fact]
    public void Rotation_survives_serialisation()
    {
        EditTimeline doc = Doc(Clip(90));
        string json = System.Text.Json.JsonSerializer.Serialize(doc, EditTimelineJson.Options);
        json.Should().Contain("\"sourceRotation\":90");
        ((MediaClip)System.Text.Json.JsonSerializer.Deserialize<EditTimeline>(json, EditTimelineJson.Options)!.Tracks[0].Items[0]).SourceRotation.Should().Be(90);
    }

    // ── real ffmpeg ──────────────────────────────────────────────────────────

    [Fact]
    public void Real_ffmpeg_renders_a_rotate_90_phone_clip_upright_and_a_source_rotation_turns_it_once_more()
    {
        if (!HasTool("ffmpeg") || !HasTool("ffprobe")) return;
        string dir = Path.Combine(Path.GetTempPath(), "srot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string plain = Path.Combine(dir, "plain.mp4"), phone = Path.Combine(dir, "phone.mp4");
            // Coded landscape 192x108, blue with a red marker top-left; the display matrix makes it a 108x192 portrait.
            Run("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=192x108:d=1:r=10", "-vf", "drawbox=x=0:y=0:w=60:h=40:color=red:t=fill",
                "-c:v", "libx264", "-pix_fmt", "yuv444p", plain);
            Run("ffmpeg", "-v", "error", "-y", "-display_rotation", "90", "-i", plain, "-c", "copy", phone);
            MediaProbeResult probe = FfprobeOutputParser.Parse(Run("ffprobe", [.. FfmpegArgvBuilder.BuildProbeArgs(phone)]));
            (probe.Width, probe.Height).Should().Be((108, 192), "the engine probes the displayed size");

            // Reference: what an upright decode of the phone clip looks like.
            byte[] reference = Frame(["-i", phone], 108, 192);
            (double rx, double ry) = RedCentroid(reference, 108, 192);

            // Case 1: no sourceRotation -> the picture fills the portrait canvas, marker exactly where the upright decode has it.
            string out1 = Path.Combine(dir, "o1.mp4");
            RunArgv(Build(Clip(), phone, out1, probe.Width, probe.Height));
            Dimensions(out1).Should().Be((108, 192));
            byte[] f1 = Frame(["-i", out1], 108, 192);
            Dark(f1, 108, 3, 3).Should().BeFalse("no letterbox bars");
            Dark(f1, 108, 3, 188).Should().BeFalse();
            Dark(f1, 108, 1, 96).Should().BeFalse();
            (double x1, double y1) = RedCentroid(f1, 108, 192);
            x1.Should().BeApproximately(rx, 2);
            y1.Should().BeApproximately(ry, 2);

            // Case 2: sourceRotation 90 -> a 192x108 picture letterboxed (bars top and bottom) in the portrait canvas.
            string out2 = Path.Combine(dir, "o2.mp4");
            RunArgv(Build(Clip(90), phone, out2, probe.Width, probe.Height));
            Dimensions(out2).Should().Be((108, 192));
            byte[] f2 = Frame(["-i", out2], 108, 192);
            Dark(f2, 108, 54, 5).Should().BeTrue("black bar above the landscape picture");
            Dark(f2, 108, 54, 186).Should().BeTrue("black bar below");
            Dark(f2, 108, 54, 96).Should().BeFalse("the picture is in the middle");
            (double x2, double y2) = RedCentroid(f2, 108, 192);
            // The upright marker (rx, ry) turned a quarter clockwise about the picture, then fitted (scale 0.5625) and centred.
            double scale = 108.0 / 192, top = (192 - 108 * scale) / 2;
            x2.Should().BeApproximately((192 - ry) * scale, 4);
            y2.Should().BeApproximately(top + rx * scale, 4);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static bool Dark(byte[] rgb, int w, int x, int y) => rgb[(y * w + x) * 3] < 40 && rgb[(y * w + x) * 3 + 1] < 40 && rgb[(y * w + x) * 3 + 2] < 40;

    private static (double X, double Y) RedCentroid(byte[] rgb, int w, int h)
    {
        double sx = 0, sy = 0; int n = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 3;
                if (rgb[i] > 180 && rgb[i + 1] < 80 && rgb[i + 2] < 80) { sx += x; sy += y; n++; }
            }
        n.Should().BeGreaterThan(50, "the red marker is visible");
        return (sx / n, sy / n);
    }

    private static byte[] Frame(string[] input, int w, int h)
    {
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-ss", "0.2" }.Concat(input).Concat(["-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"])) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        using MemoryStream ms = new();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0);
        ms.Length.Should().Be(w * h * 3);
        return ms.ToArray();
    }

    private static (int W, int H) Dimensions(string path)
    {
        MediaProbeResult p = FfprobeOutputParser.Parse(Run("ffprobe", [.. FfmpegArgvBuilder.BuildProbeArgs(path)]));
        return (p.Width, p.Height);
    }

    private static void RunArgv(RegionFilterGraphBuilder.RegionArgv argv)
    {
        argv.GraphFile.Should().BeNull();
        Run("ffmpeg", [.. argv.Args]);
    }

    private static bool HasTool(string name)
    {
        try { Run(name, "-version"); return true; } catch { return false; }
    }

    private static string Run(string tool, params string[] args)
    {
        ProcessStartInfo psi = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"{tool} exited {p.ExitCode}: {stderr}");
        return stdout;
    }

    [Fact]
    public void Rebuilding_a_clip_for_object_effects_keeps_its_rotation()
    {
        var rebuilt = ReelBolt.Inference.Api.Services.ObjectAnalysis.ObjectEffectApplier.WithRegionEffects(Clip(rotation: 270), []);
        rebuilt.SourceRotation.Should().Be(270);
    }
}
