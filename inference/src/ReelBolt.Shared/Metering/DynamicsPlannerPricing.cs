using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// The one rule for a skipped <c>DynamicsPlanner</c> step: it plans a dynamic edit, so a run with no effective pace (no brief
/// pace and no style dynamics) has nothing to plan. The agent step executor skips it, the engine takes no credit hold for it
/// and the pre-run estimate (the API's <c>RequiredMillis</c> and the engine's hold plan) leaves it out, all through
/// <see cref="Skips"/>, so the three cannot drift.
/// </summary>
public static class DynamicsPlannerPricing
{
    private static readonly AsyncLocal<bool?> Pace_ = new();

    /// <summary>
    /// True/false when the caller knows whether the run has an effective pace; null when nobody said (unknown, which the
    /// pricing holds as if the planner runs: a hold only ever grows).
    /// </summary>
    public static bool? RunHasPace => Pace_.Value;

    /// <summary>
    /// True when a step of <paramref name="agentType"/> is skipped for a run whose pace is known:
    /// <paramref name="runHasPace"/> false and the agent a <see cref="AgentType.DynamicsPlanner"/>. An unknown pace
    /// (null) never skips.
    /// </summary>
    public static bool Skips(AgentType? agentType, bool? runHasPace) =>
        agentType == AgentType.DynamicsPlanner && runHasPace == false;

    /// <summary>
    /// Whether a run has an effective pace: a brief pace the engine reads (<c>calm|medium|fast</c>, any case) or style
    /// dynamics. The same two inputs the engine's <c>RunStyleOverrides.EffectiveDynamics</c> reads.
    /// </summary>
    public static bool HasPace(string? briefPace, StyleDynamics? styleDynamics) =>
        styleDynamics is not null
        || briefPace?.Trim().ToLowerInvariant() is RunBrief.PaceCalm or RunBrief.PaceMedium or RunBrief.PaceFast;

    /// <summary><see cref="HasPace"/> for a stored run: its brief and style snapshot columns.</summary>
    public static bool HasPaceStored(string? runBriefJson, string? styleConfigJson)
    {
        RunStyleSnapshot? style = StyleJson.TryParseSnapshot(styleConfigJson);
        return HasPace(RunBriefJson.TryParse(runBriefJson)?.Pace, style is null || style.Config.IsEmpty ? null : style.Config.Dynamics);
    }

    /// <summary><see cref="Skips"/> for the pace entered on this async flow.</summary>
    public static bool SkipsAmbient(AgentType? agentType) => Skips(agentType, Pace_.Value);

    /// <summary>Makes <paramref name="runHasPace"/> the run's pace the pricing reads until the result is disposed.</summary>
    public static IDisposable Enter(bool? runHasPace)
    {
        bool? previous = Pace_.Value;
        Pace_.Value = runHasPace;
        return new Restore(previous);
    }

    private sealed class Restore(bool? previous) : IDisposable
    {
        public void Dispose() => Pace_.Value = previous;
    }
}
