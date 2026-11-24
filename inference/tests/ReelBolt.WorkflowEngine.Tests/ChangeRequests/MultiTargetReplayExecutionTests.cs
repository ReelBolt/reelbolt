using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Compositions.ReplayShapes;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>A two-target change request through the REAL executor: one run, reused rows copied, each target and its dependents run once.</summary>
public class MultiTargetReplayExecutionTests
{
    private sealed class Executor(Func<StepExecutionContext, StepExecutionResult> run) : IStepExecutor
    {
        public StepType StepType => StepType.Agent;
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) => Task.FromResult(run(context));
    }

    private sealed class NoOpPublisher : IWorkflowEventPublisher
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

    private static WorkflowStep Reads(int order, params int[] reads)
    {
        WorkflowStep step = Step(order, AgentType.DirectorAgent, mode: AgentInputContextMode.SelectedPriorSteps);
        step.SelectedPriorStepOrdersJson = JsonSerializer.Serialize(reads);
        return step;
    }

    [Fact]
    public async Task Two_targets_run_once_each_with_their_dependents_and_everything_else_is_copied()
    {
        WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase("multi-replay-" + Guid.NewGuid().ToString("N")).Options);
        Guid projectId = Guid.NewGuid();

        // 1 root, 2 and 3 are the targets, 4 reads both, 5 is unrelated.
        List<WorkflowStep> steps = [Reads(1), Reads(2, 1), Reads(3, 1), Reads(4, 2, 3), Reads(5, 1)];
        WorkflowDefinition original = new() { Id = Guid.NewGuid(), Name = "Original", ProjectId = projectId };
        foreach (WorkflowStep s in steps)
            s.WorkflowDefinitionId = original.Id;
        WorkflowExecution sourceRun = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = original.Id, ProjectId = projectId, Status = ExecutionStatus.Passed, UserRequest = "brief" };
        Dictionary<int, WorkflowStepResult> source = steps.ToDictionary(s => s.StepOrder, s => new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = sourceRun.Id,
            WorkflowStepId = s.Id,
            Status = StepStatus.Completed,
            ExecutedAt = DateTime.UtcNow.AddMinutes(-60 + s.StepOrder),
            Output = $"{{\"step\":{s.StepOrder}}}"
        });

        CompositionReplayPlan plan = CompositionReplayPlanner.Plan(
            steps, source.ToDictionary(kv => kv.Key, kv => kv.Value.Id), [2, 3], CompositionCutPolicy.KeepCut, sourceRun.Id, original.Id, "x", "Agent");
        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), Name = "Changes", ProjectId = projectId, Kind = WorkflowDefinitionKind.CompositionReplay };
        foreach (WorkflowStep s in steps)
        {
            WorkflowStep copy = WorkflowStepCloner.Clone(s, snapshot.Id);
            copy.AgentDefinition = s.AgentDefinition;
            snapshot.Steps.Add(copy);
        }

        WorkflowExecution replay = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = snapshot.Id,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            UserRequest = "brief",
            WorkflowDefinition = snapshot
        };
        foreach (int order in new[] { 2, 3 })
        {
            db.StepChangeRequests.Add(new StepChangeRequest
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                SourceExecutionId = sourceRun.Id,
                TargetStepOrder = order,
                TargetStepResultId = source[order].Id,
                StepType = "Agent",
                ExecutionId = replay.Id,
                WorkflowDefinitionId = snapshot.Id,
                ReplayPlanJson = plan.Serialize()
            });
        }

        db.AgentDefinitions.AddRange(steps.Select(s => s.AgentDefinition));
        db.WorkflowDefinitions.AddRange(original, snapshot);
        db.WorkflowSteps.AddRange(steps);
        db.WorkflowExecutions.AddRange(sourceRun, replay);
        db.WorkflowStepResults.AddRange(source.Values);
        await db.SaveChangesAsync();

        List<int> ran = [];
        WorkflowExecutorService service = new(
            scopeFactory: new FakeScopeFactory(db),
            eventPublisher: new NoOpPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: [new Executor(c =>
            {
                ran.Add(c.Step.StepOrder);
                return new StepExecutionResult { Output = "{}", NextStepIndex = c.CurrentStepIndex + 1, NewIterationCount = c.IterationCount, Status = StepStatus.Completed };
            })],
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());
        await service.ExecuteAsync(replay.Id, "corr", CancellationToken.None);

        WorkflowExecution done = await db.WorkflowExecutions.SingleAsync(e => e.Id == replay.Id);
        done.Status.Should().Be(ExecutionStatus.Passed, done.ErrorMessage);
        ran.Order().Should().Equal([2, 3, 4], "each target and the step that reads both run exactly once");

        Dictionary<int, Guid> snapshotIds = snapshot.Steps.ToDictionary(s => s.StepOrder, s => s.Id);
        foreach (int order in new[] { 1, 5 })
        {
            WorkflowStepResult row = await db.WorkflowStepResults.SingleAsync(r => r.WorkflowExecutionId == replay.Id && r.WorkflowStepId == snapshotIds[order]);
            row.ReusedFromStepResultId.Should().Be(source[order].Id);
            row.CreditsChargedMillis.Should().Be(0);
        }
    }
}
