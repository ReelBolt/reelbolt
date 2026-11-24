namespace ReelBolt.Shared.Data.Models.Marketing;

/// <summary>
/// One fixed-window mail rate counter (table <c>mail_rate_counters</c>, shared contract C2). The
/// Inference API owns the schema and writes the <c>confirm:*</c> buckets; the Go API writes the
/// <c>relay:*</c> buckets with raw SQL and never migrates. A bucket is never a raw address or IP:
/// mailboxes and IPs appear only as SHA-256 hex, domains in clear. Rows older than two days are
/// deleted by the marketing sweeper.
/// </summary>
public class MailRateCounter
{
    /// <summary>e.g. <c>confirm:mbox:&lt;sha256hex(canonical)&gt;</c>, <c>confirm:domain:example.com</c>, <c>confirm:global</c>.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>UTC start of the window: now floored to the window length (epoch seconds).</summary>
    public DateTime WindowStart { get; set; }

    public int Count { get; set; }

    public DateTime UpdatedAt { get; set; }
}
