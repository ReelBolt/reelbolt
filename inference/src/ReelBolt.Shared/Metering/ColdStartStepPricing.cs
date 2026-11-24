using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// One step's cold-start usage profile (<see cref="StepUsageProfiles"/>), priced through an
/// <see cref="IProfileQuantityPricer"/>. Shared by the Inference API's displayed range and by
/// <see cref="RunCreditHolds"/> (the hold both the API and the engine compute), so a cold step is
/// priced by ONE implementation everywhere.
/// </summary>
public static class ColdStartStepPricing
{
    /// <summary>
    /// The cold-start profile of one execution, priced. A Parallel step is the sum of its branches
    /// (the distinct parallel agents plus the primary one). Chat is priced as the step's own agent for
    /// agent-shaped steps, and as the seat or default agent type (no agent id) for rooms and media steps.
    /// A VideoAnalyze step's looks at events are priced beside it with their own range
    /// (<see cref="StepUsageProfiles.EventLookProfile"/>), unless <paramref name="withoutEventLooks"/>: the hold plan
    /// leaves them out of the cold share because it holds their worst case as an add-on.
    /// </summary>
    public static (StepCostDistribution Cost, bool OwnProvider) PriceProfile(
        WorkflowStep step, AgentType? agentType, IReadOnlyDictionary<Guid, AgentType> agentTypes, IProfileQuantityPricer pricer,
        bool withoutEventLooks = false)
    {
        if (step.StepType == StepType.Parallel)
        {
            long typical = 0, p25 = 0, p75 = 0;
            bool own = false;
            foreach (Guid branch in BranchAgentIds(step))
            {
                StepUsageProfile profile = StepUsageProfiles.For(StepType.Parallel, AgentTypeOf(branch, step, agentTypes));
                (StepCostDistribution cost, bool branchOwn) = Price(profile, branch, pricer);
                typical += cost.TypicalMillis;
                p25 += cost.P25Millis;
                p75 += cost.P75Millis;
                own |= branchOwn;
            }

            return (new StepCostDistribution(typical, p25, p75), own);
        }

        StepUsageProfile single = StepUsageProfiles.For(step, agentType);
        (StepCostDistribution Cost, bool OwnProvider) priced = Price(single, IsAgentShaped(step.StepType) ? step.AgentDefinitionId : null, pricer);
        if (withoutEventLooks || StepUsageProfiles.EventLookProfile(step) is not { } eventLooks)
            return priced;

        StepCostDistribution looks = Price(eventLooks, null, pricer).Cost;
        return (new StepCostDistribution(
            priced.Cost.TypicalMillis + looks.TypicalMillis, priced.Cost.P25Millis + looks.P25Millis, priced.Cost.P75Millis + looks.P75Millis),
            priced.OwnProvider);
    }

    /// <summary>One profile priced: its typical, and its p25/p75 from the profile's own factors.</summary>
    public static (StepCostDistribution Cost, bool OwnProvider) Price(StepUsageProfile profile, Guid? agentId, IProfileQuantityPricer pricer)
    {
        AgentType chatType = profile.PricedAs ?? AgentType.Custom;
        Guid? chatAgent = agentId is { } id && id != Guid.Empty ? id : null;
        long typical = 0;
        bool own = false;
        foreach (ProfileQuantity quantity in profile.Quantities)
        {
            ProfileQuote quote = pricer.Price(quantity, chatType, chatAgent);
            typical += Math.Max(0, quote.CreditsMillis);
            own |= quantity.Target == ProfilePriceTarget.Chat && quote.OwnProvider;
        }

        return (new StepCostDistribution(
            typical,
            (long)Math.Round(typical * profile.LowFactor, MidpointRounding.AwayFromZero),
            (long)Math.Round(typical * profile.HighFactor, MidpointRounding.AwayFromZero)), own);
    }

    /// <summary>Steps whose chat is the step's own agent; rooms (seat-priced) and media steps are not.</summary>
    public static bool IsAgentShaped(StepType stepType) => stepType is not (
        StepType.EditRoom or StepType.GraphicsRoom or StepType.ColorGradeRoom or StepType.DailiesRoom
        or StepType.VideoAnalyze or StepType.VideoCompile or StepType.VideoGenerate or StepType.Voiceover
        or StepType.ThumbnailGenerate or StepType.TimelineRender or StepType.Conditional or StepType.Extract
        or StepType.ExtractFrame or StepType.AudioGenerate or StepType.PinInsert or StepType.AudioCleanup
        or StepType.VideoEnhance or StepType.ImageGenerate);

    /// <summary>The agent type of <paramref name="agentId"/>: the known map first, else the step's own navigation.</summary>
    public static AgentType? AgentTypeOf(Guid agentId, WorkflowStep step, IReadOnlyDictionary<Guid, AgentType> agentTypes)
    {
        if (agentTypes.TryGetValue(agentId, out AgentType known))
        {
            return known;
        }

        // The navigation is "null!" by declaration but genuinely null when the step was loaded without it.
        AgentDefinition? navigation = step.AgentDefinition;
        return navigation is not null && navigation.Id == agentId ? navigation.AgentType : null;
    }

    /// <summary>Every agent a step runs: the primary one, plus a Parallel step's distinct branch agents.</summary>
    public static IEnumerable<Guid> BranchAgentIds(WorkflowStep step)
    {
        List<Guid> ids = [step.AgentDefinitionId];
        if (step.StepType == StepType.Parallel && !string.IsNullOrWhiteSpace(step.ParallelAgentIdsJson))
        {
            try
            {
                ids.AddRange(JsonSerializer.Deserialize<List<Guid>>(step.ParallelAgentIdsJson) ?? []);
            }
            catch (JsonException)
            {
                // An unreadable branch list prices the primary agent alone rather than failing the estimate.
            }
        }

        return ids.Distinct();
    }

    /// <summary>The agent types the steps' own (loaded) agent navigations already carry.</summary>
    public static Dictionary<Guid, AgentType> KnownAgentTypes(IEnumerable<WorkflowStep> steps)
    {
        Dictionary<Guid, AgentType> agentTypes = [];
        foreach (WorkflowStep step in steps)
        {
            // The navigation is "null!" by declaration but genuinely null when the step was loaded without it.
            AgentDefinition? agent = step.AgentDefinition;
            if (agent is not null && agent.Id != Guid.Empty)
            {
                agentTypes[agent.Id] = agent.AgentType;
            }
        }

        return agentTypes;
    }
}

/// <summary>A run's repeating windows, plus which of them are review loops and which ForEach steps have an unknown item count.</summary>
public sealed record RunLoops(IReadOnlyList<LoopInput> Loops, IReadOnlySet<int> ReviewLoops, IReadOnlySet<int> UnknownForEach);

/// <summary>How the steps of a run repeat: review loops and ForEach steps, as <see cref="LoopInput"/>s.</summary>
public static class RunLoopModel
{
    /// <summary>Past Passed runs a review loop needs before its pass count comes from them rather than the cold-start 1–2.</summary>
    public const int MinLoopRuns = 3;

    /// <summary>
    /// The repeating windows: every ReviewLoop (its passes from at least <see cref="MinLoopRuns"/> past
    /// runs in <paramref name="loopPasses"/>, else the cold-start 1–2,
    /// <see cref="RunCreditRange.ColdStartLoop"/>) and every ForEach (a literal item list's length,
    /// else uniform on 1..MaxIterations). With no <paramref name="loopPasses"/> every review loop is cold.
    /// </summary>
    public static RunLoops Of(IReadOnlyList<WorkflowStep> steps, IReadOnlyDictionary<Guid, IReadOnlyList<int>>? loopPasses = null)
    {
        List<LoopInput> loops = [];
        HashSet<int> reviewLoops = [];
        HashSet<int> unknownForEach = [];
        foreach (WorkflowStep step in steps)
        {
            int max = Math.Max(1, step.MaxIterations);
            if (step.StepType == StepType.ReviewLoop)
            {
                int target = step.LoopTargetStepOrder ?? int.MinValue;
                LoopInput loop = loopPasses is not null && loopPasses.TryGetValue(step.Id, out IReadOnlyList<int>? passes) && passes.Count >= MinLoopRuns
                    ? FromHistory(step.StepOrder, target, passes, max)
                    : RunCreditRange.ColdStartLoop(step.StepOrder, target, max);
                loops.Add(loop);
                if (RunCreditRange.HasBody(loop))
                {
                    reviewLoops.Add(step.StepOrder);
                }
            }
            else if (step.StepType == StepType.ForEach)
            {
                if (LiteralItemCount(step.LoopSourceExpression) is { } items)
                {
                    loops.Add(new LoopInput(step.StepOrder, step.StepOrder, items, 0, Math.Max(1, items)));
                }
                else
                {
                    // Uniform on 1..M: mean (M+1)/2, variance (M²-1)/12 — in double, so M = int.MaxValue cannot overflow.
                    double m = max;
                    loops.Add(new LoopInput(step.StepOrder, step.StepOrder, (m + 1d) / 2d, ((m * m) - 1d) / 12d, max));
                    unknownForEach.Add(step.StepOrder);
                }
            }
        }

        return new RunLoops(loops, reviewLoops, unknownForEach);
    }

    private static LoopInput FromHistory(int loopStepOrder, int targetStepOrder, IReadOnlyList<int> passes, int maxIterations)
    {
        double[] clamped = [.. passes.Select(p => (double)Math.Clamp(p, 1, maxIterations))];
        double mean = clamped.Average();
        double variance = clamped.Average(p => (p - mean) * (p - mean));
        return new LoopInput(loopStepOrder, targetStepOrder, mean, variance, maxIterations);
    }

    /// <summary>The length of a ForEach source that is a literal JSON array, or null when it is only known at run time.</summary>
    private static int? LiteralItemCount(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || !source.TrimStart().StartsWith('['))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(source);
            return document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.GetArrayLength() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
