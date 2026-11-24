using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using ReelBolt.WorkflowEngine.Services.Storage;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// saas-quality-followups T-008: each step executor re-opens the execution scope for the tools its
/// agent calls, and the render tool keys its upload on that scope's <c>StepResultId</c>. If any
/// <c>BeginScope</c> caller stops passing <c>context.StepResultId</c>, the render silently falls back
/// to the legacy key and two review-loop iterations overwrite each other. Each test records what the
/// executor handed <c>BeginScope</c> and goes red if the argument is dropped.
/// </summary>
public class StepResultIdScopeTests
{
    private sealed class RecordingAccessor : IWorkflowExecutionContextAccessor
    {
        public ConcurrentBag<Guid?> StepResultIds { get; } = [];

        public WorkflowExecutionContext? Current => null;

        public IDisposable BeginScope(Guid executionId, Guid projectId, string correlationId, Guid? organizationId = null, int runPriority = 0, Guid? stepResultId = null)
        {
            StepResultIds.Add(stepResultId);
            return Mock.Of<IDisposable>();
        }
    }

    private static void AssertScopeCarried(RecordingAccessor accessor, Guid expected)
    {
        accessor.StepResultIds.Should().NotBeEmpty("the executor must open an execution scope");
        accessor.StepResultIds.Should().OnlyContain(id => id == expected);
    }

    private static Mock<IReelBoltAgent> Agent(AgentType type)
    {
        Mock<IReelBoltAgent> agent = new();
        agent.Setup(a => a.Name).Returns(type.ToString());
        agent.Setup(a => a.AgentType).Returns(type);
        agent.Setup(a => a.Tools).Returns(Array.Empty<AIFunction>());
        agent.Setup(a => a.RunAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AgentRunResult { Output = "{}", Success = true, TokensUsed = 1 });
        return agent;
    }

    private static Mock<IAgentRegistry> Registry(IReelBoltAgent agent)
    {
        Mock<IAgentRegistry> registry = new();
        registry.Setup(r => r.GetByType(It.IsAny<AgentType>(), It.IsAny<Guid?>())).Returns(agent);
        return registry;
    }

    private static StepExecutionContext Context(StepType stepType, AgentType agentType, Guid stepResultId, string accumulated = "")
    {
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            AgentDefinitionId = Guid.NewGuid(),
            StepOrder = 1,
            StepType = stepType,
            LoopSourceExpression = "$",
            AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), Name = "a", AgentType = agentType, IsBuiltIn = true }
        };
        return new StepExecutionContext
        {
            Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), OrganizationId = Guid.NewGuid() },
            Step = step,
            AllSteps = [step],
            AccumulatedOutput = accumulated,
            StepOutputHistory = [],
            CurrentStepIndex = 0,
            IterationCount = 0,
            CorrelationId = "test",
            StepResultId = stepResultId,
            CancellationToken = CancellationToken.None
        };
    }

    [Fact]
    public async Task Agent_step_passes_the_step_result_id_to_BeginScope()
    {
        RecordingAccessor accessor = new();
        AgentStepExecutor executor = new(
            Registry(Agent(AgentType.Custom).Object).Object, accessor, Mock.Of<IMotionGraphicsPlacementAnnotator>(),
            new ConfigurationBuilder().Build(), Mock.Of<IDecisionGate>(), NullLogger<AgentStepExecutor>.Instance);
        Guid id = Guid.NewGuid();

        await executor.ExecuteAsync(Context(StepType.Agent, AgentType.Custom, id));

        AssertScopeCarried(accessor, id);
    }

    [Fact]
    public async Task ReviewLoop_step_passes_the_step_result_id_to_BeginScope()
    {
        RecordingAccessor accessor = new();
        ReviewLoopStepExecutor executor = new(
            Registry(Agent(AgentType.Custom).Object).Object, accessor, new Mock<IDecisionGate>().Object,
            NullLogger<ReviewLoopStepExecutor>.Instance);
        Guid id = Guid.NewGuid();

        await executor.ExecuteAsync(Context(StepType.ReviewLoop, AgentType.Custom, id));

        AssertScopeCarried(accessor, id);
    }

    [Fact]
    public async Task ForEach_step_passes_the_step_result_id_to_BeginScope()
    {
        RecordingAccessor accessor = new();
        ForEachStepExecutor executor = new(
            Registry(Agent(AgentType.Custom).Object).Object, accessor,
            new ConfigurationBuilder().Build(), NullLogger<ForEachStepExecutor>.Instance);
        Guid id = Guid.NewGuid();

        await executor.ExecuteAsync(Context(StepType.ForEach, AgentType.Custom, id, accumulated: "[1,2]"));

        AssertScopeCarried(accessor, id);
    }

    [Fact]
    public async Task Parallel_step_passes_the_step_result_id_to_BeginScope()
    {
        RecordingAccessor accessor = new();
        string dbName = Guid.NewGuid().ToString();
        ServiceCollection services = new();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        ServiceProvider provider = services.BuildServiceProvider();
        StepExecutionContext context = Context(StepType.Parallel, AgentType.Custom, Guid.NewGuid());
        using (IServiceScope seed = provider.CreateScope())
        {
            WorkflowEngineDbContext db = seed.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();
            db.AgentDefinitions.Add(new AgentDefinition
            {
                Id = context.Step.AgentDefinitionId,
                Name = "a",
                AgentType = AgentType.Custom,
                IsBuiltIn = true
            });
            await db.SaveChangesAsync();
        }

        ParallelStepExecutor executor = new(
            Registry(Agent(AgentType.Custom).Object).Object, accessor,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(), NullLogger<ParallelStepExecutor>.Instance);

        await executor.ExecuteAsync(context);

        AssertScopeCarried(accessor, context.StepResultId);
    }

    [Fact]
    public async Task Room_step_passes_the_step_result_id_to_BeginScope()
    {
        RecordingAccessor accessor = new();
        EditRoomStepExecutor executor = new(
            new Mock<IAgentChatClientProvider>().Object, new Mock<IAgentToolProvider>().Object,
            new Mock<IAgentRegistry>().Object, new Mock<IProjectFileWorkspace>().Object,
            accessor, NullLogger<EditRoomStepExecutor>.Instance, null!);
        Guid id = Guid.NewGuid();

        // No room config: the step fails, but only after the base class has opened its scope.
        await executor.ExecuteAsync(Context(StepType.EditRoom, AgentType.Custom, id));

        AssertScopeCarried(accessor, id);
    }

    [Fact]
    public async Task Revise_step_hands_the_step_result_id_to_the_revision_runner()
    {
        StepExecutionContext context = Context(StepType.Revise, AgentType.CompositionReviser, Guid.NewGuid());
        Mock<ReelBolt.WorkflowEngine.Services.Compositions.ICompositionWorkspaceStore> store = new();
        store.Setup(x => x.FindRevisionByExecutionAsync(context.Execution.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CompositionRevision
            {
                Id = Guid.NewGuid(),
                ProjectId = context.Execution.ProjectId,
                WorkspaceId = Guid.NewGuid(),
                ExecutionId = context.Execution.Id,
                Instruction = "x"
            });
        ReelBolt.WorkflowEngine.Services.Compositions.CompositionRevisionRequest? seen = null;
        Mock<ReelBolt.WorkflowEngine.Services.Compositions.ICompositionRevisionRunner> runner = new();
        runner.Setup(r => r.RunAsync(It.IsAny<ReelBolt.WorkflowEngine.Services.Compositions.CompositionRevisionRequest>(),
                It.IsAny<ReelBolt.WorkflowEngine.Services.Compositions.ICompositionRevisionObserver?>(), It.IsAny<CancellationToken>()))
            .Callback<ReelBolt.WorkflowEngine.Services.Compositions.CompositionRevisionRequest,
                ReelBolt.WorkflowEngine.Services.Compositions.ICompositionRevisionObserver?, CancellationToken>((request, _, _) => seen = request)
            .ReturnsAsync(new ReelBolt.WorkflowEngine.Services.Compositions.CompositionRevisionOutcome
            {
                Succeeded = false,
                FailureCode = "X",
                FailureMessage = "x"
            });
        ReviseStepExecutor executor = new(store.Object, runner.Object, NullLogger<ReviseStepExecutor>.Instance);

        await executor.ExecuteAsync(context);

        seen.Should().NotBeNull();
        seen!.StepResultId.Should().Be(context.StepResultId);
    }
}
