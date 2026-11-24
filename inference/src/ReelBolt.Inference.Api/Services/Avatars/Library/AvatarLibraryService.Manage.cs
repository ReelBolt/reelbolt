using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

public sealed partial class AvatarLibraryService
{
    /// <summary>
    /// Renames or re-describes an item; every import follows in the same save. A null field is left as it is,
    /// <paramref name="description"/> <c>""</c> clears it. Refused, with nothing written: an invalid name or a too long
    /// description (<c>400</c>), a name another live item uses (<c>409 LIBRARY_NAME_TAKEN</c>), or a name another live
    /// avatar already uses in one of the projects that import the item (<c>409 AVATAR_NAME_TAKEN</c>: an import that
    /// could not take the name would no longer follow its item).
    /// </summary>
    public async Task<AvatarLibraryResult<AvatarLibraryItemView>> UpdateAsync(
        Guid organizationId, Guid itemId, string? name, string? description, Guid userId, CancellationToken ct)
    {
        string? newName = name?.Trim();
        if (newName is not null && (newName.Length == 0 || newName.Length > ProjectAvatarLimits.MaxNameLength))
            return Refused(AvatarRefusal.BadRequest("name_invalid", $"Give it a name of at most {ProjectAvatarLimits.MaxNameLength} characters."));
        if (description is { Length: > ProjectAvatarLimits.MaxDescriptionLength })
            return Refused(AvatarRefusal.BadRequest("description_too_long", $"The description can be at most {ProjectAvatarLimits.MaxDescriptionLength} characters."));

        return await MutateAsync(organizationId, itemId, async (item, imports) =>
        {
            if (newName is not null && !string.Equals(newName, item.Name, StringComparison.Ordinal))
            {
                if (item.ConsentRevokedAt is null)
                {
                    if (await NameTakenAsync(organizationId, newName, itemId, ct)) return NameTaken(newName);

                    Guid[] importIds = [.. imports.Select(a => a.Id)];
                    Guid[] projectIds = [.. imports.Select(a => a.ProjectId)];
                    int clashes = await _db.ProjectAvatars.CountAsync(a => projectIds.Contains(a.ProjectId) && !importIds.Contains(a.Id)
                                                                           && a.ConsentRevokedAt == null && a.Name == newName, ct);
                    if (clashes > 0)
                    {
                        return AvatarRefusal.Conflict("AVATAR_NAME_TAKEN",
                            $"A project that uses this item already has another avatar called \"{newName}\". Rename one of the two, then try again.",
                            new Dictionary<string, object?> { ["projects"] = clashes });
                    }
                }

                item.Name = newName;
            }

            if (description is not null)
                item.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();

            DateTime now = DateTime.UtcNow;
            item.UpdatedAt = now;
            foreach (ProjectAvatar import in imports)
            {
                import.Name = item.Name;
                import.Description = item.Description;
                import.UpdatedAt = now;
            }

            return null;
        }, ct);
    }

    /// <summary>
    /// Confirms an item again: the WORKSPACE statement is sent again (for a person with the adult tick and the keep-for
    /// days, whose period starts again from now) and the new consent stamp is copied to every import in the same save.
    /// No file changes: new files are a new item. An item whose consent was withdrawn cannot be confirmed again
    /// (<c>409 AVATAR_CONSENT_REVOKED</c>): withdrawn consent is final for that item.
    /// </summary>
    public async Task<AvatarLibraryResult<AvatarLibraryItemView>> ReconfirmAsync(
        Guid organizationId, Guid itemId, AvatarLibraryAttestation attestation, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        return await MutateAsync(organizationId, itemId, async (item, imports) =>
        {
            if (await PlanRefusalAsync(organizationId, item.SubjectKind, ct) is { } notEntitled) return notEntitled;
            if (item.ConsentRevokedAt is not null)
            {
                return AvatarRefusal.Conflict("AVATAR_CONSENT_REVOKED",
                    "Consent for this item was withdrawn. Save a new item from a project instead.");
            }

            if (AttestationRefusal(item.SubjectKind, attestation, out int retentionDays) is { } refused) return refused;

            DateTime now = DateTime.UtcNow;
            bool person = item.SubjectKind == AvatarSubjectKind.Person;
            item.ConsentStatement = WorkspaceStatement(item.SubjectKind, attestation.ConsentLocale)!;
            item.ConsentStatementVersion = WorkspaceStatementVersion(item.SubjectKind);
            item.ConsentLocale = attestation.ConsentLocale!;
            item.ConsentAttestedByUserId = userId;
            item.ConsentAttestedAt = now;
            item.UpdatedAt = now;
            if (person)
            {
                item.ConsentSubjectAdult = true;
                item.RetentionUntil = now.AddDays(retentionDays);
            }

            foreach (ProjectAvatar import in imports)
            {
                CopyConsent(item, import);
                import.UpdatedAt = now;
            }

            // A person's variants follow the original: the same statement, attestation and keep-for date, and their
            // imports carry it too. A variant withdrawn on its own stays withdrawn.
            if (person)
            {
                List<AvatarLibraryItem> variants = await _db.AvatarLibraryItems
                    .Where(i => i.OrganizationId == organizationId && i.VariantOfItemId == item.Id && i.ConsentRevokedAt == null).ToListAsync(ct);
                Guid[] variantIds = [.. variants.Select(v => v.Id)];
                List<ProjectAvatar> variantImports = variantIds.Length == 0 ? [] :
                    await _db.ProjectAvatars.Where(a => a.LibraryItemId != null && variantIds.Contains(a.LibraryItemId.Value)).ToListAsync(ct);
                foreach (AvatarLibraryItem variant in variants)
                {
                    variant.ConsentStatement = item.ConsentStatement;
                    variant.ConsentStatementVersion = item.ConsentStatementVersion;
                    variant.ConsentLocale = item.ConsentLocale;
                    variant.ConsentAttestedByUserId = userId;
                    variant.ConsentAttestedAt = now;
                    variant.ConsentSubjectAdult = true;
                    variant.RetentionUntil = item.RetentionUntil;
                    variant.UpdatedAt = now;
                    foreach (ProjectAvatar import in variantImports.Where(a => a.LibraryItemId == variant.Id))
                    {
                        CopyConsent(variant, import);
                        import.UpdatedAt = now;
                    }
                }
            }

            Dictionary<string, object> outcomes = new()
            {
                ["via"] = "app",
                ["subject_kind"] = KindWord(item.SubjectKind),
                ["locale"] = item.ConsentLocale,
                ["imports"] = imports.Count
            };
            if (person) outcomes["retention_days"] = retentionDays;
            ProjectAvatarEvents.AddForLibraryItem(_db, item, AvatarEventKind.LibraryReconfirmed, userId, outcomes, occurredAt: now);
            return null;
        }, ct);
    }

    /// <summary>
    /// Withdraws consent for a person's item: from this instant the item and EVERY import of it are unusable (the item
    /// and all its imports are stamped in the same save, so no project can keep using the person after the revoke
    /// returned). Idempotent: the first revoke's time and actor stay, and a later call still stamps an import that is
    /// not stamped yet. An object has no consent to withdraw (<c>409 AVATAR_NOT_A_PERSON</c>). Like the project revoke,
    /// a revoke that loses a race on a row version is retried from a fresh read, so the revoke always lands.
    /// </summary>
    public async Task<AvatarLibraryResult<AvatarLibraryItemView>> RevokeAsync(Guid organizationId, Guid itemId, Guid userId, CancellationToken ct)
    {
        return await MutateAsync(organizationId, itemId, async (item, imports) =>
        {
            if (item.SubjectKind == AvatarSubjectKind.Object)
            {
                return AvatarRefusal.Conflict("AVATAR_NOT_A_PERSON",
                    "An object has no consent to withdraw. Delete it instead.");
            }

            DateTime now = DateTime.UtcNow;
            bool first = item.ConsentRevokedAt is null;
            if (first)
            {
                item.ConsentRevokedAt = now;
                item.ConsentRevokedByUserId = userId;
                item.UpdatedAt = now;
            }

            int stamped = 0;
            foreach (ProjectAvatar import in imports.Where(a => a.ConsentRevokedAt is null))
            {
                import.ConsentRevokedAt = item.ConsentRevokedAt;
                import.ConsentRevokedByUserId = item.ConsentRevokedByUserId;
                import.UpdatedAt = now;
                ProjectAvatarEvents.Add(_db, import, organizationId, AvatarEventKind.ConsentRevoked, userId,
                    new Dictionary<string, object> { ["via"] = "library" }, occurredAt: now);
                stamped++;
            }

            if (first)
            {
                ProjectAvatarEvents.AddForLibraryItem(_db, item, AvatarEventKind.LibraryConsentRevoked, userId,
                    new Dictionary<string, object> { ["via"] = "app", ["imports"] = stamped }, occurredAt: now);
                _logger.LogInformation("Consent revoked for avatar library item {ItemId} and its {Imports} import(s)", itemId, stamped);
            }

            // The person's library variants are the same person: they are withdrawn with it, and so are their imports,
            // in this same save (idempotent: a variant or import already stamped keeps its own time and actor).
            List<AvatarLibraryItem> variants = await _db.AvatarLibraryItems
                .Where(i => i.OrganizationId == organizationId && i.VariantOfItemId == itemId).ToListAsync(ct);
            Guid[] variantIds = [.. variants.Select(v => v.Id)];
            List<ProjectAvatar> variantImports = variantIds.Length == 0 ? [] :
                await _db.ProjectAvatars.Where(a => a.LibraryItemId != null && variantIds.Contains(a.LibraryItemId.Value)).ToListAsync(ct);
            foreach (AvatarLibraryItem variant in variants)
            {
                int variantStamped = 0;
                bool variantFirst = variant.ConsentRevokedAt is null;
                if (variantFirst)
                {
                    variant.ConsentRevokedAt = item.ConsentRevokedAt;
                    variant.ConsentRevokedByUserId = item.ConsentRevokedByUserId;
                    variant.UpdatedAt = now;
                }

                foreach (ProjectAvatar import in variantImports.Where(a => a.LibraryItemId == variant.Id && a.ConsentRevokedAt is null))
                {
                    import.ConsentRevokedAt = variant.ConsentRevokedAt;
                    import.ConsentRevokedByUserId = variant.ConsentRevokedByUserId;
                    import.UpdatedAt = now;
                    ProjectAvatarEvents.Add(_db, import, organizationId, AvatarEventKind.ConsentRevoked, userId,
                        new Dictionary<string, object> { ["via"] = "library" }, occurredAt: now);
                    variantStamped++;
                }

                if (variantFirst)
                {
                    ProjectAvatarEvents.AddForLibraryItem(_db, variant, AvatarEventKind.LibraryConsentRevoked, userId,
                        new Dictionary<string, object> { ["via"] = "original", ["imports"] = variantStamped }, occurredAt: now);
                }
            }

            return null;
        }, ct, retryOnConflict: true);
    }

    /// <summary>
    /// Deletes an item: every import first, each through the ONE project avatar delete routine
    /// (<see cref="ProjectAvatarDeletion"/>, so its copied files, stills and vendor copies go and files a control holds
    /// stay), then the item's rows, then its stored objects, and reports what was actually removed. A PERSON's variants are deleted with it, each through this same routine (their imports too); an object's
    /// variants are not deleted: they stay as items of their own, ungrouped, as a project's variants do when their original goes. Null when the item
    /// does not exist for the organization. Works whatever the avatar switches say: removing a likeness is never
    /// blocked by a product switch.
    /// </summary>
    /// <remarks>
    /// When an import cannot be removed the item stays (<see cref="AvatarLibraryDeleteReport.ItemDeleted"/> false):
    /// deleting the master from under a project that still uses it would leave an import that follows nothing. The
    /// imports already removed stay removed and the delete can be asked again. An object that cannot be removed after
    /// the rows are gone is reported as failed; the storage sweep removes an object whose item no longer exists.
    /// </remarks>
    public async Task<AvatarLibraryDeleteReport?> DeleteAsync(
        Guid organizationId, Guid itemId, Guid? actorUserId, AvatarLibraryDeleteCause cause, CancellationToken ct)
    {
        int importsDeleted = 0;
        int variantObjectsDeleted = 0;

        AvatarSubjectKind? kindOfItem = await _db.AvatarLibraryItems.AsNoTracking()
            .Where(i => i.Id == itemId && i.OrganizationId == organizationId).Select(i => (AvatarSubjectKind?)i.SubjectKind).FirstOrDefaultAsync(ct);

        for (int attempt = 1; ; attempt++)
        {
            if (!await _db.AvatarLibraryItems.AsNoTracking().AnyAsync(i => i.Id == itemId && i.OrganizationId == organizationId, ct))
                return attempt == 1 ? null : new AvatarLibraryDeleteReport(ItemDeleted: true, importsDeleted, 0, variantObjectsDeleted, 0);

            // A PERSON's library variants go with it, each through this same routine (its imports first, then its rows and
            // objects), read again on every attempt. When one cannot be removed the original stays and the delete can be
            // asked again. An object's variants are not deleted: they stay as items of their own, ungrouped.
            if (kindOfItem == AvatarSubjectKind.Person)
            {
                List<Guid> variantIds = await _db.AvatarLibraryItems.AsNoTracking()
                    .Where(i => i.OrganizationId == organizationId && i.VariantOfItemId == itemId).Select(i => i.Id).ToListAsync(ct);
                foreach (Guid variantId in variantIds)
                {
                    AvatarLibraryDeleteReport? variantReport = await DeleteAsync(organizationId, variantId, actorUserId, cause, ct);
                    if (variantReport is null) continue;
                    importsDeleted += variantReport.ImportsDeleted;
                    variantObjectsDeleted += variantReport.ObjectsDeleted;
                    if (!variantReport.ItemDeleted)
                        return new AvatarLibraryDeleteReport(ItemDeleted: false, importsDeleted, variantReport.ImportsFailed, variantObjectsDeleted, variantReport.ObjectsFailed);
                }
            }

            // ---- 1. The imports, one delete each (bounded by the import cap). ----
            // NOT under the organization's guard: the project delete routine commits its rows before it removes their
            // objects, and it must not do that inside somebody else's open transaction.
            var imports = await _db.ProjectAvatars.AsNoTracking()
                .Where(a => a.LibraryItemId == itemId)
                .OrderBy(a => a.CreatedAt)
                .Select(a => new { a.Id, a.ProjectId })
                .Take(AvatarLibraryLimits.MaxImportsPerItem * 2)
                .ToListAsync(ct);
            int importsFailed = 0;
            foreach (var import in imports)
            {
                try
                {
                    await _deletion.DeleteAsync(import.ProjectId, import.Id, organizationId, actorUserId, AvatarDeleteCause.LibraryItem, ct);
                    importsDeleted++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    importsFailed++;
                    _logger.LogError(ex, "Could not remove import {AvatarId} of avatar library item {ItemId}; the item is kept", import.Id, itemId);
                }
                finally
                {
                    _db.ChangeTracker.Clear();
                }
            }

            if (importsFailed > 0) return new AvatarLibraryDeleteReport(ItemDeleted: false, importsDeleted, importsFailed, 0, 0);

            // Whatever sits in the item's folder goes with it: the objects the rows name and any copy a failed
            // publish could not remove. Listed before the guard: no lock is held across the object store.
            IReadOnlyList<string> listed = await ListItemKeysAsync(organizationId, itemId, ct);

            // ---- 2. The item's rows and its audit event, as one critical section of the organization. ----
            HashSet<string> keys = new(listed, StringComparer.Ordinal);
            bool importedMeanwhile;
            await using (OrganizationStorageGuard guard = await _storageLock.AcquireAsync(_db, organizationId, ct))
            {
                // Imports are added under this guard, so this answer holds until the commit below.
                importedMeanwhile = await _db.ProjectAvatars.AnyAsync(a => a.LibraryItemId == itemId, ct);
                // A variant published since the variants were deleted (it takes this guard too) is a person's variant that
                // must not be ungrouped into an independent avatar: go round again and delete it as well.
                if (!importedMeanwhile && kindOfItem == AvatarSubjectKind.Person)
                    importedMeanwhile = await _db.AvatarLibraryItems.AnyAsync(i => i.OrganizationId == organizationId && i.VariantOfItemId == itemId, ct);
                if (!importedMeanwhile)
                {
                    AvatarLibraryItem? item = await _db.AvatarLibraryItems.FirstOrDefaultAsync(i => i.Id == itemId && i.OrganizationId == organizationId, ct);
                    if (item is null) return new AvatarLibraryDeleteReport(ItemDeleted: true, importsDeleted, 0, 0, 0);
                    List<AvatarLibraryReference> references = await _db.AvatarLibraryReferences.Where(r => r.LibraryItemId == itemId).ToListAsync(ct);
                    foreach (AvatarLibraryReference reference in references)
                    {
                        if (AvatarLibraryStorage.Parse(reference.StorageKey) == (organizationId, itemId)) keys.Add(reference.StorageKey);
                    }

                    if (item.ConsentDocumentStorageKey is { } documentKey && AvatarLibraryStorage.Parse(documentKey) == (organizationId, itemId))
                        keys.Add(documentKey);

                    // Its variants stay as items, ungrouped, with their labels (the foreign key's SET NULL, in this save).
                    int variantsUngrouped = kindOfItem == AvatarSubjectKind.Object
                        ? await UngroupVariantsAsync(organizationId, itemId, DateTime.UtcNow, ct)
                        : 0;

                    ProjectAvatarEvents.AddForLibraryItem(_db, item, AvatarEventKind.LibraryDeleted, actorUserId, new Dictionary<string, object>
                    {
                        ["cause"] = cause == AvatarLibraryDeleteCause.Retention
                            ? item.ConsentRevokedAt is not null ? "revoked_grace_ended" : "retention_expired"
                            : "user",
                        ["subject_kind"] = KindWord(item.SubjectKind),
                        ["imports_deleted"] = importsDeleted,
                        ["references"] = references.Count,
                        ["consent_document"] = item.ConsentDocumentStorageKey is not null,
                        ["objects"] = keys.Count,
                        ["variants_ungrouped"] = variantsUngrouped
                    });
                    _db.AvatarLibraryReferences.RemoveRange(references);
                    _db.AvatarLibraryItems.Remove(item);
                    await _db.SaveChangesAsync(ct);
                    await guard.CommitAsync(ct);
                }
            }

            if (importedMeanwhile)
            {
                // An import landed between step 1 and the guard: remove it too, a bounded number of times.
                if (attempt < MaxAttempts) continue;
                return new AvatarLibraryDeleteReport(ItemDeleted: false, importsDeleted, 1, 0, 0);
            }

            (int objectsDeleted, int objectsFailed) = await DeleteLibraryObjectsAsync(keys);
            _logger.LogInformation(
                "Deleted avatar library item {ItemId} ({Cause}): imports deleted={Imports}, objects deleted={Objects} failed={Failed}",
                itemId, cause, importsDeleted, objectsDeleted, objectsFailed);
            return new AvatarLibraryDeleteReport(ItemDeleted: true, importsDeleted, 0, objectsDeleted + variantObjectsDeleted, objectsFailed);
        }
    }

    private async Task<IReadOnlyList<string>> ListItemKeysAsync(Guid organizationId, Guid itemId, CancellationToken ct)
    {
        try
        {
            return [.. (await _store.ListAsync(AvatarLibraryStorage.ItemPrefix(organizationId, itemId), ct)).Select(e => e.StorageKey)];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list the objects of avatar library item {ItemId}; the rows name what is deleted", itemId);
            return [];
        }
    }

    /// <summary>
    /// The shape every change of an existing item shares: under the organization's guard, load the item and its
    /// imports, let <paramref name="change"/> decide (a refusal writes nothing) and write the item and all its imports
    /// in ONE save. A lost race on a row version is <c>409 AVATAR_CHANGED</c>, or, with
    /// <paramref name="retryOnConflict"/>, retried from a fresh read.
    /// </summary>
    private async Task<AvatarLibraryResult<AvatarLibraryItemView>> MutateAsync(
        Guid organizationId, Guid itemId, Func<AvatarLibraryItem, List<ProjectAvatar>, Task<AvatarRefusal?>> change,
        CancellationToken ct, bool retryOnConflict = false)
    {
        for (int attempt = 1; ; attempt++)
        {
            AvatarRefusal? refused = null;
            ExceptionDispatchInfo? writeFailure = null;
            bool conflict = false;
            await using (OrganizationStorageGuard guard = await _storageLock.AcquireAsync(_db, organizationId, ct))
            {
                AvatarLibraryItem? item = await FindAsync(organizationId, itemId, tracked: true, ct);
                if (item is null) return AvatarLibraryResult<AvatarLibraryItemView>.Missing();
                List<ProjectAvatar> imports = await ImportsAsync(itemId, ct);

                refused = await change(item, imports);
                if (refused is null)
                {
                    try
                    {
                        await _db.SaveChangesAsync(ct);
                        await guard.CommitAsync(ct);
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        conflict = true;
                    }
                    catch (DbUpdateException ex)
                    {
                        writeFailure = ExceptionDispatchInfo.Capture(ex);
                    }
                }
            }

            if (refused is not null || conflict || writeFailure is not null) _db.ChangeTracker.Clear();
            if (refused is not null) return Refused(refused);
            if (writeFailure is not null)
            {
                // Most likely a partial unique index on live names (the library's, or a project's): a concurrent change
                // took the name. A fresh read lets the change's own checks see it and refuse in words.
                if (attempt < MaxAttempts) continue;
                writeFailure.Throw();
            }

            if (conflict)
            {
                if (retryOnConflict && attempt < MaxAttempts) continue;
                return Refused(Changed());
            }

            AvatarLibraryItemView? view = await GetAsync(organizationId, itemId, projectId: null, ct);
            return view is null ? AvatarLibraryResult<AvatarLibraryItemView>.Missing() : AvatarLibraryResult<AvatarLibraryItemView>.Ok(view);
        }
    }

    private static AvatarLibraryResult<AvatarLibraryItemView> Refused(AvatarRefusal refusal) =>
        AvatarLibraryResult<AvatarLibraryItemView>.Refused(refusal);
}
