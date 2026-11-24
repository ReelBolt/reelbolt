using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.Video.Dynamics;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class DynamicsCompileStageTests
{
    // 120 bpm: beat 0.5 s, bar 2 s.
    private static readonly MusicBeatAnalysis Grid = new(120, 0.5, 0, 0, 40, null, null, 1);
    private static readonly IReadOnlyList<string> Origin = ["x"];

    private static List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> Spans() =>
        [(0, 10, 0, Origin), (0, 10, 1, Origin)];

    private static DynamicsStageResult Run(
        VideoDynamicsConfig cfg, IReadOnlyList<DynamicsEntryOutput>? entries, MusicBeatAnalysis? grid = null, bool useGrid = true, bool autoFill = true) =>
        DynamicsCompileStage.Run(Spans(), cfg, true, entries, useGrid ? grid ?? Grid : null, "no_music_track",
            [(5.0, 0)],
            id => id switch { "a" => (0, 10, 0), "b" => (0, 10, 1), _ => null },
            _ => (30, 1), autoFill: autoFill);

    private static DynamicsEntryOutput Alt() => new()
    {
        Effect = "BeatAlternate",
        AnchorId = "a",
        OtherId = "b",
        Moment = "OnBeat",
        Beats = "Four",
        Division = "Beat",
        Winner = "B",
        Reason = "r"
    };

    [Fact]
    public void Reframes_the_compile_validation_dropped_are_reported_with_their_count_and_leave_no_applied_entry()
    {
        // QA-DYN-23: the compile drops every dynamics reframe when the merged list is invalid; the report must not keep calling them applied.
        DynamicsStageResult r = Run(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false), [], autoFill: true);
        int punches = r.Report["applied"]!.AsArray().Count(a => a!["effect"]!.GetValue<string>() == "PunchIn");
        punches.Should().BeGreaterThan(3);
        int planned = r.Reframes.Count;
        int appliedBefore = r.Report["counts"]!["applied"]!.GetValue<int>();

        // Nothing lost: the report is untouched.
        DynamicsCompileStage.ReconcileDroppedReframes(r.Report, planned, planned).Should().Be(0);
        r.Report["counts"]!["applied"]!.GetValue<int>().Should().Be(appliedBefore);

        int lost = DynamicsCompileStage.ReconcileDroppedReframes(r.Report, planned, 0);
        lost.Should().Be(planned);
        r.Report["applied"]!.AsArray().Should().NotContain(a => a!["effect"]!.GetValue<string>() == "PunchIn", "a dropped punch is not applied");
        r.Report["dropped"]!.AsArray().Count(d => d!["reason"]!.GetValue<string>() == DynamicsCompileStage.ReframesDroppedCode).Should().Be(punches);
        r.Report["counts"]!["reframesDropped"]!.GetValue<int>().Should().Be(planned);
        r.Report["counts"]!["reframes"]!.GetValue<int>().Should().Be(0);
        r.Report["counts"]!["applied"]!.GetValue<int>().Should().Be(appliedBefore - punches);
        r.Report["reframesDropped"]!.GetValue<int>().Should().Be(planned);
        DynamicsCompileStage.ReframesDroppedCode.Should().Be("dynamics_reframes_dropped");
    }

    [Fact]
    public void Without_a_beat_grid_the_cut_is_unchanged_and_reports_it()
    {
        DynamicsStageResult r = Run(new VideoDynamicsConfig(), [Alt()], useGrid: false);
        r.Spans.Should().HaveCount(2);
        r.Warnings.Should().Contain(w => w.Code == "dynamics_no_beat_grid");
        r.Report["dropped"]!.AsArray().Single()!["reason"]!.GetValue<string>().Should().Be("dynamics_no_beat_grid");
    }

    [Fact]
    public void BeatAlternate_alternates_cells_then_holds_the_winner_and_keeps_length()
    {
        DynamicsStageResult r = Run(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false), [Alt()], autoFill: false);
        r.Report["counts"]!["applied"]!.GetValue<int>().Should().Be(1);
        // first four cells A,B,A,B of 0.5 s on source 0/1, then B holds to the end of the anchor window.
        r.Spans.Take(5).Select(s => s.SourceIndex).Should().Equal(0, 1, 0, 1, 1);
        r.Spans.Take(4).Select(s => Math.Round(s.End - s.Start, 3)).Should().OnlyContain(d => Math.Abs(d - 0.5) <= 1.0 / 30);
        r.Spans.Sum(s => s.End - s.Start).Should().BeApproximately(20, 0.1);
    }

    [Fact]
    public void BeatAlternate_can_cut_to_offered_footage_the_story_did_not_keep_without_lengthening_the_program()
    {
        DynamicsStageResult r = DynamicsCompileStage.Run(
            [(0, 10, 0, Origin)], new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false), true, [Alt()], Grid, null, [],
            id => id switch { "a" => (0, 10, 0), "b" => (0, 10, 1), _ => null }, _ => (30, 1));
        r.Report["dropped"]!.AsArray().Should().BeEmpty();
        r.Spans.Select(s => s.SourceIndex).Take(5).Should().Equal(0, 1, 0, 1, 1);
        r.Spans.Sum(s => s.End - s.Start).Should().BeApproximately(10, 0.1);
    }

    [Fact]
    public void Ids_that_are_not_offered_shots_are_the_generated_clips_an_alternation_names()
    {
        var entries = new List<DynamicsEntryOutput>
        {
            new() { Effect = "BeatAlternate", AnchorId = "s1", OtherId = "v3t1" },
            new() { Effect = "strobecut", AnchorId = "s2", OtherId = " v3t1 " },
            new() { Effect = "BeatAlternate", AnchorId = "s1", OtherId = "s4" },
            new() { Effect = "PunchIn", AnchorId = "s1", OtherId = "v9t9" },
            new() { Effect = "StrobeCut", AnchorId = "s3", OtherId = "v2t1" }
        };
        VideoCompileStepExecutor.AlternationClipIds(entries, id => id.StartsWith('s')).Should().Equal("v3t1", "v2t1");
        VideoCompileStepExecutor.AlternationClipIds(null, _ => false).Should().BeEmpty();
    }

    [Fact]
    public void An_event_moment_in_source_time_is_mapped_onto_the_program_and_owned()
    {
        // Source 0, 4.0 s: an event move of the config sits there; the agent's flash on the same bar is dropped, not doubled.
        var flash = new DynamicsEntryOutput { Effect = "FlashFrame", AnchorId = "a", Moment = "OnBar", Reason = "r" };
        DynamicsStageResult free = Run(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false), [flash], autoFill: false);
        free.Flashes!.Sum(f => f.Count).Should().Be(1);
        int frame = free.Flashes.SelectMany((f, i) => f.Select(x => x.Frame)).Single();
        double sourceSec = frame / 30.0; // the first span starts at source 0, so program frame = source frame

        DynamicsStageResult owned = DynamicsCompileStage.Run(Spans(), new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false), true, [flash], Grid, null,
            [(5.0, 0)], id => id switch { "a" => (0, 10, 0), "b" => (0, 10, 1), _ => null }, _ => (30, 1), autoFill: false,
            ownedMoments: [(0, sourceSec)]);
        owned.Flashes!.Sum(f => f.Count).Should().Be(0);
        owned.Report["dropped"]!.AsArray().Single()!["reason"]!.GetValue<string>().Should().Be("event_move_owns_moment");
    }

    [Fact]
    public void Event_features_of_the_config_own_their_source_events_only()
    {
        var artifact = new VideoAnalysisArtifact(
            Version: 1, Media: new VideoAnalysisMedia(10, 30, 1, 320, 180), Shots: [], SilenceSpans: [], Segments: [], Words: [], OfferedIds: [],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            RenderEvents:
            [
                new VideoAnalysisRenderEvent("e1", "Hit", "x", 60, 2.0, SourceIndex: 0),
                new VideoAnalysisRenderEvent("e2", "Hit", "y", 90, 3.0, InsertContentStorageKey: "k")
            ]);
        var config = new VideoCompileStepConfig(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1)
        {
            EventMoves = [new EventMoveRequest("e1")],
            FlashCues = [new FlashCueRequest("e1"), new FlashCueRequest("e2")],
            SpeedRamps = [new SpeedRampRequest("e9")]
        };
        VideoCompileStepExecutor.EventOwnedMoments(config, artifact).Should().Equal([(0, 2.0)]);
        VideoCompileStepExecutor.EventOwnedMoments(config with { EventMoves = null, FlashCues = null, SpeedRamps = null }, artifact).Should().BeEmpty();
    }

    [Fact]
    public void The_segment_cap_applies_again_after_the_stage_with_every_parallel_list_cut_to_match()
    {
        DynamicsStageResult r = Run(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false), [Alt()], autoFill: false);
        r.Spans.Count.Should().BeGreaterThan(3);
        DynamicsStageResult capped = DynamicsCompileStage.CapSegments(r, 3);
        capped.Spans.Should().HaveCount(3);
        capped.Speeds.Should().HaveCount(3);
        capped.Flashes.Should().HaveCount(3);
        capped.Report["segmentsDroppedOverCap"]!.GetValue<int>().Should().Be(r.Spans.Count - 3);
        capped.Report["counts"]!["segments"]!.GetValue<int>().Should().Be(3);
        capped.Warnings.Should().Contain(w => w.Code == "dynamics_segment_cap");
        DynamicsCompileStage.CapSegments(r, 500).Should().BeSameAs(r);
    }

    [Fact]
    public void Only_the_first_thirty_two_agent_entries_are_read()
    {
        var flash = new DynamicsEntryOutput { Effect = "PunchIn", AnchorId = "a", Moment = "OnBeat", Reason = "r" };
        DynamicsStageResult r = Run(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false), Enumerable.Repeat(flash, 200).ToList(), autoFill: false);
        (r.Report["counts"]!["applied"]!.GetValue<int>() + r.Report["counts"]!["dropped"]!.GetValue<int>()).Should().BeLessThanOrEqualTo(DynamicsCompileStage.MaxAgentEntries);
    }

    [Fact]
    public void Shot_boundaries_inside_a_span_stay_cut_points_when_asked()
    {
        Run(new VideoDynamicsConfig(Pace: DynamicPace.Calm), []).Spans.Count(s => s.SourceIndex == 0).Should().BeGreaterThan(1);
        Run(new VideoDynamicsConfig(Pace: DynamicPace.Calm, KeepSceneBoundaries: false), []).Spans.Count(s => s.SourceIndex == 0).Should().Be(1);
    }

    [Fact]
    public void FlashFrame_lands_on_the_span_that_holds_the_grid_frame_as_a_local_frame()
    {
        var flash = new DynamicsEntryOutput { Effect = "FlashFrame", AnchorId = "a", Moment = "OnBeat", Reason = "r" };
        DynamicsStageResult r = Run(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false), [flash]);
        r.Report["dropped"]!.AsArray().Should().BeEmpty();
        r.Report["counts"]!["flashes"]!.GetValue<int>().Should().Be(1);
        var hit = r.Flashes!.Select((f, i) => (f, i)).Single(t => t.f.Count > 0);
        hit.f.Should().ContainSingle().Which.Color.Should().Be("white");
        hit.f[0].Frame.Should().BeGreaterThanOrEqualTo(0);
        r.Flashes!.Sum(f => f.Count).Should().Be(1);
    }

    [Fact]
    public void SpeedRamp_retimes_a_muted_beat_and_the_program_length_stays_the_planned_frame_count()
    {
        var ramp = new DynamicsEntryOutput { Effect = "SpeedRamp", AnchorId = "a", Moment = "OnDrop", Intensity = "Normal", Reason = "r" };
        DynamicsStageResult r = Run(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false), [ramp], new MusicBeatAnalysis(120, 0.5, 0, 0, 40, 5.0, null, 1));
        r.Report["counts"]!["retimes"]!.GetValue<int>().Should().Be(1);
        int i = r.Speeds!.ToList().FindIndex(v => v != 1.0);
        r.Speeds![i].Should().BeLessThan(1.0);
        // The retimed window lasts exactly one beat (15 frames at 30 fps) in the program.
        ((r.Spans[i].End - r.Spans[i].Start) / r.Speeds[i]).Should().BeApproximately(0.5, 1e-9);
        // The whole program keeps the anchors' total length.
        r.Spans.Select((s, k) => (s.End - s.Start) / r.Speeds![k]).Sum().Should().BeApproximately(20, 0.1);
    }

    [Fact]
    public void WhipOnBar_maps_to_the_span_after_the_seam_with_a_length_from_the_grid_cell()
    {
        var whip = new DynamicsEntryOutput { Effect = "WhipOnBar", AnchorId = "b", Moment = "OnBar", Reason = "r" };
        DynamicsStageResult r = Run(new VideoDynamicsConfig(Pace: DynamicPace.Calm, KeepSceneBoundaries: false), [whip]);
        r.Report["dropped"]!.AsArray().Should().BeEmpty();
        DynamicsWhip w = r.Whips!.Should().ContainSingle().Subject;
        w.SpanIndex.Should().BeGreaterThan(0);
        w.Word.Should().BeOneOf("WhipLeft", "WhipRight");
        w.Frames.Should().Be((int)Math.Round(0.5 / 8 * 30), "one eighth of a beat per side, whole frames");
        r.Report["counts"]!["whips"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void WhipOnBar_on_spans_that_are_not_whole_frames_still_lands_on_the_cut()
    {
        // Real material: beat-synced spans have fractional frame lengths. 1.0166 s is 30.5 frames (31 in the program), so the cumulative
        // seconds (2.0332 s = 61 frames) sit one frame off the cumulative whole frames (62): the cut handed to the planner must not.
        var spans = Enumerable.Range(0, 5).Select(i => (0.0, 1.0166, i, (IReadOnlyList<string>)Origin)).ToList();
        DynamicsStageResult Whip(string anchor) => DynamicsCompileStage.Run(
            spans, new VideoDynamicsConfig(Pace: DynamicPace.Calm, KeepSceneBoundaries: false), true,
            [new DynamicsEntryOutput { Effect = "WhipOnBar", AnchorId = anchor, Moment = "OnBar", Reason = "r" }], Grid, null,
            [], id => id.StartsWith('x') ? (0, 1.0166, int.Parse(id[1..])) : null, _ => (30, 1));
        // The seam at whole frame 62 (start of x2) is on the bar line (60); before the fix the planner saw a cut at 61 inside x1 and
        // proposed a seam there, which is not a seam of the program ("whip_not_at_cut").
        DynamicsStageResult atCut = Whip("x2");
        atCut.Report["dropped"]!.AsArray().Should().BeEmpty();
        atCut.Whips.Should().ContainSingle().Which.SpanIndex.Should().Be(2);
        Whip("x1").Report["dropped"]!.AsArray().Select(d => d!["reason"]!.GetValue<string>()).Should().NotContain("whip_not_at_cut");
    }

    [Fact]
    public void Auto_cadence_gives_punch_reframes_and_agent_ids_resolve_to_spans()
    {
        DynamicsStageResult auto = Run(new VideoDynamicsConfig(Pace: DynamicPace.Fast), null);
        auto.Reframes.Should().NotBeEmpty();
        auto.Report["paceUsed"]!.GetValue<string>().Should().Be("Fast");
        auto.Report["maxBeatOffsetMs"]!.GetValue<double>().Should().BeLessThan(17);
    }

    [Fact]
    public void Plan_revision_suffix_only_when_dynamics_is_enabled()
    {
        string off = DeterministicStepRevisions.For(StepType.VideoCompile, "{\"beatSync\":\"Off\"}");
        DeterministicStepRevisions.For(StepType.VideoCompile, "{\"dynamics\":{\"enabled\":true}}").Should().Be(off + "+dynamics-1+dynamics-2+beat-grid-read-1");
        DeterministicStepRevisions.For(StepType.VideoCompile, "{\"dynamics\":null}").Should().Be(off);
        DeterministicStepRevisions.For(StepType.VideoCompile, "{\"dynamics\":{\"enabled\":false}}").Should().Be(off);
    }
}
