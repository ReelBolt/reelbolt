using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Graphics;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// "Design a graphic" in the picture (thumbnail) editor (docs/editor.md "Designed graphics"): ask the
/// GraphicDesigner agent for one still graphic — a title, a badge, a price tag, a callout — drawn in React
/// on a transparent frame and added to the document as a layer, and follow the requests of a document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a synchronous editor operation.</b> The four sibling controllers' AI operations are vendor calls
/// bounded at 90 seconds; a design is an agent tool loop plus a headless-browser render. So the POST queues
/// an execution of the project's hidden graphics workflow and answers 201 at once, and the editor follows
/// the request through <c>GET .../graphics</c> (and the execution's SSE progress). Reading a finished
/// request SETTLES it — inserts its verified PNG as a layer — through
/// <see cref="IGraphicDesignService.SettleAsync"/>; that write is the deterministic consequence of a request
/// an editor already made, so a viewer's read may complete it.
/// </para>
/// <para>
/// Every action resolves the project through <see cref="IProjectAccessService"/> as its first statement
/// (404 across organizations, never a 403 that leaks existence) and constrains the document to it; the
/// controller is in the reviewed set of project-route controllers (<c>TenantBoundaryInventoryTests</c>).
/// </para>
/// </remarks>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/image-documents")]
[Authorize]
public class EditImageGraphicsController : ControllerBase
{
    private readonly IProjectAccessService _projectAccess;
    private readonly IGraphicDesignService _graphics;
    private readonly ICurrentUser _currentUser;
    private readonly ICreditAccountProvisioner? _trialCredits;

    public EditImageGraphicsController(
        IProjectAccessService projectAccess,
        IGraphicDesignService graphics,
        ICurrentUser currentUser,
        ICreditAccountProvisioner? trialCredits = null)
    {
        _projectAccess = projectAccess;
        _graphics = graphics;
        _currentUser = currentUser;
        _trialCredits = trialCredits;
    }

    /// <summary>The document's graphic requests (newest first, finished ones settled), the cost of one more and whether designing is available.</summary>
    [HttpGet("{documentId:guid}/graphics")]
    public async Task<ActionResult<ImageGraphicsResponse>> List(Guid projectId, Guid documentId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        ImageGraphicsResponse? response = await _graphics.ListAsync(access.Project!, documentId, _currentUser.OrganizationId, ct);
        return response is null
            ? NotFound()
            : Ok(response with
            {
                CanConfigure = _currentUser.IsPlatformAdmin
                    || _currentUser.OrgRole is ReelBolt.Shared.Tenancy.OrganizationRole.Owner or ReelBolt.Shared.Tenancy.OrganizationRole.Admin
            });
    }

    /// <summary>One graphic request (settled first when it has finished).</summary>
    [HttpGet("{documentId:guid}/graphics/{graphicId:guid}")]
    public async Task<ActionResult<ImageGraphicJobDto>> Get(Guid projectId, Guid documentId, Guid graphicId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        ImageGraphicJobDto? job = await _graphics.GetAsync(access.Project!, documentId, graphicId, ct);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>
    /// Ask for a graphic. The checks run in the order the execute endpoints use — email verification 403,
    /// project 404/403, trial grant, then (inside the service) validation 400, document/layer 404, not
    /// revisable 409, rate limit 429, agent not seeded 503 and the credit preflight 402 — and an accepted
    /// request answers 201 with the queued job.
    /// </summary>
    [HttpPost("{documentId:guid}/graphics")]
    public async Task<ActionResult<ImageGraphicJobDto>> Create(
        Guid projectId, Guid documentId, [FromBody] CreateImageGraphicRequest? request, CancellationToken ct)
    {
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (_trialCredits is not null)
            await _trialCredits.EnsureTrialGrantAsync(_currentUser.UserId, _currentUser.OrganizationId, ct);

        GraphicDesignRequestResult result;
        try
        {
            result = await _graphics.RequestAsync(
                access.Project!, documentId, _currentUser.UserId, _currentUser.OrganizationId,
                request ?? new CreateImageGraphicRequest(), ExecutionInitiator.Human, ct);
        }
        catch (PlanLimitException limit)
        {
            return limit.Violation.ToResult();
        }

        if (result.Created is { } created)
            return StatusCode(201, created);

        Dictionary<string, object?> body = new() { ["message"] = result.Message };
        if (result.Code is not null)
            body["code"] = result.Code;
        foreach ((string key, object? value) in result.Extra ?? new Dictionary<string, object?>())
            body[key] = value;
        if (result.StatusCode == 429 && result.Extra?.TryGetValue("retryAfterSeconds", out object? retry) == true)
            Response.Headers.RetryAfter = retry?.ToString();
        return StatusCode(result.StatusCode, body);
    }
}
