using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AwesomeAssertions;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.EventMoves;

/// <summary>eventMoves: config checks, word tables, expansion into reframes, drop reasons, off = unchanged JSON.</summary>
public class EventMoveTests
{
    private static readonly VideoAnalysisMedia Wide = new(10, 30, 1, 1920, 1080);

    private static VideoCompileStepConfig Config(IReadOnlyList<EventMoveRequest>? moves, VideoCompileMode mode = VideoCompileMode.Reencode,
        IReadOnlyList<VideoReframe>? hand = null) =>
        new(1, new ExtractInputRef(ExtractInputSource.Previous), 1, Mode: mode, Reframes: hand, EventMoves: moves);

    private static VideoAnalysisRenderEvent Ev(string id, double sec, int? source = 0, VideoEventRegion? region = null) =>
        new(id, "event", "event", (int)(sec * 30), sec, source, null, "Flash", "local", 0.8, region);

    private static VideoAnalysisArtifact Artifact(params VideoAnalysisRenderEvent[] events) => new(
        Version: 2, Media: Wide, Shots: [new VideoAnalysisShot("s0", 0, 10)], SilenceSpans: [], Segments: [], Words: [],
        OfferedIds: ["s0"], Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
        Events: events, OfferedEventIds: events.Select(e => e.Id).ToList());

    private static readonly IReadOnlyList<(int, double, double)> WholeSource = [(0, 0, 10)];

    private static EventMoveExpansion Run(VideoCompileStepConfig config, VideoAnalysisArtifact artifact, IReadOnlyList<(int, double, double)>? spans = null) =>
        EventMoveExpander.Expand(config, artifact, spans ?? WholeSource);

    [Fact]
    public void Off_means_nothing_expands_and_the_stored_json_has_no_member()
    {
        Run(Config(null), Artifact()).Reframes.Should().BeEmpty();
        Run(Config([]), Artifact()).Report.Should().BeEmpty();
        JsonSerializer.Serialize(Config(null)).Should().NotContain("EventMoves");
    }

    [Fact]
    public void Config_checks_ids_words_count_and_mode()
    {
        VideoReframe.CheckConfig(Config([new EventMoveRequest("e3")])).IsValid.Should().BeTrue();
        VideoReframe.CheckConfig(Config([new EventMoveRequest("t3")])).ErrorCode.Should().Be("EVENT_MOVES_EVENT_ID_INVALID");
        VideoReframe.CheckConfig(Config([new EventMoveRequest("e01")])).ErrorCode.Should().Be("EVENT_MOVES_EVENT_ID_INVALID");
        VideoReframe.CheckConfig(Config([new EventMoveRequest("e1", Move: (EventMoveKind)99)])).ErrorCode.Should().Be("EVENT_MOVES_WORD_UNKNOWN");
        VideoReframe.CheckConfig(Config(Enumerable.Range(0, 16).Select(i => new EventMoveRequest("e" + i)).ToList())).IsValid.Should().BeTrue();
        VideoReframe.CheckConfig(Config(Enumerable.Range(0, 17).Select(i => new EventMoveRequest("e" + i)).ToList())).ErrorCode.Should().Be("EVENT_MOVES_TOO_MANY");
        VideoReframe.CheckConfig(Config([new EventMoveRequest("e1")], VideoCompileMode.StreamCopy)).ErrorCode.Should().Be("EVENT_MOVES_REQUIRE_REENCODE");
    }

    public static IEnumerable<object[]> AllMoves() =>
        from m in Enum.GetValues<EventMoveKind>()
        from i in Enum.GetValues<EventMoveIntensity>()
        from h in Enum.GetValues<EventMoveHold>()
        select new object[] { m, i, h };

    [Theory]
    [MemberData(nameof(AllMoves))]
    public void Every_word_combination_expands_to_valid_monotone_in_canvas_keyframes(EventMoveKind move, EventMoveIntensity intensity, EventMoveHold hold)
    {
        var region = new VideoEventRegion(0.7, 0.1, 0.2, 0.2);
        EventMoveExpansion e = Run(Config([new EventMoveRequest("e0", move, intensity, hold)]), Artifact(Ev("e0", 4.0, region: region)));

        e.Report.Single().Applied.Should().BeTrue();
        VideoReframe r = e.Reframes.Single();
        VideoReframeValidation v = VideoReframe.Validate(e.Reframes);
        v.IsValid.Should().BeTrue();
        v.Clamps.Should().BeEmpty("the tables keep every window inside the frame");
        r.Keyframes.Select(k => k.AtSec).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        r.Keyframes.Should().OnlyContain(k => k.Zoom >= 1 && k.Zoom <= VideoReframe.MaxZoom);
        r.Keyframes.Should().OnlyContain(k => k.CenterX - 0.5 / k.Zoom >= -1e-9 && k.CenterX + 0.5 / k.Zoom <= 1 + 1e-9
            && k.CenterY - 0.5 / k.Zoom >= -1e-9 && k.CenterY + 0.5 / k.Zoom <= 1 + 1e-9);
        r.Keyframes.Count.Should().BeLessThanOrEqualTo(VideoReframe.MaxKeyframes);
        r.ToSec.Should().BeApproximately(r.FromSec!.Value + r.Keyframes[^1].AtSec, 1e-9);
        r.Keyframes[^1].Zoom.Should().Be(1.0, "every move returns to the full frame");
    }

    [Fact]
    public void A_punch_in_starts_on_the_event_and_reaches_the_peak_toward_the_region()
    {
        var region = new VideoEventRegion(0.7, 0.1, 0.2, 0.2); // top-right cell
        VideoReframe r = Run(Config([new EventMoveRequest("e0", EventMoveKind.PunchIn, EventMoveIntensity.Strong)]),
            Artifact(Ev("e0", 4.0, region: region))).Reframes.Single();

        r.FromSec.Should().Be(4.0);
        ReframeKeyframe peak = r.Keyframes.OrderByDescending(k => k.Zoom).First();
        peak.Zoom.Should().Be(EventMoveExpander.PeakZoom(EventMoveIntensity.Strong));
        peak.CenterX.Should().BeGreaterThan(0.5);
        peak.CenterY.Should().BeLessThan(0.5);
    }

    [Fact]
    public void Intensity_and_hold_words_are_ordered()
    {
        EventMoveExpander.PeakZoom(EventMoveIntensity.Subtle).Should().BeLessThan(EventMoveExpander.PeakZoom(EventMoveIntensity.Normal));
        EventMoveExpander.PeakZoom(EventMoveIntensity.Normal).Should().BeLessThan(EventMoveExpander.PeakZoom(EventMoveIntensity.Strong));
        EventMoveExpander.HoldSeconds(EventMoveHold.Short).Should().BeLessThan(EventMoveExpander.HoldSeconds(EventMoveHold.Medium));
        EventMoveExpander.HoldSeconds(EventMoveHold.Medium).Should().BeLessThan(EventMoveExpander.HoldSeconds(EventMoveHold.Long));
        EventMoveExpander.ShakeAmplitude(EventMoveIntensity.Subtle).Should().BeLessThan(EventMoveExpander.ShakeAmplitude(EventMoveIntensity.Strong));
    }

    [Theory]
    [InlineData(EventMoveKind.PanLeft, -1, 0)]
    [InlineData(EventMoveKind.PanRight, 1, 0)]
    [InlineData(EventMoveKind.PanUp, 0, -1)]
    [InlineData(EventMoveKind.PanDown, 0, 1)]
    public void A_pan_travels_in_its_direction(EventMoveKind move, int sx, int sy)
    {
        VideoReframe r = Run(Config([new EventMoveRequest("e0", move, EventMoveIntensity.Strong)]), Artifact(Ev("e0", 4.0))).Reframes.Single();
        ReframeKeyframe a = r.Keyframes[1], b = r.Keyframes[2];

        Math.Sign(Math.Round(b.CenterX - a.CenterX, 6)).Should().Be(sx);
        Math.Sign(Math.Round(b.CenterY - a.CenterY, 6)).Should().Be(sy);
    }

    [Fact]
    public void Shake_is_deterministic_decays_and_stays_near_the_centre()
    {
        var cfg = Config([new EventMoveRequest("e0", EventMoveKind.Shake, EventMoveIntensity.Strong, EventMoveHold.Long)]);
        VideoReframe a = Run(cfg, Artifact(Ev("e0", 4.0))).Reframes.Single();
        VideoReframe b = Run(cfg, Artifact(Ev("e0", 4.0))).Reframes.Single();
        a.Keyframes.Should().Equal(b.Keyframes);

        VideoReframe other = Run(Config([new EventMoveRequest("e1", EventMoveKind.Shake, EventMoveIntensity.Strong, EventMoveHold.Long)]),
            Artifact(Ev("e1", 4.0))).Reframes.Single();
        other.Keyframes.Should().NotEqual(a.Keyframes, "the seed comes from the event");

        var shaking = a.Keyframes.Where(k => k.Zoom == EventMoveExpander.ShakeZoom).ToList();
        shaking.Should().OnlyContain(k => Math.Abs(k.CenterX - 0.5) <= EventMoveExpander.ShakeAmplitude(EventMoveIntensity.Strong) + 1e-9);
        double early = shaking.Take(3).Max(k => Math.Abs(k.CenterX - 0.5));
        double late = shaking.TakeLast(3).Max(k => Math.Abs(k.CenterX - 0.5));
        late.Should().BeLessThan(early + 1e-12);
        (shaking[1].AtSec - shaking[0].AtSec).Should().BeApproximately(1 / EventMoveExpander.ShakeHz, 1e-9);
    }

    [Fact]
    public void Unknown_unoffered_cut_away_and_insert_events_drop_with_a_reason()
    {
        VideoAnalysisArtifact art = Artifact(Ev("e0", 4.0), Ev("e1", 8.0), Ev("e2", 2.0, source: null)) with { OfferedEventIds = ["e0", "e1", "e2"] };
        var cfg = Config([new EventMoveRequest("e9"), new EventMoveRequest("e1"), new EventMoveRequest("e2"), new EventMoveRequest("e0")]);

        EventMoveExpansion e = Run(cfg, art, [(0, 3, 6)]);

        e.Report.Select(r => (r.EventId, r.Reason)).Should().Equal(
            ("e9", "unknown_anchor_id"), ("e1", "anchor_cut_away"), ("e2", "event_not_in_source"), ("e0", null));
        e.Reframes.Should().HaveCount(1);
        e.ToNode()["dropped"]!.AsArray().Should().HaveCount(3);
    }

    [Fact]
    public void An_event_not_in_the_offered_list_is_unknown_even_when_it_exists()
    {
        VideoAnalysisArtifact art = Artifact(Ev("e0", 4.0)) with { OfferedEventIds = [] };
        Run(Config([new EventMoveRequest("e0")]), art).Report.Single().Reason.Should().Be("unknown_anchor_id");
    }

    [Fact]
    public void Overlapping_moves_and_hand_reframes_drop_the_later_one()
    {
        VideoAnalysisArtifact art = Artifact(Ev("e0", 4.0), Ev("e1", 4.3), Ev("e2", 7.0));
        VideoReframe hand = new(0, 6.5, 7.5, [new ReframeKeyframe(0, 1.2), new ReframeKeyframe(1, 1.0)]);
        EventMoveExpansion e = Run(Config([new EventMoveRequest("e0"), new EventMoveRequest("e1"), new EventMoveRequest("e2")], hand: [hand]), art);

        e.Report.Select(r => r.Reason).Should().Equal(null, "overlaps_other_reframe", "overlaps_other_reframe");
        VideoReframe.Validate([hand, .. e.Reframes]).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Sixteen_moves_in_one_source_fit_the_shared_caps_and_a_seventeenth_reframe_does_not()
    {
        var events = Enumerable.Range(0, 16).Select(i => Ev("e" + i, 0.5 + i * 1.0)).ToArray();
        EventMoveExpansion e = Run(Config(events.Select(x => new EventMoveRequest(x.Id, EventMoveKind.PunchIn, EventMoveIntensity.Normal, EventMoveHold.Short)).ToList()),
            Artifact(events), [(0, 0, 30)]);

        e.Reframes.Should().HaveCount(16);
        VideoReframe.Validate(e.Reframes).IsValid.Should().BeTrue();
        VideoReframe.Validate([.. e.Reframes, new VideoReframe(0, 20, 21, [new ReframeKeyframe(0, 1.2)])]).ErrorCode.Should().Be("REFRAME_TOO_MANY_REFRAMES");
    }

    [Fact]
    public void A_move_on_a_source_a_follow_entry_owns_drops_instead_of_failing()
    {
        var cfg = Config([new EventMoveRequest("e0")]) with { FollowSubject = [new FollowSubjectRequest("r1", SourceIndex: 0)] };
        EventMoveExpansion e = Run(cfg, Artifact(Ev("e0", 4.0)));
        e.Report.Single().Reason.Should().Be("overlaps_other_reframe");
        e.Reframes.Should().BeEmpty();
    }
}
