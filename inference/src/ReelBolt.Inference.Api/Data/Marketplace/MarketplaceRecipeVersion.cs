namespace ReelBolt.Inference.Api.Data.Marketplace;

/// <summary>An immutable published version of a recipe; a republish adds a row. Table <c>marketplace_recipe_versions</c>.</summary>
public class MarketplaceRecipeVersion
{
    public Guid Id { get; set; }
    public Guid RecipeId { get; set; }

    /// <summary>1-based version number within the recipe.</summary>
    public int N { get; set; }

    /// <summary>The account that submitted this version. Trust and the daily cap are the submitter's, not the recipe author's.</summary>
    public Guid SubmittedByUserId { get; set; }

    /// <summary>SHA-256 (hex) of the canonical snapshot and manifest. A match with human-rejected or removed content sends a version to review.</summary>
    public string? SnapshotDigest { get; set; }

    /// <summary>The portable snapshot (jsonb).</summary>
    public string SnapshotJson { get; set; } = "{}";

    /// <summary>The portable app manifest (jsonb).</summary>
    public string ManifestJson { get; set; } = "{}";

    /// <summary>INTERNAL: how the preview bytes were chosen. Never serialised to any DTO.</summary>
    public string? PreviewSourceJson { get; set; }

    /// <summary>SHA-256 of the poster as copied to the final key; serving verifies it.</summary>
    public string? PosterSha256 { get; set; }

    public string? PreviewKey { get; set; }
    public string? PosterKey { get; set; }
    public string? PreviewSha256 { get; set; }
    public double? PreviewSeconds { get; set; }
    /// <summary>Hex nonce of the preview run currently allowed to finish this version; a result carrying any other nonce is ignored.</summary>
    public string? PreviewRunNonce { get; set; }

    public PreviewStatus PreviewStatus { get; set; } = PreviewStatus.Preparing;

    /// <summary>Moderation audit ids, lint codes and verdict summary (jsonb).</summary>
    public string? CheckJson { get; set; }

    public string? ConsentVersion { get; set; }
    public DateTime? ConsentAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public MarketplaceRecipe? Recipe { get; set; }
}
