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
using ReelBolt.Inference.Api.Services.Background;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Tests.Execution;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// F4 coverage: execution leases, the recovery sweeper, stale-queue re-publish and the lease-aware
/// Inference API startup cleanup. The logic runs against Sqlite (real constraints, real
/// <c>ExecuteUpdate</c>); the <c>FOR UPDATE SKIP LOCKED</c> claim and the migration run against a real
/// PostgreSQL when <c>REELBOLT_TEST_PG</c> is set, as in <see cref="Data.RunnerSchemaTests"/>.
/// </summary>
[Collection("Postgres")]
public class ExecutionRecoveryTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
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
        public ExecutionCancellationRegistry Cancellations { get; } = new();

        public Harness(string instanceId = "engine-a", Action<IServiceCollection>? configure = null)
        {
            // Foreign keys off: these tests seed only the rows the sweeper reads.
            Connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
            Connection.Open();

            ServiceCollection services = new();
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseSqlite(Connection));
            services.AddSingleton(Publisher.Object);
            services.AddSingleton(Hook.Object);
            services.AddSingleton(Publish.Object);
            configure?.Invoke(services);
            Services = services.BuildServiceProvider();

            using (IServiceScope scope = Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>().Database.EnsureCreated();
            }

            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = instanceId })
                .Build();
            IServiceScopeFactory factory = Services.GetRequiredService<IServiceScopeFactory>();
            Leases = new ExecutionLeaseService(factory, config, NullLogger<ExecutionLeaseService>.Instance, Time, cancellations: Cancellations);
            Sweeper = new ExecutionRecoverySweeper(factory, Leases, NullLogger<ExecutionRecoverySweeper>.Instance, Time);
        }

        public DateTime Now => Time.GetUtcNow().UtcDateTime;

        public WorkflowEngineDbContext NewDb() =>
            Services.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        public Guid AddExecution(
            ExecutionStatus status,
            string? owner = null,
            DateTime? leaseExpiresAt = null,
            DateTime? startedAt = null,
            DateTime? queuedAt = null,
            string? designSystemPinJson = null,
            string? errorMessage = null)
        {
            using WorkflowEngineDbContext db = NewDb();
            WorkflowExecution e = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid(),
                Status = status,
                CorrelationId = "c-" + Guid.NewGuid().ToString("N")[..6],
                StartedAt = startedAt,
                LeaseOwner = owner,
                LeaseExpiresAt = leaseExpiresAt,
                QueuedAt = queuedAt,
                DesignSystemPinJson = designSystemPinJson,
                ErrorMessage = errorMessage
            };
            db.WorkflowExecutions.Add(e);
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

        public WorkflowExecution Get(Guid id)
        {
            using WorkflowEngineDbContext db = NewDb();
            return db.WorkflowExecutions.AsNoTracking().Single(e => e.Id == id);
        }

        public void Dispose()
        {
            Services.Dispose();
            Connection.Dispose();
        }
    }

    [Fact]
    public async Task Sweeper_FailsAnOrphanWithProvenance_MarksItsRunningStepsFailed_AndPublishesTheFailure()
    {
        using Harness h = new();
        Guid orphan = h.AddExecution(ExecutionStatus.Running, "engine-dead", h.Now.AddSeconds(-5), h.Now.AddMinutes(-20));
        Guid running = h.AddStep(orphan, StepStatus.Running);
        Guid done = h.AddStep(orphan, StepStatus.Completed);

        (int failed, int republished) = await h.Sweeper.SweepOnceAsync(CancellationToken.None);

        failed.Should().Be(1);
        republished.Should().Be(0);
        WorkflowExecution row = h.Get(orphan);
        row.Status.Should().Be(ExecutionStatus.Failed);
        row.CompletedAt.Should().NotBeNull();
        row.ErrorMessage.Should().StartWith("Interrupted: engine instance lost");
        row.ErrorMessage.Should().Contain("engine-dead");
        // Never blame the user for a platform interruption (CLAUDE.md "Cancellation carries provenance").
        row.ErrorMessage!.ToLowerInvariant().Should().NotContain("user");
        row.ErrorMessage.ToLowerInvariant().Should().NotContain("cancelled by");

        using WorkflowEngineDbContext db = h.NewDb();
        WorkflowStepResult step = db.WorkflowStepResults.Single(r => r.Id == running);
        step.Status.Should().Be(StepStatus.Failed);
        step.ErrorDetails.Should().StartWith("Interrupted: engine instance lost");
        step.CompletedAt.Should().NotBeNull();
        db.WorkflowStepResults.Single(r => r.Id == done).Status.Should().Be(StepStatus.Completed);

        h.Publisher.Verify(p => p.PublishExecutionFailedAsync(
            It.Is<WorkflowExecution>(e => e.Id == orphan && e.Status == ExecutionStatus.Failed), It.IsAny<CancellationToken>()), Times.Once);
        h.Hook.Verify(p => p.OnPlatformFailureAsync(
            It.Is<WorkflowExecution>(e => e.Id == orphan), It.Is<string>(s => s.StartsWith("Interrupted")), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sweeper_LeavesEveryRowThatIsNotAnOrphanUntouched()
    {
        using Harness h = new("engine-a");
        Guid otherInstanceLive = h.AddExecution(ExecutionStatus.Running, "engine-b", h.Now.AddSeconds(40), h.Now.AddMinutes(-30));
        Guid mineLapsed = h.AddExecution(ExecutionStatus.Running, "engine-a", h.Now.AddSeconds(-30), h.Now.AddMinutes(-30));
        h.Leases.Track(mineLapsed);
        Guid freshQueued = h.AddExecution(ExecutionStatus.Queued, queuedAt: h.Now.AddMinutes(-1));
        Guid completed = h.AddExecution(ExecutionStatus.Passed, "engine-b", h.Now.AddSeconds(-500));
        Guid legacyRecent = h.AddExecution(ExecutionStatus.Running, null, null, h.Now.AddSeconds(-30));

        (int failed, int republished) = await h.Sweeper.SweepOnceAsync(CancellationToken.None);

        (failed, republished).Should().Be((0, 0));
        h.Get(otherInstanceLive).Status.Should().Be(ExecutionStatus.Running);
        // Lapsed but this very process is still running it: the renewal timer's job, not the sweeper's.
        h.Get(mineLapsed).Status.Should().Be(ExecutionStatus.Running);
        h.Get(freshQueued).Status.Should().Be(ExecutionStatus.Queued);
        h.Get(completed).Status.Should().Be(ExecutionStatus.Passed);
        h.Get(legacyRecent).Status.Should().Be(ExecutionStatus.Running);
        h.Publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Sweeper_RecoversAPreLeaseRunningRowOnceItIsOlderThanTwoLeaseWindows()
    {
        using Harness h = new();
        Guid legacyOld = h.AddExecution(ExecutionStatus.Running, null, null, h.Now.AddMinutes(-10));

        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(1);
        h.Get(legacyOld).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Sweeper_OrphanIsFailedWithinTheLeaseWindowPlusOneSweep()
    {
        using Harness h = new();
        // Stamp a lease as a starting execution would, then the instance "dies" (never renews).
        Guid id = h.AddExecution(ExecutionStatus.Running, "engine-dead", startedAt: h.Now);
        using (WorkflowEngineDbContext db = h.NewDb())
        {
            WorkflowExecution e = db.WorkflowExecutions.Single(x => x.Id == id);
            using Harness other = new("engine-dead");
            other.Time.Now = h.Time.Now;
            other.Leases.StampLease(e);
            other.Leases.Release(e.Id);
            db.SaveChanges();
        }

        // 59 s later the lease still holds; 90 s later (lease 60 + one 30 s sweep) it is recovered.
        h.Time.Now = h.Time.Now.AddSeconds(59);
        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(0);
        h.Time.Now = h.Time.Now.AddSeconds(31);
        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(1);
        h.Get(id).Status.Should().Be(ExecutionStatus.Failed);
    }

    [Fact]
    public async Task Sweeper_RepublishesAStaleQueuedRequest_OnceAndNotAgainUntilTheNextWindow()
    {
        using Harness h = new();
        Guid stale = h.AddExecution(ExecutionStatus.Queued, queuedAt: h.Now.AddMinutes(-11));

        (int failed, int republished) = await h.Sweeper.SweepOnceAsync(CancellationToken.None);

        (failed, republished).Should().Be((0, 1));
        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == stale),
            It.IsAny<CancellationToken>()), Times.Once);
        h.Get(stale).Status.Should().Be(ExecutionStatus.Queued);
        h.Get(stale).QueuedAt.Should().Be(h.Now);

        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).Republished.Should().Be(0);
        h.Time.Now = h.Time.Now.AddMinutes(11);
        (await h.Sweeper.SweepOnceAsync(CancellationToken.None)).Republished.Should().Be(1);
    }

    [Fact]
    public async Task Sweeper_republish_carries_design_system_pin()
    {
        using Harness h = new();
        string pin = "{\"v\":1,\"design\":{\"versionId\":\"" + Guid.NewGuid()
            + "\",\"versionNumber\":7,\"digest\":\"" + new string('a', 64) + "\"}}";
        Guid stale = h.AddExecution(
            ExecutionStatus.Queued, queuedAt: h.Now.AddMinutes(-11), designSystemPinJson: pin);

        (int failed, int republished) = await h.Sweeper.SweepOnceAsync(CancellationToken.None);

        (failed, republished).Should().Be((0, 1));
        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.DesignSystemPinJson == pin),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sweeper_RepublishesTheRowsOutputResolution()
    {
        using Harness h = new();
        Guid stale = h.AddExecution(ExecutionStatus.Queued, queuedAt: h.Now.AddMinutes(-11));
        using (WorkflowEngineDbContext db = h.NewDb())
        {
            db.WorkflowExecutions.Single(e => e.Id == stale).OutputResolution = 2160;
            db.SaveChanges();
        }

        await h.Sweeper.SweepOnceAsync(CancellationToken.None);

        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == stale && m.OutputResolution == 2160),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sweeper_RepublishesTheRowsRunBrief()
    {
        using Harness h = new();
        Guid stale = h.AddExecution(ExecutionStatus.Queued, queuedAt: h.Now.AddMinutes(-11));
        const string brief = "{\"version\":1,\"language\":\"it\",\"lengthSec\":45}";
        using (WorkflowEngineDbContext db = h.NewDb())
        {
            db.WorkflowExecutions.Single(e => e.Id == stale).RunBriefJson = brief;
            db.SaveChanges();
        }

        await h.Sweeper.SweepOnceAsync(CancellationToken.None);

        h.Publish.Verify(p => p.Publish(
            It.Is<WorkflowExecutionRequested>(m => m.ExecutionId == stale && m.RunBriefJson == brief),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sweeper_RepublishesEveryRunStartPropertyOfTheRow()
    {
        using Harness h = new();
        Guid stale = h.AddExecution(ExecutionStatus.Queued, queuedAt: h.Now.AddMinutes(-11));
        ReelBolt.Shared.Data.Models.WorkflowExecution expected = new();
        using (WorkflowEngineDbContext db = h.NewDb())
        {
            ReelBolt.Shared.Data.Models.WorkflowExecution row = db.WorkflowExecutions.Single(e => e.Id == stale);
            ReelBolt.WorkflowEngine.Tests.Briefs.RunStateCopySiteDriftGuardTests.FillRunStartState(row);
            ReelBolt.WorkflowEngine.Tests.Briefs.RunStateCopySiteDriftGuardTests.FillRunStartState(expected);
            db.SaveChanges();
        }

        WorkflowExecutionRequested? sent = null;
        h.Publish.Setup(p => p.Publish(It.IsAny<WorkflowExecutionRequested>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowExecutionRequested, CancellationToken>((m, _) => sent = m).Returns(Task.CompletedTask);

        await h.Sweeper.SweepOnceAsync(CancellationToken.None);

        sent.Should().NotBeNull();
        ReelBolt.WorkflowEngine.Tests.Briefs.RunStateCopySiteDriftGuardTests.AssertEventMirrors(expected, sent!);
    }

    [Fact]
    public async Task Lease_StampsTheRow_AndRenewalExtendsEveryTrackedRowThisInstanceOwns_EvenOnceStopped()
    {
        using Harness h = new("engine-a");
        WorkflowExecution mine = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            OrganizationId = Guid.NewGuid(),
            Status = ExecutionStatus.Running,
            CorrelationId = "c"
        };
        h.Leases.StampLease(mine);
        mine.LeaseOwner.Should().Be("engine-a");
        mine.LeaseExpiresAt.Should().Be(h.Now.AddSeconds(60));
        mine.HeartbeatAt.Should().Be(h.Now);
        h.Leases.TrackedIds.Should().Contain(mine.Id);
        using (WorkflowEngineDbContext db = h.NewDb())
        {
            db.WorkflowExecutions.Add(mine);
            db.SaveChanges();
        }

        Guid stopped = h.AddExecution(ExecutionStatus.Cancelled, "engine-a", h.Now);
        h.Leases.Track(stopped);
        Guid foreign = h.AddExecution(ExecutionStatus.Running, "engine-b", h.Now.AddSeconds(5));
        h.Leases.Track(foreign);

        h.Time.Now = h.Time.Now.AddSeconds(15);
        (await h.Leases.RenewAsync(CancellationToken.None)).Should().Be(2, "the running row and the stopped row this instance owns");

        h.Get(mine.Id).LeaseExpiresAt.Should().Be(h.Now.AddSeconds(60));
        h.Get(mine.Id).HeartbeatAt.Should().Be(h.Now);
        // Stopped through another replica, still executing here until the runner's finally releases it.
        h.Get(stopped).LeaseExpiresAt.Should().Be(h.Now.AddSeconds(60));
        h.Get(stopped).Status.Should().Be(ExecutionStatus.Cancelled);
        h.Get(foreign).LeaseExpiresAt.Should().Be(new DateTime(2026, 10, 5, 12, 0, 5, DateTimeKind.Utc));

        h.Leases.Release(mine.Id);
        h.Leases.Release(stopped);
        h.Time.Now = h.Time.Now.AddSeconds(15);
        (await h.Leases.RenewAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task PropagateStops_cancels_a_tracked_execution_stopped_through_another_instance()
    {
        using Harness h = new("engine-a");
        Guid stopped = h.AddExecution(ExecutionStatus.Cancelled, "engine-a", h.Now.AddSeconds(60), errorMessage: "Stopped by user 7c1d");
        h.Leases.Track(stopped);
        using CancellationTokenSource token = new();
        h.Cancellations.Register(stopped, token);

        (await h.Leases.PropagateStopsAsync(CancellationToken.None)).Should().Be(1);

        token.IsCancellationRequested.Should().BeTrue();
        h.Cancellations.GetReason(stopped).Should().Be("Stopped by user 7c1d");
    }

    [Fact]
    public async Task PropagateStops_ignores_running_untracked_and_foreign_rows()
    {
        using Harness h = new("engine-a");
        Guid running = h.AddExecution(ExecutionStatus.Running, "engine-a", h.Now.AddSeconds(60));
        h.Leases.Track(running);
        Guid untracked = h.AddExecution(ExecutionStatus.Cancelled, "engine-a", h.Now.AddSeconds(60), errorMessage: "Stopped by user 1");
        Guid foreign = h.AddExecution(ExecutionStatus.Cancelled, "engine-b", h.Now.AddSeconds(60), errorMessage: "Stopped by user 2");
        h.Leases.Track(foreign);
        using CancellationTokenSource runningToken = new(), untrackedToken = new(), foreignToken = new();
        h.Cancellations.Register(running, runningToken);
        h.Cancellations.Register(untracked, untrackedToken);
        h.Cancellations.Register(foreign, foreignToken);

        (await h.Leases.PropagateStopsAsync(CancellationToken.None)).Should().Be(0);

        runningToken.IsCancellationRequested.Should().BeFalse();
        untrackedToken.IsCancellationRequested.Should().BeFalse();
        foreignToken.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task EndLease_expires_only_this_instances_live_lease_of_that_execution()
    {
        // F4 (S-50 follow-up): ending a lease touches exactly one execution, and only when this
        // instance owns a lease on it that has not yet lapsed. Everything else is left as it was.
        using Harness h = new("engine-a");
        Guid mine = h.AddExecution(ExecutionStatus.Cancelled, owner: "engine-a", leaseExpiresAt: h.Now.AddSeconds(60));
        Guid otherMine = h.AddExecution(ExecutionStatus.Running, owner: "engine-a", leaseExpiresAt: h.Now.AddSeconds(60));
        Guid foreign = h.AddExecution(ExecutionStatus.Running, owner: "engine-b", leaseExpiresAt: h.Now.AddSeconds(60));
        Guid lapsed = h.AddExecution(ExecutionStatus.Failed, owner: "engine-a", leaseExpiresAt: h.Now.AddSeconds(-30));

        (await h.Leases.EndLeaseAsync(mine, CancellationToken.None)).Should().Be(1);
        WorkflowExecution endedRow = h.Get(mine);
        endedRow.LeaseExpiresAt.Should().Be(h.Now);
        endedRow.LeaseOwner.Should().Be("engine-a");
        endedRow.Status.Should().Be(ExecutionStatus.Cancelled);

        (await h.Leases.EndLeaseAsync(foreign, CancellationToken.None)).Should().Be(0);
        WorkflowExecution foreignRow = h.Get(foreign);
        foreignRow.LeaseOwner.Should().Be("engine-b");
        foreignRow.LeaseExpiresAt.Should().Be(h.Now.AddSeconds(60));

        (await h.Leases.EndLeaseAsync(lapsed, CancellationToken.None)).Should().Be(0);
        WorkflowExecution lapsedRow = h.Get(lapsed);
        lapsedRow.LeaseOwner.Should().Be("engine-a");
        lapsedRow.LeaseExpiresAt.Should().Be(h.Now.AddSeconds(-30));

        WorkflowExecution untouched = h.Get(otherMine);
        untouched.LeaseOwner.Should().Be("engine-a");
        untouched.LeaseExpiresAt.Should().Be(h.Now.AddSeconds(60));
        untouched.Status.Should().Be(ExecutionStatus.Running);
    }

    [Fact]
    public async Task The_runner_ends_its_lease_when_the_run_exits()
    {
        // F4 (S-50 follow-up): the runner's finally ends the lease of a run that has exited, so a
        // quick retry of the finished run is not refused for up to a whole lease window.
        Guid id = Guid.Empty;
        Harness? harness = null;
        using Harness h = new("engine-a", services => services.AddScoped<WorkflowExecutorService>(_ =>
            new TrackingExecutorStub(() => harness!.Leases.Track(id))));
        harness = h;

        id = h.AddExecution(ExecutionStatus.Running, owner: "engine-a", leaseExpiresAt: h.Now.AddSeconds(60));

        var runner = new WorkflowExecutionRunner(
            h.Services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            h.Cancellations,
            new DecisionQuestionMemoizer(),
            new ExecutionAdmissionGuard(
                new TestPlatformSwitches(),
                h.Services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ExecutionAdmissionGuard>.Instance),
            NullLogger<WorkflowExecutionRunner>.Instance,
            h.Leases);

        (await runner.DispatchAsync(id, "corr-end", CancellationToken.None, false, Guid.NewGuid())).Admitted.Should().BeTrue();
        await WaitForAsync(() => runner.RunningCount == 0);

        h.Get(id).LeaseExpiresAt.Should().Be(h.Now);
        h.Leases.TrackedIds.Should().NotContain(id);
    }

    /// <summary>Stands in for the step loop: it tracks the execution's lease while it "runs", then returns.</summary>
    private sealed class TrackingExecutorStub : WorkflowExecutorService
    {
        private readonly Action _onRun;

        public TrackingExecutorStub(Action onRun)
            : base(
                scopeFactory: null!,
                eventPublisher: null!,
                logger: NullLogger<WorkflowExecutorService>.Instance,
                executors: Array.Empty<IStepExecutor>(),
                rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
                hardeningOptions: Microsoft.Extensions.Options.Options.Create(new WorkflowHardeningOptions()),
                cancellationRegistry: new ExecutionCancellationRegistry())
            => _onRun = onRun;

        public override Task ExecuteAsync(Guid executionId, string correlationId, CancellationToken ct)
        {
            _onRun();
            return Task.CompletedTask;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition was not met within 10 s.");
            }

            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task StartupCleanup_LeavesLiveRunsRunning_AndCancelsOnlyExpiredOnesAndStrandedQueueRows()
    {
        using Harness h = new();
        DateTime now = DateTime.UtcNow;
        Guid live = h.AddExecution(ExecutionStatus.Running, "engine-b", now.AddSeconds(45), now.AddHours(-2));
        Guid dead = h.AddExecution(ExecutionStatus.Running, "engine-c", now.AddSeconds(-45), now.AddHours(-2));
        Guid legacyOld = h.AddExecution(ExecutionStatus.Running, null, null, now.AddHours(-1));
        Guid legacyNew = h.AddExecution(ExecutionStatus.Running, null, null, now.AddSeconds(-20));
        Guid queued = h.AddExecution(ExecutionStatus.Queued);

        StartupCleanupService cleanup = NewCleanup(h, purge: true);
        (await cleanup.CancelOrphanedExecutionsAsync(CancellationToken.None)).Should().Be(3);

        h.Get(live).Status.Should().Be(ExecutionStatus.Running);
        h.Get(legacyNew).Status.Should().Be(ExecutionStatus.Running);
        h.Get(dead).Status.Should().Be(ExecutionStatus.Cancelled);
        h.Get(legacyOld).Status.Should().Be(ExecutionStatus.Cancelled);
        h.Get(queued).Status.Should().Be(ExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task StartupCleanup_WithPurgeQueuesOff_TouchesNothing()
    {
        using Harness h = new();
        DateTime now = DateTime.UtcNow;
        Guid dead = h.AddExecution(ExecutionStatus.Running, "engine-c", now.AddSeconds(-45), now.AddHours(-2));
        Guid queued = h.AddExecution(ExecutionStatus.Queued);

        StartupCleanupService cleanup = NewCleanup(h, purge: false);
        cleanup.PurgeQueues.Should().BeFalse();
        (await cleanup.CancelOrphanedExecutionsAsync(CancellationToken.None)).Should().Be(0);

        h.Get(dead).Status.Should().Be(ExecutionStatus.Running);
        h.Get(queued).Status.Should().Be(ExecutionStatus.Queued);
    }

    [Fact]
    public void StartupCleanup_PurgesQueuesByDefault_ForSelfHost()
    {
        using Harness h = new();
        new StartupCleanupService(h.Services, new ConfigurationBuilder().Build()).PurgeQueues.Should().BeTrue();
    }

    private static StartupCleanupService NewCleanup(Harness h, bool purge)
    {
        // The Inference API maps workflow_executions read-only over the same table, so its context
        // (also on Sqlite, same connection) is what the cleanup service really runs against.
        ServiceCollection services = new();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseSqlite(h.Connection));
        IConfiguration config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["StartupCleanup:PurgeQueues"] = purge ? "true" : "false" })
            .Build();
        return new StartupCleanupService(services.BuildServiceProvider(), config);
    }

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task Postgres_TwoSweepersRacing_FailEachOrphanExactlyOnce_AndMigrationBackfillsQueuedAt()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_f4_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            await using (InferenceApiDbContext api = new(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            const string PreviousMigration = "20261005214238_AddRunnerJobsAndSessions";
            await using WorkflowEngineDbContext engine = new(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(csb.ConnectionString).Options);
            IMigrator migrator = engine.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);

            await using NpgsqlConnection c = new(csb.ConnectionString);
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
            await Run($"INSERT INTO application_users (id,email,display_name,created_at,must_change_password,is_admin) VALUES ('{user}','f4@t.local','f4',now(),false,false)");
            await Run($"INSERT INTO organizations (id,name,slug,kind,created_at,updated_at,compute_preference) VALUES ('{org}','o','o-{org:N}','Team',now(),now(),'Auto')");
            await Run($"INSERT INTO projects (id,name,owner_id,organization_id,status,created_at,updated_at) VALUES ('{project}','p','{user}','{org}','Draft',now(),now())");
            await Run($"INSERT INTO workflow_definitions (id,name,project_id,created_at,updated_at) VALUES ('{wf}','w','{project}',now(),now())");
            Guid preQueued = Guid.NewGuid();
            await Run($"INSERT INTO workflow_executions (id,workflow_definition_id,project_id,organization_id,status,iteration_count,correlation_id) VALUES ('{preQueued}','{wf}','{project}','{org}','Queued',0,'c')");

            await migrator.MigrateAsync();

            // Existing queued rows age from the deploy; nothing starts out holding a lease.
            (await Count($"SELECT count(*) FROM workflow_executions WHERE id='{preQueued}' AND queued_at IS NOT NULL AND lease_owner IS NULL")).Should().Be(1);

            const int Orphans = 60;
            for (int i = 0; i < Orphans; i++)
            {
                await Run($"INSERT INTO workflow_executions (id,workflow_definition_id,project_id,organization_id,status,iteration_count,correlation_id,started_at,lease_owner,lease_expires_at) VALUES (gen_random_uuid(),'{wf}','{project}','{org}','Running',0,'c',now() - interval '1 hour','dead-pod',now() - interval '1 minute')");
            }

            Guid live = Guid.NewGuid();
            await Run($"INSERT INTO workflow_executions (id,workflow_definition_id,project_id,organization_id,status,iteration_count,correlation_id,started_at,lease_owner,lease_expires_at) VALUES ('{live}','{wf}','{project}','{org}','Running',0,'c',now(),'other-pod',now() + interval '1 minute')");

            // Two replicas sweeping at once, small batches so they genuinely interleave.
            int published = 0;
            Mock<IWorkflowEventPublisher> publisher = new();
            publisher.Setup(p => p.PublishExecutionFailedAsync(It.IsAny<WorkflowExecution>(), It.IsAny<CancellationToken>()))
                .Callback(() => Interlocked.Increment(ref published))
                .Returns(Task.CompletedTask);

            ServiceCollection services = new();
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseNpgsql(csb.ConnectionString));
            services.AddSingleton(publisher.Object);
            ServiceProvider sp = services.BuildServiceProvider();

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

            results.Sum(r => r.Failed).Should().Be(Orphans);
            published.Should().Be(Orphans);
            (await Count("SELECT count(*) FROM workflow_executions WHERE status='Failed'")).Should().Be(Orphans);
            (await Count($"SELECT count(*) FROM workflow_executions WHERE id='{live}' AND status='Running'")).Should().Be(1);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
