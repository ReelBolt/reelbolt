using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models.Marketing;

namespace ReelBolt.Inference.Api.Services.Marketing;

/// <summary>
/// Deletes Pending (never confirmed) consent rows, and their events, older than
/// <see cref="MarketingOptions.PendingRetentionDays"/>. An address nobody confirmed is not consent and
/// is not kept. Only Pending rows are ever deleted: a Withdrawn row's anonymous re-subscribe request
/// (<see cref="ReelBolt.Shared.Data.Models.Marketing.MarketingConsent.ResubscribeRequestedAt"/>) is
/// merely cleared once as old, so a withdrawal is never swept away. Also deletes
/// <c>mail_rate_counters</c> rows older than <see cref="CounterRetentionDays"/>. Runs daily; a failed
/// sweep is retried on the next one.
/// </summary>
public sealed class PendingConsentSweeper : BackgroundService
{
    private const int BatchSize = 500;

    /// <summary><c>mail_rate_counters</c> rows whose window started longer ago than this are deleted.</summary>
    public const int CounterRetentionDays = 2;

    private readonly IServiceScopeFactory _scopes;
    private readonly IOptions<MarketingOptions> _options;
    private readonly ILogger<PendingConsentSweeper> _logger;

    public PendingConsentSweeper(IServiceScopeFactory scopes, IOptions<MarketingOptions> options, ILogger<PendingConsentSweeper> logger)
    {
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int hours = _options.Value.PendingSweepIntervalHours;
        if (hours <= 0)
        {
            return;
        }

        try
        {
            // Let the API finish migrating before the first sweep.
            await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
            using PeriodicTimer timer = new(TimeSpan.FromHours(Math.Clamp(hours, 1, 24 * 7)));
            do
            {
                try
                {
                    int deleted = await SweepOnceAsync(DateTime.UtcNow, stoppingToken);
                    if (deleted > 0)
                    {
                        _logger.LogInformation("Deleted {Count} unconfirmed marketing consent request(s)", deleted);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Never the exception object: a database error can quote a row's address (C4).
                    _logger.LogError("The pending marketing consent sweep failed; it is retried on the next run: {Error}",
                        MailLogRef.DescribeError(ex));
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>One sweep as of <paramref name="nowUtc"/>; returns how many consent rows it deleted.</summary>
    public async Task<int> SweepOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        DateTime cutoff = nowUtc.AddDays(-Math.Max(1, _options.Value.PendingRetentionDays));
        using IServiceScope scope = _scopes.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        // C2: the mail rate counters only matter inside their window (a day at most); older rows,
        // the Go relay's included, go.
        DateTime counterCutoff = nowUtc.AddDays(-CounterRetentionDays);
        await db.MailRateCounters.Where(c => c.WindowStart < counterCutoff).ExecuteDeleteAsync(ct);

        // A Withdrawn row's stale re-subscribe request is dropped; the row and its withdrawal stay.
        await db.MarketingConsents
            .Where(c => c.ResubscribeRequestedAt != null && c.ResubscribeRequestedAt < cutoff)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ResubscribeRequestedAt, (DateTime?)null), ct);

        // Data minimisation, always on (independent of the Brevo, campaign and sync switches): withdrawn rows keep no name.
        await new MarketingNameReconciler(db).ClearWithdrawnAsync(ct);

        int total = 0;
        while (true)
        {
            List<Guid> ids = await db.MarketingConsents
                .Where(c => c.Status == MarketingConsentStatus.Pending
                    && (c.RequestedAt != null ? c.RequestedAt < cutoff : c.UpdatedAt < cutoff))
                .OrderBy(c => c.Id)
                .Select(c => c.Id)
                .Take(BatchSize)
                .ToListAsync(ct);
            if (ids.Count == 0)
            {
                return total;
            }

            // Re-check the status in the delete itself, so a row confirmed since the select stays
            // (with its events).
            total += await db.MarketingConsents
                .Where(c => ids.Contains(c.Id) && c.Status == MarketingConsentStatus.Pending)
                .ExecuteDeleteAsync(ct);
            // The FK cascade removes the events on PostgreSQL; this covers a provider that does not
            // enforce it, and only ever touches events whose row is gone.
            await db.MarketingConsentEvents
                .Where(e => ids.Contains(e.ConsentId) && !db.MarketingConsents.Any(c => c.Id == e.ConsentId))
                .ExecuteDeleteAsync(ct);
            if (ids.Count < BatchSize)
            {
                return total;
            }
        }
    }
}
