namespace ReelBolt.Shared.Data.Models;

/// <summary>
/// One user's notification switches (docs/notifications.md). The Go API reads and writes this
/// table by raw SQL and never migrates it; the Inference API owns the schema. An absent row
/// means "everything on" — that default lives in the Go API, not here.
/// </summary>
public class UserNotificationPreferences
{
    public Guid UserId { get; set; }

    /// <summary>A workflow run of theirs finished successfully.</summary>
    public bool JobSucceeded { get; set; } = true;

    /// <summary>A workflow run of theirs failed.</summary>
    public bool JobFailed { get; set; } = true;

    /// <summary>A project file was added (by a team member, or by the system).</summary>
    public bool FileCreated { get; set; } = true;

    /// <summary>A thumbnail graphic in the image editor was edited.</summary>
    public bool GraphicsEdited { get; set; } = true;

    /// <summary>A guest left a note or decision on a review link of theirs.</summary>
    public bool ReviewActivity { get; set; } = true;

    /// <summary>An avatar they attested consent for is deleted by its retention date in 7 days.</summary>
    public bool AvatarRetention { get; set; } = true;

    /// <summary>A social post was published or failed, or an account needs reconnecting (social-publishing). Default on.</summary>
    public bool SocialPublishing { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One attempted notification email, the per-user delivery log the Go API writes and reads
/// (docs/notifications.md). Reached only through an authenticated user id, so it carries no
/// tenant query filter (docs/multi-tenancy.md).
/// </summary>
public class UserNotification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>One of the Go API's notification category names.</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>The mail's one-line summary, as shown in the dashboard's recent list.</summary>
    public string Title { get; set; } = string.Empty;

    public bool Delivered { get; set; }
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
