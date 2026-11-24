using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.Styles;

namespace ReelBolt.Inference.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/workflows/assistant")]
[Authorize]
public class WorkflowAssistantController : ControllerBase
{
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IWorkflowEditDiffService _diffService;
    private readonly IAssistantExecutionGuard _guard;
    private readonly IWorkflowExecutionService _workflowExecutionService;

    private readonly IRunStyleResolver _runStyleResolver;
    // T-202: optional so the tests that construct this controller directly keep compiling; the app registers it.
    private readonly IBriefFileResolver? _briefFiles;
    private readonly ICreditAccountProvisioner? _trialCredits;
    private readonly IPlanGateService? _planGate;
    private readonly StepProviderOwnershipValidator _providerOwnership;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly IDesignBuildEstimateGate _designBuildGate;
    private readonly WorkflowLintGate? _lintGate;
    private readonly VideoSourceGate? _sourceGate;

    private readonly AppInputValueValidator _appInputs;

    public WorkflowAssistantController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IWorkflowEditDiffService diffService,
        IAssistantExecutionGuard guard,
        IWorkflowExecutionService workflowExecutionService,
        IRunStyleResolver? runStyleResolver = null,
        ICreditAccountProvisioner? trialCredits = null,
        IPlanGateService? planGate = null,
        StepProviderOwnershipValidator? providerOwnership = null,
        IWorkflowCostEstimator? costEstimator = null,
        IDesignBuildEstimateGate? designBuildGate = null,
        IBriefFileResolver? briefFiles = null,
        WorkflowLintGate? lintGate = null,
        VideoSourceGate? sourceGate = null,
        AppInputValueValidator? appInputs = null)
    {
        // docs/design/app-inputs.md 6.2; the fallback reads no configuration, so app inputs stay off.
        _appInputs = appInputs ?? new AppInputValueValidator(db);
        _sourceGate = sourceGate;
        _briefFiles = briefFiles;
        _lintGate = lintGate;
        _designBuildGate = designBuildGate ?? new DesignBuildEstimateGate(db, costEstimator, null);
        _trialCredits = trialCredits;
        _planGate = planGate;
        _costEstimator = costEstimator;
        _runStyleResolver = runStyleResolver ?? new RunStyleResolver(db);
        // Optional so the tests that construct this controller directly keep compiling; the app
        // registers it (InferenceApiServiceCollectionExtensions).
        _providerOwnership = providerOwnership ?? new StepProviderOwnershipValidator(db);
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _diffService = diffService;
        _guard = guard;
        _workflowExecutionService = workflowExecutionService;
    }

    [HttpPost("validate")]
    public async Task<ActionResult<WorkflowValidationResult>> Validate(
        Guid projectId,
        [FromBody] IReadOnlyList<CreateWorkflowStepRequest> steps,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowValidationResult result = _diffService.ValidateProposedSteps(projectId, steps);
        if (_planGate is not null && await _planGate.CheckStepsAsync(project.OrganizationId, steps, ct) is { } refused)
            return Ok(new WorkflowValidationResult(false, [.. result.Errors, refused.ToString()]));
        return Ok(result);
    }

    [HttpPost("propose")]
    public async Task<ActionResult<WorkflowEditDiff>> Propose(
        Guid projectId,
        [FromBody] ProposeWorkflowRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        // A8 (SQFU-S7): the same step-agent rule as every other propose and apply surface.
        if (!await StepAgentAuthorization.AllUsableAsync(_db, project.OrganizationId, request.Steps, ct))
            return BadRequest(new { message = StepAgentAuthorization.RefusalMessage });

        var current = new List<WorkflowStep>();
        if (request.WorkflowId.HasValue)
        {
            var workflow = await _db.WorkflowDefinitions
                .FirstOrDefaultAsync(w => w.Id == request.WorkflowId && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);
            if (workflow == null) return NotFound();

            current = await _db.WorkflowSteps
                .AsNoTracking()
                .Where(s => s.WorkflowDefinitionId == workflow.Id)
                .ToListAsync(ct);
        }

        return Ok(_diffService.ComputeDiff(request.WorkflowId, current, request.Steps));
    }

    [HttpPost("apply")]
    public async Task<ActionResult<WorkflowAssistantApplyResponse>> Apply(
        Guid projectId,
        [FromBody] ApplyWorkflowRequest request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        var current = new List<WorkflowStep>();
        WorkflowDefinition? workflow = null;

        if (request.WorkflowId.HasValue)
        {
            workflow = await _db.WorkflowDefinitions
                .FirstOrDefaultAsync(w => w.Id == request.WorkflowId && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);
            if (workflow == null) return NotFound();

            current = await _db.WorkflowSteps
                .AsNoTracking()
                .Where(s => s.WorkflowDefinitionId == workflow.Id)
                .ToListAsync(ct);
        }

        // Recompute diff and verify token
        var diff = _diffService.ComputeDiff(request.WorkflowId, current, request.Steps);
        if (diff.ConfirmationToken != request.ConfirmationToken)
        {
            // The token hashes the workflow's CURRENT steps, so it stops verifying the moment anything
            // else changes them — including this same proposal having been applied already (the
            // assistant applies in the same turn, or the page applied it a moment ago and this is a
            // second click). When the stored steps already ARE the proposed steps there is nothing
            // left to apply and nothing here writes, so say so instead of calling it a conflict.
            // A fabricated token cannot use this: it asks for no write, and it only answers "yes" for
            // steps the workflow genuinely holds.
            if (workflow is not null && diff.ChangesNothing
                && (string.IsNullOrWhiteSpace(request.Name) || string.Equals(request.Name, workflow.Name, StringComparison.Ordinal)))
                return Ok(new WorkflowAssistantApplyResponse(workflow.Id, workflow.Name, diff.ProposedSteps.Count, AlreadyApplied: true));

            return Conflict("The workflow was modified since this diff was computed. Please re-propose and try again.");
        }

        // Save the NORMALIZED proposal ComputeDiff produced, not the raw request steps: a review loop it
        // retargeted or removed must be what the checks below see and what is stored, or the token and the
        // saved graph would disagree. The token verified because Apply normalized the same input identically.
        IReadOnlyList<CreateWorkflowStepRequest> steps = diff.ProposedSteps;

        // A8 (SQFU-S7): every step's own agent is a built-in or this project's organization's, on BOTH
        // branches (the update branch used to skip it), before either writes anything.
        if (!await StepAgentAuthorization.AllUsableAsync(_db, project.OrganizationId, steps, ct))
            return BadRequest(new { message = StepAgentAuthorization.RefusalMessage });

        // Covers both the create and the update branch, before either writes anything. A step may bind the app's
        // inputs as REST's save allows (docs/design/app-inputs.md 13.2).
        List<string> configErrors = [.. StepConfigSaveValidator.Validate(steps)];
        // A10: a reference to a provider this project's organization cannot use is refused here,
        // next to the config check, so the apply never writes a step that would resolve to something
        // other than what its author picked. T-014: scoped to THIS project, so a continue-composition
        // run or StepResult source of a sibling project is refused too.
        configErrors.AddRange(await _providerOwnership.ValidateAsync(project.OrganizationId, project.Id, steps, ct));
        if (configErrors.Count > 0)
        {
            return BadRequest(new { message = StepConfigSaveValidator.FormatMessage(configErrors), errors = configErrors });
        }

        if (_planGate is not null && await _planGate.CheckStepsAsync(project.OrganizationId, steps, ct) is { } planRefused)
            return planRefused.ToResult();

        // Handle create case
        if (request.WorkflowId is null)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return BadRequest("Name is required when creating a new workflow.");
            }

            // A new workflow has no app page, so a step binding an app input names an input nobody declared.
            (_, IReadOnlyList<string> bindingErrors) = WorkflowAppSupport.CheckForSave(
                null, null, await WorkflowAppSupport.FactsForRequestAsync(_db, steps, ct), StepConfigSaveValidator.BindingStepsOf(steps));
            if (bindingErrors.Count > 0)
                return BadRequest(WorkflowAppSupport.ErrorBody(bindingErrors));

            var newWorkflow = new WorkflowDefinition
            {
                Id = Guid.NewGuid(),
                Name = request.Name!,
                ProjectId = projectId,
                CreatedVia = WorkflowCreatedVia.Assistant,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            foreach (var stepReq in steps)
            {
                newWorkflow.Steps.Add(_diffService.BuildStep(newWorkflow.Id, stepReq));
            }

            _db.WorkflowDefinitions.Add(newWorkflow);
            await _db.SaveChangesAsync(ct);

            return StatusCode(201, new WorkflowAssistantApplyResponse(newWorkflow.Id, newWorkflow.Name, steps.Count));
        }

        // Handle update case
        if (workflow == null)
        {
            return NotFound();
        }

        // An edit that would orphan an input the workflow's app exposes (WL018) is a 400 naming the input; the stored page is re-checked against the new steps' bindings as REST's update does.
        (_, IReadOnlyList<string> orphanErrors) = WorkflowAppSupport.CheckForSave(
            null, workflow.AppManifestJson, await WorkflowAppSupport.FactsForRequestAsync(_db, steps, ct),
            StepConfigSaveValidator.BindingStepsOf(steps));
        if (orphanErrors.Count > 0)
            return BadRequest(WorkflowAppSupport.ErrorBody(orphanErrors));

        // Detach any tracked steps from this workflow to avoid stale entity issues
        foreach (var trackedStep in _db.ChangeTracker.Entries<WorkflowStep>()
            .Where(e => e.Entity.WorkflowDefinitionId == workflow.Id)
            .ToList())
        {
            _db.Entry(trackedStep.Entity).State = EntityState.Detached;
        }

        // Clear the workflow's Steps collection to avoid EF identity map issues
        workflow.Steps.Clear();

        if (!string.IsNullOrWhiteSpace(request.Name))
        {
            workflow.Name = request.Name;
        }

        workflow.UpdatedAt = DateTime.UtcNow;

        try
        {
            // Same replacement as PUT: steps a past run's results reference are archived first.
            await new WorkflowStepReplacer(_db).ReplaceStepsAsync(
                workflow, steps.Select(stepReq => _diffService.BuildStep(workflow.Id, stepReq)), ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("The workflow was modified or deleted by another process. Please reload and try again.");
        }

        return Ok(new WorkflowAssistantApplyResponse(workflow.Id, workflow.Name, steps.Count));
    }

    [HttpPost("~/api/v1/projects/{projectId:guid}/workflows/{workflowId:guid}/assistant/execute")]
    public async Task<ActionResult<WorkflowExecutionResponse>> Execute(
        Guid projectId,
        Guid workflowId,
        [FromBody] ExecuteWorkflowRequest? request,
        CancellationToken ct)
    {
        if (EmailVerificationGate.Check(_currentUser) is { } unverified) return unverified;

        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        // QA T-102: a stale v1 copy of the profile template is refused before anything is resolved, preflighted or
        // held (its product-only fallback also runs, and charges, the style extractor); the build route runs the
        // current version.
        if (await StaleProfileWorkflow.IsStaleAsync(_db, projectId, workflowId, ct))
            return Conflict(new { code = StaleProfileWorkflow.Code, message = StaleProfileWorkflow.Message });

        // C6: lazy trial-credit provisioning (no-op unless Billing:Enabled; never throws).
        if (_trialCredits is not null) await _trialCredits.EnsureTrialGrantAsync(_currentUser.UserId, _currentUser.OrganizationId, ct);

        // The run's choices first, exactly as on the REST execute path: a bad choice is a 400 that
        // costs nothing, and the preflight prices the run at the resolution it asked for.
        // The typed run brief (docs/run-brief.md) is split here too: with no brief this is exactly the resolver call.
        RunBriefResolution choices = await RunBriefSplitter.ResolveAsync(
            _runStyleResolver, _db, _currentUser.UserId, projectId,
            new LegacyRunInputs(request?.StyleId, request?.StylePresetKey, request?.Style, request?.OutputFormat, request?.OutputResolution, request?.UserRequest),
            request?.Brief, ct, new AppInputRunStart(_appInputs, _currentUser.OrganizationId, workflowId));
        if (choices.Error is not null)
            return BadRequest(choices.ErrorBody());

        // T-202: a brief file of this project becomes the run's userRequest. A refusal costs nothing.
        BriefResolution brief = await BriefResolutionExtensions.ResolveAsync(_briefFiles, projectId, request?.BriefFileId, choices.UserRequest, ct);
        if (brief.Error is not null)
            return BadRequest(new { message = brief.Error });
        // The shared run-start gate, in the REST execute path's order and position: after C5's 403, the project
        // access 404, the stale-profile 409, C6's trial grant and the run's choices; before the assistant
        // concurrency guard takes a slot. The plan's resolution cap (402 PLAN_NOT_ENTITLED) and concurrency cap
        // (429), the profile-build estimate gate (409/503; this endpoint has no approval card, so the figure
        // comes in the body), then the B7 credit preflight (402) over the workflow's saved steps. The assistant
        // must not be a way around any of them. A workflow not in the project prices as nothing here; the
        // execution service answers its own not-found.
        List<WorkflowStep> pricedSteps = await _db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == workflowId && s.WorkflowDefinition.ProjectId == projectId)
            .ToListAsync(ct);
        if (await new RunStartGate(_db, _currentUser, _planGate, _costEstimator, _designBuildGate, lintGate: _lintGate, sourceGate: _sourceGate)
                .RefuseAsync(project, workflowId, choices.Choices?.OutputResolution, choices.PricingOr(RunStartPricing.AllOf(pricedSteps)), request?.AcceptEstimateCredits, ct, brief: request?.Brief) is { } refused)
            return refused;

        // Which hourly bucket the run is counted against: paid when any step may hold model or vendor credits.
        // Classified INSIDE the guard's (user, project) lock (T-012), from the steps as they are then, so an edit
        // between this call and the lock cannot get a paid run counted as free. The engine re-checks a free
        // admission against the steps it loads (WorkflowExecutionRequested.AdmittedRunRateBucket).
        Task<RunRateBucket> ClassifyUnderLock() => ClassifyWorkflowAsync(workflowId, ct);

        // TryAcquireAsync accepts a callback that inserts the execution row while the (user, project) lock is
        // still held (a session advisory lock on Postgres, no transaction), preventing TOCTOU races.
        // We capture the execution result in a mutable container so we can return it to the caller.
        WorkflowExecutionResponse? executionResult = null;

        PlanGateViolation? planRefusal = null;
        RunRateDecision decision = await _guard.TryAcquireAsync(_currentUser.UserId, projectId, ClassifyUnderLock, RunRateEvent.ExecuteSource, async () =>
        {
            // This callback is only executed if the count check passes.
            // It inserts the execution row and publishes the event while the lock is still held,
            // ensuring atomic counting and insertion.
            try
            {
                executionResult = await _workflowExecutionService.ExecuteAsync(
                    projectId,
                    workflowId,
                    _currentUser.UserId,
                    brief.Text,
                    ExecutionInitiator.Assistant,
                    ct,
                    request?.RegenerateVideoClips ?? false,
                    choices.Choices);
                return executionResult?.Id;
            }
            catch (PlanLimitException limit)
            {
                planRefusal = limit.Violation;
                return null;
            }
        }, ct);

        if (planRefusal is not null) return planRefusal.ToResult();

        if (!decision.Allowed)
        {
            Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(429, new
            {
                message = RunRateRefusal.Sentence(decision),
                code = RunRateRefusal.Code,
                bucket = RunRateRefusal.BucketName(decision.Bucket),
                used = decision.Used,
                limit = decision.Limit,
                windowMinutes = decision.WindowSeconds / 60,
                retryAfterSeconds = decision.RetryAfterSeconds,
                busy = decision.Busy,
            });
        }

        // Return the execution result that was populated by the callback
        return StatusCode(201, executionResult);
    }

    /// <summary>The run-rate bucket of the workflow's steps as they are now; called under the guard's lock (T-012).</summary>
    private async Task<RunRateBucket> ClassifyWorkflowAsync(Guid workflowId, CancellationToken ct) =>
        RunRateClassifier.Classify(
            await _db.WorkflowSteps.AsNoTracking().Where(s => s.WorkflowDefinitionId == workflowId).ToListAsync(ct));
}
