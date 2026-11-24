using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Billing.Promotions;

/// <summary>
/// Cancels the provider transactions of promotion reservations that no longer reserve anything (contract
/// C10, F-5): expired ones (older than <see cref="PromotionOptions.CheckoutReservationMinutes"/>) and
/// superseded ones whose cancel at supersede time failed or had no transaction id yet. Unpaid only; a
/// failed row is retried after an exponential backoff until the provider cancelled it or refused for good
/// (already paid), at most <see cref="PromotionCheckoutCancellation.MaxAttempts"/> times. Runs
/// every <see cref="PromotionOptions.CheckoutSweepIntervalMinutes"/>; inert while no billing provider is
/// configured.
/// </summary>
public sealed class PromotionCheckoutSweeper(
    IServiceScopeFactory scopes,
    IOptions<PromotionOptions> options,
    ILogger<PromotionCheckoutSweeper> logger,
    TimeProvider? time = null) : BackgroundService
{
    private const int BatchSize = 200;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int minutes = options.Value.CheckoutSweepIntervalMinutes;
        if (minutes <= 0)
        {
            return;
        }

        try
        {
            // Let the API finish migrating before the first sweep.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using PeriodicTimer timer = new(TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60)));
            do
            {
                try
                {
                    int settled = await SweepOnceAsync(_time.GetUtcNow().UtcDateTime, stoppingToken);
                    if (settled > 0)
                    {
                        logger.LogInformation("Settled the provider transactions of {Count} stale promotion checkout(s)", settled);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError("The promotion checkout sweep failed; it is retried on the next run: {Error}", ex.GetType().Name);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>One sweep as of <paramref name="nowUtc"/>; returns how many checkouts it settled.</summary>
    public async Task<int> SweepOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        using IServiceScope scope = scopes.CreateScope();
        List<IBillingProvider> providers = scope.ServiceProvider.GetServices<IBillingProvider>().ToList();
        IBillingProvider? provider = BillingProviderSelector.Active(
            providers,
            scope.ServiceProvider.GetService<IOptions<BillingProviderOptions>>()?.Value);
        if (provider is null)
        {
            return 0;
        }

        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        DateTime expiredBefore = nowUtc - TimeSpan.FromMinutes(Math.Max(1, options.Value.CheckoutReservationMinutes));
        // N-17: only rows whose backoff has passed and that are not given up; never-tried rows first, then
        // by how long they have been due, so rows that keep failing cannot starve newer ones.
        List<Guid> owed = await db.PromotionCheckouts.AsNoTracking()
            .Where(c => c.CompletedAt == null && c.ProviderTransactionId != null && c.ProviderCancelledAt == null)
            .Where(c => c.SupersededAt != null || c.CreatedAt <= expiredBefore)
            .Where(c => c.ProviderCancelAttempts < PromotionCheckoutCancellation.MaxAttempts)
            .Where(c => c.ProviderCancelNextAttemptAt == null || c.ProviderCancelNextAttemptAt <= nowUtc)
            .OrderBy(c => c.ProviderCancelAttempts)
            .ThenBy(c => c.ProviderCancelNextAttemptAt ?? c.CreatedAt)
            .ThenBy(c => c.Id)
            .Select(c => c.Id)
            .Take(BatchSize)
            .ToListAsync(ct);
        return await PromotionCheckoutCancellation.CancelAsync(db, providers, provider, owed, _time, logger, ct);
    }
}
