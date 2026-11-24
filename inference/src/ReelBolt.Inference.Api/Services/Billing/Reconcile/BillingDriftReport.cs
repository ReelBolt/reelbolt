using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing.Reconcile;

/// <summary>
/// B12: the named classes a residual Paddle-vs-ledger difference falls into. The names are stable,
/// lower-case words an operator greps for in the log, the alert detail and the gauge
/// (<see cref="BillingDriftReport.Name"/>).
/// </summary>
public enum BillingDriftClass
{
    /// <summary>Paddle holds a subscription the platform has no row for (a lost <c>subscription.created</c>).</summary>
    SubscriptionMissing,

    /// <summary>The platform holds a provider subscription Paddle no longer has (a 404 on the row's own id).</summary>
    LedgerOnly,

    /// <summary>Paddle holds a completed upgrade payment the ledger did not record (the processor did not apply it).</summary>
    TransactionUnapplied,

    /// <summary>The provider's live plan differs from the row's.</summary>
    Plan,

    /// <summary>The provider's live seat count differs from the row's.</summary>
    Seats,

    /// <summary>The row holds a downgrade, but the provider's live items are the row's own plan and seats again.</summary>
    ScheduledChange,

    /// <summary>The provider's live billing period differs from the row's.</summary>
    Period,

    /// <summary>The provider's live status differs from the row's.</summary>
    Status,

    /// <summary>The provider's live scheduled cancellation differs from the row's.</summary>
    CancelAtPeriodEnd,

    /// <summary>Paddle could not be read, so the comparison could not be made at all.</summary>
    Unreadable
}

/// <summary>One named drift finding: what differs, and for which organization. <see cref="Detail"/> is secret-free.</summary>
public sealed record BillingDriftFinding(BillingDriftClass Class, Guid OrganizationId, string? Detail);

/// <summary>
/// B12's periodic reconciliation report: the residual drift one sweep's Paddle-vs-ledger comparison left
/// behind, plus the ledger's unattributed <c>usage_events</c>.
/// <para>
/// A finding is raised for a difference the reconcile did not resolve — <see cref="ReconcileOutcome.Stale"/>
/// (drift the webhook processor would not apply) or <see cref="ReconcileOutcome.ProviderUnavailable"/> (the
/// comparison could not be made, which must not read as "in sync") — and for a provider transaction the
/// processor did not apply. A healed, in-sync, young (<see cref="ReconcileOutcome.TooRecent"/>) or busy run
/// is not a finding: those are the reconciler working as designed.
/// </para>
/// <see cref="UnattributedUsageEvents"/> counts <c>usage_events</c> rows of the last 24 hours with no
/// attributable organization or user (<see cref="UsageSource.Unattributed"/>): B3b's invariant is that a
/// metered call always has a scope, so a row without one is a metering bug to chase, not to hide.
/// </summary>
public sealed record BillingDriftReport(int Candidates, IReadOnlyList<BillingDriftFinding> Findings, long UnattributedUsageEvents)
{
    /// <summary>Stable name of a provider transaction the ledger did not record.</summary>
    public const string TransactionUnapplied = "transaction_unapplied";

    /// <summary>Stable name of a comparison that could not be made.</summary>
    public const string Unreadable = "unreadable";

    public static BillingDriftReport Empty { get; } = new(0, [], 0);

    /// <summary>True when the sweep left at least one named finding.</summary>
    public bool HasDrift => Findings.Count > 0;

    /// <summary>The findings per class, for the log line, the alert detail and the gauge.</summary>
    public IReadOnlyDictionary<BillingDriftClass, int> ByClass =>
        Findings.GroupBy(f => f.Class).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>A stable, operator-readable summary: <c>ledger_only=2, subscription_missing=1</c>.</summary>
    public string DescribeClasses() =>
        string.Join(", ", ByClass.OrderBy(kv => Name(kv.Key), StringComparer.Ordinal).Select(kv => $"{Name(kv.Key)}={kv.Value}"));

    /// <summary>The stable lower-case name of a class — the word in the log, the alert and the runbook.</summary>
    public static string Name(BillingDriftClass @class) => @class switch
    {
        BillingDriftClass.SubscriptionMissing => SubscriptionDrift.SubscriptionMissing,
        BillingDriftClass.LedgerOnly => SubscriptionDrift.LedgerOnlyField,
        BillingDriftClass.TransactionUnapplied => TransactionUnapplied,
        BillingDriftClass.Plan => SubscriptionDrift.Plan,
        BillingDriftClass.Seats => SubscriptionDrift.Seats,
        BillingDriftClass.ScheduledChange => SubscriptionDrift.ScheduledChange,
        BillingDriftClass.Period => SubscriptionDrift.Period,
        BillingDriftClass.Status => SubscriptionDrift.Status,
        BillingDriftClass.CancelAtPeriodEnd => SubscriptionDrift.CancelAtPeriodEnd,
        _ => Unreadable
    };

    /// <summary>Turns one reconcile result into its named findings (none when the run left no difference).</summary>
    public static IReadOnlyList<BillingDriftFinding> Classify(Guid organizationId, ReconcileResult result)
    {
        List<BillingDriftFinding> findings = [];

        // A run that healed, found the row in sync, was too young to judge or could not take the claim left
        // no difference to report. Only a provider payment the processor did not apply is still a finding.
        if (result.Outcome is ReconcileOutcome.Healed or ReconcileOutcome.InSync
            or ReconcileOutcome.TooRecent or ReconcileOutcome.NothingToReconcile or ReconcileOutcome.Busy)
        {
            if (result.PaymentsUnapplied > 0) findings.Add(TransactionFinding(organizationId, result.PaymentsUnapplied));
            return findings;
        }

        foreach (string field in result.Drift)
        {
            if (FromField(field) is { } @class) findings.Add(new BillingDriftFinding(@class, organizationId, result.Error));
        }

        // A comparison that could not be made is not "in sync": name it rather than go silent.
        if (findings.Count == 0 && result.Outcome == ReconcileOutcome.ProviderUnavailable)
        {
            findings.Add(new BillingDriftFinding(BillingDriftClass.Unreadable, organizationId, result.Error));
        }

        if (result.PaymentsUnapplied > 0) findings.Add(TransactionFinding(organizationId, result.PaymentsUnapplied));
        return findings;
    }

    private static BillingDriftFinding TransactionFinding(Guid organizationId, int unapplied) =>
        new(BillingDriftClass.TransactionUnapplied, organizationId,
            $"{unapplied} completed upgrade payment(s) at the provider were not applied to the ledger");

    private static BillingDriftClass? FromField(string field) => field switch
    {
        SubscriptionDrift.SubscriptionMissing => BillingDriftClass.SubscriptionMissing,
        SubscriptionDrift.LedgerOnlyField => BillingDriftClass.LedgerOnly,
        SubscriptionDrift.Plan => BillingDriftClass.Plan,
        SubscriptionDrift.Seats => BillingDriftClass.Seats,
        SubscriptionDrift.ScheduledChange => BillingDriftClass.ScheduledChange,
        SubscriptionDrift.Period => BillingDriftClass.Period,
        SubscriptionDrift.Status => BillingDriftClass.Status,
        SubscriptionDrift.CancelAtPeriodEnd => BillingDriftClass.CancelAtPeriodEnd,
        _ => null
    };
}

/// <summary>One source's share of a window's <c>usage_events</c>.</summary>
public sealed record UsageAttributionBucket(string Source, long Events);

/// <summary>
/// B12's unattributed-usage report. A row with no attributable organization or user is a metering bug
/// (B3b's invariant): it lands on the platform org with source <see cref="UsageSource.Unattributed"/>.
/// <see cref="UnattributedEvents"/> is that count; the other figures are the surrounding context an operator
/// uses to find the source to fix.
/// </summary>
public sealed record UsageAttributionReport(
    long TotalEvents,
    long UnattributedEvents,
    long NoOrganizationEvents,
    long NoUserEvents,
    IReadOnlyList<UsageAttributionBucket> BySource);

/// <summary>Read-only queries over the ledger's attribution. Platform-wide by definition (<c>IgnoreQueryFilters</c>).</summary>
public static class BillingUsageAttribution
{
    /// <summary>Counts and slices a window's <c>usage_events</c> by attribution and source.</summary>
    public static async Task<UsageAttributionReport> QueryAsync(InferenceApiDbContext db, DateTime from, DateTime to, CancellationToken ct)
    {
        IQueryable<UsageEvent> window = db.UsageEvents.AsNoTracking().IgnoreQueryFilters()
            .Where(e => e.OccurredAt >= from && e.OccurredAt < to);

        long total = await window.LongCountAsync(ct).ConfigureAwait(false);
        long unattributed = await window.LongCountAsync(e => e.Source == UsageSource.Unattributed, ct).ConfigureAwait(false);
        long noOrganization = await window.LongCountAsync(e => e.OrganizationId == WellKnownOrganizations.PlatformId, ct).ConfigureAwait(false);
        long noUser = await window.LongCountAsync(e => e.UserId == null, ct).ConfigureAwait(false);

        // Grouped in the database; ranked and named here (the enum key is not orderable in either provider).
        List<UsageAttributionBucket> bySource = (await window
                .GroupBy(e => e.Source)
                .Select(g => new { Source = g.Key, Events = g.Count() })
                .ToListAsync(ct).ConfigureAwait(false))
            .Select(x => new UsageAttributionBucket(x.Source.ToString(), x.Events))
            .OrderByDescending(b => b.Events)
            .ThenBy(b => b.Source, StringComparer.Ordinal)
            .ToList();

        return new UsageAttributionReport(total, unattributed, noOrganization, noUser, bySource);
    }
}
