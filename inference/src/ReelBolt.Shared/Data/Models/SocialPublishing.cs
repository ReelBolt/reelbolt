namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// A social platform a finished video can be published to (docs/social-publishing.md). Persisted as an
/// int: append-only, never renumber.
/// </summary>
public enum SocialPlatformKind
{
    /// <summary>YouTube Shorts, through the Data API v3.</summary>
    YouTube = 0,

    /// <summary>LinkedIn member posting.</summary>
    LinkedIn = 1
}

/// <summary>
/// The fixed strings a platform is known by. The slug appears in URLs
/// (<c>/api/v1/social/{slug}/…</c>, the OAuth redirect URI registered with the vendor) and must never
/// change once shipped.
/// </summary>
public static class SocialPlatforms
{
    public static readonly IReadOnlyList<SocialPlatformKind> All = [SocialPlatformKind.YouTube, SocialPlatformKind.LinkedIn];

    public static string Slug(SocialPlatformKind kind) => kind switch
    {
        SocialPlatformKind.YouTube => "youtube",
        SocialPlatformKind.LinkedIn => "linkedin",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static string DisplayName(SocialPlatformKind kind) => kind switch
    {
        SocialPlatformKind.YouTube => "YouTube Shorts",
        SocialPlatformKind.LinkedIn => "LinkedIn",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static bool TryParseSlug(string? slug, out SocialPlatformKind kind)
    {
        foreach (SocialPlatformKind candidate in All)
        {
            if (string.Equals(Slug(candidate), slug, StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }
}

/// <summary>Values of <see cref="SocialAccount.Status"/>.</summary>
public static class SocialAccountStatus
{
    public const string Active = "active";
    public const string ReconnectNeeded = "reconnect_needed";
    public const string Revoked = "revoked";
}

/// <summary>Values of <see cref="SocialPost.Status"/>: <c>draft -> scheduled -> publishing -> published | failed | cancelled | unknown</c>.</summary>
public static class SocialPostStatus
{
    public const string Draft = "draft";
    public const string Scheduled = "scheduled";
    public const string Publishing = "publishing";
    public const string Published = "published";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Unknown = "unknown";
}

/// <summary>
/// An organization's connected account on one social platform (<c>social_accounts</c>). Org-owned: an
/// Owner or Admin connects and disconnects, any member with edit access to a project publishes to it.
/// Carries the named <c>Tenant</c> query filter. Tokens are encrypted at rest with <c>ISecretProtector</c>,
/// never logged, never returned by the API. Unique on (organization, platform, remote account id).
/// </summary>
public class SocialAccount
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public SocialPlatformKind Platform { get; set; }

    /// <summary>The platform's stable account id (YouTube channel id, LinkedIn member id). Never the email.</summary>
    public string RemoteAccountId { get; set; } = string.Empty;

    public string? DisplayName { get; set; }

    /// <summary>Avatar URL as the platform gave it (the image itself is not stored).</summary>
    public string? AvatarUrl { get; set; }

    /// <summary>Data Protection ciphertext. Empty once the account is disconnected.</summary>
    public string ProtectedAccessToken { get; set; } = string.Empty;

    public string? ProtectedRefreshToken { get; set; }

    public DateTime? AccessTokenExpiresAt { get; set; }

    /// <summary>Scopes the platform actually granted, space separated.</summary>
    public string? GrantedScopes { get; set; }

    /// <summary>One of <see cref="SocialAccountStatus"/>.</summary>
    public string Status { get; set; } = SocialAccountStatus.Active;

    /// <summary>Plain-language reason the account stopped working. Null while it works.</summary>
    public string? LastError { get; set; }

    public Guid ConnectedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One in-flight OAuth authorization (<c>social_oauth_states</c>): the state is stored as a SHA-256
/// hash, the PKCE verifier encrypted, the row lives 10 minutes, is consumed exactly once and is bound
/// to the user and organization that started it. Reached through its parent flow, no Tenant filter.
/// </summary>
public class SocialOAuthState
{
    public Guid Id { get; set; }

    /// <summary>Hex SHA-256 of the state value sent to the platform. Unique.</summary>
    public string StateHash { get; set; } = string.Empty;

    public Guid UserId { get; set; }

    public Guid OrganizationId { get; set; }

    public SocialPlatformKind Platform { get; set; }

    public string ProtectedCodeVerifier { get; set; } = string.Empty;

    /// <summary>A same-origin <c>/app/…</c> path to send the browser back to. Validated before it is stored.</summary>
    public string? ReturnPath { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    public DateTime? ConsumedAt { get; set; }
}

/// <summary>
/// One post of one finished video to one account (<c>social_posts</c>). Carries the named
/// <c>Tenant</c> query filter. The text is a snapshot copied at schedule time, so later edits of the
/// saved post text never change a queued post. The schema is complete from the first layer; the
/// publish engine that drives the status machine arrives with later layers.
/// </summary>
public class SocialPost
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary><c>step_result</c> or <c>project_file</c>.</summary>
    public string OutputKind { get; set; } = string.Empty;

    public Guid OutputId { get; set; }

    public Guid SocialAccountId { get; set; }

    public SocialPlatformKind Platform { get; set; }

    /// <summary>Platform format, e.g. <c>short</c>, <c>post</c>.</summary>
    public string Format { get; set; } = string.Empty;

    public string? Title { get; set; }

    public string? Body { get; set; }

    /// <summary>JSON array of hashtag strings.</summary>
    public string? HashtagsJson { get; set; }

    /// <summary>Optional link to the saved post text this snapshot was copied from.</summary>
    public Guid? PostTextId { get; set; }

    /// <summary>JSON object of platform options (privacy, AI-generated label, comments on/off).</summary>
    public string? OptionsJson { get; set; }

    public DateTime? ScheduledAt { get; set; }

    /// <summary>The user's IANA time zone, for display.</summary>
    public string? TimeZone { get; set; }

    /// <summary>One of <see cref="SocialPostStatus"/>.</summary>
    public string Status { get; set; } = SocialPostStatus.Draft;

    public string? LeaseOwner { get; set; }

    public DateTime? LeaseExpiresAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>Earliest time the next automatic attempt may start after a retryable failure (backoff). Null = none pending.</summary>
    public DateTime? NextAttemptAt { get; set; }

    public string? RemotePostId { get; set; }

    public string? RemotePostUrl { get; set; }

    /// <summary>Short machine code of a failure (never a token or a raw platform body).</summary>
    public string? FailureCode { get; set; }

    public string? FailureMessage { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PublishedAt { get; set; }
}

/// <summary>
/// One reading of a published post's numbers (<c>social_post_metrics</c>). Reached through its post.
/// A null count means the platform does not provide it (shown as "not available", never 0).
/// </summary>
public class SocialPostMetric
{
    public Guid Id { get; set; }

    public Guid PostId { get; set; }

    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    public long? Views { get; set; }

    public long? Likes { get; set; }

    public long? Comments { get; set; }

    public long? Shares { get; set; }

    public long? Saves { get; set; }

    /// <summary>Where the numbers came from, e.g. <c>youtube.videos.list</c>.</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// One publish slot taken at claim time (<c>social_publish_ledger</c>): the monthly cap and the platform's daily budget
/// are counted from these rows, not from <c>social_posts</c>, so deleting a project (which cascades its posts away) or a
/// post can never hand the quota back. No foreign key to posts or projects; <see cref="PostId"/> is only the idempotency
/// key (one slot per post, however many attempts). Reached through the organization id; purged only with the organization.
/// </summary>
public class SocialPublishLedgerEntry
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public SocialPlatformKind Platform { get; set; }

    /// <summary>The post this slot was taken for. Not a foreign key: the slot outlives the post.</summary>
    public Guid PostId { get; set; }

    /// <summary>The UTC day the slot was taken (midnight).</summary>
    public DateTime Day { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
