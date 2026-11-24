using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

using ResolvedSpan = VideoCompileStepExecutor.ResolvedSpan;

/// <summary>Video events analysis: the merged e{n} list, the number-free view, the budget pass and the anchor resolution.</summary>
public class VideoEventOffersTests
{
    private static VideoEvent Ev(VideoEventType type, int frame, double strength, VideoEventRegion? region = null, VideoEventOrigin origin = VideoEventOrigin.Pixels) =>
        new(type, frame, null, region, strength, origin, 0.9);

    private static VideoEventSet Set(params VideoEvent[] events) => new(30, 900, 1920, 1080, events);

    private static VideoAnalysisRenderEvent Declared(string id, string name, int frame, double sec, int? source = 0) =>
        new(id, name, name, frame, sec, SourceIndex: source);

    private static OutputTimeline Timeline(params ResolvedSpan[] spans) =>
        OutputTimeline.Build(spans, Enumerable.Range(0, System.Math.Max(0, spans.Length - 1))
            .Select(i => new SeamPlan(i, SeamTreatment.HardCut, 0, 0, "", 0, "test:hardCut")).ToList());

    [Fact]
    public void Declared_and_detected_events_share_one_id_space_ordered_by_source_then_time()
    {
        List<VideoAnalysisRenderEvent> merged = VideoEventOffers.Merge(
            [Declared("e0", "stamp", 45, 1.5), Declared("e1", "late", 300, 10.0), new("e2", "row", "Row", 20, 0.8, InsertContentStorageKey: "k")],
            [
                new VideoEventOffers.DetectedSource(1, Set(Ev(VideoEventType.Hit, 30, 0.9))),
                new VideoEventOffers.DetectedSource(0, Set(Ev(VideoEventType.Appear, 60, 0.5), Ev(VideoEventType.Beat, 15, 0.4, origin: VideoEventOrigin.Audio)))
            ]);

        merged.Select(e => e.Id).Should().Equal("e0", "e1", "e2", "e3", "e4", "e5");
        merged.Select(e => (e.SourceIndex, e.Name)).Should().Equal(
            (0, "beat"), (0, "stamp"), (0, "appear"), (0, "late"), (1, "hit"), (null, "row"));
        merged.Where(e => e.Name == "stamp").Single().Origin.Should().Be("Declared");
        merged.Where(e => e.Name == "stamp").Single().Type.Should().BeNull();
        merged.Where(e => e.Name == "appear").Single().Should().Match<VideoAnalysisRenderEvent>(e => e.Type == "Appear" && e.Origin == "Pixels" && e.ContentSec == 2.0);
        VideoEventOffers.DeclaredOf(merged).Select(e => e.Name).Should().Equal("stamp", "late", "row");
    }

    [Fact]
    public void With_nothing_detected_the_declared_events_come_back_unchanged_in_order()
    {
        List<VideoAnalysisRenderEvent> declared = [Declared("e0", "a", 30, 1.0), Declared("e1", "b", 60, 2.0)];

        List<VideoAnalysisRenderEvent> merged = VideoEventOffers.Merge(declared, []);

        merged.Select(e => (e.Id, e.Name, e.Frame)).Should().Equal(declared.Select(e => (e.Id, e.Name, e.Frame)));
    }

    [Fact]
    public void The_view_shows_words_only_and_never_a_number()
    {
        List<VideoAnalysisRenderEvent> merged = VideoEventOffers.Merge([],
        [
            new VideoEventOffers.DetectedSource(0, Set(
                Ev(VideoEventType.MoveStart, 30, 0.9, new VideoEventRegion(0.05, 0.05, 0.2, 0.2)),
                Ev(VideoEventType.TextIn, 90, 0.5, new VideoEventRegion(0.4, 0.8, 0.2, 0.1)),
                Ev(VideoEventType.Change, 120, 0.1)))
        ]);

        JsonArray view = VideoEventOffers.ViewNode(merged);

        view.ToJsonString().Should().Be(
            "[{\"id\":\"e0\",\"type\":\"move_start\",\"strength\":\"strong\",\"region\":\"top-left\",\"what\":\"a medium element starts to move\"}," +
            "{\"id\":\"e1\",\"type\":\"text_in\",\"strength\":\"clear\",\"region\":\"bottom\",\"what\":\"a line of text appears\"}," +
            "{\"id\":\"e2\",\"type\":\"change\",\"strength\":\"weak\",\"what\":\"something changes in place\"}]");
        AllLeaves(view).Select(n => n!.GetValueKind()).Should().OnlyContain(k => k == JsonValueKind.String);
    }

    [Fact]
    public void Only_a_moment_that_kept_a_picture_is_marked_for_a_look()
    {
        List<VideoAnalysisRenderEvent> merged = VideoEventOffers.Merge([],
        [
            new VideoEventOffers.DetectedSource(0, Set(Ev(VideoEventType.Hit, 30, 0.9), Ev(VideoEventType.Hit, 90, 0.5)))
        ]);

        JsonArray marked = VideoEventOffers.ViewNode(merged, new HashSet<string> { "e1" });

        marked[0]!.AsObject().ContainsKey("look").Should().BeFalse();
        marked[1]!["look"]!.GetValue<bool>().Should().BeTrue();
        // No kept pictures (every run that does not describe events): the view is the one it always was.
        VideoEventOffers.ViewNode(merged, new HashSet<string>()).ToJsonString().Should().Be(VideoEventOffers.ViewNode(merged).ToJsonString());
        VideoEventOffers.ViewNode(merged).ToJsonString().Should().NotContain("look");

        // The read tools mark the same moments from the artifact's own list.
        VideoAnalysisMedia media = new(30, 30, 1, 1920, 1080, "h264", "aac");
        VideoAnalysisArtifact artifact = new VideoAnalysisArtifact(2, media, [], [], [], [], [],
            new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false), Sources: [new VideoAnalysisSourceInfo(0, "k", media)])
        {
            Events = merged,
            EventStripIds = ["e1"]
        };
        VideoEventOutline.EventNode(artifact, merged[0]).ContainsKey("look").Should().BeFalse();
        VideoEventOutline.EventNode(artifact, merged[1])["look"]!.GetValue<bool>().Should().BeTrue();
        VideoEventOutline.EventNode(artifact with { EventStripIds = null }, merged[1]).ContainsKey("look").Should().BeFalse();
    }

    [Theory]
    [InlineData(0.45, 0.45, 0.1, 0.1, "center")]
    [InlineData(0.9, 0.9, 0.1, 0.1, "bottom-right")]
    [InlineData(0.0, 0.45, 0.1, 0.1, "left")]
    [InlineData(0.45, 0.0, 0.1, 0.1, "top")]
    public void Region_words_are_a_three_by_three_pad(double x, double y, double w, double h, string word) =>
        VideoEventOffers.RegionWord(new VideoEventRegion(x, y, w, h)).Should().Be(word);

    [Fact]
    public void A_tight_budget_drops_the_weakest_events_and_keeps_the_rest_in_id_order()
    {
        List<VideoAnalysisRenderEvent> merged = VideoEventOffers.Merge([],
        [
            new VideoEventOffers.DetectedSource(0, Set(
                Ev(VideoEventType.Hit, 10, 0.3), Ev(VideoEventType.Hit, 20, 0.9), Ev(VideoEventType.Hit, 30, 0.1), Ev(VideoEventType.Hit, 40, 0.7)))
        ]);

        (List<VideoAnalysisRenderEvent> shown, int dropped) = VideoEventOffers.FitToBudget(merged, remainingChars: 270);

        dropped.Should().Be(2);
        shown.Select(e => e.Id).Should().Equal("e1", "e3");
        VideoEventOffers.FitToBudget(merged, 100_000).Dropped.Should().Be(0);
        VideoEventOffers.FitToBudget(merged, -5).Shown.Should().BeEmpty();
    }

    [Fact]
    public void The_digest_is_deterministic_and_covers_frames_the_view_never_shows()
    {
        List<VideoAnalysisRenderEvent> Make(int frame) => VideoEventOffers.Merge([],
            [new VideoEventOffers.DetectedSource(0, Set(Ev(VideoEventType.Hit, frame, 0.9)))]);

        VideoEventOffers.Digest(Make(30)).Should().MatchRegex("^[0-9a-f]{64}$").And.Be(VideoEventOffers.Digest(Make(30)));
        VideoEventOffers.Digest(Make(31)).Should().NotBe(VideoEventOffers.Digest(Make(30)));
        VideoEventOffers.ViewNode(Make(30)).ToJsonString().Should().Be(VideoEventOffers.ViewNode(Make(31)).ToJsonString());
    }

    [Fact]
    public void Meta_counts_by_type_and_adds_budget_and_detector_suppression()
    {
        List<VideoAnalysisRenderEvent> merged = VideoEventOffers.Merge([],
            [new VideoEventOffers.DetectedSource(0, Set(Ev(VideoEventType.Hit, 10, 0.9), Ev(VideoEventType.Hit, 20, 0.9), Ev(VideoEventType.Beat, 30, 0.9)))]);

        JsonObject meta = VideoEventOffers.MetaNode(merged, merged, suppressed: 4);

        meta["count"]!.GetValue<int>().Should().Be(3);
        meta["byType"]!.ToJsonString().Should().Be("{\"beat\":1,\"hit\":2}");
        meta["suppressed"]!.GetValue<int>().Should().Be(4);
        meta["digest"]!.GetValue<string>().Should().HaveLength(64);
    }

    [Fact]
    public void A_flag_off_artifact_and_a_declared_only_event_serialize_exactly_as_before()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        VideoAnalysisArtifact artifact = new(
            Version: 2, Media: new VideoAnalysisMedia(10, 30, 1, 1920, 1080), Shots: [], SilenceSpans: [], Segments: [], Words: [],
            OfferedIds: [], Provenance: null!);

        string json = JsonSerializer.Serialize(artifact, options);
        json.Should().NotContain("\"events\"");
        JsonSerializer.Serialize(new VideoAnalysisRenderEvent("e0", "row", "Row", 30, 1, SourceIndex: 0), options)
            .Should().Be("{\"id\":\"e0\",\"name\":\"row\",\"label\":\"Row\",\"frame\":30,\"contentSec\":1,\"sourceIndex\":0,\"insertContentStorageKey\":null}");
    }

    [Fact]
    public void A_cue_anchored_on_a_detected_event_lands_within_one_frame_of_it()
    {
        // Source 1 is kept from 3s to 8s and placed after 4s of source 0; the detected hit is at source frame 150 (5.0s).
        List<VideoAnalysisRenderEvent> merged = VideoEventOffers.Merge([],
            [new VideoEventOffers.DetectedSource(1, Set(Ev(VideoEventType.Hit, 150, 0.9)))]);
        VideoAnalysisArtifact artifact = new(
            Version: 2, Media: new VideoAnalysisMedia(10, 30, 1, 1920, 1080), Shots: [new VideoAnalysisShot("s0", 0, 10)],
            SilenceSpans: [], Segments: [], Words: [], OfferedIds: ["s0"], Provenance: null!,
            Events: merged, OfferedEventIds: ["e0"]);
        OutputTimeline timeline = Timeline(new ResolvedSpan(0, 4, 0, 4, 0, 120, 0), new ResolvedSpan(3, 8, 3, 8, 90, 240, 1));

        VideoAnalysisRenderEvent found = VideoEventOffers.Find(artifact, "e0")!;
        (double? sec, string? reason) = RenderEventAnchors.ResolveOutputSec(found, timeline, []);

        reason.Should().BeNull();
        sec.Should().BeApproximately(6.0, 1.0 / 30);
        VideoEventOffers.Find(artifact, "e9").Should().BeNull();
    }

    [Fact]
    public void A_detected_event_the_edit_cuts_away_drops_its_cue()
    {
        List<VideoAnalysisRenderEvent> merged = VideoEventOffers.Merge([],
            [new VideoEventOffers.DetectedSource(1, Set(Ev(VideoEventType.Hit, 30, 0.9)))]);
        OutputTimeline timeline = Timeline(new ResolvedSpan(3, 8, 3, 8, 90, 240, 1));

        RenderEventAnchors.ResolveOutputSec(merged[0], timeline, []).Should().Be(((double?)null, "anchor_cut_away"));
    }

    [Fact]
    public void A_detected_event_id_is_never_a_cut_anchor()
    {
        List<VideoAnalysisRenderEvent> merged = VideoEventOffers.Merge([],
            [new VideoEventOffers.DetectedSource(0, Set(Ev(VideoEventType.Hit, 30, 0.9)))]);
        VideoAnalysisArtifact artifact = new(
            Version: 2, Media: new VideoAnalysisMedia(10, 30, 1, 1920, 1080), Shots: [new VideoAnalysisShot("s0", 0, 10)],
            SilenceSpans: [], Segments: [], Words: [], OfferedIds: ["s0"], Provenance: null!,
            Events: merged, OfferedEventIds: ["e0"]);

        VideoCompileStepExecutor.BuildIdTimeIndex(artifact).Should().NotContainKey("e0");
    }

    [Fact]
    public void The_analysis_revision_tag_names_the_events_surface() =>
        ReelBolt.WorkflowEngine.Execution.Caching.DeterministicStepRevisions.For(ReelBolt.Shared.Data.Models.StepType.VideoAnalyze)
            .Should().Contain("+events-1").And.Contain("+outline-1").And.Contain("+speech-outline-1");

    private static IEnumerable<JsonNode?> AllLeaves(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (JsonNode? child in array) foreach (JsonNode? leaf in AllLeaves(child)) yield return leaf;
                break;
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> child in obj) foreach (JsonNode? leaf in AllLeaves(child.Value)) yield return leaf;
                break;
            default:
                yield return node;
                break;
        }
    }
}
