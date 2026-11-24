using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Background;

/// <summary>
/// Hard-deletes edit timelines that have sat in the trash longer than
/// <c>Editing:TrashRetentionDays</c> (default 30). The API only ever soft-deletes; this is the one
/// place a timeline row is really removed, together with its ops, snapshots and comments (the
/// foreign keys cascade). Runs hourly. Idempotent: the selection is "deleted before the cutoff", so
/// a second run finds nothing.
/// </summary>
public sealed class TimelineTrashPurgeService : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<EditingRetentionOptions> _options;
    private readonly ILogger<TimelineTrashPurgeService> _logger;
    private readonly TimeProvider _clock;

    public TimelineTrashPurgeService(
        IServiceScopeFactory scopeFactory,
        IOptions<EditingRetentionOptions> options,
        ILogger<TimelineTrashPurgeService> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Deletes every timeline soft-deleted before <paramref name="nowUtc"/> minus <paramref name="window"/>
    /// and returns how many went. Children go with them: by the database cascade on a relational
    /// store, explicitly on a provider that cannot cascade a bulk delete.
    /// </summary>
    public static async Task<int> PurgeOnceAsync(InferenceApiDbContext db, DateTime nowUtc, TimeSpan window, CancellationToken ct)
    {
        DateTime cutoff = nowUtc - window;
        IQueryable<EditTimelineEntity> expired = db.EditTimelines
            .IgnoreQueryFilters()
            .Where(t => t.DeletedAt != null && t.DeletedAt <= cutoff);

        if (db.Database.IsRelational())
        {
            return await expired.ExecuteDeleteAsync(ct);
        }

        List<Guid> ids = await expired.Select(t => t.Id).ToListAsync(ct);
        if (ids.Count == 0) return 0;

        db.EditTimelineOps.RemoveRange(await db.EditTimelineOps.Where(o => ids.Contains(o.TimelineId)).ToListAsync(ct));
        db.EditTimelineSnapshots.RemoveRange(await db.EditTimelineSnapshots.Where(s => ids.Contains(s.TimelineId)).ToListAsync(ct));
        db.EditTimelineComments.RemoveRange(
            await db.EditTimelineComments.IgnoreQueryFilters().Where(c => ids.Contains(c.TimelineId)).ToListAsync(ct));
        // Guest review links and their decisions (Wave 4): by the database cascade on a relational store.
        List<Guid> linkIds = await db.EditReviewLinks.IgnoreQueryFilters().Where(l => ids.Contains(l.TimelineId)).Select(l => l.Id).ToListAsync(ct);
        db.EditReviewDecisions.RemoveRange(await db.EditReviewDecisions.Where(d => linkIds.Contains(d.LinkId)).ToListAsync(ct));
        db.EditReviewLinks.RemoveRange(await db.EditReviewLinks.IgnoreQueryFilters().Where(l => linkIds.Contains(l.Id)).ToListAsync(ct));
        db.EditTimelines.RemoveRange(await expired.ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return ids.Count;
    }

    /// <summary>One sweep in its own scope, exposed for tests.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken ct)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        // Housekeeping across every organization's projects.
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();
        return await PurgeOnceAsync(db, _clock.GetUtcNow().UtcDateTime, _options.Value.TrashWindow, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using PeriodicTimer timer = new(SweepInterval);
            do
            {
                try
                {
                    int purged = await SweepOnceAsync(stoppingToken);
                    if (purged > 0)
                        _logger.LogInformation("The timeline trash purge removed {Count} timeline(s) past their retention", purged);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The timeline trash purge could not complete a sweep; the next one covers the same rows");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }
}
