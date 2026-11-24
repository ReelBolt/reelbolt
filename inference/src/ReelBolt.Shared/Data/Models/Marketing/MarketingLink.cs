namespace ReelBolt.Shared.Data.Models.Marketing;

/// <summary>
/// One tracked link (table <c>marketing_links</c>, docs/design/new-user-funnel.md section 5): a short
/// <c>/l/{code}</c> address an operator posts in one place, which counts the click and redirects to a
/// landing page with the link's UTM parameters. Platform-wide, not tenant-owned.
/// <para>
/// <see cref="Code"/>, <see cref="Channel"/>, <see cref="Medium"/> and <see cref="Campaign"/> are immutable
/// once the link exists: a posted link's address and its meaning in the funnel never change. The link's
/// code is its <c>utm_content</c>, so a hand-written URL with the same <c>utm_content</c> joins the same link.
/// </para>
/// </summary>
public class MarketingLink
{
    public Guid Id { get; set; }

    /// <summary>The link code (<c>AcquisitionVocabulary.LinkCodePattern</c>), unique. Immutable.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Where the link is posted, one of <c>AcquisitionVocabulary.LinkChannels</c>. Immutable.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>The kind of placement, one of <c>AcquisitionVocabulary.LinkMedia</c>. Immutable.</summary>
    public string Medium { get; set; } = string.Empty;

    /// <summary>The campaign token (a UTM value). Immutable.</summary>
    public string Campaign { get; set; } = string.Empty;

    /// <summary>The operator's note: where exactly the link is posted. Never sent to the visitor.</summary>
    public string Placement { get; set; } = string.Empty;

    /// <summary>The own-origin path the link redirects to: starts with <c>/</c>, never <c>//</c>, no scheme, no query.</summary>
    public string DestinationPath { get; set; } = "/";

    /// <summary>The persona the landing page and signup should suggest (<c>for=</c>), or null.</summary>
    public string? Persona { get; set; }

    /// <summary>What the placement cost, in minor units of <see cref="CostCurrency"/>; null when unknown or free.</summary>
    public long? CostMinor { get; set; }

    /// <summary>ISO 4217 code of <see cref="CostMinor"/>; set exactly when the cost is.</summary>
    public string? CostCurrency { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>The platform admin who created the link; null once that account is gone.</summary>
    public Guid? CreatedByUserId { get; set; }

    /// <summary>When the link was archived. An archived link redirects to <c>/</c> and counts nothing.</summary>
    public DateTime? ArchivedAt { get; set; }
}

/// <summary>
/// Clicks of one tracked link on one UTC day (table <c>marketing_link_daily_stats</c>). A counter and
/// nothing else: no visitor, no IP address, no user agent. The redirect adds one with an atomic upsert.
/// </summary>
public class MarketingLinkDailyStat
{
    public Guid LinkId { get; set; }

    /// <summary>The UTC day.</summary>
    public DateOnly Day { get; set; }

    /// <summary>Redirects served to something that did not look like a link preview or a crawler.</summary>
    public int Clicks { get; set; }

    /// <summary>Redirects served to a known link-preview fetcher or crawler (or to no user agent at all).</summary>
    public int BotHits { get; set; }
}
