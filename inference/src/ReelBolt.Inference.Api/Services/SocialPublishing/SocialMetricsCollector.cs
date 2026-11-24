using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.SocialPublishing;

/// <summary>
/// When a published post's numbers are read: about 1 h, 6 h, 1 d, 3 d, 7 d, 14 d and 30 d after it went out, then never again.
/// A reading is due when the latest of those moments already passed and the post's newest reading is older than it (so a
/// service that was down catches up with ONE reading, not seven). A short grace after day 30 lets a late final reading happen.
/// </summary>
public static class SocialMetricsSchedule
{
    public static readonly IReadOnlyList<TimeSpan> Offsets =
        [TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromDays(1), TimeSpan.FromDays(3), TimeSpan.FromDays(7), TimeSpan.FromDays(14), TimeSpan.FromDays(30)];

    /// <summary>Readings stop this long after day 30 even if the last one never succeeded.</summary>
    public static readonly TimeSpan FinalGrace = TimeSpan.FromDays(2);

    /// <summary>The time of the next reading, or null when none is left. A reading already overdue answers a time in the past.</summary>
    public static DateTime? NextReadingAt(DateTime publishedAt, DateTime? lastCapturedAt, DateTime now)
    {
        DateTime? latestPassed = null;
        foreach (TimeSpan offset in Offsets)
        {
            DateTime slot = publishedAt + offset;
            if (slot <= now) latestPassed = slot;
            else break;
        }

        if (now > publishedAt + Offsets[^1] + FinalGrace) return null;
        if (latestPassed is { } passed && (lastCapturedAt is null || lastCapturedAt < passed)) return passed;
        if (lastCapturedAt is { } last && last >= publishedAt + Offsets[^1]) return null;
        foreach (TimeSpan offset in Offsets)
        {
            DateTime slot = publishedAt + offset;
            if (slot > now) return slot;
        }

        return null;
    }

    public static bool IsDue(DateTime publishedAt, DateTime? lastCapturedAt, DateTime now) =>
        NextReadingAt(publishedAt, lastCapturedAt, now) is { } at && at <= now;
}

public interface ISocialMetricsCollector
{
    /// <summary>One pass over published posts that are due a reading. Returns how many readings were stored.</summary>
    Task<int> CollectOnceAsync(CancellationToken ct);
}

/// <inheritdoc />
public sealed class SocialMetricsCollector : ISocialMetricsCollector
{
    private readonly InferenceApiDbContext _db;
    private readonly ISocialPlatformRegistry _platforms;
    private readonly ISocialConnectionService _connections;
    private readonly SocialPublishingOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SocialMetricsCollector> _logger;

    public SocialMetricsCollector(
        InferenceApiDbContext db,
        ISocialPlatformRegistry platforms,
        ISocialConnectionService connections,
        IOptions<SocialPublishingOptions> options,
        ILogger<SocialMetricsCollector> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _platforms = platforms;
        _connections = connections;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<int> CollectOnceAsync(CancellationToken ct)
    {
        if (!_options.Enabled) return 0;
        DateTime now = _clock.GetUtcNow().UtcDateTime;
        DateTime oldest = now - (SocialMetricsSchedule.Offsets[^1] + SocialMetricsSchedule.FinalGrace);

        List<SocialPost> candidates = await _db.SocialPosts.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Status == SocialPostStatus.Published && p.RemotePostId != null && p.PublishedAt != null && p.PublishedAt >= oldest)
            .OrderBy(p => p.PublishedAt)
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        List<Guid> ids = candidates.Select(p => p.Id).ToList();
        Dictionary<Guid, DateTime> lastByPost = (await _db.SocialPostMetrics.AsNoTracking()
                .Where(m => ids.Contains(m.PostId))
                .GroupBy(m => m.PostId)
                .Select(g => new { PostId = g.Key, Last = g.Max(m => m.CapturedAt) })
                .ToListAsync(ct))
            .ToDictionary(x => x.PostId, x => x.Last);

        List<SocialPost> due = candidates
            .Where(p => SocialMetricsSchedule.IsDue(p.PublishedAt!.Value, lastByPost.TryGetValue(p.Id, out DateTime l) ? l : null, now))
            .Take(Math.Max(1, _options.Engine.MetricsBatchSize))
            .ToList();

        int stored = 0;
        HashSet<SocialPlatformKind> limited = [];
        foreach (SocialPost post in due)
        {
            ct.ThrowIfCancellationRequested();
            if (limited.Contains(post.Platform)) continue;
            try
            {
                if (await ReadOneAsync(post, now, ct)) stored++;
            }
            catch (SocialApiException ex) when (ex.Kind == SocialErrorKind.RateLimited)
            {
                // Honour the platform's limit or our quota: no more reads for it this pass.
                limited.Add(post.Platform);
                _logger.LogInformation("Metrics reads for {Platform} paused: rate limited", post.Platform);
            }
            catch (SocialApiException ex)
            {
                _logger.LogWarning("Reading metrics of social post {PostId} failed ({Kind})", post.Id, ex.Kind);
            }
            catch (HttpRequestException)
            {
                _logger.LogWarning("Reading metrics of social post {PostId} failed (network)", post.Id);
            }
        }

        return stored;
    }

    private async Task<bool> ReadOneAsync(SocialPost post, DateTime now, CancellationToken ct)
    {
        // The account and its organization are read fresh every time: a disconnected or deleted one is never acted for.
        SocialAccount? account = await _db.SocialAccounts.IgnoreQueryFilters()
            .FirstOrDefaultAsync(a => a.Id == post.SocialAccountId && a.OrganizationId == post.OrganizationId, ct);
        if (account is null || account.Status != SocialAccountStatus.Active) return false;
        if (!await _db.Organizations.AsNoTracking().AnyAsync(o => o.Id == account.OrganizationId && o.DeletedAt == null, ct)) return false;

        ISocialPlatform? platform = _platforms.Get(post.Platform);
        if (platform is not { IsConfigured: true }) return false;

        SocialMetricsSnapshot snapshot;
        try
        {
            snapshot = await _connections.CallAsync(account, token => platform.ReadMetricsAsync(token, post.RemotePostId!, ct), ct);
        }
        catch (SocialApiException ex) when (ex.Kind is SocialErrorKind.NotFound or SocialErrorKind.Forbidden)
        {
            // The platform will not give this post's numbers (removed, or not permitted): record "not available" so the schedule moves on.
            snapshot = new SocialMetricsSnapshot(null, null, null, null, null, $"{SocialPlatforms.Slug(post.Platform)}.unavailable");
        }

        // Disconnected while the call was in flight: store nothing for it.
        SocialAccountStatusRow? current = await _db.SocialAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.Id == account.Id).Select(a => new SocialAccountStatusRow(a.Status)).FirstOrDefaultAsync(ct);
        if (current is null || current.Status == SocialAccountStatus.Revoked) return false;

        _db.SocialPostMetrics.Add(new SocialPostMetric
        {
            Id = Guid.NewGuid(),
            PostId = post.Id,
            CapturedAt = now,
            Views = snapshot.Views,
            Likes = snapshot.Likes,
            Comments = snapshot.Comments,
            Shares = snapshot.Shares,
            Saves = snapshot.Saves,
            Source = snapshot.Source.Length <= 64 ? snapshot.Source : snapshot.Source[..64]
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private sealed record SocialAccountStatusRow(string Status);
}

/// <summary>The hosted loop of the performance collector: every <c>MetricsSweepIntervalSeconds</c> reads the posts that are due.</summary>
public sealed class SocialMetricsSweeper(
    IServiceScopeFactory scopes,
    IOptions<SocialPublishingOptions> options,
    ILogger<SocialMetricsSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        SocialPublishingOptions all = options.Value;
        int seconds = all.Engine.MetricsSweepIntervalSeconds;
        if (!all.Enabled || seconds <= 0 || !SocialPlatforms.All.Any(all.IsConfigured))
        {
            logger.LogInformation("The social metrics collector is off (disabled, interval 0, or no platform configured)");
            return;
        }

        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using IServiceScope scope = scopes.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ISocialMetricsCollector>().CollectOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "A social metrics sweep failed; retrying after the interval");
                }

                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
