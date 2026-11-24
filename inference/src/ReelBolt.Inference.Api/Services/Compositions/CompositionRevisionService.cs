using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.ChangeRequests;
using ReelBolt.Inference.Api.Services.Styles;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Compositions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Inference.Api.Services.Compositions;

/// <summary>
/// The request side of composition revisions (docs/design/composition-revisions.md, the first
/// front-end): resolves the lineage a run belongs to, answers whether it can be revised and what it
/// would cost, and turns an accepted request into a <see cref="CompositionRevision"/> row plus an
/// execution of the project's hidden revision workflow. Authorization is the CALLER's job — every
/// method takes the <see cref="Project"/> the controller already resolved through
/// <c>IProjectAccessService</c>, and every query here is scoped by that project's id, which is the
/// guard for these unfiltered child tables (<c>TenantBoundaryInventoryTests</c>).
/// </summary>
public interface ICompositionRevisionService
{
    /// <summary>The lineage of <paramref name="executionId"/>, or null when the run is not in <paramref name="project"/>.</summary>
    Task<CompositionRevisionHistoryResponse?> GetHistoryAsync(Project project, Guid executionId, Guid? organizationId, CancellationToken ct, Guid? sceneVersionId = null);

    /// <summary>Validate, rate-limit, preflight and queue a revision.</summary>
    /// <exception cref="PlanLimitException">When the plan's concurrency cap, or its output-resolution cap, refuses the run.</exception>
    /// <param name="initiator">Who asked: a person by default; the assistant or the MCP when a model did.</param>
    Task<CompositionRevisionRequestResult> RequestAsync(
        Project project, Guid executionId, Guid userId, Guid? organizationId, CreateCompositionRevisionRequest body, CancellationToken ct,
        ExecutionInitiator initiator = ExecutionInitiator.Human);
}

/// <summary>The outcome of a revision request: created, or a refusal the controller renders verbatim.</summary>
public sealed record CompositionRevisionRequestResult(
    CompositionRevisionResponse? Created,
    int StatusCode,
    string? Code,
    string? Message,
    IReadOnlyDictionary<string, object?>? Extra = null)
{
    public static CompositionRevisionRequestResult Ok(CompositionRevisionResponse created) => new(created, 201, null, null);

    public static CompositionRevisionRequestResult Refuse(int status, string? code, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        new(null, status, code, message, extra);
}

/// <summary>Request-side knobs (<c>CompositionRevisions:*</c>).</summary>
public sealed class CompositionRevisionOptions
{
    public const string SectionName = "CompositionRevisions";

    /// <summary>Revision requests one user may make per project in a rolling hour.</summary>
    public int MaxPerHourPerUserProject { get; set; } = 10;

    /// <summary>
    /// How long after a run its sandbox is assumed recoverable for a run made before capture existed.
    /// Mirrors the sandbox-executor's <c>SANDBOX_TTL</c> (7 days); an executor restart can end it sooner.
    /// </summary>
    public int SandboxRecoveryWindowDays { get; set; } = 7;
}

/// <inheritdoc />
public sealed class CompositionRevisionService : ICompositionRevisionService
{
    /// <summary>The name of the hidden per-project system workflow.</summary>
    public const string SystemWorkflowName = "Video revisions";

    public const string AvailabilitySnapshot = "Snapshot";
    public const string AvailabilityRecoverable = "Recoverable";
    public const string AvailabilityUnavailable = "Unavailable";

    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };

    private readonly InferenceApiDbContext _db;
    private readonly WorkflowExecutionService _executions;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly IPlanGateService? _planGate;
    private readonly IAmazonS3? _s3;
    private readonly string _bucket;
    private readonly CompositionRevisionOptions _options;
    private readonly ILogger<CompositionRevisionService> _logger;
    private readonly IAppInputModelResolverSource? _appModels;

    public CompositionRevisionService(
        InferenceApiDbContext db,
        WorkflowExecutionService executions,
        IConfiguration configuration,
        ILogger<CompositionRevisionService> logger,
        IWorkflowCostEstimator? costEstimator = null,
        IAmazonS3? s3 = null,
        IPlanGateService? planGate = null,
        IAppInputModelResolverSource? appModels = null)
    {
        _db = db;
        _appModels = appModels;
        _executions = executions;
        _costEstimator = costEstimator;
        _planGate = planGate;
        _s3 = s3;
        _bucket = configuration["MinIO:BucketName"] ?? "reelforge";
        _options = configuration.GetSection(CompositionRevisionOptions.SectionName).Get<CompositionRevisionOptions>()
                   ?? new CompositionRevisionOptions();
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // History
    // ------------------------------------------------------------------

    public async Task<CompositionRevisionHistoryResponse?> GetHistoryAsync(
        Project project, Guid executionId, Guid? organizationId, CancellationToken ct, Guid? sceneVersionId = null)
    {
        WorkflowExecution? execution = await LoadExecutionAsync(project.Id, executionId, ct);
        if (execution is null)
            return null;

        Lineage lineage = await ResolveLineageAsync(project.Id, execution, ct);
        List<CompositionWorkspaceVersion> versions = lineage.Workspace is null
            ? []
            : await _db.CompositionWorkspaceVersions.AsNoTracking()
                .Where(v => v.ProjectId == project.Id && v.WorkspaceId == lineage.Workspace.Id)
                .OrderBy(v => v.VersionNumber)
                .ToListAsync(ct);

        (string availability, string? reason) = await AvailabilityAsync(project.Id, execution, versions, ct);
        bool finished = IsTerminal(execution.Status);
        bool canRevise = availability != AvailabilityUnavailable && finished;
        if (availability != AvailabilityUnavailable && !finished)
            reason = "You can request changes once this run has finished.";
        // A run the request gate would refuse is not offered either (the page and the assistant read this flag).
        bool closed = finished && availability != AvailabilityUnavailable
            && await RunChangeGate.IsClosedAsync(_db, project.Id, execution, ct);
        if (closed)
        {
            canRevise = false;
            availability = AvailabilityUnavailable;
            reason = RunChangeGate.Message;
        }

        CompositionWorkspaceVersion? current = CurrentVersion(execution.Id, lineage.Revision, versions);
        // A change request with no version named builds on the head, not on this run's own version.
        CompositionWorkspaceVersion? head = await WorkspaceHeadAsync(versions, ct);
        // The scenes described are those of the version the change will apply to: the one named, else the head.
        CompositionWorkspaceVersion? sceneSource = (sceneVersionId is { } sv ? versions.FirstOrDefault(v => v.Id == sv) : null) ?? head ?? current;

        IReadOnlyList<RevisionSceneResponse> scenes = sceneSource?.SceneManifestJson is { } manifestJson
            ? Scenes(manifestJson)
            : availability == AvailabilityRecoverable
                ? Scenes(await TryReadSidecarAsync(project.Id, execution.Id, ct))
                : [];

        List<CompositionVersionResponse> versionResponses = await MapVersionsAsync(project.Id, versions, ct);
        List<CompositionRevisionResponse> revisions = lineage.Workspace is null
            ? []
            : await MapRevisionsAsync(project.Id, lineage.Workspace.Id, versions, ct);

        // What a revision of the current version would replay after re-rendering the film, and its price.
        RevisionReplayResponse? replay = null;
        if (availability != AvailabilityUnavailable)
        {
            (CompositionReplaySource? replaySource, string? replayReason) = await CompositionReplaySources.ResolveAsync(
                _db, project.Id, execution, head ?? current, versions, lineage.Workspace, ct);
            replay = await DescribeReplayAsync(organizationId, replaySource, replayReason, ct);
        }

        return new CompositionRevisionHistoryResponse(
            lineage.Workspace?.Id,
            availability,
            canRevise,
            availability == AvailabilityUnavailable || !finished ? reason : null,
            current?.Id,
            scenes,
            versionResponses,
            revisions,
            replay is { Available: true } ? replay.KeepCutEstimate : await EstimateAsync(organizationId, execution.OutputResolution, ct),
            replay,
            head?.Id,
            head?.VersionNumber);
    }

    // ------------------------------------------------------------------
    // Request
    // ------------------------------------------------------------------

    public async Task<CompositionRevisionRequestResult> RequestAsync(
        Project project, Guid executionId, Guid userId, Guid? organizationId, CreateCompositionRevisionRequest body, CancellationToken ct,
        ExecutionInitiator initiator = ExecutionInitiator.Human)
    {
        string instruction = (body.Instruction ?? string.Empty).Trim();
        List<string> errors = [];
        if (instruction.Length == 0)
            errors.Add("Say what should change.");
        if (instruction.Length > 4000)
            errors.Add("The request can be at most 4000 characters.");
        CompositionRevisionMode mode = CompositionRevisionMode.Render;
        if (!string.IsNullOrWhiteSpace(body.Mode) && !Enum.TryParse(body.Mode, ignoreCase: true, out mode))
            errors.Add("mode must be Render or PreviewOnly.");
        if (body.Targets is { } targets)
            errors.AddRange(targets.Validate());
        CompositionCutPolicy cutPolicy = CompositionCutPolicy.KeepCut;
        if (!string.IsNullOrWhiteSpace(body.CutPolicy) && !Enum.TryParse(body.CutPolicy, ignoreCase: true, out cutPolicy))
            errors.Add("cutPolicy must be KeepCut or ReEdit.");
        if (errors.Count > 0)
            return CompositionRevisionRequestResult.Refuse(400, null, string.Join(" ", errors));

        WorkflowExecution? execution = await LoadExecutionAsync(project.Id, executionId, ct);
        if (execution is null)
            return CompositionRevisionRequestResult.Refuse(404, null, "Run not found.");

        if (!IsTerminal(execution.Status))
            return CompositionRevisionRequestResult.Refuse(409, "RUN_NOT_FINISHED", "You can request changes once this run has finished.");

        if (await RunChangeGate.IsClosedAsync(_db, project.Id, execution, ct))
            return CompositionRevisionRequestResult.Refuse(RunChangeGate.StatusCode, RunChangeGate.Code, RunChangeGate.Message);

        // A restore of this project is rewriting it: no run starts until it is done (covers the replay start too).
        if (await RestoreRunGate.IsHeldAsync(_db, project.Id, ct))
            return CompositionRevisionRequestResult.Refuse(409, ReelBolt.Inference.Api.Services.History.ProjectHistoryRestoreCodes.RestoreInProgress, RestoreRunGate.Message);

        Lineage lineage = await ResolveLineageAsync(project.Id, execution, ct);
        List<CompositionWorkspaceVersion> versions = lineage.Workspace is null
            ? []
            : await _db.CompositionWorkspaceVersions.AsNoTracking()
                .Where(v => v.ProjectId == project.Id && v.WorkspaceId == lineage.Workspace.Id)
                .OrderBy(v => v.VersionNumber)
                .ToListAsync(ct);

        (string availability, string? reason) = await AvailabilityAsync(project.Id, execution, versions, ct);
        if (availability == AvailabilityUnavailable)
            return CompositionRevisionRequestResult.Refuse(409, "WORKSPACE_UNAVAILABLE", reason ?? "This video cannot be changed.");

        CompositionWorkspaceVersion? baseVersion;
        if (body.FromVersionId is { } fromVersionId)
        {
            baseVersion = versions.FirstOrDefault(v => v.Id == fromVersionId);
            if (baseVersion is null)
                return CompositionRevisionRequestResult.Refuse(404, null, "That version is not part of this video's history.");
        }
        else
        {
            // No version named: the change builds on the newest version of the video, whichever run the
            // owner is looking at. Basing it on this run's own (older) version would silently drop every
            // change made since. The replay source below follows from this version's creating run.
            baseVersion = await WorkspaceHeadAsync(versions, ct) ?? CurrentVersion(execution.Id, lineage.Revision, versions);
        }

        // Per user + project, rolling hour — counted from the request rows themselves, so the limit
        // holds across API replicas without shared memory. Change requests share the bucket.
        List<DateTime> recent = await RevisionRateWindow.RecentAsync(_db, project.Id, userId, ct);
        int limit = Math.Max(1, _options.MaxPerHourPerUserProject);
        if (RevisionRateWindow.Refusal(recent, limit) is { } rate)
            return CompositionRevisionRequestResult.Refuse(429, "REVISION_RATE_LIMITED", rate.Message, rate.Extra);

        // Replay (docs/design/composition-revisions.md "Replaying what depends on the film"): when steps of
        // the workflow depend on the film — an analysis, an edit, a compile — the revision runs them again
        // after re-rendering, so it ends with the workflow's real deliverable. A preview-only revision
        // renders nothing, so nothing downstream could change.
        if (mode == CompositionRevisionMode.Render)
        {
            (CompositionReplaySource? replaySource, _) = await CompositionReplaySources.ResolveAsync(
                _db, project.Id, execution, baseVersion, versions, lineage.Workspace, ct);
            CompositionReplayPlan? plan = replaySource?.Plan(cutPolicy);
            if (replaySource is not null && plan is { HasDownstreamWork: true })
            {
                return await RequestReplayAsync(
                    project, execution, userId, organizationId, lineage, baseVersion, replaySource, plan,
                    instruction, body.Targets, mode, ct, initiator);
            }
        }

        WorkflowDefinition? workflow = await EnsureSystemWorkflowAsync(project.Id, ct);
        if (workflow is null)
        {
            return CompositionRevisionRequestResult.Refuse(
                503, "REVISION_AGENT_UNAVAILABLE", "Changes cannot be made on this installation yet. Please try again later.");
        }

        // A revision inherits the run's resolution: an organization downgraded since that run is told
        // PLAN_NOT_ENTITLED before the credit preflight (never "top up" for a render its plan refuses).
        await ThrowIfPlanRefusesResolutionAsync(project, execution.OutputResolution, ct);

        // The same B7 preflight as every other execute path, against the step the run will hold for.
        WorkflowExecutionPreflightResult preflight = await new WorkflowExecutionPreflight(_db, _costEstimator)
            .CheckAsync(organizationId, project.Id, workflow.Id, execution.OutputResolution, ct);
        if (!preflight.Allowed && !preflight.WorkflowNotFound)
        {
            return CompositionRevisionRequestResult.Refuse(
                402, "INSUFFICIENT_CREDITS",
                CreditShortfallMessage.Build(preflight),
                new Dictionary<string, object?>
                {
                    ["required"] = preflight.Required,
                    ["balance"] = preflight.Balance,
                    ["missing"] = CreditShortfallMessage.MissingCredits(preflight),
                    ["low"] = preflight.Low,
                    ["high"] = preflight.High,
                    ["hasHistory"] = preflight.HasHistory
                });
        }

        CompositionWorkspace workspace = lineage.Workspace ?? await CreateOriginWorkspaceAsync(project, execution, ct);

        Guid revisionExecutionId = Guid.NewGuid();
        CompositionRevision revision = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            WorkspaceId = workspace.Id,
            SourceExecutionId = execution.Id,
            BaseVersionId = baseVersion?.Id,
            ExecutionId = revisionExecutionId,
            WorkflowDefinitionId = workflow.Id,
            Instruction = instruction,
            TargetsJson = CompositionRevisionTargets.Serialize(body.Targets),
            Mode = mode,
            RequestedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        // The request row lands BEFORE the run is queued, so the engine can never consume an execution
        // whose request it cannot find; a refused queue removes it again.
        _db.CompositionRevisions.Add(revision);
        await _db.SaveChangesAsync(ct);

        WorkflowExecutionResponse queued;
        try
        {
            queued = await _executions.QueueAsync(
                project.Id, workflow.Id, userId, instruction, initiator, ct,
                regenerateVideoClips: false, ResolutionOnly(execution), WorkflowDefinitionKind.CompositionRevision, revisionExecutionId);
        }
        catch
        {
            _db.CompositionRevisions.Remove(revision);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        _logger.LogInformation(
            "Composition revision {RevisionId} queued as execution {ExecutionId} (workspace {WorkspaceId}, base version {BaseVersionId}, source run {SourceExecutionId})",
            revision.Id, queued.Id, workspace.Id, baseVersion?.Id, execution.Id);

        return CompositionRevisionRequestResult.Ok(new CompositionRevisionResponse(
            revision.Id, revisionExecutionId, workflow.Id, ExecutionStatus.Queued.ToString(),
            revision.Instruction, CompositionRevisionTargets.Deserialize(revision.TargetsJson), revision.Mode.ToString(),
            baseVersion?.Id, baseVersion?.VersionNumber, null, null, revision.CreatedAt, null, null));
    }

    /// <summary>
    /// The plan's output-resolution cap for a revision's inherited resolution, checked before the credit
    /// preflight; <see cref="PlanLimitException"/> is the 402 PLAN_NOT_ENTITLED the controller renders.
    /// Auto (null) and billing off (no gate) are no check. QueueAsync re-checks it (defence in depth).
    /// </summary>
    private async Task ThrowIfPlanRefusesResolutionAsync(Project project, int? outputResolution, CancellationToken ct)
    {
        if (_planGate is not null
            && await _planGate.CheckResolutionAsync(project.OrganizationId, outputResolution, ct) is { } notEntitled)
            throw new PlanLimitException(notEntitled);
    }

    // ------------------------------------------------------------------
    // Replay: the revision runs the steps that depend on the film too
    // ------------------------------------------------------------------

    /// <summary>
    /// Queues a revision that replays the workflow's dependent steps: a per-revision snapshot of the source
    /// run's workflow (<see cref="WorkflowDefinitionKind.CompositionReplay"/>, the film step replaced by a
    /// Revise step), the plan on the request row, and an execution that carries the source run's brief and
    /// style — the replayed steps must see exactly what a fresh run would have shown them.
    /// </summary>
    private async Task<CompositionRevisionRequestResult> RequestReplayAsync(
        Project project,
        WorkflowExecution execution,
        Guid userId,
        Guid? organizationId,
        Lineage lineage,
        CompositionWorkspaceVersion? baseVersion,
        CompositionReplaySource source,
        CompositionReplayPlan plan,
        string instruction,
        CompositionRevisionTargets? targets,
        CompositionRevisionMode mode,
        CancellationToken ct,
        ExecutionInitiator initiator)
    {
        Guid? reviserId = await ReviserAgentIdAsync(ct);
        if (reviserId is null)
        {
            return CompositionRevisionRequestResult.Refuse(
                503, "REVISION_AGENT_UNAVAILABLE", "Changes cannot be made on this installation yet. Please try again later.");
        }

        WorkflowDefinition snapshot = BuildReplayWorkflow(project.Id, source, plan, reviserId.Value);

        // The replay renders at the source run's resolution: the plan's cap answers before the preflight.
        await ThrowIfPlanRefusesResolutionAsync(project, source.SourceExecution.OutputResolution, ct);

        // The B7 preflight over exactly what will hold credits: the revision, every step that runs again,
        // and every decision that MAY be re-made (an incompatible kept decision re-runs and holds). The
        // hold is planned over the whole snapshot, as the engine plans the execution (QA-7).
        if (_costEstimator is { BillingEnabled: true })
        {
            using IDisposable chosenModels = (await ChosenModelsAsync(source, ct)).Enter();
            WorkflowPreflightResult preflight = await _costEstimator.PreflightChargedAsync(
                organizationId, RunSteps(snapshot), ChargedStepOrders(snapshot, plan, includeConditional: true),
                source.SourceExecution.OutputResolution, ct);
            if (preflight.Refused)
            {
                (string message, Dictionary<string, object?> extra) = ReplayShortfall(preflight);
                return CompositionRevisionRequestResult.Refuse(402, "INSUFFICIENT_CREDITS", message, extra);
            }
        }

        CompositionWorkspace workspace = lineage.Workspace ?? await CreateOriginWorkspaceAsync(project, execution, ct);

        Guid revisionExecutionId = Guid.NewGuid();
        CompositionRevision revision = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            WorkspaceId = workspace.Id,
            SourceExecutionId = execution.Id,
            BaseVersionId = baseVersion?.Id,
            ExecutionId = revisionExecutionId,
            WorkflowDefinitionId = snapshot.Id,
            Instruction = instruction,
            TargetsJson = CompositionRevisionTargets.Serialize(targets),
            Mode = mode,
            RequestedByUserId = userId,
            CutPolicy = plan.CutPolicy,
            ReplayPlanJson = plan.Serialize(),
            CreatedAt = DateTime.UtcNow
        };

        _db.WorkflowDefinitions.Add(snapshot);
        _db.CompositionRevisions.Add(revision);
        await _db.SaveChangesAsync(ct);

        WorkflowExecution sourceRun = source.SourceExecution;
        RunChoices? runChoices = ReplayRunChoices(sourceRun);

        WorkflowExecutionResponse queued;
        try
        {
            // The run's request is the ORIGINAL brief: every replayed step (the edit, the compile, the
            // review) reads it exactly as it did; the owner's instruction lives on the request row, which
            // is all the Revise step reads.
            queued = await _executions.QueueAsync(
                project.Id, snapshot.Id, userId, sourceRun.UserRequest, initiator, ct,
                regenerateVideoClips: false, runChoices, WorkflowDefinitionKind.CompositionReplay, revisionExecutionId);
        }
        catch
        {
            _db.CompositionRevisions.Remove(revision);
            _db.WorkflowDefinitions.Remove(snapshot);
            await _db.SaveChangesAsync(CancellationToken.None);
            throw;
        }

        _logger.LogInformation(
            "Composition revision {RevisionId} queued as replay execution {ExecutionId} of {WorkflowId} (source run {SourceRun}, film step {RevisedStep}, {CutPolicy}, {RunCount} step(s) to run)",
            revision.Id, queued.Id, snapshot.Id, sourceRun.Id, plan.RevisedStepOrder, plan.CutPolicy,
            plan.Steps.Count(st => st.Disposition is ReplayStepDisposition.Run or ReplayStepDisposition.KeepIfCompatible));

        return CompositionRevisionRequestResult.Ok(new CompositionRevisionResponse(
            revision.Id, revisionExecutionId, snapshot.Id, ExecutionStatus.Queued.ToString(),
            revision.Instruction, CompositionRevisionTargets.Deserialize(revision.TargetsJson), revision.Mode.ToString(),
            baseVersion?.Id, baseVersion?.VersionNumber, null, null, revision.CreatedAt, null, null,
            plan.CutPolicy.ToString()));
    }

    /// <summary>
    /// The 402 INSUFFICIENT_CREDITS message and body of a refused replay preflight (shared with change requests).
    /// </summary>
    internal static (string Message, Dictionary<string, object?> Extra) ReplayShortfall(WorkflowPreflightResult preflight)
    {
        WorkflowCostEstimate cost = preflight.Estimate;
        double required = WorkflowsController.ToCredits(cost.RequiredMillis);
        double balance = WorkflowsController.ToCredits(preflight.BalanceMillis);
        return (
            CreditShortfallMessage.BuildFromCredits(required, cost.HasHistory ? WorkflowsController.ToCredits(cost.TypicalMillis) : null, balance),
            new Dictionary<string, object?>
            {
                ["required"] = required,
                ["balance"] = balance,
                ["missing"] = CreditShortfallMessage.MissingCreditsFromCredits(required, balance),
                ["low"] = WorkflowsController.ToCredits(cost.LowMillis),
                ["high"] = WorkflowsController.ToCredits(cost.HighMillis),
                ["hasHistory"] = cost.HasHistory
            });
    }

    /// <summary>
    /// What a replay keeps of the source run: its style, format, resolution and brief (language, length,
    /// voice, source files, design-system choice), so a revision of an Italian film stays Italian.
    /// </summary>
    internal static RunChoices? ReplayRunChoices(WorkflowExecution sourceRun)
    {
        RunStyleSnapshot? style = StyleJson.TryParseSnapshot(sourceRun.StyleConfigJson);
        StoredRunBrief? storedBrief = RunBriefJson.TryParse(sourceRun.RunBriefJson);
        return style is null && sourceRun.OutputFormatOverride is null && sourceRun.OutputResolution is null && storedBrief is null
            ? null
            : new RunChoices(style, sourceRun.OutputFormatOverride, sourceRun.OutputResolution, storedBrief);
    }

    /// <summary>
    /// The per-revision snapshot of the source run's workflow: every step copied at its own order (so every
    /// step-order reference resolves exactly as it did), the film step replaced by a Revise step driven by
    /// the built-in reviser. Not saved here.
    /// </summary>
    internal static WorkflowDefinition BuildReplayWorkflow(Guid projectId, CompositionReplaySource source, CompositionReplayPlan plan, Guid reviserAgentId)
    {
        string baseName = string.IsNullOrWhiteSpace(source.OriginWorkflowName) ? "a video" : source.OriginWorkflowName!;
        WorkflowDefinition snapshot = new()
        {
            Id = Guid.NewGuid(),
            Name = Truncate($"Changes to {baseName}", 200),
            ProjectId = projectId,
            Kind = WorkflowDefinitionKind.CompositionReplay,
            RequiresUserInput = false,
            // docs/design/app-inputs.md 28 (the L9 narrowing): a run whose app chose its agents' models keeps them. The replay
            // carries the source run's brief (its inputs) and, only then, the page that names those agents, so the engine lays
            // the same choices on the replayed agents (ExecutionSnapshotter.ReplayAgentModelsAsync), as a change request does.
            AppManifestJson = KeepsAgentModels(source) ? source.AppManifestJson : null,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        foreach (WorkflowStep step in source.Steps.OrderBy(s => s.StepOrder))
        {
            WorkflowStep copy = WorkflowStepCloner.Clone(step, snapshot.Id);
            if (step.StepOrder == plan.RevisedStepOrder)
            {
                copy = ReplayReviseStep(copy.Id, snapshot.Id, step.StepOrder, reviserAgentId);
            }

            snapshot.Steps.Add(copy);
        }

        return snapshot;
    }

    /// <summary>True when the source run's app chose an agent's model (its page names the agent, its brief the model).</summary>
    /// <summary>
    /// The app's chosen models for the price of a replay (docs/design/app-inputs.md 29, the F2 28.7 narrowing): entered exactly
    /// when the replay carries the page (<see cref="KeepsAgentModels"/>), which is when the engine holds at them.
    /// </summary>
    private Task<ReplayModelChoices> ChosenModelsAsync(CompositionReplaySource source, CancellationToken ct) =>
        ReplayModelPricing.ResolveAsync(
            _appModels, source.SourceExecution.OrganizationId, source.SourceExecution.ProjectId,
            KeepsAgentModels(source) ? source.AppManifestJson : null,
            RunBriefJson.TryParse(source.SourceExecution.RunBriefJson)?.Inputs, ct);

    internal static bool KeepsAgentModels(CompositionReplaySource source) =>
        AppManifestJson.TryParse(source.AppManifestJson) is { } manifest
        && AppModelRules.HasAgentModelValue(manifest, RunBriefJson.TryParse(source.SourceExecution.RunBriefJson)?.Inputs);

    /// <summary>The Revise step that replaces the film render in a replay snapshot (shared with multi-target change requests).</summary>
    internal static WorkflowStep ReplayReviseStep(Guid id, Guid snapshotId, int stepOrder, Guid reviserAgentId) => new()
    {
        Id = id,
        WorkflowDefinitionId = snapshotId,
        AgentDefinitionId = reviserAgentId,
        StepOrder = stepOrder,
        StepType = StepType.Revise,
        Label = "Make the requested changes",
        MaxIterations = 1
    };

    /// <summary>What the film needs to be one target of a multi-target change request.</summary>
    /// <param name="Workspace">The run's composition workspace; null when it has none yet (created after the preflight).</param>
    /// <param name="BaseVersion">The version the film change builds on: the newest version of the video, as a revision does.</param>
    /// <param name="RunVersion">The version the source run itself made (or is); differs from <paramref name="BaseVersion"/> when the film was changed since.</param>
    internal sealed record FilmContext(
        CompositionWorkspace? Workspace, CompositionWorkspaceVersion? BaseVersion, Guid ReviserAgentId, CompositionWorkspaceVersion? RunVersion = null);

    /// <summary>
    /// The film context of a finished source run, or the refusal a revision of it would answer
    /// (409 WORKSPACE_UNAVAILABLE, 503 REVISION_AGENT_UNAVAILABLE). Writes nothing.
    /// </summary>
    internal async Task<(FilmContext? Film, CompositionRevisionRequestResult? Refusal)> ResolveFilmContextAsync(
        Project project, WorkflowExecution execution, CancellationToken ct)
    {
        Lineage lineage = await ResolveLineageAsync(project.Id, execution, ct);
        List<CompositionWorkspaceVersion> versions = lineage.Workspace is null
            ? []
            : await _db.CompositionWorkspaceVersions.AsNoTracking()
                .Where(v => v.ProjectId == project.Id && v.WorkspaceId == lineage.Workspace.Id)
                .OrderBy(v => v.VersionNumber)
                .ToListAsync(ct);

        (string availability, string? reason) = await AvailabilityAsync(project.Id, execution, versions, ct);
        if (availability == AvailabilityUnavailable)
            return (null, CompositionRevisionRequestResult.Refuse(409, "WORKSPACE_UNAVAILABLE", reason ?? "This video cannot be changed."));

        Guid? reviserId = await ReviserAgentIdAsync(ct);
        if (reviserId is null)
        {
            return (null, CompositionRevisionRequestResult.Refuse(
                503, "REVISION_AGENT_UNAVAILABLE", "Changes cannot be made on this installation yet. Please try again later."));
        }

        // Same base as a composition revision (RequestAsync): the newest version of the video, whichever run the owner
        // is looking at, so a change request never silently drops a fix made by a later revision. Same versions list
        // (this project, this lineage), so tenancy and lineage are judged exactly as before.
        CompositionWorkspaceVersion? own = CurrentVersion(execution.Id, lineage.Revision, versions);
        CompositionWorkspaceVersion? baseVersion = await WorkspaceHeadAsync(versions, ct) ?? own;
        return (new FilmContext(lineage.Workspace, baseVersion, reviserId.Value, own), null);
    }

    /// <summary>
    /// The <see cref="CompositionRevision"/> row of a combined replay run (the film is one of several targets): same
    /// execution as the change-request rows, the snapshot, the combined plan. When the run has no workspace yet, a NEW
    /// origin workspace is returned too (tracked as Added, not saved) so the caller saves it in the same transaction as
    /// the rows and removes it again if the queue refuses. Not added to the context.
    /// </summary>
    internal (CompositionRevision Revision, CompositionWorkspace? CreatedWorkspace) NewReplayRevision(
        Project project, WorkflowExecution execution, FilmContext film, Guid executionId, Guid snapshotId,
        string instruction, CompositionReplayPlan plan, Guid userId)
    {
        CompositionWorkspace? created = null;
        if (film.Workspace is null)
        {
            created = new CompositionWorkspace
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OrganizationId = project.OrganizationId,
                OriginExecutionId = execution.Id,
                OriginWorkflowDefinitionId = execution.WorkflowDefinitionId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.CompositionWorkspaces.Add(created);
        }

        CompositionRevision revision = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            WorkspaceId = (film.Workspace ?? created!).Id,
            SourceExecutionId = execution.Id,
            BaseVersionId = film.BaseVersion?.Id,
            ExecutionId = executionId,
            WorkflowDefinitionId = snapshotId,
            Instruction = instruction,
            TargetsJson = CompositionRevisionTargets.Serialize(null),
            Mode = CompositionRevisionMode.Render,
            RequestedByUserId = userId,
            CutPolicy = plan.CutPolicy,
            ReplayPlanJson = plan.Serialize(),
            CreatedAt = DateTime.UtcNow
        };
        return (revision, created);
    }

    /// <summary>The id of the run's origin workspace, if one exists (re-read after a unique-index conflict on origin_execution_id).</summary>
    internal Task<Guid?> FindOriginWorkspaceIdAsync(Guid projectId, Guid executionId, CancellationToken ct) =>
        _db.CompositionWorkspaces.AsNoTracking()
            .Where(w => w.OriginExecutionId == executionId && w.ProjectId == projectId)
            .Select(w => (Guid?)w.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>The steps of <paramref name="snapshot"/> that hold credits under <paramref name="plan"/>.</summary>
    internal static List<WorkflowStep> ChargedSteps(WorkflowDefinition snapshot, CompositionReplayPlan plan, bool includeConditional) =>
    [
        .. snapshot.Steps
            .OrderBy(s => s.StepOrder)
            .Where(s => plan.For(s.StepOrder)?.Disposition switch
            {
                ReplayStepDisposition.Revise or ReplayStepDisposition.Run => true,
                ReplayStepDisposition.KeepIfCompatible => includeConditional,
                _ => false
            })
    ];

    /// <summary>The snapshot's steps in run order: the set the engine plans the execution's holds over.</summary>
    internal static List<WorkflowStep> RunSteps(WorkflowDefinition snapshot) => [.. snapshot.Steps.OrderBy(s => s.StepOrder)];

    /// <summary>The step orders of <see cref="ChargedSteps"/>.</summary>
    internal static HashSet<int> ChargedStepOrders(WorkflowDefinition snapshot, CompositionReplayPlan plan, bool includeConditional) =>
        [.. ChargedSteps(snapshot, plan, includeConditional).Select(s => s.StepOrder)];

    /// <summary>The replay a revision of the current version would run, with its price under each cut choice.</summary>
    private async Task<RevisionReplayResponse> DescribeReplayAsync(
        Guid? organizationId, CompositionReplaySource? source, string? reason, CancellationToken ct)
    {
        if (source is null)
            return new RevisionReplayResponse(false, reason, false, [], null, null);

        CompositionReplayPlan keep = source.Plan(CompositionCutPolicy.KeepCut);
        CompositionReplayPlan reEdit = source.Plan(CompositionCutPolicy.ReEdit);
        if (!keep.HasDownstreamWork)
            return new RevisionReplayResponse(false, null, false, [], null, null);

        Dictionary<int, WorkflowStep> byOrder = source.Steps.ToDictionary(s => s.StepOrder);
        List<RevisionReplayStepResponse> steps = [];
        foreach (ReplayStepPlan planned in keep.Steps.Where(st => st.StepOrder > keep.RevisedStepOrder))
        {
            ReplayStepDisposition other = reEdit.For(planned.StepOrder)?.Disposition ?? planned.Disposition;
            if (planned.Disposition == ReplayStepDisposition.Reuse && other == ReplayStepDisposition.Reuse)
                continue;
            WorkflowStep step = byOrder[planned.StepOrder];
            steps.Add(new RevisionReplayStepResponse(
                planned.StepOrder,
                string.IsNullOrWhiteSpace(step.Label) ? step.AgentDefinition?.Name ?? step.StepType.ToString() : step.Label!,
                step.StepType.ToString(),
                planned.Disposition.ToString(),
                other.ToString()));
        }

        (RevisionCostEstimateResponse? keepEstimate, RevisionCostEstimateResponse? reEditEstimate) =
            await EstimateReplayAsync(organizationId, source, keep, reEdit, ct);
        return new RevisionReplayResponse(true, null, keep.HasEditDecisions, steps, keepEstimate, reEditEstimate);
    }

    /// <summary>
    /// The price of a replay under each cut choice, priced exactly as the request's preflight prices it.
    /// "Keep the same cut" is a range: its low end assumes every kept decision stays kept, its high end and
    /// the amount it must be able to hold assume every one is re-made.
    /// </summary>
    private async Task<(RevisionCostEstimateResponse? Keep, RevisionCostEstimateResponse? ReEdit)> EstimateReplayAsync(
        Guid? organizationId, CompositionReplaySource source, CompositionReplayPlan keep, CompositionReplayPlan reEdit, CancellationToken ct)
    {
        if (_costEstimator is null || !_costEstimator.BillingEnabled)
            return (null, null);
        try
        {
            Guid reviserId = await ReviserAgentIdAsync(ct) ?? Guid.Empty;
            using IDisposable chosenModels = (await ChosenModelsAsync(source, ct)).Enter();
            WorkflowDefinition keepSnapshot = BuildReplayWorkflow(Guid.Empty, source, keep, reviserId);
            WorkflowDefinition reEditSnapshot = BuildReplayWorkflow(Guid.Empty, source, reEdit, reviserId);

            // The replay renders at the source run's resolution, so every figure below is priced at it.
            int? resolution = source.SourceExecution.OutputResolution;

            // The display: the certain steps' range, widened by the decisions that may be re-made. The
            // amount to hold: the engine's plan over the whole snapshot, summed over every step that may
            // hold, exactly as the request's preflight sizes it (QA-7).
            WorkflowPreflightResult keepHold = await _costEstimator.PreflightChargedAsync(
                organizationId, RunSteps(keepSnapshot), ChargedStepOrders(keepSnapshot, keep, includeConditional: true), resolution, ct);
            // Not evaluated: no balance to compare (and perhaps no estimate), so no figures to show.
            if (!keepHold.Estimate.BillingEnabled || !keepHold.Evaluated)
                return (null, null);

            WorkflowCostEstimate certain = await _costEstimator.EstimateAsync(
                organizationId, ChargedSteps(keepSnapshot, keep, includeConditional: false), resolution, ct);
            List<WorkflowStep> conditionalSteps = [.. ChargedSteps(keepSnapshot, keep, includeConditional: true)
                .Except(ChargedSteps(keepSnapshot, keep, includeConditional: false))];
            WorkflowCostEstimate conditional = conditionalSteps.Count > 0
                ? await _costEstimator.EstimateAsync(organizationId, conditionalSteps, resolution, ct)
                : WorkflowCostEstimate.Disabled;
            WorkflowPreflightResult full = await _costEstimator.PreflightChargedAsync(
                organizationId, RunSteps(reEditSnapshot), ChargedStepOrders(reEditSnapshot, reEdit, includeConditional: true), resolution, ct);
            if (!full.Evaluated)
                return (null, null);

            long balance = keepHold.BalanceMillis;
            long keepRequired = keepHold.Estimate.RequiredMillis;
            RevisionCostEstimateResponse keepEstimate = new(
                true,
                certain.LowMillis / 1000.0,
                (certain.HighMillis + conditional.HighMillis) / 1000.0,
                keepRequired / 1000.0,
                balance / 1000.0,
                balance >= keepRequired);
            RevisionCostEstimateResponse reEditEstimate = new(
                true,
                full.Estimate.LowMillis / 1000.0,
                full.Estimate.HighMillis / 1000.0,
                full.Estimate.RequiredMillis / 1000.0,
                full.BalanceMillis / 1000.0,
                !full.Refused);
            return (keepEstimate, reEditEstimate);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not estimate a revision replay for organization {OrganizationId}", organizationId);
            return (null, null);
        }
    }

    private Task<Guid?> ReviserAgentIdAsync(CancellationToken ct) =>
        _db.AgentDefinitions.AsNoTracking()
            .Where(a => a.IsBuiltIn && a.AgentType == AgentType.CompositionReviser)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(ct);

    internal static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    /// <summary>
    /// A revision run renders at the resolution its source run chose (the Revise step's Remotion render),
    /// and the source's stored brief, nothing else of its choices; null when the source chose Auto, so a revision of an
    /// Auto run is queued exactly as before resolutions existed.
    /// </summary>
    private static RunChoices? ResolutionOnly(WorkflowExecution source)
    {
        // The source run's stored brief rides along (language, length, voice, designSystem none), so the
        // reviser run is a revision of an Italian film in Italian and never re-pins the brand a run opted out of.
        StoredRunBrief? brief = RunBriefJson.TryParse(source.RunBriefJson);
        return source.OutputResolution is null && brief is null
            ? null
            : new RunChoices(null, null, source.OutputResolution, brief);
    }

    // ------------------------------------------------------------------
    // Lineage and availability
    // ------------------------------------------------------------------

    private sealed record Lineage(CompositionWorkspace? Workspace, CompositionRevision? Revision);

    private Task<WorkflowExecution?> LoadExecutionAsync(Guid projectId, Guid executionId, CancellationToken ct) =>
        _db.WorkflowExecutions.AsNoTracking().FirstOrDefaultAsync(e => e.Id == executionId && e.ProjectId == projectId, ct);

    /// <summary>
    /// A run belongs to a lineage as a revision run (its request row), as the origin run (the workspace
    /// it started), or as the run that produced a version (an origin capture). All three resolve to one
    /// workspace, so the history reads the same from any run in it.
    /// </summary>
    private async Task<Lineage> ResolveLineageAsync(Guid projectId, WorkflowExecution execution, CancellationToken ct)
    {
        CompositionRevision? revision = await _db.CompositionRevisions.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ExecutionId == execution.Id && r.ProjectId == projectId, ct);

        Guid? workspaceId = revision?.WorkspaceId
            ?? await _db.CompositionWorkspaces.AsNoTracking()
                .Where(w => w.OriginExecutionId == execution.Id && w.ProjectId == projectId)
                .Select(w => (Guid?)w.Id)
                .FirstOrDefaultAsync(ct)
            ?? await _db.CompositionWorkspaceVersions.AsNoTracking()
                .Where(v => v.CreatedByExecutionId == execution.Id && v.ProjectId == projectId)
                .Select(v => (Guid?)v.WorkspaceId)
                .FirstOrDefaultAsync(ct);

        CompositionWorkspace? workspace = workspaceId is { } id
            ? await _db.CompositionWorkspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id && w.ProjectId == projectId, ct)
            : null;
        return new Lineage(workspace, revision);
    }

    /// <summary>
    /// Snapshot when a version exists; Recoverable for a finished run made before capture whose render
    /// came from an agent (a Remotion film) recently enough that its sandbox may still exist;
    /// otherwise Unavailable with a plain-language reason.
    /// </summary>
    private async Task<(string Availability, string? Reason)> AvailabilityAsync(
        Guid projectId, WorkflowExecution execution, IReadOnlyList<CompositionWorkspaceVersion> versions, CancellationToken ct)
    {
        // A designed graphic's lineage (docs/editor.md "Designed graphics") is a still on a transparent
        // frame, not a film: revising it as a video would render a film over it. It is changed from the
        // picture editor ("Change this graphic") instead.
        if (versions.Any(v => v.Source == CompositionVersionSource.GraphicDesign))
        {
            return (AvailabilityUnavailable,
                "This run designed a graphic for a picture. Change it from the picture editor with \"Change this graphic\".");
        }

        // A run with a result whose output is not served offers no change, in the words of a run that made no video.
        List<Guid> runResultIds = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == execution.Id).Select(r => r.Id).ToListAsync(ct);
        if ((await Outputs.OutputGate.HeldResultIdsAsync(_db, projectId, runResultIds, ct)).Count > 0)
            return (AvailabilityUnavailable, "This run did not make an animated video, so there is nothing here to change.");

        if (versions.Count > 0)
            return (AvailabilitySnapshot, null);

        bool hasFilmRender = await _db.WorkflowStepResults.AsNoTracking()
            .AnyAsync(r => r.WorkflowExecutionId == execution.Id
                           && r.SupersededAt == null
                           && r.OutputStorageKey != null
                           && r.OutputStorageKey.StartsWith("projects/" + projectId + "/outputFiles/")
                           && (r.WorkflowStep.StepType == StepType.Agent || r.WorkflowStep.StepType == StepType.Revise), ct);
        if (!hasFilmRender)
        {
            return (AvailabilityUnavailable,
                IsTerminal(execution.Status)
                    ? "This run did not make an animated video, so there is nothing here to change."
                    : "You can request changes once this run has finished.");
        }

        DateTime finishedAt = execution.CompletedAt ?? execution.StartedAt ?? DateTime.UtcNow;
        if (DateTime.UtcNow - finishedAt > TimeSpan.FromDays(Math.Max(1, _options.SandboxRecoveryWindowDays)))
        {
            return (AvailabilityUnavailable,
                $"This video was made more than {_options.SandboxRecoveryWindowDays} days ago, before changes were saved automatically, "
                + "so its project is no longer available. Run the workflow again to make a video you can change.");
        }

        return (AvailabilityRecoverable, null);
    }

    /// <summary>
    /// The version a run "is": for a revision run, what it produced (or else what it started from);
    /// for any other run, the newest version it produced with a render; else the lineage's newest version.
    /// </summary>
    private static CompositionWorkspaceVersion? CurrentVersion(
        Guid executionId, CompositionRevision? revision, IReadOnlyList<CompositionWorkspaceVersion> versions)
    {
        if (versions.Count == 0)
            return null;
        if (revision is not null)
        {
            Guid? id = revision.ResultVersionId ?? revision.BaseVersionId;
            CompositionWorkspaceVersion? own = versions.FirstOrDefault(v => v.Id == id)
                ?? versions.LastOrDefault(v => v.CreatedByExecutionId == executionId);
            if (own is not null)
                return own;
        }

        return versions.LastOrDefault(v => v.CreatedByExecutionId == executionId && v.RenderOutputStorageKey is not null)
               ?? versions.LastOrDefault(v => v.CreatedByExecutionId == executionId)
               ?? versions[^1];
    }

    /// <summary>The newest version of the video (the workspace head), or null when there is none.</summary>
    /// <summary>
    /// The newest version a change can safely build on: it has a finished render and the run that made it passed.
    /// A version from a failed or still-running revision, or a preview that never rendered, is skipped.
    /// </summary>
    private async Task<CompositionWorkspaceVersion?> WorkspaceHeadAsync(IReadOnlyList<CompositionWorkspaceVersion> versions, CancellationToken ct)
    {
        List<CompositionWorkspaceVersion> rendered = [.. versions.Where(v => v.RenderOutputStorageKey is not null).OrderByDescending(v => v.VersionNumber)];
        if (rendered.Count == 0)
            return null;
        Guid[] creators = [.. rendered.Select(v => v.CreatedByExecutionId).OfType<Guid>().Distinct()];
        HashSet<Guid> passed = [.. await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => creators.Contains(e.Id) && e.Status == ExecutionStatus.Passed)
            .Select(e => e.Id)
            .ToListAsync(ct)];
        return rendered.FirstOrDefault(v => v.CreatedByExecutionId is { } c && passed.Contains(c));
    }

    private async Task<CompositionWorkspace> CreateOriginWorkspaceAsync(Project project, WorkflowExecution execution, CancellationToken ct)
    {
        CompositionWorkspace workspace = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            OrganizationId = project.OrganizationId,
            OriginExecutionId = execution.Id,
            OriginWorkflowDefinitionId = execution.WorkflowDefinitionId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.CompositionWorkspaces.Add(workspace);
        try
        {
            await _db.SaveChangesAsync(ct);
            return workspace;
        }
        catch (DbUpdateException)
        {
            // A concurrent request (or the engine's capture) created it first: use theirs.
            _db.Entry(workspace).State = EntityState.Detached;
            return await _db.CompositionWorkspaces.AsNoTracking()
                .FirstAsync(w => w.OriginExecutionId == execution.Id && w.ProjectId == project.Id, ct);
        }
    }

    /// <summary>
    /// The project's hidden revision workflow: one <see cref="StepType.Revise"/> step driven by the
    /// built-in CompositionReviser agent. Created on first use; null when the built-in agent row has not
    /// been seeded (an engine/API version skew), which the caller reports as a 503.
    /// </summary>
    private async Task<WorkflowDefinition?> EnsureSystemWorkflowAsync(Guid projectId, CancellationToken ct)
    {
        WorkflowDefinition? existing = await _db.WorkflowDefinitions
            .FirstOrDefaultAsync(w => w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.CompositionRevision, ct);
        if (existing is not null)
            return existing;

        Guid? agentId = await ReviserAgentIdAsync(ct);
        if (agentId is null)
            return null;

        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = SystemWorkflowName,
            ProjectId = projectId,
            Kind = WorkflowDefinitionKind.CompositionRevision,
            RequiresUserInput = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        workflow.Steps.Add(ReviseStep(workflow.Id, agentId.Value));
        _db.WorkflowDefinitions.Add(workflow);
        await _db.SaveChangesAsync(ct);
        return workflow;
    }

    /// <summary>
    /// The hidden revision workflow's one step — shared by <see cref="EnsureSystemWorkflowAsync"/> (which
    /// stores it) and the lineage estimate (which prices it), so the estimate prices the step the request's
    /// preflight will: the reviser's own history and cold price, not a step with no agent.
    /// </summary>
    private static WorkflowStep ReviseStep(Guid workflowId, Guid reviserAgentId) => new()
    {
        Id = workflowId == Guid.Empty ? Guid.Empty : Guid.NewGuid(),
        WorkflowDefinitionId = workflowId,
        AgentDefinitionId = reviserAgentId,
        StepOrder = 1,
        StepType = StepType.Revise,
        Label = "Make the requested changes",
        MaxIterations = 1
    };

    // ------------------------------------------------------------------
    // Mapping
    // ------------------------------------------------------------------

    private async Task<List<CompositionVersionResponse>> MapVersionsAsync(
        Guid projectId, IReadOnlyList<CompositionWorkspaceVersion> versions, CancellationToken ct)
    {
        if (versions.Count == 0)
            return [];

        List<Guid> executionIds = [.. versions.Where(v => v.CreatedByExecutionId is not null).Select(v => v.CreatedByExecutionId!.Value).Distinct()];
        var executions = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => executionIds.Contains(e.Id) && e.ProjectId == projectId)
            .Select(e => new { e.Id, e.WorkflowDefinitionId, e.Status })
            .ToDictionaryAsync(e => e.Id, ct);

        List<string> renderKeys = [.. versions.Where(v => v.RenderOutputStorageKey is not null).Select(v => v.RenderOutputStorageKey!)];
        var renders = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => executionIds.Contains(r.WorkflowExecutionId) && r.OutputStorageKey != null && renderKeys.Contains(r.OutputStorageKey))
            .Select(r => new { r.Id, r.WorkflowExecutionId, r.OutputStorageKey, r.ExecutedAt, StepOrder = r.WorkflowStep.StepOrder })
            .ToListAsync(ct);

        Dictionary<Guid, RunDelivery> deliveries = await DeliveriesAsync(projectId, [.. executions.Values.Select(e => (e.Id, e.WorkflowDefinitionId))], ct);

        Dictionary<Guid, int> numberById = versions.ToDictionary(v => v.Id, v => v.VersionNumber);
        return [.. versions.Select(v =>
        {
            var render = renders
                .Where(r => r.WorkflowExecutionId == v.CreatedByExecutionId && r.OutputStorageKey == v.RenderOutputStorageKey)
                .OrderByDescending(r => r.ExecutedAt)
                .FirstOrDefault();
            var run = v.CreatedByExecutionId is { } exec && executions.TryGetValue(exec, out var e) ? e : null;
            RunDelivery? delivery = run is not null ? deliveries.GetValueOrDefault(run.Id) : null;

            // The deliverable of this version's run: its final videos when the run made any beyond the
            // render itself (a compile of the film), else none — the render is then what plays.
            IReadOnlyList<RevisionDeliverableResponse> deliverables = delivery?.Deliverables(render?.StepOrder) ?? [];
            return new CompositionVersionResponse(
                v.Id,
                v.VersionNumber,
                v.ParentVersionId,
                v.ParentVersionId is { } parent && numberById.TryGetValue(parent, out int parentNumber) ? parentNumber : null,
                v.CreatedAt,
                v.Instruction,
                v.Source.ToString(),
                v.CreatedByExecutionId,
                run?.WorkflowDefinitionId,
                render?.Id,
                v.RenderOutputStorageKey is not null,
                v.CompositionId,
                v.DurationInFrames,
                v.Fps,
                v.Width,
                v.Height,
                Scenes(v.SceneManifestJson).Count,
                deliverables,
                run?.Status.ToString(),
                delivery?.ExpectsFinalVideo(render?.StepOrder) ?? false,
                delivery?.CutPolicy?.ToString(),
                delivery?.CutKept);
        })];
    }

    /// <summary>
    /// What one run delivered: the step orders of its final videos (the replay plan's for a revision run,
    /// otherwise the dependency analysis of its workflow), each one's latest finished render, and — for a
    /// revision under "Keep the same cut" — whether the cut was in fact kept.
    /// </summary>
    private sealed class RunDelivery
    {
        public required IReadOnlyList<int>? FinalOrders { get; init; }
        public required Dictionary<int, (Guid Id, string? Label)> LatestRenders { get; init; }
        public CompositionCutPolicy? CutPolicy { get; init; }
        public bool? CutKept { get; init; }

        /// <summary>The final videos, excluding the version's own render (that is the "animation" link).</summary>
        public IReadOnlyList<RevisionDeliverableResponse> Deliverables(int? renderStepOrder)
        {
            List<int> orders = [.. (FinalOrders ?? []).Where(o => o != renderStepOrder && LatestRenders.ContainsKey(o)).Order()];
            return [.. orders.Select((o, i) => new RevisionDeliverableResponse(LatestRenders[o].Id, o, LatestRenders[o].Label, i == orders.Count - 1))];
        }

        /// <summary>True when the run is meant to end with a final video beyond the render (finished or not).</summary>
        public bool ExpectsFinalVideo(int? renderStepOrder) => (FinalOrders ?? []).Any(o => o != renderStepOrder);
    }

    private async Task<Dictionary<Guid, RunDelivery>> DeliveriesAsync(
        Guid projectId, IReadOnlyList<(Guid ExecutionId, Guid WorkflowId)> runs, CancellationToken ct)
    {
        Dictionary<Guid, RunDelivery> deliveries = [];
        if (runs.Count == 0)
            return deliveries;

        List<Guid> executionIds = [.. runs.Select(r => r.ExecutionId)];
        List<Guid> workflowIds = [.. runs.Select(r => r.WorkflowId).Distinct()];
        Dictionary<Guid, CompositionRevision> revisionByExecution = await _db.CompositionRevisions.AsNoTracking()
            .Where(r => r.ProjectId == projectId && executionIds.Contains(r.ExecutionId))
            .ToDictionaryAsync(r => r.ExecutionId, ct);
        List<WorkflowStep> steps = await _db.WorkflowSteps.AsNoTracking()
            .Include(s => s.AgentDefinition)
            .Where(s => workflowIds.Contains(s.WorkflowDefinitionId))
            .ToListAsync(ct);
        string prefix = $"projects/{projectId}/outputFiles/";
        var results = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => executionIds.Contains(r.WorkflowExecutionId) && r.Status == StepStatus.Completed && r.SupersededAt == null)
            .Select(r => new
            {
                r.Id,
                r.WorkflowExecutionId,
                r.WorkflowStepId,
                r.OutputStorageKey,
                r.ExecutedAt,
                r.ReusedFromStepResultId
            })
            .ToListAsync(ct);
        Dictionary<Guid, WorkflowStep> stepById = steps.ToDictionary(s => s.Id);

        foreach ((Guid executionId, Guid workflowId) in runs)
        {
            List<WorkflowStep> workflowSteps = [.. steps.Where(s => s.WorkflowDefinitionId == workflowId)];
            CompositionRevision? revision = revisionByExecution.GetValueOrDefault(executionId);
            CompositionReplayPlan? plan = CompositionReplayPlan.TryParse(revision?.ReplayPlanJson);
            IReadOnlyList<int>? finals = plan?.FinalVideoStepOrders
                ?? (workflowSteps.Count > 0 ? WorkflowStepDependencyGraph.Build(workflowSteps).FinalVideoStepOrders(null) : null);

            var runResults = results.Where(r => r.WorkflowExecutionId == executionId && stepById.ContainsKey(r.WorkflowStepId)).ToList();
            Dictionary<int, (Guid, string?)> latestRenders = runResults
                .Where(r => r.OutputStorageKey != null && r.OutputStorageKey.StartsWith(prefix, StringComparison.Ordinal))
                .GroupBy(r => stepById[r.WorkflowStepId].StepOrder)
                .ToDictionary(g => g.Key, g =>
                {
                    var latest = g.OrderByDescending(r => r.ExecutedAt).First();
                    WorkflowStep step = stepById[latest.WorkflowStepId];
                    return (latest.Id, string.IsNullOrWhiteSpace(step.Label) ? (string?)null : step.Label);
                });

            bool? cutKept = null;
            if (plan is not null)
            {
                List<int> keptCandidates = [.. plan.Steps.Where(st => st.Disposition == ReplayStepDisposition.KeepIfCompatible).Select(st => st.StepOrder)];
                var decided = runResults
                    .Where(r => keptCandidates.Contains(stepById[r.WorkflowStepId].StepOrder))
                    .ToList();
                if (keptCandidates.Count > 0 && decided.Count > 0)
                    cutKept = decided.All(r => r.ReusedFromStepResultId != null);
            }

            deliveries[executionId] = new RunDelivery
            {
                FinalOrders = finals,
                LatestRenders = latestRenders,
                CutPolicy = revision?.CutPolicy,
                CutKept = cutKept
            };
        }

        return deliveries;
    }

    private async Task<List<CompositionRevisionResponse>> MapRevisionsAsync(
        Guid projectId, Guid workspaceId, IReadOnlyList<CompositionWorkspaceVersion> versions, CancellationToken ct)
    {
        List<CompositionRevision> revisions = await _db.CompositionRevisions.AsNoTracking()
            .Where(r => r.ProjectId == projectId && r.WorkspaceId == workspaceId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(100)
            .ToListAsync(ct);
        List<Guid> executionIds = [.. revisions.Select(r => r.ExecutionId)];
        var executions = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => executionIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Status, e.CompletedAt, e.ErrorMessage })
            .ToDictionaryAsync(e => e.Id, ct);
        Dictionary<Guid, int> numberById = versions.ToDictionary(v => v.Id, v => v.VersionNumber);

        return [.. revisions.Select(r =>
        {
            executions.TryGetValue(r.ExecutionId, out var e);
            return new CompositionRevisionResponse(
                r.Id,
                r.ExecutionId,
                r.WorkflowDefinitionId,
                (e?.Status ?? ExecutionStatus.Queued).ToString(),
                r.Instruction,
                CompositionRevisionTargets.Deserialize(r.TargetsJson),
                r.Mode.ToString(),
                r.BaseVersionId,
                r.BaseVersionId is { } b && numberById.TryGetValue(b, out int bn) ? bn : null,
                r.ResultVersionId,
                r.ResultVersionId is { } rv && numberById.TryGetValue(rv, out int rn) ? rn : null,
                r.CreatedAt,
                e?.CompletedAt,
                e?.ErrorMessage,
                r.CutPolicy?.ToString());
        })];
    }

    internal static IReadOnlyList<RevisionSceneResponse> Scenes(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
            return [];
        try
        {
            CompositionSceneManifest? manifest = JsonSerializer.Deserialize<CompositionSceneManifest>(manifestJson, ManifestJson);
            return manifest?.Scenes is { } scenes
                ? [.. scenes.Select(s => new RevisionSceneResponse(s.Name, s.FromFrame, s.DurationInFrames,
                    string.IsNullOrWhiteSpace(s.Description) ? null : s.Description))]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>For a Recoverable run: the scene list beside its latest film render, best effort.</summary>
    private async Task<string?> TryReadSidecarAsync(Guid projectId, Guid executionId, CancellationToken ct)
    {
        if (_s3 is null)
            return null;
        string? renderKey = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == executionId && r.SupersededAt == null && r.OutputStorageKey != null
                        && r.OutputStorageKey.StartsWith("projects/" + projectId + "/outputFiles/"))
            .OrderByDescending(r => r.ExecutedAt)
            .Select(r => r.OutputStorageKey)
            .FirstOrDefaultAsync(ct);
        if (renderKey is null)
            return null;
        try
        {
            using GetObjectResponse response = await _s3.GetObjectAsync(_bucket, CompositionSceneManifest.SidecarKeyFor(renderKey), ct);
            using StreamReader reader = new(response.ResponseStream);
            string json = await reader.ReadToEndAsync(ct);
            return json.Length <= 1_000_000 ? json : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "No scene manifest beside {RenderKey}", renderKey);
            return null;
        }
    }

    /// <summary>
    /// What one revision is expected to cost, priced exactly as the execute preflight will price it: the
    /// hidden workflow's step (<see cref="ReviseStep"/>, the reviser agent) at the source run's
    /// <paramref name="outputResolution"/>, which the revision inherits and renders at.
    /// </summary>
    private async Task<RevisionCostEstimateResponse?> EstimateAsync(Guid? organizationId, int? outputResolution, CancellationToken ct)
    {
        if (_costEstimator is null || !_costEstimator.BillingEnabled)
            return null;
        try
        {
            WorkflowStep step = ReviseStep(Guid.Empty, await ReviserAgentIdAsync(ct) ?? Guid.Empty);
            WorkflowPreflightResult preflight = await _costEstimator.PreflightAsync(organizationId, [step], outputResolution, ct);
            WorkflowCostEstimate cost = preflight.Estimate;
            // Not evaluated: no balance to compare (and perhaps no estimate), so no figures to show.
            if (!cost.BillingEnabled || !preflight.Evaluated)
                return null;
            return new RevisionCostEstimateResponse(
                true,
                cost.LowMillis / 1000.0,
                cost.HighMillis / 1000.0,
                cost.RequiredMillis / 1000.0,
                preflight.BalanceMillis / 1000.0,
                !preflight.Refused);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not estimate a revision for organization {OrganizationId}", organizationId);
            return null;
        }
    }

    internal static bool IsTerminal(ExecutionStatus status) =>
        status is ExecutionStatus.Passed or ExecutionStatus.Failed or ExecutionStatus.Cancelled;
}
