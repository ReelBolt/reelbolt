using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>
/// Daily retention for anonymous visitor data: deletes <c>product_events</c> rows that are visitor-only
/// (no user, no organization) and <c>visitor_attribution</c> rows (a visitor's first touch), both older
/// than <see cref="AlphaTelemetryOptions.VisitRetentionMonths"/>.
/// Account-linked data is handled by <see cref="SweepAccountDataAsync"/>.
/// </summary>
public sealed class ProductEventsRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<AlphaTelemetryOptions> options,
    ILogger<ProductEventsRetentionService> logger) : BackgroundService
{
    /// <summary>Delay before the first sweep after startup.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>Time between sweeps.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    int deleted = await SweepOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
                    if (deleted > 0) logger.LogInformation("Deleted {Count} expired visitor-only product events and visitor first-touch rows", deleted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The visitor data retention sweep failed; retrying tomorrow");
                }

                try
                {
                    int accountDeleted = await SweepAccountDataAsync(DateTimeOffset.UtcNow, stoppingToken);
                    if (accountDeleted > 0) logger.LogInformation("Deleted {Count} expired account-linked telemetry rows and feedback reports", accountDeleted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The account-linked telemetry retention sweep failed; retrying tomorrow");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>
    /// Runs one visitor sweep as of <paramref name="now"/>: visitor-only <c>product_events</c> rows and
    /// <c>visitor_attribution</c> rows older than the cutoff, in one transaction with one cutoff, so a
    /// visitor's events and their first touch expire by the same rule. Returns the total rows deleted.
    /// <para>
    /// A first touch is judged by its own <c>first_touch_at</c>, not by whether the visitor still has
    /// recent events: it is data collected at that moment, and the promise is how long that is kept. A
    /// visitor who is still around after it is deleted gets a new first-touch row on their next event.
    /// </para>
    /// </summary>
    public async Task<int> SweepOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        int months = Math.Max(1, options.Value.VisitRetentionMonths);
        DateTimeOffset cutoff = now.AddMonths(-months);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int events = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM product_events WHERE user_id IS NULL AND organization_id IS NULL "
            + "AND visitor_id IS NOT NULL AND occurred_at < {0}",
            [cutoff],
            ct);
        int firstTouches = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM visitor_attribution WHERE first_touch_at < {0}",
            [cutoff],
            ct);
        await tx.CommitAsync(ct);
        return events + firstTouches;
    }

    /// <summary>
    /// Deletes account-linked <c>product_events</c> older than <see cref="AlphaTelemetryOptions.RetentionMonths"/>
    /// (consent-choice rows, <c>source = 'consent'</c>, are kept as evidence of the person's choice) and
    /// <c>feedback_reports</c> older than the same cutoff. Returns the total rows deleted.
    /// </summary>
    public async Task<int> SweepAccountDataAsync(DateTimeOffset now, CancellationToken ct)
    {
        int months = Math.Max(1, options.Value.RetentionMonths);
        DateTimeOffset cutoff = now.AddMonths(-months);
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        // One transaction: the product_events rows (incl. the alpha.first_payment row behind
        // ux_product_events_first_payment_organization) and the alpha_funnel_claims rows use the same
        // cutoff and are written with the same timestamp, so they expire together.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        int events = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM product_events WHERE (user_id IS NOT NULL OR organization_id IS NOT NULL) "
            + "AND source <> 'consent' AND occurred_at < {0}",
            [cutoff],
            ct);
        // Consent-choice evidence is kept while the person still exists; once the account is gone (or the
        // row names no user) it is deleted after the cutoff like everything else.
        int consent = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM product_events WHERE source = 'consent' AND occurred_at < {0} "
            + "AND (user_id IS NULL OR NOT EXISTS (SELECT 1 FROM application_users u WHERE u.id = product_events.user_id))",
            [cutoff],
            ct);
        int claims = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM alpha_funnel_claims WHERE claimed_at < {0}",
            [cutoff],
            ct);
        int reports = await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM feedback_reports WHERE created_at < {0}",
            [cutoff],
            ct);
        await tx.CommitAsync(ct);
        return events + consent + claims + reports;
    }
}
