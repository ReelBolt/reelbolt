using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.PublicApi;

/// <summary>
/// The public API's hourly sweeps: rate counters older than one hour, audit events past
/// <c>PublicApi:AuditRetentionDays</c>, and idempotency records past <c>PublicApi:IdempotencyHours</c>.
/// A sweep is best-effort: a failure is logged and the next hour retries.
/// </summary>
public sealed class PublicApiMaintenanceService(
    IServiceScopeFactory scopes,
    IOptions<PublicApiOptions> options,
    ILogger<PublicApiMaintenanceService> logger,
    TimeProvider? time = null) : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using IServiceScope scope = scopes.CreateScope();
                    InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
                    (int counters, int events, int idempotency) = await PurgeAsync(db, _time.GetUtcNow(), stoppingToken);
                    if (counters + events + idempotency > 0)
                        logger.LogInformation("Public API sweep removed {Counters} rate rows, {Events} audit rows and {Idempotency} idempotency rows",
                            counters, events, idempotency);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning("Public API sweep failed: {Error}", ex.GetType().Name);
                }

                await Task.Delay(SweepInterval, _time, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    /// <summary>Runs one sweep (also the test seam). Returns the rows removed from each table.</summary>
    internal async Task<(int Counters, int Events, int Idempotency)> PurgeAsync(
        InferenceApiDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        PublicApiOptions current = options.Value;
        DateTime counterCutoff = now.UtcDateTime.AddHours(-1);
        DateTime eventCutoff = now.UtcDateTime.AddDays(-Math.Max(1, current.AuditRetentionDays));
        DateTime idempotencyCutoff = now.UtcDateTime.AddHours(-Math.Max(1, current.IdempotencyHours));

        int counters = await db.ApiKeyRateCounters
            .Where(c => c.WindowStart < counterCutoff)
            .ExecuteDeleteAsync(ct);
        int events = await db.ApiKeyAuditEvents
            .Where(e => e.OccurredAt < eventCutoff)
            .ExecuteDeleteAsync(ct);
        int idempotency = await db.ApiIdempotencyRecords
            .Where(r => r.CreatedAt < idempotencyCutoff)
            .ExecuteDeleteAsync(ct);
        return (counters, events, idempotency);
    }
}
