namespace ReelBolt.Inference.Api.Data;

/// <summary>
/// One customer URL that receives signed run events (<c>webhook_endpoints</c>; public-api T-009, Standard Webhooks).
/// Organization-owned and addressed by id, so it carries the tenant filter. The secret is stored encrypted and shown
/// only when it is created or rotated.
/// </summary>
public class WebhookEndpoint
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public string Url { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The event types this endpoint subscribes to (see <c>WebhookEventTypes</c>).</summary>
    public string[] Events { get; set; } = [];

    /// <summary>The projects whose events are delivered; null = every project of the organization.</summary>
    public Guid[]? ProjectIds { get; set; }

    /// <summary>The signing secret (<c>whsec_...</c>), protected with its own data-protection purpose.</summary>
    public string SecretEncrypted { get; set; } = string.Empty;

    /// <summary>The secret before the last rotation; it keeps signing for 24 h after <see cref="SecretRotatedAt"/>.</summary>
    public string? SecretPreviousEncrypted { get; set; }

    public DateTime? SecretRotatedAt { get; set; }

    public bool Enabled { get; set; } = true;

    public string? DisabledReason { get; set; }

    public Guid CreatedByUserId { get; set; }

    public Guid? CreatedByApiKeyId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastSuccessAt { get; set; }

    /// <summary>When the endpoint's current run of failures began; cleared by a success.</summary>
    public DateTime? FailingSince { get; set; }
}

/// <summary>
/// One message to one endpoint (<c>webhook_deliveries</c>). The id is the <c>webhook-id</c> header (<c>msg_...</c>);
/// (<see cref="EndpointId"/>, <see cref="EventKey"/>) is unique so a redelivered bus message never duplicates.
/// A child of its endpoint: no tenant filter of its own, every read goes through the endpoint.
/// </summary>
public class WebhookDelivery
{
    public const string Pending = "pending";
    public const string Delivered = "delivered";
    public const string Failed = "failed";
    public const string Abandoned = "abandoned";

    public string Id { get; set; } = string.Empty;

    public Guid EndpointId { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The project whose event this is (S-L7), or null for an event with no project (a test ping). A
    /// project-restricted key sees only the deliveries of projects inside its own list.
    /// </summary>
    public Guid? ProjectId { get; set; }

    public string EventType { get; set; } = string.Empty;

    public string EventKey { get; set; } = string.Empty;

    /// <summary>The JSON body that is POSTed and signed: ids and statuses only.</summary>
    public string PayloadJson { get; set; } = "{}";

    public string Status { get; set; } = Pending;

    public int Attempts { get; set; }

    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;

    public int? LastStatusCode { get; set; }

    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DeliveredAt { get; set; }

    /// <summary>A new <c>msg_</c> id: 24 random bytes, base64url (no padding).</summary>
    public static string NewId() => "msg_" + Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
