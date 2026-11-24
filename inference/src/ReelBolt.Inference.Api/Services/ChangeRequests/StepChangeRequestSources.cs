using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Inference.Api.Services.ChangeRequests;

/// <summary>One finished step result of the seed run, as the capabilities and the planner need it.</summary>
public sealed record StepChangeResult(
    int StepOrder,
    Guid StepResultId,
    WorkflowStep Step,
    StepStatus Status,
    string? OutputStorageKey);

/// <summary>
/// The run a change request is seeded from: its steps (its own snapshot's when the engine took one), the
/// latest finished result of each step, and the user's workflow the lineage started from.
/// </summary>
public sealed record StepChangeSource(
    WorkflowExecution Execution,
    IReadOnlyList<WorkflowStep> Steps,
    IReadOnlyDictionary<int, StepChangeResult> Results,
    Guid? OriginWorkflowDefinitionId,
    string? OriginWorkflowName)
{
    /// <summary>Each step order's latest Completed/Skipped result id, as the planner takes it.</summary>
    public IReadOnlyDictionary<int, Guid> ResultIds => Results.ToDictionary(p => p.Key, p => p.Value.StepResultId);

    /// <summary>
    /// True for the step that made the film: a Revise step, or an Agent step whose output is a rendered
    /// file in the project's output folder. Its changes go through a composition revision.
    /// </summary>
    public bool IsFilmStep(StepChangeResult result, Guid projectId) =>
        result.Step.StepType == StepType.Revise
        || (result.Step.StepType == StepType.Agent
            && result.OutputStorageKey is { } key
            && key.StartsWith($"projects/{projectId}/outputFiles/", StringComparison.Ordinal));
}

/// <summary>How a target step is named in a request: by its order or by one of its result ids.</summary>
public readonly record struct StepChangeTarget(int? StepOrder, Guid? StepResultId);

/// <summary>The outcome of resolving a seed: the source and target, or a refusal the service renders.</summary>
public sealed record StepChangeSourceResolution(
    StepChangeSource? Source,
    StepChangeResult? Target,
    int StatusCode,
    string? Code,
    string? Message)
{
    public bool Found => Source is not null;
}

/// <summary>
/// Finds the run a change request is seeded from (docs/design/change-requests.md "Model"). Every query is
/// scoped by the project the caller was authorized for, which is the guard for these unfiltered child tables.
/// </summary>
public static class StepChangeRequestSources
{
    /// <summary>
    /// Loads execution <paramref name="executionId"/> of <paramref name="projectId"/> and its finished results.
    /// 404 when the run is not in the project. Does not require the run to be finished (capabilities do not).
    /// </summary>
    public static async Task<StepChangeSourceResolution> LoadAsync(
        InferenceApiDbContext db, Guid projectId, Guid executionId, CancellationToken ct)
    {
        WorkflowExecution? execution = await db.WorkflowExecutions.AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == executionId && e.ProjectId == projectId, ct);
        if (execution is null)
            return Refuse(404, null, "Run not found.");

        // The steps the run executed: its own snapshot's when the engine took one (its results reference
        // those), else the workflow's.
        Guid stepsDefinitionId = execution.SnapshotDefinitionId ?? execution.WorkflowDefinitionId;
        List<WorkflowStep> steps = await db.WorkflowSteps.AsNoTracking()
            .Include(s => s.AgentDefinition)
            .Where(s => s.WorkflowDefinitionId == stepsDefinitionId)
            .OrderBy(s => s.StepOrder)
            .ToListAsync(ct);
        if (steps.Count == 0)
            return Refuse(409, "RUN_STEPS_UNAVAILABLE", "The workflow that made this run no longer exists, so its steps cannot be changed.");

        HashSet<Guid> stepIds = [.. steps.Select(s => s.Id)];
        var rows = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == execution.Id && stepIds.Contains(r.WorkflowStepId) && r.SupersededAt == null
                        && (r.Status == StepStatus.Completed || r.Status == StepStatus.Skipped))
            .Select(r => new { r.Id, r.WorkflowStepId, r.Status, r.OutputStorageKey, r.ExecutedAt, r.CompletedAt })
            .ToListAsync(ct);

        // A run with a result whose output is not served cannot seed a change: it answers like a run that is not there.
        if ((await Outputs.OutputGate.HeldResultIdsAsync(db, projectId, rows.Select(r => r.Id).ToList(), ct)).Count > 0)
            return Refuse(404, null, "Run not found.");

        Dictionary<Guid, WorkflowStep> byId = steps.ToDictionary(s => s.Id);
        Dictionary<int, StepChangeResult> latest = rows
            .GroupBy(r => byId[r.WorkflowStepId].StepOrder)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var r = g.OrderByDescending(x => x.ExecutedAt).ThenByDescending(x => x.CompletedAt).First();
                    return new StepChangeResult(g.Key, r.Id, byId[r.WorkflowStepId], r.Status, r.OutputStorageKey);
                });

        (Guid? originId, string? originName) = await OriginAsync(db, projectId, execution, ct);
        return new StepChangeSourceResolution(new StepChangeSource(execution, steps, latest, originId, originName), null, 200, null, null);
    }

    /// <summary>
    /// <see cref="LoadAsync"/> plus the target step: the run must be finished (409 RUN_NOT_FINISHED) and the
    /// target a Completed result of it (404 unknown, 409 did not complete).
    /// </summary>
    public static async Task<StepChangeSourceResolution> ResolveAsync(
        InferenceApiDbContext db, Guid projectId, Guid executionId, StepChangeTarget target, CancellationToken ct)
    {
        StepChangeSourceResolution loaded = await LoadAsync(db, projectId, executionId, ct);
        if (!loaded.Found)
            return loaded;
        StepChangeSource source = loaded.Source!;

        if (!IsFinished(source.Execution.Status))
            return Refuse(409, "RUN_NOT_FINISHED", "You can request changes once this run has finished.");

        if (await RunChangeGate.IsClosedAsync(db, projectId, source.Execution, ct))
            return Refuse(RunChangeGate.StatusCode, RunChangeGate.Code, RunChangeGate.Message);

        return ResolveTarget(source, target);
    }

    /// <summary>The target step of an already loaded, finished <paramref name="source"/> (404 unknown, 409 did not complete, 400 ambiguous).</summary>
    public static StepChangeSourceResolution ResolveTarget(StepChangeSource source, StepChangeTarget target)
    {
        StepChangeResult? result;
        if (target.StepResultId is { } resultId)
        {
            result = source.Results.Values.FirstOrDefault(r => r.StepResultId == resultId);
            if (result is null)
                return Refuse(404, null, "That step result is not a finished result of this run.");
            if (target.StepOrder is { } order && order != result.StepOrder)
                return Refuse(400, null, "stepOrder and stepResultId name different steps.");
        }
        else if (target.StepOrder is { } order)
        {
            if (source.Steps.All(s => s.StepOrder != order))
                return Refuse(404, null, "That step is not part of this run.");
            if (!source.Results.TryGetValue(order, out result))
                return Refuse(409, "STEP_NOT_COMPLETED", "That step did not finish in this run, so there is nothing to change.");
        }
        else
        {
            return Refuse(400, null, "Say which step to change (stepOrder or stepResultId).");
        }

        if (result.Status != StepStatus.Completed)
            return Refuse(409, "STEP_NOT_COMPLETED", "That step was skipped in this run, so there is nothing to change.");

        return new StepChangeSourceResolution(source, result, 200, null, null);
    }

    public static bool IsFinished(ExecutionStatus status) =>
        status is ExecutionStatus.Passed or ExecutionStatus.Failed or ExecutionStatus.Cancelled;

    private static StepChangeSourceResolution Refuse(int status, string? code, string message) =>
        new(null, null, status, code, message);

    /// <summary>The user's own workflow the lineage started from, for names; the run's own workflow otherwise.</summary>
    private static async Task<(Guid? Id, string? Name)> OriginAsync(
        InferenceApiDbContext db, Guid projectId, WorkflowExecution execution, CancellationToken ct)
    {
        Guid? originId = null;
        WorkflowDefinition? own = await db.WorkflowDefinitions.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == execution.WorkflowDefinitionId && w.ProjectId == projectId, ct);
        if (own?.Kind == WorkflowDefinitionKind.Standard)
        {
            originId = own.Id;
        }
        else if (own?.Kind == WorkflowDefinitionKind.CompositionReplay)
        {
            // A replay run: the user's workflow its plan was made from.
            string? planJson = await db.StepChangeRequests.AsNoTracking()
                .Where(r => r.ExecutionId == execution.Id && r.ProjectId == projectId)
                .OrderBy(r => r.TargetStepOrder)
                .Select(r => r.ReplayPlanJson)
                .FirstOrDefaultAsync(ct)
                ?? await db.CompositionRevisions.AsNoTracking()
                    .Where(r => r.ExecutionId == execution.Id && r.ProjectId == projectId)
                    .Select(r => r.ReplayPlanJson)
                    .FirstOrDefaultAsync(ct);
            originId = CompositionReplayPlan.TryParse(planJson)?.OriginWorkflowDefinitionId;
        }

        string? name = originId is { } id
            ? await db.WorkflowDefinitions.AsNoTracking()
                .Where(w => w.Id == id && w.ProjectId == projectId)
                .Select(w => w.Name)
                .FirstOrDefaultAsync(ct)
            : null;
        return (originId, name ?? own?.Name);
    }
}
