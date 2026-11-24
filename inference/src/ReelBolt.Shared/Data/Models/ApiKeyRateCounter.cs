namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One fixed one-minute window of a key's request counter (<c>api_key_rate_counters</c>). One
/// <c>INSERT .. ON CONFLICT DO UPDATE .. RETURNING</c> per request increments it and reads the new totals,
/// so every replica shares the count. Rows older than an hour are swept hourly.
/// </summary>
/// <remarks>
/// No tenant query filter: the row is addressed by the key id the request authenticated with, never by an id
/// a caller supplies.
/// </remarks>
public class ApiKeyRateCounter
{
    public Guid ApiKeyId { get; set; }

    /// <summary>The UTC minute this row counts (aligned to :00 seconds).</summary>
    public DateTime WindowStart { get; set; }

    /// <summary>Every request in the window, reads and writes.</summary>
    public int Total { get; set; }

    /// <summary>POST/PUT/PATCH/DELETE requests in the window.</summary>
    public int Writes { get; set; }
}
