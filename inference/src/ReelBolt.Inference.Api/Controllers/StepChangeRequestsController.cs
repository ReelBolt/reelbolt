using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Change requests on any step of a finished run (docs/design/change-requests.md). Project-scoped like every
/// execution endpoint: the project is resolved through <see cref="IProjectAccessService"/> as the first
/// statement of each action (404 across organizations, never a 403 that leaks existence; 403 for a
/// same-organization member who may only view), and the run must belong to that project.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/executions/{executionId:guid}")]
[Authorize]
public class StepChangeRequestsController : ControllerBase
{
    private readonly IProjectAccessService _projectAccess;
    private readonly IStepChangeRequestService _changes;
    private readonly ICurrentUser _currentUser;
    private readonly ICreditAccountProvisioner? _trialCredits;

    public StepChangeRequestsController(
        IProjectAccessService projectAccess,
        IStepChangeRequestService changes,
        ICurrentUser currentUser,
        ICreditAccountProvisioner? trialCredits = null)
    {
        _projectAccess = projectAccess;
        _changes = changes;
        _currentUser = currentUser;
        _trialCredits = trialCredits;
    }

    /// <summary>Per finished step result: whether it can be changed, its items and override catalogue.</summary>
    [HttpGet("change-capabilities")]
    public async Task<ActionResult<StepChangeCapabilitiesResponse>> Capabilities(Guid projectId, Guid executionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        StepChangeCapabilitiesResponse? capabilities = await _changes.GetCapabilitiesAsync(access.Project!, executionId, ct);
        return capabilities is null ? NotFound() : Ok(capabilities);
    }

    /// <summary>The change requests made against this run, each with its own run's status.</summary>
    [HttpGet("change-requests")]
    public async Task<ActionResult<StepChangeRequestListResponse>> List(Guid projectId, Guid executionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        StepChangeRequestListResponse? list = await _changes.ListAsync(access.Project!, executionId, ct);
        return list is null ? NotFound() : Ok(list);
    }

    /// <summary>The price and replay of a request. Writes and queues nothing.</summary>
    [HttpPost("change-requests/estimate")]
    public async Task<ActionResult<StepChangeEstimatePreviewResponse>> Estimate(
        Guid projectId, Guid executionId, [FromBody] CreateStepChangeRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        StepChangeRequestResult result = await _changes.EstimateAsync(
            access.Project!, executionId, _currentUser.OrganizationId, request!, ct);
        return result.Preview is { } preview ? Ok(preview) : Refusal(result);
    }

    /// <summary>
    /// Ask for a change. Email verification 403, project 404/403, trial grant, then (inside the service)
    /// validation 400, run 404/409, rate limit 429, the plan's resolution cap 402 PLAN_NOT_ENTITLED and the
    /// credit preflight 402; an accepted request answers 201 with the request and its run id.
    /// </summary>
    [HttpPost("change-requests")]
    public async Task<ActionResult<StepChangeRequestResponse>> Create(
        Guid projectId, Guid executionId, [FromBody] CreateStepChangeRequest? request, CancellationToken ct)
    {
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (_trialCredits is not null)
            await _trialCredits.EnsureTrialGrantAsync(_currentUser.UserId, _currentUser.OrganizationId, ct);

        StepChangeRequestResult result;
        try
        {
            result = await _changes.RequestAsync(
                access.Project!, executionId, _currentUser.UserId, _currentUser.OrganizationId, request!, ct);
        }
        catch (PlanLimitException limit)
        {
            return limit.Violation.ToResult();
        }

        return result.Created is { } created ? StatusCode(201, created) : Refusal(result);
    }

    private ObjectResult Refusal(StepChangeRequestResult result)
    {
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
