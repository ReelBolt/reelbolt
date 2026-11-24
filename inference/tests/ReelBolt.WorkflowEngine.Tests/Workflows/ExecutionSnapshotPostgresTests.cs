using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// The run-snapshot schema on the production engine (<c>inference/tests/run-postgres-tests.sh</c>): both
/// schemas MIGRATED (so the engine migration <c>AddExecutionSnapshots</c> is what is under test, not the
/// model), real foreign keys and cascades. The in-process twins are in <see cref="ExecutionSnapshotTests"/>.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class ExecutionSnapshotPostgresTests
{
    private sealed class Executor(StepType type, Func<StepExecutionContext, Task<StepExecutionResult>> run) : IStepExecutor
    {
        public StepType StepType { get; } = type;
        public Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context) => run(context);
    }

    private sealed class Scopes(WorkflowEngineDbContext db) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }
        public object? GetService(Type serviceType) => serviceType == typeof(WorkflowEngineDbContext) ? db : null;
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

    private static StepExecutionResult Done(StepExecutionContext c) => new()
    {
        Output = "{\"step\":" + c.Step.StepOrder + "}",
        NextStepIndex = c.CurrentStepIndex + 1,
        NewIterationCount = c.IterationCount,
        Status = StepStatus.Completed
    };

    /// <summary>A throwaway database, both schemas migrated, one org/project/agent and a two-step Standard workflow seeded.</summary>
    private sealed class PgDatabase : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _admin;

        private PgDatabase(NpgsqlDataSource admin, string name, string connection)
        {
            _admin = admin;
            Name = name;
            Connection = connection;
        }

        public string Name { get; }
        public string Connection { get; }
        public Guid Org { get; } = Guid.NewGuid();
        public Guid Project { get; } = Guid.NewGuid();
        public Guid Workflow { get; } = Guid.NewGuid();
        public Guid Agent { get; } = Guid.NewGuid();

        public static async Task<PgDatabase> CreateAsync(string baseConnection)
        {
            NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            string name = "reelbolt_snap_test_" + Guid.NewGuid().ToString("N")[..12];
            PgDatabase database = new(admin, name, PostgresTestEnvironment.ForDatabase(baseConnection, name, maxPoolSize: 4));
            try
            {
                await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
                    await create.ExecuteNonQueryAsync();
                await database.MigrateAndSeedAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public WorkflowEngineDbContext Engine() => new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(Connection).Options);

        public InferenceApiDbContext Api() => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(Connection).Options);

        public async Task<Guid> QueueRunAsync()
        {
            Guid id = Guid.NewGuid();
            await using WorkflowEngineDbContext db = Engine();
            db.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = id,
                WorkflowDefinitionId = Workflow,
                ProjectId = Project,
                OrganizationId = Org,
                Status = ExecutionStatus.Queued,
                QueuedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task RunAsync(Guid executionId, Func<StepExecutionContext, Task>? duringStep = null)
        {
            await using WorkflowEngineDbContext db = Engine();
            WorkflowExecutorService service = new(
                scopeFactory: new Scopes(db),
                eventPublisher: new NoOpPublisher(),
                logger: NullLogger<WorkflowExecutorService>.Instance,
                executors:
                [
                    new Executor(StepType.Agent, async c =>
                    {
                        if (duringStep is not null)
                            await duringStep(c);
                        return Done(c);
                    }),
                    new Executor(StepType.ReviewLoop, c => Task.FromResult(Done(c)))
                ],
                rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
                hardeningOptions: Options.Create(new WorkflowHardeningOptions()),
                cancellationRegistry: new ExecutionCancellationRegistry());
            await service.ExecuteAsync(executionId, "corr", CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using (NpgsqlCommand drop = _admin.CreateCommand($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)"))
                await drop.ExecuteNonQueryAsync();
            await _admin.DisposeAsync();
        }

        private async Task MigrateAndSeedAsync()
        {
            await using (InferenceApiDbContext api = Api())
                await api.GetService<IMigrator>().MigrateAsync();
            await using (WorkflowEngineDbContext engine = Engine())
                await engine.Database.MigrateAsync();

            Guid user = Guid.NewGuid();
            await using (NpgsqlConnection c = new(Connection))
            {
                await c.OpenAsync();
                foreach (string sql in new[]
                {
                    $"INSERT INTO application_users (id,email,display_name,created_at,must_change_password,is_admin) VALUES ('{user}','snap@t.local','snap',now(),false,false)",
                    $"INSERT INTO organizations (id,name,slug,kind,created_at,updated_at,compute_preference) VALUES ('{Org}','o','o-{Org:N}','Team',now(),now(),'Auto')",
                    $"INSERT INTO projects (id,name,owner_id,organization_id,status,created_at,updated_at) VALUES ('{Project}','p','{user}','{Org}','Draft',now(),now())",
                })
                {
                    await using NpgsqlCommand cmd = new(sql, c);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            await using (InferenceApiDbContext api = Api())
            {
                api.AgentDefinitions.Add(new AgentDefinition { Id = Agent, Name = "Writer", AgentType = AgentType.Custom, SystemPrompt = "Write tightly.", OrganizationId = Org });
                await api.SaveChangesAsync();
            }

            await using (WorkflowEngineDbContext engine = Engine())
            {
                WorkflowDefinition workflow = new() { Id = Workflow, Name = "Launch reel", ProjectId = Project };
                workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = Workflow, AgentDefinitionId = Agent, StepOrder = 1, StepType = StepType.Agent, Label = "Write" });
                workflow.Steps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = Workflow, AgentDefinitionId = Agent, StepOrder = 2, StepType = StepType.ReviewLoop, Label = "Review", LoopTargetStepOrder = 1, MaxIterations = 3 });
                engine.WorkflowDefinitions.Add(workflow);
                await engine.SaveChangesAsync();
            }
        }
    }

    private static async Task<PgDatabase?> DatabaseAsync()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        return string.IsNullOrWhiteSpace(baseConnection) ? null : await PgDatabase.CreateAsync(baseConnection);
    }

    [PostgresFact]
    public async Task A_put_between_step_1_and_step_2_leaves_the_run_its_original_steps_and_every_result()
    {
        await using PgDatabase database = (await DatabaseAsync())!;
        Guid run = await database.QueueRunAsync();
        List<string?> ran = [];

        await database.RunAsync(run, async c =>
        {
            ran.Add(c.Step.Label);
            if (c.Step.StepOrder != 1)
                return;
            // WorkflowsController.Update: ExecuteDelete the steps, then add the new list.
            await using InferenceApiDbContext api = database.Api();
            await api.WorkflowSteps.Where(s => s.WorkflowDefinitionId == database.Workflow).ExecuteDeleteAsync();
            api.WorkflowSteps.Add(new WorkflowStep { Id = Guid.NewGuid(), WorkflowDefinitionId = database.Workflow, AgentDefinitionId = database.Agent, StepOrder = 1, StepType = StepType.Agent, Label = "Rewritten" });
            await api.SaveChangesAsync();
        });

        await using WorkflowEngineDbContext check = database.Engine();
        WorkflowExecution done = await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == run);
        done.Status.Should().Be(ExecutionStatus.Passed, done.ErrorMessage);
        ran.Should().Equal(["Write"], "only the Agent step goes through this recorder; the review is step 2");
        List<WorkflowStepResult> results = await check.WorkflowStepResults.AsNoTracking().Where(r => r.WorkflowExecutionId == run).ToListAsync();
        results.Should().HaveCount(2, "the PUT must not cascade-delete the run's results");
        results.Should().OnlyContain(r => r.Status == StepStatus.Completed);
        (await check.WorkflowSteps.AsNoTracking().Where(s => s.WorkflowDefinitionId == done.SnapshotDefinitionId).OrderBy(s => s.StepOrder).Select(s => s.Label).ToListAsync())
            .Should().Equal("Write", "Review");
    }

    [PostgresFact]
    public async Task Deleting_a_run_deletes_its_snapshot_and_deleting_the_project_cascades_the_rest()
    {
        await using PgDatabase database = (await DatabaseAsync())!;
        Guid first = await database.QueueRunAsync();
        Guid second = await database.QueueRunAsync();
        await database.RunAsync(first);
        await database.RunAsync(second);

        await using WorkflowEngineDbContext check = database.Engine();
        Guid firstSnapshot = (await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == first)).SnapshotDefinitionId!.Value;
        Guid secondSnapshot = (await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == second)).SnapshotDefinitionId!.Value;

        await check.WorkflowExecutions.Where(e => e.Id == first).ExecuteDeleteAsync();

        (await check.WorkflowDefinitions.AnyAsync(w => w.Id == firstSnapshot)).Should().BeFalse();
        (await check.WorkflowSteps.AnyAsync(s => s.WorkflowDefinitionId == firstSnapshot)).Should().BeFalse();
        (await check.WorkflowDefinitions.AnyAsync(w => w.Id == secondSnapshot)).Should().BeTrue("another run's snapshot is untouched");
        (await check.WorkflowDefinitions.AnyAsync(w => w.Id == database.Workflow)).Should().BeTrue("so is the user's workflow");

        await check.Projects.Where(p => p.Id == database.Project).ExecuteDeleteAsync();

        (await check.WorkflowDefinitions.CountAsync()).Should().Be(0);
        (await check.WorkflowExecutions.CountAsync()).Should().Be(0);
        (await check.WorkflowSteps.CountAsync()).Should().Be(0);
        (await check.WorkflowStepResults.CountAsync()).Should().Be(0);
    }

    [PostgresFact]
    public async Task Deleting_a_snapshot_on_its_own_sets_the_run_pointer_null()
    {
        await using PgDatabase database = (await DatabaseAsync())!;
        Guid run = await database.QueueRunAsync();
        await database.RunAsync(run);

        await using WorkflowEngineDbContext check = database.Engine();
        Guid snapshot = (await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == run)).SnapshotDefinitionId!.Value;

        await check.WorkflowDefinitions.Where(w => w.Id == snapshot).ExecuteDeleteAsync();

        WorkflowExecution after = await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == run);
        after.SnapshotDefinitionId.Should().BeNull("ON DELETE SET NULL");
        after.AgentSnapshotJson.Should().NotBeNull();
    }

    [PostgresFact]
    public async Task The_new_columns_carry_their_defaults_for_a_row_written_without_them()
    {
        await using PgDatabase database = (await DatabaseAsync())!;
        Guid run = Guid.NewGuid();
        await using (NpgsqlConnection c = new(database.Connection))
        {
            await c.OpenAsync();
            await using NpgsqlCommand insert = new(
                $"INSERT INTO workflow_executions (id, workflow_definition_id, project_id, organization_id, status, iteration_count, correlation_id, initiator) " +
                $"VALUES ('{run}', '{database.Workflow}', '{database.Project}', '{database.Org}', 'Passed', 0, 'c', 0)", c);
            await insert.ExecuteNonQueryAsync();
        }

        await using WorkflowEngineDbContext check = database.Engine();
        WorkflowExecution row = await check.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == run);
        row.SnapshotDefinitionId.Should().BeNull();
        row.AgentSnapshotJson.Should().BeNull();
        row.PendingResume.Should().BeFalse();
        row.ResumeCount.Should().Be(0);
        row.AutoResumeCount.Should().Be(0);
    }

    [PostgresFact]
    public async Task A_run_executed_from_its_snapshot_feeds_the_loop_passes_and_run_totals_of_its_source_workflow()
    {
        await using PgDatabase database = (await DatabaseAsync())!;
        Guid run = await database.QueueRunAsync();
        await database.RunAsync(run);

        await using InferenceApiDbContext api = database.Api();
        List<WorkflowStep> source = await api.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == database.Workflow).OrderBy(s => s.StepOrder).ToListAsync();
        WorkflowStep loop = source.Single(s => s.StepType == StepType.ReviewLoop);
        List<WorkflowStepResult> results = await api.WorkflowStepResults.AsNoTracking().Where(r => r.WorkflowExecutionId == run).ToListAsync();
        results.Select(r => r.WorkflowStepId).Should().NotIntersectWith(source.Select(s => s.Id), "results reference the snapshot");
        foreach (WorkflowStepResult result in results)
        {
            api.UsageEvents.Add(new UsageEvent
            {
                Id = Guid.NewGuid(),
                OrganizationId = database.Org,
                ProjectId = database.Project,
                ExecutionId = run,
                StepResultId = result.Id,
                IdempotencyKey = "snap-" + result.Id,
                Source = UsageSource.Workflow,
                Kind = UsageKind.LlmInputTokens,
                Quantity = 1,
                CreditsMillis = 1_000,
                OccurredAt = DateTime.UtcNow
            });
        }
        await api.SaveChangesAsync();
        DateTime since = DateTime.UtcNow.AddDays(-1);

        IReadOnlyDictionary<Guid, IReadOnlyList<int>> passes = await DisplayCreditHistory.LoadLoopPassesAsync(
            api.WorkflowStepResults, api.WorkflowExecutions, api.WorkflowSteps, database.Org, since, [loop], CancellationToken.None);
        IReadOnlyList<long> totals = await DisplayCreditHistory.LoadRunTotalsAsync(
            api.UsageEvents, api.WorkflowStepResults, api.WorkflowExecutions, api.WorkflowSteps, database.Org, since,
            database.Workflow, [], [loop.StepOrder], 10, null, CancellationToken.None);

        passes.Should().ContainKey(loop.Id).WhoseValue.Should().Equal(1);
        totals.Should().Equal([1_000L], "step 2 (excluded by order) leaves step 1's 1 000 in the run total");
    }

    [PostgresFact]
    public async Task A_review_scores_step_result_id_is_set_null_when_its_result_is_deleted()
    {
        await using PgDatabase database = (await DatabaseAsync())!;
        Guid run = await database.QueueRunAsync();
        await database.RunAsync(run);

        await using WorkflowEngineDbContext db = database.Engine();
        WorkflowStepResult result = await db.WorkflowStepResults.AsNoTracking().FirstAsync(r => r.WorkflowExecutionId == run);
        ReviewScore score = new() { Id = Guid.NewGuid(), WorkflowExecutionId = run, IterationNumber = 1, Score = 5, StepResultId = result.Id };
        db.ReviewScores.Add(score);
        await db.SaveChangesAsync();

        await db.WorkflowStepResults.Where(r => r.Id == result.Id).ExecuteDeleteAsync();

        (await db.ReviewScores.AsNoTracking().SingleAsync(x => x.Id == score.Id)).StepResultId
            .Should().BeNull("ON DELETE SET NULL: the score survives as a legacy-shaped row, resolved by timestamp");
    }
}
