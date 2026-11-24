using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Inference;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// A9a: provider resolution is organization-scoped, and several callers (VideoAnalyze, VideoCompile,
/// Voiceover, VideoGenerate, the decision gate, the context selector, the guardrail screeners)
/// resolve providers outside any agent scope. The only thing that gives them the execution's
/// organization is the OUTER scope <c>WorkflowExecutorService</c> opens for the whole run, so every
/// step executor it dispatches to must observe it.
/// </summary>
public class ExecutionOrganizationScopeTests
{
    private sealed record Observation(StepType StepType, Guid? ContextOrganizationId, Guid? TenantOrganizationId);

    private sealed class ObservingExecutor(
        StepType stepType,
        IWorkflowExecutionContextAccessor accessor,
        IInferenceTenantAccessor tenant,
        List<Observation> sink) : IStepExecutor
    {
        public StepType StepType => stepType;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            lock (sink)
            {
                sink.Add(new Observation(stepType, accessor.Current?.OrganizationId, tenant.OrganizationId));
            }

            return Task.FromResult(new StepExecutionResult
            {
                Output = "{}",
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
        }
    }

    private sealed class NoOpEventPublisher : IWorkflowEventPublisher
    {
        public Task PublishExecutionRunningAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepStartedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string? inputPreview, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepCompletedAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepProgressAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string stage, int? percentComplete, CancellationToken ct, int? tokensUsedSoFar = null, int? inputTokensSoFar = null, int? outputTokensSoFar = null) => Task.CompletedTask;
        public Task PublishStepChatTurnAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, int turnIndex, int? totalTurns, string speaker, string speakerRole, string text, IReadOnlyList<string> idsMentioned, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepDiagnosticsAsync(WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionCompletedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionFailedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task Every_step_executor_reached_through_the_workflow_executor_observes_the_executions_organization()
    {
        Guid organizationId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using WorkflowEngineDbContext db = new(options);

        WorkflowExecutionContextAccessor contextAccessor = new();
        EngineInferenceTenantAccessor tenant = new(contextAccessor);

        ServiceCollection services = new();
        services.AddSingleton(db);
        services.AddSingleton<IWorkflowExecutionContextAccessor>(contextAccessor);
        IServiceScopeFactory scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        // One step per step type: a new StepType added without an executor observing the scope is
        // caught here, because the workflow reaches it through the same dispatch.
        StepType[] stepTypes = Enum.GetValues<StepType>();
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "Stub", AgentType = AgentType.VideoTransform, SystemPrompt = "" };
        List<WorkflowStep> steps = stepTypes
            .Select((type, i) => new WorkflowStep
            {
                Id = Guid.NewGuid(),
                StepOrder = i + 1,
                StepType = type,
                AgentDefinitionId = agent.Id,
                AgentDefinition = agent
            })
            .ToList();

        WorkflowDefinition definition = new()
        {
            Id = Guid.NewGuid(),
            Name = "org-scope",
            ProjectId = projectId,
            Steps = steps
        };
        WorkflowExecution execution = new()
        {
            Id = executionId,
            WorkflowDefinitionId = definition.Id,
            ProjectId = projectId,
            OrganizationId = organizationId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = definition
        };
        db.AgentDefinitions.Add(agent);
        db.WorkflowDefinitions.Add(definition);
        db.WorkflowExecutions.Add(execution);
        await db.SaveChangesAsync();

        List<Observation> observations = [];
        IStepExecutor[] executors = stepTypes
            .Select(t => (IStepExecutor)new ObservingExecutor(t, contextAccessor, tenant, observations))
            .ToArray();

        WorkflowExecutorService service = new(
            scopeFactory: scopeFactory,
            eventPublisher: new NoOpEventPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: executors,
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());

        await service.ExecuteAsync(executionId, "corr-org", CancellationToken.None);

        observations.Select(o => o.StepType).Should().BeEquivalentTo(stepTypes,
            "every step type must have been dispatched exactly once");
        observations.Should().OnlyContain(o => o.ContextOrganizationId == organizationId);
        observations.Should().OnlyContain(o => o.TenantOrganizationId == organizationId,
            "the resolver's tenant accessor must see the execution's organization in every executor");

        contextAccessor.Current.Should().BeNull("the outer scope is released when the execution ends");
        tenant.OrganizationId.Should().BeNull();
    }
}
