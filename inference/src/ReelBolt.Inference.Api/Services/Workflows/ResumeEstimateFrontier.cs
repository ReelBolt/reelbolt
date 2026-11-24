using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// Which steps a RESUME is priced for (plan decision 7: "Resume's estimate counts only steps from the frontier
/// on"). A cheap API-side equivalent of the engine's <c>ResumeCursor</c>, for the 402 preflight only.
/// </summary>
/// <remarks>
/// <para>
/// <b>The approximation.</b> The run's non-superseded results are read in written order (ExecutedAt, then
/// CompletedAt, then id: the cursor's order); of a run of consecutive rows of one step only the last counts (the
/// cursor's rule for a miss that was re-run). The frontier is the step of the first counted row that is not
/// Completed/Skipped; with none, the step after the last counted row. Every step whose StepOrder is at or after the
/// frontier's is charged.
/// </para>
/// <para>
/// What it does NOT model, deliberately: storage HEADs (a reused render whose object is gone re-runs and is not
/// priced here), branch decisions (a Conditional that skipped steps before the frontier changes nothing; steps
/// after it are all charged), and loops back to earlier StepOrders (an iteration re-run of a step before the
/// frontier is not charged). It is advisory like every preflight: the engine's per-step credit gate enforces each
/// live step. A row of a step the snapshot does not have is treated as "unknown": every step is charged.
/// </para>
/// </remarks>
public static class ResumeEstimateFrontier
{
    public static IReadOnlyCollection<int> ChargedStepOrders(
        IReadOnlyList<WorkflowStep> steps, IEnumerable<WorkflowStepResult> results)
    {
        Dictionary<Guid, int> orderOf = steps.ToDictionary(s => s.Id, s => s.StepOrder);
        List<WorkflowStepResult> trajectory = results
            .Where(r => r.SupersededAt == null)
            .OrderBy(r => r.ExecutedAt)
            .ThenBy(r => r.CompletedAt ?? DateTime.MaxValue)
            .ThenBy(r => r.Id)
            .ToList();

        int frontier = int.MinValue;
        int? lastCompleted = null;
        bool found = false;
        for (int i = 0; i < trajectory.Count; i++)
        {
            WorkflowStepResult row = trajectory[i];
            if (i + 1 < trajectory.Count && trajectory[i + 1].WorkflowStepId == row.WorkflowStepId)
                continue;

            if (!orderOf.TryGetValue(row.WorkflowStepId, out int order))
                return AllOrders(steps);

            if (row.Status is not (StepStatus.Completed or StepStatus.Skipped))
            {
                frontier = order;
                found = true;
                break;
            }

            lastCompleted = order;
        }

        if (!found && lastCompleted is { } last)
            frontier = last + 1;

        return steps.Where(s => s.StepOrder >= frontier).Select(s => s.StepOrder).ToHashSet();
    }

    private static HashSet<int> AllOrders(IReadOnlyList<WorkflowStep> steps) => steps.Select(s => s.StepOrder).ToHashSet();
}
