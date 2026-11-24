using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.Csam;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Uploads;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

public sealed partial class AvatarLibraryService
{
    /// <summary>One reference of the source avatar and where its library copy goes.</summary>
    private sealed record PlannedCopy(ProjectAvatarReference Source, Guid LibraryReferenceId, string LibraryKey);

    /// <summary>
    /// Saves avatar <paramref name="avatarId"/> of <paramref name="project"/> to the organization's library and turns
    /// it into a linked import of the new item. The caller has checked that the user is an Owner or Admin of the
    /// project's organization with a verified email and may see the project.
    /// </summary>
    /// <remarks>
    /// In order, nothing written on a refusal: the avatar exists and its kind is switched on (else not found); the
    /// kind's plan feature (<c>402</c>); it is not itself an import (<c>409 AVATAR_NOT_USABLE</c>); the WORKSPACE
    /// statement of the kind verbatim, and for a person the adult tick and the keep-for days (a project-scoped consent
    /// never becomes workspace-wide by itself: this is a new attestation); the library's limit and name
    /// (<c>409 LIBRARY_LIMIT_REACHED</c> / <c>LIBRARY_NAME_TAKEN</c>) and the storage quota (<c>413</c>). Then every
    /// reference is copied into organization storage and the COPY is hashed and compared with the digest attested on
    /// the avatar, through the one selectability rule: an avatar that is not usable right now, or a copy whose bytes
    /// are not the attested ones, is <c>409 AVATAR_NOT_USABLE</c> and the copies are removed. The item, its reference
    /// rows, the source avatar's link and new consent stamp and the events are written in one save.
    /// <para>
    /// Variants (docs/design/avatar-objects.md "Avatar variants", "Library"): a project variant carries its label and
    /// description to the item, and when its original is linked to a library item the new item becomes a variant of that
    /// item (one level only), under the library's rules: at most <see cref="AvatarLibraryLimits.MaxVariantsPerItem"/>
    /// (<c>409 LIBRARY_VARIANT_LIMIT_REACHED</c>) and a label no live variant of that original uses
    /// (<c>409 LIBRARY_VARIANT_LABEL_TAKEN</c>). Publishing an original links, in the same save, the items its project
    /// variants were already published as.
    /// </para>
    /// </remarks>
    public async Task<AvatarLibraryResult<AvatarLibraryItemView>> PublishAsync(
        Project project, Guid avatarId, AvatarLibraryAttestation attestation, Guid userId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(attestation);
        Guid organizationId = project.OrganizationId;

        ProjectAvatar? avatar = await _db.ProjectAvatars.FirstOrDefaultAsync(a => a.Id == avatarId && a.ProjectId == project.Id, ct);
        if (avatar is null || !_options.IsEnabled(avatar.SubjectKind)) return AvatarLibraryResult<AvatarLibraryItemView>.Missing();
        AvatarSubjectKind kind = avatar.SubjectKind;

        if (await PlanRefusalAsync(organizationId, kind, ct) is { } notEntitled) return Refuse(notEntitled);
        if (await QuarantineRefusalAsync(project, ct) is { } quarantined) return Refuse(quarantined);
        if (avatar.IsLibraryImport)
        {
            return Refuse(AvatarRefusal.Conflict("AVATAR_NOT_USABLE",
                "This one already comes from the workspace library, so it cannot be saved to it again.",
                new Dictionary<string, object?> { ["reason"] = "from_library" }));
        }

        // ---- The attestation first, before any file is read. ----
        if (AttestationRefusal(kind, attestation, out int retentionDays) is { } refusedAttestation) return Refuse(refusedAttestation);

        List<ProjectAvatarReference> references = await _db.ProjectAvatarReferences.AsNoTracking()
            .Where(r => r.AvatarId == avatarId)
            .OrderBy(r => r.Ordinal)
            .ToListAsync(ct);
        Guid[] fileIds = [.. references.Select(r => r.ProjectFileId)];
        Dictionary<Guid, ProjectFile> files = await _db.ProjectFiles.AsNoTracking()
            .Where(f => f.ProjectId == project.Id && fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);
        // No file that a control holds or has not cleared yet is ever copied: judged before any byte moves.
        if (files.Values.Any(f => FileRefused(f))) return Refuse(NotUsable(ProjectAvatar.ReasonReferenceHeld));
        ProjectFile? document = await ConsentDocumentAsync(project, avatar, ct);
        long bytes = files.Values.Sum(f => Math.Max(0, f.SizeBytes)) + Math.Max(0, document?.SizeBytes ?? 0);

        // ---- The library's own limits, before any byte moves (checked again under the guard). ----
        if (await LibraryRefusalAsync(organizationId, kind, avatar.Name, bytes, ct) is { } early) return Refuse(early);
        // A project variant whose original is in the library becomes a variant of that item, under the library's rules.
        LibraryVariantPlan? variantPlan = await PublishVariantPlanAsync(project, avatar, ct);
        if (variantPlan?.OriginalItemId is { } earlyTarget
            && await LibraryVariantRefusalAsync(organizationId, earlyTarget, variantPlan.Label, ct) is { } variantRefused)
            return Refuse(variantRefused);

        // ---- What needs no file to be read: consent, retention, a picture at all. ----
        // A person variant is judged with its original (revoked, expired or gone there makes it unusable too).
        IReadOnlyDictionary<Guid, ProjectAvatar> originals = await AvatarVariantOriginals.LoadAsync(_db, project.Id, [avatar], ct);
        if (avatar.UnselectableReason(DateTime.UtcNow, [.. references.Select(r => new AvatarReferenceState(r.Kind, Present: true, Held: false))], originals) is { } unusable)
            return Refuse(NotUsable(unusable));

        // ---- Copy, then judge the COPY by the one rule. ----
        Guid itemId = Guid.NewGuid();
        Dictionary<Guid, PlannedCopy> plan = references.ToDictionary(
            r => r.ProjectFileId,
            r =>
            {
                Guid referenceId = Guid.NewGuid();
                files.TryGetValue(r.ProjectFileId, out ProjectFile? file);
                string key = AvatarLibraryStorage.ReferenceKey(
                    organizationId, itemId, referenceId, AvatarLibraryStorage.ExtensionOf(file?.OriginalFileName));
                return new PlannedCopy(r, referenceId, key);
            });
        List<string> copied = [];
        bool committed = false;
        try
        {
            // The observer never reads a held file and answers missing for a file outside the project; for every other
            // reference this delegate copies the file and hashes what the library now holds.
            IReadOnlyList<AvatarReferenceState> states = await AvatarReferenceObserver.ObserveAsync(
                _db, project.Id, references, (file, token) => CopyAndDigestAsync(project.Id, file, plan, copied, token), ct);
            DateTime now = DateTime.UtcNow;
            if (avatar.UnselectableReason(now, [.. states], originals) is { } reason) return Refuse(NotUsable(reason));

            string? documentKey = await CopyConsentDocumentAsync(project.Id, organizationId, itemId, document, copied, ct);

            var item = new AvatarLibraryItem
            {
                Id = itemId,
                OrganizationId = organizationId,
                SubjectKind = kind,
                Name = avatar.Name,
                Description = avatar.Description,
                VariantLabel = variantPlan?.Label,
                VariantDescription = variantPlan?.Description,
                ConsentStatement = WorkspaceStatement(kind, attestation.ConsentLocale)!,
                ConsentStatementVersion = WorkspaceStatementVersion(kind),
                ConsentLocale = attestation.ConsentLocale!,
                ConsentSubjectName = kind == AvatarSubjectKind.Object ? string.Empty : avatar.ConsentSubjectName,
                ConsentBasis = kind == AvatarSubjectKind.Object ? AvatarConsentBasis.Self : avatar.ConsentBasis,
                ConsentSubjectAdult = kind == AvatarSubjectKind.Person,
                ConsentAttestedByUserId = userId,
                ConsentAttestedAt = now,
                ConsentDocumentStorageKey = documentKey,
                RetentionUntil = kind == AvatarSubjectKind.Object ? ProjectAvatar.NoRetention : now.AddDays(retentionDays),
                DisclosureLabel = kind == AvatarSubjectKind.Object ? AvatarDisclosure.MetadataOnly : avatar.DisclosureLabel,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now
            };
            List<AvatarLibraryReference> rows = [.. references.Select(r =>
            {
                PlannedCopy planned = plan[r.ProjectFileId];
                ProjectFile file = files[r.ProjectFileId];
                return new AvatarLibraryReference
                {
                    Id = planned.LibraryReferenceId,
                    LibraryItemId = itemId,
                    Kind = r.Kind,
                    Role = r.Role,
                    Ordinal = r.Ordinal,
                    ContentSha256 = r.ContentSha256,
                    StorageKey = planned.LibraryKey,
                    MimeType = Clip(NormalizeMime(file.MimeType), AvatarLibraryLimits.MaxMimeTypeLength),
                    FileName = Clip(SafeFileName(file.OriginalFileName, planned.LibraryReferenceId), AvatarLibraryLimits.MaxFileNameLength),
                    SizeBytes = file.SizeBytes,
                    Width = r.Width,
                    Height = r.Height,
                    DurationMs = r.DurationMs,
                    AttachedAt = now
                };
            })];

            // ---- The limits again and the write, as one critical section of the organization. ----
            AvatarRefusal? refused;
            ExceptionDispatchInfo? writeFailure = null;
            bool projectMoved = false;
            await using (OrganizationStorageGuard guard = await _storageLock.AcquireAsync(_db, organizationId, ct))
            {
                // A transfer holds this same guard while it moves the project: a project that left is not published from.
                projectMoved = !await ProjectStillInAsync(project.Id, organizationId, ct);
                refused = projectMoved ? null : await LibraryRefusalAsync(organizationId, kind, avatar.Name, bytes, ct);
                if (refused is null && !projectMoved)
                {
                    // The original's item is read again here: deleted meanwhile, the variant is saved ungrouped.
                    variantPlan = await PublishVariantPlanAsync(project, avatar, ct);
                    if (variantPlan?.OriginalItemId is { } target)
                        refused = await LibraryVariantRefusalAsync(organizationId, target, variantPlan.Label, ct);
                    item.VariantOfItemId = variantPlan?.OriginalItemId;
                    item.VariantLabel = variantPlan?.Label;
                    item.VariantDescription = variantPlan?.Description;
                }

                if (refused is null && !projectMoved)
                {
                    _db.AvatarLibraryItems.Add(item);
                    _db.AvatarLibraryReferences.AddRange(rows);

                    // An ORIGINAL whose project variants were already saved to the library: those items become its
                    // variants in the same save.
                    (int variantsLinked, int variantsNotLinked) = avatar.VariantOfAvatarId is null
                        ? await LinkPublishedVariantsAsync(project, avatar, item, now, ct)
                        : (0, 0);

                    // The source avatar becomes a linked import: the item holds the workspace attestation.
                    // Its files stay where they are (they are the project's own, not copies).
                    avatar.LibraryItemId = itemId;
                    // The item holds the new workspace attestation; the source keeps its own consent record (a running step
                    // fingerprints it, an export reads it as one attestation), so only the keep-until date follows the
                    // item: the retention sweep treats a linked import as living and dying with its item.
                    avatar.RetentionUntil = item.RetentionUntil;
                    avatar.UpdatedAt = now;

                    Dictionary<string, object> published = new(ReferenceCounts(rows))
                    {
                        ["via"] = "app",
                        ["subject_kind"] = KindWord(kind),
                        ["locale"] = item.ConsentLocale,
                        ["consent_document"] = documentKey is not null,
                        ["variant"] = item.VariantOfItemId is not null,
                        ["variants_linked"] = variantsLinked,
                        ["variants_not_linked"] = variantsNotLinked
                    };
                    if (kind == AvatarSubjectKind.Person) published["retention_days"] = retentionDays;
                    ProjectAvatarEvents.AddForLibraryItem(_db, item, AvatarEventKind.LibraryPublished, userId, published, projectId: project.Id);
                    ProjectAvatarEvents.Add(_db, avatar, organizationId, AvatarEventKind.LibraryImported, userId,
                        new Dictionary<string, object> { ["via"] = "publish", ["subject_kind"] = KindWord(kind) });

                    try
                    {
                        await _db.SaveChangesAsync(ct);
                        await guard.CommitAsync(ct);
                        committed = true;
                    }
                    catch (DbUpdateConcurrencyException)
                    {
                        // The avatar was revoked, re-attested or deleted meanwhile: nothing of this publish was written.
                        _db.ChangeTracker.Clear();
                        refused = Changed();
                    }
                    catch (DbUpdateException ex)
                    {
                        // Most likely the partial unique index on live names; decided below, outside the guard's
                        // (now failed) transaction.
                        _db.ChangeTracker.Clear();
                        writeFailure = ExceptionDispatchInfo.Capture(ex);
                    }
                }
            }

            if (writeFailure is not null)
            {
                // A concurrent publish took the name, or the variant label, between check and write.
                if (await NameTakenAsync(organizationId, item.Name, itemId, CancellationToken.None)) return Refuse(NameTaken(item.Name));
                if (item.VariantOfItemId is { } target && item.VariantLabel is { } label
                    && await LibraryLabelTakenAsync(organizationId, target, label, itemId, CancellationToken.None))
                    return Refuse(LibraryLabelTaken(label));
                writeFailure.Throw();
            }

            if (projectMoved) return AvatarLibraryResult<AvatarLibraryItemView>.Missing();
            if (refused is not null) return Refuse(refused);

            _logger.LogInformation(
                "Published avatar {AvatarId} of project {ProjectId} to the workspace library as item {ItemId} ({Kind}, {References} references)",
                avatarId, project.Id, itemId, kind, rows.Count);
            return AvatarLibraryResult<AvatarLibraryItemView>.Ok((await ToViewsAsync([item], project.Id, ct))[0]);
        }
        finally
        {
            // Every exit but a committed publish removes what was copied: the library never keeps bytes no item names.
            if (!committed && copied.Count > 0) await DeleteLibraryObjectsAsync(copied);
        }

        static AvatarLibraryResult<AvatarLibraryItemView> Refuse(AvatarRefusal refusal) => AvatarLibraryResult<AvatarLibraryItemView>.Refused(refusal);
    }

    /// <summary>
    /// Copies one reference file into the library and answers the SHA-256 of the copy, or null when it could not be
    /// copied or read back (which the rule reads as a missing reference: fail closed). Cancellation propagates.
    /// </summary>
    private async Task<string?> CopyAndDigestAsync(
        Guid projectId, ProjectFile file, Dictionary<Guid, PlannedCopy> plan, List<string> copied, CancellationToken ct)
    {
        if (!plan.TryGetValue(file.Id, out PlannedCopy? planned)) return null;
        try
        {
            await _store.CopyIntoLibraryAsync(projectId, file.StorageKey, planned.LibraryKey, ct);
            copied.Add(planned.LibraryKey);
            await using Stream? stream = await _store.OpenReadAsync(planned.LibraryKey, ct);
            if (stream is null) return null;
            return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy reference file {FileId} into the avatar library", file.Id);
            return null;
        }
    }

    /// <summary>
    /// A project with quarantined content, or under the CSAM output hold, publishes nothing: the same condition, status
    /// and code as creating or re-attaching an avatar there and as an import. A marker that cannot be read refuses.
    /// </summary>
    private async Task<AvatarRefusal?> QuarantineRefusalAsync(Project project, CancellationToken ct)
    {
        try
        {
            DateTime? marker = await _db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Id == project.Id)
                .Select(p => p.ContentQuarantinedAt)
                .FirstOrDefaultAsync(ct);
            ProjectOutputHoldState hold = await OutputGate.ReadAsync(_db, project.Id, ct);
            if (project.ContentQuarantinedAt is null && marker is null && !hold.IsActive) return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the quarantine state of project {ProjectId}; the publish is refused", project.Id);
        }

        return AvatarRefusal.Conflict("PROJECT_CONTENT_QUARANTINED", "Avatars cannot be created or changed in this project.");
    }

    /// <summary>A source file that is quarantined (CSAM or moderation), or whose CSAM verdict is still pending, is never copied.</summary>
    private bool FileRefused(ProjectFile file) =>
        CsamScanGate.IsHeld(file) || file.ModerationStatus == FileModerationStatus.Quarantined
        || (_csam is not null && file.CsamStatus is CsamScanStatus.Pending or CsamScanStatus.Scanning);

    /// <summary>
    /// The avatar's signed release, when it has one that may be copied: a file of the project that no control holds.
    /// A held or missing release is simply not copied (the item then has none); it is never a reason to refuse.
    /// </summary>
    private async Task<ProjectFile?> ConsentDocumentAsync(Project project, ProjectAvatar avatar, CancellationToken ct)
    {
        if (avatar.SubjectKind == AvatarSubjectKind.Object || avatar.ConsentDocumentFileId is not { } documentId) return null;
        ProjectFile? file = await _db.ProjectFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == documentId && f.ProjectId == project.Id, ct);
        if (file is null || string.IsNullOrEmpty(file.StorageKey)) return null;
        if (CsamScanGate.IsHeld(file) || file.ModerationStatus == FileModerationStatus.Quarantined) return null;
        return await OutputGate.IsFileHeldAsync(_db, file, ct) ? null : file;
    }

    private async Task<string?> CopyConsentDocumentAsync(
        Guid projectId, Guid organizationId, Guid itemId, ProjectFile? document, List<string> copied, CancellationToken ct)
    {
        if (document is null) return null;
        string key = AvatarLibraryStorage.ConsentDocumentKey(organizationId, itemId, AvatarLibraryStorage.ExtensionOf(document.OriginalFileName));
        try
        {
            await _store.CopyIntoLibraryAsync(projectId, document.StorageKey, key, ct);
            copied.Add(key);
            return key;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not copy the signed release into the avatar library; the item is saved without it");
            return null;
        }
    }

    /// <summary>The library-side refusals of a publish: the per-kind limit, the name, the storage quota.</summary>
    private async Task<AvatarRefusal?> LibraryRefusalAsync(Guid organizationId, AvatarSubjectKind kind, string name, long bytes, CancellationToken ct)
    {
        if (await _db.AvatarLibraryItems.CountAsync(i => i.OrganizationId == organizationId && i.SubjectKind == kind, ct)
            >= AvatarLibraryLimits.MaxItemsPerKind)
        {
            return AvatarRefusal.Conflict("LIBRARY_LIMIT_REACHED",
                $"The workspace library can hold at most {AvatarLibraryLimits.MaxItemsPerKind} items of this kind. Delete one first.");
        }

        if (await NameTakenAsync(organizationId, name, null, ct)) return NameTaken(name);
        return await QuotaRefusalAsync(organizationId, bytes, ct);
    }

    private Task<bool> NameTakenAsync(Guid organizationId, string name, Guid? except, CancellationToken ct) =>
        _db.AvatarLibraryItems.AnyAsync(i => i.OrganizationId == organizationId && i.ConsentRevokedAt == null && i.Name == name
                                             && (except == null || i.Id != except), ct);

    private static AvatarRefusal NotUsable(string reason) =>
        AvatarRefusal.Conflict("AVATAR_NOT_USABLE",
            "It cannot be used right now, so it cannot be saved to the workspace library. Fix it in its project first.",
            new Dictionary<string, object?> { ["reason"] = reason });

    private static AvatarRefusal NameTaken(string name) =>
        AvatarRefusal.Conflict("LIBRARY_NAME_TAKEN", $"The workspace library already has an item called \"{name}\".");

    private static string? WorkspaceStatement(AvatarSubjectKind kind, string? locale) =>
        kind == AvatarSubjectKind.Object ? AvatarWorkspaceObjectStatement.For(locale) : AvatarWorkspaceConsentStatement.For(locale);

    private static string WorkspaceStatementVersion(AvatarSubjectKind kind) =>
        kind == AvatarSubjectKind.Object ? AvatarWorkspaceObjectStatement.Version : AvatarWorkspaceConsentStatement.Version;

    /// <summary>
    /// Copies everything an import takes from its item: the name and description, the whole consent record (statement,
    /// version, locale, subject, basis, adult flag, who attested and when, the revoke stamp), the keep-until date and
    /// the disclosure label. The voice link and the signed-release file are the project's own and are not touched.
    /// </summary>
    private static void CopyConsent(AvatarLibraryItem item, ProjectAvatar import)
    {
        import.Name = item.Name;
        import.Description = item.Description;
        import.ConsentStatement = item.ConsentStatement;
        import.ConsentStatementVersion = item.ConsentStatementVersion;
        import.ConsentLocale = item.ConsentLocale;
        import.ConsentSubjectName = item.ConsentSubjectName;
        import.ConsentBasis = item.ConsentBasis;
        import.ConsentSubjectAdult = item.ConsentSubjectAdult;
        import.ConsentAttestedByUserId = item.ConsentAttestedByUserId;
        import.ConsentAttestedAt = item.ConsentAttestedAt;
        import.ConsentRevokedAt = item.ConsentRevokedAt;
        import.ConsentRevokedByUserId = item.ConsentRevokedByUserId;
        import.RetentionUntil = item.RetentionUntil;
        import.DisclosureLabel = item.DisclosureLabel;
    }

    private static string NormalizeMime(string? mime)
    {
        string normalized = (mime ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();
        return normalized.Length == 0 ? "application/octet-stream" : normalized;
    }

    /// <summary>A single path segment with no separators or control characters; never empty.</summary>
    private static string SafeFileName(string? name, Guid fallbackId)
    {
        string cleaned = string.Concat((name ?? string.Empty).Where(c => c is not ('/' or '\\') && !char.IsControl(c))).Trim();
        if (cleaned.Length == 0 || cleaned is "." or "..")
            return fallbackId.ToString("N") + "." + AvatarLibraryStorage.ExtensionOf(name);
        return cleaned;
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
