using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Compositions;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Inference.Api.Services.ChangeRequests;

/// <summary>Metrics for step change requests. Tags are closed sets only: never ids, user text, org or project.</summary>
public static class ChangeRequestDiagnostics
{
    /// <summary>Meter name; the Inference API's telemetry registration adds it with <c>AddMeter</c>.</summary>
    public const string MeterName = "ReelBolt.Inference.ChangeRequests";

    private static readonly Meter Meter = new(MeterName);

    /// <summary>One per request attempt, tagged <c>step_type</c> and <c>outcome</c> = created | refused_validation | refused_rate_limit | refused_credits | refused_other.</summary>
    public static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "reelbolt.inference.change_requests", "requests", "Step change requests by step type and outcome");

    /// <summary>Scope fraction (0..1) of each created request, tagged <c>step_type</c>.</summary>
    public static readonly Histogram<double> ScopeFraction = Meter.CreateHistogram<double>(
        "reelbolt.inference.change_request_scope_fraction", "fraction", "Fraction of the step a created change request redoes");

    internal static void Count(string stepType, string outcome) =>
        Requests.Add(1, new KeyValuePair<string, object?>("step_type", stepType), new KeyValuePair<string, object?>("outcome", outcome));
}

/// <summary>
/// The request side of step change requests (docs/design/change-requests.md): what each step of a finished
/// run accepts, the price and replay of a request, and turning an accepted request into a
/// <see cref="StepChangeRequest"/> row plus a hidden <see cref="WorkflowDefinitionKind.CompositionReplay"/>
/// run. Authorization is the CALLER's job: every method takes the <see cref="Project"/> the controller
/// already resolved through <c>IProjectAccessService</c>, and every query is scoped by that project's id.
/// </summary>
public interface IStepChangeRequestService
{
    /// <summary>Per finished step result of the run: supported or why not, items and the override catalogue; null when the run is not in the project.</summary>
    Task<StepChangeCapabilitiesResponse?> GetCapabilitiesAsync(Project project, Guid executionId, CancellationToken ct);

    /// <summary>The requests made against the run (as source, or as the request's own run); null when the run is not in the project.</summary>
    Task<StepChangeRequestListResponse?> ListAsync(Project project, Guid executionId, CancellationToken ct);

    /// <summary>The price and replay of a request. Validates like <see cref="RequestAsync"/>; writes and queues nothing.</summary>
    Task<StepChangeRequestResult> EstimateAsync(Project project, Guid executionId, Guid? organizationId, CreateStepChangeRequest body, CancellationToken ct);

    /// <summary>Validate, rate-limit, preflight and queue a change request.</summary>
    /// <exception cref="PlanLimitException">When the plan's output-resolution cap or concurrency cap refuses the run.</exception>
    Task<StepChangeRequestResult> RequestAsync(
        Project project, Guid executionId, Guid userId, Guid? organizationId, CreateStepChangeRequest body, CancellationToken ct,
        ExecutionInitiator initiator = ExecutionInitiator.Human);
}

/// <summary>The outcome of a change request or estimate: created / previewed, or a refusal the controller renders verbatim.</summary>
public sealed record StepChangeRequestResult(
    StepChangeRequestResponse? Created,
    StepChangeEstimatePreviewResponse? Preview,
    int StatusCode,
    string? Code,
    string? Message,
    IReadOnlyDictionary<string, object?>? Extra = null)
{
    public static StepChangeRequestResult Ok(StepChangeRequestResponse created) => new(created, null, 201, null, null);

    public static StepChangeRequestResult Previewed(StepChangeEstimatePreviewResponse preview) => new(null, preview, 200, null, null);

    public static StepChangeRequestResult Refuse(int status, string? code, string message, IReadOnlyDictionary<string, object?>? extra = null) =>
        new(null, null, status, code, message, extra);
}

/// <inheritdoc />
public sealed partial class StepChangeRequestService : IStepChangeRequestService
{
    /// <summary>The label the target step carries in the planner's reasons.</summary>
    public const string TargetLabel = "the step this request changes";

    private readonly InferenceApiDbContext _db;
    private readonly WorkflowExecutionService _executions;
    private readonly IWorkflowCostEstimator? _costEstimator;
    private readonly IPlanGateService? _planGate;
    private readonly IServiceProvider? _services;
    private readonly CompositionRevisionOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StepChangeRequestService> _logger;

    public StepChangeRequestService(
        InferenceApiDbContext db,
        WorkflowExecutionService executions,
        IConfiguration configuration,
        ILogger<StepChangeRequestService> logger,
        IWorkflowCostEstimator? costEstimator = null,
        IPlanGateService? planGate = null,
        IServiceProvider? services = null)
    {
        _services = services;
        _db = db;
        _executions = executions;
        _costEstimator = costEstimator;
        _planGate = planGate;
        _configuration = configuration;
        _options = configuration.GetSection(CompositionRevisionOptions.SectionName).Get<CompositionRevisionOptions>()
                   ?? new CompositionRevisionOptions();
        _logger = logger;
    }

    // ------------------------------------------------------------------
    // Capabilities and list
    // ------------------------------------------------------------------

    public async Task<StepChangeCapabilitiesResponse?> GetCapabilitiesAsync(Project project, Guid executionId, CancellationToken ct)
    {
        StepChangeSourceResolution loaded = await StepChangeRequestSources.LoadAsync(_db, project.Id, executionId, ct);
        if (loaded.Source is not { } source)
            return loaded.StatusCode == 404 ? null : new StepChangeCapabilitiesResponse(executionId, false, []);

        bool finished = StepChangeRequestSources.IsFinished(source.Execution.Status);

        // Items come from the stored output of the supported steps only (a row column, never a storage key).
        HashSet<Guid> wanted = [.. source.Results.Values
            .Where(r => StepChangeCapabilities.For(r.Step.StepType, AgentTypeOf(r.Step)) is { Supported: true, ItemKind: not StepItemKind.None })
            .Select(r => r.StepResultId)];
        Dictionary<Guid, string?> outputs = wanted.Count == 0
            ? []
            : await _db.WorkflowStepResults.AsNoTracking()
                .Where(r => wanted.Contains(r.Id))
                .Select(r => new { r.Id, r.OutputJson })
                .ToDictionaryAsync(r => r.Id, r => r.OutputJson, ct);

        List<StepChangeStepCapabilityResponse> steps = [];
        foreach (StepChangeResult result in source.Results.Values.OrderBy(r => r.StepOrder))
        {
            StepChangeCapability cap = StepChangeCapabilities.For(result.Step.StepType, AgentTypeOf(result.Step));
            bool film = source.IsFilmStep(result, project.Id);
            string? route = film ? StepChangeCapabilities.CompositionRevisionRoute : cap.Route;

            bool supported = (cap.Supported || route is not null) && result.Status == StepStatus.Completed;
            string? reason = supported ? null
                : result.Status != StepStatus.Completed ? "this step was skipped in the run"
                : cap.NotSupportedReason;

            IReadOnlyList<StepItem> items = supported && cap.Supported && outputs.TryGetValue(result.StepResultId, out string? json)
                ? StepChangeCapabilities.ListItems(result.Step.StepType, AgentTypeOf(result.Step), json)
                : [];
            steps.Add(new StepChangeStepCapabilityResponse(
                result.StepOrder,
                result.StepResultId,
                result.Step.StepType.ToString(),
                LabelOf(result.Step),
                supported,
                reason,
                route,
                cap.Supported ? cap.ItemKind.ToString() : StepItemKind.None.ToString(),
                [.. items.Select(i => new StepChangeItemResponse(i.Id, i.Status, i.DurationSec, i.Text))],
                supported && cap.Supported
                    ? [.. cap.Overrides.Select(o => new StepChangeOverrideResponse(o.Key, o.Type.ToString(), o.Min, o.Max, o.MaxLength, o.MaxEntries, o.Values is null ? null : [.. o.Values], o.MultiLine))]
                    : [],
                supported && cap.Supported ? cap.CostNote : null,
                supported && cap.Supported && cap.AcceptsInstructionWithItems,
                film && supported));
        }

        return new StepChangeCapabilitiesResponse(
            source.Execution.Id, finished, steps,
            AppInputs: await AppInputCapabilitiesAsync(project, source, finished, ct));
    }

    /// <summary>The agent type an Agent step runs (its definition in the run's snapshot); null for every other step type.</summary>
    internal static AgentType? AgentTypeOf(WorkflowStep step) =>
        step.StepType == StepType.Agent ? step.AgentDefinition?.AgentType : null;

    public async Task<StepChangeRequestListResponse?> ListAsync(Project project, Guid executionId, CancellationToken ct)
    {
        bool inProject = await _db.WorkflowExecutions.AsNoTracking()
            .AnyAsync(e => e.Id == executionId && e.ProjectId == project.Id, ct);
        if (!inProject)
            return null;

        List<StepChangeRequest> rows = await _db.StepChangeRequests.AsNoTracking()
            .Where(r => r.ProjectId == project.Id && (r.SourceExecutionId == executionId || r.ExecutionId == executionId))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        if (rows.Count == 0)
            return new StepChangeRequestListResponse([]);

        HashSet<Guid> runIds = [.. rows.Select(r => r.ExecutionId)];
        Dictionary<Guid, ExecutionStatus> statuses = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.ProjectId == project.Id && runIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Status })
            .ToDictionaryAsync(e => e.Id, e => e.Status, ct);
        // Cost so far: the credits the request's own run has charged (reused copies charge nothing).
        var charged = await _db.WorkflowStepResults.AsNoTracking()
            .Where(r => runIds.Contains(r.WorkflowExecutionId))
            .Select(r => new { r.WorkflowExecutionId, r.CreditsChargedMillis })
            .ToListAsync(ct);
        // The source run's own workflow id: the id the run page route expects for that run.
        HashSet<Guid> sourceIds = [.. rows.Select(r => r.SourceExecutionId)];
        Dictionary<Guid, Guid> sourceWorkflows = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.ProjectId == project.Id && sourceIds.Contains(e.Id))
            .Select(e => new { e.Id, e.WorkflowDefinitionId })
            .ToDictionaryAsync(e => e.Id, e => e.WorkflowDefinitionId, ct);
        Dictionary<Guid, Guid> sourceDefinitions = await _db.WorkflowExecutions.AsNoTracking()
            .Where(e => e.ProjectId == project.Id && sourceIds.Contains(e.Id))
            .Select(e => new { e.Id, Definition = e.SnapshotDefinitionId ?? e.WorkflowDefinitionId })
            .ToDictionaryAsync(e => e.Id, e => e.Definition, ct);
        Dictionary<Guid, long> cost = charged.GroupBy(c => c.WorkflowExecutionId).ToDictionary(g => g.Key, g => g.Sum(c => c.CreditsChargedMillis));

        // App-input changes (docs/design/app-inputs.md 26): each changed field before and after in words, labelled by the
        // source run's own page. Read only when a listed request is one.
        IReadOnlyDictionary<Guid, IReadOnlyList<StepChangeInputChangeResponse>> inputChanges =
            await ListedInputChangesAsync(project, rows, sourceDefinitions, ct);

        // The steps behind each request's targets: the source run's (the label the owner knows) and the request's
        // own snapshot (the film target is the snapshot's Revise step).
        HashSet<Guid> definitionIds = [.. rows.Select(r => r.WorkflowDefinitionId)];
        foreach (Guid src in sourceIds)
        {
            if (sourceDefinitions.TryGetValue(src, out Guid def))
                definitionIds.Add(def);
        }

        List<WorkflowStep> stepRows = await _db.WorkflowSteps.AsNoTracking()
            .Include(s => s.AgentDefinition)
            .Where(s => definitionIds.Contains(s.WorkflowDefinitionId))
            .ToListAsync(ct);
        Dictionary<(Guid, int), WorkflowStep> stepsByKey = stepRows.GroupBy(s => (s.WorkflowDefinitionId, s.StepOrder)).ToDictionary(g => g.Key, g => g.First());

        // One entry per request (= per replay run), however many target rows it has; status and cost once per run.
        List<StepChangeRequestResponse> requests = [];
        foreach (IGrouping<Guid, StepChangeRequest> run in rows.GroupBy(r => r.ExecutionId))
        {
            List<StepChangeRequest> ordered = [.. run.OrderBy(r => r.TargetStepOrder)];
            StepChangeRequest first = ordered[0];
            statuses.TryGetValue(first.ExecutionId, out ExecutionStatus status);
            double? credits = cost.TryGetValue(first.ExecutionId, out long millis) ? millis / 1000.0 : null;
            sourceDefinitions.TryGetValue(first.SourceExecutionId, out Guid sourceDefinition);
            List<StepChangeTargetResponse> targets = [.. ordered.Select(r =>
            {
                stepsByKey.TryGetValue((r.WorkflowDefinitionId, r.TargetStepOrder), out WorkflowStep? own);
                stepsByKey.TryGetValue((sourceDefinition, r.TargetStepOrder), out WorkflowStep? origin);
                WorkflowStep? labelled = origin ?? own;
                return new StepChangeTargetResponse(
                    r.TargetStepOrder, labelled is null ? r.StepType : LabelOf(labelled), r.StepType, ItemIdsOf(r.ScopeJson), r.Instruction,
                    own?.StepType == StepType.Revise ? "film" : AppInputChangeRows.IsAppInputRow(r.OverridesJson) ? AppInputsTargetKind : "step");
            })];
            requests.Add(ToResponse(first, statuses.ContainsKey(first.ExecutionId) ? status.ToString() : "Unknown", credits,
                CompositionReplayPlan.TryParse(first.ReplayPlanJson),
                sourceWorkflowId: sourceWorkflows.TryGetValue(first.SourceExecutionId, out Guid sw) ? sw : null, targets: targets,
                inputChanges: inputChanges.GetValueOrDefault(first.ExecutionId)));
        }

        return new StepChangeRequestListResponse(requests);
    }

    // ------------------------------------------------------------------
    // Estimate and request
    // ------------------------------------------------------------------

    public async Task<StepChangeRequestResult> EstimateAsync(
        Project project, Guid executionId, Guid? organizationId, CreateStepChangeRequest body, CancellationToken ct)
    {
        (Prepared? prepared, StepChangeRequestResult? refusal) = await PrepareAnyAsync(project, executionId, body, ct);
        if (prepared is null)
            return refusal!;

        WorkflowPreflightResult? preflight = await PreflightAsync(organizationId, prepared, ct);
        return StepChangeRequestResult.Previewed(new StepChangeEstimatePreviewResponse(
            await EstimateOfAsync(organizationId, prepared, preflight, ct), ReplayOf(prepared), TargetsOf(prepared), FilmBaseOf(prepared),
            prepared.AppInputs?.Lines));
    }

    /// <summary>Which film version the request builds on (numbers only), or null when the film is not a target.</summary>
    internal static StepChangeFilmBaseResponse? FilmBaseOf(CompositionRevisionService.FilmContext? film)
    {
        if (film?.BaseVersion is not { } baseVersion)
            return null;
        int? runNumber = film.RunVersion?.VersionNumber;
        bool newer = film.RunVersion is { } own && own.Id != baseVersion.Id;
        string label = newer
            ? $"Builds on version {baseVersion.VersionNumber}, the latest version of the video (this run made version {runNumber})"
            : $"Builds on version {baseVersion.VersionNumber}, the version this run made";
        return new StepChangeFilmBaseResponse(baseVersion.VersionNumber, runNumber, newer, label);
    }

    private static StepChangeFilmBaseResponse? FilmBaseOf(Prepared p) =>
        p.Targets.Any(t => t.IsFilm) ? FilmBaseOf(p.Film) : null;

    public async Task<StepChangeRequestResult> RequestAsync(
        Project project, Guid executionId, Guid userId, Guid? organizationId, CreateStepChangeRequest body, CancellationToken ct,
        ExecutionInitiator initiator = ExecutionInitiator.Human)
    {
        (Prepared? prepared, StepChangeRequestResult? refusal) = await PrepareAnyAsync(project, executionId, body, ct);
        if (prepared is null)
        {
            ChangeRequestDiagnostics.Count(UsesInputs(body) ? AppInputsTag : "unknown", refusal is { StatusCode: 429 } ? "refused_rate_limit" : refusal is { StatusCode: 402 } ? "refused_credits" : refusal is { StatusCode: 400 or 404 or 409 or 422 } ? "refused_validation" : "refused_other");
            return refusal!;
        }

        string stepTypeTag = prepared.StepTypeTag;

        // A restore of this project is rewriting it: no run starts until it is done.
        if (await RestoreRunGate.IsHeldAsync(_db, project.Id, ct))
            return StepChangeRequestResult.Refuse(409, ReelBolt.Inference.Api.Services.History.ProjectHistoryRestoreCodes.RestoreInProgress, RestoreRunGate.Message);

        // Per user + project, rolling hour; revisions and change requests share the bucket. One request (however
        // many targets) is one run, hence one hit: the window counts distinct runs.
        List<DateTime> recent = await RevisionRateWindow.RecentAsync(_db, project.Id, userId, ct);
        int limit = Math.Max(1, _options.MaxPerHourPerUserProject);
        if (RevisionRateWindow.Refusal(recent, limit) is { } rate)
        {
            ChangeRequestDiagnostics.Count(stepTypeTag, "refused_rate_limit");
            return StepChangeRequestResult.Refuse(429, "REVISION_RATE_LIMITED", rate.Message, rate.Extra);
        }

        // The replay renders at the source run's resolution: the plan's cap answers before the preflight.
        WorkflowExecution sourceRun = prepared.Source.Execution;
        if (_planGate is not null && await _planGate.CheckResolutionAsync(project.OrganizationId, sourceRun.OutputResolution, ct) is { } notEntitled)
            throw new PlanLimitException(notEntitled);

        WorkflowPreflightResult? preflight = await PreflightAsync(organizationId, prepared, ct);
        if (preflight is { Refused: true })
        {
            (string message, Dictionary<string, object?> extra) = CompositionRevisionService.ReplayShortfall(preflight);
            ChangeRequestDiagnostics.Count(stepTypeTag, "refused_credits");
            return StepChangeRequestResult.Refuse(402, "INSUFFICIENT_CREDITS", message, extra);
        }

        Guid requestExecutionId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;
        List<StepChangeRequest> rows = [];
        foreach (PreparedTarget t in prepared.Targets)
        {
            rows.Add(new StepChangeRequest
            {
                Id = Guid.NewGuid(),
                ProjectId = project.Id,
                OrganizationId = project.OrganizationId,
                SourceExecutionId = sourceRun.Id,
                TargetStepOrder = t.Result.StepOrder,
                TargetStepResultId = t.Result.StepResultId == Guid.Empty ? null : t.Result.StepResultId,
                StepType = t.Result.Step.StepType.ToString(),
                Instruction = t.IsInputSeed && prepared.AppInputs is { } changed ? changed.Summary : (t.Payload.Instruction ?? string.Empty).Trim(),
                ScopeJson = t.Payload.ItemIds is { Count: > 0 } ids
                    ? JsonSerializer.Serialize(ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    : null,
                OverridesJson = t.IsInputSeed && prepared.AppInputs is { } stored
                    ? AppInputChangeRows.Serialize(stored.Changed, stored.BriefLines)
                    : t.Payload.Overrides is { Count: > 0 } ? OverridesOf(t.Payload) : null,
                ExecutionId = requestExecutionId,
                WorkflowDefinitionId = prepared.Snapshot.Id,
                ReplayPlanJson = prepared.Plan.Serialize(),
                ScopeFraction = t.ScopeFraction,
                RequestedByUserId = userId,
                CreatedAt = now
            });
        }

        // The film as one of several targets: ALSO a composition revision row of the same run, so the engine's Revise
        // step finds its request by execution exactly as for a plain revision, and the video's history lists the run.
        CompositionRevision? revision = null;
        CompositionWorkspace? newWorkspace = null;
        if (prepared.Film is { } film && prepared.Targets.FirstOrDefault(t => t.IsFilm) is { } filmTarget
            && FilmService() is { } filmService)
        {
            (revision, newWorkspace) = filmService.NewReplayRevision(
                project, sourceRun, film, requestExecutionId, prepared.Snapshot.Id,
                (filmTarget.Payload.Instruction ?? string.Empty).Trim(), prepared.Plan, userId);
        }

        // Rows, revision and snapshot land in ONE save (one transaction) BEFORE the run is queued, so the engine can
        // never consume an execution whose request it cannot find; a refused queue removes them again.
        _db.WorkflowDefinitions.Add(prepared.Snapshot);
        _db.StepChangeRequests.AddRange(rows);
        if (revision is not null)
            _db.CompositionRevisions.Add(revision);
        try
        {
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (newWorkspace is not null && revision is not null && FilmService() is { } conflictService)
            {
                // A concurrent request (or the engine's capture) created the run's origin workspace first: use theirs, once.
                _db.Entry(newWorkspace).State = EntityState.Detached;
                Guid? existing = await conflictService.FindOriginWorkspaceIdAsync(project.Id, sourceRun.Id, ct);
                if (existing is null)
                    throw;
                revision.WorkspaceId = existing.Value;
                newWorkspace = null;
                await _db.SaveChangesAsync(ct);
            }
        }
        catch
        {
            // Nothing was saved; drop what this attempt tracked so the scoped context is not left with Added rows.
            if (newWorkspace is not null)
                _db.Entry(newWorkspace).State = EntityState.Detached;
            throw;
        }

        try
        {
            // The run's request is the ORIGINAL brief (every replayed step reads it as it did); the
            // owner's change lives on the request rows, which each target step's executor reads.
            // An app-input change runs with the NEW values and the request text they compose (docs/design/app-inputs.md 25).
            await _executions.QueueAsync(
                project.Id, prepared.Snapshot.Id, userId,
                prepared.AppInputs is { } inputs ? inputs.UserRequest : sourceRun.UserRequest, initiator, ct,
                regenerateVideoClips: false,
                prepared.AppInputs is { } runInputs
                    ? ReplayRunChoicesWith(sourceRun, runInputs.Values, runInputs.BriefChanged, runInputs.Brief)
                    : CompositionRevisionService.ReplayRunChoices(sourceRun),
                WorkflowDefinitionKind.CompositionReplay, requestExecutionId);
        }
        catch
        {
            _db.StepChangeRequests.RemoveRange(rows);
            if (revision is not null)
                _db.CompositionRevisions.Remove(revision);
            _db.WorkflowDefinitions.Remove(prepared.Snapshot);
            await _db.SaveChangesAsync(CancellationToken.None);
            // The origin workspace we created goes with the request, but only when nothing else came to reference it meanwhile.
            if (newWorkspace is not null)
            {
                Guid created = newWorkspace.Id;
                bool shared = await _db.CompositionRevisions.AnyAsync(r => r.WorkspaceId == created)
                    || await _db.CompositionWorkspaceVersions.AnyAsync(v => v.WorkspaceId == created);
                if (!shared)
                {
                    _db.CompositionWorkspaces.Remove(newWorkspace);
                    await _db.SaveChangesAsync(CancellationToken.None);
                }
            }
            throw;
        }

        ChangeRequestDiagnostics.Count(stepTypeTag, "created");
        foreach (PreparedTarget t in prepared.Targets.Where(t => !t.IsFilm))
        {
            ChangeRequestDiagnostics.ScopeFraction.Record(t.ScopeFraction, new KeyValuePair<string, object?>("step_type", t.Result.Step.StepType.ToString()));
        }

        _logger.LogInformation(
            "Step change request queued as execution {ExecutionId} (source run {SourceRun}, {TargetCount} target(s): {Targets})",
            requestExecutionId, sourceRun.Id, rows.Count, string.Join(",", rows.Select(r => $"{r.TargetStepOrder}:{r.StepType}")));

        return StepChangeRequestResult.Ok(ToResponse(
            rows[0], ExecutionStatus.Queued.ToString(), null, prepared.Plan,
            await EstimateOfAsync(organizationId, prepared, preflight, ct), ReplayOf(prepared),
            sourceWorkflowId: sourceRun.WorkflowDefinitionId, targets: TargetsOf(prepared), filmBase: FilmBaseOf(prepared),
            inputChanges: prepared.AppInputs?.Lines));
    }

    // ------------------------------------------------------------------
    // Shared preparation: validate, resolve, plan, snapshot (writes nothing)
    // ------------------------------------------------------------------

    /// <summary>One validated target: its step, effective payload (carry-forward applied), cost share and whether it is the film.</summary>
    /// <remarks><paramref name="IsInputSeed"/>: a step re-made because an app field or run setting it uses changes (docs/design/app-inputs.md 29).</remarks>
    private sealed record PreparedTarget(StepChangeResult Result, ChangeRequestPayload Payload, double ScopeFraction, bool IsFilm, string? OutputJson, bool IsInputSeed = false);

    private sealed record Prepared(
        StepChangeSource Source,
        IReadOnlyList<PreparedTarget> Targets,
        CompositionReplayPlan Plan,
        WorkflowDefinition Snapshot,
        WorkflowDefinition PricingSnapshot,
        CompositionRevisionService.FilmContext? Film,
        PreparedAppInputs? AppInputs = null)
    {
        /// <summary>The first target (lowest step order): the one a response's top-level fields are filled from.</summary>
        public PreparedTarget First => Targets[0];

        /// <summary>The metric tag: the step type of a single target, <c>multi</c> for several.</summary>
        public string StepTypeTag => AppInputs is not null && Targets.All(t => t.IsInputSeed) ? AppInputsTag
            : Targets.Count == 1 ? First.Result.Step.StepType.ToString() : "multi";

        /// <summary>The app-input values the replay runs with: the request's when it changes them, else the source run's own.</summary>
        public IReadOnlyDictionary<string, JsonElement>? RunValues =>
            AppInputs is { } inputs ? inputs.Values : RunBriefJson.TryParse(Source.Execution.RunBriefJson)?.Inputs;

        /// <summary>Each non-film target's price multiplier (step order to the share of its items re-made); the film is priced whole.</summary>
        public IReadOnlyDictionary<int, double> StepScales =>
            Targets.Where(t => !t.IsFilm).ToDictionary(t => t.Result.StepOrder, t => t.ScopeFraction);
    }

    private CompositionRevisionService? FilmService() =>
        _services?.GetService(typeof(ICompositionRevisionService)) as CompositionRevisionService;

    /// <summary>A refused target keeps its status and code; in the multi form the body also names which entry failed.</summary>
    private static StepChangeRequestResult TargetRefusal(StepChangeSourceResolution resolved, int? index)
    {
        string message = resolved.Message ?? "The change cannot be made.";
        return StepChangeRequestResult.Refuse(
            resolved.StatusCode, resolved.Code, message,
            index is { } i
                ? new Dictionary<string, object?> { ["errors"] = new[] { new { path = $"targets[{i}]", message } } }
                : null);
    }

    private static StepChangeRequestResult Invalid(List<ChangeRequestViolation> violations) =>
        StepChangeRequestResult.Refuse(
            400, null, "The change request is not valid.",
            new Dictionary<string, object?>
            {
                ["errors"] = violations.Select(v => new { path = v.Path, message = v.Message }).ToList()
            });

    /// <remarks>
    /// <paramref name="extraSeeds"/> (docs/design/app-inputs.md 29): steps the same request also re-makes because an app field or
    /// run setting they use changes. They count as other targets: a target that reads one is re-made whole, and the film may
    /// join them as the only step target.
    /// </remarks>
    private async Task<(Prepared?, StepChangeRequestResult?)> PrepareAsync(
        Project project, Guid executionId, CreateStepChangeRequest? body, CancellationToken ct, IReadOnlySet<int>? extraSeeds = null)
    {
        bool hasExtraSeeds = extraSeeds is { Count: > 0 };
        if (body is null)
            return (null, StepChangeRequestResult.Refuse(400, null, "Describe the change to make."));

        // The request's targets: the single-target body is one target; `targets` replaces the top-level fields.
        bool multiForm = body.Targets is not null;
        List<StepChangeTargetRequest> requested;
        if (multiForm)
        {
            if (body.StepOrder is not null || body.StepResultId is not null || body.Instruction is not null
                || body.ItemIds is not null || body.Overrides is not null)
            {
                return (null, Invalid([new("targets", "Send either targets or the single-target fields (stepOrder, instruction, ...), not both.")]));
            }

            if (body.Targets!.Count is 0 or > StepChangeLimits.MaxTargets)
                return (null, Invalid([new("targets", $"Send between 1 and {StepChangeLimits.MaxTargets} targets.")]));
            if (body.Targets.Any(t => t is null))
                return (null, Invalid([new("targets", "A target cannot be empty.")]));
            requested = [.. body.Targets];
        }
        else
        {
            requested = [new StepChangeTargetRequest(body.StepOrder, body.StepResultId, body.Instruction, body.ItemIds, body.Overrides)];
        }

        StepChangeSourceResolution resolved = await StepChangeRequestSources.ResolveAsync(
            _db, project.Id, executionId, new StepChangeTarget(requested[0].StepOrder, requested[0].StepResultId), ct);
        if (resolved is not { Source: { } source, Target: { } firstTarget })
            return (null, TargetRefusal(resolved, multiForm ? 0 : null));

        List<StepChangeResult> results = [firstTarget];
        for (int i = 1; i < requested.Count; i++)
        {
            StepChangeSourceResolution next = StepChangeRequestSources.ResolveTarget(
                source, new StepChangeTarget(requested[i].StepOrder, requested[i].StepResultId));
            if (next.Target is not { } nextTarget)
                return (null, TargetRefusal(next, i));
            results.Add(nextTarget);
        }

        List<ChangeRequestViolation> violations = [];
        for (int i = 0; i < results.Count; i++)
        {
            if (results.Take(i).Any(r => r.StepOrder == results[i].StepOrder))
                violations.Add(new($"targets[{i}]", "Two targets name the same step. Combine them into one target."));
        }

        if (violations.Count > 0)
            return (null, Invalid(violations));

        bool[] film = [.. results.Select(r => source.IsFilmStep(r, project.Id))];
        for (int i = 0; i < results.Count; i++)
        {
            if (!film[i])
                continue;
            // The film alone is changed through a video revision (back compat); it can only join other changes.
            if ((!multiForm || results.Count < 2) && !hasExtraSeeds)
            {
                return (null, StepChangeRequestResult.Refuse(
                    409, "USE_COMPOSITION_REVISION",
                    "The film is changed through a video revision, not a step change request.",
                    new Dictionary<string, object?> { ["route"] = StepChangeCapabilities.CompositionRevisionRoute }));
            }
        }

        if (film.Count(f => f) > 1)
            return (null, Invalid([new("targets", "Only one target can be the film.")]));

        // Validate every target (all violations are reported together, each under its own targets[i] path).
        List<ChangeRequestPayload> payloads = [];
        List<string?> outputs = [];
        for (int i = 0; i < results.Count; i++)
        {
            StepChangeResult target = results[i];
            StepChangeTargetRequest req = requested[i];
            string prefix = multiForm ? $"targets[{i}]." : string.Empty;

            if (film[i])
            {
                string instruction = (req.Instruction ?? string.Empty).Trim();
                if (instruction.Length == 0)
                    violations.Add(new($"{prefix}instruction", "Say what should change in the film."));
                if (instruction.Length > 4000)
                    violations.Add(new($"{prefix}instruction", "The request can be at most 4000 characters."));
                if (req.ItemIds is { Count: > 0 })
                    violations.Add(new($"{prefix}itemIds", "The film change takes an instruction only."));
                if (req.Overrides is { Count: > 0 })
                    violations.Add(new($"{prefix}overrides", "The film change takes an instruction only."));
                payloads.Add(new ChangeRequestPayload(instruction, null, null));
                outputs.Add(null);
                continue;
            }

            ChangeRequestPayload payload = new(
                req.Instruction,
                req.ItemIds,
                req.Overrides?.ToDictionary(
                    p => p.Key,
                    p => (IReadOnlyDictionary<string, JsonElement>)(p.Value ?? new Dictionary<string, JsonElement>())));
            string? outputJson = await _db.WorkflowStepResults.AsNoTracking()
                .Where(r => r.Id == target.StepResultId && r.WorkflowExecutionId == source.Execution.Id)
                .Select(r => r.OutputJson)
                .FirstOrDefaultAsync(ct);
            violations.AddRange(ChangeRequestValidator.Validate(target.Step.StepType, AgentTypeOf(target.Step), payload, outputJson)
                .Select(v => new ChangeRequestViolation(prefix + v.Path, v.Message)));
            payloads.Add(payload);
            outputs.Add(outputJson);
        }

        if (violations.Count > 0)
            return (null, Invalid(violations));

        // The film's context (workspace, base version, reviser) or the refusal a revision would give.
        CompositionRevisionService.FilmContext? filmContext = null;
        if (film.Any(f => f))
        {
            CompositionRevisionService? filmService = FilmService();
            if (filmService is null)
            {
                return (null, StepChangeRequestResult.Refuse(
                    503, "REVISION_AGENT_UNAVAILABLE", "Changes cannot be made on this installation yet. Please try again later."));
            }

            (filmContext, CompositionRevisionRequestResult? filmRefusal) = await filmService.ResolveFilmContextAsync(project, source.Execution, ct);
            if (filmContext is null)
                return (null, StepChangeRequestResult.Refuse(filmRefusal!.StatusCode, filmRefusal.Code, filmRefusal.Message ?? "The film cannot be changed.", filmRefusal.Extra));
        }

        // "Changing a change": the stored payload also carries the prior request's overrides for the items the new one
        // re-makes. The merge never widens scope (only items the new request names gain overrides), so the fraction,
        // the estimate and the exact price are all taken on the EFFECTIVE payload: a long durationSec or text carried
        // from the earlier request is weighed.
        WorkflowStepDependencyGraph graph = WorkflowStepDependencyGraph.Build(source.Steps);
        List<PreparedTarget> prepared = [];
        for (int i = 0; i < results.Count; i++)
        {
            StepChangeResult target = results[i];
            ChangeRequestPayload payload = payloads[i];
            if (film[i])
            {
                prepared.Add(new PreparedTarget(target, payload, 1d, true, null));
                continue;
            }

            // Narration lines the request leaves alone but whose pronunciation changed since the run are made again:
            // named before an earlier request's overrides are brought forward (they keep them), and here so the fraction, the hold, the exact price and the listed items are the work the engine does.
            payload = await WithStaleNarrationLinesAsync(project.Id, target.Step, payload, outputs[i], ct);

            string? priorOverrides = await FindPriorOverridesAsync(project.Id, source.Execution.Id, target.StepOrder, ct);
            if (priorOverrides is not null)
            {
                HashSet<string> allowed = [.. StepChangeCapabilities.For(target.Step.StepType, AgentTypeOf(target.Step)).Overrides.Select(o => o.Key)];
                ChangeRequestPayload effective = ChangeRequestCarryForward.Apply(payload, priorOverrides, allowed);
                if (ChangeRequestValidator.Validate(target.Step.StepType, AgentTypeOf(target.Step), effective, outputs[i]).Count == 0)
                    payload = effective;
            }

            double scopeFraction = StepChangeScope.Compute(target.Step.StepType, AgentTypeOf(target.Step), payload, outputs[i]);

            // A target that reads another target is re-made whole: its untouched items were built from the other
            // target's OLD output, so carrying them over would be stale. Name every item (each keeps its own
            // overrides); the executors' content-addressed reuse keeps genuinely unchanged items free.
            if ((results.Count > 1 || hasExtraSeeds)
                && results.Where((o, j) => j != i).Select(o => o.StepOrder).Concat(extraSeeds ?? new HashSet<int>())
                    .Any(o => graph.DownstreamOf(o).Contains(target.StepOrder))
                && target.Step.StepType != StepType.Agent
                && StepChangeCapabilities.ListItems(target.Step.StepType, AgentTypeOf(target.Step), outputs[i]) is { Count: > 0 } all)
            {
                // (A script, an Agent step, is never widened: its item list is also the scope of the owner's instruction,
                // which must keep applying only to the scenes they named.)
                ChangeRequestPayload whole = payload with { ItemIds = [.. all.Select(it => it.Id)] };
                if (ChangeRequestValidator.Validate(target.Step.StepType, AgentTypeOf(target.Step), whole, outputs[i]).Count == 0)
                {
                    payload = whole;
                    scopeFraction = 1d;
                }
                else
                {
                    _logger.LogWarning(
                        "Could not re-make target step {StepOrder} ({StepType}) whole because it reads another target; it keeps its narrow scope",
                        target.StepOrder, target.Step.StepType);
                }
            }

            prepared.Add(new PreparedTarget(target, payload, scopeFraction, false, outputs[i]));
        }

        // Responses and rows list the targets by step order; the first is the top-level one.
        prepared = [.. prepared.OrderBy(t => t.Result.StepOrder)];
        int? filmOrder = prepared.FirstOrDefault(t => t.IsFilm)?.Result.StepOrder;
        CompositionReplayPlan plan = CompositionReplayPlanner.Plan(
            source.Steps, source.ResultIds, [.. prepared.Select(t => t.Result.StepOrder)], CompositionCutPolicy.KeepCut,
            source.Execution.Id, source.OriginWorkflowDefinitionId, TargetLabel, prepared[0].Result.Step.StepType.ToString(), filmOrder);
        WorkflowDefinition snapshot = BuildSnapshot(project.Id, source, plan, null, filmOrder, filmContext?.ReviserAgentId);
        // What the hold and estimate are priced from: the same steps with each target's config sized for the request's
        // overrides (the run itself executes the unchanged snapshot; each take reads its own override).
        WorkflowDefinition pricing = BuildSnapshot(
            project.Id, source, plan, prepared.Where(t => !t.IsFilm).ToDictionary(t => t.Result.StepOrder, t => t.Payload), filmOrder, filmContext?.ReviserAgentId);

        // docs/design/app-inputs.md 29: a run whose app chose its agents' models keeps them on a step change too, as a revision
        // does (28.4): the replay carries the page, so the engine lays the same choices on the replayed agents and holds at them.
        if (extraSeeds is null && await AgentModelPageAsync(project, source, ct) is { } page)
        {
            snapshot.AppManifestJson = page;
            pricing.AppManifestJson = page;
        }

        return (new Prepared(source, prepared, plan, snapshot, pricing, filmContext), null);
    }

    /// <summary>Most source-run hops walked back to find the last request on the same step.</summary>
    private const int MaxCarryForwardHops = 8;

    /// <summary>
    /// The stored (already effective) overrides of the LAST request that targeted this step order, walking back
    /// through the source chain (each request's source run) so an intervening change to another step does not lose
    /// them. Bounded, cycle-safe and project-scoped.
    /// </summary>
    private async Task<string?> FindPriorOverridesAsync(Guid projectId, Guid executionId, int stepOrder, CancellationToken ct)
    {
        HashSet<Guid> seen = [];
        Guid? current = executionId;
        for (int hop = 0; hop < MaxCarryForwardHops && current is { } exec && seen.Add(exec); hop++)
        {
            var row = await _db.StepChangeRequests.AsNoTracking()
                .Where(r => r.ProjectId == projectId && r.ExecutionId == exec)
                .OrderByDescending(r => r.TargetStepOrder == stepOrder)
                .ThenBy(r => r.TargetStepOrder)
                .Select(r => new { r.TargetStepOrder, r.OverridesJson, r.SourceExecutionId })
                .FirstOrDefaultAsync(ct);
            if (row is null)
                return null;
            if (row.TargetStepOrder == stepOrder)
                return AppInputChangeRows.IsAppInputRow(row.OverridesJson) ? null : row.OverridesJson;
            current = row.SourceExecutionId;
        }
        return null;
    }

    /// <summary>
    /// The per-request snapshot of the source run's workflow: every step cloned at its own order (so every
    /// step-order reference resolves as it did); each target clone is never served from the step cache, and the
    /// film target (when the request has one) becomes the <c>Revise</c> step exactly as a composition revision's replay.
    /// <paramref name="pricedWith"/> (step order to payload) sizes the AudioGenerate targets' config for the price.
    /// Not saved here.
    /// </summary>
    internal static WorkflowDefinition BuildSnapshot(
        Guid projectId, StepChangeSource source, CompositionReplayPlan plan,
        IReadOnlyDictionary<int, ChangeRequestPayload>? pricedWith = null, int? filmStepOrder = null, Guid? reviserAgentId = null)
    {
        string baseName = string.IsNullOrWhiteSpace(source.OriginWorkflowName) ? "a video" : source.OriginWorkflowName!;
        WorkflowDefinition snapshot = new()
        {
            Id = Guid.NewGuid(),
            Name = CompositionRevisionService.Truncate($"Changes to {baseName}", 200),
            ProjectId = projectId,
            Kind = WorkflowDefinitionKind.CompositionReplay,
            RequiresUserInput = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        foreach (WorkflowStep step in source.Steps.OrderBy(s => s.StepOrder))
        {
            WorkflowStep copy = WorkflowStepCloner.Clone(step, snapshot.Id);
            if (plan.IsTarget(step.StepOrder))
                copy.CacheMode = StepCacheMode.Never;
            if (pricedWith is not null && step.StepType == StepType.AudioGenerate && pricedWith.TryGetValue(step.StepOrder, out ChangeRequestPayload? priced))
                copy.AudioGenerateConfigJson = StepChangePricing.AudioConfigWithOverrides(copy.AudioGenerateConfigJson, priced.Overrides);
            if (filmStepOrder == step.StepOrder && reviserAgentId is { } reviser)
                copy = CompositionRevisionService.ReplayReviseStep(copy.Id, snapshot.Id, step.StepOrder, reviser);
            snapshot.Steps.Add(copy);
        }
        return snapshot;
    }

    // ------------------------------------------------------------------
    // Price and replay description
    // ------------------------------------------------------------------

    /// <summary>The B7 preflight over exactly what will hold credits (Revise + Run + every decision that MAY be re-made); null when billing is off.</summary>
    private async Task<WorkflowPreflightResult?> PreflightAsync(Guid? organizationId, Prepared p, CancellationToken ct)
    {
        if (_costEstimator is not { BillingEnabled: true })
            return null;
        using IDisposable chosenModels = (await ChosenModelsAsync(p, ct)).Enter();
        return await _costEstimator.PreflightChargedAsync(
            organizationId, CompositionRevisionService.RunSteps(p.PricingSnapshot),
            CompositionRevisionService.ChargedStepOrders(p.PricingSnapshot, p.Plan, includeConditional: true),
            p.Source.Execution.OutputResolution, p.StepScales, ct);
    }

    /// <summary>
    /// The app's chosen models for the price of this replay (docs/design/app-inputs.md 29): entered only when the replay's
    /// definition carries the app page, exactly when the engine holds at them; nothing otherwise.
    /// </summary>
    private Task<ReplayModelChoices> ChosenModelsAsync(Prepared p, CancellationToken ct) =>
        ReplayModelPricing.ResolveAsync(
            _services?.GetService(typeof(IAppInputModelResolverSource)) as IAppInputModelResolverSource,
            p.Source.Execution.OrganizationId, p.Source.Execution.ProjectId, p.Snapshot.AppManifestJson, p.RunValues, ct);

    /// <summary>
    /// The revision panel's range: the certain steps' low..high, widened by the decisions that may be
    /// re-made; <c>required</c> is what the preflight must be able to hold. Null when nothing can be shown.
    /// </summary>
    private async Task<StepChangeEstimateResponse?> EstimateOfAsync(
        Guid? organizationId, Prepared p, WorkflowPreflightResult? hold, CancellationToken ct)
    {
        if (_costEstimator is not { BillingEnabled: true } estimator || hold is null || !hold.Estimate.BillingEnabled || !hold.Evaluated)
            return null;
        try
        {
            using IDisposable chosenModels = (await ChosenModelsAsync(p, ct)).Enter();
            int? resolution = p.Source.Execution.OutputResolution;
            WorkflowCostEstimate certain = WorkflowCostEstimator.ScaleSteps(await estimator.EstimateAsync(
                organizationId, CompositionRevisionService.ChargedSteps(p.PricingSnapshot, p.Plan, includeConditional: false), resolution, ct), p.StepScales)!;
            List<WorkflowStep> conditionalSteps = [.. CompositionRevisionService.ChargedSteps(p.PricingSnapshot, p.Plan, includeConditional: true)
                .Except(CompositionRevisionService.ChargedSteps(p.PricingSnapshot, p.Plan, includeConditional: false))];
            WorkflowCostEstimate conditional = conditionalSteps.Count > 0
                ? WorkflowCostEstimator.ScaleSteps(await estimator.EstimateAsync(organizationId, conditionalSteps, resolution, ct), p.StepScales)!
                : WorkflowCostEstimate.Disabled;
            long balance = hold.BalanceMillis;
            long required = hold.Estimate.RequiredMillis;
            StepChangeExactPrice? exact = await ExactOfAsync(p, certain.HighMillis + conditional.HighMillis, ct);
            return new StepChangeEstimateResponse(
                true, certain.LowMillis / 1000.0, (certain.HighMillis + conditional.HighMillis) / 1000.0,
                required / 1000.0, balance / 1000.0, balance >= required, exact is null ? null : exact.Millis / 1000.0, exact?.Note);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Could not estimate a change request for organization {OrganizationId}", organizationId);
            return null;
        }
    }

    /// <summary>
    /// A Voiceover change plus the lines it does not name whose spoken form differs under the project's CURRENT
    /// pronunciations (saved or edited after the run being changed). A whole-step request, another step type or an
    /// unchanged glossary returns the payload as it came.
    /// </summary>
    private async Task<ChangeRequestPayload> WithStaleNarrationLinesAsync(
        Guid projectId, WorkflowStep step, ChangeRequestPayload payload, string? outputJson, CancellationToken ct)
    {
        if (step.StepType != StepType.Voiceover || string.IsNullOrWhiteSpace(outputJson)
            || payload.Overrides?.ContainsKey(ChangeRequestValidator.AllItems) == true)
            return payload;

        List<ProjectPronunciationEntry> rows = await _db.ProjectPronunciationEntries.AsNoTracking()
            .Where(e => e.ProjectId == projectId).ToListAsync(ct);
        IReadOnlyList<GlossaryEntry> entries = [.. rows
            .OrderBy(r => r.Term, StringComparer.Ordinal)
            .Select(r => new GlossaryEntry(r.Term, r.Spelling, r.CaseSensitive))];
        VoiceoverStepConfig? config = string.IsNullOrWhiteSpace(step.VoiceoverConfigJson) ? null : VoiceoverStepConfig.Parse(step.VoiceoverConfigJson);
        var glossary = NarrationGlossaryStaleness.CurrentGlossary.From(entries, config?.PronunciationOverrides?.Select(o => o.Term));
        IReadOnlySet<string> stale = NarrationGlossaryStaleness.StaleLineIds(outputJson, glossary);
        if (stale.Count == 0)
            return payload;

        HashSet<string> named = new(payload.ItemIds ?? [], StringComparer.Ordinal);
        if (payload.Overrides is not null)
            named.UnionWith(payload.Overrides.Keys);
        List<string> extra = [.. stale.Where(id => !named.Contains(id)).OrderBy(id => id, StringComparer.Ordinal)];
        return extra.Count == 0 ? payload : payload with { ItemIds = [.. payload.ItemIds ?? [], .. extra] };
    }

    /// <summary>
    /// The exact price of the redone items of the target steps (null unless every target is computable from held
    /// prices, and never when the film is one of them); never above the range's top.
    /// </summary>
    private async Task<StepChangeExactPrice?> ExactOfAsync(Prepared p, long ceilingMillis, CancellationToken ct)
    {
        if (_services?.GetService(typeof(IInferenceProviderResolver)) is not IInferenceProviderResolver providers
            || _services.GetService(typeof(IModelPriceService)) is not IModelPriceService prices
            || p.Targets.Any(t => t.IsFilm) || p.AppInputs is not null)
            return null;
        long millis = 0;
        List<string> notes = [];
        StepChangeExactPrice? first = null;
        foreach (PreparedTarget t in p.Targets)
        {
            StepChangeExactPrice? exact = await StepChangeExactPriceCalculator.TryComputeAsync(
                t.Result.Step, t.Payload, t.OutputJson, providers, prices, ct);
            if (exact is null)
                return null;
            first ??= exact;
            millis += exact.Millis;
            if (exact.Note is { Length: > 0 } note && !notes.Contains(note))
                notes.Add(note);
        }

        return first! with { Millis = Math.Min(millis, ceilingMillis), Note = notes.Count == 0 ? first!.Note : string.Join(" ", notes) };
    }

    private static StepChangeReplayResponse ReplayOf(Prepared p)
    {
        Dictionary<int, WorkflowStep> byOrder = p.Source.Steps.ToDictionary(s => s.StepOrder);
        return new StepChangeReplayResponse([.. p.Plan.Steps.Select(st => new StepChangeReplayStepResponse(
            st.StepOrder, LabelOf(byOrder[st.StepOrder]), st.Disposition.ToString(), st.Reason))]);
    }

    private static StepChangeRequestResponse ToResponse(
        StepChangeRequest r, string status, double? costCredits, CompositionReplayPlan? plan,
        StepChangeEstimateResponse? estimate = null, StepChangeReplayResponse? replay = null, Guid? sourceWorkflowId = null,
        IReadOnlyList<StepChangeTargetResponse>? targets = null, StepChangeFilmBaseResponse? filmBase = null,
        IReadOnlyList<StepChangeInputChangeResponse>? inputChanges = null)
    {
        IReadOnlyList<string> itemIds = ItemIdsOf(r.ScopeJson);

        StepChangePlanSummaryResponse? summary = plan is null
            ? null
            : new StepChangePlanSummaryResponse(
                plan.Steps.Count(s => s.Disposition is ReplayStepDisposition.Revise or ReplayStepDisposition.Run or ReplayStepDisposition.KeepIfCompatible),
                plan.Steps.Count(s => s.Disposition == ReplayStepDisposition.Reuse),
                plan.Steps.Count(s => s.Disposition == ReplayStepDisposition.Skip));
        return new StepChangeRequestResponse(
            r.Id, r.ExecutionId, r.WorkflowDefinitionId, r.SourceExecutionId, r.TargetStepOrder, r.TargetStepResultId,
            r.StepType, r.Instruction, itemIds, status, costCredits, r.CreatedAt, estimate, replay, summary, sourceWorkflowId, targets, filmBase,
            inputChanges);
    }

    private static IReadOnlyList<string> ItemIdsOf(string? scopeJson)
    {
        if (string.IsNullOrWhiteSpace(scopeJson))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(scopeJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<StepChangeTargetResponse> TargetsOf(Prepared p) =>
    [
        .. p.Targets.Select(t => new StepChangeTargetResponse(
            t.Result.StepOrder, LabelOf(t.Result.Step), t.Result.Step.StepType.ToString(),
            t.Payload.ItemIds is { Count: > 0 } ids ? [.. ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] : [],
            t.IsInputSeed && p.AppInputs is { } inputs ? inputs.Summary : (t.Payload.Instruction ?? string.Empty).Trim(),
            t.IsFilm ? "film" : t.IsInputSeed ? AppInputsTargetKind : "step"))
    ];

    private static string OverridesOf(ChangeRequestPayload payload)
    {
        using JsonDocument doc = JsonDocument.Parse(payload.ToCanonicalJson());
        return doc.RootElement.GetProperty("overrides").GetRawText();
    }

    private static string LabelOf(WorkflowStep step) =>
        string.IsNullOrWhiteSpace(step.Label) ? step.AgentDefinition?.Name ?? step.StepType.ToString() : step.Label!;
}
