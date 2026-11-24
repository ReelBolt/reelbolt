using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Builds project versions and the live manifest (contract section 4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Concurrency.</b> A capture holds <c>pg_advisory_xact_lock(hashtext('project-history:' || p))</c>
/// inside one transaction on PostgreSQL, so two replicas cannot both read max(number) and allocate
/// the same number, and the dedupe-and-prune decisions see one version list. On a provider without
/// advisory locks (the in-memory tests) no lock SQL is sent and the context is used directly.
/// </para>
/// <para>
/// <b>Content identities.</b> A file whose row already carries <c>content_sha256</c> and
/// <c>content_sha256_etag</c> costs no HEAD. Others are HEADed at capture, bounded by
/// <see cref="ProjectHistoryOptions.MaxHeadsPerCapture"/> at
/// <see cref="ProjectHistoryOptions.HeadConcurrency"/> at a time; a file past the cap, or one whose
/// HEAD failed, is stored as <c>unverified:&lt;fileId&gt;</c> with no ETag and is restorable only
/// while the live file still exists and is unchanged.
/// </para>
/// </remarks>
public sealed partial class ProjectHistoryService : IProjectHistoryService
{
    private const string UserFilesCategory = "userFiles";
    private const string AgentFilesCategory = "agentFiles";

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly ProjectHistoryOptions _options;
    private readonly ILogger<ProjectHistoryService> _logger;
    private readonly TimeProvider _clock;

    public ProjectHistoryService(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IOptions<ProjectHistoryOptions> options,
        ILogger<ProjectHistoryService> logger,
        TimeProvider? clock = null)
    {
        _db = db;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <inheritdoc />
    public async Task<ProjectVersion?> CaptureAsync(
        Guid projectId,
        string cause,
        string? name,
        Guid? userId,
        CancellationToken ct)
    {
        if (!ProjectVersionCause.IsValid(cause))
            throw new ArgumentException($"Unknown version cause '{cause}'.", nameof(cause));

        string? normalizedName = null;
        if (cause == ProjectVersionCause.Named)
            normalizedName = ProjectVersionName.Normalize(name);

        if (!_db.Database.IsNpgsql())
        {
            ProjectVersion? captured = await CaptureCoreAsync(projectId, cause, normalizedName, userId, ct);
            if (captured is not null)
                await _db.SaveChangesAsync(ct);
            return captured;
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(ct);
        string lockKey = "project-history:" + projectId.ToString("D");
        await _db.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct);

        ProjectVersion? version = await CaptureCoreAsync(projectId, cause, normalizedName, userId, ct);
        if (version is not null)
            await _db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        return version;
    }

    private async Task<ProjectVersion?> CaptureCoreAsync(
        Guid projectId,
        string cause,
        string? normalizedName,
        Guid? userId,
        CancellationToken ct)
    {
        ProjectHistoryManifest? manifest = await BuildLiveManifestAsync(projectId, ct);
        if (manifest is null)
            return null;

        int fileCount = manifest.Items.Count(i => i.Kind == ProjectVersionItemKind.File);
        if (fileCount > _options.MaxFilesPerVersion)
            throw new ProjectHistoryTooLargeException(fileCount, _options.MaxFilesPerVersion);

        // Tracked, because reusing the newest version extends its retention window below.
        ProjectVersion? newest = await _db.ProjectVersions
            .Where(v => v.ProjectId == projectId)
            .OrderByDescending(v => v.Number)
            .FirstOrDefaultAsync(ct);

        DateTime now = Now;
        bool dedupe = cause is not (ProjectVersionCause.Named or ProjectVersionCause.BeforeRestore);
        if (dedupe && newest is not null
            && newest.ManifestDigest == manifest.ManifestDigest
            && (newest.ExpiresAt is null || newest.ExpiresAt > now))
        {
            // Only a still-live version may be reused, and reusing it must refresh its retention:
            // otherwise a beforeRun/beforeAssistant safety point can expire while it is still the
            // newest unchanged version, leaving nothing to restore to (QA finding 6).
            DateTime extended = now.AddDays(_options.AutoRetentionDays);
            if (newest.ExpiresAt is not null && extended > newest.ExpiresAt.Value)
                newest.ExpiresAt = extended;

            return newest;
        }

        await EnforceVersionCapsAsync(projectId, cause, ct);

        int number = (await _db.ProjectVersions
            .Where(v => v.ProjectId == projectId)
            .MaxAsync(v => (int?)v.Number, ct) ?? 0) + 1;

        DateTime createdAt = Now;
        ProjectVersion version = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Number = number,
            Name = normalizedName,
            Cause = cause,
            CreatedByUserId = userId,
            CreatedAt = createdAt,
            ExpiresAt = cause == ProjectVersionCause.Named
                ? null
                : createdAt.AddDays(_options.AutoRetentionDays),
            ManifestDigest = manifest.ManifestDigest,
            ItemCount = manifest.Items.Count,
            TotalBytes = manifest.TotalBytes
        };

        await AddDocumentsAsync(projectId, manifest.Items, ct);

        foreach (ProjectHistoryManifestItem item in manifest.Items)
        {
            version.Items.Add(new ProjectVersionItem
            {
                Id = Guid.NewGuid(),
                VersionId = version.Id,
                ProjectId = projectId,
                Kind = item.Kind,
                ItemKey = item.Key,
                Name = item.Name,
                Category = item.Category,
                MimeType = item.MimeType,
                SizeBytes = item.SizeBytes,
                ContentId = item.ContentId,
                StorageKey = item.StorageKey,
                StorageETag = item.StorageETag,
                DocumentSha256 = item.DocumentSha256,
                TimelineRevision = item.TimelineRevision,
                DesignSystemVersionId = item.DesignSystemVersionId,
                OutputProducedAt = item.OutputProducedAt,
                PlatformMade = item.PlatformMade
            });
        }

        _db.ProjectVersions.Add(version);
        return version;
    }

    /// <summary>
    /// Enforces the per-project version caps at capture: named versions are capped and refuse a new
    /// one (409); automatic versions are capped by pruning the oldest.
    /// </summary>
    private async Task EnforceVersionCapsAsync(Guid projectId, string cause, CancellationToken ct)
    {
        if (cause == ProjectVersionCause.Named)
        {
            int namedCount = await _db.ProjectVersions
                .Where(v => v.ProjectId == projectId && v.ExpiresAt == null)
                .CountAsync(ct);

            if (namedCount >= _options.MaxNamedVersions)
                throw new ProjectHistoryNamedVersionLimitException(_options.MaxNamedVersions);

            return;
        }

        int nonNamedCount = await _db.ProjectVersions
            .Where(v => v.ProjectId == projectId && v.ExpiresAt != null)
            .CountAsync(ct);

        int allowedBeforeInsert = Math.Max(0, _options.MaxNonNamedVersions - 1);
        int excess = nonNamedCount - allowedBeforeInsert;
        if (excess <= 0)
            return;

        // The cap prunes the oldest automatic versions, but a version that holds a platform-made
        // item of a project under an output hold (or whose marker cannot be read) is evidence: its
        // history bytes must survive while the project itself cannot be deleted. Prune the next
        // oldest eligible version instead. When none is eligible the cap is exceeded for now and a
        // warning records it; a capture is never refused (unlike the named cap).
        ProjectOutputHoldState hold = await ProjectOutputHold.ReadAsync(_db, projectId, ct);
        IQueryable<ProjectVersion> candidates = _db.ProjectVersions
            .Where(v => v.ProjectId == projectId && v.ExpiresAt != null);
        if (hold.IsActive)
        {
            candidates = candidates.Where(v =>
                !_db.ProjectVersionItems.Any(i => i.VersionId == v.Id && i.PlatformMade));
        }

        List<ProjectVersion> oldest = await candidates
            .OrderBy(v => v.Number)
            .Take(excess)
            .ToListAsync(ct);

        if (oldest.Count < excess)
        {
            _logger.LogWarning(
                "Project {ProjectId}: the automatic version cap is exceeded by {Excess} version(s); "
                + "{Held} version(s) with platform-made items were kept under the output hold",
                projectId, excess - oldest.Count, excess - oldest.Count);
        }

        _db.ProjectVersions.RemoveRange(oldest);
    }

    /// <summary>Inserts every document body the manifest names that the project does not hold yet.</summary>
    private async Task AddDocumentsAsync(
        Guid projectId,
        IReadOnlyList<ProjectHistoryManifestItem> items,
        CancellationToken ct)
    {
        List<ProjectHistoryManifestItem> documented = items
            .Where(i => i.DocumentJson is not null && i.DocumentSha256 is not null)
            .ToList();

        if (documented.Count == 0)
            return;

        List<string> shas = documented
            .Select(i => i.DocumentSha256!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        List<string> existing = await _db.ProjectHistoryDocuments
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId && shas.Contains(d.Sha256))
            .Select(d => d.Sha256)
            .ToListAsync(ct);

        HashSet<string> seen = new(existing, StringComparer.Ordinal);
        DateTime createdAt = Now;

        foreach (ProjectHistoryManifestItem item in documented)
        {
            if (!seen.Add(item.DocumentSha256!))
                continue;

            _db.ProjectHistoryDocuments.Add(new ProjectHistoryDocument
            {
                ProjectId = projectId,
                Sha256 = item.DocumentSha256!,
                Kind = item.Kind,
                Json = item.DocumentJson!,
                SizeBytes = ProjectHistoryDocuments.Utf8Size(item.DocumentJson!),
                CreatedAt = createdAt
            });
        }
    }

    /// <inheritdoc />
    public async Task<ProjectHistoryManifest?> BuildLiveManifestAsync(Guid projectId, CancellationToken ct)
    {
        Project? project = await _db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        if (project is null)
            return null;

        List<ProjectHistoryManifestItem> items = new();

        items.AddRange(await BuildFileItemsAsync(projectId, ct));
        items.AddRange(await BuildWorkflowItemsAsync(projectId, ct));
        items.AddRange(await BuildTimelineItemsAsync(projectId, ct));

        ProjectHistoryManifestItem? designSystemItem = await BuildDesignSystemItemAsync(projectId, ct);
        if (designSystemItem is not null)
            items.Add(designSystemItem);

        items.Add(await BuildPronunciationItemAsync(projectId, ct));
        items.Add(BuildProjectItem(project));

        return ProjectHistoryManifest.Create(items);
    }

    private async Task<List<ProjectHistoryManifestItem>> BuildFileItemsAsync(Guid projectId, CancellationToken ct)
    {
        // Only files a user or an agent owns; outputs, links and anything else in an unknown
        // category are never versioned. A quarantined row is excluded before any HEAD is issued.
        List<ProjectFile> files = await _db.ProjectFiles
            .AsNoTracking()
            .Where(f => f.ProjectId == projectId
                && (f.Category == UserFilesCategory || f.Category == AgentFilesCategory)
                && f.ModerationStatus != FileModerationStatus.Quarantined
                && f.CsamStatus != CsamScanStatus.Quarantined)
            .OrderBy(f => f.Id)
            .ToListAsync(ct);

        Dictionary<Guid, string> headEtags = await HeadUnhashedFilesAsync(projectId, files, ct);

        List<ProjectHistoryManifestItem> items = new(files.Count);
        foreach (ProjectFile file in files)
        {
            string? known = ContentId.FromKnownHash(file.ContentSha256, file.ContentSha256ETag);
            string contentId;
            string? storageETag;

            if (known is not null)
            {
                contentId = known;
                storageETag = file.ContentSha256ETag;
            }
            else if (headEtags.TryGetValue(file.Id, out string? etag))
            {
                contentId = ContentId.FromEtag(etag, file.SizeBytes);
                storageETag = etag;
            }
            else
            {
                contentId = ContentId.Unverified(file.Id);
                storageETag = null;
            }

            bool platformMade = ProjectOutputHold.IsPlatformMade(projectId, file.StorageKey, file.OutputProducedAt);
            items.Add(new ProjectHistoryManifestItem(
                ProjectVersionItemKind.File,
                file.Id.ToString("D"),
                Name: file.OriginalPath ?? file.OriginalFileName,
                Category: file.Category,
                MimeType: file.MimeType,
                SizeBytes: file.SizeBytes,
                ContentId: contentId,
                StorageKey: file.StorageKey,
                StorageETag: storageETag,
                OutputProducedAt: file.OutputProducedAt,
                PlatformMade: platformMade));
        }

        return items;
    }

    /// <summary>
    /// HEADs the unhashed files, bounded by the option's count and concurrency. A failed HEAD, an
    /// empty key and every file past the cap are simply absent from the returned map (unverified).
    /// </summary>
    private async Task<Dictionary<Guid, string>> HeadUnhashedFilesAsync(
        Guid projectId,
        IReadOnlyList<ProjectFile> files,
        CancellationToken ct)
    {
        List<ProjectFile> candidates = files
            .Where(f => ContentId.FromKnownHash(f.ContentSha256, f.ContentSha256ETag) is null
                && !string.IsNullOrWhiteSpace(f.StorageKey))
            .Take(Math.Max(0, _options.MaxHeadsPerCapture))
            .ToList();

        Dictionary<Guid, string> etags = new();
        if (candidates.Count == 0)
            return etags;

        ConcurrentDictionary<Guid, string> concurrent = new();
        using SemaphoreSlim gate = new(Math.Max(1, _options.HeadConcurrency));

        IEnumerable<Task> tasks = candidates.Select(async file =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string? etag = await _storage.GetObjectETagAsync(projectId, file.StorageKey, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(etag))
                    concurrent[file.Id] = etag;
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        foreach (KeyValuePair<Guid, string> pair in concurrent)
            etags[pair.Key] = pair.Value;

        return etags;
    }

    private async Task<List<ProjectHistoryManifestItem>> BuildWorkflowItemsAsync(
        Guid projectId,
        CancellationToken ct)
    {
        List<WorkflowDefinition> workflows = await _db.WorkflowDefinitions
            .AsNoTracking()
            .Where(w => w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard)
            .Include(w => w.Steps)
            .ThenInclude(s => s.AgentDefinition)
            .OrderBy(w => w.Id)
            .ToListAsync(ct);

        List<ProjectHistoryManifestItem> items = new(workflows.Count);
        foreach (WorkflowDefinition workflow in workflows)
        {
            string document = ProjectHistoryDocuments.Workflow(workflow);
            items.Add(new ProjectHistoryManifestItem(
                ProjectVersionItemKind.Workflow,
                workflow.Id.ToString("D"),
                Name: workflow.Name,
                DocumentSha256: ProjectHistoryDocuments.Sha256(document),
                DocumentJson: document));
        }

        return items;
    }

    private async Task<List<ProjectHistoryManifestItem>> BuildTimelineItemsAsync(
        Guid projectId,
        CancellationToken ct)
    {
        // The context's filter is DeletedAt == null, so a soft-deleted timeline is skipped here.
        List<EditTimelineEntity> timelines = await _db.EditTimelines
            .AsNoTracking()
            .Where(t => t.ProjectId == projectId)
            .OrderBy(t => t.Id)
            .ToListAsync(ct);

        List<ProjectHistoryManifestItem> items = new(timelines.Count);
        foreach (EditTimelineEntity timeline in timelines)
        {
            string document = ProjectHistoryDocuments.Timeline(timeline);
            items.Add(new ProjectHistoryManifestItem(
                ProjectVersionItemKind.Timeline,
                timeline.Id.ToString("D"),
                Name: timeline.Name,
                DocumentSha256: ProjectHistoryDocuments.Sha256(document),
                DocumentJson: document,
                TimelineRevision: timeline.Revision));
        }

        return items;
    }

    private async Task<ProjectHistoryManifestItem?> BuildDesignSystemItemAsync(
        Guid projectId,
        CancellationToken ct)
    {
        List<DesignSystem> systems = await _db.DesignSystems
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId)
            .ToListAsync(ct);

        if (systems.Count == 0)
            return null;

        // The project's design system: the extracted design if present, otherwise the first by kind.
        DesignSystem system = systems.FirstOrDefault(d => d.Kind == DesignSystemKinds.Design)
            ?? systems.OrderBy(d => d.Kind, StringComparer.Ordinal).First();

        string? digest = null;
        if (system.CurrentVersionId is { } versionId)
        {
            digest = await _db.DesignSystemVersions
                .AsNoTracking()
                .Where(v => v.Id == versionId)
                .Select(v => v.DocumentDigest)
                .FirstOrDefaultAsync(ct);
        }

        return new ProjectHistoryManifestItem(
            ProjectVersionItemKind.DesignSystem,
            "design-system",
            Name: system.Name,
            DocumentSha256: digest,
            DesignSystemVersionId: system.CurrentVersionId);
    }

    private async Task<ProjectHistoryManifestItem> BuildPronunciationItemAsync(
        Guid projectId,
        CancellationToken ct)
    {
        List<ProjectPronunciationEntry> entries = await _db.ProjectPronunciationEntries
            .AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .ToListAsync(ct);

        string document = ProjectHistoryDocuments.Pronunciation(entries);
        return new ProjectHistoryManifestItem(
            ProjectVersionItemKind.Pronunciation,
            "pronunciation",
            DocumentSha256: ProjectHistoryDocuments.Sha256(document),
            DocumentJson: document);
    }

    private static ProjectHistoryManifestItem BuildProjectItem(Project project)
    {
        string document = ProjectHistoryDocuments.Project(project);
        return new ProjectHistoryManifestItem(
            ProjectVersionItemKind.Project,
            "project",
            Name: project.Name,
            DocumentSha256: ProjectHistoryDocuments.Sha256(document),
            DocumentJson: document);
    }

    /// <inheritdoc />
    public async Task TryCaptureAutoAsync(Guid projectId, string cause, Guid? userId, CancellationToken ct)
    {
        if (!_options.AutoCapture)
            return;

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.AutoCaptureTimeoutSeconds)));

        try
        {
            await CaptureAsync(projectId, cause, name: null, userId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Automatic capture must never fail the caller's action, so every failure (timeout,
            // storage error, database error) is logged and swallowed. It also must not leave the
            // version/document rows it added (or the rows it pruned) tracked on the caller's
            // context, or the caller's next SaveChanges would write them (QA finding 5).
            DetachCaptureEntities();
            _logger.LogWarning(
                ex,
                "Automatic project version capture failed (project {ProjectId}, cause {Cause}).",
                projectId,
                cause);
        }
    }

    /// <summary>
    /// Forgets the entity states a failed capture left behind on the caller's context. Only the
    /// types capture itself tracks are touched: a caller's unrelated pending changes stay as they
    /// were, and a version the cap-prune marked Deleted goes back to Unchanged.
    /// </summary>
    private void DetachCaptureEntities()
    {
        foreach (EntityEntry entry in _db.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is ProjectVersion or ProjectVersionItem or ProjectHistoryDocument)
                entry.State = EntityState.Detached;
        }
    }

    // ── Read surface (contract section 5) ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<ProjectHistoryVersionsPage?> ListVersionsAsync(
        Guid projectId,
        int limit,
        int? beforeNumber,
        CancellationToken ct)
    {
        bool projectExists = await _db.Projects
            .AsNoTracking()
            .AnyAsync(p => p.Id == projectId, ct);
        if (!projectExists)
            return null;

        DateTime now = Now;
        IQueryable<ProjectVersion> query = _db.ProjectVersions
            .AsNoTracking()
            .Where(v => v.ProjectId == projectId && (v.ExpiresAt == null || v.ExpiresAt > now));

        if (beforeNumber is { } before)
            query = query.Where(v => v.Number < before);

        List<ProjectVersion> rows = await query
            .OrderByDescending(v => v.Number)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);

        Dictionary<Guid, string> names = await UserDisplayNamesAsync(rows, ct);
        long historyBytes = await HistoryBytesAsync(projectId, ct);

        return new ProjectHistoryVersionsPage(
            rows.Select(v => ToVersionInfo(v, names)).ToList(),
            historyBytes,
            _options.AutoRetentionDays);
    }

    /// <inheritdoc />
    public async Task<ProjectHistoryVersionDetail?> GetVersionAsync(
        Guid projectId,
        Guid versionId,
        CancellationToken ct)
    {
        DateTime now = Now;
        ProjectVersion? version = await _db.ProjectVersions
            .AsNoTracking()
            .Include(v => v.Items)
            .FirstOrDefaultAsync(
                v => v.Id == versionId
                     && v.ProjectId == projectId
                     && (v.ExpiresAt == null || v.ExpiresAt > now),
                ct);
        if (version is null)
            return null;

        Project? project = await _db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, ct);

        Dictionary<Guid, string> names = await UserDisplayNamesAsync([version], ct);
        List<ProjectHistoryItemInfo> items = await EvaluateItemsAsync(
            projectId, version, project, new ProjectHistoryContentGuard(_db, projectId), ct);

        return new ProjectHistoryVersionDetail(ToVersionInfo(version, names), items);
    }

    /// <inheritdoc />
    public async Task<ProjectHistoryVersionInfo?> GetVersionInfoAsync(
        Guid projectId,
        Guid versionId,
        CancellationToken ct)
    {
        DateTime now = Now;
        ProjectVersion? version = await _db.ProjectVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                v => v.Id == versionId
                     && v.ProjectId == projectId
                     && (v.ExpiresAt == null || v.ExpiresAt > now),
                ct);
        if (version is null)
            return null;

        Dictionary<Guid, string> names = await UserDisplayNamesAsync([version], ct);
        return ToVersionInfo(version, names);
    }

    /// <inheritdoc />
    public async Task<ProjectHistoryVersionInfo?> RenameVersionAsync(
        Guid projectId,
        Guid versionId,
        string? name,
        CancellationToken ct)
    {
        DateTime now = Now;
        ProjectVersion? version = await _db.ProjectVersions
            .FirstOrDefaultAsync(
                v => v.Id == versionId
                     && v.ProjectId == projectId
                     && (v.ExpiresAt == null || v.ExpiresAt > now),
                ct);
        if (version is null)
            return null;

        string? normalized = string.IsNullOrWhiteSpace(name) ? null : ProjectVersionName.Normalize(name);

        if (normalized is not null && version.Name is null)
        {
            int named = await _db.ProjectVersions
                .CountAsync(v => v.ProjectId == projectId && v.Id != versionId && v.Name != null, ct);
            if (named >= _options.MaxNamedVersions)
                throw new ProjectHistoryNamedVersionLimitException(_options.MaxNamedVersions);
        }

        version.Name = normalized;
        version.ExpiresAt = normalized is null ? now.AddDays(_options.AutoRetentionDays) : null;
        await _db.SaveChangesAsync(ct);

        Dictionary<Guid, string> names = await UserDisplayNamesAsync([version], ct);
        return ToVersionInfo(version, names);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteVersionAsync(Guid projectId, Guid versionId, CancellationToken ct)
    {
        DateTime now = Now;
        ProjectVersion? version = await _db.ProjectVersions
            .FirstOrDefaultAsync(
                v => v.Id == versionId
                     && v.ProjectId == projectId
                     && (v.ExpiresAt == null || v.ExpiresAt > now),
                ct);
        if (version is null)
            return false;

        _db.ProjectVersions.Remove(version);
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> IsVersionDeletionHeldAsync(Guid projectId, Guid versionId, CancellationToken ct)
    {
        ProjectOutputHoldState hold = await ProjectOutputHold.ReadAsync(_db, projectId, ct);
        if (!hold.IsActive)
            return false;

        return await _db.ProjectVersionItems
            .AsNoTracking()
            .AnyAsync(i => i.VersionId == versionId && i.ProjectId == projectId && i.PlatformMade, ct);
    }

    /// <inheritdoc />
    public async Task<bool> IsFileDeletionHeldAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        ProjectOutputHoldState hold = await ProjectOutputHold.ReadAsync(_db, projectId, ct);
        if (!hold.IsActive)
            return false;

        string itemKey = fileId.ToString("D");
        List<(string? ContentId, bool PlatformMade)> own = (await _db.ProjectVersionItems
                .AsNoTracking()
                .Where(i => i.ProjectId == projectId
                            && i.Kind == ProjectVersionItemKind.File
                            && i.ItemKey == itemKey)
                .Select(i => new { i.ContentId, i.PlatformMade })
                .ToListAsync(ct))
            .Select(i => (i.ContentId, i.PlatformMade))
            .ToList();

        if (own.Count == 0)
            return false;

        if (own.Any(i => i.PlatformMade))
            return true;

        List<string> contentIds = own
            .Where(i => i.ContentId is not null)
            .Select(i => i.ContentId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (contentIds.Count == 0)
            return false;

        // Every content the erase would touch is evidence when each one appears on a platform-made
        // item of another key: the eraser could not remove any bytes, so the route refuses instead.
        List<string> heldContentIds = await _db.ProjectVersionItems
            .AsNoTracking()
            .Where(i => i.ProjectId == projectId
                        && i.PlatformMade
                        && i.ContentId != null
                        && contentIds.Contains(i.ContentId))
            .Select(i => i.ContentId!)
            .Distinct()
            .ToListAsync(ct);

        return heldContentIds.Count == contentIds.Count;
    }

    private async Task<long> HistoryBytesAsync(Guid projectId, CancellationToken ct)
    {
        long blobBytes = await _db.ProjectHistoryBlobs
            .AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .SumAsync(b => (long?)b.SizeBytes, ct) ?? 0;
        long documentBytes = await _db.ProjectHistoryDocuments
            .AsNoTracking()
            .Where(d => d.ProjectId == projectId)
            .SumAsync(d => (long?)d.SizeBytes, ct) ?? 0;
        return blobBytes + documentBytes;
    }

    private async Task<Dictionary<Guid, string>> UserDisplayNamesAsync(
        IReadOnlyCollection<ProjectVersion> versions,
        CancellationToken ct)
    {
        Guid[] userIds = versions
            .Where(v => v.CreatedByUserId is not null)
            .Select(v => v.CreatedByUserId!.Value)
            .Distinct()
            .ToArray();
        if (userIds.Length == 0)
            return new Dictionary<Guid, string>();

        return await _db.ApplicationUsers
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName })
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static ProjectHistoryVersionInfo ToVersionInfo(
        ProjectVersion version,
        IReadOnlyDictionary<Guid, string> names) =>
        new(
            version.Id,
            version.Number,
            version.Name,
            version.Cause,
            version.CreatedAt,
            version.ExpiresAt,
            version.CreatedByUserId is { } uid
                && names.TryGetValue(uid, out string? display)
                && !string.IsNullOrWhiteSpace(display)
                ? display
                : null,
            version.ItemCount,
            version.TotalBytes);

    private async Task<List<ProjectHistoryItemInfo>> EvaluateItemsAsync(
        Guid projectId,
        ProjectVersion version,
        Project? project,
        ProjectHistoryContentGuard guard,
        CancellationToken ct)
    {
        List<ProjectVersionItem> items = version.Items
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.ItemKey, StringComparer.Ordinal)
            .ToList();
        if (items.Count == 0)
            return [];

        Guid[] fileIds = items
            .Where(i => i.Kind == ProjectVersionItemKind.File && Guid.TryParse(i.ItemKey, out _))
            .Select(i => Guid.Parse(i.ItemKey))
            .Distinct()
            .ToArray();

        Dictionary<Guid, ProjectFile> liveFiles = fileIds.Length == 0
            ? new Dictionary<Guid, ProjectFile>()
            : await _db.ProjectFiles
                .AsNoTracking()
                .Where(f => f.ProjectId == projectId && fileIds.Contains(f.Id))
                .ToDictionaryAsync(f => f.Id, ct);

        string[] contentIds = items
            .Where(i => i.ContentId is not null)
            .Select(i => i.ContentId!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        HashSet<string> blobContentIds = contentIds.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                await _db.ProjectHistoryBlobs
                    .AsNoTracking()
                    .Where(b => b.ProjectId == projectId && contentIds.Contains(b.ContentId))
                    .Select(b => b.ContentId)
                    .ToListAsync(ct),
                StringComparer.Ordinal);

        string[] documentShas = items
            .Where(i => i.DocumentSha256 is not null)
            .Select(i => i.DocumentSha256!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        HashSet<string> documentShasPresent = documentShas.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(
                await _db.ProjectHistoryDocuments
                    .AsNoTracking()
                    .Where(d => d.ProjectId == projectId && documentShas.Contains(d.Sha256))
                    .Select(d => d.Sha256)
                    .ToListAsync(ct),
                StringComparer.Ordinal);

        Guid[] designVersionIds = items
            .Where(i => i.DesignSystemVersionId is not null)
            .Select(i => i.DesignSystemVersionId!.Value)
            .Distinct()
            .ToArray();
        HashSet<Guid> designVersionsPresent = designVersionIds.Length == 0
            ? []
            : new HashSet<Guid>(
                await _db.DesignSystemVersions
                    .AsNoTracking()
                    .Where(v => designVersionIds.Contains(v.Id))
                    .Select(v => v.Id)
                    .ToListAsync(ct));

        List<ProjectHistoryItemInfo> result = new(items.Count);
        foreach (ProjectVersionItem item in items)
        {
            (bool restorable, string? reason) = await EvaluateItemAsync(
                projectId, version, project, item, liveFiles, blobContentIds,
                documentShasPresent, designVersionsPresent, guard, ct);

            result.Add(new ProjectHistoryItemInfo(
                item.Kind,
                item.ItemKey,
                item.Name,
                item.Category,
                item.MimeType,
                item.SizeBytes,
                restorable,
                reason));
        }

        return result;
    }

    private async Task<(bool Restorable, string? Reason)> EvaluateItemAsync(
        Guid projectId,
        ProjectVersion version,
        Project? project,
        ProjectVersionItem item,
        IReadOnlyDictionary<Guid, ProjectFile> liveFiles,
        IReadOnlySet<string> blobContentIds,
        IReadOnlySet<string> documentShas,
        IReadOnlySet<Guid> designVersions,
        ProjectHistoryContentGuard guard,
        CancellationToken ct)
    {
        if (item.ErasedAt is not null)
            return (false, ProjectHistoryRestoreReasons.Erased);

        if (item.Kind != ProjectVersionItemKind.File)
        {
            if (item.Kind == ProjectVersionItemKind.DesignSystem)
            {
                return item.DesignSystemVersionId is { } designVersionId
                       && designVersions.Contains(designVersionId)
                    ? (true, null)
                    : (false, ProjectHistoryRestoreReasons.DesignVersionGone);
            }

            return item.DocumentSha256 is not null && documentShas.Contains(item.DocumentSha256)
                ? (true, null)
                : (false, ProjectHistoryRestoreReasons.ContentGone);
        }

        ProjectFile? live = Guid.TryParse(item.ItemKey, out Guid fileId)
                            && liveFiles.TryGetValue(fileId, out ProjectFile? found)
            ? found
            : null;

        if (live is not null
            && (live.ModerationStatus == FileModerationStatus.Quarantined
                || live.CsamStatus == CsamScanStatus.Quarantined))
        {
            return (false, ProjectHistoryRestoreReasons.Quarantined);
        }

        // The same shared guard the byte-reading paths use: a held output, a quarantine marker on the
        // live output or a version captured before either marker makes the item not restorable.
        if (await guard.RefusalAsync(
                project, version.CreatedAt, live, ct,
                platformMade: item.PlatformMade, outputProducedAt: item.OutputProducedAt) is { } refusal)
            return (false, refusal);

        if (item.ContentId is not null && blobContentIds.Contains(item.ContentId))
            return (true, null);

        if (ContentId.IsUnverified(item.ContentId))
        {
            // No ETag was captured, so the bytes cannot be promised even when the live row is
            // still there; layer 2 re-checks the live file before it uses it.
            return (false, live is null
                ? ProjectHistoryRestoreReasons.ContentGone
                : ProjectHistoryRestoreReasons.Unverified);
        }

        if (live is null || string.IsNullOrEmpty(item.StorageETag))
            return (false, ProjectHistoryRestoreReasons.ContentGone);

        // The live row's verified ETag is authoritative and costs no round trip.
        if (string.Equals(live.ContentSha256ETag, item.StorageETag, StringComparison.Ordinal))
            return (true, null);

        if (!string.IsNullOrWhiteSpace(live.StorageKey))
        {
            string? etag = await _storage.GetObjectETagAsync(projectId, live.StorageKey, ct);
            if (string.Equals(etag, item.StorageETag, StringComparison.Ordinal))
                return (true, null);
        }

        return (false, ProjectHistoryRestoreReasons.ContentGone);
    }
}
