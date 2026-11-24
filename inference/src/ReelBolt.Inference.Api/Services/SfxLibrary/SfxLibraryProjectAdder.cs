using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.SfxLibrary;

/// <summary>Server-side copy of one library object into a project. The two keys are checked here, not trusted from the caller.</summary>
public interface ISfxLibraryObjectCopier
{
    Task CopyAsync(string libraryKey, string projectKey, CancellationToken ct);
}

/// <inheritdoc />
public sealed class S3SfxLibraryObjectCopier(IAmazonS3 s3, IConfiguration configuration) : ISfxLibraryObjectCopier
{
    private readonly string _bucket = configuration["MinIO:BucketName"] ?? "reelforge";

    public async Task CopyAsync(string libraryKey, string projectKey, CancellationToken ct)
    {
        if (!SfxLibraryKeys.IsLibraryKey(libraryKey)) throw new UnauthorizedAccessException("Source is not a library object.");
        if (!projectKey.StartsWith("projects/", StringComparison.Ordinal) || projectKey.Contains("..", StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Destination is not a project object.");
        await s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = libraryKey,
            DestinationBucket = _bucket,
            DestinationKey = projectKey
        }, ct);
    }
}

/// <summary>Process-local sliding-window limit on library adds per user (<c>SfxLibrary:MaxAddsPerMinute</c>, default 60).</summary>
public sealed class SfxLibraryAddRateLimiter(IConfiguration configuration, TimeProvider? clock = null)
{
    private readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> _log = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private int Limit => Math.Max(1, configuration.GetValue("SfxLibrary:MaxAddsPerMinute", 60));

    public bool TryAcquire(Guid user)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        Queue<DateTimeOffset> q = _log.GetOrAdd(user, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() > TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count >= Limit) return false;
            q.Enqueue(now);
            return true;
        }
    }
}

public enum SfxAddOutcome { Added, AlreadyThere, NotFound, CapReached, RateLimited }

public sealed record SfxAddResult(SfxAddOutcome Outcome, ProjectFile? File);

/// <summary>
/// Copies a built-in library sound into a project as an ordinary audio project file. The SOURCE key comes only from the
/// library table row; the DESTINATION key is built from the row's validated slug and extension, never from a request.
/// The row is registered pre-cleared (moderation Clean, CSAM Clean): it is first-party, curated content. That is the only
/// place in the codebase that pre-clears a project file, and nothing but this class does it.
/// </summary>
public sealed class SfxLibraryProjectAdder(
    InferenceApiDbContext db, ISfxLibraryObjectCopier copier, SfxLibraryAddRateLimiter limiter, IFileStorageService storage, IPublishEndpoint publish,
    IConfiguration configuration, ILogger<SfxLibraryProjectAdder> logger, TimeProvider? clock = null)
{
    public const string SourceKind = ReelBolt.Shared.Billing.ProjectFileQuota.SfxLibrarySourceKind;
    public const string Directory = "sound-effects";
    public const int DefaultMaxPerProject = 200;
    public const int StarterSetSize = 24;

    private static readonly Regex SlugRx = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ExtRx = new(@"^\.[a-z0-9]{1,5}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private int MaxPerProject => Math.Max(1, configuration.GetValue("SfxLibrary:MaxPerProject", DefaultMaxPerProject));

    /// <summary>The destination key for an item, or null when its slug or extension is not safe to put in a key.</summary>
    public static string? DestinationKey(Guid projectId, string slug, string storageKey)
    {
        string ext = Path.GetExtension(storageKey).ToLowerInvariant();
        if (slug.Length is 0 or > 80 || !SlugRx.IsMatch(slug) || !ExtRx.IsMatch(ext)) return null;
        return $"projects/{projectId}/userFiles/{Directory}/{slug}{ext}";
    }

    /// <summary>Which items the starter set holds: round-robin over the categories in library order, so it spreads.</summary>
    public static IReadOnlyList<SfxLibraryItem> PickStarterSet(IEnumerable<SfxLibraryItem> enabledInLibraryOrder, int size = StarterSetSize)
    {
        List<Queue<SfxLibraryItem>> lanes = enabledInLibraryOrder
            .GroupBy(i => i.Category, StringComparer.Ordinal)
            .Select(g => new Queue<SfxLibraryItem>(g))
            .ToList();
        List<SfxLibraryItem> picked = [];
        while (picked.Count < size && lanes.Any(l => l.Count > 0))
        {
            foreach (Queue<SfxLibraryItem> lane in lanes)
            {
                if (picked.Count >= size) break;
                if (lane.Count > 0) picked.Add(lane.Dequeue());
            }
        }

        return picked;
    }

    /// <summary>Adds the starter set. One rate-limit hit covers the whole set; the gate lives here so every caller (REST, assistant, MCP) passes it.</summary>
    public async Task<IReadOnlyList<SfxAddResult>> AddStarterSetAsync(Guid projectId, Guid userId, CancellationToken ct)
    {
        if (!limiter.TryAcquire(userId)) return [new(SfxAddOutcome.RateLimited, null)];
        List<SfxLibraryItem> all = await db.SfxLibraryItems.AsNoTracking().Where(i => i.IsEnabled)
            .OrderBy(i => i.SortOrder).ThenBy(i => i.Slug).ToListAsync(ct);
        List<SfxAddResult> results = [];
        foreach (SfxLibraryItem item in PickStarterSet(all))
        {
            SfxAddResult r = await AddCoreAsync(projectId, item.Id, ct);
            results.Add(r);
            if (r.Outcome == SfxAddOutcome.CapReached) break;
        }

        return results;
    }

    /// <summary>Adds one sound. The per-user rate limit and the per-project cap are enforced here, for every caller.</summary>
    public async Task<SfxAddResult> AddAsync(Guid projectId, Guid itemId, Guid userId, CancellationToken ct)
    {
        if (!limiter.TryAcquire(userId)) return new(SfxAddOutcome.RateLimited, null);
        return await AddCoreAsync(projectId, itemId, ct);
    }

    private async Task<SfxAddResult> AddCoreAsync(Guid projectId, Guid itemId, CancellationToken ct)
    {
        SfxLibraryItem? item = await db.SfxLibraryItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.IsEnabled, ct);
        if (item is null || !SfxLibraryKeys.IsLibraryKey(item.StorageKey)) return new(SfxAddOutcome.NotFound, null);
        string? destination = DestinationKey(projectId, item.Slug, item.StorageKey);
        if (destination is null) return new(SfxAddOutcome.NotFound, null);

        ProjectFile? existing = await FindAsync(projectId, destination, ct);
        if (existing is not null) return new(SfxAddOutcome.AlreadyThere, existing);

        int used = await db.ProjectFiles.CountAsync(f => f.ProjectId == projectId && f.SourceKind == SourceKind, ct);
        if (used >= MaxPerProject) return new(SfxAddOutcome.CapReached, null);

        try
        {
            await copier.CopyAsync(item.StorageKey, destination, ct);
        }
        catch (Exception ex) when (ex is AmazonS3Exception or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Sound library: copy of {Slug} into project {ProjectId} failed.", item.Slug, projectId);
            return new(SfxAddOutcome.NotFound, null);
        }

        string fileName = Path.GetFileName(destination);
        DateTime now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        ProjectFile row = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            OriginalFileName = fileName,
            OriginalPath = $"{Directory}/{fileName}",
            DirectoryPath = Directory,
            Category = "userFiles",
            StorageKey = destination,
            StorageFileName = fileName,
            StorageBucket = storage.BucketName,
            StoragePrefix = ProjectFilePath.BuildStoragePrefix(projectId, "userFiles"),
            MimeType = item.MimeType,
            SizeBytes = item.SizeBytes,
            SourceKind = SourceKind,
            SummaryStatus = SummaryStatus.Done,
            IndexingStatus = FileIndexingStatus.NotIndexed,
            // First-party curated content: pre-cleared. Only this class does this (see the class summary).
            ModerationStatus = FileModerationStatus.Clean,
            CsamStatus = CsamScanStatus.Clean,
            CsamScannedAt = now,
            UploadedAt = now
        };
        db.ProjectFiles.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            ProjectFile? raced = await FindAsync(projectId, destination, ct);
            if (raced is not null) return new(SfxAddOutcome.AlreadyThere, raced);
            throw;
        }

        // Peaks (and the rest of the default set) for the timeline art, like any upload; best effort after the commit.
        await UploadDerivativeRequest.PublishAsync(publish, storage, null, row, row.MimeType, logger, ct);
        return new(SfxAddOutcome.Added, row);
    }

    // ANY row at the destination key counts (a restored or rewritten row that lost its library tag included): the bytes there are never overwritten.
    private Task<ProjectFile?> FindAsync(Guid projectId, string destination, CancellationToken ct) =>
        db.ProjectFiles.AsNoTracking().FirstOrDefaultAsync(
            f => f.StorageKey == destination, ct);
}
