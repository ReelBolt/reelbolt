using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// B5 ledger behaviour on Sqlite (logic; the per-org advisory lock is Postgres-only), plus a
/// concurrency test on a real Postgres when <c>REELBOLT_TEST_PG</c> is set (same convention as
/// <see cref="Data.RunnerSchemaTests"/>).
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class CreditLedgerTests : IDisposable
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Clock _clock = new();
    private readonly Guid _org = Guid.NewGuid();

    public CreditLedgerTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    /// <summary>A fresh context per call, as a request scope would be.</summary>
    private async Task<T> L<T>(Func<CreditLedger, Task<T>> action)
    {
        await using InferenceApiDbContext db = NewDb();
        return await action(new CreditLedger(db, _clock));
    }

    private static string K() => Guid.NewGuid().ToString("N");

    private Task<CreditLedgerResult> Grant(long millis, CreditGrantSource source = CreditGrantSource.Plan, DateTime? expires = null)
        => L(l => l.GrantAsync(_org, source, millis, expires, K()));

    private async Task<Dictionary<CreditGrantSource, long>> RemainingBySource()
    {
        await using InferenceApiDbContext db = NewDb();
        return db.CreditGrants.AsEnumerable().GroupBy(g => g.Source).ToDictionary(g => g.Key, g => g.Sum(x => x.RemainingMillis));
    }

    private async Task AssertConsistent()
        => (await L(l => l.IsConsistentAsync(_org))).Should().BeTrue();

    [Fact]
    public async Task Grant_CreatesBalanceAndBucket_AndRejectsNonPositiveAmounts()
    {
        CreditLedgerResult r = await Grant(10_000);
        r.Ok.Should().BeTrue();
        r.BalanceMillis.Should().Be(10_000);
        r.Entry!.Kind.Should().Be(CreditLedgerEntryKind.PlanRefill);
        r.Entry.BalanceAfterMillis.Should().Be(10_000);

        CreditLedgerResult bad = await Grant(0);
        bad.Ok.Should().BeFalse();
        bad.Error.Should().Be(CreditLedgerErrors.InvalidAmount);
        await AssertConsistent();
    }

    [Fact]
    public async Task ReserveThenSettle_ChargesActual_AndFreesTheHold()
    {
        await Grant(10_000);
        CreditLedgerResult reserve = await L(l => l.ReserveAsync(_org, 4_000, K()));
        reserve.Ok.Should().BeTrue();
        reserve.AvailableMillis.Should().Be(6_000);
        reserve.BalanceMillis.Should().Be(10_000);

        CreditLedgerResult settle = await L(l => l.SettleAsync(_org, reserve.Entry!.Id, 3_000, K()));
        settle.Ok.Should().BeTrue();
        settle.BalanceMillis.Should().Be(7_000);
        settle.ReservedMillis.Should().Be(0);
        settle.Entry!.BalanceDeltaMillis.Should().Be(-3_000);
        settle.Entry.ReservedDeltaMillis.Should().Be(-4_000);

        (await L(l => l.SettleAsync(_org, reserve.Entry.Id, 1, K()))).Error.Should().Be(CreditLedgerErrors.ReservationClosed);
        await AssertConsistent();
    }

    [Fact]
    public async Task Reserve_RefusesWhenAvailableIsShort_AndWritesNothing()
    {
        await Grant(1_000);
        CreditLedgerResult ok = await L(l => l.ReserveAsync(_org, 800, K()));
        CreditLedgerResult refused = await L(l => l.ReserveAsync(_org, 300, K()));
        refused.Ok.Should().BeFalse();
        refused.Error.Should().Be(CreditLedgerErrors.InsufficientCredits);
        refused.ReservedMillis.Should().Be(800);

        // An overdraft floor lets the same reservation through.
        (await L(l => l.ReserveAsync(_org, 300, K(), minAvailableAfterMillis: -500))).Ok.Should().BeTrue();

        await using InferenceApiDbContext db = NewDb();
        db.CreditLedgerEntries.Count(e => e.Kind == CreditLedgerEntryKind.Reserve).Should().Be(2);
        ok.Ok.Should().BeTrue();
        await AssertConsistent();
    }

    [Fact]
    public async Task Release_DropsTheHoldWithoutCharging()
    {
        await Grant(5_000);
        CreditLedgerResult reserve = await L(l => l.ReserveAsync(_org, 2_000, K()));
        CreditLedgerResult release = await L(l => l.ReleaseAsync(_org, reserve.Entry!.Id, K()));
        release.Ok.Should().BeTrue();
        release.BalanceMillis.Should().Be(5_000);
        release.ReservedMillis.Should().Be(0);
        (await L(l => l.ReleaseAsync(_org, reserve.Entry.Id, K()))).Error.Should().Be(CreditLedgerErrors.ReservationClosed);
        await AssertConsistent();
    }

    [Fact]
    public async Task Settle_ConsumesNonTopUpFirst_EarliestExpiryFirst_NeverExpiringLast()
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        await Grant(500, CreditGrantSource.TopUp, now.AddDays(1));               // top-up expires soonest but is used last
        await Grant(500, CreditGrantSource.Plan, now.AddDays(10));
        await Grant(500, CreditGrantSource.Plan, now.AddDays(5));
        await Grant(500, CreditGrantSource.Promo, null);                         // never expires: after expiring plan buckets

        CreditLedgerResult reserve = await L(l => l.ReserveAsync(_org, 1_700, K()));
        CreditLedgerResult settle = await L(l => l.SettleAsync(_org, reserve.Entry!.Id, 1_700, K()));
        settle.Ok.Should().BeTrue();

        await using InferenceApiDbContext db = NewDb();
        Dictionary<string, long> byExpiry = db.CreditGrants.AsEnumerable()
            .ToDictionary(g => $"{g.Source}:{(g.ExpiresAt is null ? "never" : (g.ExpiresAt.Value - now).Days.ToString())}", g => g.RemainingMillis);
        byExpiry["Plan:5"].Should().Be(0);
        byExpiry["Plan:10"].Should().Be(0);
        byExpiry["Promo:never"].Should().Be(0);
        byExpiry["TopUp:1"].Should().Be(300);
        await AssertConsistent();
    }

    [Fact]
    public async Task IdempotentReplay_ReturnsTheOriginalEntry_AndChangesNothing()
    {
        string grantKey = K();
        CreditLedgerResult first = await L(l => l.GrantAsync(_org, CreditGrantSource.Promo, 2_000, null, grantKey));
        CreditLedgerResult again = await L(l => l.GrantAsync(_org, CreditGrantSource.Promo, 2_000, null, grantKey));
        again.Replayed.Should().BeTrue();
        again.Entry!.Id.Should().Be(first.Entry!.Id);
        again.BalanceMillis.Should().Be(2_000);

        string reserveKey = K();
        CreditLedgerResult r1 = await L(l => l.ReserveAsync(_org, 500, reserveKey));
        CreditLedgerResult r2 = await L(l => l.ReserveAsync(_org, 500, reserveKey));
        r2.Replayed.Should().BeTrue();
        r2.Entry!.Id.Should().Be(r1.Entry!.Id);
        r2.ReservedMillis.Should().Be(500);

        string settleKey = K();
        await L(l => l.SettleAsync(_org, r1.Entry.Id, 400, settleKey));
        CreditLedgerResult s2 = await L(l => l.SettleAsync(_org, r1.Entry.Id, 400, settleKey));
        s2.Replayed.Should().BeTrue();
        s2.BalanceMillis.Should().Be(1_600);

        await using InferenceApiDbContext db = NewDb();
        db.CreditLedgerEntries.Count().Should().Be(3);
        await AssertConsistent();
    }

    [Fact]
    public async Task IdempotencyKey_UsedByAnotherOrganization_Throws()
    {
        Guid other = Guid.NewGuid();
        await using (InferenceApiDbContext db = NewDb())
        {
            db.Organizations.Add(new Organization { Id = other, Name = "x", Slug = "x", Kind = OrganizationKind.Team });
            await db.SaveChangesAsync();
        }

        string key = K();
        await L(l => l.GrantAsync(_org, CreditGrantSource.Promo, 100, null, key));
        Func<Task> act = () => L(l => l.GrantAsync(other, CreditGrantSource.Promo, 100, null, key));
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Refund_ReturnsCreditsToTheirGrants_AndCannotExceedWhatWasSettled()
    {
        await Grant(1_000, CreditGrantSource.Plan, _clock.GetUtcNow().UtcDateTime.AddDays(10));
        await Grant(1_000, CreditGrantSource.TopUp);
        CreditLedgerResult reserve = await L(l => l.ReserveAsync(_org, 1_500, K()));
        CreditLedgerResult settle = await L(l => l.SettleAsync(_org, reserve.Entry!.Id, 1_500, K()));
        settle.BalanceMillis.Should().Be(500);

        CreditLedgerResult refund = await L(l => l.RefundAsync(_org, settle.Entry!.Id, 600, K()));
        refund.Ok.Should().BeTrue();
        refund.BalanceMillis.Should().Be(1_100);
        // The most recently drawn bucket (the top-up's 500) is restored first, then 100 of the plan bucket.
        Dictionary<CreditGrantSource, long> remaining = await RemainingBySource();
        remaining[CreditGrantSource.TopUp].Should().Be(1_000);
        remaining[CreditGrantSource.Plan].Should().Be(100);

        (await L(l => l.RefundAsync(_org, settle.Entry.Id, 901, K()))).Error.Should().Be(CreditLedgerErrors.RefundExceedsSettled);
        (await L(l => l.RefundAsync(_org, settle.Entry.Id, 900, K()))).Ok.Should().BeTrue();
        (await L(l => l.RefundAsync(_org, settle.Entry.Id, 1, K()))).Ok.Should().BeFalse();
        (await L(l => l.GetBalanceAsync(_org))).BalanceMillis.Should().Be(2_000);
        await AssertConsistent();
    }

    [Fact]
    public async Task Refund_OfAnExpiredBucket_LandsInAGraceBucket()
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        await Grant(1_000, CreditGrantSource.Plan, now.AddDays(1));
        CreditLedgerResult reserve = await L(l => l.ReserveAsync(_org, 1_000, K()));
        CreditLedgerResult settle = await L(l => l.SettleAsync(_org, reserve.Entry!.Id, 1_000, K()));

        _clock.Now = _clock.Now.AddDays(2);
        CreditLedgerResult refund = await L(l => l.RefundAsync(_org, settle.Entry!.Id, 400, K()));
        refund.Ok.Should().BeTrue();
        refund.BalanceMillis.Should().Be(400);

        await using InferenceApiDbContext db = NewDb();
        CreditGrant grace = db.CreditGrants.AsEnumerable().Single(g => g.RemainingMillis > 0);
        grace.Source.Should().Be(CreditGrantSource.Promo);
        grace.ExpiresAt.Should().BeCloseTo(_clock.GetUtcNow().UtcDateTime + CreditLedger.RefundGrace, TimeSpan.FromSeconds(1));
        await AssertConsistent();
    }

    [Fact]
    public async Task Expire_ZeroesLapsedBuckets_OncePerGrant_AndReserveIgnoresThem()
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        await Grant(1_000, CreditGrantSource.Plan, now.AddDays(1));
        await Grant(1_000, CreditGrantSource.TopUp);

        _clock.Now = _clock.Now.AddDays(2);
        // Without a sweep the lapsed 1,000 would back this reservation; reserve sweeps first.
        CreditLedgerResult reserve = await L(l => l.ReserveAsync(_org, 1_500, K()));
        reserve.Ok.Should().BeFalse();
        reserve.BalanceMillis.Should().Be(1_000);

        (await L(l => l.ExpireAsync(_org))).Should().Be(0); // already swept by the reserve attempt
        await using (InferenceApiDbContext db = NewDb())
        {
            db.CreditLedgerEntries.Count(e => e.Kind == CreditLedgerEntryKind.Expire).Should().Be(1);
            db.CreditLedgerEntries.Single(e => e.Kind == CreditLedgerEntryKind.Expire).BalanceDeltaMillis.Should().Be(-1_000);
        }

        await AssertConsistent();
    }

    [Fact]
    public async Task GetBalance_ExcludesLapsedUnsweptGrants_WithoutWriting_AndReserveSweepsOnceAfterwards()
    {
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        await Grant(1_000, CreditGrantSource.Plan, now.AddDays(1));
        await Grant(500, CreditGrantSource.TopUp);

        _clock.Now = _clock.Now.AddDays(2); // the plan grant has lapsed, nothing has swept it
        CreditLedgerResult read = await L(l => l.GetBalanceAsync(_org));
        read.BalanceMillis.Should().Be(500);
        read.AvailableMillis.Should().Be(500);
        await using (InferenceApiDbContext db = NewDb())
        {
            db.CreditLedgerEntries.Count(e => e.Kind == CreditLedgerEntryKind.Expire).Should().Be(0); // a read writes nothing
            db.CreditAccounts.Single().BalanceMillis.Should().Be(1_500);
        }

        CreditLedgerResult reserve = await L(l => l.ReserveAsync(_org, 800, K()));
        reserve.Ok.Should().BeFalse();
        reserve.BalanceMillis.Should().Be(500);
        (await L(l => l.GetBalanceAsync(_org))).BalanceMillis.Should().Be(500); // no double subtraction after the sweep
        (await L(l => l.ReserveAsync(_org, 900, K()))).Ok.Should().BeFalse(); // a second reserve sweeps nothing more
        await using (InferenceApiDbContext db = NewDb())
        {
            db.CreditLedgerEntries.Single(e => e.Kind == CreditLedgerEntryKind.Expire).BalanceDeltaMillis.Should().Be(-1_000);
        }

        await AssertConsistent();
    }

    [Fact]
    public async Task Expire_ReturnsTheExpiredTotal()
    {
        await Grant(700, CreditGrantSource.Plan, _clock.GetUtcNow().UtcDateTime.AddDays(1));
        _clock.Now = _clock.Now.AddDays(1).AddMinutes(1);
        (await L(l => l.ExpireAsync(_org))).Should().Be(700);
        (await L(l => l.ExpireAsync(_org))).Should().Be(0);
        (await L(l => l.GetBalanceAsync(_org))).BalanceMillis.Should().Be(0);
        await AssertConsistent();
    }

    [Fact]
    public async Task Settle_PastTheBalance_Overdraws_AndALaterGrantRepaysTheDebtFirst()
    {
        await Grant(1_000);
        CreditLedgerResult reserve = await L(l => l.ReserveAsync(_org, 1_000, K()));
        CreditLedgerResult settle = await L(l => l.SettleAsync(_org, reserve.Entry!.Id, 1_300, K()));
        settle.Ok.Should().BeTrue();
        settle.BalanceMillis.Should().Be(-300);
        await AssertConsistent();

        CreditLedgerResult topUp = await L(l => l.GrantAsync(_org, CreditGrantSource.TopUp, 1_000, null, K()));
        topUp.BalanceMillis.Should().Be(700);
        (await RemainingBySource())[CreditGrantSource.TopUp].Should().Be(700);
        await AssertConsistent();

        // Refunding an overdrawn settlement restores the overdrawn part and the drawn part.
        CreditLedgerResult refund = await L(l => l.RefundAsync(_org, settle.Entry!.Id, 1_300, K()));
        refund.Ok.Should().BeTrue();
        refund.BalanceMillis.Should().Be(2_000);
        await AssertConsistent();
    }

    [Fact]
    public async Task Adjust_PositiveGrantsPromo_NegativeConsumes()
    {
        await L(l => l.AdjustAsync(_org, 1_000, K()));
        CreditLedgerResult down = await L(l => l.AdjustAsync(_org, -400, K()));
        down.BalanceMillis.Should().Be(600);
        (await L(l => l.AdjustAsync(_org, 0, K()))).Error.Should().Be(CreditLedgerErrors.InvalidAmount);
        await AssertConsistent();
    }

    [Fact]
    public async Task ASettlementOfZero_JustClosesTheReservation()
    {
        await Grant(1_000);
        CreditLedgerResult reserve = await L(l => l.ReserveAsync(_org, 400, K()));
        CreditLedgerResult settle = await L(l => l.SettleAsync(_org, reserve.Entry!.Id, 0, K()));
        settle.Ok.Should().BeTrue();
        settle.BalanceMillis.Should().Be(1_000);
        settle.ReservedMillis.Should().Be(0);
        await AssertConsistent();
    }

    [Fact]
    public async Task Concurrent_Reserves_OnARealPostgres_NeverOverCommit()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_b5_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            DbContextOptions<InferenceApiDbContext> options = new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options;
            Guid org = Guid.NewGuid();
            await using (InferenceApiDbContext setup = new(options))
            {
                await setup.GetService<IMigrator>().MigrateAsync();
                setup.Organizations.Add(new Organization { Id = org, Name = "o", Slug = "o-" + org.ToString("N"), Kind = OrganizationKind.Team });
                await setup.SaveChangesAsync();
            }

            await using (InferenceApiDbContext g = new(options))
            {
                (await new CreditLedger(g).GrantAsync(org, CreditGrantSource.Plan, 5_000, null, K())).Ok.Should().BeTrue();
            }

            // 24 contenders for 5 slots, plus a replayed key. Each has its own context, but they share a pool
            // capped at PostgresTestEnvironment.DefaultMaxPoolSize (10), so up to 10 run at once and the rest
            // queue for a connection; none holds one while waiting on another, so nothing deadlocks.
            string sharedKey = K();
            CreditLedgerResult[] results = await Task.WhenAll(Enumerable.Range(0, 24).Select(async i =>
            {
                await using InferenceApiDbContext db = new(options);
                return await new CreditLedger(db).ReserveAsync(org, 1_000, i < 2 ? sharedKey : K());
            }));

            results.Where(r => r.Ok && !r.Replayed).Count().Should().Be(5);
            results.Where(r => r.Replayed).Count().Should().BeLessThanOrEqualTo(1);

            await using InferenceApiDbContext verify = new(options);
            CreditLedger ledger = new(verify);
            CreditLedgerResult balance = await ledger.GetBalanceAsync(org);
            balance.ReservedMillis.Should().Be(5_000);
            balance.BalanceMillis.Should().Be(5_000);
            (await ledger.IsConsistentAsync(org)).Should().BeTrue();
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
