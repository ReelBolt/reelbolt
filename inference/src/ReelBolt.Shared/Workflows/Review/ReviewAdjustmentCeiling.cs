using System.Text.Json;
using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Shared.Workflows.Review;

/// <summary>
/// The bounded worst case a review loop that may change steps adds to a run (docs/design/review-loop-adjustments.md
/// "What it can cost"): how many EXTRA times each step BEFORE the loop target could run because a review asked for a
/// change. Steps inside the loop window already repeat with the loop and are priced by the loop model; this covers the
/// steps a review can re-enter from outside it: every step before the loop target a review may change (an allowlisted
/// setting or a change request) and every step between it and the loop target that depends on it, at most once per
/// loop-back (<c>MaxIterations - 1</c>). The pre-run estimate shows it in its "up to" ceiling; the engine holds each
/// re-run like any step (one hold per step result, at dispatch), so <c>RequiredMillis</c> is not changed by it.
/// </summary>
public static class ReviewAdjustmentCeiling
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Extra runs by step order (only steps with at least one), for every review loop with adjustments on.</summary>
    /// <param name="steps">The workflow's steps, in order.</param>
    /// <param name="agentOf">The agent type an Agent step hosts (decides whether a script step takes a change request).</param>
    public static IReadOnlyDictionary<int, int> ExtraRunsByStepOrder(IReadOnlyList<WorkflowStep> steps, Func<WorkflowStep, AgentType?> agentOf)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(agentOf);
        Dictionary<int, int> extra = [];
        WorkflowStepDependencyGraph? graph = null;
        foreach (WorkflowStep review in steps.Where(s => s.StepType == StepType.ReviewLoop && AllowsAdjustments(s)))
        {
            int loopBacks = Math.Max(0, Math.Max(1, review.MaxIterations) - 1);
            if (loopBacks == 0 || review.LoopTargetStepOrder is not { } target)
                continue;

            graph ??= WorkflowStepDependencyGraph.Build(steps);
            List<WorkflowStep> before = [.. steps.Where(s => s.StepOrder < target && s.StepOrder < review.StepOrder)];
            HashSet<int> reachable = [];
            foreach (WorkflowStep step in before.Where(s => Changeable(s, agentOf(s))))
            {
                reachable.Add(step.StepOrder);
                reachable.UnionWith(graph.DownstreamOf(step.StepOrder).Where(o => o < target));
            }

            foreach (int order in reachable)
                extra[order] = extra.GetValueOrDefault(order) + loopBacks;
        }

        return extra;
    }

    /// <summary>True when a review may change <paramref name="step"/> (an allowlisted setting, or a change request).</summary>
    public static bool Changeable(WorkflowStep step, AgentType? agent) =>
        ReviewAdjustableSettings.For(step.StepType).Count > 0 || StepChangeCapabilities.For(step.StepType, agent).Supported;

    /// <summary>True when the ReviewLoop step's config turns adjustments on (absent or unreadable = off).</summary>
    public static bool AllowsAdjustments(WorkflowStep step)
    {
        if (string.IsNullOrWhiteSpace(step.ReviewLoopConfigJson))
            return false;
        try
        {
            return JsonSerializer.Deserialize<ReviewLoopStepConfig>(step.ReviewLoopConfigJson, ConfigJson)?.AllowAdjustments == true;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
