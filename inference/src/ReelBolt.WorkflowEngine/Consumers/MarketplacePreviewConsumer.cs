using MassTransit;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Services.Marketplace;

namespace ReelBolt.WorkflowEngine.Consumers;

/// <summary>
/// Consumes <see cref="MarketplacePreviewRequested"/>: hands it to <see cref="MarketplacePreviewDispatcher"/> and returns at once
/// (same discipline as <see cref="MediaDerivativesConsumer"/>). Never throws, so a bad message cannot loop; a redelivery of a
/// version already running or finished is dropped by the dispatcher.
/// </summary>
public sealed class MarketplacePreviewConsumer(
    MarketplacePreviewDispatcher dispatcher,
    ILogger<MarketplacePreviewConsumer> logger) : IConsumer<MarketplacePreviewRequested>
{
    public async Task Consume(ConsumeContext<MarketplacePreviewRequested> context)
    {
        MarketplacePreviewRequested message = context.Message;
        try
        {
            if (!dispatcher.Dispatch(message))
                logger.LogInformation("Marketplace preview for version {VersionId} is already handled; repeat dropped", message.VersionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not hand marketplace preview for version {VersionId} to the runner", message.VersionId);
            try
            {
                await context.Publish(
                    new MarketplacePreviewFinished(message.VersionId, message.RecipeId, false, MarketplacePreviewFailureCodes.Internal, RunNonce: message.RunNonce),
                    CancellationToken.None);
            }
            catch (Exception publishEx)
            {
                logger.LogError(publishEx, "Could not report the failed marketplace preview for version {VersionId}", message.VersionId);
            }
        }
    }
}
