using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Projects;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Per-customer activity (runs and credits per project tag). Read-only, gated like <c>GET /billing/usage</c>:
/// an active organization and a role in it. With billing off the credits are null and the runs still show.
/// Never returns cost (COGS). The literal <c>activity</c> route sits beside the <c>{id:guid}</c> routes of
/// <see cref="ProjectTagsController"/> and cannot match a guid constraint.
/// </summary>
[ApiController]
[Route("api/v1/project-tags")]
[Authorize]
public class ProjectTagActivityController(
    ProjectTagActivityService activity,
    ICurrentUser currentUser,
    IOptions<BillingOptions> billing) : ControllerBase
{
    [HttpGet("activity")]
    public async Task<IActionResult> Overview([FromQuery] int? days, CancellationToken ct)
    {
        if (currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        return Ok(await activity.OverviewAsync(orgId, ProjectTagActivityService.ClampDays(days), billing.Value.Enabled, ct));
    }

    [HttpGet("{id:guid}/activity")]
    public async Task<IActionResult> Detail(Guid id, [FromQuery] int? days, CancellationToken ct)
    {
        if (currentUser.OrganizationId is not { } orgId || currentUser.OrgRole is null) return NotFound();
        TagActivityDetail? detail = await activity.DetailAsync(orgId, id, ProjectTagActivityService.ClampDays(days), billing.Value.Enabled, ct);
        if (detail is null) return NotFound();
        // A role without project View (Billing) sees the aggregates only: no project names, no run list.
        if (!ProjectAccessService.Allowed(currentUser.OrgRole.Value, ProjectPermission.View, isCreator: false))
            detail = detail with { Projects = [], RecentRuns = [] };
        return Ok(detail);
    }
}
