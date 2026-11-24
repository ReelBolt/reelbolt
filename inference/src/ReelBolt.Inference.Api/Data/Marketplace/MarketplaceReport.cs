namespace ReelBolt.Inference.Api.Data.Marketplace;

/// <summary>A user report against a recipe; one open report per (recipe, reporter). Table <c>marketplace_reports</c>.</summary>
public class MarketplaceReport
{
    public Guid Id { get; set; }
    public Guid RecipeId { get; set; }
    public Guid ReporterUserId { get; set; }
    public ReportReason Reason { get; set; }

    /// <summary>Plain text, at most 500 characters.</summary>
    public string? Note { get; set; }

    public ReportState State { get; set; } = ReportState.Open;
    public Guid? HandledBy { get; set; }
    public DateTime? HandledAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MarketplaceRecipe? Recipe { get; set; }
}
