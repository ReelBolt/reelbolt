namespace ReelBolt.Inference.Api.Services.Marketplace;

/// <summary>
/// Asked once a version row is committed to start its preview job. Layer 2 replaces the no-op with the engine job; the job
/// ends by calling <see cref="MarketplacePublishService.FinalizeVersionAsync"/>.
/// </summary>
public interface IMarketplacePreviewDispatcher
{
    Task DispatchAsync(Guid recipeId, Guid versionId, CancellationToken ct);
}

public sealed class LoggingMarketplacePreviewDispatcher(ILogger<LoggingMarketplacePreviewDispatcher> logger) : IMarketplacePreviewDispatcher
{
    public Task DispatchAsync(Guid recipeId, Guid versionId, CancellationToken ct)
    {
        logger.LogInformation("Marketplace preview requested for recipe {RecipeId} version {VersionId}; no preview worker is registered", recipeId, versionId);
        return Task.CompletedTask;
    }
}
