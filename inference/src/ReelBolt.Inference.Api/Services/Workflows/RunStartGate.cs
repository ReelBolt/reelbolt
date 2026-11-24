using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Observability;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.History;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>The caller's authority to start a run in a project: the project, or the refusal to answer.</summary>
public sealed record RunStartAuthorization(Project? Project, ActionResult? Failure);

/// <summary>
/// The existing run a Retry or Resume re-queues: the estimate gate then judges the run's OWN engine-taken snapshot (the steps that
/// will run), not the workflow as it is now. Null for Execute, which runs (and snapshots) the live workflow.
/// </summary>
public sealed record RunStartRun(Guid ExecutionId, Guid? SnapshotDefinitionId);

/// <summary>
/// What a run start is priced on: the workflow as saved (Execute: the B7 preflight loads it, and a workflow that
/// is not in the project is a 404), or an explicit step list (Retry: the run's own snapshot; Resume: the same with
/// only the steps from the frontier on charged).
/// </summary>
/// <param name="AgentModels">
/// The Chat models an app's <c>model</c> inputs chose for its agents (docs/design/app-inputs.md 8.4, 8.5): agent definition
/// id to capability id; their cold-start chat is priced at that model. Null (every run without such a choice) changes nothing.
/// </param>
public sealed record RunStartPricing(
    IReadOnlyList<WorkflowStep>? Steps,
    IReadOnlyCollection<int>? ChargedStepOrders,
    IReadOnlyDictionary<Guid, Guid>? AgentModels = null,
    IReadOnlySet<Guid>? StepModels = null)
{
    public static RunStartPricing SavedWorkflow { get; } = new(null, null);

    public static RunStartPricing AllOf(IReadOnlyList<WorkflowStep> steps) => new(steps, null);

    public static RunStartPricing Charged(IReadOnlyList<WorkflowStep> steps, IReadOnlyCollection<int> chargedStepOrders) =>
        new(steps, chargedStepOrders);
}

/// <summary>
/// The gate sequence every REST run start shares — Execute, Retry and Resume (T-004) — so the three cannot drift:
/// <list type="number">
/// <item><see cref="AuthorizeAsync"/>: C5's email verification (403 <c>EMAIL_NOT_VERIFIED</c>), then project
/// authority with Edit (foreign project 404, a role without Edit 403). The access service is passed by each
/// action, so every action visibly consults it (the tenant-boundary inventory counts one per action).</item>
/// <item><see cref="RefuseStaleProfileAsync"/>: QA T-102's 409 <c>PROFILE_WORKFLOW_OUTDATED</c> for a stale v1 copy
/// of the profile template, before anything is resolved, preflighted or held. Execute runs it right after the
/// authority; Retry and Resume right after loading the run (a foreign run is their 404 first).</item>
/// <item>(the route's own checks: the run, its status, its snapshot …)</item>
/// <item><see cref="RefuseAsync"/>: the plan's resolution cap (402 <c>PLAN_NOT_ENTITLED</c>), then (Execute only) its gated step types the run will certainly reach (the same 402), then its concurrent-run cap (429), then the estimate gate's
/// profile-build gate (409/503, #370), then the pre-run lint (409 <c>WORKFLOW_LINT_ERRORS</c> behind
/// <c>Workflows:Lint:Enforce</c>, otherwise warn-only telemetry; costs nothing and spends nothing), then B7's credit preflight (402 <c>INSUFFICIENT_CREDITS</c>), LAST because
/// it is the only one that reads the balance: every refusal that costs nothing wins over "top up".</item>
/// </list>
/// </summary>
public sealed class RunStartGate(
    InferenceApiDbContext db,
    ICurrentUser currentUser,
    IPlanGateService? planGate,
    IWorkflowCostEstimator? costEstimator,
    IDesignBuildEstimateGate designBuildGate,
    IProductHealthSignalDetector? signals = null,
    WorkflowLintGate? lintGate = null,
    VideoSourceGate? sourceGate = null,
    IProjectHistoryService? history = null)
{
    private readonly VideoSourceGate _sourceGate = sourceGate ?? new VideoSourceGate(db, VideoSourceLimits.Default);
    private readonly IProjectHistoryService? _history = history;

    // T-020: reporting only. The ambient detector observes a refusal and returns nothing; the refusal
    // itself is computed exactly as before and handed back untouched.
    private readonly IProductHealthSignalDetector _signals = signals ?? ProductHealthSignals.Ambient;

    /// <summary>Email verification, then Edit on the project.</summary>
    public async Task<RunStartAuthorization> AuthorizeAsync(IProjectAccessService projectAccess, Guid projectId, CancellationToken ct)
    {
        if (EmailVerificationGate.Check(currentUser) is { } unverified)
            return new RunStartAuthorization(null, unverified);

        // The route's project is verified here; the services behind it do not redo the check.
        ProjectAccessResult access = await projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        return access.Failure is { } denied
            ? new RunStartAuthorization(null, denied)
            : new RunStartAuthorization(access.Project!, null);
    }

    /// <summary>
    /// QA T-102: 409 <c>PROFILE_WORKFLOW_OUTDATED</c> when <paramref name="workflowId"/> is a stale v1 copy of the
    /// profile template (its product-only fallback also runs, and charges, the style extractor; the build route runs
    /// the current version), else null.
    /// </summary>
    public async Task<ActionResult?> RefuseStaleProfileAsync(Guid projectId, Guid workflowId, CancellationToken ct) =>
        await StaleProfileWorkflow.IsStaleAsync(db, projectId, workflowId, ct)
            ? new ConflictObjectResult(new { code = StaleProfileWorkflow.Code, message = StaleProfileWorkflow.Message })
            : null;

    /// <summary>
    /// The plan gate, the profile-build estimate gate and the credit preflight, in that order. Null when the run may be queued.
    /// </summary>
    /// <param name="acceptEstimateCredits">
    /// The figure the caller accepted for a profile-build estimate (whole credits; null = none). Ignored for
    /// any other workflow.
    /// </param>
    public async Task<ActionResult?> RefuseAsync(
        Project project,
        Guid workflowId,
        int? outputResolution,
        RunStartPricing pricing,
        double? acceptEstimateCredits,
        CancellationToken ct,
        RunStartRun? run = null,
        RunBrief? brief = null,
        Guid? apiKeyId = null,
        bool? runHasPace = null)
    {
        // A restore of this project is rewriting it: no run starts until it is done.
        if (await RestoreRunGate.IsHeldAsync(db, project.Id, ct))
            return Refused(RestoreRunGate.Refusal(), project, ProjectHistoryRestoreCodes.RestoreInProgress);
        // An app's chosen agent models price the run as it will run (docs/design/app-inputs.md 8.5); null = no scope change.
        using IDisposable chosenModels = AppAgentModelAmbient.Enter(pricing.AgentModels, pricing.StepModels);

        // The plan's resolution cap answers BEFORE the credit preflight: a resolution the plan will refuse is a
        // 402 PLAN_NOT_ENTITLED, never a "top up" for a render that could not run anyway. Auto (null) is no check
        // and no read.
        if (planGate is not null
            && await planGate.CheckResolutionAsync(project.OrganizationId, outputResolution, ct) is { } notEntitled)
            return Refused(notEntitled.ToResult(), project, notEntitled.Code);

        // The plan's gated step types (video generation, audio generation, rooms): a first run of a workflow that will certainly
        // reach one the plan lacks is refused here, at the Run button, with the same 402 PLAN_NOT_ENTITLED the engine would fail
        // that step with later (after the steps before it had run and been paid for). Execute only: a Retry or Resume may
        // reuse the earlier result of such a step. Never refuses what the engine would let through.
        if (planGate is not null && run is null
            && await planGate.CheckRunStepsAsync(project.OrganizationId, project.Id, workflowId, pricing.Steps, pricing.ChargedStepOrders, ct) is { } stepNotEntitled)
            return Refused(stepNotEntitled.ToResult(), project, stepNotEntitled.Code);

        // B8a / SQFU-S9: the plan's concurrent-run cap (429), for Retry and Resume as for Execute, and before the
        // 402: a run the plan will not start now must not be answered with "top up". Execute's queueing service
        // re-checks it (defence in depth, as for the resolution cap).
        if (planGate is not null
            && await planGate.CheckExecuteAsync(project.OrganizationId, ct) is { } atCapacity)
            return Refused(atCapacity.ToResult(), project, atCapacity.Code);

        // the product-profile-from-code template gets the cap and accept-the-estimate check of
        // POST /design-system/builds (409 ESTIMATE_OVER_CAP / ESTIMATE_NOT_ACCEPTED, 503 ESTIMATE_UNAVAILABLE), after
        // the plan's caps and before the 402: a refusal that costs nothing wins over "top up". The template is
        // recognised by its step shape. Execute judges the live workflow (the definition being snapshotted). A Retry
        // or Resume judges the run's OWN engine-taken snapshot (QA-PR1-20, SQFU-S16): an edit of the workflow after
        // the run, or its delete (the run is then filed under its snapshot), can neither lift nor impose the cap. A
        // legacy run without one is judged on the live workflow. Any other workflow is untouched (never priced).
        DesignBuildGateResult buildGate = run is null
            ? await designBuildGate.CheckAsync(currentUser.OrganizationId, project.Id, workflowId, acceptEstimateCredits, ct)
            : await designBuildGate.CheckRunAsync(
                currentUser.OrganizationId, project.Id, workflowId, run.ExecutionId, run.SnapshotDefinitionId, acceptEstimateCredits, ct);
        if (buildGate.Refused)
            return Refused(buildGate.ToActionResult(), project, buildGate.Code);

        // The pre-run lint (docs/workflow-linter.md): after every refusal that costs nothing and answers more
        // specifically, before the only check that reads the balance. Enforcing it is a flag (default off); off, it
        // only records what it would have blocked. Never fails the request on its own.
        if (lintGate is not null
            && await lintGate.CheckAsync(project, workflowId, pricing.Steps, brief, pricing.ChargedStepOrders, ct) is { } lintRefusal)
            return Refused(lintRefusal, project, WorkflowLintGate.CodeOf(lintRefusal));

        // The footage itself: a source video the platform cannot edit is refused before a credit is looked at. Costs
        // nothing, so it answers ahead of "top up". Retry and Resume judge the run's own steps and brief.
        IReadOnlyList<WorkflowStep>? sourceSteps = pricing.Steps;
        if (await _sourceGate.CheckAsync(project, workflowId, sourceSteps, brief, pricing.ChargedStepOrders, ct) is { } tooBig)
            return Refused(tooBig.ToResult(), project, tooBig.Code);

        // Repeat refunded too-heavy runs: an organization at its daily limit of SOURCE_TOO_DEMANDING failures is refused
        // (429) for a run whose source is known to be heavy, before credits. Costs nothing, so it answers ahead of "top up".
        if (await _sourceGate.CheckTooDemandingLimitAsync(project, workflowId, sourceSteps, brief, pricing.ChargedStepOrders, ct) is { } tooOften)
            return Refused(tooOften, project, VideoSourceGate.TooDemandingLimitCode);

        // B7: advisory. Everything that is not a definite "the balance cannot cover this" (billing off, no
        // organization, an estimate that could not be evaluated) proceeds: the engine's per-step gate fails closed.
        // The brief's chosen files price the open source slots and BriefFiles sources, as the engine's hold will.
        using IDisposable briefFootage = SourceFootageBrief.Enter(brief?.SourceFiles);
        using IDisposable briefAvatar = BriefAvatarPricing.Enter(brief?.Avatar?.AvatarId);
        // A DynamicsPlanner of a run with no pace is skipped by the engine, so the estimate leaves it out (the same rule).
        using IDisposable runPace = DynamicsPlannerPricing.Enter(runHasPace);
        WorkflowExecutionPreflightResult preflight = pricing.Steps is { } steps
            ? await PriceStepsAsync(steps, pricing.ChargedStepOrders, outputResolution, ct)
            : await new WorkflowExecutionPreflight(db, costEstimator)
                .CheckAsync(currentUser.OrganizationId, project.Id, workflowId, outputResolution, ct);
        if (preflight.WorkflowNotFound)
            return Refused(new NotFoundResult(), project, code: null);
        if (preflight.Allowed)
        {
            // Over an API key's budget the run is refused before anything is captured.
            if (apiKeyId is { } keyId && costEstimator?.BillingEnabled == true
                && await RefuseOverApiKeyBudgetAsync(project, keyId, preflight.RequiredMillis, ct) is { } overBudget)
                return overBudget;
            // Contract section 9: the automatic beforeRun version, at the one point every REST start
            // path (Execute, Retry, Resume and the workflow-assistant execute) passes after its gates
            // succeeded and before the caller publishes. Best effort: TryCaptureAutoAsync owns the
            // 10 s timeout and swallows every failure, so a version can never stop the run.
            await ProjectHistoryAutoCapture.TryCaptureAsync(
                _history, project.Id, ProjectVersionCause.BeforeRun, currentUser.UserId, ct);
            return null;
        }

        return Refused(new ObjectResult(new
        {
            code = "INSUFFICIENT_CREDITS",
            message = CreditShortfallMessage.Build(preflight),
            required = preflight.Required,
            balance = preflight.Balance,
            missing = CreditShortfallMessage.MissingCredits(preflight),
            low = preflight.Low,
            high = preflight.High,
            hasHistory = preflight.HasHistory,
            typical = preflight.Typical,
            fixedMax = preflight.FixedMax
        })
        { StatusCode = StatusCodes.Status402PaymentRequired }, project, "INSUFFICIENT_CREDITS");
    }

    /// <summary>
    /// The key's caps against the run's estimate, after the balance check: 402 <c>API_KEY_BUDGET_EXCEEDED</c> for the
    /// tightest failing window. Advisory like the balance read (no lock); the ledger's locked reserve is the guarantee.
    /// With billing off no hold exists, so nothing is enforced (the caller skips this).
    /// </summary>
    private async Task<ActionResult?> RefuseOverApiKeyBudgetAsync(Project project, Guid apiKeyId, long requiredMillis, CancellationToken ct)
    {
        ApiKey? key = await db.ApiKeys.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(k => k.Id == apiKeyId && k.OrganizationId == project.OrganizationId, ct);
        if (key is null)
            return null;

        DateTime now = DateTime.UtcNow;
        long[] used = (await ApiKeyBudgetWindows.UsedAsync(db, [apiKeyId], now, ct))[apiKeyId];
        ApiKeyBudgetRefusal? exceeded = ApiKeyBudgetWindows.FirstExceeded(key, used, requiredMillis, now);
        if (exceeded is null)
            return null;

        string window = ApiKeyBudgetWindows.Name(exceeded.Window);
        return Refused(new ObjectResult(new
        {
            code = "API_KEY_BUDGET_EXCEEDED",
            message = $"This API key's {window} limit of {exceeded.CapMillis / 1000m:0.##} credits would be exceeded: " +
                      $"{exceeded.UsedMillis / 1000m:0.##} used, this run needs about {requiredMillis / 1000m:0.##}. The limit resets at {exceeded.ResetsAt:yyyy-MM-dd HH:mm} UTC.",
            window,
            capCredits = exceeded.CapMillis / 1000m,
            usedCredits = exceeded.UsedMillis / 1000m,
            requiredCredits = requiredMillis / 1000m,
            resetsAt = exceeded.ResetsAt
        })
        { StatusCode = StatusCodes.Status402PaymentRequired }, project, "API_KEY_BUDGET_EXCEEDED");
    }

    /// <summary>
    /// Hands a refusal to the ambient detectors and returns it <b>unchanged</b> (T-020). The detector's
    /// methods return <c>void</c>, so there is no value it could substitute for the caller's own refusal;
    /// a null result (the run may be queued) is not a refusal and reports nothing.
    /// </summary>
    private ActionResult? Refused(ActionResult? result, Project project, string? code)
    {
        if (result is not null)
        {
            _signals.ObserveRefusal(new RefusalObservation(
                "workflow.run_start",
                currentUser.OrganizationId ?? Guid.Empty,
                currentUser.UserId,
                project.Id,
                StatusOf(result),
                code));
        }

        return result;
    }

    /// <summary>The HTTP status an <see cref="ActionResult"/> will answer with; 200 for one that sets none.</summary>
    private static int StatusOf(ActionResult result) => result switch
    {
        ObjectResult objectResult => objectResult.StatusCode ?? StatusCodes.Status200OK,
        StatusCodeResult statusResult => statusResult.StatusCode,
        _ => StatusCodes.Status200OK
    };

    /// <summary>The B7 verdict over an explicit step list, converted exactly as <see cref="WorkflowExecutionPreflight"/> does.</summary>
    private async Task<WorkflowExecutionPreflightResult> PriceStepsAsync(
        IReadOnlyList<WorkflowStep> steps, IReadOnlyCollection<int>? chargedStepOrders, int? outputResolution, CancellationToken ct)
    {
        if (costEstimator is null || !costEstimator.BillingEnabled)
            return WorkflowExecutionPreflightResult.AllowedResult;

        WorkflowStep[] ordered = [.. steps.OrderBy(s => s.StepOrder)];
        WorkflowPreflightResult preflight = chargedStepOrders is null
            ? await costEstimator.PreflightAsync(currentUser.OrganizationId, ordered, outputResolution, ct)
            : await costEstimator.PreflightChargedAsync(currentUser.OrganizationId, ordered, chargedStepOrders, outputResolution, ct);
        if (!preflight.Refused)
            return WorkflowExecutionPreflightResult.AllowedResult with { RequiredMillis = preflight.Estimate.RequiredMillis };

        WorkflowCostEstimate cost = preflight.Estimate;
        return new WorkflowExecutionPreflightResult(
            false,
            false,
            ToCredits(cost.RequiredMillis),
            ToCredits(preflight.BalanceMillis),
            ToCredits(cost.LowMillis),
            ToCredits(cost.HighMillis),
            cost.HasHistory,
            ToCredits(cost.TypicalMillis),
            ToCredits(cost.FixedMaxMillis));
    }

    /// <summary>Millicredits to the whole-credit unit the billing endpoints report (as <c>WorkflowsController.ToCredits</c>).</summary>
    private static double ToCredits(long millis) => millis / 1000.0;
}
