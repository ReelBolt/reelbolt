#nullable enable
using System;
using System.Collections.Generic;
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
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The music-driven reel findings (customer end-to-end findings, October 2026): narration that is
/// never cut by the program fade or the end of the picture. Every ffmpeg call goes through a Moq
/// <see cref="IVideoToolRunner"/>; the filtergraph the encode would have run is captured and
/// asserted exactly.
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoCompileReelFeatureTests
{
    internal static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    internal const string AnalysisKey = "projects/p/agentFiles/video-analysis/e/step-1-analysis.json";
    internal const int VoiceoverStepOrder = 3;

    // ==========================================================================================
    // Narration and the end of the program
    // ==========================================================================================

    [Fact]
    public void Narration_hold_is_what_the_last_line_plus_its_tail_needs_and_nothing_when_it_fits()
    {
        static ResolvedVoiceoverLine Line(double start, double dur) => new("vo", Guid.Empty, "k", "p", start, dur);

        VideoCompileStepExecutor.ComputeNarrationHoldSec([Line(8, 3)], 10).Should().Be(1.25);
        VideoCompileStepExecutor.ComputeNarrationHoldSec([Line(1, 2), Line(6, 3.75)], 10).Should().Be(0,
            "the last line ends exactly a tail before the picture does");
        VideoCompileStepExecutor.ComputeNarrationHoldSec([Line(1, 2)], 10).Should().Be(0);
        VideoCompileStepExecutor.ComputeNarrationHoldSec([], 10).Should().Be(0);
        VideoCompileStepExecutor.ComputeNarrationHoldSec([Line(7, 3)], 10, programOffsetSec: 2).Should().Be(2.25,
            "a cold open in front of the body moves every body-timed line");
    }

    [Fact]
    public async Task A_line_running_past_the_picture_holds_the_last_frame_and_is_mixed_after_the_program_fade_on_the_select_path()
    {
        var fixture = new ReelCompileFixture(ReelCompileFixture.SingleSourceArtifact(10.0));
        fixture.Voiceover = ReelCompileFixture.VoiceoverJson(("vo0", 8.0, 3.0, null, "And that is the whole story."));
        fixture.Config = c => c with
        {
            EnableVoiceover = true,
            VoiceoverStepOrder = VoiceoverStepOrder,
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            ProgramFadeOutMs = 1000,
            ProgramAudioFadeOutMs = 1000
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Be(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,select='gte(t,0)*lt(t,10)',setpts=N/FRAME_RATE/TB[vxfd];" +
            "[0:a]aselect='gte(t,0)*lt(t,10)',asetpts=N/SR/TB,aformat=sample_rates=48000:channel_layouts=stereo[axfd];" +
            // The picture's last frame is held 1.25 s (the line ends at 11.0, plus a 0.25 s tail)
            // and the fade-out is placed at the end of the LENGTHENED program.
            "[vxfd]tpad=stop_mode=clone:stop_duration=1.25,fade=t=out:st=10.25:d=1:color=black[vout];" +
            // The program audio is padded to that length and faded — WITHOUT the narration...
            "[axfd]apad=whole_dur=11.25,afade=t=out:st=10.25:d=1:curve=tri[anar];" +
            // ...which is mixed on top afterwards, so the fade can never cut the line mid-word.
            "[1:a]atrim=end=3,asetpts=N/SR/TB,aformat=sample_rates=48000:channel_layouts=stereo,volume=1,adelay=8000|8000[vo0];" +
            "[anar][vo0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]");

        fixture.Output!["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(11.25, 1e-9);
        fixture.Output!["programFade"]!["extendedForNarrationSec"]!.GetValue<double>().Should().Be(1.25);
        fixture.Output!["voiceover"]!["extendedForNarrationSec"]!.GetValue<double>().Should().Be(1.25);
    }

    [Fact]
    public async Task A_line_inside_the_fade_window_is_not_faded_but_nothing_is_held_when_it_fits()
    {
        var fixture = new ReelCompileFixture(ReelCompileFixture.SingleSourceArtifact(10.0));
        fixture.Voiceover = ReelCompileFixture.VoiceoverJson(("vo0", 7.5, 2.0, null, "Ends inside the fade."));
        fixture.Config = c => c with
        {
            EnableVoiceover = true,
            VoiceoverStepOrder = VoiceoverStepOrder,
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            ProgramAudioFadeOutMs = 2000
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Be(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,select='gte(t,0)*lt(t,10)',setpts=N/FRAME_RATE/TB[vout];" +
            "[0:a]aselect='gte(t,0)*lt(t,10)',asetpts=N/SR/TB,aformat=sample_rates=48000:channel_layouts=stereo[axfd];" +
            "[axfd]afade=t=out:st=8:d=2:curve=tri[anar];" +
            "[1:a]atrim=end=2,asetpts=N/SR/TB,aformat=sample_rates=48000:channel_layouts=stereo,volume=1,adelay=7500|7500[vo0];" +
            "[anar][vo0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]");
        fixture.Output!["programFade"]!.AsObject().ContainsKey("extendedForNarrationSec").Should().BeFalse();
        fixture.Output!["outputDurationSec"]!.GetValue<double>().Should().Be(10);
    }

    [Fact]
    public async Task A_line_past_the_picture_with_no_fade_still_holds_the_last_frame()
    {
        var fixture = new ReelCompileFixture(ReelCompileFixture.SingleSourceArtifact(10.0));
        fixture.Voiceover = ReelCompileFixture.VoiceoverJson(("vo0", 9.0, 2.0, null, "Runs long."));
        fixture.Config = c => c with { EnableVoiceover = true, VoiceoverStepOrder = VoiceoverStepOrder, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain("[vxfd]tpad=stop_mode=clone:stop_duration=1.25[vout]");
        fixture.FilterGraph.Should().Contain("[axfd]apad=whole_dur=11.25[anar]");
        fixture.FilterGraph.Should().EndWith("[anar][vo0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]");
        fixture.Output!.ContainsKey("programFade").Should().BeFalse("no fade was configured");
        fixture.Output!["voiceover"]!["extendedForNarrationSec"]!.GetValue<double>().Should().Be(1.25);
    }

    [Fact]
    public async Task On_the_segmented_path_the_hold_and_the_fade_are_one_tail_and_the_narration_rides_over_it()
    {
        var fixture = new ReelCompileFixture(ReelCompileFixture.TwoSourceArtifact());
        fixture.Voiceover = ReelCompileFixture.VoiceoverJson(("vo0", 0.0, 4.0, "s1", "Over the second clip, and past it."));
        fixture.Config = c => c with
        {
            EnableVoiceover = true,
            VoiceoverStepOrder = VoiceoverStepOrder,
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            ProgramFadeOutMs = 500,
            ProgramAudioFadeOutMs = 500
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s1"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Scripted.Should().BeTrue();
        // s1 plays [4, 7); the line starts at 4 and ends at 8 — 1.25 s past the 7 s picture.
        fixture.FilterGraph.Should().Contain("concat=n=2:v=1:a=1[vpre][apre];");
        fixture.FilterGraph.Should().Contain("[vpre]tpad=stop_mode=clone:stop_duration=1.25,fade=t=out:st=7.75:d=0.5:color=black[vout]");
        fixture.FilterGraph.Should().Contain("[apre]apad=whole_dur=8.25,afade=t=out:st=7.75:d=0.5:curve=tri[anar]");
        fixture.FilterGraph.Should().EndWith(
            "[2:a]atrim=end=4,asetpts=N/SR/TB,aformat=sample_rates=48000:channel_layouts=stereo,volume=1,adelay=4000|4000[vo0];" +
            "[anar][vo0]amix=inputs=2:duration=first:dropout_transition=0:normalize=0[aout]");
        fixture.Output!["outputDurationSec"]!.GetValue<double>().Should().BeApproximately(8.25, 1e-9);
    }
}

/// <summary>
/// "More than one insert video per compile" (InsertContents) and "insert content that fades to black
/// reads as a dead screen" (InsertContentDips).
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoCompileInsertContentTests
{
    private static VideoInsertRegionTrack Track(string id, int source) => new(
        Id: id, ShotId: source == 0 ? "s0" : "s1", StartSec: 0.5, EndSec: 2.5,
        Keyframes:
        [
            new VideoInsertQuadKeyframe(0.5, 0.30, 0.20, 0.60, 0.22, 0.31, 0.70, 0.61, 0.72),
            new VideoInsertQuadKeyframe(2.5, 0.35, 0.20, 0.65, 0.22, 0.36, 0.70, 0.66, 0.72)
        ],
        Confidence: 0.9, MeanAreaRatio: 0.12, MeanAspectRatio: 0.6, MotionClass: "Slow", ColorName: "green",
        SourceIndex: source);

    private static ProjectWorkspaceFile File(Guid id, string name, string mime = "video/mp4") =>
        new(id, VideoCompileReelFeatureTests.ProjectId, name, null, "userFiles",
            $"projects/{VideoCompileReelFeatureTests.ProjectId}/userFiles/{id:N}-{name}", mime, 100, DateTime.UnixEpoch, null);

    [Fact]
    public void The_most_specific_assignment_wins_and_the_single_content_is_the_fallback()
    {
        Guid portrait = Guid.NewGuid(), landscape = Guid.NewGuid(), r3Special = Guid.NewGuid(), fallback = Guid.NewGuid();
        var config = new VideoCompileStepConfig(1, new ExtractInputRef(ExtractInputSource.Previous), 1)
        {
            InsertContentProjectFileId = fallback,
            InsertContents =
            [
                new VideoInsertContentAssignment(landscape, SourceIndex: 1, Fit: InsertContentFit.Contain),
                new VideoInsertContentAssignment(r3Special, RegionId: "R3"),
                new VideoInsertContentAssignment(portrait, SourceIndex: 0)
            ]
        };

        VideoCompileStepExecutor.PickInsertContent(config, Track("r0", 0)).Should().Be((portrait, (InsertContentFit?)null));
        VideoCompileStepExecutor.PickInsertContent(config, Track("r1", 1)).Should().Be((landscape, (InsertContentFit?)InsertContentFit.Contain));
        VideoCompileStepExecutor.PickInsertContent(config, Track("r3", 1)).Should().Be((r3Special, (InsertContentFit?)null),
            "a region match outranks a source match");
        VideoCompileStepExecutor.PickInsertContent(config, Track("r4", 2)).Should().Be((fallback, (InsertContentFit?)null));
        VideoCompileStepExecutor.PickInsertContent(config with { InsertContentProjectFileId = null }, Track("r4", 2))
            .Should().BeNull("no entry and no fallback means no deterministic content");
    }

    [Fact]
    public async Task Each_clips_plate_gets_its_own_content()
    {
        Guid portrait = Guid.NewGuid(), landscape = Guid.NewGuid();
        VideoAnalysisArtifact artifact = ReelCompileFixture.TwoSourceArtifact() with
        {
            InsertRegions = [Track("r0", 0), Track("r1", 1)],
            OfferedInsertRegionIds = ["r0", "r1"]
        };
        var fixture = new ReelCompileFixture(artifact);
        fixture.ProjectFiles.AddRange([File(portrait, "phone-ui.mp4"), File(landscape, "laptop-ui.mp4")]);
        fixture.Config = c => c with
        {
            EnableInserts = true,
            InsertContentDips = InsertContentDipMode.Keep,
            InsertContents =
            [
                new VideoInsertContentAssignment(portrait, SourceIndex: 0),
                new VideoInsertContentAssignment(landscape, SourceIndex: 1)
            ],
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s1"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        var applied = fixture.Edl!["inserts"]!["appliedInserts"]!.AsArray()
            .ToDictionary(a => a!["regionId"]!.GetValue<string>(), a => a!["contentProjectFileId"]!.GetValue<string>());
        applied["r0"].Should().Be(portrait.ToString());
        applied["r1"].Should().Be(landscape.ToString());
        fixture.FfmpegCalls.Should().NotContain(c => c.Any(a => a.StartsWith("blackdetect", StringComparison.Ordinal)),
            "Keep never even looks for dips");
    }

    [Fact]
    public async Task Dips_to_black_in_project_content_are_frozen_over_by_default_and_reported()
    {
        Guid ui = Guid.NewGuid();
        VideoAnalysisArtifact artifact = ReelCompileFixture.SingleSourceArtifact(4.0) with
        {
            InsertRegions = [Track("r0", 0)],
            OfferedInsertRegionIds = ["r0"]
        };
        var fixture = new ReelCompileFixture(artifact);
        fixture.ProjectFiles.Add(File(ui, "app-ui.mp4"));
        fixture.Config = c => c with { EnableInserts = true, InsertContentProjectFileId = ui, PrePaddingMs = 0, PostPaddingMs = 0 };
        fixture.FfmpegHook = args => args.Any(a => a.StartsWith("blackdetect", StringComparison.Ordinal))
            ? new VideoToolResult(0, "", "[blackdetect @ 0x1] black_start:2.8 black_end:3.6 black_duration:0.8\n" +
                                         "[blackdetect @ 0x1] black_start:5.7 black_end:6 black_duration:0.3\n", false)
            : null;

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        List<string> bridge = fixture.FfmpegCalls.Single(c => c.Contains("-vf") && c[c.IndexOf("-vf") + 1].StartsWith("select=", StringComparison.Ordinal));
        bridge[bridge.IndexOf("-vf") + 1].Should().Be("select='not(between(t,2.8,3.6)+between(t,5.7,6))',fps=fps=30/1:start_time=0");
        string bridgedPath = bridge[^1];
        fixture.EncodeArgs.Should().Contain(bridgedPath, "the bridged copy is what gets composited");

        JsonObject dips = fixture.Edl!["inserts"]!["appliedInserts"]![0]!["contentDips"]!.AsObject();
        dips["treatment"]!.GetValue<string>().Should().Be("Freeze");
        dips["applied"]!.GetValue<bool>().Should().BeTrue();
        dips["intervals"]!.AsArray().Should().HaveCount(2);
        dips["totalSec"]!.GetValue<double>().Should().BeApproximately(1.1, 1e-9);
    }

    [Fact]
    public async Task Content_with_no_dips_composites_exactly_as_before()
    {
        Guid ui = Guid.NewGuid();
        VideoAnalysisArtifact artifact = ReelCompileFixture.SingleSourceArtifact(4.0) with
        {
            InsertRegions = [Track("r0", 0)],
            OfferedInsertRegionIds = ["r0"]
        };
        var fixture = new ReelCompileFixture(artifact);
        fixture.ProjectFiles.Add(File(ui, "app-ui.mp4"));
        fixture.Config = c => c with { EnableInserts = true, InsertContentProjectFileId = ui, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Edl!["inserts"]!["appliedInserts"]![0]!.AsObject().ContainsKey("contentDips").Should().BeFalse();
        fixture.FfmpegCalls.Should().ContainSingle(c => c.Any(a => a.StartsWith("blackdetect", StringComparison.Ordinal)));
    }

    [Fact]
    public void Dip_filters_cut_or_freeze_and_hold_a_dip_that_runs_to_the_end()
    {
        VideoCompileStepExecutor.BuildInsertDipFilter([(2.8, 3.6)], InsertContentDipMode.Cut, 30, 1, 10)
            .Should().Be("fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=10.066667,select='not(between(t,2.8,3.6))',setpts=N/FRAME_RATE/TB");
        VideoCompileStepExecutor.BuildInsertDipFilter([(0, 1), (9.2, 10)], InsertContentDipMode.Freeze, 60, 1, 10)
            .Should().Be("select='not(between(t,0,1)+between(t,9.2,10))',fps=fps=60/1:start_time=0,tpad=stop_mode=clone:stop_duration=0.8");
        VideoCompileStepExecutor.ParseBlackDetect("x black_start:1.5 black_end:2 black_duration:0.5\nnoise\nblack_start:3 black_end:3")
            .Should().Equal((1.5, 2.0));
    }
}

/// <summary>"Reorder is impossible": <see cref="VideoCompileStepConfig.KeepOrder"/> = AsListed plays spans in the editor's order.</summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoCompileKeepOrderTests
{
    private static string Spans(params (string From, string To)[] spans) => JsonSerializer.Serialize(new
    {
        keep = spans.Select(s => new { fromId = s.From, toId = s.To, reason = "keep" }),
        editRationale = "test",
        suggestedTitle = "Test"
    });

    [Fact]
    public async Task As_listed_plays_the_last_shot_first_through_the_segmented_encode()
    {
        // s0 [0,2) · s1 [2,5) · s2 [5,9)
        var fixture = new ReelCompileFixture(ReelCompileFixture.ShotsArtifact(2, 3, 4));
        fixture.Config = c => c with { KeepOrder = VideoKeepOrder.AsListed, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s2", "s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Scripted.Should().BeTrue("a clip played out of its own order needs trim + concat");
        fixture.FilterGraph.Should().Be(
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=9.066667,trim=start=5:end=9,setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration=4,trim=end=4[v0];" +
            "[0:a]atrim=start=5:end=9,asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo[a0];" +
            "[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=2.066667,trim=start=0:end=2,setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration=2,trim=end=2[v1];" +
            "[0:a]atrim=start=0:end=2,asetpts=PTS-STARTPTS,aformat=sample_rates=48000:channel_layouts=stereo[a1];" +
            "[v0][a0][v1][a1]concat=n=2:v=1:a=1[vout][aout]");
        JsonObject order = fixture.Output!["keepOrder"]!.AsObject();
        order["mode"]!.GetValue<string>().Should().Be("AsListed");
        order["outOfSourceOrderSpans"]!.GetValue<int>().Should().Be(1);
        order["overlapTrimmedSpans"]!.GetValue<int>().Should().Be(0);
        fixture.Output!.ContainsKey("spanNormalization").Should().BeFalse("nothing was re-sorted");
    }

    [Fact]
    public async Task Source_order_default_still_sorts_the_same_decision_onto_the_select_path()
    {
        var fixture = new ReelCompileFixture(ReelCompileFixture.ShotsArtifact(2, 3, 4));

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s2", "s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Scripted.Should().BeFalse();
        fixture.FilterGraph.Should().StartWith("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=9.066667,select='gte(t,0)*lt(t,2)+gte(t,5)*lt(t,9)'");
        fixture.Output!.ContainsKey("keepOrder").Should().BeFalse();
        fixture.Output!["spanNormalization"]!["reordered"]!.GetValue<int>().Should().Be(2);
    }

    [Fact]
    public async Task A_later_span_never_replays_footage_an_earlier_one_already_played()
    {
        var fixture = new ReelCompileFixture(ReelCompileFixture.ShotsArtifact(2, 3, 4));
        fixture.Config = c => c with { KeepOrder = VideoKeepOrder.AsListed, PrePaddingMs = 0, PostPaddingMs = 0 };

        // s1..s2 = [2,9), then s0..s1 = [0,5): only [0,2) of the second span is new.
        StepExecutionResult result = await fixture.RunAsync(Spans(("s1", "s2"), ("s0", "s1")));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=9.066667,trim=start=2:end=9,setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration=7,trim=end=7[v0]");
        fixture.FilterGraph.Should().Contain("[0:v]fps=30/1:start_time=0,tpad=stop=-1:stop_mode=clone,trim=end=2.066667,trim=start=0:end=2,setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration=2,trim=end=2[v1]");
        fixture.Output!["keepOrder"]!["overlapTrimmedSpans"]!.GetValue<int>().Should().Be(1);
        fixture.Output!["outputDurationSec"]!.GetValue<double>().Should().Be(9);
    }

    [Fact]
    public async Task As_listed_in_source_order_is_exactly_the_default_cut()
    {
        var listed = new ReelCompileFixture(ReelCompileFixture.ShotsArtifact(2, 3, 4));
        listed.Config = c => c with { KeepOrder = VideoKeepOrder.AsListed, PrePaddingMs = 0, PostPaddingMs = 0 };
        var plain = new ReelCompileFixture(ReelCompileFixture.ShotsArtifact(2, 3, 4));

        await listed.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2"));
        await plain.RunAsync(ReelCompileFixture.DecisionJson("s0", "s2"));

        listed.FilterGraph.Should().Be(plain.FilterGraph);
        listed.Output!["keepOrder"]!["outOfSourceOrderSpans"]!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Reordering_one_clip_with_stream_copy_is_a_config_error()
    {
        var fixture = new ReelCompileFixture(ReelCompileFixture.ShotsArtifact(2, 3, 4));
        fixture.Config = c => c with
        {
            KeepOrder = VideoKeepOrder.AsListed,
            Mode = VideoCompileMode.StreamCopy,
            AllowKeyframeSnapping = true,
            PrePaddingMs = 0,
            PostPaddingMs = 0
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s2", "s0"));

        result.Status.Should().Be(StepStatus.Failed);
        result.Output.Should().Contain("REORDER_REQUIRES_REENCODE");
    }

    [Fact]
    public void Earlier_coverage_is_removed_and_a_span_split_by_it_keeps_both_pieces()
    {
        IReadOnlyList<string> ids = ["x"];
        (List<(double Start, double End, int SourceIndex, IReadOnlyList<string> OriginIds)> spans, int trimmed) =
            VideoCompileStepExecutor.RemoveEarlierCoverage(
            [
                (4.0, 6.0, 0, ids),
                (0.0, 10.0, 0, ids),  // around the first: split into [0,4) and [6,10)
                (5.0, 5.5, 0, ids),   // entirely covered: gone
                (5.0, 7.0, 1, ids)    // another clip: untouched
            ]);

        trimmed.Should().Be(2);
        spans.Select(s => (s.Start, s.End, s.SourceIndex)).Should().Equal(
            (4.0, 6.0, 0), (0.0, 4.0, 0), (6.0, 10.0, 0), (5.0, 7.0, 1));
    }
}

/// <summary>
/// "Duplicate output registrations": a re-registered render of the same workflow step replaces the
/// earlier project-file rows it supersedes instead of piling up, while every render stays reachable
/// from its own run's outputs (only rows go, never the stored videos).
/// </summary>
[Collection(VideoFilterProbeCacheCollection.Name)]
public class VideoCompileRegistrationTests
{
    [Fact]
    public async Task A_re_registered_render_replaces_the_same_runs_earlier_passes_and_same_named_earlier_runs_only()
    {
        await using WorkflowEngineDbContext db = new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid projectId = VideoCompileReelFeatureTests.ProjectId;
        var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 4, StepType = StepType.VideoCompile };
        var current = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId };
        Guid earlierRun = Guid.NewGuid();
        Guid otherStep = Guid.NewGuid();

        ProjectFile Row(string name, string key) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = name,
            OriginalPath = name,
            Category = "outputFiles",
            StorageKey = key,
            StorageFileName = name,
            StorageBucket = "b",
            StoragePrefix = "p",
            MimeType = "video/mp4",
            SizeBytes = 1
        };
        WorkflowStepResult Result(Guid execution, Guid stepId, string key) => new()
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = execution,
            WorkflowStepId = stepId,
            OutputStorageKey = key,
            Status = StepStatus.Completed
        };

        ProjectFile samePassEarlier = Row("launch-cut (old pass).mp4", "k-same-run");
        ProjectFile earlierRunSameName = Row("launch-cut.mp4", "k-earlier-run");
        ProjectFile earlierRunOtherName = Row("launch-cut - run 1.mp4", "k-earlier-run-2");
        ProjectFile userUploadSameName = Row("launch-cut.mp4", "k-user-upload");
        ProjectFile otherStepsRender = Row("launch-cut.mp4", "k-other-step");
        db.ProjectFiles.AddRange(samePassEarlier, earlierRunSameName, earlierRunOtherName, userUploadSameName, otherStepsRender);
        db.WorkflowStepResults.AddRange(
            Result(current.Id, step.Id, "k-same-run"),
            Result(earlierRun, step.Id, "k-earlier-run"),
            Result(earlierRun, step.Id, "k-earlier-run-2"),
            Result(current.Id, otherStep, "k-other-step"));
        await db.SaveChangesAsync();

        var fixture = new ReelCompileFixture(ReelCompileFixture.SingleSourceArtifact(10.0)) { Db = db, Execution = current, Step = step };
        fixture.Config = c => c with { OutputFileName = "launch-cut.mp4", PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        List<string> remaining = db.ProjectFiles.Select(f => f.StorageKey).ToList();
        remaining.Should().NotContain("k-same-run", "an earlier pass of this run is superseded whatever it was called");
        remaining.Should().NotContain("k-earlier-run", "an earlier run that registered the very same name is replaced");
        remaining.Should().Contain("k-earlier-run-2", "an earlier run under its own name is a version worth keeping");
        remaining.Should().Contain("k-user-upload", "a file this step never produced is never touched");
        remaining.Should().Contain("k-other-step", "another step's render is never touched");
        remaining.Should().Contain(result.OutputStorageKey!);
        // The stored videos are untouched: the step results that point at them still exist.
        db.WorkflowStepResults.Count(r => r.OutputStorageKey == "k-same-run").Should().Be(1);

        JsonObject registration = fixture.Output!["registration"]!.AsObject();
        registration["replacedEarlierRenders"]!.GetValue<int>().Should().Be(2);
        registration["fileName"]!.GetValue<string>().Should().Be("launch-cut.mp4");
    }

    [Fact]
    public async Task A_re_registered_render_keeps_the_earlier_row_of_a_file_that_is_not_served()
    {
        await using WorkflowEngineDbContext db = new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid projectId = VideoCompileReelFeatureTests.ProjectId;
        db.Projects.Add(new Project { Id = projectId, Name = "p", CsamOutputHoldAt = DateTime.UtcNow.AddHours(-1) });
        var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 4, StepType = StepType.VideoCompile };
        var current = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId };
        Guid earlierRun = Guid.NewGuid();

        ProjectFile Row(string key, DateTime madeAt) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "launch-cut.mp4",
            OriginalPath = "launch-cut.mp4",
            Category = "outputFiles",
            StorageKey = key,
            StorageFileName = "launch-cut.mp4",
            StorageBucket = "b",
            StoragePrefix = "p",
            MimeType = "video/mp4",
            SizeBytes = 1,
            OutputProducedAt = madeAt,
            UploadedAt = madeAt
        };
        ProjectFile notServed = Row("k-before-marker", DateTime.UtcNow.AddHours(-3));
        ProjectFile served = Row("k-after-marker", DateTime.UtcNow.AddMinutes(-20));
        db.ProjectFiles.AddRange(notServed, served);
        db.WorkflowStepResults.AddRange(
            new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = earlierRun, WorkflowStepId = step.Id, OutputStorageKey = "k-before-marker", Status = StepStatus.Completed },
            new WorkflowStepResult { Id = Guid.NewGuid(), WorkflowExecutionId = earlierRun, WorkflowStepId = step.Id, OutputStorageKey = "k-after-marker", Status = StepStatus.Completed });
        await db.SaveChangesAsync();

        var fixture = new ReelCompileFixture(ReelCompileFixture.SingleSourceArtifact(10.0)) { Db = db, Execution = current, Step = step };
        fixture.Config = c => c with { OutputFileName = "launch-cut.mp4", PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        List<string> remaining = db.ProjectFiles.Select(f => f.StorageKey).ToList();
        remaining.Should().Contain("k-before-marker", "a row whose file is not served is never removed");
        remaining.Should().NotContain("k-after-marker", "a same-named row of a served file is still replaced");
    }

    [Fact]
    public async Task A_first_registration_reports_nothing_and_needs_no_database()
    {
        var fixture = new ReelCompileFixture(ReelCompileFixture.SingleSourceArtifact(10.0));
        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Output!.ContainsKey("registration").Should().BeFalse();
    }

    // ---- Remote media session (Compute:Mode=Fabric): the program lives on the runner's disk ----

    private const string LoudnormJson = """
        [Parsed_loudnorm_0 @ 0x1]
        {
            "input_i" : "-20.00",
            "input_tp" : "-6.00",
            "input_lra" : "5.00",
            "input_thresh" : "-30.00",
            "target_offset" : "0.10"
        }
        """;

    private static ReelCompileFixture RemoteFixture(bool register, bool loudness, WorkflowEngineDbContext? db = null,
        WorkflowExecution? execution = null, WorkflowStep? step = null)
    {
        var fixture = new ReelCompileFixture(ReelCompileFixture.SingleSourceArtifact(10.0))
        {
            RemoteSession = true,
            Db = db,
            Execution = execution,
            Step = step
        };
        fixture.Config = c => c with
        {
            OutputFileName = "launch-cut.mp4",
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            RegisterProjectFile = register,
            LoudnessTarget = loudness ? ProgramLoudness.Social : ProgramLoudness.Off
        };
        // The loudnorm measuring passes print their JSON block on stderr.
        fixture.FfmpegHook = args => args.Any(a => a.Contains("print_format=json", StringComparison.Ordinal))
            ? new VideoToolResult(0, string.Empty, LoudnormJson, false)
            : null;
        return fixture;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_registered_render_on_a_remote_session_is_published_through_the_session_and_registered_from_its_key(bool loudness)
    {
        ReelCompileFixture fixture = RemoteFixture(register: true, loudness);

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        string programVirtualPath = loudness ? "loudness-launch-cut.mp4" : "launch-cut.mp4";
        fixture.Published.Should().ContainSingle("the program is published once, through the media session")
            .Which.VirtualPath.Should().Be(programVirtualPath, "the FINAL program (after loudness) is what gets published");
        string key = fixture.Published[0].Key;
        key.Should().MatchRegex($"^projects/{VideoCompileReelFeatureTests.ProjectId}/outputFiles/[0-9a-f-]{{36}}\\.mp4$");

        fixture.Registrations.Should().ContainSingle();
        var row = fixture.Registrations[0];
        row.Key.Should().Be(key, "the row names exactly the object the session published");
        row.Mime.Should().Be("video/mp4");
        row.Category.Should().Be("outputFiles");
        row.Summary.Should().Be(SummaryStatus.Done);
        row.Indexing.Should().Be(FileIndexingStatus.NotIndexed);
        row.Size.Should().BeGreaterThan(0, "the size is the session file's, read through the seam");
        row.FileName.Should().Be("launch-cut.mp4");
        result.OutputStorageKey.Should().Be(key);
        (fixture.Output!["loudness"]?["applied"]?.GetValue<bool>() ?? false).Should().Be(loudness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unregistered_render_on_a_remote_session_keeps_its_run_keyed_publish(bool loudness)
    {
        var execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = VideoCompileReelFeatureTests.ProjectId };
        ReelCompileFixture fixture = RemoteFixture(register: false, loudness, execution: execution);

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Registrations.Should().BeEmpty();
        fixture.Published.Should().ContainSingle().Which.Should().Be((
            loudness ? "loudness-launch-cut.mp4" : "launch-cut.mp4",
            $"projects/{VideoCompileReelFeatureTests.ProjectId}/outputFiles/{execution.Id:D}/launch-cut.mp4"));
        result.OutputStorageKey.Should().Be(fixture.Published[0].Key);
    }

    [Fact]
    public async Task A_registered_speed_ramped_render_on_a_remote_session_publishes_the_ramped_program()
    {
        VideoAnalysisArtifact artifact = ReelCompileFixture.SingleSourceArtifact(10.0) with
        {
            Events = [new VideoAnalysisRenderEvent("e0", "event", "event", 120, 4.0, 0, null, "Flash", "local", 0.9, null)],
            OfferedEventIds = ["e0"]
        };
        var fixture = new ReelCompileFixture(artifact) { RemoteSession = true };
        fixture.Config = c => c with
        {
            OutputFileName = "launch-cut.mp4",
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            RegisterProjectFile = true,
            LoudnessTarget = ProgramLoudness.Off,
            SpeedRamps = [new SpeedRampRequest("e0")]
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Output!["speedRamps"]!["rampsApplied"]!.AsArray().Should().HaveCount(1);
        fixture.Published.Should().ContainSingle().Which.VirtualPath.Should().Be("ramps-launch-cut.mp4");
        fixture.Registrations.Should().ContainSingle().Which.Key.Should().Be(fixture.Published[0].Key);
    }

    [Fact]
    public async Task A_registered_segmented_render_on_a_remote_session_is_published_and_registered()
    {
        var fixture = new ReelCompileFixture(ReelCompileFixture.TwoSourceArtifact()) { RemoteSession = true };
        fixture.Config = c => c with
        {
            OutputFileName = "launch-cut.mp4",
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            RegisterProjectFile = true,
            LoudnessTarget = ProgramLoudness.Off
        };

        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0", "s1"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Published.Should().ContainSingle().Which.VirtualPath.Should().Be("launch-cut.mp4");
        fixture.Registrations.Should().ContainSingle().Which.Key.Should().Be(fixture.Published[0].Key);
    }

    [Fact]
    public async Task A_registered_remote_render_still_replaces_the_same_runs_earlier_pass()
    {
        await using WorkflowEngineDbContext db = new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid projectId = VideoCompileReelFeatureTests.ProjectId;
        var step = new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 4, StepType = StepType.VideoCompile };
        var current = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = projectId };
        db.ProjectFiles.Add(new ProjectFile
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = "launch-cut.mp4",
            OriginalPath = "launch-cut.mp4",
            Category = "outputFiles",
            StorageKey = "k-same-run",
            StorageFileName = "x.mp4",
            StorageBucket = "b",
            StoragePrefix = "p",
            MimeType = "video/mp4",
            SizeBytes = 1
        });
        db.WorkflowStepResults.Add(new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = current.Id,
            WorkflowStepId = step.Id,
            OutputStorageKey = "k-same-run",
            Status = StepStatus.Completed
        });
        await db.SaveChangesAsync();

        ReelCompileFixture fixture = RemoteFixture(register: true, loudness: true, db, current, step);
        StepExecutionResult result = await fixture.RunAsync(ReelCompileFixture.DecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        db.ProjectFiles.Select(f => f.StorageKey).ToList().Should().Equal(fixture.Published[0].Key);
        fixture.Output!["registration"]!["replacedEarlierRenders"]!.GetValue<int>().Should().Be(1);
    }
}

/// <summary>
/// One compile run against mocks: builds the context, runs the executor and captures the encode's
/// filtergraph, the EDL and the output summary. Hooks let a test answer specific ffmpeg calls
/// (blackdetect, audio extraction), serve project files and decide what each download writes.
/// </summary>
internal sealed class ReelCompileFixture(VideoAnalysisArtifact artifact, bool sourcesHaveAudio = true)
{
    private static readonly JsonSerializerOptions ConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public string? Voiceover { get; set; }
    public Func<VideoCompileStepConfig, VideoCompileStepConfig> Config { get; set; } = c => c with { PrePaddingMs = 0, PostPaddingMs = 0 };
    public List<ProjectWorkspaceFile> ProjectFiles { get; } = [];
    public List<StepOutputHistoryEntry> ExtraHistory { get; } = [];

    /// <summary>Answers one ffmpeg call (return null to fall through to the default success).</summary>
    public Func<IReadOnlyList<string>, VideoToolResult?>? FfmpegHook { get; set; }

    /// <summary>Writes a downloaded key's bytes (return false to fall through to the default two bytes).</summary>
    public Func<string, string, bool>? DownloadHook { get; set; }

    /// <summary>Overrides the probe of one local path (return null to fall through).</summary>
    public Func<string, MediaProbeResult?>? ProbeHook { get; set; }

    public WorkflowEngineDbContext? Db { get; init; }
    public WorkflowExecution? Execution { get; init; }
    public WorkflowStep? Step { get; set; }

    public JsonObject? Output { get; private set; }
    public JsonObject? Edl { get; private set; }
    public List<string>? EncodeArgs { get; private set; }
    public string? FilterGraph { get; private set; }
    public bool Scripted { get; private set; }
    public List<List<string>> FfmpegCalls { get; } = [];
    public List<string> RegisteredFileNames { get; } = [];
    public List<string> Progress { get; } = [];

    /// <summary>
    /// Runs the compile on a remote-kind media session (<see cref="RemoteKindMediaWorkspace"/>): MaterializeAsync
    /// answers the session-relative name and the program is published through the session, as on Fabric.
    /// </summary>
    public bool RemoteSession { get; init; }

    /// <summary>Every media-session publish of the remote session: (virtual path, final key).</summary>
    public List<(string VirtualPath, string Key)> Published { get; } = [];

    /// <summary>Every RegisterStoredObjectAsync call, in order.</summary>
    public List<(string Key, string FileName, string Mime, long Size, SummaryStatus Summary, FileIndexingStatus Indexing, string Category)> Registrations { get; } = [];

    public async Task<StepExecutionResult> RunAsync(string decisionJson)
    {
        var workspace = new Mock<IProjectFileWorkspace>();
        string artifactJson = JsonSerializer.Serialize(artifact, ConfigOptions);
        workspace
            .Setup(w => w.ListFilesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => ProjectFiles.ToList());
        workspace
            .Setup(w => w.DownloadStorageKeyToFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, string, CancellationToken>((_, key, dest, _) =>
            {
                if (key == VideoCompileReelFeatureTests.AnalysisKey)
                    File.WriteAllText(dest, artifactJson);
                else if (DownloadHook?.Invoke(key, dest) != true)
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
        // A local session's publish of the registered render: the object store echoes the key it was asked for.
        workspace
            .Setup(w => w.UploadArtifactAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.Is<string>(n => !n.Contains('/')), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), "outputFiles"))
            .ReturnsAsync((Guid projectId, string _, string fileName, string _, CancellationToken _, string category) =>
                $"projects/{projectId}/{category}/{fileName}");
        workspace
            .Setup(w => w.UploadBinaryFileAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync((Guid projectId, string localPath, string fileName, string mime, SummaryStatus _, FileIndexingStatus _,
                CancellationToken _, string category, string? _) =>
            {
                // The real upload reads a LOCAL file; a session-relative name is what broke remote renders.
                if (RemoteSession && !File.Exists(localPath))
                    throw new FileNotFoundException($"Local file '{localPath}' does not exist.", localPath);
                return Register(projectId, $"projects/{projectId}/{category}/{Guid.NewGuid():D}.mp4", fileName, mime, 1024, category);
            });
        workspace
            .Setup(w => w.RegisterStoredObjectAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<SummaryStatus>(), It.IsAny<FileIndexingStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string>()))
            .ReturnsAsync((Guid projectId, string key, string fileName, string mime, long size, SummaryStatus summary, FileIndexingStatus indexing,
                CancellationToken _, string category) =>
            {
                Registrations.Add((key, fileName, mime, size, summary, indexing, category));
                return Register(projectId, key, fileName, mime, size, category);
            });

        ProjectWorkspaceFile Register(Guid projectId, string key, string fileName, string mime, long size, string category)
        {
            RegisteredFileNames.Add(fileName);
            var file = new ProjectWorkspaceFile(Guid.NewGuid(), projectId, fileName, null, category,
                key, mime, size, DateTime.UnixEpoch, null);
            if (Db is not null)
            {
                Db.ProjectFiles.Add(new ProjectFile
                {
                    Id = file.Id,
                    ProjectId = projectId,
                    OriginalFileName = fileName,
                    OriginalPath = fileName,
                    Category = category,
                    StorageKey = file.StorageKey,
                    StorageFileName = fileName,
                    StorageBucket = "b",
                    StoragePrefix = "p",
                    MimeType = mime,
                    SizeBytes = size,
                    UploadedAt = DateTime.UtcNow
                });
                Db.SaveChanges();
            }

            return file;
        }

        VideoCompileStepExecutor.ResetDrawtextAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetAmixNormalizeCacheForTests();
        VideoCompileStepExecutor.ResetPerspectiveAvailabilityCacheForTests();
        VideoCompileStepExecutor.ResetColorGradeFiltersCacheForTests();
        VideoCompileStepExecutor.ResetXfadeAvailabilityCacheForTests();

        var toolRunner = new Mock<IVideoToolRunner>();
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... drawtext perspective alphamerge xfade acrossfade colorbalance colorlevels hue eq ...", string.Empty, false));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>>()))
            .ReturnsAsync((IReadOnlyList<string> args, TimeSpan _, CancellationToken _, Action<string> _) => Answer(args));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => !a.Contains("-filters")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> args, TimeSpan _, CancellationToken _) => Answer(args));
        toolRunner
            .Setup(t => t.RunFfmpegAsync(It.Is<IReadOnlyList<string>>(a => a.Contains("filter=amix")), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VideoToolResult(0, "... normalize ...", string.Empty, false));

        var mediaProbe = new Mock<IMediaProbe>();
        mediaProbe
            .Setup(p => p.ProbeAsync(It.IsAny<IMediaWorkspace>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IMediaWorkspace _, string path, CancellationToken _) =>
                ProbeHook?.Invoke(path)
                ?? (!sourcesHaveAudio && Path.GetFileName(path).StartsWith("source-", StringComparison.Ordinal)
                    ? new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", null, null)
                    : new MediaProbeResult(10, 30, 1, 1920, 1080, "h264", "aac", 48000)));

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

        IOptions<VideoEditingOptions> editingOptions = Options.Create(new VideoEditingOptions
        {
            ScratchPath = Path.Combine(Path.GetTempPath(), "video-compile-reel-feature-tests", Guid.NewGuid().ToString("N")),
            MaxConcurrentJobs = 1,
            AnalyzeTimeoutSeconds = 60,
            CompileTimeoutSeconds = 120
        });
        IMediaWorkspaceFactory? remoteFactory = RemoteSession
            ? new RemoteKindWorkspaceFactory(editingOptions.Value, toolRunner.Object, workspace.Object, Published)
            : null;
        var executor = new VideoCompileStepExecutor(
            toolRunner.Object, mediaProbe.Object, workspace.Object, scopeFactory, editingOptions,
            NullLogger<VideoCompileStepExecutor>.Instance, mediaWorkspaces: remoteFactory);

        VideoCompileStepConfig config = Config(new VideoCompileStepConfig(
            Version: 1,
            Decision: new ExtractInputRef(ExtractInputSource.Step, StepOrder: 2),
            AnalysisStepOrder: 1));

        WorkflowStep step = Step ?? new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 4, StepType = StepType.VideoCompile };
        step.VideoCompileConfigJson = JsonSerializer.Serialize(config, ConfigOptions);

        List<StepOutputHistoryEntry> history =
        [
            new(1, "Analyze", "{}", OutputStorageKey: null, ArtifactStorageKey: VideoCompileReelFeatureTests.AnalysisKey),
            new(2, "StoryEditor", decisionJson)
        ];
        if (Voiceover is not null)
            history.Add(new StepOutputHistoryEntry(VideoCompileReelFeatureTests.VoiceoverStepOrder, "Voiceover", Voiceover));
        history.AddRange(ExtraHistory);

        var context = new StepExecutionContext
        {
            Execution = Execution ?? new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = VideoCompileReelFeatureTests.ProjectId },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = decisionJson,
            StepOutputHistory = history,
            CurrentStepIndex = history.Count,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
        context.ProgressReporter = (label, _, _, _, _, _) =>
        {
            Progress.Add(label);
            return Task.CompletedTask;
        };

        StepExecutionResult result = await executor.ExecuteAsync(context);

        if (result.Status == StepStatus.Completed)
            Output = JsonNode.Parse(result.Output)!.AsObject();
        return result;
    }

    private VideoToolResult Answer(IReadOnlyList<string> args)
    {
        FfmpegCalls.Add(args.ToList());
        VideoToolResult? hooked = FfmpegHook?.Invoke(args);
        if (hooked is not null)
            return hooked;
        if (args.Contains("-map") && (args.Contains("-filter_complex") || args.Contains("-filter_complex_script")))
        {
            EncodeArgs = args.ToList();
            int inline = EncodeArgs.IndexOf("-filter_complex");
            int script = EncodeArgs.IndexOf("-filter_complex_script");
            Scripted = script >= 0;
            // Read the script INSIDE the callback — the executor deletes its scratch space afterwards.
            FilterGraph = script >= 0 ? File.ReadAllText(EncodeArgs[script + 1]) : EncodeArgs[inline + 1];
        }

        return new VideoToolResult(0, string.Empty, string.Empty, false);
    }

    // ---- Artifacts / step outputs -------------------------------------------------------------

    public static VideoAnalysisArtifact SingleSourceArtifact(double durationSec, int width = 1920, int height = 1080) =>
        Artifact((0, new VideoAnalysisMedia(durationSec, 30, 1, width, height), "s0"));

    /// <summary>Clip 0: 4 s · clip 1: 3 s, both 1920x1080 — one whole-clip shot each.</summary>
    public static VideoAnalysisArtifact TwoSourceArtifact() => Artifact(
        (0, new VideoAnalysisMedia(4, 30, 1, 1920, 1080), "s0"),
        (1, new VideoAnalysisMedia(3, 30, 1, 1920, 1080), "s1"));

    public static VideoAnalysisArtifact Artifact(params (int Source, VideoAnalysisMedia Media, string ShotId)[] sources) => new(
        Version: 2,
        Media: sources[0].Media,
        Shots: sources.Select(s => new VideoAnalysisShot(s.ShotId, 0.0, s.Media.DurationSec, SourceIndex: s.Source)).ToList(),
        SilenceSpans: [],
        Segments: [],
        Words: [],
        OfferedIds: sources.Select(s => s.ShotId).ToList(),
        Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
        Sources: sources.Select(s => new VideoAnalysisSourceInfo(s.Source, SourceKey(s.Source), s.Media)).ToList());

    /// <summary>One source with consecutive shots of the given lengths (ids s0, s1, …).</summary>
    public static VideoAnalysisArtifact ShotsArtifact(params double[] shotLengths)
    {
        double total = shotLengths.Sum();
        var media = new VideoAnalysisMedia(total, 30, 1, 1920, 1080);
        var shots = new List<VideoAnalysisShot>();
        double t = 0;
        for (int i = 0; i < shotLengths.Length; i++)
        {
            shots.Add(new VideoAnalysisShot($"s{i}", t, t + shotLengths[i]));
            t += shotLengths[i];
        }

        return new VideoAnalysisArtifact(
            Version: 2, Media: media, Shots: shots, SilenceSpans: [], Segments: [], Words: [],
            OfferedIds: shots.Select(s => s.Id).ToList(),
            Provenance: new VideoAnalysisProvenance(VideoTranscriptionMode.Off, false, false),
            Sources: [new VideoAnalysisSourceInfo(0, SourceKey(0), media)]);
    }

    public static string SourceKey(int index) => $"projects/p/userFiles/clip-{index}.mp4";

    public static string DecisionJson(params string[] shotIds) => JsonSerializer.Serialize(new
    {
        keep = shotIds.Select(id => new { fromId = id, toId = id, reason = "keep" }),
        editRationale = "test",
        suggestedTitle = "Test"
    });

    public static string VoiceoverJson(params (string Id, double StartSec, double DurationSec, string? AnchorId, string Text)[] lines) =>
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
}

/// <summary>
/// A media session with REMOTE semantics over a real scratch directory standing in for the runner's
/// disk: <see cref="MaterializeAsync"/> answers the session-relative name (exactly what the Fabric
/// workspace answers), the tool runs "inside the session" (its relative argv is resolved against the
/// session directory, as the runner does) and the program is published through the session. A
/// session-relative name handed to a .NET local-file API therefore fails here as it does in production.
/// </summary>
internal sealed class RemoteKindMediaWorkspace(
    LocalMediaWorkspace session, List<(string VirtualPath, string Key)> published) : IMediaWorkspace
{
    public string RootPath => "session:/";

    public Task StageAsync(string storageKey, string virtualPath, CancellationToken ct) =>
        session.StageAsync(storageKey, virtualPath, ct);

    public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(argv, a => session.RunFfmpegAsync(a, timeout, ct));

    public Task<VideoToolResult> RunFfmpegAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct, Action<string> onStdOutLine) =>
        RunAsync(argv, a => session.RunFfmpegAsync(a, timeout, ct, onStdOutLine));

    public Task<VideoToolResult> RunFfprobeAsync(IReadOnlyList<string> argv, TimeSpan timeout, CancellationToken ct) =>
        RunAsync(argv, a => session.RunFfprobeAsync(a, timeout, ct));

    public Task<string> MaterializeAsync(string virtualPath, CancellationToken ct) => Task.FromResult(virtualPath);

    public Task WriteBytesAsync(string virtualPath, byte[] content, CancellationToken ct) => session.WriteBytesAsync(virtualPath, content, ct);

    public Task WriteTextAsync(string virtualPath, string content, System.Text.Encoding encoding, CancellationToken ct) =>
        session.WriteTextAsync(virtualPath, content, encoding, ct);

    public Task<byte[]> ReadAsync(string virtualPath, CancellationToken ct) => session.ReadAsync(virtualPath, ct);

    public Task<byte[]> ReadAsync(string virtualPath, int maxBytes, CancellationToken ct) => session.ReadAsync(virtualPath, maxBytes, ct);

    public Task<MediaWorkspaceStat> StatAsync(string virtualPath, CancellationToken ct) => session.StatAsync(virtualPath, ct);

    public async Task<string> PublishAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct)
    {
        if (!(await session.StatAsync(virtualPath, ct)).Exists)
            throw new FileNotFoundException($"Session file '{virtualPath}' does not exist.", virtualPath);
        published.Add((virtualPath, finalStorageKey));
        return finalStorageKey;
    }

    public Task<string> UploadArtifactAsync(string virtualPath, string finalStorageKey, string contentType, CancellationToken ct) =>
        session.UploadArtifactAsync(virtualPath, finalStorageKey, contentType, ct);

    /// <summary>
    /// Resolves the session-relative names of an argv against the session (the runner's job), then
    /// leaves a small file at the output so the session holds what a real encode would have written.
    /// </summary>
    private async Task<VideoToolResult> RunAsync(IReadOnlyList<string> argv, Func<IReadOnlyList<string>, Task<VideoToolResult>> run)
    {
        var resolved = new List<string>(argv.Count);
        for (int i = 0; i < argv.Count; i++)
        {
            string a = argv[i];
            bool isOutput = i == argv.Count - 1 && IsFileName(a);
            resolved.Add(IsFileName(a) && (isOutput || (await session.StatAsync(a, CancellationToken.None)).Exists)
                ? await session.MaterializeAsync(a, CancellationToken.None)
                : a);
        }

        VideoToolResult result = await run(resolved);
        string last = argv[^1];
        if (result.Succeeded && IsFileName(last) && !(await session.StatAsync(last, CancellationToken.None)).Exists)
            await session.WriteBytesAsync(last, [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70], CancellationToken.None);
        return result;
    }

    private static bool IsFileName(string a) =>
        a.Length > 0 && a[0] != '-' && !Path.IsPathRooted(a) && a.Contains('.', StringComparison.Ordinal)
        && a.IndexOfAny(['=', ':', ';', '[', ' ', ',']) < 0;
}

internal sealed class RemoteKindWorkspaceFactory(
    VideoEditingOptions options, IVideoToolRunner runner, IProjectFileWorkspace projectFiles,
    List<(string VirtualPath, string Key)> published) : ReelBolt.WorkflowEngine.Services.Compute.IMediaWorkspaceFactory
{
    public Task<ReelBolt.WorkflowEngine.Services.Compute.MediaWorkspaceOpenResult> OpenAsync(
        ReelBolt.WorkflowEngine.Services.Compute.MediaWorkspaceRequest request, CancellationToken cancellationToken = default)
    {
        VideoScratchSpace scratch = VideoScratchSpace.Create(options, request.ExecutionId, request.StepId);
        LocalMediaWorkspace session = LocalMediaWorkspace.Create(scratch, runner, projectFiles, request.ProjectId);
        return Task.FromResult(ReelBolt.WorkflowEngine.Services.Compute.MediaWorkspaceOpenResult.Opened(
            new ReelBolt.WorkflowEngine.Services.Compute.MediaWorkspaceLease(
                new RemoteKindMediaWorkspace(session, published), scratch,
                ReelBolt.WorkflowEngine.Services.Compute.ComputeDecision.Local(ReelBolt.WorkflowEngine.Services.Compute.ComputeDecisionReasons.LocalMode),
                job: null, request.ExecutionId, request.StepOrder, NullLogger.Instance)));
    }
}
