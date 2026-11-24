#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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

/// <summary>R09.6/R09.7/R10.5 render side: caption looks, 3x3 positions, multiline, sidecars, burn-in pixels.</summary>
public sealed class TimelineRenderCaptionTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private const string Font = "/fonts/f.ttf";
    private const string DejaVu = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);

    private readonly string _work = Path.Combine(Path.GetTempPath(), "tl-cap-" + Guid.NewGuid().ToString("N"));

    public TimelineRenderCaptionTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static EditTimelineSettings SettingsFor(int w, int h) => new(w, h, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static Track VideoTrack(params Item[] items) =>
        new(Guid.NewGuid(), TrackKind.Video, "V", null, 64, false, true, false, false, null, items);

    private static MediaClip Clip() => new(Guid.NewGuid(), "c", 0, 30, AssetA, 0);

    private static Caption Cap(string text, string? look = null, string? position = null, bool enabled = true, long from = 0, long to = 30) =>
        new(Guid.NewGuid(), "cap", from, to, text, "en", enabled, null, look, position);

    private static EditTimeline Doc(int w, int h, params Track[] tracks) =>
        new(Guid.NewGuid(), ProjectId, "doc", SettingsFor(w, h), tracks, [], null, null);

    private static ResolvedRenderTarget Target(int w, int h) =>
        TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), SettingsFor(w, h));

    private static readonly Dictionary<AssetRef, LocalAsset> Assets = new()
    {
        [AssetA] = new LocalAsset("/tmp/a.mp4", "a", new MediaProbeResult(2, 30, 1, 320, 180, "h264", null, null)),
    };

    private static RegionFilterGraphBuilder.RegionArgv Build(EditTimeline doc, int w, int h)
    {
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions[0];
        return RegionFilterGraphBuilder.Build(region, Target(w, h), Assets, "/scratch/r.mp4", Font, n => $"/scratch/text-{n}.txt", 2);
    }

    private static string Graph(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-filter_complex") + 1];

    [Fact]
    public void A_caption_without_look_or_position_keeps_the_legacy_drawtext_bytes()
    {
        string graph = Graph(Build(Doc(320, 180, VideoTrack(Clip()), VideoTrack(Cap("Hello"))), 320, 180).Args);
        graph.Should().Contain("drawtext=fontfile='/fonts/f.ttf':textfile='/scratch/text-0.txt':expansion=none:fontsize=8:fontcolor=#FFFFFF:borderw=2:bordercolor=black@0.6:x=(w-text_w)/2:y=h-text_h-h/12[t0]");
    }

    [Theory]
    [InlineData("bold", "fontsize=10:fontcolor=#FFFFFF:borderw=3:bordercolor=black:x=(w-text_w)/2:y=h-text_h-h/12")]
    [InlineData("boxed", "fontsize=8:fontcolor=#FFFFFF:box=1:boxcolor=black@0.6:boxborderw=8:x=(w-text_w)/2:y=h-text_h-h/12")]
    [InlineData("outline", "fontsize=8:fontcolor=#FFFFFF:borderw=3:bordercolor=black:x=(w-text_w)/2:y=h-text_h-h/12")]
    [InlineData("spotlight", "fontsize=8:fontcolor=#FFE600:borderw=2:bordercolor=black:x=(w-text_w)/2:y=h-text_h-h/12")]
    public void Each_look_has_its_own_decoration(string look, string expected)
    {
        RegionFilterGraphBuilder.RegionArgv argv = Build(Doc(320, 180, VideoTrack(Clip()), VideoTrack(Cap("Hi", look: look))), 320, 180);
        Graph(argv.Args).Should().Contain(expected);
    }

    [Fact]
    public void Looks_scale_with_the_short_side_and_vertical_frames_keep_a_larger_bottom_margin()
    {
        EditTimeline wide = Doc(1920, 1080, VideoTrack(Clip()), VideoTrack(Cap("Hi", look: "bold")));
        EditTimeline tall = Doc(1080, 1920, VideoTrack(Clip()), VideoTrack(Cap("Hi", look: "bold")));
        string g169 = Graph(Build(wide, 1920, 1080).Args);
        string g916 = Graph(Build(tall, 1080, 1920).Args);
        g169.Should().Contain("fontsize=61:").And.Contain("y=h-text_h-h/12");
        g916.Should().Contain("fontsize=61:").And.Contain("y=h-text_h-h*0.14", "same short side, same size; vertical keeps a bigger safe area");
    }

    [Theory]
    [InlineData("topLeft", "x=w/12:y=h/12")]
    [InlineData("topRight", "x=w-text_w-w/12:y=h/12")]
    [InlineData("middleLeft", "x=w/12:y=(h-text_h)/2")]
    [InlineData("center", "x=(w-text_w)/2:y=(h-text_h)/2")]
    [InlineData("bottomRight", "x=w-text_w-w/12:y=h-text_h-h/12")]
    public void The_3x3_pad_places_a_caption(string position, string expected)
    {
        Graph(Build(Doc(1920, 1080, VideoTrack(Clip()), VideoTrack(Cap("Hi", position: position))), 1920, 1080).Args).Should().Contain(expected);
    }

    [Fact]
    public void A_multiline_caption_writes_one_line_per_row_aligned_to_its_column()
    {
        RegionFilterGraphBuilder.RegionArgv argv = Build(Doc(1920, 1080, VideoTrack(Clip()), VideoTrack(Cap("First line\nSecond:line 'x'", position: "bottomLeft"))), 1920, 1080);
        argv.TextFiles.Should().ContainSingle().Which.Contents.Should().Be("First line\nSecondline x");
        Graph(argv.Args).Should().Contain("text_align=L").And.NotContain("First");
    }

    [Fact]
    public void Special_characters_are_sanitised_and_never_reach_the_filter_string()
    {
        RegionFilterGraphBuilder.RegionArgv argv = Build(Doc(1920, 1080, VideoTrack(Clip()), VideoTrack(Cap("a%{eif:1:d}b\n,drawtext=1;[x]", look: "bold"))), 1920, 1080);
        argv.TextFiles.Single().Contents.Should().NotContain("%{").And.NotContain(";").And.NotContain("[");
        Graph(argv.Args).Should().NotContain("eif").And.NotContain("drawtext=1");
    }

    [Fact]
    public void A_long_caption_is_wrapped_to_the_frame_width()
    {
        string text = string.Join(' ', Enumerable.Repeat("wonderful", 12));
        string[] lines = Build(Doc(1080, 1920, VideoTrack(Clip()), VideoTrack(Cap(text, look: "bold"))), 1080, 1920).TextFiles.Single().Contents.Split('\n');
        lines.Length.Should().BeGreaterThan(1);
        lines.Should().OnlyContain(l => l.Length <= CaptionBurnIn.CharsPerLine(1080, 61));
    }

    [Fact]
    public void A_title_position_moves_a_plain_title_but_not_a_lower_third()
    {
        TitleClip plain = new(Guid.NewGuid(), "t", 0, 30, "Hi", 40, "Arial", "#FFFFFF", Position: "topRight");
        TitleClip third = new(Guid.NewGuid(), "t", 0, 30, "Hi", 40, "Arial", "#FFFFFF", Template: "lowerThird", Position: "topRight");
        Graph(Build(Doc(1920, 1080, VideoTrack(Clip()), VideoTrack(plain)), 1920, 1080).Args).Should().Contain("x=w-text_w-w/12:y=h/12");
        Graph(Build(Doc(1920, 1080, VideoTrack(Clip()), VideoTrack(third)), 1920, 1080).Args).Should().Contain("x=w/12:y=h-text_h-h/8");
    }

    [Fact]
    public void Look_and_position_join_the_recipe_hash_only_when_set()
    {
        string Hash(Caption c)
        {
            EditTimeline doc = Doc(320, 180, VideoTrack(Clip()), VideoTrack(c));
            return RegionRecipeHasher.Hash(SegmentPlanner.Plan(doc, null).Regions[0], Target(320, 180), Assets, "f");
        }

        Guid id = Guid.NewGuid();
        Caption Make(string? look, string? pos) => new(id, "cap", 0, 30, "Hi", "en", true, null, look, pos);
        Hash(Make(null, null)).Should().NotBe(Hash(Make("bold", null)));
        Hash(Make(null, null)).Should().NotBe(Hash(Make(null, "topLeft")));
    }

    [Fact]
    public void Sidecar_cues_equal_the_caption_list_ordered_and_shifted_by_the_range()
    {
        Caption late = Cap("Second\nline", from: 60, to: 90);
        Caption early = Cap("First", enabled: false, from: 0, to: 30);
        EditTimeline doc = Doc(320, 180, VideoTrack(late, early));
        IReadOnlyList<CaptionCue> cues = CaptionSidecar.Cues(doc, null);
        cues.Should().Equal(new CaptionCue(0, 1000, "First"), new CaptionCue(2000, 3000, "Second\nline"));
        CaptionSidecar.Srt(cues).Should().Be("1\n00:00:00,000 --> 00:00:01,000\nFirst\n\n2\n00:00:02,000 --> 00:00:03,000\nSecond\nline\n\n");
        CaptionSidecar.Vtt(cues).Should().Be("WEBVTT\n\n00:00:00.000 --> 00:00:01.000\nFirst\n\n00:00:02.000 --> 00:00:03.000\nSecond\nline\n\n");
        CaptionSidecar.Cues(doc, new TimelineRenderRange(45, 90)).Should().Equal(new CaptionCue(500, 1500, "Second\nline"));
    }

    [Fact]
    public void Captions_modes_map_to_burn_in_and_sidecar_and_default_is_legacy_burn_in()
    {
        Guid id = Guid.NewGuid();
        TimelineRenderStepConfig legacy = new(id);
        legacy.BurnInCaptions.Should().BeTrue();
        legacy.WriteCaptionSidecars.Should().BeFalse();
        System.Text.Json.JsonSerializer.Serialize(legacy).Should().NotContain("aptions", "an unset mode adds nothing to the stored config");
        (legacy with { Captions = TimelineRenderCaptions.None }).BurnInCaptions.Should().BeFalse();
        (legacy with { Captions = TimelineRenderCaptions.Sidecar }).BurnInCaptions.Should().BeFalse();
        (legacy with { Captions = TimelineRenderCaptions.Sidecar }).WriteCaptionSidecars.Should().BeTrue();
        (legacy with { Captions = TimelineRenderCaptions.Both }).BurnInCaptions.Should().BeTrue();
        (legacy with { Captions = (TimelineRenderCaptions)9 }).Validate().Should().Contain(e => e.Contains("captions"));
    }

    [FfmpegFact]
    public async Task Burn_in_changes_the_caption_band_pixels_and_sidecar_only_does_not()
    {
        if (!File.Exists(DejaVu)) return;
        const int w = 320, h = 180;
        string source = Path.Combine(_work, "src.mp4");
        Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=320x180:r=30", "-t", "2", "-pix_fmt", "yuv420p", "-c:v", "libx264", source]);
        AssetRef asset = new(AssetRefKind.ProjectFile, Guid.NewGuid(), null);
        Dictionary<AssetRef, LocalAsset> assets = new() { [asset] = new LocalAsset(source, "src", new MediaProbeResult(2, 30, 1, w, h, "h264", null, null)) };
        EditTimeline doc = Doc(w, h, VideoTrack(new MediaClip(Guid.NewGuid(), "c", 0, 60, asset, 0)), VideoTrack(Cap("Burn me in\nsecond line", look: "boxed", from: 0, to: 60)));

        string bare = await RenderTo(doc, assets, TimelineRenderCaptions.None);
        string burned = await RenderTo(doc, assets, TimelineRenderCaptions.BurnIn);
        string sidecar = await RenderTo(doc, assets, TimelineRenderCaptions.Sidecar);

        BandDiff(bare, burned, w, h).Should().BeGreaterThan(200, "the caption is drawn into the bottom band");
        BandDiff(bare, sidecar, w, h).Should().Be(0, "sidecar-only leaves the picture untouched");
    }

    private async Task<string> RenderTo(EditTimeline doc, Dictionary<AssetRef, LocalAsset> assets, TimelineRenderCaptions mode)
    {
        FfmpegVideoToolRunner runner = new(Options.Create(new VideoEditingOptions { ScratchPath = Path.GetTempPath(), MaxConcurrentJobs = 2, CompileTimeoutSeconds = 120 }), NullLogger<FfmpegVideoToolRunner>.Instance);
        string output = Path.Combine(_work, "out-" + Guid.NewGuid().ToString("N") + ".mp4");
        TimelineRenderEnvironment env = new(runner, Path.Combine(_work, "s-" + Guid.NewGuid().ToString("N")), DejaVu, TimeSpan.FromMinutes(2), 2, NullRegionCache.Instance, ProjectId);
        TimelineRenderStepConfig config = new(doc.Id, Preset: TimelineRenderPreset.Master, Captions: mode);
        await new TimelineRenderer(NullLogger.Instance).RenderAsync(doc, config, assets, output, env, CancellationToken.None);
        return output;
    }

    /// <summary>Pixels (any channel off by more than 40) that differ in the bottom third of the frame at 1 s.</summary>
    private static int BandDiff(string a, string b, int w, int h)
    {
        byte[] fa = Frame(a, w, h), fb = Frame(b, w, h);
        int diff = 0;
        for (int i = (h * 2 / 3) * w * 3; i < fa.Length; i += 3)
            if (Math.Abs(fa[i] - fb[i]) > 40 || Math.Abs(fa[i + 1] - fb[i + 1]) > 40 || Math.Abs(fa[i + 2] - fb[i + 2]) > 40) diff++;
        return diff;
    }

    private static byte[] Frame(string video, int w, int h)
    {
        byte[] frame = RunBytes("ffmpeg", ["-hide_banner", "-loglevel", "error", "-ss", "1", "-i", video, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]);
        frame.Length.Should().Be(w * h * 3);
        return frame;
    }

    private static void Run(string tool, IEnumerable<string> args) => RunBytes(tool, args);

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
