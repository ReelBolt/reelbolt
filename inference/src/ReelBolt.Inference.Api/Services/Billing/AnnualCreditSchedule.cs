using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// The money-back promise, in one place: a full refund of a payment inside this many days is honoured by
/// clawing back what it granted. The marketing site states the same number (<c>MONEY_BACK_DAYS</c> in
/// <c>site/lib/launch-offer.ts</c>); change both together. <c>IBillingProvider.RefundTransactionAsync</c>'s caller
/// decides eligibility with it, and the annual-plan refund rule in <see cref="BillingEventProcessor"/> reads it.
/// </summary>
public static class BillingRefundPolicy
{
    public const int MoneyBackDays = 14;

    public static readonly TimeSpan MoneyBackWindow = TimeSpan.FromDays(MoneyBackDays);
}

/// <summary>
/// The monthly credit calendar inside an annual term. The term runs <c>PeriodStart..PeriodEnd</c> (about a
/// year, billed once); credits are granted per month, on each monthly anniversary of the term start:
/// month 0 at the start by the renewal webhook, months 1..11 by <see cref="AnnualCreditGrantSweeper"/>. Every
/// anniversary is computed from the term start (never from the previous anniversary), so month ends never drift.
/// </summary>
public static class AnnualCreditSchedule
{
    /// <summary>Months of an annual term that get a grant: 0 (the start) and 1..11.</summary>
    public const int MonthsPerTerm = 12;

    /// <summary>The instant month <paramref name="n"/> begins (term start plus n months), never past the term end.</summary>
    public static DateTime Anniversary(DateTime periodStart, DateTime periodEnd, int n)
    {
        DateTime at = periodStart.AddMonths(n);
        return at > periodEnd ? periodEnd : at;
    }

    /// <summary>The month (0..11) of the term that contains <paramref name="at"/>; before the start is 0, past month 11 is 11.</summary>
    public static int MonthIndex(DateTime periodStart, DateTime periodEnd, DateTime at)
    {
        int n = 0;
        while (n < MonthsPerTerm - 1 && Anniversary(periodStart, periodEnd, n + 1) <= at)
        {
            n++;
        }

        return n;
    }

    /// <summary>The month window of the term containing <paramref name="at"/>: from its anniversary to the next (the last one ends at the term end).</summary>
    public static (DateTime Start, DateTime End) MonthWindow(DateTime periodStart, DateTime periodEnd, DateTime at)
    {
        int n = MonthIndex(periodStart, periodEnd, at);
        DateTime end = n >= MonthsPerTerm - 1 ? periodEnd : Anniversary(periodStart, periodEnd, n + 1);
        return (Anniversary(periodStart, periodEnd, n), end);
    }

    /// <summary>The idempotency key of month <paramref name="n"/> (1..11); month 0 keeps <see cref="BillingEventProcessor.PlanRefillKey"/>.</summary>
    public static string MonthKey(string provider, string subscriptionId, DateTime periodStart, int n)
        => $"{BillingEventProcessor.PlanRefillKey(provider, subscriptionId, periodStart)}:m{n}";

    /// <summary>Every refill key of one annual term: month 0 and months 1..11.</summary>
    public static List<string> TermKeys(string provider, string subscriptionId, DateTime periodStart)
    {
        List<string> keys = [BillingEventProcessor.PlanRefillKey(provider, subscriptionId, periodStart)];
        for (int n = 1; n < MonthsPerTerm; n++)
        {
            keys.Add(MonthKey(provider, subscriptionId, periodStart, n));
        }

        return keys;
    }

    /// <summary>The term start encoded in a plain (month-0 or monthly) refill key, or null for any other key.</summary>
    public static DateTime? RefillKeyPeriodStart(string key, string provider, string subscriptionId)
    {
        string prefix = $"plan:{provider}:{subscriptionId}:";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        string stamp = key[prefix.Length..];
        return DateTime.TryParseExact(stamp, "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// When the credits of the subscription next renew: the next monthly anniversary inside an annual term,
    /// else the period end. The billing end (<c>periodEnd</c>) is a different thing and stays the yearly one.
    /// </summary>
    public static DateTime? CreditsRenewAt(Subscription? sub, DateTime now)
    {
        if (sub is not { PeriodStart: { } start, PeriodEnd: { } end })
        {
            return sub?.PeriodEnd;
        }

        if (SubscriptionInterval.Of(start, end) != BillingInterval.Annual || now < start || now >= end)
        {
            return end;
        }

        return MonthWindow(start, end, now).End;
    }
}

/// <summary>One paid payment recorded for a subscription since an annual term opened.</summary>
public sealed record TermPaymentInfo(string TransactionId, long AmountMinor, bool IsUpgrade, bool FullyRefunded, bool Unpaired = false, bool IsOpening = false);

/// <summary>
/// The single answer to "which payments back this annual term, and are they refunded?", shared by
/// <see cref="AnnualCreditGrantSweeper"/> (stop granting) and the refund clawback in
/// <see cref="BillingEventProcessor"/> (take the term's credits back), so they cannot disagree. The term's
/// payments are EVERY positive paid payment of the subscription from the term <c>PeriodStart</c> until now that has
/// no upgrade row of its own (an earlier payment, such as the last monthly one before a switch, is never part of
/// the term); the upgrade rules govern upgrade payments (they revert to the pre-upgrade amount). The stored
/// <see cref="Subscription.TermOpeningTransactionId"/> (set only on an explicit provider signal, including an upgrade payment billed from exactly the
/// term start, the interval switch) identifies the opening payment, which counts even when an upgrade row owns it. Fail closed: no payment, or several with no opening id, is ambiguous: grants are skipped
/// (logged for manual review), but a full refund of any candidate still claws the term's credits back.
/// </summary>
public sealed record AnnualTermPayments(IReadOnlyList<TermPaymentInfo> Paid, bool HasOpeningId = false)
{
    /// <summary>
    /// The term's candidate payments: the plain ones (no upgrade row of any kind) and the stored opening payment.
    /// An unpaired upgrade or seat payment is not a candidate, but its refund still fails closed (see <see cref="Refunded"/>).
    /// </summary>
    public IReadOnlyList<TermPaymentInfo> TermPayments => Paid.Where(p => p.IsOpening || (!p.IsUpgrade && !p.Unpaired)).ToList();

    /// <summary>No term payment is recorded (fail closed).</summary>
    public bool Missing => TermPayments.Count == 0;

    /// <summary>Several candidate payments and no stored opening id: which one is the term's cannot be told.</summary>
    public bool Ambiguous => !HasOpeningId && TermPayments.Count > 1;

    /// <summary>Any term payment refunded in full or charged back.</summary>
    public bool Refunded => TermPayments.Any(p => p.FullyRefunded) || Paid.Any(p => p.Unpaired && p.FullyRefunded);

    /// <summary>A refunded payment is a term payment the clawback acts on, ambiguous or not: ambiguity never skips a clawback.</summary>
    public bool IsTermPayment(string transactionId) => TermPayments.Any(p => p.TransactionId == transactionId);

    public static async Task<AnnualTermPayments> LoadAsync(InferenceApiDbContext db, string provider, string subscriptionId, DateTime termStart, DateTime now, string? termOpeningTransactionId, CancellationToken ct)
    {
        // The stored opening payment is always loaded by id, whatever its timestamp (late paid, clock skew); the time
        // window only selects the other candidates.
        var payments = await db.BillingPayments.AsNoTracking()
            .Where(p => p.Provider == provider && p.ProviderSubscriptionId == subscriptionId && p.AmountMinor > 0
                && ((p.OccurredAt >= termStart && p.OccurredAt <= now) || p.ProviderTransactionId == termOpeningTransactionId))
            .OrderBy(p => p.OccurredAt)
            .Select(p => new { p.ProviderTransactionId, p.AmountMinor })
            .ToListAsync(ct).ConfigureAwait(false);
        List<TermPaymentInfo> paid = [];
        foreach (var payment in payments)
        {
            bool upgrade = await db.BillingPendingUpgradeGrants.AsNoTracking()
                .AnyAsync(r => r.Provider == provider && r.TransactionId == payment.ProviderTransactionId && r.NewEntitlementMillis != null, ct).ConfigureAwait(false);
            bool unpaired = !upgrade && await db.BillingPendingUpgradeGrants.AsNoTracking()
                .AnyAsync(r => r.Provider == provider && r.TransactionId == payment.ProviderTransactionId, ct).ConfigureAwait(false);
            long refunded = await db.BillingRefunds.AsNoTracking()
                .Where(r => r.Provider == provider && r.ProviderTransactionId == payment.ProviderTransactionId)
                .SumAsync(r => r.AmountMinor, ct).ConfigureAwait(false);
            paid.Add(new TermPaymentInfo(payment.ProviderTransactionId, payment.AmountMinor, upgrade, refunded >= payment.AmountMinor, unpaired, payment.ProviderTransactionId == termOpeningTransactionId));
        }

        bool openingKnown = paid.Any(p => p.IsOpening);
        return new AnnualTermPayments(paid, openingKnown);
    }
}
