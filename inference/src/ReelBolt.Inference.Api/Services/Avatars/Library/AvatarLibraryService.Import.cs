using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

public sealed partial class AvatarLibraryService
{
    private const string UserFilesCategory = "userFiles";

    /// <summary>One library reference and the project file its copy becomes.</summary>
    private sealed record PlannedImport(AvatarLibraryReference Source, Guid FileId, string FileName, string StorageFileName, string StorageKey);

    /// <summary>
    /// Imports library item <paramref name="itemId"/> into <paramref name="project"/>: a linked project avatar
    /// (<see cref="ProjectAvatar.LibraryItemId"/> set) with its own copies of the item's files. The caller has checked
    /// that the user has Edit on the project and a verified email.
    /// </summary>
    /// <remarks>
    /// In order, nothing written on a refusal: the item exists in the project's organization and its kind is switched
    /// on (else not found); the kind's plan feature (<c>402</c>); a project with quarantined content takes no avatar
    /// (<c>409 PROJECT_CONTENT_QUARANTINED</c>); the item is usable (<c>409 LIBRARY_ITEM_NOT_USABLE</c>); the project
    /// does not hold an import of it yet (<c>409 AVATAR_ALREADY_IMPORTED</c>); the item's import cap
    /// (<c>409 LIBRARY_IMPORT_LIMIT</c>); the project's per-kind limit and live names (<c>409 AVATAR_LIMIT_REACHED</c> /
    /// <c>AVATAR_NAME_TAKEN</c>); the storage quota (<c>413 STORAGE_QUOTA_EXCEEDED</c>). Then the item's objects are
    /// copied inside the store to NEW project keys under <c>userFiles/avatars/{name}/</c> and registered as ordinary
    /// project files (marked <c>avatar_reference</c> for a person, unmarked for an object, like the files of any avatar
    /// of that kind), the reference rows carry the item's digests, roles, order and probe facts, and the consent
    /// columns are the item's. The files then take the usual ingest path (CSAM scan, moderation, derivatives); they are
    /// never pre-cleared.
    /// <para>
    /// Variants (docs/design/avatar-objects.md "Avatar variants", "Library"): an item that is a variant carries its label
    /// and description to the import, and lands under the project's import of its original when there is one (then the
    /// project's variant rules apply in place of the per-kind limit: <c>409 VARIANT_LIMIT_REACHED</c> /
    /// <c>VARIANT_LABEL_TAKEN</c>); importing an original groups the imports of its variants the project already holds,
    /// in the same save.
    /// </para>
    /// </remarks>
    public async Task<AvatarLibraryResult<ProjectAvatar>> ImportAsync(Project project, Guid itemId, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        Guid organizationId = project.OrganizationId;

        AvatarLibraryItem? item = await FindAsync(organizationId, itemId, tracked: false, ct);
        if (item is null) return AvatarLibraryResult<ProjectAvatar>.Missing();

        if (await PlanRefusalAsync(organizationId, item.SubjectKind, ct) is { } notEntitled) return Refuse(notEntitled);
        ProjectOutputHoldState hold = await OutputGate.ReadAsync(_db, project.Id, ct);
        if (project.ContentQuarantinedAt is not null || hold.IsActive)
            return Refuse(AvatarRefusal.Conflict("PROJECT_CONTENT_QUARANTINED", "Avatars cannot be created or changed in this project."));

        List<AvatarLibraryReference> references = await _db.AvatarLibraryReferences.AsNoTracking()
            .Where(r => r.LibraryItemId == itemId)
            .OrderBy(r => r.Ordinal)
            .ToListAsync(ct);
        long bytes = references.Sum(r => Math.Max(0, r.SizeBytes));
        if (await ImportRefusalAsync(project, item, await ImportGroupingAsync(project, item, ct), bytes, ct) is { } early)
            return Refuse(early);

        // ---- Copy the item's objects to new project keys (outside the guard: no lock is held while bytes move). ----
        string directory = AvatarLibraryStorage.ProjectDirectory + "/" + SafeSegment(item.Name, item.Id);
        List<PlannedImport> plan = Plan(project.Id, directory, references);
        List<string> copied = [];
        bool committed = false;
        try
        {
            foreach (PlannedImport planned in plan)
            {
                // A row never vouches for a foreign key: the source must be an object of THIS item.
                if (AvatarLibraryStorage.Parse(planned.Source.StorageKey) != (organizationId, itemId)) return Refuse(Damaged());
                try
                {
                    await _store.CopyIntoProjectAsync(planned.Source.StorageKey, project.Id, planned.StorageKey, ct);
                    copied.Add(planned.StorageKey);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not copy a file of avatar library item {ItemId} into project {ProjectId}", itemId, project.Id);
                    return Refuse(Damaged());
                }
            }

            DateTime now = DateTime.UtcNow;
            bool person = item.SubjectKind == AvatarSubjectKind.Person;
            List<ProjectFile> files = [.. plan.Select(p => new ProjectFile
            {
                Id = p.FileId,
                ProjectId = project.Id,
                OriginalFileName = p.FileName,
                OriginalPath = directory + "/" + p.FileName,
                DirectoryPath = directory,
                Category = UserFilesCategory,
                StorageKey = p.StorageKey,
                StorageFileName = p.StorageFileName,
                StorageBucket = _storage.BucketName,
                StoragePrefix = ProjectFilePath.BuildStoragePrefix(project.Id, UserFilesCategory),
                MimeType = p.Source.MimeType,
                SizeBytes = p.Source.SizeBytes,
                // A person's reference files are badged in the media pool; an object's stay ordinary media.
                SourceKind = person ? ProjectAvatarReference.ProjectFileSourceKind : null,
                // How the delete routine tells this import's copies from files the project already had.
                SourceRevision = AvatarLibraryStorage.SourceRevision(itemId),
                // Reference media of an avatar are not project knowledge: never summarized, never indexed.
                SummaryStatus = SummaryStatus.Done,
                IndexingStatus = FileIndexingStatus.NotIndexed,
                UploadedAt = now
            })];

            ProjectAvatar? avatar = null;
            AvatarRefusal? refused = null;
            bool gone = false;
            await using (OrganizationStorageGuard guard = await _storageLock.AcquireAsync(_db, organizationId, ct))
            {
                // Read again under the guard: a revoke, a rename or a delete that ran meanwhile is what gets copied.
                AvatarLibraryItem? current = await ProjectStillInAsync(project.Id, organizationId, ct)
                    ? await FindAsync(organizationId, itemId, tracked: false, ct)
                    : null;
                ImportGrouping? grouping = current is null ? null : await ImportGroupingAsync(project, current, ct);
                if (current is null)
                {
                    gone = true;
                }
                else if ((refused = await ImportRefusalAsync(project, current, grouping, bytes, ct)) is null)
                {
                    var created = new ProjectAvatar
                    {
                        Id = Guid.NewGuid(),
                        ProjectId = project.Id,
                        SubjectKind = current.SubjectKind,
                        LibraryItemId = itemId,
                        CreatedAt = now,
                        CreatedByUserId = userId,
                        UpdatedAt = now
                    };
                    avatar = created;
                    CopyConsent(current, created);
                    // A variant item lands under the project's import of its original when there is one.
                    ApplyVariantFields(current, grouping, created);
                    _db.ProjectFiles.AddRange(files);
                    _db.ProjectAvatars.Add(created);
                    // An original groups the imports of its variants this project already holds, in the same save.
                    (int variantsGrouped, int variantsNotGrouped) = await GroupImportedVariantsAsync(project, current, created, now, ct);
                    _db.ProjectAvatarReferences.AddRange(plan.Select(p => new ProjectAvatarReference
                    {
                        Id = Guid.NewGuid(),
                        AvatarId = created.Id,
                        ProjectFileId = p.FileId,
                        Kind = p.Source.Kind,
                        Role = p.Source.Role,
                        Ordinal = p.Source.Ordinal,
                        ContentSha256 = p.Source.ContentSha256,
                        Width = p.Source.Width,
                        Height = p.Source.Height,
                        DurationMs = p.Source.DurationMs,
                        AttachedAt = now
                    }));
                    ProjectAvatarEvents.Add(_db, created, organizationId, AvatarEventKind.LibraryImported, userId,
                        new Dictionary<string, object>(ReferenceCounts(references))
                        {
                            ["via"] = "import",
                            ["subject_kind"] = KindWord(current.SubjectKind),
                            ["variant"] = created.VariantOfAvatarId is not null,
                            ["variants_grouped"] = variantsGrouped,
                            ["variants_not_grouped"] = variantsNotGrouped
                        });

                    try
                    {
                        await _db.SaveChangesAsync(ct);
                        await guard.CommitAsync(ct);
                        committed = true;
                    }
                    catch (DbUpdateException)
                    {
                        // The unique index on (project, item), on live names or on a variant's live label: a concurrent
                        // import or create won.
                        _db.ChangeTracker.Clear();
                        avatar = null;
                    }
                }
            }

            if (gone) return AvatarLibraryResult<ProjectAvatar>.Missing();
            if (refused is not null) return Refuse(refused);
            if (avatar is null)
            {
                AvatarLibraryItem? current = await FindAsync(organizationId, itemId, tracked: false, CancellationToken.None);
                if (current is null) return AvatarLibraryResult<ProjectAvatar>.Missing();
                return Refuse(await ImportRefusalAsync(
                    project, current, await ImportGroupingAsync(project, current, CancellationToken.None), bytes, CancellationToken.None) ?? Changed());
            }

            _logger.LogInformation(
                "Imported avatar library item {ItemId} into project {ProjectId} as avatar {AvatarId} ({Files} files)",
                itemId, project.Id, avatar.Id, files.Count);
            await RunIngestAsync(files, userId, ct);
            return AvatarLibraryResult<ProjectAvatar>.Ok(avatar);
        }
        finally
        {
            // Every exit but a committed import removes the copies: no project object is left without its row.
            if (!committed) await DeleteProjectObjectsAsync(project.Id, copied);
        }

        static AvatarLibraryResult<ProjectAvatar> Refuse(AvatarRefusal refusal) => AvatarLibraryResult<ProjectAvatar>.Refused(refusal);
    }

    /// <summary>
    /// Everything that can refuse an import once the item is known; checked before the copy and again under the guard.
    /// An import that lands under the project's import of its original (<paramref name="grouping"/>) is a variant: it
    /// is not counted toward the project's per-kind limit, and the project's variant rules apply instead
    /// (<c>409 VARIANT_LIMIT_REACHED</c> / <c>VARIANT_LABEL_TAKEN</c>), so a clash is refused in words, never dropped.
    /// </summary>
    private async Task<AvatarRefusal?> ImportRefusalAsync(Project project, AvatarLibraryItem item, ImportGrouping? grouping, long bytes, CancellationToken ct)
    {
        if (item.UnusableReason(DateTime.UtcNow) is { } reason)
        {
            return AvatarRefusal.Conflict("LIBRARY_ITEM_NOT_USABLE",
                "This library item cannot be used right now, so it cannot be imported.",
                new Dictionary<string, object?> { ["reason"] = reason });
        }

        // A person variant follows its original: revoked or expired there, it cannot be imported either.
        if (item.SubjectKind == AvatarSubjectKind.Person && item.VariantOfItemId is { } originalId)
        {
            AvatarLibraryItem? original = await _db.AvatarLibraryItems.AsNoTracking()
                .FirstOrDefaultAsync(i => i.Id == originalId && i.OrganizationId == item.OrganizationId, ct);
            if (original?.UnusableReason(DateTime.UtcNow) is { } originalReason)
            {
                return AvatarRefusal.Conflict("LIBRARY_ITEM_NOT_USABLE",
                    "This library item cannot be used right now, so it cannot be imported.",
                    new Dictionary<string, object?> { ["reason"] = originalReason });
            }
        }

        // A copy whose bytes a CSAM or moderation verdict holds (on any file of the organization with the same digest).
        List<AvatarLibraryReference> own = await _db.AvatarLibraryReferences.AsNoTracking().Where(r => r.LibraryItemId == item.Id).ToListAsync(ct);
        AvatarLibraryHoldGate.HoldState hold = await AvatarLibraryHoldGate.ReadAsync(
            _db, item.OrganizationId, own, _logger, ct);
        if (hold.HoldsAny(own))
        {
            return AvatarRefusal.Conflict("LIBRARY_ITEM_NOT_USABLE",
                "This library item cannot be used right now, so it cannot be imported.",
                new Dictionary<string, object?> { ["reason"] = ProjectAvatar.ReasonReferenceHeld });
        }

        Guid? existing = await _db.ProjectAvatars.AsNoTracking()
            .Where(a => a.ProjectId == project.Id && a.LibraryItemId == item.Id)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return AvatarRefusal.Conflict("AVATAR_ALREADY_IMPORTED", "This project already holds this library item.",
                new Dictionary<string, object?> { ["avatarId"] = existing });
        }

        if (await _db.ProjectAvatars.CountAsync(a => a.LibraryItemId == item.Id, ct) >= AvatarLibraryLimits.MaxImportsPerItem)
        {
            return AvatarRefusal.Conflict("LIBRARY_IMPORT_LIMIT",
                $"A library item can be used in at most {AvatarLibraryLimits.MaxImportsPerItem} projects. Remove it from a project that no longer needs it first.");
        }

        bool isObject = item.SubjectKind == AvatarSubjectKind.Object;
        int max = isObject ? ProjectAvatarLimits.MaxObjectsPerProject : ProjectAvatarLimits.MaxAvatarsPerProject;
        if (grouping is not null)
        {
            if (await AvatarVariantRules.LimitRefusalAsync(_db, grouping.OriginalAvatarId, ct) is { } variantsFull) return variantsFull;
            if (await AvatarVariantRules.LabelTakenAsync(_db, grouping.OriginalAvatarId, grouping.Label, null, ct))
                return AvatarVariantRules.LabelTaken(grouping.Label);
        }
        // Variants are not counted toward the per-kind limit, as on every other way in.
        else if (await _db.ProjectAvatars.CountAsync(
                     a => a.ProjectId == project.Id && a.SubjectKind == item.SubjectKind && a.VariantOfAvatarId == null, ct) >= max)
        {
            return AvatarRefusal.Conflict("AVATAR_LIMIT_REACHED",
                isObject
                    ? $"A project can hold at most {max} object avatars. Delete one first."
                    : $"A project can hold at most {max} avatars. Delete one first.");
        }

        if (await _db.ProjectAvatars.AnyAsync(a => a.ProjectId == project.Id && a.ConsentRevokedAt == null && a.Name == item.Name, ct))
            return AvatarRefusal.Conflict("AVATAR_NAME_TAKEN", $"This project already has an avatar called \"{item.Name}\".");

        return await QuotaRefusalAsync(project.OrganizationId, bytes, ct);
    }

    private static AvatarRefusal Damaged() =>
        AvatarRefusal.Conflict("LIBRARY_ITEM_NOT_USABLE",
            "A file of this library item could not be read, so it cannot be imported.",
            new Dictionary<string, object?> { ["reason"] = ProjectAvatar.ReasonReferenceMissing });

    /// <summary>
    /// Where each reference's copy goes: a NEW file id and the ordinary project key built from it
    /// (<c>projects/{p}/userFiles/avatars/{name}/{fileId}.{ext}</c>), so no existing object can be overwritten. Two
    /// references that share a file name get distinct display names.
    /// </summary>
    private static List<PlannedImport> Plan(Guid projectId, string directory, List<AvatarLibraryReference> references)
    {
        HashSet<string> used = new(StringComparer.OrdinalIgnoreCase);
        List<PlannedImport> plan = new(references.Count);
        foreach (AvatarLibraryReference reference in references)
        {
            Guid fileId = Guid.NewGuid();
            string name = SafeFileName(reference.FileName, reference.Id);
            if (name.Length > 200) name = name[..200];
            string unique = name;
            for (int n = 2; !used.Add(unique); n++)
            {
                string extension = Path.GetExtension(name);
                unique = Path.GetFileNameWithoutExtension(name) + "-" + n + extension;
            }

            string storageFileName = fileId.ToString("D") + "." + AvatarLibraryStorage.ExtensionOf(reference.FileName);
            string key = ProjectFilePath.BuildStorageKey(projectId, UserFilesCategory, directory, storageFileName);
            plan.Add(new PlannedImport(reference, fileId, unique, storageFileName, key));
        }

        return plan;
    }

    /// <summary>
    /// A single, safe folder name from a display name: letters, digits, spaces, <c>-</c> and <c>_</c> are kept, anything
    /// else becomes <c>-</c>; at most 80 characters; never empty, <c>.</c> or <c>..</c> (the item id stands in).
    /// </summary>
    private static string SafeSegment(string? name, Guid fallbackId)
    {
        char[] cleaned = [.. (name ?? string.Empty).Trim().Select(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' ? c : '-')];
        string segment = new string(cleaned).Trim(' ', '-');
        if (segment.Length > 80) segment = segment[..80].Trim(' ', '-');
        return segment.Length == 0 ? fallbackId.ToString("N") : segment;
    }

    /// <summary>
    /// The ingest path of the copied files, after their rows are committed: the CSAM scan first (a match quarantines the
    /// copy and nothing else touches it), then the moderation queue and, for a video or a recording, the derivative
    /// request (its probe and filmstrip). Best effort: the import already succeeded, and each sweeper offers a row it
    /// has not seen again. No summary, no index entry and no "file added" notification: these are an avatar's files.
    /// </summary>
    private async Task RunIngestAsync(List<ProjectFile> files, Guid userId, CancellationToken ct)
    {
        foreach (ProjectFile file in files)
        {
            try
            {
                if (_csam is not null)
                {
                    CsamScanOutcome outcome = await _csam.ScanAsync(file, userId, ct);
                    if (outcome.Status == CsamScanStatus.Quarantined) continue;
                }

                if (CsamScanGate.AllowsModeration(file)) _moderation?.TryQueue(file.Id);
                if (_publish is not null && UploadDerivativeRequest.IsMediaMimeType(file.MimeType)
                    && (_csam is null || CsamScanGate.AllowsDerivatives(file)))
                {
                    await UploadDerivativeRequest.PublishAsync(_publish, _storage, analysis: null, file, file.MimeType, _logger, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The ingest steps of imported avatar file {FileId} did not finish; the sweepers offer it again", file.Id);
            }
        }
    }

    private async Task DeleteProjectObjectsAsync(Guid projectId, List<string> keys)
    {
        foreach (string key in keys)
        {
            try
            {
                // Uncancellable: no row names these copies.
                await _storage.DeleteAsync(projectId, key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not remove a copied avatar file after a refused import; the storage sweep removes an object no row names");
            }
        }
    }
}
