using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;
using ReelBolt.Shared.Observability;

namespace ReelBolt.Inference.Api.Services.Storage;

/// <summary>
/// The one choke point that keeps the old bytes of a live project file (contract section 3). Every
/// API consumer of <see cref="IFileStorageService"/> goes through this decorator: before an upload
/// that overwrites, a delete or a move of a key under <c>projects/{p}/userFiles/</c> or
/// <c>.../agentFiles/</c>, it copies the bytes any non-expired version still names into the
/// content-addressed <c>history/blobs/</c> area.
/// <para>
/// An unchanged file costs no objects: the lookup is by <c>storage_key</c> over non-erased items of
/// non-expired versions, and a key no version captured is passed straight through. A blob is written
/// once per <c>ContentId</c> (the primary key is <c>(ProjectId, ContentId)</c>), so repeated
/// overwrites of the same bytes copy nothing more.
/// </para>
/// <para>
/// <b>Failures fail the operation.</b> If the HEAD, the copy or the blob row cannot be written, the
/// exception propagates and the caller's delete/move/overwrite never runs. History is never worth
/// silently losing. Quota is deliberately not checked: a delete must not be blocked by a full plan.
/// </para>
/// <para>
/// <b>Suppression.</b> Quarantine moves run inside <see cref="ProjectHistoryProtection.Suppress"/>
/// and erase the file's history separately, so quarantined bytes are never retained. Keys outside
/// the two protected categories (outputs, history, quarantine, editor scratch) pass through.
/// </para>
/// </summary>
public sealed class ProjectHistoryProtectingStorage : IFileStorageService
{
    private readonly IFileStorageService _inner;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProjectHistoryProtectingStorage> _logger;
    private readonly TimeProvider _clock;

    public ProjectHistoryProtectingStorage(
        IFileStorageService inner,
        IServiceScopeFactory scopeFactory,
        ILogger<ProjectHistoryProtectingStorage> logger,
        TimeProvider? clock = null)
    {
        _inner = inner;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string BucketName => _inner.BucketName;

    /// <inheritdoc />
    public async Task<StoredFileObject> UploadAsync(
        Guid projectId,
        Guid fileId,
        Stream content,
        string fileName,
        string contentType,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken ct,
        string category = "userFiles",
        string? originalPath = null)
    {
        // The destination is deterministic, so old bytes can be protected BEFORE the store
        // overwrites them.
        S3FileStorageService.UploadTarget target = S3FileStorageService.BuildUploadTarget(
            projectId, fileId, fileName, S3FileStorageService.NormalizeCategory(category), originalPath);

        await ProtectAsync(projectId, target.StorageKey, ct).ConfigureAwait(false);

        // Clear BEFORE the store overwrites the object (and again after): a capture racing the
        // overwrite must not trust a hash that describes the bytes being replaced. A failure here
        // fails the operation (QA finding 7).
        await ClearInPlaceHashesAsync(projectId, [target.StorageKey], ct).ConfigureAwait(false);
        StoredFileObject stored = await _inner.UploadAsync(
            projectId, fileId, content, fileName, contentType, metadata, ct, category, originalPath)
            .ConfigureAwait(false);

        // An overwrite invalidated any hash a row for this key still carried (step 4).
        await ClearInPlaceHashesAsync(projectId, [target.StorageKey], ct).ConfigureAwait(false);
        return stored;
    }

    /// <inheritdoc />
    public Task<Stream> DownloadAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        _inner.DownloadAsync(projectId, storageKey, ct);

    /// <inheritdoc />
    public async Task DeleteAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        await ProtectAsync(projectId, storageKey, ct).ConfigureAwait(false);
        await _inner.DeleteAsync(projectId, storageKey, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<int> DeleteManyAsync(
        Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct)
    {
        foreach (string key in storageKeys)
            await ProtectAsync(projectId, key, ct).ConfigureAwait(false);

        return await _inner.DeleteManyAsync(projectId, storageKeys, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveAsync(
        Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        await ProtectAsync(projectId, sourceStorageKey, ct).ConfigureAwait(false);
        if (!string.Equals(sourceStorageKey, destinationStorageKey, StringComparison.Ordinal))
            await ProtectAsync(projectId, destinationStorageKey, ct).ConfigureAwait(false);

        // Clear before the move (and again after): a hash must never vouch for bytes that moved
        // under it. A failure clears nothing further and fails the operation (QA finding 7).
        await ClearInPlaceHashesAsync(projectId, [sourceStorageKey, destinationStorageKey], ct)
            .ConfigureAwait(false);
        await _inner.MoveAsync(projectId, sourceStorageKey, destinationStorageKey, ct).ConfigureAwait(false);
        await ClearInPlaceHashesAsync(projectId, [sourceStorageKey, destinationStorageKey], ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> MoveIfMatchAsync(
        Guid projectId,
        string sourceStorageKey,
        string destinationStorageKey,
        string expectedETag,
        CancellationToken ct)
    {
        await ProtectAsync(projectId, sourceStorageKey, ct).ConfigureAwait(false);
        if (!string.Equals(sourceStorageKey, destinationStorageKey, StringComparison.Ordinal))
            await ProtectAsync(projectId, destinationStorageKey, ct).ConfigureAwait(false);

        await ClearInPlaceHashesAsync(projectId, [sourceStorageKey, destinationStorageKey], ct)
            .ConfigureAwait(false);
        bool moved = await _inner
            .MoveIfMatchAsync(projectId, sourceStorageKey, destinationStorageKey, expectedETag, ct)
            .ConfigureAwait(false);

        if (moved)
        {
            await ClearInPlaceHashesAsync(projectId, [sourceStorageKey, destinationStorageKey], ct)
                .ConfigureAwait(false);
        }

        return moved;
    }

    /// <inheritdoc />
    public Task<StoredObjectMetadata?> GetMetadataAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        _inner.GetMetadataAsync(projectId, storageKey, ct);

    /// <inheritdoc />
    public Task<string?> GetObjectETagAsync(Guid projectId, string storageKey, CancellationToken ct) =>
        _inner.GetObjectETagAsync(projectId, storageKey, ct);

    /// <inheritdoc />
    /// <remarks>
    /// A copy leaves the source untouched but overwrites whatever is at the destination (a restore
    /// copies a history blob onto a live key), so the destination's old bytes are protected first.
    /// </remarks>
    public async Task CopyAsync(
        Guid projectId, string sourceStorageKey, string destinationStorageKey, CancellationToken ct)
    {
        await ProtectAsync(projectId, destinationStorageKey, ct).ConfigureAwait(false);
        await ClearInPlaceHashesAsync(projectId, [destinationStorageKey], ct).ConfigureAwait(false);
        await _inner.CopyAsync(projectId, sourceStorageKey, destinationStorageKey, ct).ConfigureAwait(false);
        await ClearInPlaceHashesAsync(projectId, [destinationStorageKey], ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Contract section 3 steps 1-2: ensure a protected copy exists for every non-erased item of a
    /// non-expired version that names <paramref name="storageKey"/>. Nothing is written when no
    /// version names the key (an unchanged file costs nothing), when the item has no verified ETag,
    /// or when the live ETag no longer matches (the bytes are already gone).
    /// </summary>
    private async Task ProtectAsync(Guid projectId, string storageKey, CancellationToken ct)
    {
        // A quarantine move must never retain the bytes it is removing.
        if (ProjectHistoryProtection.IsSuppressed)
            return;

        if (!ProjectHistoryStorageKeys.IsProtectedLiveKey(projectId, storageKey))
            return;

        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();
        DateTime now = _clock.GetUtcNow().UtcDateTime;

        List<Candidate> candidates = await db.ProjectVersionItems
            .AsNoTracking()
            .Where(i => i.ProjectId == projectId
                        && i.StorageKey == storageKey
                        && i.Kind == ProjectVersionItemKind.File
                        && i.ErasedAt == null
                        && i.ContentId != null
                        && db.ProjectVersions.Any(v => v.Id == i.VersionId
                                                       && (v.ExpiresAt == null || v.ExpiresAt > now)))
            .Select(i => new Candidate(i.ContentId!, i.StorageETag))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
            return;

        List<string> contentIds = candidates
            .Select(c => c.ContentId)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        List<string> existing = await db.ProjectHistoryBlobs
            .AsNoTracking()
            .Where(b => b.ProjectId == projectId && contentIds.Contains(b.ContentId))
            .Select(b => b.ContentId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        HashSet<string> seen = new(existing, StringComparer.Ordinal);

        foreach (Candidate candidate in candidates)
        {
            if (!seen.Add(candidate.ContentId))
                continue;

            if (string.IsNullOrWhiteSpace(candidate.StorageETag))
            {
                // "unverified:<fileId>" or a HEAD that failed at capture: there is no identity to
                // check the live bytes against, so nothing can be vouched for.
                _logger.LogWarning(
                    "Project {ProjectId}: item {ContentId} has no verified ETag, so its old bytes at {StorageKey} are not protected",
                    projectId, candidate.ContentId, storageKey.ForLog());
                continue;
            }

            StoredObjectMetadata? head = await _inner
                .GetMetadataAsync(projectId, storageKey, ct)
                .ConfigureAwait(false);

            if (head is null || string.IsNullOrWhiteSpace(head.ETag)
                || !string.Equals(head.ETag, candidate.StorageETag, StringComparison.Ordinal))
            {
                // The bytes changed (or are gone) since the version was captured. Copying now would
                // protect the replacement under the old identity, which would restore the wrong file.
                _logger.LogWarning(
                    "Project {ProjectId}: the live ETag at {StorageKey} no longer matches the captured item {ContentId}; nothing is protected",
                    projectId, storageKey.ForLog(), candidate.ContentId);
                continue;
            }

            string blobKey = ProjectHistoryStorageKeys.BlobKey(projectId, candidate.ContentId);
            await _inner.CopyAsync(projectId, storageKey, blobKey, ct).ConfigureAwait(false);

            db.ProjectHistoryBlobs.Add(new ProjectHistoryBlob
            {
                ProjectId = projectId,
                ContentId = candidate.ContentId,
                StorageKey = blobKey,
                SizeBytes = head.SizeBytes,
                CreatedAt = now
            });

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // A concurrent operation inserted the same blob first. That is success, not failure.
                db.ChangeTracker.Clear();
                bool exists = await db.ProjectHistoryBlobs
                    .AsNoTracking()
                    .AnyAsync(b => b.ProjectId == projectId && b.ContentId == candidate.ContentId, ct)
                    .ConfigureAwait(false);
                if (!exists)
                    throw;
            }
        }
    }

    /// <summary>
    /// Contract section 3 step 4: an overwrite or move invalidates the content hash any row that
    /// still names the source or destination key carried.
    /// </summary>
    private async Task ClearInPlaceHashesAsync(
        Guid projectId, IReadOnlyCollection<string> storageKeys, CancellationToken ct)
    {
        string[] keys = storageKeys
            .Where(k => !string.IsNullOrEmpty(k))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (keys.Length == 0)
            return;

        using IServiceScope scope = _scopeFactory.CreateScope();
        InferenceApiDbContext db = scope.ServiceProvider.GetRequiredService<InferenceApiDbContext>();

        List<ProjectFile> rows = await db.ProjectFiles
            .IgnoreQueryFilters()
            .Where(f => f.ProjectId == projectId
                        && keys.Contains(f.StorageKey)
                        && (f.ContentSha256 != null || f.ContentSha256ETag != null))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (rows.Count == 0)
            return;

        foreach (ProjectFile row in rows)
        {
            row.ContentSha256 = null;
            row.ContentSha256ETag = null;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private sealed record Candidate(string ContentId, string? StorageETag);
}
