using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video.Events;
using ReelBolt.WorkflowEngine.Tests.Quality;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class SfxWorthyEventsTests
{
    private static VideoAnalysisRenderEvent Ev(int n, string type, double sec, double strength) =>
        new("e" + n, type, type, (int)(sec * 30), sec, SourceIndex: 0, Type: type, Origin: "Pixels", Strength: strength);

    [Theory]
    [InlineData("Hit", 0.8, true)]
    [InlineData("Appear", 0.5, true)]
    [InlineData("Transition", 0.5, true)]
    [InlineData("TextIn", 0.9, true)]
    [InlineData("CameraMoveStart", 0.5, true)]
    [InlineData("CameraMoveStart", 0.2, false)]
    [InlineData("Hit", 0.2, false)]
    [InlineData("Beat", 0.95, false)]
    [InlineData("SpeechStart", 0.9, false)]
    [InlineData("TextOut", 0.9, false)]
    [InlineData("Flash", 0.5, true)]
    [InlineData("Change", 0.5, true)]
    [InlineData("Change", 0.3, false)]
    [InlineData("CaptionTick", 0.9, false)]
    public void Classifies_by_type_and_strength(string type, double strength, bool worthy) =>
        SfxWorthyEvents.IsWorthy(Ev(0, type, 1, strength)).Should().Be(worthy);

    [Fact]
    public void A_declared_event_without_a_type_is_not_classified_here() =>
        SfxWorthyEvents.IsWorthy(new VideoAnalysisRenderEvent("e0", "stamp", "stamp", 0, 0)).Should().BeFalse();

    [Fact]
    public void Offer_drops_beats_keeps_stable_ids_and_thins_over_time()
    {
        // 400 events, 363 of them beats, over 174 s.
        List<VideoAnalysisRenderEvent> all = [];
        for (int i = 0; i < 400; i++)
        {
            bool worthy = i % 11 == 0 && all.Count(e => e.Type == "Hit") < 37;
            all.Add(worthy ? Ev(i, "Hit", i * 0.43, 0.5 + (i % 5) * 0.1) : Ev(i, "Beat", i * 0.43, 0.9));
        }

        List<VideoAnalysisRenderEvent> offered = SfxWorthyEvents.Select(all, _ => 174.0);

        offered.Should().NotBeEmpty().And.OnlyContain(e => e.Type == "Hit");
        offered.Count.Should().BeLessThanOrEqualTo(87);
        offered.Select(e => e.Id).Should().OnlyContain(id => all.Any(a => a.Id == id), "ids are never renumbered");
        offered.Zip(offered.Skip(1), (a, b) => b.ContentSec - a.ContentSec).Should().OnlyContain(gap => gap >= SfxWorthyEvents.MinGapSec - 1e-9);
    }

    [Fact]
    public void Offer_keeps_one_moment_per_instant_and_the_kind_that_counts_more()
    {
        // a flash and a plain change in the same instant, then a run of cuts closer than the gap
        List<VideoAnalysisRenderEvent> all =
        [
            Ev(0, "Change", 10.0, 0.9), Ev(1, "Flash", 10.3, 0.7),
            Ev(2, "Transition", 20.0, 1.0), Ev(3, "Transition", 20.2, 1.0), Ev(4, "Transition", 20.4, 1.0), Ev(5, "Transition", 20.9, 1.0),
            Ev(6, "CaptionTick", 30.0, 0.9), Ev(7, "CameraMoveStop", 40.0, 0.5), Ev(8, "Appear", 40.2, 0.45)
        ];

        List<VideoAnalysisRenderEvent> offered = SfxWorthyEvents.Select(all, _ => 60.0);

        offered.Select(e => e.Id).Should().Equal("e1", "e2", "e5", "e8");
    }

    [Fact]
    public void Offer_cap_follows_the_length_of_the_source()
    {
        List<VideoAnalysisRenderEvent> all = [.. Enumerable.Range(0, 200).Select(i => Ev(i, "Appear", i * 0.9, 0.5 + i % 4 * 0.1))];

        SfxWorthyEvents.Select(all, _ => 20.0).Should().HaveCount(10, "one per two seconds of a 20 s source");
    }

    [Fact]
    public void Offer_is_empty_when_only_beats_were_found() =>
        SfxWorthyEvents.Select([Ev(0, "Beat", 1, 0.9), Ev(1, "Beat", 5, 0.9)], _ => 10).Should().BeEmpty();

    [Fact]
    public void Speech_guard_drops_inside_a_word_or_on_speech_footage_only()
    {
        VideoAnalysisSegment[] seg = [new("t0", null, 1.0, 1.4, "w"), new("t1", null, 1.5, 1.9, "w")];
        VideoAnalysisArtifact speechy = Artifact(seg);
        SfxSpeechGuard.InsideSpeech(speechy, 0, 1.2).Should().BeTrue();
        SfxSpeechGuard.InsideSpeech(speechy, 0, 3.0).Should().BeFalse();
        SfxSpeechGuard.ShouldDrop(speechy, 0, 3.0, 100).Should().BeFalse("a source with little speech keeps a cue outside a word");
        SfxSpeechGuard.ShouldDrop(speechy, 0, 3.0, 1.5).Should().BeTrue("speech covers most of a short source");
    }

    [Fact]
    public void Scorers_count_cues_on_events_and_over_speech()
    {
        FootageEditQuality.SfxOnEventPrecision([1.0, 5.0], [1.05, 9.0]).Should().Be(0.5);
        FootageEditQuality.SfxOnEventPrecision([], [1.0]).Should().Be(1.0);
        FootageEditQuality.SfxOverSpeechCount([1.2, 3.0], [new("t0", null, 1.0, 1.4, "w")]).Should().Be(1);
    }

    private static VideoAnalysisArtifact Artifact(VideoAnalysisSegment[] segments) =>
        new(
            Version: 1,
            Media: new VideoAnalysisMedia(100, 30, 1, 1920, 1080),
            Shots: [],
            SilenceSpans: [],
            Segments: segments,
            Words: [],
            OfferedIds: [],
            Provenance: null!);
}
