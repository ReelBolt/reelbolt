using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Messaging;
using ReelBolt.WorkflowEngine.Services.VideoGeneration;
using Microsoft.Extensions.Options;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Workflows.ExecutionResumeTests;

namespace ReelBolt.WorkflowEngine.Tests.Workflows;

/// <summary>
/// Security SQFU-S1 and SQFU-S4 on the real schema. S1: an engine that stalls past its lease while the sweeper
/// auto-resumes the run on another engine is refused at its next ownership check, never settles, reserves or
/// dispatches again, and never writes a terminal status over the new owner's run; every step is charged once.
/// S4: the Queued→Running claim is a conditional UPDATE, so a duplicate delivery that read the row as Queued
/// cannot start the run a second time.
/// </summary>
[Collection("Postgres")]
public class ExecutionLeaseOwnershipPostgresTests
{
    /// <summary>One fresh DbContext per scope, plus this engine instance's lease service.</summary>
    private sealed class EngineScopes(Func<WorkflowEngineDbContext> newDb, ExecutionLeaseService? leases) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(newDb(), leases);

        private sealed class Scope(WorkflowEngineDbContext db, ExecutionLeaseService? leases) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public void Dispose() => db.Dispose();

            public object? GetService(Type serviceType)
                => serviceType == typeof(WorkflowEngineDbContext) ? db
                : serviceType == typeof(ExecutionLeaseService) ? leases
                : null;
        }
    }

    private static ExecutionLeaseService Leases(Func<WorkflowEngineDbContext> newDb, string instance, ExecutionCancellationRegistry? registry) =>
        new(new Scopes(newDb),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = instance }).Build(),
            NullLogger<ExecutionLeaseService>.Instance,
            cancellations: registry);

    private static async Task<(string Connection, Func<Task> Drop)> CreateDatabaseAsync(string prefix)
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = prefix + Guid.NewGuid().ToString("N")[..12];
        NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        string connection = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
        await using (InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options))
        {
            await api.GetService<IMigrator>().MigrateAsync();
        }

        await using (WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options))
        {
            await engine.GetService<IMigrator>().MigrateAsync();
        }

        return (connection, async () =>
        {
            NpgsqlConnection.ClearAllPools();
            await using (NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)"))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await admin.DisposeAsync();
        }
        );
    }

    private static async Task<Seeded> SeedAsync(string connection, params StepSpec[] steps)
    {
        Guid orgId = Guid.NewGuid();
        await using (NpgsqlConnection c = new(connection))
        {
            await c.OpenAsync();
            await using NpgsqlCommand org = new(
                $"INSERT INTO organizations (id,name,slug,kind,created_at,updated_at,compute_preference) VALUES ('{orgId}','o','o-{orgId:N}','Team',now(),now(),'Auto')", c);
            await org.ExecuteNonQueryAsync();
        }

        await using InferenceApiDbContext seed = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options);
        return SeedIn(seed, orgId, steps);
    }

    private static async Task ExecSqlAsync(string connection, string sql)
    {
        await using NpgsqlConnection c = new(connection);
        await c.OpenAsync();
        await using NpgsqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_a_stalled_engine_is_refused_after_the_sweeper_hands_its_run_to_another_engine_and_every_step_is_charged_once()
    {
        (string connection, Func<Task> drop) = await CreateDatabaseAsync("reelbolt_leaselost_test_");
        try
        {
            DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;
            WorkflowEngineDbContext NewDb() => new(options);
            Seeded s = await SeedAsync(connection,
                new StepSpec(StepType.Agent, "S1"), new StepSpec(StepType.Agent, "S2"), new StepSpec(StepType.Agent, "S3"));

            // One shared credit gate: every reserve/settle/release of both engines lands in one record.
            RecordingGate gate = new();
            ConcurrentQueue<string> aRan = new(), bRan = new();

            // Engine A: step 2 stalls (a GC pause, a hung socket) past its lease.
            ExecutionCancellationRegistry registryA = new();
            ExecutionLeaseService leasesA = Leases(NewDb, "engine-a", registryA);
            TaskCompletionSource inStep2 = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource unstall = new(TaskCreationOptions.RunContinuationsAsynchronously);
            WorkflowExecutorService engineA = Service(new EngineScopes(NewDb, leasesA), gate, null, registryA, new Executor(StepType.Agent, async c =>
            {
                aRan.Enqueue(c.Step.Label!);
                if (c.Step.Label == "S2")
                {
                    inStep2.SetResult();
                    await unstall.Task;
                }

                return Done(c, "{\"by\":\"a\"}");
            }));
            Task runA = engineA.ExecuteAsync(s.ExecutionId, "corr-a", CancellationToken.None);
            await inStep2.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // The stall outlives the lease; another replica's sweeper auto-resumes the run.
            await ExecSqlAsync(connection, $"UPDATE workflow_executions SET lease_expires_at = now() - interval '1 minute' WHERE id = '{s.ExecutionId}'");
            ExecutionRecoverySweeper sweeper = new(new Scopes(NewDb), Leases(NewDb, "engine-sweeper", null), NullLogger<ExecutionRecoverySweeper>.Instance);
            (await sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(0, "the run is auto-resumed, not failed");
            await using (WorkflowEngineDbContext db = NewDb())
            {
                WorkflowExecution queued = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId);
                queued.Status.Should().Be(ExecutionStatus.Queued);
                queued.PendingResume.Should().BeTrue();
            }

            // Engine B claims the resume and finishes the run.
            ExecutionCancellationRegistry registryB = new();
            ExecutionLeaseService leasesB = Leases(NewDb, "engine-b", registryB);
            WorkflowExecutorService engineB = Service(new EngineScopes(NewDb, leasesB), gate, null, registryB, new Executor(StepType.Agent, c =>
            {
                bRan.Enqueue(c.Step.Label!);
                return Task.FromResult(Done(c, "{\"by\":\"b\"}"));
            }));
            await engineB.ExecuteAsync(s.ExecutionId, "corr-b", CancellationToken.None);

            // Engine A wakes up: its next ownership check refuses it.
            unstall.SetResult();
            await runA.WaitAsync(TimeSpan.FromSeconds(30));

            aRan.Should().Equal(new[] { "S1", "S2" }, "A never dispatches step 3 of a run it no longer owns");
            bRan.Should().Equal(new[] { "S2", "S3" }, "B reuses step 1 and runs the rest live");
            leasesA.IsLost(s.ExecutionId).Should().BeTrue();
            registryA.Contains(s.ExecutionId).Should().BeFalse();

            await using (WorkflowEngineDbContext db = NewDb())
            {
                WorkflowExecution run = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId);
                run.Status.Should().Be(ExecutionStatus.Passed, "A never writes a terminal status over B's run");
                run.ErrorMessage.Should().BeNull();
                run.LeaseOwner.Should().Be("engine-b");

                Dictionary<Guid, string> labelOf = await db.WorkflowStepResults.AsNoTracking()
                    .Where(r => r.WorkflowExecutionId == s.ExecutionId)
                    .Join(db.WorkflowSteps, r => r.WorkflowStepId, st => st.Id, (r, st) => new { r.Id, st.Label })
                    .ToDictionaryAsync(x => x.Id, x => x.Label!);

                List<string> settled = gate.Settled.Select(id => labelOf[id]).OrderBy(l => l).ToList();
                settled.Should().Equal(new[] { "S1", "S2", "S3" }, "each step is charged exactly once across both engines");
                gate.Released.Should().ContainSingle("A's step-2 hold is given back, never settled")
                    .Which.Should().Be((gate.Reserved.First(id => labelOf[id] == "S2"), FailureClass.PlatformInternal));
                gate.Reserved.Select(id => labelOf[id]).Should().Equal(new[] { "S1", "S2", "S2", "S3" });
            }
        }
        finally
        {
            await drop();
        }
    }

    /// <summary>Lets another engine claim the row between this context's read of it as Queued and its own claim.</summary>
    private sealed class RaceTheClaim(string connection, Guid executionId) : DbCommandInterceptor
    {
        private int _fired;

        private async Task MaybeRaceAsync(DbCommand command)
        {
            // The claim (or, before SQFU-S4, the plain save that moved the row to Running): the first write of the
            // run's status. Npgsql leaves lower-case identifiers unquoted.
            if (command.CommandText.Contains("UPDATE", StringComparison.Ordinal)
                && command.CommandText.Contains("workflow_executions", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.IsMatch(command.CommandText, @"\bstatus\b")
                && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await ExecSqlAsync(connection,
                    $"UPDATE workflow_executions SET status = 'Running', lease_owner = 'engine-first', lease_expires_at = now() + interval '1 minute', started_at = now() WHERE id = '{executionId}'");
            }
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await MaybeRaceAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await MaybeRaceAsync(command);
            return result;
        }
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_a_duplicate_delivery_that_read_the_row_as_Queued_does_not_start_the_run_again()
    {
        (string connection, Func<Task> drop) = await CreateDatabaseAsync("reelbolt_claim_test_");
        try
        {
            Seeded s = await SeedAsync(connection, new StepSpec(StepType.Agent, "S1"));
            RaceTheClaim race = new(connection, s.ExecutionId);
            DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseNpgsql(connection).AddInterceptors(race).Options;
            WorkflowEngineDbContext NewDb() => new(options);

            int ran = 0;
            ExecutionLeaseService leases = Leases(NewDb, "engine-second", null);
            WorkflowExecutorService second = Service(new EngineScopes(NewDb, leases), null, null, null, new Executor(StepType.Agent, c =>
            {
                Interlocked.Increment(ref ran);
                return Task.FromResult(Done(c, "{}"));
            }));

            await second.ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);

            ran.Should().Be(0, "the other delivery claimed the row first");
            leases.TrackedIds.Should().NotContain(s.ExecutionId);
            await using WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options);
            WorkflowExecution row = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId);
            row.Status.Should().Be(ExecutionStatus.Running);
            row.LeaseOwner.Should().Be("engine-first", "the losing delivery writes nothing over the winner's claim");
            (await db.WorkflowStepResults.CountAsync(r => r.WorkflowExecutionId == s.ExecutionId)).Should().Be(0);
        }
        finally
        {
            await drop();
        }
    }

    /// <summary>Npgsql contexts whose first save that abandons a ledger row first lets a zombie attempt write that row.</summary>
    private sealed class ZombieDuringAbandon(string connection, Guid jobId) : SaveChangesInterceptor, IDbContextFactory<WorkflowEngineDbContext>
    {
        private int _fired;

        public WorkflowEngineDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).AddInterceptors(this).Options);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            bool abandons = eventData.Context!.ChangeTracker.Entries<ExternalGenerationJob>()
                .Any(e => e.State == EntityState.Modified && e.Entity.Status == ExternalGenerationJobStatus.Abandoned);
            if (abandons && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await ExecSqlAsync(connection,
                    $"UPDATE external_generation_jobs SET status = 'Submitted', remote_job_id = 'r-zombie' WHERE id = '{jobId}'");
            }

            return result;
        }
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_a_ledger_row_changed_under_the_gates_abandon_is_refused_and_reserves_nothing()
    {
        (string connection, Func<Task> drop) = await CreateDatabaseAsync("reelbolt_gateconflict_test_");
        try
        {
            DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;
            Seeded s = await SeedAsync(connection, new StepSpec(StepType.VideoGenerate, "V"));
            Guid earlier = Guid.NewGuid(), current = Guid.NewGuid(), orphan = Guid.NewGuid();
            const string Hash = "hash-1";
            await using (WorkflowEngineDbContext db = new(options))
            {
                WorkflowExecution run = await db.WorkflowExecutions.SingleAsync(e => e.Id == s.ExecutionId);
                run.Status = ExecutionStatus.Running;
                run.ResumeCount = 1;
                Guid stepId = await db.WorkflowSteps.Where(st => st.WorkflowDefinitionId == run.WorkflowDefinitionId).Select(st => st.Id).SingleAsync();
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = earlier,
                    WorkflowExecutionId = run.Id,
                    WorkflowStepId = stepId,
                    Status = StepStatus.Failed,
                    Output = "",
                    ExecutedAt = DateTime.UtcNow.AddMinutes(-30)
                });
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = current,
                    WorkflowExecutionId = run.Id,
                    WorkflowStepId = stepId,
                    Status = StepStatus.Running,
                    Output = "",
                    ExecutedAt = DateTime.UtcNow
                });
                db.ExternalGenerationJobs.Add(new ExternalGenerationJob
                {
                    Id = orphan,
                    ExecutionId = run.Id,
                    StepResultId = earlier,
                    ProjectId = s.ProjectId,
                    OrganizationId = s.OrganizationId,
                    ClipId = "clip-1",
                    RequestHash = Hash,
                    Status = ExternalGenerationJobStatus.Reserved,
                    CostEstimate = 80m,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-20),
                    UpdatedAt = DateTime.UtcNow.AddMinutes(-20)
                });
                await db.SaveChangesAsync();
            }

            VideoGenerationBudgetGate gate = new(new ZombieDuringAbandon(connection, orphan), Options.Create(new VideoGenerationOptions
            {
                DailyBudgetCredits = 1_000_000m,
                GlobalDailyBudgetCredits = 1_000_000m,
                MaxAbandonsPerTake = 1
            }));

            VideoGenerationReservation reservation = await gate.ReserveAsync(
                new VideoGenerationReservationRequest(s.ProjectId, s.ExecutionId, current, Guid.NewGuid(), 100_000m,
                    [new VideoGenerationTakeRequest("clip-1", Hash, 80m)]),
                CancellationToken.None);

            reservation.Allowed.Should().BeFalse();
            reservation.DenyReason.Should().StartWith("RESERVATION_ORPHANED:");
            await using (WorkflowEngineDbContext db = new(options))
            {
                List<ExternalGenerationJob> rows = await db.ExternalGenerationJobs.AsNoTracking().Where(j => j.ExecutionId == s.ExecutionId).ToListAsync();
                rows.Should().ContainSingle("the transaction rolled back: no fresh row was reserved");
                rows[0].Status.Should().Be(ExternalGenerationJobStatus.Submitted, "the zombie's write stands");
                rows[0].RemoteJobId.Should().Be("r-zombie");
            }
        }
        finally
        {
            await drop();
        }
    }

    /// <summary>
    /// Pauses this delivery right after it read the row as Queued (its first command after loading the run) and lets
    /// another delivery run the whole run in that gap: the two first deliveries of residual (b), made deterministic.
    /// </summary>
    private sealed class OtherDeliveryRunsAfterTheRead(Func<Task> otherDelivery) : DbCommandInterceptor
    {
        private int _sawLoad;
        private int _fired;

        private async Task MaybeRunOtherAsync()
        {
            if (Volatile.Read(ref _sawLoad) == 1 && Interlocked.Exchange(ref _fired, 1) == 0)
            {
                await otherDelivery();
            }
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            await MaybeRunOtherAsync();
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            await MaybeRunOtherAsync();
            if (command.CommandText.Contains("FROM workflow_executions", StringComparison.Ordinal))
            {
                Interlocked.CompareExchange(ref _sawLoad, 1, 0);
            }

            return result;
        }
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_two_first_deliveries_leave_one_snapshot_and_the_loser_writes_nothing()
    {
        (string connection, Func<Task> drop) = await CreateDatabaseAsync("reelbolt_snapshotrace_test_");
        try
        {
            DbContextOptions<WorkflowEngineDbContext> plain = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;
            WorkflowEngineDbContext PlainDb() => new(plain);
            Seeded s = await SeedAsync(connection, new StepSpec(StepType.Agent, "S1"), new StepSpec(StepType.Agent, "S2"));

            int winnerRan = 0, loserRan = 0;
            WorkflowExecutorService winner = Service(new EngineScopes(PlainDb, Leases(PlainDb, "engine-w", null)), null, null, null,
                new Executor(StepType.Agent, c =>
                {
                    Interlocked.Increment(ref winnerRan);
                    return Task.FromResult(Done(c, "{}"));
                }));

            OtherDeliveryRunsAfterTheRead gap = new(() => winner.ExecuteAsync(s.ExecutionId, "corr-w", CancellationToken.None));
            DbContextOptions<WorkflowEngineDbContext> intercepted = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseNpgsql(connection).AddInterceptors(gap).Options;
            WorkflowExecutorService loser = Service(new EngineScopes(() => new WorkflowEngineDbContext(intercepted), Leases(PlainDb, "engine-l", null)),
                null, null, null,
                new Executor(StepType.Agent, c =>
                {
                    Interlocked.Increment(ref loserRan);
                    return Task.FromResult(Done(c, "{}"));
                }));

            await loser.ExecuteAsync(s.ExecutionId, "corr-l", CancellationToken.None);

            winnerRan.Should().Be(2, "the winner ran the whole run inside the loser's gap");
            loserRan.Should().Be(0);
            await using WorkflowEngineDbContext db = PlainDb();
            List<Guid> snapshots = await db.WorkflowDefinitions.AsNoTracking()
                .Where(d => d.Kind == WorkflowDefinitionKind.ExecutionSnapshot
                    && EF.Property<Guid?>(d, WorkflowEngineDbContext.SnapshotOfExecutionIdColumn) == s.ExecutionId)
                .Select(d => d.Id)
                .ToListAsync();
            Guid snapshot = snapshots.Should().ContainSingle("the losing delivery never snapshots").Subject;
            WorkflowExecution run = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId);
            run.SnapshotDefinitionId.Should().Be(snapshot, "the loser never re-points the run");
            run.Status.Should().Be(ExecutionStatus.Passed);
            run.LeaseOwner.Should().Be("engine-w");
            List<Guid> resultSteps = await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.WorkflowExecutionId == s.ExecutionId).Select(r => r.WorkflowStepId).ToListAsync();
            List<Guid> snapshotSteps = await db.WorkflowSteps.AsNoTracking()
                .Where(st => st.WorkflowDefinitionId == snapshot).Select(st => st.Id).ToListAsync();
            resultSteps.Should().HaveCount(2).And.OnlyContain(id => snapshotSteps.Contains(id), "every result belongs to the run's one snapshot");
        }
        finally
        {
            await drop();
        }
    }

    /// <summary>Fails the snapshot: throws on the insert of the kind-5 definition.</summary>
    private sealed class FailTheSnapshot : DbCommandInterceptor
    {
        private static void MaybeThrow(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT INTO workflow_definitions", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("snapshot store unavailable");
            }
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            MaybeThrow(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            MaybeThrow(command);
            return ValueTask.FromResult(result);
        }
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_a_snapshot_that_fails_after_the_claim_ends_the_run_Failed_as_a_platform_failure()
    {
        (string connection, Func<Task> drop) = await CreateDatabaseAsync("reelbolt_snapshotfail_test_");
        try
        {
            DbContextOptions<WorkflowEngineDbContext> plain = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;
            WorkflowEngineDbContext PlainDb() => new(plain);
            DbContextOptions<WorkflowEngineDbContext> failing = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseNpgsql(connection).AddInterceptors(new FailTheSnapshot()).Options;
            Seeded s = await SeedAsync(connection, new StepSpec(StepType.Agent, "S1"));

            int ran = 0;
            ConcurrentQueue<Guid> failedEvents = new();
            Moq.Mock<IWorkflowEventPublisher> publisher = new();
            publisher.Setup(p => p.PublishExecutionFailedAsync(Moq.It.IsAny<WorkflowExecution>(), Moq.It.IsAny<CancellationToken>()))
                .Callback<WorkflowExecution, CancellationToken>((e, _) => failedEvents.Enqueue(e.Id))
                .Returns(Task.CompletedTask);
            WorkflowExecutorService service = Service(
                new EngineScopes(() => new WorkflowEngineDbContext(failing), Leases(PlainDb, "engine-a", null)), null, publisher.Object, null,
                new Executor(StepType.Agent, c =>
                {
                    Interlocked.Increment(ref ran);
                    return Task.FromResult(Done(c, "{}"));
                }));

            Func<Task> run = () => service.ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
            await run.Should().ThrowAsync<Exception>();

            ran.Should().Be(0);
            failedEvents.Should().Equal(s.ExecutionId);
            publisher.Verify(p => p.PublishExecutionRunningAsync(Moq.It.IsAny<WorkflowExecution>(), Moq.It.IsAny<CancellationToken>()), Moq.Times.Never,
                "a run that never got its snapshot never reported Running");
            await using WorkflowEngineDbContext db = PlainDb();
            WorkflowExecution row = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId);
            row.Status.Should().Be(ExecutionStatus.Failed, "a claimed run is never left Running without a snapshot");
            row.ErrorMessage.Should().Be(WorkflowExecutorService.SnapshotFailedMessage);
            row.ErrorMessage!.ToLowerInvariant().Should().NotContain("user");
            row.CompletedAt.Should().NotBeNull();
            row.SnapshotDefinitionId.Should().BeNull();
            (await db.WorkflowDefinitions.CountAsync(d => d.Kind == WorkflowDefinitionKind.ExecutionSnapshot)).Should().Be(0);
            (await db.WorkflowStepResults.CountAsync(r => r.WorkflowExecutionId == s.ExecutionId)).Should().Be(0);
        }
        finally
        {
            await drop();
        }
    }

    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_a_retried_archived_run_keeps_its_archive_pointer_until_its_new_snapshot_commits()
    {
        (string connection, Func<Task> drop) = await CreateDatabaseAsync("reelbolt_archiveptr_test_");
        try
        {
            DbContextOptions<WorkflowEngineDbContext> plain = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;
            WorkflowEngineDbContext PlainDb() => new(plain);
            DbContextOptions<WorkflowEngineDbContext> failing = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseNpgsql(connection).AddInterceptors(new FailTheSnapshot()).Options;
            Seeded s = await SeedAsync(connection, new StepSpec(StepType.Agent, "S1"));

            // A legacy run whose edited steps T-002 archived: it points at a kind-5 row owned by no run.
            Guid archive = Guid.NewGuid();
            await using (WorkflowEngineDbContext db = PlainDb())
            {
                db.WorkflowDefinitions.Add(new WorkflowDefinition
                {
                    Id = archive,
                    Name = "archive",
                    ProjectId = s.ProjectId,
                    Kind = WorkflowDefinitionKind.ExecutionSnapshot
                });
                await db.SaveChangesAsync();
                WorkflowExecution run = await db.WorkflowExecutions.SingleAsync(e => e.Id == s.ExecutionId);
                run.SnapshotDefinitionId = archive;
                await db.SaveChangesAsync();
            }

            int ran = 0;
            Executor executor = new(StepType.Agent, c =>
            {
                Interlocked.Increment(ref ran);
                return Task.FromResult(Done(c, "{}"));
            });

            // The retry's new snapshot fails: the run still points at its archive.
            WorkflowExecutorService failingRetry = Service(
                new EngineScopes(() => new WorkflowEngineDbContext(failing), Leases(PlainDb, "engine-a", null)), null, null, null, executor);
            Func<Task> first = () => failingRetry.ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
            await first.Should().ThrowAsync<Exception>();
            await using (WorkflowEngineDbContext db = PlainDb())
            {
                WorkflowExecution row = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId);
                row.Status.Should().Be(ExecutionStatus.Failed);
                row.SnapshotDefinitionId.Should().Be(archive, "the archive pointer is kept until a new snapshot commits");
            }

            ran.Should().Be(0);

            // Retried again with a working store: the pointer moves to the new engine-taken snapshot, the archive survives.
            await RequeueAsync(PlainDb, s.ExecutionId, resume: false);
            WorkflowExecutorService retry = Service(new EngineScopes(PlainDb, Leases(PlainDb, "engine-a", null)), null, null, null, executor);
            await retry.ExecuteAsync(s.ExecutionId, "corr", CancellationToken.None);
            await using (WorkflowEngineDbContext db = PlainDb())
            {
                WorkflowExecution row = await db.WorkflowExecutions.AsNoTracking().SingleAsync(e => e.Id == s.ExecutionId);
                row.Status.Should().Be(ExecutionStatus.Passed);
                row.SnapshotDefinitionId.Should().NotBe(archive).And.NotBeNull();
                (await ResumeCursor.IsEngineTakenSnapshotAsync(db, s.ExecutionId, row.SnapshotDefinitionId!.Value, CancellationToken.None))
                    .Should().BeTrue();
                (await db.WorkflowDefinitions.AnyAsync(d => d.Id == archive)).Should().BeTrue("the archive's results keep their steps");
            }

            ran.Should().Be(1);
        }
        finally
        {
            await drop();
        }
    }
}
