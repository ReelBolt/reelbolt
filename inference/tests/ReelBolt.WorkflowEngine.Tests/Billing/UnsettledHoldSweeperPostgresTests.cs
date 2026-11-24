#nullable enable

using System;
using System.Collections.Concurrent;
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
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// S-3 on a real Postgres, both schemas migrated. Two replicas race over the same open holds. Each
/// replica has its own pool, its own gate with its own in-process lock table (so only the ledger's
/// advisory lock serialises them), and its own lease service. The ledger key plus the per-org
/// advisory lock must close each hold exactly once and move the balance once.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class UnsettledHoldSweeperPostgresTests
{
    private const int Holds = 12;
    private const long HoldMillis = 2_000;
    private const long RecordedMillis = 1_500;
    private const long Grant = 100_000;

    private sealed class Factory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    /// <summary>A scope whose only service is the engine's context, which is all the lease service asks of one.</summary>
    private sealed class ScopeOver(IDbContextFactory<WorkflowEngineDbContext> factory) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(factory.CreateDbContext());

        private sealed class Scope(WorkflowEngineDbContext db) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;
            public object? GetService(Type serviceType) => serviceType == typeof(WorkflowEngineDbContext) ? db : null;
            public void Dispose() => db.Dispose();
        }
    }

    /// <summary>A fresh database with both schemas migrated and an org, project and workflow seeded. Dropped on dispose.</summary>
    private sealed class PgDatabase : IAsyncDisposable
    {
        private readonly string _baseConnection;
        private readonly NpgsqlDataSource _admin;

        private PgDatabase(string baseConnection, NpgsqlDataSource admin, string name)
        {
            _baseConnection = baseConnection;
            _admin = admin;
            Name = name;
        }

        public string Name { get; }
        public Guid Org { get; } = Guid.NewGuid();
        public Guid Project { get; } = Guid.NewGuid();
        public Guid Definition { get; } = Guid.NewGuid();
        public Guid Agent { get; } = Guid.NewGuid();

        public static async Task<PgDatabase> CreateAsync(string baseConnection)
        {
            NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            PgDatabase database = new(baseConnection, admin, "reelbolt_s3_test_" + Guid.NewGuid().ToString("N")[..12]);
            try
            {
                await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {database.Name}"))
                {
                    await create.ExecuteNonQueryAsync();
                }

                await database.MigrateAndSeedAsync();
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        /// <summary>A pool for one replica, labelled with its application name as two processes would be.</summary>
        public Factory Replica(string application)
            => new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseNpgsql(PostgresTestEnvironment.ForDatabase(_baseConnection, Name, maxPoolSize: 4) + ";Application Name=" + application)
                .Options);

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using (NpgsqlCommand drop = _admin.CreateCommand($"DROP DATABASE IF EXISTS {Name} WITH (FORCE)"))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await _admin.DisposeAsync();
        }

        private async Task MigrateAndSeedAsync()
        {
            string connection = PostgresTestEnvironment.ForDatabase(_baseConnection, Name);
            await using (InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            await using (WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connection).Options))
            {
                await engine.Database.MigrateAsync();
            }

            Guid user = Guid.NewGuid();
            await using (NpgsqlConnection c = new(connection))
            {
                await c.OpenAsync();
                foreach (string sql in new[]
                {
                    $"INSERT INTO application_users (id,email,display_name,created_at,must_change_password,is_admin) VALUES ('{user}','s3@t.local','s3',now(),false,false)",
                    $"INSERT INTO organizations (id,name,slug,kind,created_at,updated_at,compute_preference) VALUES ('{Org}','o','o-{Org:N}','Team',now(),now(),'Auto')",
                    $"INSERT INTO projects (id,name,owner_id,organization_id,status,created_at,updated_at) VALUES ('{Project}','p','{user}','{Org}','Draft',now(),now())",
                    $"INSERT INTO workflow_definitions (id,name,project_id,created_at,updated_at) VALUES ('{Definition}','w','{Project}',now(),now())",
                })
                {
                    await using NpgsqlCommand cmd = new(sql, c);
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            // agent_definitions belongs to the API (its jsonb columns are mapped there), so seed it through its owner.
            AgentDefinition agent = new() { Id = Agent, Name = "A", AgentType = AgentType.VideoStoryEditor, SystemPrompt = "", OrganizationId = Org };
            await using (InferenceApiDbContext api = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connection).Options))
            {
                api.AgentDefinitions.Add(agent);
                await api.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task Two_concurrent_sweeps_close_each_open_hold_exactly_once()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        await using PgDatabase database = await PgDatabase.CreateAsync(baseConnection);
        Factory replicaA = database.Replica("replica-a"), replicaB = database.Replica("replica-b");
        (Guid exec, List<Guid> steps) = await SeedRunAsync(replicaA, database, ExecutionStatus.Passed, StepStatus.Completed, unsettledMillis: RecordedMillis);
        CreditGate gateA = PgGate(replicaA), gateB = PgGate(replicaB);
        await ReserveAsync(gateA, database.Org, exec, steps);

        // The holds were made "now"; both sweepers live an hour later and start from one barrier.
        FixedTime later = new(DateTimeOffset.UtcNow.AddHours(1));
        int[] closed = await RaceAsync(
            () => Sweeper(replicaA, gateA, later).SweepOnceAsync(CancellationToken.None),
            () => Sweeper(replicaB, gateB, later).SweepOnceAsync(CancellationToken.None));

        closed.Sum().Should().BeGreaterThanOrEqualTo(Holds, "every hold is closed by one sweeper or the other (a replay counts as closed)");

        await using WorkflowEngineDbContext verify = replicaA.CreateDbContext();
        List<CreditLedgerEntry> ledger = await verify.CreditLedgerEntries.AsNoTracking().Where(e => e.OrganizationId == database.Org).ToListAsync();
        List<CreditLedgerEntry> reserves = ledger.Where(e => e.Kind == CreditLedgerEntryKind.Reserve).ToList();
        reserves.Should().HaveCount(Holds);
        foreach (CreditLedgerEntry reserve in reserves)
        {
            ledger.Where(e => e.RefEntryId == reserve.Id).Should().ContainSingle(e => e.Kind == CreditLedgerEntryKind.Settle)
                .Which.IdempotencyKey.Should().Be($"gate-settle:{reserve.Id:N}");
        }

        ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Release || e.Kind == CreditLedgerEntryKind.Refund);
        CreditLedger check = new(verify);
        CreditLedgerResult balance = await check.GetBalanceAsync(database.Org);
        balance.BalanceMillis.Should().Be(Grant - (Holds * RecordedMillis), "the balance moved exactly once per hold");
        balance.ReservedMillis.Should().Be(0);
        (await check.IsConsistentAsync(database.Org)).Should().BeTrue();

        // A third pass finds nothing left.
        (await Sweeper(replicaB, gateB, later).SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Two_replicas_never_sweep_a_running_steps_hold_while_its_engine_renews_the_lease()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        await using PgDatabase database = await PgDatabase.CreateAsync(baseConnection);
        Factory replicaA = database.Replica("replica-a"), replicaB = database.Replica("replica-b");
        (Guid exec, List<Guid> steps) = await SeedRunAsync(
            replicaA, database, ExecutionStatus.Cancelled, StepStatus.Running,
            leaseOwner: "replica-a", leaseExpiresAt: DateTime.UtcNow.AddSeconds(-30));
        CreditGate gateA = PgGate(replicaA), gateB = PgGate(replicaB);
        List<Guid> reserves = await ReserveAsync(gateA, database.Org, exec, steps);

        // Replica A's engine is still executing the run it was told to stop, so at T it renews the lease.
        FixedTime atT = new(DateTimeOffset.UtcNow.AddHours(49));
        ExecutionLeaseService leasesA = LeasesOf(replicaA, "replica-a", atT);
        leasesA.Track(exec);
        (await leasesA.RenewAsync(CancellationToken.None)).Should().Be(1, "a Cancelled row this instance owns is renewed on real Postgres");

        int[] raced = await RaceAsync(
            () => Sweeper(replicaA, gateA, atT, leasesA).SweepOnceAsync(CancellationToken.None),
            () => Sweeper(replicaB, gateB, atT).SweepOnceAsync(CancellationToken.None));
        raced.Sum().Should().Be(0, "neither replica closes a hold whose step is Running while its engine renews the lease");

        // The steps then finish: every hold is settled once, through replica A.
        foreach (Guid reserve in reserves)
        {
            (await gateA.SettleOpenAsync(reserve, RecordedMillis)).Ok.Should().BeTrue();
        }

        await using (WorkflowEngineDbContext db = replicaA.CreateDbContext())
        {
            await db.WorkflowStepResults.Where(s => s.WorkflowExecutionId == exec)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, StepStatus.Completed));
        }

        raced = await RaceAsync(
            () => Sweeper(replicaA, gateA, atT, leasesA).SweepOnceAsync(CancellationToken.None),
            () => Sweeper(replicaB, gateB, atT).SweepOnceAsync(CancellationToken.None));
        raced.Sum().Should().Be(0, "every hold is already closed");

        await using WorkflowEngineDbContext verify = replicaA.CreateDbContext();
        List<CreditLedgerEntry> ledger = await verify.CreditLedgerEntries.AsNoTracking().Where(e => e.OrganizationId == database.Org).ToListAsync();
        foreach (CreditLedgerEntry reserve in ledger.Where(e => e.Kind == CreditLedgerEntryKind.Reserve))
        {
            ledger.Where(e => e.RefEntryId == reserve.Id).Should().ContainSingle(e => e.Kind == CreditLedgerEntryKind.Settle)
                .Which.IdempotencyKey.Should().Be($"gate-settle:{reserve.Id:N}");
        }

        ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Release || e.Kind == CreditLedgerEntryKind.Refund);
        CreditLedger check = new(verify);
        CreditLedgerResult balance = await check.GetBalanceAsync(database.Org);
        balance.BalanceMillis.Should().Be(Grant - (Holds * RecordedMillis));
        balance.ReservedMillis.Should().Be(0);
        (await check.IsConsistentAsync(database.Org)).Should().BeTrue();
    }

    [Fact]
    public async Task Two_replicas_release_a_running_steps_hold_exactly_once_after_its_engine_is_gone()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        await using PgDatabase database = await PgDatabase.CreateAsync(baseConnection);
        Factory replicaA = database.Replica("replica-a"), replicaB = database.Replica("replica-b");
        (Guid exec, List<Guid> steps) = await SeedRunAsync(
            replicaA, database, ExecutionStatus.Running, StepStatus.Running,
            leaseOwner: "replica-a", leaseExpiresAt: DateTime.UtcNow.AddHours(-1));
        CreditGate gateA = PgGate(replicaA), gateB = PgGate(replicaB);
        await ReserveAsync(gateA, database.Org, exec, steps);

        // The engine is gone and its lease lapsed long ago; nothing renews it, so both replicas may close the holds.
        FixedTime atT = new(DateTimeOffset.UtcNow.AddHours(49));
        int[] closed = await RaceAsync(
            () => Sweeper(replicaA, gateA, atT).SweepOnceAsync(CancellationToken.None),
            () => Sweeper(replicaB, gateB, atT).SweepOnceAsync(CancellationToken.None));

        closed.Sum().Should().BeGreaterThanOrEqualTo(Holds, "every hold is closed by one sweeper or the other (a replay counts as closed)");

        await using WorkflowEngineDbContext verify = replicaA.CreateDbContext();
        List<CreditLedgerEntry> ledger = await verify.CreditLedgerEntries.AsNoTracking().Where(e => e.OrganizationId == database.Org).ToListAsync();
        foreach (CreditLedgerEntry reserve in ledger.Where(e => e.Kind == CreditLedgerEntryKind.Reserve))
        {
            CreditLedgerEntry release = ledger.Where(e => e.RefEntryId == reserve.Id).Should().ContainSingle(e => e.Kind == CreditLedgerEntryKind.Release).Subject;
            release.Note.Should().Be(nameof(FailureClass.PlatformInternal));
        }

        ledger.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Settle || e.Kind == CreditLedgerEntryKind.Refund);
        CreditLedger check = new(verify);
        CreditLedgerResult balance = await check.GetBalanceAsync(database.Org);
        balance.BalanceMillis.Should().Be(Grant, "a released hold moves nothing");
        balance.ReservedMillis.Should().Be(0);
        (await check.IsConsistentAsync(database.Org)).Should().BeTrue();

        (await Sweeper(replicaB, gateB, atT).SweepOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    /// <summary>One execution with <see cref="Holds"/> step results in the given state, its lease stamped as given.</summary>
    private static async Task<(Guid Execution, List<Guid> Steps)> SeedRunAsync(
        Factory replica,
        PgDatabase database,
        ExecutionStatus status,
        StepStatus stepStatus,
        long? unsettledMillis = null,
        string? leaseOwner = null,
        DateTime? leaseExpiresAt = null)
    {
        Guid wfStep = Guid.NewGuid(), exec = Guid.NewGuid();
        List<Guid> steps = Enumerable.Range(0, Holds).Select(_ => Guid.NewGuid()).ToList();
        await using WorkflowEngineDbContext db = replica.CreateDbContext();
        db.WorkflowSteps.Add(new WorkflowStep { Id = wfStep, WorkflowDefinitionId = database.Definition, StepOrder = 1, StepType = StepType.Agent, AgentDefinitionId = database.Agent });
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = exec,
            WorkflowDefinitionId = database.Definition,
            ProjectId = database.Project,
            OrganizationId = database.Org,
            Status = status,
            CorrelationId = "c",
            LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpiresAt
        });
        foreach (Guid id in steps)
        {
            db.WorkflowStepResults.Add(new WorkflowStepResult
            {
                Id = id,
                WorkflowExecutionId = exec,
                WorkflowStepId = wfStep,
                Status = stepStatus,
                CreditsUnsettledMillis = unsettledMillis
            });
        }

        await db.SaveChangesAsync();
        (await new CreditLedger(db).GrantAsync(database.Org, CreditGrantSource.Plan, Grant, null, "grant")).Ok.Should().BeTrue();
        return (exec, steps);
    }

    private static async Task<List<Guid>> ReserveAsync(CreditGate gate, Guid org, Guid exec, List<Guid> steps)
    {
        List<Guid> reserves = [];
        foreach (Guid step in steps)
        {
            CreditReservation r = await gate.ReserveAsync(org, exec, step, HoldMillis);
            r.Allowed.Should().BeTrue();
            reserves.Add(r.ReservationId!.Value);
        }

        return reserves;
    }

    private static UnsettledHoldSweeper Sweeper(Factory replica, ICreditGate gate, TimeProvider time, ExecutionLeaseService? leases = null)
        => new(
            replica, gate, Options.Create(new SettleSweepOptions { MinAgeSeconds = 600, MaxPerSweep = 100 }),
            Options.Create(new CreditGateOptions { Enabled = true }), NullLogger<UnsettledHoldSweeper>.Instance,
            time, leases);

    /// <summary>A lease service standing for one replica's engine, whose clock is <paramref name="time"/>.</summary>
    private static ExecutionLeaseService LeasesOf(Factory replica, string instance, TimeProvider time)
        => new(
            new ScopeOver(replica),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowEngine:InstanceId"] = instance }).Build(),
            NullLogger<ExecutionLeaseService>.Instance,
            time);

    /// <summary>Starts every sweep from one barrier, so the replicas really contend for the same holds.</summary>
    private static async Task<int[]> RaceAsync(params Func<Task<int>>[] sweeps)
    {
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int>[] runs = sweeps.Select(sweep => Task.Run(async () =>
        {
            await start.Task;
            return await sweep();
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(runs);
    }

    /// <summary>A gate standing for one replica: its own in-process lock table, so only the ledger serialises it against the other.</summary>
    private static CreditGate PgGate(IDbContextFactory<WorkflowEngineDbContext> factory)
        => new(
            factory,
            Options.Create(new CreditGateOptions { Enabled = true, OverdraftTolerancePercent = 0 }),
            NullLogger<CreditGate>.Instance,
            new ConcurrentDictionary<Guid, SemaphoreSlim>());
}
