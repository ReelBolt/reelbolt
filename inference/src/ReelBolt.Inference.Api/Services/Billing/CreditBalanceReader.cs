using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>An organization's credit position in millicredits, with the plan it is on.</summary>
/// <param name="BalanceMillis">Spendable credits, including those held by open reservations.</param>
/// <param name="ReservedMillis">Credits held by open reservations.</param>
/// <param name="Plan">The plan key (<c>trial</c>, <c>creator</c>, ...), or null when no plan row exists.</param>
/// <param name="RenewsAt">When the current period ends and renews; null when none is scheduled or it is set to cancel.</param>
public sealed record CreditBalanceSnapshot(long BalanceMillis, long ReservedMillis, string? Plan, DateTime? RenewsAt)
{
    public long AvailableMillis => BalanceMillis - ReservedMillis;
}

/// <summary>
/// Reads the same balance <c>GET /api/v1/billing/summary</c> reports (the credit ledger's account row
/// plus the organization's current subscription), for callers that are not that controller.
/// </summary>
public interface ICreditBalanceReader
{
    /// <summary>The organization's credit position, or null when <c>Billing:Enabled</c> is false.</summary>
    Task<CreditBalanceSnapshot?> GetAsync(Guid organizationId, CancellationToken ct);
}

public sealed class CreditBalanceReader(InferenceApiDbContext db, IOptions<BillingOptions> options, TimeProvider? time = null) : ICreditBalanceReader
{
    public async Task<CreditBalanceSnapshot?> GetAsync(Guid organizationId, CancellationToken ct)
    {
        if (!options.Value.Enabled)
            return null;

        // A manual (provider-less) grant lapses at its period end, exactly as the plan gates read it.
        DateTime now = DateTime.UtcNow;
        Subscription? sub = await db.Subscriptions.AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && s.Status != SubscriptionStatus.Canceled)
            .Where(s => s.Provider != null || s.PeriodEnd == null || s.PeriodEnd > now)
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        // Same plan resolution as the summary: the subscription's plan, else the trial plan.
        string? planKey = sub is null
            ? await db.Plans.AsNoTracking().Where(p => p.IsActive && p.Kind == PlanKind.Trial)
                .OrderBy(p => p.SortOrder).Select(p => p.Key).FirstOrDefaultAsync(ct)
            : await db.Plans.AsNoTracking().Where(p => p.Id == sub.PlanId).Select(p => p.Key).FirstOrDefaultAsync(ct);

        CreditLedgerResult balance = await new CreditLedger(db, time).GetBalanceAsync(organizationId, ct);
        DateTime? renewsAt = sub is { CancelAtPeriodEnd: false } ? AnnualCreditSchedule.CreditsRenewAt(sub, now) : null;
        return new CreditBalanceSnapshot(balance.BalanceMillis, balance.ReservedMillis, planKey, renewsAt);
    }
}
