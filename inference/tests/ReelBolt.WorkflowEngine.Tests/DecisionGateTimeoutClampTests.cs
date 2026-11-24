using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Decision;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The security finding this file pins (decision-engine-p2, finding 3): <c>TimeoutSeconds</c> used
/// to reach the gate with no clamp at all, from the <c>Conditional</c> step config, the
/// <c>ReviewLoop</c> step config and the room convening gate alike, producing two failure shapes.
///
/// <para>
/// <b>Zero or negative</b> made the gate's <c>TimeoutSeconds &gt; 0 ? linkedTokenSource : null</c>
/// branch produce a null cancellation source, so no deadline existed and the provider's own
/// up-to-300 s <c>HttpClient</c> timeout was the only bound — the phase-1 defect D-004 fixed,
/// reintroduced silently for that step. <b>Very large</b> made
/// <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> throw on millisecond overflow
/// inside the gate's own <c>try</c>, where the generic soft-failure catch swallowed it: the gate
/// then returned "no decision" forever, with one warning per call that named no cause.
/// </para>
///
/// <para>
/// The arithmetic tests below are cheap, but the two behavioural ones are the point: they exercise
/// a real <see cref="DecisionGate"/> against a decision client that never completes, which is the
/// only way to prove a deadline is actually in force and that a huge configured value no longer
/// makes the gate permanently inert.
/// </para>
/// </summary>
public sealed class DecisionGateTimeoutClampTests
{
    // ---------------------------------------------------------------------------------------
    // DecisionGateLimits — the single definition
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void DecisionGateLimits_PublishesTheDocumentedBounds()
    {
        DecisionGateLimits.MinSeconds.Should().Be(1);
        DecisionGateLimits.DefaultSeconds.Should().Be(5);
        DecisionGateLimits.MaxSeconds.Should().Be(300);
    }

    [Fact]
    public void DecisionGateLimits_DefaultSeconds_MatchesDecisionGateConfigsOwnDefault()
    {
        // An omitted timeout and a configured 0 must behave identically, which is only true while
        // these two numbers agree. Read from the record rather than retyped.
        new DecisionGateConfig().TimeoutSeconds.Should().Be(DecisionGateLimits.DefaultSeconds);
    }

    [Theory]
    [InlineData(0, DecisionGateLimits.DefaultSeconds, "zero means 'I did not set this', not the floor")]
    [InlineData(-1, DecisionGateLimits.DefaultSeconds, "a negative is as unset as a zero")]
    [InlineData(-300, DecisionGateLimits.DefaultSeconds, "sign is irrelevant below the floor")]
    [InlineData(int.MinValue, DecisionGateLimits.DefaultSeconds, "the most negative int still means unset")]
    [InlineData(DecisionGateLimits.MinSeconds, DecisionGateLimits.MinSeconds, "the floor is in range")]
    [InlineData(DecisionGateLimits.DefaultSeconds, DecisionGateLimits.DefaultSeconds, "the default is in range")]
    [InlineData(DecisionGateLimits.MaxSeconds, DecisionGateLimits.MaxSeconds, "the ceiling is in range")]
    [InlineData(DecisionGateLimits.MaxSeconds + 1, DecisionGateLimits.MaxSeconds, "one past the ceiling clamps")]
    [InlineData(3600, DecisionGateLimits.MaxSeconds, "an hour clamps")]
    [InlineData(int.MaxValue, DecisionGateLimits.MaxSeconds, "int.MaxValue clamps — this is what removed the CancelAfter overflow")]
    public void Clamp_ResolvesEveryDocumentedInput(int configured, int expected, string because)
    {
        DecisionGateLimits.Clamp(configured).Should().Be(expected, because);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(299)]
    [InlineData(300)]
    public void Clamp_PassesAnInRangeTimeoutThroughUnchanged(int configured)
    {
        // No clamp means no behaviour change for any configuration that was already sane.
        DecisionGateLimits.Clamp(configured).Should().Be(configured);
    }

    // ---------------------------------------------------------------------------------------
    // The two step configs — same definition, same answers
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, DecisionGateLimits.DefaultSeconds)]
    [InlineData(-1, DecisionGateLimits.DefaultSeconds)]
    [InlineData(int.MinValue, DecisionGateLimits.DefaultSeconds)]
    [InlineData(1, 1)]
    [InlineData(5, 5)]
    [InlineData(300, 300)]
    [InlineData(301, DecisionGateLimits.MaxSeconds)]
    [InlineData(int.MaxValue, DecisionGateLimits.MaxSeconds)]
    public void BothStepConfigs_ClampedTimeoutSecondsAgreeOnTheSameInputs(int configured, int expected)
    {
        new ConditionalStepConfig(TimeoutSeconds: configured).ClampedTimeoutSeconds.Should().Be(expected);
        new ReviewLoopStepConfig(TimeoutSeconds: configured).ClampedTimeoutSeconds.Should().Be(expected);
    }

    [Fact]
    public void BothStepConfigs_DefaultToTheSharedDefaultClamped()
    {
        new ConditionalStepConfig().ClampedTimeoutSeconds.Should().Be(DecisionGateLimits.DefaultSeconds);
        new ReviewLoopStepConfig().ClampedTimeoutSeconds.Should().Be(DecisionGateLimits.DefaultSeconds);
    }

    [Fact]
    public void BothStepConfigs_ClampedTimeoutSecondsIsAlwaysWithinTheDocumentedBounds()
    {
        foreach (int configured in new[] { int.MinValue, -1, 0, 1, 5, 300, 301, int.MaxValue })
        {
            new ConditionalStepConfig(TimeoutSeconds: configured).ClampedTimeoutSeconds
                .Should().BeInRange(DecisionGateLimits.MinSeconds, DecisionGateLimits.MaxSeconds);
            new ReviewLoopStepConfig(TimeoutSeconds: configured).ClampedTimeoutSeconds
                .Should().BeInRange(DecisionGateLimits.MinSeconds, DecisionGateLimits.MaxSeconds);
        }
    }

    // ---------------------------------------------------------------------------------------
    // The raw value stays raw
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(int.MaxValue)]
    public void ConditionalStepConfig_KeepsTheRawConfiguredValueInTheRecord(int configured)
    {
        // The clamp is never applied in the constructor: the value the client sent is what the
        // record holds, so nothing downstream that reads the persisted config sees a rewritten one.
        new ConditionalStepConfig(TimeoutSeconds: configured).TimeoutSeconds.Should().Be(configured);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(int.MaxValue)]
    public void ReviewLoopStepConfig_KeepsTheRawConfiguredValueInTheRecord(int configured)
    {
        new ReviewLoopStepConfig(TimeoutSeconds: configured).TimeoutSeconds.Should().Be(configured);
    }

    [Fact]
    public void ConditionalStepConfig_RawValueSurvivesAJsonRoundTripUnclamped()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        string json = JsonSerializer.Serialize(new ConditionalStepConfig(TimeoutSeconds: 0), options);
        ConditionalStepConfig? roundTripped = JsonSerializer.Deserialize<ConditionalStepConfig>(json, options);

        roundTripped.Should().NotBeNull();
        roundTripped!.TimeoutSeconds.Should().Be(0, "the client's 0 must round-trip as 0, not as the clamped 5");
        roundTripped.ClampedTimeoutSeconds.Should().Be(DecisionGateLimits.DefaultSeconds);
    }

    [Fact]
    public void ReviewLoopStepConfig_RawValueSurvivesAJsonRoundTripUnclamped()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        string json = JsonSerializer.Serialize(new ReviewLoopStepConfig(TimeoutSeconds: 0), options);
        ReviewLoopStepConfig? roundTripped = JsonSerializer.Deserialize<ReviewLoopStepConfig>(json, options);

        roundTripped.Should().NotBeNull();
        roundTripped!.TimeoutSeconds.Should().Be(0, "the client's 0 must round-trip as 0, not as the clamped 5");
        roundTripped.ClampedTimeoutSeconds.Should().Be(DecisionGateLimits.DefaultSeconds);
    }

    [Fact]
    public void BothStepConfigs_DoNotSerializeTheDerivedValue()
    {
        // ClampedTimeoutSeconds is a derived, read-only view of a field the client sends. It must
        // not appear in the JSON (nor in the schema StepConfigSchemaExporter builds from the same
        // record for the assistant/MCP ListStepSchemas tool), or every client would be told to
        // send a field it cannot meaningfully set.
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        JsonSerializer.Serialize(new ConditionalStepConfig(TimeoutSeconds: 42), options)
            .Should().NotContain("clampedTimeoutSeconds");
        JsonSerializer.Serialize(new ReviewLoopStepConfig(TimeoutSeconds: 42), options)
            .Should().NotContain("clampedTimeoutSeconds");
    }

    // ---------------------------------------------------------------------------------------
    // The gate itself — the real control
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Gate_WithATimeoutOfZero_StillReturnsNoDecisionWithinABoundedBudget()
    {
        // The regression this pins: with the pre-fix `TimeoutSeconds > 0 ? … : null` branch a zero
        // created NO cancellation source at all, so this call would never return — the never-
        // completing client has no other bound. The elapsed-time assertion is the whole test.
        var harness = new GateHarness();
        WorkflowEngineDbContext ctx = harness.SetupScope();
        ResolvedDecisionProvider provider = GateHarness.CreateProvider();
        harness.SetupProvider(provider);
        harness.SetupClient(provider, GateHarness.NeverCompletingClient());

        DecisionGateRequest request = GateHarness.CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate, TimeoutSeconds: 0));

        var sw = Stopwatch.StartNew();
        DecisionGateOutcome outcome = await harness.Sut.DecideAsync(request, CancellationToken.None);
        sw.Stop();

        // Clamped to DefaultSeconds = 5, so a budget that comfortably contains it and excludes
        // "never returns" is all this needs to prove the deadline is in force.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        outcome.HasDecision.Should().BeFalse();
        outcome.Result.Should().BeNull();
        ctx.DecisionObservations.Should().BeEmpty();
    }

    [Fact]
    public async Task Gate_WithANegativeTimeout_StillReturnsNoDecisionWithinABoundedBudget()
    {
        // Same shape as a zero, and the same regression: a negative is "unset", not "no deadline".
        var harness = new GateHarness();
        harness.SetupScope();
        ResolvedDecisionProvider provider = GateHarness.CreateProvider();
        harness.SetupProvider(provider);
        harness.SetupClient(provider, GateHarness.NeverCompletingClient());

        DecisionGateRequest request = GateHarness.CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate, TimeoutSeconds: -30));

        var sw = Stopwatch.StartNew();
        DecisionGateOutcome outcome = await harness.Sut.DecideAsync(request, CancellationToken.None);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        outcome.HasDecision.Should().BeFalse();
    }

    [Fact]
    public async Task Gate_WithIntMaxValueTimeout_DoesNotThrowOutOfCancelAfterAndStillDecides()
    {
        // The second regression: CancelAfter threw ArgumentOutOfRangeException on millisecond
        // overflow, inside the gate's own try, where the generic catch turned it into a permanent
        // "no decision" for that step. Clamped to MaxSeconds, the call must behave normally — a
        // real outcome, not an inert gate.
        var harness = new GateHarness();
        WorkflowEngineDbContext ctx = harness.SetupScope();
        ResolvedDecisionProvider provider = GateHarness.CreateProvider();
        harness.SetupProvider(provider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(
                It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateHarness.ConfidentResult());
        harness.SetupClient(provider, mockClient);

        DecisionGateRequest request = GateHarness.CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate, TimeoutSeconds: int.MaxValue));

        Exception? escaped = await Record.ExceptionAsync(
            () => harness.Sut.DecideAsync(request, CancellationToken.None));

        escaped.Should().BeNull("a huge configured timeout must clamp, not throw out of CancelAfter");

        DecisionGateOutcome outcome = await harness.Sut.DecideAsync(request, CancellationToken.None);
        outcome.HasDecision.Should().BeTrue("the gate must not be permanently inert");
        outcome.Accepted.Should().BeTrue();
        ctx.DecisionObservations.Should().HaveCount(2, "one observation per call, so the second call proves the gate still runs");
    }

    [Fact]
    public async Task Gate_WithAVeryLargeButFiniteTimeout_StillDecides()
    {
        // 3600 is below int.MaxValue and therefore never overflowed CancelAfter, but it is far
        // above the provider's own ceiling: it clamps to MaxSeconds, which is the whole point.
        var harness = new GateHarness();
        harness.SetupScope();
        ResolvedDecisionProvider provider = GateHarness.CreateProvider();
        harness.SetupProvider(provider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(
                It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateHarness.ConfidentResult());
        harness.SetupClient(provider, mockClient);

        DecisionGateRequest request = GateHarness.CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate, TimeoutSeconds: 3600));

        DecisionGateOutcome outcome = await harness.Sut.DecideAsync(request, CancellationToken.None);

        outcome.HasDecision.Should().BeTrue();
    }

    [Fact]
    public async Task Gate_WithAnInRangeTimeout_IsUnchangedBehaviour()
    {
        // A configured value inside the bounds is passed through untouched, so this is byte-for-byte
        // the behaviour an already-sane site had before the clamp existed.
        var harness = new GateHarness();
        WorkflowEngineDbContext ctx = harness.SetupScope();
        ResolvedDecisionProvider provider = GateHarness.CreateProvider();
        harness.SetupProvider(provider);

        var mockClient = new Mock<IDecisionClient>();
        mockClient.Setup(c => c.DecideAsync(
                It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(GateHarness.ConfidentResult());
        harness.SetupClient(provider, mockClient);

        DecisionGateRequest request = GateHarness.CreateRequest(
            new DecisionGateConfig(Mode: DecisionGateMode.Gate, TimeoutSeconds: 30));

        DecisionGateOutcome outcome = await harness.Sut.DecideAsync(request, CancellationToken.None);

        outcome.HasDecision.Should().BeTrue();
        outcome.Accepted.Should().BeTrue();
        ctx.DecisionObservations.Should().ContainSingle();
    }

    [Fact]
    public async Task Gate_TheClampedValueIsWhatTheStepConfigsHandTheGate()
    {
        // End-to-end over the seam the two executors own: a step config configured 0 produces a
        // gate config whose effective budget is the shared default, not "no deadline". The
        // executors pass ClampedTimeoutSeconds, so this is the value the gate will act on.
        int fromConditional = new ConditionalStepConfig(TimeoutSeconds: 0).ClampedTimeoutSeconds;
        int fromReviewLoop = new ReviewLoopStepConfig(TimeoutSeconds: 0).ClampedTimeoutSeconds;

        var harness = new GateHarness();
        harness.SetupScope();
        ResolvedDecisionProvider provider = GateHarness.CreateProvider();
        harness.SetupProvider(provider);
        harness.SetupClient(provider, GateHarness.NeverCompletingClient());

        var gateConfig = new DecisionGateConfig(
            Mode: DecisionGateMode.Gate, ProviderId: null, TimeoutSeconds: fromConditional);

        var sw = Stopwatch.StartNew();
        DecisionGateOutcome outcome = await harness.Sut.DecideAsync(
            GateHarness.CreateRequest(gateConfig), CancellationToken.None);
        sw.Stop();

        fromConditional.Should().Be(fromReviewLoop);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
        outcome.HasDecision.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------
    // Harness — the fake decision client shape used by DecisionGateTests, kept local because that
    // file's helpers are private to it.
    // ---------------------------------------------------------------------------------------

    private sealed class GateHarness
    {
        private readonly Mock<IInferenceProviderResolver> _resolver = new();
        private readonly Mock<IDecisionClientFactory> _factory = new();
        private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
        private readonly Mock<ILogger<DecisionGate>> _logger = new();
        private readonly DbContextOptions<WorkflowEngineDbContext> _dbContextOptions =
            new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseInMemoryDatabase($"DecisionGateTimeoutClampTests_{Guid.NewGuid()}")
                .Options;

        public DecisionGate Sut => new(
            _resolver.Object, _factory.Object, _scopeFactory.Object, _logger.Object);

        public WorkflowEngineDbContext SetupScope()
        {
            var ctx = new WorkflowEngineDbContext(_dbContextOptions);
            var scopeServiceProvider = new Mock<IServiceProvider>();
            scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(ctx);

            var scope = new Mock<IServiceScope>();
            scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);

            _scopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);
            return ctx;
        }

        public void SetupProvider(ResolvedDecisionProvider provider)
        {
            _resolver.Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(provider);
        }

        public void SetupClient(ResolvedDecisionProvider provider, Mock<IDecisionClient> client)
        {
            _factory.Setup(f => f.Get(provider)).Returns(client.Object);
        }

        public static ResolvedDecisionProvider CreateProvider() => new(
            ProviderId: Guid.NewGuid(),
            Name: "mock-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://test",
            ModelName: "test-model",
            ApiKey: "key",
            TimeoutSeconds: 300);

        /// <summary>
        /// A client that never completes on its own: it only ever returns by observing the token it
        /// was handed, which is exactly the gate's own deadline. Without that deadline in force this
        /// call cannot end, which is what makes the bounded-budget assertions meaningful.
        /// </summary>
        public static Mock<IDecisionClient> NeverCompletingClient()
        {
            var client = new Mock<IDecisionClient>();
            client.Setup(c => c.DecideAsync(
                    It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
                .Returns(async (DecisionState _, IReadOnlyList<DecisionQuestion> _, CancellationToken token) =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return ConfidentResult();
                });
            return client;
        }

        public static DecisionGateRequest CreateRequest(DecisionGateConfig config) => new(
            ExecutionId: Guid.NewGuid(),
            StepOrder: 3,
            AgentType: AgentType.Colorist,
            Site: "Conditional.Decision",
            State: new DecisionState("content"),
            Questions:
            [
                new DecisionGateQuestion("Conditional.Decision", "q1", DecisionKind.Noul, "inst1", ["yes", "no"], "yes")
            ],
            Config: config,
            SystemTwoAnswer: null,
            AgentTokensUsed: null);

        public static DecisionResult ConfidentResult() => new(
            Model: "gpt-4",
            Answers: new Dictionary<string, DecisionAnswer>
            {
                {
                    "q1",
                    new DecisionAnswer(
                        Name: "q1",
                        Kind: DecisionKind.Noul,
                        Choice: null,
                        Score: null,
                        Noul: 0.95,
                        Confidence: 0.95,
                        Probabilities: new Dictionary<string, double> { ["yes"] = 0.95, ["no"] = 0.05 })
                }
            },
            InputTokens: 100,
            OutputTokens: 50);
    }
}
