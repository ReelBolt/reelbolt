namespace ReelBolt.Inference.Api.Data.Marketplace;

/// <summary>
/// Append-only trail of marketplace decisions (publish, list, reject, remove, restore, report handling). Table
/// <c>marketplace_audit</c>. No FK to the recipe: the trail outlives a purged recipe. The account purge clears
/// <see cref="ActorUserId"/> for a purged user.
/// </summary>
public class MarketplaceAuditEntry
{
    public Guid Id { get; set; }
    public Guid? RecipeId { get; set; }
    public Guid? VersionId { get; set; }
    public string Action { get; set; } = "";

    /// <summary><c>author</c>, <c>system</c> or <c>admin</c>.</summary>
    public string ActorKind { get; set; } = "system";

    public Guid? ActorUserId { get; set; }

    /// <summary>Free-form detail (jsonb): reason codes, old and new status.</summary>
    public string? DetailJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
