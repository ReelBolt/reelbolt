#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// "Beat sync": cuts snap to the beat or bar of the music actually used, by trimming the tail of
/// each kept span, and the cut nearest the drop lands on it.
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class BeatSyncTests
{
    /// <summary>A 16-bit mono RIFF/WAVE of the given samples.</summary>
    internal static byte[] Wav(float[] samples, int rate = 16000)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(samples.Length * 2);
        foreach (float s in samples)
            w.Write((short)Math.Clamp(s * 32767, short.MinValue, short.MaxValue));
        return ms.ToArray();
    }

    // 120 BPM from t = 0: beats every 0.5 s, bars every 2 s.
    private static readonly List<double> Beats = Enumerable.Range(0, 40).Select(i => i * 0.5).ToList();
    private static readonly List<double> Bars = Enumerable.Range(0, 10).Select(i => i * 2.0).ToList();

    [Fact]
    public void Bar_mode_trims_each_tail_back_to_a_bar_and_never_extends_a_span()
    {
        (List<double> durations, List<BeatSyncCut> cuts, bool drop) =
            BeatSyncPlanner.Plan([2.3, 2.9, 3.2], Beats, Bars, 0.5, dropSec: null, useBars: true);

        durations.Should().Equal(2.0, 2.0, 2.0);
        cuts.Select(c => (c.OutputEndSec, c.SnappedEndSec, c.Grid)).Should().Equal((2.3, 2.0, "bar"), (4.9, 4.0, "bar"), (7.2, 6.0, "bar"));
        cuts.All(c => c.SnappedEndSec <= c.OutputEndSec).Should().BeTrue();
        drop.Should().BeFalse();
    }

    [Fact]
    public void A_span_is_never_cut_shorter_than_the_narration_spoken_over_it()
    {
        // Observed live: Bar mode trimmed 3.75 s off the tails of a narrated edit and 5 of its 10
        // lines ran onto the next shot. The first span carries a line needing 2.65 s: the bar at
        // 2.0 would cut it mid-word, and nothing on the grid fits before the span's own end, so it
        // stays whole. The second span's line needs only 1.25 s of its 2.9, so it still snaps.
        (List<double> durations, List<BeatSyncCut> cuts, _) =
            BeatSyncPlanner.Plan([2.3, 2.9, 3.2], Beats, Bars, 0.5, dropSec: null, useBars: true, minDurations: [2.65, 1.25, 0]);

        cuts[0].Grid.Should().Be("none", "no bar or beat at or after 2.65 s falls inside a 2.3 s span");
        durations[0].Should().BeApproximately(2.3, 1e-9);
        durations[1].Should().BeGreaterThanOrEqualTo(1.25);
        cuts[1].Grid.Should().Be("bar");
        cuts[1].SnappedEndSec.Should().Be(4.0);

        // No narration: identical to the narration-blind planner.
        BeatSyncPlanner.Plan([2.3, 2.9, 3.2], Beats, Bars, 0.5, null, true, minDurations: [0, 0, 0]).Durations
            .Should().Equal(BeatSyncPlanner.Plan([2.3, 2.9, 3.2], Beats, Bars, 0.5, null, true).Durations);
    }

    [Fact]
    public void Narration_floors_are_read_from_the_voiceover_steps_lines()
    {
        // Two kept spans from one clip: s0 [0,4) and s2 [10,15). vo0 (2.0 s) is anchored to s0, vo1
        // (1.5 s) to s1 at 5 s — outside both kept spans, so it protects nothing — and vo2 (3.0 s)
        // to s2, which starts 1 s into the second span (s2 at 11 s, span from 10 s).
        var artifact = new VideoAnalysisArtifact(
            Version: 2,
            Media: new VideoAnalysisMedia(20, 30, 1, 1920, 1080),
            Shots: [new VideoAnalysisShot("s0", 0, 5), new VideoAnalysisShot("s1", 5, 11), new VideoAnalysisShot("s2", 11, 20)],
            SilenceSpans: [], Segments: [], Words: [], OfferedIds: ["s0", "s1", "s2"],
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false));
        string voiceover = """
            {"view":{"lines":[
              {"id":"vo0","status":"ok","durationSec":2.0,"anchorId":"s0"},
              {"id":"vo1","status":"ok","durationSec":1.5,"anchorId":"s1"},
              {"id":"vo2","status":"ok","durationSec":3.0,"anchorId":"s2"},
              {"id":"vo3","status":"failed","anchorId":"s2"}]}}
            """;
        var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 5, StepType = StepType.VideoCompile };
        var context = new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = "",
            CurrentStepIndex = 4,
            IterationCount = 0,
            CorrelationId = "t",
            StepOutputHistory = [new StepOutputHistoryEntry(4, "Record the narration", voiceover)]
        };
        var config = new VideoCompileStepConfig(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Previous), AnalysisStepOrder: 1) with
        {
            EnableVoiceover = true,
            VoiceoverStepOrder = 4
        };
        IReadOnlyList<string> none = [];

        IReadOnlyList<double> floors = ReelBolt.WorkflowEngine.Execution.StepExecutors.VideoCompileStepExecutor.NarrationSpanFloors(
            context, config, artifact, [(0.0, 4.0, 0, none), (10.0, 15.0, 0, none)]);

        floors[0].Should().BeApproximately(2.0 + 0.25, 1e-9);
        floors[1].Should().BeApproximately(1.0 + 3.0 + 0.25, 1e-9);
    }

    [Fact]
    public void Beat_mode_uses_every_beat_and_a_span_shorter_than_a_beat_is_kept_whole()
    {
        (List<double> durations, List<BeatSyncCut> cuts, _) =
            BeatSyncPlanner.Plan([1.3, 0.3, 1.2], Beats, Bars, 0.5, dropSec: null, useBars: false);

        durations[0].Should().BeApproximately(1.0, 1e-9);
        cuts[1].Grid.Should().Be("none", "0.3 s cannot keep a whole beat");
        durations[1].Should().BeApproximately(0.3, 1e-9);
        // The next cut re-aligns from 1.3: its end 2.5 is already on a beat.
        cuts[2].SnappedEndSec.Should().Be(2.5);
    }

    [Fact]
    public void Bar_mode_falls_back_to_a_beat_when_no_bar_fits()
    {
        (_, List<BeatSyncCut> cuts, _) = BeatSyncPlanner.Plan([1.3], Beats, Bars, 0.5, null, useBars: true);
        cuts[0].Grid.Should().Be("beat");
        cuts[0].SnappedEndSec.Should().Be(1.0);
    }

    [Fact]
    public void The_cut_that_crosses_the_drop_lands_on_it_when_the_intro_is_long_enough()
    {
        (List<double> durations, List<BeatSyncCut> cuts, bool aligned) =
            BeatSyncPlanner.Plan([2.3, 2.9, 3.2], Beats, Bars, 0.5, dropSec: 4.5, useBars: true);

        aligned.Should().BeTrue();
        cuts[1].Grid.Should().Be("drop");
        cuts[1].SnappedEndSec.Should().Be(4.5);
        durations.Take(2).Sum().Should().BeApproximately(4.5, 1e-9);

        BeatSyncPlanner.Plan([2.3, 2.9], Beats, Bars, 0.5, dropSec: 1.0, useBars: true).DropAligned
            .Should().BeFalse("an intro shorter than two bars is not worth a forced cut");
    }

    [Fact]
    public void A_drop_far_before_the_end_of_a_single_long_segment_is_not_snapped_to()
    {
        // Run ec066f71: 140 BPM, one coalesced 19.82 s segment, drop at 6.884 s. The end was cut on the
        // drop, throwing away 12.9 s (65%) of the film. A snap may remove at most one bar (1.7143 s).
        double beat = 60.0 / 140;
        List<double> beats = Enumerable.Range(0, 120).Select(i => Math.Round(i * beat, 4)).ToList();
        List<double> bars = Enumerable.Range(0, 30).Select(i => Math.Round(i * 4 * beat, 4)).ToList();

        (List<double> durations, List<BeatSyncCut> cuts, bool aligned) =
            BeatSyncPlanner.Plan([19.8198], beats, bars, beat, dropSec: 6.884, useBars: false);

        aligned.Should().BeFalse();
        cuts[0].Grid.Should().NotBe("drop");
        cuts[0].TrimmedSec.Should().BeLessThanOrEqualTo(4 * beat + 1e-6);
        durations.Sum().Should().BeGreaterThan(19.8198 - 4 * beat - 1e-6);
    }

    [Fact]
    public void A_drop_within_a_bar_of_the_segment_end_is_still_cut_on()
    {
        double beat = 60.0 / 140;
        List<double> beats = Enumerable.Range(0, 120).Select(i => Math.Round(i * beat, 4)).ToList();
        List<double> bars = Enumerable.Range(0, 30).Select(i => Math.Round(i * 4 * beat, 4)).ToList();

        (_, List<BeatSyncCut> cuts, bool aligned) =
            BeatSyncPlanner.Plan([8.09], beats, bars, beat, dropSec: 6.884, useBars: false);

        aligned.Should().BeTrue();
        cuts[0].Grid.Should().Be("drop");
    }

    [Fact]
    public void After_a_length_fit_cuts_aim_at_the_fitted_length_using_the_footage_the_fit_trimmed()
    {
        // Run a60d5aae step 8: the fit brought 8.4 s down to 7 s, then every cut moved EARLIER to the
        // previous beat and the film came out 5.5 s. With the pre-fit lengths, each cut lands on the
        // beat nearest where the fitted program put it, inside the span's own footage.
        double[] fitted = [1.9, 1.9, 1.9, 1.3];
        double[] footage = [2.4, 2.4, 2.4, 1.3];

        BeatSyncPlanner.Plan(fitted, Beats, Bars, 0.5, null, useBars: false).Durations.Sum()
            .Should().BeApproximately(5.5, 1e-9, "the old rule: every cut only moves earlier");

        (List<double> durations, List<BeatSyncCut> cuts, _) =
            BeatSyncPlanner.Plan(fitted, Beats, Bars, 0.5, null, useBars: false, maxDurations: footage);

        cuts.Select(c => c.SnappedEndSec).Should().Equal(2.0, 4.0, 5.5, 6.5);
        cuts.Select(c => c.Grid).Should().AllBe("beat");
        durations.Sum().Should().BeApproximately(7.0, 0.5, "within one beat of the fitted length");
        for (int i = 0; i < durations.Count; i++)
        {
            durations[i].Should().BeLessThanOrEqualTo(footage[i] + 1e-9, "never past the span's own footage");
            durations[i].Should().BeGreaterThanOrEqualTo(0.5 - 1e-9);
        }

        cuts[0].TrimmedSec.Should().BeNegative("the first cut moved later, into footage the fit had trimmed");
    }

    [Fact]
    public void Footage_equal_to_the_span_lengths_plans_exactly_as_before()
    {
        foreach ((double[] d, bool bars, double? drop) in new (double[], bool, double?)[]
                 {
                     ([2.3, 2.9, 3.2], true, null), ([1.3, 0.3, 1.2], false, null), ([2.3, 2.9, 3.2], true, 4.5), ([1.3], true, null)
                 })
        {
            var before = BeatSyncPlanner.Plan(d, Beats, Bars, 0.5, drop, bars);
            var after = BeatSyncPlanner.Plan(d, Beats, Bars, 0.5, drop, bars, maxDurations: d);
            after.Durations.Should().Equal(before.Durations);
            after.Cuts.Should().Equal(before.Cuts);
            after.DropAligned.Should().Be(before.DropAligned);
        }
    }

    [Fact]
    public void A_looped_track_repeats_its_grid()
    {
        BeatSyncPlanner.LoopedGrid([0.25, 1.25, 2.25], trackSec: 3, untilSec: 7).Should().Equal(0.25, 1.25, 2.25, 3.25, 4.25, 5.25, 6.25);
    }

    // ---- Compile ------------------------------------------------------------------------------

    private static readonly Guid TrackId = Guid.NewGuid();

    private static ReelCompileFixture Fixture(VideoAnalysisMusicTrack? measured, VideoBeatSync mode = VideoBeatSync.Bar)
    {
        // s0 [0,2.5) · s1 [2.5,3.5) · s2 [3.5,6.5) · s3 [6.5,7.5) · s4 [7.5,11)
        VideoAnalysisArtifact artifact = ReelCompileFixture.ShotsArtifact(2.5, 1.0, 3.0, 1.0, 3.5) with { Music = measured };
        var fixture = new ReelCompileFixture(artifact);
        fixture.ProjectFiles.Add(new ProjectWorkspaceFile(TrackId, VideoCompileReelFeatureTests.ProjectId, "the-last-point.mp3", null, "userFiles",
            $"projects/{VideoCompileReelFeatureTests.ProjectId}/userFiles/the-last-point.mp3", "audio/mpeg", 100, DateTime.UnixEpoch, null));
        fixture.Config = c => c with
        {
            EnableMusic = true,
            MusicTrackProjectFileId = TrackId,
            BeatSync = mode,
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };
        return fixture;
    }

    [Fact]
    public async Task Cuts_land_on_the_bars_and_the_drop_of_the_measured_track()
    {
        var facts = new VideoAnalysisMusicBeat(120, 0.5, 2.0, 0, 0, 60, DropSec: 4.0, OutroSec: 40, Confidence: 0.9, Tempo: "medium");
        ReelCompileFixture fixture = Fixture(new VideoAnalysisMusicTrack(TrackId, "the-last-point.mp3", facts));

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2", "s4"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        // Program cuts at 2.5, 5.0, 7.5 become 2.0 (bar), 4.0 (the drop), 6.0 (bar): each kept span
        // loses only its tail — s0 [0,2) · s2 [3.5,5.5) · s4 [7.5,9.5).
        fixture.FilterGraph.Should().StartWith("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=9.566667,select='gte(t,0)*lt(t,2)+gte(t,3.5)*lt(t,5.5)+gte(t,7.5)*lt(t,9.5)'");
        JsonObject sync = fixture.Output!["beatSync"]!.AsObject();
        sync["applied"]!.GetValue<bool>().Should().BeTrue();
        sync["gridSource"]!.GetValue<string>().Should().Be("analysis");
        sync["dropAligned"]!.GetValue<bool>().Should().BeTrue();
        sync["cuts"]!.AsArray().Select(c => c!["grid"]!.GetValue<string>()).Should().Equal("bar", "drop", "bar");
        fixture.Edl!["beatSync"]!["cuts"]!.AsArray().Should().HaveCount(3);
        fixture.Output!["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(6.0, 1e-9);
    }

    [Fact]
    public async Task With_no_measurement_the_track_is_decoded_and_measured_here()
    {
        ReelCompileFixture fixture = Fixture(measured: null, VideoBeatSync.Beat);
        byte[] wav = Wav(MusicBeatAnalyzerTests.ClickTrack(120, 0.0, 20));
        fixture.FfmpegHook = args =>
        {
            if (args[^1].EndsWith("beat-sync-music.wav", StringComparison.Ordinal))
            {
                File.WriteAllBytes(args[^1], wav);
                return new VideoToolResult(0, "", "", false);
            }

            return null;
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2", "s4"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject sync = fixture.Output!["beatSync"]!.AsObject();
        sync["gridSource"]!.GetValue<string>().Should().Be("measured");
        sync["bpm"]!.GetValue<double>().Should().BeApproximately(120, 0.5);
        sync["cuts"]!.AsArray().Select(c => c!["grid"]!.GetValue<string>()).Should().AllBe("beat");
    }

    [Fact]
    public async Task Without_music_nothing_moves_and_the_reason_is_reported()
    {
        ReelCompileFixture fixture = Fixture(measured: null);
        fixture.Config = c => c with { BeatSync = VideoBeatSync.Beat, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Output!["beatSync"]!["reason"]!.GetValue<string>().Should().Be("music_not_enabled");
        fixture.FilterGraph.Should().StartWith("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=6.566667,select='gte(t,0)*lt(t,2.5)+gte(t,3.5)*lt(t,6.5)'");
    }

    [Fact]
    public async Task Narration_over_muted_clips_ducks_the_music_under_each_line_and_lifts_it_between()
    {
        ReelCompileFixture fixture = Fixture(measured: null, VideoBeatSync.Off);
        fixture.Voiceover = ReelCompileFixture.VoiceoverJson(("vo0", 1.0, 2.0, null, "One line."), ("vo1", 7.0, 2.0, null, "Another."));
        Func<VideoCompileStepConfig, VideoCompileStepConfig> music = fixture.Config;
        fixture.Config = c => music(c) with
        {
            MuteSourceAudio = true,
            EnableVoiceover = true,
            VoiceoverStepOrder = VideoCompileReelFeatureTests.VoiceoverStepOrder
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2", "s4"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject m = fixture.Edl!["music"]!.AsObject();
        m["duckBasis"]!.GetValue<string>().Should().Be("narration");
        m["bedBasis"]!.GetValue<string>().Should().Be("narration_gaps");
        m["bedDbfs"]!.GetValue<int>().Should().Be(-20 + ReelBolt.WorkflowEngine.Execution.StepExecutors.VideoCompileStepExecutor.NarrationGapLiftDb);
        m["duckedDbfs"]!.GetValue<int>().Should().Be(-20 + ReelBolt.WorkflowEngine.Execution.StepExecutors.VideoCompileStepExecutor.NarrationGapLiftDb - 11);
        m["liftWindows"]!.GetValue<int>().Should().BeGreaterThan(0, "the music comes back up between the lines");
        // The clips are muted, so there is no dialogue for the narration to be buried under.
        fixture.Edl!["voiceover"]!["headroom"]!["reason"]!.GetValue<string>().Should().Be("no_dialogue_audio_in_output");
    }

    [Fact]
    public async Task A_target_length_survives_cutting_to_the_beat()
    {
        // s0 2.5 s · s2 3.0 s · s4 3.5 s = 9 s, asked for 7 s at 120 BPM (beat 0.5 s), no drop. The fit
        // gives [1.75, 1.75, 3.5]; trimming every cut back to the previous beat gave 6.5 s. Aiming each
        // cut at the fitted program time, inside the footage the fit trimmed, lands on 7 s exactly.
        var facts = new VideoAnalysisMusicBeat(120, 0.5, 2.0, 0, 0, 60, DropSec: null, OutroSec: null, Confidence: 0.9, Tempo: "medium");
        ReelCompileFixture fixture = Fixture(new VideoAnalysisMusicTrack(TrackId, "the-last-point.mp3", facts), VideoBeatSync.Beat);
        Func<VideoCompileStepConfig, VideoCompileStepConfig> baseConfig = fixture.Config;
        fixture.Config = c => baseConfig(c) with { TargetDurationSec = 7 };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2", "s4"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Output!["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(7.0, 1e-9);
        fixture.Output!["length"]!["met"]!.GetValue<bool>().Should().BeTrue();
        fixture.Output!["length"]!["fitted"]!["editedSec"]!.GetValue<double>().Should().Be(9.0);
        JsonObject sync = fixture.Output!["beatSync"]!.AsObject();
        sync["targetLength"]!.GetValue<bool>().Should().BeTrue();
        sync["cuts"]!.AsArray().Select(c => c!["snappedEndSec"]!.GetValue<double>()).Should().Equal(1.5, 3.5, 7.0);
        // Every span stays inside the footage the editor chose: s0 [0,1.5) · s2 [3.5,5.5) · s4 [7.5,11).
        fixture.FilterGraph.Should().Contain("select='gte(t,0)*lt(t,1.5)+gte(t,3.5)*lt(t,5.5)+gte(t,7.5)*lt(t,11)'");
    }

    [Fact]
    public async Task A_cut_snapped_to_the_beat_stays_on_the_beat_through_speech_safe_cuts()
    {
        // Run 21bb6f46: the plan said 4.74 / 17.598 s but the render cut at 4.938 / 17.885 s because the
        // speech-safe pass ran after the snap and moved three cuts off the grid by up to 289 ms. Here a
        // word spans the snapped cut (2.5 s, a beat at 120 BPM): the cut must still land on the beat.
        var facts = new VideoAnalysisMusicBeat(120, 0.5, 2.0, 0, 0, 60, DropSec: null, OutroSec: null, Confidence: 0.9, Tempo: "medium");
        VideoAnalysisArtifact artifact = ReelCompileFixture.ShotsArtifact(2.7, 1.0, 3.0, 1.0, 3.5) with
        {
            Music = new VideoAnalysisMusicTrack(TrackId, "the-last-point.mp3", facts),
            Words = [new VideoAnalysisWord("w0", 2.3, 2.9, "hello")]
        };
        var fixture = new ReelCompileFixture(artifact);
        fixture.ProjectFiles.Add(new ProjectWorkspaceFile(TrackId, VideoCompileReelFeatureTests.ProjectId, "the-last-point.mp3", null, "userFiles",
            $"projects/{VideoCompileReelFeatureTests.ProjectId}/userFiles/the-last-point.mp3", "audio/mpeg", 100, DateTime.UnixEpoch, null));
        fixture.Config = c => c with
        {
            EnableMusic = true,
            MusicTrackProjectFileId = TrackId,
            BeatSync = VideoBeatSync.Beat,
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2", "s4"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain("select='gte(t,0)*lt(t,2.5)+");
        JsonObject sync = fixture.Output!["beatSync"]!.AsObject();
        sync["maxOffsetMs"]!.GetValue<double>().Should().BeLessThan(1000.0 / 30);
    }

    [Fact]
    public async Task A_beat_synced_program_with_auto_transitions_keeps_every_cut_on_the_beat()
    {
        var facts = new VideoAnalysisMusicBeat(120, 0.5, 2.0, 0, 0, 60, DropSec: null, OutroSec: null, Confidence: 0.9, Tempo: "medium");
        VideoAnalysisArtifact artifact = ReelCompileFixture.ShotsArtifact(2.7, 1.0, 3.0, 1.0, 3.5) with
        {
            Music = new VideoAnalysisMusicTrack(TrackId, "the-last-point.mp3", facts)
        };
        var fixture = new ReelCompileFixture(artifact);
        fixture.ProjectFiles.Add(new ProjectWorkspaceFile(TrackId, VideoCompileReelFeatureTests.ProjectId, "the-last-point.mp3", null, "userFiles",
            $"projects/{VideoCompileReelFeatureTests.ProjectId}/userFiles/the-last-point.mp3", "audio/mpeg", 100, DateTime.UnixEpoch, null));
        fixture.Config = c => c with
        {
            EnableMusic = true,
            MusicTrackProjectFileId = TrackId,
            BeatSync = VideoBeatSync.Beat,
            TransitionPolicy = VideoTransitionPolicy.Auto,
            MaxTransitionRatioPct = 100,
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2", "s4"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Output!["beatSync"]!["maxOffsetMs"]!.GetValue<double>().Should().BeLessThan(1000.0 / 30, fixture.Output!["transitions"]?.ToJsonString());
    }

    [Fact]
    public async Task A_failed_loudness_measurement_falls_back_to_a_true_peak_limiter_with_a_warning()
    {
        var facts = new VideoAnalysisMusicBeat(120, 0.5, 2.0, 0, 0, 60, DropSec: null, OutroSec: null, Confidence: 0.9, Tempo: "medium");
        ReelCompileFixture fixture = Fixture(new VideoAnalysisMusicTrack(TrackId, "the-last-point.mp3", facts), VideoBeatSync.Off);
        var calls = new List<string>();
        fixture.FfmpegHook = args =>
        {
            calls.Add(string.Join(' ', args));
            return args.Any(a => a.Contains("print_format=json", StringComparison.Ordinal))
                ? new VideoToolResult(1, string.Empty, "boom", false)
                : null;
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2", "s4"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject loudness = fixture.Output!["loudness"]!.AsObject();
        loudness["reason"]!.GetValue<string>().Should().Be("measure_failed");
        loudness["warning"]!.GetValue<string>().Should().Contain("fallback_limiter");
        calls.Should().Contain(c => c.Contains("alimiter=limit=0.794", StringComparison.Ordinal) && !c.Contains("loudnorm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_music_mix_with_no_loudness_target_is_normalised_to_web_and_not_played_at_minus_one()
    {
        // Run 21bb6f46: music over muted footage came out at -9.4 LUFS and +1.1 dBTP (target -16): the
        // sole-soundtrack bed sat at -1 dBFS and no LoudnessTarget was set, so nothing normalised the mix.
        var facts = new VideoAnalysisMusicBeat(120, 0.5, 2.0, 0, 0, 60, DropSec: null, OutroSec: null, Confidence: 0.9, Tempo: "medium");
        ReelCompileFixture fixture = Fixture(new VideoAnalysisMusicTrack(TrackId, "the-last-point.mp3", facts), VideoBeatSync.Off);
        fixture.FfmpegHook = args => args.Any(a => a.Contains("print_format=json", StringComparison.Ordinal))
            ? new VideoToolResult(0, string.Empty, "{\"input_i\":\"-9.40\",\"input_tp\":\"1.10\",\"input_lra\":\"5.00\",\"input_thresh\":\"-20.00\",\"target_offset\":\"0.10\"}", false)
            : null;

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2", "s4"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject loudness = fixture.Output!["loudness"]!.AsObject();
        loudness["target"]!.GetValue<string>().Should().Be("Web");
        // The fake workspace has no output file to stat, so the apply pass is not asserted; the measure pass ran against the Web target.
        loudness["measuredLufs"]!.GetValue<double>().Should().Be(-9.4);
        loudness["targetLufs"]!.GetValue<double>().Should().Be(-16);
        loudness["implicit"]!.GetValue<bool>().Should().BeTrue();
        fixture.Output!["music"]!["bedDbfs"]!.GetValue<double>().Should().BeLessThanOrEqualTo(-6);
    }

    [Fact]
    public async Task Off_adds_nothing()
    {
        ReelCompileFixture fixture = Fixture(measured: null, VideoBeatSync.Off);
        await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));
        fixture.Output!.ContainsKey("beatSync").Should().BeFalse();
    }

    [Fact]
    public void The_view_states_the_drop_and_outro_as_bars_and_seconds()
    {
        var facts = new VideoAnalysisMusicBeat(134.99, 0.44448, 1.77792, 0.155, 1.4884, 124.58, 13.934, 42.83, 0.87, "fast");
        JsonObject node = MusicBeatProbe.ViewNode(facts);

        node["bpm"]!.GetValue<double>().Should().Be(135.0);
        node["tempo"]!.GetValue<string>().Should().Be("fast");
        node["drop"]!.GetValue<string>().Should().Be("bar 8, at 13.9 s");
        node["outro"]!.GetValue<string>().Should().Be("bar 24, at 42.8 s");
        node["grid"]!.GetValue<string>().Should().Be("steady");
    }
}
