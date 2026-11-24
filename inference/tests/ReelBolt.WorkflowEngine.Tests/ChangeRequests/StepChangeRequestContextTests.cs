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
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Execution.Replay;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Tests.Compositions;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.ChangeRequests;

/// <summary>
/// The engine plumbing of a change request: the context that finds it, the replay session that loads its plan, and a
/// replay whose target step is a Voiceover step going down the ordinary executor path.
/// </summary>
public class StepChangeRequestContextTests
{
    private sealed class Setup
    {
        public required WorkflowEngineDbContext Db { get; init; }
        public required Guid ProjectId { get; init; }
        public required WorkflowExecution Replay { get; init; }
        public required WorkflowDefinition Snapshot { get; init; }
        public required WorkflowStep Target { get; init; }
        public required StepChangeRequest Row { get; init; }
        public required Guid SourceVoiceoverResultId { get; init; }
        public required WorkflowStepResult SourceAgentResult { get; init; }
        public required CompositionReplayPlan Plan { get; init; }
    }

    private const string PreviousOutput = "{\"view\":{\"lines\":[{\"id\":\"vo0\",\"status\":\"ok\"}]}}";

    private static async Task<Setup> BuildAsync(
        Action<StepChangeRequest>? tweakRow = null, WorkflowDefinitionKind kind = WorkflowDefinitionKind.CompositionReplay,
        DbContextOptions<WorkflowEngineDbContext>? options = null)
    {
        WorkflowEngineDbContext db = new(options ?? new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseInMemoryDatabase("scr-" + Guid.NewGuid().ToString("N")).Options);
        Guid projectId = Guid.NewGuid();

        List<WorkflowStep> steps =
        [
            ReplayShapes.Step(1, AgentType.Custom),
            ReplayShapes.Step(2, AgentType.Custom, StepType.Voiceover, voiceover: "{}")
        ];
        WorkflowDefinition original = new() { Id = Guid.NewGuid(), Name = "Original", ProjectId = projectId };
        foreach (WorkflowStep s in steps)
            s.WorkflowDefinitionId = original.Id;
        WorkflowExecution sourceRun = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = original.Id, ProjectId = projectId, Status = ExecutionStatus.Passed };
        Dictionary<int, WorkflowStepResult> source = steps.ToDictionary(s => s.StepOrder, s => new WorkflowStepResult
        {
            Id = Guid.NewGuid(),
            WorkflowExecutionId = sourceRun.Id,
            WorkflowStepId = s.Id,
            Status = StepStatus.Completed,
            ExecutedAt = DateTime.UtcNow.AddMinutes(-10 + s.StepOrder),
            Output = s.StepOrder == 2 ? PreviousOutput : "{\"step\":1}"
        });

        CompositionReplayPlan plan = CompositionReplayPlanner.Plan(
            steps, source.ToDictionary(kv => kv.Key, kv => kv.Value.Id), 2, CompositionCutPolicy.KeepCut,
            sourceRun.Id, original.Id, "the narration", "Voiceover");

        WorkflowDefinition snapshot = new() { Id = Guid.NewGuid(), Name = "Change", ProjectId = projectId, Kind = kind };
        foreach (WorkflowStep step in steps)
        {
            WorkflowStep copy = WorkflowStepCloner.Clone(step, snapshot.Id);
            copy.AgentDefinition = step.AgentDefinition;
            if (step.StepOrder == 2)
                copy.CacheMode = StepCacheMode.Never;
            snapshot.Steps.Add(copy);
        }

        WorkflowExecution replay = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = snapshot.Id,
            ProjectId = projectId,
            Status = ExecutionStatus.Queued,
            WorkflowDefinition = snapshot
        };
        StepChangeRequest row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OrganizationId = Guid.NewGuid(),
            SourceExecutionId = sourceRun.Id,
            TargetStepOrder = 2,
            StepType = "Voiceover",
            Instruction = "faster",
            ScopeJson = "[\"vo0\"]",
            OverridesJson = "{\"vo0\":{\"speed\":1.2}}",
            ExecutionId = replay.Id,
            WorkflowDefinitionId = snapshot.Id,
            ReplayPlanJson = plan.Serialize()
        };
        tweakRow?.Invoke(row);

        db.AgentDefinitions.AddRange(steps.Select(s => s.AgentDefinition));
        db.WorkflowDefinitions.AddRange(original, snapshot);
        db.WorkflowSteps.AddRange(steps);
        db.WorkflowExecutions.AddRange(sourceRun, replay);
        db.WorkflowStepResults.AddRange(source.Values);
        db.StepChangeRequests.Add(row);
        await db.SaveChangesAsync();

        return new Setup
        {
            Db = db,
            ProjectId = projectId,
            Replay = replay,
            Snapshot = snapshot,
            Target = snapshot.Steps.Single(s => s.StepOrder == 2),
            Row = row,
            SourceVoiceoverResultId = source[2].Id,
            SourceAgentResult = source[1],
            Plan = plan
        };
    }

    private static StepChangeRequestContext Context(Setup s) => new(new FakeScopeFactory(s.Db));

    [Fact]
    public async Task Finds_the_request_with_its_payload_and_the_previous_output_of_the_target_step()
    {
        Setup s = await BuildAsync();

        StepChangeRequestScope? scope = await Context(s).FindAsync(s.Replay, s.Target, CancellationToken.None);

        scope.Should().NotBeNull();
        scope!.RequestId.Should().Be(s.Row.Id);
        scope.Payload.ItemIds.Should().Equal("vo0");
        scope.Payload.Overrides!["vo0"]["speed"].GetDouble().Should().Be(1.2);
        scope.PreviousOutputJson.Should().Be(PreviousOutput);
    }

    private static async Task<StepChangeRequestScope?> FindAfterQuarantineAsync(int runOffsetHours, int resultOffsetHours)
    {
        DateTime marker = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        Setup s = await BuildAsync();
        s.Db.Projects.Add(new Project { Id = s.ProjectId, Name = "p", ContentQuarantinedAt = marker });
        WorkflowStepResult source = await s.Db.WorkflowStepResults.SingleAsync(r => r.Id == s.SourceVoiceoverResultId);
        source.ExecutedAt = marker.AddHours(resultOffsetHours);
        source.ArtifactStorageKey = $"projects/{s.ProjectId}/agentFiles/generated/manifest.json";
        WorkflowExecution sourceRun = await s.Db.WorkflowExecutions.SingleAsync(e => e.Id == source.WorkflowExecutionId);
        sourceRun.FirstStartedAt = marker.AddHours(runOffsetHours);
        await s.Db.SaveChangesAsync();
        return await Context(s).FindAsync(s.Replay, s.Target, CancellationToken.None);
    }

    [Theory]
    [InlineData(-1, -1)]
    [InlineData(1, -1)] // Retry overwrote the run's start; the output is still the old one.
    [InlineData(-1, 1)] // Resume kept the run's start.
    public async Task A_source_that_may_predate_a_quarantine_carries_nothing_over(int runOffsetHours, int resultOffsetHours)
    {
        StepChangeRequestScope? scope = await FindAfterQuarantineAsync(runOffsetHours, resultOffsetHours);

        scope.Should().NotBeNull("the request itself still applies");
        scope!.PreviousOutputJson.Should().BeNull();
        scope.ArtifactStorageKey.Should().BeNull();
    }

    [Fact]
    public async Task A_source_made_after_the_quarantine_is_carried_over()
    {
        StepChangeRequestScope? scope = await FindAfterQuarantineAsync(1, 1);

        scope!.PreviousOutputJson.Should().Be(PreviousOutput);
        scope.ArtifactStorageKey.Should().NotBeNull();
    }

    [Fact]
    public async Task A_quarantine_state_that_cannot_be_read_carries_nothing_over()
    {
        var failure = new FailProjectFileReads();
        Setup s = await BuildAsync(options: FailProjectFileReads.Options("scr-" + Guid.NewGuid().ToString("N"), failure));
        failure.ArmFor(s.Db);

        StepChangeRequestScope? scope = await Context(s).FindAsync(s.Replay, s.Target, CancellationToken.None);

        scope!.PreviousOutputJson.Should().BeNull();
    }

    [Fact]
    public async Task A_step_that_is_not_the_target_gets_no_request()
    {
        Setup s = await BuildAsync();
        WorkflowStep other = s.Snapshot.Steps.Single(x => x.StepOrder == 1);

        (await Context(s).FindAsync(s.Replay, other, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_row_of_another_project_never_applies()
    {
        Setup s = await BuildAsync(r => r.ProjectId = Guid.NewGuid());

        (await Context(s).FindAsync(s.Replay, s.Target, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task A_corrupt_stored_scope_or_overrides_json_is_flagged_malformed_not_whole_step()
    {
        foreach (Action<StepChangeRequest> tweak in new Action<StepChangeRequest>[]
        {
            r => r.ScopeJson = "[\"vo0\"",
            r => r.OverridesJson = "{\"vo0\":{\"speed\":",
            r => r.OverridesJson = "[1,2]",
        })
        {
            Setup s = await BuildAsync(tweak);

            StepChangeRequestScope? scope = await Context(s).FindAsync(s.Replay, s.Target, CancellationToken.None);

            scope.Should().NotBeNull();
            scope!.Malformed.Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_well_formed_request_is_not_flagged_malformed()
    {
        Setup s = await BuildAsync();

        (await Context(s).FindAsync(s.Replay, s.Target, CancellationToken.None))!.Malformed.Should().BeFalse();
    }

    [Fact]
    public async Task An_ordinary_run_never_gets_a_request_even_with_a_row_pointing_at_it()
    {
        Setup s = await BuildAsync(kind: WorkflowDefinitionKind.Standard);

        (await Context(s).FindAsync(s.Replay, s.Target, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task An_execution_with_no_row_gets_no_request()
    {
        Setup s = await BuildAsync();
        s.Db.StepChangeRequests.RemoveRange(s.Db.StepChangeRequests);
        await s.Db.SaveChangesAsync();

        (await Context(s).FindAsync(s.Replay, s.Target, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task The_previous_output_is_read_project_scoped()
    {
        Setup s = await BuildAsync();
        WorkflowStepResult foreign = await s.Db.WorkflowStepResults.SingleAsync(r => r.Id == s.SourceVoiceoverResultId);
        WorkflowExecution run = await s.Db.WorkflowExecutions.SingleAsync(e => e.Id == foreign.WorkflowExecutionId);
        run.ProjectId = Guid.NewGuid();
        await s.Db.SaveChangesAsync();

        StepChangeRequestScope? scope = await Context(s).FindAsync(s.Replay, s.Target, CancellationToken.None);

        scope.Should().NotBeNull();
        scope!.PreviousOutputJson.Should().BeNull("a result of another project is never read");
    }

    [Fact]
    public async Task The_replay_session_loads_its_plan_from_a_change_request()
    {
        Setup s = await BuildAsync();

        CompositionReplaySession? session = await CompositionReplaySession.LoadAsync(
            s.Db, s.Replay, [.. s.Snapshot.Steps], CancellationToken.None);

        session.Should().NotBeNull();
        session!.Plan.RevisedStepOrder.Should().Be(2);
        session.Decide(s.Snapshot.Steps.Single(x => x.StepOrder == 1)).Kind.Should().Be(ReplayActionKind.Reuse);
        session.Decide(s.Target).Kind.Should().Be(ReplayActionKind.Execute);
    }

    [Fact]
    public async Task A_change_request_row_of_another_project_leaves_the_session_without_a_plan()
    {
        Setup s = await BuildAsync(r => r.ProjectId = Guid.NewGuid());

        Func<Task> act = () => CompositionReplaySession.LoadAsync(s.Db, s.Replay, [.. s.Snapshot.Steps], CancellationToken.None);

        (await act.Should().ThrowAsync<CompositionReplayException>()).Which.Code.Should().Be(CompositionReplaySession.PlanMissing);
    }

    [Fact]
    public async Task A_replay_with_neither_a_revision_nor_a_request_still_has_no_plan()
    {
        Setup s = await BuildAsync();
        s.Db.StepChangeRequests.RemoveRange(s.Db.StepChangeRequests);
        await s.Db.SaveChangesAsync();

        Func<Task> act = () => CompositionReplaySession.LoadAsync(s.Db, s.Replay, [.. s.Snapshot.Steps], CancellationToken.None);

        (await act.Should().ThrowAsync<CompositionReplayException>()).Which.Code.Should().Be(CompositionReplaySession.PlanMissing);
    }

    [Fact]
    public async Task A_voiceover_target_goes_down_the_ordinary_executor_path_and_its_step_cache_mode_is_never()
    {
        Setup s = await BuildAsync();
        int voiceoverCalls = 0;
        int agentCalls = 0;
        int reviseCalls = 0;
        StepExecutionContext? seen = null;
        IStepExecutor[] executors =
        [
            new Stub(StepType.Voiceover, c => { voiceoverCalls++; seen = c; return Done(c, "{\"view\":{\"lines\":[]}}"); }),
            new Stub(StepType.Agent, c => { agentCalls++; return Done(c, "{}"); }),
            new Stub(StepType.Revise, c => { reviseCalls++; return Done(c, "{}"); })
        ];
        WorkflowExecutorService service = new(
            scopeFactory: new FakeScopeFactory(s.Db),
            eventPublisher: new NoOpPublisher(),
            logger: NullLogger<WorkflowExecutorService>.Instance,
            executors: executors,
            rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
            hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
            cancellationRegistry: new ExecutionCancellationRegistry());

        await service.ExecuteAsync(s.Replay.Id, "corr", CancellationToken.None);

        s.Plan.For(2)!.Disposition.Should().Be(ReplayStepDisposition.Revise, "the target step is the plan's Revise step");
        voiceoverCalls.Should().Be(1, "the Voiceover executor runs the target step itself");
        reviseCalls.Should().Be(0, "no Revise executor is involved for a non-film target");
        agentCalls.Should().Be(0, "step 1 is a copied Reuse row");
        seen!.Step.CacheMode.Should().Be(StepCacheMode.Never);
        new StepCachePolicy().IsCacheable(seen.Step).Should().BeFalse("CacheMode.Never means no step-cache read and no write");
        (await s.Db.WorkflowExecutions.SingleAsync(e => e.Id == s.Replay.Id)).Status.Should().Be(ExecutionStatus.Passed);
    }

    private sealed class Stub(StepType type, Func<StepExecutionContext, StepExecutionResult> run) : IStepExecutor
    {
        public StepType StepType { get; } = type;
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) => Task.FromResult(run(context));
    }

    private static StepExecutionResult Done(StepExecutionContext c, string output) => new()
    {
        Output = output,
        NextStepIndex = c.CurrentStepIndex + 1,
        NewIterationCount = c.IterationCount,
        Status = StepStatus.Completed
    };

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
}
