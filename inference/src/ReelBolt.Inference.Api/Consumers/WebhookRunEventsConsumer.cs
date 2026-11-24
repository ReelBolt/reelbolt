using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Tenancy;
using ReelBolt.Inference.Api.Services.Webhooks;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Consumers;

/// <summary>
/// Turns the engine's run events into webhook deliveries. One class on
/// one queue of its own (MassTransit binds each consumer's queue to the message exchange), so the Go API's consumers
/// keep receiving their copy. Nothing happens while <c>PublicApi:Enabled</c> is off.
/// </summary>
/// <remarks>
/// The terminal outcome comes from the MESSAGE, not the execution row (Q-7): a retried run reuses the row, so a
/// completed message's <c>FinalStatus</c> (<c>Passed</c>/<c>Failed</c>/<c>Cancelled</c>) is the one truth, and a
/// failed message can only say "failed". When the message cannot distinguish a cancel from a failure, the row is
/// consulted only if it is terminal and its <c>CompletedAt</c> is at or before the message's occurrence (and it was
/// not requeued after it, <c>QueuedAt &gt; OccurredAt</c>); otherwise the message's kind decides.
/// Which runs produce events: Standard workflows and any run the public API started (<c>api_key_id</c> set);
/// the hidden composition-revision workflows do not.
/// </remarks>
public sealed class WebhookRunEventsConsumer(
    InferenceApiDbContext db,
    WebhookEventWriter writer,
    IOptions<PublicApiOptions> options,
    ITenantContext? tenant = null) :
    IConsumer<WorkflowExecutionCompleted>,
    IConsumer<WorkflowExecutionFailed>,
    IConsumer<WorkflowStepCompleted>
{
    public Task Consume(ConsumeContext<WorkflowExecutionCompleted> context) =>
        TerminalAsync(context.Message.ExecutionId, context.Message.FinalStatus, completedMessage: true,
            context.Message.CompletedAt, context.CancellationToken);

    public Task Consume(ConsumeContext<WorkflowExecutionFailed> context) =>
        TerminalAsync(context.Message.ExecutionId, messageFinalStatus: null, completedMessage: false,
            context.Message.FailedAt, context.CancellationToken);

    public async Task Consume(ConsumeContext<WorkflowStepCompleted> context)
    {
        if (!options.Value.Enabled) return;
        WorkflowStepCompleted message = context.Message;
        CancellationToken ct = context.CancellationToken;

        using IDisposable system = tenant.System();
        // The step stream is the busiest one: do nothing unless somebody subscribed to it.
        bool subscribed = await db.WebhookEndpoints.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(e => e.Enabled && e.Events.Contains(WebhookEventTypes.RunStepCompleted), ct);
        if (!subscribed) return;

        RunFacts? run = await ReadRunAsync(message.ExecutionId, ct);
        if (run is null || !run.Eligible) return;

        await writer.EnqueueAsync(run.OrganizationId, run.ProjectId, WebhookEventTypes.RunStepCompleted,
            $"{WebhookEventTypes.RunStepCompleted}:{message.StepResultId}", message.CompletedAt,
            new WebhookEventData(
                RunId: run.Id, ProjectId: run.ProjectId, WorkflowId: run.WorkflowId,
                Status: message.StepStatus.ToLowerInvariant(), RunRequestId: run.RunRequestId,
                StepId: message.StepId, StepOrder: message.StepOrder, StepType: message.StepType), ct);
    }

    private async Task TerminalAsync(
        Guid executionId, string? messageFinalStatus, bool completedMessage, DateTime occurredAt, CancellationToken ct)
    {
        if (!options.Value.Enabled) return;

        using IDisposable system = tenant.System();
        RunFacts? run = await ReadRunAsync(executionId, ct);
        if (run is null || !run.Eligible) return;

        (string type, string status) = ResolveOutcome(run, messageFinalStatus, completedMessage, occurredAt);

        List<(string? FailureCode, long Charged)> steps = (await db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.WorkflowExecutionId == executionId && r.SupersededAt == null)
                .Select(r => new { r.FailureCode, r.CreditsChargedMillis })
                .ToListAsync(ct))
            .Select(r => (r.FailureCode, r.CreditsChargedMillis))
            .ToList();
        string? failureCode = type == WebhookEventTypes.RunFailed
            ? steps.Select(s => s.FailureCode).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "RUN_FAILED"
            : null;
        decimal? credits = steps.Count == 0 ? null : steps.Sum(s => s.Charged) / 1000m;

        // The event time is part of the key: a retried run that fails again is a new event, a redelivered message is not.
        await writer.EnqueueAsync(run.OrganizationId, run.ProjectId, type,
            $"{type}:{executionId}:{occurredAt.ToUniversalTime().Ticks}", occurredAt,
            new WebhookEventData(
                RunId: run.Id, ProjectId: run.ProjectId, WorkflowId: run.WorkflowId, Status: status,
                FailureCode: failureCode, CreditsCharged: credits, RunRequestId: run.RunRequestId), ct);
    }

    /// <summary>
    /// The message's own terminal status when it carries one; otherwise the row's only if the row is terminal,
    /// completed no later than the message, and not requeued after it. A message that can only say "failed" never
    /// turns a stale <c>Passed</c> row into a success, and a retried run's new outcome is never hidden by the old row.
    /// </summary>
    private static (string Type, string Status) ResolveOutcome(
        RunFacts run, string? messageFinalStatus, bool completedMessage, DateTime occurredAt)
    {
        ExecutionStatus? fromMessage =
            completedMessage
            && Enum.TryParse(messageFinalStatus, ignoreCase: true, out ExecutionStatus parsed)
            && parsed is ExecutionStatus.Passed or ExecutionStatus.Failed or ExecutionStatus.Cancelled
                ? parsed
                : null;

        ExecutionStatus outcome;
        if (fromMessage is { } known)
        {
            outcome = known;
        }
        else
        {
            bool rowTerminal = run.Status is ExecutionStatus.Passed or ExecutionStatus.Failed or ExecutionStatus.Cancelled;
            bool rowBeforeMessage = run.CompletedAt is { } completedAt && completedAt <= occurredAt
                && !(run.QueuedAt is { } queuedAt && queuedAt > occurredAt);
            outcome = rowTerminal && rowBeforeMessage
                ? run.Status
                : completedMessage ? ExecutionStatus.Passed : ExecutionStatus.Failed;
        }

        return outcome switch
        {
            ExecutionStatus.Cancelled => (WebhookEventTypes.RunCancelled, "cancelled"),
            ExecutionStatus.Failed => (WebhookEventTypes.RunFailed, "failed"),
            _ => (WebhookEventTypes.RunSucceeded, "succeeded")
        };
    }

    private sealed record RunFacts(
        Guid Id, Guid OrganizationId, Guid ProjectId, Guid WorkflowId, ExecutionStatus Status, bool Eligible, Guid? RunRequestId,
        DateTime? CompletedAt, DateTime? QueuedAt);

    private async Task<RunFacts?> ReadRunAsync(Guid executionId, CancellationToken ct)
    {
        var row = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == executionId)
            .Select(e => new
            {
                e.Id,
                e.OrganizationId,
                e.ProjectId,
                e.WorkflowDefinitionId,
                e.Status,
                e.ApiKeyId,
                e.CorrelationId,
                e.CompletedAt,
                e.QueuedAt,
                Kind = e.WorkflowDefinition.Kind
            })
            .FirstOrDefaultAsync(ct);
        if (row is null) return null;

        Guid organizationId = row.OrganizationId;
        if (organizationId == Guid.Empty)
        {
            organizationId = await db.Projects.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Id == row.ProjectId).Select(p => p.OrganizationId).FirstOrDefaultAsync(ct);
            if (organizationId == Guid.Empty) return null;
        }

        Guid? requestId = null;
        if (row.CorrelationId.StartsWith(ApiRunRequest.CorrelationPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(row.CorrelationId[ApiRunRequest.CorrelationPrefix.Length..], "N", out Guid parsed))
            requestId = parsed;

        bool eligible = row.Kind == WorkflowDefinitionKind.Standard || row.ApiKeyId is not null;
        return new RunFacts(row.Id, organizationId, row.ProjectId, row.WorkflowDefinitionId, row.Status, eligible, requestId,
            row.CompletedAt, row.QueuedAt);
    }
}
