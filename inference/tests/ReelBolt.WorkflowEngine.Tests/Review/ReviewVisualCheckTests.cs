using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Review;
using ReelBolt.WorkflowEngine.Services.Video;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// <see cref="ReviewVisualCheck"/> (the orchestration: source → provider → frames → one vision call) and
/// <see cref="FfmpegVisualCheckFrameGrabber"/> (stage, probe, extract the scheduled frames through the engine's
/// own media workspace). Every collaborator is a fake: no ffmpeg, no model.
/// </summary>
public class ReviewVisualCheckTests
{
    private static readonly Guid ProjectId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly string CompileKey = $"projects/{ProjectId:D}/outputFiles/compile.mp4";
    private static readonly ResolvedInferenceProvider Provider = new(Guid.NewGuid(), "vision", default, "http://vision", "vision-model", "k", 30);
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 9];

    // ---- orchestration -------------------------------------------------

    [Fact]
    public async Task No_playable_output_is_unavailable_and_asks_nobody()
    {
        Mock<IInferenceProviderResolver> resolver = new();
        Mock<IVisualCheckFrameGrabber> grabber = new();
        Mock<IVisualFrameReviewer> reviewer = new();

        VisualCheckOutcome outcome = await new ReviewVisualCheck(resolver.Object, grabber.Object, reviewer.Object, NullLogger<ReviewVisualCheck>.Instance)
            .RunAsync(Context(outputKey: null), new VisualCheckConfig(true), CancellationToken.None);

        outcome.Status.Should().Be(VisualCheckOutcome.StatusUnavailable);
        outcome.Reason.Should().Contain("no playable render or compile output");
        resolver.VerifyNoOtherCalls();
        grabber.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task No_vision_provider_is_unavailable_and_extracts_nothing()
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedInferenceProvider?)null);
        Mock<IVisualCheckFrameGrabber> grabber = new();

        VisualCheckOutcome outcome = await new ReviewVisualCheck(resolver.Object, grabber.Object, Mock.Of<IVisualFrameReviewer>(), NullLogger<ReviewVisualCheck>.Instance)
            .RunAsync(Context(CompileKey), new VisualCheckConfig(true), CancellationToken.None);

        outcome.Status.Should().Be(VisualCheckOutcome.StatusUnavailable);
        outcome.Reason.Should().Be("no vision provider is configured");
        outcome.Capped.Should().BeFalse();
        grabber.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task The_configured_provider_id_is_resolved_and_findings_come_back_checked()
    {
        Guid providerId = Guid.NewGuid();
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(providerId, It.IsAny<CancellationToken>())).ReturnsAsync(Provider);

        IReadOnlyList<VisualCheckFrameImage> frames = [new(1, 0, Jpeg), new(2, 5, Jpeg), new(3, 9.9, Jpeg)];
        Mock<IVisualCheckFrameGrabber> grabber = new();
        grabber.Setup(g => g.GrabAsync(It.IsAny<StepExecutionContext>(), It.Is<VisualCheckSource>(s => s.StorageKey == CompileKey && s.StepOrder == 3), 4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisualCheckFrameGrab(frames, null));

        VisualCheckFinding finding = new(3, 9.9, "text_cropped", "end-card lockup cut at the right edge", "high");
        Mock<IVisualFrameReviewer> reviewer = new();
        reviewer.Setup(r => r.ReviewAsync(Provider, frames, It.IsAny<VisualCheckConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisualFrameReview([finding], null, 1));

        VisualCheckOutcome outcome = await new ReviewVisualCheck(resolver.Object, grabber.Object, reviewer.Object, NullLogger<ReviewVisualCheck>.Instance)
            .RunAsync(Context(CompileKey), new VisualCheckConfig(true, Frames: 4, FailScoreCap: 5, VisionProviderId: providerId), CancellationToken.None);

        outcome.Status.Should().Be(VisualCheckOutcome.StatusChecked);
        outcome.SourceStepOrder.Should().Be(3);
        outcome.Frames.Select(f => f.AtSec).Should().Equal(0, 5, 9.9);
        outcome.Findings.Should().Equal(finding);
        outcome.Capped.Should().BeTrue();
        outcome.FailScoreCap.Should().Be(5);
    }

    [Fact]
    public async Task A_frame_grab_failure_or_exception_is_unavailable_never_thrown()
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Provider);
        Mock<IVisualCheckFrameGrabber> grabber = new();
        grabber.SetupSequence(g => g.GrabAsync(It.IsAny<StepExecutionContext>(), It.IsAny<VisualCheckSource>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisualCheckFrameGrab([], "the output's duration could not be read"))
            .ThrowsAsync(new InvalidOperationException("ffmpeg exploded at /tmp/secret/path"));

        ReviewVisualCheck check = new(resolver.Object, grabber.Object, Mock.Of<IVisualFrameReviewer>(), NullLogger<ReviewVisualCheck>.Instance);

        VisualCheckOutcome first = await check.RunAsync(Context(CompileKey), new VisualCheckConfig(true), CancellationToken.None);
        VisualCheckOutcome second = await check.RunAsync(Context(CompileKey), new VisualCheckConfig(true), CancellationToken.None);

        first.Reason.Should().Be("the output's duration could not be read");
        second.Status.Should().Be(VisualCheckOutcome.StatusUnavailable);
        second.Reason.Should().Be("the visual check failed").And.NotContain("/tmp");
    }

    [Fact]
    public async Task A_reviewer_that_could_not_answer_is_unavailable()
    {
        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Provider);
        Mock<IVisualCheckFrameGrabber> grabber = new();
        grabber.Setup(g => g.GrabAsync(It.IsAny<StepExecutionContext>(), It.IsAny<VisualCheckSource>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisualCheckFrameGrab([new(1, 0, Jpeg)], null));
        Mock<IVisualFrameReviewer> reviewer = new();
        reviewer.Setup(r => r.ReviewAsync(It.IsAny<ResolvedInferenceProvider>(), It.IsAny<IReadOnlyList<VisualCheckFrameImage>>(), It.IsAny<VisualCheckConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(VisualFrameReview.None("the vision model's answer could not be read", 2));

        VisualCheckOutcome outcome = await new ReviewVisualCheck(resolver.Object, grabber.Object, reviewer.Object, NullLogger<ReviewVisualCheck>.Instance)
            .RunAsync(Context(CompileKey), new VisualCheckConfig(true), CancellationToken.None);

        outcome.Status.Should().Be(VisualCheckOutcome.StatusUnavailable);
        outcome.Reason.Should().Be("the vision model's answer could not be read");
    }

    // ---- the ffmpeg frame grabber ---------------------------------------

    [Fact]
    public async Task The_grabber_extracts_the_scheduled_frames_from_the_staged_output()
    {
        Mock<IMediaWorkspace> workspace = new();
        workspace.Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Jpeg);
        Mock<IMediaWorkspaceFactory> factory = WorkspaceFactory(workspace.Object, out List<MediaWorkspaceRequest> requests);

        Mock<IMediaProbe> probe = new();
        probe.Setup(p => p.ProbeAsync(workspace.Object, "visual-check/source.mp4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(20, 30, 1, 1920, 1080, "h264", null, null));

        List<(string Output, double AtSec, int MaxWidth)> extracted = [];
        Mock<IKeyframeExtractor> extractor = new();
        extractor.Setup(e => e.ExtractKeyframeAsync(workspace.Object, "visual-check/source.mp4", It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<IMediaWorkspace, string, string, double, int, CancellationToken>((_, _, output, at, width, _) => extracted.Add((output, at, width)))
            .Returns(Task.CompletedTask);

        StepExecutionContext context = Context(CompileKey);
        VisualCheckFrameGrab grab = await new FfmpegVisualCheckFrameGrabber(factory.Object, probe.Object, extractor.Object, NullLogger<FfmpegVisualCheckFrameGrabber>.Instance)
            .GrabAsync(context, new VisualCheckSource(3, CompileKey), 4, CancellationToken.None);

        grab.Failure.Should().BeNull();
        workspace.Verify(w => w.StageAsync(CompileKey, "visual-check/source.mp4", It.IsAny<CancellationToken>()), Times.Once);
        extracted.Select(e => e.AtSec).Should().Equal(0, 5, 10, 15, 19.9);
        extracted.Select(e => e.Output).Should().Equal(
            "visual-check/frame-01.jpg", "visual-check/frame-02.jpg", "visual-check/frame-03.jpg", "visual-check/frame-04.jpg", "visual-check/frame-05.jpg");
        extracted.Should().OnlyContain(e => e.MaxWidth == FfmpegVisualCheckFrameGrabber.FrameMaxWidth);
        grab.Frames.Select(f => (f.Frame, f.AtSec)).Should().Equal((1, 0), (2, 5), (3, 10), (4, 15), (5, 19.9));

        requests.Should().ContainSingle();
        requests[0].RunnerEligible.Should().BeFalse();
        requests[0].ProjectId.Should().Be(ProjectId);
        requests[0].Purpose.Should().Be("review.visual-check:4");
    }

    [Fact]
    public async Task The_grabber_skips_a_frame_ffmpeg_could_not_extract_and_fails_on_no_duration()
    {
        Mock<IMediaWorkspace> workspace = new();
        workspace.Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Jpeg);
        Mock<IMediaWorkspaceFactory> factory = WorkspaceFactory(workspace.Object, out _);

        Mock<IMediaProbe> probe = new();
        probe.SetupSequence(p => p.ProbeAsync(workspace.Object, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(10, 30, 1, 1080, 1920, "h264", null, null))
            .ReturnsAsync(new MediaProbeResult(0, 0, 0, 0, 0, null, null, null));

        Mock<IKeyframeExtractor> extractor = new();
        extractor.Setup(e => e.ExtractKeyframeAsync(workspace.Object, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns<IMediaWorkspace, string, string, double, int, CancellationToken>((_, _, output, _, _, _) =>
                output.EndsWith("frame-02.jpg", StringComparison.Ordinal) ? throw new InvalidOperationException("ffmpeg failed") : Task.CompletedTask);

        FfmpegVisualCheckFrameGrabber grabber = new(factory.Object, probe.Object, extractor.Object, NullLogger<FfmpegVisualCheckFrameGrabber>.Instance);

        VisualCheckFrameGrab grab = await grabber.GrabAsync(Context(CompileKey), new VisualCheckSource(3, CompileKey), 2, CancellationToken.None);
        grab.Frames.Select(f => f.Frame).Should().Equal(1, 3);

        VisualCheckFrameGrab empty = await grabber.GrabAsync(Context(CompileKey), new VisualCheckSource(3, CompileKey), 2, CancellationToken.None);
        empty.Frames.Should().BeEmpty();
        empty.Failure.Should().Be("the output's duration could not be read");
    }

    // ---- overlays sampled by the visual check ---------------------------

    [Fact]
    public async Task A_short_card_midpoint_is_sampled_instead_of_an_even_frame_within_the_cap()
    {
        List<(string Output, double AtSec, int MaxWidth)> extracted = await GrabWithCompileOutput(
            durationSec: 30, frames: 4,
            compileOutput: """{"mediaOverlays":{"applied":true,"items":[{"media":"card.png","form":"Card","startSec":12,"endSec":15}]}}""");

        // Even sampling alone (0, 7.5, 15, 22.5, 29.9) only touches the card's edge at 15 s; the
        // midpoint 13.5 replaces the nearest interior sample and the count stays at Frames + 1.
        extracted.Select(e => e.AtSec).Should().Equal(0, 7.5, 13.5, 22.5, 29.9);
        extracted.Should().HaveCount(5);
    }

    [Fact]
    public async Task Several_overlay_midpoints_never_exceed_the_cap()
    {
        List<(string Output, double AtSec, int MaxWidth)> extracted = await GrabWithCompileOutput(
            durationSec: 30, frames: 4,
            compileOutput: """
                {"mediaOverlays":{"items":[
                  {"startSec":2,"endSec":4},
                  {"startSec":25,"endSec":29}
                ]}}
                """);

        double[] atSec = [.. extracted.Select(e => e.AtSec)];
        atSec.Should().HaveCount(5);
        atSec.Should().Contain(3).And.Contain(27);
    }

    [Fact]
    public async Task An_unreadable_or_missing_compile_node_keeps_even_sampling()
    {
        List<(string Output, double AtSec, int MaxWidth)> unreadable = await GrabWithCompileOutput(
            30, 4, "{not json");
        List<(string Output, double AtSec, int MaxWidth)> noItems = await GrabWithCompileOutput(
            30, 4, """{"mediaOverlays":{"applied":false,"items":[]}}""");

        double[] even = [0, 7.5, 15, 22.5, 29.9];
        unreadable.Select(e => e.AtSec).Should().Equal(even);
        noItems.Select(e => e.AtSec).Should().Equal(even);
    }

    [Fact]
    public async Task Overlay_frames_ride_the_one_existing_vision_call()
    {
        Mock<IMediaWorkspace> workspace = new();
        workspace.Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Jpeg);
        Mock<IMediaWorkspaceFactory> factory = WorkspaceFactory(workspace.Object, out _);

        Mock<IMediaProbe> probe = new();
        probe.Setup(p => p.ProbeAsync(workspace.Object, "visual-check/source.mp4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(30, 30, 1, 1920, 1080, "h264", null, null));
        Mock<IKeyframeExtractor> extractor = new();
        extractor.Setup(e => e.ExtractKeyframeAsync(workspace.Object, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        Mock<IInferenceProviderResolver> resolver = new();
        resolver.Setup(r => r.ResolveVisionAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Provider);
        Mock<IVisualFrameReviewer> reviewer = new();
        reviewer.Setup(r => r.ReviewAsync(Provider, It.IsAny<IReadOnlyList<VisualCheckFrameImage>>(), It.IsAny<VisualCheckConfig>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VisualFrameReview([], null, 1));

        ReviewVisualCheck check = new(
            resolver.Object,
            new FfmpegVisualCheckFrameGrabber(factory.Object, probe.Object, extractor.Object, NullLogger<FfmpegVisualCheckFrameGrabber>.Instance),
            reviewer.Object,
            NullLogger<ReviewVisualCheck>.Instance);

        VisualCheckOutcome outcome = await check.RunAsync(
            Context(CompileKey, """{"mediaOverlays":{"items":[{"startSec":12,"endSec":15}]}}"""),
            new VisualCheckConfig(true, Frames: 4),
            CancellationToken.None);

        outcome.Status.Should().Be(VisualCheckOutcome.StatusChecked);
        outcome.Frames.Select(f => f.AtSec).Should().Equal(0, 7.5, 13.5, 22.5, 29.9);
        reviewer.Verify(r => r.ReviewAsync(Provider, It.IsAny<IReadOnlyList<VisualCheckFrameImage>>(), It.IsAny<VisualCheckConfig>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task<List<(string Output, double AtSec, int MaxWidth)>> GrabWithCompileOutput(
        double durationSec, int frames, string compileOutput)
    {
        Mock<IMediaWorkspace> workspace = new();
        workspace.Setup(w => w.ReadAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Jpeg);
        Mock<IMediaWorkspaceFactory> factory = WorkspaceFactory(workspace.Object, out _);

        Mock<IMediaProbe> probe = new();
        probe.Setup(p => p.ProbeAsync(workspace.Object, "visual-check/source.mp4", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaProbeResult(durationSec, 30, 1, 1920, 1080, "h264", null, null));

        List<(string Output, double AtSec, int MaxWidth)> extracted = [];
        Mock<IKeyframeExtractor> extractor = new();
        extractor.Setup(e => e.ExtractKeyframeAsync(workspace.Object, "visual-check/source.mp4", It.IsAny<string>(), It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback<IMediaWorkspace, string, string, double, int, CancellationToken>((_, _, output, at, width, _) => extracted.Add((output, at, width)))
            .Returns(Task.CompletedTask);

        await new FfmpegVisualCheckFrameGrabber(factory.Object, probe.Object, extractor.Object, NullLogger<FfmpegVisualCheckFrameGrabber>.Instance)
            .GrabAsync(Context(CompileKey, compileOutput), new VisualCheckSource(3, CompileKey), frames, CancellationToken.None);
        return extracted;
    }

    private static Mock<IMediaWorkspaceFactory> WorkspaceFactory(IMediaWorkspace workspace, out List<MediaWorkspaceRequest> requests)
    {
        List<MediaWorkspaceRequest> seen = [];
        requests = seen;
        Mock<IMediaWorkspaceFactory> factory = new();
        factory.Setup(f => f.OpenAsync(It.IsAny<MediaWorkspaceRequest>(), It.IsAny<CancellationToken>()))
            .Callback<MediaWorkspaceRequest, CancellationToken>((r, _) => seen.Add(r))
            .ReturnsAsync(() => MediaWorkspaceOpenResult.Opened(new MediaWorkspaceLease(
                workspace, null, ComputeDecision.Local(ComputeDecisionReasons.LocalMode), null, Guid.NewGuid(), 4, NullLogger.Instance)));
        return factory;
    }

    private static StepExecutionContext Context(string? outputKey, string compileOutput = "{}")
    {
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 4, StepType = StepType.ReviewLoop, LoopTargetStepOrder = 2 };
        WorkflowStep compile = new() { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile };
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = ProjectId },
            Step = step,
            AllSteps = [step, compile],
            AccumulatedOutput = "{}",
            StepOutputHistory =
            [
                new StepOutputHistoryEntry(2, "Planner", "{}"),
                new StepOutputHistoryEntry(3, "VideoCompile", compileOutput, outputKey),
            ],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None,
        };
    }
}
