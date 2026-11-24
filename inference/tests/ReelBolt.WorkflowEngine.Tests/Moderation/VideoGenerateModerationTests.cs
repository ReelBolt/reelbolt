using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Moderation;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Compute;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using ReelBolt.WorkflowEngine.Tests;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// T-005: <see cref="VideoGenerateStepExecutor"/> screens the inline prompt (and the planned shots)
/// before any budget reservation. A Block buys nothing; an outage (Unavailable) or the mid band
/// (Review) fails open.
/// </summary>
public sealed class VideoGenerateModerationTests
{
    private const string Prompt = "Create a video of a lighthouse at dusk";
    private const string InlineConfig =
        """{"maxSpendCredits": 100000.0, "takes": 1, "inlinePrompt": "Create a video of a lighthouse at dusk", "timeoutSeconds": 60, "model": "MiniMax-H3", "durationSeconds": 10, "resolution": "1080P", "ratio": "16:9"}""";

    private sealed class FakeGate : IContentModerationGate
    {
        public ModerationDecision Decision { get; set; } = new(
            ModerationAction.Allow, true, false, null, string.Empty, "hash");

        public List<ModerationInput> Inputs { get; } = [];
        public List<ModerationAttribution> Attributions { get; } = [];

        public Task<ModerationDecision> ModerateAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
        {
            Inputs.Add(input);
            Attributions.Add(attribution);
            return Task.FromResult(Decision);
        }

        public Task<bool> ScreenGenerationPromptAsync(
            string text, IReadOnlyList<ModerationImageInput> images, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Action != ModerationAction.Block);

        public Task<bool> ScreenGeneratedTextAsync(
            string text, string site, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Action != ModerationAction.Block);

        public Task<bool> ScreenPublishAsync(
            ModerationInput input, ModerationAttribution attribution, CancellationToken ct)
            => Task.FromResult(Decision.Available && Decision.Action != ModerationAction.Block);
    }

    private sealed class Rig
    {
        public SharedInMemoryDbFactory Db { get; } = new();
        public Mock<IVideoGenerationClient> Client { get; } = new();
        public Mock<IVideoGenerationBudgetGate> BudgetGate { get; } = new();
        public Guid ExecutionId { get; } = Guid.NewGuid();
        public Guid ProjectId { get; } = Guid.NewGuid();

        public Rig()
        {
            Client.Setup(c => c.Describe(It.IsAny<string>()))
                .Returns(new VideoGenerationCapabilities(
                    new Dictionary<string, decimal> { { "1080P", 0.025m } },
                    4, 15, new[] { "16:9" }));
            Client.Setup(c => c.SubmitAsync(It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ExternalJobHandle("remote-1"));
            Client.Setup(c => c.PollAsync(It.IsAny<ExternalJobHandle>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("vendor poll failed"));
        }

        public VideoGenerateStepExecutor Executor(IContentModerationGate? gate, bool realBudgetGate)
        {
            var resolver = new Mock<IInferenceProviderResolver>();
            var provider = new ResolvedVideoGenerationProvider(
                Guid.NewGuid(), "MiniMax", InferenceProviderKind.MiniMax,
                "https://api.minimax.io", "MiniMax-H3", "key123", 1800);
            resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(provider);

            var factory = new Mock<IVideoGenerationClientFactory>();
            factory.Setup(f => f.Get(provider)).Returns(Client.Object);

            var runner = new Mock<IVideoToolRunner>();
            var normalizer = new GeneratedClipNormalizer(
                runner.Object, Options.Create(new VideoEditingOptions()), NullLogger<GeneratedClipNormalizer>.Instance);

            IVideoGenerationBudgetGate budgetGate = realBudgetGate
                ? VideoGenerateStepExecutorTests.RealGate(Db)
                : BudgetGate.Object;

            return new VideoGenerateStepExecutor(
                budgetGate,
                resolver.Object,
                factory.Object,
                new Mock<IProjectFileWorkspace>().Object,
                Db,
                normalizer,
                Options.Create(new VideoGenerationOptions()),
                NullLogger<VideoGenerateStepExecutor>.Instance,
                new Mock<IMediaWorkspaceFactory>().Object,
                new Mock<IKeyframeExtractor>().Object,
                moderation: gate);
        }

        public Task<StepExecutionResult> RunAsync(IContentModerationGate gate, bool realBudgetGate = false)
        {
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepOrder = 1,
                StepType = StepType.VideoGenerate,
                VideoGenerateConfigJson = InlineConfig
            };

            var execution = new WorkflowExecution
            {
                Id = ExecutionId,
                ProjectId = ProjectId,
                OrganizationId = Guid.NewGuid(),
                InitiatedByUserId = Guid.NewGuid(),
                Status = ExecutionStatus.Running
            };

            return Executor(gate, realBudgetGate).ExecuteAsync(new StepExecutionContext
            {
                StepResultId = Guid.NewGuid(),
                Execution = execution,
                Step = step,
                AllSteps = [step],
                AccumulatedOutput = string.Empty,
                CurrentStepIndex = 0,
                IterationCount = 0,
                CorrelationId = "moderation-test",
                StepOutputHistory = [],
                CancellationToken = CancellationToken.None
            });
        }
    }

    private static FakeGate GateFor(ModerationAction action, bool available = true) =>
        new()
        {
            Decision = new ModerationDecision(action, available, action == ModerationAction.Review,
                action == ModerationAction.Block ? "violence" : null,
                action == ModerationAction.Block ? "This prompt cannot be used for video generation." : string.Empty,
                "hash")
        };

    [Fact]
    public async Task A_blocked_inline_prompt_reserves_and_submits_nothing()
    {
        var rig = new Rig();
        FakeGate gate = GateFor(ModerationAction.Block);

        StepExecutionResult result = await rig.RunAsync(gate);

        result.Status.Should().Be(StepStatus.Failed);
        FailureClassifier.CodeFromOutput(result.Output).Should().Be("CONTENT_BLOCKED");
        result.ErrorDetails.Should().Be(gate.Decision.UserMessage);
        FailureClassifier.ForCode("CONTENT_BLOCKED").Should().Be(FailureClass.UserInput);

        rig.BudgetGate.Verify(g => g.ReserveAsync(
            It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Client.Verify(c => c.SubmitAsync(
            It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        gate.Inputs.Should().ContainSingle();
        gate.Inputs[0].Text.Should().Be(Prompt);
        gate.Attributions[0].Surface.Should().Be(ModerationSurface.GenerationPrompt);
        gate.Attributions[0].OrganizationId.Should().NotBe(Guid.Empty);
    }

    [Theory]
    [InlineData(ModerationAction.Review, true)]
    [InlineData(ModerationAction.Allow, false)]
    public async Task Review_and_unavailable_proceed_to_reserve_and_submit(ModerationAction action, bool available)
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(GateFor(action, available), realBudgetGate: true);

        result.Status.Should().Be(StepStatus.Failed);
        FailureClassifier.CodeFromOutput(result.Output).Should().NotBe("CONTENT_BLOCKED");
        rig.Client.Verify(c => c.SubmitAsync(
            It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task An_allow_result_leaves_the_step_behaviour_unchanged_and_still_moderates()
    {
        var rig = new Rig();
        FakeGate gate = GateFor(ModerationAction.Allow);

        StepExecutionResult result = await rig.RunAsync(gate, realBudgetGate: true);

        gate.Inputs.Should().ContainSingle().Which.Text.Should().Be(Prompt);
        result.Status.Should().Be(StepStatus.Failed);
        FailureClassifier.CodeFromOutput(result.Output).Should().NotBe("CONTENT_BLOCKED");
        rig.Client.Verify(c => c.SubmitAsync(
            It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Without_a_gate_the_executor_behaves_exactly_as_before()
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.Executor(gate: null, realBudgetGate: true)
            .ExecuteAsync(Context(rig));

        FailureClassifier.CodeFromOutput(result.Output).Should().NotBe("CONTENT_BLOCKED");
        rig.Client.Verify(c => c.SubmitAsync(
            It.IsAny<VideoGenerationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static StepExecutionContext Context(Rig rig)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.VideoGenerate,
            VideoGenerateConfigJson = InlineConfig
        };
        return new StepExecutionContext
        {
            StepResultId = Guid.NewGuid(),
            Execution = new WorkflowExecution
            {
                Id = rig.ExecutionId,
                ProjectId = rig.ProjectId,
                OrganizationId = Guid.NewGuid(),
                Status = ExecutionStatus.Running
            },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "moderation-test",
            StepOutputHistory = [],
            CancellationToken = CancellationToken.None
        };
    }
}
