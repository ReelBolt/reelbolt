using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// Once a day (at <c>Billing:StorageSampler:HourUtc</c>) writes one <see cref="UsageKind.StorageGbDay"/>
/// <see cref="UsageEvent"/> per organization: the exact stored bytes under every one of its projects'
/// <c>projects/{projectId}/</c> object-store prefixes (ListObjectsV2, paginated — this covers uploads,
/// renders, artifacts and timelines, none of which store sizes in the DB), expressed in GB (one
/// daily sample = one GB-day). Idempotency key <c>storage:{org}:{yyyy-MM-dd}</c>, so a rerun the same
/// day is a no-op and, if a run hit its time budget, a rerun picks up only the orgs still missing.
/// Disabled by default (<c>Billing:StorageSampler:Enabled</c>) — self-host has nothing to meter.
/// </summary>
public sealed class StorageUsageSampler : BackgroundService
{
    private const double BytesPerGb = 1024d * 1024d * 1024d;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StorageUsageSampler> _logger;

    public StorageUsageSampler(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<StorageUsageSampler> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public static string IdempotencyKey(Guid organizationId, DateOnly day) =>
        $"storage:{organizationId}:{day:yyyy-MM-dd}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_configuration.GetValue("Billing:StorageSampler:Enabled", false))
            return;

        int hour = Math.Clamp(_configuration.GetValue("Billing:StorageSampler:HourUtc", 2), 0, 23);

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTime now = DateTime.UtcNow;
            DateTime next = now.Date.AddHours(hour);
            if (next <= now) next = next.AddDays(1);
            try { await Task.Delay(next - now, stoppingToken); } catch (OperationCanceledException) { return; }

            try
            {
                int written = await SampleOnceAsync(DateTime.UtcNow, stoppingToken);
                _logger.LogInformation("Storage sampler wrote {Count} usage row(s)", written);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Storage sampler run failed; will retry at the next scheduled hour");
            }
        }
    }

    /// <summary>One sampling pass, exposed for tests. Returns the number of rows written.</summary>
    public async Task<int> SampleOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        var s3 = scope.ServiceProvider.GetRequiredService<IAmazonS3>();
        // Spans every organization: without a system scope the tenant filters hide all rows.
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();

        string bucket = _configuration["MinIO:BucketName"] ?? "reelforge";
        int budgetSeconds = _configuration.GetValue("Billing:StorageSampler:TimeBudgetSeconds", 1800);
        DateTime deadline = DateTime.UtcNow.AddSeconds(budgetSeconds);
        var day = DateOnly.FromDateTime(nowUtc);

        var projectsByOrg = (await db.Projects.AsNoTracking()
                .Select(p => new { p.Id, p.OrganizationId })
                .ToListAsync(ct))
            .GroupBy(p => p.OrganizationId)
            .ToList();

        int written = 0;
        foreach (var org in projectsByOrg)
        {
            if (DateTime.UtcNow >= deadline)
            {
                _logger.LogWarning("Storage sampler hit its time budget; remaining organizations are sampled on the next run");
                break;
            }

            string key = IdempotencyKey(org.Key, day);
            if (await db.UsageEvents.AnyAsync(e => e.IdempotencyKey == key, ct))
                continue;

            long bytes = 0;
            foreach (var project in org)
                bytes += await SumPrefixBytesAsync(s3, bucket, $"projects/{project.Id}/", ct);

            db.UsageEvents.Add(new UsageEvent
            {
                Id = Guid.NewGuid(),
                OrganizationId = org.Key,
                Source = UsageSource.Storage,
                Kind = UsageKind.StorageGbDay,
                Quantity = (decimal)(bytes / BytesPerGb),
                BillingMode = UsageBillingMode.Platform,
                // Storage is deliberately unpriced (free, decision 9): no price row, credits 0.
                CreditsMillis = 0,
                CostUsd = 0m,
                IdempotencyKey = key,
                OccurredAt = nowUtc
            });
            try
            {
                await db.SaveChangesAsync(ct);
                written++;
            }
            catch (DbUpdateException)
            {
                // Lost a race with a concurrent sampler (unique idempotency key): treat as already written.
                db.ChangeTracker.Clear();
            }
        }

        return written;
    }

    private static async Task<long> SumPrefixBytesAsync(IAmazonS3 s3, string bucket, string prefix, CancellationToken ct)
    {
        long total = 0;
        string? token = null;
        do
        {
            ListObjectsV2Response resp = await s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix,
                ContinuationToken = token
            }, ct);
            total += resp.S3Objects?.Sum(o => o.Size ?? 0) ?? 0;
            token = resp.IsTruncated == true ? resp.NextContinuationToken : null;
        } while (token is not null);
        return total;
    }
}
