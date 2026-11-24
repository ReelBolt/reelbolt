using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Inference;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using ReelBolt.WorkflowEngine.Tests;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Moderation;

/// <summary>
/// T-005: <see cref="AudioGenerateStepExecutor"/> screens <c>config.Prompt</c> before the single
/// budget reservation. A Block reserves and submits nothing; Unavailable/Review fail open.
/// </summary>
public sealed class AudioGenerateModerationTests
{
    private const string Prompt = "Warm uplifting corporate pop, light percussion, no vocals";
    private const string MusicConfig =
        """{"kind":"Music","prompt":"Warm uplifting corporate pop, light percussion, no vocals","durationSec":30,"instrumental":true,"model":"music_v2_5"}""";

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
        public Mock<IAudioGenerationClient> Client { get; } = new();
        public Mock<IVideoGenerationBudgetGate> BudgetGate { get; } = new();

        public Rig()
        {
            AudioGenerationCapabilities capabilities =
                AudioModelCatalog.Instance.Find(InferenceProviderKind.ElevenLabs, "music_v2_5")!.Capabilities;
            Client.Setup(c => c.Describe("music_v2_5")).Returns(capabilities);
            Client.Setup(c => c.GenerateAsync(
                    It.IsAny<AudioGenerationRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()))
                .ThrowsAsync(new IOException("the vendor connection dropped after submit"));
        }

        public AudioGenerateStepExecutor Executor(IContentModerationGate? gate, bool realBudgetGate)
        {
            var provider = new ResolvedAudioGenerationProvider(
                Guid.NewGuid(), "ElevenLabs", InferenceProviderKind.ElevenLabs,
                InferenceProviderCapability.MusicGeneration, "https://api.elevenlabs.io", "", "test", 60,
                IsPlatformManaged: true, AccountId: Guid.NewGuid());

            var resolver = new Mock<IInferenceProviderResolver>();
            resolver.Setup(r => r.ResolveMusicGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(provider);

            var factory = new Mock<IAudioGenerationClientFactory>();
            factory.Setup(f => f.Get(provider)).Returns(Client.Object);

            var probe = new Mock<IMediaProbe>();
            IVideoGenerationBudgetGate budgetGate = realBudgetGate
                ? VideoGenerateStepExecutorTests.RealGate(Db)
                : BudgetGate.Object;

            return new AudioGenerateStepExecutor(
                budgetGate,
                resolver.Object,
                factory.Object,
                new AudioGenerationDurationProbe(probe.Object),
                new Mock<IProjectFileWorkspace>().Object,
                Db,
                NullLogger<AudioGenerateStepExecutor>.Instance,
                moderation: gate);
        }

        public Task<StepExecutionResult> RunAsync(IContentModerationGate? gate, bool realBudgetGate = false)
        {
            var step = new WorkflowStep
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                StepOrder = 2,
                StepType = StepType.AudioGenerate,
                AudioGenerateConfigJson = MusicConfig
            };

            var execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
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
                CurrentStepIndex = 1,
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
                action == ModerationAction.Block ? "harassment" : null,
                action == ModerationAction.Block ? "This prompt cannot be used for audio generation." : string.Empty,
                "hash")
        };

    [Fact]
    public async Task A_blocked_prompt_reserves_and_submits_nothing()
    {
        var rig = new Rig();
        FakeGate gate = GateFor(ModerationAction.Block);

        StepExecutionResult result = await rig.RunAsync(gate);

        result.Status.Should().Be(StepStatus.Failed);
        result.FailureCode.Should().Be("CONTENT_BLOCKED");
        result.FailureClass.Should().Be(FailureClass.UserInput);
        result.ErrorDetails.Should().Contain(gate.Decision.UserMessage);

        rig.BudgetGate.Verify(g => g.ReserveAsync(
            It.IsAny<VideoGenerationReservationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        rig.Client.Verify(c => c.GenerateAsync(
            It.IsAny<AudioGenerationRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()), Times.Never);

        gate.Inputs.Should().ContainSingle();
        gate.Inputs[0].Text.Should().Be(Prompt);
        gate.Attributions[0].Surface.Should().Be(ModerationSurface.GenerationPrompt);
    }

    [Theory]
    [InlineData(ModerationAction.Review, true)]
    [InlineData(ModerationAction.Allow, false)]
    public async Task Review_and_unavailable_proceed_to_reserve_and_submit(ModerationAction action, bool available)
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(GateFor(action, available), realBudgetGate: true);

        result.FailureCode.Should().NotBe("CONTENT_BLOCKED");
        rig.Client.Verify(c => c.GenerateAsync(
            It.IsAny<AudioGenerationRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()), Times.Once);
    }

    [Fact]
    public async Task Without_a_gate_the_executor_behaves_exactly_as_before()
    {
        var rig = new Rig();

        StepExecutionResult result = await rig.RunAsync(gate: null, realBudgetGate: true);

        result.FailureCode.Should().NotBe("CONTENT_BLOCKED");
        rig.Client.Verify(c => c.GenerateAsync(
            It.IsAny<AudioGenerationRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>()), Times.Once);
    }
}
