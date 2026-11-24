namespace ReelBolt.Inference.Api.Data.Marketplace;

/// <summary>One user's 1-5 star rating of a recipe; unique (recipe, user). Table <c>marketplace_ratings</c>.</summary>
public class MarketplaceRating
{
    public Guid Id { get; set; }
    public Guid RecipeId { get; set; }
    public Guid UserId { get; set; }
    public int Stars { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public MarketplaceRecipe? Recipe { get; set; }
}
