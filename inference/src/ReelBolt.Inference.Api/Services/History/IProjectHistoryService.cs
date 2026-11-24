using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.History;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// Makes and reads project versions (contract section 4), and the layer-2 diff reads (section 7).
/// Capture is serialized per project, so two concurrent captures cannot allocate the same
/// <see cref="ProjectVersion.Number"/>.
/// </summary>
public interface IProjectHistoryService : IProjectHistoryDiffService
{
    /// <summary>
    /// Captures the project's live manifest as a new version, or returns the newest version when an
    /// unchanged project is captured again (except for <c>named</c> and <c>beforeRestore</c>, which
    /// always create a row). Returns null when the project does not exist.
    /// </summary>
    Task<ProjectVersion?> CaptureAsync(
        Guid projectId,
        string cause,
        string? name,
        Guid? userId,
        CancellationToken ct);

    /// <summary>The same item list capture would persist, without writing anything.</summary>
    Task<ProjectHistoryManifest?> BuildLiveManifestAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Best-effort automatic capture for a trigger (run start, assistant write, idle sweep): owned
    /// timeout, never throws, never blocks the caller's action. A no-op when
    /// <see cref="ProjectHistoryOptions.AutoCapture"/> is off.
    /// </summary>
    Task TryCaptureAutoAsync(Guid projectId, string cause, Guid? userId, CancellationToken ct);

    /// <summary>
    /// The project's non-expired versions, newest first, for the REST list (contract section 5):
    /// <paramref name="beforeNumber"/> pages backwards, <paramref name="limit"/> is already clamped
    /// by the caller. Returns null when the project does not exist.
    /// </summary>
    Task<ProjectHistoryVersionsPage?> ListVersionsAsync(
        Guid projectId,
        int limit,
        int? beforeNumber,
        CancellationToken ct);

    /// <summary>One non-expired version with its items and per-item restorability; null when missing.</summary>
    Task<ProjectHistoryVersionDetail?> GetVersionAsync(Guid projectId, Guid versionId, CancellationToken ct);

    /// <summary>The version's list-row projection, without its items; null when missing or expired.</summary>
    Task<ProjectHistoryVersionInfo?> GetVersionInfoAsync(Guid projectId, Guid versionId, CancellationToken ct);

    /// <summary>
    /// Names (or renames) a version. A null/blank name makes it automatic again: the cause is kept
    /// and it expires after the retention window. Null when the version is missing or expired.
    /// Throws <see cref="ProjectHistoryNamedVersionLimitException"/> when naming would exceed the cap.
    /// </summary>
    Task<ProjectHistoryVersionInfo?> RenameVersionAsync(
        Guid projectId,
        Guid versionId,
        string? name,
        CancellationToken ct);

    /// <summary>Deletes a version row; the retention GC frees its unreferenced bytes later.</summary>
    Task<bool> DeleteVersionAsync(Guid projectId, Guid versionId, CancellationToken ct);

    /// <summary>
    /// True when the project's output hold is set (or unreadable) and the version still holds a
    /// platform-made file item, so deleting the version would destroy held output history.
    /// </summary>
    Task<bool> IsVersionDeletionHeldAsync(Guid projectId, Guid versionId, CancellationToken ct);

    /// <summary>
    /// True when the project's output hold is set (or unreadable) and any version holds a
    /// platform-made item for <paramref name="fileId"/>, or every content the erase would touch is
    /// named by a platform-made item of another key, so "Delete forever" would destroy evidence.
    /// </summary>
    Task<bool> IsFileDeletionHeldAsync(Guid projectId, Guid fileId, CancellationToken ct);
}
