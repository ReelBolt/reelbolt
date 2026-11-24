#nullable enable
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Follow;
using ReelBolt.WorkflowEngine.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>followSubject through the whole compile; shares <see cref="CompileFixture"/> with the reframe tests.</summary>
public partial class VideoCompileNarrationAndFormatTests
{
    /// <summary>A subject walking right, sampled every 0.5 s over 0..9 s of source 0.</summary>
    private static VideoInsertRegionTrack FollowTrack(string id = "r1") =>
        new(Id: id, ShotId: "s0", StartSec: 0, EndSec: 9,
            Keyframes: Enumerable.Range(0, 19).Select(i =>
            {
                double t = i * 0.5;
                double x = 0.2 + 0.6 * t / 9;
                return new VideoInsertQuadKeyframe(t, x, 0.45, x + 0.08, 0.45, x, 0.55, x + 0.08, 0.55);
            }).ToList(),
            Confidence: 0.2, MeanAreaRatio: 0.01, MeanAspectRatio: 1, MotionClass: "Moving", ColorName: "none",
            SourceIndex: 0, Method: "feature");

    private static VideoAnalysisArtifact FollowArtifact() => SingleSourceArtifact(10.0) with { InsertRegions = [FollowTrack()] };

    [Fact]
    public async Task A_followed_subject_runs_the_zoompan_stage_and_is_reported_in_output_and_edl()
    {
        var fixture = new CompileFixture(FollowArtifact());
        fixture.Config = c => c with
        {
            OutputFormat = VideoOutputFormat.Vertical,
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            FollowSubject = [new FollowSubjectRequest("r1", Tightness: FollowTightness.Loose)]
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain("zoompan");
        JsonObject entry = fixture.Output!["followSubject"]!.AsArray().Single()!.AsObject();
        entry["trackId"]!.GetValue<string>().Should().Be("r1");
        entry["applied"]!.GetValue<bool>().Should().BeTrue();
        entry["keyframes"]!.GetValue<int>().Should().BeGreaterThan(1);
        fixture.Output["reframes"]!["reframes"]!.AsArray().Should().HaveCount(entry["reframes"]!.GetValue<int>());
        fixture.Edl!["followSubject"]!.AsArray().Should().HaveCount(1);
        fixture.Edl["reframes"].Should().NotBeNull();
    }

    [Fact]
    public async Task Under_subject_fit_a_followed_uncaptioned_clip_is_cropped_and_followed_not_padded()
    {
        // Production run 180c2c62: Vertical + Subject + followSubject on an uncaptioned clip padded the whole
        // frame (text_unknown) and the follow did nothing. The reframe owns its spans now.
        var fixture = new CompileFixture(FollowArtifact());
        fixture.Config = c => c with
        {
            OutputFormat = VideoOutputFormat.Vertical,
            OutputFit = VideoOutputFit.Subject,
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            FollowSubject = [new FollowSubjectRequest("r1")]
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.FilterGraph.Should().Contain("zoompan");
        fixture.FilterGraph.Should().NotContain("scale=972:1728", "the safe-area pad never applies to a followed span");
        fixture.FilterGraph.Should().Contain("force_original_aspect_ratio=increase,crop=1080:1920:");
        JsonObject reframes = fixture.Output!["reframes"]!.AsObject();
        reframes.ContainsKey("safeAreaShots").Should().BeFalse();
        reframes["safeAreaReframed"]![0]!["reason"]!.GetValue<string>().Should().Be("text_unknown");
        fixture.Output.ContainsKey("warnings").Should().BeFalse("a covered follow can pan, so nothing to warn about");
    }

    [Fact]
    public async Task A_follow_on_a_padded_clip_cannot_move_and_says_so()
    {
        var fixture = new CompileFixture(FollowArtifact());
        fixture.Config = c => c with
        {
            OutputFormat = VideoOutputFormat.Vertical,
            OutputFit = VideoOutputFit.Pad,
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            FollowSubject = [new FollowSubjectRequest("r1")]
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        fixture.Output!["followSubject"]![0]!["framingFixed"]!.GetValue<bool>().Should().BeTrue();
        fixture.Output["warnings"]!.AsArray().Select(w => w!["code"]!.GetValue<string>())
            .Should().Contain("follow_subject_cannot_move");
    }

    [Fact]
    public async Task An_explicit_crop_survives_a_vertical_run_format_and_the_output_reports_it()
    {
        // Production run a76cd793: outputFit "Crop" was set on the step, yet the output said Subject. The
        // fixture stores the full config, so outputFit is named explicitly here.
        var fixture = new CompileFixture(SingleSourceArtifact(10.0))
        {
            Execution = new WorkflowExecution { Id = System.Guid.NewGuid(), ProjectId = ProjectId, OutputFormatOverride = RunOutputFormat.Vertical }
        };
        fixture.Config = c => c with { OutputFit = VideoOutputFit.Crop, PrePaddingMs = 0, PostPaddingMs = 0 };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Completed, because: result.ErrorDetails ?? result.Output);
        JsonObject output = fixture.Output!["output"]!.AsObject();
        output["format"]!.GetValue<string>().Should().Be("Vertical");
        output["fit"]!.GetValue<string>().Should().Be("Crop");
        output.ContainsKey("fitSetBy").Should().BeFalse();
    }

    [Fact]
    public async Task An_absent_or_empty_followSubject_leaves_the_graph_and_argv_unchanged()
    {
        var plain = new CompileFixture(FollowArtifact());
        plain.Config = c => c with { OutputFormat = VideoOutputFormat.Vertical, PrePaddingMs = 0, PostPaddingMs = 0 };
        var empty = new CompileFixture(FollowArtifact());
        empty.Config = c => c with { OutputFormat = VideoOutputFormat.Vertical, PrePaddingMs = 0, PostPaddingMs = 0, FollowSubject = [] };

        (await plain.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);
        (await empty.RunAsync(BuildDecisionJson("s0"))).Status.Should().Be(StepStatus.Completed);

        empty.FilterGraph.Should().Be(plain.FilterGraph);
        empty.FilterGraph.Should().NotContain("zoompan");
        empty.EncodeArgs!.Select(NormalizeScratch).Should().Equal(plain.EncodeArgs!.Select(NormalizeScratch));
        empty.Output!.ContainsKey("followSubject").Should().BeFalse();
        empty.Edl!.ContainsKey("followSubject").Should().BeFalse();
    }

    [Fact]
    public async Task A_track_the_analysis_does_not_hold_fails_before_any_encode()
    {
        var fixture = new CompileFixture(FollowArtifact());
        fixture.Config = c => c with { PrePaddingMs = 0, PostPaddingMs = 0, FollowSubject = [new FollowSubjectRequest("r7")] };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("FOLLOW_TRACK_NOT_FOUND");
        fixture.EncodeArgs.Should().BeNull();
    }

    [Fact]
    public async Task Following_a_stream_copy_is_a_config_error()
    {
        var fixture = new CompileFixture(FollowArtifact());
        fixture.Config = c => c with
        {
            Mode = VideoCompileMode.StreamCopy,
            AllowKeyframeSnapping = true,
            FollowSubject = [new FollowSubjectRequest("r1")]
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("FOLLOW_REQUIRES_REENCODE");
    }

    [Fact]
    public async Task A_hand_reframe_and_a_follow_on_one_source_is_refused()
    {
        var fixture = new CompileFixture(FollowArtifact());
        fixture.Config = c => c with
        {
            PrePaddingMs = 0,
            PostPaddingMs = 0,
            Reframes = [new VideoReframe(0, 0, 2, [RKey(0, 2)])],
            FollowSubject = [new FollowSubjectRequest("r1", SourceIndex: 0)]
        };

        StepExecutionResult result = await fixture.RunAsync(BuildDecisionJson("s0"));

        result.Status.Should().Be(StepStatus.Failed);
        ErrorCode(result).Should().Be("FOLLOW_CONFLICTS_REFRAME");
    }
}
