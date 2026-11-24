namespace ReelBolt.Inference.Api.Data.Marketplace;

/// <summary>
/// Where a published recipe is in its life (docs/design/recipe-marketplace.md). Stored by name; members are only
/// ever appended, never renumbered or renamed.
/// </summary>
public enum RecipeStatus
{
    /// <summary>A version is being prepared (snapshot, checks, preview).</summary>
    Draft,

    /// <summary>Checks passed or flagged; waiting for a human.</summary>
    Pending,

    Listed,
    Rejected,

    /// <summary>Taken down by the platform. Leaves only through an admin restore.</summary>
    Removed,

    /// <summary>Withdrawn by the author.</summary>
    Unpublished
}

/// <summary>State of a version's preview video.</summary>
public enum PreviewStatus
{
    Preparing,
    Ready,
    Failed
}

public enum ReportReason
{
    Illegal,
    Sexual,
    Copyright,
    Spam,
    Misleading,
    Other
}

public enum ReportState
{
    Open,
    Dismissed,
    Actioned
}
