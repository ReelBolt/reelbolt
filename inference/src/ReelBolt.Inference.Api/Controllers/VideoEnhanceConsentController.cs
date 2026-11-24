using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The project-level egress consent behind the hosted video enhancements (AI upscale, AI smooth slow motion upload the source video
/// to a hosted model). A UI-only act, a twin of <see cref="ObjectEffectsConsentController"/>: it is deliberately not an assistant or MCP
/// tool, and the MCP scope guard admits no REST route but the tool catalog and tool call, so an agent can read the timestamp (through
/// its own tools) but never grant it. Every action resolves the project through <see cref="IProjectAccessService"/> once: 404 across
/// workspaces, 403 for a role below the permission.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/video-enhance/consent")]
[Authorize]
public class VideoEnhanceConsentController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly IProjectAccessService _projectAccess;
    private readonly ICurrentUser _currentUser;
    private readonly IPublishEndpoint _publish;

    public VideoEnhanceConsentController(
        InferenceApiDbContext db, IProjectAccessService projectAccess, ICurrentUser currentUser, IPublishEndpoint publish)
    {
        _db = db;
        _projectAccess = projectAccess;
        _currentUser = currentUser;
        _publish = publish;
    }

    [HttpGet]
    public async Task<ActionResult<VideoEnhanceConsentResponse>> Get(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        return Ok(ToResponse(access.Project!));
    }

    /// <summary>Records the consent now. Idempotent: an existing timestamp is kept.</summary>
    [HttpPut]
    public async Task<ActionResult<VideoEnhanceConsentResponse>> Grant(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        Project project = access.Project!;
        if (project.VideoEnhanceConsentAt is null)
        {
            project.VideoEnhanceConsentAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        return Ok(ToResponse(project));
    }

    /// <summary>
    /// Withdraws the consent and stops work not yet done: the project's still-queued or running HOSTED enhancements (of any workflow kind) get a stop request
    /// (the same event the stop endpoint publishes, so hold release follows its semantics). A run already past the vendor submit
    /// cannot be recalled: the engine finishes the call it made. Idempotent. Free local runs are left alone.
    /// </summary>
    [HttpDelete]
    public async Task<ActionResult<VideoEnhanceConsentResponse>> Revoke(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        Project project = access.Project!;
        if (project.VideoEnhanceConsentAt is not null)
        {
            project.VideoEnhanceConsentAt = null;
            await _db.SaveChangesAsync(ct);
        }

        await StopInFlightHostedAsync(projectId, ct);
        return Ok(ToResponse(project));
    }

    private async Task StopInFlightHostedAsync(Guid projectId, CancellationToken ct)
    {
        var inFlight = await _db.WorkflowExecutions.AsNoTracking()
            // Every queued or running run that has a VideoEnhance step, of ANY workflow kind (an editor's workflow can carry a hosted
            // enhance step too): the engine refuses it without the consent anyway, and this stops the ones already waiting.
            .Where(e => e.ProjectId == projectId && e.WorkflowDefinition.Steps.Any(s => s.StepType == StepType.VideoEnhance)
                && (e.Status == ExecutionStatus.Queued || e.Status == ExecutionStatus.Running))
            .Select(e => new
            {
                e.Id,
                // EVERY VideoEnhance step of the run (a workflow can carry more than one), not only the first.
                Configs = e.WorkflowDefinition.Steps
                    .Where(s => s.StepType == StepType.VideoEnhance)
                    .Select(s => s.VideoEnhanceConfigJson)
                    .ToList()
            })
            .ToListAsync(ct);

        foreach (var run in inFlight.Where(r => r.Configs.Any(IsHosted)))
        {
            await _publish.Publish(new WorkflowExecutionStopRequested { ExecutionId = run.Id, RequestedByUserId = _currentUser.UserId }, ct);
        }
    }

    /// <summary>A config that cannot be read is treated as hosted: stopping it is the safe side.</summary>
    private static bool IsHosted(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson)) return true;
        try
        {
            return JsonSerializer.Deserialize<VideoEnhanceStepConfig>(configJson, VideoEnhanceStepConfig.JsonOptions)?.IsHosted ?? true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static VideoEnhanceConsentResponse ToResponse(Project project) =>
        new(project.VideoEnhanceConsentAt is not null, project.VideoEnhanceConsentAt);
}
