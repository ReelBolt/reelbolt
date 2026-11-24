using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for the Shadow-mode Decision gate hook wired into <see cref="AgentStepExecutor"/> for
/// Colorist/MusicSupervisor steps only. The most important assertions here are the two
/// invisibility tests: the returned <see cref="StepExecutionResult"/> must be identical whether or
/// not a Decision provider is configured, and identical even when the gate itself throws.
///
/// <para>
/// The second half of this file covers phase 2's <b>Gate</b> mode on the same two agents, where the
/// order deliberately inverts: the bounded descriptor state and the decision call happen
/// <i>before</i> the agent, and a fully-accepted decision replaces the agent's output entirely. The
/// Gate tests drive a REAL <see cref="DecisionGate"/> over a fake <see cref="IDecisionClient"/> (see
/// <see cref="RealGateHarness"/>), so "accepted" and "escalated" are the shipped accept rule rather
/// than a stub of it.
/// </para>
/// </summary>
public class AgentStepExecutorDecisionShadowTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static IConfiguration GateConfig(string agentConfigName, IDictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?>
        {
            [$"Agents:{agentConfigName}:DecisionGate:Mode"] = "Gate"
        };

        if (extra is not null)
        {
            foreach (KeyValuePair<string, string?> pair in extra)
                values[pair.Key] = pair.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static Mock<IWorkflowExecutionContextAccessor> MockAccessor()
    {
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);
        return accessor;
    }

    private static AgentStepExecutor CreateExecutor(
        IReelBoltAgent agent,
        IDecisionGate decisionGate,
        IConfiguration? configuration = null)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);

        return new AgentStepExecutor(
            registry.Object,
            MockAccessor().Object,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            configuration ?? EmptyConfig(),
            decisionGate,
            NullLogger<AgentStepExecutor>.Instance);
    }

    private static Mock<IReelBoltAgent> MockAgent(AgentType agentType, string output, Type? outputSchemaType)
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(agentType.ToString());
        agent.Setup(a => a.AgentType).Returns(agentType);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        agent.Setup(a => a.OutputSchemaType).Returns(outputSchemaType);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = output, Success = true, TokensUsed = 10, InputTokens = 6, OutputTokens = 4 });
        return agent;
    }

    private static StepExecutionContext CreateContext(
        AgentType agentType, IReadOnlyList<StepOutputHistoryEntry>? history = null)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 5,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = agentType, Name = agentType.ToString() }
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = history ?? [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    private const string ColoristJson =
        """
        {"look":"Warm","strength":"Normal","shadowTone":"Lifted","highlightTone":"Neutral","reason":"r","planRationale":"pr"}
        """;

    private const string MusicJson =
        """
        {"trackId":"m1","intensity":"Balanced","ducking":"Normal","fit":"LoopToFit","reason":"r","planRationale":"pr"}
        """;

    // ------------------------------------------------------------------
    // The bounded descriptor history a Gate-mode step needs upstream of it
    // ------------------------------------------------------------------

    /// <summary>
    /// An upstream (step 1) <c>VideoAnalyze</c> <c>{view, meta}</c> envelope carrying both descriptor
    /// families a Gate-mode question set needs: the per-shot measured colour words (colourist) and
    /// the offered <c>m{n}</c> candidates (music supervisor).
    /// </summary>
    private static IReadOnlyList<StepOutputHistoryEntry> DescriptorHistory() =>
    [
        new StepOutputHistoryEntry(1, "VideoAnalyze", """
        {"view":{
           "shots":[
             {"id":"s0","startSec":0.0,"endSec":4.0,"durationSec":4.0,"v":{"temp":"Warm","tone":"Neutral","sat":"Normal"}},
             {"id":"s1","startSec":4.0,"endSec":8.0,"durationSec":4.0,"v":{"temp":"Cool","tone":"Deepened","sat":"Muted"}}
           ],
           "segments":[{"id":"t0","startSec":0.0,"endSec":4.0,"text":"TRANSCRIPT TEXT"}],
           "musicTracks":[{"id":"m0","name":"calm-piano-loop.mp3"},{"id":"m1","name":"bed-two.mp3"}]
         },
         "meta":{"version":2}}
        """)
    ];

    // ------------------------------------------------------------------
    // Decision-client fixtures
    // ------------------------------------------------------------------

    private static DecisionAnswer Answer(string name, string choice, double top, double second) =>
        new(name, DecisionKind.Choice, choice, null, null, top,
            new Dictionary<string, double>(StringComparer.Ordinal) { [choice] = top, ["__runnerUp"] = second });

    /// <summary>Every option at 0.95 with a 0.90 margin — clears the shipped 0.85/0.25 defaults.</summary>
    private static DecisionResult ConfidentResult(params (string Name, string Choice)[] answers)
    {
        var dict = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach ((string name, string choice) in answers)
            dict[name] = Answer(name, choice, top: 0.95, second: 0.05);

        return new DecisionResult("fake-model", dict, InputTokens: 11, OutputTokens: 7);
    }

    private static DecisionResult ColoristConfidentResult() => ConfidentResult(
        ("Look", "Filmic"), ("Strength", "Strong"), ("ShadowTone", "Deepened"), ("HighlightTone", "Brightened"));

    private static DecisionResult MusicConfidentResult() => ConfidentResult(
        ("Intensity", "Quiet"), ("Ducking", "Heavy"), ("Fit", "PlayOnce"));

    /// <summary>
    /// Answers everything the caller was actually asked, choosing its first offered option. Used by
    /// the grouping tests, where the number and membership of the calls is what is under test.
    /// </summary>
    private static void SetupEchoClient(Mock<IDecisionClient> client)
    {
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DecisionState _, IReadOnlyList<DecisionQuestion> questions, CancellationToken _) =>
                new DecisionResult(
                    "fake-model",
                    questions.ToDictionary(
                        q => q.Name,
                        q => Answer(q.Name, q.Options[0], top: 0.95, second: 0.05),
                        StringComparer.Ordinal),
                    InputTokens: 11,
                    OutputTokens: 7));
    }

    private static JsonSerializerOptions WebJson() => new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Every observable field of a <see cref="StepExecutionResult"/> except <c>DurationMs</c>, which
    /// is wall-clock rather than behaviour. Comparing this is how the "byte-identical to Mode.Off"
    /// claim is checked instead of asserted.
    /// </summary>
    private static string Describe(StepExecutionResult result) => JsonSerializer.Serialize(new
    {
        result.Output,
        result.NextStepIndex,
        result.NewIterationCount,
        result.TokensUsed,
        result.InputTokens,
        result.OutputTokens,
        result.ToolCalls,
        result.Reasoning,
        result.Status,
        result.ErrorDetails,
        result.IterationNumber,
        result.OutputStorageKey,
        result.ArtifactStorageKey,
        result.ChatTranscriptJson
    });

    /// <summary>
    /// A REAL <see cref="DecisionGate"/> over a fake <see cref="IDecisionClient"/> and an in-memory
    /// observation store. Driving the real gate is what makes the accept/escalate assertions here
    /// evidence about the shipped rule rather than about a re-statement of it.
    /// </summary>
    private sealed class RealGateHarness
    {
        public Mock<IDecisionClient> Client { get; } = new();

        public DecisionGate Gate { get; }

        public WorkflowEngineDbContext Db { get; }

        public RealGateHarness(bool providerConfigured = true)
        {
            Db = new WorkflowEngineDbContext(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                    .UseInMemoryDatabase($"AgentStepExecutorGate_{Guid.NewGuid()}")
                    .Options);

            var resolver = new Mock<IInferenceProviderResolver>();
            var factory = new Mock<IDecisionClientFactory>();

            if (providerConfigured)
            {
                var provider = new ResolvedDecisionProvider(
                    ProviderId: Guid.NewGuid(),
                    Name: "mock-provider",
                    Kind: InferenceProviderKind.OpenAICompatible,
                    Endpoint: "http://test",
                    ModelName: "test-model",
                    ApiKey: "key",
                    TimeoutSeconds: 300);

                resolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(provider);
                factory.Setup(f => f.Get(provider)).Returns(Client.Object);
            }

            var scopeServiceProvider = new Mock<IServiceProvider>();
            scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(Db);

            var scope = new Mock<IServiceScope>();
            scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

            var scopeFactory = new Mock<IServiceScopeFactory>();
            scopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);

            Gate = new DecisionGate(
                resolver.Object, factory.Object, scopeFactory.Object, NullLogger<DecisionGate>.Instance);
        }
    }

    // ==================================================================
    // Shadow mode (phase 1) — unchanged
    // ==================================================================

    [Fact]
    public async Task Colorist_step_invokes_gate_exactly_once_with_four_fixed_questions()
    {
        var decisionGate = new Mock<IDecisionGate>();
        decisionGate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, decisionGate.Object);
        var context = CreateContext(AgentType.Colorist);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);

        decisionGate.Verify(g => g.RunShadowAsync(
            context.Execution.Id, context.Step.StepOrder, AgentType.Colorist, It.IsAny<DecisionState>(),
            It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()),
            Times.Once);

        decisionGate.Invocations.Should().ContainSingle();
        var captured = (IReadOnlyList<DecisionGateQuestion>)decisionGate.Invocations[0].Arguments[4];

        captured.Should().HaveCount(4);
        captured[0].Site.Should().Be("Colorist.Look");
        captured[0].Options.Should().BeEquivalentTo(["None", "Warm", "Cool", "Filmic", "Vibrant", "Muted", "Mono"]);
        captured[0].SystemTwoAnswer.Should().Be("Warm");
        captured[1].Site.Should().Be("Colorist.Strength");
        captured[1].Options.Should().BeEquivalentTo(["Subtle", "Normal", "Strong"]);
        captured[1].SystemTwoAnswer.Should().Be("Normal");
        captured[2].Site.Should().Be("Colorist.ShadowTone");
        captured[2].Options.Should().BeEquivalentTo(["Neutral", "Lifted", "Deepened"]);
        captured[2].SystemTwoAnswer.Should().Be("Lifted");
        captured[3].Site.Should().Be("Colorist.HighlightTone");
        captured[3].Options.Should().BeEquivalentTo(["Neutral", "Softened", "Brightened"]);
        captured[3].SystemTwoAnswer.Should().Be("Neutral");
    }

    [Fact]
    public async Task MusicSupervisor_step_invokes_gate_exactly_once_with_three_fixed_questions_excluding_trackId()
    {
        var decisionGate = new Mock<IDecisionGate>();
        decisionGate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var agent = MockAgent(AgentType.MusicSupervisor, MusicJson, typeof(ReelBolt.Shared.Data.OutputSchemas.MusicPlanOutput));
        var executor = CreateExecutor(agent.Object, decisionGate.Object);
        var context = CreateContext(AgentType.MusicSupervisor);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);

        decisionGate.Verify(g => g.RunShadowAsync(
            context.Execution.Id, context.Step.StepOrder, AgentType.MusicSupervisor, It.IsAny<DecisionState>(),
            It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()),
            Times.Once);

        var captured = (IReadOnlyList<DecisionGateQuestion>)decisionGate.Invocations[0].Arguments[4];

        captured.Should().HaveCount(3);
        captured.Should().NotContain(q => q.Site.Contains("TrackId", StringComparison.OrdinalIgnoreCase));
        captured[0].Site.Should().Be("MusicSupervisor.Intensity");
        captured[0].Options.Should().BeEquivalentTo(["Quiet", "Balanced", "Feature"]);
        captured[0].SystemTwoAnswer.Should().Be("Balanced");
        captured[1].Site.Should().Be("MusicSupervisor.Ducking");
        captured[1].Options.Should().BeEquivalentTo(["Off", "Light", "Normal", "Heavy"]);
        captured[1].SystemTwoAnswer.Should().Be("Normal");
        captured[2].Site.Should().Be("MusicSupervisor.Fit");
        captured[2].Options.Should().BeEquivalentTo(["LoopToFit", "PlayOnce"]);
        captured[2].SystemTwoAnswer.Should().Be("LoopToFit");
    }

    [Fact]
    public async Task Other_agent_type_never_invokes_gate()
    {
        var decisionGate = new Mock<IDecisionGate>();
        var agent = MockAgent(AgentType.DirectorAgent, """{"anything":"goes"}""", null);
        var executor = CreateExecutor(agent.Object, decisionGate.Object);
        var context = CreateContext(AgentType.DirectorAgent);

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        decisionGate.Verify(g => g.RunShadowAsync(
            It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
            It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Gate_receives_exactly_the_bounded_prompt_input_the_agent_saw()
    {
        DecisionState? capturedState = null;
        var decisionGate = new Mock<IDecisionGate>();
        decisionGate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, int, AgentType, DecisionState, IReadOnlyList<DecisionGateQuestion>, DecisionGateConfig, CancellationToken>(
                (_, _, _, state, _, _, _) => capturedState = state)
            .Returns(Task.CompletedTask);

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, decisionGate.Object);
        var context = CreateContext(AgentType.Colorist);

        await executor.ExecuteAsync(context);

        capturedState.Should().NotBeNull();
        capturedState!.Content.Should().Be(context.LastResolvedAgentInput);
    }

    [Fact]
    public async Task Result_is_identical_whether_or_not_a_decision_provider_is_configured()
    {
        var noOpGate = new Mock<IDecisionGate>();
        noOpGate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask); // simulates Mode=Off / no provider resolved

        var configuredGate = new Mock<IDecisionGate>();
        configuredGate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask); // simulates a fully configured provider that persisted rows internally

        var agent1 = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var agent2 = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));

        StepExecutionResult resultWithoutProvider = await CreateExecutor(agent1.Object, noOpGate.Object).ExecuteAsync(CreateContext(AgentType.Colorist));
        StepExecutionResult resultWithProvider = await CreateExecutor(agent2.Object, configuredGate.Object).ExecuteAsync(CreateContext(AgentType.Colorist));

        resultWithoutProvider.Output.Should().Be(resultWithProvider.Output);
        resultWithoutProvider.Status.Should().Be(resultWithProvider.Status);
        resultWithoutProvider.TokensUsed.Should().Be(resultWithProvider.TokensUsed);
        resultWithoutProvider.InputTokens.Should().Be(resultWithProvider.InputTokens);
        resultWithoutProvider.OutputTokens.Should().Be(resultWithProvider.OutputTokens);
        resultWithoutProvider.OutputStorageKey.Should().Be(resultWithProvider.OutputStorageKey);
        resultWithoutProvider.NextStepIndex.Should().Be(resultWithProvider.NextStepIndex);
        resultWithoutProvider.NewIterationCount.Should().Be(resultWithProvider.NewIterationCount);
        resultWithoutProvider.ErrorDetails.Should().Be(resultWithProvider.ErrorDetails);
    }

    [Fact]
    public async Task Result_is_unchanged_even_when_gate_throws()
    {
        var throwingGate = new Mock<IDecisionGate>();
        throwingGate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var noOpGate = new Mock<IDecisionGate>();
        noOpGate
            .Setup(g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var agent1 = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var agent2 = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));

        StepExecutionResult resultThrowing = await CreateExecutor(agent1.Object, throwingGate.Object).ExecuteAsync(CreateContext(AgentType.Colorist));
        StepExecutionResult resultNoOp = await CreateExecutor(agent2.Object, noOpGate.Object).ExecuteAsync(CreateContext(AgentType.Colorist));

        resultThrowing.Status.Should().Be(StepStatus.Completed);
        resultThrowing.Output.Should().Be(resultNoOp.Output);
        resultThrowing.Status.Should().Be(resultNoOp.Status);
        resultThrowing.TokensUsed.Should().Be(resultNoOp.TokensUsed);
        resultThrowing.OutputStorageKey.Should().Be(resultNoOp.OutputStorageKey);
        resultThrowing.ErrorDetails.Should().Be(resultNoOp.ErrorDetails);
    }

    // ==================================================================
    // Gate mode (phase 2) — the agent is a fallback, not the default
    // ==================================================================

    [Fact]
    public async Task Gate_mode_with_a_fully_confident_decision_completes_without_running_the_agent()
    {
        var harness = new RealGateHarness();
        harness.Client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ColoristConfidentResult());

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, harness.Gate, GateConfig("Colorist"));

        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(AgentType.Colorist, DescriptorHistory()));

        result.Status.Should().Be(StepStatus.Completed);
        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput? plan =
            JsonSerializer.Deserialize<ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput>(result.Output, WebJson());

        plan.Should().NotBeNull();
        plan!.Look.Should().Be("Filmic");
        plan.Strength.Should().Be("Strong");
        plan.ShadowTone.Should().Be("Deepened");
        plan.HighlightTone.Should().Be("Brightened");

        // The synthesized prose is the decision model's, and says so — it never poses as an agent's
        // reasoned choice (Global Constraints: machine-generated text is marked as such).
        plan.Reason.Should().Contain("Machine-generated");

        result.TokensUsed.Should().Be(18);
        result.InputTokens.Should().Be(11);
        result.OutputTokens.Should().Be(7);
        result.OutputStorageKey.Should().BeNull();
    }

    [Fact]
    public async Task Gate_mode_accepted_music_step_never_guesses_a_track_id()
    {
        var harness = new RealGateHarness();
        harness.Client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MusicConfidentResult());

        var agent = MockAgent(AgentType.MusicSupervisor, MusicJson, typeof(ReelBolt.Shared.Data.OutputSchemas.MusicPlanOutput));
        var executor = CreateExecutor(agent.Object, harness.Gate, GateConfig("MusicSupervisor"));

        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(AgentType.MusicSupervisor, DescriptorHistory()));

        result.Status.Should().Be(StepStatus.Completed);
        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);

        ReelBolt.Shared.Data.OutputSchemas.MusicPlanOutput? plan =
            JsonSerializer.Deserialize<ReelBolt.Shared.Data.OutputSchemas.MusicPlanOutput>(result.Output, WebJson());

        plan.Should().NotBeNull();
        plan!.TrackId.Should().BeNull("TrackId is a dynamic offered-id choice the question set deliberately excludes, so the gate must never invent the first candidate");
        plan.Intensity.Should().Be("Quiet");
        plan.Ducking.Should().Be("Heavy");
        plan.Fit.Should().Be("PlayOnce");
    }

    [Fact]
    public async Task Gate_mode_low_margin_question_runs_the_agent_and_the_agents_output_wins_verbatim()
    {
        var harness = new RealGateHarness();
        harness.Client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                "fake-model",
                new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal)
                {
                    // Clears AcceptAt (0.85 >= 0.85) but NOT MinMargin (0.85 - 0.62 = 0.23 < 0.25).
                    ["Look"] = Answer("Look", "Filmic", top: 0.85, second: 0.62),
                    ["Strength"] = Answer("Strength", "Strong", top: 0.95, second: 0.05),
                    ["ShadowTone"] = Answer("ShadowTone", "Deepened", top: 0.95, second: 0.05),
                    ["HighlightTone"] = Answer("HighlightTone", "Brightened", top: 0.95, second: 0.05),
                },
                InputTokens: 11,
                OutputTokens: 7));

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, harness.Gate, GateConfig("Colorist"));
        var context = CreateContext(AgentType.Colorist, DescriptorHistory());

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.Output.Should().Be(ColoristJson, "one failing question escalates the whole step and the agent's output wins verbatim");

        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Gate_mode_escalation_passes_the_distribution_as_an_evidence_prior()
    {
        string? agentInput = null;
        var harness = new RealGateHarness();
        harness.Client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ColoristConfidentResult());

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        agent
            .Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid?, CancellationToken>((input, _, _) => agentInput = input)
            .ReturnsAsync(new AgentRunResult { Output = ColoristJson, Success = true, TokensUsed = 10, InputTokens = 6, OutputTokens = 4 });

        // AcceptAt raised above the model's 0.95 top probability, so every question escalates while
        // the model still produced a full distribution worth showing the agent.
        IConfiguration config = GateConfig("Colorist", new Dictionary<string, string?>
        {
            ["Agents:Colorist:DecisionGate:AcceptAt"] = "0.99"
        });

        var executor = CreateExecutor(agent.Object, harness.Gate, config);
        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(AgentType.Colorist, DescriptorHistory()));

        result.Output.Should().Be(ColoristJson);
        agentInput.Should().NotBeNull();
        agentInput!.Should().Contain("System One prior");
        agentInput.Should().Contain("Look").And.Contain("Filmic").And.Contain("0.95");
        agentInput.Should().Contain("EVIDENCE");
        agentInput.Should().Contain("not an instruction");
    }

    [Fact]
    public async Task Gate_mode_escalation_attaches_the_agents_real_answers_and_token_count()
    {
        var gate = new Mock<IDecisionGate>();
        gate
            .Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionGateOutcome(
                HasDecision: true,
                Accepted: false,
                Result: ColoristConfidentResult(),
                TopTwoProbabilities: new Dictionary<string, (double Top, double Second)>
                {
                    ["Look"] = (0.95, 0.05),
                    ["Strength"] = (0.95, 0.05),
                    ["ShadowTone"] = (0.95, 0.05),
                    ["HighlightTone"] = (0.95, 0.05),
                }));
        gate
            .Setup(g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, gate.Object, GateConfig("Colorist"));
        var context = CreateContext(AgentType.Colorist, DescriptorHistory());

        await executor.ExecuteAsync(context);

        gate.Verify(
            g => g.AttachSystemTwoAnswersAsync(
                context.Execution.Id,
                context.Step.StepOrder,
                It.Is<IReadOnlyDictionary<string, string>>(answers =>
                    answers["Look"] == "Warm"
                    && answers["Strength"] == "Normal"
                    && answers["ShadowTone"] == "Lifted"
                    && answers["HighlightTone"] == "Neutral"),
                10,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Gate_mode_never_runs_the_shadow_hook_on_the_escalated_path()
    {
        var gate = new Mock<IDecisionGate>();
        gate
            .Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecisionGateOutcome.NoDecision);

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, gate.Object, GateConfig("Colorist"));

        await executor.ExecuteAsync(CreateContext(AgentType.Colorist, DescriptorHistory()));

        // Gate mode owns this step's observation rows; a Shadow call would write a second row per
        // question with Mode="Gate" (RunShadowAsync passes the config's own mode through).
        gate.Verify(
            g => g.RunShadowAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<AgentType>(), It.IsAny<DecisionState>(),
                It.IsAny<IReadOnlyList<DecisionGateQuestion>>(), It.IsAny<DecisionGateConfig>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Gate_mode_decision_client_timeout_escalates_and_runs_the_agent_as_today()
    {
        // NoDecision is exactly what the gate returns when its own deadline fires (the timeout path
        // itself — a client that never completes — is covered end to end by
        // DecisionGateTests.DecideAsync_WhenClientNeverCompletes_ReturnsNoDecisionWithinTheTimeoutBudget).
        var gate = new Mock<IDecisionGate>();
        gate
            .Setup(g => g.DecideAsync(It.IsAny<DecisionGateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DecisionGateOutcome.NoDecision);

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, gate.Object, GateConfig("Colorist"));
        var context = CreateContext(AgentType.Colorist, DescriptorHistory());

        StepExecutionResult result = await executor.ExecuteAsync(context);

        result.Status.Should().Be(StepStatus.Completed);
        result.Output.Should().Be(ColoristJson);
        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);

        // Nothing was observed, so there is nothing to complete afterwards.
        gate.Verify(
            g => g.AttachSystemTwoAnswersAsync(
                It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, string>>(),
                It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Gate_mode_with_no_provider_configured_is_byte_identical_to_mode_off()
    {
        // A real gate with nothing configured: the provider does not resolve, so DecideAsync returns
        // NoDecision and the step escalates. The descriptor history is present, so this really does
        // exercise the pre-agent Gate call rather than short-circuiting on an empty descriptor.
        var harness = new RealGateHarness(providerConfigured: false);

        var agentGate = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var agentOff = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));

        var gateExecutor = CreateExecutor(agentGate.Object, harness.Gate, GateConfig("Colorist"));
        var offExecutor = CreateExecutor(agentOff.Object, harness.Gate, EmptyConfig());

        StepExecutionResult gateResult = await gateExecutor.ExecuteAsync(CreateContext(AgentType.Colorist, DescriptorHistory()));
        StepExecutionResult offResult = await offExecutor.ExecuteAsync(CreateContext(AgentType.Colorist, DescriptorHistory()));

        Describe(gateResult).Should().Be(Describe(offResult));
        agentGate.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
        offResult.Output.Should().Be(ColoristJson);
    }

    [Fact]
    public async Task Gate_mode_with_an_absent_descriptor_escalates_without_calling_the_decision_provider()
    {
        var harness = new RealGateHarness();

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, harness.Gate, GateConfig("Colorist"));

        // No upstream analyze step at all: there is no bounded state to send, and sending an empty
        // one would ask the model to decide from nothing.
        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(AgentType.Colorist));

        result.Status.Should().Be(StepStatus.Completed);
        result.Output.Should().Be(ColoristJson);
        harness.Client.Invocations.Should().BeEmpty();
        harness.Db.DecisionObservations.ToList().Should().BeEmpty();
        agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Gate_mode_makes_exactly_one_decision_call_when_no_site_key_is_set()
    {
        var harness = new RealGateHarness();
        SetupEchoClient(harness.Client);

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, harness.Gate, GateConfig("Colorist"));

        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(AgentType.Colorist, DescriptorHistory()));

        result.Status.Should().Be(StepStatus.Completed);
        harness.Client.Invocations.Should().HaveCount(1, "every question resolves to the same config, so they are one group");
        ((IReadOnlyList<DecisionQuestion>)harness.Client.Invocations[0].Arguments[1])
            .Select(q => q.Name)
            .Should().BeEquivalentTo(["Look", "Strength", "ShadowTone", "HighlightTone"]);
    }

    [Fact]
    public async Task Gate_mode_splits_into_one_decision_call_per_resolved_site_config()
    {
        var harness = new RealGateHarness();
        SetupEchoClient(harness.Client);

        IConfiguration config = GateConfig("Colorist", new Dictionary<string, string?>
        {
            ["Agents:Colorist:DecisionGate:Sites:Look:AcceptAt"] = "0.99"
        });

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, harness.Gate, config);

        await executor.ExecuteAsync(CreateContext(AgentType.Colorist, DescriptorHistory()));

        harness.Client.Invocations.Should().HaveCount(2, "the site key gives Look its own config, so it is its own group");

        var askedPerCall = harness.Client.Invocations
            .Select(invocation => ((IReadOnlyList<DecisionQuestion>)invocation.Arguments[1]).Select(q => q.Name).ToList())
            .ToList();

        askedPerCall.Should().ContainSingle(names => names.Count == 1 && names[0] == "Look");
        askedPerCall.Should().ContainSingle(names =>
            names.Count == 3 && names.Contains("Strength") && names.Contains("ShadowTone") && names.Contains("HighlightTone"));
    }

    [Fact]
    public async Task Gate_mode_is_all_or_nothing_across_groups()
    {
        var harness = new RealGateHarness();
        SetupEchoClient(harness.Client);

        // Look's own group would clear its (tighter) threshold on the fake client's 0.95, and so
        // would the others — but a site config that makes its group FAIL must escalate the whole
        // step, exactly as a failing sibling question does within one group.
        IConfiguration config = GateConfig("Colorist", new Dictionary<string, string?>
        {
            ["Agents:Colorist:DecisionGate:Sites:Look:MinMargin"] = "0.99"
        });

        var agent = MockAgent(AgentType.Colorist, ColoristJson, typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        var executor = CreateExecutor(agent.Object, harness.Gate, config);

        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(AgentType.Colorist, DescriptorHistory()));

        result.Output.Should().Be(ColoristJson, "one group failing its own thresholds escalates the whole step");
        agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
