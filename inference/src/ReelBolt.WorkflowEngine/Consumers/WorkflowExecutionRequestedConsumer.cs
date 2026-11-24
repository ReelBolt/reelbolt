using MassTransit;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.Shared.IntegrationEvents;

namespace ReelBolt.WorkflowEngine.Consumers;

/// <summary>
/// MassTransit consumer that processes workflow execution requests from RabbitMQ.
///
/// <para>
/// This consumer <b>dispatches</b> — it does not execute. It hands the request to
/// <see cref="WorkflowExecutionRunner"/> and returns, so the RabbitMQ delivery is acknowledged
/// within milliseconds instead of being held open for the execution's entire duration. See
/// <see cref="WorkflowExecutionRunner"/>'s summary for the failure this prevents: awaiting the
/// workflow here made RabbitMQ's <c>consumer_timeout</c> a hard cap on how long any workflow was
/// allowed to take, and three real executions were destroyed at exactly that cap — and then
/// blamed on the user — before it was tracked down.
/// </para>
/// </summary>
public class WorkflowExecutionRequestedConsumer : IConsumer<WorkflowExecutionRequested>
{
    private readonly WorkflowExecutionRunner _runner;
    private readonly ILogger<WorkflowExecutionRequestedConsumer> _logger;
    private readonly ReelBolt.WorkflowEngine.Drain.EngineDrainTarget? _drain;

    public WorkflowExecutionRequestedConsumer(
        WorkflowExecutionRunner runner,
        ILogger<WorkflowExecutionRequestedConsumer> logger,
        ReelBolt.WorkflowEngine.Drain.EngineDrainTarget? drain = null)
    {
        _drain = drain;
        _runner = runner;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<WorkflowExecutionRequested> context)
    {
        var message = context.Message;
        _logger.LogInformation(
            "Received workflow execution request: ExecutionId={ExecutionId}, CorrelationId={CorrelationId}",
            message.ExecutionId, message.CorrelationId);

        try
        {
            // context.CancellationToken is passed as the ADMISSION token only — it governs the wait
            // for a concurrency slot, never the execution itself. That distinction is the fix: a
            // transport-level cancellation while we are still queueing costs nothing (the execution
            // is untouched and still Queued, so redelivery restarts it cleanly), whereas the same
            // token reaching the running execution is what used to shred hours of work.
            // Drain: a delivery still waiting for a slot is released (the OCE below leaves it unacked and
            // the row Queued) so the new engine takes it. Off-path the token is just the context's.
            using CancellationTokenSource? linked = _drain is null
                ? null
                : CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _drain.DrainToken);
            AdmissionDecision decision = await _runner.DispatchAsync(
                message.ExecutionId, message.CorrelationId, linked?.Token ?? context.CancellationToken, message.RegenerateVideoClips, message.OrganizationId,
                message.AdmittedRunRateBucket);

            if (!decision.Admitted)
            {
                // A refusal is not an error and not a loss, and the delivery is acknowledged either way.
                // A kill-switch refusal leaves the row Queued, and ExecutionRecoverySweeper re-publishes
                // it once it has been queued for its stale window. Throwing here instead would push the
                // message through MassTransit's retries into the dead-letter queue, where the run would
                // need a human to replay it — the opposite of what a kill switch is for. An
                // already_running_here refusal leaves a run already executing here untouched.
                _logger.LogInformation(
                    "Workflow execution {ExecutionId} was not admitted ({Reason}); the delivery is acknowledged and the row is left as it is (CorrelationId={CorrelationId})",
                    message.ExecutionId,
                    decision.Reason,
                    message.CorrelationId);
                return;
            }

            _logger.LogInformation(
                "Workflow execution dispatched to the background runner: ExecutionId={ExecutionId}, CorrelationId={CorrelationId}",
                message.ExecutionId,
                message.CorrelationId);
        }
        catch (OperationCanceledException) when (_drain is not null && _drain.DrainToken.IsCancellationRequested && !context.CancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Workflow execution {ExecutionId} was still waiting for admission when drain began; its delivery goes back to the broker",
                message.ExecutionId);
            throw new ReelBolt.WorkflowEngine.Drain.DrainReleasedException();
        }
        catch (OperationCanceledException)
        {
            // Cancelled while waiting for a slot. Nothing has started, so letting the broker
            // redeliver is exactly right.
            _logger.LogInformation(
                "Workflow execution {ExecutionId} was not admitted before its delivery was cancelled; it will be redelivered",
                message.ExecutionId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to dispatch workflow execution {ExecutionId}", message.ExecutionId);

            throw; // Let MassTransit handle retry/DLQ
        }
    }
}
