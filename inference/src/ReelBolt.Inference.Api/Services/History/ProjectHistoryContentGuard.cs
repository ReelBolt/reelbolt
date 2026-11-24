using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Services.History;

/// <summary>
/// The one guard every history path that serves a file's bytes passes (item diff, item content download,
/// restore source and the layer-1 restorability flag). A history copy of a held output must not become a
/// way around the project's CSAM output hold, so the guard checks both sides:
/// <list type="bullet">
/// <item>the historical version: when its capture time predates the project's
/// <see cref="Project.ContentQuarantinedAt"/> or <see cref="Project.CsamOutputHoldAt"/> (plus the
/// clock-skew margin), or the output marker says so;</item>
/// <item>the historical File item itself: when it was platform-made and its own production stamp is held
/// (<see cref="OutputGate.HoldsFileStamp(ProjectOutputHoldState, DateTime?)"/>, falling back to the
/// version's capture time when the stamp is missing), or when it predates
/// <see cref="Project.ContentQuarantinedAt"/>. This is what keeps a held output refused after its live
/// row is gone or has been overwritten;</item>
/// <item>the LIVE file: when the output hold holds its stamp
/// (<see cref="OutputGate.HoldsFileStamp(ProjectOutputHoldState, DateTime?)"/>), or when it predates
/// <see cref="Project.ContentQuarantinedAt"/>.</item>
/// </list>
/// <para>
/// One instance belongs to one request: it reads <see cref="ProjectOutputHold.ReadAsync"/> once and
/// reuses the result. A failed read (<see cref="ProjectOutputHoldState.Failed"/>) holds everything.
/// </para>
/// </summary>
public sealed class ProjectHistoryContentGuard
{
    private readonly DbContext _db;
    private readonly Guid _projectId;
    private ProjectOutputHoldState? _state;

    public ProjectHistoryContentGuard(DbContext db, Guid projectId)
    {
        _db = db;
        _projectId = projectId;
    }

    /// <summary>The project's output marker, read at most once for this guard (one request).</summary>
    public async Task<ProjectOutputHoldState> HoldAsync(CancellationToken ct)
    {
        if (_state is { } cached)
            return cached;

        ProjectOutputHoldState state = await ProjectOutputHold.ReadAsync(_db, _projectId, ct);
        _state = state;
        return state;
    }

    /// <summary>
    /// Null when the bytes may be served; otherwise the refusal reason
    /// (<see cref="ProjectHistoryRestoreReasons.QuarantineMarker"/> for the historical side,
    /// <see cref="ProjectHistoryRestoreReasons.OutputHeld"/> for the live row).
    /// </summary>
    /// <param name="project">The project, or null when it could not be loaded.</param>
    /// <param name="versionCreatedAt">The version's capture time; null for the live manifest side.</param>
    /// <param name="live">The live row for the item, or null when the row is gone.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="platformMade">
    /// The historical item's <see cref="ProjectVersionItem.PlatformMade"/> stamp: the platform made its bytes.
    /// </param>
    /// <param name="outputProducedAt">
    /// The historical item's <see cref="ProjectVersionItem.OutputProducedAt"/> stamp, so a held output stays
    /// refused even when its live row is gone or now holds other bytes. Null falls back to the capture time.
    /// </param>
    public async Task<string?> RefusalAsync(
        Project? project,
        DateTime? versionCreatedAt,
        ProjectFile? live,
        CancellationToken ct,
        bool platformMade = false,
        DateTime? outputProducedAt = null)
    {
        ProjectOutputHoldState hold = await HoldAsync(ct);

        // The historical side: a version captured at or before the project's markers may carry bytes
        // that must no longer be served. A failed hold read lands here too (HoldsFileStamp is active).
        if (versionCreatedAt is { } capturedAt)
        {
            if (project is not null
                && (ContentQuarantineRule.Predates(project.ContentQuarantinedAt, capturedAt)
                    || ContentQuarantineRule.Predates(project.CsamOutputHoldAt, capturedAt)))
            {
                return ProjectHistoryRestoreReasons.QuarantineMarker;
            }

            if (OutputGate.HoldsFileStamp(hold, capturedAt))
                return ProjectHistoryRestoreReasons.QuarantineMarker;
        }

        // The historical item's own production stamp: a platform-made output that the hold covers is not
        // served through its historical copy either, whether its live row is still there or already gone.
        if (platformMade)
        {
            DateTime? stamp = outputProducedAt ?? versionCreatedAt;
            if (OutputGate.HoldsFileStamp(hold, stamp))
                return ProjectHistoryRestoreReasons.OutputHeld;

            if (project is not null
                && ContentQuarantineRule.Predates(project.ContentQuarantinedAt, outputProducedAt))
            {
                return ProjectHistoryRestoreReasons.OutputHeld;
            }
        }

        // The live side follows the platform's own serving rule (OutputGate.IsFileHeldAsync): a platform-made
        // file (an output, an output copy, a derived file) under the CSAM output hold is held, through picture
        // exports too, while a user's own upload stays available. A platform-made file produced at or before the
        // content-quarantine marker is held as well (ContentQuarantineRule, docs/csam.md).
        if (live is not null)
        {
            bool liveMadeByPlatform = ProjectOutputHold.IsPlatformMade(live.ProjectId, live.StorageKey, live.OutputProducedAt);
            if (hold.IsActive && await OutputGate.IsFileHeldAsync(_db, live, ct))
                return ProjectHistoryRestoreReasons.OutputHeld;

            if (liveMadeByPlatform
                && project is not null
                && ContentQuarantineRule.Predates(project.ContentQuarantinedAt, live.OutputProducedAt))
            {
                return ProjectHistoryRestoreReasons.OutputHeld;
            }
        }

        return null;
    }
}
