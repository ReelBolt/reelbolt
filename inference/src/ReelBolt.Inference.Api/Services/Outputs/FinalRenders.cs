using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Outputs;

/// <summary>One render (a step result with an output file) as the final-deliverable rule sees it.</summary>
public readonly record struct RenderCandidate(Guid Id, Guid WorkflowExecutionId, StepStatus Status, DateTime ProducedAt);

/// <summary>
/// The single rule for "the finished video of a run": the LAST render of a run that passed; every
/// earlier render of that run and every render of a run that did not pass is a draft. Shared by the
/// outputs list and the timelines list so both agree on what is final.
/// </summary>
public static class FinalRenders
{
    public static HashSet<Guid> FinalRenderIds(
        IEnumerable<RenderCandidate> renders, Func<Guid, bool> executionPassed) =>
        renders
            .Where(r => r.Status == StepStatus.Completed && executionPassed(r.WorkflowExecutionId))
            .GroupBy(r => r.WorkflowExecutionId)
            .Select(g => g.OrderByDescending(r => r.ProducedAt).First().Id)
            .ToHashSet();
}
