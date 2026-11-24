using Amazon.S3;
using MassTransit;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Storage;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Storage;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Consumers;

/// <summary>
/// A step finished with a picture: store its 320, 480 and 1920 px WebP previews now, so the first view is fast.
/// A preview is a derivative, so nothing is made for a missing or quarantined project, a held output or a quarantined
/// file, and what was written is deleted again if the source vanished, changed or became held meanwhile.
/// Never throws and never asks for a retry; a skipped or failed preview is simply made on demand later.
/// </summary>
public sealed class ImagePreviewPrewarmConsumer(
    IAmazonS3 s3,
    IConfiguration configuration,
    InferenceApiDbContext db,
    ITenantContext? tenant = null,
    ILogger<ImagePreviewPrewarmConsumer>? logger = null) : IConsumer<WorkflowStepCompleted>
{
    public static readonly int[] Widths = [320, 480, 1920];

    public async Task Consume(ConsumeContext<WorkflowStepCompleted> context)
    {
        WorkflowStepCompleted message = context.Message;
        if (!string.Equals(message.StepStatus, nameof(StepStatus.Completed), StringComparison.OrdinalIgnoreCase)
            || message.ProjectId is not Guid projectId
            || !ProjectOutputKeys.IsAddressableOutput(projectId, message.OutputStorageKey)
            || !ImagePreviewStore.IsImageKey(message.OutputStorageKey!))
        {
            return;
        }

        try
        {
            string bucket = configuration["MinIO:BucketName"] ?? "reelforge";
            string key = message.OutputStorageKey!;
            CancellationToken ct = context.CancellationToken;
            if (!await ImagePreviewGate.MayPreviewAsync(db, tenant, projectId, key, ct)) return;
            int written = await ImagePreviewStore.EnsureAsync(
                s3, bucket, projectId, key, Widths, ct, token => ImagePreviewGate.MayPreviewAsync(db, tenant, projectId, key, token));
            logger?.LogDebug("Pre-warmed {Count} previews for {Key}", written, key);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Preview pre-warm skipped for {Key}", message.OutputStorageKey);
        }
    }
}
