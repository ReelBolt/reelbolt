namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// The only reason words an author ever sees. Internal reasons (holds, safety, digest matches, human review, caps) say things the
/// author must not learn (that their organization or a teammate is under a safety hold, that content matched something removed), so
/// every one of them, and every unknown string, becomes <see cref="Policy"/>. This is the canonical mapping: later layers reuse it.
/// </summary>
public static class MarketplaceAuthorReasons
{
    public const string Content = "content";
    public const string Policy = "policy";
    public const string Preview = "preview";
    public const string Copyright = "copyright";
    public const string Quality = "quality";
    public const string Other = "other";

    /// <summary>The closed set of public categories.</summary>
    public static readonly IReadOnlyList<string> PublicCategories = [Content, Policy, Preview, Copyright, Quality, Other];

    /// <summary>Null stays null; an allowlisted internal reason maps to its category; anything else is <see cref="Policy"/>.</summary>
    public static string? ToPublicCategory(string? internalReason)
    {
        if (string.IsNullOrWhiteSpace(internalReason))
            return null;
        return internalReason.Trim().ToLowerInvariant() switch
        {
            "text_blocked" or "content" => Content,
            "preview_failed" or "preview_blocked" or "preview" => Preview,
            "copyright" => Copyright,
            "quality" or "low_quality" => Quality,
            "other" => Other,
            _ => Policy
        };
    }
}
