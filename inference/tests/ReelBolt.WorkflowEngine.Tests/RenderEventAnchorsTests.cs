using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

using ResolvedSpan = VideoCompileStepExecutor.ResolvedSpan;

/// <summary>
/// T-110: <c>e{n}</c> event anchors — built from render event sidecars at analysis (source and insert
/// content), shown to the model as id + label only, and mapped to OUTPUT time by the compile.
/// </summary>
public class RenderEventAnchorsTests
{
    private const string ContentKey = "projects/11111111-1111-1111-1111-111111111111/outputFiles/22222222-2222-2222-2222-222222222222/screen.mp4";

    private static RenderEventManifest Manifest(double fps, params (string Name, int Frame, string Label)[] events) =>
        RenderEventManifest.Validate(events.Select(e => new RenderEvent(e.Name, e.Frame, e.Label)).ToList()).Manifest! with { Fps = fps };

    private static OutputTimeline Timeline(params ResolvedSpan[] spans) =>
        OutputTimeline.Build(spans, Enumerable.Range(0, System.Math.Max(0, spans.Length - 1))
            .Select(i => new SeamPlan(i, SeamTreatment.HardCut, 0, 0, "", 0, "test:hardCut")).ToList());

    private static ResolvedScreenInsert Insert(string regionId, double start, double end, string? key = ContentKey) =>
        new(regionId, "/tmp/content.mp4", start, end, [], 30, 1, AssetStorageKey: key);

    [Fact]
    public void Source_events_are_trim_adjusted_and_numbered_contiguously()
    {
        // The analysed window starts 2s into the render and lasts 5s.
        List<VideoAnalysisRenderEvent> events = RenderEventAnchors.BuildEvents(
        [
            RenderEventAnchors.ForSource(Manifest(30, ("early", 30, "Before the window"), ("stamp", 90, "Stamp lands"), ("slam", 180, "Door slams"), ("late", 300, "After the window")),
                sourceIndex: 1, trimInSec: 2, durationSec: 5)
        ]);

        events.Select(e => e.Id).Should().Equal("e0", "e1");
        events[0].Should().Be(new VideoAnalysisRenderEvent("e0", "stamp", "Stamp lands", 90, 1.0, SourceIndex: 1));
        events[1].ContentSec.Should().BeApproximately(4.0, 1e-9);
    }

    [Fact]
    public void Insert_events_stay_on_the_content_timeline()
    {
        List<VideoAnalysisRenderEvent> events = RenderEventAnchors.BuildEvents(
            [RenderEventAnchors.ForInsertContent(Manifest(25, ("row", 50, "Row appears")), ContentKey)]);

        events.Should().ContainSingle().Which.Should().Be(
            new VideoAnalysisRenderEvent("e0", "row", "Row appears", 50, 2.0, InsertContentStorageKey: ContentKey));
    }

    [Fact]
    public void The_offer_is_capped_across_every_manifest()
    {
        RenderEventManifest full = Manifest(30, Enumerable.Range(0, RenderEventManifest.MaxEvents).Select(i => ($"r{i}", i, "Row")).ToArray());

        List<VideoAnalysisRenderEvent> events = RenderEventAnchors.BuildEvents(
            [RenderEventAnchors.ForSource(full, 0, 0, null), RenderEventAnchors.ForInsertContent(full, ContentKey)]);

        events.Should().HaveCount(RenderEventAnchors.MaxOfferedEvents);
        events[^1].Id.Should().Be($"e{RenderEventAnchors.MaxOfferedEvents - 1}");
    }

    [Fact]
    public void The_view_shows_ids_and_labels_never_a_time_or_frame()
    {
        List<VideoAnalysisRenderEvent> events = RenderEventAnchors.BuildEvents(
        [
            RenderEventAnchors.ForSource(Manifest(30, ("stamp", 90, "Stamp lands")), 0, 0, null),
            RenderEventAnchors.ForInsertContent(Manifest(30, ("row", 45, "Row appears")), ContentKey)
        ]);

        JsonArray view = RenderEventAnchors.ViewNode(events);
        string json = view.ToJsonString();

        json.Should().Be("""[{"id":"e0","label":"Stamp lands","src":0},{"id":"e1","label":"Row appears","insert":true}]""");
    }

    [Fact]
    public void The_digest_changes_with_any_event_and_is_stable()
    {
        List<VideoAnalysisRenderEvent> a = RenderEventAnchors.BuildEvents([RenderEventAnchors.ForInsertContent(Manifest(30, ("row", 45, "Row")), ContentKey)]);
        List<VideoAnalysisRenderEvent> b = RenderEventAnchors.BuildEvents([RenderEventAnchors.ForInsertContent(Manifest(30, ("row", 46, "Row")), ContentKey)]);

        RenderEventAnchors.Digest(a).Should().MatchRegex("^[0-9a-f]{64}$").And.Be(RenderEventAnchors.Digest(a.ToList()));
        RenderEventAnchors.Digest(b).Should().NotBe(RenderEventAnchors.Digest(a));
    }

    [Fact]
    public void A_source_event_maps_through_the_cut()
    {
        // Kept: source 1 from 3s to 8s, placed after 4s of source 0.
        OutputTimeline timeline = Timeline(new ResolvedSpan(0, 4, 0, 4, 0, 120, 0), new ResolvedSpan(3, 8, 3, 8, 90, 240, 1));
        VideoAnalysisRenderEvent ev = new("e0", "stamp", "Stamp lands", 150, 5.0, SourceIndex: 1);

        (double? sec, string? reason) = RenderEventAnchors.ResolveOutputSec(ev, timeline, []);

        reason.Should().BeNull();
        sec.Should().BeApproximately(6.0, 1e-9);
    }

    [Fact]
    public void A_source_event_inside_a_cut_region_is_dropped()
    {
        OutputTimeline timeline = Timeline(new ResolvedSpan(3, 8, 3, 8, 90, 240, 1));
        VideoAnalysisRenderEvent ev = new("e0", "stamp", "Stamp lands", 30, 1.0, SourceIndex: 1);

        RenderEventAnchors.ResolveOutputSec(ev, timeline, []).Should().Be(((double?)null, "anchor_cut_away"));
    }

    [Fact]
    public void An_insert_event_maps_through_the_insert_window()
    {
        OutputTimeline timeline = Timeline(new ResolvedSpan(0, 20, 0, 20, 0, 600, 0));
        VideoAnalysisRenderEvent ev = new("e0", "row", "Row appears", 60, 2.0, InsertContentStorageKey: ContentKey);

        (double? sec, string? reason) = RenderEventAnchors.ResolveOutputSec(ev, timeline, [Insert("r1", 9, 15, "other"), Insert("r0", 4, 9)]);

        reason.Should().BeNull();
        sec.Should().BeApproximately(6.0, 1e-9);
    }

    /// <summary>QA-P1-19 item 3: with <c>InsertContentDips = Cut</c> the encoded content is shorter than the
    /// file the event was declared against, so the content second shifts back by every removed second
    /// before it. Without that, a sound cue anchored to the event fires late by the removed duration.</summary>
    [Fact]
    public void An_insert_event_follows_content_seconds_removed_by_a_cut_dip()
    {
        OutputTimeline timeline = Timeline(new ResolvedSpan(0, 20, 0, 20, 0, 600, 0));
        VideoAnalysisRenderEvent ev = new("e0", "row", "Row appears", 150, 5.0, InsertContentStorageKey: ContentKey);

        ResolvedScreenInsert insert = Insert("r0", 4, 9) with { RemovedContentDips = [(1.0, 2.5)] };

        (double? sec, string? reason) = RenderEventAnchors.ResolveOutputSec(ev, timeline, [insert]);

        reason.Should().BeNull();
        sec.Should().BeApproximately(7.5, 1e-9); // 4 + (5.0 - 1.5)
    }

    [Fact]
    public void An_insert_event_inside_a_removed_dip_has_nothing_left_to_anchor_to()
    {
        OutputTimeline timeline = Timeline(new ResolvedSpan(0, 20, 0, 20, 0, 600, 0));
        VideoAnalysisRenderEvent ev = new("e0", "row", "Row appears", 60, 2.0, InsertContentStorageKey: ContentKey);

        ResolvedScreenInsert insert = Insert("r0", 4, 9) with { RemovedContentDips = [(1.0, 2.5)] };

        RenderEventAnchors.ResolveOutputSec(ev, timeline, [insert]).Should().Be(((double?)null, "anchor_cut_away"));
    }

    [Fact]
    public void An_insert_event_past_the_on_screen_window_is_dropped()
    {
        OutputTimeline timeline = Timeline(new ResolvedSpan(0, 20, 0, 20, 0, 600, 0));
        VideoAnalysisRenderEvent ev = new("e0", "row", "Row appears", 180, 6.0, InsertContentStorageKey: ContentKey);

        RenderEventAnchors.ResolveOutputSec(ev, timeline, [Insert("r0", 4, 9)]).Should().Be(((double?)null, "anchor_cut_away"));
    }

    [Fact]
    public void An_insert_event_whose_content_was_not_composited_is_dropped()
    {
        OutputTimeline timeline = Timeline(new ResolvedSpan(0, 20, 0, 20, 0, 600, 0));
        VideoAnalysisRenderEvent ev = new("e0", "row", "Row appears", 30, 1.0, InsertContentStorageKey: ContentKey);

        RenderEventAnchors.ResolveOutputSec(ev, timeline, [Insert("r0", 4, 9, "projects/x/outputFiles/y/other.mp4")])
            .Should().Be(((double?)null, "event_insert_not_applied"));
        RenderEventAnchors.ResolveOutputSec(ev, timeline, [Insert("r0", 4, 9) with { GuardFillColor = "black" }])
            .Should().Be(((double?)null, "event_insert_not_applied"), "a plate guard is not content");
    }

    [Fact]
    public void An_event_id_is_never_a_cut_anchor()
    {
        VideoAnalysisArtifact artifact = new(
            Version: 2,
            Media: new VideoAnalysisMedia(10, 30, 1, 1920, 1080),
            Shots: [new VideoAnalysisShot("s0", 0, 10)],
            SilenceSpans: [],
            Segments: [],
            Words: [],
            OfferedIds: ["s0"],
            Provenance: null!,
            RenderEvents: [new VideoAnalysisRenderEvent("e0", "row", "Row", 30, 1, SourceIndex: 0)],
            OfferedEventIds: ["e0"]);

        VideoCompileStepExecutor.BuildIdTimeIndex(artifact).Should().NotContainKey("e0");
    }
}
