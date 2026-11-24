using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The three deterministic fixes behind the alpha-launch promo run (execution 9158ad85): transient
/// flash frames folded into the shot they cut into, rendered overlay assets placed at their own
/// aspect ratio, and generated seam bridges spliced between beats.
/// </summary>
public class CompositionEditingFixesTests
{
    private const double Frame = 1.0 / 30;

    // ---- ShotSpanConsolidator ----

    [Fact]
    public void Flash_frames_join_the_shot_they_cut_into()
    {
        // The real detection from the run: a one-frame bloom flash before every scene.
        var spans = new List<(double, double)>
        {
            (0, Frame), (Frame, 3.6), (3.6, 3.6 + Frame), (3.6 + Frame, 9.6), (9.6, 9.6 + Frame), (9.6 + Frame, 11)
        };

        var result = ShotSpanConsolidator.AbsorbTransients(spans, 0.2);

        result.Should().Equal((0.0, 3.6), (3.6, 9.6), (9.6, 11.0));
    }

    [Fact]
    public void A_run_of_slivers_collapses_forward_and_a_trailing_sliver_joins_the_shot_before_it()
    {
        var spans = new List<(double, double)> { (0, 2), (2, 2.03), (2.03, 2.06), (2.06, 4), (4, 4.03) };

        var result = ShotSpanConsolidator.AbsorbTransients(spans, 0.2);

        result.Should().Equal((0.0, 2.0), (2.0, 4.03));
    }

    [Fact]
    public void Nothing_changes_when_disabled_or_when_every_shot_is_long_enough()
    {
        var spans = new List<(double, double)> { (0, 0.1), (0.1, 3) };

        ShotSpanConsolidator.AbsorbTransients(spans, 0).Should().Equal(spans);
        ShotSpanConsolidator.AbsorbTransients(new List<(double, double)> { (0, 1), (1, 2) }, 0.2)
            .Should().Equal((0.0, 1.0), (1.0, 2.0));
    }

    [Fact]
    public void A_file_made_only_of_slivers_becomes_one_shot()
    {
        var result = ShotSpanConsolidator.AbsorbTransients(new List<(double, double)> { (0, 0.05), (0.05, 0.1) }, 0.2);

        result.Should().Equal((0.0, 0.1));
    }

    // ---- Rendered overlay asset geometry ----

    private static readonly VideoAnalysisRect CenterBand = new(0, 1.0 / 3, 1, 1.0 / 3);
    private static readonly VideoAnalysisRect LowerThird = new(0, 2.0 / 3, 1, 1.0 / 3);

    [Fact]
    public void A_centre_card_keeps_its_designed_size_and_aspect()
    {
        // The run's "01 · DASHBOARD" card: 1500x330, which the old accent box drew at 1574x173.
        var box = DrawtextFilterBuilder.ComputeAssetBoxPixels(CenterBand, "CenterBand", 1920, 1080, 1500, 330);

        box.Should().Be((210, 375, 1500, 330));
    }

    [Fact]
    public void An_oversized_asset_is_scaled_down_uniformly_never_stretched()
    {
        // A full-frame 16:9 render placed in a lower third must shrink to fit, keeping 16:9.
        var box = DrawtextFilterBuilder.ComputeAssetBoxPixels(LowerThird, "LowerThird", 1920, 1080, 1920, 1080)!.Value;

        (box.W / (double)box.H).Should().BeApproximately(16.0 / 9, 0.02);
        box.H.Should().BeLessThanOrEqualTo(360 - 54);
        (box.Y + box.H).Should().Be(1080 - 54, "a lower third sits on the title-safe line");
    }

    [Fact]
    public void Asset_size_follows_the_output_width()
    {
        var box = DrawtextFilterBuilder.ComputeAssetBoxPixels(CenterBand, "CenterBand", 3840, 2160, 1500, 330)!.Value;

        box.W.Should().Be(3000);
        box.H.Should().Be(660);
    }

    [Fact]
    public void Unknown_asset_size_falls_back_to_the_accent_box()
    {
        DrawtextFilterBuilder.ComputeAssetBoxPixels(CenterBand, "CenterBand", 1920, 1080, 0, 0).Should().BeNull();

        var overlay = new ResolvedOverlay("p2", "Title", "", "", 3000, "Normal", 1, 4, CenterBand, "Light",
            RenderedAssetLocalPath: "/tmp/a.webm", PlacementRegion: "CenterBand");
        OverlayAssetFilterBuilder.ComputeBox(overlay, 1920, 1080)
            .Should().Be(DrawtextFilterBuilder.ComputeAccentBoxPixels(CenterBand, "CenterBand", 1920, 1080));
    }

    [Fact]
    public void The_asset_filter_scales_to_the_aspect_preserving_box()
    {
        var overlay = new ResolvedOverlay("p2", "Title", "", "", 3000, "Normal", 1, 4, CenterBand, "Light",
            RenderedAssetLocalPath: "/tmp/a.webm", PlacementRegion: "CenterBand",
            RenderedAssetWidth: 1500, RenderedAssetHeight: 330);

        string chain = OverlayAssetFilterBuilder.BuildFilterChain("[vcut]", [overlay], 1920, 1080, _ => 1);

        chain.Should().Contain("scale=1500:330:");
        chain.Should().Contain("overlay=x=210:y=375:");
    }

    // ---- Seam bridge splicing ----

    private static VideoCompileStepExecutor.ResolvedSeamBridge Bridge(string clipId, string anchor, int source, double sec = 3) =>
        new(clipId, anchor, source, $"gen/{clipId}.mp4", $"/tmp/{clipId}.mp4", sec, new VideoAnalysisMedia(9.44, 24, 1, 1344, 768));

    private static readonly Dictionary<string, (double Start, double End, int SourceIndex)> IdTimes = new()
    {
        ["s0"] = (0, 3.6, 0),
        ["s1"] = (3.6, 9.6, 0),
        ["s2"] = (9.6, 11, 0),
        ["s3"] = (11, 17.3, 0)
    };

    [Fact]
    public void A_whole_source_span_is_split_at_each_anchor_and_bridges_play_between_the_beats()
    {
        var spans = new List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)>
        {
            (0, 17.3, 0, new[] { "s0" })
        };

        var (result, unplaced) = VideoCompileStepExecutor.SpliceSeamBridges(
            spans, [Bridge("v1", "s0", 1), Bridge("v2", "s1", 2), Bridge("v3", "s3", 3)], IdTimes);

        unplaced.Should().BeEmpty();
        result.Select(s => (s.Start, s.End, s.SourceIndex)).Should().Equal(
            (0.0, 3.0, 1),
            (0.0, 3.6, 0),
            (0.0, 3.0, 2),
            (3.6, 11.0, 0),
            (0.0, 3.0, 3),
            (11.0, 17.3, 0));
    }

    [Fact]
    public void A_bridge_whose_anchor_was_cut_away_is_reported_not_placed()
    {
        var spans = new List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)>
        {
            (0, 3.6, 0, new[] { "s0" }),
            (11, 17.3, 0, new[] { "s3" })
        };

        var (result, unplaced) = VideoCompileStepExecutor.SpliceSeamBridges(spans, [Bridge("v2", "s1", 1)], IdTimes);

        result.Should().HaveCount(2);
        unplaced.Should().ContainSingle().Which.Reason.Should().Be("anchor_not_in_output");
    }

    [Fact]
    public void Bridges_sharing_an_anchor_play_in_manifest_order()
    {
        var spans = new List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)>
        {
            (3.6, 11, 0, new[] { "s1" })
        };

        var (result, _) = VideoCompileStepExecutor.SpliceSeamBridges(
            spans, [Bridge("a", "s1", 1), Bridge("b", "s1", 2)], IdTimes);

        result.Select(s => s.SourceIndex).Should().Equal(1, 2, 0);
    }

    // ---- Pinned shot plans ----

    [Fact]
    public void A_project_file_plan_source_requires_the_file()
    {
        var config = new VideoGenerateStepConfig { PlanSource = VideoGeneratePlanSource.ProjectFile };
        config.Validate().Should().Contain(e => e.Contains("PlanProjectFileId"));

        config.PlanProjectFileId = System.Guid.NewGuid();
        config.Validate().Should().NotContain(e => e.Contains("PlanProjectFileId"));
    }
    // ---- Author-declared scenes ----

    [Fact]
    public void Declared_scenes_get_engine_handles_and_become_gapless_shot_spans()
    {
        var (manifest, errors) = CompositionSceneManifest.FromDeclaration("FinalVideo", 30,
        [
            new DeclaredScene("Hero", 0, 108, "Wordmark reveal on violet rings", ["REELFORGE"]),
            new DeclaredScene("Dashboard", 156, 132, "The dashboard takeover"),
            new DeclaredScene("EndCard", 1090, 136, "Call to action", ["Start your project"])
        ]);

        errors.Should().BeEmpty();
        manifest!.Scenes.Select(sc => sc.Handle).Should().Equal("scene-1", "scene-2", "scene-3");

        var (spans, scenes) = manifest.ToShotSpans(40.866);
        spans.Should().Equal((0.0, 156 / 30.0), (156 / 30.0, 1090 / 30.0), (1090 / 30.0, 40.866));
        scenes.Select(sc => sc.Name).Should().Equal("Hero", "Dashboard", "EndCard");
    }

    [Fact]
    public void A_scene_too_short_to_read_is_refused_with_the_frames_it_needs()
    {
        // 14 words in 42 frames (1.4 s): a viewer needs ~4.8 s.
        var (manifest, errors) = CompositionSceneManifest.FromDeclaration("FinalVideo", 30,
        [
            new DeclaredScene("Sync", 0, 42, "Sources syncing",
                ["Everything in sync. Automatically.", "Every asset, project and render stays indexed and in sync."])
        ]);

        manifest.Should().BeNull();
        errors.Should().ContainSingle().Which.Should().Contain("rushed").And.Contain("at least 144 frames");
    }

    [Fact]
    public void Overlapping_or_undescribed_scenes_are_refused_all_at_once()
    {
        var (manifest, errors) = CompositionSceneManifest.FromDeclaration("FinalVideo", 30,
        [
            new DeclaredScene("A", 0, 100, "first"),
            new DeclaredScene("B", 90, 100, ""),
        ]);

        manifest.Should().BeNull();
        errors.Should().HaveCount(2);
    }

    [Fact]
    public void Reading_time_grows_with_the_copy()
    {
        CompositionSceneManifest.MinReadSeconds([]).Should().Be(0);
        CompositionSceneManifest.MinReadSeconds(["FAST."]).Should().BeApproximately(0.8 + 1 / 3.5, 1e-9);
        CompositionSceneManifest.RushedReason(["FAST."], 0.4).Should().NotBeNull();
        CompositionSceneManifest.RushedReason(["FAST."], 1.2).Should().BeNull();
    }

    // ---- Vision caption parsing ----

    [Fact]
    public void A_caption_list_written_as_a_string_or_null_still_parses()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            Converters = { new LenientStringListConverter() }
        };
        const string json = """
            {"shotId":"s13","summary":"A hand holds a phone with a green screen","subjects":["hand","phone"],
             "action":"holding","setting":"park","mood":"calm","shotScale":"Medium","cameraAngle":"Eye level",
             "onScreenText":"","tags":null,"timeOfDay":"Day","lighting":"overcast","visualStyle":"natural",
             "framing":"centered","technicalIssues":"slight motion blur"}
            """;

        VideoShotCaption caption = System.Text.Json.JsonSerializer.Deserialize<VideoShotCaption>(json, options)!;

        caption.OnScreenText.Should().BeEmpty();
        caption.Tags.Should().BeEmpty();
        caption.TechnicalIssues.Should().Equal("slight motion blur");
        caption.Subjects.Should().Equal("hand", "phone");
    }

    [Fact]
    public void A_bridge_goes_before_the_run_that_starts_at_its_anchor_not_into_a_padded_neighbour()
    {
        // Production shape: [s0..s1] padded past s2's start, then another clip, then [s2..s3].
        var spans = new List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)>
        {
            (0, 9.72, 0, new[] { "s0" }),
            (0, 4, 5, new[] { "phone" }),
            (9.48, 17.3, 0, new[] { "s2" })
        };

        var (result, unplaced) = VideoCompileStepExecutor.SpliceSeamBridges(
            spans, [Bridge("v4", "s2", 9)], IdTimes, startToleranceSec: 0.29);

        unplaced.Should().BeEmpty();
        result.Select(s => s.SourceIndex).Should().Equal(0, 5, 9, 0);
        result[0].End.Should().Be(9.72, "the neighbouring run is not split");
    }
}

