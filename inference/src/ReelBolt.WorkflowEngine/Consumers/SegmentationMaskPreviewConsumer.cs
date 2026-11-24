using MassTransit;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.WorkflowEngine.Services.Segmentation;

namespace ReelBolt.WorkflowEngine.Consumers;

/// <summary>
/// Consumes <see cref="SegmentationMaskPreviewRequested"/>: hands it to <see cref="SegmentationMaskPreviewDispatcher"/>
/// and returns. It dispatches, never produces (the delivery must not be held open for an encode), and never
/// throws: a lost request is repaired by the editor's next poll.
/// </summary>
public class SegmentationMaskPreviewConsumer(
    SegmentationMaskPreviewDispatcher dispatcher,
    ILogger<SegmentationMaskPreviewConsumer> logger) : IConsumer<SegmentationMaskPreviewRequested>
{
    public Task Consume(ConsumeContext<SegmentationMaskPreviewRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            dispatcher.TryEnqueue(context.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not queue a mask preview request for step result {StepResultId}", context.Message?.StepResultId);
        }

        return Task.CompletedTask;
    }
}
