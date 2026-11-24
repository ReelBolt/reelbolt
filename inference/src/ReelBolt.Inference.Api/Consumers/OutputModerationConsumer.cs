using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Tenancy;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Moderation;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Moderation;
using ReelBolt.Shared.Storage;

namespace ReelBolt.Inference.Api.Consumers;

/// <summary>
/// A step finished: offer its output to the background moderation lane so it has a verdict before
/// anyone asks to see it. Offers only; it reads no object, touches no row and never blocks.
/// </summary>
public sealed class OutputModerationConsumer(
    IOutputModerationEnqueuer enqueuer,
    IOptions<ContentModerationOptions> options,
    InferenceApiDbContext db,
    ITenantContext? tenant = null,
    ILogger<OutputModerationConsumer>? logger = null) : IConsumer<WorkflowStepCompleted>
{
    public async Task Consume(ConsumeContext<WorkflowStepCompleted> context)
    {
        WorkflowStepCompleted message = context.Message;
        if (!options.Value.Enabled
            || !string.Equals(message.StepStatus, nameof(StepStatus.Completed), StringComparison.OrdinalIgnoreCase)
            || message.ProjectId is not Guid projectId
            || !ProjectOutputKeys.IsAddressableOutput(projectId, message.OutputStorageKey))
        {
            return;
        }

        // Offers are capped per organization, so one tenant's burst leaves room for the others.
        Guid organizationId;
        using (tenant.System())
        {
            organizationId = await db.Projects
                .Where(p => p.Id == projectId)
                .Select(p => p.OrganizationId)
                .FirstOrDefaultAsync(context.CancellationToken);
        }

        if (organizationId == Guid.Empty) return;

        // A refusal (the organization already has its few offers waiting, the channel is full, or this exact offer is
        // already queued/running) leaves the output to the sweep; say so, because that is the slow path.
        if (!enqueuer.TryQueue(projectId, message.OutputStorageKey!, organizationId))
            logger?.LogInformation(
                "Output moderation refused the produced output's offer (organization {OrganizationId}); the sweep will pick it up",
                organizationId);
    }
}
