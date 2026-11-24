using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Billing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Security SQFU-S1: an engine that stalled past its lease (alive, not dead) must notice that the run is no longer
/// its own and stop, rather than reserve, settle or submit next to the instance the run was handed to. Ownership is
/// the row's <c>lease_owner</c>: the sweeper clears it on every recovery and a new claim overwrites it.
/// </summary>
public class ExecutionLeaseOwnershipTests
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
        public ExecutionCancellationRegistry Cancellations { get; } = new();
        public ExecutionLeaseService Leases { get; }

        public Harness()
        {
            Connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
            Connection.Open();
            ServiceCollection services = new();
            services.AddDbContext<WorkflowEngineDbContext>(o => o.UseSqlite(Connection));
            Services = services.BuildServiceProvider();
            using (IServiceScope scope = Services.CreateScope())
            {
                scope.ServiceProvider.GetRequiredService<WorkflowEngineDbContext>().Database.EnsureCreated();
            }

            IConfiguration config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = "engine-a" })
                .Build();
            Leases = new ExecutionLeaseService(
                Services.GetRequiredService<IServiceScopeFactory>(), config, NullLogger<ExecutionLeaseService>.Instance, Time, Cancellations);
        }

        public DateTime Now => Time.GetUtcNow().UtcDateTime;

        public WorkflowEngineDbContext NewDb() =>
            Services.CreateScope().ServiceProvider.GetRequiredService<WorkflowEngineDbContext>();

        /// <summary>A Running row this instance stamped and is executing, with a registered cancellation token.</summary>
        public (Guid Id, CancellationTokenSource Token) AddMyRun()
        {
            using WorkflowEngineDbContext db = NewDb();
            WorkflowExecution e = new()
            {
                Id = Guid.NewGuid(),
                WorkflowDefinitionId = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                OrganizationId = Guid.NewGuid(),
                Status = ExecutionStatus.Running,
                CorrelationId = "c",
                StartedAt = Now
            };
            Leases.StampLease(e);
            db.WorkflowExecutions.Add(e);
            db.SaveChanges();
            CancellationTokenSource cts = new();
            Cancellations.Register(e.Id, cts);
            return (e.Id, cts);
        }

        public void Set(Guid id, Action<WorkflowExecution> change)
        {
            using WorkflowEngineDbContext db = NewDb();
            change(db.WorkflowExecutions.Single(e => e.Id == id));
            db.SaveChanges();
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
    public async Task Renewal_cancels_a_tracked_run_another_instance_now_owns_with_a_platform_lease_lost_reason()
    {
        using Harness h = new();
        (Guid lost, CancellationTokenSource lostToken) = h.AddMyRun();
        (Guid kept, CancellationTokenSource keptToken) = h.AddMyRun();
        h.Set(lost, e => { e.LeaseOwner = "engine-b"; e.LeaseExpiresAt = h.Now.AddSeconds(60); });

        h.Time.Now = h.Time.Now.AddSeconds(15);
        (await h.Leases.RenewAsync(CancellationToken.None)).Should().Be(1);

        lostToken.IsCancellationRequested.Should().BeTrue("the run was handed to engine-b; this instance must stop it");
        h.Cancellations.GetReason(lost).Should().Be(ExecutionLeaseService.LeaseLostReason);
        h.Leases.IsLost(lost).Should().BeTrue();
        h.Leases.TrackedIds.Should().NotContain(lost, "a lost lease is no longer renewed");
        h.Get(lost).LeaseOwner.Should().Be("engine-b", "renewal never writes another instance's row");

        keptToken.IsCancellationRequested.Should().BeFalse();
        h.Leases.IsLost(kept).Should().BeFalse();
        h.Get(kept).LeaseExpiresAt.Should().Be(h.Now.AddSeconds(60));
    }

    [Fact]
    public async Task Renewal_cancels_a_tracked_run_the_sweeper_requeued_for_an_auto_resume()
    {
        using Harness h = new();
        (Guid id, CancellationTokenSource token) = h.AddMyRun();
        h.Set(id, e => { e.Status = ExecutionStatus.Queued; e.PendingResume = true; e.LeaseOwner = null; e.LeaseExpiresAt = null; });

        await h.Leases.RenewAsync(CancellationToken.None);

        token.IsCancellationRequested.Should().BeTrue();
        h.Leases.IsLost(id).Should().BeTrue();
        WorkflowExecution row = h.Get(id);
        row.LeaseOwner.Should().BeNull("the re-queued row is left for the next claimant");
        row.Status.Should().Be(ExecutionStatus.Queued);
    }

    [Fact]
    public async Task TryHold_extends_an_owned_lease_and_refuses_once_another_instance_owns_the_run()
    {
        using Harness h = new();
        (Guid id, CancellationTokenSource token) = h.AddMyRun();

        h.Time.Now = h.Time.Now.AddSeconds(50);
        (await h.Leases.TryHoldAsync(id, CancellationToken.None)).Should().BeTrue();
        h.Get(id).LeaseExpiresAt.Should().Be(h.Now.AddSeconds(60), "a passed check pushes the lease out, so no sweep can claim the row right behind it");

        h.Set(id, e => e.LeaseOwner = "engine-b");
        (await h.Leases.TryHoldAsync(id, CancellationToken.None)).Should().BeFalse();
        token.IsCancellationRequested.Should().BeTrue();
        h.Cancellations.GetReason(id).Should().Be(ExecutionLeaseService.LeaseLostReason);

        Func<Task> again = () => h.Leases.ThrowIfLeaseLostAsync(id, CancellationToken.None);
        (await again.Should().ThrowAsync<OperationCanceledException>()).Which.Message.Should().Be(ExecutionLeaseService.LeaseLostReason);
    }

    [Fact]
    public async Task An_expired_lease_nobody_claimed_is_still_this_instances_run()
    {
        // A stall the sweeper has not acted on yet loses nothing: the sweeper's claim is what hands a run over.
        using Harness h = new();
        (Guid id, CancellationTokenSource token) = h.AddMyRun();
        h.Time.Now = h.Time.Now.AddSeconds(120);

        (await h.Leases.TryHoldAsync(id, CancellationToken.None)).Should().BeTrue();
        token.IsCancellationRequested.Should().BeFalse();
        h.Get(id).LeaseExpiresAt.Should().Be(h.Now.AddSeconds(60));
    }

    [Fact]
    public async Task The_sweeper_failing_a_lost_run_clears_its_lease_owner()
    {
        using Harness h = new();
        (Guid id, CancellationTokenSource token) = h.AddMyRun();
        h.Time.Now = h.Time.Now.AddSeconds(120);
        IConfiguration sweeperConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = "engine-sweeper" })
            .Build();
        IServiceScopeFactory factory = h.Services.GetRequiredService<IServiceScopeFactory>();
        ExecutionLeaseService sweeperLeases = new(factory, sweeperConfig, NullLogger<ExecutionLeaseService>.Instance, h.Time);
        ExecutionRecoverySweeper sweeper = new(factory, sweeperLeases, NullLogger<ExecutionRecoverySweeper>.Instance, h.Time);

        (await sweeper.SweepOnceAsync(CancellationToken.None)).Failed.Should().Be(1, "a legacy run (no snapshot) is failed");

        WorkflowExecution row = h.Get(id);
        row.Status.Should().Be(ExecutionStatus.Failed);
        row.ErrorMessage.Should().Contain("engine-a", "the message still names the lost instance");
        row.LeaseOwner.Should().BeNull("the lost instance must find the run is no longer its own");
        (await h.Leases.TryHoldAsync(id, CancellationToken.None)).Should().BeFalse();
        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void A_lost_lease_is_a_platform_failure_never_the_users_stop()
    {
        FailureClassifier.ForException(new OperationCanceledException(), ExecutionLeaseService.LeaseLostReason)
            .Should().Be(FailureClass.PlatformInternal);
        ExecutionLeaseService.LeaseLostReason.ToLowerInvariant().Should().NotContain("user");
    }
}
