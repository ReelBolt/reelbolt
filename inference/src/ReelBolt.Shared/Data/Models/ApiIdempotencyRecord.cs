namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One <c>Idempotency-Key</c> record of an API key (<c>api_idempotency_records</c>). The primary key is
/// (key id, key), so one key's record can never be read by another key. InProgress means the first request
/// is still running; Done carries the stored status and body (or no body when the response was over 64 KB).
/// Rows older than <c>PublicApi:IdempotencyHours</c> are swept hourly.
/// </summary>
/// <remarks>No tenant query filter: the composite primary key includes the authenticating key's id.</remarks>
public class ApiIdempotencyRecord
{
    public const string InProgress = "InProgress";
    public const string Done = "Done";

    public Guid ApiKeyId { get; set; }

    /// <summary>The client's <c>Idempotency-Key</c> header (1..200 printable ASCII).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>SHA-256 hex of method + route template + route values + canonical body bytes.</summary>
    public string RequestHash { get; set; } = string.Empty;

    /// <summary><see cref="InProgress"/> or <see cref="Done"/>.</summary>
    public string Status { get; set; } = InProgress;

    public int? ResponseStatus { get; set; }

    /// <summary>The stored JSON body, or null when it was too large or the in-flight request failed.</summary>
    public string? ResponseBody { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
