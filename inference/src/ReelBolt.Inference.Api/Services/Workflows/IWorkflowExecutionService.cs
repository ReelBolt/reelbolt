using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Inference.Api.Services.Styles;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Extra attribution a start carries (public-api T-007): the API key whose caps the run spends against, and a
/// caller-chosen correlation id (a queued run request's id, so a crash between its claim and the start can be
/// reconciled by looking the execution up). Null for every dashboard start, which keeps its old behaviour.
/// </summary>
public sealed record ExecutionOrigin(Guid? ApiKeyId = null, string? CorrelationId = null);

/// <summary>
/// Service for executing workflows.
/// </summary>
public interface IWorkflowExecutionService
{
    /// <summary>
    /// Executes a workflow asynchronously.
    /// </summary>
    /// <param name="projectId">The ID of the project containing the workflow.</param>
    /// <param name="workflowId">The ID of the workflow to execute.</param>
    /// <param name="initiatedByUserId">The ID of the user initiating the execution.</param>
    /// <param name="userRequest">Optional user request/input for the workflow.</param>
    /// <param name="initiator">Who initiated this execution (Human, Assistant, or MCP).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="regenerateVideoClips">Pay for fresh VideoGenerate clips instead of reusing identical ones.</param>
    /// <param name="runChoices">The run's editing style and output-format override, already resolved (null = neither).</param>
    /// <returns>A response containing the created execution details.</returns>
    /// <exception cref="WorkflowNotFoundException">Thrown if the project or workflow is not found.</exception>
    Task<WorkflowExecutionResponse> ExecuteAsync(
        Guid projectId,
        Guid workflowId,
        Guid initiatedByUserId,
        string? userRequest,
        ExecutionInitiator initiator,
        CancellationToken ct,
        bool regenerateVideoClips = false,
        RunChoices? runChoices = null);

    /// <summary>
    /// The public API's start: the same queue as <see cref="ExecuteAsync"/>, plus the key the run spends against
    /// and the caller's correlation id. The default body keeps simple test doubles compiling; the real service
    /// overrides it so <see cref="WorkflowExecution.ApiKeyId"/> and the correlation id are persisted.
    /// </summary>
    async Task<WorkflowExecutionResponse> ExecuteAsync(
        Guid projectId,
        Guid workflowId,
        Guid initiatedByUserId,
        string? userRequest,
        ExecutionInitiator initiator,
        CancellationToken ct,
        bool regenerateVideoClips,
        RunChoices? runChoices,
        ExecutionOrigin origin)
        => await ExecuteAsync(projectId, workflowId, initiatedByUserId, userRequest, initiator, ct, regenerateVideoClips, runChoices);

    /// <summary>
    /// The one queueing path, for a caller that owns a hidden system workflow kind (the editor's
    /// <see cref="WorkflowDefinitionKind.TimelineRender"/> renders). <see cref="ExecuteAsync"/> pins
    /// <see cref="WorkflowDefinitionKind.Standard"/>, so a hidden workflow can never be started
    /// through the public execute routes; only the code that created it passes its kind here.
    /// <paramref name="beforePublish"/>, when given, runs after the execution row is saved and before
    /// the bus message goes out: a caller that wrapped its own workflow insert and this call in one
    /// transaction commits there, so the engine never receives a run whose rows are not committed,
    /// and any earlier failure rolls everything back.
    /// </summary>
    Task<WorkflowExecutionResponse> QueueAsync(
        Guid projectId,
        Guid workflowId,
        Guid initiatedByUserId,
        string? userRequest,
        ExecutionInitiator initiator,
        CancellationToken ct,
        bool regenerateVideoClips,
        RunChoices? runChoices,
        WorkflowDefinitionKind kind,
        Guid? executionId,
        Func<CancellationToken, Task>? beforePublish = null);
}
