using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Inference.Api.Services.Compositions;

/// <summary>
/// The run a revision's replay is seeded from, with everything the planner needs: its workflow's steps,
/// each step's latest finished result, and the step whose render the revision replaces.
/// </summary>
/// <param name="SourceExecution">The run whose results describe the base version's whole pipeline.</param>
/// <param name="Steps">That run's workflow steps, agent definitions loaded.</param>
/// <param name="SourceResults">Each step order's latest Completed/Skipped result id in that run.</param>
/// <param name="RevisedStepOrder">The step that rendered the film being revised.</param>
/// <param name="OriginWorkflowDefinitionId">The user's own workflow the lineage started from (for takes and names).</param>
/// <param name="OriginWorkflowName">Its name, when it still exists.</param>
/// <param name="AppManifestJson">
/// The app page the source run ran with (its snapshot's copy, else its workflow's): a replay of a run whose app chose its agents'
/// models carries it, so the replayed agents run on those models (docs/design/app-inputs.md 28). Null when there is none.
/// </param>
public sealed record CompositionReplaySource(
    WorkflowExecution SourceExecution,
    IReadOnlyList<WorkflowStep> Steps,
    IReadOnlyDictionary<int, Guid> SourceResults,
    int RevisedStepOrder,
    Guid? OriginWorkflowDefinitionId,
    string? OriginWorkflowName,
    string? AppManifestJson = null)
{
    public CompositionReplayPlan Plan(CompositionCutPolicy policy) =>
        CompositionReplayPlanner.Plan(Steps, SourceResults, RevisedStepOrder, policy, SourceExecution.Id, OriginWorkflowDefinitionId);
}

/// <summary>
/// Finds the run a revision's replay is seeded from (docs/design/composition-revisions.md "Which run a
/// replay starts from"). The base version's own run, normally; through a pre-replay one-step revision run
/// (a hidden <see cref="WorkflowDefinitionKind.CompositionRevision"/> execution, which holds only the
/// render) back to the run it revised, because only that run holds the rest of the pipeline. Every query
/// is scoped by the project the caller was authorized for.
/// </summary>
public static class CompositionReplaySources
{
    private const int MaxHops = 32;

    /// <summary>The replay source, or null with a plain-language reason when the revision can only re-render the film.</summary>
    public static async Task<(CompositionReplaySource? Source, string? Reason)> ResolveAsync(
        InferenceApiDbContext db,
        Guid projectId,
        WorkflowExecution clicked,
        CompositionWorkspaceVersion? baseVersion,
        IReadOnlyList<CompositionWorkspaceVersion> versions,
        CompositionWorkspace? workspace,
        CancellationToken ct)
    {
        Guid? startId = baseVersion?.CreatedByExecutionId ?? clicked.Id;
        WorkflowExecution? source = null;
        HashSet<Guid> seen = [];
        for (int hop = 0; startId is { } id && hop < MaxHops && seen.Add(id); hop++)
        {
            WorkflowExecution? candidate = await db.WorkflowExecutions.AsNoTracking()
                .Include(e => e.WorkflowDefinition)
                .FirstOrDefaultAsync(e => e.Id == id && e.ProjectId == projectId, ct);
            if (candidate?.WorkflowDefinition is null)
                break;

            if (candidate.WorkflowDefinition.Kind != WorkflowDefinitionKind.CompositionRevision)
            {
                // ExecutionSnapshot: a past run re-pointed to its own snapshot when its workflow was deleted.
                source = candidate.WorkflowDefinition.Kind is WorkflowDefinitionKind.Standard or WorkflowDefinitionKind.CompositionReplay
                        or WorkflowDefinitionKind.ExecutionSnapshot
                    ? candidate
                    : null;
                break;
            }

            // A one-step revision run: its own results are only the render. Step back to the run it revised.
            startId = await db.CompositionRevisions.AsNoTracking()
                .Where(r => r.ExecutionId == candidate.Id && r.ProjectId == projectId)
                .Select(r => (Guid?)r.SourceExecutionId)
                .FirstOrDefaultAsync(ct);
        }

        if (source is null)
            return (null, "The run that made this video is no longer available, so only the animation itself is made again.");

        // The steps the source run executed: its own snapshot's when the engine took one (its results
        // reference those), else the workflow's.
        Guid runStepsDefinitionId = source.SnapshotDefinitionId ?? source.WorkflowDefinitionId;
        List<WorkflowStep> steps = await db.WorkflowSteps.AsNoTracking()
            .Include(s => s.AgentDefinition)
            .Where(s => s.WorkflowDefinitionId == runStepsDefinitionId)
            .OrderBy(s => s.StepOrder)
            .ToListAsync(ct);
        if (steps.Count == 0)
            return (null, "The workflow that made this video no longer exists, so only the animation itself is made again.");

        HashSet<Guid> stepIds = [.. steps.Select(s => s.Id)];
        var results = await db.WorkflowStepResults.AsNoTracking()
            .Where(r => r.WorkflowExecutionId == source.Id && stepIds.Contains(r.WorkflowStepId) && r.SupersededAt == null)
            .Select(r => new { r.Id, r.WorkflowStepId, r.Status, r.OutputStorageKey, r.ExecutedAt, r.CompletedAt })
            .ToListAsync(ct);
        // Earlier steps whose output is not served are not reused: the run reads as no longer available.
        if ((await Outputs.OutputGate.HeldResultIdsAsync(db, projectId, results.Select(r => r.Id).ToList(), ct)).Count > 0)
            return (null, "The run that made this video is no longer available, so only the animation itself is made again.");
        if (results.Count == 0)
        {
            return (null, "The workflow was changed after this video was made, so its earlier steps cannot be reused; "
                + "only the animation itself is made again.");
        }

        Dictionary<Guid, int> orderByStep = steps.ToDictionary(s => s.Id, s => s.StepOrder);
        Dictionary<int, Guid> latest = results
            .Where(r => r.Status is StepStatus.Completed or StepStatus.Skipped)
            .GroupBy(r => orderByStep[r.WorkflowStepId])
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ExecutedAt).ThenByDescending(r => r.CompletedAt).First().Id);

        // The step that rendered the film: the base version's render (or an ancestor's — a one-step
        // revision's render is not in this run, but the version it revised is) as one of this run's results.
        List<string> renderKeys = [.. Ancestry(baseVersion, versions).Select(v => v.RenderOutputStorageKey).OfType<string>()];
        int? revisedOrder = null;
        foreach (string key in renderKeys)
        {
            var match = results
                .Where(r => r.Status == StepStatus.Completed && r.OutputStorageKey == key)
                .OrderByDescending(r => r.ExecutedAt)
                .FirstOrDefault();
            if (match is not null)
            {
                revisedOrder = orderByStep[match.WorkflowStepId];
                break;
            }
        }

        if (revisedOrder is null && source.WorkflowDefinition.Kind == WorkflowDefinitionKind.CompositionReplay)
        {
            string? planJson = await db.CompositionRevisions.AsNoTracking()
                .Where(r => r.ExecutionId == source.Id && r.ProjectId == projectId)
                .Select(r => r.ReplayPlanJson)
                .FirstOrDefaultAsync(ct);
            revisedOrder = CompositionReplayPlan.TryParse(planJson)?.RevisedStepOrder;
        }

        if (revisedOrder is null && baseVersion is null)
        {
            // A run recovered from its sandbox: its latest film render, as the recovery itself picks it.
            HashSet<int> filmSteps = [.. steps.Where(s => s.StepType is StepType.Agent or StepType.Revise).Select(s => s.StepOrder)];
            string prefix = $"projects/{projectId}/outputFiles/";
            revisedOrder = results
                .Where(r => r.Status == StepStatus.Completed && r.OutputStorageKey != null
                            && r.OutputStorageKey.StartsWith(prefix, StringComparison.Ordinal)
                            && filmSteps.Contains(orderByStep[r.WorkflowStepId]))
                .OrderByDescending(r => r.ExecutedAt)
                .Select(r => (int?)orderByStep[r.WorkflowStepId])
                .FirstOrDefault();
        }

        if (revisedOrder is null)
            return (null, "The step that made this animation could not be found in its run, so only the animation itself is made again.");

        Guid? originWorkflowId = workspace?.OriginWorkflowDefinitionId
            ?? (source.WorkflowDefinition.Kind == WorkflowDefinitionKind.Standard ? source.WorkflowDefinitionId : null);
        string? originName = originWorkflowId is { } originId
            ? await db.WorkflowDefinitions.AsNoTracking()
                .Where(w => w.Id == originId && w.ProjectId == projectId)
                .Select(w => w.Name)
                .FirstOrDefaultAsync(ct)
            : null;

        // The page of the time: the run's snapshot carries it when the app declared inputs; a run without a snapshot reads its workflow's.
        string? appManifestJson = runStepsDefinitionId == source.WorkflowDefinitionId
            ? source.WorkflowDefinition.AppManifestJson
            : await db.WorkflowDefinitions.AsNoTracking()
                .Where(w => w.Id == runStepsDefinitionId && w.ProjectId == projectId)
                .Select(w => w.AppManifestJson)
                .FirstOrDefaultAsync(ct);

        return (new CompositionReplaySource(
            source, steps, latest, revisedOrder.Value, originWorkflowId, originName ?? source.WorkflowDefinition.Name, appManifestJson), null);
    }

    /// <summary><paramref name="version"/>, then its parent, grandparent … (cycle-safe).</summary>
    private static IEnumerable<CompositionWorkspaceVersion> Ancestry(
        CompositionWorkspaceVersion? version, IReadOnlyList<CompositionWorkspaceVersion> versions)
    {
        Dictionary<Guid, CompositionWorkspaceVersion> byId = versions.ToDictionary(v => v.Id);
        HashSet<Guid> seen = [];
        for (CompositionWorkspaceVersion? current = version; current is not null && seen.Add(current.Id);)
        {
            yield return current;
            current = current.ParentVersionId is { } parent ? byId.GetValueOrDefault(parent) : null;
        }
    }
}
