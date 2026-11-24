#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.TimelineRender;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The TimelineRender pipeline without ffmpeg (P4-M1's unit half; the real-ffmpeg half is the
/// golden-parity gate): the planner's regions and audio plan, the recipe hash that makes the region
/// cache safe (an edit changes exactly the regions it touches), the exact argv the builders emit,
/// the renderer skipping ffmpeg on a cache hit, and the config validator refusing hostile numbers
/// before any of that runs.
/// </summary>
public class TimelineRenderTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly AssetRef AssetB = new(AssetRefKind.ProjectFile, Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"), null);

    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static MediaClip Clip(string id, long recordIn, long recordOut, AssetRef asset, long sourceIn = 0, double rate = 1.0, Keyframed<double>? gain = null) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-{id}{id}{id}{id}-4{id}{id}{id}-8{id}{id}{id}-{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}"), $"clip-{id}", recordIn, recordOut, asset, sourceIn, rate, true, null, gain);

    private static Track VideoTrack(string id, params Item[] items) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-0000-4000-8000-000000000000"), TrackKind.Video, $"V{id}", null, 64, false, true, false, false, null, items);

    private static Track AudioTrack(string id, params Item[] items) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-0000-4000-8000-000000000001"), TrackKind.Audio, $"A{id}", null, 48, false, true, false, false, null, items);

    private static EditTimeline Doc(params Track[] tracks) =>
        new(Guid.NewGuid(), ProjectId, "doc", Settings, tracks, [], null, null);

    private static LocalAsset Asset(string path, string fingerprint) =>
        new(path, fingerprint, new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000));

    private static readonly Dictionary<AssetRef, LocalAsset> Assets = new()
    {
        [AssetA] = Asset("/tmp/a.mp4", "a|100"),
        [AssetB] = Asset("/tmp/b.mp4", "b|200"),
    };

    private static ResolvedRenderTarget Target() =>
        TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);

    // ── SegmentPlanner ────────────────────────────────────────────────────────

    [Fact]
    public void Planner_splits_at_every_item_edge_and_stacks_layers_bottom_to_top()
    {
        EditTimeline doc = Doc(
            VideoTrack("1", Clip("1", 0, 60, AssetA), Clip("2", 60, 90, AssetB)),
            VideoTrack("2", Clip("3", 30, 75, AssetB)),
            AudioTrack("3", Clip("4", 0, 90, AssetA, gain: new Keyframed<double>(-6))));

        RenderPlan plan = SegmentPlanner.Plan(doc, null);

        plan.RangeStartFrame.Should().Be(0);
        plan.RangeEndFrame.Should().Be(90);
        plan.Regions.Select(r => (r.StartFrame, r.EndFrame)).Should().Equal((0, 30), (30, 60), (60, 75), (75, 90));
        plan.Regions[1].Layers.Select(l => l.TrackIndex).Should().Equal([0, 1]); // the second track stacks on top of the first
        plan.Regions[3].Layers.Should().ContainSingle().Which.Item.Name.Should().Be("clip-2");
        plan.AudioClips.Should().ContainSingle();
        plan.AudioClips[0].StartFrame.Should().Be(0);
        plan.AudioClips[0].EndFrame.Should().Be(90);
        plan.SkippedItems.Should().BeEmpty();
    }

    [Fact]
    public void Planner_honours_the_range_disabled_tracks_and_reports_what_it_does_not_render()
    {
        Track disabled = VideoTrack("5", Clip("6", 0, 90, AssetB)) with { Enabled = false };
        EditTimeline doc = Doc(
            VideoTrack("1", Clip("1", 0, 90, AssetA), new Transition(Guid.NewGuid(), "x", 90, 100, "fade")),
            disabled);

        RenderPlan plan = SegmentPlanner.Plan(doc, new TimelineRenderRange(15, 45));

        plan.RangeStartFrame.Should().Be(15);
        plan.RangeEndFrame.Should().Be(45);
        plan.Regions.Should().ContainSingle();
        plan.Regions[0].Layers.Should().ContainSingle("the disabled track contributes nothing");
        // The transition lies outside the range and is therefore not even reported; a range that
        // covers it reports it as skipped, since it has no media clip on its right.
        plan.SkippedItems.Should().BeEmpty();
        RenderPlan whole = SegmentPlanner.Plan(doc, null);
        whole.SkippedItems.Should().ContainSingle().Which.Reason.Should().Be("transition_neighbours_not_media");
    }

    // ── RegionRecipeHasher ────────────────────────────────────────────────────

    [Fact]
    public void Trimming_one_clip_changes_only_the_regions_that_clip_covers()
    {
        EditTimeline before = Doc(VideoTrack("1", Clip("1", 0, 60, AssetA), Clip("2", 60, 120, AssetB), Clip("3", 120, 180, AssetA)));
        // The middle clip's source-in moves by 5 frames: regions 0 and 2 are untouched.
        EditTimeline after = Doc(VideoTrack("1", Clip("1", 0, 60, AssetA), Clip("2", 60, 120, AssetB, sourceIn: 5), Clip("3", 120, 180, AssetA)));

        string[] hashesBefore = Hashes(before);
        string[] hashesAfter = Hashes(after);

        hashesBefore.Should().HaveCount(3);
        hashesAfter[0].Should().Be(hashesBefore[0]);
        hashesAfter[1].Should().NotBe(hashesBefore[1]);
        hashesAfter[2].Should().Be(hashesBefore[2]);
    }

    [Fact]
    public void The_recipe_hash_depends_on_the_asset_fingerprint_the_target_and_the_renderer_version_but_not_on_the_region_position()
    {
        EditTimeline doc = Doc(VideoTrack("1", Clip("1", 0, 60, AssetA)));
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions[0];

        string baseline = RegionRecipeHasher.Hash(region, Target(), Assets, "font");
        RegionRecipeHasher.Hash(region, Target(), Assets, "font").Should().Be(baseline, "the hash is a pure function");

        Dictionary<AssetRef, LocalAsset> changed = new(Assets) { [AssetA] = Asset("/tmp/a.mp4", "a|101") };
        RegionRecipeHasher.Hash(region, Target(), changed, "font").Should().NotBe(baseline, "a re-uploaded asset is a different recipe");
        RegionRecipeHasher.Hash(region, Target(), Assets, "other-font").Should().NotBe(baseline, "a font swap changes glyphs");

        // The same clip shifted along the timeline (same source range, same length) is the same region recipe.
        EditTimeline shifted = Doc(VideoTrack("1", Clip("1", 100, 160, AssetA)));
        RenderRegion shiftedRegion = SegmentPlanner.Plan(shifted, new TimelineRenderRange(100, 160)).Regions[0];
        RegionRecipeHasher.Hash(shiftedRegion, Target(), Assets, "font").Should().Be(baseline);
        RegionRecipeHasher.RendererVersion.Should().Be("timeline-render-region-v7", "bump this when the region argv changes for the same inputs");
    }

    private static string Graph(IReadOnlyList<string> args) => args[args.ToList().IndexOf("-filter_complex") + 1];

    private static string[] Hashes(EditTimeline doc, string fontFingerprint = "font") =>
        SegmentPlanner.Plan(doc, null).Regions.Select(r => RegionRecipeHasher.Hash(r, Target(), Assets, fontFingerprint)).ToArray();

    // ── RegionFilterGraphBuilder ──────────────────────────────────────────────

    [Fact]
    public void A_single_clip_region_builds_the_exact_mezzanine_argv()
    {
        EditTimeline doc = Doc(VideoTrack("1", Clip("1", 0, 30, AssetA, sourceIn: 15)));
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions[0];

        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(region, Target(), Assets, "/scratch/region.mp4", "/fonts/f.ttf", n => $"/scratch/text-{n}.txt", 2);

        argv.TextFiles.Should().BeEmpty();
        argv.Args.Should().Equal(
            "-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "2",
            "-protocol_whitelist", "file", "-format_whitelist", UserMediaInputFormats.Whitelist,
            "-ss", "0.5", "-t", "1.5", "-i", "/tmp/a.mp4",
            "-filter_complex",
            "color=c=black:s=320x180:r=30/1:d=1[base];[0:v]trim=duration=1,setpts=PTS-STARTPTS,fps=30/1,scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p[l0];[base][l0]overlay=eof_action=pass[v0];[v0]format=yuv420p[vout]",
            "-map", "[vout]", "-an",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "10", "-g", "1", "-keyint_min", "1", "-pix_fmt", "yuv420p", "-x264-params", "scenecut=0:open_gop=0",
            "-r", "30/1", "-frames:v", "30", "-movflags", "+faststart", "/scratch/region.mp4");
    }

    [Fact]
    public void Title_text_goes_through_the_sanitiser_into_a_textfile_never_into_the_filter_string()
    {
        TitleClip title = new(Guid.NewGuid(), "t", 0, 30, "Hello:world,'drawtext=1' %{eif}", 40, "Arial", "#FF0000");
        EditTimeline doc = Doc(VideoTrack("1", Clip("1", 0, 30, AssetA)), VideoTrack("2", title));
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions[0];

        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(region, Target(), Assets, "/scratch/region.mp4", "/fonts/f.ttf", n => $"/scratch/text-{n}.txt", 2);

        argv.TextFiles.Should().ContainSingle();
        argv.TextFiles[0].Path.Should().Be("/scratch/text-0.txt");
        argv.TextFiles[0].Contents.Should().Be(ReelBolt.Shared.Editing.OverlayTextSanitizer.Sanitize(title.Text, RegionFilterGraphBuilder.MaxTitleChars));
        argv.TextFiles[0].Contents.Should().NotContain("%{", "the sanitiser strips drawtext expansion syntax");
        string graph = Graph(argv.Args);
        graph.Should().Contain("drawtext=fontfile='/fonts/f.ttf':textfile='/scratch/text-0.txt':expansion=none:fontsize=40:fontcolor=#FF0000");
        graph.Should().NotContain("Hello", "the text itself never appears in argv");
    }

    // ── AudioMixBuilder ───────────────────────────────────────────────────────

    [Fact]
    public void Two_clips_mix_sample_accurately_with_a_keyframed_gain_as_a_balanced_if_tree()
    {
        Keyframed<double> envelope = new(0, [new Keyframe<double>(0, -12, InterpKind.Linear), new Keyframe<double>(30, 0, InterpKind.Step), new Keyframe<double>(60, -6, InterpKind.Linear)]);
        EditTimeline doc = Doc(AudioTrack("3", Clip("1", 0, 60, AssetA, gain: envelope), Clip("2", 60, 90, AssetB, sourceIn: 30, rate: 2.0)));
        RenderPlan plan = SegmentPlanner.Plan(doc, null);

        IReadOnlyList<string> args = AudioMixBuilder.BuildMixArgs(plan, Target(), Assets, "/scratch/mix.wav", 2);

        args.Take(7).Should().Equal("-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "2");
        args.Should().ContainInOrder("-ss", "0", "-t", "2.5", "-i", "/tmp/a.mp4", "-ss", "1", "-t", "2.5", "-i", "/tmp/b.mp4");
        string graph = Graph(args);
        graph.Should().StartWith("[0:a]atrim=duration=2,asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=stereo,volume='pow(10,(if(lt(t,1),if(lt(t,0),-12,(-12+(12)*clip((t-0)/(1-0),0,1))),if(lt(t,2),0,-6)))/20)':eval=frame,adelay=0|0,apad=whole_dur=3[a0]");
        graph.Should().Contain("[1:a]atrim=duration=2,asetpts=PTS-STARTPTS,aresample=48000,aformat=channel_layouts=stereo,atempo=2,adelay=2000|2000,apad=whole_dur=3[a1]");
        graph.Should().EndWith("[a0][a1]amix=inputs=2:normalize=0:duration=longest,apad=whole_dur=3,atrim=duration=3,asetpts=PTS-STARTPTS[aout]");
        args.TakeLast(9).Should().Equal("-map", "[aout]", "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2", "/scratch/mix.wav");
    }

    [Fact]
    public void Every_mixed_clip_is_padded_to_the_program_length_so_the_mix_never_ends_at_the_last_sound()
    {
        // Program 3 s (video to frame 90); the two sounds end at 0.2 s and 1.6 s.
        EditTimeline doc = Doc(
            VideoTrack("1", Clip("1", 0, 90, AssetA)),
            AudioTrack("3", Clip("2", 0, 6, AssetA)),
            AudioTrack("4", Clip("3", 42, 48, AssetB)));

        string graph = Graph(AudioMixBuilder.BuildMixArgs(SegmentPlanner.Plan(doc, null), Target(), Assets, "/scratch/mix.wav", 1, doc.Tracks));

        graph.Should().Contain("adelay=0|0,apad=whole_dur=3[a0]");
        graph.Should().Contain("adelay=1400|1400,apad=whole_dur=3[a1]");
    }

    [Fact]
    public async Task The_delivery_encode_is_cut_at_the_program_length_never_by_the_shorter_stream()
    {
        EditTimeline doc = Doc(VideoTrack("1", Clip("1", 0, 90, AssetA)), AudioTrack("3", Clip("2", 0, 6, AssetA)));
        string scratch = Path.Combine(Path.GetTempPath(), "tl-render-" + Guid.NewGuid().ToString("N"));
        RecordingRunner runner = new();
        TimelineRenderEnvironment env = new(runner, scratch, "/fonts/missing.ttf", TimeSpan.FromMinutes(1), 1, NullRegionCache.Instance, ProjectId);

        TimelineRenderResult result = await new TimelineRenderer(NullLogger.Instance).RenderAsync(
            doc, new TimelineRenderStepConfig(doc.Id, Preset: TimelineRenderPreset.Master), Assets, Path.Combine(scratch, "out.mp4"), env, CancellationToken.None);

        IReadOnlyList<string> final = result.Invocations[^1];
        final.Should().NotContain("-shortest");
        final.Should().ContainInOrder("-t", "3", "-movflags", "+faststart", Path.Combine(scratch, "out.mp4"));
        Directory.Delete(scratch, recursive: true);
    }

    [Theory]
    [InlineData(1.0, "")]
    [InlineData(1.0 + 1e-12, "")]
    [InlineData(1.0 - 1e-12, "")]
    [InlineData(4.0, "atempo=2,atempo=2,")]
    [InlineData(0.25, "atempo=0.5,atempo=0.5,")]
    [InlineData(1.5, "atempo=1.5,")]
    public void Atempo_never_emits_an_empty_stage(double rate, string expected)
    {
        AudioMixBuilder.Atempo(rate).Should().Be(expected);
    }

    [Fact]
    public void A_document_without_audio_mixes_digital_silence_of_the_program_length()
    {
        EditTimeline doc = Doc(VideoTrack("1", Clip("1", 0, 45, AssetA)));
        IReadOnlyList<string> args = AudioMixBuilder.BuildMixArgs(SegmentPlanner.Plan(doc, null), Target(), Assets, "/scratch/mix.wav", 1);
        Graph(args).Should().Be("anullsrc=r=48000:cl=stereo,atrim=duration=1.5[aout]");
    }

    [Fact]
    public void Too_many_keyframed_gains_in_one_graph_are_refused_before_ffmpeg_sees_them()
    {
        Keyframed<double> envelope = new(0, [new Keyframe<double>(0, -12, InterpKind.Linear), new Keyframe<double>(10, 0, InterpKind.Linear)]);
        KeyframeExpressionBuilder.MaxKeyframedParametersPerGraph.Should().BeLessThan(9, "the clip ids below are single digits");
        Item[] clips = Enumerable.Range(0, KeyframeExpressionBuilder.MaxKeyframedParametersPerGraph + 1)
            .Select(i => (Item)Clip((i + 1).ToString(), i * 10, i * 10 + 10, AssetA, gain: envelope))
            .ToArray();
        EditTimeline doc = Doc(AudioTrack("3", clips));
        Action act = () => AudioMixBuilder.BuildMixArgs(SegmentPlanner.Plan(doc, null), Target(), Assets, "/scratch/mix.wav", 1);
        act.Should().Throw<TimelineRenderLimitException>().WithMessage("*a render can automate at most 8*");
    }

    // ── TimelineRenderer ─────────────────────────────────────────────────────

    [Fact]
    public async Task A_cached_region_skips_its_ffmpeg_invocation_and_is_reported_as_from_cache()
    {
        EditTimeline doc = Doc(VideoTrack("1", Clip("1", 0, 30, AssetA), Clip("2", 30, 60, AssetB)));
        string scratch = Path.Combine(Path.GetTempPath(), "tl-render-" + Guid.NewGuid().ToString("N"));
        RecordingRunner runner = new();
        // The renderer fingerprints the font file it is given; a missing file hashes as "missing".
        string[] hashes = Hashes(doc, "missing");
        HitOnceCache cache = new(hashes[0]);

        TimelineRenderEnvironment env = new(runner, scratch, "/fonts/missing.ttf", TimeSpan.FromMinutes(1), 1, cache, ProjectId);
        TimelineRenderStepConfig config = new(doc.Id, Preset: TimelineRenderPreset.Master);
        TimelineRenderResult result = await new TimelineRenderer(NullLogger.Instance).RenderAsync(doc, config, Assets, Path.Combine(scratch, "out.mp4"), env, CancellationToken.None);

        result.Regions.Should().HaveCount(2);
        result.Regions[0].FromCache.Should().BeTrue();
        result.Regions[1].FromCache.Should().BeFalse();
        result.CachedRegionCount.Should().Be(1);
        // One region render (not two), the concat, the mix, the loudness measurement and the delivery encode.
        runner.Invocations.Count(a => a.Contains("-filter_complex") && a.Contains("-an")).Should().Be(1, "the cached region never reached ffmpeg");
        runner.Invocations.Should().ContainSingle(a => a.Contains("-f") && a.Contains("concat"));
        runner.Invocations.Should().ContainSingle(a => a.Contains("loudnorm=I=-16:TP=-1.5:LRA=11:print_format=json"));
        result.LoudnessNormalised.Should().BeFalse("no Loudness target was asked for");
        result.MeasuredLoudness.Should().BeNull("the fake runner printed no loudnorm block");
        cache.Stored.Should().Equal([hashes[1]]); // only the freshly rendered region is stored
        result.Invocations.Should().HaveCount(6);
        Directory.Delete(scratch, recursive: true);
    }

    [Fact]
    public async Task An_unresolved_asset_is_refused_before_any_ffmpeg_call()
    {
        EditTimeline doc = Doc(VideoTrack("1", Clip("1", 0, 30, AssetA)));
        RecordingRunner runner = new();
        TimelineRenderEnvironment env = new(runner, Path.GetTempPath(), "/fonts/f.ttf", TimeSpan.FromMinutes(1), 1, NullRegionCache.Instance, ProjectId);
        Func<Task> act = () => new TimelineRenderer(NullLogger.Instance).RenderAsync(doc, new TimelineRenderStepConfig(doc.Id), new Dictionary<AssetRef, LocalAsset>(), "/tmp/out.mp4", env, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*was not resolved before rendering*");
        runner.Invocations.Should().BeEmpty();
    }

    // ── TimelineRenderStepConfig.Validate: the trust boundary ────────────────

    [Theory]
    [InlineData(0, 18, 192, "timeline-render.mp4", "editTimelineId is required.")]
    [InlineData(1, 60, 192, "timeline-render.mp4", "encode.crf must be between 0 and 51, found 60.")]
    [InlineData(1, 18, 32, "timeline-render.mp4", "encode.audioBitrateKbps must be between 64 and 320, found 32.")]
    [InlineData(1, 18, 192, "../escape.mp4", "outputFileName may only contain letters, digits, '.', '_', '-' and spaces, must start with a letter or digit, and may not contain '..'.")]
    [InlineData(1, 18, 192, "out;rm -rf.mp4", "outputFileName may only contain letters, digits, '.', '_', '-' and spaces, must start with a letter or digit, and may not contain '..'.")]
    [InlineData(1, 18, 192, "", "outputFileName is required.")]
    public void Hostile_config_numbers_and_names_die_at_the_validator(int idKind, int crf, int audioKbps, string fileName, string expected)
    {
        TimelineRenderStepConfig config = new(
            idKind == 0 ? Guid.Empty : Guid.NewGuid(),
            Encode: new TimelineRenderEncodeSettings(Crf: crf, AudioBitrateKbps: audioKbps),
            OutputFileName: fileName);
        config.Validate().Should().Contain(expected);
    }

    [Fact]
    public void Odd_or_lopsided_dimensions_and_out_of_range_loudness_are_each_named()
    {
        TimelineRenderStepConfig config = new(
            Guid.NewGuid(),
            Range: new TimelineRenderRange(10, 5),
            Encode: new TimelineRenderEncodeSettings(Width: 1001, Height: null),
            Loudness: new TimelineRenderLoudness(TargetLufs: -40, TruePeakDbtp: 1, LoudnessRange: 0));
        List<string> errors = config.Validate();
        errors.Should().Contain("range.outFrame (5) must be greater than range.inFrame (10).");
        errors.Should().Contain("encode.width must be an even number between 16 and 7680, found 1001.");
        errors.Should().Contain("encode.width and encode.height must be supplied together.");
        errors.Should().Contain("loudness.targetLufs must be between -36 and -5, found -40.");
        errors.Should().Contain("loudness.truePeakDbtp must be between -9 and 0, found 1.");
        errors.Should().Contain("loudness.loudnessRange must be between 1 and 30, found 0.");
        new TimelineRenderStepConfig(Guid.NewGuid()).Validate().Should().BeEmpty("the defaults are a valid render");
    }

    [Fact]
    public void Presets_resolve_to_the_first_party_table_and_encode_settings_only_refine_within_the_allowlist()
    {
        ResolvedRenderTarget hd = TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid()), Settings);
        (hd.Width, hd.Height, hd.Encoder, hd.EncoderPreset, hd.Crf, hd.VideoEnabled).Should().Be((1920, 1080, "libx264", "medium", 18, true));

        ResolvedRenderTarget master = TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);
        (master.Width, master.Height, master.Crf, master.EncoderPreset).Should().Be((320, 180, 14, "slow"));

        ResolvedRenderTarget audio = TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.AudioOnly), Settings);
        audio.VideoEnabled.Should().BeFalse();
        audio.Extension.Should().Be(".m4a");

        ResolvedRenderTarget refined = TimelineRenderPresets.Resolve(
            new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Vertical9x16, Encode: new TimelineRenderEncodeSettings(Codec: TimelineRenderVideoCodec.H265, Crf: 24, Speed: TimelineRenderEncoderSpeed.Fast, AudioBitrateKbps: 96)), Settings);
        (refined.Width, refined.Height, refined.Encoder, refined.EncoderPreset, refined.Crf, refined.AudioBitrateKbps).Should().Be((1080, 1920, "libx265", "veryfast", 24, 96));
    }

    // ── P5: clip depth ───────────────────────────────────────────────────────

    private static MediaClip Depth(MediaClip clip, ClipTransform? transform = null, ClipCrop? crop = null, bool reverse = false, bool hold = false, long fadeIn = 0, long fadeOut = 0) =>
        new(clip.Id, clip.Name, clip.RecordIn, clip.RecordOut, clip.AssetRef, clip.SourceIn, clip.PlaybackRate, clip.Enabled, clip.Provenance, clip.GainDb, transform, crop, reverse, hold, fadeIn, fadeOut);

    [Fact]
    public void A_clip_without_depth_fields_keeps_the_P4_chain_byte_for_byte_and_the_P4_recipe()
    {
        EditTimeline doc = Doc(VideoTrack("1", Clip("1", 0, 30, AssetA, sourceIn: 15)));
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions[0];
        string graph = Graph(RegionFilterGraphBuilder.Build(region, Target(), Assets, "/scratch/region.mp4", "/fonts/f.ttf", n => $"/scratch/text-{n}.txt", 2).Args);
        graph.Should().Be("color=c=black:s=320x180:r=30/1:d=1[base];[0:v]trim=duration=1,setpts=PTS-STARTPTS,fps=30/1,scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p[l0];[base][l0]overlay=eof_action=pass[v0];[v0]format=yuv420p[vout]");
        TransformFilterBuilder.IsIdentity((MediaClip)region.Layers[0].Item).Should().BeTrue();
    }

    [Fact]
    public void A_transformed_clip_fits_without_padding_scales_rotates_sets_alpha_and_overlays_at_its_position()
    {
        ClipTransform transform = new(
            PositionX: new Keyframed<double>(0.25),
            Scale: new Keyframed<double>(0, [new Keyframe<double>(0, 1, InterpKind.Linear), new Keyframe<double>(30, 2, InterpKind.EaseInOutQuad)]),
            Rotation: new Keyframed<double>(90),
            Opacity: new Keyframed<double>(0.5));
        MediaClip clip = Depth(Clip("1", 0, 30, AssetA), transform, new ClipCrop(Left: 0.1, Right: 0.2));
        EditTimeline doc = Doc(VideoTrack("1", clip));
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions[0];

        string graph = Graph(RegionFilterGraphBuilder.Build(region, Target(), Assets, "/scratch/region.mp4", "/fonts/f.ttf", n => $"/scratch/text-{n}.txt", 2).Args);

        graph.Should().Contain("[0:v]trim=duration=1,setpts=PTS-STARTPTS,fps=30/1,crop=iw*0.7:ih*1:iw*0.1:ih*0,scale=320:180:force_original_aspect_ratio=decrease,");
        // The first key's interpolation governs the segment after it (linear here); the second key's easing would govern a segment after IT.
        graph.Should().Contain("scale=w='trunc(iw*(if(lt(t,1),if(lt(t,0),1,(1+(1)*clip((t-0)/(1-0),0,1))),2))/2)*2':h='trunc(ih*(");
        graph.Should().Contain(":eval=frame,format=rgba,rotate=a='(90)*PI/180':ow='rotw((90)*PI/180)':oh='roth((90)*PI/180)':c=0x00000000,colorchannelmixer=aa=0.5,setsar=1[l0]");
        graph.Should().Contain("[base][l0]overlay=x='(W-w)/2+W*(0.25)':y='(H-h)/2':eval=frame:format=auto:eof_action=pass[v0]");
        graph.Should().NotContain("pad=", "a transformed layer is positioned, never padded to the canvas");
    }

    [Fact]
    public void Fades_and_a_keyframed_opacity_become_one_geq_alpha_curve_only_where_they_vary()
    {
        MediaClip clip = Depth(Clip("1", 0, 90, AssetA), fadeIn: 30, fadeOut: 30);
        EditTimeline doc = Doc(VideoTrack("1", clip), VideoTrack("2", new TitleClip(Guid.NewGuid(), "t", 30, 60, "mid")));
        RenderPlan plan = SegmentPlanner.Plan(doc, null);
        plan.Regions.Select(r => (r.StartFrame, r.EndFrame)).Should().Equal((0, 30), (30, 60), (60, 90));

        string head = Graph(RegionFilterGraphBuilder.Build(plan.Regions[0], Target(), Assets, "/scratch/r0.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1).Args);
        head.Should().Contain("format=gbrap,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='alpha(X,Y)*clip(clip((T-(0))/1,0,1),0,1)'", "the fade-in ramps over this region in region-local time");

        string middle = Graph(RegionFilterGraphBuilder.Build(plan.Regions[1], Target(), Assets, "/scratch/r1.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1).Args);
        middle.Should().NotContain("geq", "neither fade overlaps the middle region, so its alpha is constantly 1");
        middle.Should().NotContain("colorchannelmixer");

        string tail = Graph(RegionFilterGraphBuilder.Build(plan.Regions[2], Target(), Assets, "/scratch/r2.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1).Args);
        tail.Should().Contain("a='alpha(X,Y)*clip(clip((1-T)/1,0,1),0,1)'", "the fade-out ends at the clip's end, one second into this region");
    }

    [Fact]
    public void Reverse_and_hold_read_the_source_as_the_planner_says_and_the_mix_follows()
    {
        MediaClip reversed = Depth(Clip("1", 0, 30, AssetA, sourceIn: 60), reverse: true);
        MediaClip held = Depth(Clip("2", 30, 60, AssetB, sourceIn: 90), hold: true);
        EditTimeline doc = Doc(VideoTrack("1", reversed, held), AudioTrack("3", Depth(Clip("3", 0, 30, AssetA, sourceIn: 60), reverse: true), Depth(Clip("4", 30, 60, AssetB), hold: true)));
        RenderPlan plan = SegmentPlanner.Plan(doc, null);

        SegmentPlanner.SourceFrameAt(reversed, 0).Should().Be(90);
        SegmentPlanner.SourceFrameAt(reversed, 29).Should().Be(61);
        SegmentPlanner.SourceFrameAt(held, 45).Should().Be(90);

        RegionFilterGraphBuilder.RegionArgv first = RegionFilterGraphBuilder.Build(plan.Regions[0], Target(), Assets, "/scratch/r0.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1);
        first.Args.Should().ContainInOrder("-ss", "2", "-t", "1.5", "-i", "/tmp/a.mp4");
        Graph(first.Args).Should().Contain("[0:v]trim=duration=1,setpts=PTS-STARTPTS,reverse,setpts=PTS-STARTPTS,fps=30/1,scale=320:180");

        RegionFilterGraphBuilder.RegionArgv second = RegionFilterGraphBuilder.Build(plan.Regions[1], Target(), Assets, "/scratch/r1.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1);
        second.Args.Should().ContainInOrder("-ss", "3", "-i", "/tmp/b.mp4");
        Graph(second.Args).Should().Contain("[0:v]trim=end_frame=1,setpts=PTS-STARTPTS,loop=loop=-1:size=1:start=0,trim=duration=1,setpts=N/(30/1)/TB,fps=30/1,scale=320:180");

        plan.AudioClips.Should().ContainSingle("the held clip is silent").Which.Clip.Reverse.Should().BeTrue();
        string mix = Graph(AudioMixBuilder.BuildMixArgs(plan, Target(), Assets, "/scratch/mix.wav", 1, doc.Tracks));
        mix.Should().StartWith("[0:a]atrim=duration=1,asetpts=PTS-STARTPTS,areverse,aresample=48000,");
    }

    [Fact]
    public void A_transition_renders_both_legs_over_the_whole_span_blends_with_xfade_and_trims_to_the_region()
    {
        MediaClip from = Clip("1", 0, 60, AssetA);
        Transition transition = new(Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee"), "x", 60, 90, "wipeleft");
        MediaClip to = Clip("2", 90, 150, AssetB, sourceIn: 45);
        EditTimeline doc = Doc(VideoTrack("1", from, transition, to), VideoTrack("2", new TitleClip(Guid.NewGuid(), "t", 75, 150, "late")));
        RenderPlan plan = SegmentPlanner.Plan(doc, null);

        plan.Regions.Select(r => (r.StartFrame, r.EndFrame)).Should().Equal((0, 60), (60, 75), (75, 90), (90, 150));
        plan.SkippedItems.Should().BeEmpty();
        RenderLayer layer = plan.Regions[2].Layers[0];
        layer.Item.Should().BeSameAs(transition);
        layer.Transition!.From.Should().BeSameAs(from);
        layer.Transition.To.Should().BeSameAs(to);

        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(plan.Regions[2], Target(), Assets, "/scratch/r2.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1);
        // The outgoing leg continues past its own end (source 60..90 frames = 2 s), the incoming leg is
        // pulled back before its start (source 45 - 30 = 15 frames = 0.5 s); both read the whole 1 s span.
        argv.Args.Should().ContainInOrder("-ss", "2", "-t", "1.5", "-i", "/tmp/a.mp4", "-ss", "0.5", "-t", "1.5", "-i", "/tmp/b.mp4");
        string graph = Graph(argv.Args);
        graph.Should().Contain("[0:v]trim=duration=1,setpts=PTS-STARTPTS,fps=30/1,tpad=stop=-1:stop_mode=clone,trim=duration=1,scale=320:180:force_original_aspect_ratio=decrease,pad=320:180:(ow-iw)/2:(oh-ih)/2,setsar=1,format=yuv420p[x0a]");
        graph.Should().Contain("[x0a][x0b]xfade=transition=wipeleft:duration=1:offset=0,trim=start=0.5:duration=0.5,setpts=PTS-STARTPTS,format=yuv420p[l0]");
        graph.Should().Contain("[base][l0]overlay=eof_action=pass[v0]");
        graph.Should().Contain("drawtext=", "the title stacks on top of the blend");

        // Both sub-regions of the transition hash differently (different offsets into the span), and an
        // edit to the incoming clip changes both of them but not the region before the transition.
        string[] hashes = Hashes(doc);
        hashes[1].Should().NotBe(hashes[2]);
        EditTimeline edited = Doc(VideoTrack("1", from, transition, Clip("2", 90, 150, AssetB, sourceIn: 46)), VideoTrack("2", new TitleClip(Guid.NewGuid(), "t", 75, 150, "late")));
        string[] editedHashes = Hashes(edited);
        editedHashes[0].Should().Be(hashes[0]);
        editedHashes[1].Should().NotBe(hashes[1]);
        editedHashes[2].Should().NotBe(hashes[2]);
    }

    [Fact]
    public void An_audio_transition_crossfades_the_two_clips_and_a_clip_without_the_handle_keeps_a_hard_cut()
    {
        Transition transition = new(Guid.NewGuid(), "x", 60, 90, "fade");
        EditTimeline doc = Doc(AudioTrack("3", Clip("1", 0, 60, AssetA), transition, Clip("2", 90, 150, AssetB, sourceIn: 45)));
        RenderPlan plan = SegmentPlanner.Plan(doc, null);

        plan.SkippedItems.Should().BeEmpty();
        plan.AudioClips.Should().HaveCount(2);
        AudioClipPlan outgoing = plan.AudioClips[0];
        (outgoing.StartFrame, outgoing.EndFrame).Should().Be((0L, 90L));
        outgoing.FadeOut.Should().Be(new AudioFade(60, 90));
        AudioClipPlan incoming = plan.AudioClips[1];
        (incoming.StartFrame, incoming.EndFrame, incoming.SourceInFrames).Should().Be((60L, 150L, 15.0));
        incoming.FadeIn.Should().Be(new AudioFade(60, 90));

        string graph = Graph(AudioMixBuilder.BuildMixArgs(plan, Target(), Assets, "/scratch/mix.wav", 1, doc.Tracks));
        graph.Should().Contain("volume='1*clip((3-t)/1,0,1)':eval=frame,adelay=0|0,apad=whole_dur=5[a0]");
        graph.Should().Contain("volume='1*clip((t-(0))/1,0,1)':eval=frame,adelay=2000|2000,apad=whole_dur=5[a1]");

        // No handle: the incoming clip starts at source 0 and cannot be pulled back → hard cut, reported.
        EditTimeline noHandle = Doc(AudioTrack("3", Clip("1", 0, 60, AssetA), new Transition(Guid.NewGuid(), "x", 60, 90, "fade"), Clip("2", 90, 150, AssetB)));
        RenderPlan hard = SegmentPlanner.Plan(noHandle, null);
        hard.SkippedItems.Should().ContainSingle().Which.Reason.Should().Be("insufficient_handles");
        hard.AudioClips.Select(a => (a.StartFrame, a.EndFrame)).Should().Equal((0L, 60L), (90L, 150L));
    }

    [Fact]
    public void Track_processing_builds_one_bus_per_track_and_a_flat_document_keeps_the_P4_mix_graph()
    {
        Keyframed<double> automation = new(0, [new Keyframe<double>(0, 0, InterpKind.Linear), new Keyframe<double>(30, -12, InterpKind.Linear)]);
        Track music = AudioTrack("3", Clip("1", 0, 60, AssetA)) with { GainDb = automation, Pan = -0.5, Eq = new TrackEq(LowCutHz: 80, Bands: [new EqBand(3000, 2, 1.4)]) };
        Track voice = AudioTrack("4", Clip("2", 0, 60, AssetB));
        EditTimeline doc = Doc(music, voice);
        RenderPlan plan = SegmentPlanner.Plan(doc, null);

        AudioMixBuilder.HasProcessing(music).Should().BeTrue();
        AudioMixBuilder.HasProcessing(voice).Should().BeFalse();
        string graph = Graph(AudioMixBuilder.BuildMixArgs(plan, Target(), Assets, "/scratch/mix.wav", 1, doc.Tracks));
        graph.Should().Contain("[a0]anull,volume='pow(10,(if(lt(t,1),if(lt(t,0),0,(0+(-12)*clip((t-0)/(1-0),0,1))),-12))/20)':eval=frame,pan=stereo|c0=1.306563*c0|c1=0.541196*c1,highpass=f=80,equalizer=f=3000:width_type=q:w=1.4:g=2,apad=whole_dur=2,atrim=duration=2[bus0]");
        graph.Should().Contain("[a1]anull,apad=whole_dur=2,atrim=duration=2[bus1]");
        graph.Should().EndWith("[bus0][bus1]amix=inputs=2:normalize=0:duration=longest,atrim=duration=2,asetpts=PTS-STARTPTS[aout]");

        EditTimeline flat = Doc(AudioTrack("3", Clip("1", 0, 60, AssetA)), AudioTrack("4", Clip("2", 0, 60, AssetB)));
        string flatGraph = Graph(AudioMixBuilder.BuildMixArgs(SegmentPlanner.Plan(flat, null), Target(), Assets, "/scratch/mix.wav", 1, flat.Tracks));
        flatGraph.Should().EndWith("[a0][a1]amix=inputs=2:normalize=0:duration=longest,apad=whole_dur=2,atrim=duration=2,asetpts=PTS-STARTPTS[aout]");
        flatGraph.Should().NotContain("bus");
    }

    [Fact]
    public void A_generator_is_a_filter_source_at_the_canvas_size_and_an_unknown_colour_falls_back_to_black()
    {
        GeneratorClip bars = new(Guid.NewGuid(), "bars", 0, 30, "bars");
        GeneratorClip colour = new(Guid.NewGuid(), "c", 30, 60, "color", new Dictionary<string, object> { ["color"] = "#FF8800" });
        GeneratorClip bad = new(Guid.NewGuid(), "c2", 60, 90, "color", new Dictionary<string, object> { ["color"] = "red;movie=/etc/passwd" });
        EditTimeline doc = Doc(VideoTrack("1", bars, colour, bad));
        RenderPlan plan = SegmentPlanner.Plan(doc, null);
        plan.SkippedItems.Should().BeEmpty();

        string Build(int i) => Graph(RegionFilterGraphBuilder.Build(plan.Regions[i], Target(), Assets, $"/scratch/r{i}.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1).Args);
        Build(0).Should().Contain("smptehdbars=s=320x180:r=30/1:d=1,setsar=1,format=yuv420p[l0]");
        Build(1).Should().Contain("color=c=#FF8800:s=320x180:r=30/1:d=1,setsar=1,format=yuv420p[l0]");
        Build(2).Should().Contain("color=c=#000000:s=320x180:r=30/1:d=1").And.NotContain("passwd");
        RegionFilterGraphBuilder.Build(plan.Regions[0], Target(), Assets, "/scratch/r0.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 1).Args.Should().NotContain("-i", "a generator has no input file");
    }

    [Fact]
    public void A_graph_longer_than_the_inline_ceiling_goes_to_a_filter_complex_script_file()
    {
        // 80 keyframes on three channels: the expressions run to well over the inline ceiling.
        Keyframe<double>[] keys = Enumerable.Range(0, 80).Select(i => new Keyframe<double>(i, 1 + (i % 7) / 10.0, InterpKind.EaseInOutCubic)).ToArray();
        ClipTransform transform = new(PositionX: new Keyframed<double>(0, keys), PositionY: new Keyframed<double>(0, keys), Scale: new Keyframed<double>(0, keys));
        EditTimeline doc = Doc(VideoTrack("1", Depth(Clip("1", 0, 90, AssetA), transform)));
        RenderRegion region = SegmentPlanner.Plan(doc, null).Regions[0];

        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(region, Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf", n => n < 0 ? "/scratch/graph.txt" : $"/scratch/t-{n}.txt", 1);

        argv.GraphFile.Should().NotBeNull();
        argv.GraphFile!.Path.Should().Be("/scratch/graph.txt");
        argv.GraphFile.Contents.Length.Should().BeGreaterThan(RegionFilterGraphBuilder.MaxInlineGraphChars);
        argv.Args.Should().ContainInOrder("-filter_complex_script", "/scratch/graph.txt").And.NotContain("-filter_complex");
    }

    // ── doubles ──────────────────────────────────────────────────────────────

    /// <summary>Succeeds every call, records every argv, and "writes" the output file ffmpeg would have.</summary>
    private sealed class RecordingRunner : IVideoToolRunner
    {
        public List<IReadOnlyList<string>> Invocations { get; } = [];

        private Task<VideoToolResult> Record(IReadOnlyList<string> args)
        {
            Invocations.Add(args.ToList());
            string output = args[^1];
            if (output != "-" && output.Contains(Path.DirectorySeparatorChar))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.WriteAllBytes(output, [0]);
            }
            return Task.FromResult(new VideoToolResult(0, string.Empty, string.Empty, false));
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Record(args);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) => Record(args);
        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Record(args);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) => Record(args);
        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority) => Record(args);
        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) => Record(args);
        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => Record(args);
        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) => Record(args);
    }

    /// <summary>A region cache that holds exactly one recipe and records what it is asked to store.</summary>
    private sealed class HitOnceCache(string hitHash) : IRegionCache
    {
        public List<string> Stored { get; } = [];

        public Task<bool> TryFetchAsync(Guid projectId, string recipeHash, string destinationPath, CancellationToken ct)
        {
            if (recipeHash != hitHash) return Task.FromResult(false);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, [1]);
            return Task.FromResult(true);
        }

        public Task StoreAsync(Guid projectId, string recipeHash, string localPath, CancellationToken ct)
        {
            Stored.Add(recipeHash);
            return Task.CompletedTask;
        }
    }
}
