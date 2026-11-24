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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Compile correctness fixes from the 2026-10-04 customer run (customer end-to-end findings):
/// narration on multi-clip edits (37), program audio on silent footage (42), dropped narration that
/// looked like success (41), the output canvas (38), output format / fit / target length (44 + the
/// format request), "no music / no SFX to choose from" warnings, and run-named output files (46).
/// Every ffmpeg call goes through a Moq <see cref="IVideoToolRunner"/> except the one gated
/// real-ffmpeg test at the bottom.
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public partial class VideoCompileNarrationAndFormatTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    private const int VoiceoverStepOrder = 3;
    private static readonly ExtractInputRef AnyDecision = new(ExtractInputSource.Previous);

    // ==========================================================================================
    // Finding 37 — narration on multi-clip edits
    // ==========================================================================================

    /// <summary>
    /// Three clips of 4 s, 5 s and 6 s kept whole, in order: s0 plays at [0,4), s1 at [4,9), s2 at
    /// [9,15). Each NarrationPlan line is anchored to one clip's shot. Before the fix every line's
    /// declared back-to-back start was read as a time on SOURCE 0: vo1 (declared 3.0) landed at 3.0
    /// over the wrong clip, and vo2 (declared 5.0, past source 0's 4 s) was dropped as line_cut_away.
    /// </summary>
    [Fact]
    public async Task Narration_lines_anchored_to_later_clips_play_over_their_own_clip()
    {
        var fixture = new CompileFixture(ThreeSourceArtifact());
        fixture.Voiceover = VoiceoverJson(
            ("vo0", 0.0, 3.0, "s0", "Here is the first clip."),
            ("vo1", 3.0, 2.0, "s1", "Then the second one."),
            ("vo2", 5.0, 4.0, "s2", "And finally the third."));
        fixture.Config = c => c with
        {
            EnableVoiceover = true,
            VoiceoverStepOrder = VoiceoverStepOrder,
            EnableCaptions = true,
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0", "s1", "s2"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject voiceover = fixture.Edl!["voiceover"]!.AsObject();
        voiceover["appliedLineCount"]!.GetValue<int>().Should().Be(3);
        voiceover["dropped"]!.AsArray().Should().BeEmpty("every anchor's clip is in the cut");

        var lines = voiceover["lines"]!.AsArray().Select(n => n!.AsObject()).ToDictionary(l => l["lineId"]!.GetValue<string>());
        lines["vo0"]["outputStartSec"]!.GetValue<double>().Should().Be(0.0);
        lines["vo1"]["outputStartSec"]!.GetValue<double>().Should().Be(4.0, "s1 is clip 2, which starts at 4 s in the program");
        lines["vo2"]["outputStartSec"]!.GetValue<double>().Should().Be(9.0, "s2 is clip 3, which starts at 9 s in the program");
        lines["vo1"]["sourceIndex"]!.GetValue<int>().Should().Be(1);
        lines["vo2"]["sourceIndex"]!.GetValue<int>().Should().Be(2);
        lines["vo2"]["anchorId"]!.GetValue<string>().Should().Be("s2");

        // The picture-window fit is measured against the clip each line actually plays over.
        lines["vo1"]["windowStartSec"]!.GetValue<double>().Should().Be(4.0);
        lines["vo1"]["windowEndSec"]!.GetValue<double>().Should().Be(9.0);
        lines["vo2"]["windowEndSec"]!.GetValue<double>().Should().Be(15.0);
        voiceover["fit"]!["overflowLineCount"]!.GetValue<int>().Should().Be(0);

        // The mix places each WAV at that output time — the one place timing enters the graph.
        string graph = fixture.FilterGraph!;
        graph.Should().Contain("adelay=4000|4000[vo1]");
        graph.Should().Contain("adelay=9000|9000[vo2]");

        // Captions are cut from the same placed lines (cue selection/timing is this stage's job).
        JsonObject captions = fixture.Edl!["captions"]!.AsObject();
        List<(string LineId, double Start)> cueStarts = captions["cues"]!.AsArray()
            .Select(c => (c!["lineId"]!.GetValue<string>(), c["outputStartSec"]!.GetValue<double>()))
            .GroupBy(c => c.Item1).Select(g => (g.Key, g.Min(x => x.Item2))).ToList();
        cueStarts.Should().Contain(("vo1", 4.0)).And.Contain(("vo2", 9.0));

        fixture.Output!["warnings"].Should().BeNull("every declared line was placed");
    }

    /// <summary>
    /// The production shape of run ee0ec9ff: a 4-line plan on an edit that kept two clips, where the
    /// anchors of vo1 and vo2 were cut away. BEFORE this fix the compile reported
    /// <c>appliedLineCount 2</c> of <c>lineCount 4</c> with vo1/vo2 listed in <c>dropped</c> as
    /// line_cut_away — the film shipped with half its narration missing and still read Passed. Now
    /// both lines are re-anchored onto the nearest kept span that can fit them and all four are spoken.
    /// </summary>
    [Fact]
    public async Task Narration_whose_anchors_the_edit_cut_away_is_reanchored_onto_the_kept_clips()
    {
        var fixture = new CompileFixture(ThreeSourceArtifact());
        fixture.Voiceover = VoiceoverJson(
            ("vo0", 0.0, 1.0, "s0", "First."),
            ("vo1", 1.0, 2.0, "s1", "Second, on a clip the editor did not keep."),
            ("vo2", 3.0, 2.0, "s1", "Third, on the same cut-away clip."),
            ("vo3", 5.0, 1.5, "s2", "Fourth."));
        fixture.Config = c => c with { EnableVoiceover = true, VoiceoverStepOrder = VoiceoverStepOrder, PrePaddingMs = 0, PostPaddingMs = 0 };

        // s0 (4 s) and s2 (6 s) survive as the two kept spans: [0,4) and [4,10).
        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0", "s2"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject voiceover = fixture.Edl!["voiceover"]!.AsObject();
        voiceover["lineCount"]!.GetValue<int>().Should().Be(4);
        voiceover["appliedLineCount"]!.GetValue<int>().Should().Be(
            4, "the pre-fix failure this test quotes was appliedLineCount 2 of 4 — no line may be dropped");
        voiceover["reanchoredLineCount"]!.GetValue<int>().Should().Be(2);
        voiceover["dropped"]!.AsArray().Should().BeEmpty("both cut-away anchors found a kept span that fits");

        var lines = voiceover["lines"]!.AsArray().Select(n => n!.AsObject()).ToDictionary(l => l["lineId"]!.GetValue<string>());
        lines.Should().HaveCount(4);
        lines["vo0"]["outputStartSec"]!.GetValue<double>().Should().Be(0.0);
        lines["vo1"]["outputStartSec"]!.GetValue<double>().Should().Be(1.0, "the nearest kept span starts at 0 s, pushed after vo0");
        lines["vo2"]["outputStartSec"]!.GetValue<double>().Should().Be(3.0);
        lines["vo3"]["outputStartSec"]!.GetValue<double>().Should().Be(5.0, "s2's clip still starts at 4 s, pushed after the re-anchored lines");

        // The moved lines say so, and still name the anchor they were written for.
        lines["vo1"]["reanchored"]!.GetValue<bool>().Should().BeTrue();
        lines["vo1"]["reanchoredFrom"]!.GetValue<string>().Should().Be("anchor_cut_away");
        lines["vo1"]["anchorId"]!.GetValue<string>().Should().Be("s1");
        lines["vo1"]["sourceIndex"]!.GetValue<int>().Should().Be(0, "the kept span it now speaks over is s0's");
        lines["vo2"]["reanchored"]!.GetValue<bool>().Should().BeTrue();
        lines["vo0"].ContainsKey("reanchored").Should().BeFalse();
        lines["vo3"].ContainsKey("reanchored").Should().BeFalse();

        fixture.Output!["warnings"].Should().BeNull("every declared line was placed, so nothing was dropped");
    }

    /// <summary>
    /// A cut-away line is re-anchored only onto a kept span LONG ENOUGH to speak it. When none is —
    /// the line is longer than every kept clip — the compile must fail loudly rather than ship a
    /// green film missing it, naming the line and the reason, and the failure is refunded.
    /// </summary>
    [Fact]
    public async Task A_line_longer_than_every_kept_span_fails_with_NARRATION_ANCHOR_LOST()
    {
        var fixture = new CompileFixture(ThreeSourceArtifact());
        fixture.Voiceover = VoiceoverJson(
            ("vo0", 0.0, 1.0, "s0", "First."),
            ("vo1", 1.0, 20.0, "s1", "A line far longer than either kept clip."),
            ("vo2", 3.0, 1.0, "s2", "Third."));
        fixture.Config = c => c with { EnableVoiceover = true, VoiceoverStepOrder = VoiceoverStepOrder, PrePaddingMs = 0, PostPaddingMs = 0 };

        // Kept spans are 4 s and 6 s; nothing can hold 20 s of speech.
        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0", "s2"));

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("NARRATION_ANCHOR_LOST");
        result.FailureClass.Should().Be(FailureClass.PlatformInternal, "this is our edit losing the narration, so it refunds");
        using JsonDocument error = JsonDocument.Parse(result.Output);
        error.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("NARRATION_ANCHOR_LOST");
        result.ErrorDetails.Should().Contain("vo1").And.Contain("s1").And.Contain("cut away")
            .And.Contain("20");
        fixture.EncodeArgs.Should().BeNull("nothing is rendered when a line cannot be spoken anywhere");
    }

    /// <summary>
    /// The other production shape, run f211b7e1: vo3 speaks 5.782 s starting at 10 s, running into a
    /// window that ends at 14.56 s. That is the pre-existing fit measurement (a line may overflow its
    /// shot; the reviewer reads overflowSec/fits) and this fix must leave it exactly as it was — no
    /// re-anchor, no anchor-lost failure, still Completed.
    /// </summary>
    [Fact]
    public async Task A_line_that_overflows_its_window_keeps_the_existing_fit_failure_and_is_not_reanchored()
    {
        VideoAnalysisArtifact artifact = Artifact(
            (0, new VideoAnalysisMedia(14.56, 30, 1, 1920, 1080), "s0"),
            (1, new VideoAnalysisMedia(6, 30, 1, 1280, 720), "s1"));
        var fixture = new CompileFixture(artifact);
        fixture.Voiceover = VoiceoverJson(("vo3", 10.0, 5.782, null, "This line runs past the end of its shot."));
        fixture.Config = c => c with { EnableVoiceover = true, VoiceoverStepOrder = VoiceoverStepOrder, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject voiceover = fixture.Edl!["voiceover"]!.AsObject();
        JsonObject line = voiceover["lines"]!.AsArray().Single()!.AsObject();
        line["lineId"]!.GetValue<string>().Should().Be("vo3");
        line["outputStartSec"]!.GetValue<double>().Should().Be(10.0, "the declared start still maps straight through source 0");
        line["playDurationSec"]!.GetValue<double>().Should().Be(5.782);
        line["windowEndSec"]!.GetValue<double>().Should().BeApproximately(14.56, 0.05);
        line["overflowSec"]!.GetValue<double>().Should().BeApproximately(
            10.0 + 5.782 - line["windowEndSec"]!.GetValue<double>(), 1e-6, "the line speaks past the window's end");
        line["overflowSec"]!.GetValue<double>().Should().BeGreaterThan(0);
        line["fits"]!.GetValue<bool>().Should().BeFalse();
        line.ContainsKey("reanchored").Should().BeFalse("an overflow is not a lost anchor");
        line.ContainsKey("reanchoredFrom").Should().BeFalse();
        voiceover.ContainsKey("reanchoredLineCount").Should().BeFalse();
        voiceover["fit"]!["overflowLineCount"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Anchored_lines_never_talk_over_each_other_and_a_line_pushed_past_the_end_is_dropped()
    {
        // Two placements: [0,4) from source 0, [4,9) from source 1.
        (OutputTimeline timeline, List<VideoCompileStepExecutor.ResolvedSpan> spans) =
            TimelineWithSpans((0, 0.0, 4.0), (1, 0.0, 5.0));
        var idTimes = new Dictionary<string, (double Start, double End, int SourceIndex)>
        {
            ["s0"] = (0.0, 4.0, 0),
            ["s1"] = (0.0, 5.0, 1),
            ["s9"] = (0.0, 2.0, 7) // a clip that is not in the cut at all
        };

        IReadOnlyList<VideoCompileStepExecutor.VoiceoverLinePlacement> placed =
            VideoCompileStepExecutor.PlanVoiceoverLinePlacements(
            [
                ("vo0", 0.0, 6.0, "s0"),    // runs 2 s past its clip
                ("vo1", 6.0, 2.0, "s1"),    // would start at 4.0 — pushed to 6.0
                ("vo2", 8.0, 3.0, "s1"),    // same anchor: pushed to 8.0, still inside the 9 s program
                ("vo3", 11.0, 1.0, "s1"),   // pushed to 11.0 — past the end
                ("vo4", 0.0, 10.0, "s9"),   // anchor never kept AND longer than every kept span
                ("vo5", 1.5, 1.0, null)     // unanchored: declared start on source 0, unchanged behaviour
            ],
            idTimes, timeline, spans);

        placed[0].OutputStartSec.Should().Be(0.0);
        placed[1].OutputStartSec.Should().Be(6.0);
        placed[1].ShiftSec.Should().Be(2.0);
        placed[2].OutputStartSec.Should().Be(8.0);
        placed[3].OutputStartSec.Should().BeNull();
        placed[3].DropReason.Should().Be("line_past_program_end");
        placed[4].OutputStartSec.Should().BeNull("a line longer than every kept span cannot be spoken anywhere");
        placed[4].DropReason.Should().Be(VideoCompileStepExecutor.NarrationAnchorLostReason);
        placed[4].CutAwayReason.Should().Be("anchor_cut_away");
        placed[4].Reanchored.Should().BeFalse();
        placed[5].OutputStartSec.Should().Be(1.5);
        placed[5].Anchored.Should().BeFalse();
        placed[5].SourceIndex.Should().Be(0);
    }

    [Fact]
    public void An_anchor_whose_first_seconds_were_cut_starts_where_its_kept_part_starts()
    {
        // Source 0 kept from 2.0 (the shot began at 0.0).
        (OutputTimeline timeline, List<VideoCompileStepExecutor.ResolvedSpan> spans) =
            TimelineWithSpans((0, 2.0, 6.0));
        IReadOnlyList<VideoCompileStepExecutor.VoiceoverLinePlacement> placed =
            VideoCompileStepExecutor.PlanVoiceoverLinePlacements(
                [("vo0", 0.0, 1.0, "s0")],
                new Dictionary<string, (double Start, double End, int SourceIndex)> { ["s0"] = (0.0, 6.0, 0) },
                timeline, spans);

        placed[0].OutputStartSec.Should().Be(0.0, "the anchor's first surviving second is the program's first");
    }

    [Fact]
    public void A_source_that_returns_later_in_the_cut_with_an_earlier_part_of_itself_still_maps()
    {
        // Clip A 10-15 s, clip B 0-3 s, then clip A again 2-5 s.
        OutputTimeline timeline = Timeline((0, 10.0, 15.0), (1, 0.0, 3.0), (0, 2.0, 5.0));

        timeline.MapToOutputSec(3.0, 0).Should().Be(9.0, "3 s of clip A is kept in the THIRD span, which starts at 8 s");
        timeline.MapToOutputSec(12.0, 0).Should().Be(2.0);
        timeline.MapToOutputSec(7.0, 0).Should().BeNull();
    }

    // ==========================================================================================
    // Finding 42 — narration on silent footage
    // ==========================================================================================

    [Fact]
    public async Task Single_silent_source_with_narration_gets_a_silent_base_on_the_select_path()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0), sourcesHaveAudio: false);
        fixture.Voiceover = VoiceoverJson(("vo0", 2.0, 3.0, null, "Hello."));
        fixture.Config = c => c with { EnableVoiceover = true, VoiceoverStepOrder = VoiceoverStepOrder, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Scripted.Should().BeFalse("a single clip with no transitions keeps the select path");
        fixture.FilterGraph.Should().Be(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,select='gte(t,0)*lt(t,10)',setpts=N/FRAME_RATE/TB[vout];" +
            "anullsrc=r=48000:cl=stereo:d=10[abase];" +
            "[1:a]atrim=end=3,asetpts=N/SR/TB,aformat=sample_rates=48000:channel_layouts=stereo,volume=1,adelay=2000|2000[vo0];" +
            "[abase][vo0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]");
        fixture.EncodeArgs.Should().ContainInOrder("-map", "[aout]");
        fixture.EncodeArgs.Should().Contain("-c:a");

        JsonObject audio = fixture.Output!["audio"]!.AsObject();
        audio["silentBase"]!.GetValue<bool>().Should().BeTrue();
        audio["programAudio"]!.GetValue<bool>().Should().BeTrue();
        fixture.Output!["voiceover"]!["applied"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Several_silent_sources_with_narration_get_a_silent_base_on_the_segmented_path()
    {
        var fixture = new CompileFixture(ThreeSourceArtifact(), sourcesHaveAudio: false);
        fixture.Voiceover = VoiceoverJson(("vo0", 0.0, 2.0, "s1", "Over clip two."));
        fixture.Config = c => c with { EnableVoiceover = true, VoiceoverStepOrder = VoiceoverStepOrder, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0", "s1"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Scripted.Should().BeTrue();
        // Clip sizes differ (1920x1080 and 1280x720); the canvas is the larger one, and the
        // smaller clip is scaled into it. Each silent span gets its own matching-duration silence.
        fixture.FilterGraph.Should().Be(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=4.066667,trim=start=0:end=4,setpts=PTS-STARTPTS,scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30/1,tpad=stop_mode=clone:stop_duration=4,trim=end=4[v0];" +
            "anullsrc=r=48000:cl=stereo:d=4[a0];" +
            "[1:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=5.066667,trim=start=0:end=5,setpts=PTS-STARTPTS,scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps=30/1,tpad=stop_mode=clone:stop_duration=5,trim=end=5[v1];" +
            "anullsrc=r=48000:cl=stereo:d=5[a1];" +
            // The concat ends at [aout] and the narration mix re-emits [aout]: ffmpeg links a label
            // to its next use, so this is the segmented path's established (valid) labelling.
            "[v0][a0][v1][a1]concat=n=2:v=1:a=1[vout][aout];" +
            "[2:a]atrim=end=2,asetpts=N/SR/TB,aformat=sample_rates=48000:channel_layouts=stereo,volume=1,adelay=4000|4000[vo0];" +
            "[aout][vo0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]");
        fixture.EncodeArgs.Should().ContainInOrder("-map", "[aout]");
        fixture.Output!["audio"]!["silentBase"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public async Task Silent_source_with_nothing_to_mix_still_has_no_audio_track()
    {
        // Unchanged: no narration, music or effects means there is nothing for a silent track to carry.
        var fixture = new CompileFixture(SingleSourceArtifact(10.0), sourcesHaveAudio: false);

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().NotContain("anullsrc");
        fixture.EncodeArgs.Should().NotContain("[aout]");
        fixture.Output!["audio"]!["applied"]!.GetValue<bool>().Should().BeFalse();
        fixture.Output!["audio"]!.AsObject().ContainsKey("silentBase").Should().BeFalse();
    }

    // ==========================================================================================
    // Finding 41 — dropped narration must not look like success
    // ==========================================================================================

    [Fact]
    public void More_than_half_of_the_narration_missing_is_its_own_warning()
    {
        JsonObject node = new() { ["lineCount"] = 8, ["appliedLineCount"] = 1 };
        List<JsonObject> warnings = Warnings(new VideoCompileStepConfig(Version: 1, Decision: AnyDecision, AnalysisStepOrder: 1), node);

        warnings.Should().ContainSingle();
        warnings[0]["code"]!.GetValue<string>().Should().Be("narration_mostly_dropped");
        warnings[0]["message"]!.GetValue<string>().Should().Contain("Only 1 of 8");
    }

    [Fact]
    public void Exactly_half_missing_is_a_plain_narration_dropped_warning_and_nothing_missing_is_none()
    {
        Warnings(new VideoCompileStepConfig(Version: 1, Decision: AnyDecision, AnalysisStepOrder: 1), new JsonObject { ["lineCount"] = 4, ["appliedLineCount"] = 2 })
            .Single()["code"]!.GetValue<string>().Should().Be("narration_dropped");
        Warnings(new VideoCompileStepConfig(Version: 1, Decision: AnyDecision, AnalysisStepOrder: 1), new JsonObject { ["lineCount"] = 4, ["appliedLineCount"] = 4 })
            .Should().BeEmpty();
        Warnings(new VideoCompileStepConfig(Version: 1, Decision: AnyDecision, AnalysisStepOrder: 1), new JsonObject { ["lineCount"] = 0, ["appliedLineCount"] = 0 })
            .Should().BeEmpty("an empty narration plan asked for nothing");
    }

    [Fact]
    public void Review_loop_caps_the_score_when_more_than_half_the_narration_is_missing()
    {
        string mostlyLost = """{"status":"completed","voiceover":{"applied":true,"lineCount":8,"appliedLineCount":1}}""";
        string halfLost = """{"status":"completed","voiceover":{"applied":true,"lineCount":8,"appliedLineCount":4}}""";
        string allPlaced = """{"status":"completed","voiceover":{"applied":true,"lineCount":8,"appliedLineCount":8}}""";

        ReviewLoopStepExecutor.ApplyNarrationCaps(9, mostlyLost).Should().Be(ReviewLoopStepExecutor.NarrationMostlyDroppedScoreCap);
        ReviewLoopStepExecutor.ApplyNarrationCaps(9, halfLost).Should().Be(9, "exactly half is not 'mostly'");
        ReviewLoopStepExecutor.ApplyNarrationCaps(9, allPlaced).Should().Be(9);
        ReviewLoopStepExecutor.ApplyNarrationCaps(3, mostlyLost).Should().Be(3, "a cap never raises a score");
        ReviewLoopStepExecutor.NarrationCapsWouldFire(mostlyLost).Should().BeTrue(
            "the calibrated early stop must escalate to the reviewer instead of passing a mostly-mute video");
        ReviewLoopStepExecutor.ApplyNarrationMostlyDroppedCap(9, """{"status":"completed"}""").Should().Be(9);
        ReviewLoopStepExecutor.ApplyNarrationMostlyDroppedCap(9, "not json").Should().Be(9);
    }

    // ==========================================================================================
    // Finding 38 + output format — the canvas
    // ==========================================================================================

    [Fact]
    public async Task Multi_clip_canvas_is_the_largest_source_not_the_first()
    {
        // The first kept clip is the SMALL one (1344x768, a generated clip); the second is 1080p.
        VideoAnalysisArtifact artifact = Artifact(
            (0, new VideoAnalysisMedia(4, 24, 1, 1344, 768), "s0"),
            (1, new VideoAnalysisMedia(5, 30, 1, 1920, 1080), "s1"));
        var fixture = new CompileFixture(artifact);

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0", "s1"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain("scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080");
        fixture.FilterGraph.Should().Contain("fps=30/1", "the canonical clip also supplies the frame rate");
        fixture.FilterGraph.Should().NotContain("1344:768");
        JsonObject output = fixture.Output!["output"]!.AsObject();
        output["format"]!.GetValue<string>().Should().Be("Source");
        output["width"]!.GetValue<int>().Should().Be(1920);
        output["height"]!.GetValue<int>().Should().Be(1080);
        output["fit"]!.GetValue<string>().Should().Be("Pad");
    }

    [Fact]
    public void Canonical_media_ties_go_to_the_clip_kept_first()
    {
        VideoAnalysisArtifact artifact = Artifact(
            (0, new VideoAnalysisMedia(4, 25, 1, 1920, 1080), "s0"),
            (1, new VideoAnalysisMedia(5, 30, 1, 1920, 1080), "s1"));

        VideoCompileStepExecutor.SelectCanonicalMedia(artifact, [1, 0], 1).FpsNum.Should().Be(30);
        VideoCompileStepExecutor.SelectCanonicalMedia(artifact, [0, 1], 0).FpsNum.Should().Be(25);
    }

    [Fact]
    public void A_composition_owns_the_frame_even_when_a_larger_landscape_photo_is_kept()
    {
        // Staging run 1e9c441f: a 9:16 composition with 1920x1280 photos placed into it came out
        // 1920x1280 with the reel padded inside, because "largest kept source" picked a photo.
        VideoAnalysisArtifact artifact = Artifact(
            (0, new VideoAnalysisMedia(120, 30, 1, 1080, 1920), "s0"),
            (2, new VideoAnalysisMedia(5, 30, 1, 1920, 1280), "s24"));
        artifact = artifact with
        {
            Sources = [.. artifact.Sources!.Select(src => src.SourceIndex == 0 ? src with { IsComposition = true } : src)]
        };

        VideoAnalysisMedia canvas = VideoCompileStepExecutor.SelectCanonicalMedia(artifact, [0, 2, 0], 0);

        canvas.Width.Should().Be(1080);
        canvas.Height.Should().Be(1920);
    }

    [Fact]
    public void Without_a_composition_the_frame_keeps_the_first_clips_shape()
    {
        VideoAnalysisArtifact artifact = Artifact(
            (0, new VideoAnalysisMedia(30, 30, 1, 1080, 1920), "s0"),
            (1, new VideoAnalysisMedia(5, 30, 1, 3840, 2160), "s1"),
            (2, new VideoAnalysisMedia(5, 30, 1, 1440, 2560), "s2"));

        // A bigger PORTRAIT clip still wins (the original b-roll fix), a bigger landscape one does not.
        VideoAnalysisMedia canvas = VideoCompileStepExecutor.SelectCanonicalMedia(artifact, [0, 1, 2], 0);
        canvas.Width.Should().Be(1440);
        canvas.Height.Should().Be(2560);
    }

    [Fact]
    public async Task Vertical_crop_on_a_single_landscape_source_centre_crops_on_the_select_path()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0));
        fixture.Config = c => c with { OutputFormat = VideoOutputFormat.Vertical, OutputFit = VideoOutputFit.Crop, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Scripted.Should().BeFalse();
        fixture.FilterGraph.Should().StartWith(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,select='gte(t,0)*lt(t,10)',setpts=N/FRAME_RATE/TB," +
            "scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1[vout]");
        JsonObject output = fixture.Output!["output"]!.AsObject();
        output["format"]!.GetValue<string>().Should().Be("Vertical");
        output["width"]!.GetValue<int>().Should().Be(1080);
        output["height"]!.GetValue<int>().Should().Be(1920);
        output["fit"]!.GetValue<string>().Should().Be("Crop");
    }

    [Fact]
    public async Task Square_pad_with_black_fill_pads_on_the_select_path()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0));
        fixture.Config = c => c with { OutputFormat = VideoOutputFormat.Square, OutputFit = VideoOutputFit.Pad, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain(
            "setpts=N/FRAME_RATE/TB,scale=1080:1080:force_original_aspect_ratio=decrease,pad=1080:1080:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1[vout]");
    }

    [Fact]
    public async Task Vertical_pad_with_blur_fill_routes_a_single_source_to_the_segmented_path()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0));
        fixture.Config = c => c with
        {
            OutputFormat = VideoOutputFormat.Vertical,
            OutputFit = VideoOutputFit.Pad,
            CanvasFill = VideoCanvasFill.Blur,
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Scripted.Should().BeTrue();
        fixture.FilterGraph.Should().Contain("gblur=sigma=40");
        fixture.FilterGraph.Should().Contain("scale=1080:1920:force_original_aspect_ratio=decrease[vfgs0]");
    }

    [Fact]
    public async Task Landscape_crop_fits_every_clip_of_a_multi_clip_edit_by_cover()
    {
        var fixture = new CompileFixture(ThreeSourceArtifact());
        fixture.Config = c => c with { OutputFormat = VideoOutputFormat.Landscape, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0", "s1"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=4.066667,trim=start=0:end=4,setpts=PTS-STARTPTS,scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,setsar=1,fps=30/1,tpad=stop_mode=clone:stop_duration=4,trim=end=4[v0]");
        fixture.FilterGraph.Should().Contain("[1:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=5.066667,trim=start=0:end=5,setpts=PTS-STARTPTS,scale=1920:1080:force_original_aspect_ratio=increase,crop=1920:1080,setsar=1,fps=30/1,tpad=stop_mode=clone:stop_duration=5,trim=end=5[v1]");
    }

    // ==========================================================================================
    // OutputFit=Subject — reframing to the subject instead of the centre
    // ==========================================================================================

    [Fact]
    public async Task Vertical_subject_crop_places_a_landscape_source_on_its_located_subject()
    {
        // A phone held left of centre (x 0.2..0.4): a centre crop keeps x 0.34..0.66 and loses it.
        VideoAnalysisArtifact artifact = SingleSourceArtifact(10.0);
        artifact = artifact with { Shots = [artifact.Shots[0] with { Subject = new VideoAnalysisRect(0.2, 0.3, 0.2, 0.4) }] };
        var fixture = new CompileFixture(artifact);
        fixture.Config = c => c with { OutputFormat = VideoOutputFormat.Vertical, OutputFit = VideoOutputFit.Subject, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Scripted.Should().BeFalse();
        fixture.FilterGraph.Should().Contain(
            "scale=1080:1920:force_original_aspect_ratio=increase," +
            "crop=1080:1920:'max(0,min(iw-ow,0.3*iw-ow/2))':'max(0,min(ih-oh,0.5*ih-oh/2))',setsar=1[vout]");
        JsonObject output = fixture.Output!["output"]!.AsObject();
        output["fit"]!.GetValue<string>().Should().Be("Subject");
        JsonObject focus = output["focus"]!.AsArray().Single()!.AsObject();
        focus["x"]!.GetValue<double>().Should().Be(0.3);
        focus["basis"]!.GetValue<string>().Should().Be("subject");
    }

    /// <summary>A caption that read no on-screen text: the text question is answered, so Subject fit crops rather than pads.</summary>
    private static VideoShotCaption NoTextCaption(string shotId) =>
        new(shotId, "s", [], "", "", "", "Wide", "", [], [], "Unknown", "", "", "", []);

    [Fact]
    public async Task Subject_crop_with_nothing_located_is_exactly_the_centre_crop()
    {
        VideoAnalysisArtifact single = SingleSourceArtifact(10.0);
        var fixture = new CompileFixture(single with { Shots = [single.Shots[0] with { Caption = NoTextCaption(single.Shots[0].Id) }] });
        fixture.Config = c => c with { OutputFormat = VideoOutputFormat.Vertical, OutputFit = VideoOutputFit.Subject, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain("force_original_aspect_ratio=increase,crop=1080:1920,setsar=1[vout]");
        fixture.Output!["output"]!["focus"]!.AsArray().Single()!["basis"]!.GetValue<string>().Should().Be("centre");
    }

    [Fact]
    public async Task Subject_crop_places_each_clip_of_a_multi_clip_edit_on_its_own_subject()
    {
        VideoAnalysisArtifact artifact = ThreeSourceArtifact();
        artifact = artifact with
        {
            Shots =
            [
                artifact.Shots[0] with { Subject = new VideoAnalysisRect(0.6, 0.2, 0.2, 0.5) },
                artifact.Shots[1] with { Caption = NoTextCaption(artifact.Shots[1].Id) },
                artifact.Shots[2]
            ]
        };
        var fixture = new CompileFixture(artifact);
        fixture.Config = c => c with { OutputFormat = VideoOutputFormat.Vertical, OutputFit = VideoOutputFit.Subject, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0", "s1"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=4.066667,trim=start=0:end=4,setpts=PTS-STARTPTS,scale=1080:1920:force_original_aspect_ratio=increase," +
            "crop=1080:1920:'max(0,min(iw-ow,0.7*iw-ow/2))':'max(0,min(ih-oh,0.5*ih-oh/2))',setsar=1,fps=30/1,tpad=stop_mode=clone:stop_duration=4,trim=end=4[v0]");
        fixture.FilterGraph.Should().Contain(
            "[1:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=5.066667,trim=start=0:end=5,setpts=PTS-STARTPTS,scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30/1,tpad=stop_mode=clone:stop_duration=5,trim=end=5[v1]");
    }

    [Fact]
    public void A_tracked_screen_outranks_a_located_subject()
    {
        VideoAnalysisArtifact artifact = SingleSourceArtifact(10.0);
        var track = new VideoInsertRegionTrack(
            "r0", "s0", 0, 10,
            [new VideoInsertQuadKeyframe(2.0, 0.60, 0.3, 0.70, 0.3, 0.60, 0.7, 0.70, 0.7)],
            Confidence: 0.9, MeanAreaRatio: 0.04, MeanAspectRatio: 0.5, MotionClass: "Static", ColorName: "green");
        artifact = artifact with
        {
            Shots = [artifact.Shots[0] with { Subject = new VideoAnalysisRect(0.1, 0.1, 0.2, 0.2) }],
            InsertRegions = [track]
        };

        IReadOnlyDictionary<int, CropFocus> focus = CropFocusResolver.Resolve(
            artifact, [(0, 0.0, 10.0)], _ => artifact.Media, new VideoAnalysisMedia(10, 30, 1, 1080, 1920));

        focus[0].Basis.Should().Be("tracked");
        focus[0].X.Should().Be(0.65);
    }

    [Fact]
    public void A_subject_wider_than_the_crop_keeps_the_window_inside_it_on_its_weighted_centre()
    {
        // Two located boxes far apart, the left one on screen four times as long. Both cannot fit the
        // 9:16 window (0.316 of the frame), so the window keeps the left one WHOLE — the weighted
        // centre (0.29) would have halved it and a union centre (0.5) would show neither.
        var boxes = new List<CropFocusResolver.WeightedBox>
        {
            new(0.05, 0.4, 0.25, 0.6, 4.0, "s0"),
            new(0.75, 0.4, 0.95, 0.6, 1.0, "s1")
        };

        CropFocus focus = CropFocusResolver.Place(boxes, 0.316, 1.0, "subject");

        focus.X.Should().Be(0.208); // window 0.05..0.366: the whole left box, as close to 0.29 as that allows
        focus.Y.Should().Be(0.5);
        focus.Pad.Should().BeFalse("the main subject is kept whole; a secondary one not fitting is no reason to pad");
    }

    [Fact]
    public void A_subject_no_crop_can_keep_whole_is_shown_whole_instead()
    {
        // An end card's headline spanning 0.30..0.71 of a 16:9 frame: a 9:16 window (0.316 wide)
        // would keep 77% of it and cut words off, so the clip is padded rather than cropped.
        CropFocus wide = CropFocusResolver.Place([new(0.30, 0.35, 0.71, 0.65, 1)], 0.316, 1.0, "subject");
        wide.Pad.Should().BeTrue();
        wide.IsCentre.Should().BeFalse();

        // A laptop screen 0.29 wide fits and is cropped.
        CropFocusResolver.Place([new(0.33, 0.3, 0.62, 0.7, 1)], 0.316, 1.0, "subject").Pad.Should().BeFalse();

        var source = new VideoAnalysisMedia(10, 30, 1, 1920, 1080);
        var canvas = new VideoAnalysisMedia(10, 30, 1, 1080, 1920);
        VideoCompileStepExecutor.SourceCanvasFit.For(source, canvas, cover: true, wide)
            .Should().Be(VideoCompileStepExecutor.SourceCanvasFit.For(source, canvas, cover: false));
    }

    [Fact]
    public async Task Subject_crop_pads_only_the_clip_whose_subject_will_not_fit_with_the_canvas_fill()
    {
        VideoAnalysisArtifact artifact = ThreeSourceArtifact();
        artifact = artifact with
        {
            Shots =
            [
                artifact.Shots[0] with { Subject = new VideoAnalysisRect(0.4, 0.3, 0.2, 0.4) },  // fits: cropped
                artifact.Shots[1] with { Subject = new VideoAnalysisRect(0.25, 0.3, 0.5, 0.4) }, // too wide: padded
                artifact.Shots[2]
            ]
        };
        var fixture = new CompileFixture(artifact);
        fixture.Config = c => c with
        {
            OutputFormat = VideoOutputFormat.Vertical,
            OutputFit = VideoOutputFit.Subject,
            CanvasFill = VideoCanvasFill.Blur,
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0", "s1"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=4.066667,trim=start=0:end=4,setpts=PTS-STARTPTS,scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30/1,tpad=stop_mode=clone:stop_duration=4,trim=end=4[v0]");
        fixture.FilterGraph.Should().Contain("[1:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=5.066667,trim=start=0:end=5,setpts=PTS-STARTPTS,fps=30/1,tpad=stop_mode=clone:stop_duration=5,trim=end=5,split[vfg1][vbg1]");
        fixture.FilterGraph.Should().Contain("gblur=sigma=40");
        JsonArray focus = fixture.Output!["output"]!["focus"]!.AsArray();
        focus[0]!["fit"].Should().BeNull();
        focus[1]!["fit"]!.GetValue<string>().Should().Be("Pad");
    }

    [Fact]
    public void The_geometry_fit_mirrors_the_focused_crop_ffmpeg_takes()
    {
        var source = new VideoAnalysisMedia(10, 30, 1, 1920, 1080);
        var canvas = new VideoAnalysisMedia(10, 30, 1, 1080, 1920);

        VideoCompileStepExecutor.SourceCanvasFit fit = VideoCompileStepExecutor.SourceCanvasFit.For(
            source, canvas, cover: true, new CropFocus(0.33, 0.5, "subject"));

        // ffmpeg: iw = 1920 * 1920/1080 = 3413.33, x = clamp(0.33*iw - 540, 0, iw - 1080) = 586.4.
        fit.OffsetX.Should().BeApproximately(-586.4, 1e-6);
        fit.OffsetY.Should().Be(0);
        VideoCompileStepExecutor.SourceCanvasFit.For(source, canvas, cover: true, new CropFocus(0.99, 0.5, "subject"))
            .OffsetX.Should().BeApproximately(-(3413.3333333 - 1080), 1e-4);
        VideoCompileStepExecutor.SourceCanvasFit.For(source, canvas, cover: true, CropFocus.Centre)
            .Should().Be(VideoCompileStepExecutor.SourceCanvasFit.For(source, canvas, cover: true));
    }

    [Fact]
    public async Task A_fixed_output_format_with_stream_copy_is_a_config_error()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0));
        fixture.Config = c => c with { OutputFormat = VideoOutputFormat.Vertical, Mode = VideoCompileMode.StreamCopy, AllowKeyframeSnapping = true };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Failed);
        JsonDocument.Parse(result.Output).RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("OUTPUT_FORMAT_REQUIRES_REENCODE");
        fixture.EncodeArgs.Should().BeNull("nothing is encoded for a config error");
    }

    [Fact]
    public async Task A_stream_copy_source_compile_with_an_explicit_resolution_is_encoded_at_that_size()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0))
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId, OutputResolution = 2160 }
        };
        fixture.Config = c => c with { OutputFormat = VideoOutputFormat.Source, Mode = VideoCompileMode.StreamCopy, AllowKeyframeSnapping = true };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.EncodeArgs.Should().NotBeNull("a chosen resolution forces a re-encode, never a stream copy");

        // QA-P0-24: pin the REAL encoded size, not "some scale filter exists". The source is
        // 1920x1080 and the run asked for a 2160 short side, so the canvas is 3840x2160 and the
        // source is PADDED into it (OutputFormat=Source never crops) — the exact scale expression
        // the graph emits and the exact pixel dimensions. Both numbers come from the config's own
        // arithmetic (source x shortSide/min(source) x 2), never from the planned canvas, so a
        // canvas planned wrong — the 1080 class, a cover/crop instead of a pad, a swapped axis —
        // fails here where `Contain("scale=")` passed for any of them.
        fixture.FilterGraph.Should().Contain(
            "scale=3840:2160:force_original_aspect_ratio=decrease,pad=3840:2160:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1");
        JsonObject res = fixture.Output!["outputResolution"]!.AsObject();
        res["honoured"]!.GetValue<bool>().Should().BeTrue();
        res["width"]!.GetValue<int>().Should().Be(3840);
        res["height"]!.GetValue<int>().Should().Be(2160);
        fixture.Output["output"]!["width"]!.GetValue<int>().Should().Be(3840);
        fixture.Output["output"]!["height"]!.GetValue<int>().Should().Be(2160);
    }

    [Fact]
    public void Cover_fit_overflows_the_canvas_and_maps_geometry_through_the_crop()
    {
        var source = new VideoAnalysisMedia(10, 30, 1, 1920, 1080);
        var canvas = new VideoAnalysisMedia(10, 30, 1, 1080, 1920);

        VideoCompileStepExecutor.SourceCanvasFit cover = VideoCompileStepExecutor.SourceCanvasFit.For(source, canvas, cover: true);
        cover.Height.Should().BeApproximately(1920, 1e-9);
        cover.Width.Should().BeApproximately(1920 * (1920 / 1080.0), 1e-6);
        cover.OffsetX.Should().BeApproximately((1080 - cover.Width) / 2, 1e-6);
        cover.OffsetY.Should().Be(0);

        // A centred source band stays centred and full height; its sides are cropped to the canvas.
        VideoAnalysisRect centre = VideoCompileStepExecutor.MapRectThroughFit(new VideoAnalysisRect(0.4, 0.8, 0.2, 0.1), cover, canvas)!;
        centre.X.Should().BeApproximately((cover.OffsetX + 0.4 * cover.Width) / 1080, 1e-9);
        centre.Y.Should().BeApproximately(0.8, 1e-9);
        centre.H.Should().BeApproximately(0.1, 1e-9);

        VideoCompileStepExecutor.MapRectThroughFit(new VideoAnalysisRect(0.0, 0.8, 0.1, 0.1), cover, canvas)
            .Should().BeNull("a band at the far left edge of a 16:9 frame is cropped out of 9:16");

        // Pad keeps the whole picture inside the canvas: a full-frame rect becomes the letterbox.
        VideoCompileStepExecutor.SourceCanvasFit pad = VideoCompileStepExecutor.SourceCanvasFit.For(source, canvas);
        VideoAnalysisRect full = VideoCompileStepExecutor.MapRectThroughFit(new VideoAnalysisRect(0, 0, 1, 1), pad, canvas)!;
        full.X.Should().Be(0);
        full.W.Should().BeApproximately(1, 1e-9);
        full.H.Should().BeApproximately(1080 * (1080 / 1920.0) / 1920, 1e-9);
    }

    // ==========================================================================================
    // Finding 44 + item 12 — length and "nothing to choose from" warnings
    // ==========================================================================================

    [Fact]
    public void Target_length_missed_by_more_than_20_percent_is_reported_and_warned()
    {
        var config = new VideoCompileStepConfig(Version: 1, Decision: AnyDecision, AnalysisStepOrder: 1, TargetDurationSec: 20);
        List<JsonObject> warnings = VideoCompileStepExecutor.BuildCompileWarnings(
            config, SingleSourceArtifact(10), null, false, 0, false, false, 10.0, out JsonObject? length);

        length!["targetSec"]!.GetValue<int>().Should().Be(20);
        length["actualSec"]!.GetValue<double>().Should().Be(10.0);
        length["met"]!.GetValue<bool>().Should().BeFalse();
        warnings.Single()["code"]!.GetValue<string>().Should().Be("target_length_missed");
        warnings.Single()["message"]!.GetValue<string>().Should().Contain("20-second").And.Contain("10 seconds");

        VideoCompileStepExecutor.BuildCompileWarnings(
            config, SingleSourceArtifact(10), null, false, 0, false, false, 17.0, out JsonObject? close);
        close!["met"]!.GetValue<bool>().Should().BeTrue("17 s is within 20% of 20 s");

        VideoCompileStepExecutor.BuildCompileWarnings(
            new VideoCompileStepConfig(Version: 1, Decision: AnyDecision, AnalysisStepOrder: 1), SingleSourceArtifact(10), null, false, 0, false, false, 10.0, out JsonObject? none);
        none.Should().BeNull("no target, no length node");
    }

    [Fact]
    public void A_length_lost_to_beat_snapping_is_blamed_on_the_snapping_not_the_footage()
    {
        // Run a60d5aae step 8: the fit reached 7 s, then cutting to the beat removed 1.5 s and the
        // warning said "there was not enough usable footage" — sending the customer for clips they
        // did not need.
        var config = new VideoCompileStepConfig(Version: 1, Decision: AnyDecision, AnalysisStepOrder: 1, TargetDurationSec: 7);
        List<JsonObject> warnings = VideoCompileStepExecutor.BuildCompileWarnings(
            config, SingleSourceArtifact(20), null, false, 0, false, false, 5.495, out JsonObject? length, beatSyncTrimmedSec: 1.505);

        length!["met"]!.GetValue<bool>().Should().BeFalse();
        JsonObject warning = warnings.Single();
        warning["code"]!.GetValue<string>().Should().Be("beat_sync_trimmed");
        warning["trimmedSec"]!.GetValue<double>().Should().Be(1.505);
        warning["message"]!.GetValue<string>().Should().Contain("cutting to the beat removed 1.5 seconds")
            .And.NotContain("not enough usable footage");

        // Short footage AND snapping: still the footage, with the snapping's share named.
        List<JsonObject> both = VideoCompileStepExecutor.BuildCompileWarnings(
            new VideoCompileStepConfig(Version: 1, Decision: AnyDecision, AnalysisStepOrder: 1, TargetDurationSec: 20),
            SingleSourceArtifact(10), null, false, 0, false, false, 9.5, out _, beatSyncTrimmedSec: 0.5);
        both.Single()["code"]!.GetValue<string>().Should().Be("target_length_missed");
        both.Single()["message"]!.GetValue<string>().Should().Contain("not enough usable footage")
            .And.Contain("cutting to the beat removed another 0.5 seconds");
    }

    [Fact]
    public void Music_or_sfx_enabled_with_nothing_uploaded_tells_the_customer_to_upload_audio()
    {
        var config = new VideoCompileStepConfig(Version: 1, Decision: AnyDecision, AnalysisStepOrder: 1, EnableMusic: true, EnableSfx: true);
        List<JsonObject> warnings = VideoCompileStepExecutor.BuildCompileWarnings(
            config, SingleSourceArtifact(10), null, false, 0, false, true, 10.0, out _);

        warnings.Select(w => w["code"]!.GetValue<string>()).Should().Equal("music_no_tracks", "sfx_no_clips");
        warnings[0]["message"]!.GetValue<string>().Should().Contain("Upload a music track");
        warnings[1]["message"]!.GetValue<string>().Should().Contain("Upload short sound-effect audio files");

        // With candidates on offer (the agent just chose none) there is nothing to tell them.
        VideoAnalysisArtifact withCandidates = SingleSourceArtifact(10) with
        {
            MusicCandidates = [new VideoAnalysisMusicCandidate("m0", Guid.NewGuid(), "bed.mp3", "audio/mpeg", 1)],
            SfxCandidates = [new VideoAnalysisSfxCandidate("x0", Guid.NewGuid(), "whoosh.wav", "audio/wav", 1)]
        };
        VideoCompileStepExecutor.BuildCompileWarnings(config, withCandidates, null, false, 0, false, true, 10.0, out _)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Clean_compile_carries_no_warnings_key()
    {
        var fixture = new CompileFixture(SingleSourceArtifact(10.0));
        await fixture.RunAsync(BuildDecisionJson("s0"));
        fixture.Output!.ContainsKey("warnings").Should().BeFalse();
        fixture.Output!.ContainsKey("length").Should().BeFalse();
    }

    // ==========================================================================================
    // Finding 46 — run-named output files
    // ==========================================================================================

    [Fact]
    public void Run_file_names_are_readable_ascii_and_capped()
    {
        VideoCompileStepExecutor.BuildRunFileName("60s narrated product story", 2, 0)
            .Should().Be("60s narrated product story - run 2.mp4");
        VideoCompileStepExecutor.BuildRunFileName("Café / promo: v2", 3, 1)
            .Should().Be("Cafe promo v2 - run 3 (revision 2).mp4");
        VideoCompileStepExecutor.BuildRunFileName("Teaser", null, 0).Should().Be("Teaser.mp4");
        VideoCompileStepExecutor.BuildRunFileName("   ", 1, 0).Should().BeNull();
        VideoCompileStepExecutor.BuildRunFileName("🎬🎬", 1, 0).Should().BeNull();
        VideoCompileStepExecutor.BuildRunFileName(new string('a', 400), 12, 0)!.Length
            .Should().Be(VideoCompileStepExecutor.MaxRunFileNameLength);

        VideoCompileStepExecutor.IsDefaultOutputFileName("edited.mp4").Should().BeTrue();
        VideoCompileStepExecutor.IsDefaultOutputFileName(null).Should().BeTrue();
        VideoCompileStepExecutor.IsDefaultOutputFileName("launch-cut.mp4").Should().BeFalse();
    }

    [Fact]
    public async Task Registered_render_is_named_after_the_workflow_and_its_run_number()
    {
        await using WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid definitionId = Guid.NewGuid();
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Id = definitionId, Name = "Spring teaser", ProjectId = ProjectId });
        var earlier = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = definitionId, ProjectId = ProjectId, StartedAt = new DateTime(2026, 10, 1) };
        var current = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = definitionId, ProjectId = ProjectId, StartedAt = new DateTime(2026, 10, 4) };
        var later = new WorkflowExecution { Id = Guid.NewGuid(), WorkflowDefinitionId = definitionId, ProjectId = ProjectId, StartedAt = new DateTime(2026, 10, 5) };
        db.WorkflowExecutions.AddRange(earlier, current, later);
        await db.SaveChangesAsync();

        var fixture = new CompileFixture(SingleSourceArtifact(10.0)) { Db = db, Execution = current };
        await fixture.RunAsync(BuildDecisionJson("s0"));

        fixture.RegisteredFileName.Should().Be("Spring teaser - run 2.mp4");

        var custom = new CompileFixture(SingleSourceArtifact(10.0)) { Db = db, Execution = current };
        custom.Config = c => c with { OutputFileName = "launch-cut.mp4" };
        await custom.RunAsync(BuildDecisionJson("s0"));
        custom.RegisteredFileName.Should().Be("launch-cut.mp4", "a name the author chose is kept verbatim");
    }

    // ==========================================================================================
    // Real ffmpeg (skipped when ffmpeg/ffprobe are not on PATH)
    // ==========================================================================================

    [RealFfmpegFact]
    public Task Real_ffmpeg_narration_over_a_silent_clip_produces_a_file_with_an_audio_stream() =>
        RealSilentNarrationAsync(SingleSourceArtifact(2.0, 320, 180), BuildDecisionJson("s0"), anchorId: null);

    [RealFfmpegFact]
    public Task Real_ffmpeg_narration_over_two_silent_clips_vertical_produces_a_file_with_an_audio_stream() =>
        RealSilentNarrationAsync(
            Artifact(
                (0, new VideoAnalysisMedia(2.0, 30, 1, 320, 180), "s0"),
                (1, new VideoAnalysisMedia(2.0, 30, 1, 320, 180), "s1")),
            BuildDecisionJson("s0", "s1"), anchorId: "s1", VideoOutputFormat.Vertical);

    private static async Task RealSilentNarrationAsync(
        VideoAnalysisArtifact artifact, string decisionJson, string? anchorId, VideoOutputFormat format = VideoOutputFormat.Source)
    {
        string work = Path.Combine(Path.GetTempPath(), "reelbolt-silent-base-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string clip = Path.Combine(work, "clip.mp4");
            string line = Path.Combine(work, "line.wav");
            Run("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i testsrc=size=320x180:rate=30 -t 2 -pix_fmt yuv420p \"{clip}\"");
            Run("ffmpeg", $"-hide_banner -loglevel error -y -f lavfi -i sine=frequency=440:duration=1 \"{line}\"");

            var options = Options.Create(new VideoEditingOptions
            {
                ScratchPath = Path.Combine(work, "scratch"),
                MaxConcurrentJobs = 1,
                AnalyzeTimeoutSeconds = 60,
                CompileTimeoutSeconds = 120
            });
            var runner = new FfmpegVideoToolRunner(options, NullLogger<FfmpegVideoToolRunner>.Instance);
            var probe = new FfprobeMediaProbe(runner, options, NullLogger<FfprobeMediaProbe>.Instance);

            var fixture = new CompileFixture(artifact)
            {
                RealToolRunner = runner,
                RealProbe = probe,
                SourceFile = clip,
                VoiceoverFile = line,
                OutputCopy = Path.Combine(work, "out.mp4")
            };
            fixture.Voiceover = VoiceoverJson(("vo0", 0.5, 1.0, anchorId, "Beep."));
            fixture.Config = c => c with
            {
                EnableVoiceover = true,
                VoiceoverStepOrder = VoiceoverStepOrder,
                PrePaddingMs = 0,
                PostPaddingMs = 0,
                OutputFormat = format
            };

            StepExecutionResult result = await fixture.RunAsync(decisionJson);

            result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
            string streams = Run("ffprobe", $"-v error -show_entries stream=codec_type -of csv=p=0 \"{fixture.OutputCopy}\"");
            streams.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim())
                .Should().Contain("audio", "narration over a silent clip must ship with sound");
            if (format == VideoOutputFormat.Vertical)
            {
                Run("ffprobe", $"-v error -select_streams v:0 -show_entries stream=width,height -of csv=p=0 \"{fixture.OutputCopy}\"")
                    .Trim().Should().Be("1080,1920");
            }
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string Run(string tool, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(tool, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        })!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{tool} {arguments} failed: {stderr}");
        return stdout;
    }

    // ==========================================================================================
    // Fixtures
    // ==========================================================================================

    private static List<JsonObject> Warnings(VideoCompileStepConfig config, JsonObject voiceoverNode) =>
        VideoCompileStepExecutor.BuildCompileWarnings(
            config, SingleSourceArtifact(10), voiceoverNode, false, 0, true, true, 10.0, out _);

    private static OutputTimeline Timeline(params (int Source, double Start, double End)[] spans) =>
        TimelineWithSpans(spans).Timeline;

    /// <summary>The timeline AND the resolved spans it was built from — the placement plan needs the
    /// spans' own source ranges to decide where a cut-away anchor belongs.</summary>
    private static (OutputTimeline Timeline, List<VideoCompileStepExecutor.ResolvedSpan> Spans) TimelineWithSpans(
        params (int Source, double Start, double End)[] spans)
    {
        List<VideoCompileStepExecutor.ResolvedSpan> resolved = spans
            .Select(s => new VideoCompileStepExecutor.ResolvedSpan(
                s.Start, s.End, s.Start, s.End, (long)(s.Start * 30), (long)(s.End * 30), s.Source, ["x"]))
            .ToList();
        return (OutputTimeline.Build(resolved, VideoCompileStepExecutor.BuildHardCutSeamsForFallback(resolved.Count)), resolved);
    }

    private static VideoAnalysisArtifact SingleSourceArtifact(double durationSec, int width = 1920, int height = 1080) =>
        Artifact((0, new VideoAnalysisMedia(durationSec, 30, 1, width, height), "s0"));

    /// <summary>Clip 0: 4 s 1920x1080 · clip 1: 5 s 1280x720 · clip 2: 6 s 1920x1080 — one whole-clip shot each.</summary>
    private static VideoAnalysisArtifact ThreeSourceArtifact() => Artifact(
        (0, new VideoAnalysisMedia(4, 30, 1, 1920, 1080), "s0"),
        (1, new VideoAnalysisMedia(5, 30, 1, 1280, 720), "s1"),
        (2, new VideoAnalysisMedia(6, 30, 1, 1920, 1080), "s2"));

    private static VideoAnalysisArtifact Artifact(params (int Source, VideoAnalysisMedia Media, string ShotId)[] sources) => new(
        Version: 2,
        Media: sources[0].Media,
        Shots: sources.Select(s => new VideoAnalysisShot(s.ShotId, 0.0, s.Media.DurationSec, SourceIndex: s.Source)).ToList(),
        SilenceSpans: [],
        Segments: [],
        Words: [],
        OfferedIds: sources.Select(s => s.ShotId).ToList(),
        Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
        Sources: sources.Select(s => new VideoAnalysisSourceInfo(s.Source, SourceKey(s.Source), s.Media)).ToList());

    private static string SourceKey(int index) => $"projects/p/userFiles/clip-{index}.mp4";

    private static string BuildDecisionJson(params string[] shotIds) => JsonSerializer.Serialize(new
    {
        keep = shotIds.Select(id => new { fromId = id, toId = id, reason = "keep" }),
        editRationale = "test",
        suggestedTitle = "Test"
    });

    private static string VoiceoverJson(params (string Id, double StartSec, double DurationSec, string? AnchorId, string Text)[] lines) =>
        JsonSerializer.Serialize(new
        {
            view = new
            {
                lines = lines.Select(l => new Dictionary<string, object?>
                {
                    ["id"] = l.Id,
                    ["startSec"] = l.StartSec,
                    ["status"] = "ok",
                    ["durationSec"] = l.DurationSec,
                    ["storageKey"] = $"projects/p/agentFiles/voiceover/lines/{l.Id}.wav",
                    ["text"] = l.Text,
                    ["anchorId"] = l.AnchorId
                }.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value))
            }
        });

    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>One compile run: builds the context, runs the executor, captures what came out.</summary>
    private sealed class CompileFixture(VideoAnalysisArtifact artifact, bool sourcesHaveAudio = true)
    {
        public string? Voiceover { get; set; }
        public Func<VideoCompileStepConfig, VideoCompileStepConfig> Config { get; set; } = c => c with { PrePaddingMs = 0, PostPaddingMs = 0 };
        public WorkflowEngineDbContext? Db { get; init; }
        public WorkflowExecution? Execution { get; init; }

        // Real-ffmpeg mode.
        public IVideoToolRunner? RealToolRunner { get; init; }
        public IMediaProbe? RealProbe { get; init; }
        public string? SourceFile { get; init; }
        public string? VoiceoverFile { get; init; }
        public string? OutputCopy { get; init; }

        public JsonObject? Output { get; private set; }
        public JsonObject? Edl { get; private set; }
        public List<string>? EncodeArgs { get; private set; }
        public string? FilterGraph { get; private set; }
        public bool Scripted { get; private set; }
        public string? RegisteredFileName { get; private set; }

        public async Task<StepExecutionResult> RunAsync(string decisionJson)
        {
            var workspace = new Mock<IProjectFileWorkspace>();
            string artifactJson = JsonSerializer.Serialize(artifact, ConfigOptions);
            workspace
                .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, string, string, CancellationToken>((_, key, dest, _) =>
                {
                    if (key == AnalysisKey)
                        File.WriteAllText(dest, artifactJson);
                    else if (key.Contains("/voiceover/") && VoiceoverFile is not null)
                        File.Copy(VoiceoverFile, dest, overwrite: true);
                    else if (key.Contains("/userFiles/clip-") && SourceFile is not null)
                        File.Copy(SourceFile, dest, overwrite: true);
                    else
                        File.WriteAllBytes(dest, [0x00, 0x01]);
                    return Task.CompletedTask;
                });
            workspace
                .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<CancellationToken>(), It.IsAny<string>()))
                .Callback<Guid, string, string, string, CancellationToken, string>((_, path, _, _, _, _) =>
                {
                    if (path.EndsWith("edl.json", StringComparison.Ordinal))
                        Edl = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                })
                .ReturnsAsync("projects/p/agentFiles/video-analysis/e/step-4-edl.json");
            workspace
                .Setup(w => w.UploadBinaryFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync((Guid projectId, string localPath, string fileName, string mime, SummaryStatus _, FileIndexingStatus _,
                    CancellationToken _, string category, string? _) =>
                {
                    RegisteredFileName = fileName;
                    if (OutputCopy is not null)
                        File.Copy(localPath, OutputCopy, overwrite: true);
                    return new ProjectWorkspaceFile(Guid.NewGuid(), projectId, fileName, null, category,
                        $"projects/{projectId}/{category}/{Guid.NewGuid():D}.mp4", mime, 1024, DateTime.UnixEpoch, null);
                });
            workspace.SetupRegisteredRenderPublish(onPublishedLocalPath: p => { if (OutputCopy is not null) File.Copy(p, OutputCopy, overwrite: true); }, onRegisteredFileName: n => RegisteredFileName = n);

            VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
            VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
            VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();
            VideoCompileStepExecutor.ResetXfadeAvailabilityCacheForTests();

            IVideoToolRunner runner;
            if (RealToolRunner is not null)
            {
                runner = RealToolRunner;
            }
            else
            {
                var toolRunner = new Mock<IVideoToolRunner>();
                toolRunner
                    .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new VideoToolResult(0, "... drawtext perspective alphamerge xfade acrossfade colorbalance colorlevels hue eq ...", string.Empty, false));
                toolRunner
                    .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
                        It.IsAny<Action<string>>()))
                    .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken, Action<string>>((args, _, _, _) => Capture(args))
                    .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
                toolRunner
                    .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                    .Callback<IReadOnlyList<string>, TimeSpan, CancellationToken>((args, _, _) => Capture(args))
                    .ReturnsAsync(new VideoToolResult(0, string.Empty, string.Empty, false));
                toolRunner
                    .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("filter=amix")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new VideoToolResult(0, "... normalize ...", string.Empty, false));
                runner = toolRunner.Object;
            }

            IMediaProbe probe;
            if (RealProbe is not null)
            {
                probe = RealProbe;
            }
            else
            {
                var mediaProbe = new Mock<IMediaProbe>();
                mediaProbe
                    .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000));
                if (!sourcesHaveAudio)
                {
                    mediaProbe
                        .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.Is<string>(path => Path.GetFileName(path).StartsWith("source-")), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", null, null));
                }
                probe = mediaProbe.Object;
            }

            IServiceScopeFactory scopeFactory = new Mock<IServiceScopeFactory>().Object;
            if (Db is not null)
            {
                var provider = new Mock<IServiceProvider>();
                provider.Setup(p => p.GetService(typeof(WorkflowEngineDbContext))).Returns(Db);
                var scope = new Mock<IServiceScope>();
                scope.Setup(s => s.ServiceProvider).Returns(provider.Object);
                var factory = new Mock<IServiceScopeFactory>();
                factory.Setup(f => f.CreateScope()).Returns(scope.Object);
                scopeFactory = factory.Object;
            }

            var executor = new VideoCompileStepExecutor(
                runner, probe, workspace.Object, scopeFactory,
                Options.Create(new VideoEditingOptions
                {
                    ScratchPath = Path.Combine(Path.GetTempPath(), "video-compile-narration-format-tests", Guid.NewGuid().ToString("N")),
                    MaxConcurrentJobs = 1,
                    AnalyzeTimeoutSeconds = 60,
                    CompileTimeoutSeconds = 120
                }),
                NullLogger<VideoCompileStepExecutor>.Instance);

            VideoCompileStepConfig config = Config(new VideoCompileStepConfig(
                Version: 1,
                Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
                AnalysisStepOrder: 1));

            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = 4,
                StepType = StepType.VideoCompile,
                VideoCompileConfigJson = JsonSerializer.Serialize(config, ConfigOptions)
            };

            List<StepOutputHistoryEntry> history =
            [
                new(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: AnalysisKey),
                new(2, "StoryEditor", decisionJson)
            ];
            if (Voiceover is not null)
                history.Add(new StepOutputHistoryEntry(VoiceoverStepOrder, "Voiceover", Voiceover));

            StepExecutionResult result = await executor.ExecuteAsync(new StepExecutionContext
            {
                Execution = Execution ?? new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = decisionJson,
                StepOutputHistory = history,
                CurrentStepIndex = history.Count,
                IterationCount = 0,
                CorrelationId = "test",
                CancellationToken = CancellationToken.None
            });

            if (result.Status == StepStatus.Completed)
                Output = JsonNode.Parse(result.Output)!.AsObject();
            return result;
        }

        private void Capture(IReadOnlyList<string> args)
        {
            if (!args.Contains("-map"))
                return;
            EncodeArgs = args.ToList();
            int inline = EncodeArgs.IndexOf("-filter_complex");
            int script = EncodeArgs.IndexOf("-filter_complex_script");
            Scripted = script >= 0;
            // Read the script INSIDE the callback — the executor deletes its scratch space afterwards.
            FilterGraph = script >= 0 ? File.ReadAllText(EncodeArgs[script + 1]) : EncodeArgs[inline + 1];
        }
    }
}

/// <summary>Runs only where real <c>ffmpeg</c> and <c>ffprobe</c> binaries are on PATH; skipped (not passed) elsewhere.</summary>
public sealed class RealFfmpegFactAttribute : FactAttribute
{
    public RealFfmpegFactAttribute()
    {
        if (!OnPath("ffmpeg") || !OnPath("ffprobe"))
            Skip = "Requires real ffmpeg and ffprobe binaries on PATH.";
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => File.Exists(Path.Combine(dir, tool)) || File.Exists(Path.Combine(dir, tool + ".exe")));
}
