using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-012: a run the assistant/MCP admitted as FREE is re-classified over the steps the engine actually loaded;
/// if those are paid (the workflow was edited after admission) the run fails RUN_CLASS_CHANGED before any step.
/// </summary>
public class WorkflowExecutorServiceRunClassTests
{
    private sealed class CountingExecutor(StepType stepType) : IStepExecutor
    {
        public int CallCount { get; private set; }

        public StepType StepType { get; } = stepType;

        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
        {
            CallCount++;
            return Task.FromResult(new StepExecutionResult
            {
                Output = "{}",
                NextStepIndex = context.CurrentStepIndex + 1,
                NewIterationCount = context.IterationCount,
                Status = StepStatus.Completed
            });
        }
    }

    private sealed class Rig : IDisposable
    {
        public WorkflowEngineDbContext Db { get; } = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        public Guid ExecutionId { get; } = Guid.NewGuid();
        public CountingExecutor Agent { get; } = new(StepType.Agent);
        public CountingExecutor Extract { get; } = new(StepType.Extract);

        public Rig(params StepType[] stepTypes)
        {
            Guid projectId = Guid.NewGuid();
            AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = "Agent", AgentType = AgentType.VideoStoryEditor, SystemPrompt = "" };
            var steps = new List<WorkflowStep>();
            int order = 1;
            foreach (StepType type in stepTypes)
                steps.Add(new WorkflowStep { Id = Guid.NewGuid(), StepOrder = order++, StepType = type, AgentDefinitionId = agent.Id, AgentDefinition = agent });
            WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "w", ProjectId = projectId, Steps = steps };
            Db.AgentDefinitions.Add(agent);
            Db.WorkflowDefinitions.Add(definition);
            Db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = ExecutionId,
                WorkflowDefinitionId = definition.Id,
                ProjectId = projectId,
                Status = ExecutionStatus.Queued,
                WorkflowDefinition = definition
            });
            Db.SaveChanges();
        }

        public WorkflowExecutorService Service() => new(
            scopeFactory: new FakeScopeFactory(Db),
            eventPublisher: new NoOpPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: new IStepExecutor[] { Agent, Extract },
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());

        public async Task<WorkflowExecution> FinalAsync() =>
            await Db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == ExecutionId);

        public void Dispose() => Db.Dispose();
    }

    private sealed class NoOpPublisher : IWorkflowEventPublisher
    {
        public Task PublishExecutionRunningAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepStartedAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult, string? inputPreview, CancellationToken ct) =>
            Task.CompletedTask;
        public Task PublishStepCompletedAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepProgressAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            string stage, int? percentComplete, CancellationToken ct,
            int? tokensUsedSoFar = null, int? inputTokensSoFar = null, int? outputTokensSoFar = null) => Task.CompletedTask;
        public Task PublishStepChatTurnAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            int turnIndex, int? totalTurns, string speaker, string speakerRole, string text,
            IReadOnlyList<string> idsMentioned, CancellationToken ct) => Task.CompletedTask;
        public Task PublishStepDiagnosticsAsync(
            WorkflowExecution execution, WorkflowStep step, WorkflowStepResult stepResult,
            StepExecutionResult stepExecutionResult, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionCompletedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
        public Task PublishExecutionFailedAsync(WorkflowExecution execution, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_free_admitted_run_whose_loaded_steps_are_paid_fails_before_any_step()
    {
        using var rig = new Rig(StepType.Extract, StepType.Agent);

        Func<Task> run = () => rig.Service().ExecuteAsync(rig.ExecutionId, "corr", "free", CancellationToken.None);

        (await run.Should().ThrowAsync<RunClassChangedException>()).Which.Code.Should().Be("RUN_CLASS_CHANGED");
        rig.Extract.CallCount.Should().Be(0, "no step runs, not even a free one");
        rig.Agent.CallCount.Should().Be(0);
        WorkflowExecution final = await rig.FinalAsync();
        final.Status.Should().Be(ExecutionStatus.Failed);
        final.ErrorMessage.Should().Be("This workflow changed after the run started; start it again.");
        (await rig.Db.WorkflowStepResults.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("FREE")]
    [InlineData("bogus")]
    public async Task An_admitted_bucket_that_is_not_paid_is_checked_like_free(string admitted)
    {
        using var rig = new Rig(StepType.Agent);

        Func<Task> run = () => rig.Service().ExecuteAsync(rig.ExecutionId, "corr", admitted, CancellationToken.None);

        await run.Should().ThrowAsync<RunClassChangedException>();
        rig.Agent.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task A_free_admitted_run_whose_loaded_steps_are_still_free_runs()
    {
        using var rig = new Rig(StepType.Extract);

        await rig.Service().ExecuteAsync(rig.ExecutionId, "corr", "free", CancellationToken.None);

        rig.Extract.CallCount.Should().Be(1);
        (await rig.FinalAsync()).Status.Should().NotBe(ExecutionStatus.Failed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("paid")]
    public async Task A_run_with_no_admitted_bucket_or_admitted_paid_is_not_checked(string? admitted)
    {
        using var rig = new Rig(StepType.Agent);

        await rig.Service().ExecuteAsync(rig.ExecutionId, "corr", admitted, CancellationToken.None);

        rig.Agent.CallCount.Should().Be(1);
        (await rig.FinalAsync()).Status.Should().NotBe(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task The_three_argument_overload_is_unchanged()
    {
        using var rig = new Rig(StepType.Agent);

        await rig.Service().ExecuteAsync(rig.ExecutionId, "corr", CancellationToken.None);

        rig.Agent.CallCount.Should().Be(1);
    }

    [Fact]
    public void RUN_CLASS_CHANGED_is_the_users_input()
    {
        FailureClassifier.ForCode(RunClassChangedException.FailureCode).Should().Be(FailureClass.UserInput);
        FailureClassifier.ForException(new RunClassChangedException()).Should().Be(FailureClass.UserInput);
    }
}
