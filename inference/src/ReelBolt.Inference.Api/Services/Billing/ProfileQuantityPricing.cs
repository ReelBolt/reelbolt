using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;

namespace ReelBolt.Inference.Api.Services.Billing;

/// <summary>
/// The display path's inputs beyond the steps themselves: history for the per-step buckets, the agent
/// type of every agent the run names, past run totals, and past review-loop pass counts.
/// </summary>
/// <param name="StepSamples">Charged (credits &gt; 0) per-step-result samples, with agent type.</param>
/// <param name="AgentTypes">Agent definition id to agent type, for every agent the steps name (primary and parallel).</param>
/// <param name="RunTotals">Credit totals of past Passed runs of this workflow (fixed-price steps excluded, zeros excluded).</param>
/// <param name="LoopPasses">Per ReviewLoop step id, its result count in each past Passed run.</param>
/// <param name="MinRuns">Run totals needed before the range comes from them (<c>Billing:Estimate:MinRuns</c>).</param>
public sealed record DisplayCreditInputs(
    IReadOnlyList<DisplayStepSample> StepSamples,
    IReadOnlyDictionary<Guid, AgentType> AgentTypes,
    IReadOnlyList<long> RunTotals,
    IReadOnlyDictionary<Guid, IReadOnlyList<int>> LoopPasses,
    int MinRuns = StepCreditEstimateOptions.DefaultMinRuns)
{
    /// <summary>No history and no known agent types: the cold-start display.</summary>
    public static DisplayCreditInputs Empty { get; } = new(
        [], new Dictionary<Guid, AgentType>(), [], new Dictionary<Guid, IReadOnlyList<int>>());
}
