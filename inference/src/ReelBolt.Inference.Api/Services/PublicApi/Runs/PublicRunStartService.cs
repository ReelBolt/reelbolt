using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.PublicApi.Runs;

/// <summary>The public start body: the dashboard's <c>ExecuteWorkflowRequest</c> without <c>regenerateVideoClips</c>.</summary>
public sealed record PublicStartRunRequest(
    string? UserRequest = null,
    Guid? StyleId = null,
    string? StylePresetKey = null,
    StyleConfig? Style = null,
    string? OutputFormat = null,
    string? OutputResolution = null,
    double? AcceptEstimateCredits = null,
    Guid? BriefFileId = null,
    RunBrief? Brief = null);

/// <summary>How a start ended: started, refused by a gate (the dashboard's own result), or held back by the hourly run limit.</summary>
public sealed class RunStartOutcome
{
    public WorkflowExecutionResponse? Started { get; private init; }

    public IActionResult? Refusal { get; private init; }

    /// <summary>Set when the hourly guard was full (not busy): the start may be queued.</summary>
    public RunRateDecision? RateFull { get; private init; }

    public static RunStartOutcome Run(WorkflowExecutionResponse run) => new() { Started = run };

    /// <summary>
    /// Set when the run-limit lock was only busy (transient, not a full window). <see cref="Refusal"/> still carries the
    /// immediate 429 a direct start answers with; a queued start is requeued instead of refused.
    /// </summary>
    public RunRateDecision? LockBusy { get; private init; }

    public static RunStartOutcome Refused(IActionResult refusal) => new() { Refusal = refusal };

    public static RunStartOutcome Contended(IActionResult refusal, RunRateDecision decision) => new() { Refusal = refusal, LockBusy = decision };

    public static RunStartOutcome Full(RunRateDecision decision) => new() { RateFull = decision };
}

/// <summary>
/// The public API's start sequence, in <c>WorkflowsController.Execute</c>'s gate order: authority, stale profile,
/// trial grant, choices, brief, then <see cref="RunStartGate.RefuseAsync"/> (plan, estimate gate, 402, the key's
/// caps), then the hourly run limit (source <c>api</c>) around the queueing call. One method used by the
/// synchronous start AND by <see cref="ApiRunQueueDispatcher"/>, so the two cannot drift. Nothing is written
/// before every gate has passed; a refusal at any gate leaves no execution row, no run-rate row and no hold.
/// </summary>
public sealed class PublicRunStartService(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IProjectAccessService projectAccess,
    IWorkflowExecutionService executionService,
    IAssistantExecutionGuard guard,
    IPlanGateService? planGate = null,
    IWorkflowCostEstimator? costEstimator = null,
    IDesignBuildEstimateGate? designBuildGate = null,
    WorkflowLintGate? lintGate = null,
    VideoSourceGate? sourceGate = null,
    ICreditAccountProvisioner? trialCredits = null,
    IRunStyleResolver? runStyleResolver = null,
    IBriefFileResolver? briefFiles = null,
    Microsoft.Extensions.Options.IOptions<Services.ApiKeys.PublicApiOptions>? publicApiOptions = null,
    TimeProvider? timeProvider = null,
    AppInputValueValidator? appInputs = null)
{
    /// <summary>The refusal of <c>inputs.allowMediaEgress = true</c> from a key without the <c>media.egress</c> scope.</summary>
    public const string MediaEgressScopeMessage =
        "This key may not allow sending media. Add the media.egress permission to the key in the app.";

    /// <summary>What the run just started was allowed to send (null when it was not asked); read by the route for its answer.</summary>
    public ReelBolt.Shared.Workflows.Apps.MediaEgressSummary? MediaEgressSent { get; private set; }

    /// <param name="onStarted">
    /// Runs inside the run-limit lock, right after the execution is queued, with its id: the queue dispatcher
    /// records <c>started</c> there so the claim and the start are one unit as far as a crash can tell.
    /// </param>
    public async Task<RunStartOutcome> StartAsync(
        Guid projectId, Guid workflowId, PublicStartRunRequest? request, Guid apiKeyId, string? correlationId,
        Func<Guid, CancellationToken, Task>? onStarted, CancellationToken ct)
    {
        RunStartGate gate = new(db, currentUser, planGate, costEstimator,
            designBuildGate ?? new DesignBuildEstimateGate(db, costEstimator, null), lintGate: lintGate, sourceGate: sourceGate);

        RunStartAuthorization authority = await gate.AuthorizeAsync(projectAccess, projectId, ct);
        if (authority.Failure is { } denied) return RunStartOutcome.Refused(denied);
        Project project = authority.Project!;

        // S-M1b: a start is identified by its correlation id. When one already exists in this organization, this is a
        // retry of a start that committed (a lost response, a crash after the save): answer with that run, never a
        // second one. The unique partial index on apirun:% is the final backstop for the not-yet-committed race; the
        // unique violation below re-reads it.
        if (correlationId is not null && await FindStartedAsync(project.OrganizationId, projectId, workflowId, correlationId, ct) is { } existing)
            return RunStartOutcome.Run(WorkflowsController.MapExecutionResponse(existing));

        // A hidden workflow kind (a revision, a render, a snapshot) is never started through the public API.
        bool standard = await db.WorkflowDefinitions.AsNoTracking()
            .AnyAsync(w => w.Id == workflowId && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);
        if (!standard) return RunStartOutcome.Refused(new NotFoundResult());

        if (await gate.RefuseStaleProfileAsync(projectId, workflowId, ct) is { } outdated) return RunStartOutcome.Refused(outdated);

        if (trialCredits is not null) await trialCredits.EnsureTrialGrantAsync(currentUser.UserId, currentUser.OrganizationId, ct);

        // A key may allow sending media for the run it starts only with the media.egress scope (docs/public-api.md). The
        // scopes are read from the key's own row on this start (the queue dispatcher has no request principal), and a key
        // that is not found holds none (fail closed). Same 403 shape as every other scope refusal.
        string? mediaEgressBy = null;
        if (ReelBolt.Shared.Workflows.Apps.MediaEgressPermission.IsRequested(request?.Brief?.Inputs))
        {
            string[]? scopes = await db.ApiKeys.AsNoTracking().Where(k => k.Id == apiKeyId).Select(k => k.Scopes).FirstOrDefaultAsync(ct);
            if (scopes?.Contains(ApiScopes.MediaEgress, StringComparer.Ordinal) != true)
            {
                return RunStartOutcome.Refused(PublicApiEnvelope.Result(
                    StatusCodes.Status403Forbidden, McpScopeGuard.InsufficientScopeCode, MediaEgressScopeMessage,
                    new { requiredScopes = new[] { ApiScopes.MediaEgress } }));
            }

            mediaEgressBy = ReelBolt.Shared.Workflows.Apps.MediaEgressPermission.ByApiKey;
        }

        RunBriefResolution choices = await RunBriefSplitter.ResolveAsync(
            runStyleResolver ?? new RunStyleResolver(db), db, currentUser.UserId, projectId,
            new LegacyRunInputs(request?.StyleId, request?.StylePresetKey, request?.Style, request?.OutputFormat, request?.OutputResolution, request?.UserRequest),
            request?.Brief, ct, new AppInputRunStart(appInputs ?? new AppInputValueValidator(db), currentUser.OrganizationId, workflowId, mediaEgressBy));
        MediaEgressSent = choices.AppInputs?.MediaEgress;
        if (choices.Error is not null)
            return RunStartOutcome.Refused(new BadRequestObjectResult(choices.ErrorBody()));

        BriefResolution brief = await BriefResolutionExtensions.ResolveAsync(briefFiles, projectId, request?.BriefFileId, choices.UserRequest, ct);
        if (brief.Error is not null)
            return RunStartOutcome.Refused(new BadRequestObjectResult(new { message = brief.Error }));

        // The plan gates, the estimate gate, the 402 and the key's caps (apiKeyId), in the dashboard's position. An app with
        // typed inputs is priced, linted and gated on its bound steps, on the branch its inputs choose (app-inputs 8.5), and the
        // inputs are validated by the same splitter and validator as the dashboard's start; they never turn on a consent.
        if (await gate.RefuseAsync(project, workflowId, choices.Choices?.OutputResolution, choices.PricingOr(RunStartPricing.SavedWorkflow),
                request?.AcceptEstimateCredits, ct, brief: request?.Brief, apiKeyId: apiKeyId) is { } refused)
            return RunStartOutcome.Refused(refused);

        WorkflowExecutionResponse? started = null;
        PlanGateViolation? planRefusal = null;
        IActionResult? reuseRefusal = null;
        RunRateDecision decision = await guard.TryAcquireAsync(currentUser.UserId, projectId,
            () => ClassifyAsync(workflowId, ct), RunRateEvent.ApiSource, async () =>
            {
                try
                {
                    // Once every gate has passed the write must complete even if the client disconnects: a
                    // half-start (a run row, no run-rate event, no queue hand-off) is worse than an uncancellable save.
                    started = await executionService.ExecuteAsync(
                        projectId, workflowId, currentUser.UserId, brief.Text, ExecutionInitiator.Api, CancellationToken.None,
                        regenerateVideoClips: false, choices.Choices, new ExecutionOrigin(apiKeyId, correlationId));
                    if (onStarted is not null && started is not null) await onStarted(started.Id, CancellationToken.None);
                    return started?.Id;
                }
                catch (PlanLimitException limit)
                {
                    planRefusal = limit.Violation;
                    return null;
                }
                catch (WorkflowNotFoundException)
                {
                    return null;
                }
                catch (DbUpdateException) when (correlationId is not null)
                {
                    // The unique partial index (workflow_executions.correlation_id WHERE apirun:%) caught a parallel
                    // start of the same key. Detach the loser and answer with the winner; if there is none, it was a
                    // different constraint and must surface.
                    db.ChangeTracker.Clear();
                    WorkflowExecution? winner = await FindStartedAsync(project.OrganizationId, projectId, workflowId, correlationId, CancellationToken.None);
                    if (winner is null)
                    {
                        // The same key used again after its window: the id is globally unique, so the old run still holds
                        // it. Re-read without the window bound. The same project and workflow is a replay; anything else
                        // is a key reused for a different start.
                        WorkflowExecution? existingRun = await db.WorkflowExecutions.AsNoTracking()
                            .FirstOrDefaultAsync(e => e.CorrelationId == correlationId, CancellationToken.None);
                        if (existingRun is null) throw;
                        if (existingRun.OrganizationId != project.OrganizationId || existingRun.ProjectId != projectId
                            || existingRun.WorkflowDefinitionId != workflowId)
                        {
                            reuseRefusal = PublicApiEnvelope.Result(StatusCodes.Status422UnprocessableEntity,
                                PublicApiErrorCodes.IdempotencyKeyReused,
                                "This Idempotency-Key was already used for a different request.");
                            return null;
                        }

                        winner = existingRun;
                    }

                    started = WorkflowsController.MapExecutionResponse(winner);
                    if (onStarted is not null) await onStarted(winner.Id, CancellationToken.None);
                    return winner.Id;
                }
            }, ct);

        if (planRefusal is not null) return RunStartOutcome.Refused(planRefusal.ToResult());
        if (reuseRefusal is not null) return RunStartOutcome.Refused(reuseRefusal);
        if (decision.Allowed && started is not null) return RunStartOutcome.Run(started);
        if (!decision.Allowed) return decision.Busy ? RunStartOutcome.Contended(Busy(decision, DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, decision.RetryAfterSeconds))), decision) : RunStartOutcome.Full(decision);
        return RunStartOutcome.Refused(new NotFoundResult());
    }

    /// <summary>
    /// L20: the run this correlation id already names in the organization FOR THIS project and workflow, and only
    /// while it is inside the idempotency window (<c>PublicApi:IdempotencyHours</c>). Past the window, or on another
    /// workflow, the same id starts a fresh run, as an Idempotency-Key does.
    /// </summary>
    private Task<WorkflowExecution?> FindStartedAsync(Guid organizationId, Guid projectId, Guid workflowId, string correlationId, CancellationToken ct)
    {
        int hours = Math.Max(1, publicApiOptions?.Value.IdempotencyHours ?? new Services.ApiKeys.PublicApiOptions().IdempotencyHours);
        DateTime cutoff = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime.AddHours(-hours);
        return db.WorkflowExecutions.AsNoTracking()
            .FirstOrDefaultAsync(e => e.CorrelationId == correlationId && e.OrganizationId == organizationId
                && e.ProjectId == projectId && e.WorkflowDefinitionId == workflowId
                && (e.QueuedAt == null || e.QueuedAt >= cutoff), ct);
    }

    /// <summary>The run-rate bucket of the workflow's steps as they are now; called under the guard's lock.</summary>
    private async Task<RunRateBucket> ClassifyAsync(Guid workflowId, CancellationToken ct) =>
        RunRateClassifier.Classify(
            await db.WorkflowSteps.AsNoTracking().Where(s => s.WorkflowDefinitionId == workflowId).ToListAsync(ct));

    /// <summary>
    /// The lock could not be taken in time: transient, answered as the dashboard answers it (429), never queued.
    /// Every 429 carries <c>Retry-After</c> and <c>error.resetsAt</c> (Q-4); <paramref name="resetsAt"/> is the instant
    /// the caller may retry, derived from <see cref="RunRateDecision.RetryAfterSeconds"/>.
    /// </summary>
    internal static ObjectResult Busy(RunRateDecision decision, DateTimeOffset resetsAt) =>
        PublicApiEnvelope.RateLimitedResult(resetsAt, RunRateRefusal.Code, RunRateRefusal.Sentence(decision),
            new Dictionary<string, object?>
            {
                ["bucket"] = RunRateRefusal.BucketName(decision.Bucket),
                ["used"] = decision.Used,
                ["limit"] = decision.Limit,
                ["windowMinutes"] = decision.WindowSeconds / 60,
                ["retryAfterSeconds"] = decision.RetryAfterSeconds,
                ["busy"] = decision.Busy,
            });
}
