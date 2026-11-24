using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>Thrown by the execution service when a plan limit refuses an execute; callers map it to the gate's status.</summary>
public sealed class PlanLimitException(PlanGateViolation violation) : Exception(violation.ToString())
{
    /// <summary>The refused gate.</summary>
    public PlanGateViolation Violation { get; } = violation;
}

/// <summary>
/// Enforces plan entitlements in the Inference API (B8a). Reads <see cref="IOrganizationEntitlements"/>
/// per call (it is cached inside), so a downgrade applies on the next request. With billing off the
/// entitlement set is unlimited and every check passes.
/// </summary>
public interface IPlanGateService
{
    /// <summary>Save-time: does the plan allow every step type in <paramref name="steps"/>?</summary>
    Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct);

    /// <summary>Execute-time: is the org below its concurrent-execution cap?</summary>
    Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct);

    /// <summary>Upload-time: would <paramref name="incomingBytes"/> more pass the plan's storage quota?</summary>
    Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct);

    /// <summary>
    /// Execute-time: does the plan render at the run's <paramref name="chosenShortSide"/> (see
    /// <c>OutputResolution</c>)? Null (Auto) always passes.
    /// </summary>
    Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct);

    /// <summary>
    /// Run-start: does the plan include every gated step type (video generation, audio generation, rooms) the run is
    /// certain to reach? The engine refuses the same step the moment it gets there (<c>EnsurePlanAllowsStepAsync</c>), so
    /// this only moves that refusal to the Run button: it never refuses anything the engine would let through. A workflow
    /// that routes (a condition, a branch, a review loop) is not judged unless <paramref name="chargedStepOrders"/> names the
    /// steps that will run. <paramref name="steps"/> null reads the saved workflow. Null (no refusal) by default.
    /// </summary>
    Task<PlanGateViolation?> CheckRunStepsAsync(
        Guid organizationId, Guid projectId, Guid workflowId, IReadOnlyList<WorkflowStep>? steps, IReadOnlyCollection<int>? chargedStepOrders, CancellationToken ct)
        => Task.FromResult<PlanGateViolation?>(null);
}

/// <inheritdoc />
public sealed class PlanGateService(
    InferenceApiDbContext db,
    IOrganizationEntitlements entitlements,
    IStorageUsageService? storageUsage = null) : IPlanGateService
{
    private readonly IStorageUsageService _storageUsage = storageUsage ?? new StorageUsageService(db, entitlements);

    /// <inheritdoc />
    public async Task<PlanGateViolation?> CheckStepsAsync(Guid organizationId, IEnumerable<CreateWorkflowStepRequest> steps, CancellationToken ct)
    {
        List<StepType> types = [];
        foreach (CreateWorkflowStepRequest step in steps)
        {
            if (step.StepType is not null && Enum.TryParse(step.StepType, out StepType parsed))
                types.Add(parsed);
        }

        if (!types.Exists(t => PlanGate.FeatureFor(t) is not null)) return null;
        return PlanGate.CheckSteps(types, await entitlements.GetAsync(organizationId, ct));
    }

    /// <inheritdoc />
    public async Task<PlanGateViolation?> CheckRunStepsAsync(
        Guid organizationId, Guid projectId, Guid workflowId, IReadOnlyList<WorkflowStep>? steps, IReadOnlyCollection<int>? chargedStepOrders, CancellationToken ct)
    {
        // Only a standard workflow of THIS project is judged: another project's or another organization's workflow id (or a
        // hidden kind) gets no answer here, so the caller's own 404 stays the only thing a foreign id can learn.
        if (!await db.WorkflowDefinitions.AsNoTracking()
                .AnyAsync(w => w.Id == workflowId && w.ProjectId == projectId && w.Kind == WorkflowDefinitionKind.Standard, ct))
            return null;
        List<WorkflowStep> all = steps is not null
            ? [.. steps]
            : await db.WorkflowSteps.AsNoTracking().Where(st => st.WorkflowDefinitionId == workflowId).ToListAsync(ct);
        // Straight workflow (no app branch named): every step runs, so any routing leaves it to the engine.
        if (chargedStepOrders is null)
        {
            if (all.Exists(IsRouting)) return null;
            List<StepType> types = [.. all.Select(st => st.StepType)];
            if (!types.Exists(t => PlanGate.FeatureFor(t) is not null)) return null;
            return PlanGate.CheckSteps(types, await entitlements.GetAsync(organizationId, ct));
        }

        // An app: the named steps are the ones that CAN run (StaticBranchResolver keeps both sides of a Conditional it cannot
        // decide). Refuse only when EVERY path through them reaches a step the plan lacks, so the engine would fail whichever
        // way it goes. Anything else that routes (ReviewLoop, ForEach, Parallel, a condition on a non-Conditional step) is left
        // to the engine. Never refuse what the engine could let finish.
        if (!all.Exists(st => PlanGate.FeatureFor(st.StepType) is not null)) return null;
        if (all.Exists(st => chargedStepOrders.Contains(st.StepOrder) && RoutesBeyondConditional(st))) return null;
        OrganizationEntitlementSet set = await entitlements.GetAsync(organizationId, ct);
        return EveryPathGated(all, chargedStepOrders, t => PlanGate.CheckStep(t, set));
    }

    private static bool IsRouting(WorkflowStep st) =>
        st.StepType is StepType.Conditional or StepType.ReviewLoop or StepType.ForEach or StepType.Parallel
        || st.ConditionExpression is not null || st.EdgeConditionJson is not null
        || st.TrueBranchStepOrder is not null || st.FalseBranchStepOrder is not null;

    /// <summary>Routing the path walk cannot follow: anything but a plain Conditional (its expression is the resolver's business).</summary>
    private static bool RoutesBeyondConditional(WorkflowStep st) => st.StepType == StepType.Conditional
        ? st.EdgeConditionJson is not null
        : st.StepType is StepType.ReviewLoop or StepType.ForEach or StepType.Parallel
            || st.ConditionExpression is not null || st.EdgeConditionJson is not null
            || st.TrueBranchStepOrder is not null || st.FalseBranchStepOrder is not null;

    /// <summary>
    /// The violation of the first plan-lacking step when every path from the first step, through the reachable steps only,
    /// reaches one; null when any path can end (or cannot be followed) without one. A Conditional goes to each branch target
    /// that is reachable (or ends the run: <c>"end"</c>); an undecided one therefore has two ways, a decided one has one.
    /// </summary>
    internal static PlanGateViolation? EveryPathGated(
        IReadOnlyList<WorkflowStep> steps, IReadOnlyCollection<int> reachable, Func<StepType, PlanGateViolation?> check)
    {
        List<WorkflowStep> ordered = [.. steps.OrderBy(st => st.StepOrder)];
        PlanGateViolation? first = null;

        int Target(string? branch, int fallThrough)
        {
            if (string.Equals(branch?.Trim(), "end", StringComparison.OrdinalIgnoreCase)) return ordered.Count;
            if (!int.TryParse(branch, out int order)) return fallThrough;
            int index = ordered.FindIndex(st => st.StepOrder == order);
            return index < 0 ? fallThrough : index;
        }

        bool Walk(int at, HashSet<int> visiting)
        {
            if (at >= ordered.Count) return false;
            WorkflowStep st = ordered[at];
            if (!reachable.Contains(st.StepOrder)) return false;
            if (check(st.StepType) is { } violation)
            {
                first ??= violation;
                return true;
            }

            if (!visiting.Add(at)) return false;
            List<int> next = st.StepType == StepType.Conditional
                ? [Target(st.TrueBranchStepOrder, at + 1), Target(st.FalseBranchStepOrder, at + 1)]
                : [at + 1];
            // A branch the resolver ruled out is not a way the run can go.
            List<int> ways = [.. next.Distinct().Where(n => n >= ordered.Count || reachable.Contains(ordered[n].StepOrder))];
            bool every = ways.Count > 0 && ways.All(n => Walk(n, visiting));
            visiting.Remove(at);
            return every;
        }

        return ordered.Count > 0 && Walk(0, []) ? first : null;
    }

    /// <inheritdoc />
    public async Task<PlanGateViolation?> CheckExecuteAsync(Guid organizationId, CancellationToken ct)
    {
        OrganizationEntitlementSet set = await entitlements.GetAsync(organizationId, ct);
        if (set.MaxConcurrentExecutions == OrganizationEntitlementSet.UnlimitedInt) return null;
        int running = await db.WorkflowExecutions
            .IgnoreQueryFilters()
            .CountAsync(e => e.OrganizationId == organizationId && e.Status == ExecutionStatus.Running, ct);
        return PlanGate.CheckConcurrency(running, set);
    }

    /// <inheritdoc />
    public async Task<PlanGateViolation?> CheckResolutionAsync(Guid organizationId, int? chosenShortSide, CancellationToken ct)
    {
        if (chosenShortSide is null) return null;
        OrganizationEntitlementSet set = await entitlements.GetAsync(organizationId, ct);
        return PlanGate.CheckResolution(chosenShortSide, set);
    }

    /// <summary>
    /// Storage is measured LIVE through <see cref="IStorageUsageService"/> — the sum of the org's
    /// project-file sizes, one aggregate per check — not from B3d's daily GB-day sampler, which is an S3
    /// listing and lags by up to a day. EVERY project file counts: uploads, synced files, agent files and
    /// rendered workflow outputs. Outputs and agent writes are never refused by this gate (the run is
    /// already paid for in credits), but they still use storage, so a workspace whose outputs filled it
    /// must free space before it can add more. <paramref name="incomingBytes"/> is the NET growth: when a
    /// file is rewritten, its new size minus its old one (a check with zero or less always passes).
    /// </summary>
    public async Task<PlanGateViolation?> CheckUploadAsync(Guid organizationId, long incomingBytes, CancellationToken ct)
    {
        if (incomingBytes <= 0) return null;
        OrganizationEntitlementSet set = await entitlements.GetAsync(organizationId, ct);
        if (set.StorageGb == OrganizationEntitlementSet.UnlimitedInt) return null;
        long used = await _storageUsage.GetUsedBytesAsync(organizationId, ct);
        return PlanGate.CheckStorage(used, incomingBytes, set);
    }
}

/// <summary>Maps a violation to its HTTP response.</summary>
public static class PlanGateResults
{
    /// <summary>402/429/413 with the machine-readable body.</summary>
    public static ObjectResult ToResult(this PlanGateViolation violation)
        => new(violation.ToBody()) { StatusCode = violation.StatusCode };
}
