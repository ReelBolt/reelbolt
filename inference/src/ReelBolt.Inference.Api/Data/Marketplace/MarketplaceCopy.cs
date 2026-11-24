namespace ReelBolt.Inference.Api.Data.Marketplace;

/// <summary>
/// One organization's copy of a recipe; unique (recipe, org) gives "distinct organizations". Table
/// <c>marketplace_copies</c>. <see cref="WorkflowId"/> is a plain column (no FK): the workflow table is engine-owned
/// and may not exist when the API migrates.
/// </summary>
public class MarketplaceCopy
{
    public Guid Id { get; set; }
    public Guid RecipeId { get; set; }
    public Guid VersionId { get; set; }
    public Guid OrgId { get; set; }
    public Guid? WorkflowId { get; set; }
    public DateTime FirstCopiedAt { get; set; } = DateTime.UtcNow;

    public MarketplaceRecipe? Recipe { get; set; }
}
