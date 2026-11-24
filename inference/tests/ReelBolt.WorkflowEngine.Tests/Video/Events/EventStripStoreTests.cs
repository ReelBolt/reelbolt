using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.VideoAnalyzeEventsTests;

namespace ReelBolt.WorkflowEngine.Tests.Video.Events;

/// <summary>
/// The strips a describing analysis keeps for a later look (<c>LookAtMoment</c>): only with <c>DescribeEvents</c> on, only
/// for moments on offer that a look can add to and that got no answer, and never a reason for the step to fail.
/// </summary>
public class EventStripStoreTests
{
    private static VideoAnalysisRenderEvent Event(string id, VideoEventType type, double strength, string origin = "Pixels") =>
        new(id, "x", "x", 300, 5.0, SourceIndex: 0, Type: type.ToString(), Origin: origin, Strength: strength);

    /// <summary>Four moments far apart: three a look can add to (appear, hit, text in) and a flash, which needs none.</summary>
    private static VideoEventSet FourMoments() => Set(0,
        Ev(VideoEventType.Appear, 60, 0.9), Ev(VideoEventType.Hit, 240, 0.6), Ev(VideoEventType.Flash, 420, 0.8), Ev(VideoEventType.TextIn, 600, 0.4));

    private static List<string> StripUploads(List<string> uploads) => [.. uploads.Where(u => u.Contains("-event-strips/", StringComparison.Ordinal))];

    private static string[] StripIds(Run run) =>
        [.. (run.Artifact!["eventStripIds"] as JsonArray ?? []).Select(n => n!.GetValue<string>())];

    [Fact]
    public async Task With_describing_on_the_moments_that_got_no_look_keep_a_strip_and_the_described_one_and_the_flash_do_not()
    {
        var describer = new EventDescriptionTests.FakeDescriber(_ => new EventDescription("a price tag stamps on", "graphic"));
        List<string> uploads = [];

        Run run = await RunAsync(new StubDetector(_ => FourMoments()),
            c => c with { AnalyzeEvents = true, DescribeEvents = true, MaxDescribedEvents = 1 },
            describer: describer, visionProvider: true, uploads: uploads);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        describer.Requests.Should().ContainSingle("one look was allowed");
        string described = describer.Requests[0].EventId;
        JsonArray events = run.Artifact!["events"]!.AsArray();
        string[] describable = [.. events.Where(e => e!["type"]!.GetValue<string>() != nameof(VideoEventType.Flash)).Select(e => e!["id"]!.GetValue<string>())];
        string flash = events.Single(e => e!["type"]!.GetValue<string>() == nameof(VideoEventType.Flash))!["id"]!.GetValue<string>();

        string[] kept = StripIds(run);
        kept.Should().BeEquivalentTo(describable.Where(id => id != described));
        kept.Should().NotContain(flash).And.NotContain(described);
        run.Meta["events"]!["picturesKept"]!.GetValue<int>().Should().Be(2);

        // The view says which moments keep a picture, so an agent asks only about those.
        run.View["events"]!.AsArray().Where(e => e!["look"]?.GetValue<bool>() == true).Select(e => e!["id"]!.GetValue<string>())
            .Should().BeEquivalentTo(kept);

        // Each strip sits beside the analysis document, under the engine's own event id, and nowhere in the view.
        List<string> strips = StripUploads(uploads);
        strips.Should().HaveCount(2);
        strips.Should().OnlyContain(u => u.StartsWith("video-analysis/", StringComparison.Ordinal) && u.Contains("/step-1-event-strips/e", StringComparison.Ordinal) && u.EndsWith(".jpg", StringComparison.Ordinal));
        strips.Select(u => u[(u.LastIndexOf('/') + 1)..^4]).Should().BeEquivalentTo(kept);
        run.Result.Output.Should().NotContain("event-strips").And.NotContain("eventStripIds");
    }

    [Fact]
    public async Task A_look_that_failed_keeps_its_strip_so_it_can_be_asked_again()
    {
        var describer = new EventDescriptionTests.FakeDescriber((Func<EventDescriptionRequest, EventDescription?>)(_ => throw new InvalidOperationException("provider down")));
        List<string> uploads = [];

        Run run = await RunAsync(new StubDetector(_ => FourMoments()),
            c => c with { AnalyzeEvents = true, DescribeEvents = true }, describer: describer, visionProvider: true, uploads: uploads);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        StripIds(run).Should().HaveCount(3, "every moment a look can add to is still undescribed");
        StripUploads(uploads).Should().HaveCount(3);
    }

    [Fact]
    public async Task With_describing_off_nothing_is_stored_and_the_artifact_has_no_strip_list()
    {
        List<string> uploads = [];

        Run run = await RunAsync(new StubDetector(_ => FourMoments()), c => c with { AnalyzeEvents = true },
            describer: new EventDescriptionTests.FakeDescriber(_ => new EventDescription("x y z", null)), visionProvider: true, uploads: uploads);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        StripUploads(uploads).Should().BeEmpty();
        run.Artifact!.ContainsKey("eventStripIds").Should().BeFalse("a run that keeps no strips writes the artifact it always wrote");
        run.View["events"]!.ToJsonString().Should().NotContain("\"look\"", "no moment keeps a picture, so none is marked");
        run.Meta["events"]!["picturesKept"].Should().BeNull();
    }

    [Fact]
    public async Task With_no_vision_provider_nothing_is_stored_because_nobody_could_look()
    {
        List<string> uploads = [];

        Run run = await RunAsync(new StubDetector(_ => FourMoments()), c => c with { AnalyzeEvents = true, DescribeEvents = true },
            describer: new EventDescriptionTests.FakeDescriber(_ => new EventDescription("x y z", null)), visionProvider: false, uploads: uploads);

        run.Result.Status.Should().Be(StepStatus.Completed, because: run.Result.ErrorDetails ?? run.Result.Output);
        StripUploads(uploads).Should().BeEmpty();
        run.Artifact!.ContainsKey("eventStripIds").Should().BeFalse();
    }

    [Fact]
    public void Strips_go_to_undescribed_describable_events_strongest_first_and_never_past_the_ceiling()
    {
        List<VideoAnalysisRenderEvent> offered =
        [
            Event("e0", VideoEventType.Appear, 0.3),
            Event("e1", VideoEventType.Hit, 0.9),
            Event("e2", VideoEventType.Flash, 1.0),
            Event("e3", VideoEventType.TextIn, 0.6),
            Event("e4", VideoEventType.Appear, 0.95, origin: "Declared"),
            Event("e5", VideoEventType.Change, 0.6)
        ];

        EventDescriptionPlan.SelectForStrips(offered, new HashSet<string> { "e1" }).Select(e => e.Id).Should().Equal("e3", "e5", "e0");
        EventDescriptionPlan.SelectForStrips(offered, new HashSet<string>(), max: 2).Select(e => e.Id).Should().Equal("e1", "e3");
        EventDescriptionPlan.SelectForStrips(
                [.. Enumerable.Range(0, 100).Select(i => Event("e" + i, VideoEventType.Appear, 0.5))], new HashSet<string>(), max: 1000)
            .Should().HaveCount(VideoEventStrips.MaxStoredStrips);
    }

    [Fact]
    public void The_look_and_the_analysis_ask_the_same_first_party_question()
    {
        VideoAnalysisRenderEvent e = Event("e7", VideoEventType.Appear, 0.8) with { Region = new VideoEventRegion(0.0, 0.0, 0.2, 0.2) };

        EventDescriptionRequest request = EventDescriptionPlan.RequestFor(e, "/tmp/strip.jpg", [1, 2, 3]);

        request.EventId.Should().Be("e7");
        request.TypeWord.Should().Be(VideoEventWords.TypeWord(VideoEventType.Appear));
        request.What.Should().Be(VideoEventWords.What(e));
        request.RegionWord.Should().Be(VideoEventWords.RegionWord(e.Region!));
        request.StripBytes.Should().Equal(1, 2, 3);
        VisionEventDescriber.BuildPrompt(request).Should().NotContain("e7");
    }
}
