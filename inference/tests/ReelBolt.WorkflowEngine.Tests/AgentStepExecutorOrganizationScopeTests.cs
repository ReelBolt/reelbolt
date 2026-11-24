using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// WP A8, run-time half: the engine refuses to execute a custom agent whose organization is not the
/// execution's. The save-side checks in the Inference API
/// (<c>WorkflowsController</c>, <c>WorkflowAssistantController.Apply</c>,
/// <c>WorkflowEditDiffService.ValidateProposedSteps</c>) reject the reference when a workflow is
/// written; this is the independent enforcement an already-stored row still meets.
///
/// <para>
/// The step FAILS rather than being skipped: an unresolvable agent is skipped further down, and a
/// silently skipped step would let a cross-organization run look successful while doing nothing.
/// </para>
/// </summary>
public class AgentStepExecutorOrganizationScopeTests
{
    private static AgentStepExecutor CreateExecutor(IReelBoltAgent? agent)
    {
        var registry = new Mock<IAgentRegistry>();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);

        var accessor = new Mock<IWorkflowExecutionContextAccessor>();
        accessor.Setup(a => a.BeginScope(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());
        accessor.Setup(a => a.Current).Returns((WorkflowExecutionContext?)null);

        return new AgentStepExecutor(
            registry.Object,
            accessor.Object,
            Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IDecisionGate>(),
            NullLogger<AgentStepExecutor>.Instance);
    }

    private static Mock<IReelBoltAgent> MockAgent(AgentType agentType)
    {
        var agent = new Mock<IReelBoltAgent>();
        agent.Setup(a => a.Name).Returns(agentType.ToString());
        agent.Setup(a => a.AgentType).Returns(agentType);
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        return agent;
    }

    private static StepExecutionContext CreateContext(
        AgentType agentType, bool isBuiltIn, Guid? agentOrganizationId, Guid executionOrganizationId)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = StepType.Agent,
            AgentDefinition = new AgentDefinition
            {
                Id = Guid.NewGuid(),
                Name = "a",
                AgentType = agentType,
                IsBuiltIn = isBuiltIn,
                OrganizationId = agentOrganizationId
            }
        };

        return new StepExecutionContext
        {
            Execution = new WorkflowExecution
            {
                Id = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                OrganizationId = executionOrganizationId
            },
            Step = step,
            AllSteps = new List<WorkflowStep> { step },
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    [Fact]
    public async Task A_custom_agent_from_another_organization_fails_the_step_and_never_runs()
    {
        Guid agentOrganization = Guid.NewGuid();
        Guid executionOrganization = Guid.NewGuid();
        Mock<IReelBoltAgent> agent = MockAgent(AgentType.Custom);
        AgentStepExecutor executor = CreateExecutor(agent.Object);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(AgentType.Custom, isBuiltIn: false, agentOrganization, executionOrganization));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Contain(agentOrganization.ToString());
        result.ErrorDetails.Should().Contain(executionOrganization.ToString());

        // The refusal happens BEFORE the agent is handed anything — the foreign agent never runs.
        agent.Verify(
            a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_custom_agent_from_the_executions_own_organization_is_not_refused()
    {
        Guid organization = Guid.NewGuid();
        AgentStepExecutor executor = CreateExecutor(agent: null);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(AgentType.Custom, isBuiltIn: false, organization, organization));

        // No engine-side AgentType.Custom implementation is registered, so the step is SKIPPED —
        // the important half is that it is not the organization-mismatch FAILURE.
        result.Status.Should().Be(StepStatus.Skipped);
    }

    [Fact]
    public async Task A_built_in_agent_with_no_organization_is_never_refused()
    {
        AgentStepExecutor executor = CreateExecutor(agent: null);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(AgentType.AuthorAgent, isBuiltIn: true, agentOrganizationId: null, Guid.NewGuid()));

        result.Status.Should().Be(StepStatus.Skipped);
    }

    [Fact]
    public async Task A_built_in_agent_stays_visible_to_every_organization_even_if_it_carries_one()
    {
        // The A1 check constraint lets a built-in row carry an organization_id (it only forbids a
        // CUSTOM row without one), so the guard must key on IsBuiltIn, not on the column alone.
        AgentStepExecutor executor = CreateExecutor(agent: null);

        StepExecutionResult result = await executor.ExecuteAsync(
            CreateContext(AgentType.AuthorAgent, isBuiltIn: true, Guid.NewGuid(), Guid.NewGuid()));

        result.Status.Should().Be(StepStatus.Skipped);
    }
}
