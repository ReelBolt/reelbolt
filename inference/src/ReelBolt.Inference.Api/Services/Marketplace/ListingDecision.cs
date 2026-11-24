namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>What the automatic text check said about everything an author wrote.</summary>
public enum TextVerdict
{
    Allow,
    Review,

    /// <summary>Moderation is off, has no provider, or did not answer: never auto-lists.</summary>
    Unavailable,
    Block
}

/// <summary>What the preview video check (layer 2) said.</summary>
public enum PreviewVerdict
{
    Allow,
    Review,
    Block,

    /// <summary>The preview could not be produced.</summary>
    Failed
}

public enum ListingOutcome
{
    Listed,
    Pending,
    Rejected
}

public sealed record ListingResult(ListingOutcome Outcome, string Reason);

/// <summary>
/// Pure: a version is listed at once only when every check allows it AND the author is trusted (already had a recipe
/// listed) or the organization is on a paid plan. Anything unknown fails toward a human, never toward listing.
/// </summary>
public static class ListingDecision
{
    /// <summary>
    /// Every reason <see cref="Decide"/> can return, kept next to it; a source-scan test fails when a literal is missing here. None of
    /// them reaches an author as written (see <see cref="MarketplaceAuthorReasons"/>).
    /// </summary>
    public static readonly IReadOnlyList<string> Reasons =
    [
        "text_blocked", "preview_blocked", "preview_failed", "moderation_unavailable", "text_review", "preview_not_checked",
        "preview_review", "submitter_held", "submitter_org_held", "digest_match", "human_review_required", "not_trusted", "auto_listed"
    ];

    public static ListingResult Decide(TextVerdict text, PreviewVerdict? preview, bool authorTrusted, bool organizationPaid, bool humanRejected = false, bool digestMatch = false, bool submitterHeld = false, bool submitterOrgHeld = false)
    {
        if (text == TextVerdict.Block)
            return new(ListingOutcome.Rejected, "text_blocked");
        if (preview == PreviewVerdict.Block)
            return new(ListingOutcome.Rejected, "preview_blocked");
        if (preview == PreviewVerdict.Failed)
            return new(ListingOutcome.Rejected, "preview_failed");

        if (text == TextVerdict.Unavailable)
            return new(ListingOutcome.Pending, "moderation_unavailable");
        if (text != TextVerdict.Allow)
            return new(ListingOutcome.Pending, "text_review");

        if (preview is null)
            return new(ListingOutcome.Pending, "preview_not_checked");
        if (preview != PreviewVerdict.Allow)
            return new(ListingOutcome.Pending, "preview_review");

        // The submitter has a project under a CSAM output hold or content quarantine anywhere: a human decides.
        if (submitterHeld)
            return new(ListingOutcome.Pending, "submitter_held");

        // A project the submitter can edit, in one of their organizations, is under a hold or quarantine: a human decides, no hint given.
        if (submitterOrgHeld)
            return new(ListingOutcome.Pending, "submitter_org_held");

        // The same content as something a human rejected or removed, from any author.
        if (digestMatch)
            return new(ListingOutcome.Pending, "digest_match");

        // A human rejected or removed this recipe once: every later version waits for a human, whoever the author is.
        if (humanRejected)
            return new(ListingOutcome.Pending, "human_review_required");

        if (!authorTrusted && !organizationPaid)
            return new(ListingOutcome.Pending, "not_trusted");

        return new(ListingOutcome.Listed, "auto_listed");
    }
}
