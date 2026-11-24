namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One run (or revision) a user started in a project, recorded by the Inference API's run guard so the
/// hourly run limit can be counted per bucket. API-owned table <c>run_rate_events</c> (the engine never
/// reads or writes it).
/// </summary>
/// <remarks>
/// Not tenant-filtered, like <see cref="McpToolCall"/>: it is only ever read by the guard through the
/// caller's own (user, project) pair and is never addressed by an id a caller supplies. Rows older than a
/// day are pruned by the guard; the account purge deletes a user's rows.
/// </remarks>
public class RunRateEvent
{
    public const string PaidBucket = "paid";
    public const string FreeBucket = "free";
    public const string ExecuteSource = "execute";
    public const string RevisionSource = "revision";

    /// <summary>A run the public v1 API started (or re-queued) through an API key (public-api T-007).</summary>
    public const string ApiSource = "api";

    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary><c>paid</c> or <c>free</c> (see <c>RunRateBucket</c>).</summary>
    public string Bucket { get; set; } = PaidBucket;

    /// <summary><c>execute</c>, <c>revision</c> or <c>api</c>.</summary>
    public string Source { get; set; } = ExecuteSource;

    public Guid? ExecutionId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
