using MassTransit;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Marketplace;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.Inference.Api.Consumers;

/// <summary>The engine's answer to a marketplace preview request. A no-op when <c>Marketplace:Enabled</c> is off. Never throws: the sweeper recovers anything left preparing.</summary>
public sealed class MarketplacePreviewFinishedConsumer(
    MarketplacePreviewFinalizer finalizer,
    IOptions<MarketplaceOptions> options,
    ILogger<MarketplacePreviewFinishedConsumer> logger) : IConsumer<MarketplacePreviewFinished>
{
    public async Task Consume(ConsumeContext<MarketplacePreviewFinished> context)
    {
        if (!options.Value.Enabled)
            return;

        try
        {
            await finalizer.HandleAsync(context.Message, context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Handling the marketplace preview result of version {VersionId} failed", context.Message.VersionId);
        }
    }
}
