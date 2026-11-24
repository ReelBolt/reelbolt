namespace ReelBolt.Shared.IntegrationEvents;

/// <summary>
/// Published by the Inference API when a user adds a project file or edits a thumbnail graphic
/// in the image editor. The Go API consumes it and mails the workspace members who have the
/// matching switch on (docs/notifications.md). Publishing is best-effort: a bus failure never
/// fails the user's original request.
/// </summary>
public record UserActivityNotificationRequested
{
    /// <summary>Fixed category names (docs/notifications.md); the Go API switches on them.</summary>
    public const string FileCreated = "file_created";
    public const string GraphicsEdited = "graphics_edited";

    /// <summary>Guest review link activity (a guest note or decision): mailed to <see cref="RecipientUserId"/> only.</summary>
    public const string ReviewActivity = "review_activity";

    /// <summary>An API key nears expiry; one event per recipient. Subject = key name, Details = expiry (ISO-8601 UTC).</summary>
    public const string ApiKeyExpiring = "api_key_expiring";

    /// <summary>A webhook endpoint was auto-disabled. Subject = endpoint URL host, Details = short reason.</summary>
    public const string WebhookDisabled = "webhook_disabled";
    /// <summary>
    /// An avatar reaches its retention date in 7 days (docs/design/avatars.md "Retention sweep"): mailed to
    /// <see cref="RecipientUserId"/> only (the member who attested its consent). <see cref="Subject"/> is the avatar's
    /// display name, <see cref="Details"/> the plain "Deleted on yyyy-MM-dd" line; never the subject's own name.
    /// </summary>
    public const string AvatarRetention = "avatar_retention";

    /// <summary>
    /// A social post was published or failed, or a connected account needs reconnecting (social-publishing):
    /// mailed to <see cref="RecipientUserId"/> only. <see cref="Subject"/> is the one-line headline, <see cref="Details"/> a
    /// neutral sentence (never a token or a platform body). <see cref="ProjectId"/> is <see cref="Guid.Empty"/> for a reconnect.
    /// </summary>
    public const string SocialPublishing = "social_publishing";

    public Guid OrganizationId { get; init; }

    /// <summary>The member whose action this is; null when the action was system-triggered.</summary>
    public Guid? ActorUserId { get; init; }

    public Guid ProjectId { get; init; }

    /// <summary>One of <see cref="FileCreated"/> / <see cref="GraphicsEdited"/> / <see cref="ReviewActivity"/> / <see cref="ApiKeyExpiring"/> / <see cref="WebhookDisabled"/>.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>The added file's name, or the edited graphic's subject line.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Optional detail line (a short description of the edit).</summary>
    public string? Details { get; init; }

    /// <summary>
    /// Optional single recipient (additive, Wave 4). Set only for <see cref="ReviewActivity"/>, <see cref="ApiKeyExpiring"/> and <see cref="WebhookDisabled"/>: the member who
    /// created the review link. A consumer routes such an event to that one member (skipping them when they are
    /// not a member of <see cref="OrganizationId"/>) and never to the actor. Null for every other type, which
    /// fan out to the opted-in workspace members as before.
    /// </summary>
    public Guid? RecipientUserId { get; init; }

    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
