using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Editing;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Editing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// "Find this object in my video": queues a HIDDEN one-step <see cref="WorkflowDefinitionKind.ObjectAnalysis"/> workflow whose single
/// step is a <see cref="StepType.VideoAnalyze"/> with one text-prompted segmentation target. The gate, estimate, queue and status
/// logic lives in <see cref="IObjectAnalysisService"/> (shared with the assistant); this controller authorizes the project once and
/// maps the service's result to HTTP. Every failure to find the source is a 404 (never a 403), and no response carries a storage
/// key or hash. Status is derived from the run on read (the finalizer applies a timeline intent separately).
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/media/{mediaRef}/objects")]
[Authorize]
public class ObjectAnalysisController : ControllerBase
{
    /// <summary>Paid analyses per user per project per rolling hour (the paid bucket's default).</summary>
    public const int PaidRunsPerHour = ObjectAnalysisService.PaidRunsPerHour;

    public const int MaxListed = ObjectAnalysisService.MaxListed;

    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IObjectAnalysisService _service;

    public ObjectAnalysisController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IWorkflowExecutionService executions,
        IFileStorageService storage,
        ILogger<ObjectAnalysisController> logger,
        TimeProvider? clock = null,
        IOrganizationEntitlements? entitlements = null,
        IWorkflowCostEstimator? costEstimator = null,
        IObjectAnalysisService? service = null)
    {
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _service = service ?? new ObjectAnalysisService(db, executions, storage, logger, clock, entitlements, costEstimator);
    }

    [HttpPost("estimate")]
    public async Task<ActionResult<ObjectAnalysisEstimate>> Estimate(
        Guid projectId, string mediaRef, [FromBody] ObjectAnalysisEstimateRequest? body, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        return Map(await _service.EstimateAsync(CallerOf(access.Project!), mediaRef, body, ct));
    }

    [HttpPost]
    public async Task<ActionResult<ObjectAnalysisQueuedResponse>> Request(
        Guid projectId, string mediaRef, [FromBody] ObjectAnalysisRequestBody? body, CancellationToken ct)
    {
        // A run like any other: an unverified address cannot start one (before anything about the project is looked at).
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        return Map(await _service.RequestAsync(CallerOf(access.Project!), mediaRef, body, intent: null, ct));
    }

    [HttpGet("{requestId:guid}")]
    public async Task<ActionResult<ObjectAnalysisResponse>> Get(Guid projectId, string mediaRef, Guid requestId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        return Map(await _service.GetAsync(projectId, mediaRef, requestId, ct));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ObjectAnalysisResponse>>> List(Guid projectId, string mediaRef, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        return Map(await _service.ListAsync(projectId, mediaRef, ct));
    }

    /// <summary>Finished results the timeline changed under: waiting in the editor for apply or discard. Only <c>needs_review</c> is listed.</summary>
    [HttpGet("/api/v1/projects/{projectId:guid}/edit-timelines/{timelineId:guid}/object-requests")]
    public async Task<ActionResult<ObjectRequestsResponse>> ListForTimeline(
        Guid projectId, Guid timelineId, [FromQuery] string? state, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        if (state != ObjectEffectRequest.StatusNeedsReview) return NotFound();
        if (!await _db.EditTimelines.AsNoTracking().AnyAsync(t => t.Id == timelineId && t.ProjectId == projectId, ct)) return NotFound();

        List<ObjectEffectRequest> rows = await _db.ObjectEffectRequests.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.TimelineId == timelineId && r.Status == ObjectEffectRequest.StatusNeedsReview)
            .OrderBy(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(ct);

        List<ObjectRequestItem> items = [];
        foreach (ObjectEffectRequest row in rows)
        {
            ObjectAnalysisDerived derived = await _service.DeriveAsync(row, ct);
            items.Add(new ObjectRequestItem(
                row.Id, row.ExecutionId, $"{row.SourceKind}:{row.SourceId}", ClipIds(row.TimelineItemIdsJson),
                ModelSafeText.Clean(row.Prompt, ObjectEffectRequest.MaxPromptLength), row.Effect, row.Strength, row.Invert, row.FeatherPx,
                derived.CoverageEndSec, derived.StepResultId, derived.TrackIds ?? [], row.CreatedAt,
                await _db.WorkflowExecutions.AsNoTracking().Where(e => e.Id == row.ExecutionId).Select(e => (Guid?)e.WorkflowDefinitionId).FirstOrDefaultAsync(ct)));
        }

        return Ok(new ObjectRequestsResponse(items));
    }

    /// <summary>The user's answer for a finished result: it was applied by the editor (at <c>revision</c>) or discarded. Idempotent.</summary>
    [HttpPost("{requestId:guid}/resolve")]
    public async Task<IActionResult> Resolve(
        Guid projectId, string mediaRef, Guid requestId, [FromBody] ObjectRequestResolveBody? body, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        string[] parts = mediaRef.Split(':', 2);
        if (parts.Length != 2 || !Guid.TryParse(parts[1], out Guid sourceGuid)
            || parts[0] is not (ObjectEffectRequest.SourceFile or ObjectEffectRequest.SourceStepResult)) return NotFound();
        if (body?.Action is not ("applied" or "discarded")) return BadRequest();

        string sourceId = sourceGuid.ToString("D");
        ObjectEffectRequest? row = await _db.ObjectEffectRequests
            .FirstOrDefaultAsync(r => r.Id == requestId && r.ProjectId == projectId && r.SourceKind == parts[0] && r.SourceId == sourceId, ct);
        if (row is null) return NotFound();

        string target = body.Action == "applied" ? ObjectEffectRequest.StatusApplied : ObjectEffectRequest.StatusDiscarded;

        // A row the sweeper claimed and is still applying (applied, no revision yet) is not the client's to resolve: 404, like every other unresolvable row.
        if (IsBeingApplied(row)) return NotFound();
        if (row.Status == target) return NoContent();
        if (row.Status is not (ObjectEffectRequest.StatusSucceeded or ObjectEffectRequest.StatusNeedsReview)) return NotFound();

        // Only a revision that exists on this timeline and is not older than the request is recorded; anything else is ignored.
        // (A recorded revision is excluded from change detection, so an arbitrary client number must never be stored.)
        long? appliedRevision = null;
        if (target == ObjectEffectRequest.StatusApplied && row.TimelineId is { } timelineId)
        {
            DateTime floor = row.CreatedAt - TimeSpan.FromSeconds(1);
            bool real = body.Revision is { } claimed && await _db.EditTimelineOps.AsNoTracking()
                .AnyAsync(o => o.TimelineId == timelineId && o.Revision == claimed && o.CreatedAt >= floor, ct);
            // A row with a timeline intent that carries no revision would look like an abandoned claim to the sweeper: record 0 then.
            appliedRevision = real ? body.Revision : 0;
        }

        // The write only lands if the row is still what was read: serializable on a relational database (the sweeper's claim
        // then conflicts), a plain re-read on InMemory. A lost race is answered from a fresh read.
        string seen = row.Status;
        try
        {
            await using IDbContextTransaction? tx = _db.Database.IsRelational()
                ? await _db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
            await _db.Entry(row).ReloadAsync(ct);
            if (IsBeingApplied(row)) return NotFound();
            if (row.Status == target) return NoContent();
            if (row.Status != seen) return NotFound();
            row.Status = target;
            row.AppliedRevision = appliedRevision;
            row.FinalizedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            if (tx is not null) await tx.CommitAsync(ct);
            return NoContent();
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException && IsSerializationFailure(ex))
        {
            _db.ChangeTracker.Clear();
            ObjectEffectRequest? now = await _db.ObjectEffectRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct);
            if (now is null) return NotFound();
            if (IsBeingApplied(now)) return NotFound();
            return now.Status == target ? NoContent() : NotFound();
        }
    }

    private static bool IsBeingApplied(ObjectEffectRequest row) =>
        row.Status == ObjectEffectRequest.StatusApplied && row.TimelineId != null && row.AppliedRevision == null;

    /// <summary>Postgres SQLSTATE 40001 (a lost serializable race), however EF wrapped it.</summary>
    private static bool IsSerializationFailure(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is Npgsql.PostgresException { SqlState: "40001" }) return true;
        }

        return false;
    }

    private static string[] ClipIds(string? json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<string?>>(json ?? "[]") ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private ObjectAnalysisCaller CallerOf(Project project) =>
        new(_currentUser.UserId, project.OrganizationId, project, _currentUser.EmailVerified);

    private ActionResult<T> Map<T>(ObjectAnalysisResult<T> result)
    {
        if (result.Refusal is { } refusal)
        {
            return refusal switch
            {
                { StatusCode: StatusCodes.Status404NotFound, Body: null } => NotFound(),
                { StatusCode: StatusCodes.Status400BadRequest } => BadRequest(refusal.Body),
                { StatusCode: StatusCodes.Status409Conflict } => Conflict(refusal.Body),
                { StatusCode: StatusCodes.Status422UnprocessableEntity } => UnprocessableEntity(refusal.Body),
                _ => StatusCode(refusal.StatusCode, refusal.Body)
            };
        }

        return result.SuccessStatus == StatusCodes.Status200OK
            ? Ok(result.Value)
            : StatusCode(result.SuccessStatus, result.Value);
    }
}
