using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Avatars;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.Avatars.Library;

/// <summary>
/// The durable side of <see cref="AvatarLibraryHoldGate"/>: finds which digests a live file row holds, and writes
/// <see cref="AvatarLibraryReference.HeldAt"/> on the library copies of that content. The one hook
/// (<see cref="StampBeforeRemoval"/>) is called by <c>InferenceApiDbContext</c> in EVERY save, so a hard delete of a
/// <see cref="ProjectFile"/>, a <see cref="ProjectAvatarReference"/> or a <see cref="Project"/> leaves the marker in the
/// same transaction as the delete. A marker is never cleared by a delete, a file change or a re-scan.
/// </summary>
internal static class AvatarLibraryHoldMarker
{
    private const string Csam = HeldFileRule.Csam;

    private sealed record Candidate(
        string Digest, CsamScanStatus CsamStatus, string? CsamQuarantineKey, FileModerationStatus ModerationStatus,
        DateTime? OutputProducedAt, DateTime? ContentQuarantinedAt);

    private static string? ReasonOf(Candidate c) =>
        HeldFileRule.Reason(c.CsamStatus, c.CsamQuarantineKey, c.ModerationStatus, c.OutputProducedAt, c.ContentQuarantinedAt);

    private static void Merge(Dictionary<string, string> held, Candidate c)
    {
        if (string.IsNullOrEmpty(c.Digest) || ReasonOf(c) is not { } reason) return;
        string key = c.Digest.ToLowerInvariant();
        // CSAM outranks the others and is never downgraded.
        if (!held.TryGetValue(key, out string? existing) || (existing != Csam && reason == Csam)) held[key] = reason;
    }

    /// <summary>The digests among <paramref name="wanted"/> that a live file row of the organization holds, with the reason.</summary>
    internal static async Task<Dictionary<string, string>> LiveHeldDigestsAsync(
        DbContext db, Guid organizationId, string[] wanted, CancellationToken ct)
    {
        // The organization predicate is explicit and the filters are off: a hidden or quarantined row is the point of
        // the question, and another organization's file never holds this organization's copy.
        List<Candidate> byFile = await db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.Project.OrganizationId == organizationId && f.ContentSha256 != null && wanted.Contains(f.ContentSha256))
            .Select(f => new Candidate(f.ContentSha256!, f.CsamStatus, f.CsamQuarantineKey, f.ModerationStatus, f.OutputProducedAt, f.Project.ContentQuarantinedAt))
            .ToListAsync(ct);
        List<Candidate> byReference = await (
            from r in db.Set<ProjectAvatarReference>().IgnoreQueryFilters().AsNoTracking()
            join f in db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking() on r.ProjectFileId equals f.Id
            where f.Project.OrganizationId == organizationId && wanted.Contains(r.ContentSha256)
            select new Candidate(r.ContentSha256, f.CsamStatus, f.CsamQuarantineKey, f.ModerationStatus, f.OutputProducedAt, f.Project.ContentQuarantinedAt))
            .ToListAsync(ct);
        Dictionary<string, string> held = new(StringComparer.Ordinal);
        foreach (Candidate c in byFile.Concat(byReference)) Merge(held, c);
        return held;
    }

    /// <summary>Writes the marker on the organization's copies of <paramref name="held"/> digests. Best effort: a failure is logged, the live rule still holds.</summary>
    internal static async Task StampAsync(DbContext db, Guid organizationId, Dictionary<string, string> held, ILogger logger, CancellationToken ct)
    {
        try
        {
            string[] digests = [.. held.Keys];
            List<AvatarLibraryReference> rows = await db.Set<AvatarLibraryReference>().IgnoreQueryFilters()
                .Where(r => r.HeldAt == null && digests.Contains(r.ContentSha256)
                            && db.Set<AvatarLibraryItem>().IgnoreQueryFilters().Any(i => i.Id == r.LibraryItemId && i.OrganizationId == organizationId))
                .ToListAsync(ct);
            if (rows.Count == 0) return;
            DateTime now = DateTime.UtcNow;
            foreach (AvatarLibraryReference row in rows)
            {
                row.HeldAt = now;
                row.HeldReason = held[row.ContentSha256.ToLowerInvariant()];
            }

            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not write the hold marker on avatar library copies of organization {OrganizationId}", organizationId);
        }
    }

    // ------------------------------------------------------------------ the deletion hook

    /// <summary>
    /// Called before every save of the API context: for each held source row about to be removed (a file, an avatar
    /// reference, or a whole project), stamps the library copies of its content in the SAME save. Fails the save when it
    /// cannot read what it must (fail closed: no held row is removed without its marker).
    /// </summary>
    internal static async Task StampBeforeRemovalAsync(DbContext db, CancellationToken ct)
    {
        Collected c = Collect(db);
        if (c.IsEmpty) return;
        // A removed avatar takes its reference rows with it (a database cascade, no tracked row): read them here.
        if (c.AvatarIds.Count > 0)
        {
            Guid[] avatarIds = [.. c.AvatarIds];
            c.References.AddRange(await db.Set<ProjectAvatarReference>().IgnoreQueryFilters().AsNoTracking().Where(r => avatarIds.Contains(r.AvatarId)).ToListAsync(ct));
        }

        Dictionary<string, string> held = [];
        Dictionary<Guid, Guid> orgOfProject = [];
        List<(Guid Org, string Digest, string Reason)> found = [];

        // Whole projects: every held file of the project, its attested digests and its own.
        Guid[] projectIds = [.. c.ProjectIds, .. c.Files.Select(f => f.ProjectId)];
        foreach (var p in await db.Set<Project>().IgnoreQueryFilters().AsNoTracking().Where(p => projectIds.Contains(p.Id))
                     .Select(p => new { p.Id, p.OrganizationId, p.ContentQuarantinedAt }).ToListAsync(ct))
            orgOfProject[p.Id] = p.OrganizationId;
        Dictionary<Guid, DateTime?> markers = (await db.Set<Project>().IgnoreQueryFilters().AsNoTracking().Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.ContentQuarantinedAt }).ToListAsync(ct)).ToDictionary(p => p.Id, p => p.ContentQuarantinedAt);

        List<ProjectFile> files = [.. c.Files];
        if (c.ProjectIds.Count > 0)
        {
            Guid[] ids = [.. c.ProjectIds];
            files.AddRange(await db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking().Where(f => ids.Contains(f.ProjectId)).ToListAsync(ct));
        }

        Guid[] fileIds = [.. files.Select(f => f.Id).Distinct()];
        List<ProjectAvatarReference> refsOfFiles = fileIds.Length == 0 ? [] :
            await db.Set<ProjectAvatarReference>().IgnoreQueryFilters().AsNoTracking().Where(r => fileIds.Contains(r.ProjectFileId)).ToListAsync(ct);
        foreach (ProjectFile f in files)
        {
            if (!orgOfProject.TryGetValue(f.ProjectId, out Guid org)) continue;
            Candidate? self = f.ContentSha256 is null ? null : new Candidate(f.ContentSha256, f.CsamStatus, f.CsamQuarantineKey, f.ModerationStatus, f.OutputProducedAt, markers.GetValueOrDefault(f.ProjectId));
            if (self is not null && ReasonOf(self) is { } r1) found.Add((org, self.Digest.ToLowerInvariant(), r1));
            foreach (ProjectAvatarReference r in refsOfFiles.Where(r => r.ProjectFileId == f.Id))
            {
                var cand = new Candidate(r.ContentSha256, f.CsamStatus, f.CsamQuarantineKey, f.ModerationStatus, f.OutputProducedAt, markers.GetValueOrDefault(f.ProjectId));
                if (ReasonOf(cand) is { } r2) found.Add((org, cand.Digest.ToLowerInvariant(), r2));
            }
        }

        // Reference rows removed while their file stays (or is removed elsewhere in the same save).
        foreach (ProjectAvatarReference r in c.References)
        {
            ProjectFile? f = files.FirstOrDefault(x => x.Id == r.ProjectFileId)
                ?? await db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(x => x.Id == r.ProjectFileId, ct);
            if (f is null) continue;
            if (!orgOfProject.ContainsKey(f.ProjectId))
            {
                var p = await db.Set<Project>().IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == f.ProjectId)
                    .Select(x => new { x.OrganizationId, x.ContentQuarantinedAt }).FirstOrDefaultAsync(ct);
                if (p is null) continue;
                orgOfProject[f.ProjectId] = p.OrganizationId;
                markers[f.ProjectId] = p.ContentQuarantinedAt;
            }

            var cand = new Candidate(r.ContentSha256, f.CsamStatus, f.CsamQuarantineKey, f.ModerationStatus, f.OutputProducedAt, markers.GetValueOrDefault(f.ProjectId));
            if (ReasonOf(cand) is { } reason) found.Add((orgOfProject[f.ProjectId], cand.Digest.ToLowerInvariant(), reason));
        }

        await ApplyAsync(db, found, ct);
    }

    /// <summary>Stages <see cref="AvatarLibraryReference.HeldAt"/> on the tracked context for each (organization, digest, reason); never overwrites a marker.</summary>
    private static async Task ApplyAsync(DbContext db, List<(Guid Org, string Digest, string Reason)> found, CancellationToken ct)
    {
        foreach (IGrouping<Guid, (Guid Org, string Digest, string Reason)> byOrg in found.GroupBy(x => x.Org))
        {
            Dictionary<string, string> held = [];
            foreach ((Guid _, string digest, string reason) in byOrg)
                if (!held.TryGetValue(digest, out string? e) || (e != Csam && reason == Csam)) held[digest] = reason;
            string[] digests = [.. held.Keys];
            List<AvatarLibraryReference> rows = await db.Set<AvatarLibraryReference>().IgnoreQueryFilters()
                .Where(r => r.HeldAt == null && digests.Contains(r.ContentSha256)
                            && db.Set<AvatarLibraryItem>().IgnoreQueryFilters().Any(i => i.Id == r.LibraryItemId && i.OrganizationId == byOrg.Key))
                .ToListAsync(ct);
            DateTime now = DateTime.UtcNow;
            foreach (AvatarLibraryReference row in rows)
            {
                row.HeldAt = now;
                row.HeldReason = held[row.ContentSha256.ToLowerInvariant()];
            }
        }
    }

    // ------------------------------------------------------------------ stamp at verdict time

    /// <summary>
    /// Stamps the library copies of the content of <paramref name="files"/> that are held NOW (by <see cref="HeldFileRule"/>),
    /// on the tracked context, so the caller's save carries the marker with the change that made them held (or moved
    /// their project to another organization). The file's own digest and the digests of the avatar reference rows that
    /// attested it both count. A failed read throws: the save fails (fail closed).
    /// </summary>
    internal static async Task StampFilesAsync(
        DbContext db, IReadOnlyCollection<ProjectFile> files, CancellationToken ct, IReadOnlyDictionary<Guid, DateTime?>? markerOverrides = null) =>
        await ApplyAsync(db, await CollectAsync(db, files, ct, markerOverrides), ct);

    private static async Task<List<(Guid Org, string Digest, string Reason)>> CollectAsync(
        DbContext db, IReadOnlyCollection<ProjectFile> files, CancellationToken ct, IReadOnlyDictionary<Guid, DateTime?>? markerOverrides)
    {
        List<(Guid Org, string Digest, string Reason)> found = [];
        if (files.Count == 0) return found;
        Guid[] projectIds = [.. files.Select(f => f.ProjectId).Distinct()];
        var projects = (await db.Set<Project>().IgnoreQueryFilters().AsNoTracking().Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.OrganizationId, p.ContentQuarantinedAt }).ToListAsync(ct)).ToDictionary(p => p.Id);
        Guid[] fileIds = [.. files.Select(f => f.Id).Distinct()];
        List<ProjectAvatarReference> refs = await db.Set<ProjectAvatarReference>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => fileIds.Contains(r.ProjectFileId)).ToListAsync(ct);
        foreach (ProjectFile f in files)
        {
            if (!projects.TryGetValue(f.ProjectId, out var project)) continue;
            DateTime? marker = markerOverrides is not null && markerOverrides.TryGetValue(f.ProjectId, out DateTime? inFlight) ? inFlight : project.ContentQuarantinedAt;
            if (HeldFileRule.Reason(f, marker) is not { } reason) continue;
            if (!string.IsNullOrEmpty(f.ContentSha256)) found.Add((project.OrganizationId, f.ContentSha256.ToLowerInvariant(), reason));
            foreach (ProjectAvatarReference r in refs.Where(r => r.ProjectFileId == f.Id))
                found.Add((project.OrganizationId, r.ContentSha256.ToLowerInvariant(), reason));
        }

        return found;
    }

    private static Task<List<ProjectFile>> CandidatesAsync(DbContext db, Guid projectId, CancellationToken ct) =>
        db.Set<ProjectFile>().IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.ProjectId == projectId
                        && (f.CsamStatus == CsamScanStatus.Quarantined || f.CsamQuarantineKey != null
                            || f.ModerationStatus == FileModerationStatus.Quarantined || f.OutputProducedAt != null))
            .ToListAsync(ct);

    /// <summary>
    /// The set-based form for a verdict written by <c>ExecuteUpdate</c> (CSAM matcher, project marker): stamps every held
    /// file's library copies of one project with <c>ExecuteUpdate</c> too, inside the verdict's own transaction (call it
    /// after the verdict write, on the same connection). One read of the project's candidate files, one write per
    /// (organization, reason). A failure throws, so the verdict write fails with it and its retry runs again.
    /// </summary>
    internal static async Task StampProjectSetBasedAsync(DbContext db, Guid projectId, CancellationToken ct)
    {
        List<(Guid Org, string Digest, string Reason)> found = await CollectAsync(db, await CandidatesAsync(db, projectId, ct), ct, null);
        DateTime now = DateTime.UtcNow;
        foreach (var group in found.GroupBy(x => (x.Org, x.Reason)))
        {
            string[] digests = [.. group.Select(x => x.Digest).Distinct()];
            Guid org = group.Key.Org;
            string reason = group.Key.Reason;
            await db.Set<AvatarLibraryReference>().IgnoreQueryFilters()
                .Where(r => r.HeldAt == null && digests.Contains(r.ContentSha256)
                            && db.Set<AvatarLibraryItem>().IgnoreQueryFilters().Any(i => i.Id == r.LibraryItemId && i.OrganizationId == org))
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.HeldAt, now).SetProperty(r => r.HeldReason, reason), ct);
        }
    }

    /// <summary>Stamps every held file of one project (used when the project's marker is set, and before a transfer to another organization).</summary>
    internal static async Task StampProjectAsync(DbContext db, Guid projectId, CancellationToken ct, DateTime? inFlightMarker = null)
    {
        List<ProjectFile> candidates = await CandidatesAsync(db, projectId, ct);
        await StampFilesAsync(db, candidates, ct, inFlightMarker is null ? null : new Dictionary<Guid, DateTime?> { [projectId] = inFlightMarker });
    }

    /// <summary>
    /// Called before every save of the API context, next to the removal hook: a tracked <see cref="ProjectFile"/> that
    /// BECOMES held (a CSAM or moderation verdict) or a tracked <see cref="Project"/> whose content-quarantine marker or
    /// CSAM output hold is being set stamps the library copies in the SAME save as the verdict, so no later delete,
    /// cascade or transfer can lose the hold. Tracker-only work when nothing relevant is tracked (no query).
    /// </summary>
    internal static async Task StampAtVerdictAsync(DbContext db, CancellationToken ct)
    {
        List<ProjectFile> becameHeld = [];
        List<(Guid Id, DateTime? Marker)> markedProjects = [];
        db.ChangeTracker.DetectChanges();
        foreach (var entry in db.ChangeTracker.Entries<ProjectFile>())
        {
            if (entry.State == EntityState.Added)
            {
                if (HeldFileRule.Reason(entry.Entity, null) is not null) becameHeld.Add(entry.Entity);
            }
            else if (entry.State == EntityState.Modified
                     && (entry.Property(f => f.CsamStatus).IsModified || entry.Property(f => f.CsamQuarantineKey).IsModified || entry.Property(f => f.ModerationStatus).IsModified)
                     && HeldFileRule.Reason(entry.Entity, null) is not null
                     && HeldFileRule.Reason(
                         entry.OriginalValues.GetValue<CsamScanStatus>(nameof(ProjectFile.CsamStatus)),
                         entry.OriginalValues.GetValue<string?>(nameof(ProjectFile.CsamQuarantineKey)),
                         entry.OriginalValues.GetValue<FileModerationStatus>(nameof(ProjectFile.ModerationStatus)), null, null) is null)
            {
                becameHeld.Add(entry.Entity);
            }
        }

        foreach (var entry in db.ChangeTracker.Entries<Project>().Where(e => e.State == EntityState.Modified))
        {
            if ((entry.Property(p => p.ContentQuarantinedAt).IsModified && entry.Entity.ContentQuarantinedAt is not null)
                || (entry.Property(p => p.CsamOutputHoldAt).IsModified && entry.Entity.CsamOutputHoldAt is not null))
                markedProjects.Add((entry.Entity.Id, entry.Entity.ContentQuarantinedAt));
        }

        if (becameHeld.Count == 0 && markedProjects.Count == 0) return;
        await StampFilesAsync(db, becameHeld, ct);
        foreach ((Guid projectId, DateTime? marker) in markedProjects.DistinctBy(m => m.Id)) await StampProjectAsync(db, projectId, ct, marker);
    }

    internal static void StampAtVerdict(DbContext db) => StampAtVerdictAsync(db, CancellationToken.None).GetAwaiter().GetResult();

    internal static void StampBeforeRemoval(DbContext db) =>
        StampBeforeRemovalAsync(db, CancellationToken.None).GetAwaiter().GetResult();

    private sealed record Collected(List<ProjectFile> Files, List<ProjectAvatarReference> References, List<Guid> ProjectIds, List<Guid> AvatarIds)
    {
        public bool IsEmpty => Files.Count == 0 && References.Count == 0 && ProjectIds.Count == 0 && AvatarIds.Count == 0;
    }

    private static Collected Collect(DbContext db)
    {
        db.ChangeTracker.DetectChanges();
        return new Collected(
            [.. db.ChangeTracker.Entries<ProjectFile>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity)],
            [.. db.ChangeTracker.Entries<ProjectAvatarReference>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity)],
            [.. db.ChangeTracker.Entries<Project>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity.Id)],
            [.. db.ChangeTracker.Entries<ProjectAvatar>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity.Id)]);
    }
}
