namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One request that authenticated with an API key (<c>api_key_audit_events</c>). Written after the response
/// through a bounded channel and a batch writer, never on the request path. It carries the endpoint's route
/// TEMPLATE (never the raw path or query), the status, the duration and the request id - never a body or a
/// header. Kept for <c>PublicApi:AuditRetentionDays</c>.
/// </summary>
/// <remarks>
/// Reached only through the key that wrote it, so it carries no tenant query filter: <c>organization_id</c>
/// is a plain indexed column like <see cref="BillingAdminAction"/>, and the dashboard route filters by the
/// key's organization explicitly.
/// </remarks>
public class ApiKeyAuditEvent
{
    public long Id { get; set; }

    public Guid ApiKeyId { get; set; }

    public Guid OrganizationId { get; set; }

    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>The HTTP method.</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>The endpoint's route template, e.g. <c>api/public/v1/projects/{projectId}/runs</c>.</summary>
    public string Route { get; set; } = string.Empty;

    public int StatusCode { get; set; }

    public long DurationMs { get; set; }

    public string RequestId { get; set; } = string.Empty;
}
