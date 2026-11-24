using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Execution.Caching;

namespace ReelBolt.WorkflowEngine.Services.Compositions;

/// <summary>
/// The one test of whether a stored composition version may be restored into a sandbox. A version that may predate
/// the project's quarantine marker may hold a copy of a file quarantined since.
/// </summary>
public static class CompositionVersionQuarantine
{
    /// <summary>
    /// True when <paramref name="version"/> must not be restored: its CreatedAt, or its producing run's StartedAt
    /// when one is linked, may predate the marker (<see cref="ProjectQuarantineState.Predates"/>; a Retry overwrites
    /// StartedAt, and a run can capture a version after the marker), or the state cannot be read.
    /// Cancellation propagates.
    /// </summary>
    public static async Task<bool> PredatesAsync(WorkflowEngineDbContext db, CompositionWorkspaceVersion version, CancellationToken ct)
    {
        try
        {
            DateTime? marker = await ProjectQuarantineState.ReadMarkerAsync(db, version.ProjectId, ct);
            if (marker is null)
                return false;

            DateTime? startedAt = version.CreatedAt;
            if (version.CreatedByExecutionId is { } executionId)
            {
                startedAt = await db.WorkflowExecutions.AsNoTracking()
                    .Where(e => e.Id == executionId)
                    .Select(e => e.FirstStartedAt)
                    .FirstOrDefaultAsync(ct);
            }

            return ProjectQuarantineState.Predates(marker, version.CreatedAt, startedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// True when work taken from the run <paramref name="executionId"/> (a sandbox to recover) must not be used: the run
    /// first started at or before the marker plus margin, is not found, or the state cannot be read. Same answer
    /// <see cref="PredatesAsync"/> gives a version. Cancellation propagates.
    /// </summary>
    public static async Task<bool> RunPredatesAsync(WorkflowEngineDbContext db, Guid projectId, Guid executionId, CancellationToken ct)
    {
        try
        {
            DateTime? marker = await ProjectQuarantineState.ReadMarkerAsync(db, projectId, ct);
            if (marker is null)
                return false;

            DateTime? firstStartedAt = await db.WorkflowExecutions.AsNoTracking()
                .Where(e => e.Id == executionId && e.ProjectId == projectId)
                .Select(e => e.FirstStartedAt)
                .FirstOrDefaultAsync(ct);
            return ProjectQuarantineState.Predates(marker, firstStartedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }
}
