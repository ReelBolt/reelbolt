using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models.Billing;

namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>
/// Cancels the provider transaction of superseded or expired <see cref="PromotionCheckout"/> reservations
/// (contract C10, F-5): a reservation that reserves nothing any more must not stay payable with its
/// discount or bonus. Each attempt is counted on the row; a cancelled transaction, or one the provider
/// will not cancel (already paid), is marked with <see cref="PromotionCheckout.ProviderCancelledAt"/> and
/// never tried again. A failure leaves the row owed, for <see cref="PromotionCheckoutSweeper"/> to retry
/// after an exponential backoff (N-17), up to <see cref="MaxAttempts"/>; the last failure logs one Error.
/// </summary>
internal static class PromotionCheckoutCancellation
{
    /// <summary>The most cancels ever attempted for one transaction; past it the row is given up, with one Error.</summary>
    public const int MaxAttempts = 20;

    /// <summary>The wait after the first failure; it doubles with every further failure.</summary>
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(5);

    /// <summary>The longest wait between two attempts.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    /// <summary>How long to wait after the <paramref name="attempts"/>-th failed attempt (1-based).</summary>
    public static TimeSpan BackoffAfter(int attempts)
    {
        int doublings = Math.Clamp(attempts - 1, 0, 16);
        TimeSpan wait = FirstBackoff * (1L << doublings);
        return wait < MaxBackoff ? wait : MaxBackoff;
    }

    /// <summary>Tries each still-owed checkout of <paramref name="checkoutIds"/> once; returns how many are now settled.</summary>
    public static async Task<int> CancelAsync(
        InferenceApiDbContext db,
        IEnumerable<IBillingProvider> providers,
        IBillingProvider active,
        IReadOnlyCollection<Guid> checkoutIds,
        TimeProvider time,
        ILogger logger,
        CancellationToken ct)
    {
        if (checkoutIds.Count == 0)
        {
            return 0;
        }

        List<Guid> ids = checkoutIds.Distinct().ToList();
        var owed = await db.PromotionCheckouts.AsNoTracking()
            .Where(c => ids.Contains(c.Id) && c.ProviderTransactionId != null && c.ProviderCancelledAt == null && c.CompletedAt == null
                && c.ProviderCancelAttempts < MaxAttempts)
            .Select(c => new { c.Id, c.ProviderTransactionId, c.ProviderCancelAttempts })
            .ToListAsync(ct);

        int settled = 0;
        List<IBillingProvider> providerList = providers.ToList();
        // Keep the caller's order (the sweeper's due order); a supersede passes its own list.
        foreach (var row in owed.OrderBy(r => ids.IndexOf(r.Id)))
        {
            Guid id = row.Id;
            // DEC-20: the provider that owns the transaction cancels it (a Stripe `cs_` reservation made before a switch back
            // to Paddle still goes to Stripe); an id nobody claims falls back to the active provider.
            IBillingProvider? owner = providerList.FirstOrDefault(p => p.OwnsTransaction(row.ProviderTransactionId!));
            if (owner is { IsConfigured: false })
            {
                // Never route the cancel to another provider: leave the row owed and retryable.
                logger.LogError(
                    "Provider {Provider} owns the transaction of promotion checkout {Checkout} but is not configured; the cancel stays owed until it is.",
                    owner.Name, row.Id);
                continue;
            }

            IBillingProvider provider = owner ?? active;
            int attempt = row.ProviderCancelAttempts + 1;
            await db.PromotionCheckouts.Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProviderCancelAttempts, c => c.ProviderCancelAttempts + 1), ct);
            try
            {
                await provider.CancelTransactionAsync(row.ProviderTransactionId!, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or ArgumentException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                DateTime retryAt = time.GetUtcNow().UtcDateTime + BackoffAfter(attempt);
                await db.PromotionCheckouts.Where(c => c.Id == id)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProviderCancelNextAttemptAt, (DateTime?)retryAt), CancellationToken.None);

                // Status code or error class only: a provider's error text can quote the buyer.
                string error = ex is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}" : ex.GetType().Name;
                if (attempt >= MaxAttempts)
                {
                    logger.LogError(
                        "Gave up cancelling provider transaction {Transaction} of superseded or expired promotion checkout {Checkout} "
                        + "after {Attempt} attempts ({Error}); it may still be payable with its promotion. Cancel it in the provider's dashboard",
                        row.ProviderTransactionId, id, attempt, error);
                }
                else
                {
                    logger.LogWarning(
                        "Could not cancel provider transaction {Transaction} of superseded or expired promotion checkout {Checkout} "
                        + "(attempt {Attempt}, {Error}); the promotion checkout sweeper retries it after {RetryAt:o}",
                        row.ProviderTransactionId, id, attempt, error, retryAt);
                }

                if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests })
                {
                    // Rate limited: the rest of the batch would only be refused too.
                    break;
                }

                continue;
            }

            DateTime now = time.GetUtcNow().UtcDateTime;
            await db.PromotionCheckouts.Where(c => c.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.ProviderCancelledAt, (DateTime?)now), ct);
            settled++;
        }

        return settled;
    }
}
