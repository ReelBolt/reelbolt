#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// Annual terms: month-0 refill that lives one month, the monthly grant sweep (catch-up, idempotency, skips),
/// the switch from monthly, month-window proration and allowance, and the 14-day refund rule. Sqlite in-memory.
/// </summary>
public sealed class AnnualCreditsTests : IDisposable
{
    private const string P = "fake";
    private static readonly DateTime A0 = new(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AEnd = A0.AddYears(1);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _org = Guid.NewGuid();

    public AnnualCreditsTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, PriceMonthlyUsd = 29, MonthlyCreditsMillis = 1_000_000, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_creator","annual":"pri_creator_y"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, ProviderPriceIdsJson = """{"fake":{"monthly":"pri_pro","annual":"pri_pro_y"}}""" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now, TimeSpan.Zero);
    }

    private async Task StartAnnual(DateTime at = default, string price = "pri_creator_y")
    {
        at = at == default ? A0 : at;
        await Process(AnnualSub("evt_1", at, price: price), at);
        await Process(Payment("txn_term", at) with { Kind = BillingEventKind.TransactionCompleted, PeriodStart = at == default ? A0 : at, PeriodEnd = (at == default ? A0 : at).AddYears(1) }, at);
    }

    private async Task<BillingEventOutcome> Process(BillingEvent evt, DateTime now)
    {
        await using InferenceApiDbContext db = NewDb();
        return await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new FixedClock(now)).ProcessAsync(evt, "{}");
    }

    private BillingEvent AnnualSub(string id, DateTime at, string price = "pri_creator_y", DateTime? start = null, SubscriptionStatus status = SubscriptionStatus.Active, bool cancelAtEnd = false, DateTime? cancelAt = null) => new()
    {
        Provider = P,
        EventId = id,
        EventType = "subscription.updated",
        OccurredAt = at,
        Kind = BillingEventKind.SubscriptionChanged,
        OrganizationId = _org,
        CustomerId = "ctm_1",
        SubscriptionId = "sub_1",
        Status = status,
        PeriodStart = start ?? A0,
        PeriodEnd = (start ?? A0).AddYears(1),
        CancelAtPeriodEnd = cancelAtEnd,
        CancelEffectiveAt = cancelAt,
        Items = [new BillingLineItem(price, 1)]
    };

    private BillingEvent MonthlySub(string id, DateTime at, DateTime start, string price = "pri_creator") => new()
    {
        Provider = P,
        EventId = id,
        EventType = "subscription.updated",
        OccurredAt = at,
        Kind = BillingEventKind.SubscriptionChanged,
        OrganizationId = _org,
        CustomerId = "ctm_1",
        SubscriptionId = "sub_1",
        Status = SubscriptionStatus.Active,
        PeriodStart = start,
        PeriodEnd = start.AddMonths(1),
        Items = [new BillingLineItem(price, 1)]
    };

    private BillingEvent Payment(string txn, DateTime at, long amount = 29000) => new()
    {
        Provider = P,
        EventId = $"evt_{txn}",
        EventType = "transaction.completed",
        OccurredAt = at,
        Kind = BillingEventKind.Ignored,
        OrganizationId = _org,
        CustomerId = "ctm_1",
        SubscriptionId = "sub_1",
        TransactionId = txn,
        AmountMinor = amount,
        CurrencyCode = "USD",
        IsPaidTransaction = true
    };

    private BillingEvent Refund(string id, string txn, string adj, DateTime at, long amount = 29000, bool full = true) => new()
    {
        Provider = P,
        EventId = id,
        EventType = "adjustment.updated",
        OccurredAt = at,
        Kind = BillingEventKind.PaymentRefunded,
        TransactionId = txn,
        AdjustmentId = adj,
        FullRefund = full,
        RefundAmountMinor = amount,
        SubscriptionId = "sub_1"
    };

    private ServiceProvider Services(DateTime now, bool enabled = true)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<TimeProvider>(new FixedClock(now));
        services.AddSingleton(Options.Create(new BillingOptions { Enabled = true }));
        services.AddSingleton(Options.Create(new AnnualGrantOptions { Enabled = enabled }));
        return services.BuildServiceProvider();
    }

    private async Task<int> Sweep(DateTime now, bool enabled = true)
    {
        using ServiceProvider sp = Services(now, enabled);
        var sweeper = new AnnualCreditGrantSweeper(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<BillingOptions>>(),
            sp.GetRequiredService<IOptions<AnnualGrantOptions>>(),
            NullLogger<AnnualCreditGrantSweeper>.Instance,
            new FixedClock(now));
        return await sweeper.SweepOnceAsync(now, default);
    }

    private async Task<long> Balance()
    {
        await using InferenceApiDbContext db = NewDb();
        // Read at a fixed instant inside the first month of the term, not the wall clock (the grants expire by date).
        return (await new CreditLedger(db, new FixedClock(A0.AddDays(12))).GetBalanceAsync(_org)).BalanceMillis;
    }

    private async Task<List<CreditLedgerEntry>> Refills()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditLedgerEntries.AsNoTracking().Where(e => e.IdempotencyKey.StartsWith("plan:")).OrderBy(e => e.IdempotencyKey).ToListAsync();
    }

    private async Task<CreditGrant> GrantOf(string key)
    {
        await using InferenceApiDbContext db = NewDb();
        Guid grantId = (await db.CreditLedgerEntries.AsNoTracking().SingleAsync(e => e.IdempotencyKey == key)).GrantId!.Value;
        return await db.CreditGrants.AsNoTracking().SingleAsync(g => g.Id == grantId);
    }

    private static string Key0 => BillingEventProcessor.PlanRefillKey(P, "sub_1", A0);

    private static string M(int n) => AnnualCreditSchedule.MonthKey(P, "sub_1", A0, n);

    // ---------------------------------------------------------------- month 0

    [Fact]
    public async Task AnnualMonthZero_ExpiresOneMonthAfterThePeriodStart_UnderTheUnchangedKey()
    {
        await StartAnnual();

        (await Refills()).Select(e => e.IdempotencyKey).Should().Equal(Key0);
        CreditGrant grant = await GrantOf(Key0);
        grant.AmountMillis.Should().Be(1_000_000);
        grant.ExpiresAt.Should().Be(A0.AddMonths(1));
        await using InferenceApiDbContext db = NewDb();
        (await db.Subscriptions.SingleAsync()).PeriodEnd.Should().Be(AEnd, "the billing end stays yearly");
    }

    [Fact]
    public async Task MonthlyRefill_StillExpiresAtThePeriodEnd()
    {
        await Process(MonthlySub("evt_1", A0, A0), A0);

        (await GrantOf(Key0)).ExpiresAt.Should().Be(A0.AddMonths(1));
        (await Refills()).Should().ContainSingle();
        (await Sweep(A0.AddDays(20))).Should().Be(0, "a monthly subscription has no monthly anniversaries to sweep");
    }

    // ---------------------------------------------------------------- sweeper

    [Fact]
    public async Task Sweeper_GrantsEachElapsedAnniversary_AtTheCurrentPlan_ExpiringAtTheNextOne_AndNeverTheFuture()
    {
        await StartAnnual();

        (await Sweep(A0.AddMonths(1).AddMinutes(-1))).Should().Be(0, "month 1 has not begun");
        (await Sweep(A0.AddMonths(1))).Should().Be(1);

        CreditGrant m1 = await GrantOf(M(1));
        m1.AmountMillis.Should().Be(1_000_000);
        m1.ExpiresAt.Should().Be(A0.AddMonths(2));
        (await Refills()).Should().HaveCount(2);
    }

    [Fact]
    public async Task Sweeper_GrantsOnlyTheOpenMonthAfterDowntime_Once_NeverABornExpiredGrant()
    {
        await StartAnnual();
        DateTime now = A0.AddMonths(3).AddDays(2);

        (await Sweep(now)).Should().Be(1, "only the open month is granted; months 1 and 2 are lost, not born expired");
        (await Sweep(now.AddHours(5))).Should().Be(0, "a second pass, or a second replica, replays the keys");

        (await Refills()).Select(e => e.IdempotencyKey).Should().BeEquivalentTo([Key0, M(3)]);
        (await GrantOf(M(3))).ExpiresAt.Should().Be(A0.AddMonths(4));
    }

    [Fact]
    public async Task Sweeper_UsesTheCurrentPlanAndSeats_NotTheOneTheTermStartedWith()
    {
        await StartAnnual();
        DateTime at = A0.AddDays(5);
        await Process(AnnualSub("evt_2", at, price: "pri_pro_y"), at);
        await Process(new BillingEvent
        {
            Items = [new BillingLineItem("pri_pro_y", 1)],
            Provider = P,
            EventId = "evt_p0",
            EventType = "transaction.completed",
            OccurredAt = at.AddMinutes(1),
            Kind = BillingEventKind.UpgradePaid,
            OrganizationId = _org,
            CustomerId = "ctm_1",
            SubscriptionId = "sub_1",
            TransactionId = "txn_up0",
            AmountMinor = 25000,
            CurrencyCode = "USD",
            IsPaidTransaction = true
        }, at.AddMinutes(1));

        await Sweep(A0.AddMonths(1).AddHours(1));

        (await GrantOf(M(1))).AmountMillis.Should().Be(3_500_000);
    }

    [Fact]
    public async Task Sweeper_LastMonthExpiresAtTheTermEnd_AndNothingIsGrantedPastIt()
    {
        await StartAnnual();

        for (int month = 1; month <= 11; month++)
        {
            await Sweep(A0.AddMonths(month).AddDays(3));
        }

        (await GrantOf(M(11))).ExpiresAt.Should().Be(AEnd);
        (await Refills()).Should().HaveCount(12);

        (await Sweep(AEnd.AddDays(1))).Should().Be(0, "the term is over and its renewal webhook has not opened the next one");
        (await Refills()).Should().HaveCount(12);
    }

    [Theory]
    [InlineData("pastdue")]
    [InlineData("canceled")]
    [InlineData("cancel-effective")]
    public async Task Sweeper_SkipsASubscriptionThatIsNotEntitledOrWhoseCancellationHasTakenEffect(string scenario)
    {
        DateTime now = A0.AddMonths(2).AddDays(1);
        await StartAnnual();
        BillingEvent update = scenario switch
        {
            "pastdue" => AnnualSub("evt_2", A0.AddDays(40), status: SubscriptionStatus.PastDue),
            "canceled" => AnnualSub("evt_2", A0.AddDays(40), status: SubscriptionStatus.Canceled),
            _ => AnnualSub("evt_2", A0.AddDays(40), cancelAtEnd: true, cancelAt: A0.AddMonths(2))
        };
        await Process(update, A0.AddDays(40));

        (await Sweep(now)).Should().Be(0);
        (await Refills()).Should().ContainSingle();
    }

    [Fact]
    public async Task Sweeper_KeepsGranting_WhileACancellationIsScheduledButNotYetEffective()
    {
        await Process(AnnualSub("evt_1", A0, cancelAtEnd: true, cancelAt: AEnd), A0);
        await Process(Payment("txn_term", A0), A0);

        (await Sweep(A0.AddMonths(1).AddDays(1))).Should().Be(1);
    }

    [Fact]
    public async Task Sweeper_StopsOnceTheTermPaymentIsFullyRefunded_ButNotAfterAPartialRefund()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Payment("txn_y", A0), A0);

        await Process(Refund("evt_r0", "txn_y", "adj_0", A0.AddDays(20), amount: 1000, full: false), A0.AddDays(20));
        (await Sweep(A0.AddMonths(1).AddDays(1))).Should().Be(1, "a partial refund is a human decision");

        await Process(Refund("evt_r1", "txn_y", "adj_1", A0.AddMonths(1).AddDays(2), amount: 28000, full: false), A0.AddMonths(1).AddDays(2));
        (await Sweep(A0.AddMonths(2).AddDays(1))).Should().Be(0, "refunds now add up to the whole payment");
    }

    [Fact]
    public async Task Sweeper_IsInertWhenDisabled()
    {
        await StartAnnual();

        (await Sweep(A0.AddMonths(2), enabled: false)).Should().Be(0);
        (await Refills()).Should().ContainSingle();
    }

    // ---------------------------------------------------------------- switch from monthly

    [Fact]
    public async Task MonthlyToAnnual_FirstRefillIsReducedByTheRefillAlreadyGrantedThatMonth_NeverNegative()
    {
        DateTime start = A0.AddDays(10);
        await Process(MonthlySub("evt_m", A0, A0), A0);
        (await Balance()).Should().Be(1_000_000);

        // The same plan, annual: nothing more to grant this month.
        await Process(AnnualSub("evt_a", start, start: start), start);

        (await Balance()).Should().Be(1_000_000, "the month already received its refill");
        (await Refills()).Should().ContainSingle();
    }

    [Fact]
    public async Task MonthlyToAnnual_GrantsOnlyWhatIsLeftOfTheMonthsEntitlement_ExpiringAfterOneMonth()
    {
        DateTime start = A0.AddDays(10);
        await Process(MonthlySub("evt_m", A0, A0), A0);
        await using (InferenceApiDbContext db = NewDb())
        {
            (await db.Plans.SingleAsync(p => p.Key == "creator")).MonthlyCreditsMillis = 1_500_000;
            await db.SaveChangesAsync();
        }

        await Process(AnnualSub("evt_a", start, start: start), start);

        (await Balance()).Should().Be(1_500_000, "the 1,500 entitlement minus the 1,000 already granted");
        CreditGrant grant = await GrantOf(BillingEventProcessor.PlanRefillKey(P, "sub_1", start));
        grant.AmountMillis.Should().Be(500_000);
        grant.ExpiresAt.Should().Be(start.AddMonths(1));
    }

    [Fact]
    public async Task AnnualRenewal_IsNotReducedByThePreviousTermsLastMonthlyGrant()
    {
        await StartAnnual();
        await Sweep(A0.AddMonths(11).AddDays(2));
        DateTime next = AEnd;

        await Process(AnnualSub("evt_2", next, start: next), next);

        string key = BillingEventProcessor.PlanRefillKey(P, "sub_1", next);
        (await GrantOf(key)).AmountMillis.Should().Be(1_000_000);
    }

    // ---------------------------------------------------------------- proration and windows

    [Fact]
    public void AnnualUpgradeProration_UsesTheCurrentMonthWindow_NeverMoreThanOneMonthsDifference()
    {
        long old = 1_000_000;
        long upgraded = 3_500_000;
        DateTime at = A0.AddMonths(3);

        BillingEventProcessor.ProratedUpgradeGrantMillis(old, upgraded, A0, AEnd, at).Should().Be(2_500_000, "the whole month is still ahead at its first instant");
        BillingEventProcessor.ProratedUpgradeGrantMillis(old, upgraded, A0, AEnd, A0).Should().Be(2_500_000);

        DateTime windowStart = A0.AddMonths(3);
        DateTime windowEnd = A0.AddMonths(4);
        DateTime half = windowStart + (windowEnd - windowStart) / 2;
        BillingEventProcessor.ProratedUpgradeGrantMillis(old, upgraded, A0, AEnd, half).Should().Be(1_250_000);

        for (int day = 0; day < 365; day += 7)
        {
            BillingEventProcessor.ProratedUpgradeGrantMillis(old, upgraded, A0, AEnd, A0.AddDays(day)).Should().BeInRange(0, 2_500_000);
        }
    }

    [Fact]
    public void MonthlyProration_IsUnchanged()
    {
        DateTime start = A0;
        DateTime end = A0.AddMonths(1);
        BillingEventProcessor.ProratedUpgradeGrantMillis(1_000_000, 3_500_000, start, end, start + (end - start) / 2).Should().Be(1_250_000);
    }

    [Fact]
    public async Task AnnualUpgrade_GrantsTheCurrentMonthsDifference_ExpiringAtThatMonthsEnd()
    {
        await StartAnnual();
        DateTime at = A0.AddMonths(2).AddDays(15);
        DateTime windowEnd = A0.AddMonths(3);
        await Process(AnnualSub("evt_2", at, price: "pri_pro_y"), at);

        await Process(new BillingEvent
        {
            Items = [new BillingLineItem("pri_pro_y", 1)],
            Provider = P,
            EventId = "evt_p",
            EventType = "transaction.completed",
            OccurredAt = at.AddMinutes(1),
            Kind = BillingEventKind.UpgradePaid,
            OrganizationId = _org,
            CustomerId = "ctm_1",
            SubscriptionId = "sub_1",
            TransactionId = "txn_up",
            AmountMinor = 25000,
            CurrencyCode = "USD",
            IsPaidTransaction = true
        }, at.AddMinutes(1));

        await using InferenceApiDbContext db = NewDb();
        CreditGrant grant = await db.CreditGrants.AsNoTracking().SingleAsync(g => g.AmountMillis != 1_000_000);
        grant.ExpiresAt.Should().Be(windowEnd);
        grant.AmountMillis.Should().BeLessThan(2_500_000).And.BeGreaterThan(1_000_000, "about half of the month is left");
    }

    [Fact]
    public void AssistantAllowancePeriod_InsideAnAnnualTerm_IsTheMonthlyAnniversaryWindow()
    {
        var sub = new Subscription { PeriodStart = A0, PeriodEnd = AEnd };

        AssistantAllowanceService.PeriodOf(sub, A0.AddMonths(4).AddDays(3)).Should().Be((A0.AddMonths(4), A0.AddMonths(5)));
        AssistantAllowanceService.PeriodOf(sub, A0.AddDays(2)).Should().Be((A0, A0.AddMonths(1)));
        AssistantAllowanceService.PeriodOf(sub, A0.AddMonths(11).AddDays(5)).Should().Be((A0.AddMonths(11), AEnd));
    }

    [Fact]
    public void AssistantAllowancePeriod_OnAMonthlySubscription_IsTheWholePeriod()
    {
        var sub = new Subscription { PeriodStart = A0, PeriodEnd = A0.AddMonths(1) };

        AssistantAllowanceService.PeriodOf(sub, A0.AddDays(9)).Should().Be((A0, A0.AddMonths(1)));
    }

    [Fact]
    public void CreditsRenewAt_IsTheNextAnniversaryOnAnnual_AndThePeriodEndOtherwise()
    {
        var annual = new Subscription { PeriodStart = A0, PeriodEnd = AEnd };
        AnnualCreditSchedule.CreditsRenewAt(annual, A0.AddMonths(6).AddDays(1)).Should().Be(A0.AddMonths(7));
        annual.PeriodEnd.Should().Be(AEnd);

        var monthly = new Subscription { PeriodStart = A0, PeriodEnd = A0.AddMonths(1) };
        AnnualCreditSchedule.CreditsRenewAt(monthly, A0.AddDays(3)).Should().Be(A0.AddMonths(1));
    }

    [Fact]
    public void Anniversaries_AreComputedFromTheTermStart_SoShortMonthsDoNotDrift()
    {
        DateTime start = new(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);
        DateTime end = start.AddYears(1);

        AnnualCreditSchedule.Anniversary(start, end, 1).Should().Be(new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc));
        AnnualCreditSchedule.Anniversary(start, end, 2).Should().Be(new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc));
    }

    // ---------------------------------------------------------------- refunds

    [Fact]
    public void RefundPromise_IsOneNamedConstant()
        => BillingRefundPolicy.MoneyBackDays.Should().Be(14);

    [Fact]
    public async Task FullRefundWithinFourteenDays_ClawsBackTheUnspentTermCredits_Once_AndStopsFurtherGrants()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Payment("txn_y", A0), A0);
        await using (InferenceApiDbContext spend = NewDb())
        {
            await new CreditLedger(spend, new FixedClock(A0.AddDays(1))).AdjustAsync(_org, -300_000, "spent-1");
        }

        DateTime at = A0.AddDays(14);
        (await Process(Refund("evt_r", "txn_y", "adj_1", at), at)).Should().Be(BillingEventOutcome.Applied);
        (await Process(Refund("evt_r2", "txn_y", "adj_2", at.AddHours(1)), at.AddHours(1))).Should().Be(BillingEventOutcome.Applied);

        (await Balance()).Should().Be(0, "the 700 unspent credits of month 0 are gone, once");
        (await Sweep(A0.AddMonths(2).AddDays(1))).Should().Be(0, "the fully refunded term grants nothing more");
        (await Balance()).Should().Be(0);
    }

    [Fact]
    public async Task FullRefundAfterFourteenDays_ChangesNoCredits_AndStaysAHumanDecision()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Payment("txn_y", A0), A0);

        DateTime at = A0.AddDays(15);
        (await Process(Refund("evt_r", "txn_y", "adj_1", at), at)).Should().Be(BillingEventOutcome.Applied);

        (await Balance()).Should().Be(1_000_000);
    }

    [Fact]
    public async Task PartialRefundWithinFourteenDays_ChangesNoCredits()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Payment("txn_y", A0), A0);

        DateTime at = A0.AddDays(3);
        await Process(Refund("evt_r", "txn_y", "adj_1", at, amount: 5000, full: false), at);

        (await Balance()).Should().Be(1_000_000);
    }

    [Fact]
    public async Task FullRefundOfAMonthlyPayment_IsUnchanged_NoAutomaticClawback()
    {
        await Process(MonthlySub("evt_1", A0, A0), A0);
        await Process(Payment("txn_m", A0, 2900), A0);

        await Process(Refund("evt_r", "txn_m", "adj_1", A0.AddDays(3), amount: 2900), A0.AddDays(3));

        (await Balance()).Should().Be(1_000_000, "a refunded subscription payment on a monthly plan needs a human decision, as before");
    }

    [Fact]
    public async Task Sweeper_FailsClosed_WhenAnActiveTermHasNoRecordedPayment()
    {
        await Process(AnnualSub("evt_1", A0), A0);

        (await Sweep(A0.AddMonths(1).AddDays(1))).Should().Be(0);
        (await Refills()).Should().ContainSingle();
    }

    [Fact]
    public async Task Sweeper_GrantsATrialingTerm_WithoutAPayment()
    {
        await Process(AnnualSub("evt_1", A0, status: SubscriptionStatus.Trialing), A0);

        (await Sweep(A0.AddMonths(1).AddDays(1))).Should().Be(1);
    }

    [Fact]
    public async Task Sweeper_StopsOnAChargeback_OfTheTermPayment()
    {
        await StartAnnual();
        await Process(Refund("evt_cb", "txn_term", "adj_cb", A0.AddMonths(1)) with { EventType = "adjustment.created" }, A0.AddMonths(1));

        (await Sweep(A0.AddMonths(1).AddDays(1))).Should().Be(0);
    }

    [Fact]
    public async Task Sweeper_GrantsTheEntitlementBeforeAnUpgradeUntilItsPaymentIsPaired()
    {
        await StartAnnual();
        await Process(AnnualSub("evt_2", A0.AddDays(5), price: "pri_pro_y"), A0.AddDays(5));

        await Sweep(A0.AddMonths(1).AddHours(1));

        (await GrantOf(M(1))).AmountMillis.Should().Be(1_000_000, "the upgrade is not paid for yet");
    }

    [Fact]
    public async Task Sweeper_GrantsTheNewPlan_OnceTheUpgradePaymentIsPaired_NotWhenItWasRefunded()
    {
        await StartAnnual();
        DateTime at = A0.AddDays(5);
        await Process(AnnualSub("evt_2", at, price: "pri_pro_y"), at);
        await Process(new BillingEvent
        {
            Items = [new BillingLineItem("pri_pro_y", 1)],
            Provider = P,
            EventId = "evt_p",
            EventType = "transaction.completed",
            OccurredAt = at.AddMinutes(1),
            Kind = BillingEventKind.UpgradePaid,
            OrganizationId = _org,
            CustomerId = "ctm_1",
            SubscriptionId = "sub_1",
            TransactionId = "txn_up",
            AmountMinor = 25000,
            CurrencyCode = "USD",
            IsPaidTransaction = true
        }, at.AddMinutes(1));

        await Sweep(A0.AddMonths(1).AddHours(1));
        (await GrantOf(M(1))).AmountMillis.Should().Be(3_500_000);

        await Process(Refund("evt_ru", "txn_up", "adj_u", A0.AddMonths(1).AddHours(2), amount: 25000), A0.AddMonths(1).AddHours(2));
        await Sweep(A0.AddMonths(2).AddHours(1));
        (await GrantOf(M(2))).AmountMillis.Should().Be(1_000_000, "the refunded upgrade no longer entitles");
    }

    [Fact]
    public async Task Sweeper_StopsWhenTheTermPaymentIsRefunded_EvenIfAnotherPaymentInTheWindowIsLarger()
    {
        await StartAnnual();
        await Process(Payment("txn_big", A0.AddHours(1), 90000), A0.AddHours(1));
        await Process(Refund("evt_rt", "txn_term", "adj_t", A0.AddMonths(1)), A0.AddMonths(1));

        (await Sweep(A0.AddMonths(1).AddDays(1))).Should().Be(0);
    }

    private BillingEvent UpgradePayment(string txn, DateTime at, string price = "pri_pro_y", long amount = 25000) => new()
    {
        Items = [new BillingLineItem(price, 1)],
        Provider = P,
        EventId = $"evt_{txn}",
        EventType = "transaction.completed",
        OccurredAt = at,
        Kind = BillingEventKind.UpgradePaid,
        OrganizationId = _org,
        CustomerId = "ctm_1",
        SubscriptionId = "sub_1",
        TransactionId = txn,
        AmountMinor = amount,
        CurrencyCode = "USD",
        IsPaidTransaction = true
    };

    [Fact]
    public async Task LatePaidAnnualTerm_FullRefundWithinFourteenDaysOfThePayment_ClawsBackAndStopsGrants()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        DateTime paid = A0.AddDays(6);
        await Process(Payment("txn_late", paid), paid);
        (await Balance()).Should().Be(1_000_000);

        DateTime at = paid.AddDays(10);
        await Process(Refund("evt_r", "txn_late", "adj_l", at), at);

        (await Balance()).Should().Be(0, "the refund is inside 14 days of the payment, though 16 days after the term start");
        (await Sweep(A0.AddMonths(1).AddDays(1))).Should().Be(0);
    }

    [Fact]
    public async Task ChargebackOfAnAnnualUpgradePayment_TakesItsGrantBack_AndRevertsFurtherGrantsToThePreUpgradeAmount()
    {
        await StartAnnual();
        DateTime at = A0.AddDays(5);
        await Process(AnnualSub("evt_2", at, price: "pri_pro_y"), at);
        await Process(UpgradePayment("txn_up", at.AddMinutes(1)), at.AddMinutes(1));
        (await Balance()).Should().BeGreaterThan(1_000_000);

        await Process(Refund("evt_cb", "txn_up", "adj_cb", at.AddDays(3), amount: 25000), at.AddDays(3));
        (await Balance()).Should().Be(1_000_000, "the upgrade difference is clawed back");

        (await Sweep(A0.AddMonths(1).AddHours(1))).Should().Be(1);
        (await GrantOf(M(1))).AmountMillis.Should().Be(1_000_000, "no further grant at the upgraded amount");
        (await Balance()).Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task ARefundedPaymentOfTheTermThatIsNeitherTheTermNorAnUpgrade_StopsGrants()
    {
        await StartAnnual();
        await Process(Payment("txn_seat", A0.AddDays(4), 9000), A0.AddDays(4));
        await Process(Refund("evt_rs", "txn_seat", "adj_s", A0.AddDays(5), amount: 9000), A0.AddDays(5));

        (await Sweep(A0.AddMonths(1).AddHours(1))).Should().Be(0);
    }

    [Fact]
    public async Task MonthlyCreatorToAnnualPro_ReducesTheFirstRefillByTheMonthsGrant_NoDoubleGrant_NeverNegative()
    {
        await Process(MonthlySub("evt_m", A0, A0), A0);
        (await Balance()).Should().Be(1_000_000);

        DateTime start = A0.AddDays(10);
        await Process(AnnualSub("evt_a", start, price: "pri_pro_y", start: start), start);
        await Process(UpgradePayment("txn_pro_y", start.AddMinutes(1), amount: 790000) with { ProrationStartsAt = start }, start.AddMinutes(1));
        (await Balance()).Should().Be(3_500_000, "Pro's month in total; the Creator refill already granted is not granted again");

        DateTime next = start.AddMonths(1).AddHours(1);
        await using (InferenceApiDbContext db = NewDb())
        {
            (await db.Subscriptions.SingleAsync()).Status.Should().Be(SubscriptionStatus.Active);
        }
        (await Sweep(next)).Should().Be(1);
        (await GrantOf(AnnualCreditSchedule.MonthKey(P, "sub_1", start, 1))).AmountMillis.Should().Be(3_500_000);
    }

    [Fact]
    public async Task Sweeper_ReachesEverySubscriptionAcrossPasses_WhenMoreAreDueThanOnePassTakes()
    {
        List<string> subs = ["sub_1"];
        await StartAnnual();
        for (int i = 2; i <= 5; i++)
        {
            Guid org = Guid.NewGuid();
            await using (InferenceApiDbContext db = NewDb())
            {
                db.Organizations.Add(new Organization { Id = org, Name = $"o{i}", Slug = $"o{i}", Kind = OrganizationKind.Team });
                await db.SaveChangesAsync();
            }

            string sub = $"sub_{i}";
            subs.Add(sub);
            await Process(AnnualSub($"evt_s{i}", A0) with { OrganizationId = org, CustomerId = $"ctm_{i}", SubscriptionId = sub }, A0);
            await Process(Payment($"txn_{i}", A0) with { OrganizationId = org, CustomerId = $"ctm_{i}", SubscriptionId = sub }, A0);
        }

        DateTime now = A0.AddMonths(1).AddHours(1);
        using ServiceProvider sp = Services(now);
        var sweeper = new AnnualCreditGrantSweeper(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<BillingOptions>>(),
            Options.Create(new AnnualGrantOptions { MaxPerSweep = 2 }),
            NullLogger<AnnualCreditGrantSweeper>.Instance,
            new FixedClock(now));

        (await sweeper.SweepOnceAsync(now, default)).Should().Be(2);
        (await sweeper.SweepOnceAsync(now, default)).Should().Be(2, "the second pass resumes after the first two, never the same first ones");
        (await sweeper.SweepOnceAsync(now, default)).Should().Be(1);
        (await sweeper.SweepOnceAsync(now, default)).Should().Be(0);

        await using InferenceApiDbContext check = NewDb();
        (await check.CreditLedgerEntries.CountAsync(e => e.IdempotencyKey.EndsWith(":m1"))).Should().Be(5);
    }

    [Fact]
    public async Task Sweeper_FailsClosed_WhenTheUpgradePaymentThatOpenedTheTermIsRefunded()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        DateTime at = A0.AddDays(5);
        await Process(AnnualSub("evt_2", at, price: "pri_pro_y"), at);
        await Process(new BillingEvent
        {
            Items = [new BillingLineItem("pri_pro_y", 1)],
            Provider = P,
            EventId = "evt_p",
            EventType = "transaction.completed",
            OccurredAt = at.AddMinutes(1),
            Kind = BillingEventKind.UpgradePaid,
            OrganizationId = _org,
            CustomerId = "ctm_1",
            SubscriptionId = "sub_1",
            TransactionId = "txn_up",
            AmountMinor = 25000,
            CurrencyCode = "USD",
            IsPaidTransaction = true
        }, at.AddMinutes(1));
        await Process(Refund("evt_ru", "txn_up", "adj_u", A0.AddMonths(1), amount: 25000), A0.AddMonths(1));

        (await Sweep(A0.AddMonths(1).AddDays(1))).Should().Be(0, "no other payment backs the term, so a refunded upgrade payment stops it");
    }

    [Fact]
    public async Task Refund_OfAnyNonUpgradePaymentOfTheTerm_ClawsBackTheTermCredits()
    {
        await StartAnnual();
        await Process(Payment("txn_seat", A0.AddDays(3), 5000), A0.AddDays(3));

        DateTime at = A0.AddDays(4);
        await Process(Refund("evt_rs", "txn_seat", "adj_s", at, amount: 5000), at);

        (await Balance()).Should().Be(0);
        (await Sweep(A0.AddMonths(1).AddDays(1))).Should().Be(0);
    }

    // ---------------------------------------------------------------- term-opening transaction id

    private async Task<string?> TermId()
    {
        await using InferenceApiDbContext db = NewDb();
        return (await db.Subscriptions.AsNoTracking().SingleAsync()).TermOpeningTransactionId;
    }

    private BillingEvent Invoice(string txn, DateTime at, DateTime periodStart, long amount = 29000)
        => Payment(txn, at, amount) with { Kind = BillingEventKind.TransactionCompleted, PeriodStart = periodStart, PeriodEnd = periodStart.AddYears(1) };

    private BillingEvent Seat(string txn, DateTime at, long amount = 5000) => new()
    {
        Provider = P,
        EventId = $"evt_{txn}",
        EventType = "transaction.completed",
        OccurredAt = at,
        Kind = BillingEventKind.UpgradePaid,
        OrganizationId = _org,
        CustomerId = "ctm_1",
        SubscriptionId = "sub_1",
        TransactionId = txn,
        AmountMinor = amount,
        IsPaidTransaction = true,
        Items = [new BillingLineItem("pri_creator_y", 2)]
    };

    [Fact]
    public async Task TermId_IsSetOnlyOnTheExplicitInvoiceSignal_AndNeverOverwritten()
    {
        await StartAnnual();
        (await TermId()).Should().Be("txn_term");
        await Process(Seat("txn_seat", A0.AddDays(3)), A0.AddDays(3));
        await Process(Invoice("txn_other", A0.AddDays(4), A0), A0.AddDays(4));
        (await TermId()).Should().Be("txn_term");
    }

    [Fact]
    public async Task TermId_IsNull_WithoutAnExplicitSignal_ZeroAmount_OrAnotherPeriod()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Payment("txn_plain", A0), A0);
        await Process(Invoice("txn_zero", A0, A0, 0), A0);
        await Process(Invoice("txn_monthly", A0, A0.AddMonths(-1), 3000), A0);
        (await TermId()).Should().BeNull();
    }

    [Fact]
    public async Task TermId_IsCleared_WhenANewTermOpens()
    {
        await StartAnnual();
        await Process(AnnualSub("evt_2", AEnd, start: AEnd), AEnd);
        (await TermId()).Should().BeNull();
        await Process(Invoice("txn_term2", AEnd, AEnd), AEnd);
        (await TermId()).Should().Be("txn_term2");
    }

    [Fact]
    public async Task SeatPaymentBeforeTheRenewalInvoice_IsNeverTheTermId_RefundingItDoesNotClawTheTerm_ButTheRealPaymentDoes()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Seat("txn_seat", A0.AddMinutes(10)), A0.AddMinutes(10));
        await Process(Invoice("txn_term", A0.AddMinutes(20), A0), A0.AddMinutes(20));
        (await TermId()).Should().Be("txn_term");

        await Process(Refund("evt_rs", "txn_seat", "adj_s", A0.AddDays(1), amount: 5000), A0.AddDays(1));
        (await Balance()).Should().Be(1_000_000, "a seat payment's refund does not claw the whole term");

        await Process(Refund("evt_rt", "txn_term", "adj_t", A0.AddDays(2)), A0.AddDays(2));
        (await Balance()).Should().Be(0);
        (await Sweep(A0.AddMonths(1).AddHours(1))).Should().Be(0);
    }

    [Fact]
    public async Task MonthlyPaymentBeforePeriodStart_IsNeverPartOfTheTerm()
    {
        await Process(Payment("txn_monthly", A0.AddHours(-1), 3000), A0.AddHours(-1));
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Invoice("txn_term", A0, A0), A0);
        await Process(Refund("evt_rm", "txn_monthly", "adj_m", A0.AddDays(1), amount: 3000), A0.AddDays(1));
        (await Balance()).Should().Be(1_000_000);
        (await Sweep(A0.AddMonths(1).AddHours(1))).Should().Be(1, "the monthly refund is not the term's");
    }

    [Fact]
    public async Task OrphanOrStoredPayments_AreNeverAdopted_AsTheTermId()
    {
        await Process(Payment("txn_early", A0.AddHours(1)), A0.AddHours(1));
        await Process(Seat("txn_seat", A0.AddHours(2)), A0.AddHours(2));
        await Process(AnnualSub("evt_1", A0), A0);
        (await TermId()).Should().BeNull();
    }

    [Fact]
    public async Task WithAKnownId_RefundOfAnotherPlainTermPayment_StopsGrants()
    {
        await StartAnnual();
        await Process(Payment("txn_seat2", A0.AddDays(2), 5000), A0.AddDays(2));
        await Process(Refund("evt_r", "txn_seat2", "adj_2", A0.AddDays(3), amount: 5000), A0.AddDays(3));
        (await Sweep(A0.AddMonths(1).AddHours(1))).Should().Be(0);
    }

    [Fact]
    public async Task WithoutAnId_SeveralPlainPayments_AreAmbiguous_GrantsSkip_AndNothingIsClawed()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Payment("txn_a", A0), A0);
        await Process(Payment("txn_b", A0.AddDays(1), 5000), A0.AddDays(1));
        (await Sweep(A0.AddMonths(1).AddHours(1))).Should().Be(0, "ambiguous: skipped, Review manually");

        await Process(Refund("evt_r", "txn_b", "adj_b", A0.AddDays(2), amount: 5000), A0.AddDays(2));
        (await Balance()).Should().Be(0, "ambiguity never skips a clawback");
    }

    [Theory]
    [InlineData("txn_a")]
    [InlineData("txn_b")]
    public async Task AmbiguousTerm_FullRefundOrChargebackOfEitherCandidate_ClawsBackAndStopsGrants(string refunded)
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Payment("txn_a", A0), A0);
        await Process(Payment("txn_b", A0.AddDays(1), 5000), A0.AddDays(1));

        await Process(Refund("evt_r", refunded, "adj_x", A0.AddDays(2), amount: refunded == "txn_a" ? 29000 : 5000), A0.AddDays(2));

        (await Balance()).Should().Be(0);
        (await Sweep(A0.AddMonths(1).AddHours(1))).Should().Be(0);
    }

    [Fact]
    public async Task OpeningPaymentOutsideTheWindow_IsStillLoadedById_AndItsRefundClaws()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        DateTime skewed = A0.AddDays(-1);
        await Process(Invoice("txn_term", skewed, A0), skewed);
        (await TermId()).Should().Be("txn_term");
        await Process(Payment("txn_seat", A0.AddDays(1), 5000), A0.AddDays(1));

        await Process(Refund("evt_r", "txn_term", "adj_t", A0.AddDays(2)), A0.AddDays(2));
        (await Balance()).Should().Be(0);
        (await Sweep(A0.AddMonths(1).AddHours(1))).Should().Be(0);
    }

    [Fact]
    public async Task PaddleBillingPeriod_SetsTheId_ButASeatTransactionDoesNot()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Seat("txn_seat", A0.AddMinutes(1)) with { BillingPeriodStart = A0 }, A0.AddMinutes(1));
        (await TermId()).Should().BeNull();

        await Process(Payment("txn_term", A0.AddMinutes(2)) with { BillingPeriodStart = A0.AddMinutes(1) }, A0.AddMinutes(2));
        (await TermId()).Should().Be("txn_term");
    }

    [Fact]
    public async Task WithoutAnId_ASinglePlainPayment_IsTheTermPayment()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Payment("txn_late", A0.AddDays(6)), A0.AddDays(6));
        (await TermId()).Should().BeNull();
        (await Sweep(A0.AddMonths(1).AddHours(1))).Should().Be(1);
        await Process(Refund("evt_r", "txn_late", "adj_l", A0.AddDays(8)), A0.AddDays(8));
        (await Balance()).Should().Be(0);
    }

    [Fact]
    public async Task SeatChangeProratedTwoMinutesAfterTheTermStart_DoesNotPin_ButTheExactSwitchDoes()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Seat("txn_seat", A0.AddMinutes(3)) with { ProrationStartsAt = A0.AddMinutes(2) }, A0.AddMinutes(3));
        (await TermId()).Should().BeNull();

        await using (InferenceApiDbContext db = NewDb())
        {
            db.BillingPendingUpgradeGrants.Add(new BillingPendingUpgradeGrant
            {
                Id = Guid.NewGuid(),
                OrganizationId = _org,
                Provider = P,
                ProviderSubscriptionId = "sub_1",
                PeriodStart = A0,
                PeriodEnd = AEnd,
                NewEntitlementMillis = 3_500_000,
                OldEntitlementMillis = 1_000_000,
                TargetItems = "pri_pro_y:1",
                EffectiveAt = A0,
                CreatedAt = A0
            });
            await db.SaveChangesAsync();
        }

        await Process(UpgradePayment("txn_switch", A0.AddMinutes(4), amount: 790000) with { ProrationStartsAt = A0 }, A0.AddMinutes(4));
        (await TermId()).Should().Be("txn_switch");
    }

    [Fact]
    public async Task TwoRefundedCandidates_NeverTakeTheTermsCreditsTwice()
    {
        await Process(AnnualSub("evt_1", A0), A0);
        await Process(Payment("txn_a", A0), A0);
        await Process(Payment("txn_b", A0.AddDays(1), 5000), A0.AddDays(1));
        await using (InferenceApiDbContext db = NewDb())
        {
            await new CreditLedger(db, new FixedClock(A0)).GrantAsync(_org, CreditGrantSource.Promo, 500_000, A0.AddDays(13), "promo-extra");
        }

        (await Balance()).Should().Be(1_500_000);
        await Process(Refund("evt_ra", "txn_a", "adj_a", A0.AddDays(2)), A0.AddDays(2));
        await Process(Refund("evt_rb", "txn_b", "adj_b", A0.AddDays(3), amount: 5000), A0.AddDays(3));

        (await Balance()).Should().Be(500_000, "the term's 1,000,000 is taken once; the account's other credits stay");
    }
}
