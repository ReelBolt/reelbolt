using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

public sealed record PublicRunRequestError(string Code, string Message);

public sealed record PublicRunRequestView(
    Guid Id, string Status, DateTime? EstimatedStartAt, Guid? RunId, PublicRunRequestError? Error,
    DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>
/// Run requests: starts the hourly run limit held back. A row is reached only
/// through the key that created it (and only inside a project that key may see).
/// </summary>
public sealed class RunRequestsController(
    InferenceApiDbContext db,
    IProjectAccessService projectAccess,
    IApiKeyContext keyContext,
    TimeProvider? time = null) : PublicApiControllerBase
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    [HttpGet("projects/{projectId:guid}/run-requests")]
    [RequireScope(ApiScopes.RunsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> List(
        Guid projectId, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
    {
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);
        if (keyContext.KeyId is not Guid keyId) return PublicError(401, PublicApiErrorCodes.ApiKeyRequired, PublicApiErrorCodes.ApiKeyRequiredMessage);

        int take = PublicApiPagination.NormalizeLimit(limit);
        IQueryable<ApiRunRequest> query = db.ApiRunRequests.AsNoTracking().Where(r => r.ApiKeyId == keyId && r.ProjectId == projectId);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!PublicApiPagination.TryDecodeCursor(cursor, out DateTime at, out Guid id))
                return PublicError(400, PublicApiErrorCodes.InvalidRequest, "The cursor is not valid.");
            query = query.Where(r => r.CreatedAt < at || (r.CreatedAt == at && r.Id.CompareTo(id) < 0));
        }

        List<ApiRunRequest> rows = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(take + 1).ToListAsync(ct);
        bool more = rows.Count > take;
        if (more) rows = rows.Take(take).ToList();
        return Ok(new PublicApiPage<PublicRunRequestView>(
            rows.Select(View).ToList(), more ? PublicApiPagination.EncodeCursor(rows[^1].CreatedAt, rows[^1].Id) : null));
    }

    [HttpGet("projects/{projectId:guid}/run-requests/{requestId:guid}")]
    [RequireScope(ApiScopes.RunsRead)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(Guid projectId, Guid requestId, CancellationToken ct)
    {
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        ApiRunRequest? row = await FindAsync(projectId, requestId, track: false, ct);
        return row is null ? PublicError(404, PublicApiErrorCodes.NotFound, "Run request not found.") : Ok(View(row));
    }

    [HttpPost("projects/{projectId:guid}/run-requests/{requestId:guid}/cancel")]
    [Idempotent]
    [RequireScope(ApiScopes.RunsExecute)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Cancel(Guid projectId, Guid requestId, CancellationToken ct)
    {
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        ApiRunRequest? row = await FindAsync(projectId, requestId, track: true, ct);
        if (row is null) return PublicError(404, PublicApiErrorCodes.NotFound, "Run request not found.");

        // Only a request still waiting can be cancelled. The update is conditional so a dispatcher that claims the
        // row between the read and the write wins, and the caller gets the 409.
        DateTime now = _time.GetUtcNow().UtcDateTime;
        int changed;
        if (db.Database.IsRelational())
        {
            changed = await db.ApiRunRequests
                .Where(r => r.Id == requestId && r.Status == ApiRunRequest.Queued)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, ApiRunRequest.Cancelled)
                    .SetProperty(r => r.UpdatedAt, now), ct);
        }
        else
        {
            // Providers without set-based updates (tests): a single writer, so the tracked row is enough.
            changed = row.Status == ApiRunRequest.Queued ? 1 : 0;
            if (changed == 1)
            {
                row.Status = ApiRunRequest.Cancelled;
                row.UpdatedAt = now;
                await db.SaveChangesAsync(ct);
            }
        }

        if (changed == 0)
            return PublicError(409, "run_request_not_cancellable", "Only a queued run request can be cancelled.");

        db.Entry(row).State = EntityState.Detached;
        ApiRunRequest? fresh = await FindAsync(projectId, requestId, track: false, ct);
        return Ok(View(fresh!));
    }

    private async Task<ApiRunRequest?> FindAsync(Guid projectId, Guid requestId, bool track, CancellationToken ct)
    {
        if (keyContext.KeyId is not Guid keyId) return null;
        IQueryable<ApiRunRequest> query = track ? db.ApiRunRequests : db.ApiRunRequests.AsNoTracking();
        return await query.FirstOrDefaultAsync(r => r.Id == requestId && r.ProjectId == projectId && r.ApiKeyId == keyId, ct);
    }

    private static PublicRunRequestView View(ApiRunRequest r) => new(
        r.Id, r.Status, r.Status == ApiRunRequest.Queued ? r.EstimatedStartAt : null, r.ExecutionId,
        r.ErrorCode is null ? null : new PublicRunRequestError(r.ErrorCode, r.ErrorMessage ?? string.Empty),
        r.CreatedAt, r.UpdatedAt);
}
