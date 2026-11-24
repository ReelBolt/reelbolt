using NpgsqlTypes;

namespace ReelBolt.Inference.Api.Data.Marketplace;

/// <summary>
/// A recipe published to the public marketplace (API-owned table <c>marketplace_recipes</c>). Published recipes are
/// public by design, so the table carries no <c>Tenant</c> query filter; every owner action checks
/// <see cref="AuthorOrgId"/> against the caller's active organization and answers 404 otherwise.
/// </summary>
public class MarketplaceRecipe
{
    public Guid Id { get; set; }

    public Guid AuthorUserId { get; set; }
    public Guid AuthorOrgId { get; set; }

    public RecipeStatus Status { get; set; } = RecipeStatus.Draft;

    /// <summary>Hidden from listings by the report threshold; independent of <see cref="Status"/>.</summary>
    public bool HiddenByReports { get; set; }

    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Language { get; set; }
    public string CostClass { get; set; } = "";
    public int StepCount { get; set; }

    /// <summary>Distinct step types, for the filter (text[]).</summary>
    public string[] StepTypes { get; set; } = [];

    /// <summary>The version served to the public. No FK (circular with the version rows).</summary>
    public Guid? CurrentVersionId { get; set; }

    public DateTime? ListedAt { get; set; }

    /// <summary>Set the first time the recipe becomes listed; an author with any such recipe is "trusted".</summary>
    public DateTime? EverListedAt { get; set; }

    /// <summary>When a human (admin) last rejected or removed this recipe. While set, no version of it ever auto-lists.</summary>
    public DateTime? HumanRejectedAt { get; set; }

    /// <summary>Optimistic concurrency token: bumped by every state change so two finishing versions cannot both win.</summary>
    public int Revision { get; set; }

    public string? RemovedReason { get; set; }

    public string? ConsentVersion { get; set; }
    public DateTime? ConsentAt { get; set; }

    public string? AuthorLabel { get; set; }

    // Denormalised counters.
    public int CopyOrgs { get; set; }
    public long RunCount { get; set; }
    public int RatingSum { get; set; }
    public int RatingCount { get; set; }

    /// <summary>Generated (name + description + category, <c>simple</c> config). Never written by the app.</summary>
    public NpgsqlTsVector SearchVector { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<MarketplaceRecipeVersion> Versions { get; set; } = [];
}
