using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// An organization's monthly assistant allowance (docs/design/model-catalog.md §7.2): how many millicredits of
/// assistant chat and MCP use the plan includes, how many were used, and over which half-open period.
/// </summary>
/// <param name="IncludedMillis">
/// Included millicredits; null = unlimited (only the explicit <c>assistantUnlimited</c> entitlement, or billing off).
/// </param>
/// <param name="UsedMillis">
/// What the charge gate counts as used in the period (<see cref="AssistantAllowanceService.AllowanceInUseAsync"/>):
/// open assistant/MCP holds plus settled consumption, so a refused workspace sees its allowance used up (QA-1).
/// </param>
/// <param name="PeriodStart">Inclusive start (UTC).</param>
/// <param name="PeriodEnd">Exclusive end (UTC).</param>
public sealed record AssistantAllowance(long? IncludedMillis, long UsedMillis, DateTime PeriodStart, DateTime PeriodEnd)
{
    /// <summary>True when there is a limit and the usage has reached it.</summary>
    public bool UsedUp => IncludedMillis is { } included && UsedMillis >= included;
}

/// <summary>What an organization's plan includes and over which half-open UTC period (no usage read).</summary>
/// <param name="IncludedMillis">
/// Included millicredits; null = unlimited (only the explicit <c>assistantUnlimited</c> entitlement, or billing off).
/// </param>
public sealed record AssistantAllowanceTerms(long? IncludedMillis, DateTime PeriodStart, DateTime PeriodEnd);

/// <summary>Reads <see cref="AssistantAllowance"/>.</summary>
public interface IAssistantAllowanceService
{
    /// <summary>The included allowance and its current period, without reading usage.</summary>
    Task<AssistantAllowanceTerms> GetTermsAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>The allowance of <paramref name="organizationId"/> right now.</summary>
    Task<AssistantAllowance> GetAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>Only the included part (no usage read): null = unlimited.</summary>
    Task<long?> GetIncludedMillisAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IAssistantAllowanceService"/> over the plan entitlements (<c>assistantMonthlyCredits</c>, or
/// <c>assistantMonthlyCreditsPerSeat</c> × the subscription's seats; unlimited only with <c>assistantUnlimited</c>,
/// a missing value is 0) and the <c>assistant_allowance_holds</c> counter. The period is the
/// entitling subscription's current period when it has one that contains now, else the UTC calendar month.
/// Reads on its own short-lived context, so it never shares a caller's change tracker.
/// </summary>
public sealed class AssistantAllowanceService(
    IServiceScopeFactory scopes,
    IOrganizationEntitlements entitlements,
    IOptions<BillingOptions> billing,
    TimeProvider? time = null) : IAssistantAllowanceService
{
    private const long MillisPerCredit = 1000;

    /// <summary>Statuses that still entitle (the same set the plan source uses).</summary>
    private static readonly SubscriptionStatus[] Entitling =
        [SubscriptionStatus.Trialing, SubscriptionStatus.Active, SubscriptionStatus.PastDue];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <inheritdoc />
    public async Task<AssistantAllowance> GetAsync(Guid organizationId, CancellationToken ct = default)
    {
        using IServiceScope scope = scopes.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        Subscription? subscription = await EntitlingSubscriptionAsync(db, organizationId, ct).ConfigureAwait(false);
        long? included = await IncludedAsync(organizationId, subscription, ct).ConfigureAwait(false);
        (DateTime start, DateTime end) = PeriodOf(subscription, _time.GetUtcNow().UtcDateTime);

        // QA-1: the counter and period the gate admits turns against, never the asynchronously flushed usage_events.
        long used = await AllowanceInUseAsync(db, organizationId, start, end, Guid.Empty, ct).ConfigureAwait(false);

        return new AssistantAllowance(included, used, start, end);
    }

    /// <summary>
    /// The authoritative allowance counter, the one definition the charge gate (<see cref="NonWorkflowChargeService"/>)
    /// and the billing summary both read: over the organization's <c>assistant_allowance_holds</c> turn rows created in
    /// [<paramref name="start"/>, <paramref name="end"/>) (but <paramref name="except"/>), open holds count what they
    /// hold, settled and swept ones what they consumed (a swept hold consumed nothing unless the sweep charged its
    /// recorded usage, T-026), released ones nothing. Editor, subject and proposal hold rows are never counted.
    /// </summary>
    internal static async Task<long> AllowanceInUseAsync(
        InferenceApiDbContext db, Guid organizationId, DateTime start, DateTime end, Guid except, CancellationToken ct)
        => await db.AssistantAllowanceHolds.AsNoTracking()
            .Where(h => h.OrganizationId == organizationId
                        && h.CreatedAt >= start
                        && h.CreatedAt < end
                        && h.Id != except
                        && h.Status != AssistantAllowanceHoldStatus.Released
                        && !h.IdempotencyKey.StartsWith(NonWorkflowChargeService.HoldRowPrefix))
            .SumAsync(h => (long?)(h.Status == AssistantAllowanceHoldStatus.Open ? h.HeldMillis : h.ConsumedMillis), ct)
            .ConfigureAwait(false) ?? 0;

    /// <inheritdoc />
    public async Task<AssistantAllowanceTerms> GetTermsAsync(Guid organizationId, CancellationToken ct = default)
    {
        using IServiceScope scope = scopes.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        Subscription? subscription = await EntitlingSubscriptionAsync(db, organizationId, ct).ConfigureAwait(false);
        long? included = await IncludedAsync(organizationId, subscription, ct).ConfigureAwait(false);
        (DateTime start, DateTime end) = PeriodOf(subscription, _time.GetUtcNow().UtcDateTime);
        return new AssistantAllowanceTerms(included, start, end);
    }

    /// <inheritdoc />
    public async Task<long?> GetIncludedMillisAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (!billing.Value.Enabled) return null;

        using IServiceScope scope = scopes.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        Subscription? subscription = await EntitlingSubscriptionAsync(db, organizationId, ct).ConfigureAwait(false);
        return await IncludedAsync(organizationId, subscription, ct).ConfigureAwait(false);
    }

    private async Task<long?> IncludedAsync(Guid organizationId, Subscription? subscription, CancellationToken ct)
    {
        if (!billing.Value.Enabled) return null;

        OrganizationEntitlementSet set = await entitlements.GetAsync(organizationId, ct).ConfigureAwait(false);
        if (set.AssistantUnlimited) return null;
        if (set.AssistantMonthlyCreditsPerSeat is { } perSeat)
        {
            int seats = Math.Max(1, subscription?.Seats ?? 1);
            return Math.Max(0, (long)perSeat) * seats * MillisPerCredit;
        }

        // Unlimited only from the explicit entitlement (checked above): a missing key, a missing plan
        // row or the config-default fallback includes nothing.
        return Math.Max(0, (long)(set.AssistantMonthlyCredits ?? 0)) * MillisPerCredit;
    }

    private static Task<Subscription?> EntitlingSubscriptionAsync(InferenceApiDbContext db, Guid organizationId, CancellationToken ct)
    {
        // A manual (provider-less) grant lapses at its period end, as in EfPlanSource; a provider-backed
        // row's status is authoritative.
        DateTime now = DateTime.UtcNow;
        return db.Subscriptions.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId && Entitling.Contains(s.Status))
            .Where(s => s.Provider != null || s.PeriodEnd == null || s.PeriodEnd > now)
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// The subscription's current period (inside an annual term, its current monthly anniversary window) when it
    /// is set and contains <paramref name="now"/>; otherwise (no
    /// subscription, the internal Trial, or a period whose renewal has not arrived yet) the UTC calendar month.
    /// </summary>
    internal static (DateTime Start, DateTime End) PeriodOf(Subscription? subscription, DateTime now)
    {
        if (subscription is { PeriodStart: { } start, PeriodEnd: { } end })
        {
            DateTime s = AsUtc(start);
            DateTime e = AsUtc(end);
            if (s < e && s <= now && now < e)
            {
                // Inside an annual term the allowance (like the credits) resets monthly, on the term's anniversaries.
                return SubscriptionInterval.Of(s, e) == BillingInterval.Annual ? AnnualCreditSchedule.MonthWindow(s, e, now) : (s, e);
            }
        }

        DateTime month = new(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        return (month, month.AddMonths(1));
    }

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
