using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Hourly history GC (contract section 4): drops expired versions, then frees the protected objects
/// and document rows no remaining non-erased item references. Objects under <c>history/</c> are
/// deleted through <see cref="IFileStorageService.DeleteManyAsync"/>, which the copy-on-change
/// decorator passes straight through.
/// </summary>
public sealed class ProjectHistoryRetentionService : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<ProjectHistoryOptions> _options;
    private readonly ILogger<ProjectHistoryRetentionService> _logger;
    private readonly TimeProvider _clock;

    public ProjectHistoryRetentionService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<ProjectHistoryOptions> options,
        ILogger<ProjectHistoryRetentionService> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>One GC pass, exposed for tests.</summary>
    public async Task<ProjectHistoryGcResult> SweepOnceAsync(CancellationToken ct)
    {
        int batch = Math.Max(1, _options.CurrentValue.GcBatchSize);
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        IFileStorageService storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
        using IDisposable systemScope = scope.ServiceProvider.GetService<ITenantContext>().System();

        // 0. Retry the erase of every file that is quarantined now (moderation or CSAM). The move and
        //    the eraser are not transactional; a failed or raced erase leaves bytes under history/,
        //    so each pass re-runs the idempotent erase for any file that still has a history item.
        IProjectHistoryEraser? eraser = scope.ServiceProvider.GetService<IProjectHistoryEraser>();
        if (eraser is not null)
        {
            Guid[] quarantinedFileIds = await db.ProjectFiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(f => f.ModerationStatus == FileModerationStatus.Quarantined
                            || f.CsamStatus == CsamScanStatus.Quarantined)
                .Select(f => f.Id)
                .ToArrayAsync(ct)
                .ConfigureAwait(false);

            if (quarantinedFileIds.Length > 0)
            {
                string[] itemKeys = quarantinedFileIds.Select(id => id.ToString("D")).ToArray();
                var pending = await db.ProjectVersionItems
                    .AsNoTracking()
                    .Where(i => i.Kind == ProjectVersionItemKind.File && itemKeys.Contains(i.ItemKey))
                    .Select(i => new { i.ProjectId, i.ItemKey })
                    .Distinct()
                    .ToListAsync(ct)
                    .ConfigureAwait(false);

                foreach (var item in pending)
                {
                    if (!Guid.TryParse(item.ItemKey, out Guid fileId))
                        continue;

                    try
                    {
                        await eraser.EraseFileAsync(item.ProjectId, fileId, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "History GC: erasing quarantined file {FileId} of project {ProjectId} failed; the next pass retries",
                            fileId, item.ProjectId);
                    }
                }
            }
        }

        // 1. Expired versions (named versions have a null ExpiresAt and are never selected). A version
        //    that still holds a platform-made file of a project whose output hold is set (or unreadable)
        //    is deferred, the way account purge defers: its history bytes are evidence and must not be
        //    destroyed while the project itself cannot be deleted.
        int versions = 0;
        int deferredVersions = 0;
        {
            Guid[] expiredProjectIds = await db.ProjectVersions
                .Where(v => v.ExpiresAt != null && v.ExpiresAt < now)
                .Select(v => v.ProjectId)
                .Distinct()
                .ToArrayAsync(ct)
                .ConfigureAwait(false);
            IReadOnlyDictionary<Guid, ProjectOutputHoldState> holds =
                await ProjectOutputHold.ReadManyAsync(db, expiredProjectIds, ct).ConfigureAwait(false);
            Guid[] heldProjects = holds
                .Where(pair => pair.Value.IsActive)
                .Select(pair => pair.Key)
                .ToArray();

            while (true)
            {
                IQueryable<ProjectVersion> expiredQuery = db.ProjectVersions
                    .Where(v => v.ExpiresAt != null && v.ExpiresAt < now);
                if (heldProjects.Length > 0)
                {
                    expiredQuery = expiredQuery.Where(v => !heldProjects.Contains(v.ProjectId)
                        || !db.ProjectVersionItems.Any(i => i.VersionId == v.Id && i.PlatformMade));
                }

                List<(Guid Id, Guid ProjectId)> expired = (await expiredQuery
                    .OrderBy(v => v.ExpiresAt)
                    .Select(v => new { v.Id, v.ProjectId })
                    .Take(batch)
                    .ToListAsync(ct)
                    .ConfigureAwait(false))
                    .Select(v => (v.Id, v.ProjectId))
                    .ToList();
                if (expired.Count == 0)
                    break;

                int removed = 0;
                foreach (IGrouping<Guid, (Guid Id, Guid ProjectId)> project in expired.GroupBy(v => v.ProjectId))
                {
                    Guid[] ids = project.Select(v => v.Id).ToArray();
                    if (db.Database.IsNpgsql())
                    {
                        // The same per-project advisory lock capture takes: an expiring version is
                        // never deleted while a capture of that project is reading its list (QA low).
                        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                        string lockKey = "project-history:" + project.Key.ToString("D");
                        await db.Database.ExecuteSqlAsync(
                            $"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct).ConfigureAwait(false);

                        // Re-check under the lock; a capture may have changed the list meanwhile.
                        Guid[] deletable = await db.ProjectVersions
                            .Where(v => ids.Contains(v.Id) && v.ExpiresAt != null && v.ExpiresAt < now)
                            .Where(v => !heldProjects.Contains(v.ProjectId)
                                        || !db.ProjectVersionItems.Any(i => i.VersionId == v.Id && i.PlatformMade))
                            .Select(v => v.Id)
                            .ToArrayAsync(ct)
                            .ConfigureAwait(false);

                        if (deletable.Length > 0)
                        {
                            // The items cascade in the database.
                            removed += await db.ProjectVersions
                                .Where(v => deletable.Contains(v.Id))
                                .ExecuteDeleteAsync(ct)
                                .ConfigureAwait(false);
                        }

                        await transaction.CommitAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        db.ProjectVersionItems.RemoveRange(
                            await db.ProjectVersionItems.Where(i => ids.Contains(i.VersionId)).ToListAsync(ct));
                        db.ProjectVersions.RemoveRange(
                            await db.ProjectVersions.Where(v => ids.Contains(v.Id)).ToListAsync(ct));
                        await db.SaveChangesAsync(ct).ConfigureAwait(false);
                        removed += ids.Length;
                    }
                }

                versions += removed;
                if (expired.Count < batch)
                    break;
            }

            if (heldProjects.Length > 0)
            {
                deferredVersions = await db.ProjectVersions
                    .CountAsync(v => v.ExpiresAt != null && v.ExpiresAt < now
                                     && heldProjects.Contains(v.ProjectId)
                                     && db.ProjectVersionItems.Any(i => i.VersionId == v.Id && i.PlatformMade), ct)
                    .ConfigureAwait(false);
            }
        }

        // 2. Blobs no remaining non-erased item references: delete the object, then the row. A blob
        //    that a platform-made item of a held project names is deferred (skipped) as well.
        int blobs = 0;
        int deferredBlobs = 0;
        {
            Guid[] blobProjectIds = await db.ProjectHistoryBlobs
                .Select(b => b.ProjectId)
                .Distinct()
                .ToArrayAsync(ct)
                .ConfigureAwait(false);
            IReadOnlyDictionary<Guid, ProjectOutputHoldState> blobHolds =
                await ProjectOutputHold.ReadManyAsync(db, blobProjectIds, ct).ConfigureAwait(false);
            Guid[] heldBlobProjects = blobHolds
                .Where(pair => pair.Value.IsActive)
                .Select(pair => pair.Key)
                .ToArray();

            while (true)
            {
                IQueryable<ProjectHistoryBlob> orphansQuery = db.ProjectHistoryBlobs
                    .Where(b => !db.ProjectVersionItems.Any(i => i.ProjectId == b.ProjectId
                                                                 && i.ContentId == b.ContentId
                                                                 && i.ErasedAt == null));
                if (heldBlobProjects.Length > 0)
                {
                    orphansQuery = orphansQuery.Where(b => !heldBlobProjects.Contains(b.ProjectId)
                        || !db.ProjectVersionItems.Any(i => i.ProjectId == b.ProjectId
                                                             && i.ContentId == b.ContentId
                                                             && i.PlatformMade));
                }

                List<ProjectHistoryBlob> orphans = await orphansQuery
                    .OrderBy(b => b.CreatedAt)
                    .Take(batch)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
                if (orphans.Count == 0)
                    break;

                int freedCount = 0;
                foreach (IGrouping<Guid, ProjectHistoryBlob> project in orphans.GroupBy(b => b.ProjectId))
                {
                    if (db.Database.IsNpgsql())
                    {
                        // Same per-project advisory lock as capture: a catch-up capture cannot
                        // re-reference a ContentId while its blob is being deleted (QA low).
                        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                        string lockKey = "project-history:" + project.Key.ToString("D");
                        await db.Database.ExecuteSqlAsync(
                            $"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct).ConfigureAwait(false);

                        string[] contentIds = project.Select(b => b.ContentId).ToArray();
                        List<ProjectHistoryBlob> deletable = await db.ProjectHistoryBlobs
                            .Where(b => b.ProjectId == project.Key
                                        && contentIds.Contains(b.ContentId)
                                        && !db.ProjectVersionItems.Any(i => i.ProjectId == b.ProjectId
                                                                            && i.ContentId == b.ContentId
                                                                            && i.ErasedAt == null)
                                        && (!heldBlobProjects.Contains(b.ProjectId)
                                            || !db.ProjectVersionItems.Any(i => i.ProjectId == b.ProjectId
                                                                                && i.ContentId == b.ContentId
                                                                                && i.PlatformMade)))
                            .ToListAsync(ct)
                            .ConfigureAwait(false);
                        if (deletable.Count == 0)
                        {
                            await transaction.CommitAsync(ct).ConfigureAwait(false);
                            continue;
                        }

                        string[] keys = deletable.Select(b => b.StorageKey).ToArray();
                        int deleted = await storage.DeleteManyAsync(project.Key, keys, ct).ConfigureAwait(false);
                        if (deleted < keys.Length)
                        {
                            // Keep the rows (the map back to surviving objects); the next pass retries.
                            _logger.LogWarning("History GC: {Missing} object(s) of project {ProjectId} were not deleted",
                                keys.Length - deleted, project.Key);
                            await transaction.CommitAsync(ct).ConfigureAwait(false);
                            continue;
                        }

                        string[] deletableIds = deletable.Select(b => b.ContentId).ToArray();
                        await db.ProjectHistoryBlobs
                            .Where(b => b.ProjectId == project.Key && deletableIds.Contains(b.ContentId))
                            .ExecuteDeleteAsync(ct)
                            .ConfigureAwait(false);
                        await transaction.CommitAsync(ct).ConfigureAwait(false);
                        freedCount += deletable.Count;
                        continue;
                    }

                    string[] legacyKeys = project.Select(b => b.StorageKey).ToArray();
                    int legacyDeleted = await storage.DeleteManyAsync(project.Key, legacyKeys, ct).ConfigureAwait(false);
                    if (legacyDeleted < legacyKeys.Length)
                    {
                        // Keep the rows (the map back to surviving objects); the next pass retries.
                        _logger.LogWarning("History GC: {Missing} object(s) of project {ProjectId} were not deleted",
                            legacyKeys.Length - legacyDeleted, project.Key);
                        continue;
                    }

                    db.ProjectHistoryBlobs.RemoveRange(project);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    freedCount += project.Count();
                }

                if (freedCount == 0)
                    break;

                blobs += freedCount;
                if (orphans.Count < batch)
                    break;
            }

            if (heldBlobProjects.Length > 0)
            {
                deferredBlobs = await db.ProjectHistoryBlobs
                    .CountAsync(b => !db.ProjectVersionItems.Any(i => i.ProjectId == b.ProjectId
                                                                      && i.ContentId == b.ContentId
                                                                      && i.ErasedAt == null)
                                     && heldBlobProjects.Contains(b.ProjectId)
                                     && db.ProjectVersionItems.Any(i => i.ProjectId == b.ProjectId
                                                                        && i.ContentId == b.ContentId
                                                                        && i.PlatformMade), ct)
                    .ConfigureAwait(false);
            }
        }

        if (deferredVersions > 0 || deferredBlobs > 0)
        {
            _logger.LogInformation(
                "History GC deferred {Versions} expired version(s) and {Blobs} object(s) under a project output hold",
                deferredVersions, deferredBlobs);
        }

        // 3. Documents no item names.
        int documents = 0;
        while (true)
        {
            List<ProjectHistoryDocument> orphans = await db.ProjectHistoryDocuments
                .Where(d => !db.ProjectVersionItems.Any(i => i.ProjectId == d.ProjectId
                                                             && i.DocumentSha256 == d.Sha256))
                .OrderBy(d => d.CreatedAt)
                .Take(batch)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            if (orphans.Count == 0)
                break;

            db.ProjectHistoryDocuments.RemoveRange(orphans);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            documents += orphans.Count;
            if (orphans.Count < batch)
                break;
        }

        return new ProjectHistoryGcResult(versions, blobs, documents);
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
                    ProjectHistoryGcResult result = await SweepOnceAsync(stoppingToken);
                    if (result.Versions + result.Blobs + result.Documents > 0)
                    {
                        _logger.LogInformation(
                            "Project history GC removed {Versions} version(s), {Blobs} object(s) and {Documents} document(s)",
                            result.Versions, result.Blobs, result.Documents);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The project history GC could not complete a pass; the next one covers the same rows");
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

/// <summary>What one GC pass removed.</summary>
public sealed record ProjectHistoryGcResult(int Versions, int Blobs, int Documents);
