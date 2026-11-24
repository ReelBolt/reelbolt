using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The monthly assistant allowance (decision 9): what the plan includes, over which period, and which usage
/// counts against it. Read through the real plan-backed entitlements over the shipped catalog.
/// </summary>
public sealed class AssistantAllowanceServiceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private NonWorkflowChargingHost _host = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _host = Host();
        await using (InferenceApiDbContext db = _host.NewDb())
        {
            await db.Database.EnsureCreatedAsync();
        }

        await _host.SeedPlansAsync();
    }

    public Task DisposeAsync()
    {
        _host.Dispose();
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private NonWorkflowChargingHost Host(bool billingEnabled = true)
        => new(o => o.UseSqlite(_connection), billingEnabled);

    [Theory]
    [InlineData(null, 20_000L)]
    [InlineData("creator", 100_000L)]
    [InlineData("pro", 300_000L)]
    public async Task Included_IsThePlansMonthlyAssistantCredits(string? plan, long expectedMillis)
    {
        Guid org = await _host.AddOrgAsync(plan, periodStart: Now.AddDays(-3), periodEnd: Now.AddDays(27));

        AssistantAllowance allowance = await _host.Allowance.GetAsync(org);

        allowance.IncludedMillis.Should().Be(expectedMillis);
    }

    [Fact]
    public async Task Team_IsPerSeatTimesTheSubscriptionsSeats()
    {
        Guid org = await _host.AddOrgAsync("team", seats: 4, periodStart: Now.AddDays(-3), periodEnd: Now.AddDays(27));

        (await _host.Allowance.GetAsync(org)).IncludedMillis.Should().Be(400_000);
    }

    [Fact]
    public async Task Enterprise_IsUnlimited()
    {
        Guid org = await _host.AddOrgAsync("enterprise");

        (await _host.Allowance.GetAsync(org)).IncludedMillis.Should().BeNull();
    }

    /// <summary>T-032 (F-5.1): a plan whose entitlements omit both allowance keys includes nothing, never "unlimited".</summary>
    [Fact]
    public async Task MissingAllowanceKey_ReadsAsZeroIncluded()
    {
        await AddPlanAsync("legacy", """{"byoAllowed":true}""");
        Guid org = await _host.AddOrgAsync("legacy");

        (await _host.Allowance.GetAsync(org)).IncludedMillis.Should().Be(0);
        (await _host.Allowance.GetIncludedMillisAsync(org)).Should().Be(0);
    }

    /// <summary>T-032 (F-5.1): unlimited comes only from the explicit <c>assistantUnlimited</c> entitlement.</summary>
    [Fact]
    public async Task ExplicitUnlimitedEntitlement_IsUnlimited()
    {
        await AddPlanAsync("custom-unlimited", """{"assistantUnlimited":true}""");
        Guid org = await _host.AddOrgAsync("custom-unlimited");

        (await _host.Allowance.GetAsync(org)).IncludedMillis.Should().BeNull();
    }

    /// <summary>
    /// T-032 (F-5.1): no plan row at all (the catalog is not seeded, so not even the Trial row exists) falls back to the
    /// config default entitlements, which read as 0 included while billing is on.
    /// </summary>
    [Fact]
    public async Task NoPlanRow_TheDefaultEntitlementsFallback_ReadsAsZeroIncluded()
    {
        Guid org = await _host.AddOrgAsync();
        await using (InferenceApiDbContext db = _host.NewDb())
        {
            db.Plans.RemoveRange(db.Plans);
            await db.SaveChangesAsync();
        }

        (await _host.Allowance.GetAsync(org)).IncludedMillis.Should().Be(0);
    }

    private async Task AddPlanAsync(string key, string entitlementsJson)
    {
        await using InferenceApiDbContext db = _host.NewDb();
        db.Plans.Add(new Plan { Id = Guid.NewGuid(), Key = key, Name = key, Kind = PlanKind.Subscription, EntitlementsJson = entitlementsJson });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task BillingDisabled_IsUnlimited()
    {
        Guid org = await _host.AddOrgAsync("creator");
        using NonWorkflowChargingHost off = Host(billingEnabled: false);

        (await off.Allowance.GetAsync(org)).IncludedMillis.Should().BeNull();
    }

    [Fact]
    public async Task Period_IsTheActiveSubscriptionsCurrentPeriod()
    {
        DateTime start = new(2026, 10, 5, 8, 30, 0, DateTimeKind.Utc);
        DateTime end = new(2026, 11, 5, 8, 30, 0, DateTimeKind.Utc);
        Guid org = await _host.AddOrgAsync("pro", periodStart: start, periodEnd: end);

        AssistantAllowance allowance = await _host.Allowance.GetAsync(org);

        (allowance.PeriodStart, allowance.PeriodEnd).Should().Be((start, end));
    }

    [Fact]
    public async Task Period_WithoutASubscription_IsTheUtcCalendarMonth()
    {
        Guid org = await _host.AddOrgAsync();

        AssistantAllowance allowance = await _host.Allowance.GetAsync(org);

        allowance.PeriodStart.Should().Be(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        allowance.PeriodEnd.Should().Be(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Period_OfACanceledSubscription_FallsBackToTheCalendarMonth()
    {
        Guid org = await _host.AddOrgAsync("pro", periodStart: Now.AddDays(-3), periodEnd: Now.AddDays(27), status: SubscriptionStatus.Canceled);

        AssistantAllowance allowance = await _host.Allowance.GetAsync(org);

        allowance.PeriodStart.Should().Be(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        allowance.IncludedMillis.Should().Be(20_000, "a canceled subscription no longer entitles, so the trial plan applies");
    }

    /// <summary>
    /// QA-1: what is reported as used is the charge gate's counter, the organization's assistant/MCP hold rows of the
    /// period (open ones at what they hold, closed ones at what they consumed, released ones not at all), never
    /// <c>usage_events</c>, and never an editor, subject or proposal hold row (T-110).
    /// </summary>
    [Fact]
    public async Task Used_IsTheGatesHoldCounter_InThePeriod_OfThisOrganization()
    {
        Guid org = await _host.AddOrgAsync();
        Guid other = await _host.AddOrgAsync();
        DateTime inPeriod = new(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);

        await _host.AddUsageAsync(org, UsageSource.Assistant, 9_000, inPeriod);
        await using (InferenceApiDbContext db = _host.NewDb())
        {
            AssistantAllowanceHold Row(Guid o, string key, AssistantAllowanceHoldStatus status, long held, long consumed, DateTime at) => new()
            {
                Id = Guid.NewGuid(),
                OrganizationId = o,
                Source = UsageSource.Assistant,
                IdempotencyKey = key + Guid.NewGuid().ToString("N"),
                HeldMillis = held,
                ConsumedMillis = consumed,
                Status = status,
                CreatedAt = at
            };
            db.AssistantAllowanceHolds.AddRange(
                Row(org, "nwa-hold:", AssistantAllowanceHoldStatus.Settled, 5_000, 1_000, inPeriod),
                Row(org, "nwa-hold:", AssistantAllowanceHoldStatus.Swept, 5_000, 200, inPeriod),
                Row(org, "nwa-hold:", AssistantAllowanceHoldStatus.Open, 500, 0, inPeriod),
                Row(org, "nwa-hold:", AssistantAllowanceHoldStatus.Released, 5_000, 0, inPeriod),
                Row(org, "nwc-hold:", AssistantAllowanceHoldStatus.Open, 0, 7_000, inPeriod),
                Row(org, "nwa-hold:", AssistantAllowanceHoldStatus.Settled, 5_000, 7_000, new DateTime(2026, 9, 30, 23, 59, 59, DateTimeKind.Utc)),
                Row(org, "nwa-hold:", AssistantAllowanceHoldStatus.Settled, 5_000, 7_000, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)),
                Row(other, "nwa-hold:", AssistantAllowanceHoldStatus.Settled, 5_000, 8_000, inPeriod));
            await db.SaveChangesAsync();
        }

        (await _host.Allowance.GetAsync(org)).UsedMillis.Should().Be(1_700, "settled 1 + swept 0.2 + open 0.5; usage_events are not the counter");
    }
}
