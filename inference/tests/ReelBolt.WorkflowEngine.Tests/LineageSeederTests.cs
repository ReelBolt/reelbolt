#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-102 unit tests for LineageSeeder: time composition, fallback spans, effect calibration, geometry parity.</summary>
public class LineageSeederTests
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly Guid FileId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid R2 = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid R4 = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Analysis = Guid.Parse("11111111-1111-4111-8111-111111111111");

    private static SegmentationTrackFacts Facts(string id, Guid file, bool trimmed, double coverage = 60) =>
        new(id, new AssetRef(AssetRefKind.ProjectFile, file), trimmed, coverage, null);

    private static TimelineItem Cut(string id, double t0, double t1, double s0, double s1) =>
        new(Id: id, TimelineStartSec: t0, TimelineEndSec: t1, SourceIndex: 0, SourceStartSec: s0, SourceEndSec: s1, Label: id);

    private static TimelineManifest Manifest(int w, int h, TimelineSource source, double seconds, IReadOnlyList<TimelineItem> cuts, params TimelineTrack[] extra) =>
        new(1, Guid.Parse("99999999-9999-4999-8999-999999999999"), Guid.NewGuid(), new TimelineOutputVideo(w, h, 30, seconds, "out.mp4"), [source],
            [new TimelineTrack("v", "video", "Video", 0, cuts), .. extra]);

    private static List<LineageLevel> Chain(IReadOnlyList<TimelineItem> innerCuts, IReadOnlyList<TimelineItem> outerCuts, bool censor, TimelineTrack? outerExtra = null)
    {
        string step2 = ProdRun04ef3b0aConfigs.Step2Compile;
        if (!censor)
        {
            step2 = System.Text.RegularExpressions.Regex.Replace(step2, "\"censorLabels\": \\[[^\\]]*\\]", "\"censorLabels\": []");
        }

        double innerLen = innerCuts.Max(c => c.TimelineEndSec!.Value);
        TimelineManifest m2 = Manifest(1920, 1080, new TimelineSource(0, "src.mp4", 60, 1920, 1080, 30, ProjectFileId: FileId), innerLen, innerCuts);
        TimelineManifest m4 = Manifest(1080, 1920, new TimelineSource(0, "r2.mp4", innerLen, 1920, 1080, 30, StepResultId: R2), innerLen, outerCuts,
            outerExtra is null ? [] : [outerExtra]);
        return
        [
            new LineageLevel(R4, m4, JsonSerializer.Deserialize<VideoCompileStepConfig>(ProdRun04ef3b0aConfigs.Step4Compile, ConfigJson)),
            new LineageLevel(R2, m2, JsonSerializer.Deserialize<VideoCompileStepConfig>(step2, ConfigJson), Analysis, [new LineageSegmentationTrack("g1", 0, "digital speedometer", Facts("g1", FileId, trimmed: false))])
        ];
    }

    private static readonly Guid MusicFileId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    private static SeededTimeline? Seed(List<LineageLevel> levels, Func<string, bool?>? altersPixels = null) =>
        LineageSeeder.TrySeed(levels, Guid.NewGuid(), s => s.ProjectFileId is { } id ? new AssetRef(AssetRefKind.ProjectFile, id) : null, _ => true,
            key => key == "bed.mp3" ? new AssetRef(AssetRefKind.ProjectFile, MusicFileId) : null, altersPixels);

    [Fact]
    public void Cuts_of_both_levels_compose_into_clips_on_the_original_with_offsets()
    {
        // Step 2 keeps source 1-5 s then 20-26 s (10 s program); step 4 keeps the program whole.
        List<LineageLevel> levels = Chain([Cut("a", 0, 4, 1, 5), Cut("b", 4, 10, 20, 26)], [Cut("w", 0, 10, 0, 10)], censor: false);

        EditTimeline doc = Seed(levels)!.Document;

        List<MediaClip> main = doc.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().ToList();
        main.Select(c => (c.RecordIn, c.RecordOut, c.SourceIn)).Should().Equal((0L, 120L, 30L), (120L, 300L, 600L));
        main.Should().OnlyContain(c => c.AssetRef.ProjectFileId == FileId);
        // one fill clip and one region clip per piece, aligned with the main clips
        doc.Tracks.Single(t => t.Name.StartsWith("Canvas fill", StringComparison.Ordinal)).Items.Should().HaveCount(2);
        doc.Tracks.Single(t => t.Name.StartsWith("Blur:", StringComparison.Ordinal)).Items.Should().HaveCount(2);
        doc.Tracks.Single(t => t.Name == "Camera audio").Items.Should().HaveCount(2);
    }

    [Fact]
    public void Only_the_window_under_an_insert_falls_back_and_the_rest_of_the_cut_stays_decomposed()
    {
        TimelineTrack inserts = new("i", "inserts", "Inserts", 1, [new TimelineItem(Id: "ins", TimelineStartSec: 6, TimelineEndSec: 8)]);
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("whole", 0, 10, 0, 10)], censor: false, inserts);

        SeededTimeline seeded = Seed(levels)!;

        List<MediaClip> main = seeded.Document.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().OrderBy(c => c.RecordIn).ToList();
        main.Select(c => (c.RecordIn, c.RecordOut, c.AssetRef.ProjectFileId == FileId)).Should().Equal((0L, 180L, true), (180L, 240L, false), (240L, 300L, true));
        main[1].AssetRef.StepResultId.Should().Be(R4);
        main[1].Provenance!.Rationale.Should().Contain("screen_insert");
        seeded.Document.Markers.Should().ContainSingle().Which.Frame.Should().Be(180);
        seeded.Unplaced.Should().ContainSingle().Which.Reason.Should().Contain("screen_insert");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_segmentation_track_that_does_not_bind_to_the_clip_sends_the_span_to_the_compile_output(bool trimmed, bool otherSource)
    {
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        LineageLevel inner = levels[1];
        levels[1] = inner with { AnalysisTracks = [new LineageSegmentationTrack("g1", 0, "digital speedometer", Facts("g1", otherSource ? Guid.NewGuid() : FileId, trimmed))] };

        SeededTimeline seeded = Seed(levels)!;

        seeded.Document.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().Single().AssetRef.StepResultId.Should().Be(R4);
        seeded.Document.Markers.Should().ContainSingle();
        seeded.Unplaced.Single().Reason.Should().Contain("mask_not_bound");
    }

    [Fact]
    public void A_track_cut_from_an_analysis_before_upright_tracking_of_a_source_that_alters_pixels_falls_back_with_a_marker()
    {
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);

        SeededTimeline unknown = Seed(levels, _ => null)!;
        unknown.Document.Tracks.Any(t => t.Name.StartsWith("Blur:", StringComparison.Ordinal)).Should().BeTrue("an unknown rotation is not a refusal");

        SeededTimeline rotated = Seed(levels, _ => true)!;
        rotated.Document.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().Single().AssetRef.StepResultId.Should().Be(R4);
        rotated.Document.Markers.Should().ContainSingle();
        rotated.Unplaced.Single().Reason.Should().Contain("mask_not_bound");

        LineageLevel inner = levels[1];
        levels[1] = inner with { AnalysisTracks = [new LineageSegmentationTrack("g1", 0, "digital speedometer", new SegmentationTrackFacts("g1", new AssetRef(AssetRefKind.ProjectFile, FileId), false, 60, VideoAnalysisSourceInfo.UprightGeometry))] };
        Seed(levels, _ => true)!.Document.Tracks.Should().Contain(t => t.Name.StartsWith("Blur:", StringComparison.Ordinal), "an upright-1 analysis binds to a rotated source");
    }

    [Fact]
    public void A_music_bed_and_captions_on_the_final_compile_get_their_own_tracks_and_force_no_fallback()
    {
        TimelineTrack music = new("m", "music", "Music", 2, [new TimelineItem(Id: "bed", TimelineStartSec: 0, TimelineEndSec: 10, TrackName: "bed.mp3", TrackId: "t1", BedDbfs: -20)]);
        TimelineTrack captions = new("d", "dialogue", "Captions", 3, [new TimelineItem(Id: "cap", TimelineStartSec: 2, TimelineEndSec: 4, Text: "Hello")]);
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        TimelineManifest m4 = levels[0].Manifest;
        levels[0] = levels[0] with { Manifest = m4 with { Tracks = [.. m4.Tracks, music, captions] } };

        SeededTimeline seeded = Seed(levels)!;

        seeded.Document.Markers.Should().BeEmpty("nothing fell back");
        seeded.Document.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().Single().AssetRef.ProjectFileId.Should().Be(FileId);
        seeded.Document.Tracks.Should().Contain(t => t.Name == "Music").Which.Items.OfType<MediaClip>().Single().AssetRef.ProjectFileId.Should().Be(MusicFileId);
        seeded.Document.Tracks.Should().Contain(t => t.Name == "Captions").Which.Items.Should().ContainSingle().Which.Should().BeOfType<Caption>();
        seeded.Document.Tracks.Should().Contain(t => t.Name.StartsWith("Blur:", StringComparison.Ordinal), "the picture is still decomposed");

        // The picture tracks are byte-identical to the same chain without the music bed and captions, so the SSIM of the
        // render test (picture only) is unchanged by them.
        EditTimeline without = Seed(Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false))!.Document;
        string Picture(EditTimeline d) => JsonSerializer.Serialize(
            d.Tracks.Where(t => t.Name == "Video" || t.Name.StartsWith("Canvas fill", StringComparison.Ordinal) || t.Name.StartsWith("Blur:", StringComparison.Ordinal)).ToList(), EditTimelineJson.Options);
        Picture(seeded.Document).Should().Be(Picture(without));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_final_transcript_is_a_visible_caption_track_only_when_the_compile_burned_captions_in(bool burned)
    {
        TimelineTrack captions = new("d", "dialogue", "Captions", 3, [new TimelineItem(Id: "cap", TimelineStartSec: 2, TimelineEndSec: 4, Text: "Hello")]);
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        TimelineManifest m4 = levels[0].Manifest;
        levels[0] = levels[0] with { Manifest = m4 with { Tracks = [.. m4.Tracks, captions] }, Config = levels[0].Config! with { EnableCaptions = burned } };

        Track track = Seed(levels)!.Document.Tracks.Single(t => t.Name == "Captions");

        track.Enabled.Should().Be(burned, "a disabled track is neither rendered nor previewed, so unburned subtitles never appear");
        track.Items.Should().ContainSingle().Which.Should().BeOfType<Caption>();
    }

    [Fact]
    public void A_music_bed_on_an_inner_compile_is_baked_into_the_intermediate_so_that_span_falls_back()
    {
        TimelineTrack music = new("m", "music", "Music", 2, [new TimelineItem(Id: "bed", TimelineStartSec: 0, TimelineEndSec: 10, TrackName: "bed.mp3")]);
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        TimelineManifest m2 = levels[1].Manifest;
        levels[1] = levels[1] with { Manifest = m2 with { Tracks = [.. m2.Tracks, music] } };

        Seed(levels)!.Document.Markers.Should().ContainSingle();
    }

    [Fact]
    public void A_music_bed_on_a_fallback_span_is_flagged_as_doubled_in_the_marker()
    {
        TimelineTrack music = new("m", "music", "Music", 2, [new TimelineItem(Id: "bed", TimelineStartSec: 0, TimelineEndSec: 10, TrackName: "bed.mp3")]);
        TimelineTrack inserts = new("i", "inserts", "Inserts", 1, [new TimelineItem(Id: "ins", TimelineStartSec: 6, TimelineEndSec: 8)]);
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("first", 0, 5, 0, 5), Cut("second", 5, 10, 5, 10)], censor: false, inserts);
        TimelineManifest m4 = levels[0].Manifest;
        levels[0] = levels[0] with { Manifest = m4 with { Tracks = [.. m4.Tracks, music] } };

        Seed(levels)!.Document.Markers.Single().Note.Should().Contain("play twice");
    }

    [Fact]
    public void Blur_golden_argv_one_effect_reaches_sigma_40_and_existing_amounts_keep_their_meaning()
    {
        ReelBolt.WorkflowEngine.Services.Video.TimelineRender.GradeFilterBuilder.Effects([new ClipEffect("blur", 2.0)]).Should().Be("gblur=sigma=40");
        ReelBolt.WorkflowEngine.Services.Video.TimelineRender.GradeFilterBuilder.Effects([new ClipEffect("blur", 1.0)]).Should().Be("gblur=sigma=20");
        ReelBolt.WorkflowEngine.Services.Video.TimelineRender.GradeFilterBuilder.Effects([new ClipEffect("blur", 0.5)]).Should().Be("gblur=sigma=10");
        ReelBolt.WorkflowEngine.Services.Video.TimelineRender.GradeFilterBuilder.Effects([new ClipEffect("blur", 3.0)]).Should().Be("gblur=sigma=40", "clamped to the new max");
        ReelBolt.WorkflowEngine.Services.Video.TimelineRender.GradeFilterBuilder.Effects([new ClipEffect("sharpen", 3.0)]).Should().Be("unsharp=la=3", "other effects still clamp at 1");
        LineageSeeder.BlurEffects(40).Should().ContainSingle().Which.Amount.Should().Be(2.0);
        LineageSeeder.BlurEffects(120).Should().BeNull("(120/40)^2 = 9 effects is past the 8-effect cap: a weaker layer must never be seeded");
        LineageSeeder.BlurEffects(114).Should().BeNull("(114/40)^2 = 8.12 rounds up to 9");
        LineageSeeder.BlurEffects(113).Should().HaveCount(8);
    }

    [Fact]
    public void A_gap_in_the_inner_cuts_falls_back_rather_than_inventing_footage()
    {
        List<LineageLevel> levels = Chain([Cut("a", 0, 4, 0, 4)], [Cut("w", 0, 10, 0, 10)], censor: false);

        SeededTimeline seeded = Seed(levels)!;

        List<MediaClip> main = seeded.Document.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().OrderBy(c => c.RecordIn).ToList();
        main.Select(c => (c.RecordIn, c.RecordOut, c.AssetRef.StepResultId == R4)).Should().Equal((0L, 120L, false), (120L, 300L, true));
        seeded.Document.Markers.Should().ContainSingle().Which.Frame.Should().Be(120);
    }

    [Fact]
    public void A_single_identity_compile_adds_nothing_so_the_plain_seeding_keeps_its_behaviour()
    {
        TimelineManifest m = Manifest(1920, 1080, new TimelineSource(0, "src.mp4", 10, 1920, 1080, 30, ProjectFileId: FileId), 10, [Cut("a", 0, 10, 0, 10)]);
        VideoCompileStepConfig config = JsonSerializer.Deserialize<VideoCompileStepConfig>("""{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"keepWholeSources":true}""", ConfigJson)!;

        Seed([new LineageLevel(R2, m, config)]).Should().BeNull();
    }

    [Fact]
    public void Region_blur_is_the_engines_sigma_scaled_by_the_later_fit_and_a_censor_label_alone_adds_nothing()
    {
        MediaClip Region(bool censor) => Seed(Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor))!.Document.Tracks
            .Single(t => t.Name.StartsWith("Blur:", StringComparison.Ordinal)).Items.OfType<MediaClip>().Single();

        // 1.5% of the 1080 short side = 16.2 px at the 1920x1080 step-2 canvas; step 4 shrinks by 0.5625 -> 9.1 px = amount 0.456.
        MediaClip alone = Region(censor: false);
        alone.Effects.Should().ContainSingle().Which.Amount.Should().BeApproximately(0.4556, 0.001);
        alone.Mask!.Kind.Should().Be("segmentation");
        alone.Mask.AnalysisStepResultId.Should().Be(Analysis);
        alone.Mask.TrackIds.Should().Equal("g1");
        alone.Mask.FeatherPx.Should().Be(8);

        // censorLabels alone draw nothing here (no feature track, so no censor item in the manifest): the layer is unchanged.
        Region(censor: true).Effects.Should().ContainSingle().Which.Amount.Should().BeApproximately(0.4556, 0.001);
    }

    [Fact]
    public void The_canvas_fill_is_one_blur_of_amount_2_which_is_the_engines_sigma_40()
    {
        EditTimeline doc = Seed(Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false))!.Document;

        MediaClip fill = doc.Tracks.Single(t => t.Name.StartsWith("Canvas fill", StringComparison.Ordinal)).Items.OfType<MediaClip>().Single();

        fill.Effects!.Select(e => (e.Type, e.Amount)).Should().Equal(("blur", 2.0));
        (fill.Effects![0].Amount * LineageCalibration.EditorBlurSigmaPerAmount).Should().BeApproximately(40, 1e-9);
        fill.Grade!.Nodes!.Single().Primaries!.Offset!.Master.Should().Be(-0.08);
    }

    [Fact]
    public void Twelve_tracks_become_two_masked_layers_and_none_is_left_unblurred()
    {
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        levels[1] = levels[1] with { AnalysisTracks = Enumerable.Range(1, 12).Select(n => new LineageSegmentationTrack($"g{n}", 0, "digital speedometer", Facts($"g{n}", FileId, trimmed: false))).ToList() };

        EditTimeline doc = Seed(levels)!.Document;

        List<MediaClip> layers = doc.Tracks.Where(t => t.Name.StartsWith("Blur:", StringComparison.Ordinal)).SelectMany(t => t.Items).OfType<MediaClip>().ToList();
        layers.Should().HaveCount(2);
        layers.SelectMany(l => l.Mask!.TrackIds!).Should().BeEquivalentTo(Enumerable.Range(1, 12).Select(n => $"g{n}"));
        layers.Should().OnlyContain(l => l.Mask!.TrackIds!.Count <= 8);
        doc.Tracks.Where(t => t.Name.StartsWith("Blur:", StringComparison.Ordinal)).Should().HaveCount(2, "two simultaneous masked clips cannot share a track");
    }

    [Fact]
    public void An_inverted_mask_over_twelve_tracks_falls_back_because_split_inverted_layers_would_blur_each_others_objects()
    {
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        string inverted = System.Text.RegularExpressions.Regex.Replace(ProdRun04ef3b0aConfigs.Step2Compile, "\"censorLabels\": \\[[^\\]]*\\]", "\"censorLabels\": []").Replace("\"effect\": \"Blur\"", "\"invert\": true, \"effect\": \"Blur\"");
        levels[1] = levels[1] with
        {
            Config = JsonSerializer.Deserialize<VideoCompileStepConfig>(inverted, ConfigJson),
            AnalysisTracks = Enumerable.Range(1, 12).Select(n => new LineageSegmentationTrack($"g{n}", 0, "digital speedometer", Facts($"g{n}", FileId, false))).ToList()
        };

        SeededTimeline seeded = Seed(levels)!;

        seeded.Document.Markers.Should().ContainSingle();
        seeded.Unplaced.Single().Reason.Should().Contain("inverted_mask_over_8_tracks");
        seeded.Document.Tracks.Should().NotContain(t => t.Name.StartsWith("Blur:", StringComparison.Ordinal));

        // up to 8 inverted tracks stay ONE layer
        levels[1] = levels[1] with { AnalysisTracks = levels[1].AnalysisTracks!.Take(8).ToList() };
        Seed(levels)!.Document.Tracks.Count(t => t.Name.StartsWith("Blur:", StringComparison.Ordinal)).Should().Be(1);
    }

    [Fact]
    public void More_tracks_than_the_engine_blurs_per_effect_fall_back_with_a_marker()
    {
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        levels[1] = levels[1] with { AnalysisTracks = Enumerable.Range(1, 17).Select(n => new LineageSegmentationTrack($"g{n}", 0, "digital speedometer", Facts($"g{n}", FileId, false))).ToList() };

        SeededTimeline seeded = Seed(levels)!;

        seeded.Document.Markers.Should().ContainSingle();
        seeded.Unplaced.Single().Reason.Should().Contain("too_many_region_tracks");
    }

    [Fact]
    public void A_censor_item_sends_only_its_window_to_the_compile_output_and_a_transcript_is_metadata()
    {
        TimelineTrack censor = new("c", "censor", "Censored", 1, [new TimelineItem(Id: "cz", TimelineStartSec: 2, TimelineEndSec: 3, Label: "face")]);
        TimelineTrack transcript = new("d", "dialogue", "Dialogue", 2, [new TimelineItem(Id: "w1", TimelineStartSec: 0, TimelineEndSec: 10, Text: "spoken words")]);
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        TimelineManifest m2 = levels[1].Manifest;
        levels[1] = levels[1] with { Manifest = m2 with { Tracks = [.. m2.Tracks, censor, transcript] } };

        SeededTimeline seeded = Seed(levels)!;

        List<MediaClip> main = seeded.Document.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().OrderBy(c => c.RecordIn).ToList();
        main.Select(c => (c.RecordIn, c.RecordOut, c.AssetRef.ProjectFileId == FileId)).Should().Equal((0L, 60L, true), (60L, 91L, false), (91L, 300L, true));
        seeded.Unplaced.Single().Reason.Should().Contain("censor_boxes");
        seeded.Document.Tracks.Should().Contain(t => t.Name.StartsWith("Blur:", StringComparison.Ordinal), "the inner transcript did not block the masked layer");
    }

    [Fact]
    public void Blurs_stack_in_variance_so_sigma_64_8_seeds_at_an_effective_64_8()
    {
        IReadOnlyList<ClipEffect> effects = LineageSeeder.BlurEffects(64.8)!;

        effects.Should().HaveCount(3, "ceil((64.8 / 40)^2) = ceil(2.62)");
        Math.Sqrt(effects.Sum(e => Math.Pow(e.Amount * LineageCalibration.EditorBlurSigmaPerAmount, 2))).Should().BeApproximately(64.8, 0.01);
        effects.Should().OnlyContain(e => e.Amount <= EditTimelineValidator.MaxBlurAmount);
    }

    [Theory]
    [InlineData(2.0, 3.0, 60L, 91L)]         // start exactly on a frame is unchanged (60); end on a frame boundary: the inclusive between(t,s,e) still touches frame 90
    [InlineData(2.0, 3.01, 60L, 91L)]        // end with a fractional part below .5 (90.3): frame 90 is touched
    [InlineData(2.0166667, 3.0, 60L, 91L)]   // start mid-frame (60.5): round DOWN
    [InlineData(1.99, 2.0, 59L, 61L)]
    [InlineData(2.0, 2.0, 60L, 61L)]          // a zero-length censor at a frame time still yields its one frame (inclusive between())
    [InlineData(2.0, 2.033, 60L, 62L)]        // written 2.033 for a true 2.0334 (61.0 frames): frame 61 is touched, so the window ends at 62        // 30 fps, e = 2.000: frame 60 must be inside the window
    public void A_censored_window_is_rounded_outward_to_the_frames_the_compile_touches(double start, double end, long recIn, long recOut)
    {
        TimelineTrack censor = new("c", "censor", "Censored", 1, [new TimelineItem(Id: "cz", TimelineStartSec: start, TimelineEndSec: end, Label: "face")]);
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        TimelineManifest m2 = levels[1].Manifest;
        levels[1] = levels[1] with { Manifest = m2 with { Tracks = [.. m2.Tracks, censor] } };

        List<MediaClip> main = Seed(levels)!.Document.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().OrderBy(c => c.RecordIn).ToList();

        MediaClip window = main.Single(c => c.AssetRef.StepResultId == R4);
        (window.RecordIn, window.RecordOut).Should().Be((recIn, recOut));
        main.Where(c => c != window).Should().OnlyContain(c => c.AssetRef.ProjectFileId == FileId);
        for (int i = 1; i < main.Count; i++)
        {
            main[i].RecordIn.Should().Be(main[i - 1].RecordOut, "pieces stay contiguous and never overlap");
        }

        main[0].RecordIn.Should().Be(0);
        main[^1].RecordOut.Should().Be(300);
        // the original source stays aligned with the program after the window
        MediaClip after = main[^1];
        after.SourceIn.Should().Be(after.RecordIn);
    }

    [Fact]
    public void A_retimed_inner_cut_falls_back_and_is_never_seeded_at_one_times()
    {
        // The inner cut shows 5 s of source in 2.5 s of program (2x): the old check compared two derived windows and never failed.
        List<LineageLevel> levels = Chain([Cut("a", 0, 2.5, 0, 5), Cut("b", 2.5, 10, 5, 12.5)], [Cut("w", 0, 10, 0, 10)], censor: false);

        SeededTimeline seeded = Seed(levels)!;

        List<MediaClip> main = seeded.Document.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().OrderBy(c => c.RecordIn).ToList();
        main[0].AssetRef.StepResultId.Should().Be(R4);
        (main[0].RecordIn, main[0].RecordOut).Should().Be((0L, 75L));
        main[1].AssetRef.ProjectFileId.Should().Be(FileId);
        seeded.Unplaced.Single().Reason.Should().Contain("retimed_or_speed_changed");
    }

    [Fact]
    public void A_mute_on_the_final_compile_removes_the_camera_audio_and_a_cut_grade_or_missing_source_falls_back()
    {
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        levels[0] = levels[0] with { Config = levels[0].Config! with { MuteSourceAudio = true } };
        Seed(levels)!.Document.Tracks.Should().NotContain(t => t.Name == "Camera audio");

        List<LineageLevel> graded = Chain([new TimelineItem(Id: "a", TimelineStartSec: 0, TimelineEndSec: 10, SourceIndex: 0, SourceStartSec: 0, SourceEndSec: 10, Label: "a", ColorGrade: "warm")], [Cut("w", 0, 10, 0, 10)], censor: false);
        Seed(graded)!.Unplaced.Single().Reason.Should().Contain("color_grade");

        List<LineageLevel> sourceless = Chain([Cut("a", 0, 10, 0, 10)], [new TimelineItem(Id: "w", TimelineStartSec: 0, TimelineEndSec: 10, SourceStartSec: 0, SourceEndSec: 10, Label: "w")], censor: false);
        Seed(sourceless)!.Unplaced.Single().Reason.Should().Contain("cut_without_source");
    }

    [Fact]
    public void An_effect_the_editor_cannot_express_at_the_compiles_strength_falls_back_instead_of_a_weaker_layer()
    {
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        string step2 = System.Text.RegularExpressions.Regex.Replace(ProdRun04ef3b0aConfigs.Step2Compile, "\"censorLabels\": \\[[^\\]]*\\]", "\"censorLabels\": []")
            .Replace("\"effect\": \"Blur\"", "\"effect\": \"Pixelate\"").Replace("\"strength\": 1", "\"strength\": 4");
        levels[1] = levels[1] with { Config = JsonSerializer.Deserialize<VideoCompileStepConfig>(step2, ConfigJson) };

        SeededTimeline seeded = Seed(levels)!;

        // 1080 short side x 2% x 4 = 86 px at step 2, x 0.5625 = 48.6 px: inside 64, so this still seeds; strength 8 does not.
        seeded.Document.Tracks.Should().Contain(t => t.Name.StartsWith("Pixelate:", StringComparison.Ordinal));
        levels[1] = levels[1] with { Config = JsonSerializer.Deserialize<VideoCompileStepConfig>(step2.Replace("\"strength\": 4", "\"strength\": 8"), ConfigJson) };
        Seed(levels)!.Unplaced.Single().Reason.Should().Contain("effect_beyond_editor_range");
    }

    [Fact]
    public void Seeded_layers_and_camera_audio_share_one_link_group_per_piece()
    {
        EditTimeline doc = Seed(Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false))!.Document;

        List<MediaClip> clips = doc.Tracks.SelectMany(t => t.Items).OfType<MediaClip>().ToList();
        clips.Select(c => c.LinkId).Distinct().Should().ContainSingle().Which.Should().MatchRegex("^[A-Za-z0-9_-]{1,64}$");
        clips.Should().HaveCount(4, "fill, main, masked layer and camera audio");
    }

    [Fact]
    public void Every_compile_config_member_is_classified_so_none_is_dropped_silently()
    {
        string[] members = typeof(VideoCompileStepConfig).GetConstructors().Single().GetParameters().Select(p => p.Name!).ToArray();
        string[] keys = LineageConfigCoverage.Table.Keys.ToArray();
        members.Select(m => char.ToUpperInvariant(m[0]) + m[1..]).Except(keys).Should().BeEmpty("a new compile option must be classified in LineageConfigCoverage");
        keys.Except(members.Select(m => char.ToUpperInvariant(m[0]) + m[1..])).Should().BeEmpty("stale entries");
        LineageConfigCoverage.Table.Values.Distinct().Should().BeSubsetOf(["mapped", "fallback", "marked", "irrelevant"]);
    }

    [Fact]
    public void Calibration_constants_match_the_engines()
    {
        LineageCalibration.RegionBlurSigmaFraction.Should().Be(RegionEffectFilterBuilder.BlurSigmaFraction);
        LineageCalibration.RegionPixelateBlockFraction.Should().Be(RegionEffectFilterBuilder.PixelateBlockFraction);
        CensorFilterBuilder.EffectFilter(CensorStyle.Blur, 1920, 1080).Should().Contain("sigma=28.8");
        (1920 * LineageCalibration.CensorBlurSigmaFraction).Should().BeApproximately(28.8, 1e-9);
        // the compile's canvas fill, both in the segmented path and the reframe path
        System.IO.File.ReadAllText(SourcePath("Execution/StepExecutors/VideoCompileStepExecutor.cs")).Should().Contain("gblur=sigma=40,eq=brightness=-0.08");
        System.IO.File.ReadAllText(SourcePath("Services/Video/ReframeFilterBuilder.cs")).Should().Contain("gblur=sigma=40,eq=brightness=-0.08");
    }

    [Theory]
    [InlineData(1920, 1080, 1080, 1920, false)]
    [InlineData(1920, 1080, 1080, 1920, true)]
    [InlineData(1080, 1920, 1920, 1080, true)]
    [InlineData(1920, 1080, 1920, 1080, false)]
    public void The_shared_fit_is_the_executors_fit(int sw, int sh, int cw, int ch, bool cover)
    {
        VideoCompileStepExecutor.SourceCanvasFit engine = VideoCompileStepExecutor.SourceCanvasFit.For(
            new VideoAnalysisMedia(1, 30, 1, sw, sh), new VideoAnalysisMedia(1, 30, 1, cw, ch), cover);
        CanvasFitRect shared = CanvasFitGeometry.For(sw, sh, cw, ch, cover);

        (shared.OffsetX, shared.OffsetY, shared.Width, shared.Height).Should().Be((engine.OffsetX, engine.OffsetY, engine.Width, engine.Height));
    }

    [Fact]
    public void Seed_failure_body_wording_is_plain_and_carries_no_internals()
    {
        ReelBolt.Inference.Api.Controllers.EditTimelinesController.SeedFailedMessage.Should().Be("This edit could not be opened in the editor.");
        ReelBolt.Inference.Api.Controllers.EditTimelinesController.SeedFailedMessage.Should().NotContainAny("EditTimelineSeeder", "step result", "validation");
    }

    [Theory]
    // Plain caption, a caption with an embedded quoted phrase, and one that starts and ends with a
    // quote: the manifest label is not sanitised upstream, so all three can arrive in the message.
    [InlineData("A sunset over the harbor with warm light")]
    [InlineData("A man says \"hello\" to the camera")]
    [InlineData("\"Sunny\" day at the beach")]
    public void Seed_rejection_log_drops_a_caption_like_clip_label_but_keeps_the_rule_and_numbers(string caption)
    {
        // The manifest sets clip.Name to the shot caption summary, so the overrun message can carry
        // customer-derived text; the log form must keep the path, the rule and the numbers only.
        string message = $"The clip \"{caption}\" runs past the end of its source: it needs 10.5 s of the source but the source is only 9 s long. Shorten the clip or move its start earlier.";

        string logged = ReelBolt.Inference.Api.Controllers.EditTimelinesController.RedactQuotedLabelsForLog(message);

        logged.Should().NotContain(caption);
        logged.Should().Be("The clip \"<clip>\" runs past the end of its source: it needs 10.5 s of the source but the source is only 9 s long. Shorten the clip or move its start earlier.");
    }

    [Fact]
    public void Seed_rejection_log_leaves_an_unquoted_message_unchanged()
    {
        ReelBolt.Inference.Api.Controllers.EditTimelinesController.RedactQuotedLabelsForLog("The timeline has no items.")
            .Should().Be("The timeline has no items.");
    }

    private const string DerushEditCompile = """{"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"transitionPolicy":"Auto","programFadeInMs":500,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800}""";

    private static (List<LineageLevel> Levels, double Seconds) DerushEditShape(bool overlappingCue)
    {
        const double seconds = 90.05;
        TimelineSource source = new(0, "src.mp4", 600, 1920, 1080, 60, ProjectFileId: FileId);
        TimelineItem[] cuts = [Cut("a", 0, 30, 10, 40), Cut("b", 30, 60, 100, 130), Cut("c", 60, seconds, 200, 230.05)];
        double[] starts = [23.613, 31.0, 38.2, 45.0, 52.4, 58.0, 64.1, 70.0, 76.3, 83.0, 88.0];
        TimelineItem[] cues = starts.Select((st, k) => new TimelineItem(
            Id: "w" + k, TimelineStartSec: st, TimelineEndSec: k == 10 ? seconds : starts[k + 1] - (k == 4 && overlappingCue ? -0.7 : 0.1), Text: "words " + k)).ToArray();
        TimelineManifest m = new(1, Guid.Parse("99999999-9999-4999-8999-999999999999"), Guid.NewGuid(), new TimelineOutputVideo(1920, 1080, 60, seconds, "out.mp4"), [source],
            [new TimelineTrack("v", "video", "Video", 0, cuts), new TimelineTrack("d", "dialogue", "Dialogue", 2, cues)]);
        return ([new LineageLevel(R4, m, JsonSerializer.Deserialize<VideoCompileStepConfig>(DerushEditCompile, ConfigJson))], seconds);
    }

    private static ValidationResult ValidateDoc(EditTimeline doc, Guid project, double seconds) =>
        EditTimelineValidator.Validate(doc, new EditTimelineValidationContext(
            project,
            new Dictionary<AssetRef, long>
            {
                [new AssetRef(AssetRefKind.ProjectFile, FileId)] = 60 * 600,
                [new AssetRef(AssetRefKind.StepResult, StepResultId: R4)] = (long)Math.Ceiling(seconds * 60),
            },
            new Dictionary<Guid, Guid> { [FileId] = project },
            new Dictionary<Guid, Guid> { [R4] = project },
            60));

    [Fact]
    public void Real_derush_edit_shape_with_an_overlapping_cue_seeds_a_valid_document()
    {
        (List<LineageLevel> levels, double seconds) = DerushEditShape(overlappingCue: true);

        SeededTimeline seeded = Seed(levels)!;
        ValidationResult result = ValidateDoc(seeded.Document, levels[0].Manifest.ProjectId, seconds);

        result.Violations.Should().BeEmpty(string.Join("; ", result.Violations.Select(v => v.Path + ": " + v.Message)));
    }

    private static readonly Guid FileB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid FileC = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid RealProject = Guid.Parse("99999999-9999-4999-8999-999999999999");

    /// <summary>
    /// The production "Highlight edit from footage" shape: one compile, 4K60 HLG footage across three
    /// sources, ten cuts, Auto seam treatments (three blended seams and an audio-only one), program fades
    /// black 500/800 and audio fades 300/900, plus a transcript with one overlapping cue.
    /// </summary>
    private static (List<LineageLevel> Levels, double Seconds, int CutCount) RealDerushShape(string fadeColor = "black")
    {
        const int fps = 60;
        double[] durations = [9.0, 8.5, 9.2, 8.8, 9.5, 8.7, 9.1, 8.9, 9.35, 9.0];
        // A seam's overlap only pulls the following span's start back; the span keeps its own length.
        double[] overlaps = [0, 0, 0.5, 0, 0, 0, 0.5, 0, 0.2, 0];
        string?[] transitions = [null, null, "Dissolve", "AudioOnly", null, null, "Dissolve", null, "SoftCut", null];
        TimelineSource[] sources =
        [
            new(0, "src-a.mp4", 600, 3840, 2160, fps, ProjectFileId: FileId),
            new(1, "src-b.mp4", 600, 3840, 2160, fps, ProjectFileId: FileB),
            new(2, "src-c.mp4", 600, 3840, 2160, fps, ProjectFileId: FileC),
        ];
        List<TimelineItem> cuts = [];
        double start = 0;
        for (int k = 0; k < durations.Length; k++)
        {
            if (k > 0)
            {
                start += durations[k - 1] - overlaps[k - 1];
            }

            cuts.Add(new TimelineItem(
                Id: $"cut-{k}",
                TimelineStartSec: Math.Round(start, 3),
                TimelineEndSec: Math.Round(start + durations[k], 3),
                SourceIndex: k % 3,
                SourceStartSec: 20 + (k * 11),
                SourceEndSec: 20 + (k * 11) + durations[k],
                Label: $"cut {k + 1}",
                TransitionOut: transitions[k]));
        }

        double seconds = cuts[^1].TimelineEndSec!.Value;
        List<TimelineItem> cues = [];
        for (int k = 0; k < 12; k++)
        {
            double cueStart = Math.Round((k * seconds / 12) + 0.2, 3);
            // Cue 4 runs 0.7 s into its successor: ASR timing, which the seeder must trim, not emit overlapping.
            double cueEnd = Math.Round(((k + 1) * seconds / 12) - 0.1 + (k == 4 ? 0.7 : 0), 3);
            cues.Add(new TimelineItem(Id: $"w{k}", TimelineStartSec: cueStart, TimelineEndSec: Math.Min(cueEnd, seconds), Text: "words " + k));
        }

        TimelineManifest manifest = new(1, RealProject, Guid.NewGuid(),
            new TimelineOutputVideo(3840, 2160, fps, seconds, "out.mp4"), sources,
            [new TimelineTrack("v", "video", "Video", 0, cuts), new TimelineTrack("d", "dialogue", "Dialogue", 2, cues)]);
        string config = """
            {"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"transitionPolicy":"Auto","programFadeInMs":500,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"programFadeColor":"COLOR","minSegmentMs":800}
            """.Replace("COLOR", fadeColor);
        return ([new LineageLevel(R4, manifest, JsonSerializer.Deserialize<VideoCompileStepConfig>(config, ConfigJson))], seconds, cuts.Count);
    }

    private static EditTimelineValidationContext RealShapeContext(double seconds) =>
        new(
            RealProject,
            new Dictionary<AssetRef, long>
            {
                [new AssetRef(AssetRefKind.ProjectFile, FileId)] = 60 * 600,
                [new AssetRef(AssetRefKind.ProjectFile, FileB)] = 60 * 600,
                [new AssetRef(AssetRefKind.ProjectFile, FileC)] = 60 * 600,
                // SourceDurationResolver rounds the render UP, and the manifest's duration is rounded to the ms.
                [new AssetRef(AssetRefKind.StepResult, StepResultId: R4)] = (long)Math.Ceiling(seconds * 60),
            },
            new Dictionary<Guid, Guid> { [FileId] = RealProject, [FileB] = RealProject, [FileC] = RealProject },
            new Dictionary<Guid, Guid> { [R4] = RealProject },
            60);

    [Fact]
    public void A_real_derush_edit_opens_as_one_clip_per_cut_with_the_program_fades_preserved()
    {
        (List<LineageLevel> levels, double seconds, int cutCount) = RealDerushShape();

        SeededTimeline seeded = Seed(levels)!;
        ValidationResult result = EditTimelineValidator.Validate(seeded.Document, RealShapeContext(seconds));
        result.Violations.Should().BeEmpty(string.Join("; ", result.Violations.Select(v => v.Path + ": " + v.Message)));

        List<MediaClip> main = seeded.Document.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>().ToList();
        main.Should().HaveCount(cutCount, "every cut opens as its own clip on the original footage");
        main.Should().OnlyContain(c => c.AssetRef.Kind == AssetRefKind.ProjectFile, "no span plays the compile output");
        for (int i = 1; i < main.Count; i++)
        {
            main[i].RecordIn.Should().Be(main[i - 1].RecordOut, "a treated seam is a hard cut, so clips must not overlap");
        }

        main[0].RecordIn.Should().Be(0);
        main[0].FadeInFrames.Should().Be(30, "500 ms at 60 fps");
        main[^1].FadeOutFrames.Should().Be(48, "800 ms at 60 fps");

        List<MediaClip> audio = seeded.Document.Tracks.Single(t => t.Name == EditTimelineSeeder.CameraAudioName).Items.OfType<MediaClip>().ToList();
        audio.Should().HaveCount(cutCount);
        audio[0].FadeInFrames.Should().Be(18, "300 ms at 60 fps");
        audio[^1].FadeOutFrames.Should().Be(54, "900 ms at 60 fps");

        seeded.Document.Markers.Should().Contain(m => m.Name == "Softened seam");
        seeded.Unplaced.Should().BeEmpty("a mappable program fade must not send any span to the compile output");
    }

    [Fact]
    public void A_white_program_fade_still_falls_back_to_the_compile_output()
    {
        (List<LineageLevel> levels, _, _) = RealDerushShape(fadeColor: "white");

        SeededTimeline seeded = Seed(levels)!;

        seeded.Unplaced.Select(u => u.Reason).Should().Contain(r => r.Contains("program_fade"));
        seeded.Document.Tracks.Single(t => t.Name == "Video").Items.OfType<MediaClip>()
            .Should().OnlyContain(c => c.AssetRef.Kind == AssetRefKind.StepResult, "a white fade is not representable and stays a fallback");
    }

    [Fact]
    public void A_program_fade_on_an_inner_compile_still_falls_back()
    {
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        levels[1] = levels[1] with { Config = levels[1].Config! with { ProgramFadeInMs = 500 } };

        Seed(levels)!.Unplaced.Single().Reason.Should().Contain("program_fade");
    }

    private static string SourcePath(string relative)
    {
        string dir = AppContext.BaseDirectory;
        while (!System.IO.Directory.Exists(System.IO.Path.Combine(dir, "src")) && System.IO.Directory.GetParent(dir) is { } p) { dir = p.FullName; }
        return System.IO.Path.Combine(dir, "src", "ReelBolt.WorkflowEngine", relative);
    }

    [Fact]
    public void A_level_with_generated_clips_and_censoring_never_replays_the_original_so_the_cut_plays_the_censored_render()
    {
        List<LineageLevel> levels = Chain([Cut("a", 0, 10, 0, 10)], [Cut("w", 0, 10, 0, 10)], censor: false);
        // The final level cuts to generated clips AND censors something; the original under a cutaway would show the censored stretch.
        levels[0] = levels[0] with { Config = levels[0].Config! with { EnableGeneratedClips = true, CensorLabels = ["face"] } };

        SeededTimeline? seeded = Seed(levels);

        seeded.Should().NotBeNull("the censored render is still seeded, as a whole-cut fallback");
        seeded!.Document.Tracks.SelectMany(t => t.Items).OfType<MediaClip>()
            .Should().NotContain(c => c.AssetRef.ProjectFileId == FileId, "the original must not be replayed");
        seeded.Unplaced.Should().Contain(u => u.Reason.Contains("generated_clip_censored"));
    }
}
