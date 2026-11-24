using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// One step result's charged credits for the DISPLAY range, with the agent type it ran as.
/// <paramref name="AgentType"/> is null when the agent definition no longer exists, in which case the
/// sample can still fill the exact-agent bucket but not the agent-type one. <paramref name="OutputResolution"/>
/// is the resolution its execution chose (null = Auto, also when the execution row is gone; QA-15).
/// </summary>
public readonly record struct DisplayStepSample(StepType StepType, AgentType? AgentType, Guid AgentDefinitionId, long CreditsMillis, int? OutputResolution = null);

/// <summary>
/// The history queries behind the pre-run display range. They are deliberately separate from
/// <see cref="StepCreditHistoryQuery"/>, which sizes the HOLD (shared by the API and the engine, so it
/// changes for both or not at all): the display path additionally needs the agent type, whole-run totals and review-loop pass counts, and
/// it drops zero-credit samples (own-provider or unpriced work) that would drag a shown median to
/// nothing.
///
/// <para>
/// Each takes <see cref="IQueryable{T}"/>s so the caller's own DbContext supplies them, filters and
/// groups in SQL, and finishes the small remainder in memory (the InMemory provider used by tests has
/// no <c>ExecuteUpdate</c> and limited grouping, so nothing here depends on either).
/// </para>
/// </summary>
public static class DisplayCreditHistory
{
    /// <summary>
    /// Per-step-result credit totals for <paramref name="organizationId"/> since <paramref name="since"/>:
    /// the hold query's filters (workflow usage, not from cache) plus <c>credits &gt; 0</c>, each tagged with
    /// its execution's output resolution by the same LEFT join (a missing execution row is Auto, never dropped).
    /// </summary>
    public static async Task<IReadOnlyList<DisplayStepSample>> LoadStepSamplesAsync(
        IQueryable<UsageEvent> events,
        IQueryable<WorkflowStepResult> stepResults,
        IQueryable<WorkflowStep> steps,
        IQueryable<WorkflowExecution> executions,
        IQueryable<AgentDefinition> agents,
        Guid organizationId,
        DateTime since,
        int maxSamples,
        CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || maxSamples <= 0)
        {
            return [];
        }

        var rows = await events.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId
                && e.Source == UsageSource.Workflow
                && e.StepResultId != null
                && e.OccurredAt >= since)
            .Join(
                stepResults.AsNoTracking().Where(r => !r.FromCache),
                e => e.StepResultId,
                r => (Guid?)r.Id,
                (e, r) => new { r.Id, r.WorkflowStepId, r.WorkflowExecutionId, e.CreditsMillis, e.OccurredAt })
            .Join(
                steps.AsNoTracking(),
                x => x.WorkflowStepId,
                s => s.Id,
                (x, s) => new { x.Id, x.WorkflowExecutionId, s.StepType, s.AgentDefinitionId, x.CreditsMillis, x.OccurredAt })
            .GroupJoin(
                executions.AsNoTracking(),
                x => x.WorkflowExecutionId,
                w => w.Id,
                (x, w) => new { x, w })
            .SelectMany(
                t => t.w.DefaultIfEmpty(),
                (t, w) => new { t.x.Id, t.x.StepType, t.x.AgentDefinitionId, OutputResolution = w == null ? null : w.OutputResolution, t.x.CreditsMillis, t.x.OccurredAt })
            .GroupBy(x => new { x.Id, x.StepType, x.AgentDefinitionId, x.OutputResolution })
            .Select(g => new { g.Key.Id, g.Key.StepType, g.Key.AgentDefinitionId, g.Key.OutputResolution, Credits = g.Sum(x => x.CreditsMillis), Latest = g.Max(x => x.OccurredAt) })
            .Where(x => x.Credits > 0)
            // The NEWEST samples, in a fixed order (latest usage first, the step result's id breaking
            // ties): above the cap, the API's preflight and the engine's hold read the same rows.
            .OrderByDescending(x => x.Latest)
            .ThenBy(x => x.Id)
            .Take(maxSamples)
            .ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return [];
        }

        // A second, small query rather than a left join: an agent row that is gone must not drop the
        // sample, and the distinct id set is bounded by the number of agents the org has run.
        List<Guid> agentIds = [.. rows.Select(r => r.AgentDefinitionId).Distinct()];
        Dictionary<Guid, AgentType> types = await agents.AsNoTracking()
            .Where(a => agentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AgentType })
            .ToDictionaryAsync(a => a.Id, a => a.AgentType, ct).ConfigureAwait(false);

        return [.. rows.Select(r => new DisplayStepSample(
            r.StepType,
            types.TryGetValue(r.AgentDefinitionId, out AgentType type) ? type : null,
            r.AgentDefinitionId,
            r.Credits,
            r.OutputResolution))];
    }

    /// <summary>
    /// The credit total of every <see cref="ExecutionStatus.Passed"/> execution of
    /// <paramref name="workflowDefinitionId"/> that started at or after <paramref name="since"/>,
    /// excluding the step results of <paramref name="excludedStepIds"/> (fixed-price steps, shown as
    /// "up to" instead) and excluding totals of 0 (all own-provider or unpriced). Only runs that chose
    /// the same <paramref name="outputResolution"/> (null = Auto) count: a 4K run's render costs four
    /// times a 1080p one's, so another resolution's totals would describe a different run.
    /// </summary>
    public static Task<IReadOnlyList<long>> LoadRunTotalsAsync(
        IQueryable<UsageEvent> events,
        IQueryable<WorkflowStepResult> stepResults,
        IQueryable<WorkflowExecution> executions,
        Guid organizationId,
        DateTime since,
        Guid workflowDefinitionId,
        IReadOnlyCollection<Guid> excludedStepIds,
        int maxRuns,
        int? outputResolution,
        CancellationToken ct = default)
        => LoadRunTotalsAsync(events, stepResults, executions, null, organizationId, since, workflowDefinitionId,
            excludedStepIds, [], maxRuns, outputResolution, ct);

    /// <summary>
    /// As the overload above, for runs executed from their own <see cref="WorkflowDefinitionKind.ExecutionSnapshot"/>
    /// too: such a run's results reference its snapshot's copies of the steps, so a fixed-price step is
    /// also recognised by its <paramref name="excludedStepOrders"/> within the run's snapshot (a clone
    /// keeps its source step's order). Superseded results (replaced by a Retry) are not part of a total.
    /// </summary>
    public static async Task<IReadOnlyList<long>> LoadRunTotalsAsync(
        IQueryable<UsageEvent> events,
        IQueryable<WorkflowStepResult> stepResults,
        IQueryable<WorkflowExecution> executions,
        IQueryable<WorkflowStep>? steps,
        Guid organizationId,
        DateTime since,
        Guid workflowDefinitionId,
        IReadOnlyCollection<Guid> excludedStepIds,
        IReadOnlyCollection<int> excludedStepOrders,
        int maxRuns,
        int? outputResolution,
        CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || workflowDefinitionId == Guid.Empty || maxRuns <= 0)
        {
            return [];
        }

        List<Guid> excluded = [.. excludedStepIds];
        List<int> excludedOrders = [.. excludedStepOrders];
        var results = executions.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                && x.WorkflowDefinitionId == workflowDefinitionId
                && x.Status == ExecutionStatus.Passed
                && x.OutputResolution == outputResolution
                && (x.StartedAt ?? x.QueuedAt) >= since)
            .Join(
                stepResults.AsNoTracking().Where(r => !excluded.Contains(r.WorkflowStepId) && r.SupersededAt == null),
                x => x.Id,
                r => r.WorkflowExecutionId,
                (x, r) => new { ExecutionId = x.Id, x.SnapshotDefinitionId, ResultId = r.Id, r.WorkflowStepId });
        if (steps is not null && excludedOrders.Count > 0)
        {
            IQueryable<WorkflowStep> stepRows = steps.AsNoTracking();
            results = results.Where(t => t.SnapshotDefinitionId == null
                || !stepRows.Any(s => s.Id == t.WorkflowStepId
                    && s.WorkflowDefinitionId == t.SnapshotDefinitionId
                    && excludedOrders.Contains(s.StepOrder)));
        }

        var runs = await results
            .Select(t => new { t.ExecutionId, t.ResultId })
            .Join(
                events.AsNoTracking().Where(e => e.OrganizationId == organizationId
                    && e.Source == UsageSource.Workflow
                    && e.StepResultId != null),
                x => (Guid?)x.ResultId,
                e => e.StepResultId,
                (x, e) => new { x.ExecutionId, e.CreditsMillis })
            .GroupBy(x => x.ExecutionId)
            .Select(g => new { Total = g.Sum(x => x.CreditsMillis) })
            .Where(x => x.Total > 0)
            .Take(maxRuns)
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. runs.Select(r => r.Total)];
    }

    /// <summary>
    /// For each of <paramref name="loopStepIds"/>, the number of step results it produced in every
    /// <see cref="ExecutionStatus.Passed"/> execution since <paramref name="since"/>: one per review pass.
    /// An execution in which the loop step never ran contributes nothing.
    /// </summary>
    public static Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> LoadLoopPassesAsync(
        IQueryable<WorkflowStepResult> stepResults,
        IQueryable<WorkflowExecution> executions,
        Guid organizationId,
        DateTime since,
        IReadOnlyCollection<Guid> loopStepIds,
        CancellationToken ct = default)
        => LoadLoopPassesAsync(stepResults, executions, null, organizationId, since,
            [.. loopStepIds.Select(id => new WorkflowStep { Id = id })], ct);

    /// <summary>
    /// As the overload above, keyed by each of <paramref name="loopSteps"/>' id, and also counting the
    /// runs executed from their own <see cref="WorkflowDefinitionKind.ExecutionSnapshot"/>: a run of the
    /// loop step's workflow (<see cref="WorkflowExecution.WorkflowDefinitionId"/>) whose results reference
    /// its snapshot's clone of the step — the clone at the same <see cref="WorkflowStep.StepOrder"/>, still
    /// a <see cref="StepType.ReviewLoop"/>. Superseded results (replaced by a Retry) are not passes.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<int>>> LoadLoopPassesAsync(
        IQueryable<WorkflowStepResult> stepResults,
        IQueryable<WorkflowExecution> executions,
        IQueryable<WorkflowStep>? steps,
        Guid organizationId,
        DateTime since,
        IReadOnlyCollection<WorkflowStep> loopSteps,
        CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || loopSteps.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<int>>();
        }

        IQueryable<WorkflowExecution> passed = executions.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Status == ExecutionStatus.Passed);

        List<Guid> ids = [.. loopSteps.Select(s => s.Id)];
        var counts = await stepResults.AsNoTracking()
            .Where(r => ids.Contains(r.WorkflowStepId) && r.ExecutedAt >= since && r.SupersededAt == null)
            .Join(
                passed,
                r => r.WorkflowExecutionId,
                x => x.Id,
                (r, x) => new { r.WorkflowStepId, r.WorkflowExecutionId })
            .GroupBy(x => new { x.WorkflowStepId, x.WorkflowExecutionId })
            .Select(g => new { g.Key.WorkflowStepId, Passes = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        List<(Guid StepId, int Passes)> all = [.. counts.Select(c => (c.WorkflowStepId, c.Passes))];

        if (steps is not null)
        {
            foreach (IGrouping<Guid, WorkflowStep> workflow in loopSteps
                .Where(s => s.WorkflowDefinitionId != Guid.Empty)
                .GroupBy(s => s.WorkflowDefinitionId))
            {
                Guid workflowId = workflow.Key;
                Dictionary<int, Guid> idByOrder = workflow
                    .GroupBy(s => s.StepOrder)
                    .ToDictionary(g => g.Key, g => g.First().Id);
                List<int> orders = [.. idByOrder.Keys];

                var snapshotCounts = await stepResults.AsNoTracking()
                    .Where(r => r.ExecutedAt >= since && r.SupersededAt == null)
                    .Join(
                        passed.Where(x => x.WorkflowDefinitionId == workflowId && x.SnapshotDefinitionId != null),
                        r => r.WorkflowExecutionId,
                        x => x.Id,
                        (r, x) => new { r.WorkflowStepId, r.WorkflowExecutionId, x.SnapshotDefinitionId })
                    .Join(
                        steps.AsNoTracking().Where(s => s.StepType == StepType.ReviewLoop && orders.Contains(s.StepOrder)),
                        t => t.WorkflowStepId,
                        s => s.Id,
                        (t, s) => new { t.WorkflowExecutionId, t.SnapshotDefinitionId, s.WorkflowDefinitionId, s.StepOrder })
                    .Where(t => t.WorkflowDefinitionId == t.SnapshotDefinitionId)
                    .GroupBy(t => new { t.StepOrder, t.WorkflowExecutionId })
                    .Select(g => new { g.Key.StepOrder, Passes = g.Count() })
                    .ToListAsync(ct).ConfigureAwait(false);

                all.AddRange(snapshotCounts.Select(c => (idByOrder[c.StepOrder], c.Passes)));
            }
        }

        return all
            .GroupBy(c => c.StepId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<int>)[.. g.Select(c => c.Passes)]);
    }
}
