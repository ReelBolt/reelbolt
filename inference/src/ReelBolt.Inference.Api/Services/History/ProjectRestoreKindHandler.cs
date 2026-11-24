using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Per-request state a restore handler needs: the tracked project, the caller, the version being
/// restored, all of its items (a project-scope handler may need the whole set) and the shared content
/// guard (one output-hold read for the whole request).
/// </summary>
public sealed record ProjectRestoreWriteContext(
    Project Project,
    ProjectVersion Version,
    IReadOnlyList<ProjectVersionItem> VersionItems,
    Guid? UserId,
    ProjectHistoryContentGuard ContentGuard)
{
    /// <summary>
    /// Project scope, file handler: live files moved to a temporary path so a swap or rename chain among
    /// the version's files cannot hit PATH_TAKEN; the value is where the file lived before the move.
    /// </summary>
    public Dictionary<Guid, MovedAsideFile> MovedAside { get; } = new();
}

/// <summary>A file parked at a temporary path during a project-scope restore, with its pre-move location.</summary>
public sealed record MovedAsideFile(string OriginalPath, string? DirectoryPath);

/// <summary>What a handler did with one item.</summary>
public enum ProjectRestoreDisposition
{
    /// <summary>The live project now matches the version for this item.</summary>
    Restored,

    /// <summary>A live item the version does not hold was removed (project scope).</summary>
    Removed,

    /// <summary>Nothing changed; <see cref="ProjectRestoreOutcome.Reason"/> says why.</summary>
    Skipped
}

/// <summary>One handler outcome. A notice may accompany a restored item (contract section 8 step 7).</summary>
public sealed record ProjectRestoreOutcome(
    ProjectVersionItemKind Kind,
    string Key,
    string? Name,
    ProjectRestoreDisposition Disposition,
    string? Reason = null,
    string? Notice = null)
{
    public static ProjectRestoreOutcome Restored(
        ProjectVersionItemKind kind, string key, string? name, string? notice = null) =>
        new(kind, key, name, ProjectRestoreDisposition.Restored, Notice: notice);

    public static ProjectRestoreOutcome Removed(ProjectVersionItemKind kind, string key, string? name) =>
        new(kind, key, name, ProjectRestoreDisposition.Removed);

    public static ProjectRestoreOutcome Skipped(
        ProjectVersionItemKind kind, string key, string? name, string reason) =>
        new(kind, key, name, ProjectRestoreDisposition.Skipped, reason);
}

/// <summary>
/// One kind's restore rules (contract section 8 steps 5-6). The restore service owns the guards, the
/// safety version and the result shape; a handler only applies (or removes) its kind. Kinds with no
/// registered handler are skipped <c>NOT_YET_SUPPORTED</c>, so the workflow and timeline rules
/// (T-010) are added by registering handlers, not by editing the service.
/// </summary>
public interface IProjectRestoreKindHandler
{
    ProjectVersionItemKind Kind { get; }

    /// <summary>Applies one version item to the live project. Called for item and project scope.</summary>
    Task<ProjectRestoreOutcome> ApplyAsync(
        ProjectRestoreWriteContext context,
        ProjectVersionItem item,
        CancellationToken ct);

    /// <summary>Project scope only: runs once before any item is applied (path swaps are parked here).</summary>
    Task PrepareProjectScopeAsync(ProjectRestoreWriteContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Project scope only: runs once after every item was tried (anything still parked goes home).</summary>
    Task CompleteProjectScopeAsync(ProjectRestoreWriteContext context, CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Project scope only: removes live items of this kind whose key is not in
    /// <paramref name="presentKeys"/> (the keys the version holds). Default: nothing to remove.
    /// </summary>
    Task<IReadOnlyList<ProjectRestoreOutcome>> RemoveAbsentAsync(
        ProjectRestoreWriteContext context,
        IReadOnlySet<string> presentKeys,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ProjectRestoreOutcome>>([]);
}
