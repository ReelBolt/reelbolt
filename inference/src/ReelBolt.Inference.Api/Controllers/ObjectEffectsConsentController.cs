using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>Whether a project may send its video frames to the segmentation vendor to find a named object.</summary>
public sealed record ObjectEffectsConsentResponse(bool Consented, DateTime? ConsentedAt);

/// <summary>
/// The project-level egress consent behind "magic blur / mask" (object analysis sends frames to a segmentation vendor).
/// A UI-only act: it is deliberately not an assistant or MCP tool, and the MCP scope guard admits no REST route but the
/// tool catalog and tool call, so an agent can read the timestamp (through its own tools) but never grant it.
/// Every action resolves the project through <see cref="IProjectAccessService"/> once: 404 across workspaces, 403 for a
/// role below the permission.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/object-effects/consent")]
[Authorize]
public class ObjectEffectsConsentController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser _currentUser;
    private readonly IPublishEndpoint _publish;

    public ObjectEffectsConsentController(
        InferenceApiDbContext db, IProjectAccessService projectAccess, ICurrentUser currentUser, IPublishEndpoint publish)
    {
        _publish = publish;
        _db = db;
        _projectAccess = projectAccess;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<ActionResult<ObjectEffectsConsentResponse>> Get(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        return Ok(ToResponse(access.Project!));
    }

    /// <summary>Records the consent now. Idempotent: an existing timestamp is kept.</summary>
    [HttpPut]
    public async Task<ActionResult<ObjectEffectsConsentResponse>> Grant(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        Project project = access.Project!;
        if (project.ObjectEffectsConsentAt is null)
        {
            project.ObjectEffectsConsentAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return Ok(ToResponse(project));
    }

    /// <summary>
    /// Withdraws the consent and stops work not yet done: the project's still-queued or running object analyses get a stop request
    /// (the same event the stop endpoint publishes, so hold release follows its semantics) and their request rows are marked failed
    /// (<c>CONSENT_WITHDRAWN</c>). A run already past the vendor submit cannot be recalled: the engine finishes the call it made.
    /// Idempotent.
    /// </summary>
    [HttpDelete]
    public async Task<ActionResult<ObjectEffectsConsentResponse>> Revoke(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        Project project = access.Project!;
        if (project.ObjectEffectsConsentAt is not null)
        {
            project.ObjectEffectsConsentAt = null;
            await _db.SaveChangesAsync(ct);
        }

        await CancelInFlightAsync(projectId, ct);
        return Ok(ToResponse(project));
    }

    private async Task CancelInFlightAsync(Guid projectId, CancellationToken ct)
    {
        List<ObjectEffectRequest> inFlight = await _db.ObjectEffectRequests
            .Where(r => r.ProjectId == projectId && r.Status == ObjectEffectRequest.StatusQueued)
            .ToListAsync(ct);
        if (inFlight.Count == 0) return;

        List<Guid> executionIds = inFlight.Select(r => r.ExecutionId).ToList();
        HashSet<Guid> finished = (await _db.WorkflowExecutions.AsNoTracking()
                .Where(e => executionIds.Contains(e.Id) && e.ProjectId == projectId
                    && e.Status != ExecutionStatus.Queued && e.Status != ExecutionStatus.Running)
                .Select(e => e.Id)
                .ToListAsync(ct))
            .ToHashSet();

        DateTime now = DateTime.UtcNow;
        List<Guid> toStop = [];
        foreach (ObjectEffectRequest row in inFlight.Where(r => !finished.Contains(r.ExecutionId)))
        {
            // Conditional on still being queued, so a finalizer that won the race is not overwritten.
            if (await ObjectAnalysisRowWriter.TryFinalizeAsync(
                    _db, row, ObjectEffectRequest.StatusFailed, ObjectAnalysisMessages.ConsentWithdrawn,
                    ObjectAnalysisMessages.For(ObjectAnalysisMessages.ConsentWithdrawn), now, ct))
            {
                toStop.Add(row.ExecutionId);
            }
        }

        foreach (Guid executionId in toStop)
        {
            await _publish.Publish(new WorkflowExecutionStopRequested { ExecutionId = executionId, RequestedByUserId = _currentUser.UserId }, ct);
        }
    }

    private static ObjectEffectsConsentResponse ToResponse(Project project) =>
        new(project.ObjectEffectsConsentAt is not null, project.ObjectEffectsConsentAt);
}
