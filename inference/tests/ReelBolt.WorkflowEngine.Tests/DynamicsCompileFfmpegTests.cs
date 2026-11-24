#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The dynamic edit (docs/design/dynamic-edit.md) through the REAL <see cref="VideoCompileStepExecutor"/> and real ffmpeg:
/// two synthetic solid-colour sources (red = s0, blue = s1, 10 s at 30 fps each), a measured 120 bpm grid (beat 0.5 s,
/// bar 2 s, drop at 5 s) and the frame colours of the encoded file. Skipped where ffmpeg is not on PATH.
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class DynamicsCompileFfmpegTests
{
    private static readonly Guid ProjectId = Guid.Parse("88888888-8888-4888-8888-888888888888");
    private static readonly Guid TrackId = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/dyn-analysis.json";
    private const string TrackKey = "projects/p/userFiles/track.wav";
    private const double Fps = 30.0;

    private static JsonSerializerOptions JsonOptions() => new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private sealed record Run(JsonObject Output, string Graph, string OutputPath, List<(byte R, byte G, byte B)> Frames, double AudioSec = 0);

    private static VideoAnalysisMusicTrack Track(double bpm = 120, double drop = 5.0) => new(TrackId, "track.wav",
        new VideoAnalysisMusicBeat(bpm, 60.0 / bpm, 240.0 / bpm, 0, 0, 40, DropSec: drop, OutroSec: null, Confidence: 0.9, Tempo: bpm >= 130 ? "fast" : "medium"));

    private static VideoAnalysisArtifact TwoSources(IReadOnlyList<VideoAnalysisShot>? shots = null) => new(
        Version: 1, Media: new VideoAnalysisMedia(10.0, 30, 1, 320, 180),
        Shots: shots ?? [new VideoAnalysisShot("s0", 0, 10, SourceIndex: 0), new VideoAnalysisShot("s1", 0, 10, SourceIndex: 1)],
        SilenceSpans: [], Segments: [], Words: [], OfferedIds: (shots ?? [new VideoAnalysisShot("s0", 0, 10), new VideoAnalysisShot("s1", 0, 10)]).Select(s => s.Id).ToList(),
        Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false), Placements: null, OfferedPlacementIds: null,
        Sources:
        [
            new VideoAnalysisSourceInfo(0, "projects/p/userFiles/red.mp4", new VideoAnalysisMedia(10.0, 30, 1, 320, 180)),
            new VideoAnalysisSourceInfo(1, "projects/p/userFiles/blue.mp4", new VideoAnalysisMedia(10.0, 30, 1, 320, 180))
        ],
        Music: Track());

    private static string Decision(string keepIds, params object[] dynamics) => JsonSerializer.Serialize(new
    {
        keep = keepIds.Split(',').Select(id => new { fromId = id, toId = id, reason = "k" }).ToArray(),
        editRationale = "r",
        suggestedTitle = "T",
        dynamics
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static VideoCompileStepConfig Config(VideoDynamicsConfig? dynamics, VideoTransitionPolicy policy = VideoTransitionPolicy.Off) =>
        new(Version: 1, Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2), AnalysisStepOrder: 1)
        {
            Mode = VideoCompileMode.Reencode,
            RegisterProjectFile = false,
            LoudnessTarget = ProgramLoudness.Off,
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            MuteSourceAudio = true,
            EnableMusic = true,
            MusicTrackProjectFileId = TrackId,
            TransitionPolicy = policy,
            Dynamics = dynamics
        };

    [FfmpegToolsFact]
    public async Task BeatAlternate_alternates_four_cells_then_holds_the_winner_and_the_cut_stays_ten_seconds()
    {
        Run run = await CompileAsync(Config(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)),
            Decision("s0", new { effect = "BeatAlternate", anchorId = "s0", otherId = "s1", moment = "OnBeat", beats = "Four", division = "Beat", winner = "B", reason = "mix" }));

        run.Output["dynamics"]!["counts"]!["applied"]!.GetValue<int>().Should().Be(1, run.Output["dynamics"]!.ToJsonString());
        run.Frames.Count.Should().BeInRange(299, 301, "the program keeps the anchor window's length");
        // Cells of 15 frames on the grid lines: red, blue, red, blue, then blue holds (within one frame of each line).
        for (int cell = 0; cell < 4; cell++)
            for (int f = cell * 15 + 2; f <= cell * 15 + 12; f++)
                Colour(run.Frames[f]).Should().Be(cell % 2 == 0 ? "red" : "blue", $"frame {f} of cell {cell}");
        for (int f = 62; f < run.Frames.Count - 2; f += 7)
            Colour(run.Frames[f]).Should().Be("blue", $"the winner holds at frame {f}");
    }

    [FfmpegToolsFact]
    public async Task FlashFrame_is_exactly_one_white_frame_at_the_reported_grid_frame()
    {
        Run run = await CompileAsync(Config(new VideoDynamicsConfig(Pace: DynamicPace.Medium, KeepSceneBoundaries: false)),
            Decision("s0", new { effect = "FlashFrame", anchorId = "s0", moment = "OnBar", reason = "hit" }));

        JsonObject applied = run.Output["dynamics"]!["applied"]!.AsArray().Single(x => x!["effect"]!.GetValue<string>() == "FlashFrame")!.AsObject();
        applied["effect"]!.GetValue<string>().Should().Be("FlashFrame");
        long programFrame = applied["programFrame"]!.GetValue<long>();
        run.Graph.Should().Contain("drawbox=x=0:y=0:w=iw:h=ih:color=white:t=fill:enable='eq(n,");
        var white = run.Frames.Select((f, i) => (f, i)).Where(t => Colour(t.f) == "white").Select(t => t.i).ToList();
        white.Should().Equal([(int)programFrame], "the flash is exactly one frame, on the planned frame");
        if (programFrame > 0) Colour(run.Frames[(int)programFrame - 1]).Should().Be("red");
        Colour(run.Frames[(int)programFrame + 1]).Should().Be("red");
    }

    [FfmpegToolsFact]
    public async Task SpeedRamp_keeps_the_program_length_equal_to_the_report()
    {
        Run run = await CompileAsync(Config(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)),
            Decision("s0", new { effect = "SpeedRamp", anchorId = "s0", moment = "OnDrop", intensity = "Normal", reason = "drop" }));

        run.Output["dynamics"]!["counts"]!["retimes"]!.GetValue<int>().Should().Be(1);
        run.Graph.Should().Contain("setpts=PTS/0.");
        double programSec = run.Output["dynamics"]!["programSec"]!.GetValue<double>();
        programSec.Should().BeApproximately(10.0, 1.0 / Fps, "a ramp reshapes the beat before the drop, not the length");
        (run.Frames.Count / Fps).Should().BeApproximately(programSec, 1.5 / Fps, "the file lasts what the report says");
        run.Output["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(programSec, 1e-9);
    }

    [FfmpegToolsFact]
    public async Task WhipOnBar_is_a_duration_neutral_whip_at_the_bar_line_also_under_beat_sync()
    {
        // Beat sync used to turn every overlapping seam back into a hard cut, so the xfade whip never reached the film.
        VideoCompileStepConfig config = Config(new VideoDynamicsConfig(Pace: DynamicPace.Calm, KeepSceneBoundaries: false), VideoTransitionPolicy.Editor) with
        {
            BeatSync = VideoBeatSync.Beat
        };
        Run run = await CompileAsync(config, Decision("s0,s1", new { effect = "WhipOnBar", anchorId = "s1", moment = "OnBar", reason = "bar" }));

        JsonObject dyn = run.Output["dynamics"]!.AsObject();
        dyn["counts"]!["whips"]!.GetValue<int>().Should().Be(1);
        dyn["whipsRealised"]!.GetValue<int>().Should().Be(1);
        dyn["applied"]!.AsArray().Should().Contain(x => x!["effect"]!.GetValue<string>() == "WhipOnBar");
        // The whip is on both sides of the cut as filters on whole frames (one eighth of a beat = 2 frames each side), no xfade, no overlap.
        run.Graph.Should().Contain("scroll=horizontal=").And.Contain("gblur=sigma=14:sigmaV=0");
        run.Graph.Should().NotContain("xfade=");
        run.Frames.Count.Should().BeInRange(599, 601, "a whip never shifts a later frame");
        Colour(run.Frames[280]).Should().Be("red");
        Colour(run.Frames[320]).Should().Be("blue");
    }

    [Fact]
    public void The_whip_filters_cover_whole_frames_on_each_side_of_the_cut()
    {
        var outgoing = new VideoCompileStepExecutor.ResolvedSpan(0, 2, 0, 2, 0, 60, 0, null, 1.0, null, WhipOutFrames: 2, WhipWord: "WhipLeft");
        string o = VideoCompileStepExecutor.WhipFilters(outgoing, 30, 1);
        o.Should().Contain("enable='gte(n,58)'").And.Contain("scroll=horizontal=0.25").And.Contain("gblur=sigma=14");
        var incoming = new VideoCompileStepExecutor.ResolvedSpan(0, 2, 0, 2, 0, 60, 0, null, 1.0, null, WhipInFrames: 2, WhipWord: "WhipRight");
        string i = VideoCompileStepExecutor.WhipFilters(incoming, 30, 1);
        i.Should().Contain("enable='lt(n,2)'").And.Contain("scroll=horizontal=-0.25").And.Contain("hpos=0.5");
        VideoCompileStepExecutor.WhipFilters(new VideoCompileStepExecutor.ResolvedSpan(0, 2, 0, 2, 0, 60), 30, 1).Should().BeEmpty();
    }

    [FfmpegToolsFact]
    public async Task The_music_bed_runs_under_the_cold_open_and_the_end_card_too()
    {
        VideoCompileStepConfig config = Config(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)) with
        {
            EnableGeneratedClips = true,
            GeneratedClips = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 4),
            GeneratedClipAudio = GeneratedAudioMode.Mute
        };
        Run run = await CompileAsync(config, Decision("s0"), generated: true);

        // 2 s cold open + 10 s body + 1 s end card. The sound used to stop where the body did (10 s).
        run.Output["generatedClips"]!["appliedCount"]!.GetValue<int>().Should().Be(2);
        run.Frames.Count.Should().BeInRange(12 * 30 - 1, 13 * 30 + 1);
        run.AudioSec.Should().BeApproximately(run.Frames.Count / Fps, 0.1, "the music covers the whole film");
        run.Output["music"]!["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(run.Frames.Count / Fps, 0.1);
        run.Output.ContainsKey("dynamics").Should().BeTrue();
    }

    // ---- QA-DYN-28 / QA-DYN-31: the cold-open bed shift and fileMaxOffsetMs, in CI (synthetic sources, a fake grid, no media, no env var) ----

    private static VideoCompileStepConfig ColdOpenConfig(VideoDynamicsConfig? dynamics) =>
        Config(dynamics) with
        {
            BeatSync = VideoBeatSync.Beat,
            EnableGeneratedClips = true,
            GeneratedClips = new ExtractInputRef(ExtractInputSource.Step, StepOrder: 4),
            GeneratedClipAudio = GeneratedAudioMode.Mute
        };

    private static VideoAnalysisArtifact TwoSourcesAt(double bpm, double drop = 5.0, IReadOnlyList<VideoAnalysisShot>? shots = null) => TwoSources(shots) with { Music = Track(bpm, drop) };

    /// <summary>The first frame (index) of a colour after the run of another one: the file's own cut frame, from real frames.</summary>
    private static int FirstFrame(Run run, string colour, int from = 0)
    {
        for (int i = from; i < run.Frames.Count; i++)
            if (Colour(run.Frames[i]) == colour)
                return i;
        throw new InvalidOperationException($"no {colour} frame after {from}");
    }

    private static double OffGrid(double sec, double beatSec) => Math.Abs(sec - Math.Round(sec / beatSec) * beatSec);

    // ---- QA-DYN-54: every grid fact is in film time. The bed starts with the film; a cold open that is not a whole number of beats puts the
    // body at another place of the beat, and the cuts, the drop-aligned cut and the bars must follow the music in the FILE, not the track's own clock.

    [FfmpegToolsFact]
    public async Task With_a_cold_open_off_the_beat_the_body_cut_sits_on_the_beat_the_bed_really_plays()
    {
        // 120 bpm (beat 0.5 s), a 2.2 s cold open (4.4 beats), no Dynamics. The red shot is 10 s: the sync cuts it on a beat line of the FILM
        // (film 12.0 s = body 9.8 s), not on body 10.0 s = film 12.2 s which is 200 ms off every beat the bed plays.
        Run run = await CompileAsync(ColdOpenConfig(null), Decision("s0,s1"), TwoSourcesAt(120), generated: true, coldOpenSec: 2.2);

        int blue = FirstFrame(run, "blue");
        OffGrid(blue / Fps, 0.5).Should().BeLessThan(1.0 / Fps + 1e-6, $"the red-to-blue cut is at film {blue / Fps:F3} s");
        Colour(run.Frames[(int)Math.Round(2.2 * Fps) + 2]).Should().Be("red", "the body starts after the cold open");
        run.Output["beatSync"]!["bodyShiftMs"]!.GetValue<double>().Should().BeApproximately(-2200, 0.2);
        run.Graph.Should().NotContain("adelay=").And.NotContain("atrim=start=", "the bed is not shifted, the cuts follow it");
    }

    [FfmpegToolsFact]
    public async Task With_a_cold_open_and_the_drop_inside_the_body_the_drop_cut_is_at_the_drop_the_music_plays()
    {
        // The track's drop plays at film 11.2 s; the cold open is 2.2 s, so it is body 9.0 s: the red shot (10 s) is cut on it.
        Run run = await CompileAsync(ColdOpenConfig(null), Decision("s0,s1"), TwoSourcesAt(120, drop: 11.2), generated: true, coldOpenSec: 2.2);

        JsonObject beat = run.Output["beatSync"]!.AsObject();
        beat["dropAligned"]!.GetValue<bool>().Should().BeTrue(beat.ToJsonString());
        beat["dropBodySec"]!.GetValue<double>().Should().BeApproximately(9.0, 1e-6);
        int blue = FirstFrame(run, "blue");
        (blue / Fps).Should().BeApproximately(11.2, 1.0 / Fps + 1e-6, "the cut is on the drop at the time the bed plays it");
    }

    [FfmpegToolsFact]
    public async Task With_the_drop_inside_the_cold_open_no_cut_is_made_on_it_and_it_is_reported()
    {
        // A 6.2 s cold open and a drop at 5.0 s (film and music alike): it plays inside the cold open, so the body has no drop. The red shot
        // is 6 s, so the old body-time grid cut it on "the drop" at body 5.0 (film 11.2), where the bed has no drop at all.
        VideoAnalysisShot[] shots = [new("s0", 0, 6, SourceIndex: 0), new("s1", 0, 10, SourceIndex: 1)];
        Run run = await CompileAsync(ColdOpenConfig(null), Decision("s0,s1"), TwoSourcesAt(120, drop: 5.0, shots), generated: true, coldOpenSec: 6.2);

        JsonObject beat = run.Output["beatSync"]!.AsObject();
        beat["dropAligned"]!.GetValue<bool>().Should().BeFalse(beat.ToJsonString());
        beat["dropReason"]!.GetValue<string>().Should().Be("drop_in_cold_open");
        beat["dropBodySec"].Should().BeNull();
        int blue = FirstFrame(run, "blue");
        (blue / Fps).Should().NotBeApproximately(11.2, 0.2, "no cut on a drop the body does not have");
        OffGrid(blue / Fps, 0.5).Should().BeLessThan(1.0 / Fps + 1e-6, "it is on a beat line of the film instead");
    }

    [FfmpegToolsFact]
    public async Task A_dynamic_edit_with_a_cold_open_off_the_beat_keeps_every_body_cut_on_the_music_and_does_not_shift_the_bed()
    {
        // 140 bpm: beat 0.42857 s. A 2 s cold open is 4.667 beats. The bed starts with the film and is not shifted: the grid the planner
        // places moves on is the music's at the body's time, so the body cuts are within a frame of a beat line in the file.
        Run run = await CompileAsync(ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)), Decision("s0,s1"),
            TwoSourcesAt(140), generated: true, coldOpenSec: 2);

        JsonObject beat = run.Output["beatSync"]!.AsObject();
        beat.ContainsKey("musicShiftMs").Should().BeFalse();
        beat["bodyShiftMs"]!.GetValue<double>().Should().BeApproximately(-2000, 0.2);
        run.Graph.Should().NotContain("adelay=").And.NotContain("atrim=start=");
        var cuts = beat["fileCuts"]!.AsArray().Select(c => (Type: c!["type"]!.GetValue<string>(), Off: c["offsetMs"]!.GetValue<double>())).ToList();
        cuts.Where(c => c.Type is "body_cut" or "split_piece").Should().NotBeEmpty();
        cuts.Where(c => c.Type is "body_cut" or "split_piece").Max(c => c.Off).Should().BeLessThan(1000.0 / Fps + 1.0, "the body cuts sit on the beats the bed plays");
        cuts.Single(c => c.Type == "cold_open_join").Off.Should().BeApproximately(142.9, 1.0, "the join is where the cold open ends: off the beat by its own remainder");
        int blue = FirstFrame(run, "blue");
        OffGrid(blue / Fps, 60.0 / 140.0).Should().BeLessThan(1.0 / Fps + 1e-6, "measured from the frames of the file");
    }

    [FfmpegToolsFact]
    public async Task QA_DYN_66_the_segments_take_the_slots_before_a_cutaway_listed_ahead_of_them()
    {
        // Cap 2, manifest [cutaway, cutaway, cold open, end card] (cutaways listed first): the segments win, the cutaways are dropped.
        Run run = await CompileAsync(ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)), Decision("s0,s1"),
            TwoSourcesAt(140), generated: true, cutaways: [("s0", 1), ("s1", 1)], cutawaysFirst: true, maxClips: 2);

        JsonObject generated = run.Output["generatedClips"]!.AsObject();
        generated["appliedCount"]!.GetValue<int>().Should().Be(2, "the cap is 2");
        generated["coldOpensApplied"]!.GetValue<int>().Should().Be(1);
        generated["endCardsApplied"]!.GetValue<int>().Should().Be(1);
        generated["dropped"]!.AsArray().Select(d => d!["reason"]!.GetValue<string>()).Should().Equal("max_generated_clips_exceeded", "max_generated_clips_exceeded");
    }

    [FfmpegToolsFact]
    public async Task A_cold_open_that_is_a_whole_number_of_beats_shifts_nothing()
    {
        Run run = await CompileAsync(ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)), Decision("s0,s1"),
            TwoSourcesAt(120), generated: true, coldOpenSec: 2);

        run.Output["beatSync"]!["coldOpenOffsetMs"]!.GetValue<double>().Should().BeApproximately(0, 0.2);
        run.Graph.Should().NotContain("adelay=").And.NotContain("atrim=start=");
    }

    [FfmpegToolsFact]
    public async Task Without_Dynamics_the_cold_open_cuts_follow_the_music_and_the_bed_is_not_shifted()
    {
        Run run = await CompileAsync(ColdOpenConfig(null), Decision("s0"), TwoSourcesAt(140), generated: true, coldOpenSec: 2);

        JsonObject beat = run.Output["beatSync"]!.AsObject();
        beat["coldOpenOffsetMs"]!.GetValue<double>().Should().BeApproximately(142.9, 0.2, "how far the body starts from a beat line");
        beat.ContainsKey("musicShiftMs").Should().BeFalse();
        beat.ContainsKey("coldOpenRemainderMs").Should().BeFalse();
        beat["fileCuts"]!.AsArray().Select(c => c!["type"]!.GetValue<string>()).Should().Contain(["cold_open_join", "end_card_join"]);
        beat["fileCuts"]!.AsArray().Single(c => c!["type"]!.GetValue<string>() == "end_card_join")!["offsetMs"]!.GetValue<double>()
            .Should().BeLessThan(1000.0 / Fps + 1.0, "the body ends on a beat line of the film");
        run.Graph.Should().NotContain("adelay=").And.NotContain("atrim=start=");
        run.Output.ContainsKey("dynamics").Should().BeFalse();
    }

    [FfmpegToolsFact]
    public async Task A_half_beat_strobe_is_measured_against_the_half_beat_grid_not_reported_as_half_a_beat_off()
    {
        // 140 bpm: a half-beat cell is 0.2143 s. Cells are whole frames, so they sit within a frame of the half-beat grid; against the
        // beat lines alone they would read about 214 ms off (QA-DYN-31).
        Run run = await CompileAsync(ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)),
            Decision("s0", new { effect = "StrobeCut", anchorId = "s0", otherId = "s1", moment = "OnBeat", division = "HalfBeat", reason = "strobe" }),
            TwoSourcesAt(140), generated: true, coldOpenSec: 60.0 / 140.0 * 6);

        JsonObject dyn = run.Output["dynamics"]!.AsObject();
        JsonObject strobe = dyn["applied"]!.AsArray().Single(a => a!["effect"]!.GetValue<string>() == "StrobeCut")!.AsObject();
        strobe["division"]!.GetValue<string>().Should().Be("HalfBeat");
        run.Output["beatSync"]!["fileMaxOffsetMs"]!.GetValue<double>().Should().BeLessThan(1000.0 / Fps, "half-beat cells are checked against the half-beat grid");
    }

    [FfmpegToolsFact]
    public async Task The_file_offsets_cover_the_joins_and_the_cutaway_edges_not_only_the_body_cuts()
    {
        // 140 bpm, 2 s cold open, a body, a 1 s end card: every join is measured against the film's grid (it used to cover the
        // body cuts alone and read as on the beat whatever the joins did).
        Run run = await CompileAsync(ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)), Decision("s0"),
            TwoSourcesAt(140), generated: true, coldOpenSec: 2);

        JsonObject beat = run.Output["beatSync"]!.AsObject();
        var cuts = beat["fileCuts"]!.AsArray().Select(c => (Type: c!["type"]!.GetValue<string>(), Off: c["offsetMs"]!.GetValue<double>())).ToList();
        cuts.Select(c => c.Type).Should().Contain(["cold_open_join", "end_card_join"]);
        cuts.Single(c => c.Type == "cold_open_join").Off.Should().BeApproximately(142.9, 1.0, "the join is where the cold open ends: off the beat by its own remainder");
        cuts.Single(c => c.Type == "end_card_join").Off.Should().BeLessThan(1000.0 / Fps * 2, "the beat sync put the body end on a line");
        beat["fileMaxOffsetMs"]!.GetValue<double>().Should().Be(cuts.Max(c => c.Off));
        beat["fileMedianOffsetMs"]!.GetValue<double>().Should().BeLessThanOrEqualTo(beat["fileP90OffsetMs"]!.GetValue<double>());
        beat["fileP90OffsetMs"]!.GetValue<double>().Should().BeLessThanOrEqualTo(beat["fileMaxOffsetMs"]!.GetValue<double>());
        run.Output["dynamics"]!["fileMaxOffsetMs"]!.GetValue<double>().Should().Be(beat["fileMaxOffsetMs"]!.GetValue<double>(),
            "the dynamics block names the file's number next to its own, planner-only maxBeatOffsetMs");
    }

    [FfmpegToolsFact]
    public async Task A_dynamic_cutaway_ends_on_a_grid_line_not_where_its_clip_runs_out()
    {
        // 140 bpm (beat 0.4286 s): a 4 s cutaway from t=0 would end 9.33 beats in; the return edge snaps down to the 9th beat (3.857 s, frame 116).
        Run run = await CompileAsync(ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)), Decision("s0"),
            TwoSourcesAt(140), cutaways: [("s0", 4)]);

        JsonObject beat = run.Output["beatSync"]!.AsObject();
        beat["cutawayEdgesSnapped"]!.GetValue<int>().Should().Be(1);
        var end = beat["fileCuts"]!.AsArray().Single(c => c!["type"]!.GetValue<string>() == "cutaway_end")!;
        end["offsetMs"]!.GetValue<double>().Should().BeLessThan(1000.0 / Fps / 2 + 0.5);
        Colour(run.Frames[114]).Should().Be("green");
        Colour(run.Frames[119]).Should().Be("red", "the base is back on the grid line, not 4 s in");
    }

    /// <summary>The frame colours as runs: "red x300, green x24, ..." so a flash of one frame is visible in a failure message.</summary>
    private static string Runs(IEnumerable<(byte R, byte G, byte B)> frames)
    {
        var parts = new List<string>();
        string? last = null;
        int count = 0;
        foreach (string c in frames.Select(Colour))
        {
            if (c == last) { count++; continue; }
            if (last is not null) parts.Add($"{last}x{count}");
            last = c;
            count = 1;
        }

        if (last is not null) parts.Add($"{last}x{count}");
        return string.Join(", ", parts);
    }

    [FfmpegToolsFact]
    public async Task A_cutaway_that_starts_on_a_cut_is_frame_exact_at_29_97_fps_too()
    {
        Run run = await CompileAsync(ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)), Decision("s0,s1"),
            TwoSourcesAt(140), cutaways: [("s1", 1)], sourceRate: "30000/1001");

        int firstNotRed = run.Frames.FindIndex(f => Colour(f) != "red");
        firstNotRed.Should().BeGreaterThan(0);
        Colour(run.Frames[firstNotRed]).Should().Be("green", $"runs: {Runs(run.Frames)}");
        run.Frames.Skip(firstNotRed).Take(24).Select(Colour).Should().AllBeEquivalentTo("green", $"no blue flash in front of the clip; runs: {Runs(run.Frames)}");
    }

    [FfmpegToolsFact]
    public async Task A_cutaway_whose_clip_ends_before_its_window_shows_no_black_and_no_one_frame_flash()
    {
        // The manifest says 4 s, the clip really is 1.5 s long (the media end comes before the window end).
        Run run = await CompileAsync(ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)), Decision("s0"),
            TwoSourcesAt(140), cutaways: [("s0", 4)], cutawayClipSec: 1.5);

        string runs = Runs(run.Frames);
        run.Frames.Select(Colour).Should().NotContain("black", $"nothing goes black; runs: {runs}");
        runs.Split(", ").Where(r => !r.StartsWith("green", StringComparison.Ordinal)).Should().OnlyContain(r => !r.EndsWith("x1", StringComparison.Ordinal), $"no one-frame flash; runs: {runs}");
    }

    // ---- QA-DYN-43: a cutaway that starts on a cut starts on the cut's own frame ----

    [FfmpegToolsFact]
    public async Task A_cutaway_that_starts_on_a_cut_starts_on_the_frame_of_the_cut_with_no_flash_of_the_anchor()
    {
        // 140 bpm (beat 12.857 frames): red s0 then blue s1, the cut between them on a grid frame that is not a whole number of beats'
        // worth of seconds. A 1 s cutaway anchored on s1 begins where s1 does: the first frame that is not red must be green, never one blue frame.
        Run run = await CompileAsync(ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: false)), Decision("s0,s1"),
            TwoSourcesAt(140), cutaways: [("s1", 1)]);

        int firstNotRed = run.Frames.FindIndex(f => Colour(f) != "red");
        firstNotRed.Should().BeGreaterThan(0);
        Colour(run.Frames[firstNotRed]).Should().Be("green", $"the cutaway takes the cut's own frame; frames around: {string.Join(",", run.Frames.Skip(Math.Max(0, firstNotRed - 1)).Take(3).Select(Colour))}");
        run.Frames.Skip(firstNotRed).Take(24).Select(Colour).Should().AllBeEquivalentTo("green", "24 frames of the clip, no blue flash in front of it");
        var start = run.Output["beatSync"]!["fileCuts"]!.AsArray().Single(c => c!["type"]!.GetValue<string>() == "cutaway_start")!;
        start["sec"]!.GetValue<double>().Should().BeApproximately(firstNotRed / Fps, 0.5 / Fps, "fileCuts names the frame the picture changes");
    }

    // ---- QA-DYN-44: body cuts do not drift late by per-span rounding ----

    private static VideoAnalysisArtifact EightShots(double bpm)
    {
        // Alternating red (source 0) and blue (source 1) shots of irregular lengths: every cut is a visible colour change.
        (int Src, double From, double To)[] cuts = [(0, 0, 2.0), (1, 0, 1.9), (0, 2.0, 4.1), (1, 1.9, 4.2), (0, 4.1, 6.3), (1, 4.2, 6.0), (0, 6.3, 8.4), (1, 6.0, 8.5)];
        List<VideoAnalysisShot> shots = cuts.Select((c, i) => new VideoAnalysisShot($"s{i}", c.From, c.To, SourceIndex: c.Src)).ToList();
        return TwoSources(shots) with { Music = Track(bpm) };
    }

    [FfmpegToolsFact]
    public async Task Body_cuts_stay_within_a_frame_of_the_beat_over_the_whole_film_and_the_length_matches_the_plan()
    {
        // 140 bpm = 12.857 frames a beat: a span laid on F(end) - F(start) frames rounds every span the same way and the error piles up
        // (14, 29, 43, 57 ms ... by the seventh cut). The cumulative layout rounds each boundary from the exact time: never more than half a frame.
        Run run = await CompileAsync(ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: true)) with { EnableGeneratedClips = false },
            Decision("s0,s1,s2,s3,s4,s5,s6,s7"), EightShots(140));

        var bodyCuts = run.Output["beatSync"]!["fileCuts"]!.AsArray().Where(c => c!["type"]!.GetValue<string>() == "body_cut")
            .Select(c => (Sec: c!["sec"]!.GetValue<double>(), Off: c["offsetMs"]!.GetValue<double>())).ToList();
        bodyCuts.Count.Should().BeGreaterThanOrEqualTo(6);
        string table = string.Join(" ", bodyCuts.Select(c => $"{c.Sec:0.000}:{c.Off:0.0}"));
        bodyCuts.Max(c => c.Off).Should().BeLessThanOrEqualTo(1000.0 / Fps, "no body cut is more than a frame off its beat line: " + table);

        // The report is the file: every colour change sits on the frame the report names.
        var changes = new List<double>();
        for (int i = 1; i < run.Frames.Count; i++)
            if (Colour(run.Frames[i - 1]) != Colour(run.Frames[i])) changes.Add(i / Fps);
        foreach (var cut in bodyCuts)
            changes.Should().Contain(c => Math.Abs(c - cut.Sec) < 0.5 / Fps, $"the body cut at {cut.Sec} s is in the file: {string.Join(",", changes.Select(c => c.ToString("0.000")))}");

        // The programme length is the plan's, to within a frame.
        double planned = run.Output["outputDurationSec"]!.GetValue<double>();
        (run.Frames.Count / Fps).Should().BeApproximately(planned, 1.5 / Fps);
    }

    // ---- QA-DYN-33: a cutaway anchored on a shot that hosts a BeatAlternate must not take over the alternation's cells ----

    private static VideoAnalysisArtifact AlternationShots() => TwoSources(
    [
        new VideoAnalysisShot("a0", 0, 3, SourceIndex: 0),
        new VideoAnalysisShot("a1", 3, 5.75, SourceIndex: 0),
        new VideoAnalysisShot("a2", 6, 9, SourceIndex: 0),
        new VideoAnalysisShot("s1", 0, 10, SourceIndex: 1)
    ]);

    private static object Alt(string anchor, string moment, string division, string beats, string winner) =>
        new { effect = "BeatAlternate", anchorId = anchor, otherId = "s1", moment, beats, division, winner, reason = "mix" };

    [FfmpegToolsFact]
    public async Task A_cutaway_anchored_on_a_shot_that_hosts_an_alternation_never_covers_its_cells()
    {
        // 120 bpm, no beat sync: a0 body 0-3 s (frames 0-90), a1 3-5.75 s (90-173), a2 5.75-8.75 s (173-263). Alternations: a0 from its first
        // line (winner A, hold from frame 60), a1 from its first line in half-beat cells (winner B: no A footage is left outside the cells),
        // a2 from the line at 6 s, 7 frames into the shot (winner A, hold from frame 210). Every shot has its own 1 s cutaway clip (green).
        Run run = await CompileAsync(
            ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: true)) with { BeatSync = VideoBeatSync.Off, EnableGeneratedClips = true },
            Decision("a0,a1,a2",
                Alt("a0", "OnBeat", "Beat", "Four", "A"),
                Alt("a1", "OnBeat", "HalfBeat", "Four", "B"),
                Alt("a2", "OnBeat", "Beat", "Two", "A")),
            AlternationShots(), cutaways: [("a0", 1), ("a1", 1), ("a2", 1)]);

        JsonObject dyn = run.Output["dynamics"]!.AsObject();
        string because = dyn.ToJsonString();
        dyn["applied"]!.AsArray().Count(a => a!["effect"]!.GetValue<string>() == "BeatAlternate").Should().Be(3, because);

        // First alternation: A B A B on 15-frame cells from frame 0; the middle frame of every cell shows its own side.
        for (int cell = 0; cell < 4; cell++)
            Colour(run.Frames[cell * 15 + 7]).Should().Be(cell % 2 == 0 ? "red" : "blue", $"a0 cell {cell}: {because}");
        // Second: 8 half-beat cells of 7.5 frames from frame 90.
        for (int cell = 0; cell < 8; cell++)
            Colour(run.Frames[(int)(90 + cell * 7.5 + 3.5)]).Should().Be(cell % 2 == 0 ? "red" : "blue", $"a1 cell {cell}: {because}");
        // Third: from frame 180, A B on 15-frame cells.
        for (int cell = 0; cell < 2; cell++)
            Colour(run.Frames[180 + cell * 15 + 7]).Should().Be(cell % 2 == 0 ? "red" : "blue", $"a2 cell {cell}: {because}");

        // The cutaway of a0 lands on the winner hold (60-90), the one part the alternation does not rewrite.
        Enumerable.Range(62, 26).Select(f => Colour(run.Frames[f])).Should().AllBeEquivalentTo("green", "the generated clip is used where a0's alternation left footage");
        // a1 has no unrewritten A footage: its clip is skipped with the reason, nothing green in its range.
        run.Output["generatedClips"]!["dropped"]!.AsArray().Select(d => d!["reason"]!.GetValue<string>()).Should().Contain("anchor_hosts_alternation");
        Enumerable.Range(90, 83).Select(f => Colour(run.Frames[f])).Should().NotContain("green");
        // a2's clip sits in the 7 frames before its alternation, and its cells and hold are the alternation's.
        Enumerable.Range(175, 4).Select(f => Colour(run.Frames[f])).Should().AllBeEquivalentTo("green");
        Enumerable.Range(180, 30).Select(f => Colour(run.Frames[f])).Should().NotContain("green");
        Colour(run.Frames[240]).Should().Be("red", "the winner hold");
    }

    [FfmpegToolsFact]
    public async Task A_cutaway_that_would_flash_for_a_frame_or_two_is_skipped_with_a_reason()
    {
        // QA-DYN-51: a2 starts at body 5.97 s (frame 179) and its alternation at the 6 s line (frame 180): the piece before it is one frame.
        // That window used to show the clip for a single frame, a visible flash; it is now skipped as cutaway_too_short.
        VideoAnalysisArtifact shots = TwoSources(
        [
            new VideoAnalysisShot("a0", 0, 3, SourceIndex: 0),
            new VideoAnalysisShot("a1", 3, 5.97, SourceIndex: 0),
            new VideoAnalysisShot("a2", 6, 9, SourceIndex: 0),
            new VideoAnalysisShot("s1", 0, 10, SourceIndex: 1)
        ]);
        Run run = await CompileAsync(
            ColdOpenConfig(new VideoDynamicsConfig(Pace: DynamicPace.Fast, KeepSceneBoundaries: true)) with { BeatSync = VideoBeatSync.Off, EnableGeneratedClips = true },
            Decision("a0,a1,a2", Alt("a2", "OnBeat", "Beat", "Two", "A")),
            shots, cutaways: [("a2", 1)]);

        run.Output["generatedClips"]!["dropped"]!.AsArray().Select(d => d!["reason"]!.GetValue<string>()).Should().Contain("cutaway_too_short", run.Output["generatedClips"]!.ToJsonString());
        run.Frames.Select(Colour).Should().NotContain("green", "no frame of the too-short cutaway is shown");
    }

    private static double AudioSeconds(string film)
    {
        // ffprobe can fail under load (empty stdout): check the exit code and the text, and retry once before the test fails on a parse.
        string last = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ProcessStartInfo psi = new("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string a in new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration", "-of", "default=nw=1:nk=1", film }) psi.ArgumentList.Add(a);
            using Process p = Process.Start(psi)!;
            Task<string> err = p.StandardError.ReadToEndAsync();
            string text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            last = $"exit {p.ExitCode}, stdout '{text}', stderr '{err.Result.Trim()}'";
            if (p.ExitCode == 0 && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double sec))
                return sec;
        }

        throw new InvalidOperationException("ffprobe gave no audio duration for " + film + ": " + last);
    }

    [FfmpegToolsFact]
    public async Task Without_Dynamics_nothing_of_it_reaches_the_graph_or_the_report()
    {
        Run run = await CompileAsync(Config(null), Decision("s0", new { effect = "FlashFrame", anchorId = "s0", moment = "OnBar", reason = "ignored" }));

        run.Output.ContainsKey("dynamics").Should().BeFalse();
        run.Graph.Should().NotContain("drawbox").And.NotContain("setpts=PTS/");
        run.Frames.Count.Should().BeInRange(299, 301);
        run.Frames.Should().OnlyContain(f => Colour(f) == "red");
    }

    [FfmpegToolsFact]
    public async Task The_real_C3_source_gives_at_least_twelve_dynamic_moments_at_pace_Fast_when_DYN_C3_SOURCE_is_set()
    {
        string? source = Environment.GetEnvironmentVariable("DYN_C3_SOURCE");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
            return; // skipped: no real footage on this machine

        string root = Path.Combine(Path.GetTempPath(), "dyn-c3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            IOptions<VideoEditingOptions> options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(root, "scratch"),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 300
            });
            IVideoToolRunner runner = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
            IShotDetector standard = new FfmpegShotDetector(runner, options, NullLogger<FfmpegShotDetector>.Instance);
            IMediaWorkspace workspace = LocalMediaWorkspace.ForLocalFile(source, runner, NullLogger.Instance);
            SceneDetectionResult soft = await SceneCutFinder.DetectWithMethodsAsync(
                standard, workspace, Path.GetFileName(source), SceneDetectionMode.Soft, 0.30, 19.82, 0.5, options.Value, CancellationToken.None, null);

            var shots = soft.Spans.Select((s, i) => new VideoAnalysisShot($"s{i}", s.StartSec, s.EndSec, SourceIndex: 0)).ToList();
            VideoAnalysisArtifact artifact = new(
                Version: 1, Media: new VideoAnalysisMedia(19.82, 30000, 1001, 1080, 1920), Shots: shots, SilenceSpans: [], Segments: [], Words: [],
                OfferedIds: shots.Select(s => s.Id).ToList(), Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
                Placements: null, OfferedPlacementIds: null,
                Sources: [new VideoAnalysisSourceInfo(0, "projects/p/userFiles/c3.mp4", new VideoAnalysisMedia(19.82, 30000, 1001, 1080, 1920))],
                Music: Track(140)); // a fast track: bars of 1.71 s give one auto punch per bar over the 19.4 s film
            string keep = string.Join(",", shots.Select(s => s.Id));
            Run run = await CompileAsync(Config(new VideoDynamicsConfig(Pace: DynamicPace.Fast), VideoTransitionPolicy.Editor), Decision(keep),
                artifact, sources: [source]);

            JsonObject dyn = run.Output["dynamics"]!.AsObject();
            int moments = dyn["counts"]!["applied"]!.GetValue<int>();
            moments.Should().BeGreaterThanOrEqualTo(12, dyn.ToJsonString());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ---- harness ----------------------------------------------------------------------------

    private static string Colour((byte R, byte G, byte B) f) =>
        f.R > 200 && f.G > 200 && f.B > 200 ? "white"
        : f.R > 150 && f.G < 100 && f.B < 100 ? "red"
        : f.B > 150 && f.R < 100 && f.G < 120 ? "blue"
        : f.G > 100 && f.R < 80 && f.B < 80 ? "green"
        : $"other({f.R},{f.G},{f.B})";

    private static async Task<Run> CompileAsync(
        VideoCompileStepConfig config, string decisionJson, VideoAnalysisArtifact? artifact = null, IReadOnlyList<string>? sources = null, bool generated = false, double coldOpenSec = 2,
        IReadOnlyList<(string Anchor, double Dur)>? cutaways = null,
        bool cutawaysFirst = false, string sourceRate = "30", double? cutawayClipSec = null, int maxClips = 0)
    {
        string root = Path.Combine(Path.GetTempPath(), "dyn-compile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();
            VideoCompileStepExecutor.ResetXfadeAvailabilityCacheForTests();

            List<string> inputs = sources?.ToList() ?? [];
            if (inputs.Count == 0)
            {
                foreach (string colour in new[] { "red", "blue" })
                {
                    string path = Path.Combine(root, colour + ".mp4");
                    Ff("-f", "lavfi", "-i", $"color=c={colour}:s=320x180:r={sourceRate}:d=10", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-crf", "12", "-an", path);
                    inputs.Add(path);
                }
            }

            // Generated clips: a 2 s cold open and a 1 s end card, solid green and white, delivered as the manifest says.
            const string coldKey = "projects/p/agentFiles/generated/cold.mp4", endKey = "projects/p/agentFiles/generated/end.mp4", manifestKey = "projects/p/agentFiles/generated/manifest.json";
            string coldPath = Path.Combine(root, "cold.mp4"), endPath = Path.Combine(root, "end.mp4"), manifestPath = Path.Combine(root, "manifest.json");
            string cutawayPath = Path.Combine(root, "cutaway.mp4");
            const string cutawayKey = "projects/p/agentFiles/generated/cutaway.mp4";
            if (generated || cutaways is { Count: > 0 })
            {
                var clips = new List<object>();
                var cutawayClips = new List<object>();
                if (generated)
                {
                    Ff("-f", "lavfi", "-i", $"color=c=green:s=320x180:r=30:d={coldOpenSec.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", coldPath);
                    Ff("-f", "lavfi", "-i", "color=c=white:s=320x180:r=30:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", endPath);
                    clips.Add(new { clipId = "v1", shotIndex = 1, take = 1, purpose = "ColdOpen", anchor = "s0", status = "succeeded", storageKey = coldKey, durationSeconds = coldOpenSec });
                    clips.Add(new { clipId = "v2", shotIndex = 2, take = 1, purpose = "EndCard", anchor = "s0", status = "succeeded", storageKey = endKey, durationSeconds = 1 });
                }

                int n = 0;
                foreach ((string anchor, double dur) in cutaways ?? [])
                {
                    // One solid-green clip serves every cutaway (the manifest names the same file); its length is the manifest's duration.
                    if (n++ == 0)
                        Ff("-f", "lavfi", "-i", $"color=c=green:s=320x180:r=30:d={(cutawayClipSec ?? dur).ToString(System.Globalization.CultureInfo.InvariantCulture)}", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", cutawayPath);
                    cutawayClips.Add(new { clipId = $"vc{n}", shotIndex = 10 + n, take = 1, purpose = "Cutaway", anchor, status = "succeeded", storageKey = cutawayKey, durationSeconds = (int)Math.Round(dur) });
                }

                if (cutawaysFirst) clips.InsertRange(0, cutawayClips); else clips.AddRange(cutawayClips);
                File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { version = 1, clips }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }

            string track = Path.Combine(root, "track.wav");
            Ff("-f", "lavfi", "-i", "sine=frequency=220:duration=40", track);
            artifact ??= TwoSources();
            if (maxClips > 0) config = config with { MaxGeneratedClips = maxClips };
            string artifactJson = JsonSerializer.Serialize(artifact, JsonOptions());

            var options = Options.Create(new VideoEditingOptions { ScratchPath = Path.Combine(root, "scratch"), MaxConcurrentJobs = 1, AnalyzeTimeoutSeconds = 120, CompileTimeoutSeconds = 300 });
            var real = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
            var probe = new FfprobeMediaProbe(real, options, NullLogger<FfprobeMediaProbe>.Instance);
            var files = new Mock<IProjectFileWorkspace>();
            files.Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new List<ProjectWorkspaceFile>
                {
                    new(TrackId, ProjectId, "track.wav", null, "userFiles", TrackKey, "audio/wav", 100, DateTime.UnixEpoch, null)
                });
            files.Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, key, destination, _) =>
                {
                    if (key == AnalysisKey) File.WriteAllText(destination, artifactJson);
                    else if (key == TrackKey) File.Copy(track, destination, overwrite: true);
                    else if (key == manifestKey) File.Copy(manifestPath, destination, overwrite: true);
                    else if (key == coldKey) File.Copy(coldPath, destination, overwrite: true);
                    else if (key == endKey) File.Copy(endPath, destination, overwrite: true);
                    else if (key == cutawayKey) File.Copy(cutawayPath, destination, overwrite: true);
                    else
                    {
                        string name = Path.GetFileNameWithoutExtension(key);
                        string? input = name switch { "red" or "c3" => inputs[0], "blue" => inputs.ElementAtOrDefault(1), _ => null };
                        File.Copy(input ?? inputs[0], destination, overwrite: true);
                    }
                    return Task.CompletedTask;
                });
            files.Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .ReturnsAsync((Guid pid, string _, string fileName, string _, CancellationToken _, string category) => $"projects/{pid}/{category}/{fileName}");

            string preserved = Path.Combine(root, "preserved");
            var graphs = new List<string>();
            IVideoToolRunner runner = new GraphReadingRunner(new VideoCompileRealFfmpegWorkspaceTests.RecordingRunner(real, preserved), graphs);
            var analyzeStep = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze, VideoAnalyzeConfigJson = JsonSerializer.Serialize(new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.PreviousStepOutput)), JsonOptions()) };
            var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, VideoCompileConfigJson = JsonSerializer.Serialize(config, JsonOptions()) };
            var context = new StepExecutionContext
            {
                Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
                Step = step,
                AllSteps = [analyzeStep, step],
                AccumulatedOutput = decisionJson,
                StepOutputHistory =
                [
                    new StepOutputHistoryEntry(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
                    new StepOutputHistoryEntry(2, "StoryEditor", decisionJson, OutputStorageKey: null, ArtifactStorageKey: null),
                    .. generated || cutaways is { Count: > 0 } ? [new StepOutputHistoryEntry(4, "GenerateClips", "{}", OutputStorageKey: null, ArtifactStorageKey: manifestKey)] : Array.Empty<StepOutputHistoryEntry>()
                ],
                CurrentStepIndex = 2,
                IterationCount = 0,
                CorrelationId = "dyn-test",
                CancellationToken = CancellationToken.None
            };
            var executor = new VideoCompileStepExecutor(runner, probe, files.Object, new Mock<IServiceScopeFactory>().Object, options, NullLogger<VideoCompileStepExecutor>.Instance);
            StepExecutionResult result = await executor.ExecuteAsync(context);
            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            string produced = Path.Combine(preserved, "edited.mp4");
            File.Exists(produced).Should().BeTrue();
            JsonObject output = JsonNode.Parse(result.Output)!.AsObject();
            return new Run(output, string.Join("\n", graphs), produced, ReadFrames(produced), AudioSeconds(produced));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The mean colour of every frame (one pixel each), in order.</summary>
    private static List<(byte R, byte G, byte B)> ReadFrames(string path)
    {
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in new[] { "-v", "error", "-i", path, "-vf", "scale=1:1:flags=area,format=rgb24", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" })
            psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        using var ms = new MemoryStream();
        Task<string> err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        p.ExitCode.Should().Be(0, err.Result);
        byte[] bytes = ms.ToArray();
        var frames = new List<(byte, byte, byte)>(bytes.Length / 3);
        for (int i = 0; i + 2 < bytes.Length; i += 3)
            frames.Add((bytes[i], bytes[i + 1], bytes[i + 2]));
        return frames;
    }

    private static void Ff(params string[] args)
    {
        ProcessStartInfo psi = new("ffmpeg") { RedirectStandardError = true };
        foreach (string a in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(args)) psi.ArgumentList.Add(a);
        using Process p = Process.Start(psi)!;
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        p.ExitCode.Should().Be(0, error);
    }

    /// <summary>Passes every call on and keeps the text of every filtergraph it was given (inline or as a script file).</summary>
    private sealed class GraphReadingRunner(IVideoToolRunner inner, List<string> graphs) : IVideoToolRunner
    {
        private void Read(IReadOnlyList<string> args)
        {
            for (int i = 0; i + 1 < args.Count; i++)
            {
                if (args[i] == "-filter_complex_script" && File.Exists(args[i + 1])) graphs.Add(File.ReadAllText(args[i + 1]));
                else if (args[i] == "-filter_complex") graphs.Add(args[i + 1]);
            }
        }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        { Read(args); return inner.RunFfmpegAsync(args, timeout, ct); }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        { Read(args); return inner.RunFfmpegAsync(args, timeout, ct, onStdOutLine); }

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) =>
            inner.RunFfprobeAsync(args, timeout, ct);

        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
        { Read(args); return inner.RunFfmpegAsync(workspace, args, timeout, ct); }

        public Task<VideoToolResult> RunFfmpegAsync(IMediaWorkspace workspace, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine)
        { Read(args); return inner.RunFfmpegAsync(workspace, args, timeout, ct, onStdOutLine); }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority)
        { Read(args); return inner.RunFfmpegAsync(args, timeout, ct, priority); }

        public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine, VideoToolPriority priority)
        { Read(args); return inner.RunFfmpegAsync(args, timeout, ct, onStdOutLine, priority); }

        public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct, VideoToolPriority priority) =>
            inner.RunFfprobeAsync(args, timeout, ct, priority);
    }
}
