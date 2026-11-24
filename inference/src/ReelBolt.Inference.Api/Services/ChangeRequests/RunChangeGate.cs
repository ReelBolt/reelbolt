using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Storage;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.ChangeRequests;

/// <summary>
/// The one up-front test of whether a finished run may still be changed (a step change request, an estimate for one, or a
/// composition revision). A run that first started at or before its project's quarantine marker plus the skew margin
/// (<see cref="ContentQuarantineRule"/>), or whose marker cannot be read, is refused before anything is spent; the engine's
/// own gates stay as the second line. Neutral text and code: the answer says nothing about why.
/// </summary>
public static class RunChangeGate
{
    public const string Code = "RUN_CLOSED";
    public const string Message = "This run can no longer be changed. Start a new run.";
    public const int StatusCode = 409;

    /// <summary>True when <paramref name="execution"/> must not be changed. Cancellation propagates.</summary>
    public static async Task<bool> IsClosedAsync(InferenceApiDbContext db, Guid projectId, WorkflowExecution execution, CancellationToken ct)
    {
        try
        {
            DateTime? marker = await db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Id == projectId)
                .Select(p => p.ContentQuarantinedAt)
                .FirstOrDefaultAsync(ct);
            return ContentQuarantineRule.Predates(marker, execution.FirstStartedAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }
}
