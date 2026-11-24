using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for the <see cref="AgentStepExecutor"/> side of cost routing: the hook is optional (the
/// pre-existing 6-argument construction still compiles and runs), it fires exactly once, it fires
/// BEFORE the agent run, its scope is disposed AFTER it, and the returned
/// <see cref="StepExecutionResult"/> is unaffected by whether routing is active.
/// </summary>
public class AgentStepExecutorCostRoutingTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>()).Build();

    private static Mock<IWorkflowExecutionContextAccessor> MockAccessor()
    {
        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext)null);
        return accessor;
    }

    /// <summary>The 6-argument shape every existing helper (including
    /// <c>AgentStepExecutorDecisionShadowTests.CreateExecutor</c>) uses — the gate is optional.</summary>
    private static AgentStepExecutor CreateExecutorWithoutCostRouting(IReelBoltAgent agent, IDecisionGate decisionGate)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);

        return new AgentStepExecutor(
            registry.Object,
            MockAccessor().Object,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            EmptyConfig(),
            decisionGate,
            NullLogger<AgentStepExecutor>.Instance);
    }

    private static AgentStepExecutor CreateExecutor(
        IReelBoltAgent agent, ICostRoutingGate costRoutingGate, Action onRun = null)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);

        return new AgentStepExecutor(
            registry.Object,
            MockAccessor().Object,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            EmptyConfig(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance,
            costRoutingGate);
    }

    private static Mock<IReelBoltAgent> MockAgent(AgentType agentType, Action onRun = null)
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(agentType.ToString());
        agent.Setup(a => a.AgentType).Returns(agentType);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        agent.Setup(a => a.OutputSchemaType).Returns((Type)null);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .Callback(() => onRun?.Invoke())
            .ReturnsAsync(new AgentRunResult { Output = "{\"ok\":true}", Success = true, TokensUsed = 10, InputTokens = 6, OutputTokens = 4 });
        return agent;
    }

    private static StepExecutionContext CreateContext(AgentType agentType)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 3,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), AgentType = agentType, Name = agentType.ToString() },
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None,
        };
    }

    [Fact]
    public async Task The_gate_is_optional_and_a_null_gate_leaves_the_step_untouched()
    {
        var agent = MockAgent(AgentType.DirectorAgent);

        StepExecutionResult result = await CreateExecutorWithoutCostRouting(agent.Object, Mock.Of<IDecisionGate>())
            .ExecuteAsync(CreateContext(AgentType.DirectorAgent));

        result.Status.Should().Be(StepStatus.Completed);
        result.Output.Should().Be("{\"ok\":true}");
        agent.Verify(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Routing_begins_before_the_agent_runs_and_its_scope_is_disposed_after()
    {
        List<string> order = [];

        var gate = new Mock<ICostRoutingGate>();
        gate.Setup(g => g.BeginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<IReelBoltAgent>(), It.IsAny<string>()))
            .Callback(() => order.Add("begin"))
            .ReturnsAsync(Decision(_ =>
            {
                order.Add("dispose");
                return ValueTask.CompletedTask;
            }));

        var agent = MockAgent(AgentType.DirectorAgent, onRun: () => order.Add("run"));

        StepExecutionResult result = await CreateExecutor(agent.Object, gate.Object).ExecuteAsync(CreateContext(AgentType.DirectorAgent));

        result.Status.Should().Be(StepStatus.Completed);
        order.Should().Equal("begin", "run", "dispose");

        gate.Verify(g => g.BeginStepAsync(
            It.IsAny<StepExecutionContext>(),
            agent.Object,
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task A_declined_gate_is_a_harmless_no_op()
    {
        var gate = new Mock<ICostRoutingGate>();
        gate.Setup(g => g.BeginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<IReelBoltAgent>(), It.IsAny<string>()))
            .ReturnsAsync((CostRoutingDecision)null);

        StepExecutionResult result = await CreateExecutor(MockAgent(AgentType.DirectorAgent).Object, gate.Object)
            .ExecuteAsync(CreateContext(AgentType.DirectorAgent));

        result.Status.Should().Be(StepStatus.Completed);
    }

    [Fact]
    public async Task Routing_intent_is_visible_inside_the_agent_run()
    {
        // The regression this exists for: an AsyncLocal write performed inside the gate's own async
        // method is DISCARDED before control returns to the executor, so a decision armed there
        // would never reach the agent's chat-client resolution. Arming must happen in the executor's
        // frame (CostRoutingScope.Enter), and this test fails if it ever moves back across an await.
        var ambient = new CostRoutingAmbientState();
        bool? visibleInsideTheRun = null;

        var gate = new Mock<ICostRoutingGate>();
        gate.Setup(g => g.BeginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<IReelBoltAgent>(), It.IsAny<string>()))
            .ReturnsAsync(new CostRoutingDecision
            {
                RunState = RunState(),
                Ambient = ambient,
                Complete = _ => ValueTask.CompletedTask,
            });

        var agent = MockAgent(AgentType.DirectorAgent, onRun: () => visibleInsideTheRun = ambient.Current is not null);

        await CreateExecutor(agent.Object, gate.Object).ExecuteAsync(CreateContext(AgentType.DirectorAgent));

        visibleInsideTheRun.Should().BeTrue("the routing decision must reach the agent's chat-client resolution");
        ambient.Current.Should().BeNull("the intent must be cleared once the run is over");
    }

    [Fact]
    public async Task The_routing_intent_does_not_survive_into_the_next_step()
    {
        var ambient = new CostRoutingAmbientState();

        var gate = new Mock<ICostRoutingGate>();
        gate.Setup(g => g.BeginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<IReelBoltAgent>(), It.IsAny<string>()))
            .ReturnsAsync(new CostRoutingDecision
            {
                RunState = RunState(),
                Ambient = ambient,
                Complete = _ => ValueTask.CompletedTask,
            });

        AgentStepExecutor executor = CreateExecutor(MockAgent(AgentType.DirectorAgent).Object, gate.Object);

        await executor.ExecuteAsync(CreateContext(AgentType.DirectorAgent));

        ambient.Current.Should().BeNull();
    }

    [Fact]
    public async Task A_routed_step_returns_exactly_the_same_result_fields_as_an_unrouted_one()
    {
        var activeGate = new Mock<ICostRoutingGate>();
        activeGate.Setup(g => g.BeginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<IReelBoltAgent>(), It.IsAny<string>()))
            .ReturnsAsync(Decision(_ => ValueTask.CompletedTask));

        var inertGate = new Mock<ICostRoutingGate>();
        inertGate.Setup(g => g.BeginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<IReelBoltAgent>(), It.IsAny<string>()))
            .ReturnsAsync((CostRoutingDecision)null);

        StepExecutionResult routed = await CreateExecutor(MockAgent(AgentType.DirectorAgent).Object, activeGate.Object)
            .ExecuteAsync(CreateContext(AgentType.DirectorAgent));
        StepExecutionResult unrouted = await CreateExecutor(MockAgent(AgentType.DirectorAgent).Object, inertGate.Object)
            .ExecuteAsync(CreateContext(AgentType.DirectorAgent));

        routed.Output.Should().Be(unrouted.Output);
        routed.Status.Should().Be(unrouted.Status);
        routed.TokensUsed.Should().Be(unrouted.TokensUsed);
        routed.InputTokens.Should().Be(unrouted.InputTokens);
        routed.OutputTokens.Should().Be(unrouted.OutputTokens);
        routed.NextStepIndex.Should().Be(unrouted.NextStepIndex);
        routed.NewIterationCount.Should().Be(unrouted.NewIterationCount);
        routed.ErrorDetails.Should().Be(unrouted.ErrorDetails);
        routed.OutputStorageKey.Should().Be(unrouted.OutputStorageKey);
    }

    [Fact]
    public async Task A_failed_step_still_disposes_the_routing_scope()
    {
        bool disposed = false;

        var gate = new Mock<ICostRoutingGate>();
        gate.Setup(g => g.BeginStepAsync(It.IsAny<StepExecutionContext>(), It.IsAny<IReelBoltAgent>(), It.IsAny<string>()))
            .ReturnsAsync(Decision(_ =>
            {
                disposed = true;
                return ValueTask.CompletedTask;
            }));

        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns("DirectorAgent");
        agent.Setup(a => a.AgentType).Returns(AgentType.DirectorAgent);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        agent.Setup(a => a.OutputSchemaType).Returns((Type)null);
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("agent exploded"));

        Exception exception = await Record.ExceptionAsync(
            async () => await CreateExecutor(agent.Object, gate.Object).ExecuteAsync(CreateContext(AgentType.DirectorAgent)));

        exception.Should().NotBeNull("the executor's own failure semantics are unchanged by cost routing");
        disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Routing_is_never_attempted_for_a_step_whose_agent_does_not_resolve()
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns((IReelBoltAgent)null);

        var gate = new Mock<ICostRoutingGate>();

        var executor = new AgentStepExecutor(
            registry.Object,
            MockAccessor().Object,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            EmptyConfig(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance,
            gate.Object);

        StepExecutionResult result = await executor.ExecuteAsync(CreateContext(AgentType.DirectorAgent));

        result.Status.Should().Be(StepStatus.Skipped);
        gate.Verify(g => g.BeginStepAsync(
            It.IsAny<StepExecutionContext>(), It.IsAny<IReelBoltAgent>(), It.IsAny<string>()), Times.Never);
    }

    private static CostRoutingRunState RunState() => new()
    {
        ObservationId = Guid.NewGuid(),
        ExecutionId = Guid.NewGuid(),
        StepOrder = 3,
        AgentType = AgentType.DirectorAgent,
        Escalated = false,
        CheapProviderId = null,
        ExpensiveProviderId = Guid.NewGuid(),
    };

    private static CostRoutingDecision Decision(Func<CostRoutingRunState, ValueTask> complete) => new()
    {
        RunState = RunState(),
        Ambient = new CostRoutingAmbientState(),
        Complete = complete,
    };
}
