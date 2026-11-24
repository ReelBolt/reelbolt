using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// B6a gate behaviour on Sqlite (the per-org advisory lock is Postgres-only; the gate's own per-org
/// semaphore covers the single-process case), plus a real-Postgres concurrency test when
/// <c>REELBOLT_TEST_PG</c> is set.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class CreditGateTests : IDisposable
{
    private sealed class Factory(DbContextOptions<WorkflowEngineDbContext> options) : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:;Foreign Keys=False");
    private readonly Factory _factory;
    private readonly Guid _org = Guid.NewGuid();
    private readonly Guid _exec = Guid.NewGuid();

    public CreditGateTests()
    {
        _connection.Open();
        DbContextOptions<WorkflowEngineDbContext> options = new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(_connection).Options;
        // The engine maps the credit tables ExcludeFromMigrations, so the schema comes from the owning context.
        using (InferenceApiDbContext owner = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options))
        {
            owner.Database.EnsureCreated();
        }

        // S-3: ReleaseExecutionAsync reads workflow_step_results next to the ledger, as on Postgres.
        UnsettledHoldTestKit.AddEngineTables(_connection);
        _factory = new Factory(options);
    }

    public void Dispose() => _connection.Dispose();

    private CreditGate Gate(bool enabled = true, IOrganizationEntitlements? ent = null, int tol = 20, long floor = 100)
        => new(_factory, Options.Create(new CreditGateOptions { Enabled = enabled, OverdraftTolerancePercent = tol, OverdraftFloorCredits = floor }),
            NullLogger<CreditGate>.Instance, ent);

    private async Task GrantAsync(long millis)
    {
        await using WorkflowEngineDbContext db = _factory.CreateDbContext();
        (await new CreditLedger(db).GrantAsync(_org, CreditGrantSource.Plan, millis, null, Guid.NewGuid().ToString("N"))).Ok.Should().BeTrue();
    }

    private async Task<CreditLedgerResult> BalanceAsync()
    {
        await using WorkflowEngineDbContext db = _factory.CreateDbContext();
        return await new CreditLedger(db).GetBalanceAsync(_org);
    }

    private async Task<int> EntryCountAsync()
    {
        await using WorkflowEngineDbContext db = _factory.CreateDbContext();
        return await db.CreditLedgerEntries.CountAsync();
    }

    private Task<CreditReservation> Reserve(CreditGate g, long estimate, Guid? step = null, CreditEntitlement req = CreditEntitlement.None)
        => g.ReserveAsync(_org, _exec, step ?? Guid.NewGuid(), estimate, requires: req);

    [Fact]
    public async Task Reserve_Allowed_HoldsTheEstimate_AndSettleChargesActualAndReleasesTheRest()
    {
        await GrantAsync(10_000);
        CreditGate g = Gate();
        CreditReservation r = await Reserve(g, 4_000);
        r.Outcome.Should().Be(CreditReserveOutcome.Allowed);
        r.ReservationId.Should().NotBeNull();
        (await BalanceAsync()).ReservedMillis.Should().Be(4_000);

        CreditSettlement s = await g.SettleAsync(r.ReservationId, 1_500);
        s.Ok.Should().BeTrue();
        s.ChargedMillis.Should().Be(1_500);
        CreditLedgerResult b = await BalanceAsync();
        b.BalanceMillis.Should().Be(8_500);
        b.ReservedMillis.Should().Be(0);
    }

    [Fact]
    public async Task Settle_MayExceedTheReservation_WithinOverdraft()
    {
        await GrantAsync(10_000);
        CreditGate g = Gate();
        CreditReservation r = await Reserve(g, 10_000);
        (await g.SettleAsync(r.ReservationId, 11_500)).Ok.Should().BeTrue();
        (await BalanceAsync()).BalanceMillis.Should().Be(-1_500);
    }

    [Fact]
    public async Task Reserve_IsRefused_WhenShortByMoreThanTheTolerance()
    {
        await GrantAsync(10_000);
        CreditGate g = Gate();
        // Admitted while available - estimate >= -20% of estimate, i.e. up to an estimate of 12_500.
        (await Reserve(g, 12_600)).Outcome.Should().Be(CreditReserveOutcome.InsufficientCredits);
        (await Reserve(g, 12_000)).Outcome.Should().Be(CreditReserveOutcome.Allowed);
    }

    [Fact]
    public async Task Reserve_Headroom_IsDerivedFromTheOverdraftBasis_WhenGiven_AndFromTheHoldOtherwise()
    {
        await GrantAsync(1_000);
        CreditGate g = Gate(); // 20%, floor 100 credits
        // A scaled hold of 2_000: shortfall 1_000 > 20% of 2_000 (400): refused with no basis ...
        (await g.ReserveAsync(_org, _exec, Guid.NewGuid(), 2_000)).Outcome.Should().Be(CreditReserveOutcome.InsufficientCredits);
        // ... and admitted when the full step (10_000) is the basis: headroom 2_000.
        (await g.ReserveAsync(_org, _exec, Guid.NewGuid(), 2_000, overdraftBasisMillis: 10_000)).Outcome.Should().Be(CreditReserveOutcome.Allowed);
    }

    [Fact]
    public async Task Reserve_Headroom_IsCappedByTheAbsoluteFloor()
    {
        await GrantAsync(1_000);
        CreditGate g = Gate(floor: 1); // 1 credit = 1_000 millis
        // 20% of 3_000 is 600 (< floor): 1_000 - 3_000 = -2_000 < -600, refused.
        (await Reserve(g, 3_000)).Outcome.Should().Be(CreditReserveOutcome.InsufficientCredits);
        // 20% of 100_000 would be 20_000, but the floor caps the shortfall at 1_000: -99_000 refused.
        (await Reserve(g, 100_000)).Outcome.Should().Be(CreditReserveOutcome.InsufficientCredits);
        // 1_200 estimate: shortfall 200 <= min(240, 1_000).
        (await Reserve(g, 1_200)).Outcome.Should().Be(CreditReserveOutcome.Allowed);
    }

    [Fact]
    public async Task NegativeBalance_BlocksFurtherReservations()
    {
        await GrantAsync(1_000);
        CreditGate g = Gate();
        CreditReservation r = await Reserve(g, 1_000);
        await g.SettleAsync(r.ReservationId, 5_000);
        (await Reserve(g, 1_000)).Outcome.Should().Be(CreditReserveOutcome.InsufficientCredits);
    }

    [Fact]
    public async Task BillingDisabled_AlwaysAllows_AndWritesNothing()
    {
        CreditGate g = Gate(enabled: false);
        CreditReservation r = await Reserve(g, 999_999_999);
        r.Outcome.Should().Be(CreditReserveOutcome.Allowed);
        r.ReservationId.Should().BeNull();
        (await g.SettleAsync(r.ReservationId, 5)).Ok.Should().BeTrue();
        (await g.ReleaseAsync(r.ReservationId, FailureClass.PlatformInternal)).Ok.Should().BeTrue();
        (await g.ReleaseExecutionAsync(_exec)).Should().Be(0);
        (await EntryCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ZeroEstimate_ShortCircuits_WithNoWrites()
    {
        CreditGate g = Gate();
        CreditReservation r = await Reserve(g, 0);
        r.Outcome.Should().Be(CreditReserveOutcome.Allowed);
        r.ReservationId.Should().BeNull();
        (await EntryCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Entitlement_Denied_BeforeAnyHold()
    {
        await GrantAsync(10_000);
        Mock<IOrganizationEntitlements> ent = new();
        ent.Setup(e => e.GetAsync(_org, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { RoomsAllowed = false }));
        CreditGate g = Gate(ent: ent.Object);
        CreditReservation r = await Reserve(g, 1_000, req: CreditEntitlement.Rooms);
        r.Outcome.Should().Be(CreditReserveOutcome.EntitlementDenied);
        (await BalanceAsync()).ReservedMillis.Should().Be(0);
        (await Reserve(g, 1_000, req: CreditEntitlement.VideoGeneration)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Entitlement_Denied_EvenForAZeroEstimate()
    {
        Mock<IOrganizationEntitlements> ent = new();
        ent.Setup(e => e.GetAsync(_org, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { VideoGenAllowed = false }));
        (await Reserve(Gate(ent: ent.Object), 0, req: CreditEntitlement.VideoGeneration)).Outcome.Should().Be(CreditReserveOutcome.EntitlementDenied);
    }

    [Fact]
    public async Task Audio_generation_entitlement_needs_no_daily_video_budget_and_is_refused_when_the_plan_excludes_it()
    {
        // Decision 6: audio has its own entitlement and no daily cap, so a zero video budget does not refuse it.
        await GrantAsync(10_000);
        Mock<IOrganizationEntitlements> ent = new();
        ent.Setup(e => e.GetAsync(_org, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { VideoGenAllowed = false }));
        (await Reserve(Gate(ent: ent.Object), 1_000, req: CreditEntitlement.AudioGeneration)).Allowed.Should().BeTrue();

        Mock<IOrganizationEntitlements> noAudio = new();
        noAudio.Setup(e => e.GetAsync(_org, It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<OrganizationEntitlementSet>(new OrganizationEntitlementSet { AudioGenAllowed = false }));
        CreditReservation refused = await Reserve(Gate(ent: noAudio.Object), 1_000, req: CreditEntitlement.AudioGeneration);
        refused.Outcome.Should().Be(CreditReserveOutcome.EntitlementDenied);
    }

    [Fact]
    public async Task CrashResume_SameStep_GetsTheSameReservation_AndHoldsOnlyOnce()
    {
        await GrantAsync(10_000);
        CreditGate g = Gate();
        Guid step = Guid.NewGuid();
        CreditReservation a = await Reserve(g, 3_000, step);
        CreditReservation b = await Reserve(g, 3_000, step);
        b.ReservationId.Should().Be(a.ReservationId);
        b.Reason.Should().Be("RESUMED");
        (await BalanceAsync()).ReservedMillis.Should().Be(3_000);

        // A later attempt of the same step is a new hold.
        CreditReservation c = await g.ReserveAsync(_org, _exec, step, 3_000, attempt: 1);
        c.ReservationId.Should().NotBe(a.ReservationId!.Value);
    }

    [Fact]
    public async Task Concurrent_Reserves_NeverOverCommit()
    {
        await GrantAsync(5_000);
        CreditGate g = Gate(tol: 0);
        CreditReservation[] results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Reserve(g, 1_000)));
        results.Count(r => r.Allowed).Should().Be(5);
        results.Count(r => r.Outcome == CreditReserveOutcome.InsufficientCredits).Should().Be(15);
        (await BalanceAsync()).ReservedMillis.Should().Be(5_000);
    }

    [Fact]
    public async Task FailClosed_WhenTheLedgerIsUnavailable()
    {
        Mock<IDbContextFactory<WorkflowEngineDbContext>> broken = new();
        broken.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db down"));
        CreditGate g = new(broken.Object, Options.Create(new CreditGateOptions { Enabled = true }), NullLogger<CreditGate>.Instance);
        CreditReservation r = await g.ReserveAsync(_org, _exec, Guid.NewGuid(), 1_000);
        r.Outcome.Should().Be(CreditReserveOutcome.InsufficientCredits);
        r.Reason.Should().Be("GATE_UNAVAILABLE");
    }

    [Fact]
    public async Task FailClosed_OnMalformedRequests()
    {
        CreditGate g = Gate();
        (await g.ReserveAsync(Guid.Empty, _exec, Guid.NewGuid(), 1_000)).Outcome.Should().Be(CreditReserveOutcome.InsufficientCredits);
        (await g.ReserveAsync(_org, _exec, Guid.Empty, 1_000)).Outcome.Should().Be(CreditReserveOutcome.InsufficientCredits);
        (await g.ReserveAsync(_org, _exec, Guid.NewGuid(), -1)).Outcome.Should().Be(CreditReserveOutcome.InsufficientCredits);
    }

    [Theory]
    [InlineData(FailureClass.PlatformProviderError, true)]
    [InlineData(FailureClass.PlatformInternal, true)]
    [InlineData(FailureClass.None, true)]
    [InlineData((FailureClass)999, true)]
    [InlineData(FailureClass.UserInput, false)]
    [InlineData(FailureClass.UserCancelled, false)]
    [InlineData(FailureClass.ByoProviderError, false)]
    [InlineData(FailureClass.InsufficientCredits, false)]
    [InlineData(FailureClass.EntitlementDenied, false)]
    public async Task Release_OfAnOpenHold_ChargesActual_OnlyForUserClasses(FailureClass cls, bool refunded)
    {
        await GrantAsync(10_000);
        CreditGate g = Gate();
        CreditReservation r = await Reserve(g, 4_000);
        CreditSettlement s = await g.ReleaseAsync(r.ReservationId, cls, actualMillis: 1_200);
        s.Ok.Should().BeTrue();
        CreditLedgerResult b = await BalanceAsync();
        b.ReservedMillis.Should().Be(0);
        b.BalanceMillis.Should().Be(refunded ? 10_000 : 8_800);
    }

    [Fact]
    public async Task Release_OfAnOpenHold_ForAUserInputFailureWithZeroUsage_ChargesNothing()
    {
        // A step that failed on the customer's input before calling any model metered nothing: the hold is
        // released in full, not kept (NO_READABLE_SOURCE / CODE_ARCHIVE_UNREADABLE rely on this).
        await GrantAsync(10_000);
        CreditGate g = Gate();
        CreditReservation r = await Reserve(g, 4_000);
        CreditSettlement s = await g.ReleaseAsync(r.ReservationId, FailureClass.UserInput, actualMillis: 0);
        s.Ok.Should().BeTrue();
        s.ChargedMillis.Should().Be(0);
        CreditLedgerResult b = await BalanceAsync();
        b.ReservedMillis.Should().Be(0);
        b.BalanceMillis.Should().Be(10_000);
    }

    [Fact]
    public async Task Release_OfAnAlreadySettledStep_RefundsPlatformFailures_ToTheSourceGrants_Once()
    {
        await GrantAsync(10_000);
        CreditGate g = Gate();
        CreditReservation r = await Reserve(g, 4_000);
        await g.SettleAsync(r.ReservationId, 3_000);
        (await BalanceAsync()).BalanceMillis.Should().Be(7_000);

        CreditSettlement s = await g.ReleaseAsync(r.ReservationId, FailureClass.PlatformProviderError);
        s.Ok.Should().BeTrue();
        s.RefundedMillis.Should().Be(3_000);
        (await BalanceAsync()).BalanceMillis.Should().Be(10_000);

        CreditSettlement again = await g.ReleaseAsync(r.ReservationId, FailureClass.PlatformProviderError);
        again.Ok.Should().BeTrue();
        again.RefundedMillis.Should().Be(0);
        (await BalanceAsync()).BalanceMillis.Should().Be(10_000);
        await using WorkflowEngineDbContext db = _factory.CreateDbContext();
        (await new CreditLedger(db).IsConsistentAsync(_org)).Should().BeTrue();
    }

    [Fact]
    public async Task Release_OfAnAlreadySettledStep_DoesNotRefundUserClasses()
    {
        await GrantAsync(10_000);
        CreditGate g = Gate();
        CreditReservation r = await Reserve(g, 4_000);
        await g.SettleAsync(r.ReservationId, 3_000);
        (await g.ReleaseAsync(r.ReservationId, FailureClass.UserInput)).RefundedMillis.Should().Be(0);
        (await BalanceAsync()).BalanceMillis.Should().Be(7_000);
    }

    [Fact]
    public async Task Release_OfAnUnknownReservation_IsRefused()
    {
        CreditSettlement s = await Gate().ReleaseAsync(Guid.NewGuid(), FailureClass.PlatformInternal);
        s.Ok.Should().BeFalse();
        s.Error.Should().Be(CreditLedgerErrors.ReservationNotFound);
    }

    [Fact]
    public async Task PlatformFailureHook_ReleasesTheOpenHolds_OfTheRecoveredExecution_Only()
    {
        await GrantAsync(10_000);
        CreditGate g = Gate();
        CreditReservation open1 = await Reserve(g, 2_000);
        CreditReservation open2 = await Reserve(g, 1_000);
        CreditReservation settled = await Reserve(g, 1_500);
        await g.SettleAsync(settled.ReservationId, 1_500);
        CreditReservation other = await g.ReserveAsync(_org, Guid.NewGuid(), Guid.NewGuid(), 500);

        CreditPlatformFailureHook hook = new(g, NullLogger<CreditPlatformFailureHook>.Instance);
        await hook.OnPlatformFailureAsync(new WorkflowExecution { Id = _exec, OrganizationId = _org }, "orphaned", CancellationToken.None);

        CreditLedgerResult b = await BalanceAsync();
        b.ReservedMillis.Should().Be(500); // only the other execution's hold remains
        b.BalanceMillis.Should().Be(8_500); // the settled step stays charged
        open1.Allowed.Should().BeTrue();
        open2.Allowed.Should().BeTrue();
        other.Allowed.Should().BeTrue();

        // Idempotent: a second sweep finds nothing left to release.
        (await g.ReleaseExecutionAsync(_exec)).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_Reserves_OnARealPostgres_NeverOverCommit()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_b6a_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            Guid org = Guid.NewGuid();
            await using (InferenceApiDbContext setup = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options))
            {
                await setup.GetService<IMigrator>().MigrateAsync();
                // S-3: ReleaseExecutionAsync reads the engine's workflow_step_results, as in production.
                await using (WorkflowEngineDbContext engine = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(csb.ConnectionString).Options))
                {
                    await engine.Database.MigrateAsync();
                }

                setup.Organizations.Add(new Organization { Id = org, Name = "o", Slug = "o-" + org.ToString("N"), Kind = OrganizationKind.Team });
                await setup.SaveChangesAsync();
            }

            Factory factory = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(csb.ConnectionString).Options);
            await using (WorkflowEngineDbContext g = factory.CreateDbContext())
            {
                (await new CreditLedger(g).GrantAsync(org, CreditGrantSource.Plan, 5_000, null, "grant")).Ok.Should().BeTrue();
            }

            // Two gate instances model two engine replicas; the static per-org semaphore is shared in-process,
            // so the advisory lock inside the ledger is what also covers separate processes.
            CreditGate[] gates = [PgGate(factory), PgGate(factory)];
            Guid exec = Guid.NewGuid();
            Guid sharedStep = Guid.NewGuid();
            CreditReservation[] results = await Task.WhenAll(Enumerable.Range(0, 24).Select(i =>
                Task.Run(() => gates[i % 2].ReserveAsync(org, exec, i < 2 ? sharedStep : Guid.NewGuid(), 1_000))));

            results.Select(r => r.ReservationId).Where(id => id is not null).Distinct().Count().Should().Be(5);

            await using WorkflowEngineDbContext verify = factory.CreateDbContext();
            CreditLedger ledger = new(verify);
            CreditLedgerResult balance = await ledger.GetBalanceAsync(org);
            balance.ReservedMillis.Should().Be(5_000);
            (await ledger.IsConsistentAsync(org)).Should().BeTrue();

            // The platform-failure path on Postgres: release every hold of the execution.
            (await gates[0].ReleaseExecutionAsync(exec)).Should().Be(5);
            (await new CreditLedger(verify).GetBalanceAsync(org)).ReservedMillis.Should().Be(0);
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static CreditGate PgGate(IDbContextFactory<WorkflowEngineDbContext> factory)
        => new(factory, Options.Create(new CreditGateOptions { Enabled = true, OverdraftTolerancePercent = 0 }), NullLogger<CreditGate>.Instance);
}
