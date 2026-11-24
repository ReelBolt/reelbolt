using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>What a restore (or its preview) covers: one item, or the whole project.</summary>
public enum ProjectHistoryRestoreScope
{
    /// <summary>The listed items only; needs project Edit.</summary>
    Item,

    /// <summary>Every item of the version, and removal of what the version does not hold; needs project Delete.</summary>
    Project
}

/// <summary>One item the caller asks to restore (scope <see cref="ProjectHistoryRestoreScope.Item"/>).</summary>
public sealed record ProjectHistoryRestoreItemRef(ProjectVersionItemKind Kind, string Key);

/// <summary>An item the preview says cannot be restored, with the reason code.</summary>
public sealed record ProjectHistoryRestoreBlockedItem(
    ProjectVersionItemKind Kind,
    string Key,
    string? Name,
    string Reason);

/// <summary>
/// The <c>restore/preview</c> body (contract section 8): the changes a restore would make (the
/// section-7 change shape), the items it cannot restore, and how many bytes it would copy. Writes nothing.
/// </summary>
public sealed record ProjectHistoryRestorePreview(
    ProjectHistoryRestoreScope Scope,
    IReadOnlyList<ProjectHistoryItemChange> Changes,
    IReadOnlyList<ProjectHistoryRestoreBlockedItem> Blocked,
    long EstimateBytes);

/// <summary>One item a restore applied.</summary>
public sealed record ProjectHistoryRestoreEntry(
    ProjectVersionItemKind Kind,
    string Key,
    string? Name);

/// <summary>One item a restore left alone, with its reason code.</summary>
public sealed record ProjectHistoryRestoreSkipped(
    ProjectVersionItemKind Kind,
    string Key,
    string? Name,
    string Reason);

/// <summary>A notice attached to an item a restore applied (for example a reset egress consent).</summary>
public sealed record ProjectHistoryRestoreNotice(
    ProjectVersionItemKind Kind,
    string Key,
    string Code);

/// <summary>The <c>restore</c> body (contract section 8 step 7): partial success is normal.</summary>
public sealed record ProjectHistoryRestoreResult(
    Guid SafetyVersionId,
    IReadOnlyList<ProjectHistoryRestoreEntry> Restored,
    IReadOnlyList<ProjectHistoryRestoreEntry> Removed,
    IReadOnlyList<ProjectHistoryRestoreSkipped> Skipped,
    IReadOnlyList<ProjectHistoryRestoreNotice> Notices);

/// <summary>The 409 codes a restore answers with (contract section 8 steps 2-3).</summary>
public static class ProjectHistoryRestoreCodes
{
    /// <summary>Project scope: a run of the project is Queued or Running.</summary>
    public const string ProjectRunActive = "PROJECT_RUN_ACTIVE";

    /// <summary>Item scope: the workflow being restored has a run Queued or Running.</summary>
    public const string WorkflowRunActive = "WORKFLOW_RUN_ACTIVE";

    /// <summary>Another restore already holds the project's session lock.</summary>
    public const string RestoreInProgress = "RESTORE_IN_PROGRESS";
}

/// <summary>A restore refused by a guard; the controller maps it to <c>409</c> with the code.</summary>
public sealed class ProjectHistoryRestoreConflictException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>Item scope: none of the requested items is in the version; the controller answers <c>400 NOTHING_TO_RESTORE</c>.</summary>
public sealed class ProjectHistoryNothingToRestoreException()
    : Exception("None of the requested items is in this version.")
{
    public const string ErrorCode = "NOTHING_TO_RESTORE";
}

/// <summary>
/// Restore of a project version (contract section 8, part 1). Authorization is the controller's job:
/// item scope needs Edit, project scope Delete, decided with exactly one
/// <c>IProjectAccessService</c> call before this service runs. This service never re-checks access.
/// </summary>
public interface IProjectHistoryRestoreService
{
    /// <summary>
    /// What restoring <paramref name="versionId"/> would change; writes nothing. Null when the version
    /// is missing or expired (the caller answers 404).
    /// </summary>
    Task<ProjectHistoryRestorePreview?> PreviewAsync(
        Guid projectId,
        Guid versionId,
        ProjectHistoryRestoreScope scope,
        IReadOnlyList<ProjectHistoryRestoreItemRef> items,
        CancellationToken ct);

    /// <summary>
    /// Takes a safety version (cause <c>beforeRestore</c>), then applies the version through the
    /// per-kind handlers. Throws <see cref="ProjectHistoryRestoreConflictException"/> for the section 8
    /// guards. Null when the version is missing or expired.
    /// </summary>
    Task<ProjectHistoryRestoreResult?> RestoreAsync(
        Guid projectId,
        Guid versionId,
        ProjectHistoryRestoreScope scope,
        IReadOnlyList<ProjectHistoryRestoreItemRef> items,
        Guid? userId,
        CancellationToken ct);
}
