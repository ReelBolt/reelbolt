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
using ReelBolt.Shared.Editing.Color;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;
using Xunit.Abstractions;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-011: a clip with <see cref="MediaClip.InputProfile"/> is converted from camera log to Rec.709 at
/// the start of its leg, before trim/crop/transform/stabilise/keyer/grade. The exact-string tests pin
/// the argv (and prove a profile-less region is byte-identical); the real-ffmpeg tests prove the
/// picture is legal Rec.709 and independent of the file's colour tags.
/// </summary>
public sealed class TimelineCameraProfileTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private readonly string _work = Path.Combine(Path.GetTempPath(), "tl-profile-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public TimelineCameraProfileTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── the stage text ───────────────────────────────────────────────────────

    [Fact]
    public void The_sony_stage_is_the_measured_prefix_the_table_and_the_bt709_tail()
    {
        CameraProfileStage.Filter("sonySLog3", "/scratch/p.cube").Should().Be(
            "zscale=rin=pc:min=709:tin=709:pin=709:t=709:p=709,format=gbrpf32le," +
            "lut3d=file='/scratch/p.cube':interp=tetrahedral," +
            "scale=out_range=tv:out_color_matrix=bt709:flags=accurate_rnd+full_chroma_int,format=yuv420p," +
            "setparams=range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709");
    }

    [Fact]
    public void The_fallback_drops_zscale_but_keeps_the_profile_range_and_matrix()
    {
        CameraProfileStage.Filter("appleLog", "/scratch/a.cube", useZscale: false).Should().Be(
            "scale=in_range=tv:in_color_matrix=bt2020:flags=accurate_rnd+full_chroma_int,format=gbrpf32le," +
            "lut3d=file='/scratch/a.cube':interp=tetrahedral," +
            "scale=out_range=tv:out_color_matrix=bt709:flags=accurate_rnd+full_chroma_int,format=yuv420p," +
            "setparams=range=tv:colorspace=bt709:color_primaries=bt709:color_trc=bt709");
    }

    [Theory]
    [InlineData("appleLog", "rin=tv:min=2020_ncl", "in_range=tv:in_color_matrix=bt2020")]
    [InlineData("sonySLog3", "rin=pc:min=709", "in_range=pc:in_color_matrix=bt709")]
    [InlineData("djiDLog", "rin=pc:min=709", "in_range=pc:in_color_matrix=bt709")]
    public void Range_and_matrix_come_from_the_profile_not_the_file(string id, string zscale, string fallback)
    {
        CameraProfileStage.Filter(id, "/scratch/p.cube").Should().StartWith("zscale=" + zscale);
        CameraProfileStage.Filter(id, "/scratch/p.cube", useZscale: false).Should().StartWith("scale=" + fallback);
    }

    [Fact]
    public void The_cube_path_is_escaped_exactly_like_a_grade_lut_path()
    {
        CameraProfileStage.Filter("sonySLog3", "/scratch/a'b:c.cube").Should()
            .Contain(@"lut3d=file='/scratch/a'\''b\:c.cube':interp=tetrahedral");
    }

    [Fact]
    public void A_cube_path_with_a_comma_is_escaped_so_it_does_not_split_the_filter_graph()
    {
        CameraProfileStage.Filter("sonySLog3", "/scratch/a,b.cube").Should()
            .Contain(@"lut3d=file='/scratch/a\,b.cube':interp=tetrahedral");
    }

    [Fact]
    public void A_cube_path_with_a_colon_quote_and_comma_is_escaped_at_every_layer()
    {
        CameraProfileStage.Filter("appleLog", "/scratch/it's:a,b.cube").Should()
            .Contain(@"lut3d=file='/scratch/it'\''s\:a\,b.cube':interp=tetrahedral");
    }

    [Fact]
    public void The_capability_matches_only_the_exact_filter_name()
    {
        FfmpegCameraProfileCapability.ContainsZscale(" .S zscale            V->V       Apply resizing\n").Should().BeTrue();
        FfmpegCameraProfileCapability.ContainsZscale(" .S zscale_opencl     V->V       OpenCL\n").Should().BeFalse();
        FfmpegCameraProfileCapability.ContainsZscale(" T. tonemap           V->V       Conversion\n").Should().BeFalse();
    }

    // ── argv ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_profile_less_region_is_byte_identical_with_or_without_the_new_parameters()
    {
        EditTimeline doc = Doc(Video(Clip("1", 0, 30, AssetA)));
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions[0];

        RegionFilterGraphBuilder.RegionArgv plain = RegionFilterGraphBuilder.Build(
            region, Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 2);
        RegionFilterGraphBuilder.RegionArgv explicitNulls = RegionFilterGraphBuilder.Build(
            region, Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 2,
            cameraProfilePathFor: n => $"/scratch/p-{n}.cube", cameraProfileZscale: false);

        explicitNulls.Args.Should().Equal(plain.Args);
        plain.CameraProfileFiles.Should().BeNull();
        plain.Args.Should().NotContain(a => a.Contains("lut3d", StringComparison.Ordinal) || a.Contains("zscale", StringComparison.Ordinal));
    }

    [Fact]
    public void The_stage_is_the_first_filter_of_the_leg_before_trim_and_the_cube_is_reported()
    {
        EditTimeline doc = Doc(Video(Clip("1", 0, 30, AssetA, profile: "sonySLog3")));
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions[0];

        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(
            region, Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1,
            cameraProfilePathFor: n => $"/scratch/p-{n}.cube");

        RegionFilterGraphBuilder.CameraProfileRequest request = argv.CameraProfileFiles.Should().ContainSingle().Subject;
        (request.ProfileId, request.Path).Should().Be(("sonySLog3", "/scratch/p-0.cube"));
        argv.LutFiles.Should().BeNull("a profile cube is not a grade LUT");
        string graph = Graph(argv.Args);
        graph.Should().Contain("[0:v]" + CameraProfileStage.Filter("sonySLog3", "/scratch/p-0.cube") + ",trim=");
        graph.IndexOf("lut3d=", StringComparison.Ordinal).Should().BeLessThan(graph.IndexOf("trim=", StringComparison.Ordinal));
        graph.Should().Contain("lut3d=file='/scratch/p-0.cube':interp=tetrahedral");
    }

    [Fact]
    public void A_transition_puts_each_legs_own_profile_stage_on_that_leg()
    {
        MediaClip from = Clip("1", 0, 30, AssetA, profile: "sonySLog3");
        MediaClip to = Clip("2", 40, 60, AssetB, profile: "appleLog");
        Transition fade = new(Guid.NewGuid(), "x", 30, 40, "fade");
        EditTimeline doc = Doc(Video(from, fade, to));
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions.Single(r => r.StartFrame == 30);

        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(
            region, Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1,
            cameraProfilePathFor: n => $"/scratch/p-{n}.cube");

        argv.CameraProfileFiles!.Select(r => r.ProfileId).Should().Equal("sonySLog3", "appleLog");
        string graph = Graph(argv.Args);
        graph.Should().Contain(CameraProfileStage.Filter("sonySLog3", "/scratch/p-0.cube") + ",trim=");
        graph.Should().Contain(CameraProfileStage.Filter("appleLog", "/scratch/p-1.cube") + ",trim=");
    }

    [Fact]
    public void An_hdr_clip_that_also_names_a_profile_gets_only_the_tone_map()
    {
        AssetRef hdr = new(AssetRefKind.ProjectFile, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
        Dictionary<AssetRef, LocalAsset> assets = new(Assets)
        {
            [hdr] = new LocalAsset("/tmp/hdr.mkv", "hdr|1",
                new MediaProbeResult(2, 30, 1, 320, 180, "hevc", null, null, ColorTransfer: "arib-std-b67", ColorPrimaries: "bt2020", ColorSpace: "bt2020nc"))
        };
        EditTimeline doc = Doc(Video(Clip("3", 0, 30, hdr, profile: "sonySLog3")));
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions[0];

        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(
            region, Target(), assets, "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1,
            cameraProfilePathFor: n => $"/scratch/p-{n}.cube",
            hdrToneMaps: new Dictionary<AssetRef, string> { [hdr] = "hlg" });

        argv.CameraProfileFiles.Should().BeNull();
        string graph = Graph(argv.Args);
        graph.Should().Contain("zscale=t=linear:npl=203");
        graph.Should().NotContain("lut3d=");
    }

    [Fact]
    public void A_downscaled_hdr_clip_with_nothing_needing_source_pixels_tone_maps_after_the_scale()
    {
        AssetRef hdr = new(AssetRefKind.ProjectFile, Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc"));
        Dictionary<AssetRef, LocalAsset> big = new(Assets) { [hdr] = HdrAsset(1280, 720) };
        Dictionary<AssetRef, string> kinds = new() { [hdr] = "hlg" };
        EditTimeline plain = Doc(Video(Clip("3", 0, 30, hdr)));
        RenderRegion region = SegmentPlanner.Plan(plain, null).Regions[0];

        string graph = Graph(RegionFilterGraphBuilder.Build(region, Target(), big, "/s/r.mp4", "/f.ttf", n => $"/s/t-{n}.txt", 1, hdrToneMaps: kinds).Args);
        graph.Should().Contain("[0:v]scale=320:180:force_original_aspect_ratio=decrease:force_divisible_by=2,zscale=t=linear:npl=203");

        // Same-size source: no downscale, tone-map stays first.
        Dictionary<AssetRef, LocalAsset> same = new(Assets) { [hdr] = HdrAsset(320, 180) };
        Graph(RegionFilterGraphBuilder.Build(region, Target(), same, "/s/r.mp4", "/f.ttf", n => $"/s/t-{n}.txt", 1, hdrToneMaps: kinds).Args)
            .Should().Contain("[0:v]zscale=t=linear:npl=203");

        // A crop needs source pixels: the full-resolution path is kept.
        MediaClip cropped = new(Guid.NewGuid(), "clip-c", 0, 30, hdr, 0, Crop: new ClipCrop(0.1, 0, 0, 0));
        RenderRegion croppedRegion = SegmentPlanner.Plan(Doc(Video(cropped)), null).Regions[0];
        Graph(RegionFilterGraphBuilder.Build(croppedRegion, Target(), big, "/s/r.mp4", "/f.ttf", n => $"/s/t-{n}.txt", 1, hdrToneMaps: kinds).Args)
            .Should().Contain("[0:v]zscale=t=linear:npl=203");

        // The recipe changes only for the affected region.
        string withKinds = RegionRecipeHasher.Hash(region, Target(), big, "font", kinds);
        string sameKinds = RegionRecipeHasher.Hash(region, Target(), same, "font", kinds);
        withKinds.Should().NotBe(sameKinds);
    }

    private static LocalAsset HdrAsset(int width, int height) =>
        new("/tmp/hdr.mkv", $"hdr|{width}", new MediaProbeResult(2, 30, 1, width, height, "hevc", null, null, ColorTransfer: "arib-std-b67", ColorPrimaries: "bt2020", ColorSpace: "bt2020nc"));

    // ── recipe hash ──────────────────────────────────────────────────────────

    [Fact]
    public void Only_a_region_with_a_profile_clip_changes_its_hash_and_profiles_differ()
    {
        // Region 0 (0..30) has no profile clip; region 1 (30..60) does. Adding the profile must leave
        // region 0 alone and change region 1.
        EditTimeline without = Doc(Video(Clip("1", 0, 60, AssetA)), Video(Clip("2", 30, 60, AssetB)));
        EditTimeline with = Doc(Video(Clip("1", 0, 60, AssetA)), Video(Clip("2", 30, 60, AssetB, profile: "sonySLog3")));
        EditTimeline other = Doc(Video(Clip("1", 0, 60, AssetA)), Video(Clip("2", 30, 60, AssetB, profile: "appleLog")));

        string[] baseline = Hashes(without);
        string[] sony = Hashes(with);
        string[] apple = Hashes(other);

        sony[0].Should().Be(baseline[0], "a region with no profile clip keeps its cache");
        sony[1].Should().NotBe(baseline[1]);
        sony[1].Should().NotBe(apple[1], "each profile is its own recipe");
        CameraProfileStage.RecipeVersion.Should().Be("camera-profile-v1");
    }

    [Fact]
    public void The_zscale_choice_is_in_the_token_only_for_a_profiled_region()
    {
        EditTimeline plain = Doc(Video(Clip("1", 0, 30, AssetA)));
        EditTimeline profiled = Doc(Video(Clip("1", 0, 30, AssetA, profile: "sonySLog3")));
        RenderRegion plainRegion = SegmentPlanner.Plan(plain, null).Regions[0];
        RenderRegion profiledRegion = SegmentPlanner.Plan(profiled, null).Regions[0];

        RegionRecipeHasher.Hash(plainRegion, Target(), Assets, "font", cameraProfileZscale: false).Should()
            .Be(RegionRecipeHasher.Hash(plainRegion, Target(), Assets, "font", cameraProfileZscale: true), "a profile-less region ignores it");
        RegionRecipeHasher.Hash(profiledRegion, Target(), Assets, "font", cameraProfileZscale: false).Should()
            .NotBe(RegionRecipeHasher.Hash(profiledRegion, Target(), Assets, "font", cameraProfileZscale: true));
        RegionRecipeHasher.Hash(profiledRegion, Target(), Assets, "font").Should()
            .Be(RegionRecipeHasher.Hash(profiledRegion, Target(), Assets, "font", cameraProfileZscale: true), "the zscale token is unchanged");
    }

    [Fact]
    public void An_hdr_profile_clip_folds_no_profile_token()
    {
        AssetRef hdr = new(AssetRefKind.ProjectFile, Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd"));
        Dictionary<AssetRef, LocalAsset> assets = new(Assets)
        {
            [hdr] = new LocalAsset("/tmp/hdr.mkv", "hdr|1",
                new MediaProbeResult(2, 30, 1, 320, 180, "hevc", null, null, ColorTransfer: "arib-std-b67"))
        };
        EditTimeline withProfile = Doc(Video(Clip("3", 0, 30, hdr, profile: "sonySLog3")));
        EditTimeline withoutProfile = Doc(Video(Clip("3", 0, 30, hdr)));
        RenderRegion region = SegmentPlanner.Plan(withProfile, null).Regions[0];

        // No tone map available: neither stage runs, so the profile must not change the recipe either.
        RegionRecipeHasher.Hash(region, Target(), assets, "font").Should()
            .Be(RegionRecipeHasher.Hash(region, Target(), assets, "font"));
        RegionRecipeHasher.Hash(region, Target(), assets, "font").Should()
            .Be(Hashes(withoutProfile, assets)[0]);
    }

    // ── renderer orchestration (no ffmpeg) ───────────────────────────────────

    [Fact]
    public async Task The_renderer_warns_once_when_an_hdr_clip_ignores_its_profile()
    {
        AssetRef hdr = new(AssetRefKind.ProjectFile, Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"));
        Dictionary<AssetRef, LocalAsset> assets = new(Assets)
        {
            [hdr] = new LocalAsset("/tmp/hdr.mkv", "hdr|1",
                new MediaProbeResult(2, 30, 1, 320, 180, "hevc", null, null, ColorTransfer: "smpte2084", ColorPrimaries: "bt2020", ColorSpace: "bt2020nc"))
        };
        EditTimeline doc = Doc(Video(Clip("4", 0, 30, hdr, profile: "sonySLog3")));

        TimelineRenderResult result = await Render(doc, assets);

        result.Warnings.Should().ContainSingle().Which.Code.Should().Be(CameraProfileStage.IgnoredHdrWarning);
        string invocations = string.Join(' ', result.Invocations.SelectMany(i => i));
        invocations.Should().Contain("tonemap=tonemap=mobius");
        invocations.Should().NotContain("lut3d=");
    }

    // ── real ffmpeg ──────────────────────────────────────────────────────────

    [CameraProfileFact]
    public async Task A_log_chart_renders_to_the_expected_legal_rec709_value_and_ignores_the_files_tags()
    {
        // Sony expects full-range BT.709 codes. The 18% grey S-Log3 code is 420/1023, i.e. Y=105 in 8-bit.
        const int sonyY = 105;
        string untagged = MakeChart("sony-untagged", sonyY, wrongTag: false);
        string wrongTag = MakeChart("sony-bt2020", sonyY, wrongTag: true);
        Probe(wrongTag).ColorSpace.Should().Be("bt2020nc", "the second file really is tagged wrongly");

        int expectedSony = ExpectedLegalY("sonySLog3", sonyY);
        int untaggedY = await RenderChartAndReadPatch("sony-untagged", untagged, "sonySLog3", expectedSony);
        int wrongTagY = await RenderChartAndReadPatch("sony-bt2020", wrongTag, "sonySLog3", expectedSony);
        _output.WriteLine($"sonySLog3 tag independence: untagged Y={untaggedY}, bt2020-tagged Y={wrongTagY}, delta={Math.Abs(untaggedY - wrongTagY)}");
        Math.Abs(untaggedY - wrongTagY).Should().BeLessThanOrEqualTo(2, $"the file tags must not change the picture ({untaggedY} vs {wrongTagY})");

        // Apple expects legal-range BT.2020 codes; the 18% code is about 0.488, i.e. Y=123.
        const int appleY = 123;
        int expectedApple = ExpectedLegalY("appleLog", appleY);
        await RenderChartAndReadPatch("apple-untagged", MakeChart("apple-untagged", appleY, wrongTag: false), "appleLog", expectedApple);
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"));
    private static readonly AssetRef AssetB = new(AssetRefKind.ProjectFile, Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"));

    private static readonly Dictionary<AssetRef, LocalAsset> Assets = new()
    {
        [AssetA] = new LocalAsset("/tmp/a.mp4", "a|100", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
        [AssetB] = new LocalAsset("/tmp/b.mp4", "b|200", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
    };

    private static MediaClip Clip(string id, long recordIn, long recordOut, AssetRef asset, long sourceIn = 0, string? profile = null) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-{id}{id}{id}{id}-4{id}{id}{id}-8{id}{id}{id}-{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}"),
            $"clip-{id}", recordIn, recordOut, asset, sourceIn, InputProfile: profile);

    private static Track Video(params Item[] items) =>
        new(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, items);

    private static EditTimeline Doc(params Track[] tracks) =>
        new(Guid.NewGuid(), ProjectId, "doc", Settings, tracks, [], null, null);

    private static ResolvedRenderTarget Target() =>
        TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);

    private static string Graph(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-filter_complex") + 1];

    private static string[] Hashes(EditTimeline doc, IReadOnlyDictionary<AssetRef, LocalAsset>? assets = null) =>
        SegmentPlanner.Plan(doc, null).Regions.Select(r => RegionRecipeHasher.Hash(r, Target(), assets ?? Assets, "font")).ToArray();

    private async Task<TimelineRenderResult> Render(EditTimeline doc, IReadOnlyDictionary<AssetRef, LocalAsset> assets, bool hdrAvailable = true)
    {
        string scratch = Path.Combine(_work, "scratch-" + Guid.NewGuid().ToString("N"));
        TimelineRenderEnvironment env = new(
            new NoopRunner(), scratch, "/fonts/missing.ttf", TimeSpan.FromMinutes(2), 2, NullRegionCache.Instance, ProjectId,
            ToneMapCapability: new FakeHdr(hdrAvailable), CameraProfileCapability: new UntouchableProfileCapability());
        return await new TimelineRenderer(NullLogger.Instance)
            .RenderAsync(doc, new TimelineRenderStepConfig(doc.Id, Preset: TimelineRenderPreset.Master), assets, Path.Combine(_work, "out.mp4"), env, CancellationToken.None);
    }

    private sealed class FakeHdr(bool available) : IHdrToneMapCapability
    {
        public Task<bool> IsAvailableAsync(IVideoToolRunner runner, CancellationToken ct) => Task.FromResult(available);
    }

    /// <summary>An HDR+profile clip never needs the zscale probe; touching this is a bug.</summary>
    private sealed class UntouchableProfileCapability : ICameraProfileCapability
    {
        public Task<bool> IsAvailableAsync(IVideoToolRunner runner, CancellationToken ct) =>
            throw new InvalidOperationException("a profile stage that will not run must not probe the host");
    }

    private sealed class NoopRunner : IVideoToolRunner
    {
        private static readonly VideoToolResult Ok = new(0, string.Empty, string.Empty, false);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Task.FromResult(Ok);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) => Task.FromResult(Ok);
        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Task.FromResult(Ok);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) => Task.FromResult(Ok);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority) => Task.FromResult(Ok);
        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) => Task.FromResult(Ok);
        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Task.FromResult(Ok);
        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) => Task.FromResult(Ok);
    }

    /// <summary>One frame of a 320x180 synthetic log chart: four vertical patches, the second at mid-grey.</summary>
    private string MakeChart(string name, int midY, bool wrongTag)
    {
        string path = Path.Combine(_work, name + ".mkv");
        string lum = $"if(lt(X,80),40,if(lt(X,160),{midY},if(lt(X,240),160,210)))";
        string filter = $"format=yuv420p,geq=lum='{lum}':cb='128':cr='128'";
        if (wrongTag) filter += ",setparams=colorspace=bt2020nc:color_primaries=bt2020";
        Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "nullsrc=s=320x180:d=1:r=30",
            "-vf", filter, "-c:v", "libx264", "-crf", "10", "-g", "1", "-an", path]);
        return path;
    }

    private static int ExpectedLegalY(string profileId, int y)
    {
        CameraProfiles.TryGet(profileId, out CameraProfileDefinition? profile).Should().BeTrue();
        double code = profile!.ExpectedRange == "full" ? y / 255.0 : (y - 16) / 219.0;
        (double v, _, _) = CameraDisplayTransform.ToRec709(profile, code, code, code);
        return (int)Math.Round(16 + (v * 219), MidpointRounding.AwayFromZero);
    }

    private async Task<int> RenderChartAndReadPatch(string name, string chart, string profileId, int expected)
    {
        AssetRef asset = Register(name, chart);
        MediaClip clip = new(Guid.NewGuid(), name, 0, 30, asset, InputProfile: profileId);
        EditTimeline doc = Doc(Video(clip));

        TimelineRenderResult result = await RealRender(doc);
        result.Plan.SkippedItems.Should().BeEmpty();
        int actual = FrameY(result.OutputPath, 0.5, 120, 90);
        _output.WriteLine($"{profileId} ({name}): expected legal Rec.709 Y={expected}, rendered Y={actual}");
        actual.Should().BeInRange(expected - 6, expected + 6,
            $"{profileId}: the mid-grey patch must be the legal Rec.709 value {expected} (got {actual})");
        return actual;
    }

    private readonly Dictionary<AssetRef, LocalAsset> _assets = [];

    private AssetRef Register(string name, string path)
    {
        AssetRef reference = new(AssetRefKind.ProjectFile, Guid.NewGuid(), null);
        _assets[reference] = new LocalAsset(path, $"{name}|fixed", Probe(path));
        return reference;
    }

    private async Task<TimelineRenderResult> RealRender(EditTimeline doc)
    {
        FfmpegVideoToolRunner runner = new(
            Options.Create(new VideoEditingOptions { ScratchPath = Path.GetTempPath(), MaxConcurrentJobs = 2, CompileTimeoutSeconds = 120 }),
            NullLogger<FfmpegVideoToolRunner>.Instance);
        string scratch = Path.Combine(_work, "scratch-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(_work, "out-" + Guid.NewGuid().ToString("N") + ".mp4");
        TimelineRenderEnvironment env = new(runner, scratch, "/fonts/missing.ttf", TimeSpan.FromMinutes(2), 2, NullRegionCache.Instance, ProjectId);
        return await new TimelineRenderer(NullLogger.Instance)
            .RenderAsync(doc, new TimelineRenderStepConfig(doc.Id, Preset: TimelineRenderPreset.Master), _assets, output, env, CancellationToken.None);
    }

    private static MediaProbeResult Probe(string path) =>
        FfprobeOutputParser.Parse(Run("ffprobe", ["-v", "error", "-show_format", "-show_streams", "-of", "json", path]));

    private static int FrameY(string video, double atSec, int x, int y)
    {
        byte[] frame = RunBytes("ffmpeg", ["-hide_banner", "-loglevel", "error", "-ss", atSec.ToString("R", CultureInfo.InvariantCulture),
            "-i", video, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-"]);
        frame.Length.Should().BeGreaterThanOrEqualTo(320 * 180 * 3 / 2);
        return frame[y * 320 + x];
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

/// <summary>A fact that needs a real ffmpeg with <c>zscale</c> and <c>lut3d</c> (skipped, with the reason, without).</summary>
public sealed class CameraProfileFactAttribute : FactAttribute
{
    public CameraProfileFactAttribute()
    {
        try
        {
            ProcessStartInfo info = new("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-hide_banner");
            info.ArgumentList.Add("-filters");
            using Process process = Process.Start(info)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            bool lut3d = output.Split('\n').Any(l =>
            {
                string[] parts = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return parts.Length >= 2 && parts[1] == "lut3d";
            });
            if (!FfmpegCameraProfileCapability.ContainsZscale(output) || !lut3d)
                Skip = "This host's ffmpeg lacks the zscale/lut3d filters.";
        }
        catch (Exception)
        {
            Skip = "Requires an ffmpeg binary on PATH.";
        }
    }
}
