using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Inference;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// An organization's own (bring-your-own) model catalog — provider accounts → models → capabilities — managed by its
/// Owners and Admins (docs/design/model-catalog.md §6). Same routes and shapes as
/// <see cref="InferenceProvidersController"/> minus the platform-only ones: there are NO price routes, no consolidation
/// report, and every capability answers <c>prices: null</c> (BYO is never priced). Routed under the SINGULAR
/// <c>/api/v1/org/…</c>: nginx sends the plural <c>/api/v1/orgs/…</c> to the Go API and everything else under
/// <c>/api/v1/</c> to this service, so the two never collide.
/// <para>
/// A row in another organization — or a platform-managed one — is <c>404</c> here, never <c>403</c> (decision D5), and
/// so is a model or capability addressed under the wrong parent; a Member or Billing caller in the right org is
/// <c>403</c>; creating, editing or testing without the plan's BYO entitlement is <c>402 byo_not_in_plan</c>. The shared
/// rules (no <c>env:</c> key, no <c>PersonalOAuthMode</c>, no private-address endpoints) live in
/// <see cref="InferenceProviderAdminService"/>.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/org/inference-providers")]
[Authorize]
public class OrgInferenceProvidersController : ControllerBase
{
    private readonly ICurrentUser _currentUser;
    private readonly InferenceProviderAdminService _service;

    public OrgInferenceProvidersController(ICurrentUser currentUser, InferenceProviderAdminService service)
    {
        _currentUser = currentUser;
        _service = service;
    }

    // ------------------------------------------------------------------ provider accounts

    [HttpGet]
    public async Task<ActionResult<List<ProviderResponse>>> List(CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return Ok(await _service.ListAsync(scope, ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ProviderResponse>> Get(Guid id, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return Map(await _service.GetAsync(scope, id, ct));
    }

    [HttpPost]
    public async Task<ActionResult<ProviderResponse>> Create([FromBody] CreateProviderRequest request, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;

        AdminOutcome<ProviderResponse> outcome = await _service.CreateProviderAsync(scope, request, ct);
        return outcome.Status == AdminStatus.Created
            ? CreatedAtAction(nameof(Get), new { id = outcome.Value!.Id }, outcome.Value)
            : Map(outcome);
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ProviderResponse>> Update(Guid id, [FromBody] UpdateProviderRequest request, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return Map(await _service.UpdateProviderAsync(scope, id, request, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return NoContentOr(await _service.DeleteProviderAsync(scope, id, ct));
    }

    /// <summary>The workspace's own one-click picture-tool setup under one of its fal.ai providers.</summary>
    [HttpPost("{id:guid}/image-tools")]
    public async Task<ActionResult<List<ModelResponse>>> CreateImageTools(Guid id, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return Map(await _service.CreateImageToolsAsync(scope, id, ct));
    }

    [HttpPost("test")]
    public async Task<ActionResult<TestInferenceProviderResponse>> TestUnsaved([FromBody] TestProviderRequest request, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return Map(await _service.TestUnsavedAsync(scope, request, ct));
    }

    // ------------------------------------------------------------------ models

    [HttpPost("{id:guid}/models")]
    public async Task<ActionResult<ModelResponse>> CreateModel(Guid id, [FromBody] CreateModelRequest request, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return Map(await _service.CreateModelAsync(scope, id, request, ct));
    }

    [HttpPut("{id:guid}/models/{modelId:guid}")]
    public async Task<ActionResult<ModelResponse>> UpdateModel(
        Guid id, Guid modelId, [FromBody] UpdateModelRequest request, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return Map(await _service.UpdateModelAsync(scope, id, modelId, request, ct));
    }

    [HttpDelete("{id:guid}/models/{modelId:guid}")]
    public async Task<IActionResult> DeleteModel(Guid id, Guid modelId, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return NoContentOr(await _service.DeleteModelAsync(scope, id, modelId, ct));
    }

    // ------------------------------------------------------------------ capabilities

    [HttpPost("{id:guid}/models/{modelId:guid}/capabilities")]
    public async Task<ActionResult<CapabilityResponse>> CreateCapability(
        Guid id, Guid modelId, [FromBody] CreateCapabilityRequest request, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return Map(await _service.CreateCapabilityAsync(scope, id, modelId, request, ct));
    }

    [HttpPut("{id:guid}/models/{modelId:guid}/capabilities/{capId:guid}")]
    public async Task<ActionResult<CapabilityResponse>> UpdateCapability(
        Guid id, Guid modelId, Guid capId, [FromBody] UpdateCapabilityRequest request, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return Map(await _service.UpdateCapabilityAsync(scope, id, modelId, capId, request, ct));
    }

    [HttpDelete("{id:guid}/models/{modelId:guid}/capabilities/{capId:guid}")]
    public async Task<IActionResult> DeleteCapability(Guid id, Guid modelId, Guid capId, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;
        return NoContentOr(await _service.DeleteCapabilityAsync(scope, id, modelId, capId, ct));
    }

    [HttpPost("{id:guid}/models/{modelId:guid}/capabilities/{capId:guid}/test")]
    public async Task<ActionResult<TestInferenceProviderResponse>> TestCapability(
        Guid id, Guid modelId, Guid capId, CancellationToken ct)
    {
        if (Deny(out ProviderScope scope) is { } denied) return denied;

        AdminOutcome<ProviderTestOutcome> outcome = await _service.TestCapabilityAsync(scope, id, modelId, capId, ct);
        if (outcome.Status != AdminStatus.Ok) return MapStatus(outcome.Status, outcome.Body);
        return outcome.Value!.Rejection is { } rejection
            ? BadRequest(new { error = rejection })
            : Ok(outcome.Value.Result);
    }

    /// <summary>Null when the caller is an Owner/Admin of the active org; otherwise the refusal.</summary>
    private ActionResult? Deny(out ProviderScope scope) => OrgAdminGate.Check(this, _currentUser, out scope);

    private IActionResult NoContentOr(AdminOutcome<object> outcome) =>
        outcome.Status == AdminStatus.Ok ? NoContent() : MapStatus(outcome.Status, outcome.Body);

    private ActionResult<T> Map<T>(AdminOutcome<T> outcome) => outcome.Status == AdminStatus.Ok
        ? Ok(outcome.Value)
        : OrgAdminGate.MapStatus(this, outcome.Status, outcome.Body);

    private ActionResult MapStatus(AdminStatus status, object? body) => OrgAdminGate.MapStatus(this, status, body);
}

/// <summary>The org-admin authority check and outcome mapping shared by the two <c>/api/v1/org/…</c> provider controllers.</summary>
internal static class OrgAdminGate
{
    public static ActionResult? Check(ControllerBase controller, ICurrentUser user, out ProviderScope scope)
    {
        scope = default;
        // No active org / no membership: the resource is simply not there for this caller.
        if (user.OrganizationId is not { } orgId || user.OrgRole is null) return controller.NotFound();
        // Same org, insufficient role (Member, Billing): refused.
        if (user.OrgRole is not (OrganizationRole.Owner or OrganizationRole.Admin)) return controller.Forbid();
        scope = ProviderScope.Org(orgId);
        return null;
    }

    public static ActionResult MapStatus(ControllerBase controller, AdminStatus status, object? body) => status switch
    {
        AdminStatus.BadRequest => controller.BadRequest(body),
        AdminStatus.NotFound => body is null ? controller.NotFound() : controller.NotFound(body),
        AdminStatus.Conflict => controller.Conflict(body),
        AdminStatus.PaymentRequired => controller.StatusCode(StatusCodes.Status402PaymentRequired, body),
        _ => controller.StatusCode(StatusCodes.Status500InternalServerError)
    };
}
