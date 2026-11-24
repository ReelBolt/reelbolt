using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Save-time rules for the two step options, <c>continueCompositionJson</c> and
/// <c>registerOutputAsProjectFile</c>.
///
/// <para>
/// <see cref="ValidateShape"/> is the database-free half, run by <see cref="StepConfigSaveValidator"/> on
/// every save path: the JSON parses to exactly one id, continue-composition is on an Agent step, and
/// register-output is on a step type that can produce a playable output.
/// </para>
///
/// <para>
/// <see cref="ValidateReferencesAsync"/>/<see cref="ValidateReferences"/> are the database half, run by
/// <see cref="StepProviderOwnershipValidator"/> (which every save path already calls): the agent must
/// hold <see cref="ToolGroup.SandboxAuthoring"/> (continue) or <see cref="ToolGroup.SandboxRender"/>
/// (register on an Agent step), and the source run/version must belong to the workflow's own project.
/// A source outside that scope gets ONE answer whether it belongs to another project, another tenant,
/// or does not exist at all (D5: no existence oracle). Rows are read with the tenant filter dropped and
/// explicit project/organization comparisons instead, so the answer is a property of the workflow's
/// project and never of whichever organization is ambient on the request. The engine checks the same
/// project equality again at run time; this check is not the bound.
/// </para>
/// </summary>
public static class StepOptionsSaveValidator
{
    public const string RequiresAuthoringAgentCode = "CONTINUE_COMPOSITION_REQUIRES_AUTHORING_AGENT";
    public const string NotFoundCode = "CONTINUE_COMPOSITION_NOT_FOUND";
    public const string RequiresProducingStepCode = "REGISTER_OUTPUT_REQUIRES_PRODUCING_STEP";

    /// <summary>Step types whose result can be a playable output a project file can point at.</summary>
    private static readonly HashSet<StepType> ProducingStepTypes =
        [StepType.Agent, StepType.VideoGenerate, StepType.TimelineRender, StepType.VideoCompile];

    /// <summary>The database-free rules for one step; empty when its options are savable.</summary>
    public static IReadOnlyList<string> ValidateShape(CreateWorkflowStepRequest step)
    {
        List<string> errors = [];
        StepType? stepType = TypeOf(step);

        if (!string.IsNullOrWhiteSpace(step.ContinueCompositionJson))
        {
            if (stepType != StepType.Agent)
                errors.Add(RequiresAuthoring(step, stepType));
            else if (!ContinueCompositionConfig.TryParse(step.ContinueCompositionJson, out _, out string? error))
                errors.Add($"Step {step.StepOrder} ({StepType.Agent}): {error}");
        }

        if (step.RegisterOutputAsProjectFile && (stepType is not { } t || !ProducingStepTypes.Contains(t)))
            errors.Add(RequiresProducing(step, stepType));

        return errors;
    }

    /// <summary>
    /// The database rules across <paramref name="steps"/>. <paramref name="projectId"/> is the
    /// workflow's project and the only scope a source may come from: there is no organization-only mode
    /// (a sibling project of the same organization is as foreign as another tenant).
    /// </summary>
    public static async Task<IReadOnlyList<string>> ValidateReferencesAsync(
        InferenceApiDbContext db, Guid organizationId, Guid projectId,
        IReadOnlyList<CreateWorkflowStepRequest> steps, CancellationToken ct)
    {
        Wanted wanted = Collect(steps);
        if (wanted.IsEmpty)
            return [];

        Dictionary<Guid, AgentType> agents = await AgentsQuery(db, wanted).ToDictionaryAsync(a => a.Id, a => a.AgentType, ct);
        List<SourceScope> runs = await RunsQuery(db, wanted).ToListAsync(ct);
        List<SourceScope> versions = await VersionsQuery(db, wanted).ToListAsync(ct);
        return Check(wanted, agents, runs, versions, organizationId, projectId);
    }

    /// <summary>The synchronous twin of <see cref="ValidateReferencesAsync"/> for <see cref="WorkflowEditDiffService.ValidateProposedSteps"/>.</summary>
    public static IReadOnlyList<string> ValidateReferences(
        InferenceApiDbContext db, Guid organizationId, Guid projectId, IReadOnlyList<CreateWorkflowStepRequest> steps)
    {
        Wanted wanted = Collect(steps);
        if (wanted.IsEmpty)
            return [];

        Dictionary<Guid, AgentType> agents = AgentsQuery(db, wanted).ToDictionary(a => a.Id, a => a.AgentType);
        return Check(wanted, agents, [.. RunsQuery(db, wanted)], [.. VersionsQuery(db, wanted)], organizationId, projectId);
    }

    // ------------------------------------------------------------------

    private sealed record Wanted(
        List<(CreateWorkflowStepRequest Step, ContinueCompositionConfig? Continue)> Steps,
        HashSet<Guid> AgentIds,
        HashSet<Guid> RunIds,
        HashSet<Guid> VersionIds)
    {
        public bool IsEmpty => Steps.Count == 0;
    }

    private sealed record SourceScope(Guid Id, Guid ProjectId, Guid OrganizationId, Guid OwnerProjectId);

    private static Wanted Collect(IReadOnlyList<CreateWorkflowStepRequest> steps)
    {
        Wanted wanted = new([], [], [], []);
        foreach (CreateWorkflowStepRequest step in steps)
        {
            // Only Agent steps reach the database rules; every other shape problem is ValidateShape's.
            if (TypeOf(step) != StepType.Agent)
                continue;

            ContinueCompositionConfig? config = null;
            bool continues = !string.IsNullOrWhiteSpace(step.ContinueCompositionJson)
                && ContinueCompositionConfig.TryParse(step.ContinueCompositionJson, out config, out _);
            if (!continues && !step.RegisterOutputAsProjectFile)
                continue;

            wanted.Steps.Add((step, config));
            wanted.AgentIds.Add(step.AgentDefinitionId);
            if (config?.ExecutionId is { } runId)
                wanted.RunIds.Add(runId);
            if (config?.VersionId is { } versionId)
                wanted.VersionIds.Add(versionId);
        }

        return wanted;
    }

    private static IQueryable<AgentRow> AgentsQuery(InferenceApiDbContext db, Wanted wanted) =>
        db.AgentDefinitions.AsNoTracking()
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(a => wanted.AgentIds.Contains(a.Id))
            .Select(a => new AgentRow(a.Id, a.AgentType));

    private sealed record AgentRow(Guid Id, AgentType AgentType);

    private static IQueryable<SourceScope> RunsQuery(InferenceApiDbContext db, Wanted wanted) =>
        db.WorkflowExecutions.AsNoTracking()
            .IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            .Where(e => wanted.RunIds.Contains(e.Id))
            .Select(e => new SourceScope(e.Id, e.ProjectId, e.OrganizationId, e.ProjectId));

    // A version is in scope only when it AND its workspace name the same project (and the workspace
    // the organization): a version row is never trusted on its own project column.
    private static IQueryable<SourceScope> VersionsQuery(InferenceApiDbContext db, Wanted wanted) =>
        from v in db.CompositionWorkspaceVersions.AsNoTracking().IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
        join w in db.CompositionWorkspaces.AsNoTracking().IgnoreQueryFilters([InferenceApiDbContext.TenantFilterName])
            on v.WorkspaceId equals w.Id
        where wanted.VersionIds.Contains(v.Id)
        select new SourceScope(v.Id, w.ProjectId, w.OrganizationId, v.ProjectId);

    private static IReadOnlyList<string> Check(
        Wanted wanted,
        Dictionary<Guid, AgentType> agents,
        IReadOnlyList<SourceScope> runs,
        IReadOnlyList<SourceScope> versions,
        Guid organizationId,
        Guid projectId)
    {
        List<string> errors = [];
        foreach ((CreateWorkflowStepRequest step, ContinueCompositionConfig? config) in wanted.Steps)
        {
            // An unknown agent is the agent check's own refusal (A8); treat it as granting nothing here.
            IReadOnlyList<ToolGroup> groups = agents.TryGetValue(step.AgentDefinitionId, out AgentType type)
                ? ToolGroupCatalog.GroupsFor(type)
                : [];

            if (step.RegisterOutputAsProjectFile && !groups.Contains(ToolGroup.SandboxRender))
                errors.Add(RequiresProducing(step, StepType.Agent));

            if (config is null)
                continue;

            if (!groups.Contains(ToolGroup.SandboxAuthoring))
            {
                errors.Add(RequiresAuthoring(step, StepType.Agent));
                continue;
            }

            SourceScope? source = config.ExecutionId is { } runId
                ? runs.FirstOrDefault(r => r.Id == runId)
                : versions.FirstOrDefault(v => v.Id == config.VersionId);
            if (!InScope(source, organizationId, projectId))
                errors.Add(NotFound(step));
        }

        return errors;
    }

    private static bool InScope(SourceScope? source, Guid organizationId, Guid projectId) =>
        source is not null
        && source.OrganizationId == organizationId
        && source.ProjectId == source.OwnerProjectId
        && source.ProjectId == projectId;

    private static StepType? TypeOf(CreateWorkflowStepRequest step) =>
        step.StepType is null ? StepType.Agent
        : Enum.TryParse(step.StepType, out StepType parsed) ? parsed
        : null;

    private static string RequiresAuthoring(CreateWorkflowStepRequest step, StepType? stepType) =>
        $"Step {step.StepOrder} ({stepType?.ToString() ?? step.StepType}): continueCompositionJson is only available on an Agent step " +
        $"whose agent authors compositions (SandboxAuthoring) ({RequiresAuthoringAgentCode}).";

    private static string RequiresProducing(CreateWorkflowStepRequest step, StepType? stepType) =>
        $"Step {step.StepOrder} ({stepType?.ToString() ?? step.StepType}): registerOutputAsProjectFile is only available on a step " +
        $"that produces a playable output: a rendering Agent, VideoGenerate, TimelineRender or VideoCompile ({RequiresProducingStepCode}).";

    // One message for "another project", "another tenant" and "does not exist": nothing in it says which.
    private static string NotFound(CreateWorkflowStepRequest step) =>
        $"Step {step.StepOrder} ({StepType.Agent}): continueCompositionJson names a run or composition version " +
        $"that is not in this project ({NotFoundCode}).";
}
