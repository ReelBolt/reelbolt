namespace ReelBolt.Shared.Data.Models.Marketing;

/// <summary>Where a marketing campaign is in its life. Stored as the member name.</summary>
public enum MarketingCampaignStatus
{
    Draft,
    Queued,
    Syncing,
    Sending,
    Sent,
    Failed,

    /// <summary>The send call may or may not have reached Brevo. A human checks Brevo; it is never resent automatically.</summary>
    SendUnknown,
}

/// <summary>
/// One marketing campaign (table <c>marketing_campaigns</c>). Platform-wide, not tenant-owned. At most one
/// row may be Queued, Syncing or Sending at a time.
/// </summary>
public class MarketingCampaign
{
    public Guid Id { get; set; }

    /// <summary>≤200, no control characters.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The body without the footer (the footer is added at send time).</summary>
    public string Html { get; set; } = string.Empty;

    public MarketingCampaignStatus Status { get; set; } = MarketingCampaignStatus.Draft;

    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? TestSentAt { get; set; }

    /// <summary>Hash of the subject and html the last test send carried.</summary>
    public string? TestContentHash { get; set; }

    public int? ConfirmedRecipientCount { get; set; }
    public int? FinalRecipientCount { get; set; }
    public long? BrevoCampaignId { get; set; }

    /// <summary>Set BEFORE the send call; a row with this and no outcome is SendUnknown after a crash.</summary>
    public DateTime? DispatchStartedAt { get; set; }

    public string? FailureCode { get; set; }

    /// <summary>Brevo's global statistics, as JSON.</summary>
    public string? StatsJson { get; set; }

    public DateTime? StatsAt { get; set; }
}
