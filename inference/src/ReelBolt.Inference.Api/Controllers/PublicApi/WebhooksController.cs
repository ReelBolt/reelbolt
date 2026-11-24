using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.Webhooks;
using ReelBolt.Shared.Auth;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

/// <summary>
/// Outbound webhooks over the public API (<c>webhooks.manage</c>): endpoint CRUD, secret rotation, a test ping and the
/// delivery log with redelivery. The rules live in <see cref="WebhookEndpointService"/>, shared with the dashboard
/// twin. A project-restricted key manages only endpoints scoped to projects inside its own list. The signing secret
/// appears only in the create and rotate responses.
/// </summary>
public sealed class WebhooksController(
    WebhookEndpointService service,
    ICurrentUser currentUser,
    IApiKeyContext keyContext) : PublicApiControllerBase
{
    [HttpGet("webhooks")]
    [RequireScope(ApiScopes.WebhooksManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> List(CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthenticated();
        return Ok(new PublicApiPage<WebhookEndpointView>(await service.ListAsync(actor, ct), null));
    }

    [HttpPost("webhooks")]
    [Idempotent(RedactFields = ["secret"])]
    [RequireScope(ApiScopes.WebhooksManage)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Create([FromBody] WebhookEndpointCreate? body, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthenticated();
        WebhookResult<WebhookEndpointView> result = await service.CreateAsync(actor, body ?? new WebhookEndpointCreate(null, null, null, null), ct);
        return result.Failure is { } f ? Refuse(f) : StatusCode(StatusCodes.Status201Created, result.Value);
    }

    [HttpGet("webhooks/{id:guid}")]
    [RequireScope(ApiScopes.WebhooksManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(Guid id, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthenticated();
        WebhookResult<WebhookEndpointView> result = await service.GetAsync(actor, id, ct);
        return result.Failure is { } f ? Refuse(f) : Ok(result.Value);
    }

    [HttpPatch("webhooks/{id:guid}")]
    [RequireScope(ApiScopes.WebhooksManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult> Update(Guid id, [FromBody] WebhookEndpointPatch? body, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthenticated();
        WebhookResult<WebhookEndpointView> result = await service.UpdateAsync(actor, id, body ?? new WebhookEndpointPatch(null, null, null, null, null), ct);
        return result.Failure is { } f ? Refuse(f) : Ok(result.Value);
    }

    [HttpDelete("webhooks/{id:guid}")]
    [RequireScope(ApiScopes.WebhooksManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthenticated();
        WebhookResult<WebhookEndpointView> result = await service.DeleteAsync(actor, id, ct);
        return result.Failure is { } f ? Refuse(f) : NoContent();
    }

    /// <summary>
    /// A new secret in the response; the old one keeps signing for 24 hours, unless <c>revokePrevious=true</c>
    /// drops it at once. Not idempotent: the secret must never be stored for replay.
    /// </summary>
    [HttpPost("webhooks/{id:guid}/rotate-secret")]
    [RequireScope(ApiScopes.WebhooksManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RotateSecret(Guid id, [FromQuery] bool revokePrevious, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthenticated();
        WebhookResult<WebhookEndpointView> result = await service.RotateSecretAsync(actor, id, revokePrevious, ct);
        return result.Failure is { } f ? Refuse(f) : Ok(new WebhookSecretView(result.Value!.Secret!));
    }

    [HttpPost("webhooks/{id:guid}/test")]
    [Idempotent]
    [RequireScope(ApiScopes.WebhooksManage)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult> Test(Guid id, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthenticated();
        WebhookResult<WebhookDeliveryView> result = await service.TestAsync(actor, id, ct);
        return result.Failure is { } f ? Refuse(f) : StatusCode(StatusCodes.Status202Accepted, result.Value);
    }

    [HttpGet("webhooks/{id:guid}/deliveries")]
    [RequireScope(ApiScopes.WebhooksManage)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Deliveries(Guid id, [FromQuery] int? limit, [FromQuery] string? cursor, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthenticated();
        WebhookResult<WebhookDeliveryPage> result = await service.ListDeliveriesAsync(actor, id, limit, cursor, ct);
        return result.Failure is { } f ? Refuse(f) : Ok(new PublicApiPage<WebhookDeliveryView>(result.Value!.Data, result.Value.NextCursor));
    }

    [HttpPost("webhooks/{id:guid}/deliveries/{deliveryId}/redeliver")]
    [Idempotent]
    [RequireScope(ApiScopes.WebhooksManage)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Redeliver(Guid id, string deliveryId, CancellationToken ct)
    {
        if (Actor() is not { } actor) return Unauthenticated();
        WebhookResult<WebhookDeliveryView> result = await service.RedeliverAsync(actor, id, deliveryId, ct);
        return result.Failure is { } f ? Refuse(f) : StatusCode(StatusCodes.Status202Accepted, result.Value);
    }

    private WebhookActor? Actor() =>
        keyContext.KeyId is Guid keyId && currentUser.OrganizationId is Guid org
            ? new WebhookActor(org, currentUser.UserId, keyId, keyContext.AllowedProjectIds)
            : null;

    private ObjectResult Unauthenticated() =>
        PublicError(StatusCodes.Status401Unauthorized, PublicApiErrorCodes.ApiKeyRequired, PublicApiErrorCodes.ApiKeyRequiredMessage);

    private ObjectResult Refuse(WebhookFailure failure)
    {
        // A 429 is normalized by PublicError (Retry-After + RateLimit-Reset + error.resetsAt); the surface's
        // retryAfterSeconds/resetsAt are carried in the body so the shared helper keeps the service's horizon.
        Dictionary<string, object?>? extras = failure.RetryAfterSeconds is { } seconds
            ? new()
            {
                ["retryAfterSeconds"] = seconds,
                ["resetsAt"] = (failure.ResetsAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : DateTime.UtcNow.AddSeconds(seconds))
                    .ToString("O", CultureInfo.InvariantCulture)
            }
            : failure.ResetsAt is { } resetsAt
                ? new() { ["resetsAt"] = DateTime.SpecifyKind(resetsAt, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture) }
                : null;
        return PublicError(failure.Status, failure.Code, failure.Message, extras);
    }
}

/// <summary>The body of a secret rotation: the new signing secret, shown once.</summary>
public sealed record WebhookSecretView(string Secret);
