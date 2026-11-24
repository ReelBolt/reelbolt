using Microsoft.EntityFrameworkCore;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// Binding for the <c>Billing:Estimate</c> configuration section: how much recorded usage B7's
/// estimator looks at before it falls back to <see cref="StepCreditDefaults"/>.
/// </summary>
public sealed class StepCreditEstimateOptions
{
    /// <summary>Configuration section name (env <c>Billing__Estimate__*</c>).</summary>
    public const string SectionName = "Billing:Estimate";

    /// <summary>How far back the history window reaches. Older usage describes a different model and price.</summary>
    public int WindowDays { get; set; } = 90;

    /// <summary>
    /// Hard cap on how many step results one estimate reads. <see cref="WindowDays"/> is what bounds
    /// recency; within that window the cap is a plain ceiling on the work a single preflight or step
    /// reservation can do, and the percentile of a few thousand recent steps is already stable. A
    /// truncated sample is a sample of the window, not a promise about which rows it contains.
    /// </summary>
    public int MaxSamples { get; set; } = 20_000;

    /// <summary>Observations a bucket needs before its percentile is trusted over the B0 defaults.</summary>
    public int MinSamples { get; set; } = StepCreditEstimator.DefaultMinSamples;

    /// <summary>
    /// Past Passed runs of a workflow (each with a non-zero credit total) the DISPLAY range needs before
    /// it is taken from their p25–p75 instead of the per-step combination. Never affects the hold.
    /// </summary>
    public int MinRuns { get; set; } = DefaultMinRuns;

    /// <summary>The default for <see cref="MinRuns"/>.</summary>
    public const int DefaultMinRuns = 5;
}

/// <summary>
/// The one query B7's estimator runs, shared by the Inference API (the execute preflight and
/// <c>GET {id}/estimate</c>) and the WorkflowEngine (the per-step hold), so the two can never drift
/// into estimating the same run differently.
///
/// <para>
/// It joins <c>usage_events</c> to their <c>workflow_step_result</c> and <c>workflow_step</c> to learn
/// the step type and agent, then totals each step result's credits. Cached steps are excluded, for the
/// same reason B0's own measurement query excluded them: a cache hit charges nothing and would drag
/// the p50 towards zero. Failed and cancelled steps are <b>kept</b> — a step that failed after burning
/// five million tokens is a real cost, which is the whole reason B1's failure classification exists.
/// </para>
///
/// <para>
/// QA-15: each sample also carries the output resolution its execution chose, through a LEFT join to
/// <c>workflow_executions</c>, so a render step's hold can read its history per resolution. A step result
/// whose execution row is missing is kept and counted as Auto (null), never dropped.
/// </para>
///
/// <para>
/// Takes <see cref="IQueryable{T}"/>s rather than a context so the caller's own DbContext supplies
/// them (both services map all four tables; the Inference API's are read-only). Everything is
/// materialised into memory before the percentiles are computed: <c>percentile_cont</c> is a
/// PostgreSQL aggregate EF cannot project, and the O(n log n) sort of a bounded, already-filtered
/// sample set is not the expensive part of this query.
/// </para>
/// </summary>
public static class StepCreditHistoryQuery
{
    /// <summary>
    /// Loads <paramref name="organizationId"/>'s per-step credit totals since <paramref name="since"/>.
    /// Returns an empty list — never null, never a throw — when nothing matches, which is the
    /// cold-start path the caller must handle explicitly.
    /// </summary>
    public static async Task<IReadOnlyList<StepCreditSample>> LoadAsync(
        IQueryable<UsageEvent> events,
        IQueryable<WorkflowStepResult> stepResults,
        IQueryable<WorkflowStep> steps,
        IQueryable<WorkflowExecution> executions,
        Guid organizationId,
        DateTime since,
        int maxSamples,
        CancellationToken ct = default)
    {
        if (organizationId == Guid.Empty || maxSamples <= 0)
        {
            return [];
        }

        // Anonymous projections throughout: the query is grouped and capped in SQL, and only the
        // finished per-step totals are mapped onto StepCreditSample in memory, so nothing here depends
        // on EF binding a constructor of ours.
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
            // The NEWEST samples, in a fixed order (latest usage first, the step result's id breaking
            // ties): above the cap, the API's preflight and the engine's hold read the same rows.
            .OrderByDescending(x => x.Latest)
            .ThenBy(x => x.Id)
            .Take(maxSamples)
            .ToListAsync(ct).ConfigureAwait(false);

        return [.. rows.Select(r => new StepCreditSample(r.StepType, r.AgentDefinitionId, r.Credits, r.OutputResolution))];
    }
}
