using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Shared.Avatars;

/// <summary>
/// Reads the bytes of one reference file and returns their lower-case hex SHA-256, or null when the object is gone or
/// cannot be read (which the rule treats as missing: fail closed). Cancellation propagates. The engine reads the object
/// store directly; the Inference API reads it through its file storage service. Both hash the same bytes the same way.
/// </summary>
public delegate Task<string?> AvatarReferenceDigest(ProjectFile file, CancellationToken ct);

/// <summary>
/// The one place a reference row becomes an <see cref="AvatarReferenceState"/> for <see cref="ProjectAvatar.IsSelectable"/>
/// (docs/avatars.md "The selectability rule"). The Inference API's tool and the WorkflowEngine's
/// <c>IAvatarResolver</c> both call it on their own contexts, so they observe a reference by the same signals:
/// <list type="bullet">
/// <item><b>Present</b>: the file row exists in the avatar's project (query filters bypassed: a hidden row is held, not
/// gone) AND its bytes now hash to the attested <see cref="ProjectAvatarReference.ContentSha256"/>.</item>
/// <item><b>Held</b>: the file is quarantined by CSAM matching or ingest moderation; or it is a platform-made output at
/// or before the project's content-quarantine marker (the same rule as the engine's file query filter); or it is a
/// platform-made output the CSAM output hold covers (<see cref="ProjectOutputHold"/>, an unreadable hold state holds).</item>
/// </list>
/// A held file's bytes are never read: it is observed as present (the row exists) and held, so the rule answers
/// <c>reference_held</c> for it. A file whose key is outside <c>projects/{projectId}/</c> is observed as missing.
/// Works on any context that maps <see cref="Project"/>, <see cref="ProjectFile"/>, <see cref="WorkflowStepResult"/>
/// and <see cref="WorkflowExecution"/>.
/// </summary>
public static class AvatarReferenceObserver
{
    /// <summary>
    /// One state per reference, in the order given. Reads the project's markers once for the whole list. A failed read
    /// of the CSAM output hold holds every platform-made reference; any other read failure throws (the caller fails
    /// closed).
    /// </summary>
    public static async Task<IReadOnlyList<AvatarReferenceState>> ObserveAsync(
        DbContext db,
        Guid projectId,
        IReadOnlyList<ProjectAvatarReference> references,
        AvatarReferenceDigest digest,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(digest);
        if (references.Count == 0)
            return [];

        Guid[] fileIds = references.Select(r => r.ProjectFileId).Distinct().ToArray();
        Dictionary<Guid, ProjectFile> files = await db.Set<ProjectFile>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(f => f.ProjectId == projectId && fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        DateTime? contentMarker = await db.Set<Project>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.ContentQuarantinedAt)
            .FirstOrDefaultAsync(ct);
        ProjectOutputHoldState hold = await ProjectOutputHold.ReadAsync(db, projectId, ct);
        HashSet<string> heldOwnerKeys = await HeldOwnerKeysAsync(db, projectId, hold, files.Values, ct);

        List<AvatarReferenceState> states = new(references.Count);
        foreach (ProjectAvatarReference reference in references)
        {
            if (!files.TryGetValue(reference.ProjectFileId, out ProjectFile? file) || !IsInsideProject(projectId, file.StorageKey))
            {
                states.Add(reference.Observe(currentSha256: null, held: false));
                continue;
            }

            if (IsHeld(projectId, file, contentMarker, hold, heldOwnerKeys))
            {
                // Never read held bytes: the row exists, the rule answers reference_held.
                states.Add(new AvatarReferenceState(reference.Kind, Present: true, Held: true));
                continue;
            }

            string? current = await digest(file, ct);
            states.Add(reference.Observe(current, held: false));
        }

        return states;
    }

    /// <summary>
    /// Whether the file is held by any of the signals above, given the project's content-quarantine marker, its
    /// output-hold state and the storage keys of step results the hold covers. Pure, so both hosts and the tests share it.
    /// </summary>
    public static bool IsHeld(
        Guid projectId, ProjectFile file, DateTime? contentQuarantinedAt, ProjectOutputHoldState hold, IReadOnlySet<string> heldOwnerKeys)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.CsamStatus == CsamScanStatus.Quarantined || file.ModerationStatus == FileModerationStatus.Quarantined)
            return true;

        // Same predicate as the engine's ProjectFile query filter: an output made at or before the marker plus margin.
        if (file.OutputProducedAt is { } produced && contentQuarantinedAt is { } marker
            && produced <= marker + ContentQuarantineRule.ClockSkewMargin)
            return true;

        if (!hold.IsActive || !ProjectOutputHold.IsPlatformMade(projectId, file.StorageKey, file.OutputProducedAt))
            return false; // a user's own upload is never held by the output hold
        return hold.Unreadable
            || hold.HoldsFile(ProjectOutputHold.FileStamp(file.OutputProducedAt, file.UploadedAt))
            || heldOwnerKeys.Contains(file.StorageKey);
    }

    private static bool IsInsideProject(Guid projectId, string? storageKey) =>
        !string.IsNullOrEmpty(storageKey) && storageKey.StartsWith($"projects/{projectId}/", StringComparison.Ordinal);

    private static async Task<HashSet<string>> HeldOwnerKeysAsync(
        DbContext db, Guid projectId, ProjectOutputHoldState hold, IEnumerable<ProjectFile> files, CancellationToken ct)
    {
        if (!hold.IsActive || hold.Unreadable)
            return [];
        string[] keys = files
            .Where(f => ProjectOutputHold.IsPlatformMade(projectId, f.StorageKey, f.OutputProducedAt))
            .Select(f => f.StorageKey)
            .Distinct()
            .ToArray();
        if (keys.Length == 0)
            return [];

        var owners = await db.Set<WorkflowStepResult>()
            .AsNoTracking()
            .Where(r => r.WorkflowExecution.ProjectId == projectId
                && (keys.Contains(r.OutputStorageKey!) || keys.Contains(r.ArtifactStorageKey!)))
            .Select(r => new { r.OutputStorageKey, r.ArtifactStorageKey, r.ExecutedAt, r.WorkflowExecution.StartedAt, r.WorkflowExecution.FirstStartedAt })
            .ToListAsync(ct);

        HashSet<string> held = new(StringComparer.Ordinal);
        foreach (var owner in owners.Where(o => hold.HoldsOutput(o.ExecutedAt, ProjectOutputHold.RunStart(o.FirstStartedAt, o.StartedAt))))
        {
            if (owner.OutputStorageKey is not null) held.Add(owner.OutputStorageKey);
            if (owner.ArtifactStorageKey is not null) held.Add(owner.ArtifactStorageKey);
        }
        return held;
    }
}
