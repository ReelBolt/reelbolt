using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Follow;
using ReelBolt.Inference.Api.Services.ObjectAnalysis;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// "Follow this subject", step one: track a drawn box through a video with the free local tracker (a hidden run, no vendor, nothing
/// leaves the host). Each action authorizes the project once (Edit to start, View to read); a foreign or missing project, source or
/// run is a 404, a role too low is a 403. No response carries a storage key or an internal name.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/media/{mediaRef}/subject-tracks")]
[Authorize]
public class SubjectTracksController : ControllerBase
{
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly SubjectTrackService _service;

    public SubjectTracksController(ICurrentUser currentUser, IProjectAccessService projectAccess, SubjectTrackService service)
    {
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _service = service;
    }

    [HttpPost]
    public async Task<ActionResult<SubjectTrackQueuedResponse>> Start(
        Guid projectId, string mediaRef, [FromBody] SubjectTrackRequestBody? body, CancellationToken ct)
    {
        // A run like any other: an unverified address cannot start one (before anything about the project is looked at).
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        ObjectAnalysisCaller caller = new(_currentUser.UserId, access.Project!.OrganizationId, access.Project, _currentUser.EmailVerified);
        return FollowResults.Map(this, await _service.RequestAsync(caller, mediaRef, body, ct));
    }

    [HttpGet("{executionId:guid}")]
    public async Task<ActionResult<SubjectTrackStatusResponse>> Get(Guid projectId, string mediaRef, Guid executionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        return FollowResults.Map(this, await _service.GetAsync(projectId, mediaRef, executionId, ct));
    }
}

/// <summary>Maps a transport-neutral service result to HTTP, as the object-analysis controller does.</summary>
internal static class FollowResults
{
    public static ActionResult<T> Map<T>(ControllerBase c, ObjectAnalysisResult<T> result)
    {
        if (result.Refusal is { } refusal)
        {
            return refusal switch
            {
                { StatusCode: StatusCodes.Status404NotFound, Body: null } => c.NotFound(),
                { StatusCode: StatusCodes.Status400BadRequest } => c.BadRequest(refusal.Body),
                { StatusCode: StatusCodes.Status409Conflict } => c.Conflict(refusal.Body),
                { StatusCode: StatusCodes.Status422UnprocessableEntity } => c.UnprocessableEntity(refusal.Body),
                _ => c.StatusCode(refusal.StatusCode, refusal.Body)
            };
        }

        return result.SuccessStatus == StatusCodes.Status200OK
            ? c.Ok(result.Value)
            : c.StatusCode(result.SuccessStatus, result.Value);
    }
}
