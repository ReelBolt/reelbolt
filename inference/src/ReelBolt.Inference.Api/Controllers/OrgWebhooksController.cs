using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.PublicApi;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.Webhooks;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

/// <summary>
/// The dashboard twin of the public webhook routes (<c>/api/v1/org/webhooks/**</c>): session principal only (a scoped
/// principal never reaches it), org Owner/Admin, the same <see cref="WebhookEndpointService"/> and error codes.
/// No project restriction applies to a person. Flag off: 404.
/// </summary>
[ApiController]
[Route("api/v1/org/webhooks")]
[Authorize]
public sealed class OrgWebhooksController(
    WebhookEndpointService service,
    ICurrentUser currentUser,
    IOptions<PublicApiOptions> options) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> List(CancellationToken ct) =>
        Gate(out WebhookActor actor) ?? Ok(new { items = await service.ListAsync(actor, ct) });

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] WebhookEndpointCreate? body, CancellationToken ct)
    {
        if (Gate(out WebhookActor actor) is { } denied) return denied;
        WebhookResult<WebhookEndpointView> result = await service.CreateAsync(actor, body ?? new WebhookEndpointCreate(null, null, null, null), ct);
        return result.Failure is { } f ? Refuse(f) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult> Get(Guid id, CancellationToken ct)
    {
        if (Gate(out WebhookActor actor) is { } denied) return denied;
        WebhookResult<WebhookEndpointView> result = await service.GetAsync(actor, id, ct);
        return result.Failure is { } f ? Refuse(f) : Ok(result.Value);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult> Update(Guid id, [FromBody] WebhookEndpointPatch? body, CancellationToken ct)
    {
        if (Gate(out WebhookActor actor) is { } denied) return denied;
        WebhookResult<WebhookEndpointView> result = await service.UpdateAsync(actor, id, body ?? new WebhookEndpointPatch(null, null, null, null, null), ct);
        return result.Failure is { } f ? Refuse(f) : Ok(result.Value);
    }

    [HttpDelete("{id:guid}")]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (Gate(out WebhookActor actor) is { } denied) return denied;
        WebhookResult<WebhookEndpointView> result = await service.DeleteAsync(actor, id, ct);
        return result.Failure is { } f ? Refuse(f) : NoContent();
    }

    [HttpPost("{id:guid}/rotate-secret")]
    public async Task<ActionResult> RotateSecret(Guid id, [FromQuery] bool revokePrevious, CancellationToken ct)
    {
        if (Gate(out WebhookActor actor) is { } denied) return denied;
        WebhookResult<WebhookEndpointView> result = await service.RotateSecretAsync(actor, id, revokePrevious, ct);
        return result.Failure is { } f ? Refuse(f) : Ok(new WebhookSecretView(result.Value!.Secret!));
    }

    [HttpPost("{id:guid}/test")]
    public async Task<ActionResult> Test(Guid id, CancellationToken ct)
    {
        if (Gate(out WebhookActor actor) is { } denied) return denied;
        WebhookResult<WebhookDeliveryView> result = await service.TestAsync(actor, id, ct);
        return result.Failure is { } f ? Refuse(f) : StatusCode(StatusCodes.Status202Accepted, result.Value);
    }

    [HttpGet("{id:guid}/deliveries")]
    public async Task<ActionResult> Deliveries(Guid id, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
    {
        if (Gate(out WebhookActor actor) is { } denied) return denied;
        WebhookResult<WebhookDeliveryPage> result = await service.ListDeliveriesAsync(actor, id, limit, cursor, ct);
        return result.Failure is { } f ? Refuse(f) : Ok(new { data = result.Value!.Data, nextCursor = result.Value.NextCursor });
    }

    [HttpPost("{id:guid}/deliveries/{deliveryId}/redeliver")]
    public async Task<ActionResult> Redeliver(Guid id, string deliveryId, CancellationToken ct)
    {
        if (Gate(out WebhookActor actor) is { } denied) return denied;
        WebhookResult<WebhookDeliveryView> result = await service.RedeliverAsync(actor, id, deliveryId, ct);
        return result.Failure is { } f ? Refuse(f) : StatusCode(StatusCodes.Status202Accepted, result.Value);
    }

    private ActionResult? Gate(out WebhookActor actor)
    {
        actor = new WebhookActor(Guid.Empty, Guid.Empty, null, null);
        if (!options.Value.Enabled) return NotFound();
        if (currentUser.OrganizationId is not { } org || currentUser.OrgRole is null) return NotFound();
        if (!OrgAuthority.IsOrganizationAdmin(currentUser.OrgRole)) return Forbid();
        actor = new WebhookActor(org, currentUser.UserId, null, null);
        return null;
    }

    private ObjectResult Refuse(WebhookFailure failure) => StatusCode(failure.Status, new { code = failure.Code, message = failure.Message });
}
