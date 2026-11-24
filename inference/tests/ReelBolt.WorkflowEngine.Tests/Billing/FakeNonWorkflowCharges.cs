using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// A recording <see cref="INonWorkflowChargeService"/> for controller tests: it allows or refuses as told and
/// records every call, so a test can assert what the controller held, settled and released.
/// </summary>
internal sealed class FakeNonWorkflowCharges : INonWorkflowChargeService
{
    public NonWorkflowChargeOptions Options { get; } = new() { Enabled = true };

    /// <summary>Refuse every hold with INSUFFICIENT_CREDITS.</summary>
    public bool RefuseHolds { get; init; }

    /// <summary>Refuse every turn with this code (null = allow).</summary>
    public string? RefuseTurns { get; init; }

    /// <summary>How many turn settlements fail (Ok false) before one succeeds.</summary>
    public int FailTurnSettlements { get; set; }

    public long AvailableMillis { get; init; } = 1_500;

    public long EstimateMillis { get; init; } = 4_000;

    /// <summary>When set, what <see cref="EstimateAsync"/> answers for each request (instead of <see cref="EstimateMillis"/>).</summary>
    public Func<PriceRequest, long>? EstimateBy { get; init; }

    public List<PriceRequest> Estimates { get; } = [];
    public List<(Guid Org, UsageSource Source, long EstimateMillis)> Holds { get; } = [];
    public List<(ChargeHold Hold, long ActualMillis)> Settled { get; } = [];
    public List<ChargeHold> Released { get; } = [];
    public List<(Guid? Org, UsageSource Source)> Turns { get; } = [];
    public List<(TurnHold Turn, long MeteredMillis)> TurnSettlements { get; } = [];
    public List<TurnHold> TurnReleases { get; } = [];
    public List<UsageBillingMode> TurnModes { get; } = [];

    /// <summary>Every <see cref="CheckTurnAsync"/> call's metered amount, in order.</summary>
    public List<long> TurnChecks { get; } = [];

    /// <summary>The turn is stopped once it has metered this much (null = never), with <see cref="StopCode"/>.</summary>
    public long? StopTurnsAt { get; set; }

    public string StopCode { get; set; } = NonWorkflowChargeCodes.TurnLimit;

    /// <summary>The turn is stopped from this (1-based) check on (null = never), with <see cref="StopCode"/>.</summary>
    public int? StopAtCheck { get; set; }

    public Task<long?> GetAvailableMillisAsync(Guid organizationId, CancellationToken ct = default) => Task.FromResult<long?>(AvailableMillis);

    public Task<long> EstimateOneUnitAsync(PriceRequest request, CancellationToken ct = default)
    {
        Estimates.Add(request);
        return Task.FromResult(EstimateBy?.Invoke(request with { Quantity = 1m }) ?? EstimateMillis);
    }

    public Task<long> EstimateAsync(PriceRequest request, CancellationToken ct = default)
    {
        Estimates.Add(request);
        return Task.FromResult(EstimateBy?.Invoke(request) ?? EstimateMillis);
    }

    public Task<ChargeHold> BeginHoldAsync(Guid organizationId, UsageSource source, long estimateMillis, Guid requestId, CreditLedgerContext context, CancellationToken ct = default)
    {
        Holds.Add((organizationId, source, estimateMillis));

        // Like the real service: nothing to hold is an allowed hold with no reservation.
        if (estimateMillis <= 0)
        {
            return Task.FromResult(new ChargeHold(true, null, 0, 0, NonWorkflowChargeCodes.NoPlatformCost) { OrganizationId = organizationId });
        }

        return Task.FromResult(RefuseHolds
            ? new ChargeHold(false, null, estimateMillis, AvailableMillis, NonWorkflowChargeCodes.InsufficientCredits) { OrganizationId = organizationId }
            : new ChargeHold(true, Guid.NewGuid(), estimateMillis, AvailableMillis, null) { OrganizationId = organizationId });
    }

    public Task<bool> SettleAsync(ChargeHold hold, long actualMillis, CancellationToken ct = default)
    {
        Settled.Add((hold, actualMillis));
        return Task.FromResult(true);
    }

    /// <summary>Every <see cref="SettleUncappedAsync"/> call (also listed in <see cref="Settled"/>).</summary>
    public List<(ChargeHold Hold, long ActualMillis)> UncappedSettles { get; } = [];

    public Task<bool> SettleUncappedAsync(ChargeHold hold, long actualMillis, CancellationToken ct = default)
    {
        Settled.Add((hold, actualMillis));
        UncappedSettles.Add((hold, actualMillis));
        return Task.FromResult(true);
    }

    /// <summary>Every <see cref="ChargeUnheldAsync"/> call.</summary>
    public List<(Guid Org, UsageSource Source, long ActualMillis, Guid RequestId)> UnheldCharges { get; } = [];

    public Task<bool> ChargeUnheldAsync(Guid organizationId, UsageSource source, long actualMillis, Guid requestId, CreditLedgerContext context, CancellationToken ct = default)
    {
        UnheldCharges.Add((organizationId, source, actualMillis, requestId));
        return Task.FromResult(true);
    }

    public Task<bool> ReleaseAsync(ChargeHold hold, CancellationToken ct = default)
    {
        Released.Add(hold);
        return Task.FromResult(true);
    }

    public Task<TurnHold> BeginTurnAsync(
        Guid? organizationId, UsageSource source, Guid requestId, UsageBillingMode billingMode = UsageBillingMode.Platform, CancellationToken ct = default)
    {
        Turns.Add((organizationId, source));
        TurnModes.Add(billingMode);
        return Task.FromResult(RefuseTurns is { } code
            ? new TurnHold(false, null, 5_000, 0, code) { OrganizationId = organizationId, Source = source, RequestId = requestId }
            : new TurnHold(true, Guid.NewGuid(), 5_000, AvailableMillis, null) { OrganizationId = organizationId, Source = source, RequestId = requestId });
    }

    public Task<TurnSettlement> SettleTurnAsync(TurnHold turn, long meteredMillis, CancellationToken ct = default)
    {
        TurnSettlements.Add((turn, meteredMillis));
        if (FailTurnSettlements > 0)
        {
            FailTurnSettlements--;
            return Task.FromResult(new TurnSettlement(false, 0, 0, 0, NonWorkflowChargeCodes.GateUnavailable));
        }

        return Task.FromResult(new TurnSettlement(true, 0, meteredMillis, 0, null));
    }

    public Task<TurnCheck> CheckTurnAsync(TurnHold turn, long meteredMillis, CancellationToken ct = default)
    {
        TurnChecks.Add(meteredMillis);
        if (StopAtCheck is { } n && TurnChecks.Count >= n) return Task.FromResult(new TurnCheck(false, StopCode, meteredMillis));
        return Task.FromResult(StopTurnsAt is { } stop && meteredMillis >= stop
            ? new TurnCheck(false, StopCode, stop)
            : new TurnCheck(true, null, StopTurnsAt ?? long.MaxValue));
    }

    /// <summary>Every <see cref="ClaimStepAsync"/> call, in order (its metered amount is in <see cref="TurnChecks"/> too).</summary>
    public List<TurnStepClaim> Claims { get; } = [];

    /// <summary>Every <see cref="LandStepAsync"/> call: (metered, still claimed).</summary>
    public List<(long MeteredMillis, long ClaimedMillis)> Landings { get; } = [];

    /// <summary>Refuse a step that asks to be admitted under the concurrency cap, with TURN_CONCURRENCY_LIMIT.</summary>
    public bool RefuseCapAdmission { get; set; }

    /// <summary>Runs on every <see cref="ClaimStepAsync"/> call, after it is recorded (a test's "meanwhile").</summary>
    public Action<TurnStepClaim>? OnClaim { get; set; }

    public Task<TurnCheck> ClaimStepAsync(TurnHold turn, TurnStepClaim step, CancellationToken ct = default)
    {
        Claims.Add(step);
        OnClaim?.Invoke(step);
        if (step.AdmitUnderConcurrencyCap && RefuseCapAdmission)
        {
            TurnChecks.Add(step.MeteredMillis);
            return Task.FromResult(new TurnCheck(false, NonWorkflowChargeCodes.TurnConcurrencyLimit, 0));
        }

        return CheckTurnAsync(turn, step.MeteredMillis, ct);
    }

    public Task LandStepAsync(TurnHold turn, long meteredMillis, long claimedMillis, CancellationToken ct = default)
    {
        Landings.Add((meteredMillis, claimedMillis));
        return Task.CompletedTask;
    }

    /// <summary>Every <see cref="RecordUnsettledTurnAsync"/> call: (turn, metered).</summary>
    public List<(TurnHold Turn, long MeteredMillis)> UnsettledRecords { get; } = [];

    public Task RecordUnsettledTurnAsync(TurnHold turn, long meteredMillis, CancellationToken ct = default)
    {
        UnsettledRecords.Add((turn, meteredMillis));
        return Task.CompletedTask;
    }

    public Task<bool> ReleaseTurnAsync(TurnHold turn, CancellationToken ct = default)
    {
        TurnReleases.Add(turn);
        return Task.FromResult(true);
    }

    /// <summary>Every <see cref="RecordUnsettledHoldAsync"/> call: (hold, metered).</summary>
    public List<(ChargeHold Hold, long MeteredMillis)> UnsettledHoldRecords { get; } = [];

    public Task RecordUnsettledHoldAsync(ChargeHold hold, long meteredMillis, CancellationToken ct = default)
    {
        UnsettledHoldRecords.Add((hold, meteredMillis));
        return Task.CompletedTask;
    }
}
