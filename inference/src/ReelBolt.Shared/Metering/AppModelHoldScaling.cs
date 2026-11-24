using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// One agent step's cold-start profile priced twice (docs/design/app-inputs.md 28): at the model the run's app chose for its
/// agent (<see cref="Chosen"/>) and at the agent's ordinary model (<see cref="Baseline"/>, the model its past usage was most
/// likely metered on). The organization's history of the step is in credits of the ordinary model, so it is moved by the
/// ratio of the two before it is held or shown.
/// </summary>
public readonly record struct AppModelPricing(StepCostDistribution Chosen, StepCostDistribution Baseline)
{
    /// <summary>
    /// <paramref name="millis"/> of history moved to the chosen model: times <c>Chosen.Typical / Baseline.Typical</c>, rounded
    /// up. Unchanged when the baseline cannot be priced (0).
    /// </summary>
    public long Scale(long millis)
    {
        if (millis <= 0 || Baseline.TypicalMillis <= 0)
            return Math.Max(0, millis);
        decimal scaled = Math.Ceiling((decimal)millis * Math.Max(0, Chosen.TypicalMillis) / Baseline.TypicalMillis);
        return scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
    }

    /// <summary>
    /// The hold of a step with history: its history hold (the p90) moved to the chosen model, never below the chosen model's
    /// own profile at its high edge (P75), so a cheap past never under-holds a dearer choice.
    /// </summary>
    public long HoldFrom(long historyHoldMillis) => Math.Max(Scale(historyHoldMillis), Math.Max(0, Chosen.P75Millis));

    /// <summary>A history distribution, as shown, moved to the chosen model.</summary>
    public StepCostDistribution Display(StepCostDistribution history) =>
        new(Scale(history.TypicalMillis), Scale(history.P25Millis), Scale(history.P75Millis));
}

/// <summary>
/// The agent-model rule of a step WITH history (docs/design/app-inputs.md 28, the L4 narrowing): when the run's app chose the
/// Chat model of an agent the step runs (<see cref="AppAgentModelAmbient"/>), the step's history (p90 hold and shown range) is
/// moved by the price ratio of the chosen model to the agent's ordinary one, both priced on the step's own cold-start profile
/// through the ONE pricer the API's estimate and the engine's holds share (<see cref="ProfilePriceTable"/>), so the two cannot
/// disagree. Null for every other step and every run without a choice: nothing changes and no extra price is asked.
/// </summary>
public static class AppModelHoldScaling
{
    /// <summary>The two prices of <paramref name="step"/>'s profile, or null when no agent it runs has an app-chosen model.</summary>
    public static AppModelPricing? For(
        WorkflowStep step, AgentType? agentType, IReadOnlyDictionary<Guid, AgentType> agentTypes, IProfileQuantityPricer pricer)
    {
        if (AppAgentModelAmbient.Current is null || !ColdStartStepPricing.IsAgentShaped(step.StepType))
            return null;
        if (!ColdStartStepPricing.BranchAgentIds(step).Any(id => AppAgentModelAmbient.For(id) is not null))
            return null;

        (StepCostDistribution chosen, _) = ColdStartStepPricing.PriceProfile(step, agentType, agentTypes, pricer);
        (StepCostDistribution baseline, _) = ColdStartStepPricing.PriceProfile(step, agentType, agentTypes, new BaselinePricer(pricer));
        return new AppModelPricing(chosen, baseline);
    }

    /// <summary>Prices every chat quantity at the agent's ordinary model (<see cref="IProfileQuantityPricer.PriceBaseline"/>).</summary>
    private sealed class BaselinePricer(IProfileQuantityPricer inner) : IProfileQuantityPricer
    {
        public decimal Markup => inner.Markup;

        public bool IsSeedFallback(ProfilePriceKey key) => inner.IsSeedFallback(key with { Baseline = key.Target == ProfilePriceTarget.Chat });

        public ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
            => inner.PriceBaseline(quantity, chatAgentType, chatAgentDefinitionId);
    }
}
