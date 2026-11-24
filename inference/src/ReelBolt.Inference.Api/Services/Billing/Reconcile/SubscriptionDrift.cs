using PlanEntity = ReelBolt.Shared.Data.Models.Billing.Plan;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing.Reconcile;

/// <summary>
/// The one rule for "does the provider's live subscription still disagree with our row?" — the change
/// endpoints' <c>409 change_pending</c> check (<see cref="IsSettled"/>) and the reconciler's drift list
/// (<see cref="Fields"/>) both come from <see cref="Compute(Subscription, PlanEntity?, int, DateTime?, DateTime?, SubscriptionStatus?, bool?)"/>.
/// <list type="bullet">
/// <item><c>period</c>: a live period bound differs from the row's (a bound the provider does not report is
/// not evidence of drift) — a renewal or reactivation whose webhook has not landed (Security R-3).</item>
/// <item><c>plan</c> / <c>seats</c>: the live items are neither the row's plan and seats nor, with a downgrade
/// held, the held plan and seats the provider already bills from the next period (Security F-2).</item>
/// <item><c>scheduled_change</c>: the row holds a downgrade (<c>ScheduledPlanId</c>/<c>ScheduledSeats</c>) but the live
/// items are the row's own plan and seats again — the member's "keep my plan" reached the provider and its webhook
/// was lost (QA-1). The processor releases the hold when that state is fed to it. It never makes a row unsettled for
/// the change endpoints (their <c>409 downgrade_scheduled</c> rule reads the row's hold itself).</item>
/// <item><c>status</c> / <c>cancel_at_period_end</c>: only when the caller supplies them (the reconciler);
/// they never make a row unsettled for the change endpoints.</item>
/// </list>
/// </summary>
/// <param name="Fields">The drifting fields, in the order <c>plan, seats, scheduled_change, period, status, cancel_at_period_end</c> (or <c>subscription_missing</c>).</param>
public sealed record SubscriptionDrift(IReadOnlyList<string> Fields)
{
    public const string Plan = "plan";
    public const string Seats = "seats";
    public const string Period = "period";
    public const string Status = "status";
    public const string CancelAtPeriodEnd = "cancel_at_period_end";

    /// <summary>The row holds a downgrade, but the provider's live items are the row's own plan and seats again.</summary>
    public const string ScheduledChange = "scheduled_change";

    /// <summary>The provider holds a live subscription the platform has no row for (a lost <c>subscription.created</c>).</summary>
    public const string SubscriptionMissing = "subscription_missing";

    /// <summary>The platform holds a provider subscription the provider no longer has (a 404 on the row's own id).</summary>
    public const string LedgerOnlyField = "ledger_only";

    public static SubscriptionDrift None { get; } = new(Array.Empty<string>());

    public static SubscriptionDrift Missing { get; } = new([SubscriptionMissing]);

    /// <summary>The reverse of <see cref="Missing"/>: the ledger has a provider subscription the provider does not.</summary>
    public static SubscriptionDrift LedgerOnly { get; } = new([LedgerOnlyField]);

    /// <summary>Nothing differs.</summary>
    public bool InSync => Fields.Count == 0;

    /// <summary>
    /// No plan, seat or period change is in flight: the change endpoints may act. Status, cancellation
    /// and <see cref="ScheduledChange"/> differences do not count here.
    /// </summary>
    public bool IsSettled => !Fields.Any(f => f is Plan or Seats or Period or SubscriptionMissing);

    /// <summary>
    /// Compares the provider's live view with the row. <paramref name="livePlan"/>/<paramref name="liveSeats"/>
    /// are the live items resolved with <see cref="BillingEventProcessor.ResolveItems"/>.
    /// <paramref name="liveStatus"/> and <paramref name="liveCancelAtPeriodEnd"/> are compared only when given.
    /// </summary>
    public static SubscriptionDrift Compute(
        Subscription row,
        PlanEntity? livePlan,
        int liveSeats,
        DateTime? livePeriodStart,
        DateTime? livePeriodEnd,
        SubscriptionStatus? liveStatus = null,
        bool? liveCancelAtPeriodEnd = null)
    {
        List<string> fields = [];
        bool onRow = MatchesRowPlan(livePlan, liveSeats, row);
        bool onHeld = !onRow
            && row.ScheduledPlanId is { } held
            && livePlan is not null
            && livePlan.Id == held
            && liveSeats == (row.ScheduledSeats ?? liveSeats);
        if (!onRow && !onHeld)
        {
            if (livePlan is null || livePlan.Id != row.PlanId)
            {
                fields.Add(Plan);
            }

            if (liveSeats != row.Seats)
            {
                fields.Add(Seats);
            }

            if (fields.Count == 0)
            {
                // Unreachable (not on the row means plan or seats differ), kept so the rule can never read as settled by accident.
                fields.Add(Plan);
            }
        }
        else if (onRow && (row.ScheduledPlanId is not null || row.ScheduledSeats is not null))
        {
            fields.Add(ScheduledChange);
        }

        if (!MatchesRowPeriod(livePeriodStart, livePeriodEnd, row))
        {
            fields.Add(Period);
        }

        if (liveStatus is { } status && status != row.Status)
        {
            fields.Add(Status);
        }

        if (liveCancelAtPeriodEnd is { } cancel && cancel != row.CancelAtPeriodEnd)
        {
            fields.Add(CancelAtPeriodEnd);
        }

        return fields.Count == 0 ? None : new SubscriptionDrift(fields);
    }

    /// <summary>The reconciler's full comparison: a provider snapshot event (plan, seats, period, status, cancellation).</summary>
    public static SubscriptionDrift Compute(Subscription row, BillingEvent live, IReadOnlyList<PlanEntity> plans)
    {
        (PlanEntity? plan, int seats) = BillingEventProcessor.ResolveItems(live.Items, plans, live.Provider);
        return Compute(row, plan, seats, live.PeriodStart, live.PeriodEnd, live.Status, live.CancelAtPeriodEnd);
    }

    /// <summary>The change endpoints' comparison: plan, seats and period only (see <see cref="IsSettled"/>).</summary>
    public static SubscriptionDrift Compute(Subscription row, ProviderSubscriptionState live, IReadOnlyList<PlanEntity> plans, string provider)
    {
        (PlanEntity? plan, int seats) = BillingEventProcessor.ResolveItems(live.Items, plans, provider);
        return Compute(row, plan, seats, live.PeriodStart, live.PeriodEnd);
    }

    /// <summary>The live items are exactly the row's plan and seat count.</summary>
    public static bool MatchesRowPlan(PlanEntity? livePlan, int liveSeats, Subscription row)
        => livePlan is not null && livePlan.Id == row.PlanId && liveSeats == row.Seats;

    /// <summary>The live billing period is the row's; a bound the provider does not report is not evidence of drift.</summary>
    public static bool MatchesRowPeriod(DateTime? livePeriodStart, DateTime? livePeriodEnd, Subscription row)
        => (livePeriodStart is not { } start || start == row.PeriodStart)
            && (livePeriodEnd is not { } end || end == row.PeriodEnd);
}
