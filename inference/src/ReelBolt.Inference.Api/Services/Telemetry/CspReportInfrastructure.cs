using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Telemetry;

/// <summary>Counters for the CSP report endpoint: the only per-report signal that is ever logged or exported.</summary>
public static class CspReportDiagnostics
{
    /// <summary>Meter name; the Inference API's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.CspReports";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>Reports by outcome: stored, disabled, rate_limited, malformed, rejected_type, error.</summary>
    public static readonly Counter<long> Reports =
        Meter.CreateCounter<long>("reelbolt.csp_reports", "reports", "CSP/COEP violation reports by outcome");
}

/// <summary>A per-process token bucket: the app-level safety net behind the nginx zone.</summary>
public sealed class CspReportRateLimiter(IOptions<AlphaTelemetryOptions> options, TimeProvider time)
{
    private readonly object _gate = new();
    private double _tokens = -1;
    private long _last;

    /// <summary>Takes one token; false means drop the report.</summary>
    public bool TryAcquire()
    {
        double rate = Math.Max(0.01, options.Value.CspReportRatePerSecond);
        double burst = Math.Max(1, options.Value.CspReportBurst);
        lock (_gate)
        {
            long now = time.GetTimestamp();
            if (_tokens < 0)
            {
                _tokens = burst;
            }
            else
            {
                _tokens = Math.Min(burst, _tokens + time.GetElapsedTime(_last, now).TotalSeconds * rate);
            }

            _last = now;
            if (_tokens < 1) return false;
            _tokens -= 1;
            return true;
        }
    }
}

/// <summary>Writes one report into the daily aggregate.</summary>
public interface ICspReportStore
{
    /// <summary>Upserts <c>count + 1</c> for the key in ONE statement, folding a new key into the overflow row past the day's cap.</summary>
    Task RecordAsync(CspViolationKey key, DateOnly day, int maxKeysPerDay, CancellationToken ct);
}

/// <summary>The PostgreSQL <see cref="ICspReportStore"/>.</summary>
public sealed class CspReportStore(InferenceApiDbContext db) : ICspReportStore
{
    /// <summary>
    /// One INSERT .. ON CONFLICT DO UPDATE. A key that already exists for the day is incremented; a new key
    /// is stored as itself while the day has fewer than the cap of distinct keys, else as the
    /// <c>(overflow)</c> row of its report type, disposition and browser family. Concurrent first reports
    /// can overshoot the cap by a handful of rows; the table cannot grow without bound.
    /// </summary>
    public const string UpsertSql = """
        WITH incoming AS (
            SELECT @p_day::date AS day, @p_type AS report_type, @p_disp AS disposition, @p_dir AS effective_directive,
                   @p_origin AS blocked_origin, @p_path AS document_path, @p_ua AS ua_family
        ), fold AS (
            SELECT NOT EXISTS (
                       SELECT 1 FROM csp_violation_counts c, incoming i
                       WHERE c.day = i.day AND c.report_type = i.report_type AND c.disposition = i.disposition
                         AND c.effective_directive = i.effective_directive AND c.blocked_origin = i.blocked_origin
                         AND c.document_path = i.document_path AND c.ua_family = i.ua_family)
                   AND (SELECT count(*) FROM csp_violation_counts c, incoming i WHERE c.day = i.day) >= @p_max AS folded
        )
        INSERT INTO csp_violation_counts (day, report_type, disposition, effective_directive, blocked_origin, document_path, ua_family, count)
        SELECT i.day, i.report_type, i.disposition,
               CASE WHEN f.folded THEN '(overflow)' ELSE i.effective_directive END,
               CASE WHEN f.folded THEN '(overflow)' ELSE i.blocked_origin END,
               CASE WHEN f.folded THEN '(overflow)' ELSE i.document_path END,
               i.ua_family, 1
        FROM incoming i, fold f
        ON CONFLICT (day, report_type, disposition, effective_directive, blocked_origin, document_path, ua_family)
        DO UPDATE SET count = csp_violation_counts.count + 1
        """;

    /// <inheritdoc />
    public Task RecordAsync(CspViolationKey key, DateOnly day, int maxKeysPerDay, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            UpsertSql,
            [
                new NpgsqlParameter("p_day", NpgsqlDbType.Date) { Value = day },
                new NpgsqlParameter("p_type", NpgsqlDbType.Varchar) { Value = key.ReportType },
                new NpgsqlParameter("p_disp", NpgsqlDbType.Varchar) { Value = key.Disposition },
                new NpgsqlParameter("p_dir", NpgsqlDbType.Varchar) { Value = key.EffectiveDirective },
                new NpgsqlParameter("p_origin", NpgsqlDbType.Varchar) { Value = key.BlockedOrigin },
                new NpgsqlParameter("p_path", NpgsqlDbType.Varchar) { Value = key.DocumentPath },
                new NpgsqlParameter("p_ua", NpgsqlDbType.Varchar) { Value = key.UaFamily },
                new NpgsqlParameter("p_max", NpgsqlDbType.Integer) { Value = Math.Max(1, maxKeysPerDay) },
            ],
            ct);
}

/// <summary>Daily sweep: deletes aggregate rows older than <see cref="AlphaTelemetryOptions.CspReportRetentionDays"/>.</summary>
public sealed class CspReportRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<AlphaTelemetryOptions> options,
    ILogger<CspReportRetentionService> logger) : BackgroundService
{
    /// <summary>Delay before the first sweep after startup.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

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
                    int deleted = await SweepOnceAsync(DateOnly.FromDateTime(DateTime.UtcNow), stoppingToken);
                    if (deleted > 0) logger.LogInformation("Deleted {Count} expired CSP violation count rows", deleted);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The CSP violation count retention sweep failed; retrying tomorrow");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Deletes rows whose day is older than the retention window as of <paramref name="today"/>. Returns the rows deleted.</summary>
    public async Task<int> SweepOnceAsync(DateOnly today, CancellationToken ct)
    {
        DateOnly cutoff = today.AddDays(-Math.Max(1, options.Value.CspReportRetentionDays));
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        return await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM csp_violation_counts WHERE day < {0}",
            [cutoff],
            ct);
    }
}
