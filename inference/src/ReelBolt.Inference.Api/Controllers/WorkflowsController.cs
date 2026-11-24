using MassTransit;
using ReelBolt.Inference.Api.Agents.Tools;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Outputs;
using ReelBolt.Inference.Api.Services.Auth;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.DesignSystems;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;
using ReelBolt.Shared.IntegrationEvents;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using System;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Controllers;

[ApiController]
[Route("api/v1/projects/{projectId:guid}/workflows")]
[Authorize]
public partial class WorkflowsController : ControllerBase
{
    private readonly ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? _productEvents;
    private readonly InferenceApiDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectAccessService _projectAccess;
    private readonly IPublishEndpoint _publishEndpoint;
    private readonly WorkflowTemplateProvisioningService _workflowTemplateProvisioningService;
    private readonly IWorkflowExecutionService _workflowExecutionService;
    private readonly IRunStyleResolver _runStyleResolver;
    private readonly ICreditAccountProvisioner? _trialCredits;
    private readonly ILogger<WorkflowsController> _logger;
    private readonly IPlanGateService? _planGate;
    private readonly StepProviderOwnershipValidator _providerOwnership;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly IOrganizationEntitlements? _entitlements;
    private readonly IDesignBuildEstimateGate _designBuildGate;
    private readonly WorkflowLintGate? _lintGate;
    private readonly VideoSourceGate? _sourceGate;
    private readonly IWorkflowLinter? _linter;

    // T-202: optional so the tests that construct this controller directly keep compiling; the app registers it.
    private readonly IBriefFileResolver? _briefFiles;

    // docs/design/app-inputs.md 6.2: optional so the tests that construct this controller directly keep compiling; the
    // fallback reads no configuration, so app inputs stay off (lane L1's behaviour).
    private readonly AppInputValueValidator _appInputs;

    public WorkflowsController(
        InferenceApiDbContext db,
        ICurrentUser currentUser,
        IProjectAccessService projectAccess,
        IPublishEndpoint publishEndpoint,
        WorkflowTemplateProvisioningService workflowTemplateProvisioningService,
        IWorkflowExecutionService workflowExecutionService,
        ILogger<WorkflowsController> logger,
        IRunStyleResolver? runStyleResolver = null,
        ICreditAccountProvisioner? trialCredits = null,
        IPlanGateService? planGate = null,
        StepProviderOwnershipValidator? providerOwnership = null,
        IWorkflowCostEstimator? costEstimator = null,
        IOrganizationEntitlements? entitlements = null,
        IDesignBuildEstimateGate? designBuildGate = null,
        IBriefFileResolver? briefFiles = null,
        WorkflowLintGate? lintGate = null,
        IWorkflowLinter? linter = null,
        ReelBolt.Inference.Api.Services.Telemetry.IProductEventEmitter? productEvents = null,
        VideoSourceGate? sourceGate = null,
        AppInputValueValidator? appInputs = null)
    {
        _appInputs = appInputs ?? new AppInputValueValidator(db);
        _sourceGate = sourceGate;
        _productEvents = productEvents;
        _briefFiles = briefFiles;
        // Optional so the tests that construct this controller directly keep compiling; the app registers both.
        _lintGate = lintGate;
        _linter = linter;
        // Optional so the tests that construct this controller directly keep compiling; the app
        // registers it. The fallback reads no configuration, so the cap is the single default.
        _designBuildGate = designBuildGate ?? new DesignBuildEstimateGate(db, costEstimator, null);
        _trialCredits = trialCredits;
        _planGate = planGate;
        _costEstimator = costEstimator;
        // B8c: a retry is re-queued at the plan's priority, like the original run. Optional so the
        // tests that construct this controller directly keep compiling; the app registers it.
        _entitlements = entitlements;
        // Optional so the many tests that construct this controller directly keep compiling; the
        // app registers it (InferenceApiServiceCollectionExtensions).
        _providerOwnership = providerOwnership ?? new StepProviderOwnershipValidator(db);
        // Optional so the many tests that construct this controller directly keep compiling; the
        // app registers it (InferenceApiServiceCollectionExtensions).
        _runStyleResolver = runStyleResolver ?? new RunStyleResolver(db);
        _db = db;
        _currentUser = currentUser;
        _projectAccess = projectAccess;
        _publishEndpoint = publishEndpoint;
        _workflowTemplateProvisioningService = workflowTemplateProvisioningService;
        _workflowExecutionService = workflowExecutionService;
        _logger = logger;
    }

    [HttpGet("templates")]
    public async Task<ActionResult<List<WorkflowTemplateSummaryResponse>>> ListTemplates(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        IReadOnlyList<WorkflowTemplateDefinition> templates = await _workflowTemplateProvisioningService.ListRecipesAsync(ct);

        return Ok(templates.Select(template => new WorkflowTemplateSummaryResponse(
            template.Key,
            template.Name,
            template.Description,
            template.Version,
            template.AutoCreateOnProject,
            template.RequiresUserInput,
            template.Steps.Count,
            RequiresProfile: template.Key is WorkflowTemplateCatalog.QuickWinPromoDsTemplateKey
                or WorkflowTemplateCatalog.QuickWinPromoPlatesDsTemplateKey)).ToList());
    }

    [HttpPost("templates/{templateKey}/apply")]
    public async Task<ActionResult<WorkflowDefinitionResponse>> ApplyTemplate(
        Guid projectId,
        string templateKey,
        [FromBody] ApplyWorkflowTemplateRequest? request,
        CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        bool skipIfExists = request?.SkipIfExists ?? false;
        WorkflowDefinition? workflow;
        try
        {
            workflow = await _workflowTemplateProvisioningService.CreateFromTemplateAsync(projectId, templateKey, skipIfExists, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }

        if (workflow is null)
            return Conflict(new { message = "Template could not be applied." });

        workflow = await _db.WorkflowDefinitions
            .Include(w => w.Steps)
            .FirstAsync(w => w.Id == workflow.Id, ct);

        // product-analytics: the template chosen. The detail is the CATALOG's key (the route value only
        // selected it), so the row can never carry caller-chosen text.
        if (_productEvents is not null && WorkflowTemplateCatalog.GetByKey(templateKey) is { } chosen)
        {
            await _productEvents.AppendAsync(
                _currentUser.UserId, project.OrganizationId,
                ReelBolt.Inference.Api.Services.Telemetry.ProductEventNames.TemplateChosen, chosen.Key, ct);
        }

        return Ok(MapWorkflowResponse(workflow));
    }

    [HttpGet]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<WorkflowDefinitionResponse>>> List(Guid projectId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        // The hidden composition-revision workflow is a system object, never listed with the user's own.
        List<WorkflowDefinitionResponse> workflows = await _db.WorkflowDefinitions
            .Where(w => w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard)
            .Include(w => w.Steps)
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new WorkflowDefinitionResponse(
                w.Id, w.Name, w.CreatedAt, w.UpdatedAt,
                w.Steps.OrderBy(s => s.StepOrder).Select(s =>
                    new WorkflowStepResponse(s.Id, s.AgentDefinitionId, s.StepOrder, s.EdgeConditionJson, s.Label,
                        s.StepType.ToString(), s.ConditionExpression, s.LoopSourceExpression,
                        s.LoopTargetStepOrder, s.MaxIterations, s.MinScore, s.InputMappingJson,
                        s.AgentInputContextMode.HasValue ? s.AgentInputContextMode.Value.ToString() : null,
                        s.SelectedPriorStepOrdersJson,
                        s.TrueBranchStepOrder, s.FalseBranchStepOrder, s.ParallelAgentIdsJson,
                        s.ExtractConfigJson, s.VideoAnalyzeConfigJson, s.VideoCompileConfigJson, s.EditRoomConfigJson, s.GraphicsRoomConfigJson,
                        s.ColorGradeRoomConfigJson, s.ConditionalConfigJson, s.ReviewLoopConfigJson,
                        s.CacheMode.HasValue ? s.CacheMode.Value.ToString() : null,
                        s.VideoGenerateConfigJson, s.VoiceoverConfigJson, s.ThumbnailGenerateConfigJson, s.DailiesRoomConfigJson, s.TimelineRenderConfigJson, s.DesignSystemConfigJson,
                        s.ContinueCompositionJson, s.RegisterOutputAsProjectFile, s.ExtractFrameConfigJson, s.AudioGenerateConfigJson, s.PinInsertConfigJson, s.AudioCleanupConfigJson,
                        s.VideoEnhanceConfigJson, s.ImageGenerateConfigJson)
                ).ToList(),
                w.RequiresUserInput))
            .ToListAsync(ct);
        return Ok(workflows);
    }

    [HttpGet("{id:guid}")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<WorkflowDefinitionResponse>> Get(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        // A run's snapshot (ExecutionSnapshot) is a system object, never a workflow of the user's.
        WorkflowDefinition? workflow = await _db.WorkflowDefinitions
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind != WorkflowDefinitionKind.ExecutionSnapshot, ct);
        if (workflow == null) return NotFound();
        return Ok(MapWorkflowResponse(workflow));
    }

    [HttpPost]
    public async Task<ActionResult<WorkflowDefinitionResponse>> Create(Guid projectId, [FromBody] CreateWorkflowRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        if (await InvalidStepConfigsAsync(project, request.Steps, ct) is { } invalid) return invalid;
        if (await InvalidStepAgentsAsync(project, request.Steps, ct) is { } badAgent) return badAgent;
        if (await PlanRefusesStepsAsync(project.OrganizationId, request.Steps, ct) is { } refused) return refused;

        // The app manifest is checked against the steps it will live with, before anything is written.
        (AppManifest? manifest, IReadOnlyList<string> manifestErrors) = WorkflowAppSupport.CheckForSave(
            request.AppManifest, null, await WorkflowAppSupport.FactsForRequestAsync(_db, request.Steps, ct), AppBindingStepsOf(request.Steps));
        if (manifestErrors.Count > 0)
            return BadRequest(WorkflowAppSupport.ErrorBody(manifestErrors));

        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = request.Name,
            ProjectId = projectId,
            RequiresUserInput = request.RequiresUserInput,
            AppManifestJson = manifest is null ? null : AppManifestJson.Serialize(manifest),
            CreatedVia = WorkflowCreatedVia.User,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        foreach (CreateWorkflowStepRequest stepReq in request.Steps)
        {
            workflow.Steps.Add(CreateStep(workflow.Id, stepReq));
        }

        _db.WorkflowDefinitions.Add(workflow);
        await _db.SaveChangesAsync(ct);

        return StatusCode(201, MapWorkflowResponse(workflow));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<WorkflowDefinitionResponse>> Update(Guid projectId, Guid id, [FromBody] UpdateWorkflowRequest request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions
            .FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);
        if (workflow == null) return NotFound();

        // Before the delete below: a rejected update must leave the stored workflow untouched.
        if (await InvalidStepConfigsAsync(project, request.Steps, ct) is { } invalid) return invalid;
        if (await InvalidStepAgentsAsync(project, request.Steps, ct) is { } badAgent) return badAgent;
        if (await PlanRefusesStepsAsync(project.OrganizationId, request.Steps, ct) is { } refused) return refused;

        // Also before the delete: a manifest the new steps cannot honour (or a stored exposed input they would
        // orphan, WL018) is a 400 naming the input, and the stored workflow stays as it was.
        if (request.ClearAppManifest && request.AppManifest is not null)
            return BadRequest(WorkflowAppSupport.ErrorBody(["Send either appManifest or clearAppManifest, not both."]));

        // Clearing returns the app to its derived manifest, so there is nothing stored left to orphan.
        (AppManifest? manifest, IReadOnlyList<string> manifestErrors) = WorkflowAppSupport.CheckForSave(
            request.AppManifest, request.ClearAppManifest ? null : workflow.AppManifestJson,
            await WorkflowAppSupport.FactsForRequestAsync(_db, request.Steps, ct), AppBindingStepsOf(request.Steps));
        if (manifestErrors.Count > 0)
            return BadRequest(WorkflowAppSupport.ErrorBody(manifestErrors));

        if (request.ClearAppManifest)
            workflow.AppManifestJson = null;
        else if (manifest is not null)
            workflow.AppManifestJson = AppManifestJson.Serialize(manifest);

        if (!string.IsNullOrWhiteSpace(request.Name))
            workflow.Name = request.Name;

        if (request.RequiresUserInput.HasValue)
            workflow.RequiresUserInput = request.RequiresUserInput.Value;

        workflow.UpdatedAt = DateTime.UtcNow;
        try
        {
            // Replace the steps in one transaction; steps a past run's results still reference are
            // archived first, so the replacement cannot cascade-delete that run's results.
            await new WorkflowStepReplacer(_db).ReplaceStepsAsync(
                workflow, request.Steps.Select(stepReq => CreateStep(workflow.Id, stepReq)), ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // the workflow or one of its steps was removed by another user/session
            return Conflict("The workflow was modified or deleted by another process. Please reload and try again.");
        }

        return Ok(MapWorkflowResponse(workflow));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct);
        if (workflow == null) return NotFound();

        // Refused while a run is in flight; otherwise every past run is re-pointed to its own snapshot
        // first, so the delete's cascade reaches none of them or their results.
        if (!await new WorkflowStepReplacer(_db).DeleteAsync(workflow, ct))
            return Conflict(new { code = WorkflowStepReplacer.RunActiveCode, message = WorkflowStepReplacer.RunActiveMessage });
        return NoContent();
    }

    [HttpPost("{id:guid}/execute")]
    public async Task<ActionResult<WorkflowExecutionResponse>> Execute(
        Guid projectId, Guid id,
        [FromBody] ExecuteWorkflowRequest? request,
        CancellationToken ct)
    {
        // Email verification, then project authority (the service does not redo this check): the shared
        // run-start gate, so Execute, Retry and Resume cannot drift.
        RunStartAuthorization authority = await RunStartGate.AuthorizeAsync(_projectAccess, projectId, ct);
        if (authority.Failure is { } denied) return denied;
        Project project = authority.Project!;

        // QA T-102: a stale v1 copy of the profile template is refused before anything is resolved, preflighted or
        // held (the shared run-start gate's check).
        if (await RunStartGate.RefuseStaleProfileAsync(projectId, id, ct) is { } outdated) return outdated;

        // C6: lazy trial-credit provisioning (no-op unless Billing:Enabled; never throws).
        if (_trialCredits is not null) await _trialCredits.EnsureTrialGrantAsync(_currentUser.UserId, _currentUser.OrganizationId, ct);

        // The run's choices are resolved BEFORE the credit preflight: a bad style, format or resolution
        // is a 400 that costs nothing, and the preflight must price the run at the resolution it asked
        // for (a 4K render holds four times a 1080p one).
        // The typed run brief (docs/run-brief.md) is split here too: with no brief this is exactly the resolver call.
        RunBriefResolution choices = await RunBriefSplitter.ResolveAsync(
            _runStyleResolver, _db, _currentUser.UserId, projectId,
            new LegacyRunInputs(request?.StyleId, request?.StylePresetKey, request?.Style, request?.OutputFormat, request?.OutputResolution, request?.UserRequest),
            request?.Brief, ct, new AppInputRunStart(_appInputs, _currentUser.OrganizationId, id, ReelBolt.Shared.Auth.MediaEgressCallers.AppSession(User)));
        if (choices.Error is not null)
            return BadRequest(choices.ErrorBody());

        // T-202: a brief file of this project becomes the run's userRequest. A refusal costs nothing.
        BriefResolution brief = await BriefResolutionExtensions.ResolveAsync(_briefFiles, projectId, request?.BriefFileId, choices.UserRequest, ct);
        if (brief.Error is not null)
            return BadRequest(new { message = brief.Error });
        // The shared run-start gate: the plan's resolution cap (402 PLAN_NOT_ENTITLED) and concurrency cap (429;
        // QueueAsync re-checks both, defence in depth), the profile-build estimate gate (409/503), then B7's
        // pre-run cost preflight over the saved workflow, deliberately LAST of the refusing checks before the
        // queue: C5's email-verification 403, the project access 404 and the plan must win, and C6's trial grant
        // must already have landed so a first-ever run is priced against the credits it has just been given.
        // Advisory only — the per-step gate enforces every credit.
        // An app with inputs is priced, linted and gated on its bound steps, on the branch its inputs choose (app-inputs 8.5).
        if (await RunStartGate.RefuseAsync(project, id, choices.Choices?.OutputResolution, choices.PricingOr(RunStartPricing.SavedWorkflow), request?.AcceptEstimateCredits, ct, brief: request?.Brief,
                runHasPace: DynamicsPlannerPricing.HasPace(request?.Brief?.Pace, choices.Choices?.Style?.Config.Dynamics)) is { } refused)
            return refused;

        try
        {
            var response = await _workflowExecutionService.ExecuteAsync(
                projectId,
                id,
                _currentUser.UserId,
                brief.Text,
                ExecutionInitiator.Human,
                ct,
                request?.RegenerateVideoClips ?? false,
                choices.Choices);

            return StatusCode(201, response);
        }
        catch (WorkflowNotFoundException)
        {
            return NotFound();
        }
        catch (PlanLimitException limit)
        {
            return limit.Violation.ToResult();
        }
    }

    /// <summary>
    /// B7: what this run is expected to cost, before anyone presses Execute. Answers 200 with the
    /// range, the required credits and the per-step breakdown; the balance is included so the caller
    /// can render "≈ X–Y credits" and a shortfall in one round trip.
    /// </summary>
    /// <param name="outputResolution">
    /// Optional run choice (<c>Auto</c>, <c>720p</c>, <c>1080p</c>, <c>2160p</c>): the render steps are
    /// priced at that resolution. Unknown values are a 400.
    /// </param>
    /// <param name="videoFileIds">Optional (repeatable, at most 50 used): the clips the run page chose, priced into the open source slots.</param>
    [HttpGet("{id:guid}/estimate")]
    public async Task<ActionResult<WorkflowCostEstimateResponse>> Estimate(
        Guid projectId, Guid id, CancellationToken ct, [FromQuery] string? outputResolution = null,
        [FromQuery] Guid[]? videoFileIds = null)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        if (!OutputResolution.TryParse(outputResolution, out int? resolution))
            return BadRequest(new { message = OutputResolution.UnknownMessage(outputResolution) });

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind != WorkflowDefinitionKind.ExecutionSnapshot, ct);
        if (workflow is null) return NotFound();

        // No brief or style is carried: a DynamicsPlanner would be skipped, so the estimate leaves it out.
        using IDisposable runPace = DynamicsPlannerPricing.Enter(false);
        return await EstimateStepsAsync(OrderedSteps(workflow), resolution, videoFileIds, ct);
    }

    /// <summary>
    /// The estimate of a run with its brief (docs/design/app-inputs.md 11.4): the same answer as <c>GET</c>, for the steps as
    /// the brief's app inputs bind them and only on the branch those inputs choose. A missing required input is not a
    /// refusal here (the page prices as the person types); a value that cannot be used is the run gate's 400. The
    /// resolution and the clips come from the body, else from the brief.
    /// </summary>
    [HttpPost("{id:guid}/estimate")]
    public async Task<ActionResult<WorkflowCostEstimateResponse>> EstimateWithBrief(
        Guid projectId, Guid id, [FromBody] EstimateWorkflowRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        ReelBolt.Shared.Briefs.RunBrief? runBrief = request?.Brief;
        if (runBrief is not null && RunBriefValidator.Validate(runBrief) is { } invalidBrief)
            return BadRequest(new { message = invalidBrief });

        string? resolutionText = string.IsNullOrWhiteSpace(request?.OutputResolution) ? runBrief?.Resolution : request.OutputResolution;
        if (!OutputResolution.TryParse(resolutionText, out int? resolution))
            return BadRequest(new { message = OutputResolution.UnknownMessage(resolutionText) });

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind != WorkflowDefinitionKind.ExecutionSnapshot, ct);
        if (workflow is null) return NotFound();

        IReadOnlyList<WorkflowStep> steps = OrderedSteps(workflow);
        IReadOnlyDictionary<Guid, Guid>? agentModels = null;
        IReadOnlySet<Guid>? stepModels = null;
        AppInputGateResult gate = await _appInputs.CheckAsync(
            _currentUser.OrganizationId, projectId, id, steps, null, runBrief?.Inputs, notes: null, ct, forEstimate: true,
            mediaEgress: new MediaEgressGate(runBrief?.Avatar?.AvatarId is not null, null));
        if (gate.Refused)
            return BadRequest(gate.ErrorBody());
        if (gate.ReachableSteps is { } reachable)
            steps = reachable;
        agentModels = gate.AgentModels;
        stepModels = gate.StepModels;

        Guid[]? clips = request?.VideoFileIds is { Length: > 0 } chosen
            ? chosen
            : runBrief?.SourceFiles?.Where(g => g.Role == ReelBolt.Shared.Briefs.RunBriefFileRole.Video).SelectMany(g => g.FileIds ?? []).ToArray();
        // The agents' chosen Chat models (docs/design/app-inputs.md 8.5) price their cold-start chat at that model.
        using IDisposable chosenModels = ReelBolt.Shared.Metering.AppAgentModelAmbient.Enter(agentModels, stepModels);
        // The run brief's avatar fills a plain inline step, as the gate and the hold price it.
        using IDisposable briefAvatar = ReelBolt.Shared.Metering.BriefAvatarPricing.Enter(runBrief?.Avatar?.AvatarId);
        // A DynamicsPlanner is skipped by a run with no pace, so the estimate leaves it out unless the brief carries one.
        using IDisposable runPace = DynamicsPlannerPricing.Enter(DynamicsPlannerPricing.HasPace(runBrief?.Pace, null));
        return await EstimateStepsAsync(steps, resolution, clips, ct);
    }

    /// <summary>The estimate body shared by <c>GET</c> and <c>POST</c>: <paramref name="steps"/> priced at the resolution, with the chosen clips.</summary>
    private async Task<ActionResult<WorkflowCostEstimateResponse>> EstimateStepsAsync(
        IReadOnlyList<WorkflowStep> steps, int? resolution, Guid[]? videoFileIds, CancellationToken ct)
    {
        if (_costEstimator is null)
        {
            // Nothing is wired in this host: answer the honest empty estimate rather than a 404 a
            // client would have to special-case.
            return Ok(new WorkflowCostEstimateResponse(false, false, 0, 0, 0, null, true, [], 0, 0, CreditRangeBasis.FirstRun, 0, []));
        }

        // One call: the preflight carries the estimate it was based on, so the breakdown and the
        // balance are guaranteed to describe the same read.
        // The clips the run page chose price the workflow's open source slots as the run will hold them. Bounded;
        // a file outside this project and organization is unknown to the estimate and ignored.
        using IDisposable chosenClips = ReelBolt.Shared.Metering.SourceFootageBrief.Enter(
            videoFileIds is { Length: > 0 }
                ? new ReelBolt.Shared.Briefs.RunBriefSourceFiles[] { new(ReelBolt.Shared.Briefs.RunBriefFileRole.Video, [.. videoFileIds.Distinct().Take(50)]) }
                : null);
        WorkflowPreflightResult preflight = await _costEstimator
            .PreflightAsync(_currentUser.OrganizationId, steps, resolution, ct);
        WorkflowCostEstimate cost = preflight.Estimate;
        // Not evaluated (the estimate or the balance read failed): the same empty answer as before the
        // estimate was kept, rather than real figures against a balance of zero.
        if (!cost.BillingEnabled || !preflight.Evaluated)
        {
            return Ok(new WorkflowCostEstimateResponse(false, false, 0, 0, 0, null, true, [], 0, 0, CreditRangeBasis.FirstRun, 0, []));
        }

        return Ok(new WorkflowCostEstimateResponse(
            cost.BillingEnabled,
            cost.HasHistory,
            ToCredits(cost.LowMillis),
            ToCredits(cost.HighMillis),
            ToCredits(cost.RequiredMillis),
            ToCredits(preflight.BalanceMillis),
            preflight.BalanceMillis >= cost.RequiredMillis,
            [.. cost.Steps.Select(s => new WorkflowStepCostEstimateResponse(
                s.StepOrder, s.StepType, s.AgentDefinitionId, s.Label,
                ToCredits(s.LowMillis), ToCredits(s.HighMillis), ToCredits(s.HoldMillis), s.Basis,
                ToCredits(s.TypicalMillis), s.ExpectedRuns, ToCredits(s.FixedMaxMillis)))],
            ToCredits(cost.TypicalMillis),
            ToCredits(cost.FixedMaxMillis),
            cost.RangeBasis,
            cost.PastRunCount,
            cost.SpreadReasons,
            steps.Any(st => st.StepType == StepType.VideoGenerate)
                ? await VideoBudgetSnapshot.ForUserAsync(_db, _currentUser.UserId, ct)
                : null));
    }

    /// <summary>The most steps one lint request may carry (a draft is bounded like any other request body).</summary>
    private const int MaxLintSteps = 200;

    /// <summary>The largest lint request body (a draft of 200 ordinary steps is far below this).</summary>
    private const int MaxLintBodyBytes = 1_000_000;

    /// <summary>The most distinct ids (files, step results) one lint request may name; each is a lookup.</summary>
    internal const int MaxLintReferences = 500;

    [System.Text.RegularExpressions.GeneratedRegex("[0-9a-fA-F]{8}-?(?:[0-9a-fA-F]{4}-?){3}[0-9a-fA-F]{12}")]
    private static partial System.Text.RegularExpressions.Regex IdPattern();

    private static int DistinctReferences(IEnumerable<CreateWorkflowStepRequest> steps) =>
        steps.SelectMany(step => new[]
            {
                step.ExtractConfigJson, step.VideoAnalyzeConfigJson, step.VideoCompileConfigJson, step.VideoGenerateConfigJson,
                step.VoiceoverConfigJson, step.DesignSystemConfigJson, step.PinInsertConfigJson, step.ExtractFrameConfigJson,
                step.ThumbnailGenerateConfigJson, step.AudioGenerateConfigJson, step.AudioCleanupConfigJson, step.VideoEnhanceConfigJson, step.ImageGenerateConfigJson
            })
            .Where(json => json is not null)
            .SelectMany(json => IdPattern().Matches(json!).Select(m => m.Value.Replace("-", "").ToLowerInvariant()))
            .Distinct()
            .Count();

    /// <summary>
    /// The pre-run linter on a saved workflow (docs/workflow-linter.md): the findings, plain-language, most serious
    /// first. <c>steps</c> in the body lints that draft in place of the saved steps (unsaved edits); <c>brief</c> adds
    /// the brief rules. Reads only; View is enough.
    /// </summary>
    [HttpPost("{id:guid}/lint")]
    [RequestSizeLimit(MaxLintBodyBytes)]
    public async Task<ActionResult<WorkflowLintResponse>> Lint(
        Guid projectId, Guid id, [FromBody] LintWorkflowRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions
            .AsNoTracking()
            .Include(w => w.Steps)
            .FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind != WorkflowDefinitionKind.ExecutionSnapshot, ct);
        if (workflow is null) return NotFound();

        return await LintAsync(project, workflow.Id, request, [.. workflow.Steps], ct);
    }

    /// <summary>The same lint for a draft that has no saved workflow yet: <c>steps</c> is required.</summary>
    [HttpPost("lint")]
    [RequestSizeLimit(MaxLintBodyBytes)]
    public async Task<ActionResult<WorkflowLintResponse>> LintDraft(
        Guid projectId, [FromBody] LintWorkflowRequest? request, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        if (request?.Steps is not { Count: > 0 })
            return BadRequest(new { code = "LINT_STEPS_REQUIRED", message = "Send the draft's steps to lint it." });

        return await LintAsync(access.Project!, null, request, [], ct);
    }

    private async Task<ActionResult<WorkflowLintResponse>> LintAsync(
        Project project, Guid? workflowId, LintWorkflowRequest? request, List<WorkflowStep> savedSteps, CancellationToken ct)
    {
        if (_linter is null)
            return StatusCode(503, new { code = "LINT_UNAVAILABLE", message = "The workflow check is not available." });
        if (request?.Steps is { Count: > MaxLintSteps })
            return BadRequest(new { code = "LINT_TOO_MANY_STEPS", message = $"A workflow can have at most {MaxLintSteps} steps." });
        if (request?.Brief is { } briefToCheck && RunBriefValidator.Validate(briefToCheck) is { } briefError)
            return BadRequest(new { message = briefError });

        if (request?.Steps is { Count: > 0 } sent && DistinctReferences(sent) > MaxLintReferences)
            return BadRequest(new { code = "LINT_TOO_LARGE", message = $"A workflow can name at most {MaxLintReferences} different files or results to check at once." });

        List<WorkflowStep> steps;
        if (request?.Steps is { Count: > 0 } draft)
        {
            Dictionary<Guid, AgentDefinition> agents = await WorkflowLinterAgents.LoadAsync(
                _db, project.OrganizationId, [.. draft.Select(s => s.AgentDefinitionId).Distinct()], ct);
            steps = WorkflowLinter.FromRequests(draft, agents);
        }
        else
        {
            steps = savedSteps;
            await WorkflowLinterAgents.FillAsync(_db, project.OrganizationId, steps, ct);
        }

        // Counted twice, by the text pattern and by the linter's own id extractor, so neither can be sidestepped.
        if (DistinctReferences(steps.Select(AssistantStepShapes.FromStep)) > MaxLintReferences
            || WorkflowLinter.NamedFileIds(steps).Count > MaxLintReferences)
            return BadRequest(new { code = "LINT_TOO_LARGE", message = $"A workflow can name at most {MaxLintReferences} different files or results to check at once." });

        // The same time budget as an enforcing run start: a check that cannot finish says so instead of holding a connection.
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(WorkflowLintGate.EnforceBudget);
        try
        {
            WorkflowLintReport report = await _linter.LintAsync(
                new WorkflowLintRequest(project.OrganizationId, project.Id, steps, request?.Brief, workflowId, Manifest: request?.AppManifest), budget.Token);
            // Footage the run would refuse (too big or too long), so the run page says so before Run. Never fails the lint.
            IReadOnlyList<VideoSourceRefusal> sourceProblems = [];
            if (_sourceGate is not null)
            {
                try
                {
                    sourceProblems = await _sourceGate.FindAllAsync(project, workflowId ?? Guid.Empty, steps, request?.Brief, budget.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "Source-size check skipped for the lint of workflow {WorkflowId}", workflowId);
                }
            }

            return Ok(WorkflowLintResponse.From(report.Findings) with { SourceProblems = sourceProblems });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return StatusCode(503, new { code = "LINT_TIMEOUT", message = "The check took too long and was stopped. Try again, or check fewer steps at once." });
        }
    }

    /// <summary>The step list B7 prices: deterministic order, so the range does not shuffle between calls.</summary>
    private static WorkflowStep[] OrderedSteps(WorkflowDefinition workflow)
        => [.. workflow.Steps.OrderBy(s => s.StepOrder)];

    /// <summary>
    /// Set only by the public API's retry/resume (never by a route): the CALLING key pays. The key preflight is run
    /// against it and it is written onto the run in the same conditional update as the requeue, so the engine's
    /// per-step reserves charge the caller's windows. Null (every dashboard call) keeps the row's own ApiKeyId.
    /// </summary>
    internal Guid? ApiKeyOverride { get; set; }

    /// <summary>
    /// S-L2: the key a dashboard retry/resume charges. A caller key (<see cref="ApiKeyOverride"/>) always pays. On a
    /// dashboard restart (no override) the row's own key keeps paying only while it is usable; if it was revoked or
    /// expired, clearing it makes the organization pay as for any dashboard run — the engine's per-step reserves would
    /// otherwise charge a key whose windows belong to a credential nobody can use any more, or refuse the restart.
    /// A missing key row is cleared for the same reason.
    /// </summary>
    private async Task<Guid?> RestartApiKeyAsync(WorkflowExecution execution, CancellationToken ct)
    {
        if (ApiKeyOverride is { } caller) return caller;
        if (execution.ApiKeyId is not { } keyId) return null;

        var key = await _db.ApiKeys.IgnoreQueryFilters().AsNoTracking()
            .Where(k => k.Id == keyId)
            .Select(k => new { k.RevokedAt, k.ExpiresAt })
            .FirstOrDefaultAsync(ct);
        bool usable = key is not null && key.RevokedAt is null
            && (key.ExpiresAt is null || DateTime.SpecifyKind(key.ExpiresAt.Value, DateTimeKind.Utc) > DateTime.UtcNow);
        return usable ? keyId : null;
    }

    /// <summary>
    /// L19b: a dashboard retry/resume of a run a service account started whose key is revoked, expired or gone is
    /// refused with 409 run_key_revoked. Clearing the key would let the restart fail later with a 401 at the engine.
    /// Null when the restart may go on (a caller key pays, a human started it, or the key is still usable).
    /// </summary>
    private async Task<ActionResult?> RefuseRevokedKeyRestartAsync(WorkflowExecution execution, CancellationToken ct)
    {
        if (ApiKeyOverride is not null || execution.InitiatedByUserId is not { } initiator) return null;
        bool serviceAccount = await _db.ApplicationUsers.AsNoTracking().AnyAsync(u => u.Id == initiator && u.IsServiceAccount, ct);
        if (!serviceAccount) return null;
        if (execution.ApiKeyId is { } keyId && await RestartApiKeyAsync(execution, ct) == keyId) return null;
        return Conflict(new
        {
            error = "run_key_revoked",
            message = "The API key that started this run was revoked; start a new run instead."
        });
    }

    private RunStartGate? _runStartGate;

    /// <summary>
    /// T-004: the gate sequence Execute, Retry and Resume share (authority, then plan, the estimate gate and the 402), so
    /// the three cannot drift. Built on first use from the controller's own dependencies.
    /// </summary>
    private RunStartGate RunStartGate =>
        _runStartGate ??= new RunStartGate(_db, _currentUser, _planGate, _costEstimator, _designBuildGate, lintGate: _lintGate, sourceGate: _sourceGate);

    /// <summary>Millicredits to the whole-credit unit the billing endpoints report.</summary>
    internal static double ToCredits(long millis) => millis / 1000.0;

    [HttpPost("{id:guid}/executions/{executionId:guid}/stop")]
    public async Task<IActionResult> StopExecution(
        Guid projectId, Guid id,
        Guid executionId,
        CancellationToken ct)
    {
        // verify project ownership
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.Edit, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowExecution? exec = await _db.WorkflowExecutions
            .FirstOrDefaultAsync(e => e.Id == executionId && e.ProjectId == projectId && e.WorkflowDefinitionId == id, ct);
        if (exec == null) return NotFound();

        _logger.LogInformation(
            "Workflow stop requested via API (ExecutionId={ExecutionId}, WorkflowId={WorkflowId}, ProjectId={ProjectId}, UserId={UserId}, CurrentStatus={CurrentStatus})",
            executionId,
            id,
            projectId,
            _currentUser.UserId,
            exec.Status);

        await _publishEndpoint.Publish(new WorkflowExecutionStopRequested
        {
            ExecutionId = executionId,
            RequestedByUserId = _currentUser.UserId
        }, ct);

        _logger.LogInformation(
            "Workflow stop request published to bus (ExecutionId={ExecutionId}, UserId={UserId})",
            executionId,
            _currentUser.UserId);

        return Accepted();
    }
    [HttpPost("{id:guid}/executions/{executionId:guid}/retry")]
    public async Task<ActionResult<WorkflowExecutionResponse>> RetryExecution(
        Guid projectId, Guid id,
        Guid executionId,
        [FromBody] ExecuteWorkflowRequest? request,
        CancellationToken ct)
    {
        // The shared run-start gate's authority half: email verification, then Edit on the project.
        RunStartAuthorization authority = await RunStartGate.AuthorizeAsync(_projectAccess, projectId, ct);
        if (authority.Failure is { } denied) return denied;
        Project project = authority.Project!;

        // A retry re-queues the same run with the choices it was started with; a brief sent now would be ignored,
        // so it is refused instead of being silently dropped.
        if (request?.Brief is not null)
            return BadRequest(new { message = "A retry reuses the run's own brief. Send the retry without a brief, or start a new run with the one you want." });

        // Scoped to the project in the route. workflow_executions carries no tenant filter, so without
        // the ProjectId match a caller with edit rights on one project could re-queue another
        // project's run by naming its workflow and execution ids.
        WorkflowExecution? execution = await _db.WorkflowExecutions
            .Include(e => e.StepResults)
            .Include(e => e.ReviewScores)
            .FirstOrDefaultAsync(e => e.Id == executionId && e.WorkflowDefinitionId == id && e.ProjectId == projectId, ct);

        if (execution == null) return NotFound();

        // An output that is not served cannot be restarted and watched: refused before any write or gate.
        if (await OutputGate.IsExecutionHeldAsync(_db, execution, ct)) return OutputGate.Unavailable();

        // A run of a hidden system workflow (a video revision, a designed graphic) is started only
        // through its own endpoint, which applies its own rate limit and credit preflight and writes the
        // request row the run reads; re-queuing it here would bypass all three.
        WorkflowDefinitionKind? systemKind = await _db.WorkflowDefinitions
            .Where(w => w.Id == id && w.ProjectId == projectId && w.Kind != WorkflowDefinitionKind.Standard)
            .Select(w => (WorkflowDefinitionKind?)w.Kind)
            .FirstOrDefaultAsync(ct);
        // a retry runs the run's OWN snapshot when the engine took one. A run whose workflow was deleted
        // is filed under its snapshot (T-002): with an engine-taken one it is retried from it; with only a legacy
        // archive (owned by no run) there is nothing safe to run, which the engine would only mark Failed.
        bool engineTakenSnapshot = await ExecutionResumability.HasEngineTakenSnapshotAsync(_db, execution, ct);
        if (systemKind == WorkflowDefinitionKind.ExecutionSnapshot && !engineTakenSnapshot)
            return Conflict(new { code = ExecutionResumability.RetryUnavailableCode, message = ExecutionResumability.RetryUnavailableMessage });
        if (systemKind == WorkflowDefinitionKind.GraphicDesign)
            return Conflict(new { code = "GRAPHIC_RETRY_NOT_SUPPORTED", message = "Ask for the graphic again from the picture editor instead." });
        // An object analysis is a paid vendor run whose request row carries the intent: ask again from the editor.
        if (systemKind == WorkflowDefinitionKind.ObjectAnalysis)
            return Conflict(new { code = "REVISION_RETRY_NOT_SUPPORTED", message = "Find the object again from the editor instead." });
        if (systemKind == WorkflowDefinitionKind.AudioCleanup)
            return Conflict(new { code = "AUDIO_CLEANUP_RETRY_NOT_SUPPORTED", message = "Clean the audio again from the editor instead." });
        // A video enhancement is a paid hosted job behind its own route and consent: ask again from the editor.
        if (systemKind == WorkflowDefinitionKind.VideoEnhance)
            return Conflict(new { code = "ENHANCE_RETRY_NOT_SUPPORTED", message = "Enhance the video again from the editor instead." });
        // An avatar build's run belongs to its build job, which holds the money, the media permission and the files: ask again
        // from the Avatars tab.
        if (systemKind == WorkflowDefinitionKind.AvatarBuild)
            return Conflict(new { code = "AVATAR_BUILD_RETRY_NOT_SUPPORTED", message = "Ask for the avatar again from the Avatars tab instead." });
        // A clip with avatars is a paid vendor run behind its own route, the project's media consent and the paid run rate:
        // ask again from the editor's media pool.
        if (systemKind == WorkflowDefinitionKind.SubjectClip)
            return Conflict(new { code = "SUBJECT_CLIP_RETRY_NOT_SUPPORTED", message = "Generate the clip again from the editor instead." });
        // A timeline render is deterministic and spends nothing but compute: retrying it is the same
        // as pressing Render again, so it is the one system kind the retry endpoint lets through.
        if (systemKind is not null && systemKind != WorkflowDefinitionKind.TimelineRender && systemKind != WorkflowDefinitionKind.ExecutionSnapshot)
            return Conflict(new { code = "REVISION_RETRY_NOT_SUPPORTED", message = "Request the change again from the original video instead." });

        // QA T-102: a stale v1 copy of the profile template is refused before anything is resolved, preflighted or
        // held (the shared run-start gate's check).
        if (await RunStartGate.RefuseStaleProfileAsync(projectId, id, ct) is { } outdated) return outdated;

        // Optional: you can constrain retries only to Failed or Stopped executions
        if (execution.Status == ExecutionStatus.Running || execution.Status == ExecutionStatus.Queued)
        {
            return BadRequest(new { code = "RUN_ALREADY_ACTIVE", message = "This run is already running or queued." });
        }

        // S-49, S-50: while the previous run's lease is live, whatever the status, that run may still be
        // finishing its current step. A retry now would start a second run of the same execution.
        DateTime now = DateTime.UtcNow;
        if (execution.LeaseExpiresAt is { } leaseExpiresAt && leaseExpiresAt > now)
        {
            _logger.LogInformation(
                "Workflow execution retry refused while the previous run still holds its lease (ExecutionId={ExecutionId}, Status={Status}, LeaseOwner={LeaseOwner}, LeaseExpiresAt={LeaseExpiresAt:O})",
                executionId, execution.Status, execution.LeaseOwner, leaseExpiresAt);
            return Conflict(new { code = "RUN_STILL_STOPPING", message = "This run is still stopping. Try again in a moment." });
        }

        // T-004: the plan gates, the estimate gate (a retry of the profile-build template re-spends the fallback)
        // and the 402, in Execute's order, through the shared run-start gate. The
        // full estimate, over the steps the retry will run: the run's own engine-taken snapshot, or, for a run
        // without one (a legacy run, or one that never reached a step), the workflow the engine snapshots at start.
        Guid pricedDefinitionId = engineTakenSnapshot ? execution.SnapshotDefinitionId!.Value : id;
        List<WorkflowStep> retrySteps = await _db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == pricedDefinitionId)
            .ToListAsync(ct);
        // docs/design/app-inputs.md 6.2: the run's stored app-input values, re-checked against the steps that will run
        // (a 400 that costs nothing, before the plan and credit gates). Off, or an app with no inputs: nothing is read.
        (ActionResult? badInputs, AppInputGateResult? inputs) =
            await RefuseAppInputsAsync(projectId, id, retrySteps, engineTakenSnapshot ? execution.SnapshotDefinitionId : null, execution.RunBriefJson, ct);
        if (badInputs is not null)
            return badInputs;
        // S-L2: the key this retry charges (the caller's, or the row's only while still usable).
        if (await RefuseRevokedKeyRestartAsync(execution, ct) is { } revoked) return revoked;
        Guid? restartKeyId = await RestartApiKeyAsync(execution, ct);
        if (await RunStartGate.RefuseAsync(project, id, execution.OutputResolution, RunStartPricing.AllOf(retrySteps) with { AgentModels = inputs?.AgentModels, StepModels = inputs?.StepModels }, request?.AcceptEstimateCredits, ct,
                new RunStartRun(execution.Id, execution.SnapshotDefinitionId), WorkflowLintGate.BriefFromStored(execution.RunBriefJson),
                // A run started by an API key keeps spending against that key's caps when anyone retries or resumes it (null for a dashboard run).
                apiKeyId: restartKeyId, runHasPace: DynamicsPlannerPricing.HasPaceStored(execution.RunBriefJson, execution.StyleConfigJson)) is { } refused)
            return refused;

        _logger.LogInformation(
            "Workflow execution retry requested via API (ProjectId={ProjectId}, WorkflowId={WorkflowId}, ExecutionId={ExecutionId})",
            projectId,
            id,
            executionId);

        // The re-queue is one conditional UPDATE on relational providers: it also requires the lease to have
        // lapsed, so a renewal that lands after the check above is never nulled. The InMemory tests have no
        // concurrent writer, so the checks above are their guard.
        bool relational = _db.Database.IsRelational();
        string? userRequest = request?.UserRequest ?? execution.UserRequest;
        int requeued;
        if (relational)
        {
            // the re-queue and the superseding of the attempt's results are one transaction, so a
            // retry is never queued over live-looking old results nor supersedes a run it did not queue.
            requeued = await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                // SQFU-S6: serialized with a delete of the workflow (which holds this row FOR UPDATE).
                await ExecutionResumability.LockDefinitionForShareAsync(_db, id, ct);
                int updated = await _db.WorkflowExecutions
                    .Where(e => e.Id == executionId && e.ProjectId == projectId && e.WorkflowDefinitionId == id
                        && e.Status != ExecutionStatus.Running && e.Status != ExecutionStatus.Queued
                        && (e.LeaseExpiresAt == null || e.LeaseExpiresAt <= now))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.Status, ExecutionStatus.Queued)
                        .SetProperty(e => e.QueuedAt, (DateTime?)now)
                        .SetProperty(e => e.LeaseOwner, (string?)null)
                        .SetProperty(e => e.LeaseExpiresAt, (DateTime?)null)
                        .SetProperty(e => e.HeartbeatAt, (DateTime?)null)
                        .SetProperty(e => e.ErrorMessage, (string?)null)
                        .SetProperty(e => e.StartedAt, (DateTime?)null)
                        .SetProperty(e => e.CompletedAt, (DateTime?)null)
                        .SetProperty(e => e.PendingResume, false)
                        .SetProperty(e => e.ApiKeyId, restartKeyId)
                        .SetProperty(e => e.UserRequest, userRequest), ct);
                if (updated > 0)
                {
                    // Replaced, never deleted: superseded rows stay for audit and billing (their credit keys are
                    // their own ids, so nothing re-settles) and are ignored by resume, the live view and lineage.
                    await _db.WorkflowStepResults
                        .Where(r => r.WorkflowExecutionId == executionId && r.SupersededAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(r => r.SupersededAt, (DateTime?)now), ct);
                }

                await transaction.CommitAsync(ct);
                return updated;
            });
        }
        else
        {
            requeued = 1;
        }

        if (requeued == 0)
        {
            // Lost to a renewed lease, another resume or retry, a delete of the workflow or of the run: a fresh read
            // says which, so the answer is accurate (RUN_STILL_STOPPING only for the lease).
            _logger.LogInformation(
                "Workflow execution retry lost a race; not re-queued (ExecutionId={ExecutionId})",
                executionId);
            return await ExecutionResumability.LostRequeueAsync(
                _db, executionId, projectId, id, now, ExecutionResumability.RunChangedCode, ct);
        }

        // The response mapping and the publish read the tracked entity, so it takes the same values.
        execution.Status = ExecutionStatus.Queued;
        execution.QueuedAt = now;
        execution.LeaseOwner = null;
        execution.LeaseExpiresAt = null;
        execution.HeartbeatAt = null;
        execution.ErrorMessage = null;
        execution.StartedAt = null;
        execution.CompletedAt = null;
        execution.PendingResume = false;
        execution.ApiKeyId = restartKeyId;
        execution.UserRequest = userRequest;
        foreach (WorkflowStepResult replaced in execution.StepResults.Where(r => r.SupersededAt == null))
        {
            replaced.SupersededAt = now;
        }

        if (!relational)
        {
            await _db.SaveChangesAsync(ct);
        }

        await PublishRunRequestAsync(execution, id, projectId, request?.RegenerateVideoClips ?? false, "retry", ct);

        return StatusCode(200, MapExecutionResponse(execution));
    }

    /// <summary>
    /// T-004: continues a Failed or Cancelled run from its first missing result, on the same row (plan decisions
    /// 5-7). Gates, in order: email verification and Edit on the project (foreign 404, no Edit 403) → the run is
    /// Failed/Cancelled (409 <c>RUN_NOT_RESUMABLE</c>) → it has an engine-taken snapshot with its agents recorded
    /// (409 <c>RESUME_UNAVAILABLE</c>: a legacy run, use Retry) → its lease has lapsed (409 <c>RUN_STILL_STOPPING</c>)
    /// → the plan's resolution cap (402 <c>PLAN_NOT_ENTITLED</c>) → [estimate gate] → the credit preflight over the steps
    /// from the frontier on (402 <c>INSUFFICIENT_CREDITS</c>) → one conditional UPDATE, so of two concurrent resumes
    /// one queues and the other gets 409 → <see cref="WorkflowExecutionRequested"/> → 202 with the run.
    /// </summary>
    /// <remarks>
    /// The intent lives on the row (<c>pending_resume</c>): the engine resumes any delivery of it, including the
    /// sweeper's re-publish of a Queued row. A resume keeps the run's StartedAt (the engine leaves it), its request,
    /// style, format, resolution and design-system pin.
    /// </remarks>
    [HttpPost("{id:guid}/executions/{executionId:guid}/resume")]
    public async Task<ActionResult<WorkflowExecutionResponse>> ResumeExecution(
        Guid projectId, Guid id,
        Guid executionId,
        [FromBody] ResumeExecutionRequest? request,
        CancellationToken ct)
    {
        RunStartAuthorization authority = await RunStartGate.AuthorizeAsync(_projectAccess, projectId, ct);
        if (authority.Failure is { } denied) return denied;
        Project project = authority.Project!;

        // Scoped to the route's project and workflow, exactly as Retry.
        WorkflowExecution? execution = await _db.WorkflowExecutions
            .Include(e => e.StepResults)
            .Include(e => e.ReviewScores)
            .FirstOrDefaultAsync(e => e.Id == executionId && e.WorkflowDefinitionId == id && e.ProjectId == projectId, ct);
        if (execution == null) return NotFound();
        // An output that is not served cannot be restarted and watched: refused before any write or gate.
        if (await OutputGate.IsExecutionHeldAsync(_db, execution, ct)) return OutputGate.Unavailable();

        // A hidden object analysis is a paid vendor run behind a consent and its request row: resuming it would bypass a
        // withdrawn consent, the request row and the hourly limit, so it is refused exactly like Retry.
        if (await _db.WorkflowDefinitions.AsNoTracking()
                .AnyAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.ObjectAnalysis, ct))
            return Conflict(new { code = "OBJECT_RESUME_NOT_SUPPORTED", message = "Find the object again from the editor instead." });

        // A hidden audio cleanup is a paid hosted job behind its own request route and hourly limit: resuming it would
        // re-run it without either, so it is refused exactly like Retry.
        if (await _db.WorkflowDefinitions.AsNoTracking()
                .AnyAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.AudioCleanup, ct))
            return Conflict(new { code = "AUDIO_CLEANUP_RESUME_NOT_SUPPORTED", message = "Clean the audio again from the editor instead." });

        // A hidden video enhancement is a paid hosted job behind its own request route, the project consent and its run limits:
        // resuming it would re-run it without any of them, so it is refused exactly like Retry.
        if (await _db.WorkflowDefinitions.AsNoTracking()
                .AnyAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.VideoEnhance, ct))
            return Conflict(new { code = "ENHANCE_RESUME_NOT_SUPPORTED", message = "Enhance the video again from the editor instead." });

        // A hidden avatar build run is driven by its build job (its hold, its media permission, its files): resuming it here
        // would run it without the job, so it is refused exactly like Retry.
        if (await _db.WorkflowDefinitions.AsNoTracking()
                .AnyAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.AvatarBuild, ct))
            return Conflict(new { code = "AVATAR_BUILD_RESUME_NOT_SUPPORTED", message = "Ask for the avatar again from the Avatars tab instead." });

        // A hidden clip with avatars is a paid vendor run behind its own route, the media consent and the paid run rate:
        // resuming it would re-run it without them, so it is refused exactly like Retry.
        if (await _db.WorkflowDefinitions.AsNoTracking()
                .AnyAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.SubjectClip, ct))
            return Conflict(new { code = "SUBJECT_CLIP_RESUME_NOT_SUPPORTED", message = "Generate the clip again from the editor instead." });

        // QA T-102, as Execute and Retry: a stale v1 profile copy is refused before anything is resolved or priced.
        if (await RunStartGate.RefuseStaleProfileAsync(projectId, id, ct) is { } outdated) return outdated;

        if (!ExecutionResumability.HasResumableStatus(execution.Status))
            return Conflict(new { code = ExecutionResumability.NotResumableCode, message = ExecutionResumability.NotResumableMessage });

        // A change request or composition revision (a replay run) is asked for again, never resumed: resuming would
        // copy its reused steps a second time and run the revised step again, as Retry already refuses.
        if (await _db.WorkflowDefinitions.AsNoTracking()
                .AnyAsync(w => w.Id == id && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.CompositionReplay, ct))
            return Conflict(new { code = ExecutionResumability.ReplayResumeCode, message = ExecutionResumability.ReplayResumeMessage });

        // A kind-5 row owned by no run is a T-002 archive of a legacy run's steps: never resumed from.
        if (execution.AgentSnapshotJson is null || !await ExecutionResumability.HasEngineTakenSnapshotAsync(_db, execution, ct))
            return Conflict(new { code = ExecutionResumability.UnavailableCode, message = ExecutionResumability.UnavailableMessage });

        // S-49, S-50 (as Retry): while the previous run's lease is live it may still be finishing its step.
        DateTime now = DateTime.UtcNow;
        if (execution.LeaseExpiresAt is { } leaseExpiresAt && leaseExpiresAt > now)
            return Conflict(new { code = "RUN_STILL_STOPPING", message = "This run is still stopping. Try again in a moment." });

        // Priced from the frontier on (ResumeEstimateFrontier: an approximation of the engine's cursor).
        Guid snapshotId = execution.SnapshotDefinitionId!.Value;
        List<WorkflowStep> snapshotSteps = await _db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == snapshotId)
            .ToListAsync(ct);
        IReadOnlyCollection<int> charged = ResumeEstimateFrontier.ChargedStepOrders(snapshotSteps, execution.StepResults);
        // docs/design/app-inputs.md 6.2, as Retry: the stored values against the run's own snapshot.
        (ActionResult? badInputs, AppInputGateResult? inputs) =
            await RefuseAppInputsAsync(projectId, id, snapshotSteps, snapshotId, execution.RunBriefJson, ct);
        if (badInputs is not null)
            return badInputs;
        // S-L2: the key this resume charges (the caller's, or the row's only while still usable).
        if (await RefuseRevokedKeyRestartAsync(execution, ct) is { } revoked) return revoked;
        Guid? restartKeyId = await RestartApiKeyAsync(execution, ct);
        if (await RunStartGate.RefuseAsync(project, id, execution.OutputResolution, RunStartPricing.Charged(snapshotSteps, charged) with { AgentModels = inputs?.AgentModels, StepModels = inputs?.StepModels }, request?.AcceptEstimateCredits, ct,
                new RunStartRun(execution.Id, execution.SnapshotDefinitionId), WorkflowLintGate.BriefFromStored(execution.RunBriefJson),
                // A run started by an API key keeps spending against that key's caps when anyone retries or resumes it (null for a dashboard run).
                apiKeyId: restartKeyId, runHasPace: DynamicsPlannerPricing.HasPaceStored(execution.RunBriefJson, execution.StyleConfigJson)) is { } refused)
            return refused;

        _logger.LogInformation(
            "Workflow execution resume requested via API (ProjectId={ProjectId}, WorkflowId={WorkflowId}, ExecutionId={ExecutionId}, ChargedSteps={ChargedSteps})",
            projectId, id, executionId, charged.Count);

        // One conditional UPDATE: of two concurrent resumes (or a resume and a retry) exactly one moves the row,
        // and a lease renewed after the check above is never nulled. InMemory has no concurrent writer.
        bool relational = _db.Database.IsRelational();
        Task<int> RequeueAsync() => _db.WorkflowExecutions
            .Where(e => e.Id == executionId && e.ProjectId == projectId && e.WorkflowDefinitionId == id
                && (e.Status == ExecutionStatus.Failed || e.Status == ExecutionStatus.Cancelled)
                && (e.LeaseExpiresAt == null || e.LeaseExpiresAt <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExecutionStatus.Queued)
                .SetProperty(e => e.QueuedAt, (DateTime?)now)
                .SetProperty(e => e.PendingResume, true)
                .SetProperty(e => e.ApiKeyId, restartKeyId)
                .SetProperty(e => e.ResumeCount, e => e.ResumeCount + 1)
                .SetProperty(e => e.ErrorMessage, (string?)null)
                .SetProperty(e => e.CompletedAt, (DateTime?)null)
                .SetProperty(e => e.LeaseOwner, (string?)null)
                .SetProperty(e => e.LeaseExpiresAt, (DateTime?)null)
                .SetProperty(e => e.HeartbeatAt, (DateTime?)null), ct);

        // SQFU-S6: on Postgres, in one transaction behind a FOR SHARE lock on the workflow row, so a concurrent
        // delete of the workflow and this re-queue serialize. Elsewhere there is no such lock to hold and the single
        // conditional UPDATE is atomic by itself (SQLite serializes writers).
        int queued;
        if (_db.Database.IsNpgsql())
        {
            queued = await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await _db.Database.BeginTransactionAsync(ct);
                await ExecutionResumability.LockDefinitionForShareAsync(_db, id, ct);
                int updated = await RequeueAsync();
                await transaction.CommitAsync(ct);
                return updated;
            });
        }
        else
        {
            queued = relational ? await RequeueAsync() : 1;
        }

        if (queued == 0)
        {
            // As Retry: a fresh read says what changed (the row may also be gone: a 409, never a 500).
            _logger.LogInformation(
                "Workflow execution resume lost a race; not re-queued (ExecutionId={ExecutionId})",
                executionId);
            return await ExecutionResumability.LostRequeueAsync(
                _db, executionId, projectId, id, now, ExecutionResumability.NotResumableCode, ct);
        }

        // The response and the publish read the tracked entity, so it takes the same values. StartedAt is kept.
        execution.Status = ExecutionStatus.Queued;
        execution.QueuedAt = now;
        execution.PendingResume = true;
        execution.ApiKeyId = restartKeyId;
        execution.ResumeCount += 1;
        execution.ErrorMessage = null;
        execution.CompletedAt = null;
        execution.LeaseOwner = null;
        execution.LeaseExpiresAt = null;
        execution.HeartbeatAt = null;
        if (!relational)
        {
            await _db.SaveChangesAsync(ct);
        }

        await PublishRunRequestAsync(execution, id, projectId, regenerateVideoClips: false, "resume", ct);

        return StatusCode(202, MapExecutionResponse(execution));
    }

    /// <summary>
    /// Publishes the <see cref="WorkflowExecutionRequested"/> of a re-queued run (Retry, Resume): the same row, its
    /// own request, style, format, resolution and design-system pin. B8c: it re-enters the SAME queue as the
    /// original run, stamped with the plan's priority read fresh, so a plan change since the run is honoured.
    /// </summary>
    private async Task PublishRunRequestAsync(
        WorkflowExecution execution, Guid workflowId, Guid projectId, bool regenerateVideoClips, string kind, CancellationToken ct)
    {
        int runPriority = await ResolveRunPriorityAsync(execution.OrganizationId, ct);

        WorkflowExecutionRequested runRequest = new()
        {
            ExecutionId = execution.Id,
            WorkflowDefinitionId = workflowId,
            ProjectId = projectId,
            OrganizationId = execution.OrganizationId,
            InitiatedByUserId = _currentUser.UserId,
            CorrelationId = execution.CorrelationId ?? Guid.NewGuid().ToString(),
            RequestedAt = DateTime.UtcNow,
            UserRequest = execution.UserRequest,
            RegenerateVideoClips = regenerateVideoClips,
            // The run's own style, format and resolution. The resolution passed the plan gate above; the engine
            // still clamps it to the plan at run time.
            StyleConfigJson = execution.StyleConfigJson,
            RunBriefJson = execution.RunBriefJson,
            DesignSystemPinJson = execution.DesignSystemPinJson,
            OutputFormat = execution.OutputFormatOverride?.ToString(),
            OutputResolution = execution.OutputResolution,
            // T-015: a retry re-queues the SAME row, whose admitted bucket is kept (the engine re-checks it from the row);
            // the message copy is a hint for logs.
            AdmittedRunRateBucket = execution.AdmittedRunRateBucket
        };

        // Stamped only when the plan actually grants a priority class.
        if (RunPriority.AmqpPriorityToStamp(runPriority) is { } amqpPriority)
        {
            await _publishEndpoint.Publish(runRequest, ctx => ctx.SetPriority(amqpPriority), ct);
        }
        else
        {
            await _publishEndpoint.Publish(runRequest, ct);
        }

        _logger.LogInformation(
            "Workflow execution {Kind} request published to bus (ExecutionId={ExecutionId}, RunPriority={RunPriority})",
            kind,
            execution.Id,
            runPriority);
    }

    [HttpGet("{id:guid}/executions")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<List<WorkflowExecutionResponse>>> ListExecutions(Guid projectId, Guid id, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowDefinition? workflow = await _db.WorkflowDefinitions.FirstOrDefaultAsync(
            w => w.Id == id && w.ProjectId == projectId && w.Kind != WorkflowDefinitionKind.ExecutionSnapshot, ct);
        if (workflow == null) return NotFound();

        // The run-history list, not the run. Every run's step results used to come back whole —
        // input, output, tool calls, reasoning and room transcripts — which measured 9.8 MB for one
        // workflow's history, refetched on every focus of the page. The list's readers (run history,
        // run numbering, the duration estimate, the "Ran on your machine" chip) need only the run's
        // own fields and each step's status, timing and compute target, so only those columns are
        // read. The full run is GetExecution.
        var rows = await _db.WorkflowExecutions
            .Where(e => e.WorkflowDefinitionId == id && e.ProjectId == projectId)
            .OrderByDescending(e => e.StartedAt)
            .Select(e => new
            {
                e.Id,
                e.WorkflowDefinitionId,
                e.Status,
                e.StartedAt,
                e.FirstStartedAt,
                e.CompletedAt,
                e.IterationCount,
                e.CorrelationId,
                e.ErrorMessage,
                e.UserRequest,
                e.StyleConfigJson,
                e.RunBriefJson,
                e.AttentionJson,
                e.DesignSystemPinJson,
                e.OutputFormatOverride,
                e.OutputResolution,
                e.SnapshotDefinitionId,
                HasAgentSnapshot = e.AgentSnapshotJson != null,
                e.ResumeCount,
                // A retry's replaced results stay for audit and billing, not for the run's view.
                Steps = e.StepResults.Where(r => r.SupersededAt == null).Select(r => new
                {
                    r.Id,
                    r.WorkflowStepId,
                    r.TokensUsed,
                    r.DurationMs,
                    r.ExecutedAt,
                    r.Status,
                    r.IterationNumber,
                    r.CompletedAt,
                    r.OutputStorageKey,
                    r.ArtifactStorageKey,
                    r.FromCache,
                    r.CachedTokensSaved,
                    r.ComputeTarget,
                    r.FromRunner,
                    r.RunnerId,
                    r.RunnerJobId,
                    r.ReusedFromStepResultId,
                    r.CreditsChargedMillis,
                    r.FailureCode,
                    r.FailureClass,
                }).ToList(),
                Scores = e.ReviewScores.Select(rs => new { rs.Id, rs.IterationNumber, rs.Score, rs.CreatedAt, rs.StepResultId }).ToList(),
                // Every completed row, superseded ones included: what ReviewScoreLineage ties each score to.
                Completions = e.StepResults.Where(r => r.CompletedAt != null)
                    .Select(r => new { r.Id, CompletedAt = r.CompletedAt!.Value, Superseded = r.SupersededAt != null }).ToList(),
            })
            .AsSplitQuery()
            .ToListAsync(ct);

        // Which step each result belongs to is one small read of this workflow's steps and of the runs' own
        // snapshots' steps (a snapshotted run's results reference the snapshot's rows), kept out of the
        // per-result projection so a result whose step row was removed still lists.
        List<Guid> stepDefinitionIds = [id, .. rows.Where(e => e.SnapshotDefinitionId is not null).Select(e => e.SnapshotDefinitionId!.Value).Distinct()];
        Dictionary<Guid, WorkflowStep> stepsById = await _db.WorkflowSteps.AsNoTracking()
            .Where(s => stepDefinitionIds.Contains(s.WorkflowDefinitionId))
            .ToDictionaryAsync(s => s.Id, ct);

        // T-004: canResume needs the snapshot's owner, read in one query for the runs that could resume.
        IReadOnlySet<Guid> engineTaken = await ExecutionResumability.EngineTakenAsync(_db,
            [.. rows.Where(e => e.SnapshotDefinitionId is not null && e.HasAgentSnapshot && ExecutionResumability.HasResumableStatus(e.Status))
                .Select(e => (e.Id, e.SnapshotDefinitionId!.Value))], ct);

        // A replay run (revision / change request) cannot be resumed: same predicate as the run detail.
        List<Guid> rowDefinitionIds = [.. rows.Select(e => e.WorkflowDefinitionId).Distinct()];
        HashSet<Guid> replayDefinitions = [.. await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => rowDefinitionIds.Contains(w.Id) && w.Kind == WorkflowDefinitionKind.CompositionReplay)
            .Select(w => w.Id).ToListAsync(ct)];

        // A result whose output is not served shows no storage key; its row stays.
        ReelBolt.Shared.Storage.ProjectOutputHoldState outputHold = await ReelBolt.Shared.Storage.ProjectOutputHold.ReadAsync(_db, projectId, ct);

        // App inputs (docs/design/app-inputs.md 10.4): each run's fields in words, by its own page's labels.
        IReadOnlyDictionary<Guid, IReadOnlyList<RunAppInputResponse>> appInputs = await RunAppInputsAsync(
            [.. rows.Select(e => (e.Id, e.SnapshotDefinitionId, e.WorkflowDefinitionId, e.RunBriefJson))], ct);

        return Ok(rows.Select(e => new WorkflowExecutionResponse(
            e.Id, e.WorkflowDefinitionId, e.Status.ToString(),
            e.StartedAt, e.CompletedAt, e.IterationCount,
            ResultJson: null, e.CorrelationId, e.ErrorMessage,
            e.Steps.OrderBy(r => r.ExecutedAt).Select(r =>
                new StepResultResponse(r.Id, r.WorkflowStepId, Output: string.Empty, r.TokensUsed, r.DurationMs, r.ExecutedAt,
                    Status: r.Status.ToString(), IterationNumber: r.IterationNumber, CompletedAt: r.CompletedAt,
                    OutputStorageKey: outputHold.IsActive && outputHold.HoldsOutput(r.ExecutedAt, ReelBolt.Shared.Storage.ProjectOutputHold.RunStart(e.FirstStartedAt, e.StartedAt)) ? null : r.OutputStorageKey,
                    ArtifactStorageKey: outputHold.IsActive && outputHold.HoldsOutput(r.ExecutedAt, ReelBolt.Shared.Storage.ProjectOutputHold.RunStart(e.FirstStartedAt, e.StartedAt)) ? null : r.ArtifactStorageKey,
                    FromCache: r.FromCache, CachedTokensSaved: r.CachedTokensSaved,
                    ComputeTarget: r.ComputeTarget.ToString(), FromRunner: r.FromRunner, RunnerId: r.RunnerId,
                    RunnerJobId: r.RunnerJobId, ReusedFromStepResultId: r.ReusedFromStepResultId,
                    CreditsCharged: StepCredits(r.Status, r.CompletedAt, r.CreditsChargedMillis),
                    FailureCode: r.FailureCode, FailureClass: r.FailureClass.ToString(),
                    StepOrder: stepsById.GetValueOrDefault(r.WorkflowStepId)?.StepOrder,
                    StepLabel: stepsById.GetValueOrDefault(r.WorkflowStepId)?.Label,
                    StepType: stepsById.GetValueOrDefault(r.WorkflowStepId)?.StepType.ToString())
            ).ToList(),
            ReviewScoreLineage.Current(e.Scores, rs => rs.CreatedAt,
                    e.Completions.Select(c => new ReviewScoreLineage.Completion(c.CompletedAt, c.Superseded, c.Id)),
                    rs => rs.StepResultId)
                .OrderBy(rs => rs.IterationNumber).Select(rs =>
                new ReviewScoreResponse(rs.Id, rs.IterationNumber, rs.Score, Comments: string.Empty, rs.CreatedAt)
            ).ToList(),
            e.UserRequest,
            StyleJson.TryParseSnapshot(e.StyleConfigJson)?.Name,
            e.OutputFormatOverride?.ToString(),
            DesignSystemPinJson.TryParse(e.DesignSystemPinJson),
            ResolutionLabel(e.OutputResolution),
            CanResume: !replayDefinitions.Contains(e.WorkflowDefinitionId) && ExecutionResumability.CanResume(e.Status, engineTaken.Contains(e.Id), e.HasAgentSnapshot),
            ResumeCount: e.ResumeCount,
            Brief: RunBriefView.Compose(e.RunBriefJson, e.StyleConfigJson, e.OutputFormatOverride, e.OutputResolution),
            NeedsAttention: AttentionOf(e.Status, e.AttentionJson),
            AppInputs: appInputs.GetValueOrDefault(e.Id))).ToList());
    }

    /// <summary>
    /// The app fields runs were started with, in words (docs/design/app-inputs.md 10.4): labelled by each run's own page
    /// (its snapshot's copy; the workflow's page for a run without one), a model by name. Runs that set no app field are
    /// absent, and nothing is read for them.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<RunAppInputResponse>>> RunAppInputsAsync(
        IReadOnlyList<(Guid RunId, Guid? SnapshotId, Guid WorkflowId, string? BriefJson)> runs, CancellationToken ct)
    {
        Dictionary<Guid, IReadOnlyList<RunAppInputResponse>> result = [];
        var withInputs = runs
            .Select(r => (r.RunId, Definition: r.SnapshotId ?? r.WorkflowId, r.WorkflowId, Brief: ReelBolt.Shared.Briefs.RunBriefJson.TryParse(r.BriefJson)))
            .Select(r => (r.RunId, r.Definition, r.WorkflowId, r.Brief?.Inputs, By: r.Brief?.AllowMediaEgressBy))
            .Where(r => r.Inputs is { Count: > 0 })
            .ToList();
        if (withInputs.Count == 0)
            return result;

        List<Guid> definitionIds = [.. withInputs.SelectMany(r => new[] { r.Definition, r.WorkflowId }).Distinct()];
        Dictionary<Guid, string?> manifestJson = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => definitionIds.Contains(w.Id))
            .Select(w => new { w.Id, w.AppManifestJson })
            .ToDictionaryAsync(w => w.Id, w => w.AppManifestJson, ct);
        Dictionary<Guid, AppManifest?> manifests = manifestJson.ToDictionary(p => p.Key, p => AppManifestJson.TryParse(p.Value));
        AppManifest? PageOf(Guid definition, Guid workflow) =>
            manifests.GetValueOrDefault(definition) ?? manifests.GetValueOrDefault(workflow);

        IReadOnlyDictionary<Guid, string> modelNames = await AssistantAppInputs.ModelNamesAsync(_db,
            withInputs.SelectMany(r => RunAppInputView.ModelIds(PageOf(r.Definition, r.WorkflowId), r.Inputs)), ct);
        // "Allowed by API key <name>": the key a run was started with, read only for runs a key allowed.
        List<Guid> byKey = [.. withInputs.Where(r => r.By == MediaEgressPermission.ByApiKey).Select(r => r.RunId)];
        Dictionary<Guid, string> keyNames = byKey.Count == 0
            ? []
            : await (from e in _db.WorkflowExecutions.AsNoTracking()
                     join k in _db.ApiKeys.AsNoTracking() on e.ApiKeyId equals k.Id
                     where byKey.Contains(e.Id)
                     select new { e.Id, k.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        foreach (var run in withInputs)
        {
            if (RunAppInputView.Compose(PageOf(run.Definition, run.WorkflowId), run.Inputs, modelNames, run.By,
                    run.By == MediaEgressPermission.ByApiKey ? keyNames.GetValueOrDefault(run.RunId) : null) is { } rows)
                result[run.RunId] = rows;
        }

        return result;
    }

    [HttpGet("~/api/v1/projects/{projectId:guid}/executions/{executionId:guid}")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<WorkflowExecutionResponse>> GetExecution(Guid projectId, Guid executionId, CancellationToken ct, [FromQuery] string? detail = null)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;
        Project project = access.Project!;

        WorkflowExecution? execution = await _db.WorkflowExecutions
            .Include(e => e.StepResults)
            .ThenInclude(r => r.WorkflowStep)
            .Include(e => e.ReviewScores)
            .AsSplitQuery()
            .FirstOrDefaultAsync(e => e.Id == executionId && e.ProjectId == projectId, ct);
        if (execution == null) return NotFound();

        // The steps the run actually executed: its own snapshot's, which its results reference. Null for
        // a run without one (a hidden system workflow, or a run from before snapshots), whose results
        // still reference the workflow's own steps.
        List<WorkflowStepResponse>? runSteps = null;
        if (execution.SnapshotDefinitionId is { } snapshotId)
        {
            List<WorkflowStep> snapshotSteps = await _db.WorkflowSteps.AsNoTracking()
                .Where(s => s.WorkflowDefinitionId == snapshotId)
                .ToListAsync(ct);
            runSteps = MapWorkflowResponse(new WorkflowDefinition { Steps = snapshotSteps }).Steps;
        }

        bool canResume = ExecutionResumability.HasResumableStatus(execution.Status)
            && ExecutionResumability.CanResume(
                execution.Status,
                await ExecutionResumability.HasEngineTakenSnapshotAsync(_db, execution, ct),
                execution.AgentSnapshotJson is not null)
            && !await _db.WorkflowDefinitions.AsNoTracking()
                .AnyAsync(w => w.Id == execution.WorkflowDefinitionId && w.Kind == WorkflowDefinitionKind.CompositionReplay, ct);

        // The workflow the run is filed under: the source, or (once that was deleted) its hidden snapshot,
        // which carries the source's name.
        string? workflowName = await _db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == execution.WorkflowDefinitionId)
            .Select(w => w.Name)
            .FirstOrDefaultAsync(ct);

        IReadOnlyList<string> designWarnings = await DesignSystemRunWarnings.ForExecutionAsync(_db, execution, ct);

        bool summary = string.Equals(detail, "summary", StringComparison.OrdinalIgnoreCase);
        HashSet<Guid> unavailable = await OutputGate.HeldResultIdsAsync(
            _db, projectId, execution.StepResults.Select(r => r.Id).ToList(), ct);
        bool runUnavailable = await OutputGate.IsRunHeldAsync(_db, projectId, ReelBolt.Shared.Storage.ProjectOutputHold.RunStart(execution.FirstStartedAt, execution.StartedAt), ct);
        return Ok(MapExecutionResponse(execution, canResume, summary, unavailable, runUnavailable) with
        {
            Steps = runSteps,
            WorkflowName = workflowName,
            DesignSystemWarnings = designWarnings.Count > 0 ? designWarnings : null,
            AppInputs = (await RunAppInputsAsync(
                [(execution.Id, execution.SnapshotDefinitionId, execution.WorkflowDefinitionId, execution.RunBriefJson)], ct))
                .GetValueOrDefault(execution.Id),
        });
    }

    /// <summary>One step result in full, for a run read with <c>?detail=summary</c> that now needs the heavy fields.</summary>
    [HttpGet("~/api/v1/projects/{projectId:guid}/executions/{executionId:guid}/step-results/{resultId:guid}")]
    [Filters.ConditionalGet]
    public async Task<ActionResult<StepResultResponse>> GetStepResult(Guid projectId, Guid executionId, Guid resultId, CancellationToken ct)
    {
        ProjectAccessResult access = await _projectAccess.GetAsync(projectId, ProjectPermission.View, ct);
        if (access.Failure is { } denied) return denied;

        WorkflowStepResult? result = await _db.WorkflowStepResults.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == resultId
                && r.WorkflowExecutionId == executionId
                && r.WorkflowExecution!.ProjectId == projectId, ct);
        if (result == null) return NotFound();

        // Loaded on its own, not Included, so a result whose step row was removed still reads.
        result.WorkflowStep = (await _db.WorkflowSteps.AsNoTracking().FirstOrDefaultAsync(s => s.Id == result.WorkflowStepId, ct))!;
        bool resultUnavailable = await OutputGate.IsResultHeldAsync(_db, result.Id, ct);
        return Ok(MapStepResult(result, unavailable: resultUnavailable));
    }

    /// <summary>
    /// The steps of a save in the app-input shape, which the manifest validator checks the app's inputs against.
    /// </summary>
    private static IReadOnlyList<AppBindingStep> AppBindingStepsOf(IReadOnlyList<CreateWorkflowStepRequest> steps) =>
        StepConfigSaveValidator.BindingStepsOf(steps);

    /// <summary>
    /// The app-input check of a Retry or Resume (docs/design/app-inputs.md 6.2): the run's stored canonical values against
    /// the steps that will run and the manifest of the run's snapshot (else the workflow's). Null when the run may go on.
    /// </summary>
    private async Task<(ActionResult? Refusal, AppInputGateResult? Inputs)> RefuseAppInputsAsync(
        Guid projectId, Guid workflowId, IReadOnlyList<WorkflowStep> steps, Guid? snapshotDefinitionId, string? runBriefJson, CancellationToken ct)
    {
        // The permission to send media belongs to the run: its stored grant is re-bound whoever retries.
        ReelBolt.Shared.Briefs.StoredRunBrief? storedBrief = ReelBolt.Shared.Briefs.RunBriefJson.TryParse(runBriefJson);
        AppInputGateResult gate = await _appInputs.CheckAsync(
            _currentUser.OrganizationId, projectId, workflowId, steps, snapshotDefinitionId,
            storedBrief?.Inputs, notes: null, ct, mediaEgress: MediaEgressGate.FromStored(storedBrief));
        // The run's chosen agent and step models (re-checked here) price it as the engine runs it (8.5, 28).
        return gate.Refused ? (BadRequest(gate.ErrorBody()), null) : (null, gate);
    }

    /// <summary>
    /// Save-time step-config check: a 400 <c>{ message, errors }</c> when any step's config would
    /// only fail at run time (<see cref="StepConfigSaveValidator"/>) or names an inference provider
    /// this project's organization cannot use (A10, <see cref="StepProviderOwnershipValidator"/>),
    /// else null. Both are reported in ONE response so an author fixes every rejection in one pass.
    /// </summary>
    private async Task<BadRequestObjectResult?> InvalidStepConfigsAsync(
        Project project, IReadOnlyList<CreateWorkflowStepRequest> steps, CancellationToken ct)
    {
        List<string> errors = [.. StepConfigSaveValidator.Validate(steps)];
        errors.AddRange(await _providerOwnership.ValidateAsync(project.OrganizationId, project.Id, steps, ct));

        return errors.Count == 0
            ? null
            : BadRequest(new { message = StepConfigSaveValidator.FormatMessage(errors), errors });
    }

    /// <summary>
    /// A8: every step must name a built-in agent, or a custom agent belonging to the project's
    /// organization. Returns the rejection to send, or null when every step is usable.
    /// </summary>
    /// <remarks>
    /// Deliberately called BEFORE any write. <see cref="Update"/> clears the existing steps with
    /// <c>ExecuteDeleteAsync</c>, which runs immediately, so a check placed after it would destroy
    /// the stored steps on a request it then rejects.
    /// </remarks>
    private async Task<ObjectResult?> InvalidStepAgentsAsync(
        Project project, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct)
    {
        foreach (CreateWorkflowStepRequest stepReq in steps)
        {
            // The tenant query filter already scopes this lookup; the explicit OrganizationId
            // comparison is the second, independent enforcement.
            AgentDefinition? agent = await _db.AgentDefinitions
                .FirstOrDefaultAsync(a => a.Id == stepReq.AgentDefinitionId, ct);
            if (agent == null || (!agent.IsBuiltIn && agent.OrganizationId != project.OrganizationId))
                return BadRequest(new { message = "Invalid or unauthorized agent" });
        }

        return null;
    }

    /// <summary>Save-time plan gate (B8a): 402 <c>PLAN_NOT_ENTITLED</c> when the plan excludes a step type.</summary>
    private async Task<ObjectResult?> PlanRefusesStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct)
    {
        if (_planGate is null) return null;
        return (await _planGate.CheckStepsAsync(organizationId, steps, ct))?.ToResult();
    }

    private static WorkflowStep CreateStep(Guid workflowId, CreateWorkflowStepRequest req)
    {
        var step = new WorkflowStep
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflowId,
            AgentDefinitionId = req.AgentDefinitionId,
            StepOrder = req.StepOrder,
            EdgeConditionJson = req.EdgeConditionJson,
            Label = req.Label,
            ConditionExpression = req.ConditionExpression,
            LoopSourceExpression = req.LoopSourceExpression,
            LoopTargetStepOrder = req.LoopTargetStepOrder,
            MinScore = req.MinScore,
            InputMappingJson = req.InputMappingJson,
            SelectedPriorStepOrdersJson = req.SelectedPriorStepOrdersJson,
            TrueBranchStepOrder = req.TrueBranchStepOrder,
            FalseBranchStepOrder = req.FalseBranchStepOrder,
            ParallelAgentIdsJson = req.ParallelAgentIdsJson,
            ExtractConfigJson = req.ExtractConfigJson,
            VideoAnalyzeConfigJson = req.VideoAnalyzeConfigJson,
            VideoCompileConfigJson = req.VideoCompileConfigJson,
            EditRoomConfigJson = req.EditRoomConfigJson,
            GraphicsRoomConfigJson = req.GraphicsRoomConfigJson,
            ColorGradeRoomConfigJson = req.ColorGradeRoomConfigJson,
            ConditionalConfigJson = req.ConditionalConfigJson,
            ReviewLoopConfigJson = req.ReviewLoopConfigJson,
            VideoGenerateConfigJson = req.VideoGenerateConfigJson,
            VoiceoverConfigJson = req.VoiceoverConfigJson,
            ThumbnailGenerateConfigJson = req.ThumbnailGenerateConfigJson,
            DailiesRoomConfigJson = req.DailiesRoomConfigJson,
            TimelineRenderConfigJson = req.TimelineRenderConfigJson,
            DesignSystemConfigJson = req.DesignSystemConfigJson,
            ContinueCompositionJson = req.ContinueCompositionJson,
            RegisterOutputAsProjectFile = req.RegisterOutputAsProjectFile,
            ExtractFrameConfigJson = req.ExtractFrameConfigJson,
            AudioGenerateConfigJson = req.AudioGenerateConfigJson,
            PinInsertConfigJson = req.PinInsertConfigJson,
            AudioCleanupConfigJson = req.AudioCleanupConfigJson,
            VideoEnhanceConfigJson = req.VideoEnhanceConfigJson,
            ImageGenerateConfigJson = req.ImageGenerateConfigJson
        };

        if (req.StepType != null && Enum.TryParse<StepType>(req.StepType, out var stepType))
            step.StepType = stepType;

        if (req.AgentInputContextMode != null
            && Enum.TryParse<AgentInputContextMode>(req.AgentInputContextMode, out var mode))
            step.AgentInputContextMode = mode;

        if (req.MaxIterations.HasValue)
            step.MaxIterations = req.MaxIterations.Value;

        if (req.CacheMode != null && Enum.TryParse<StepCacheMode>(req.CacheMode, out var cacheMode))
            step.CacheMode = cacheMode;

        return step;
    }

    private static WorkflowDefinitionResponse MapWorkflowResponse(WorkflowDefinition workflow) =>
        new(workflow.Id, workflow.Name, workflow.CreatedAt, workflow.UpdatedAt,
            workflow.Steps.OrderBy(s => s.StepOrder).Select(s =>
                new WorkflowStepResponse(s.Id, s.AgentDefinitionId, s.StepOrder, s.EdgeConditionJson, s.Label,
                    s.StepType.ToString(), s.ConditionExpression, s.LoopSourceExpression,
                    s.LoopTargetStepOrder, s.MaxIterations, s.MinScore, s.InputMappingJson,
                    s.AgentInputContextMode?.ToString(), s.SelectedPriorStepOrdersJson,
                    s.TrueBranchStepOrder, s.FalseBranchStepOrder, s.ParallelAgentIdsJson,
                    s.ExtractConfigJson, s.VideoAnalyzeConfigJson, s.VideoCompileConfigJson, s.EditRoomConfigJson, s.GraphicsRoomConfigJson,
                        s.ColorGradeRoomConfigJson, s.ConditionalConfigJson, s.ReviewLoopConfigJson,
                        s.CacheMode?.ToString(), s.VideoGenerateConfigJson, s.VoiceoverConfigJson,
                        s.ThumbnailGenerateConfigJson, s.DailiesRoomConfigJson, s.TimelineRenderConfigJson, s.DesignSystemConfigJson,
                        s.ContinueCompositionJson, s.RegisterOutputAsProjectFile, s.ExtractFrameConfigJson, s.AudioGenerateConfigJson, s.PinInsertConfigJson, s.AudioCleanupConfigJson,
                        s.VideoEnhanceConfigJson, s.ImageGenerateConfigJson)
            ).ToList(),
            workflow.RequiresUserInput);

    /// <summary>One step result as the API shows it. <paramref name="summary"/> drops the heavy fields (see <see cref="SummarizeOutputJson"/>) and sets DetailOmitted.</summary>
    internal static StepResultResponse MapStepResult(WorkflowStepResult r, bool summary = false, bool unavailable = false) =>
        new(r.Id, r.WorkflowStepId, summary || unavailable ? string.Empty : Shared.Workflows.Review.ReviewEngineRecord.Without(r.Output), r.TokensUsed, r.DurationMs, r.ExecutedAt,
            summary || unavailable ? null : r.InputJson, unavailable ? null : summary ? SummarizeOutputJson(r.OutputJson) : r.OutputJson,
            r.Status.ToString(), r.ErrorDetails, r.IterationNumber, r.CompletedAt,
            unavailable ? null : r.OutputStorageKey, unavailable ? null : r.ArtifactStorageKey, summary || unavailable ? null : r.ToolCallsJson, summary || unavailable ? null : r.ReasoningJson,
            summary || unavailable ? null : r.ChatTranscriptJson,
            r.FromCache, r.CachedTokensSaved,
            r.ComputeTarget.ToString(), r.FromRunner, r.RunnerId, r.RunnerJobId, r.ReusedFromStepResultId,
            StepCredits(r.Status, r.CompletedAt, r.CreditsChargedMillis), r.FailureCode, r.FailureClass.ToString(),
            r.WorkflowStep?.StepOrder, r.WorkflowStep?.Label, r.WorkflowStep?.StepType.ToString(),
            DetailOmitted: summary);

    /// <summary>The summary view of OutputJson: null, unless it is an object carrying <c>warnings</c>, then only <c>{"warnings":[...]}</c>.</summary>
    internal static string? SummarizeOutputJson(string? outputJson)
    {
        if (string.IsNullOrWhiteSpace(outputJson)) return null;
        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(outputJson);
            if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                && doc.RootElement.TryGetProperty("warnings", out System.Text.Json.JsonElement warnings))
                return "{\"warnings\":" + warnings.GetRawText() + "}";
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON: nothing to keep in the summary.
        }
        return null;
    }

    /// <summary>The run as the API shows it. Results a retry superseded are left out.</summary>
    internal static WorkflowExecutionResponse MapExecutionResponse(WorkflowExecution execution, bool canResume = false, bool summary = false, IReadOnlySet<Guid>? unavailable = null, bool runUnavailable = false) =>
        new(execution.Id, execution.WorkflowDefinitionId, execution.Status.ToString(),
            execution.StartedAt, execution.CompletedAt, execution.IterationCount,
            runUnavailable || unavailable?.Count > 0 ? null : execution.ResultJson, execution.CorrelationId, execution.ErrorMessage,
            (execution.StepResults ?? Enumerable.Empty<WorkflowStepResult>()).Where(r => r.SupersededAt == null).OrderBy(r => r.ExecutedAt)
                .Select(r => MapStepResult(r, summary, unavailable?.Contains(r.Id) == true)).ToList(),
            // A score whose review dispatch was superseded (Retry, or a resume's re-run) is audit only, like its row.
            ReviewScoreLineage.Current(
                    execution.ReviewScores ?? Enumerable.Empty<ReviewScore>(), rs => rs.CreatedAt,
                    execution.StepResults ?? Enumerable.Empty<WorkflowStepResult>(), rs => rs.StepResultId)
                .OrderBy(rs => rs.IterationNumber).Select(rs =>
                new ReviewScoreResponse(rs.Id, rs.IterationNumber, rs.Score, rs.Comments, rs.CreatedAt)
            ).ToList(),
            execution.UserRequest,
            StyleJson.TryParseSnapshot(execution.StyleConfigJson)?.Name,
            execution.OutputFormatOverride?.ToString(),
            DesignSystemPinJson.TryParse(execution.DesignSystemPinJson),
            ResolutionLabel(execution.OutputResolution),
            CanResume: canResume,
            ResumeCount: execution.ResumeCount,
            Brief: RunBriefView.Compose(execution),
            NeedsAttention: AttentionOf(execution.Status, execution.AttentionJson));

    /// <summary>Needs-attention view; only a Passed run can carry one.</summary>
    private static ExecutionAttention? AttentionOf(ExecutionStatus status, string? attentionJson) =>
        status == ExecutionStatus.Passed ? ExecutionAttention.TryParse(attentionJson) : null;

    /// <summary>A step's cost in whole credits, or null while it has not finished and so has nothing recorded.</summary>
    private static decimal? StepCredits(StepStatus status, DateTime? completedAt, long millis) =>
        millis == 0 && completedAt is null && (status is StepStatus.Pending or StepStatus.Running) ? null : millis / 1000m;

    /// <summary>The wire label of a run's resolution (<c>2160p</c>), or null for Auto.</summary>
    private static string? ResolutionLabel(int? shortSide) => shortSide is { } s ? OutputResolution.Label(s) : null;

    /// <summary>
    /// The organization's queue-priority class for a re-published run (B8c). Best-effort, exactly like
    /// <c>WorkflowExecutionService</c>'s copy: a missing entitlement service or a failed read must
    /// never stop a retry from being queued, and <see cref="RunPriority.Standard"/> is the safe answer.
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
                "Could not read the plan priority of organization {OrganizationId}; queueing the retry at standard priority",
                organizationId);
            return RunPriority.Standard;
        }
    }
}
