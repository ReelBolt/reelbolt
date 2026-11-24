using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.PublicApi.Runs;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Webhooks;

/// <summary>
/// The webhook side of a queued run request's end (<c>run_request.started</c> / <c>run_request.refused</c>, plan
/// section 8). Writes deliveries in a scope of its own; the dispatcher already survives (and logs) a throw.
/// </summary>
public sealed class ApiRunRequestWebhookEvents(
    IServiceScopeFactory scopes, IOptions<PublicApiOptions> options, TimeProvider? time = null) : IApiRunRequestEvents
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Task StartedAsync(Guid requestId, Guid apiKeyId, Guid organizationId, Guid projectId, Guid executionId, CancellationToken ct) =>
        WriteAsync(organizationId, projectId, WebhookEventTypes.RunRequestStarted, requestId,
            new WebhookEventData(RunId: executionId, ProjectId: projectId, Status: "started", RunRequestId: requestId), ct);

    public Task RefusedAsync(Guid requestId, Guid apiKeyId, Guid organizationId, Guid projectId, string code, string message, CancellationToken ct) =>
        WriteAsync(organizationId, projectId, WebhookEventTypes.RunRequestRefused, requestId,
            new WebhookEventData(ProjectId: projectId, Status: "refused", FailureCode: code, RunRequestId: requestId), ct);

    private async Task WriteAsync(Guid organizationId, Guid projectId, string type, Guid requestId, WebhookEventData data, CancellationToken ct)
    {
        if (!options.Value.Enabled) return;

        using IServiceScope scope = scopes.CreateScope();
        using IDisposable system = scope.ServiceProvider.GetService<ITenantContext>().System();
        WebhookEventWriter writer = scope.ServiceProvider.GetRequiredService<WebhookEventWriter>();
        await writer.EnqueueAsync(organizationId, projectId, type, $"{type}:{requestId}", _time.GetUtcNow().UtcDateTime, data, ct);
    }
}
