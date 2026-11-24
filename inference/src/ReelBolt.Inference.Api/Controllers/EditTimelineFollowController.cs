using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Follow;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// "Follow this subject", step two: the camera path of a tracked subject for one clip of an editor timeline, as transform keys
/// anchored to source time. Computes and answers; writes nothing (no timeline change, no revision, no credits). The action
/// authorizes the project once (Edit); a foreign or missing project, timeline or track is a 404, a role too low a 403.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/edit-timelines/{timelineId:guid}/follow")]
[Authorize]
public class EditTimelineFollowController : ControllerBase
{
    private readonly IProjectAccessService _projectAccess;
    private readonly FollowPathService _service;

    public EditTimelineFollowController(IProjectAccessService projectAccess, FollowPathService service)
    {
        _projectAccess = projectAccess;
        _service = service;
    }

    [HttpPost]
    public async Task<ActionResult<FollowResponse>> Follow(
        Guid projectId, Guid timelineId, [FromBody] FollowRequestBody? body, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        return FollowResults.Map(this, await _service.ComputeAsync(projectId, timelineId, body, ct));
    }
}
