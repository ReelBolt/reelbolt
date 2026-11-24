using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Consumers;

/// <summary>
/// A run ended Failed: append <c>activation.run_failed</c> once per execution (the claim key carries the
/// execution id, so a redelivery writes nothing), detail = the failing step's FailureCode reduced to a closed
/// token (<c>other</c> when the code is absent or not a plain token). Best-effort: never throws, so the
/// message is never retried over telemetry.
/// </summary>
public sealed class RunFailedProductEventConsumer(
    InferenceApiDbContext db,
    IProductEventLog log,
    ITenantContext? tenant = null,
    ILogger<RunFailedProductEventConsumer>? logger = null) : IConsumer<WorkflowExecutionFailed>
{
    /// <summary>Claim-key prefix; the key is this plus the execution id (47 characters, under the 64 bound).</summary>
    public const string ClaimPrefix = "run_failed:";

    /// <inheritdoc />
    public async Task Consume(ConsumeContext<WorkflowExecutionFailed> context)
    {
        WorkflowExecutionFailed message = context.Message;
        if (message.InitiatedByUserId is not Guid userId || userId == Guid.Empty) return;
        CancellationToken ct = context.CancellationToken;
        try
        {
            Guid? organizationId;
            string? code;
            using (tenant.System())
            {
                organizationId = await db.WorkflowExecutions
                    .Where(e => e.Id == message.ExecutionId)
                    .Select(e => (Guid?)e.OrganizationId)
                    .FirstOrDefaultAsync(ct);
                code = await db.WorkflowStepResults
                    .Where(r => r.WorkflowExecutionId == message.ExecutionId && r.Status == StepStatus.Failed && r.FailureCode != null)
                    .OrderBy(r => r.CompletedAt)
                    .Select(r => r.FailureCode)
                    .FirstOrDefaultAsync(ct);
            }

            await log.ClaimAndAppendWithDetailAsync(
                userId, organizationId, ProductEventNames.RunFailed, ClaimPrefix + message.ExecutionId,
                ProductEventNames.ClosedToken(code), ProductEventSources.Server, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Product event {Event} could not be recorded", ProductEventNames.RunFailed);
        }
    }
}
