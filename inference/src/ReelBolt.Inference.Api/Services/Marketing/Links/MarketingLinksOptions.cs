namespace ReelBolt.Inference.Api.Services.Marketing.Links;

/// <summary>
/// <c>Marketing:Links:*</c>: the tracked links of the new-user funnel (docs/design/new-user-funnel.md).
/// </summary>
public sealed class MarketingLinksOptions
{
    public const string SectionName = "Marketing:Links";

    /// <summary>
    /// Whether <c>GET /api/v1/l/{code}</c> answers at all (compose <c>Marketing__Links__Enabled</c>).
    /// Default true. False: the redirect answers a bare 404 for every code and counts nothing. The
    /// platform-admin links routes do not depend on it, so links can be prepared while it is off.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
