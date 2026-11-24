namespace ReelBolt.Inference.Api.Data;

/// <summary>
/// One start the public API accepted but could not run yet because the hourly run limit was full
/// (<c>api_run_requests</c>). Every other gate had already passed when it
/// was written; the dispatcher re-runs the whole start sequence when the window frees. Reached only through the
/// key that wrote it, so it carries no tenant filter.
/// </summary>
public class ApiRunRequest
{
    public const string Queued = "queued";

    /// <summary>Claimed by a dispatcher; the start is in flight. Reconciled by correlation id when it goes stale.</summary>
    public const string Starting = "starting";

    public const string Started = "started";
    public const string Refused = "refused";
    public const string Cancelled = "cancelled";

    /// <summary>The prefix of the execution's correlation id, followed by the request id (N format).</summary>
    public const string CorrelationPrefix = "apirun:";

    public Guid Id { get; set; }

    public Guid ApiKeyId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid WorkflowId { get; set; }

    /// <summary>The start body as the client sent it (public DTO JSON).</summary>
    public string RequestJson { get; set; } = "{}";

    public string Status { get; set; } = Queued;

    /// <summary>The dispatcher does not look at the row before this time.</summary>
    public DateTime NotBefore { get; set; } = DateTime.UtcNow;

    /// <summary>When the oldest counted run leaves the rolling hour, as last computed.</summary>
    public DateTime? EstimatedStartAt { get; set; }

    public Guid? ExecutionId { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>When the row was claimed (<see cref="Starting"/>).</summary>
    public DateTime? StartingAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public static string CorrelationFor(Guid requestId) => CorrelationPrefix + requestId.ToString("N");
}
