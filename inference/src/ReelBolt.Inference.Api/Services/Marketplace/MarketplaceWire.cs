using ReelBolt.Inference.Api.Data.Marketplace;

namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// The wire words of the contract. Enums are stored PascalCase (repo convention); every response says them in lowercase, mapped
/// here and nowhere else, so the C# casing never leaks.
/// </summary>
public static class MarketplaceWire
{
    public static string Status(RecipeStatus status) => status switch
    {
        RecipeStatus.Draft => "draft",
        RecipeStatus.Pending => "pending",
        RecipeStatus.Listed => "listed",
        RecipeStatus.Rejected => "rejected",
        RecipeStatus.Removed => "removed",
        RecipeStatus.Unpublished => "unpublished",
        _ => "draft"
    };

    public static string Preview(PreviewStatus status) => status switch
    {
        PreviewStatus.Preparing => "preparing",
        PreviewStatus.Ready => "ready",
        PreviewStatus.Failed => "failed",
        _ => "preparing"
    };

    /// <summary>A PascalCase enum member as its lowercase wire word.</summary>
    public static string Word(string member) => member.ToLowerInvariant();
}
