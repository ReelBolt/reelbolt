using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
using ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution.Decision;

/// <summary>
/// Coverage for the cost-routing gate: the "off by default / nothing configured / anything went
/// wrong" paths (which must be pure no-ops, asserted by verifying no provider call was ever made),
/// the escalation arithmetic, and the two-phase observation write (row before the run, outcome
/// after it).
/// </summary>
public class CostRoutingGateTests
{
    private readonly Mock<IInferenceProviderResolver> _resolver = new();
    private readonly Mock<IDecisionClientFactory> _clientFactory = new();
    private readonly Mock<IServiceScopeFactory> _scopeFactory = new();
    private readonly CostRoutingAmbientState _ambient = new();
    private readonly WorkflowEngineDbContext _db;

    private readonly Guid _expensiveProviderId = Guid.NewGuid();
    private readonly Guid _cheapProviderId = Guid.NewGuid();
    private readonly Guid _decisionProviderId = Guid.NewGuid();

    public CostRoutingGateTests()
    {
        var options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase($"CostRoutingGateTests_{Guid.NewGuid()}")
            .Options;

        _db = new WorkflowEngineDbContext(options);

        var scopeServiceProvider = new Mock<IServiceProvider>();
        scopeServiceProvider.Setup(sp => sp.GetService(typeof(WorkflowEngineDbContext))).Returns(_db);

        var scope = new Mock<IServiceScope>();
        scope.Setup(s => s.ServiceProvider).Returns(scopeServiceProvider.Object);
        _scopeFactory.Setup(sf => sf.CreateScope()).Returns(scope.Object);
    }

    private static IConfiguration Config(IDictionary<string, string> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(kv => KeyValuePair.Create(kv.Key, (string)kv.Value)))
            .Build();

    private IConfiguration FullyConfiguredConfig() => Config(new Dictionary<string, string>
    {
        ["Agents:Author:CostRouting:Enabled"] = "true",
        ["Agents:Author:CostRouting:CheapProviderId"] = _cheapProviderId.ToString(),
        ["Agents:Author:CostRouting:ExpensiveProviderId"] = _expensiveProviderId.ToString(),
        ["Agents:Author:CostRouting:DecisionProviderId"] = _decisionProviderId.ToString(),
    });

    private CostRoutingGate CreateGate(IConfiguration configuration) => new(
        _resolver.Object,
        _clientFactory.Object,
        _scopeFactory.Object,
        _ambient,
        configuration,
        NullLogger<CostRoutingGate>.Instance);

    private static IReelBoltAgent Agent(string name = "Author")
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(name);
        agent.Setup(a => a.AgentType).Returns(AgentType.AuthorAgent);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        agent.Setup(a => a.OutputSchemaType).Returns(typeof(ReelBolt.Shared.Data.OutputSchemas.ColorGradePlanOutput));
        return agent.Object;
    }

    private static StepExecutionContext Context(int stepOrder = 4) => new()
    {
        Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
        Step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = stepOrder,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = AgentType.AuthorAgent, Name = "Author" },
        },
        AllSteps = [],
        AccumulatedOutput = "upstream",
        StepOutputHistory = [],
        CurrentStepIndex = 0,
        IterationCount = 0,
        CorrelationId = "test",
        CancellationToken = CancellationToken.None,
    };

    private ResolvedDecisionProvider SetupDecisionProvider(double? noul, string model = "decision-model")
    {
        var resolved = new ResolvedDecisionProvider(
            ProviderId: _decisionProviderId,
            Name: "decision-provider",
            Kind: InferenceProviderKind.OpenAICompatible,
            Endpoint: "http://decision.example",
            ModelName: model,
            ApiKey: "key",
            TimeoutSeconds: 300);

        _resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);

        var client = new Mock<IDecisionClient>();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult(
                Model: model,
                Answers: new Dictionary<string, DecisionAnswer>
                {
                    [CostRoutingGate.QuestionName] = new(
                        CostRoutingGate.QuestionName, DecisionKind.Noul, null, null, noul, null, new Dictionary<string, double>())
                },
                InputTokens: 11,
                OutputTokens: 3));

        _clientFactory.Setup(f => f.Get(resolved)).Returns(client.Object);
        return resolved;
    }

    // --- Off / unconfigured: the default state, and the one that must be provably inert ---

    [Fact]
    public async Task Not_enabled_is_a_no_op_and_makes_no_provider_call()
    {
        CostRoutingDecision decision = await CreateGate(Config(new Dictionary<string, string>()))
            .BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().BeNull();
        _ambient.Current.Should().BeNull();
        _resolver.Verify(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
        _db.DecisionObservations.Count().Should().Be(0);
    }

    [Fact]
    public async Task Enabled_without_an_expensive_provider_is_a_no_op()
    {
        IConfiguration configuration = Config(new Dictionary<string, string>
        {
            ["Agents:Author:CostRouting:Enabled"] = "true",
        });

        CostRoutingDecision decision = await CreateGate(configuration).BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().BeNull();
        _resolver.Verify(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enabled_config_for_a_different_agent_does_not_leak_into_this_one()
    {
        IConfiguration configuration = Config(new Dictionary<string, string>
        {
            ["Agents:Colorist:CostRouting:Enabled"] = "true",
            ["Agents:Colorist:CostRouting:ExpensiveProviderId"] = _expensiveProviderId.ToString(),
        });

        CostRoutingDecision decision = await CreateGate(configuration).BeginStepAsync(Context(), Agent("Author"), "prompt");

        decision.Should().BeNull();
        _resolver.Verify(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task No_Decision_provider_configured_is_a_no_op_and_never_reaches_the_client_factory()
    {
        _resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ResolvedDecisionProvider)null);

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().BeNull();
        _clientFactory.Verify(f => f.Get(It.IsAny<ResolvedDecisionProvider>()), Times.Never);
        _db.DecisionObservations.Count().Should().Be(0);
    }

    // --- The escalation arithmetic (Noul is P(the CHEAP tier will pass), so a low value escalates) ---

    [Fact]
    public async Task A_confident_Noul_keeps_the_cheap_tier_and_records_the_decision()
    {
        SetupDecisionProvider(noul: 0.97);
        StepExecutionContext context = Context();

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(context, Agent(), "prompt");

        decision.Should().NotBeNull();
        decision.RunState.Escalated.Should().BeFalse();

        DecisionObservation row = _db.DecisionObservations.Single();
        row.Escalated.Should().BeFalse();
        row.Kind.Should().Be(DecisionKind.Noul);
        row.Site.Should().Be("CostRouting.Author");
        row.Question.Should().Be(CostRoutingGate.QuestionName);
        row.Mode.Should().Be("CostRouting");
        row.Answer.Should().Be(0.97.ToString());
        row.ProviderId.Should().Be(_decisionProviderId);
        row.Model.Should().Be("decision-model");
        row.ExecutionId.Should().Be(context.Execution.Id);
        row.StepOrder.Should().Be(context.Step.StepOrder);
        row.AgentType.Should().Be(AgentType.AuthorAgent);
        row.OptionsHash.Should().NotBeNullOrEmpty();
        row.ProbabilitiesJson.Should().Contain("0.97");
    }

    [Fact]
    public async Task A_low_Noul_escalates_to_the_expensive_tier()
    {
        SetupDecisionProvider(noul: 0.10);

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().NotBeNull();
        decision.RunState.Escalated.Should().BeTrue();
        decision.RunState.ExpensiveProviderId.Should().Be(_expensiveProviderId);
        decision.RunState.CheapProviderId.Should().Be(_cheapProviderId);
        _db.DecisionObservations.Single().Escalated.Should().BeTrue();
    }

    [Fact]
    public async Task An_ambiguous_Noul_escalates_even_when_it_clears_AcceptAt()
    {
        // 0.62 clears AcceptAt=0.60, but its margin (|2*0.62-1| = 0.24) is under MinMargin=0.30 —
        // a coin flip, so it escalates.
        SetupDecisionProvider(noul: 0.62);

        IConfiguration configuration = Config(new Dictionary<string, string>
        {
            ["Agents:Author:CostRouting:Enabled"] = "true",
            ["Agents:Author:CostRouting:ExpensiveProviderId"] = _expensiveProviderId.ToString(),
            ["Agents:Author:CostRouting:AcceptAt"] = "0.60",
            ["Agents:Author:CostRouting:MinMargin"] = "0.30",
        });

        CostRoutingDecision decision = await CreateGate(configuration).BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().NotBeNull();
        decision.RunState.Escalated.Should().BeTrue();
    }

    [Fact]
    public async Task A_confident_Noul_with_a_wide_margin_does_not_escalate()
    {
        SetupDecisionProvider(noul: 0.90);

        IConfiguration configuration = Config(new Dictionary<string, string>
        {
            ["Agents:Author:CostRouting:Enabled"] = "true",
            ["Agents:Author:CostRouting:ExpensiveProviderId"] = _expensiveProviderId.ToString(),
            ["Agents:Author:CostRouting:AcceptAt"] = "0.85",
            ["Agents:Author:CostRouting:MinMargin"] = "0.25",
        });

        CostRoutingDecision decision = await CreateGate(configuration).BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().NotBeNull();
        decision.RunState.Escalated.Should().BeFalse();
    }

    // --- Two-phase write: row BEFORE the run, outcome AFTER it ---

    [Fact]
    public async Task The_observation_is_persisted_before_the_run_and_its_outcome_only_after()
    {
        SetupDecisionProvider(noul: 0.97);

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");
        CostRoutingScope scope = CostRoutingScope.Enter(decision);
        scope.IsActive.Should().BeTrue();
        _ambient.Current.Should().BeSameAs(decision.RunState, "Enter arms the intent synchronously in the caller's frame");

        DecisionObservation row = _db.DecisionObservations.Single();
        row.DownstreamOutcomeJson.Should().BeNull("the outcome cannot exist before the run has happened");

        // ... the run happens here (observed through the recorder the chat client feeds) ...
        CostRoutingRunState state = _ambient.Current;
        state.RecordRoutedProvider(_cheapProviderId, "cheap-model");
        state.RecordChatCallSucceeded(responseHadJsonObject: true);

        await scope.DisposeAsync();

        JsonElement outcome = JsonDocument.Parse(_db.DecisionObservations.Single().DownstreamOutcomeJson).RootElement;
        outcome.GetProperty("routedTo").GetString().Should().Be("cheap");
        outcome.GetProperty("routedProviderId").GetString().Should().Be(_cheapProviderId.ToString());
        outcome.GetProperty("routedModel").GetString().Should().Be("cheap-model");
        outcome.GetProperty("chatCalls").GetInt32().Should().Be(1);
        outcome.GetProperty("lastCallSucceeded").GetBoolean().Should().BeTrue();
        outcome.GetProperty("lastResponseHadJsonObject").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_run_is_recorded_as_the_real_outcome()
    {
        SetupDecisionProvider(noul: 0.10);

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");
        CostRoutingScope scope = CostRoutingScope.Enter(decision);

        CostRoutingRunState state = _ambient.Current;
        state.RecordRoutedProvider(_expensiveProviderId, "expensive-model");
        state.RecordChatCallFailed("HttpRequestException: 503");

        await scope.DisposeAsync();

        JsonElement outcome = JsonDocument.Parse(_db.DecisionObservations.Single().DownstreamOutcomeJson).RootElement;
        outcome.GetProperty("routedTo").GetString().Should().Be("expensive");
        outcome.GetProperty("lastCallSucceeded").GetBoolean().Should().BeFalse();
        outcome.GetProperty("error").GetString().Should().Contain("503");
    }

    [Fact]
    public async Task Enter_arms_the_intent_and_disposal_clears_it_in_the_callers_frame()
    {
        SetupDecisionProvider(noul: 0.97);

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");

        CostRoutingScope scope = CostRoutingScope.Enter(decision);
        _ambient.Current.Should().BeSameAs(decision.RunState);

        await scope.DisposeAsync();

        // Observable here because CostRoutingScope.DisposeAsync is NOT async: an AsyncLocal write
        // made inside an async method is discarded before its caller resumes, which would leave the
        // intent armed for whatever the caller did next.
        _ambient.Current.Should().BeNull();
    }

    [Fact]
    public async Task Enter_of_nothing_is_the_shared_disabled_scope()
    {
        (await Task.FromResult(CostRoutingScope.Enter(null))).Should().BeSameAs(CostRoutingScope.Disabled);
        CostRoutingScope.Disabled.IsActive.Should().BeFalse();
        CostRoutingScope.Disabled.RunState.Should().BeNull();

        await CostRoutingScope.Disabled.DisposeAsync();
        await CostRoutingScope.Disabled.DisposeAsync();
    }

    [Fact]
    public void The_ambient_state_restores_the_enclosing_intent_when_scopes_nest()
    {
        var ambient = new CostRoutingAmbientState();
        CostRoutingRunState first = RunState(escalated: false);
        CostRoutingRunState second = RunState(escalated: true);

        ambient.Current.Should().BeNull();

        using (ambient.Begin(first))
        {
            ambient.Current.Should().BeSameAs(first);

            using (ambient.Begin(second))
                ambient.Current.Should().BeSameAs(second);

            ambient.Current.Should().BeSameAs(first, "disposal restores the enclosing intent rather than clearing it");
        }

        ambient.Current.Should().BeNull();
    }

    [Fact]
    public async Task Disposing_a_scope_whose_row_vanished_does_not_throw()
    {
        SetupDecisionProvider(noul: 0.97);

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");
        CostRoutingScope scope = CostRoutingScope.Enter(decision);

        _db.DecisionObservations.RemoveRange(_db.DecisionObservations);
        await _db.SaveChangesAsync();

        Exception exception = await Record.ExceptionAsync(async () => await scope.DisposeAsync());

        exception.Should().BeNull();
    }

    // --- Soft failure: a gate can never fail, skip or retry a step ---

    [Fact]
    public async Task A_resolver_failure_yields_no_decision_instead_of_throwing()
    {
        _resolver
            .Setup(r => r.ResolveDecisionAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("resolver exploded"));

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().BeNull();
        _db.DecisionObservations.Count().Should().Be(0);
    }

    [Fact]
    public async Task A_client_factory_failure_yields_no_decision_instead_of_throwing()
    {
        SetupDecisionProvider(noul: 0.97);
        _clientFactory
            .Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>()))
            .Throws(new InvalidOperationException("cannot build the decision client"));

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().BeNull();
    }

    [Fact]
    public async Task A_decision_client_failure_yields_no_decision_instead_of_throwing()
    {
        SetupDecisionProvider(noul: 0.97);
        var client = new Mock<IDecisionClient>();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));
        _clientFactory.Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>())).Returns(client.Object);

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().BeNull();
        _db.DecisionObservations.Count().Should().Be(0);
    }

    [Fact]
    public async Task A_response_without_the_expected_answer_is_a_no_op()
    {
        ResolvedDecisionProvider resolved = SetupDecisionProvider(noul: 0.97);
        var client = new Mock<IDecisionClient>();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DecisionResult("m", new Dictionary<string, DecisionAnswer>(), 0, 0));
        _clientFactory.Setup(f => f.Get(resolved)).Returns(client.Object);

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().BeNull();
        _db.DecisionObservations.Count().Should().Be(0);
    }

    [Fact]
    public async Task A_malformed_Noul_answer_is_a_no_op()
    {
        SetupDecisionProvider(noul: null);

        CostRoutingDecision decision = await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "prompt");

        decision.Should().BeNull();
    }

    [Fact]
    public async Task The_question_asked_is_a_Noul_question_carrying_the_bounded_descriptor()
    {
        SetupDecisionProvider(noul: 0.97);
        DecisionState captured = null;
        IReadOnlyList<DecisionQuestion> capturedQuestions = null;

        var client = new Mock<IDecisionClient>();
        client
            .Setup(c => c.DecideAsync(It.IsAny<DecisionState>(), It.IsAny<IReadOnlyList<DecisionQuestion>>(), It.IsAny<CancellationToken>()))
            .Callback<DecisionState, IReadOnlyList<DecisionQuestion>, CancellationToken>((state, questions, _) =>
            {
                captured = state;
                capturedQuestions = questions;
            })
            .ReturnsAsync(new DecisionResult(
                "m",
                new Dictionary<string, DecisionAnswer>
                {
                    [CostRoutingGate.QuestionName] = new(
                        CostRoutingGate.QuestionName, DecisionKind.Noul, null, null, 0.97, null, new Dictionary<string, double>())
                },
                0,
                0));
        _clientFactory.Setup(f => f.Get(It.IsAny<ResolvedDecisionProvider>())).Returns(client.Object);

        await CreateGate(FullyConfiguredConfig()).BeginStepAsync(Context(), Agent(), "RAW-UPSTREAM-MARKER");

        capturedQuestions.Should().ContainSingle();
        capturedQuestions[0].Kind.Should().Be(DecisionKind.Noul);
        capturedQuestions[0].Name.Should().Be(CostRoutingGate.QuestionName);
        captured.Should().NotBeNull();
        captured.Content.Length.Should().BeLessThanOrEqualTo(CostRoutingDescriptor.MaxDescriptorChars);
        captured.Content.Should().NotContain("RAW-UPSTREAM-MARKER");
    }

    private static CostRoutingRunState RunState(bool escalated) => new()
    {
        ObservationId = Guid.NewGuid(),
        ExecutionId = Guid.NewGuid(),
        StepOrder = 4,
        AgentType = AgentType.AuthorAgent,
        Escalated = escalated,
        CheapProviderId = null,
        ExpensiveProviderId = Guid.NewGuid(),
    };
}
