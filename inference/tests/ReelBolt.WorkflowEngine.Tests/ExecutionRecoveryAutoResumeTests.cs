using AwesomeAssertions;
using MassTransit;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Auto-resume: a run lost through a lapsed lease is re-queued as a resume once, unless the lost work had paid
/// VideoGenerate work in flight (an <c>external_generation_jobs</c> row still <c>Reserved</c>/<c>Submitted</c>),
/// the run has no engine-taken snapshot (legacy or archive) or it was already auto-resumed. Those are failed
/// exactly as before and wait for a manual Resume. Holds are released in every case.
/// </summary>
public class ExecutionRecoveryAutoResumeTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness : IDisposable
    {
        public SqliteConnection Connection { get; }
        public ServiceProvider Services { get; }
        public FakeTime Time { get; } = new();
        public Mock<IWorkflowEventPublisher> Publisher { get; } = new();
        public Mock<IPlatformFailureHook> Hook { get; } = new();
        public Mock<IPublishEndpoint> Publish { get; } = new();
        public ExecutionLeaseService Leases { get; }
        public ExecutionRecoverySweeper Sweeper { get; }

        public Harness()
        {
            Connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
            Connection.Open();

            ServiceCollection services = new();
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseSqlite(Connection));
            services.AddSingleton(Publisher.Object);
            services.AddSingleton(Hook.Object);
            services.AddSingleton(Publish.Object);
            Services = services.BuildServiceProvider();

            using (IServiceScope scope = Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>().Database.EnsureCreated();
            }

            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = "engine-a" })
                .Build();
            IServiceScopeFactory factory = Services.GetRequiredService<IServiceScopeFactory>();
            Leases = new ExecutionLeaseService(factory, config, NullLogger<ExecutionLeaseService>.Instance, Time);
            Sweeper = new ExecutionRecoverySweeper(factory, Leases, NullLogger<ExecutionRecoverySweeper>.Instance, Time);
        }

        public DateTime Now => Time.GetUtcNow().UtcDateTime;

        public WorkflowEngineDbContext NewDb() =>
            Services.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        /// <summary>A Running row whose lease lapsed 5 s ago. <paramref name="snapshot"/>: engine, archive or none.</summary>
        public Guid AddLostRun(string snapshot = "engine", int autoResumeCount = 0, int resumeCount = 0)
        {
            using WorkflowEngineDbContext db = NewDb();
            WorkflowExecution e = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid(),
                Status = ExecutionStatus.Running,
                CorrelationId = "c-" + Guid.NewGuid().ToString("N")[..6],
                StartedAt = Now.AddMinutes(-20),
                QueuedAt = Now.AddMinutes(-21),
                LeaseOwner = "engine-dead",
                LeaseExpiresAt = Now.AddSeconds(-5),
                AutoResumeCount = autoResumeCount,
                ResumeCount = resumeCount
            };
            db.WorkflowExecutions.Add(e);
            // Two saves, as the engine does: the run and its snapshot reference each other.
            db.SaveChanges();
            if (snapshot != "none")
            {
                WorkflowDefinition def = new()
                {
                    Id = Guid.NewGuid(),
                    Name = "snapshot",
                    ProjectId = e.ProjectId,
                    Kind = WorkflowDefinitionKind.ExecutionSnapshot
                };
                db.WorkflowDefinitions.Add(def);
                // An archive (T-002) is a kind-5 row owned by no run; an engine-taken snapshot is owned by this run.
                db.Entry(def).Property<Guid?>(WorkflowEngineDbContext.SnapshotOfExecutionIdColumn).CurrentValue =
                    snapshot == "engine" ? e.Id : null;
                e.SnapshotDefinitionId = def.Id;
            }

            db.SaveChanges();
            return e.Id;
        }

        public Guid AddStep(Guid executionId, StepStatus status)
        {
            using WorkflowEngineDbContext db = NewDb();
            WorkflowStepResult r = new()
            {
                Id = Guid.NewGuid(),
                WorkflowExecutionId = executionId,
                WorkflowStepId = Guid.NewGuid(),
                Status = status
            };
            db.WorkflowStepResults.Add(r);
            db.SaveChanges();
            return r.Id;
        }

        public void AddJob(Guid executionId, Guid stepResultId, ExternalGenerationJobStatus status)
        {
            using WorkflowEngineDbContext db = NewDb();
            db.ExternalGenerationJobs.Add(new ExternalGenerationJob
            {
                Id = Guid.NewGuid(),
                ExecutionId = executionId,
                StepResultId = stepResultId,
                ProjectId = Guid.NewGuid(),
                ClipId = "clip-1",
                RequestHash = "h",
                ProviderId = Guid.NewGuid(),
                RemoteJobId = status == ExternalGenerationJobStatus.Reserved ? null : "remote-1",
                Status = status,
                CostEstimate = 100
            });
            db.SaveChanges();
        }

        public WorkflowExecution Get(Guid id)
        {
            using WorkflowEngineDbContext db = NewDb();
            return db.WorkflowExecutions.AsNoTracking().Single(e => e.Id == id);
        }

        public StepStatus StepStatusOf(Guid id)
        {
            using WorkflowEngineDbContext db = NewDb();
            return db.WorkflowStepResults.AsNoTracking().Single(r => r.Id == id).Status;
        }

        public void VerifyHoldsReleased(Guid id) =>
            Hook.Verify(p => p.OnPlatformFailureAsync(
                It.Is<WorkflowExecution>(e => e.Id == id), It.Is<string>(s => s.StartsWith("Interrupted")), It.IsAny<CancellationToken>()),
                Times.Once);

        public void VerifyFailedAsToday(Guid id, int requestsPublishedEarlier = 0)
        {
            WorkflowExecution row = Get(id);
            row.Status.Should().Be(ExecutionStatus.Failed);
            row.ErrorMessage.Should().StartWith("Interrupted: engine instance lost");
            row.CompletedAt.Should().NotBeNull();
            row.PendingResume.Should().BeFalse();
            Publisher.Verify(p => p.PublishExecutionFailedAsync(
                It.Is<WorkflowExecution>(e => e.Id == id), It.IsAny<CancellationToken>()), Times.Once);
            Publish.Verify(p => p.Publish(
                It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == id), It.IsAny<CancellationToken>()),
                Times.Exactly(requestsPublishedEarlier), "a failed lost run is never re-queued");
        }

        public void Dispose()
        {
            Services.Dispose();
            Connection.Dispose();
        }
    }

    [Fact]
    public async Task First_loss_with_an_engine_snapshot_is_requeued_as_a_resume_and_never_failed()
    {
        using Harness h = new();
        Guid run = h.AddLostRun(resumeCount: 2);
        Guid lost = h.AddStep(run, StepStatus.Running);
        Guid done = h.AddStep(run, StepStatus.Completed);
        // A delivered take of an earlier step is not paid work in flight.
        h.AddJob(run, done, ExternalGenerationJobStatus.Succeeded);

        (int failed, int republished) = await h.Sweeper.SweepOnceAsync(CancellationToken.None);

        (failed, republished).Should().Be((0, 0), "a re-queued run is not counted as failed nor as a stale re-publish");
        WorkflowExecution row = h.Get(run);
        row.Status.Should().Be(ExecutionStatus.Queued);
        row.PendingResume.Should().BeTrue();
        row.AutoResumeCount.Should().Be(1);
        row.ResumeCount.Should().Be(3);
        row.LeaseOwner.Should().BeNull();
        row.LeaseExpiresAt.Should().BeNull();
        row.ErrorMessage.Should().BeNull();
        row.CompletedAt.Should().BeNull();
        row.QueuedAt.Should().Be(h.Now, "a fresh queue stamp keeps the stale-queue re-publish from firing at once");
        row.StartedAt.Should().Be(h.Now.AddMinutes(-20), "a resume keeps the original start");

        h.StepStatusOf(lost).Should().Be(StepStatus.Failed, "the lost step row is failed and re-runs live on resume");
        h.StepStatusOf(done).Should().Be(StepStatus.Completed);

        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == run && m.CorrelationId == row.CorrelationId),
            It.IsAny<CancellationToken>()), Times.Once);
        h.Publisher.Verify(p => p.PublishExecutionFailedAsync(It.IsAny<WorkflowExecution>(), It.IsAny<CancellationToken>()), Times.Never);
        h.VerifyHoldsReleased(run);
    }

    [Fact]
    public async Task Second_loss_after_an_auto_resume_is_failed()
    {
        using Harness h = new();
        Guid run = h.AddLostRun(autoResumeCount: 1, resumeCount: 1);
        Guid lost = h.AddStep(run, StepStatus.Running);

        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(1);

        h.VerifyFailedAsToday(run);
        h.Get(run).AutoResumeCount.Should().Be(1);
        h.Get(run).ResumeCount.Should().Be(1);
        h.StepStatusOf(lost).Should().Be(StepStatus.Failed);
        h.VerifyHoldsReleased(run);
    }

    [Fact]
    public async Task The_same_run_lost_twice_is_requeued_once_then_failed()
    {
        using Harness h = new();
        Guid run = h.AddLostRun();
        h.AddStep(run, StepStatus.Running);
        await h.Sweeper.SweepOnceAsync(CancellationToken.None);
        h.Get(run).Status.Should().Be(ExecutionStatus.Queued);

        // The resumed dispatch starts on another engine, which dies too.
        using (WorkflowEngineDbContext db = h.NewDb())
        {
            WorkflowExecution e = db.WorkflowExecutions.Single(x => x.Id == run);
            e.Status = ExecutionStatus.Running;
            e.PendingResume = false;
            e.LeaseOwner = "engine-dead-2";
            e.LeaseExpiresAt = h.Now.AddSeconds(-5);
            db.SaveChanges();
        }

        h.AddStep(run, StepStatus.Running);
        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(1);

        // Exactly the first loss's resume request; the second loss publishes none.
        h.VerifyFailedAsToday(run, requestsPublishedEarlier: 1);
        h.Get(run).AutoResumeCount.Should().Be(1);
        h.Hook.Verify(p => p.OnPlatformFailureAsync(
            It.Is<WorkflowExecution>(e => e.Id == run), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData(ExternalGenerationJobStatus.Submitted)]
    [InlineData(ExternalGenerationJobStatus.Reserved)]
    [InlineData(ExternalGenerationJobStatus.Running)]
    public async Task Loss_during_a_VideoGenerate_step_with_paid_work_in_flight_is_failed_not_requeued(ExternalGenerationJobStatus status)
    {
        using Harness h = new();
        Guid run = h.AddLostRun();
        Guid lost = h.AddStep(run, StepStatus.Running);
        h.AddJob(run, lost, status);

        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(1);

        h.VerifyFailedAsToday(run);
        h.Get(run).AutoResumeCount.Should().Be(0);
        h.Get(run).ResumeCount.Should().Be(0);
        h.StepStatusOf(lost).Should().Be(StepStatus.Failed);
        h.VerifyHoldsReleased(run);
    }

    [Fact]
    public async Task Legacy_run_without_a_snapshot_is_failed()
    {
        using Harness h = new();
        Guid run = h.AddLostRun(snapshot: "none");
        h.AddStep(run, StepStatus.Running);

        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(1);

        h.VerifyFailedAsToday(run);
        h.VerifyHoldsReleased(run);
    }

    [Fact]
    public async Task Run_pointing_at_an_archive_is_failed()
    {
        using Harness h = new();
        Guid run = h.AddLostRun(snapshot: "archive");
        h.AddStep(run, StepStatus.Running);

        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(1);

        h.VerifyFailedAsToday(run);
        h.VerifyHoldsReleased(run);
    }

    [Fact]
    public async Task Holds_are_released_before_the_resume_request_is_published()
    {
        using Harness h = new();
        Guid run = h.AddLostRun();
        List<string> order = new();
        h.Hook.Setup(p => p.OnPlatformFailureAsync(It.IsAny<WorkflowExecution>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("release"))
            .Returns(Task.CompletedTask);
        h.Publish.Setup(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("publish"))
            .Returns(Task.CompletedTask);

        await h.Sweeper.SweepOnceAsync(CancellationToken.None);

        h.Get(run).Status.Should().Be(ExecutionStatus.Queued);
        order.Should().Equal(new[] { "release", "publish" }, "the resumed dispatch's fresh holds must never be released by the sweep");
    }
}

/// <summary>
/// Auto-resume on the real schema: two replicas sweeping the same lost runs at once (FOR UPDATE SKIP LOCKED, small
/// batches so they interleave) re-queue each resumable run exactly once, fail each paid-in-flight or legacy run
/// exactly once, and never both re-queue and fail a row.
/// </summary>
[Collection("Postgres")]
public class ExecutionRecoveryAutoResumePostgresTests
{
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_two_sweepers_racing_requeue_each_resumable_run_exactly_once_and_fail_the_rest_exactly_once()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_autoresume_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        string connection = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
        try
        {
            await using (InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options;
            await using (WorkflowEngineDbContext engine = new(options))
            {
                await engine.GetService<IMigrator>().MigrateAsync();
            }

            await using NpgsqlConnection c = new(connection);
            await c.OpenAsync();
            async Task Run(string sql)
            {
                await using NpgsqlCommand cmd = new(sql, c);
                await cmd.ExecuteNonQueryAsync();
            }

            async Task<long> Count(string sql)
            {
                await using NpgsqlCommand cmd = new(sql, c);
                return (long)(await cmd.ExecuteScalarAsync())!;
            }

            Guid user = Guid.NewGuid(), org = Guid.NewGuid(), project = Guid.NewGuid(), wf = Guid.NewGuid();
            await Run($"INSERT INTO application_users (id,email,display_name,created_at,must_change_password,is_admin) VALUES ('{user}','ar@t.local','ar',now(),false,false)");
            await Run($"INSERT INTO organizations (id,name,slug,kind,created_at,updated_at,compute_preference) VALUES ('{org}','o','o-{org:N}','Team',now(),now(),'Auto')");
            await Run($"INSERT INTO projects (id,name,owner_id,organization_id,status,created_at,updated_at) VALUES ('{project}','p','{user}','{org}','Draft',now(),now())");
            await Run($"INSERT INTO workflow_definitions (id,name,project_id,created_at,updated_at) VALUES ('{wf}','w','{project}',now(),now())");

            const int Resumable = 40, PaidInFlight = 10, Legacy = 5;
            DateTime now = DateTime.UtcNow;
            List<Guid> resumableIds = new(), paidIds = new(), legacyIds = new();
            await using (WorkflowEngineDbContext db = new(options))
            {
                WorkflowExecution Lost() => new()
                {
                    Id = Guid.NewGuid(),
                    WorkflowDefinitionId = wf,
                    ProjectId = project,
                    OrganizationId = org,
                    Status = ExecutionStatus.Running,
                    CorrelationId = "c",
                    StartedAt = now.AddHours(-1),
                    LeaseOwner = "dead-pod",
                    LeaseExpiresAt = now.AddMinutes(-1)
                };

                List<WorkflowExecution> withSnapshot = new();
                for (int i = 0; i < Resumable + PaidInFlight; i++)
                {
                    withSnapshot.Add(Lost());
                }

                for (int i = 0; i < Legacy; i++)
                {
                    WorkflowExecution e = Lost();
                    db.WorkflowExecutions.Add(e);
                    legacyIds.Add(e.Id);
                }

                db.WorkflowExecutions.AddRange(withSnapshot);
                await db.SaveChangesAsync();

                foreach (WorkflowExecution e in withSnapshot)
                {
                    WorkflowDefinition snap = new() { Id = Guid.NewGuid(), Name = "snapshot", ProjectId = project, Kind = WorkflowDefinitionKind.ExecutionSnapshot };
                    db.WorkflowDefinitions.Add(snap);
                    db.Entry(snap).Property<Guid?>(WorkflowEngineDbContext.SnapshotOfExecutionIdColumn).CurrentValue = e.Id;
                    e.SnapshotDefinitionId = snap.Id;
                }

                for (int i = 0; i < withSnapshot.Count; i++)
                {
                    WorkflowExecution e = withSnapshot[i];
                    if (i < Resumable)
                    {
                        resumableIds.Add(e.Id);
                        continue;
                    }

                    paidIds.Add(e.Id);
                    db.ExternalGenerationJobs.Add(new ExternalGenerationJob
                    {
                        Id = Guid.NewGuid(),
                        ExecutionId = e.Id,
                        StepResultId = Guid.NewGuid(),
                        ProjectId = project,
                        OrganizationId = org,
                        ClipId = "clip",
                        RequestHash = "h",
                        ProviderId = Guid.NewGuid(),
                        RemoteJobId = "remote",
                        Status = ExternalGenerationJobStatus.Submitted,
                        CostEstimate = 100
                    });
                }

                await db.SaveChangesAsync();
            }

            ConcurrentBag<Guid> requested = new(), failedEvents = new(), released = new();
            Mock<IPublishEndpoint> publish = new();
            publish.Setup(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()))
                .Callback<WorkflowExecutionRequested, CancellationToken>((m, _) => requested.Add(m.ExecutionId))
                .Returns(Task.CompletedTask);
            Mock<IWorkflowEventPublisher> publisher = new();
            publisher.Setup(p => p.PublishExecutionFailedAsync(It.IsAny<WorkflowExecution>(), It.IsAny<CancellationToken>()))
                .Callback<WorkflowExecution, CancellationToken>((e, _) => failedEvents.Add(e.Id))
                .Returns(Task.CompletedTask);
            Mock<IPlatformFailureHook> hook = new();
            hook.Setup(p => p.OnPlatformFailureAsync(It.IsAny<WorkflowExecution>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<WorkflowExecution, string, CancellationToken>((e, _, _) => released.Add(e.Id))
                .Returns(Task.CompletedTask);

            ServiceCollection services = new();
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseNpgsql(connection));
            services.AddSingleton(publish.Object);
            services.AddSingleton(publisher.Object);
            services.AddSingleton(hook.Object);
            await using ServiceProvider sp = services.BuildServiceProvider();

            ExecutionRecoverySweeper Make(string id) => new(
                sp.GetRequiredService<IServiceScopeFactory>(),
                new ExecutionLeaseService(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["WorkflowEngine:InstanceId"] = id,
                        ["WorkflowEngine:Lease:SweepBatchSize"] = "7"
                    }).Build(),
                    NullLogger<ExecutionLeaseService>.Instance),
                NullLogger<ExecutionRecoverySweeper>.Instance);

            (int Failed, int Republished)[] results = await Task.WhenAll(
                Make("sweeper-1").SweepOnceAsync(CancellationToken.None),
                Make("sweeper-2").SweepOnceAsync(CancellationToken.None));

            results.Sum(r => r.Failed).Should().Be(PaidInFlight + Legacy);
            results.Sum(r => r.Republished).Should().Be(0, "the re-queued rows carry a fresh queued_at");
            requested.Should().HaveCount(Resumable).And.OnlyHaveUniqueItems();
            requested.Should().BeEquivalentTo(resumableIds);
            failedEvents.Should().HaveCount(PaidInFlight + Legacy).And.OnlyHaveUniqueItems();
            failedEvents.Should().BeEquivalentTo(paidIds.Concat(legacyIds));
            released.Should().HaveCount(Resumable + PaidInFlight + Legacy).And.OnlyHaveUniqueItems("holds are released once per lost run, in every case");

            (await Count("SELECT count(*) FROM workflow_executions WHERE status='Queued' AND pending_resume AND auto_resume_count=1 AND resume_count=1 AND lease_owner IS NULL AND error_message IS NULL"))
                .Should().Be(Resumable);
            (await Count("SELECT count(*) FROM workflow_executions WHERE status='Failed' AND NOT pending_resume AND auto_resume_count=0 AND error_message LIKE 'Interrupted: engine instance lost%'"))
                .Should().Be(PaidInFlight + Legacy);
            (await Count("SELECT count(*) FROM workflow_executions WHERE status='Running'")).Should().Be(0);

            // A second pass by either replica finds nothing left to claim: the re-queued rows are not Running.
            (await Make("sweeper-1").SweepOnceAsync(CancellationToken.None)).Should().Be((0, 0));
            requested.Should().HaveCount(Resumable);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
