using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.ApiKeys;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.PublicApi;
using ReelBolt.Inference.Api.Services.PublicApi.Runs;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Controllers.PublicApi;

public sealed record PublicRunCreditsView(double Charged, double? Held);

public sealed record PublicRunFailureView(string Code, string Message);

public sealed record PublicRunStepView(
    int? Order, string? Type, string? Name, string Status, DateTime? StartedAt, DateTime? FinishedAt, string? FailureCode);

/// <summary>A run in a list: the run without its steps.</summary>
public sealed record PublicRunSummaryView(
    Guid Id, Guid WorkflowId, string Status, string Initiator, DateTime? StartedAt, DateTime? FinishedAt,
    PublicRunCreditsView? Credits, PublicRunFailureView? Failure);

public sealed record PublicRunView(
    Guid Id, Guid WorkflowId, string Status, string Initiator, DateTime? StartedAt, DateTime? FinishedAt,
    PublicRunCreditsView? Credits, PublicRunFailureView? Failure, IReadOnlyList<PublicRunStepView> Steps)
{
    /// <summary>
    /// Present only on the answer of a start that set <c>inputs.allowMediaEgress = true</c>: which steps may send the
    /// customer's media to an AI service, and what each sends, so the caller's own log shows what left.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public PublicMediaEgressView? MediaEgress { get; init; }
}

/// <summary>What a run was allowed to send: <c>{ allowed: true, steps: [{ stepName, sends }] }</c>.</summary>
public sealed record PublicMediaEgressView(bool Allowed, IReadOnlyList<PublicMediaEgressStepView> Steps);

public sealed record PublicMediaEgressStepView(string StepName, string Sends);

public sealed record PublicEstimateRequest(string? OutputResolution = null, Guid[]? VideoFileIds = null);

public sealed record PublicKeyBudgetView(string Window, double Remaining);

public sealed record PublicEstimateView(
    double Low, double High, double Typical, double Required, double? Balance, bool Affordable, PublicKeyBudgetView? KeyBudget);

/// <summary>Optional body of retry and resume: the figure the caller accepts for a profile-build estimate.</summary>
public sealed record PublicRunControlRequest(double? AcceptEstimateCredits = null);

/// <summary>
/// The run lifecycle over the public API: estimate, start, list, status, stop, resume, retry.
/// Start goes through <see cref="PublicRunStartService"/> (the dashboard's gate order plus the key's caps and the
/// hourly run limit); stop, resume and retry run the dashboard's own <see cref="WorkflowsController"/> actions, so the
/// rules and error codes are the dashboard's by construction.
/// </summary>
public sealed class RunsController(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IProjectAccessService projectAccess,
    IApiKeyContext keyContext,
    PublicRunStartService startService,
    IAssistantExecutionGuard guard,
    IOptions<PublicApiOptions> options,
    TimeProvider? time = null) : PublicApiControllerBase
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    [HttpPost("projects/{projectId:guid}/workflows/{workflowId:guid}/estimate")]
    // Accepted by Security (L16): a POST that changes no state, so runs.read is the right scope.
    [RequireScope(ApiScopes.RunsRead)]
    [ProducesResponseType(typeof(PublicEstimateView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult> Estimate(
        Guid projectId, Guid workflowId, [FromBody] PublicEstimateRequest? body, CancellationToken ct)
    {
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        bool standard = await db.WorkflowDefinitions.AsNoTracking()
            .AnyAsync(w => w.Id == workflowId && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);
        if (!standard) return PublicError(StatusCodes.Status404NotFound, PublicApiErrorCodes.NotFound, "Workflow not found.");

        ActionResult<WorkflowCostEstimateResponse> estimate = await Dashboard().Estimate(
            projectId, workflowId, ct, body?.OutputResolution, body?.VideoFileIds);
        if (estimate.Result is not null && estimate.Result is not OkObjectResult)
            return PublicRefusal(estimate.Result);
        WorkflowCostEstimateResponse? value = estimate.Value ?? (estimate.Result as OkObjectResult)?.Value as WorkflowCostEstimateResponse;
        if (value is null) return PublicError(StatusCodes.Status500InternalServerError, PublicApiErrorCodes.Internal, "The estimate could not be computed.");

        return Ok(new PublicEstimateView(
            value.LowCredits, value.HighCredits, value.TypicalCredits, value.RequiredCredits, value.BalanceCredits,
            value.Affordable, await KeyBudgetAsync(ct)));
    }

    [HttpPost("projects/{projectId:guid}/workflows/{workflowId:guid}/runs")]
    [Idempotent]
    [RequireScope(ApiScopes.RunsExecute)]
    [ProducesResponseType(typeof(PublicRunView), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(PublicQueuedRunView), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> Start(
        Guid projectId, Guid workflowId, [FromBody] PublicStartRunRequest? body, CancellationToken ct)
    {
        if (keyContext.KeyId is not Guid keyId)
            return PublicError(StatusCodes.Status401Unauthorized, PublicApiErrorCodes.ApiKeyRequired, PublicApiErrorCodes.ApiKeyRequiredMessage);

        // S-M1b: with an Idempotency-Key every start of this key+key pair names one deterministic run, so a retry
        // after a lost response finds it instead of starting a second.
        string? correlationId = PublicIdempotency.KeyOf(HttpContext) is { } idempotencyKey
            ? PublicIdempotency.CorrelationId(keyId, idempotencyKey)
            : null;
        RunStartOutcome outcome = await startService.StartAsync(projectId, workflowId, body, keyId, correlationId,
            onStarted: (_, _) =>
            {
                // The run is committed: a later exception must not abandon the key and let a replay start another.
                PublicIdempotency.MarkCommitted(HttpContext);
                return Task.CompletedTask;
            }, ct);
        if (outcome.Refusal is { } refusal) return PublicRefusal(refusal);
        if (outcome.Started is { } started)
        {
            PublicRunView? view = await ReadRunAsync(projectId, started.Id, CancellationToken.None);
            if (view is not null && startService.MediaEgressSent is { Present: true } sent)
            {
                view = view with
                {
                    MediaEgress = new PublicMediaEgressView(true,
                        [.. ReelBolt.Shared.Workflows.Apps.MediaEgressPermission.Sent(sent).Select(s => new PublicMediaEgressStepView(s.StepName, s.Sends))])
                };
            }

            return StatusCode(StatusCodes.Status201Created, view);
        }

        // The hourly run limit is full. Every other gate has passed (the start service runs them first), so the
        // start is held for the dispatcher instead of refused.
        RunRateDecision full = outcome.RateFull!;
        DateTime now = _time.GetUtcNow().UtcDateTime;
        DateTime estimatedStartAt = now.AddSeconds(Math.Max(1, full.RetryAfterSeconds));
        int maxQueued = Math.Max(1, options.Value.RunQueue.MaxQueuedPerKey);

        ApiRunRequest row = new()
        {
            Id = Guid.NewGuid(),
            ApiKeyId = keyId,
            OrganizationId = currentUser.OrganizationId ?? Guid.Empty,
            ProjectId = projectId,
            WorkflowId = workflowId,
            RequestJson = System.Text.Json.JsonSerializer.Serialize(body ?? new PublicStartRunRequest(), PublicApiEnvelope.Json),
            Status = ApiRunRequest.Queued,
            NotBefore = estimatedStartAt,
            EstimatedStartAt = estimatedStartAt,
            CreatedAt = now,
            UpdatedAt = now
        };

        // S-L1/Q-6: the count and the insert are one critical section per key, so two replicas cannot both see room
        // and both insert past the bound. Postgres serialises with a transaction-scoped advisory lock; providers
        // without it (tests) have one writer. The same shape as CreditLedger.RunAsync.
        int queued;
        if (db.Database.IsNpgsql())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            string lockKey = "apiqueue:" + keyId.ToString("D");
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtext({lockKey}))", ct);
            queued = await CountQueuedAsync(keyId, ct);
            if (queued < maxQueued)
            {
                db.ApiRunRequests.Add(row);
                // Past every gate: the queue write must survive a client disconnect.
                await db.SaveChangesAsync(CancellationToken.None);
            }

            await transaction.CommitAsync(CancellationToken.None);
        }
        else
        {
            queued = await CountQueuedAsync(keyId, ct);
            if (queued < maxQueued)
            {
                db.ApiRunRequests.Add(row);
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        if (queued >= maxQueued)
        {
            return PublicError(StatusCodes.Status429TooManyRequests, "run_queue_full",
                "This key already has the most queued run requests it may hold. Wait for some to start, or cancel one.",
                new Dictionary<string, object?>
                {
                    ["limit"] = options.Value.RunQueue.MaxQueuedPerKey,
                    ["resetsAt"] = DateTime.SpecifyKind(estimatedStartAt, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)
                });
        }

        return StatusCode(StatusCodes.Status202Accepted, new PublicQueuedRunView(row.Id, ApiRunRequest.Queued, estimatedStartAt, projectId, workflowId));
    }

    private Task<int> CountQueuedAsync(Guid keyId, CancellationToken ct) =>
        db.ApiRunRequests.CountAsync(
            r => r.ApiKeyId == keyId && (r.Status == ApiRunRequest.Queued || r.Status == ApiRunRequest.Starting), ct);

    [HttpGet("projects/{projectId:guid}/runs")]
    [RequireScope(ApiScopes.RunsRead)]
    [ProducesResponseType(typeof(PublicApiPage<PublicRunSummaryView>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> List(
        Guid projectId, [FromQuery] Guid? workflowId, [FromQuery] string? status, [FromQuery] int? limit,
        [FromQuery] string? cursor, CancellationToken ct)
    {
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        ExecutionStatus? statusFilter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            statusFilter = ParseStatus(status);
            if (statusFilter is null)
                return PublicError(StatusCodes.Status400BadRequest, PublicApiErrorCodes.InvalidRequest,
                    "status must be one of queued, running, succeeded, failed, cancelled.");
        }

        int take = PublicApiPagination.NormalizeLimit(limit);
        IQueryable<WorkflowExecution> query = db.WorkflowExecutions.AsNoTracking().Where(e => e.ProjectId == projectId);
        if (workflowId is Guid wf) query = query.Where(e => e.WorkflowDefinitionId == wf);
        if (statusFilter is { } st) query = query.Where(e => e.Status == st);
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            if (!PublicApiPagination.TryDecodeCursor(cursor, out DateTime cursorAt, out Guid cursorId))
                return PublicError(StatusCodes.Status400BadRequest, PublicApiErrorCodes.InvalidRequest, "The cursor is not valid.");
            query = query.Where(e => e.QueuedAt < cursorAt || (e.QueuedAt == cursorAt && e.Id.CompareTo(cursorId) < 0));
        }

        List<WorkflowExecution> rows = await query
            .OrderByDescending(e => e.QueuedAt).ThenByDescending(e => e.Id)
            .Take(take + 1).ToListAsync(ct);
        bool more = rows.Count > take;
        if (more) rows = rows.Take(take).ToList();

        List<Guid> ids = rows.Select(r => r.Id).ToList();
        Dictionary<Guid, List<WorkflowStepResult>> results = (await db.WorkflowStepResults.AsNoTracking()
                .Where(r => ids.Contains(r.WorkflowExecutionId) && r.SupersededAt == null)
                .Select(r => new WorkflowStepResult
                {
                    Id = r.Id,
                    WorkflowExecutionId = r.WorkflowExecutionId,
                    CreditsChargedMillis = r.CreditsChargedMillis,
                    CreditsUnsettledMillis = r.CreditsUnsettledMillis,
                    FailureCode = r.FailureCode,
                    Status = r.Status
                })
                .ToListAsync(ct))
            .GroupBy(r => r.WorkflowExecutionId).ToDictionary(g => g.Key, g => g.ToList());

        List<PublicRunSummaryView> data = rows.Select(e =>
        {
            List<WorkflowStepResult> mine = results.GetValueOrDefault(e.Id) ?? [];
            return new PublicRunSummaryView(e.Id, e.WorkflowDefinitionId, StatusName(e.Status), InitiatorName(e.Initiator),
                e.StartedAt ?? e.FirstStartedAt, e.CompletedAt, CreditsOf(mine), FailureOf(e, mine));
        }).ToList();

        string? next = more ? PublicApiPagination.EncodeCursor(rows[^1].QueuedAt ?? DateTime.MinValue, rows[^1].Id) : null;
        return Ok(new PublicApiPage<PublicRunSummaryView>(data, next));
    }

    [HttpGet("projects/{projectId:guid}/runs/{runId:guid}")]
    [RequireScope(ApiScopes.RunsRead)]
    [ProducesResponseType(typeof(PublicRunView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Get(Guid projectId, Guid runId, CancellationToken ct)
    {
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return PublicRefusal(denied);

        PublicRunView? view = await ReadRunAsync(projectId, runId, ct);
        return view is null ? PublicError(StatusCodes.Status404NotFound, PublicApiErrorCodes.NotFound, "Run not found.") : Ok(view);
    }

    [HttpPost("projects/{projectId:guid}/runs/{runId:guid}/stop")]
    [Idempotent]
    [RequireScope(ApiScopes.RunsExecute)]
    [ProducesResponseType(typeof(PublicRunView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> Stop(Guid projectId, Guid runId, CancellationToken ct)
    {
        // S-L3: stop needs no ownership check. Stopping does not elevate: it ends a run the caller can already see
        // and edit, and the engine acts on the row's own initiator, not on the key. Retry and resume DO elevate (they
        // make the engine run again as the run's initiator), so they are restricted below.
        (Guid? workflowId, ActionResult? failure) = await ResolveRunAsync(projectId, runId, requireApiKeyOwnership: false, ct);
        if (failure is not null) return PublicRefusal(failure);

        IActionResult result = await Dashboard().StopExecution(projectId, workflowId!.Value, runId, ct);
        return await ControlResultAsync(result, projectId, runId, ct);
    }

    [HttpPost("projects/{projectId:guid}/runs/{runId:guid}/resume")]
    [Idempotent]
    [RequireScope(ApiScopes.RunsExecute)]
    [ProducesResponseType(typeof(PublicRunView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> Resume(Guid projectId, Guid runId, [FromBody] PublicRunControlRequest? body, CancellationToken ct)
    {
        (Guid? workflowId, ActionResult? failure) = await ResolveRunAsync(projectId, runId, requireApiKeyOwnership: true, ct);
        if (failure is not null) return PublicRefusal(failure);

        ResumeExecutionRequest request = new(body?.AcceptEstimateCredits);
        return await GuardedRestartAsync(projectId, workflowId!.Value, runId, async () =>
            (await Dashboard(callerPays: true).ResumeExecution(projectId, workflowId.Value, runId, request, ct)).Result
                ?? new OkResult(), ct);
    }

    [HttpPost("projects/{projectId:guid}/runs/{runId:guid}/retry")]
    [Idempotent]
    [RequireScope(ApiScopes.RunsExecute)]
    [ProducesResponseType(typeof(PublicRunView), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> Retry(Guid projectId, Guid runId, [FromBody] PublicRunControlRequest? body, CancellationToken ct)
    {
        (Guid? workflowId, ActionResult? failure) = await ResolveRunAsync(projectId, runId, requireApiKeyOwnership: true, ct);
        if (failure is not null) return PublicRefusal(failure);

        ExecuteWorkflowRequest request = new(AcceptEstimateCredits: body?.AcceptEstimateCredits);
        return await GuardedRestartAsync(projectId, workflowId!.Value, runId, async () =>
            (await Dashboard(callerPays: true).RetryExecution(projectId, workflowId.Value, runId, request, ct)).Result
                ?? new OkResult(), ct);
    }

    /// <summary>
    /// Retry and resume start paid work again, so through the API they take the hourly run-limit slot like a start
    /// does (the dashboard takes none; the assistant takes it only for execute). The limit counts runs a key has
    /// had started in the hour, and a restart is one more. The dashboard action runs INSIDE the slot and a result
    /// that is not a success returns null, which releases it, so a refusal (409, 402 ...) consumes nothing. Unlike
    /// a start, a full limit is a plain 429 here (a restart is an answer to a run the caller is watching, not a
    /// fire-and-forget request, so it is not queued). The CALLING key pays: it is passed to the dashboard action as
    /// <c>ApiKeyOverride</c>, so its caps are checked and the engine's per-step reserves charge it, even when the run
    /// was started by another key or by a person.
    /// </summary>
    private async Task<ActionResult> GuardedRestartAsync(
        Guid projectId, Guid workflowId, Guid runId, Func<Task<IActionResult>> action, CancellationToken ct)
    {
        IActionResult? actionResult = null;
        RunRateDecision decision = await guard.TryAcquireAsync(currentUser.UserId, projectId,
            () => ClassifyRestartAsync(runId, workflowId, ct),
            RunRateEvent.ApiSource, async () =>
            {
                actionResult = await action();
                return IsSuccess(actionResult) ? runId : null;
            }, ct);

        if (!decision.Allowed)
        {
            if (decision.Busy)
                return PublicRefusal(PublicRunStartService.Busy(decision,
                    _time.GetUtcNow().AddSeconds(Math.Max(1, decision.RetryAfterSeconds))));
            return PublicRefusal(new ObjectResult(new
            {
                message = RunRateRefusal.Sentence(decision),
                code = RunRateRefusal.Code,
                bucket = RunRateRefusal.BucketName(decision.Bucket),
                used = decision.Used,
                limit = decision.Limit,
                retryAfterSeconds = decision.RetryAfterSeconds,
                resetsAt = _time.GetUtcNow().UtcDateTime.AddSeconds(Math.Max(1, decision.RetryAfterSeconds)).ToString("O", CultureInfo.InvariantCulture)
            })
            { StatusCode = StatusCodes.Status429TooManyRequests });
        }

        return await ControlResultAsync(actionResult!, projectId, runId, ct);
    }

    /// <summary>
    /// The bucket of the step set a retry or resume will actually run: the run's own engine snapshot when it has one,
    /// else the workflow (the same choice as <c>pricedDefinitionId</c> in the dashboard's retry and resume), never the
    /// live workflow of a snapshotted run, which may have been edited to look free. One helper for both actions.
    /// </summary>
    private Task<RunRateBucket> ClassifyRestartAsync(Guid runId, Guid workflowId, CancellationToken ct) =>
        ClassifyRestartAsync(db, runId, workflowId, null, ct);

    /// <param name="engineTaken">Test seam for the ownership check, which a non-relational provider cannot answer.</param>
    internal static async Task<RunRateBucket> ClassifyRestartAsync(
        InferenceApiDbContext db, Guid runId, Guid workflowId, Func<WorkflowExecution, Task<bool>>? engineTaken, CancellationToken ct)
    {
        WorkflowExecution? execution = await db.WorkflowExecutions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == runId, ct);
        Guid definitionId = execution is not null && execution.SnapshotDefinitionId is { } snapshotId
            && await (engineTaken?.Invoke(execution) ?? ExecutionResumability.HasEngineTakenSnapshotAsync(db, execution, ct))
            ? snapshotId
            : workflowId;
        return RunRateClassifier.Classify(await db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == definitionId).ToListAsync(ct));
    }

    private static bool IsSuccess(IActionResult result) => result switch
    {
        ObjectResult o => (o.StatusCode ?? 200) is >= 200 and < 300,
        StatusCodeResult s => s.StatusCode is >= 200 and < 300,
        EmptyResult => true,
        ForbidResult or NotFoundResult or BadRequestResult or ConflictResult => false,
        _ => false
    };

    /// <summary>A success answers with the run as it is now; a refusal is re-shaped by the envelope adapter.</summary>
    private async Task<ActionResult> ControlResultAsync(IActionResult result, Guid projectId, Guid runId, CancellationToken ct)
    {
        if (!IsSuccess(result)) return PublicRefusal(result);
        PublicRunView? view = await ReadRunAsync(projectId, runId, ct);
        int status = result is ObjectResult o ? o.StatusCode ?? 200 : result is StatusCodeResult s ? s.StatusCode : 200;
        return StatusCode(status, view);
    }

    /// <summary>
    /// Project authority (Edit) and the run's workflow id; the dashboard action re-checks both.
    /// <paramref name="requireApiKeyOwnership"/> is true for retry and resume (S-L3): those restart the run as its
    /// initiator, so the engine would finish it with that user's role. Only the API key that started a run may retry
    /// or resume it: they are allowed only when the run's <c>InitiatedByUserId</c> is the calling key's own service
    /// user; a run a person started, or another key's run, is refused <c>403 run_not_owned_by_api_key</c>. Stop passes false (it does not elevate), and
    /// reads never come through here.
    /// </summary>
    private async Task<(Guid? WorkflowId, ActionResult? Failure)> ResolveRunAsync(
        Guid projectId, Guid runId, bool requireApiKeyOwnership, CancellationToken ct)
    {
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return (null, denied);

        var run = await db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.Id == runId && e.ProjectId == projectId)
            .Select(e => new { e.WorkflowDefinitionId, e.InitiatedByUserId })
            .FirstOrDefaultAsync(ct);
        if (run is null) return (null, new NotFoundResult());

        if (requireApiKeyOwnership && !await IsOwnedByThisOrganizationsServiceAccountAsync(run.InitiatedByUserId, ct))
        {
            return (null, PublicError(StatusCodes.Status403Forbidden, "run_not_owned_by_api_key",
                "Only the API key that started a run may retry or resume it."));
        }

        return (run.WorkflowDefinitionId, null);
    }

    /// <summary>
    /// L19a: the run's initiator is the calling key's OWN service user. Retry and resume are restricted to the
    /// key that started the run (another key of the same org gets 403); stop stays org-wide, see
    /// <c>requireApiKeyOwnership: false</c>. A human, or another service account, does not qualify.
    /// </summary>
    private async Task<bool> IsOwnedByThisOrganizationsServiceAccountAsync(Guid? initiator, CancellationToken ct)
    {
        if (initiator is not Guid userId || keyContext.KeyId is not Guid keyId) return false;
        return await db.ApiKeys.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(k => k.Id == keyId && k.ServiceUserId == userId, ct);
    }

    /// <summary>The dashboard's own controller, in this request's context (same user, same project access).</summary>
    private WorkflowsController Dashboard(bool callerPays = false)
    {
        WorkflowsController controller = ActivatorUtilities.CreateInstance<WorkflowsController>(HttpContext.RequestServices);
        controller.ControllerContext = ControllerContext;
        // Retry/resume: the calling key is the one whose caps are checked and which is charged (see ApiKeyOverride).
        if (callerPays) controller.ApiKeyOverride = keyContext.KeyId;
        return controller;
    }

    private async Task<PublicKeyBudgetView?> KeyBudgetAsync(CancellationToken ct)
    {
        if (keyContext.KeyId is not Guid keyId) return null;
        ApiKey? key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(k => k.Id == keyId, ct);
        if (key is null) return null;

        DateTime now = _time.GetUtcNow().UtcDateTime;
        long[] used = (await ApiKeyBudgetWindows.UsedAsync(db, [keyId], now, ct))[keyId];
        PublicKeyBudgetView? tightest = null;
        long tightestRemaining = long.MaxValue;
        for (int i = 0; i < ApiKeyBudgetWindows.All.Length; i++)
        {
            if (ApiKeyBudgetWindows.CapOf(key, ApiKeyBudgetWindows.All[i]) is not { } cap) continue;
            long remaining = Math.Max(0, cap - used[i]);
            if (remaining < tightestRemaining)
            {
                tightestRemaining = remaining;
                tightest = new PublicKeyBudgetView(ApiKeyBudgetWindows.Name(ApiKeyBudgetWindows.All[i]), remaining / 1000.0);
            }
        }

        return tightest;
    }

    /// <summary>One run with its steps, or null when it is not a run of the project.</summary>
    private async Task<PublicRunView?> ReadRunAsync(Guid projectId, Guid runId, CancellationToken ct)
    {
        WorkflowExecution? e = await db.WorkflowExecutions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == runId && x.ProjectId == projectId, ct);
        if (e is null) return null;

        List<WorkflowStepResult> results = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == runId && r.SupersededAt == null)
            .Select(r => new WorkflowStepResult
            {
                Id = r.Id,
                WorkflowExecutionId = r.WorkflowExecutionId,
                WorkflowStepId = r.WorkflowStepId,
                Status = r.Status,
                ExecutedAt = r.ExecutedAt,
                CompletedAt = r.CompletedAt,
                FailureCode = r.FailureCode,
                CreditsChargedMillis = r.CreditsChargedMillis,
                CreditsUnsettledMillis = r.CreditsUnsettledMillis
            })
            .ToListAsync(ct);
        List<Guid> stepIds = results.Select(r => r.WorkflowStepId).Distinct().ToList();
        Dictionary<Guid, (int Order, StepType Type, string? Label)> steps = (await db.WorkflowSteps.AsNoTracking()
                .Where(s => stepIds.Contains(s.Id))
                .Select(s => new { s.Id, s.StepOrder, s.StepType, s.Label })
                .ToListAsync(ct))
            .ToDictionary(s => s.Id, s => (s.StepOrder, s.StepType, s.Label));

        List<PublicRunStepView> stepViews = results
            .Select(r =>
            {
                steps.TryGetValue(r.WorkflowStepId, out var step);
                bool known = steps.ContainsKey(r.WorkflowStepId);
                return new PublicRunStepView(
                    known ? step.Order : null, known ? step.Type.ToString() : null, known ? step.Label : null,
                    r.Status.ToString().ToLowerInvariant(),
                    r.Status == StepStatus.Pending ? null : r.ExecutedAt, r.CompletedAt, r.FailureCode);
            })
            .OrderBy(s => s.Order ?? int.MaxValue).ThenBy(s => s.StartedAt)
            .ToList();

        return new PublicRunView(e.Id, e.WorkflowDefinitionId, StatusName(e.Status), InitiatorName(e.Initiator),
            e.StartedAt ?? e.FirstStartedAt, e.CompletedAt, CreditsOf(results), FailureOf(e, results), stepViews);
    }

    private static PublicRunCreditsView? CreditsOf(IReadOnlyCollection<WorkflowStepResult> results)
    {
        if (results.Count == 0) return null;
        long charged = results.Sum(r => r.CreditsChargedMillis);
        long held = results.Sum(r => r.CreditsUnsettledMillis ?? 0);
        return new PublicRunCreditsView(charged / 1000.0, held > 0 ? held / 1000.0 : null);
    }

    private static PublicRunFailureView? FailureOf(WorkflowExecution e, IReadOnlyCollection<WorkflowStepResult> results)
    {
        if (e.Status != ExecutionStatus.Failed) return null;
        string code = results.Select(r => r.FailureCode).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "RUN_FAILED";
        string message = e.ErrorMessage ?? "The run failed.";
        if (message.Length > 2000) message = message[..2000];
        return new PublicRunFailureView(code, message);
    }

    internal static string StatusName(ExecutionStatus status) => status switch
    {
        ExecutionStatus.Queued => "queued",
        ExecutionStatus.Running => "running",
        ExecutionStatus.Passed => "succeeded",
        ExecutionStatus.Failed => "failed",
        _ => "cancelled"
    };

    private static ExecutionStatus? ParseStatus(string value) => value.ToLowerInvariant() switch
    {
        "queued" => ExecutionStatus.Queued,
        "running" => ExecutionStatus.Running,
        "succeeded" => ExecutionStatus.Passed,
        "failed" => ExecutionStatus.Failed,
        "cancelled" => ExecutionStatus.Cancelled,
        _ => null
    };

    internal static string InitiatorName(ExecutionInitiator initiator) => initiator switch
    {
        ExecutionInitiator.Assistant => "assistant",
        ExecutionInitiator.Mcp => "mcp",
        ExecutionInitiator.Api => "api",
        _ => "dashboard"
    };
}

/// <summary>The 202 body of a start the run limit held back: poll <c>run-requests/{id}</c>.</summary>
public sealed record PublicQueuedRunView(Guid Id, string Status, DateTime EstimatedStartAt, Guid ProjectId, Guid WorkflowId);
