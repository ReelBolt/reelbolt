#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// R10.7: HLG / PQ phone footage is tone-mapped to SDR BT.709 before the grade. The pure rules (tag to
/// kind, ffprobe parsing, the stored probe, the capability check, the report) run everywhere; the tests
/// through the real ffmpeg generate 2 s HLG / PQ fixtures at test time and skip, with the reason, on a
/// host whose ffmpeg lacks <c>zscale</c> / <c>tonemap</c>.
/// </summary>
public sealed class HdrToneMapTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private readonly string _work = Path.Combine(Path.GetTempPath(), "tl-hdr-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<AssetRef, LocalAsset> _assets = [];

    public HdrToneMapTests() => Directory.CreateDirectory(_work);

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ── pure rules ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("arib-std-b67", "hlg")]
    [InlineData("smpte2084", "pq")]
    [InlineData("bt709", null)]
    [InlineData("smpte428", null)]
    [InlineData("log", null)]
    [InlineData("log_sqrt", null)]
    [InlineData(null, null)]
    public void Kind_follows_the_transfer_tag_and_never_guesses_log(string? transfer, string? kind) =>
        HdrToneMap.KindOf(transfer).Should().Be(kind);

    [Fact]
    public void The_parser_reads_the_colour_tags_and_treats_unknown_as_not_known()
    {
        const string tagged = """{"streams":[{"codec_type":"video","width":1920,"height":1080,"codec_name":"hevc","pix_fmt":"yuv420p10le","r_frame_rate":"30/1","color_transfer":"arib-std-b67","color_primaries":"bt2020","color_space":"bt2020nc"}],"format":{"duration":"2.0"}}""";
        const string untagged = """{"streams":[{"codec_type":"video","width":1920,"height":1080,"codec_name":"h264","pix_fmt":"yuv420p","r_frame_rate":"30/1","color_transfer":"unknown"}],"format":{"duration":"2.0"}}""";

        MediaProbeResult hlg = FfprobeOutputParser.Parse(tagged);
        hlg.ColorTransfer.Should().Be("arib-std-b67");
        hlg.ColorPrimaries.Should().Be("bt2020");
        hlg.ColorSpace.Should().Be("bt2020nc");
        MediaProbeResult sdr = FfprobeOutputParser.Parse(untagged);
        sdr.ColorTransfer.Should().BeNull();
        HdrToneMap.KindOf(sdr).Should().BeNull();
    }

    [Fact]
    public void The_stored_probe_carries_the_facts_and_an_old_stored_probe_still_loads()
    {
        MediaSourceProbe stored = MediaDerivativeDispatcher.ToStoredProbe(
            new MediaProbeResult(2, 30, 1, 1920, 1080, "hevc", "aac", 48000, ColorTransfer: "smpte2084", ColorPrimaries: "bt2020"));
        stored.Hdr.Should().Be("pq");
        MediaSourceProbe.TryParse(stored.ToJson())!.Hdr.Should().Be("pq");

        const string old = """{"durationSec":2,"fpsNum":30,"fpsDen":1,"width":1920,"height":1080,"videoCodec":"h264","hasAudio":true}""";
        MediaSourceProbe loaded = MediaSourceProbe.TryParse(old)!;
        loaded.Hdr.Should().BeNull();
        loaded.ColorTransfer.Should().BeNull();
    }

    [Fact]
    public void The_capability_check_needs_both_filters()
    {
        const string both = " .S zscale            V->V       Apply resizing\n .S tonemap           V->V       Conversion\n";
        FfmpegHdrToneMapCapability.ContainsFilters(both).Should().BeTrue();
        FfmpegHdrToneMapCapability.ContainsFilters(" .S tonemap           V->V       Conversion\n").Should().BeFalse();
        FfmpegHdrToneMapCapability.ContainsFilters(" T. tonemap_opencl     V->V       OpenCL\n").Should().BeFalse();
    }

    [Fact]
    public void The_filter_chain_is_fixed_text_per_kind()
    {
        HdrToneMap.Filter("hlg").Should().Be("zscale=t=linear:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:param=0.2:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p");
        HdrToneMap.Filter("pq").Should().Be("zscale=t=linear:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:param=0.5:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p", "PQ is unchanged");
        HdrToneMap.Filter("pq").Should().Contain("npl=203").And.Contain("tonemap=mobius");
    }

    [Fact]
    public void Pq_delivery_filter_is_byte_identical_to_the_chain_shipped_before_the_hlg_change()
    {
        // PQ must not regress. This is the exact delivery chain before hdr-tonemap-v3 (when both kinds used
        // param=0.5). ffmpeg is deterministic for a given filter text and input, so text equality is the
        // output guarantee; The_filter_chain_is_fixed_text_per_kind also pins that HLG differs only here.
        const string beforeTheHlgChange =
            "zscale=t=linear:npl=203,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=mobius:param=0.5:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

        HdrToneMap.Filter(HdrToneMap.Pq).Should().Be(beforeTheHlgChange);
        HdrToneMap.Filter(HdrToneMap.Hlg).Should().Be(beforeTheHlgChange.Replace("param=0.5", "param=0.2"),
            "the HLG knee is the only difference between the two kinds");
    }

    [Fact]
    public void Tone_mapped_stills_proxy_and_frame_grabs_start_with_the_filter_and_sdr_argv_is_unchanged()
    {
        string f = HdrToneMap.Filter("hlg");
        string[] sdr = FfmpegArgvBuilder.BuildKeyframeArgs("in.mp4", "o.jpg", 1, 512);
        string[] hdr = FfmpegArgvBuilder.BuildKeyframeArgs("in.mp4", "o.jpg", 1, 512, "hlg");
        sdr[sdr.ToList().IndexOf("-vf") + 1].Should().Be("scale='min(512,iw)':-2");
        hdr[hdr.ToList().IndexOf("-vf") + 1].Should().Be("scale='min(512,iw)':-2," + f);

        string[] thumb = FfmpegArgvBuilder.BuildThumbnailArgs("in.mp4", "o.jpg", 1, 320, 180, true, 4, null, "pq");
        thumb[thumb.ToList().IndexOf("-vf") + 1].Should().StartWith(HdrToneMap.Filter("pq") + ",scale=320:180");

        string[] frame = FfmpegArgvBuilder.BuildExtractFrameArgs("in.mp4", "o.png", FrameAt.Time, 1, "png", null, null, "hlg");
        frame[frame.ToList().IndexOf("-vf") + 1].Should().Be(f);
        FfmpegArgvBuilder.BuildExtractFrameArgs("in.mp4", "o.png", FrameAt.Time, 1, "png", null, null).Should().NotContain("-vf");

        string[] proxy = ProxyArgvBuilder.Build("in.mp4", "o.mp4", 360, 15, true, 2, false, "hlg");
        proxy[proxy.ToList().IndexOf("-vf") + 1].Should().StartWith("scale=").And.EndWith("," + f);
        proxy.Should().ContainInOrder("-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709");
        ProxyArgvBuilder.Build("in.mp4", "o.mp4", 360, 15, true, 2, false).Should().NotContain("-colorspace");

        StillsArgvBuilder.Build("in.mp4", "o.jpg", 500, "hlg").Should().Contain(a => a.StartsWith("scale=") && a.EndsWith("," + f));
        FilmstripArgvBuilder.BuildMultiDensity("in.mp4", [(5, "s-%03d.jpg")], "pq").Should().Contain(a => a.Contains("fps=1/5") && a.Contains("]scale=160:-2," + HdrToneMap.Filter("pq") + ",tile="));
        FilmstripArgvBuilder.BuildMultiDensity("in.mp4", [(5, "s-%03d.jpg")]).Should().Contain(a => a.Contains("fps=1/5"));
    }

    [Fact]
    public void The_recipe_version_and_output_tags_are_the_v3_ones()
    {
        HdrToneMap.RecipeToken("hlg").Should().Be("hdr-tonemap-v3:hlg:mobius");
        List<string> args = [];
        VideoCompileStepExecutor.AddOutputVideoArgs(args, "libx264", 20, "veryfast", tagBt709: true);
        args.Should().ContainInOrder("-color_range", "tv", "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709");
    }

    [Fact]
    public void The_step_output_names_tone_mapped_sources_and_the_warning_without_ids()
    {
        TimelineRenderResult result = new(
            "/o.mp4", TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings),
            new RenderPlan(0, 30, [], [], []), [], null, false, [],
            [new HdrToneMapped("Beach", "hlg")], [new RenderWarning(HdrToneMap.UnavailableWarning, HdrToneMap.UnavailableMessage)]);

        System.Text.Json.Nodes.JsonObject json = TimelineRenderStepExecutor.BuildOutput(
            new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Guid.NewGuid(), 1, result.Target, result, "k", "t", null, 30);

        json["hdrToneMapped"]!.ToJsonString().Should().Be("""[{"name":"Beach","kind":"hlg"}]""");
        json["warnings"]!.ToJsonString().Should().Contain("cannot convert HDR");
        json["warningCodes"]!.ToJsonString().Should().Be("""["hdr_tonemap_unavailable"]""");
    }

    // ── real ffmpeg ──────────────────────────────────────────────────────────

    [HdrFact]
    public void Probe_detection_tells_hlg_pq_and_sdr_apart()
    {
        Probe(MakeHdr("hlg", "arib-std-b67")).ColorTransfer.Should().Be("arib-std-b67");
        MediaProbeResult pq = Probe(MakeHdr("pq", "smpte2084"));
        HdrToneMap.KindOf(pq).Should().Be("pq");
        pq.ColorPrimaries.Should().Be("bt2020");
        HdrToneMap.KindOf(Probe(MakeSdr("sdr"))).Should().BeNull();
    }

    [HdrFact]
    public async Task A_graded_hlg_clip_is_tone_mapped_first_so_highlights_are_lower_and_not_clipped()
    {
        AssetRef hlg = Register("hlg", MakeHdr("hlg", "arib-std-b67"));
        EditTimeline doc = Doc(Graded(hlg));

        (TimelineRenderResult mapped, string mappedOut) = await Render(doc, new Fake(true));
        (TimelineRenderResult raw, string rawOut) = await Render(doc, new Fake(false));

        mapped.HdrToneMapped.Should().ContainSingle().Which.Should().Be(new HdrToneMapped("graded", "hlg"));
        raw.HdrToneMapped.Should().BeEmpty();
        string mappedGraph = string.Join(' ', mapped.Invocations.SelectMany(i => i));
        mappedGraph.Should().Contain("zscale=t=linear:npl=203");
        mappedGraph.IndexOf("zscale=", StringComparison.Ordinal).Should().BeLessThan(mappedGraph.IndexOf("trim=", StringComparison.Ordinal), "the tone-map is the first filter of the leg");

        Stats m = SignalStats(mappedOut), r = SignalStats(rawOut);
        m.YHigh.Should().BeLessThan(r.YHigh, "tone-mapped highlights are lower than the untouched HDR source");
        m.YHigh.Should().BeLessThan(235, "the 90th percentile of luma stays inside the legal SDR range");
        m.YAvg.Should().BeInRange(40, 200);
    }

    [HdrFact]
    public async Task A_pq_clip_renders_through_the_tone_map()
    {
        AssetRef pq = Register("pq", MakeHdr("pq", "smpte2084"));
        (TimelineRenderResult result, string output) = await Render(Doc(Graded(pq)), new Fake(true));

        result.HdrToneMapped.Single().Kind.Should().Be("pq");
        string.Join(' ', result.Invocations.SelectMany(i => i)).Should().Contain("npl=203");
        File.Exists(output).Should().BeTrue();
        SignalStats(output).YHigh.Should().BeLessThan(235);
    }

    [HdrFact]
    public async Task Without_the_filters_the_render_still_succeeds_and_says_so()
    {
        AssetRef hlg = Register("hlg", MakeHdr("hlg", "arib-std-b67"));

        (TimelineRenderResult result, string output) = await Render(Doc(Graded(hlg)), new Fake(false));

        File.Exists(output).Should().BeTrue();
        new FileInfo(output).Length.Should().BeGreaterThan(0);
        result.Warnings.Should().ContainSingle().Which.Code.Should().Be("hdr_tonemap_unavailable");
        string.Join(' ', result.Invocations.SelectMany(i => i)).Should().NotContain("zscale");
    }

    [HdrFact]
    public async Task An_sdr_render_never_carries_the_tone_map_and_its_hash_ignores_the_capability()
    {
        AssetRef sdr = Register("sdr", MakeSdr("sdr"));
        EditTimeline doc = Doc(Graded(sdr));

        (TimelineRenderResult on, _) = await Render(doc, new Fake(true));
        (TimelineRenderResult off, _) = await Render(doc, new Fake(false));

        string.Join(' ', on.Invocations.SelectMany(i => i)).Should().NotContain("zscale");
        on.HdrToneMapped.Should().BeEmpty();
        on.Warnings.Should().BeEmpty();
        on.Regions.Select(x => x.RecipeHash).Should().Equal(off.Regions.Select(x => x.RecipeHash), "SDR region caches must stay valid");
    }

    [HdrFact]
    public async Task The_recipe_hash_of_an_hdr_region_changes_with_the_tone_map()
    {
        AssetRef hlg = Register("hlg", MakeHdr("hlg", "arib-std-b67"));
        EditTimeline doc = Doc(Graded(hlg));

        (TimelineRenderResult on, _) = await Render(doc, new Fake(true));
        (TimelineRenderResult off, _) = await Render(doc, new Fake(false));

        on.Regions.Select(x => x.RecipeHash).Should().NotEqual(off.Regions.Select(x => x.RecipeHash));
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    private static MediaClip Graded(AssetRef asset) =>
        new(Guid.NewGuid(), "graded", 0, 60, asset, Grade: new GradeStack(true, [new GradeNode(new Guid("00000000-0000-4000-8000-000000000901"), "Warm", true, new GradePrimaries(Exposure: 0.5, Saturation: 1.1))]));

    private static EditTimeline Doc(MediaClip clip) =>
        new(Guid.NewGuid(), ProjectId, "doc", Settings,
            [new Track(Guid.NewGuid(), TrackKind.Video, "V1", null, 64, false, true, false, false, null, [clip])], [], null, null);

    private string MakeHdr(string name, string transfer)
    {
        string path = Path.Combine(_work, name + ".mkv");
        Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2,format=yuv420p10le,setparams=color_trc=" + transfer + ":color_primaries=bt2020:colorspace=bt2020nc", "-c:v", "ffv1", "-an", path]);
        return path;
    }

    private string MakeSdr(string name)
    {
        string path = Path.Combine(_work, name + ".mp4");
        Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=2", "-pix_fmt", "yuv420p", "-c:v", "libx264", "-an", path]);
        return path;
    }

    private static MediaProbeResult Probe(string path) =>
        FfprobeOutputParser.Parse(Run("ffprobe", ["-v", "error", "-show_format", "-show_streams", "-of", "json", path]));

    private AssetRef Register(string name, string path)
    {
        AssetRef reference = new(AssetRefKind.ProjectFile, Guid.NewGuid(), null);
        _assets[reference] = new LocalAsset(path, $"{name}|fixed", Probe(path));
        return reference;
    }

    private async Task<(TimelineRenderResult Result, string Output)> Render(EditTimeline doc, IHdrToneMapCapability capability)
    {
        FfmpegVideoToolRunner runner = new(
            Microsoft.Extensions.Options.Options.Create(new VideoEditingOptions { ScratchPath = Path.GetTempPath(), MaxConcurrentJobs = 2, CompileTimeoutSeconds = 120 }),
            NullLogger<FfmpegVideoToolRunner>.Instance);
        string scratch = Path.Combine(_work, "scratch-" + Guid.NewGuid().ToString("N"));
        string output = Path.Combine(_work, "out-" + Guid.NewGuid().ToString("N") + ".mp4");
        TimelineRenderEnvironment env = new(runner, scratch, "/fonts/missing.ttf", TimeSpan.FromMinutes(2), 2, NullRegionCache.Instance, ProjectId, ToneMapCapability: capability);
        TimelineRenderResult result = await new TimelineRenderer(NullLogger.Instance)
            .RenderAsync(doc, new TimelineRenderStepConfig(doc.Id, Preset: TimelineRenderPreset.Master), _assets, output, env, CancellationToken.None);
        return (result, output);
    }

    private sealed class Fake(bool available) : IHdrToneMapCapability
    {
        public Task<bool> IsAvailableAsync(IVideoToolRunner runner, CancellationToken ct) => Task.FromResult(available);
    }

    private readonly record struct Stats(double YAvg, double YHigh, double YMax);

    private static Stats SignalStats(string video)
    {
        string text = Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-i", video, "-vf", "signalstats,metadata=print:file=-", "-an", "-f", "null", "-"]);
        double Mean(string key) => text.Split('\n').Where(l => l.StartsWith("lavfi.signalstats." + key + "=", StringComparison.Ordinal))
            .Select(l => double.Parse(l[(l.IndexOf('=') + 1)..], CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Average();
        return new Stats(Mean("YAVG"), Mean("YHIGH"), Mean("YMAX"));
    }

    private static string Run(string tool, IEnumerable<string> args)
    {
        ProcessStartInfo info = new(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string arg in args) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);
        process.ExitCode.Should().Be(0, $"{tool} failed: {stderr.Result}");
        return stdout;
    }
}

/// <summary>A fact that needs an ffmpeg with <c>zscale</c> and <c>tonemap</c> (skipped, with the reason, without).</summary>
public sealed class HdrFactAttribute : FactAttribute
{
    public HdrFactAttribute()
    {
        try
        {
            ProcessStartInfo info = new("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-hide_banner");
            info.ArgumentList.Add("-filters");
            using Process process = Process.Start(info)!;
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            if (!FfmpegHdrToneMapCapability.ContainsFilters(output))
                Skip = "This host's ffmpeg lacks the zscale/tonemap filters.";
        }
        catch (Exception)
        {
            Skip = "Requires an ffmpeg binary on PATH.";
        }
    }
}
