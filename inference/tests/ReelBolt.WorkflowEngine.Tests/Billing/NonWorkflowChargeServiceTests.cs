using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Moq;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The non-workflow charge path (decision 9) at ledger level: hold then settle for editor ops and timeline
/// proposals, preflight then debit for the assistant and MCP. Every scenario runs on Sqlite (always) and on a
/// real Postgres when <c>REELBOLT_TEST_PG</c> is set (<see cref="NonWorkflowChargeServicePostgresTests"/>), the
/// same convention as <see cref="CreditLedgerTests"/>.
/// </summary>
public abstract class NonWorkflowChargeServiceTestsBase : IAsyncLifetime
{
    private NonWorkflowChargingHost? _host;

    /// <summary>How to reach the database; null when it is not available (a <see cref="RequiresPostgresAttribute"/> class's tests are then skipped).</summary>
    protected abstract Action<DbContextOptionsBuilder>? Configure { get; }

    /// <summary>Creates the schema once the options are known.</summary>
    protected abstract Task CreateSchemaAsync(InferenceApiDbContext db);

    protected bool Available => Configure is not null;

    private NonWorkflowChargingHost H => _host!;

    public async Task InitializeAsync()
    {
        if (Configure is null) return;
        _host = new NonWorkflowChargingHost(Configure, prices: new NonWorkflowChargingHost.FixedPrices(4_000));
        await using (InferenceApiDbContext db = _host.NewDb())
        {
            await CreateSchemaAsync(db);
        }

        await _host.SeedPlansAsync();
    }

    public virtual Task DisposeAsync()
    {
        _host?.Dispose();
        return Task.CompletedTask;
    }

    private static CreditLedgerContext Ctx => new(Note: "test");

    // ---- hold then settle ------------------------------------------------------------------------

    [DatabaseFact]
    public async Task Hold_ThenSettle_ChargesTheActualAmount()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);

        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx);
        hold.Allowed.Should().BeTrue();
        hold.ReservationId.Should().NotBeNull();
        (await H.BalanceAsync(org)).ReservedMillis.Should().Be(10_000);

        (await H.Charges.SettleAsync(hold, 7_000)).Should().BeTrue();

        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((43_000, 0));
        (await H.EntriesAsync(org)).Select(e => e.Kind)
            .Should().BeEquivalentTo([CreditLedgerEntryKind.PlanRefill, CreditLedgerEntryKind.Reserve, CreditLedgerEntryKind.Settle]);
    }

    [DatabaseFact]
    public async Task Hold_IsRefused_WhenTheBalanceCannotCoverTheEstimatePlusTolerance()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 1_000);

        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx);

        hold.Allowed.Should().BeFalse();
        hold.Code.Should().Be(NonWorkflowChargeCodes.InsufficientCredits);
        (hold.RequiredMillis, hold.BalanceMillis, hold.ReservationId).Should().Be((10_000L, 1_000L, (Guid?)null));
        (await H.EntriesAsync(org)).Should().ContainSingle(e => e.Kind == CreditLedgerEntryKind.PlanRefill);
    }

    [DatabaseFact]
    public async Task Hold_IsAllowed_WithinTheOverdraftTolerance()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 9_000);

        // 10 credits estimated, 9 available: 1 credit short is within 20 % of the estimate (2 credits).
        (await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx)).Allowed.Should().BeTrue();
        // 10 more: 11 short of a 10-credit estimate is beyond the tolerance.
        (await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx)).Allowed.Should().BeFalse();
    }

    [DatabaseFact]
    public async Task BalanceRead_NeverTakesTheOrganizationLedgerLock_SoAnEstimateDoesNotWaitBehindACharge()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        (await H.Charges.GetAvailableMillisAsync(org)).Should().Be(50_000);

        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx);
        hold.Allowed.Should().BeTrue();
        (await H.Charges.GetAvailableMillisAsync(org)).Should().Be(40_000, "the balance minus open holds");

        // R09.1: with another transaction holding the organization's advisory lock (a charge in flight), the read answers anyway.
        await using InferenceApiDbContext other = H.NewDb();
        if (other.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) != true) return;
        await using var tx = await other.Database.BeginTransactionAsync();
        string key = org.ToString();
        await other.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({key}))");
        Task<long?> read = H.Charges.GetAvailableMillisAsync(org);
        (await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5)))).Should().BeSameAs(read, "a lock-free read does not queue behind the lock");
        (await read).Should().Be(40_000);
    }

    [DatabaseFact]
    public async Task Release_OnFailure_DropsTheHold_AndChargesNothing()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.TimelineAssist, 5_000, Guid.NewGuid(), Ctx);

        (await H.Charges.ReleaseAsync(hold)).Should().BeTrue();

        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((50_000, 0));
        (await H.EntriesAsync(org)).Should().Contain(e => e.Kind == CreditLedgerEntryKind.Release
            && e.IdempotencyKey == $"nwc-release:{hold.ReservationId:N}");
    }

    [DatabaseFact]
    public async Task Settle_WithNothingConsumed_Releases()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 5_000, Guid.NewGuid(), Ctx);

        (await H.Charges.SettleAsync(hold, 0)).Should().BeTrue();

        List<CreditLedgerEntry> entries = await H.EntriesAsync(org);
        entries.Should().Contain(e => e.Kind == CreditLedgerEntryKind.Release);
        entries.Should().NotContain(e => e.Kind == CreditLedgerEntryKind.Settle);
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(50_000);
    }

    [DatabaseFact]
    public async Task Hold_ReplayedWithTheSameRequestId_ReusesTheReservation()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        Guid request = Guid.NewGuid();

        ChargeHold first = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 5_000, request, Ctx);
        ChargeHold again = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 5_000, request, Ctx);
        (await H.Charges.SettleAsync(first, 3_000)).Should().BeTrue();
        (await H.Charges.SettleAsync(again, 3_000)).Should().BeTrue();

        again.ReservationId.Should().Be(first.ReservationId);
        List<CreditLedgerEntry> entries = await H.EntriesAsync(org);
        entries.Should().ContainSingle(e => e.Kind == CreditLedgerEntryKind.Reserve)
            .Which.IdempotencyKey.Should().Be($"nwc-reserve:Editor:{request:N}");
        entries.Should().ContainSingle(e => e.Kind == CreditLedgerEntryKind.Settle)
            .Which.IdempotencyKey.Should().Be($"nwc-settle:{first.ReservationId:N}");
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(47_000);
    }

    // ---- T-110 (L1): a hold's close is retried once, and a stale sweep closes what is left ----------------

    private static long SettledMillis(List<CreditLedgerEntry> entries)
        => -entries.Where(e => e.Kind == CreditLedgerEntryKind.Settle).Sum(e => e.BalanceDeltaMillis);

    /// <summary>The real service, except that its hold settlement throws the first <paramref name="failures"/> times.</summary>
    private Mock<INonWorkflowChargeService> SettleThrows(int failures)
    {
        int calls = 0;
        Mock<INonWorkflowChargeService> flaky = new();
        flaky.Setup(c => c.SettleAsync(It.IsAny<ChargeHold>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((ChargeHold hold, long millis, CancellationToken ct) => ++calls <= failures
                ? throw new InvalidOperationException("the database is down")
                : H.Charges.SettleAsync(hold, millis, ct));
        flaky.Setup(c => c.RecordUnsettledHoldAsync(It.IsAny<ChargeHold>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((ChargeHold hold, long millis, CancellationToken ct) => H.Charges.RecordUnsettledHoldAsync(hold, millis, ct));
        return flaky;
    }

    [DatabaseFact]
    public async Task HoldClose_WhoseSettleThrowsOnce_IsRetried_AndSettlesOnce()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx);
        Mock<INonWorkflowChargeService> flaky = SettleThrows(1);

        await NonWorkflowChargeResults.CloseHoldAsync(flaky.Object, hold, 7_000);

        flaky.Verify(c => c.SettleAsync(hold, 7_000, It.IsAny<CancellationToken>()), Times.Exactly(2));
        flaky.Verify(c => c.RecordUnsettledHoldAsync(It.IsAny<ChargeHold>(), It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never);
        List<CreditLedgerEntry> entries = await H.EntriesAsync(org);
        entries.Should().ContainSingle(e => e.Kind == CreditLedgerEntryKind.Settle);
        SettledMillis(entries).Should().Be(7_000);
        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((43_000, 0));
    }

    [DatabaseFact]
    public async Task HoldClose_WhoseSettleThrowsTwice_LeavesTheHoldOpen_WithItsUsageRecordedForTheSweep()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx);
        Mock<INonWorkflowChargeService> flaky = SettleThrows(2);

        await NonWorkflowChargeResults.CloseHoldAsync(flaky.Object, hold, 7_000);

        flaky.Verify(c => c.SettleAsync(hold, 7_000, It.IsAny<CancellationToken>()), Times.Exactly(2));
        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((50_000, 10_000), "nothing settled; the hold is left for the sweep");
        await using InferenceApiDbContext db = H.NewDb();
        AssistantAllowanceHold row = await db.AssistantAllowanceHolds.SingleAsync(h => h.CreditReservationId == hold.ReservationId);
        (row.Status, row.UnsettledMeteredMillis, row.Source).Should().Be((AssistantAllowanceHoldStatus.Open, (long?)7_000, UsageSource.Editor));
    }

    [DatabaseFact]
    public async Task StaleSweep_ChargesAnAbandonedEditorHoldItsRecordedUsage_Exactly_AndALateCloseNeverChargesAgain()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx);
        await NonWorkflowChargeResults.CloseHoldAsync(SettleThrows(2).Object, hold, 7_000);

        H.Time.Now += NonWorkflowChargeService.StaleHoldAge - TimeSpan.FromSeconds(1);
        (await H.Charges.SweepStaleHoldsAsync(org)).Should().Be(0, "a hold younger than the stale age is never swept");
        (await H.BalanceAsync(org)).ReservedMillis.Should().Be(10_000);

        H.Time.Now += TimeSpan.FromSeconds(2);
        (await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 1_000, Guid.NewGuid(), Ctx)).Allowed.Should().BeTrue("the next hold sweeps first");

        List<CreditLedgerEntry> entries = await H.EntriesAsync(org);
        SettledMillis(entries).Should().Be(7_000, "the recorded usage is charged exactly");
        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((43_000, 1_000));

        // The request was not dead after all and closes late: the sweep already charged it, so nothing more is.
        (await H.Charges.SettleAsync(hold, 7_000)).Should().BeTrue();
        (await H.Charges.SettleAsync(hold, 7_000)).Should().BeTrue();
        SettledMillis(await H.EntriesAsync(org)).Should().Be(7_000, "charged exactly once");
        (await H.EntriesAsync(org)).Select(e => e.IdempotencyKey).Should().OnlyHaveUniqueItems();
        (await H.Charges.SweepStaleHoldsAsync(org)).Should().Be(0, "idempotent: a swept hold is never swept again");
    }

    [DatabaseFact]
    public async Task StaleSweep_ReleasesAnAbandonedHoldWithNothingRecorded_AndALateCloseIsChargedOnce()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        // The process died between hold and close: nothing closed it and nothing was recorded.
        ChargeHold editor = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx);
        ChargeHold proposal = await H.Charges.BeginHoldAsync(org, UsageSource.TimelineAssist, 5_000, Guid.NewGuid(), Ctx);

        H.Time.Now += NonWorkflowChargeService.StaleHoldAge + TimeSpan.FromSeconds(1);
        (await H.Charges.SweepStaleHoldsAsync(org)).Should().Be(2);

        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((50_000, 0), "nothing recorded: released");
        SettledMillis(await H.EntriesAsync(org)).Should().Be(0);

        // It was still running: its late close is charged (once, through the late key), its late release charges nothing.
        (await H.Charges.SettleAsync(editor, 7_000)).Should().BeTrue();
        (await H.Charges.SettleAsync(editor, 7_000)).Should().BeTrue();
        (await H.Charges.ReleaseAsync(proposal)).Should().BeTrue();
        List<CreditLedgerEntry> entries = await H.EntriesAsync(org);
        SettledMillis(entries).Should().Be(7_000, "charged exactly once");
        entries.Select(e => e.IdempotencyKey).Should().OnlyHaveUniqueItems();
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(43_000);
    }

    [DatabaseFact]
    public async Task OpenEditorHolds_NeverCountAgainstTheAssistantTurnCap_OrTheAllowance()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 100_000);
        for (int i = 0; i < 6; i++)
        {
            (await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 1_000, Guid.NewGuid(), Ctx)).Allowed.Should().BeTrue();
        }

        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(0);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid())).Allowed.Should().BeTrue();
    }

    // ---- QA-1: the allowance the billing summary reports is the gate's counter -------------------------

    [DatabaseFact]
    public async Task AllowanceReported_IsTheGatesCounter_SoAnAllowanceUsedUpThroughSettledHolds_ShowsAsUsedUp()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync(); // trial: 20 credits a month, no credits
        await UseAllowanceAsync(org, 20_000);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid())).Allowed.Should().BeFalse("the gate sees it used up");

        AssistantAllowance reported = await H.Allowance.GetAsync(org);

        await using (InferenceApiDbContext db = H.NewDb())
        {
            (await db.UsageEvents.IgnoreQueryFilters().AnyAsync()).Should().BeFalse("no usage_events rows: only the holds count");
        }

        (reported.IncludedMillis, reported.UsedMillis, reported.UsedUp).Should().Be(((long?)20_000, 20_000L, true));
    }

    [DatabaseFact]
    public async Task AllowanceReported_CountsOpenHolds()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await UseAllowanceAsync(org, 6_000);
        TurnHold open = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());
        open.AllowanceHeldMillis.Should().Be(5_000);

        (await H.Allowance.GetAsync(org)).UsedMillis.Should().Be(11_000, "6 consumed plus the 5 held by the open turn");
        (await H.Allowance.GetAsync(org)).UsedMillis.Should().Be((await H.Charges.AllowanceUsedAsync(org))!.Value);
    }

    [DatabaseFact]
    public async Task AllowanceReported_OnAnUnlimitedPlan_StillReportsWhatWasUsed()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("enterprise");
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (await H.Charges.SettleTurnAsync(turn, 7_000)).Ok.Should().BeTrue();

        AssistantAllowance reported = await H.Allowance.GetAsync(org);

        (reported.IncludedMillis, reported.UsedMillis).Should().Be(((long?)null, 7_000L));
    }

    /// <summary>
    /// T-026 (finding 5): a call that metered Platform usage without a hold is charged after the fact, keyed on its
    /// request id (a retry charges once), even past the balance (the usage already happened).
    /// </summary>
    [DatabaseFact]
    public async Task UnheldCharge_IsChargedOnce_KeyedOnTheRequestId()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 2_000);
        Guid request = Guid.NewGuid();

        (await H.Charges.ChargeUnheldAsync(org, UsageSource.TimelineAssist, 3_000, request, Ctx)).Should().BeTrue();
        (await H.Charges.ChargeUnheldAsync(org, UsageSource.TimelineAssist, 3_000, request, Ctx)).Should().BeTrue();

        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((-1_000L, 0L));
        (await H.EntriesAsync(org)).Where(e => e.Kind == CreditLedgerEntryKind.Settle).Should().ContainSingle();
    }

    [DatabaseFact]
    public async Task Hold_WithNoPlatformCost_ReservesNothing()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");

        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 0, Guid.NewGuid(), Ctx);

        (hold.Allowed, hold.ReservationId).Should().Be((true, (Guid?)null));
        (await H.Charges.SettleAsync(hold, 0)).Should().BeTrue();
        (await H.EntriesAsync(org)).Should().BeEmpty();
    }

    // ---- settle ceiling (hold then settle) -------------------------------------------------------

    [DatabaseFact]
    public async Task Settle_AboveTheHoldPlusHeadroom_IsCappedThere_AndTheRestIsNotCharged()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx);

        using SettleCapListener capped = new();
        (await H.Charges.SettleAsync(hold, 30_000)).Should().BeTrue();

        // 10 credits held + min(20 % of 10, 100) = 12 credits at most.
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(38_000);
        capped.Count(UsageSource.Editor).Should().BeGreaterThanOrEqualTo(1);
    }

    [DatabaseFact]
    public async Task SettleUncapped_AboveTheHoldPlusHeadroom_ChargesTheFullActualAmount()
    {
        // T-013 F1: a size-priced picture job the vendor already answered is charged what it metered, past the ceiling.
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), Ctx);

        using SettleCapListener capped = new();
        (await H.Charges.SettleUncappedAsync(hold, 30_000)).Should().BeTrue();

        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(20_000);
        capped.Count(UsageSource.Editor).Should().Be(0);
    }

    [Fact]
    public async Task HoldClose_Uncapped_SettlesThroughTheUncappedPath_AndCappedStaysOnTheCappedOne()
    {
        ChargeHold hold = new(true, Guid.NewGuid(), 10_000, 50_000, null);
        Mock<INonWorkflowChargeService> charges = new();
        charges.Setup(c => c.SettleAsync(It.IsAny<ChargeHold>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        charges.Setup(c => c.SettleUncappedAsync(It.IsAny<ChargeHold>(), It.IsAny<long>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        await NonWorkflowChargeResults.CloseHoldAsync(charges.Object, hold, 30_000, uncapped: true);
        await NonWorkflowChargeResults.CloseHoldAsync(charges.Object, hold, 7_000);

        charges.Verify(c => c.SettleUncappedAsync(hold, 30_000, It.IsAny<CancellationToken>()), Times.Once);
        charges.Verify(c => c.SettleAsync(hold, 7_000, It.IsAny<CancellationToken>()), Times.Once);
        charges.Verify(c => c.SettleAsync(hold, 30_000, It.IsAny<CancellationToken>()), Times.Never);
    }

    [DatabaseFact]
    public async Task Settle_WithinTheHoldPlusHeadroom_ChargesTheActualAmount()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("pro");
        await H.GrantAsync(org, 50_000);
        ChargeHold hold = await H.Charges.BeginHoldAsync(org, UsageSource.TimelineAssist, 10_000, Guid.NewGuid(), Ctx);

        (await H.Charges.SettleAsync(hold, 11_500)).Should().BeTrue();

        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(38_500);
    }

    // ---- assistant / MCP turns: reserve, then settle to the metered amount -------------------------

    /// <summary>Uses <paramref name="millis"/> of the allowance through the real turn path (multiples of the hold).</summary>
    protected async Task UseAllowanceAsync(Guid org, long millis)
    {
        for (long left = millis; left > 0; left -= 5_000)
        {
            TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
            turn.Allowed.Should().BeTrue();
            (await H.Charges.SettleTurnAsync(turn, Math.Min(5_000, left))).Ok.Should().BeTrue();
        }
    }

    private protected NonWorkflowChargingHost Host => H;

    [DatabaseFact]
    public async Task Turn_WithinTheAllowance_HoldsAllowance_AndSettlingWritesNoLedgerEntry()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync(); // trial: 20 credits a month, no credits at all

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (turn.Allowed, turn.AllowanceHeldMillis, turn.CreditHeldMillis).Should().Be((true, 5_000L, 0L));

        TurnSettlement settled = await H.Charges.SettleTurnAsync(turn, 3_000);

        (settled.Ok, settled.AllowanceMillis, settled.ChargedMillis, settled.UnchargedMillis).Should().Be((true, 3_000L, 0L, 0L));
        (await H.EntriesAsync(org)).Should().BeEmpty();
        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(3_000);
    }

    [DatabaseFact]
    public async Task Turn_CrossingTheAllowance_ConsumesWhatIsLeft_AndChargesOnlyTheExcess()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 18_000);

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());
        (turn.AllowanceHeldMillis, turn.CreditHeldMillis).Should().Be((2_000L, 3_000L));
        (await H.BalanceAsync(org)).ReservedMillis.Should().Be(3_000);

        TurnSettlement settled = await H.Charges.SettleTurnAsync(turn, 7_000);

        (settled.AllowanceMillis, settled.ChargedMillis, settled.UnchargedMillis).Should().Be((2_000L, 5_000L, 0L));
        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((45_000L, 0L));
        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(20_000);
    }

    [DatabaseFact]
    public async Task Turn_SettlesToTheMeteredAmount_EvenAboveTheHold_WhileCreditsAreThere()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        TurnSettlement settled = await H.Charges.SettleTurnAsync(turn, 12_000);

        (settled.ChargedMillis, settled.UnchargedMillis).Should().Be((12_000L, 0L));
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(38_000);
    }

    /// <summary>
    /// Settlement charges the metered actual: the bound is the in-turn cut-off, not a dropped charge, so a turn
    /// that settles beyond the balance plus the headroom is charged in full and the overshoot is counted.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_OverageBeyondTheBalance_IsChargedInFull_AndTheOvershootIsCounted()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 5_000);
        await UseAllowanceAsync(org, 20_000);

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        using SettleCapListener overshoot = new();
        TurnSettlement settled = await H.Charges.SettleTurnAsync(turn, 12_000);

        (settled.Ok, settled.ChargedMillis, settled.UnchargedMillis).Should().Be((true, 12_000L, 0L));
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(-7_000);
        overshoot.Count(UsageSource.Assistant).Should().BeGreaterThanOrEqualTo(1, "going past balance + headroom is counted");
    }

    /// <summary>A turn that meters 5x its hold is charged 5x while credits last (F-3 item 4).</summary>
    [DatabaseFact]
    public async Task Turn_MeteringFiveTimesItsHold_IsChargedFiveTimes_WhileCreditsLast()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());
        (await H.Charges.CheckTurnAsync(turn, 20_000)).Continue.Should().BeTrue("45 credits are still there");
        TurnSettlement settled = await H.Charges.SettleTurnAsync(turn, 25_000);

        (settled.ChargedMillis, settled.UnchargedMillis).Should().Be((25_000L, 0L));
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(25_000);
    }

    /// <summary>
    /// The in-turn cut-off: a turn stops once it has metered its allowance left plus the available balance
    /// plus the headroom of its hold, and is then charged what it metered (the last round-trip's overshoot at most).
    /// </summary>
    [DatabaseFact]
    public async Task Turn_IsCutOff_OnceAllowanceAndCreditsDownToTheHeadroomAreMetered()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 12_000);
        await UseAllowanceAsync(org, 18_000);

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (turn.AllowanceHeldMillis, turn.CreditHeldMillis).Should().Be((2_000L, 3_000L));

        // 2 credits of allowance + 12 of balance + 1 of headroom (20 % of the 5-credit hold) = 15.
        (await H.Charges.CheckTurnAsync(turn, 4_999)).Continue.Should().BeTrue();
        (await H.Charges.CheckTurnAsync(turn, 14_999)).Continue.Should().BeTrue();
        TurnCheck stop = await H.Charges.CheckTurnAsync(turn, 15_000);
        (stop.Continue, stop.Code, stop.LimitMillis).Should().Be((false, NonWorkflowChargeCodes.InsufficientCredits, 15_000L));

        TurnSettlement settled = await H.Charges.SettleTurnAsync(turn, 15_500);
        (settled.AllowanceMillis, settled.ChargedMillis, settled.UnchargedMillis).Should().Be((2_000L, 13_500L, 0L));
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(-1_500, "the floor plus the last round-trip, never more");
    }

    /// <summary>The per-turn spend ceiling stops a turn even when credits are plentiful.</summary>
    [DatabaseFact]
    public async Task Turn_IsCutOff_AtThePerTurnCeiling()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 200_000);
        await UseAllowanceAsync(org, 20_000);

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        // Past the 5-credit hold, the turn meters on up to the 100-credit ceiling when the funds exist.
        (await H.Charges.CheckTurnAsync(turn, 99_999)).Continue.Should().BeTrue();
        TurnCheck stop = await H.Charges.CheckTurnAsync(turn, 100_000);
        (stop.Continue, stop.Code, stop.LimitMillis).Should().Be((false, NonWorkflowChargeCodes.TurnLimit, 100_000L));

        H.Options.NonWorkflow.AssistantTurnCeilingCredits = 10;
        (await H.Charges.CheckTurnAsync(turn, 10_000)).Code.Should().Be(NonWorkflowChargeCodes.TurnLimit);
    }

    /// <summary>
    /// A turn started by a run wake takes its own 5-credit hold like any other turn (a fresh request id each time), and a
    /// step past the hold is funds-checked up to the ceiling, not capped at the hold.
    /// </summary>
    [DatabaseFact]
    public async Task EveryTurn_IncludingAWakeTurn_TakesItsOwnHold_AndMayMeterPastItUpToTheCeiling()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 500_000);
        await UseAllowanceAsync(org, 20_000);

        TurnHold typed = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        TurnHold wake = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        typed.HoldId.Should().NotBeNull();
        wake.HoldId.Should().NotBeNull();
        (wake.HoldId != typed.HoldId).Should().BeTrue();
        wake.RequiredMillis.Should().Be(5_000);
        (await H.Charges.CheckTurnAsync(wake, 60_000)).Continue.Should().BeTrue("12x the hold, still under the ceiling, funds exist");
        (await H.Charges.ClaimStepAsync(wake, new TurnStepClaim(60_000, 0, 39_000))).Continue.Should().BeTrue();
        (await H.Charges.ClaimStepAsync(wake, new TurnStepClaim(60_000, 0, 41_000))).Code.Should().Be(NonWorkflowChargeCodes.TurnLimit);
    }

    /// <summary>
    /// The cut-off counts what other open turns of the organization metered beyond their own holds, so concurrent
    /// turns cannot each spend the same balance.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_CutOff_CountsWhatOtherOpenTurnsMeteredBeyondTheirHolds()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 20_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold a = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        TurnHold b = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());

        // 10 credits unreserved + 5 of its own + 1 of headroom = 16 for either turn alone.
        (await H.Charges.CheckTurnAsync(a, 12_000)).Continue.Should().BeTrue();

        // a has used 7 credits beyond its hold: b has 16 - 7 = 9.
        (await H.Charges.CheckTurnAsync(b, 8_999)).Continue.Should().BeTrue();
        TurnCheck stop = await H.Charges.CheckTurnAsync(b, 9_000);
        (stop.Continue, stop.Code).Should().Be((false, NonWorkflowChargeCodes.InsufficientCredits));
    }

    /// <summary>
    /// T-026 (finding 4): the unlimited entitlement exempts the allowance only. An unlimited turn still stops at the
    /// per-turn ceiling, however much it is entitled to.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_Check_OnAnUnlimitedPlan_StillStopsAtThePerTurnCeiling()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("enterprise");
        TurnHold unlimited = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        (await H.Charges.CheckTurnAsync(unlimited, 99_999)).Continue.Should().BeTrue("the allowance never runs out on an unlimited plan");
        TurnCheck stop = await H.Charges.CheckTurnAsync(unlimited, 100_000);
        (stop.Continue, stop.Code).Should().Be((false, NonWorkflowChargeCodes.TurnLimit));
        (await H.Charges.ClaimStepAsync(unlimited, new TurnStepClaim(10_000, 0, 91_000))).Code
            .Should().Be(NonWorkflowChargeCodes.TurnLimit, "a step is pre-claimed against the ceiling before it runs");
    }

    /// <summary>
    /// T-026 (finding 4): unlimited turns count against the in-flight cap like any Platform turn, with a zero credit
    /// hold (Enterprise is billed outside credits), and settle charging nothing.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_OnAnUnlimitedPlan_CountsAgainstTheInFlightCap_WithAZeroCreditHold()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("enterprise");

        List<TurnHold> open = [];
        for (int i = 0; i < 4; i++) open.Add(await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid()));
        open.Should().OnlyContain(t => t.Allowed && t.HoldId != null && t.CreditHeldMillis == 0 && t.Code == NonWorkflowChargeCodes.UnlimitedAllowance);

        TurnHold fifth = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());
        (fifth.Allowed, fifth.Code).Should().Be((false, NonWorkflowChargeCodes.TurnConcurrencyLimit));

        (await H.Charges.SettleTurnAsync(open[0], 30_000)).ChargedMillis.Should().Be(0, "Enterprise is billed outside credits");
        (await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid())).Allowed.Should().BeTrue("one turn closed");
        (await H.EntriesAsync(org)).Should().BeEmpty();
    }

    /// <summary>
    /// The minimum hold: a turn is admitted only when the allowance left plus the available balance covers
    /// <c>AssistantMinTurnCredits</c> (default the 5-credit hold); a partial hold is never run in full.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_WithLessThanTheMinimumLeft_IsRefused_AndHoldsNothing()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 2_000);
        await UseAllowanceAsync(org, 18_000);

        // 2 credits of allowance + 2 credits of balance: less than the 5-credit minimum.
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        (turn.Allowed, turn.Code, turn.HoldId).Should().Be((false, NonWorkflowChargeCodes.InsufficientCredits, (Guid?)null));
        (await H.BalanceAsync(org)).ReservedMillis.Should().Be(0);
        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(18_000);
    }

    [DatabaseFact]
    public async Task Turn_WithOneMillicredit_IsRefused()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await UseAllowanceAsync(org, 20_000);
        await H.GrantAsync(org, 1);

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());

        (turn.Allowed, turn.Code).Should().Be((false, NonWorkflowChargeCodes.InsufficientCredits));
        (await H.BalanceAsync(org)).ReservedMillis.Should().Be(0);
    }

    [DatabaseFact]
    public async Task Turn_WithZeroBalanceAndNoAllowanceLeft_IsRefused()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await UseAllowanceAsync(org, 20_000);

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        (turn.Allowed, turn.Code, turn.BalanceMillis).Should().Be((false, NonWorkflowChargeCodes.InsufficientCredits, 0L));
    }

    /// <summary>
    /// An allowance-only organization at a zero balance runs while its allowance left covers the minimum turn: with 5
    /// credits of it left a turn is admitted and settles free; with 3 left it is refused (unless the minimum is 3).
    /// </summary>
    [DatabaseFact]
    public async Task Turn_WithThreeCreditsOfAllowanceLeftAndNoBalance_IsRefused_UnlessTheMinimumIsThree()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync(); // trial: 20 credits a month, no credits at all
        await UseAllowanceAsync(org, 15_000);

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());
        (turn.Allowed, turn.RequiredMillis, turn.AllowanceHeldMillis, turn.CreditHeldMillis).Should().Be((true, 5_000L, 5_000L, 0L));
        TurnSettlement settled = await H.Charges.SettleTurnAsync(turn, 2_000);
        (settled.Ok, settled.AllowanceMillis, settled.ChargedMillis, settled.UnchargedMillis).Should().Be((true, 2_000L, 0L, 0L));
        (await H.EntriesAsync(org)).Should().BeEmpty("the allowance is consumed, not debited");

        TurnHold next = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (next.Allowed, next.Code, next.HoldId).Should().Be((false, NonWorkflowChargeCodes.InsufficientCredits, (Guid?)null));
        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(17_000, "a refused turn holds nothing");

        H.Options.NonWorkflow.AssistantMinTurnCredits = 3;
        TurnHold small = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (small.Allowed, small.RequiredMillis, small.AllowanceHeldMillis).Should().Be((true, 3_000L, 3_000L));
    }

    /// <summary>The in-flight cap: the fifth open charged turn of an organization is refused; BYO is exempt.</summary>
    [DatabaseFact]
    public async Task Turn_FifthConcurrentTurn_IsRefused_ButAByoTurnIsExempt()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 100_000);
        await UseAllowanceAsync(org, 20_000);

        List<TurnHold> open = [];
        for (int i = 0; i < 4; i++) open.Add(await H.Charges.BeginTurnAsync(org, i % 2 == 0 ? UsageSource.Assistant : UsageSource.Mcp, Guid.NewGuid()));
        open.Should().OnlyContain(t => t.Allowed);

        TurnHold fifth = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());
        (fifth.Allowed, fifth.Code, fifth.HoldId).Should().Be((false, NonWorkflowChargeCodes.TurnConcurrencyLimit, (Guid?)null));
        (await H.BalanceAsync(org)).ReservedMillis.Should().Be(20_000, "a refused turn reserves nothing");

        (await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid(), UsageBillingMode.Byo)).Allowed.Should().BeTrue();

        (await H.Charges.SettleTurnAsync(open[0], 1_000)).Ok.Should().BeTrue();
        (await H.Charges.ReleaseTurnAsync(open[1])).Should().BeTrue();
        (await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid())).Allowed.Should().BeTrue("two turns closed");
    }

    [DatabaseFact]
    public async Task Turn_IsRefused_WhenTheAllowanceIsUsedUpAndNoCreditsAreAvailable()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 5_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold spender = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (spender.Allowed, spender.CreditHeldMillis).Should().Be((true, 5_000L));

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        (turn.Allowed, turn.Code, turn.HoldId).Should().Be((false, NonWorkflowChargeCodes.InsufficientCredits, (Guid?)null));
        turn.BalanceMillis.Should().Be(0, "the other turn's reservation holds the whole balance");
        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((5_000L, 5_000L));
    }

    /// <summary>
    /// A process that dies between a turn's hold and its settlement leaves the hold Open. The next turn of that
    /// organization sweeps every hold open for more than an hour (allowance and credit reservation freed, status
    /// Swept), and leaves a younger one alone.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_HoldsLeftOpenForMoreThanAnHour_AreSweptByTheNextTurn()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 18_000);
        TurnHold crashed = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (crashed.AllowanceHeldMillis, crashed.CreditHeldMillis).Should().Be((2_000L, 3_000L));

        H.Time.Now += TimeSpan.FromMinutes(59);
        TurnHold young = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());
        (young.AllowanceHeldMillis, young.CreditHeldMillis).Should().Be((0L, 5_000L), "the crashed hold is not an hour old yet");
        (await H.Charges.ReleaseTurnAsync(young)).Should().BeTrue();

        H.Time.Now += TimeSpan.FromMinutes(2);
        TurnHold next = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        (next.AllowanceHeldMillis, next.CreditHeldMillis).Should().Be((2_000L, 3_000L), "the crashed hold's allowance is free again");
        await using (InferenceApiDbContext db = H.NewDb())
        {
            AssistantAllowanceHold row = await db.AssistantAllowanceHolds.AsNoTracking().SingleAsync(h => h.Id == crashed.HoldId);
            row.Status.Should().Be(AssistantAllowanceHoldStatus.Swept);
            row.ClosedAt.Should().Be(H.Time.Now.UtcDateTime);
        }

        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((50_000L, 3_000L), "only the new turn's reservation is left");
        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(20_000);

        // The swept turn was not dead after all: it settles late as a hold of 0 and is still charged. The allowance is
        // used up (18 settled + the new turn's 2 held), so all 5 credits come from the balance, through a ledger
        // reservation of its own (never the released one's key).
        TurnSettlement late = await H.Charges.SettleTurnAsync(crashed, 5_000);
        (late.Ok, late.AllowanceMillis, late.ChargedMillis, late.UnchargedMillis).Should().Be((true, 0L, 5_000L, 0L));
        balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((45_000L, 3_000L), "only the new turn's reservation is still held");
        List<CreditLedgerEntry> entries = await H.EntriesAsync(org);
        entries.Select(e => e.IdempotencyKey).Should().OnlyHaveUniqueItems();
        entries.Where(e => e.Kind == CreditLedgerEntryKind.Settle).Sum(e => e.BalanceDeltaMillis).Should().Be(-5_000);
        await using (InferenceApiDbContext db = H.NewDb())
        {
            AssistantAllowanceHold row = await db.AssistantAllowanceHolds.AsNoTracking().SingleAsync(h => h.Id == crashed.HoldId);
            (row.Status, row.ConsumedMillis, row.ChargedMillis, row.UnchargedMillis)
                .Should().Be((AssistantAllowanceHoldStatus.Settled, 0L, 5_000L, 0L));
        }

        TurnSettlement again = await H.Charges.SettleTurnAsync(crashed, 5_000);
        (again.Code, again.ChargedMillis).Should().Be((NonWorkflowChargeCodes.Resumed, 5_000L));
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(45_000, "settling again charges nothing more");
    }

    /// <summary>
    /// A swept turn that settles late takes the allowance first (up to what nobody else holds or has consumed), then
    /// is charged the rest in credits, in full: a turn kept open past the stale threshold is charged its actual.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_SweptThenSettledLate_TakesTheAllowanceLeft_ThenChargesTheRestInFull()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync(); // trial: 20 credits a month
        await H.GrantAsync(org, 2_000);
        await UseAllowanceAsync(org, 15_000);
        TurnHold crashed = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (crashed.AllowanceHeldMillis, crashed.CreditHeldMillis).Should().Be((5_000L, 0L));

        H.Time.Now += TimeSpan.FromMinutes(61);
        TurnHold other = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());
        (other.AllowanceHeldMillis, other.CreditHeldMillis).Should().Be((5_000L, 0L), "the sweep freed the crashed hold's allowance");
        (await H.Charges.SettleTurnAsync(other, 2_000)).Ok.Should().BeTrue();

        using SettleCapListener overshoot = new();
        TurnSettlement late = await H.Charges.SettleTurnAsync(crashed, 10_000);

        // 3 credits of allowance left; the 7 beyond it are charged in full (2 of balance, 5 below zero, counted).
        (late.Ok, late.AllowanceMillis, late.ChargedMillis, late.UnchargedMillis).Should().Be((true, 3_000L, 7_000L, 0L));
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(-5_000);
        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(20_000);
        overshoot.Count(UsageSource.Assistant).Should().BeGreaterThanOrEqualTo(1);
        await using InferenceApiDbContext db = H.NewDb();
        AssistantAllowanceHold row = await db.AssistantAllowanceHolds.AsNoTracking().SingleAsync(h => h.Id == crashed.HoldId);
        (row.Status, row.ConsumedMillis, row.ChargedMillis, row.UnchargedMillis)
            .Should().Be((AssistantAllowanceHoldStatus.Settled, 3_000L, 7_000L, 0L));
    }

    /// <summary>
    /// T-026 (finding 1): a turn whose settlement failed twice leaves its hold Open with its metered usage recorded on
    /// it. The stale sweep then charges that usage (allowance first, then credits) instead of releasing the hold, and a
    /// later late settle charges only what the sweep did not: the ledger shows the turn's charge exactly once.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_WhoseSettlementFailedTwice_IsChargedByTheStaleSweep_ExactlyOnce()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        // Both settlement attempts fail (the ledger was unreachable for the request); everything else is the real service.
        Mock<INonWorkflowChargeService> failing = new();
        failing.Setup(c => c.SettleTurnAsync(It.IsAny<TurnHold>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TurnSettlement(false, 0, 0, 0, NonWorkflowChargeCodes.GateUnavailable));
        failing.Setup(c => c.RecordUnsettledTurnAsync(It.IsAny<TurnHold>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns((TurnHold t, long m, CancellationToken ct) => H.Charges.RecordUnsettledTurnAsync(t, m, ct));
        await NonWorkflowChargeResults.CloseTurnAsync(failing.Object, turn, 12_000, null);
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(50_000, "nothing was charged yet");

        H.Time.Now += TimeSpan.FromMinutes(61);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid())).Allowed.Should().BeTrue();

        static long Settled(List<CreditLedgerEntry> entries) => entries.Where(e => e.Kind == CreditLedgerEntryKind.Settle).Sum(e => e.BalanceDeltaMillis);
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(38_000, "the sweep charged the recorded 12 credits");
        Settled(await H.EntriesAsync(org)).Should().Be(-12_000);

        // The request was not dead after all and settles late: only what the sweep did not charge is charged.
        (await H.Charges.SettleTurnAsync(turn, 12_000)).ChargedMillis.Should().Be(0, "the sweep already charged it");
        Settled(await H.EntriesAsync(org)).Should().Be(-12_000, "charged exactly once");
        (await H.EntriesAsync(org)).Select(e => e.IdempotencyKey).Should().OnlyHaveUniqueItems();
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(38_000);
    }

    /// <summary>T-026 (finding 1): a late settle after a charging sweep pays only the remainder, once.</summary>
    [DatabaseFact]
    public async Task Turn_ChargedByTheSweep_ThenSettledLateForMore_PaysOnlyTheRemainder()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        // A pre-claimed step beyond the hold is recorded on it; then the process died.
        (await H.Charges.ClaimStepAsync(turn, new TurnStepClaim(0, 0, 9_000))).Continue.Should().BeTrue();
        H.Time.Now += TimeSpan.FromMinutes(61);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid())).Allowed.Should().BeTrue();
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(41_000, "the recorded 9-credit exposure was charged");

        TurnSettlement late = await H.Charges.SettleTurnAsync(turn, 11_000);
        late.ChargedMillis.Should().Be(2_000, "only the remainder");
        (await H.Charges.SettleTurnAsync(turn, 11_000)).Code.Should().Be(NonWorkflowChargeCodes.Resumed);
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(39_000);
    }

    /// <summary>
    /// The real service, except that every settlement fails (the ledger was unreachable for the request) and, unless
    /// <paramref name="recordWorks"/>, recording the unsettled turn fails too (the same outage).
    /// </summary>
    private Mock<INonWorkflowChargeService> FailingSettlements(bool recordWorks)
    {
        Mock<INonWorkflowChargeService> failing = new();
        failing.Setup(c => c.SettleTurnAsync(It.IsAny<TurnHold>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TurnSettlement(false, 0, 0, 0, NonWorkflowChargeCodes.GateUnavailable));
        if (recordWorks)
        {
            failing.Setup(c => c.RecordUnsettledTurnAsync(It.IsAny<TurnHold>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .Returns((TurnHold t, long m, CancellationToken ct) => H.Charges.RecordUnsettledTurnAsync(t, m, ct));
        }
        else
        {
            failing.Setup(c => c.RecordUnsettledTurnAsync(It.IsAny<TurnHold>(), It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("the database is down"));
        }

        return failing;
    }

    private static long SettledSum(List<CreditLedgerEntry> entries)
        => entries.Where(e => e.Kind == CreditLedgerEntryKind.Settle).Sum(e => e.BalanceDeltaMillis);

    /// <summary>
    /// T-040 (item 1): a turn whose usage stayed within its hold, whose settlement failed twice and whose usage could
    /// not be recorded either (same outage) is still charged by the stale sweep: its first paid claim marked the hold
    /// used, so the sweep charges it (at least its hold, the actual being unknown), exactly once.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_WithinItsHold_WhoseSettleAndRecordFailed_IsChargedByTheSweep_ExactlyOnce()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (turn.AllowanceHeldMillis, turn.CreditHeldMillis).Should().Be((0L, 5_000L));

        // Its only paid step stays within the 5-credit hold.
        (await H.Charges.ClaimStepAsync(turn, new TurnStepClaim(0, 0, 3_000))).Continue.Should().BeTrue();
        await NonWorkflowChargeResults.CloseTurnAsync(FailingSettlements(recordWorks: false).Object, turn, 4_000, null);
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(50_000, "nothing was charged yet");

        H.Time.Now += TimeSpan.FromMinutes(61);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid())).Allowed.Should().BeTrue();

        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(45_000, "the sweep charged the used hold");
        SettledSum(await H.EntriesAsync(org)).Should().Be(-5_000);
        await using (InferenceApiDbContext db = H.NewDb())
        {
            AssistantAllowanceHold row = await db.AssistantAllowanceHolds.AsNoTracking().SingleAsync(h => h.Id == turn.HoldId);
            (row.Status, row.ChargedMillis).Should().Be((AssistantAllowanceHoldStatus.Swept, 5_000L));
        }

        // A second sweep and a late settle charge nothing more.
        H.Time.Now += TimeSpan.FromMinutes(61);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid())).Allowed.Should().BeTrue();
        (await H.Charges.SettleTurnAsync(turn, 4_000)).ChargedMillis.Should().Be(0);
        SettledSum(await H.EntriesAsync(org)).Should().Be(-5_000, "charged exactly once");
        (await H.EntriesAsync(org)).Select(e => e.IdempotencyKey).Should().OnlyHaveUniqueItems();
    }

    /// <summary>T-040 (item 1): a hold that never metered anything (no paid claim, nothing recorded) is still released.</summary>
    [DatabaseFact]
    public async Task Turn_ThatNeverClaimedAPaidStep_IsReleasedByTheSweep()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (await H.Charges.CheckTurnAsync(turn, 0)).Continue.Should().BeTrue("a check of a zero-cost step is not a paid claim");

        H.Time.Now += TimeSpan.FromMinutes(61);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid())).Allowed.Should().BeTrue();

        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(50_000);
        SettledSum(await H.EntriesAsync(org)).Should().Be(0);
    }

    /// <summary>
    /// T-040 (item 3): when the failed settlement's metered actual was recorded and is below the hold, the sweep charges
    /// exactly that actual, not the hold.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_WhoseRecordedActualIsBelowItsHold_IsChargedExactlyThatActualByTheSweep()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (await H.Charges.ClaimStepAsync(turn, new TurnStepClaim(0, 0, 4_500))).Continue.Should().BeTrue();
        await NonWorkflowChargeResults.CloseTurnAsync(FailingSettlements(recordWorks: true).Object, turn, 3_000, null);

        H.Time.Now += TimeSpan.FromMinutes(61);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid())).Allowed.Should().BeTrue();

        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(47_000, "the recorded 3-credit actual, not the 5-credit hold");
        SettledSum(await H.EntriesAsync(org)).Should().Be(-3_000);
        (await H.Charges.SettleTurnAsync(turn, 3_000)).ChargedMillis.Should().Be(0, "the sweep already charged it");
        SettledSum(await H.EntriesAsync(org)).Should().Be(-3_000);
    }

    /// <summary>T-040 (item 3): a recorded actual of nothing is known too: the sweep releases the hold it claimed on.</summary>
    [DatabaseFact]
    public async Task Turn_WhoseRecordedActualIsZero_IsReleasedByTheSweep_ThoughItClaimedAStep()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (await H.Charges.ClaimStepAsync(turn, new TurnStepClaim(0, 0, 4_500))).Continue.Should().BeTrue();
        await H.Charges.RecordUnsettledTurnAsync(turn, 0);

        H.Time.Now += TimeSpan.FromMinutes(61);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid())).Allowed.Should().BeTrue();

        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(50_000);
        SettledSum(await H.EntriesAsync(org)).Should().Be(0);
    }

    /// <summary>
    /// T-040 (item 2): a stale hold taken in an earlier allowance period is swept against that period, never the current
    /// one: its own allowance hold (taken within that period's allowance) covers what it can, the rest is charged in
    /// credits, and the current period's allowance is untouched.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_SweptAcrossAPeriodBoundary_UsesOnlyItsOwnPeriodsHold_NeverTheCurrentAllowance()
    {
        if (!Available) return;
        H.Time.Now = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        Guid org = await H.AddOrgAsync(); // trial: 20 credits a calendar month
        await H.GrantAsync(org, 50_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (turn.AllowanceHeldMillis, turn.CreditHeldMillis).Should().Be((5_000L, 0L));
        (await H.Charges.ClaimStepAsync(turn, new TurnStepClaim(0, 0, 9_000))).Continue.Should().BeTrue();

        H.Time.Now += TimeSpan.FromMinutes(61); // 1 November, 00:31: a new period
        TurnHold next = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());
        (next.AllowanceHeldMillis, next.CreditHeldMillis).Should().Be((5_000L, 0L));

        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(46_000, "5 of the 9 from October's own hold, 4 in credits");
        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(5_000, "November's allowance holds only the new turn");
        await using InferenceApiDbContext db = H.NewDb();
        AssistantAllowanceHold row = await db.AssistantAllowanceHolds.AsNoTracking().SingleAsync(h => h.Id == turn.HoldId);
        (row.Status, row.ConsumedMillis, row.ChargedMillis).Should().Be((AssistantAllowanceHoldStatus.Swept, 5_000L, 4_000L));
    }

    /// <summary>T-040 (item 2): an unlimited turn swept in a later period still charges nothing (its zero hold is unlimited).</summary>
    [DatabaseFact]
    public async Task UnlimitedTurn_SweptAcrossAPeriodBoundary_ChargesNothing()
    {
        if (!Available) return;
        H.Time.Now = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        Guid org = await H.AddOrgAsync("enterprise");
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (await H.Charges.ClaimStepAsync(turn, new TurnStepClaim(0, 0, 9_000))).Continue.Should().BeTrue();

        H.Time.Now += TimeSpan.FromMinutes(61);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid())).Allowed.Should().BeTrue();

        (await H.EntriesAsync(org)).Should().BeEmpty();
    }

    /// <summary>
    /// The late-settle path keys on the Swept status, not on how long the hold was open: a turn that released itself
    /// (it failed or its client left), however long after it began, is never charged again.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_ThatReleasedItself_IsNeverChargedAgain_EvenAfterTheStaleAge()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        H.Time.Now += TimeSpan.FromHours(2);
        (await H.Charges.ReleaseTurnAsync(turn)).Should().BeTrue();
        TurnSettlement again = await H.Charges.SettleTurnAsync(turn, 5_000);

        (again.ChargedMillis, again.Code).Should().Be((0L, NonWorkflowChargeCodes.Resumed));
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(50_000);
    }

    /// <summary>
    /// F-4.2: the stale threshold is at least twice the wall-clock ceiling of a turn (Assistant:MaxTurnSeconds), so the
    /// sweep never takes a turn that is still allowed to run.
    /// </summary>
    [DatabaseFact]
    public async Task Turn_StaleSweep_NeverReleasesATurnYoungerThanTheWallClockCeiling()
    {
        if (!Available) return;
        TimeSpan ceiling = TimeSpan.FromSeconds(ReelBolt.Inference.Api.Services.Assistant.AssistantChatLimits.DefaultMaxTurnSeconds);
        NonWorkflowChargeService.StaleTurnHoldAge.Should().BeGreaterThanOrEqualTo(ceiling * 2);
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        TurnHold running = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());

        H.Time.Now += ceiling + TimeSpan.FromMinutes(5);
        (await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid())).Allowed.Should().BeTrue();

        await using InferenceApiDbContext db = H.NewDb();
        (await db.AssistantAllowanceHolds.AsNoTracking().SingleAsync(h => h.Id == running.HoldId)).Status
            .Should().Be(AssistantAllowanceHoldStatus.Open);
    }

    [DatabaseFact]
    public async Task Turn_Release_FreesTheAllowanceAndTheCreditHold()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 18_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(20_000, "an open turn holds its allowance");

        (await H.Charges.ReleaseTurnAsync(turn)).Should().BeTrue();
        (await H.Charges.ReleaseTurnAsync(turn)).Should().BeTrue("releasing twice is a no-op");

        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(18_000);
        CreditLedgerResult balance = await H.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((50_000L, 0L));
        (await H.Charges.SettleTurnAsync(turn, 5_000)).ChargedMillis.Should().Be(0, "a released turn cannot be settled afterwards");
    }

    [DatabaseFact]
    public async Task Turn_SettledAtZero_Releases()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());
        TurnSettlement settled = await H.Charges.SettleTurnAsync(turn, 0);

        (settled.Ok, settled.AllowanceMillis, settled.ChargedMillis).Should().Be((true, 0L, 0L));
        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(0);
    }

    [DatabaseFact]
    public async Task Turn_ReplayedWithTheSameRequestId_ReusesTheHold()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);
        Guid request = Guid.NewGuid();

        TurnHold first = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, request);
        TurnHold again = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, request);

        again.HoldId.Should().Be(first.HoldId);
        (await H.BalanceAsync(org)).ReservedMillis.Should().Be(5_000);
        await H.Charges.SettleTurnAsync(first, 4_000);
        await H.Charges.SettleTurnAsync(again, 4_000);
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(46_000);
    }

    [DatabaseFact]
    public async Task Turn_OnAnUnlimitedPlan_HoldsNoCredits()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("enterprise");

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (turn.Allowed, turn.RequiredMillis, turn.AllowanceHeldMillis, turn.CreditHeldMillis).Should().Be((true, 0L, 0L, 0L));
        (await H.Charges.SettleTurnAsync(turn, 1_000_000)).ChargedMillis.Should().Be(0);
        (await H.EntriesAsync(org)).Should().BeEmpty();
    }

    // ---- every paid step is pre-claimed at its worst case before it runs -----------

    /// <summary>
    /// F-5.2: the check runs before the FIRST step, sized on the step about to run: a turn admitted on its 5-credit hold
    /// with nothing else available cannot start an 8-credit round-trip (5 held + 1 of headroom), even at 0 metered.
    /// </summary>
    [DatabaseFact]
    public async Task Step_WhoseWorstCaseWouldPassWhatTheTurnCanPay_IsRefusedBeforeItRuns_EvenAsTheFirstStep()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 5_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        turn.Allowed.Should().BeTrue();

        TurnCheck huge = await H.Charges.ClaimStepAsync(turn, new TurnStepClaim(0, 0, 8_000));
        (huge.Continue, huge.Code).Should().Be((false, NonWorkflowChargeCodes.InsufficientCredits));

        (await H.Charges.ClaimStepAsync(turn, new TurnStepClaim(0, 0, 6_000))).Continue.Should().BeTrue("5 held + 1 of headroom covers it");
        (await H.Charges.ClaimStepAsync(turn, new TurnStepClaim(0, 4_000, 3_000))).Continue.Should().BeFalse("the turn's other in-flight claims count");
    }

    [DatabaseFact]
    public async Task Step_PastTheTurnCeiling_IsRefusedWithTheTurnLimit_HoweverMuchIsAvailable()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 1_000_000);
        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        TurnCheck check = await H.Charges.ClaimStepAsync(turn, new TurnStepClaim(10_000, 0, 91_000));

        (check.Continue, check.Code).Should().Be((false, NonWorkflowChargeCodes.TurnLimit));
    }

    /// <summary>
    /// F-5.2: a claim is recorded on the turn's open hold, so other turns' checks count it; once the step's usage lands
    /// the claim is replaced by the metered actual.
    /// </summary>
    [DatabaseFact]
    public async Task Step_ClaimsAreSeenByOtherTurns_AndReplacedByTheActualWhenTheyLand()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 20_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold a = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        TurnHold b = await H.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid());

        // 10 credits unreserved + 5 of its own + 1 of headroom = 16 for either alone; a claims 12 (7 beyond its hold).
        (await H.Charges.ClaimStepAsync(a, new TurnStepClaim(0, 0, 12_000))).Continue.Should().BeTrue();
        (await H.Charges.ClaimStepAsync(b, new TurnStepClaim(0, 0, 10_000))).Continue.Should().BeFalse("16 - 7 claimed by a = 9");

        await H.Charges.LandStepAsync(a, 6_000, 0);
        (await H.Charges.ClaimStepAsync(b, new TurnStepClaim(0, 0, 10_000))).Continue.Should().BeTrue("a's claim became its 6-credit actual: 16 - 1 = 15");
    }

    /// <summary>
    /// F-7: a BYO-chat turn is exempt from the in-flight cap until its first Platform-billed step, which is admitted
    /// under the cap (refused while the workspace already has the maximum open); what it then metered is charged.
    /// </summary>
    [DatabaseFact]
    public async Task ByoTurn_FirstPlatformStep_CountsAgainstTheCap_AndItsPlatformUsageIsCharged()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 100_000);
        await UseAllowanceAsync(org, 20_000);
        List<TurnHold> open = [];
        for (int i = 0; i < 4; i++) open.Add(await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid()));
        TurnHold byo = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid(), UsageBillingMode.Byo);
        byo.Allowed.Should().BeTrue("a pure BYO turn is not refused by the cap");

        TurnCheck capped = await H.Charges.ClaimStepAsync(byo, new TurnStepClaim(0, 0, 10_000, AdmitUnderConcurrencyCap: true));
        (capped.Continue, capped.Code).Should().Be((false, NonWorkflowChargeCodes.TurnConcurrencyLimit));
        (await H.Charges.ClaimStepAsync(byo, new TurnStepClaim(0, 0, 1_000))).Continue.Should().BeTrue("a step that is not Platform-billed is not capped");

        (await H.Charges.SettleTurnAsync(open[0], 0)).Ok.Should().BeTrue();
        (await H.Charges.ClaimStepAsync(byo, new TurnStepClaim(0, 0, 10_000, AdmitUnderConcurrencyCap: true))).Continue.Should().BeTrue();

        long before = (await H.BalanceAsync(org)).BalanceMillis;
        TurnSettlement settled = await H.Charges.SettleTurnAsync(byo, 3_000);
        settled.ChargedMillis.Should().Be(3_000, "the Platform call the BYO turn made is charged");
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(before - 3_000);
    }

    [Fact]
    public void WorstCaseOvershoot_IsTheHeadroomPlusOneEstimationErrorPerOpenTurn()
    {
        NonWorkflowChargeService.WorstCaseOvershootMillis(4, 1_000, 200).Should().Be(1_800);
        NonWorkflowChargeService.WorstCaseOvershootMillis(1, 1_000, 0).Should().Be(1_000, "with exact estimates only the headroom is ever overdrawn");
    }

    // ---- the only turns admitted without a hold --------------------------------------

    /// <summary>
    /// F-6, T-026: the explicit unlimited entitlement is admitted under its own code with a zero credit hold (so it is
    /// still pre-claimed, capped and counted in flight), and charges nothing.
    /// </summary>
    [DatabaseFact]
    public async Task UnlimitedAllowance_IsTheExplicitEntitlement_HoldsZeroCredits_AndChargesNothing()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync("enterprise");

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());

        (turn.Allowed, turn.HoldId.HasValue, turn.Code, turn.CreditHeldMillis).Should().Be((true, true, NonWorkflowChargeCodes.UnlimitedAllowance, 0L));
        (await H.Charges.CheckTurnAsync(turn, 20_000)).Continue.Should().BeTrue();
        (await H.Charges.SettleTurnAsync(turn, 20_000)).ChargedMillis.Should().Be(0);
        (await H.EntriesAsync(org)).Should().BeEmpty();
    }

    /// <summary>
    /// F-6: a pure BYO turn is never one of the hold-less turns (it holds like any other and is exempt only from the
    /// in-flight cap); with nothing metered it settles by releasing, so it is free.
    /// </summary>
    [DatabaseFact]
    public async Task AllowedWithoutHold_NeverIncludesAByoTurn_WhichHolds_AndSettlesFreeWhenNothingIsMetered()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 10_000);

        TurnHold turn = await H.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid(), UsageBillingMode.Byo);

        (turn.Allowed, turn.HoldId.HasValue).Should().Be((true, true));
        NonWorkflowChargeCodes.IsAllowedWithoutHold(turn.Code).Should().BeFalse();
        (await H.Charges.SettleTurnAsync(turn, 0)).ChargedMillis.Should().Be(0);
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(10_000);
        (await H.Charges.AllowanceUsedAsync(org)).Should().Be(0);
    }

    /// <summary>
    /// F-6: an admitted turn with no hold and any code other than billing-off or the explicit unlimited entitlement
    /// (a future path, a mis-built hold) is still charged at its metered actual, keyed on the request id (a retry
    /// charges once), and its in-turn check fails closed.
    /// </summary>
    [DatabaseFact]
    public async Task AnAdmittedTurnWithoutAHold_OutsideTheAllowedCodes_IsChargedAnyway_KeyedOnTheRequestId()
    {
        if (!Available) return;
        Guid org = await H.AddOrgAsync();
        await H.GrantAsync(org, 50_000);
        await UseAllowanceAsync(org, 20_000);
        TurnHold holdless = new(true, null, 0, 0, NonWorkflowChargeCodes.NoPlatformCost)
        {
            OrganizationId = org,
            Source = UsageSource.Assistant,
            RequestId = Guid.NewGuid()
        };

        (await H.Charges.CheckTurnAsync(holdless, 0)).Continue.Should().BeFalse("a hold-less turn outside the allowed codes is not run on");
        TurnSettlement settled = await H.Charges.SettleTurnAsync(holdless, 3_000);
        TurnSettlement again = await H.Charges.SettleTurnAsync(holdless, 3_000);

        (settled.Ok, settled.ChargedMillis).Should().Be((true, 3_000L));
        again.Ok.Should().BeTrue();
        (await H.BalanceAsync(org)).BalanceMillis.Should().Be(47_000, "charged once, keyed on the request id");
    }

    [DatabaseFact]
    public async Task Turn_WithNoOrganization_IsRefused_WhenBillingIsOn()
    {
        if (!Available) return;

        TurnHold turn = await H.Charges.BeginTurnAsync(null, UsageSource.Mcp, Guid.NewGuid());

        (turn.Allowed, turn.Code).Should().Be((false, NonWorkflowChargeCodes.NoOrganization));
    }
}

/// <summary>Counts <c>reelbolt.billing.nonworkflow_settle_capped</c> measurements by source while alive.</summary>
internal sealed class SettleCapListener : IDisposable
{
    private readonly System.Diagnostics.Metrics.MeterListener _listener = new();
    private readonly System.Collections.Concurrent.ConcurrentBag<string?> _sources = [];

    public SettleCapListener()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "reelbolt.billing.nonworkflow_settle_capped") listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                if (tag.Key == "source") _sources.Add(tag.Value?.ToString());
            }
        });
        _listener.Start();
    }

    public int Count(UsageSource source) => _sources.Count(s => s == source.ToString());

    public void Dispose() => _listener.Dispose();
}

/// <summary>The scenarios on Sqlite (logic; no advisory lock).</summary>
public sealed class NonWorkflowChargeServiceTests : NonWorkflowChargeServiceTestsBase
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public NonWorkflowChargeServiceTests() => _connection.Open();

    protected override Action<DbContextOptionsBuilder>? Configure => o => o.UseSqlite(_connection);

    protected override Task CreateSchemaAsync(InferenceApiDbContext db) => db.Database.EnsureCreatedAsync();

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        _connection.Dispose();
    }

    private static NonWorkflowChargingHost Off() => new(o => o.UseSqlite("Data Source=:memory:"), billingEnabled: false);

    [Fact]
    public async Task BillingDisabled_IsANoOp_ThatTouchesNoDatabase()
    {
        // A database with no schema at all: any read or write would throw.
        using NonWorkflowChargingHost off = Off();
        Guid org = Guid.NewGuid();

        ChargeHold hold = await off.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), default);
        (hold.Allowed, hold.Code, hold.ReservationId).Should().Be((true, NonWorkflowChargeCodes.BillingDisabled, (Guid?)null));
        (await off.Charges.SettleAsync(hold, 5_000)).Should().BeTrue();
        (await off.Charges.ReleaseAsync(hold)).Should().BeTrue();
        TurnHold turn = await off.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (turn.Allowed, turn.Code, turn.HoldId).Should().Be((true, NonWorkflowChargeCodes.BillingDisabled, (Guid?)null));
        (await off.Charges.BeginTurnAsync(null, UsageSource.Mcp, Guid.NewGuid())).Allowed.Should().BeTrue("no workspace is fine when nothing is charged");
        (await off.Charges.SettleTurnAsync(turn, 1_000_000)).ChargedMillis.Should().Be(0);
        (await off.Charges.ReleaseTurnAsync(turn)).Should().BeTrue();
        (await off.Charges.EstimateOneUnitAsync(PriceOf(UsageBillingMode.Platform))).Should().Be(0);
    }

    [Fact]
    public async Task LedgerFailure_FailsClosed()
    {
        // Billing on, but the database has no schema: every ledger call throws.
        using NonWorkflowChargingHost broken = new(o => o.UseSqlite("Data Source=:memory:"));
        Guid org = Guid.NewGuid();

        ChargeHold hold = await broken.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), default);
        (hold.Allowed, hold.Code).Should().Be((false, NonWorkflowChargeCodes.GateUnavailable));

        TurnHold turn = await broken.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
        (turn.Allowed, turn.Code).Should().Be((false, NonWorkflowChargeCodes.GateUnavailable));
    }

    private static PriceRequest PriceOf(UsageBillingMode mode) => new(
        Guid.NewGuid(), null, InferenceProviderKind.FalAi, InferenceProviderCapability.ImageErase, "fal-ai/x",
        UsageKind.ImageEditImages, null, mode, 1);

    [Fact]
    public async Task Estimate_IsOneUnitPriced_ZeroForByo_AndTenCreditsWhenUnpriced()
    {
        var prices = new NonWorkflowChargingHost.FixedPrices(4_000);
        using NonWorkflowChargingHost priced = new(o => o.UseSqlite(_connection), prices: prices);
        (await priced.Charges.EstimateOneUnitAsync(PriceOf(UsageBillingMode.Platform) with { Quantity = 7 })).Should().Be(4_000);
        prices.Requests.Should().ContainSingle().Which.Quantity.Should().Be(1);
        (await priced.Charges.EstimateOneUnitAsync(PriceOf(UsageBillingMode.Byo))).Should().Be(0);

        using NonWorkflowChargingHost unpriced = new(o => o.UseSqlite(_connection), prices: new NonWorkflowChargingHost.FixedPrices(null));
        (await unpriced.Charges.EstimateOneUnitAsync(PriceOf(UsageBillingMode.Platform))).Should().Be(10_000);
    }

    [Fact]
    public async Task Estimate_PricesTheRequestedQuantity_HoldsTheUnpricedDefaultWhenUnpricedOrFailing_AndIsZeroOnlyForByoOrBillingOff()
    {
        // a Platform call with no price, or whose quote throws, is never estimated at 0 (which would let it
        // run with no hold): it holds the configured unpriced default and settles at least that.
        var prices = new NonWorkflowChargingHost.FixedPrices(2);
        using NonWorkflowChargingHost priced = new(o => o.UseSqlite(_connection), prices: prices);
        (await priced.Charges.EstimateAsync(PriceOf(UsageBillingMode.Platform) with { Quantity = 3_000 })).Should().Be(6_000);
        (await priced.Charges.EstimateAsync(PriceOf(UsageBillingMode.Byo) with { Quantity = 3_000 })).Should().Be(0);
        (await priced.Charges.EstimateAsync(PriceOf(UsageBillingMode.Runner) with { Quantity = 3_000 })).Should().Be(0);

        using NonWorkflowChargingHost unpriced = new(o => o.UseSqlite(_connection), prices: new NonWorkflowChargingHost.FixedPrices(null));
        (await unpriced.Charges.EstimateAsync(PriceOf(UsageBillingMode.Platform))).Should().Be(10_000);
        (await unpriced.Charges.EstimateAsync(PriceOf(UsageBillingMode.Byo))).Should().Be(0);

        using NonWorkflowChargingHost failing = new(o => o.UseSqlite(_connection), prices: new ThrowingPrices());
        (await failing.Charges.EstimateAsync(PriceOf(UsageBillingMode.Platform))).Should().Be(10_000);
        (await failing.Charges.EstimateOneUnitAsync(PriceOf(UsageBillingMode.Platform))).Should().Be(10_000);

        using NonWorkflowChargingHost noPriceService = new(o => o.UseSqlite(_connection));
        (await noPriceService.Charges.EstimateAsync(PriceOf(UsageBillingMode.Platform))).Should().Be(10_000);

        using NonWorkflowChargingHost off = new(o => o.UseSqlite(_connection), billingEnabled: false, prices: new NonWorkflowChargingHost.FixedPrices(null));
        (await off.Charges.EstimateAsync(PriceOf(UsageBillingMode.Platform))).Should().Be(0);
        (await off.Charges.EstimateAsync(PriceOf(UsageBillingMode.Platform) with { PromptTokens = 500_000 }))
            .Should().Be(0, "a prompt-length tier changes which row prices a call, never whether billing is on");
    }

    [Fact]
    public async Task Estimate_UnpricedDefault_IsConfigurable()
    {
        using NonWorkflowChargingHost unpriced = new(o => o.UseSqlite(_connection), prices: new NonWorkflowChargingHost.FixedPrices(null));
        unpriced.Options.NonWorkflow.UnpricedEstimateCredits.Should().Be(10, "the existing 10-credit fallback is the default");
        unpriced.Options.NonWorkflow.UnpricedEstimateCredits = 25;

        (await unpriced.Charges.EstimateAsync(PriceOf(UsageBillingMode.Platform))).Should().Be(25_000);
        (await unpriced.Charges.EstimateOneUnitAsync(PriceOf(UsageBillingMode.Platform))).Should().Be(25_000);
    }

    private sealed class ThrowingPrices : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("price snapshot unavailable");

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void Invalidate()
        {
        }
    }

    [Fact]
    public void Options_DefaultToTheCreditGatePolicy()
    {
        NonWorkflowChargeOptions o = new();
        (o.OverdraftTolerancePercent, o.OverdraftFloorCredits, o.NonWorkflow.TimelineProposalHoldCredits, o.NonWorkflow.AssistantTurnHoldCredits)
            .Should().Be((20, 100L, 5L, 5L));
        (o.NonWorkflow.AssistantMinTurnCredits, o.NonWorkflow.AssistantTurnCeilingCredits, o.NonWorkflow.MaxConcurrentTurnsPerOrg)
            .Should().Be(((long?)null, 100L, 4));
        NonWorkflowChargeOptions.SectionName.Should().Be("Billing");
    }

    /// <summary>F-4.2: a wall-clock ceiling the stale sweep could overtake is a configuration error, refused up front.</summary>
    [Fact]
    public void TheTurnWallClock_MustBeAtMostHalfTheStaleThreshold()
    {
        NonWorkflowChargeService.ValidateTurnClock(TimeSpan.FromSeconds(1800));
        Action tooLong = () => NonWorkflowChargeService.ValidateTurnClock(TimeSpan.FromSeconds(1801));
        tooLong.Should().Throw<InvalidOperationException>().WithMessage("*Assistant:MaxTurnSeconds*");

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:MaxTurnSeconds"] = "3600" })
            .Build();
        Action build = () => _ = new NonWorkflowChargeService(
            null!, null!, Microsoft.Extensions.Options.Options.Create(new NonWorkflowChargeOptions { Enabled = true }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NonWorkflowChargeService>.Instance, configuration: configuration);
        build.Should().Throw<InvalidOperationException>();
    }
}

/// <summary>
/// The same scenarios on a real Postgres (advisory lock, jsonb, real migrations) when <c>REELBOLT_TEST_PG</c>
/// is set to a server connection string; each class instance migrates its own throwaway database. Skipped, not
/// passed, without it.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
[RequiresPostgres]
public sealed class NonWorkflowChargeServicePostgresTests : NonWorkflowChargeServiceTestsBase
{
    private static readonly string? BaseConnection = PostgresTestEnvironment.BaseConnectionString();
    private readonly string? _connectionString;
    private readonly string _dbName = "reelbolt_nwc_test_" + Guid.NewGuid().ToString("N")[..12];

    public NonWorkflowChargeServicePostgresTests()
    {
        if (string.IsNullOrWhiteSpace(BaseConnection)) return;
        _connectionString = PostgresTestEnvironment.ForDatabase(BaseConnection, _dbName);
    }

    protected override Action<DbContextOptionsBuilder>? Configure
        => _connectionString is null ? null : o => o.UseNpgsql(_connectionString);

    protected override async Task CreateSchemaAsync(InferenceApiDbContext db)
    {
        await using (NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(BaseConnection!))
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {_dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        await db.GetService<IMigrator>().MigrateAsync();
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_connectionString is null) return;
        NpgsqlConnection.ClearAllPools();
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(BaseConnection!);
        await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The security property: concurrent turns are reserved one at a time under the org's ledger lock, against the
    /// authoritative allowance counter, so N turns racing for one remaining turn of allowance (and no credits) let
    /// exactly one through, and the allowance consumed never exceeds what the plan includes.
    /// </summary>
    [PostgresTheory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task ConcurrentTurns_CannotTogetherSpendMoreThanTheAllowanceLeft(int concurrent)
    {
        if (!Available) return;
        Guid org = await Host.AddOrgAsync(); // trial: 20 credits a month, no credits at all
        await UseAllowanceAsync(org, 15_000); // one 5-credit turn of allowance left

        TurnHold[] turns = await RaceAsync(concurrent, i => Host.Charges.BeginTurnAsync(org, i % 2 == 0 ? UsageSource.Assistant : UsageSource.Mcp, Guid.NewGuid()));

        turns.Count(t => t.Allowed).Should().Be(1, "only one turn of allowance was left and there are no credits");
        turns.Where(t => !t.Allowed).Should().OnlyContain(t => t.Code == NonWorkflowChargeCodes.InsufficientCredits);

        // The allowed turn then runs until it is cut off (5 credits of allowance + 1 of headroom, no credits) and is
        // charged what it metered: the allowance cannot be overspent and the balance ends at minus the headroom.
        TurnHold allowed = turns.Single(t => t.Allowed);
        (await Host.Charges.CheckTurnAsync(allowed, 5_999)).Continue.Should().BeTrue();
        (await Host.Charges.CheckTurnAsync(allowed, 6_000)).Continue.Should().BeFalse();
        TurnSettlement settled = await Host.Charges.SettleTurnAsync(allowed, 6_000);
        settled.AllowanceMillis.Should().Be(5_000);
        (await Host.Charges.AllowanceUsedAsync(org)).Should().Be(20_000, "the allowance consumed never exceeds what the plan includes");
        CreditLedgerResult balance = await Host.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((-1_000L, 0L), "beyond the allowance only the headroom is metered before the cut-off");
    }

    /// <summary>
    /// F-2.4: 20 concurrent turns straddling the allowance edge (two turns of it left, plenty of credits) consume
    /// exactly the allowance and charge everything else in credits.
    /// </summary>
    [PostgresFact]
    public async Task TwentyConcurrentTurns_StraddlingTheAllowanceEdge_ConsumeExactlyTheAllowance_AndChargeTheRest()
    {
        if (!Available) return;
        Host.Options.NonWorkflow.MaxConcurrentTurnsPerOrg = 20;
        Guid org = await Host.AddOrgAsync(); // trial: 20 credits a month
        await Host.GrantAsync(org, 200_000);
        await UseAllowanceAsync(org, 10_000);

        TurnHold[] turns = await RaceAsync(20, i => Host.Charges.BeginTurnAsync(org, i % 2 == 0 ? UsageSource.Assistant : UsageSource.Mcp, Guid.NewGuid()));
        turns.Should().OnlyContain(t => t.Allowed);
        turns.Sum(t => t.AllowanceHeldMillis).Should().Be(10_000);

        TurnSettlement[] settled = await RaceAsync(20, i => Host.Charges.SettleTurnAsync(turns[i], 7_000));

        settled.Should().OnlyContain(s => s.Ok && s.UnchargedMillis == 0);
        settled.Sum(s => s.AllowanceMillis).Should().Be(10_000);
        settled.Sum(s => s.ChargedMillis).Should().Be(130_000);
        CreditLedgerResult balance = await Host.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((70_000L, 0L));
        (await Host.Charges.AllowanceUsedAsync(org)).Should().Be(20_000);
    }

    /// <summary>
    /// F-2.4: a burst of concurrent turns after the allowance, each running round-trips until it is cut off, never
    /// takes the balance below minus the headroom plus one round-trip per turn in flight.
    /// </summary>
    [PostgresFact]
    public async Task ABurstAfterTheAllowance_NeverTakesTheBalanceBelowTheFloorPlusOneRoundTripPerTurn()
    {
        if (!Available) return;
        const long roundTrip = 3_000;
        Guid org = await Host.AddOrgAsync();
        await Host.GrantAsync(org, 20_000);
        await UseAllowanceAsync(org, 20_000);

        TurnHold[] turns = await RaceAsync(6, i => Host.Charges.BeginTurnAsync(org, i % 2 == 0 ? UsageSource.Assistant : UsageSource.Mcp, Guid.NewGuid()));
        TurnHold[] admitted = turns.Where(t => t.Allowed).ToArray();
        admitted.Should().HaveCount(4, "20 credits are four 5-credit holds, and four is also the in-flight cap");

        long[] metered = await RaceAsync(admitted.Length, async i =>
        {
            long running = 0;
            for (int step = 0; step < 50 && (await Host.Charges.CheckTurnAsync(admitted[i], running)).Continue; step++)
                running += roundTrip;
            await Host.Charges.SettleTurnAsync(admitted[i], running);
            return running;
        });

        metered.Should().OnlyContain(m => m < 50 * roundTrip, "every turn was cut off");
        CreditLedgerResult balance = await Host.BalanceAsync(org);
        balance.ReservedMillis.Should().Be(0);
        balance.BalanceMillis.Should().BeGreaterThanOrEqualTo(-(1_000 + roundTrip * admitted.Length));
        balance.BalanceMillis.Should().Be(20_000 - metered.Sum(), "every metered credit was charged");
    }

    /// <summary>
    /// T-032 under a real race: four turns at the edge of the balance, each pre-claiming every 3-credit
    /// step before it runs and then metering 0.2 credits more than it claimed (the estimation error), cannot together
    /// take the balance below minus the headroom plus one estimation error per turn
    /// (<see cref="NonWorkflowChargeService.WorstCaseOvershootMillis"/>), where unclaimed round-trips overshot by a
    /// whole round-trip per turn.
    /// </summary>
    [PostgresFact]
    public async Task FourConcurrentTurnsAtTheEdge_PreClaimingEachStep_OvershootAtMostTheHeadroomPlusTheEstimationError()
    {
        if (!Available) return;
        const long step = 3_000;
        const long estimationError = 200;
        Guid org = await Host.AddOrgAsync();
        await Host.GrantAsync(org, 20_000);
        await UseAllowanceAsync(org, 20_000);

        TurnHold[] turns = await RaceAsync(4, i => Host.Charges.BeginTurnAsync(org, i % 2 == 0 ? UsageSource.Assistant : UsageSource.Mcp, Guid.NewGuid()));
        turns.Should().OnlyContain(t => t.Allowed);

        long[] metered = await RaceAsync(turns.Length, async i =>
        {
            long running = 0;
            for (int n = 0; n < 50 && (await Host.Charges.ClaimStepAsync(turns[i], new TurnStepClaim(running, 0, step))).Continue; n++)
            {
                running += step + estimationError;
                await Host.Charges.LandStepAsync(turns[i], running, 0);
            }

            await Host.Charges.SettleTurnAsync(turns[i], running);
            return running;
        });

        CreditLedgerResult balance = await Host.BalanceAsync(org);
        balance.ReservedMillis.Should().Be(0);
        balance.BalanceMillis.Should().Be(20_000 - metered.Sum(), "every metered credit was charged");
        long headroom = 1_000; // min(5-credit hold x 20 %, 100 credits)
        (-balance.BalanceMillis).Should().BeLessThanOrEqualTo(
            NonWorkflowChargeService.WorstCaseOvershootMillis(turns.Length, headroom, estimationError));
    }

    /// <summary>F-2.3 under a real race: of eight turns started at once with credits for all, exactly four run.</summary>
    [PostgresFact]
    public async Task EightConcurrentTurns_OnlyFourAreAdmitted_TheRestAreRefusedForConcurrency()
    {
        if (!Available) return;
        Guid org = await Host.AddOrgAsync();
        await Host.GrantAsync(org, 500_000);

        TurnHold[] turns = await RaceAsync(8, _ => Host.Charges.BeginTurnAsync(org, UsageSource.Mcp, Guid.NewGuid()));

        turns.Count(t => t.Allowed).Should().Be(4);
        turns.Where(t => !t.Allowed).Should().OnlyContain(t => t.Code == NonWorkflowChargeCodes.TurnConcurrencyLimit);
    }

    [PostgresFact]
    public async Task ConcurrentTurns_WithCredits_ReserveNoMoreThanAllowanceLeftPlusBalance()
    {
        if (!Available) return;
        Guid org = await Host.AddOrgAsync();
        await Host.GrantAsync(org, 12_000);
        await UseAllowanceAsync(org, 17_000); // 3 credits of allowance + 12 credits = 15 = three 5-credit holds

        TurnHold[] turns = await RaceAsync(8, _ => Host.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid()));

        turns.Count(t => t.Allowed).Should().Be(3);
        turns.Where(t => t.Allowed).Sum(t => t.AllowanceHeldMillis).Should().Be(3_000);
        (await Host.BalanceAsync(org)).ReservedMillis.Should().Be(12_000);
    }

    /// <summary>
    /// Starts <paramref name="count"/> calls at once: each waits on one gate after its pool connection is warm, so
    /// they really overlap instead of trickling in one after the other.
    /// </summary>
    private async Task<T[]> RaceAsync<T>(int count, Func<int, Task<T>> call)
    {
        // Warm the pool and the per-call reads that precede the locked section, concurrently, so the calls below
        // reach it together.
        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(() => Host.Allowance.GetTermsAsync(Guid.NewGuid()))));
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<T>[] calls = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await call(i);
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(calls);
    }

    /// <summary>
    /// T-110 (L1): the stale sweep and a late close of the same hold, racing, serialize on the org's ledger lock: the
    /// hold is charged what the call cost exactly once, whichever closes it first.
    /// </summary>
    [PostgresFact]
    public async Task StaleSweep_RacingLateCloses_ChargesTheHoldExactlyOnce()
    {
        if (!Available) return;
        Guid org = await Host.AddOrgAsync("pro");
        await Host.GrantAsync(org, 50_000);
        ChargeHold hold = await Host.Charges.BeginHoldAsync(org, UsageSource.Editor, 10_000, Guid.NewGuid(), new CreditLedgerContext(Note: "test"));
        Host.Time.Now += NonWorkflowChargeService.StaleHoldAge + TimeSpan.FromSeconds(1);

        await RaceAsync(8, i => i % 2 == 0
            ? Host.Charges.SweepStaleHoldsAsync(org).ContinueWith(_ => true, TaskScheduler.Default)
            : Host.Charges.SettleAsync(hold, 7_000));

        List<CreditLedgerEntry> entries = await Host.EntriesAsync(org);
        (-entries.Where(e => e.Kind == CreditLedgerEntryKind.Settle).Sum(e => e.BalanceDeltaMillis)).Should().Be(7_000);
        CreditLedgerResult balance = await Host.BalanceAsync(org);
        (balance.BalanceMillis, balance.ReservedMillis).Should().Be((43_000, 0));
    }

    [PostgresFact]
    public void RunsAgainstPostgres_WhenConfigured()
    {
        // Makes it visible in the test output whether the Postgres half actually ran.
        if (!Available) return;
        _connectionString.Should().Contain(_dbName);
    }
}
