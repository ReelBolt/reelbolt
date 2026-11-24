using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Diff and content reads for project versions (contract section 7, and the last row of section 5).
/// A target is a version id or, when null, the live project.
/// </summary>
public interface IProjectHistoryDiffService
{
    /// <summary>
    /// The items that differ between <paramref name="fromVersionId"/> and
    /// <paramref name="toVersionId"/> (null = the live project). Null when either side is missing or
    /// expired, so the caller answers 404.
    /// </summary>
    Task<ProjectHistoryChanges?> ComputeChangesAsync(
        Guid projectId,
        Guid fromVersionId,
        Guid? toVersionId,
        CancellationToken ct);

    /// <summary>
    /// One item's diff between a version and another version or the live project. Null when the item
    /// does not exist in the source version (a removed item is still diffed, against an empty target).
    /// </summary>
    Task<ProjectHistoryItemDiff?> ComputeItemDiffAsync(
        Guid projectId,
        Guid fromVersionId,
        ProjectVersionItemKind kind,
        string key,
        Guid? toVersionId,
        CancellationToken ct);

    /// <summary>
    /// A file item's bytes at the version, from its protected blob or, when no blob exists, from the
    /// live object after verifying its ETag. Null when the item is not a file, is erased, is
    /// quarantined, predates a quarantine marker, or its bytes are no longer provably the captured ones.
    /// </summary>
    Task<ProjectHistoryItemContent?> GetFileContentAsync(
        Guid projectId,
        Guid fromVersionId,
        ProjectVersionItemKind kind,
        string key,
        CancellationToken ct);
}
