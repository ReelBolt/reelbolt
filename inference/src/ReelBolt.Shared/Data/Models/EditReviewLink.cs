namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// Guest review link (editor v2, Wave 4; <c>docs/design/guest-review-links.md</c>): a capability URL that
/// lets someone without an account watch ONE pinned render of a timeline, leave notes and record a
/// decision. Table <c>edit_review_links</c>, Inference-API-owned.
/// </summary>
/// <remarks>
/// <para>
/// The token itself is never stored: <see cref="TokenHash"/> is its SHA-256 and is the only lookup key.
/// <see cref="RenderExecutionId"/> names the pinned render (no foreign key: the workflow tables are mapped
/// <c>ExcludeFromMigrations</c>); the public media route can reach that one render's output file and nothing
/// else. Rows are removed with the timeline, the project, the organization and the creator's account.
/// </para>
/// </remarks>
public class EditReviewLink
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>Denormalised from the project so the named <c>Tenant</c> query filter needs no join.</summary>
    public Guid OrganizationId { get; set; }

    public Guid TimelineId { get; set; }

    public Guid CreatedByUserId { get; set; }

    /// <summary>SHA-256 of the 32-byte token (unique). The only way a link is looked up.</summary>
    public byte[] TokenHash { get; set; } = [];

    /// <summary>Owner-facing name, at most 80 characters.</summary>
    public string? Label { get; set; }

    /// <summary>The pinned render: a passed full-timeline <c>TimelineRender</c> execution of this timeline.</summary>
    public Guid RenderExecutionId { get; set; }

    /// <summary>The timeline revision the pinned render was made from.</summary>
    public int PinnedRevision { get; set; }

    public bool AllowComments { get; set; } = true;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? RevokedAt { get; set; }

    public DateTime? LastViewedAt { get; set; }

    public int ViewCount { get; set; }

    /// <summary>When the owner last opened this link's detail; guest activity after it is "unread".</summary>
    public DateTime? OwnerSeenAt { get; set; }

    /// <summary>When the last owner notification for this link was sent (coalescing).</summary>
    public DateTime? LastNotifiedAt { get; set; }
}

/// <summary>A guest's verdict on a review link. Stored by name.</summary>
public enum EditReviewDecisionKind
{
    Approved,
    ChangesRequested
}

/// <summary>
/// One recorded decision on a review link (table <c>edit_review_decisions</c>); append-only, the latest row
/// per link is its current state. Reached only through its link, so it carries no tenant filter of its own.
/// </summary>
public class EditReviewDecision
{
    public Guid Id { get; set; }

    public Guid LinkId { get; set; }

    public Guid ProjectId { get; set; }

    public Guid TimelineId { get; set; }

    public EditReviewDecisionKind Decision { get; set; }

    /// <summary>The self-asserted guest name (1..80), plain text.</summary>
    public string GuestName { get; set; } = string.Empty;

    /// <summary>Optional note, at most 1000 characters, plain text.</summary>
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
