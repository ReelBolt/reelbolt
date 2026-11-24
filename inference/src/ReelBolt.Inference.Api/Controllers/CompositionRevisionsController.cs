using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// Composition revisions, first front-end (docs/design/composition-revisions.md): ask for changes to a
/// finished Remotion film, and read the version chain of the film a run belongs to. Project-scoped like
/// every execution endpoint — the project is resolved through <see cref="IProjectAccessService"/> as the
/// first statement of each action (404 across organizations, never a 403 that leaks existence), and the
/// execution must belong to that project.
/// </summary>
[ApiController]
[Route("api/v1/projects/{projectId:guid}/executions/{executionId:guid}/revisions")]
[Authorize]
public class CompositionRevisionsController : ControllerBase
{
    private readonly IProjectAccessService _projectAccess;
    private readonly ICompositionRevisionService _revisions;
    private readonly ICurrentUser _currentUser;
    private readonly ICreditAccountProvisioner? _trialCredits;

    private readonly ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? _productEvents;

    public CompositionRevisionsController(
        IProjectAccessService projectAccess,
        ICompositionRevisionService revisions,
        ICurrentUser currentUser,
        ICreditAccountProvisioner? trialCredits = null,
        ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null)
    {
        _productEvents = productEvents;
        _projectAccess = projectAccess;
        _revisions = revisions;
        _currentUser = currentUser;
        _trialCredits = trialCredits;
    }

    /// <summary>The run's lineage: versions, revisions, availability and the expected cost of one more.</summary>
    [HttpGet]
    public async Task<ActionResult<CompositionRevisionHistoryResponse>> List(Guid projectId, Guid executionId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        CompositionRevisionHistoryResponse? history = await _revisions.GetHistoryAsync(
            access.Project!, executionId, _currentUser.OrganizationId, ct);
        return history is null ? NotFound() : Ok(history);
    }

    /// <summary>
    /// Ask for changes. The checks run in the order the execute endpoint uses — email verification 403,
    /// project 404, trial grant, then (inside the service) validation 400, run 404/409, rate limit 429,
    /// the plan's cap on the inherited output resolution (402 PLAN_NOT_ENTITLED) and the credit
    /// preflight 402 — and an accepted request answers 201 with the queued revision.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<CompositionRevisionResponse>> Create(
        Guid projectId, Guid executionId, [FromBody] CreateCompositionRevisionRequest? request, CancellationToken ct)
    {
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;

        if (_trialCredits is not null)
            await _trialCredits.EnsureTrialGrantAsync(_currentUser.UserId, _currentUser.OrganizationId, ct);

        CompositionRevisionRequestResult result;
        try
        {
            result = await _revisions.RequestAsync(
                access.Project!, executionId, _currentUser.UserId, _currentUser.OrganizationId,
                request ?? new CreateCompositionRevisionRequest(null), ct);
        }
        catch (PlanLimitException limit)
        {
            return limit.Violation.ToResult();
        }

        if (result.Created is { } created)
        {
            // product-analytics: a change was requested (accepted). Plain append, no detail.
            if (_productEvents is not null)
            {
                await _productEvents.AppendAsync(
                    _currentUser.UserId, _currentUser.OrganizationId,
                    ReelBolt.Inference.Api.Services.Telemetry.ProductEventNames.ChangeRequested, null, ct);
            }

            return StatusCode(201, created);
        }

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
