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
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// SQFU-S7, run-time half for Parallel steps: the extra agents a stored <c>ParallelAgentIdsJson</c> names
/// obey the same A8 rule as a step's own agent (<see cref="AgentStepExecutorOrganizationScopeTests"/>).
/// Only platform built-ins and the execution's organization's agents are loaded and run; a row of another
/// organization fails the step closed with AgentStepExecutor's refusal, and nothing runs. The configs here
/// are seeded straight into the database, bypassing the save-time check, as an old or tampered row would be.
/// </summary>
public sealed class ParallelStepExecutorOrganizationScopeTests : IDisposable
{
    private readonly WorkflowEngineDbContext _db = new(
        new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase("parallel-a8-" + Guid.NewGuid().ToString("N")).Options);

    private readonly Guid _executionOrg = Guid.NewGuid();
    private readonly List<Guid?> _ran = [];

    public void Dispose() => _db.Dispose();

    private sealed class Scopes(WorkflowEngineDbContext db) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }
        public object? GetService(Type serviceType) => serviceType == typeof(WorkflowEngineDbContext) ? db : null;
    }

    private ParallelStepExecutor Executor()
    {
        Mock<IReelBoltAgent> agent = new();
        agent.Setup(a => a.Name).Returns("agent");
        agent.Setup(a => a.Tools).Returns(Array.Empty<Microsoft.Extensions.AI.AIFunction>());
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Guid? id, CancellationToken _) =>
            {
                lock (_ran)
                    _ran.Add(id);
                return new AgentRunResult { Output = "{}", Success = true };
            });
        Mock<IAgentRegistry> registry = new();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent.Object);

        Mock<IWorkflowExecutionContextAccessor> accessor = new();
        accessor.Setup(a => a.BeginScope(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(Mock.Of<IDisposable>());

        return new ParallelStepExecutor(registry.Object, accessor.Object, new Scopes(_db), new ConfigurationBuilder().Build(),
            NullLogger<ParallelStepExecutor>.Instance);
    }

    private async Task<AgentDefinition> AgentAsync(Guid? organizationId, bool builtIn = false)
    {
        AgentDefinition agent = new()
        {
            Id = Guid.NewGuid(),
            Name = builtIn ? "VideoStoryEditor" : "Custom",
            AgentType = builtIn ? AgentType.VideoStoryEditor : AgentType.Custom,
            IsBuiltIn = builtIn,
            OrganizationId = organizationId
        };
        _db.AgentDefinitions.Add(agent);
        await _db.SaveChangesAsync();
        return agent;
    }

    private StepExecutionContext Context(AgentDefinition primary, params Guid[] extra)
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = primary.Id,
            AgentDefinition = primary,
            StepOrder = 1,
            StepType = StepType.Parallel,
            ParallelAgentIdsJson = JsonSerializer.Serialize(extra.Prepend(primary.Id))
        };
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), OrganizationId = _executionOrg },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = string.Empty,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            CancellationToken = CancellationToken.None
        };
    }

    [Fact]
    public async Task A_stored_parallel_config_naming_another_organizations_agent_fails_the_step_and_nothing_runs()
    {
        AgentDefinition mine = await AgentAsync(_executionOrg);
        Guid foreignOrg = Guid.NewGuid();
        AgentDefinition theirs = await AgentAsync(foreignOrg);

        StepExecutionResult result = await Executor().ExecuteAsync(Context(mine, theirs.Id));

        result.Status.Should().Be(StepStatus.Failed);
        result.ErrorDetails.Should().Be(
            $"Agent definition {theirs.Id} belongs to organization {foreignOrg}, but this execution runs for organization {_executionOrg}.",
            "the refusal is AgentStepExecutor's, word for word");
        result.FailureClass.Should().Be(FailureClass.None, "AgentStepExecutor's refusal carries no failure class either");
        _ran.Should().BeEmpty("the step fails before any agent, the run's own included, is handed the input");
    }

    [Fact]
    public async Task Own_organization_and_built_in_agents_run_as_before()
    {
        AgentDefinition mine = await AgentAsync(_executionOrg);
        AgentDefinition builtIn = await AgentAsync(organizationId: null, builtIn: true);

        StepExecutionResult result = await Executor().ExecuteAsync(Context(mine, builtIn.Id));

        result.Status.Should().Be(StepStatus.Completed);
        _ran.Should().BeEquivalentTo([(Guid?)mine.Id, builtIn.Id]);
    }

    [Fact]
    public async Task An_id_with_no_agent_row_at_all_is_still_skipped_as_before()
    {
        AgentDefinition mine = await AgentAsync(_executionOrg);

        StepExecutionResult result = await Executor().ExecuteAsync(Context(mine, Guid.NewGuid()));

        result.Status.Should().Be(StepStatus.Completed, "a missing row was never a cross-organization reference");
        _ran.Should().Equal([(Guid?)mine.Id]);
    }
}
