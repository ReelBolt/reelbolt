#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// F-3.2: a Platform usage that cannot be priced is never "costs 0" for an assistant/MCP turn. The recorder marks
/// the turn's usage scope, and the turn settles at max(metered, hold).
/// </summary>
public sealed class UnpricedTurnUsageTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public UnpricedTurnUsageTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private sealed class NullSink : IUsageEventSink
    {
        public Task WriteAsync(IReadOnlyList<UsageEvent> events, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Every quote fails with a non-pricing error (a broken price table, a timeout).</summary>
    private sealed class BrokenPrices : IModelPriceService
    {
        public ValueTask<PriceQuote> PriceAsync(PriceRequest request, CancellationToken ct = default) => throw new InvalidOperationException("price table unreadable");

        public ValueTask<PriceQuote> PriceQuotedUsdAsync(decimal costUsd, UsageKind unit, UsageBillingMode billingMode, DateTime? at = null, CancellationToken ct = default)
            => throw new InvalidOperationException("price table unreadable");

        public void Invalidate() { }
    }

    private static UsageRecordRequest Chat(UsageBillingMode mode) =>
        new(UsageKind.LlmOutputTokens, 1_000, Guid.NewGuid(), InferenceProviderKind.Anthropic, "claude-x", mode);

    private static async Task<UsageScopeState> RecordInScopeAsync(IModelPriceService prices, UsageBillingMode mode)
    {
        UsageRecorder recorder = new(prices, new NullSink(), NullLogger<UsageRecorder>.Instance);
        using (UsageScope.Begin(Guid.NewGuid(), null, null, null, Guid.NewGuid(), UsageSource.Assistant))
        {
            UsageScopeState scope = UsageScope.Current!;
            await recorder.RecordAsync(Chat(mode));
            return scope;
        }
    }

    [Fact]
    public async Task AnUnpricedPlatformUsage_MarksTheScope()
    {
        (await RecordInScopeAsync(new NonWorkflowChargingHost.FixedPrices(null), UsageBillingMode.Platform)).SawUnpricedPlatformUsage.Should().BeTrue();
        (await RecordInScopeAsync(new BrokenPrices(), UsageBillingMode.Platform)).SawUnpricedPlatformUsage.Should().BeTrue("a failed quote is unpriced too");
    }

    [Fact]
    public async Task APricedOrNonPlatformUsage_LeavesTheScopeUnmarked()
    {
        (await RecordInScopeAsync(new NonWorkflowChargingHost.FixedPrices(4), UsageBillingMode.Platform)).SawUnpricedPlatformUsage.Should().BeFalse();
        (await RecordInScopeAsync(new BrokenPrices(), UsageBillingMode.Byo)).SawUnpricedPlatformUsage.Should().BeFalse("BYO is never charged");
        (await RecordInScopeAsync(new BrokenPrices(), UsageBillingMode.Runner)).SawUnpricedPlatformUsage.Should().BeFalse("Runner is never charged");
    }

    [Fact]
    public async Task ATurnWhoseScopeSawUnpricedUsage_SettlesAtLeastAtItsHold()
    {
        var charges = new FakeNonWorkflowCharges();
        TurnHold turn = new(true, Guid.NewGuid(), 5_000, 0, null) { OrganizationId = Guid.NewGuid(), Source = UsageSource.Assistant };
        UsageScopeState scope = await RecordInScopeAsync(new NonWorkflowChargingHost.FixedPrices(null), UsageBillingMode.Platform);

        await new TurnCharge(charges, turn, null).SettleAsync(scope);

        charges.TurnSettlements.Should().ContainSingle().Which.MeteredMillis.Should().Be(5_000);
    }

    [Fact]
    public async Task ATurnWithOnlyPricedUsage_SettlesAtWhatItMetered()
    {
        var charges = new FakeNonWorkflowCharges();
        TurnHold turn = new(true, Guid.NewGuid(), 5_000, 0, null) { OrganizationId = Guid.NewGuid(), Source = UsageSource.Assistant };
        UsageScopeState scope = await RecordInScopeAsync(new NonWorkflowChargingHost.FixedPrices(2), UsageBillingMode.Platform);

        await new TurnCharge(charges, turn, null).SettleAsync(scope);

        charges.TurnSettlements.Should().ContainSingle().Which.MeteredMillis.Should().Be(2_000);
    }

    /// <summary>End to end on the real charge path: an unpriced Platform turn is not free; BYO and Runner turns charge 0.</summary>
    [Theory]
    [InlineData(UsageBillingMode.Platform, 5_000)]
    [InlineData(UsageBillingMode.Byo, 0)]
    [InlineData(UsageBillingMode.Runner, 0)]
    public async Task ATurnOnTheRealChargePath_IsChargedItsHoldWhenUnpriced_AndNothingForByoOrRunner(UsageBillingMode mode, long expectedCharge)
    {
        using NonWorkflowChargingHost host = new(o => o.UseSqlite(_connection), prices: new NonWorkflowChargingHost.FixedPrices(null));
        await using (InferenceApiDbContext db = host.NewDb()) await db.Database.EnsureCreatedAsync();
        await host.SeedPlansAsync();
        Guid org = await host.AddOrgAsync();
        await host.GrantAsync(org, 50_000);
        for (int i = 0; i < 4; i++)
        {
            TurnHold used = await host.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid());
            await host.Charges.SettleTurnAsync(used, 5_000); // the 20-credit trial allowance, used up
        }

        TurnHold turn = await host.Charges.BeginTurnAsync(org, UsageSource.Assistant, Guid.NewGuid(), mode);
        UsageScopeState scope = await RecordInScopeAsync(new NonWorkflowChargingHost.FixedPrices(null), mode);
        await using (TurnCharge charge = new(host.Charges, turn, null)) await charge.SettleAsync(scope);

        (await host.BalanceAsync(org)).BalanceMillis.Should().Be(50_000 - expectedCharge);
    }
}
