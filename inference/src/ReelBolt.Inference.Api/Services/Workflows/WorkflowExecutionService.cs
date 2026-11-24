using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.IntegrationEvents;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Service for executing workflows.
/// </summary>
public class WorkflowExecutionService : IWorkflowExecutionService
{
    private readonly InferenceApiDbContext _db;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly ILogger<WorkflowExecutionService> _logger;
    private readonly IPlanGateService? _planGate;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly IDesignSystemPinService? _designSystemPinService;

    public WorkflowExecutionService(
        InferenceApiDbContext db,
        IPublishEndpoint publishEndpoint,
        ILogger<WorkflowExecutionService> logger,
        IPlanGateService? planGate = null,
        IOrganizationEntitlements? entitlements = null,
        IDesignSystemPinService? designSystemPinService = null)
    {
        _planGate = planGate;
        // B8c: the run's queue priority comes from this org's plan entitlement (RunPriority). Optional
        // so the many tests that construct this service directly keep compiling; the app registers it
        // (InferenceApiServiceCollectionExtensions, AddReelBoltPlanEntitlements).
        _entitlements = entitlements;
        // The per-run design-system pin (SPEC 9.1). Optional so service-only tests keep compiling;
        // a null service simply queues without a pin.
        _designSystemPinService = designSystemPinService;
        _db = db;
        _publishEndpoint = publishEndpoint;
        _logger = logger;
    }

    public Task<WorkflowExecutionResponse> ExecuteAsync(
        Guid projectId,
        Guid workflowId,
        Guid initiatedByUserId,
        string? userRequest,
        ExecutionInitiator initiator,
        CancellationToken ct,
        bool regenerateVideoClips = false,
        RunChoices? runChoices = null)
        => QueueCoreAsync(projectId, workflowId, initiatedByUserId, userRequest, initiator, ct,
            regenerateVideoClips, runChoices, WorkflowDefinitionKind.Standard, executionId: null, beforePublish: null, origin: null);

    /// <inheritdoc />
    public Task<WorkflowExecutionResponse> ExecuteAsync(
        Guid projectId,
        Guid workflowId,
        Guid initiatedByUserId,
        string? userRequest,
        ExecutionInitiator initiator,
        CancellationToken ct,
        bool regenerateVideoClips,
        RunChoices? runChoices,
        ExecutionOrigin origin)
        => QueueCoreAsync(projectId, workflowId, initiatedByUserId, userRequest, initiator, ct,
            regenerateVideoClips, runChoices, WorkflowDefinitionKind.Standard, executionId: null, beforePublish: null, origin);

    /// <summary>
    /// The one queueing path. <paramref name="kind"/> is the kind of workflow the caller means to run:
    /// every public execute path passes <see cref="WorkflowDefinitionKind.Standard"/>, so a system
    /// workflow (the hidden composition-revision workflow) is reported exactly like a missing one and
    /// can only be started by the service that owns it (<c>CompositionRevisionService</c>), which also
    /// pre-assigns <paramref name="executionId"/> so its request row and the execution share one id.
    /// </summary>
    public Task<WorkflowExecutionResponse> QueueAsync(
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
        Func<CancellationToken, Task>? beforePublish = null)
        => QueueCoreAsync(projectId, workflowId, initiatedByUserId, userRequest, initiator, ct,
            regenerateVideoClips, runChoices, kind, executionId, beforePublish, origin: null);

    private async Task<WorkflowExecutionResponse> QueueCoreAsync(
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
        Func<CancellationToken, Task>? beforePublish,
        ExecutionOrigin? origin)
    {
        // Load project
        Project? project = await _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project == null)
            throw new WorkflowNotFoundException($"Project {projectId} not found.");

        // Load workflow
        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.FirstOrDefaultAsync(
            w => w.Id == workflowId && w.ProjectId == projectId && w.Kind == kind, ct);
        if (workflow == null)
            throw new WorkflowNotFoundException($"Workflow {workflowId} not found in project {projectId}.");

        _logger.LogInformation(
            "Workflow execution requested via API (ProjectId={ProjectId}, WorkflowId={WorkflowId}, UserId={UserId}, UserRequestLength={UserRequestLength})",
            projectId,
            workflowId,
            initiatedByUserId,
            userRequest?.Length ?? 0);

        string correlationId = origin?.CorrelationId ?? Guid.NewGuid().ToString();

        Guid organizationId = await _db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.OrganizationId)
            .FirstOrDefaultAsync(ct);

        // Plan concurrency cap (B8a): refuse now with a clear 429 rather than queueing behind it.
        if (_planGate is not null && await _planGate.CheckExecuteAsync(organizationId, ct) is { } refused)
            throw new PlanLimitException(refused);

        // The per-run design-system pin (SPEC 9.1): the project's currently Approved design and
        // product-map versions, snapshotted now. Rebuilt on every queue, including a replay; no
        // request field can override it.
        // A brief that chose designSystem "none" runs without brand tokens: nothing is pinned.
        string? designSystemPinJson = _designSystemPinService is null || runChoices?.Brief?.DesignSystem == RunBrief.DesignSystemNone
            ? null
            : await _designSystemPinService.BuildPinJsonAsync(projectId, ct);

        // The chosen output resolution must be one the plan renders (402 PLAN_NOT_ENTITLED). Checked
        // here, with the concurrency cap and before any row is written, so every execute path — REST,
        // the assistant's REST twin, the assistant tool, composition revisions — refuses it the same way.
        if (_planGate is not null && runChoices?.OutputResolution is { } resolution
            && await _planGate.CheckResolutionAsync(organizationId, resolution, ct) is { } tooLarge)
            throw new PlanLimitException(tooLarge);

        WorkflowExecution execution = new()
        {
            Id = executionId ?? Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            ProjectId = projectId,
            OrganizationId = organizationId,
            Status = ExecutionStatus.Queued,
            QueuedAt = DateTime.UtcNow,
            IterationCount = 0,
            CorrelationId = correlationId,
            InitiatedByUserId = initiatedByUserId,
            UserRequest = userRequest,
            Initiator = initiator,
            // The run's editing style and output-format override (docs/editing-styles.md), as a
            // snapshot: null for a run that chose neither, which keeps it byte-identical.
            StyleConfigJson = runChoices?.Style is { } style ? StyleJson.Serialize(style) : null,
            OutputFormatOverride = runChoices?.OutputFormat,
            // The design-system pin the run is frozen against. Null when nothing was approved.
            DesignSystemPinJson = designSystemPinJson,
            OutputResolution = runChoices?.OutputResolution,
            // The typed run brief's stored part (docs/run-brief.md); null when the run set none of it.
            RunBriefJson = runChoices?.Brief is { } storedBrief ? RunBriefJson.Serialize(storedBrief) : null,
            // T-015: the bucket the guard admitted this run under (set only inside the classify-under-lock start
            // callback), persisted so the engine's RUN_CLASS_CHANGED re-check survives a retry or a recovery re-publish.
            AdmittedRunRateBucket = RunRateAdmission.CurrentWireValue,
            // public-api T-007: the key whose caps this run spends against (null for every dashboard start).
            ApiKeyId = origin?.ApiKeyId
        };

        _db.WorkflowExecutions.Add(execution);
        await _db.SaveChangesAsync(ct);
        // S-11: from here the recovery sweeper will start this run even if the publish below fails.
        RunRateAdmission.ReportCommitted(execution.Id);

        // The caller's commit point (see IWorkflowExecutionService.QueueAsync): before the publish,
        // so the engine is never handed an execution it cannot read yet.
        if (beforePublish is not null)
            await beforePublish(ct);

        _logger.LogInformation(
            "Workflow execution queued (ExecutionId={ExecutionId}, WorkflowId={WorkflowId}, ProjectId={ProjectId}, CorrelationId={CorrelationId})",
            execution.Id,
            workflowId,
            projectId,
            correlationId);

        // B8c: how early this run is dequeued is a plan entitlement and nothing else. Read per
        // call (the entitlement set is cached, and a downgrade must apply to the next run); a null
        // service, a missing plan or any read failure degrades to Standard, which is the behaviour
        // that shipped before priority existed.
        int runPriority = await ResolveRunPriorityAsync(organizationId, ct);

        WorkflowExecutionRequested request = new()
        {
            ExecutionId = execution.Id,
            WorkflowDefinitionId = workflowId,
            ProjectId = projectId,
            OrganizationId = organizationId,
            InitiatedByUserId = initiatedByUserId,
            CorrelationId = correlationId,
            RequestedAt = DateTime.UtcNow,
            UserRequest = userRequest,
            RegenerateVideoClips = regenerateVideoClips,
            StyleConfigJson = execution.StyleConfigJson,
            RunBriefJson = execution.RunBriefJson,
            DesignSystemPinJson = execution.DesignSystemPinJson,
            OutputFormat = execution.OutputFormatOverride?.ToString(),
            OutputResolution = execution.OutputResolution,
            // T-012: set only inside the guard's classify-under-lock start callback (assistant/MCP execute);
            // null for every other start, which the engine does not re-check.
            AdmittedRunRateBucket = execution.AdmittedRunRateBucket
        };

        // Publish to RabbitMQ via MassTransit. The priority overload is used ONLY for a non-standard
        // class: a standard run keeps the plain publish it always had, so nothing about a Trial,
        // Creator or self-host run changes. The engine must declare the queue with x-max-priority for
        // the stamped value to order anything (see its receive-endpoint registration); without that
        // argument the broker accepts the property and ignores it.
        if (RunPriority.AmqpPriorityToStamp(runPriority) is { } amqpPriority)
        {
            await _publishEndpoint.Publish(request, ctx => ctx.SetPriority(amqpPriority), ct);
        }
        else
        {
            await _publishEndpoint.Publish(request, ct);
        }

        _logger.LogInformation(
            "Workflow execution request published to bus (ExecutionId={ExecutionId}, CorrelationId={CorrelationId}, RunPriority={RunPriority})",
            execution.Id,
            correlationId,
            runPriority);

        return new WorkflowExecutionResponse(
            execution.Id, execution.WorkflowDefinitionId, execution.Status.ToString(),
            execution.StartedAt, execution.CompletedAt, execution.IterationCount,
            execution.ResultJson, execution.CorrelationId, execution.ErrorMessage,
            new List<StepResultResponse>(), new List<ReviewScoreResponse>(),
            execution.UserRequest,
            runChoices?.Style?.Name,
            execution.OutputFormatOverride?.ToString(),
            DesignSystemPinJson.TryParse(execution.DesignSystemPinJson),
            execution.OutputResolution is { } shortSide ? OutputResolution.Label(shortSide) : null,
            Brief: RunBriefView.Compose(execution));
    }

    /// <summary>
    /// The organization's queue-priority class (B8c). Best-effort: a missing entitlement service or a
    /// failed read must never stop a run from being queued, and
    /// <see cref="RunPriority.Standard"/> is the safe answer — it is also what a self-host
    /// deployment gets, whose entitlement set is unlimited.
    /// </summary>
    private async Task<int> ResolveRunPriorityAsync(Guid organizationId, CancellationToken ct)
    {
        if (_entitlements is null)
        {
            return RunPriority.Standard;
        }

        try
        {
            return await RunPriority.OfAsync(_entitlements, organizationId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Could not read the plan priority of organization {OrganizationId}; queueing the run at standard priority",
                organizationId);
            return RunPriority.Standard;
        }
    }
}
