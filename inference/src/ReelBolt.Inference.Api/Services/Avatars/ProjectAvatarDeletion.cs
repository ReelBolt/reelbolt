using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Services.Avatars;

/// <summary>Why an avatar is being deleted: it decides the audit event's kind and its actor.</summary>
public enum AvatarDeleteCause
{
    /// <summary>A member pressed Delete (<c>DELETE .../avatars/{id}</c>).</summary>
    User,

    /// <summary>The retention sweep: past <c>retention_until</c>, or revoked longer than the grace.</summary>
    Retention,

    /// <summary>
    /// The workspace avatar library item this avatar is an import of is being deleted (by a member or by the retention
    /// sweep), which removes every import first (docs/design/avatar-objects.md "The workspace avatar library").
    /// </summary>
    LibraryItem
}

/// <summary>One vendor-side copy and what deleting it did.</summary>
/// <param name="Provider">The provider kind the copy was uploaded to (<c>MiniMax</c>), or null when that row is gone.</param>
/// <param name="Outcome">One of <c>deleted</c>, <c>already_gone</c>, <c>vendor_refused</c>, <c>vendor_error</c>, <c>not_supported</c>.</param>
public sealed record AvatarVendorAssetReport(string? Provider, string Outcome);

/// <summary>What deleting an avatar actually removed: reported, never assumed (docs/design/avatars.md "Delete").</summary>
/// <param name="ReferencesDeleted">Reference files whose row and object are gone.</param>
/// <param name="ReferencesFailed">Reference files whose row is gone but whose object could not be removed yet (a durable mark makes the storage sweep finish it).</param>
/// <param name="ReferencesKept">
/// Reference files deliberately kept: another avatar of the project still lists them, or the file is held (CSAM
/// quarantine or output hold), or the avatar is an OBJECT (its files are ordinary project files and all stay).
/// </param>
/// <param name="StillsDeleted">Phase 3: stills of the avatar (made for talking-head clips) whose object is gone.</param>
/// <param name="StillsFailed">
/// Phase 3: stills whose object could not be removed yet (a durable mark makes the storage sweep finish it), or -1 when
/// the stills folder could not be listed (the storage sweep's orphan pass removes a still whose avatar is gone).
/// </param>
public sealed record AvatarDeleteReport(
    int ReferencesDeleted,
    int ReferencesFailed,
    int ReferencesKept,
    IReadOnlyList<AvatarVendorAssetReport> VendorAssets,
    bool ConsentDocumentDeleted,
    int StillsDeleted = 0,
    int StillsFailed = 0);

/// <summary>
/// The ONE delete routine of an avatar, shared by the DELETE route and <c>ProjectAvatarRetentionService</c>
/// (docs/design/avatars.md "Delete"): every vendor-side copy (best effort, outcome per copy), the reference files
/// (objects and <c>project_files</c> rows), the optional signed release, the stills made from the avatar (phase 3,
/// <see cref="AvatarStillStorage"/>), then the row. The row goes even when a vendor refuses, so a withdrawn consent is
/// never kept alive here.
/// </summary>
/// <remarks>
/// <para>
/// A reference file another avatar of the project still lists is kept (deleting it would break that avatar, and the
/// <c>NO ACTION</c> foreign key would refuse it anyway), and so is a file the CSAM control or the output hold keeps:
/// evidence is never destroyed by a product delete. Rows and their durable storage marks go in one transaction, then the
/// objects, as every other user file delete does (QA-SQC-1): a failed object delete keeps its mark and the storage sweep
/// finishes it. Pictures that used a reference photo get no private copy: the person withdrew, so their face goes.
/// </para>
/// <para>
/// <b>An object avatar keeps its files</b> (docs/design/avatar-objects.md): its references are ordinary project media
/// (a product shot, an extracted frame), never marked as avatar references and not personal data, so deleting the object
/// removes the row, its reference links and its stills and reports every file as kept.
/// </para>
/// <para>
/// The avatar row carries the <c>xmin</c> concurrency token; a delete that loses a race with a reference change is
/// retried from a fresh read, so it deletes the files the avatar holds NOW.
/// </para>
/// </remarks>
public sealed class ProjectAvatarDeletion
{
    private const int MaxAttempts = 3;

    private readonly InferenceApiDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly IAvatarVendorAssetDeleter _vendor;
    private readonly ILogger<ProjectAvatarDeletion> _logger;
    private readonly IPublishEndpoint? _publish;
    private readonly IObjectListing? _listing;
    private readonly ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser? _historyEraser;

    public ProjectAvatarDeletion(
        InferenceApiDbContext db,
        IFileStorageService storage,
        IAvatarVendorAssetDeleter vendor,
        ILogger<ProjectAvatarDeletion> logger,
        IPublishEndpoint? publish = null,
        IObjectListing? listing = null,
        ReelBolt.Inference.Api.Services.History.IProjectHistoryEraser? historyEraser = null)
    {
        _historyEraser = historyEraser;
        _listing = listing;
        _db = db;
        _storage = storage;
        _vendor = vendor;
        _logger = logger;
        _publish = publish;
    }

    /// <summary>
    /// Deletes avatar <paramref name="avatarId"/> of <paramref name="projectId"/>. Null when it no longer exists (a
    /// concurrent delete won).
    /// </summary>
    public async Task<AvatarDeleteReport?> DeleteAsync(
        Guid projectId, Guid avatarId, Guid organizationId, Guid? actorUserId, AvatarDeleteCause cause, CancellationToken ct)
    {
        // A PERSON's variants show the same person under the same consent, so they go first, each through this same path
        // (files, copies, vendor-side cleanup, events), then the original. They are never left behind as usable
        // avatars. An object's variants are not touched here: they are ungrouped below, as before.
        List<Guid> personVariants = await _db.ProjectAvatars.AsNoTracking()
            .Where(a => a.VariantOfAvatarId == avatarId && a.ProjectId == projectId && a.SubjectKind == AvatarSubjectKind.Person)
            .Select(a => a.Id)
            .ToListAsync(ct);
        AvatarDeleteReport? total = null;
        foreach (Guid variantId in personVariants)
        {
            AvatarDeleteReport? one = await DeleteAsync(projectId, variantId, organizationId, actorUserId, cause, ct);
            if (one is not null) total = Combine(total, one);
        }

        AvatarDeleteReport? own = await DeleteWithRetriesAsync(projectId, avatarId, organizationId, actorUserId, cause, ct);
        return own is null ? null : total is null ? own : Combine(total, own);
    }

    /// <summary>The sum of two reports (the original's own and its variants'): counts add, the vendor outcomes join.</summary>
    private static AvatarDeleteReport Combine(AvatarDeleteReport? a, AvatarDeleteReport b) => a is null ? b : b with
    {
        ReferencesDeleted = a.ReferencesDeleted + b.ReferencesDeleted,
        ReferencesFailed = a.ReferencesFailed + b.ReferencesFailed,
        ReferencesKept = a.ReferencesKept + b.ReferencesKept,
        VendorAssets = [.. a.VendorAssets, .. b.VendorAssets],
        StillsDeleted = a.StillsDeleted + b.StillsDeleted,
        StillsFailed = a.StillsFailed < 0 || b.StillsFailed < 0 ? -1 : a.StillsFailed + b.StillsFailed
    };

    private async Task<AvatarDeleteReport?> DeleteWithRetriesAsync(
        Guid projectId, Guid avatarId, Guid organizationId, Guid? actorUserId, AvatarDeleteCause cause, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await DeleteOnceAsync(projectId, avatarId, organizationId, actorUserId, cause, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Somebody changed the avatar between our read and our delete: read it again and redo the plan.
                _db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<AvatarDeleteReport?> DeleteOnceAsync(
        Guid projectId, Guid avatarId, Guid organizationId, Guid? actorUserId, AvatarDeleteCause cause, CancellationToken ct)
    {
        ProjectAvatar? avatar = await _db.ProjectAvatars
            .FirstOrDefaultAsync(a => a.Id == avatarId && a.ProjectId == projectId, ct);
        if (avatar is null) return null;

        List<ProjectAvatarReference> references = await _db.ProjectAvatarReferences
            .Where(r => r.AvatarId == avatarId)
            .ToListAsync(ct);

        // ---- 1. Vendor-side copies, best effort, one outcome each. ----
        List<ProjectAvatarVendorAsset> assets = await _db.ProjectAvatarVendorAssets
            .Where(v => v.AvatarId == avatarId)
            .ToListAsync(ct);
        List<AvatarVendorAssetReport> vendorReports = await DeleteVendorAssetsAsync(assets, ct);

        // ---- 2. The files this avatar alone holds. ----
        HashSet<Guid> candidateIds = [.. references.Select(r => r.ProjectFileId)];
        Guid? documentId = avatar.ConsentDocumentFileId;
        if (documentId is { } doc) candidateIds.Add(doc);

        HashSet<Guid> usedElsewhere = [.. await _db.ProjectAvatarReferences
            .Where(r => r.AvatarId != avatarId && candidateIds.Contains(r.ProjectFileId))
            .Select(r => r.ProjectFileId)
            .ToListAsync(ct)];
        usedElsewhere.UnionWith(await _db.ProjectAvatars
            .Where(a => a.Id != avatarId && a.ProjectId == projectId && a.ConsentDocumentFileId != null
                        && candidateIds.Contains(a.ConsentDocumentFileId.Value))
            .Select(a => a.ConsentDocumentFileId!.Value)
            .ToListAsync(ct));

        List<ProjectFile> files = await _db.ProjectFiles
            .Where(f => f.ProjectId == projectId && candidateIds.Contains(f.Id))
            .ToListAsync(ct);
        HashSet<Guid> held = await OutputGate.HeldFileIdsAsync(_db, projectId, files, ct);
        // An object's files are ordinary project files: none is deleted with it. The one exception is a linked import of
        // a workspace avatar library item: the files an import COPIED from the library (told apart by their
        // source revision) are its own and go with it; files the project already had stay.
        bool isObject = avatar.SubjectKind == AvatarSubjectKind.Object;
        Guid? libraryItemId = avatar.LibraryItemId;
        List<ProjectFile> deletable =
        [
            .. files.Where(f => !usedElsewhere.Contains(f.Id) && !held.Contains(f.Id) && !CsamScanGate.IsHeld(f)
                                && (!isObject || (libraryItemId is { } itemId && AvatarLibraryStorage.IsCopyOf(f, itemId))))
        ];

        HashSet<Guid> referenceFileIds = [.. references.Select(r => r.ProjectFileId)];
        int referencesKept = referenceFileIds.Count(id => deletable.All(f => f.Id != id));

        // ---- 2b. The stills made from the avatar (phase 3): bare objects under its stills folder, no rows. ----
        (List<string>? stillKeys, bool stillsListed) = await ListStillsAsync(projectId, avatarId, ct);
        bool documentDeletable = documentId is { } d && !referenceFileIds.Contains(d) && deletable.Any(f => f.Id == d);

        // ---- 3. Rows and durable marks in one transaction, then the objects. ----
        DateTime now = DateTime.UtcNow;
        List<StorageRetentionMark> marks = [.. deletable
            .Where(f => !string.IsNullOrEmpty(f.StorageKey))
            .Select(f => new StorageRetentionMark
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ProjectId = projectId,
                StorageKey = f.StorageKey,
                Category = f.Category,
                MarkedAt = now
            })];
        List<string> keys = [.. deletable.Select(f => f.StorageKey).Where(k => !string.IsNullOrEmpty(k))];
        List<StorageRetentionMark> stillMarks = await MarksForStillsAsync(stillKeys ?? [], projectId, organizationId, now, ct);

        // The audit trail, in the same transaction as the delete it records (counts and outcome words only).
        foreach (AvatarVendorAssetReport vendorReport in vendorReports)
        {
            ProjectAvatarEvents.Add(_db, avatar, organizationId, AvatarEventKind.VendorAssetDeleted, actorUserId,
                new Dictionary<string, object> { ["outcome"] = vendorReport.Outcome });
        }

        Dictionary<string, object> deleted = new()
        {
            ["cause"] = cause switch
            {
                AvatarDeleteCause.Retention => avatar.ConsentRevokedAt is not null ? "revoked_grace_ended" : "retention_expired",
                AvatarDeleteCause.LibraryItem => "library_item_deleted",
                _ => "user"
            },
            ["references_deleted"] = deletable.Count(f => referenceFileIds.Contains(f.Id)),
            ["references_kept"] = referencesKept,
            ["consent_document_deleted"] = documentDeletable,
            ["vendor_assets"] = vendorReports.Count,
            ["stills"] = stillKeys?.Count ?? 0
        };
        if (isObject) deleted["subject_kind"] = "object";
        if (libraryItemId is not null) deleted["from_library"] = true;
        ProjectAvatarEvents.Add(_db, avatar, organizationId,
            cause == AvatarDeleteCause.Retention ? AvatarEventKind.RetentionDeleted : AvatarEventKind.Deleted, actorUserId, deleted);

        _db.StorageRetentionMarks.AddRange(marks);
        _db.StorageRetentionMarks.AddRange(stillMarks);
        _db.ProjectAvatarVendorAssets.RemoveRange(assets);
        _db.ProjectAvatarReferences.RemoveRange(references);
        // Its variants stay, ungrouped (docs/design/avatar-objects.md "Avatar variants"). The foreign key says the same
        // (ON DELETE SET NULL); written here too so the rows this unit of work holds agree with the database.
        foreach (ProjectAvatar variant in await _db.ProjectAvatars.Where(a => a.VariantOfAvatarId == avatarId).ToListAsync(ct))
            variant.VariantOfAvatarId = null;
        _db.ProjectAvatars.Remove(avatar);
        _db.ProjectFiles.RemoveRange(deletable);
        if (keys.Count > 0)
        {
            _db.OutputModerationVerdicts.RemoveRange(await _db.OutputModerationVerdicts
                .Where(v => v.ProjectId == projectId && keys.Contains(v.StorageKey)).ToListAsync(ct));
        }

        await _db.SaveChangesAsync(ct);

        HashSet<Guid> objectsFailed = await DeleteObjectsAsync(projectId, deletable, marks);
        await EraseHistoryAsync(projectId, deletable);
        await PublishIndexDeletesAsync(projectId, deletable);
        (int stillsDeleted, int stillsFailed) = await DeleteStillsAsync(projectId, stillKeys ?? [], stillMarks);
        if (!stillsListed) stillsFailed = -1;

        int referencesDeleted = deletable.Count(f => referenceFileIds.Contains(f.Id) && !objectsFailed.Contains(f.Id));
        int referencesFailed = deletable.Count(f => referenceFileIds.Contains(f.Id) && objectsFailed.Contains(f.Id));
        bool documentDeleted = documentDeletable && !objectsFailed.Contains(documentId!.Value);

        _logger.LogInformation(
            "Deleted avatar {AvatarId} of project {ProjectId} ({Cause}): references deleted={Deleted} failed={Failed} kept={Kept}, vendor copies={Vendor}, consent document deleted={Document}, stills deleted={Stills} failed={StillsFailed}",
            avatarId, projectId, cause, referencesDeleted, referencesFailed, referencesKept, vendorReports.Count, documentDeleted, stillsDeleted, stillsFailed);

        return new AvatarDeleteReport(referencesDeleted, referencesFailed, referencesKept, vendorReports, documentDeleted, stillsDeleted, stillsFailed);
    }

    /// <summary>
    /// The objects under the avatar's stills folder (<see cref="AvatarStillStorage.ObjectPrefix"/>), or (null, false) when
    /// the store cannot list or the listing failed: the delete goes on, and the storage sweep's orphan pass removes a still
    /// whose avatar is gone.
    /// </summary>
    private async Task<(List<string>? Keys, bool Listed)> ListStillsAsync(Guid projectId, Guid avatarId, CancellationToken ct)
    {
        if (_listing is null) return (null, false);
        try
        {
            string prefix = AvatarStillStorage.ObjectPrefix(projectId, avatarId);
            IReadOnlyList<StoredObjectEntry> listed = await _listing.ListAsync(
                projectId, AvatarStillStorage.Category + "/" + AvatarStillStorage.RelativeDirectory(avatarId), ct);
            return ([.. listed.Select(e => e.StorageKey).Where(k => k.StartsWith(prefix, StringComparison.Ordinal))], true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the stills of avatar {AvatarId}; the storage sweep removes them", avatarId);
            return (null, false);
        }
    }

    /// <summary>A durable mark per still not marked yet, written in the delete's own transaction.</summary>
    private async Task<List<StorageRetentionMark>> MarksForStillsAsync(
        List<string> stillKeys, Guid projectId, Guid organizationId, DateTime now, CancellationToken ct)
    {
        if (stillKeys.Count == 0) return [];
        HashSet<string> already = [.. await _db.StorageRetentionMarks.IgnoreQueryFilters()
            .Where(m => stillKeys.Contains(m.StorageKey))
            .Select(m => m.StorageKey)
            .ToListAsync(ct)];
        return [.. stillKeys.Where(k => !already.Contains(k)).Select(k => new StorageRetentionMark
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ProjectId = projectId,
            StorageKey = k,
            Category = AvatarStillStorage.Category,
            MarkedAt = now
        })];
    }

    /// <summary>Deletes the stills' objects after the commit; a still that stays keeps its mark for the storage sweep.</summary>
    private async Task<(int Deleted, int Failed)> DeleteStillsAsync(Guid projectId, List<string> stillKeys, List<StorageRetentionMark> marks)
    {
        if (stillKeys.Count == 0) return (0, 0);
        Dictionary<string, StorageRetentionMark> byKey = marks.ToDictionary(m => m.StorageKey, StringComparer.Ordinal);
        List<StorageRetentionMark> done = [];
        int deleted = 0, failed = 0;
        foreach (string key in stillKeys)
        {
            try
            {
                // Uncancellable: the avatar row is already gone, so the likeness made from it must follow.
                await _storage.DeleteAsync(projectId, key, CancellationToken.None);
                deleted++;
                if (byKey.TryGetValue(key, out StorageRetentionMark? mark)) done.Add(mark);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex, "Could not remove a still of a deleted avatar; its retention mark makes the storage sweep retry it");
            }
        }

        if (done.Count > 0)
        {
            _db.StorageRetentionMarks.RemoveRange(done);
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        return (deleted, failed);
    }

    private async Task<List<AvatarVendorAssetReport>> DeleteVendorAssetsAsync(List<ProjectAvatarVendorAsset> assets, CancellationToken ct)
    {
        List<AvatarVendorAssetReport> reports = new(assets.Count);
        if (assets.Count == 0) return reports;

        Guid[] capabilityIds = [.. assets.Where(a => a.ModelCapabilityId is not null).Select(a => a.ModelCapabilityId!.Value).Distinct()];
        Dictionary<Guid, string> providers = capabilityIds.Length == 0
            ? []
            : await _db.ModelCapabilities.IgnoreQueryFilters()
                .Where(c => capabilityIds.Contains(c.Id))
                .Select(c => new { c.Id, Kind = c.Model.Provider.Kind })
                .ToDictionaryAsync(c => c.Id, c => c.Kind.ToString(), ct);

        foreach (ProjectAvatarVendorAsset asset in assets)
        {
            AvatarVendorAssetDeleteOutcome outcome;
            if (asset.DeletedAt is not null && asset.DeleteOutcome is { } earlier)
            {
                outcome = earlier;
            }
            else
            {
                try
                {
                    outcome = await _vendor.DeleteAsync(asset, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Logged with the vendor's id for the operator; the avatar row is deleted anyway.
                    _logger.LogWarning(ex, "Vendor deletion of avatar asset {AssetId} ({RemoteAssetId}) failed", asset.Id, asset.RemoteAssetId);
                    outcome = AvatarVendorAssetDeleteOutcome.VendorError;
                }

                asset.DeletedAt = DateTime.UtcNow;
                asset.DeleteOutcome = outcome;
                if (outcome is not (AvatarVendorAssetDeleteOutcome.Deleted or AvatarVendorAssetDeleteOutcome.AlreadyGone))
                {
                    _logger.LogWarning(
                        "Avatar {AvatarId}: vendor copy {RemoteAssetId} was not deleted ({Outcome}); follow up with the vendor",
                        asset.AvatarId, asset.RemoteAssetId, outcome);
                }
            }

            string? provider = asset.ModelCapabilityId is { } cap && providers.TryGetValue(cap, out string? kind) ? kind : null;
            reports.Add(new AvatarVendorAssetReport(provider, AvatarVendorAssetOutcomeWords.Of(outcome)));
        }

        return reports;
    }

    /// <summary>Deletes the committed rows' objects; answers the file ids whose object is still there.</summary>
    private async Task<HashSet<Guid>> DeleteObjectsAsync(Guid projectId, List<ProjectFile> files, List<StorageRetentionMark> marks)
    {
        HashSet<Guid> failed = [];
        if (files.Count == 0) return failed;

        Dictionary<string, StorageRetentionMark> byKey = [];
        foreach (StorageRetentionMark mark in marks) byKey.TryAdd(mark.StorageKey, mark);
        List<StorageRetentionMark> done = [];
        foreach (ProjectFile file in files)
        {
            if (!byKey.TryGetValue(file.StorageKey, out StorageRetentionMark? mark)) continue;
            try
            {
                // Uncancellable: the rows are already gone, so the objects must follow.
                await _storage.DeleteAsync(projectId, file.StorageKey, CancellationToken.None);
                done.Add(mark);
            }
            catch (Exception ex)
            {
                failed.Add(file.Id);
                _logger.LogWarning(ex, "Could not remove the object of avatar reference file {FileId}; its retention mark makes the storage sweep retry it", file.Id);
            }
        }

        if (done.Count > 0)
        {
            _db.StorageRetentionMarks.RemoveRange(done);
            await _db.SaveChangesAsync(CancellationToken.None);
        }

        return failed;
    }

    /// <summary>
    /// The reference and consent files are gone for good, so their protected history copies go too: otherwise a
    /// restore could bring a deleted or revoked avatar's face or release back. Best effort per file, logged.
    /// </summary>
    private async Task EraseHistoryAsync(Guid projectId, List<ProjectFile> files)
    {
        if (_historyEraser is null) return;
        foreach (ProjectFile file in files)
        {
            try
            {
                await _historyEraser.EraseFileAsync(projectId, file.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not erase the history copies of deleted avatar file {FileId}", file.Id);
            }
        }
    }

    private async Task PublishIndexDeletesAsync(Guid projectId, List<ProjectFile> files)
    {
        if (_publish is null) return;
        foreach (ProjectFile file in files)
        {
            try
            {
                await _publish.Publish(new ProjectFileIndexingRequested
                {
                    ProjectId = projectId,
                    FileId = file.Id,
                    Operation = "Delete",
                    RequestedAt = DateTime.UtcNow
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not queue the index removal of deleted avatar reference file {FileId}", file.Id);
            }
        }
    }
}
