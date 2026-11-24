using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Data;

namespace ReelBolt.WorkflowEngine.Execution.Snapshots;

/// <summary>
/// What one dispatch of a run executes: its ordered steps, its agent snapshot when it has one, and its app-input state
/// (docs/design/app-inputs.md 6.3; null for a run that carries no app input value).
/// </summary>
public sealed record ExecutionRunPlan(List<WorkflowStep> Steps, AgentSnapshot? Agents, AppInputRunState? AppInputs = null);

/// <summary>
/// Takes, and later reloads, a run's own copy of its workflow (docs: plan "Design decisions" 1). The
/// engine is the one place every run starts, so every way of creating a run (REST, assistant, MCP,
/// templates) is covered here without touching each.
/// <list type="bullet">
/// <item>A <see cref="WorkflowDefinitionKind.Standard"/> run without a snapshot gets one: the definition
/// and its steps cloned (<see cref="WorkflowStepCloner"/>) into a hidden
/// <see cref="WorkflowDefinitionKind.ExecutionSnapshot"/> in the same project, owned by the run (deleted
/// with it), plus the behaviour fields of every agent those steps use.</item>
/// <item>A run that already has one (a retry or resume of the same row) reloads it; nothing is re-taken.</item>
/// <item>A run of a hidden system workflow (kinds 1-4) runs its definition's steps as before: no user can
/// edit those.</item>
/// </list>
/// <see cref="WorkflowExecution.WorkflowDefinitionId"/> is never changed, so lists, URLs, the step-cache
/// key and estimates keep naming the source workflow.
/// <para>
/// App inputs (docs/design/app-inputs.md 6.3): right after the clone, the run's stored canonical values are bound onto the
/// cloned rows by the same pure <see cref="AppInputBinder"/> the API ran as a dry run, and the source's manifest is copied
/// onto the snapshot; the snapshot then holds plain configs (no placeholder, no <c>appInputs</c>). A value that no longer
/// binds (the workflow was edited between Run and the claim) throws <see cref="AppInputsStaleException"/> before anything
/// is written. A workflow that uses no app input is cloned exactly as before.
/// </para>
/// </summary>
public static class ExecutionSnapshotter
{
    /// <summary>The name limit the API applies to workflow names.</summary>
    private const int MaxNameLength = 200;

    /// <param name="execution">
    /// Tracked by <paramref name="db"/>, with <see cref="WorkflowExecution.WorkflowDefinition"/> and its
    /// steps (and their agents) loaded, as <see cref="WorkflowExecutorService.ExecuteAsync"/> loads it.
    /// </param>
    public static async Task<ExecutionRunPlan> PrepareAsync(WorkflowEngineDbContext db, WorkflowExecution execution, CancellationToken ct)
    {
        if (execution.SnapshotDefinitionId is null)
        {
            if (execution.WorkflowDefinition.Kind != WorkflowDefinitionKind.Standard)
            {
                // A hidden system run (a revision or change-request replay among them) runs steps already bound from
                // the source run's snapshot; its replayed brief still carries the values its conditions read.
                List<WorkflowStep> systemSteps = [.. execution.WorkflowDefinition.Steps.OrderBy(s => s.StepOrder)];
                return new ExecutionRunPlan(systemSteps, await ReplayAgentModelsAsync(db, execution, systemSteps, ct), AppInputSnapshotBinding.StateFor(
                    execution, execution.WorkflowDefinition.AppManifestJson, systemSteps, systemSteps));
            }

            await TakeAsync(db, execution, ct);
        }

        Guid snapshotId = execution.SnapshotDefinitionId!.Value;

        // Detached on purpose: each step's agent becomes an overlay copy below, and nothing about a
        // snapshot step is ever written during the run (results and the execution carry ids only).
        List<WorkflowStep> steps = await db.WorkflowSteps.AsNoTracking()
            .Where(s => s.WorkflowDefinitionId == snapshotId)
            .OrderBy(s => s.StepOrder)
            .ToListAsync(ct);

        // Loaded separately rather than Include'd: a required-navigation Include is an inner join, and a
        // step whose agent row is gone must still run on the snapshot's values, not silently vanish.
        List<Guid> agentIds = [.. steps.Select(s => s.AgentDefinitionId).Distinct()];
        Dictionary<Guid, AgentDefinition> rows = await db.AgentDefinitions.AsNoTracking()
            .Where(a => agentIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, ct);

        AgentSnapshot? agents = AgentSnapshot.Parse(execution.AgentSnapshotJson);
        foreach (WorkflowStep step in steps)
        {
            AgentDefinition? row = rows.GetValueOrDefault(step.AgentDefinitionId);
            step.AgentDefinition = (agents is null ? row : agents.Overlay(row, step.AgentDefinitionId))!;
        }

        // The values' state for this dispatch: the components read what each authored step consumed (the source workflow's
        // steps, as the snapshot was taken from them), the reach reads the snapshot steps that run.
        string? manifestJson = await db.WorkflowDefinitions.AsNoTracking()
            .Where(w => w.Id == snapshotId)
            .Select(w => w.AppManifestJson)
            .FirstOrDefaultAsync(ct) ?? execution.WorkflowDefinition.AppManifestJson;
        AppInputRunState? appInputs = AppInputSnapshotBinding.StateFor(
            execution, manifestJson, [.. execution.WorkflowDefinition.Steps], steps);

        return new ExecutionRunPlan(steps, agents, appInputs);
    }

    private static async Task TakeAsync(WorkflowEngineDbContext db, WorkflowExecution execution, CancellationToken ct)
    {
        WorkflowDefinition source = execution.WorkflowDefinition;
        DateTime now = DateTime.UtcNow;
        WorkflowDefinition snapshot = new()
        {
            Id = Guid.NewGuid(),
            // The source's plain name: Kind alone hides the row, and a run filed under its snapshot
            // (after its workflow is deleted) keeps showing the name it was started from.
            Name = Truncate(source.Name, MaxNameLength),
            ProjectId = source.ProjectId,
            Kind = WorkflowDefinitionKind.ExecutionSnapshot,
            RequiresUserInput = source.RequiresUserInput,
            CreatedAt = now,
            UpdatedAt = now
        };
        List<WorkflowStep> sourceSteps = [.. source.Steps.OrderBy(s => s.StepOrder)];
        List<WorkflowStep> clones = [.. sourceSteps.Select(step => WorkflowStepCloner.Clone(step, snapshot.Id))];

        // App inputs: bound onto the clones BEFORE anything is added, so a stale value writes nothing (6.3). The manifest is
        // copied only for an app that uses inputs (labels of the time for the run's history; Retry re-checks against it).
        // Lane L4: a chosen model is re-resolved here against the run organization's models (8.3); one that can no longer
        // be used is stale too, so the run fails before step 1 rather than running on a model nobody chose.
        AppManifest? manifest = AppManifestJson.TryParse(source.AppManifestJson);
        // The permission to send media is honoured only with its grantor on record (GrantedValues), and applies to this
        // snapshot alone: the source workflow's steps are never written.
        StoredRunBrief? storedBrief = RunBriefJson.TryParse(execution.RunBriefJson);
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? values = AppInputSnapshotBinding.GrantedValues(storedBrief);
        IReadOnlyList<AppModelCandidate>? candidates = AppInputSnapshotBinding.NeedsModels(manifest, values)
            ? await AppInputSnapshotBinding.LoadModelCandidatesAsync(db, execution.OrganizationId, ct)
            : null;
        if (!AppInputSnapshotBinding.TryBindInPlace(manifest, clones, values, out IReadOnlyList<AppBindingProblem> problems,
                candidates is null ? null : AppModelRules.Resolver(manifest!, candidates),
                briefAvatar: storedBrief?.Avatar?.AvatarId is not null))
            throw new AppInputsStaleException(problems);
        IReadOnlyDictionary<Guid, Guid>? agentModels = AppInputSnapshotBinding.AgentModelsOrThrow(manifest, values, candidates);
        if (AppInputUsage.Declares(manifest))
            snapshot.AppManifestJson = source.AppManifestJson;

        db.WorkflowDefinitions.Add(snapshot);
        db.Entry(snapshot).Property(WorkflowEngineDbContext.SnapshotOfExecutionIdColumn).CurrentValue = execution.Id;

        foreach (WorkflowStep clone in clones)
            db.WorkflowSteps.Add(clone);

        // Every agent a step can run: its own, and the agents its config names (a Parallel step's extra
        // agents, a room's custom seat and director agents; StepAgentReferences). Only platform
        // built-ins and the run's OWN organization's agents are recorded (SQFU-S3): another
        // organization's id is never frozen into this run, and resolves fail-closed at run time
        // exactly as it would without a snapshot.
        HashSet<Guid> agentIds = [.. sourceSteps.Select(s => s.AgentDefinitionId)];
        foreach (WorkflowStep step in sourceSteps)
            agentIds.UnionWith(StepAgentReferences.Extract(step).Select(r => r.AgentDefinitionId));
        Guid organizationId = execution.OrganizationId;
        List<AgentDefinition> agents = await db.AgentDefinitions.AsNoTracking()
            .Where(a => agentIds.Contains(a.Id) && (a.IsBuiltIn || a.OrganizationId == organizationId))
            .ToListAsync(ct);

        execution.SnapshotDefinitionId = snapshot.Id;
        execution.AgentSnapshotJson = AgentSnapshot.Capture(agents).WithAppModels(agentModels).Serialize();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The agents' app-chosen Chat models of a change-request replay (docs/design/app-inputs.md 25): a replay runs its own
    /// hidden definition, which carries the app page only when the request copied it, so a run whose brief chose an agent's
    /// model keeps (or, for an app-input change, takes) that choice. The live agent rows are recorded as they are now (the
    /// replay has no snapshot of its own) with the choices laid on; a choice that can no longer be used is stale. Null for
    /// every other system run and for a replay that chose no agent model, exactly as before.
    /// </summary>
    private static async Task<AgentSnapshot?> ReplayAgentModelsAsync(
        WorkflowEngineDbContext db, WorkflowExecution execution, List<WorkflowStep> steps, CancellationToken ct)
    {
        if (execution.WorkflowDefinition.Kind != WorkflowDefinitionKind.CompositionReplay)
            return null;
        AppManifest? manifest = AppManifestJson.TryParse(execution.WorkflowDefinition.AppManifestJson);
        IReadOnlyDictionary<string, System.Text.Json.JsonElement>? values = RunBriefJson.TryParse(execution.RunBriefJson)?.Inputs;
        if (!AppModelRules.HasAgentModelValue(manifest, values))
            return null;

        IReadOnlyList<AppModelCandidate> candidates = await AppInputSnapshotBinding.LoadModelCandidatesAsync(db, execution.OrganizationId, ct);
        IReadOnlyDictionary<Guid, Guid>? chosen = AppInputSnapshotBinding.AgentModelsOrThrow(manifest, values, candidates);
        if (chosen is null)
            return null;

        HashSet<Guid> agentIds = [.. steps.Select(s => s.AgentDefinitionId)];
        Guid organizationId = execution.OrganizationId;
        List<AgentDefinition> agents = await db.AgentDefinitions.AsNoTracking()
            .Where(a => agentIds.Contains(a.Id) && (a.IsBuiltIn || a.OrganizationId == organizationId))
            .ToListAsync(ct);
        return AgentSnapshot.Capture(agents).WithAppModels(chosen);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
