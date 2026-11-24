using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Media;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// "Enhance this video": queues a HIDDEN one-step <see cref="WorkflowDefinitionKind.VideoEnhance"/> workflow whose single step is a
/// <see cref="StepType.VideoEnhance"/>. The gate, estimate, queue, status and stop logic lives in <see cref="IVideoEnhanceService"/>
/// (shared with the assistant); this controller authorizes the project once (one <see cref="IProjectAccessService"/> consult per
/// action) and maps the service's result to HTTP. Every failure to find the source is a 404 (never a 403), and no response carries a
/// storage key, bucket or URL.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/media/{mediaRef}/enhance")]
[Authorize]
public class VideoEnhanceController : ControllerBase
{
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IVideoEnhanceService _service;

    public VideoEnhanceController(ICurrentUser currentUser, IProjectAccessService projectAccess, IVideoEnhanceService service)
    {
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _service = service;
    }

    [HttpPost("estimate")]
    public async Task<ActionResult<VideoEnhanceEstimate>> Estimate(
        Guid projectId, string mediaRef, [FromBody] VideoEnhanceRequestBody? body, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        return Map(await _service.EstimateAsync(CallerOf(access.Project!), mediaRef, body, ct));
    }

    [HttpPost]
    public async Task<ActionResult<VideoEnhanceQueuedResponse>> Request(
        Guid projectId, string mediaRef, [FromBody] VideoEnhanceRequestBody? body, CancellationToken ct)
    {
        // A run like any other: an unverified address cannot start one (before anything about the project is looked at).
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        return Map(await _service.RequestAsync(CallerOf(access.Project!), mediaRef, body, ct));
    }

    [HttpGet("{executionId:guid}")]
    public async Task<ActionResult<VideoEnhanceStatusResponse>> Get(Guid projectId, string mediaRef, Guid executionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        return Map(await _service.GetAsync(projectId, mediaRef, executionId, ct));
    }

    /// <summary>Stops the hidden run through the same stop event as <c>WorkflowsController.StopExecution</c> (the web has no workflow id). 202 stopping, 409 finished, 404 otherwise.</summary>
    [HttpPost("{executionId:guid}/cancel")]
    public async Task<ActionResult<object>> Cancel(Guid projectId, string mediaRef, Guid executionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        return Map(await _service.CancelAsync(projectId, mediaRef, executionId, _currentUser.UserId, ct));
    }

    private VideoEnhanceCaller CallerOf(Project project) =>
        new(_currentUser.UserId, project.OrganizationId, project, _currentUser.EmailVerified);

    private ActionResult<T> Map<T>(VideoEnhanceResult<T> result)
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
