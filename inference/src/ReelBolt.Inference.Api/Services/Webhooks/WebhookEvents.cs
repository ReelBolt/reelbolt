using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;

namespace ReelBolt.Inference.Api.Services.Webhooks;

/// <summary>The event types an endpoint can subscribe to.</summary>
public static class WebhookEventTypes
{
    public const string RunSucceeded = "run.succeeded";
    public const string RunFailed = "run.failed";
    public const string RunCancelled = "run.cancelled";
    public const string RunStepCompleted = "run.step_completed";
    public const string RunRequestStarted = "run_request.started";
    public const string RunRequestRefused = "run_request.refused";

    /// <summary>Sent by <c>POST /webhooks/{id}/test</c> to the one endpoint, whatever it subscribes to.</summary>
    public const string Test = "webhook.test";

    public static readonly IReadOnlyList<string> Subscribable =
        [RunSucceeded, RunFailed, RunCancelled, RunStepCompleted, RunRequestStarted, RunRequestRefused];

    /// <summary>What an endpoint created without <c>events</c> receives (the step events are opt-in).</summary>
    public static readonly IReadOnlyList<string> Default = [RunSucceeded, RunFailed, RunCancelled];

    public static bool IsSubscribable(string? type) => type is not null && Subscribable.Contains(type, StringComparer.Ordinal);
}

/// <summary>
/// The body every delivery carries: <c>{ "type", "timestamp", "data": { runId, projectId, workflowId, status,
/// failureCode?, creditsCharged?, runRequestId? } }</c>. Ids and statuses only. This record is the schema the
/// OpenAPI document publishes for the webhook payloads.
/// </summary>
public sealed record WebhookEventPayload(string Type, DateTime Timestamp, WebhookEventData Data);

/// <summary>The <c>data</c> member of <see cref="WebhookEventPayload"/>; absent members are omitted from the JSON.</summary>
public sealed record WebhookEventData(
    Guid? RunId = null,
    Guid? ProjectId = null,
    Guid? WorkflowId = null,
    string? Status = null,
    string? FailureCode = null,
    decimal? CreditsCharged = null,
    Guid? RunRequestId = null,
    Guid? StepId = null,
    int? StepOrder = null,
    string? StepType = null);

/// <summary>
/// Writes <c>webhook_deliveries</c> rows: one per enabled endpoint of the organization that subscribes to the
/// event and covers the project. Idempotent on (endpoint, event key), so a redelivered bus message adds nothing.
/// Runs under a system tenant scope (a bus consumer has no request); every query names the organization itself.
/// </summary>
public sealed class WebhookEventWriter(InferenceApiDbContext db, ILogger<WebhookEventWriter>? logger = null, TimeProvider? time = null)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The body for <paramref name="type"/>; <paramref name="occurredAt"/> is the event's own time (UTC).</summary>
    public static string BuildBody(string type, DateTime occurredAt, WebhookEventData data) =>
        JsonSerializer.Serialize(new WebhookEventPayload(type, DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc), data), Json);

    /// <summary>Queues the event for every subscribed endpoint; returns how many deliveries were created.</summary>
    public async Task<int> EnqueueAsync(
        Guid organizationId, Guid? projectId, string type, string eventKey, DateTime occurredAt, WebhookEventData data, CancellationToken ct)
    {
        List<WebhookEndpoint> endpoints = await db.WebhookEndpoints.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.Enabled)
            .ToListAsync(ct);
        endpoints = endpoints
            .Where(e => e.Events.Contains(type, StringComparer.Ordinal)
                && (e.ProjectIds is null || (projectId is Guid p && e.ProjectIds.Contains(p))))
            .ToList();
        if (endpoints.Count == 0) return 0;

        Guid[] ids = endpoints.Select(e => e.Id).ToArray();
        HashSet<Guid> already = (await db.WebhookDeliveries.AsNoTracking()
                .Where(d => ids.Contains(d.EndpointId) && d.EventKey == eventKey)
                .Select(d => d.EndpointId)
                .ToListAsync(ct))
            .ToHashSet();

        string body = BuildBody(type, occurredAt, data);
        DateTime now = _time.GetUtcNow().UtcDateTime;
        int created = 0;
        foreach (WebhookEndpoint endpoint in endpoints.Where(e => !already.Contains(e.Id)))
        {
            db.WebhookDeliveries.Add(NewDelivery(endpoint, type, eventKey, body, now, projectId));
            created++;
        }

        if (created == 0) return 0;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // A concurrent consumer of the same message won the unique (endpoint, event key) index.
            logger?.LogInformation("Webhook deliveries for {EventKey} already queued: {Error}", eventKey, ex.GetType().Name);
            return 0;
        }

        return created;
    }

    public static WebhookDelivery NewDelivery(
        WebhookEndpoint endpoint, string type, string eventKey, string body, DateTime now, Guid? projectId = null) => new()
        {
            Id = WebhookDelivery.NewId(),
            EndpointId = endpoint.Id,
            OrganizationId = endpoint.OrganizationId,
            ProjectId = projectId,
            EventType = type,
            EventKey = eventKey,
            PayloadJson = body,
            Status = WebhookDelivery.Pending,
            NextAttemptAt = now,
            CreatedAt = now
        };

    /// <summary>The ISO-8601 UTC form used in payloads.</summary>
    public static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
}
