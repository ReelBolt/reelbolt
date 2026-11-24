using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Consumers;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Messaging;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Execution;

/// <summary>
/// F11's acceptance tests, run against the real reader over a real (in-memory) table, because the
/// interesting behaviour is precisely what happens when a row appears or the store goes away.
///
/// <list type="bullet">
/// <item>"kill switch on means new runs stay Queued"</item>
/// <item>"org limit 1 means the second run waits"</item>
/// </list>
///
/// <para>
/// "Stays Queued" is asserted on the ROW, not on the return value: a refusal that had already
/// flipped the execution to Running, or that threw the message away, would satisfy a weaker test
/// while destroying the run.
/// </para>
/// </summary>
public class PlatformKillSwitchAdmissionTests
{
    [Fact]
    public async Task Killing_compute_means_a_new_run_stays_Queued()
    {
        Guid org = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        int executionsRun = 0;

        var (provider, runner) = Build(
            switches: [new PlatformSetting { Key = PlatformSetting.ComputeKey, Paused = true }],
            executions: [Queued(executionId, org)],
            body: _ => { Interlocked.Increment(ref executionsRun); return Task.CompletedTask; });

        await using (provider)
        {
            AdmissionDecision decision = await runner.DispatchAsync(executionId, "corr", CancellationToken.None);

            decision.Admitted.Should().BeFalse();
            decision.Reason.Should().Be("compute_paused");
            runner.RunningCount.Should().Be(0, "nothing may start while compute is paused");
            executionsRun.Should().Be(0);

            // The whole point: the run is not lost, it is waiting. ExecutionRecoverySweeper
            // re-publishes a Queued row once it has been queued for its stale window.
            ExecutionStatus status = await StatusOfAsync(provider, executionId);
            status.Should().Be(ExecutionStatus.Queued);
        }
    }

    [Fact]
    public async Task The_switch_being_off_again_admits_the_same_run()
    {
        // The other half of the pair: the refusal above must be reversible, or "paused" would be a
        // slower way of saying "lost".
        Guid org = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        var (provider, runner) = Build(
            switches: [new PlatformSetting { Key = PlatformSetting.ComputeKey, Paused = true }],
            executions: [Queued(executionId, org)]);

        await using (provider)
        {
            (await runner.DispatchAsync(executionId, "corr", CancellationToken.None)).Admitted.Should().BeFalse();

            await SetSwitchAsync(provider, PlatformSetting.ComputeKey, paused: false);

            // The reader caches for ten seconds on purpose; time is advanced rather than waited out.
            AdvanceClock(provider, TimeSpan.FromSeconds(11));

            AdmissionDecision decision = await runner.DispatchAsync(executionId, "corr", CancellationToken.None);
            decision.Admitted.Should().BeTrue("resuming must take effect, and within the documented TTL");
        }
    }

    [Fact]
    public async Task An_organization_limit_of_one_means_the_second_run_waits()
    {
        Guid org = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        var (provider, runner) = Build(
            cap: 1,
            executions: [Queued(executionId, org), Running(Guid.NewGuid(), org)]);

        await using (provider)
        {
            AdmissionDecision decision = await runner.DispatchAsync(executionId, "corr", CancellationToken.None);

            decision.Admitted.Should().BeFalse();
            decision.Reason.Should().Be("organization_concurrency");
            (await StatusOfAsync(provider, executionId)).Should().Be(ExecutionStatus.Queued);
        }
    }

    [Fact]
    public async Task A_run_of_another_organization_does_not_consume_this_ones_limit()
    {
        Guid org = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        var (provider, runner) = Build(
            cap: 1,
            executions: [Queued(executionId, org), Running(Guid.NewGuid(), other)]);

        await using (provider)
        {
            (await runner.DispatchAsync(executionId, "corr", CancellationToken.None)).Admitted.Should().BeTrue();
        }
    }

    [Fact]
    public async Task This_replicas_own_running_execution_is_left_to_the_local_semaphore()
    {
        // The cross-replica count would otherwise refuse a run the process is perfectly able to wait
        // for: FairShareAdmission already caps this replica's own runs, and it resumes the waiter the
        // moment a slot frees. Counting it here too would push the second run back to the queue for
        // the whole stale window instead of seconds.
        Guid org = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        Guid mine = Guid.NewGuid();

        var leases = new ExecutionLeaseService(
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            NullLogger<ExecutionLeaseService>.Instance);
        leases.Track(mine);

        var (provider, runner) = Build(
            cap: 1,
            leases: leases,
            executions: [Queued(executionId, org), Running(mine, org)]);

        await using (provider)
        {
            (await runner.DispatchAsync(executionId, "corr", CancellationToken.None)).Admitted.Should().BeTrue();
        }
    }

    [Fact]
    public async Task A_disabled_organization_is_not_admitted()
    {
        Guid org = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        var (provider, runner) = Build(
            switches: [new PlatformSetting { Key = PlatformSetting.OrganizationKey(org), Paused = true }],
            executions: [Queued(executionId, org)]);

        await using (provider)
        {
            AdmissionDecision decision = await runner.DispatchAsync(executionId, "corr", CancellationToken.None);
            decision.Reason.Should().Be("organization_disabled");
        }
    }

    [Fact]
    public async Task Pausing_platform_inference_stops_an_organization_that_runs_on_our_account()
    {
        Guid org = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        var (provider, runner) = Build(
            switches: [new PlatformSetting { Key = PlatformSetting.InferenceKey, Paused = true }],
            executions: [Queued(executionId, org)]);

        await using (provider)
        {
            AdmissionDecision decision = await runner.DispatchAsync(executionId, "corr", CancellationToken.None);
            decision.Admitted.Should().BeFalse();
            decision.Reason.Should().Be("platform_inference_paused");
        }
    }

    [Fact]
    public async Task Pausing_platform_inference_lets_an_organization_paying_its_own_bill_run()
    {
        Guid org = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        var (provider, runner) = Build(
            switches: [new PlatformSetting { Key = PlatformSetting.InferenceKey, Paused = true }],
            executions: [Queued(executionId, org)],
            inferenceProviders: [new InferenceProvider { Id = Guid.NewGuid(), OrganizationId = org, Name = "own" }]);

        await using (provider)
        {
            (await runner.DispatchAsync(executionId, "corr", CancellationToken.None)).Admitted.Should().BeTrue();
        }
    }

    [Fact]
    public async Task An_unreadable_settings_store_refuses_rather_than_admitting()
    {
        // The guard's contract with the reader: an unknown state is a refusal, in the guard's own
        // vocabulary ("settings_unavailable"), so an operator can tell a store outage from a
        // deliberate pause in the logs and the metric tag.
        Guid org = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        var (provider, runner) = Build(
            executions: [Queued(executionId, org)],
            switchesOverride: new ThrowingSwitches());

        await using (provider)
        {
            AdmissionDecision decision = await runner.DispatchAsync(executionId, "corr", CancellationToken.None);
            decision.Admitted.Should().BeFalse();
            decision.Reason.Should().Be("settings_unavailable");
            (await StatusOfAsync(provider, executionId)).Should().Be(ExecutionStatus.Queued);
        }
    }

    [Fact]
    public async Task An_execution_with_no_organization_is_still_bound_by_the_global_switches()
    {
        // Messages published before tenancy carry no organization (WorkflowExecutionRunner falls back
        // to the row). The platform-wide switches must still apply to them: "no org" is not a way
        // around a pause.
        Guid executionId = Guid.NewGuid();

        var (provider, runner) = Build(
            switches: [new PlatformSetting { Key = PlatformSetting.ComputeKey, Paused = true }],
            executions: [Queued(executionId, Guid.Empty)]);

        await using (provider)
        {
            AdmissionDecision decision = await runner.DispatchAsync(executionId, "corr", CancellationToken.None);
            decision.Reason.Should().Be("compute_paused");
        }
    }

    [Fact]
    public async Task A_refused_execution_is_acknowledged_rather_than_dead_lettered()
    {
        // The consumer's half of the contract, which is the difference between a pause and an
        // incident: Consume must RETURN. Throwing would hand the message to MassTransit's retries and
        // then to the dead-letter queue, where the run would sit until a human replayed it — and the
        // execution row would still say Queued while nothing was ever going to pick it up.
        Guid org = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();

        var (provider, runner) = Build(
            switches: [new PlatformSetting { Key = PlatformSetting.ComputeKey, Paused = true }],
            executions: [Queued(executionId, org)]);

        await using (provider)
        {
            var consumer = new WorkflowExecutionRequestedConsumer(
                runner, NullLogger<WorkflowExecutionRequestedConsumer>.Instance);
            var context = new Mock<ConsumeContext<WorkflowExecutionRequested>>();
            context.SetupGet(c => c.Message).Returns(new WorkflowExecutionRequested
            {
                ExecutionId = executionId,
                CorrelationId = "corr",
                OrganizationId = org
            });
            context.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

            await consumer.Consume(context.Object);

            runner.RunningCount.Should().Be(0);
            (await StatusOfAsync(provider, executionId)).Should().Be(
                ExecutionStatus.Queued, "the row is left for ExecutionRecoverySweeper to re-publish");
        }
    }

    // --- harness ---------------------------------------------------------------------------------

    private static WorkflowExecution Queued(Guid id, Guid organizationId) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        Status = ExecutionStatus.Queued,
        QueuedAt = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc)
    };

    private static WorkflowExecution Running(Guid id, Guid organizationId) => new()
    {
        Id = id,
        OrganizationId = organizationId,
        Status = ExecutionStatus.Running,
        StartedAt = new DateTime(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc)
    };

    private static (ServiceProvider Provider, WorkflowExecutionRunner Runner) Build(
        IEnumerable<PlatformSetting>? switches = null,
        IEnumerable<WorkflowExecution>? executions = null,
        IEnumerable<InferenceProvider>? inferenceProviders = null,
        int cap = OrganizationEntitlementSet.UnlimitedInt,
        ExecutionLeaseService? leases = null,
        Func<CancellationToken, Task>? body = null,
        IPlatformSwitches? switchesOverride = null)
    {
        string dbName = Guid.NewGuid().ToString();
        var clock = new MutableClock(new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:MaxConcurrency"] = "4" })
            .Build());
        services.AddSingleton<TimeProvider>(clock);
        services.AddDbContextFactory<WorkflowEngineDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IOrganizationEntitlements>(new FixedEntitlements(cap));
        services.AddSingleton<IPlatformSwitches>(sp => switchesOverride ?? new PlatformSwitches(
            sp.GetRequiredService<IDbContextFactory<WorkflowEngineDbContext>>(),
            NullLogger<PlatformSwitches>.Instance,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ExecutionCancellationRegistry>();
        services.AddSingleton<IDecisionQuestionMemoizer, DecisionQuestionMemoizer>();
        services.AddScoped<WorkflowExecutorService>(_ => new StubExecutorService(body ?? (_ => Task.CompletedTask)));
        services.AddSingleton<ExecutionAdmissionGuard>(sp => new ExecutionAdmissionGuard(
            sp.GetRequiredService<IPlatformSwitches>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ExecutionAdmissionGuard>.Instance,
            leases,
            sp.GetRequiredService<IOrganizationEntitlements>()));
        services.AddSingleton<WorkflowExecutionRunner>(sp => new WorkflowExecutionRunner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IConfiguration>(),
            sp.GetRequiredService<ExecutionCancellationRegistry>(),
            sp.GetRequiredService<IDecisionQuestionMemoizer>(),
            sp.GetRequiredService<ExecutionAdmissionGuard>(),
            NullLogger<WorkflowExecutionRunner>.Instance,
            leases,
            sp.GetRequiredService<IOrganizationEntitlements>()));

        ServiceProvider provider = services.BuildServiceProvider();

        using (WorkflowEngineDbContext db = provider.GetRequiredService<IDbContextFactory<WorkflowEngineDbContext>>().CreateDbContext())
        {
            if (switches is not null)
            {
                db.PlatformSettings.AddRange(switches);
            }

            if (executions is not null)
            {
                db.WorkflowExecutions.AddRange(executions);
            }

            if (inferenceProviders is not null)
            {
                db.InferenceProviders.AddRange(inferenceProviders);
            }

            db.SaveChanges();
        }

        return (provider, provider.GetRequiredService<WorkflowExecutionRunner>());
    }

    private static async Task<ExecutionStatus> StatusOfAsync(IServiceProvider provider, Guid executionId)
    {
        using WorkflowEngineDbContext db = provider.GetRequiredService<IDbContextFactory<WorkflowEngineDbContext>>().CreateDbContext();
        return await db.WorkflowExecutions.Where(e => e.Id == executionId).Select(e => e.Status).SingleAsync();
    }

    private static async Task SetSwitchAsync(IServiceProvider provider, string key, bool paused)
    {
        using WorkflowEngineDbContext db = provider.GetRequiredService<IDbContextFactory<WorkflowEngineDbContext>>().CreateDbContext();
        PlatformSetting row = await db.PlatformSettings.SingleAsync(s => s.Key == key);
        row.Paused = paused;
        await db.SaveChangesAsync();
    }

    /// <summary>Moves the clock past the reader's TTL without sleeping through it.</summary>
    private static void AdvanceClock(IServiceProvider provider, TimeSpan by)
        => ((MutableClock)provider.GetRequiredService<TimeProvider>()).Advance(by);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FixedEntitlements(int cap) : IOrganizationEntitlements
    {
        public ValueTask<OrganizationEntitlementSet> GetAsync(Guid organizationId, CancellationToken cancellationToken = default)
            => new(new OrganizationEntitlementSet { MaxConcurrentExecutions = cap });
    }

    /// <summary>A reader whose store is unreachable, in the shape the real one reports it.</summary>
    private sealed class ThrowingSwitches : IPlatformSwitches
    {
        public ValueTask<PlatformSwitchState> GetAsync(CancellationToken cancellationToken = default)
            => new(PlatformSwitchState.Unknown);
    }

    private sealed class StubExecutorService : WorkflowExecutorService
    {
        private readonly Func<CancellationToken, Task> _body;

        public StubExecutorService(Func<CancellationToken, Task> body)
            : base(
                scopeFactory: null!,
                eventPublisher: null!,
                logger: NullLogger<WorkflowExecutorService>.Instance,
                executors: Array.Empty<IStepExecutor>(),
                rabbitHelper: new RabbitMqHelper(new ConfigurationBuilder().Build()),
                hardeningOptions: Microsoft.Extensions.Options.Options.Create(new WorkflowHardeningOptions()),
                cancellationRegistry: new ExecutionCancellationRegistry())
            => _body = body;

        public override Task ExecuteAsync(Guid executionId, string correlationId, CancellationToken ct) => _body(ct);
    }
}
