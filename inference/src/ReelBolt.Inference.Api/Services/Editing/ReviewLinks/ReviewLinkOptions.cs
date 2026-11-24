namespace ReelBolt.Inference.Api.Services.Editing.ReviewLinks;

/// <summary>Guest review link limits (config section <c>ReviewLinks</c>).</summary>
public sealed class ReviewLinkOptions
{
    public const string SectionName = "ReviewLinks";

    /// <summary>The expiry applied when the owner does not choose one (default 14 days).</summary>
    public int DefaultExpiryDays { get; set; } = 14;

    /// <summary>The longest expiry an owner may choose (default 90 days).</summary>
    public int MaxExpiryDays { get; set; } = 90;

    /// <summary>The most live (not revoked, not expired) links one timeline may hold (default 20).</summary>
    public int MaxLinksPerTimeline { get; set; } = 20;

    /// <summary>The most guest comments one link may collect, replies included (default 200).</summary>
    public int MaxGuestCommentsPerLink { get; set; } = 200;

    /// <summary>The most guest writes (comments and decisions) per link per minute, per API replica (default 10).</summary>
    public int GuestCommentsPerMinute { get; set; } = 10;

    /// <summary>The most decisions one link records (append-only history).</summary>
    public const int MaxDecisionsPerLink = 20;

    /// <summary>A link counts at most one view per this long.</summary>
    public static readonly TimeSpan ViewCountInterval = TimeSpan.FromMinutes(1);

    /// <summary>At most one comment notification per link per this long.</summary>
    public static readonly TimeSpan NotifyCommentInterval = TimeSpan.FromMinutes(10);

    /// <summary>Decisions always notify, but never closer together than this.</summary>
    public static readonly TimeSpan NotifyDecisionFloor = TimeSpan.FromMinutes(1);

    public int EffectiveDefaultExpiryDays => Math.Clamp(DefaultExpiryDays, 1, EffectiveMaxExpiryDays);

    public int EffectiveMaxExpiryDays => Math.Clamp(MaxExpiryDays, 1, 365);
}
