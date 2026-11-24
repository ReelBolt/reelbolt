using System;
using System.Collections.Generic;
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
/// Titles and captions render at their own track's place in the stack, as the viewer stacks them: a title on a
/// lower track sits under a video on a higher track, a title on a higher track over it.
/// </summary>
public class TimelineRenderTextOrderTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly AssetRef AssetA = new(AssetRefKind.ProjectFile, Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"), null);
    private static readonly EditTimelineSettings Settings = new(320, 180, new Rational(30, 1), 48000, new Rational(1, 1), AspectPolicy.Fit, 0);

    private static readonly Dictionary<AssetRef, LocalAsset> Assets = new()
    {
        [AssetA] = new LocalAsset("/tmp/a.mp4", "a|100", new MediaProbeResult(10, 30, 1, 320, 180, "h264", "aac", 48000)),
    };

    private static MediaClip Clip(string id) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-{id}{id}{id}{id}-4{id}{id}{id}-8{id}{id}{id}-{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}{id}"), $"clip-{id}", 0, 30, AssetA, 0, 1.0, true, null,
            null, null, null, false, false, 0, 0, null, null, null, null, false, null, "frame", null, "stereo", null, null, null, null);

    private static Track TrackOf(string id, params Item[] items) =>
        new(Guid.Parse($"{id}{id}{id}{id}{id}{id}{id}{id}-0000-4000-8000-000000000000"), TrackKind.Video, $"T{id}", null, 64, false, true, false, false, null, items);

    private static TitleClip Title(string text) => new(Guid.NewGuid(), "t", 0, 30, text);

    private static EditTimeline Doc(params Track[] tracks) => new(Guid.NewGuid(), ProjectId, "doc", Settings, tracks, []);

    private static ResolvedRenderTarget Target() => TimelineRenderPresets.Resolve(new TimelineRenderStepConfig(Guid.NewGuid(), Preset: TimelineRenderPreset.Master), Settings);

    private static string Graph(EditTimeline doc)
    {
        RegionFilterGraphBuilder.RegionArgv argv = RegionFilterGraphBuilder.Build(SegmentPlanner.Plan(doc, null).Regions[0], Target(), Assets, "/scratch/r.mp4", "/fonts/f.ttf", n => $"/scratch/t-{n}.txt", 2,
            lutFilePathFor: n => $"/scratch/lut-{n}.cube", stabilizePathFor: n => $"/scratch/stab-{n}.trf");
        return argv.Args[argv.Args.ToList().IndexOf("-filter_complex") + 1];
    }

    private static string Hash(EditTimeline doc) => RegionRecipeHasher.Hash(SegmentPlanner.Plan(doc, null).Regions[0], Target(), Assets, "f");

    [Fact]
    public void Title_on_a_lower_track_is_drawn_under_the_video_above_it()
    {
        string graph = Graph(Doc(TrackOf("1", Title("under")), TrackOf("2", Clip("2"))));
        int text = graph.IndexOf("drawtext", StringComparison.Ordinal);
        int video = graph.LastIndexOf("overlay=eof_action=pass", StringComparison.Ordinal);
        text.Should().BeGreaterThan(0);
        video.Should().BeGreaterThan(text, "the video overlay comes after (over) the title");
        graph.Should().Contain("[base]drawtext=").And.Contain("[t0]").And.Contain("[t0][l0]overlay");
    }

    [Fact]
    public void Title_on_a_higher_track_is_drawn_over_the_video()
    {
        string graph = Graph(Doc(TrackOf("1", Clip("1")), TrackOf("2", Title("over"))));
        graph.IndexOf("overlay=eof_action=pass", StringComparison.Ordinal).Should().BeLessThan(graph.IndexOf("drawtext", StringComparison.Ordinal));
        graph.Should().Contain("[base][l0]overlay=eof_action=pass[v0];[v0]drawtext=").And.EndWith("[t0];[t0]format=yuv420p[vout]");
    }

    [Fact]
    public void Text_on_top_keeps_the_legacy_hash_and_text_under_a_picture_changes_it()
    {
        string legacy = Hash(Doc(TrackOf("1", Clip("1")), TrackOf("2", Title("x"))));
        string under = Hash(Doc(TrackOf("1", Title("x")), TrackOf("2", Clip("1"))));
        legacy.Should().NotBe(under);
        RegionRecipeHasher.Hash(SegmentPlanner.Plan(Doc(TrackOf("1", Clip("1")), TrackOf("2", Title("x"))), null).Regions[0], Target(), Assets, "f").Should().Be(legacy);
    }

    [Fact]
    public void Text_under_an_adjustment_layer_changes_the_hash_too()
    {
        // An adjustment clip above a title now grades it, so a region cached before track-order text must not be reused.
        string titleOnly = Hash(Doc(TrackOf("1", Title("x"))));
        string graded = Hash(Doc(TrackOf("1", Title("x")), TrackOf("2", new AdjustmentClip(Guid.NewGuid(), "a", 0, 30))));
        string adjustmentBelow = Hash(Doc(TrackOf("1", new AdjustmentClip(Guid.NewGuid(), "a", 0, 30)), TrackOf("2", Title("x"))));
        graded.Should().NotBe(adjustmentBelow);
        titleOnly.Should().NotBe(graded);
    }
}
