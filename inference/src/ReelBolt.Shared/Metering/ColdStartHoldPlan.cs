using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;

namespace ReelBolt.Shared.Metering;

/// <summary>
/// Cold-start hold re-plan: how the low edge of a run's cold steps is split across them. Pure arithmetic.
///
/// <para>
/// The run's cold low edge <c>L</c> is <see cref="RunCreditRange.Combine"/> over the cold steps only
/// (their priced cold-start profiles, and the run's loops) — the bottom of the usual range the user is
/// shown for that part of the run. Each cold step holds <c>L × typical_i / Σ typical</c>, with
/// single-execution typicals as the weights (a loop body counts once in the weights; its 1–2 passes
/// are already inside <c>L</c>), rounded by largest remainder so the shares sum to <c>L</c> exactly.
/// Each share is then floored at <see cref="MinimumHoldMillis"/>.
/// </para>
/// <para>
/// A share is not a hold on its own: <see cref="RunCreditHolds.Plan"/> holds, per cold step, the larger
/// of the share and the step's previous default hold. The re-plan raised the cold-start hold to the
/// low edge; it never lowered one.
/// </para>
/// </summary>
public static class ColdStartHoldPlan
{
    /// <summary>
    /// The smallest share, and the smallest hold a charged cold step takes: a 0 hold makes the credit
    /// gate take no reservation at all, and a step with no reservation is never settled, so it would be
    /// free. In the run plan this floor is defensive only: a charged cold step's previous default is
    /// already above it.
    /// </summary>
    public const long MinimumHoldMillis = 1;

    /// <summary>
    /// The per-step-order hold of every step in <paramref name="coldSteps"/>. Empty when there are no
    /// cold steps. Every value is at least <see cref="MinimumHoldMillis"/>; before that floor they sum to
    /// <c>Combine(coldSteps, loops).LowMillis</c> exactly. Step orders must be distinct.
    /// </summary>
    public static IReadOnlyDictionary<int, long> Allocate(IReadOnlyList<RunStepInput> coldSteps, IReadOnlyList<LoopInput> loops)
    {
        if (coldSteps.Count == 0)
        {
            return new Dictionary<int, long>();
        }

        long low = RunCreditRange.Combine(coldSteps, loops).LowMillis;
        return Allocate(coldSteps, low);
    }

    /// <summary>Splits <paramref name="lowMillis"/> across <paramref name="coldSteps"/> (see <see cref="Allocate(IReadOnlyList{RunStepInput}, IReadOnlyList{LoopInput})"/>).</summary>
    internal static IReadOnlyDictionary<int, long> Allocate(IReadOnlyList<RunStepInput> coldSteps, long lowMillis)
    {
        if (coldSteps.Select(s => s.StepOrder).Distinct().Count() != coldSteps.Count)
        {
            throw new ArgumentException("Cold step orders must be distinct.", nameof(coldSteps));
        }

        // A fixed-price step never belongs here (it is outside the range); weigh it 0 if it does.
        long[] weights = [.. coldSteps.Select(s => s.FixedMaxMillis > 0 ? 0 : Math.Max(0, s.Cost.TypicalMillis))];
        Int128 total = 0;
        foreach (long weight in weights)
        {
            total += weight;
        }

        long low = Math.Max(0, lowMillis);
        long[] shares = new long[coldSteps.Count];
        if (total > 0 && low > 0)
        {
            // Largest remainder: floor every exact share, then hand the leftover units (fewer than the
            // number of steps) to the largest fractional parts, ties to the earlier step.
            Int128[] remainders = new Int128[coldSteps.Count];
            long assigned = 0;
            for (int i = 0; i < weights.Length; i++)
            {
                Int128 numerator = (Int128)low * weights[i];
                shares[i] = (long)(numerator / total);
                remainders[i] = numerator % total;
                assigned += shares[i];
            }

            long leftover = low - assigned;
            foreach (int i in Enumerable.Range(0, shares.Length)
                .OrderByDescending(i => remainders[i])
                .ThenBy(i => coldSteps[i].StepOrder)
                .Take((int)Math.Max(0, leftover)))
            {
                shares[i]++;
            }
        }

        Dictionary<int, long> plan = [];
        for (int i = 0; i < shares.Length; i++)
        {
            plan[coldSteps[i].StepOrder] = Math.Max(MinimumHoldMillis, shares[i]);
        }

        return plan;
    }
}

/// <summary>
/// The per-step holds of one run, aligned with the steps it was planned from.
/// </summary>
/// <param name="HoldsMillis">
/// One FINAL hold per step, in the order of the steps given to <see cref="RunCreditHolds.Plan"/>: the run's
/// output resolution is already applied (<see cref="ResolutionHoldScaling.HoldAt"/>), so a caller never scales it again.
/// </param>
/// <param name="ColdLowMillis">The cold low edge <c>L</c> the cold steps' shares split (0 when there are none, or on fallback); the cold holds sum to at least it.</param>
/// <param name="UsedFallback">True when the cold plan could not be computed and every cold step holds B0's default low.</param>
/// <param name="RendersOutput">One flag per step: whether it renders the run's output (<see cref="ResolutionHoldScaling.RendersOutput"/>).</param>
/// <param name="SourceAddOnsMillis">Per step: the part of its hold that comes from known source footage (<see cref="SourceFootageCredits"/>); null when none.</param>
public sealed record RunHoldPlan(
    IReadOnlyList<long> HoldsMillis, long ColdLowMillis, bool UsedFallback, IReadOnlyList<bool> RendersOutput,
    IReadOnlyList<long>? SourceAddOnsMillis = null)
{
    /// <summary>Σ of the per-step holds: what a run must be able to afford to start (on a cold start, at least the old Σ default and the low edge).</summary>
    public long RequiredMillis => HoldsMillis.Sum();
}

/// <summary>
/// The ONE place a run's per-step credit holds are decided, used by the Inference API's estimate
/// (<c>RequiredMillis</c>, the 402) and by the WorkflowEngine before each step's reservation, so the
/// two cannot drift.
///
/// <list type="bullet">
/// <item>A step with history (<see cref="CreditEstimateBasis.History"/>/<see cref="CreditEstimateBasis.StepTypeHistory"/>)
/// holds its <see cref="StepCreditEstimator"/> p90, exactly as before; when the run's app chose its agent's model, that p90
/// moved to the chosen model and never below the chosen model's profile (<see cref="AppModelHoldScaling"/>).</item>
/// <item>A not-charged step holds 0.</item>
/// <item>A VideoGenerate step holds what <see cref="StepCreditEstimator.Estimate"/> answers given the
/// caller's declared price (its declared price, else today's fallback). It is fixed-price and never
/// part of the cold plan. The engine resolves the declared price itself before asking.</item>
/// <item>Every other step with <see cref="CreditEstimateBasis.Default"/> is cold: it holds the larger of
/// its previous default hold (B0's default low, what <see cref="StepCreditEstimator.Estimate"/> answers)
/// and its share of the run's cold low edge (<see cref="ColdStartHoldPlan"/>), its profile priced through
/// <paramref name="pricer"/> and the loops modelled cold (<see cref="RunLoopModel.Of"/> with no loop
/// history). So a cold step never holds less than before the cold-start hold re-plan, a zero-typical step keeps its
/// default rather than 1 millicredit, and the cold steps together hold at least their low edge.</item>
/// </list>
///
/// <para>
/// QA-15, the run's output resolution: a step that renders the output (<see cref="ResolutionHoldScaling.RendersOutput"/>,
/// its agent type from <paramref name="agentTypes"/>) is estimated from its REFERENCE samples only (Auto and
/// 1080p), so the plan above is priced at the 1080p reference; every other step pools all resolutions. Each
/// planned hold then becomes <see cref="ResolutionHoldScaling.HoldAt"/>: scaled by the pixel factor above 1,
/// and raised (never lowered) to the p90 of the step's own history at exactly that resolution.
/// </para>
///
/// Never throws: if the cold plan cannot be computed, each cold step holds B0's default low, as before.
/// </summary>
public static class RunCreditHolds
{
    /// <summary>Plans the holds of <paramref name="steps"/> for an Auto run (<c>outputResolution: null</c>).</summary>
    public static RunHoldPlan Plan(
        IReadOnlyList<WorkflowStep> steps,
        StepCreditEstimator history,
        IReadOnlyDictionary<int, long>? videoHolds,
        IReadOnlyDictionary<Guid, AgentType> agentTypes,
        IProfileQuantityPricer pricer)
        => Plan(steps, history, videoHolds, agentTypes, pricer, outputResolution: null);

    /// <summary>Plans the FINAL holds of <paramref name="steps"/> for a run at <paramref name="outputResolution"/>.</summary>
    /// <param name="steps">Every step of the run.</param>
    /// <param name="history">The organization's hold history (an empty estimator on a cold start).</param>
    /// <param name="videoHolds">Declared VideoGenerate prices by step order, when the caller priced them.</param>
    /// <param name="agentTypes">Agent definition id to agent type, for the agents the steps name.</param>
    /// <param name="pricer">Prices the cold-start profiles (a table built by <see cref="ProfilePriceTable"/> in production).</param>
    /// <param name="outputResolution">The run's chosen output resolution (short side; null = Auto).</param>
    public static RunHoldPlan Plan(
        IReadOnlyList<WorkflowStep> steps,
        StepCreditEstimator history,
        IReadOnlyDictionary<int, long>? videoHolds,
        IReadOnlyDictionary<Guid, AgentType> agentTypes,
        IProfileQuantityPricer pricer,
        int? outputResolution,
        IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>>? sourceFootage = null)
    {
        // A Plan-stage compile (encode-once) renders nothing: it holds nothing, so the resolution scaling and the
        // compile's own history must not raise it either.
        bool[] renders = [.. steps.Select(step => !CompileEncodeStages.IsPlanStage(step) && ResolutionHoldScaling.RendersOutput(
            step.StepType, ColdStartStepPricing.AgentTypeOf(step.AgentDefinitionId, step, agentTypes)))];

        // docs/design/app-inputs.md 28: an agent step whose model the run's app chose moves its history to that model. Null for
        // every step of a run without such a choice (no extra price is asked, every hold as before).
        AppModelPricing?[] chosen = [.. steps.Select(step => AppModelHoldScaling.For(
            step, ColdStartStepPricing.AgentTypeOf(step.AgentDefinitionId, step, agentTypes), agentTypes, pricer))];
        RunHoldPlan reference = PlanAtReference(steps, history, videoHolds, agentTypes, pricer, renders, sourceFootage, chosen);

        long[] final = new long[steps.Count];
        for (int i = 0; i < steps.Count; i++)
        {
            WorkflowStep step = steps[i];
            long? native = renders[i] ? history.NativeHoldMillis(step.StepType, step.AgentDefinitionId, outputResolution) : null;
            if (native is { } measured && chosen[i] is { } model)
                native = model.Scale(measured);
            final[i] = ResolutionHoldScaling.HoldAt(reference.HoldsMillis[i], renders[i], outputResolution, native);
        }

        return reference with { HoldsMillis = final };
    }

    /// <summary>The plan at the 1080p reference: render steps read reference samples only (see this class's summary).</summary>
    private static RunHoldPlan PlanAtReference(
        IReadOnlyList<WorkflowStep> steps,
        StepCreditEstimator history,
        IReadOnlyDictionary<int, long>? videoHolds,
        IReadOnlyDictionary<Guid, AgentType> agentTypes,
        IProfileQuantityPricer pricer,
        bool[] renders,
        IReadOnlyDictionary<int, IReadOnlyList<SourceFootage>>? sourceFootage,
        AppModelPricing?[] chosen)
    {
        long[] holds = new long[steps.Count];
        long[] searchAddOns = new long[steps.Count];
        long[] sourceAddOns = new long[steps.Count];
        List<int> cold = [];
        for (int i = 0; i < steps.Count; i++)
        {
            WorkflowStep step = steps[i];

            // Encode-once: a Plan-stage compile is neither held nor part of the cold plan. It records no usage, so it
            // leaves no sample either (history is built from usage rows): the Final-stage compile's p90 stays the
            // p90 of an encode, and the run still holds exactly one encode.
            if (CompileEncodeStages.IsPlanStage(step))
            {
                holds[i] = 0;
                continue;
            }

            // A skipped DynamicsPlanner (the run has no pace) takes no hold and is not part of the cold plan.
            if (DynamicsPlannerPricing.SkipsAmbient(ColdStartStepPricing.AgentTypeOf(step.AgentDefinitionId, step, agentTypes)))
            {
                holds[i] = 0;
                continue;
            }

            long? declared = StepCreditDefaults.IsDeclaredPriced(step.StepType)
                && videoHolds is not null && videoHolds.TryGetValue(step.StepOrder, out long price)
                ? price
                : null;

            // The cold-start default at the pricer's (live) markup, like the profiles priced below (T-032); a render
            // step reads only its reference (Auto/1080p) samples (QA-15).
            StepCreditEstimate estimate = history.Estimate(
                step.StepType, step.AgentDefinitionId, declared, pricer.Markup, referenceOnly: renders[i],
                computeOnly: StepCreditDefaults.IsComputeOnly(step));
            holds[i] = Math.Max(0, estimate.HoldMillis);

            // An app chose this agent's model (docs/design/app-inputs.md 28): the p90 of history metered on the ordinary model
            // is moved to the chosen one, never below the chosen model's own profile. A cold step is already priced at the
            // chosen model (AppAgentModelAmbient, through the pricer), so only a history-backed hold moves here.
            if (chosen[i] is { } model && estimate.Basis is CreditEstimateBasis.History or CreditEstimateBasis.StepTypeHistory)
                holds[i] = model.HoldFrom(holds[i]);

            // Text-prompted segmentation (initiative "prompted region effects", plan §6): a VideoAnalyze
            // step whose targets name Method = Segmentation buys a hosted, per-frame-billed call that the
            // token-shaped row above cannot see, so the hold grows by the worst case it can buy. Added
            // AFTER the history/default ladder and before the cold-start share, so it is part of the
            // floor a cold step may never drop below — an addition only, never a subtraction. Both the
            // API's preflight and the engine's reservation read this plan, so the two cannot disagree.
            if (step.StepType == StepType.VideoAnalyze)
            {
                holds[i] += SegmentationCreditPricing.AddOnMillis(step.VideoAnalyzeConfigJson);

                // Looks at events (describeEvents): the most the step can take (the author's count, else the automatic
                // ceiling), each at the describer's own token ceiling, on the Vision model the step names (the model the
                // run's app chose is already bound into the step). Held once, whether the step has history or is cold:
                // kept apart from the ladder like the searches, and left out of the cold share below.
                searchAddOns[i] += EventLookAddOn(pricer, step);

                // Known source footage (initiative large-source-footage): the settle meters the probed seconds at
                // the source's tier plus the ASR seconds, which the fixed profile does not see. Kept apart like the
                // search add-on (a cold step's re-plan is a max that would swallow it) and added at the end.
                if (sourceFootage is not null && sourceFootage.TryGetValue(step.StepOrder, out IReadOnlyList<SourceFootage>? footage))
                {
                    sourceAddOns[i] = SourceFootageCredits.AddOnMillis(step, footage, pricer);
                }
            }

            // Web search (initiative agent-web-search): an Agent step whose agent type is granted ToolGroup.WebSearch
            // can buy up to WebSearchLimits.DefaultMaxCallsPerStep searches, which the token-shaped row above cannot
            // see. The hold grows by that worst case at the default WebSearch provider's price, and by nothing when
            // none resolves (the seed price of the kind is 0) or when it is the organization's own (not charged).
            // An addition only, and kept apart from the ladder: a cold step's hold is raised to its share of the
            // run's low edge (a max), which would swallow a term added before it, so the term is added to the
            // final hold below. The API's preflight and the engine's reservation both read this plan, so the two
            // cannot disagree.
            if (step.StepType == StepType.Agent
                && ColdStartStepPricing.AgentTypeOf(step.AgentDefinitionId, step, agentTypes) is { } searchAgent
                && ToolGroupCatalog.GroupsFor(searchAgent).Contains(ToolGroup.WebSearch))
            {
                searchAddOns[i] = SearchAddOn(pricer, searchAgent, step.AgentDefinitionId);
            }

            // Looks at single moments (LookAtMoment): an Agent step whose agent type is granted ToolGroup.MomentLook can
            // buy up to MomentLookLimits.MaxLooksPerStep small Vision calls, which its token row cannot see. The hold
            // grows by that worst case, like the searches above and kept apart from the ladder for the same reason. A
            // look needs a strip, and only an analysis that describes events keeps strips: a run with no such
            // VideoAnalyze step adds nothing and asks for no price. An EditRoom holds it too: its seats never get the
            // tool, but its solo fallback is an ordinary story editor run that may look. A Parallel step holds it once
            // when its own agent or any of its extra agents may look: they share the step's one look counter.
            if (MomentLookAgentOf(step, agentTypes) is { } lookAgent)
            {
                searchAddOns[i] += MomentLookAddOn(pricer, steps, lookAgent.Type, lookAgent.AgentDefinitionId);
            }

            if (IsCold(step.StepType, estimate))
            {
                cold.Add(i);
            }
        }

        if (cold.Count == 0)
        {
            return new RunHoldPlan(WithSearchAddOns(holds, searchAddOns, sourceAddOns), 0, false, renders, sourceAddOns);
        }

        try
        {
            List<RunStepInput> inputs = [.. cold.Select(i =>
            {
                WorkflowStep step = steps[i];
                AgentType? agentType = ColdStartStepPricing.AgentTypeOf(step.AgentDefinitionId, step, agentTypes);
                (StepCostDistribution cost, _) = ColdStartStepPricing.PriceProfile(step, agentType, agentTypes, pricer, withoutEventLooks: true);
                return new RunStepInput(step.StepOrder, cost, 0);
            })];

            IReadOnlyList<LoopInput> loops = RunLoopModel.Of(steps).Loops;
            long low = RunCreditRange.Combine(inputs, loops).LowMillis;
            IReadOnlyDictionary<int, long> shares = ColdStartHoldPlan.Allocate(inputs, low);

            // The re-plan raised the cold hold to the low edge and never lowered one. holds[i] is
            // still the step's pre-re-plan hold (B0's default low), so the share only ever adds to it.
            // The final floor is defensive: a charged cold step's default is never 0 today.
            long[] planned = [.. holds];
            foreach (int i in cold)
            {
                planned[i] = Math.Max(ColdStartHoldPlan.MinimumHoldMillis, Math.Max(holds[i], shares[steps[i].StepOrder]));
            }

            return new RunHoldPlan(WithSearchAddOns(planned, searchAddOns, sourceAddOns), low, false, renders, sourceAddOns);
        }
        catch (Exception)
        {
            // The holds above already carry B0's default low for every cold step: today's rule.
            return new RunHoldPlan(WithSearchAddOns(holds, searchAddOns, sourceAddOns), 0, true, renders, sourceAddOns);
        }
    }

    /// <summary>The worst case of one step's searches at the default WebSearch provider's price; 0 when it is the organization's own, unresolvable, or the pricer fails.</summary>
    private static long SearchAddOn(IProfileQuantityPricer pricer, AgentType agentType, Guid agentDefinitionId)
    {
        try
        {
            ProfileQuote searches = pricer.Price(
                new ProfileQuantity(UsageKind.WebSearches, ProfilePriceTarget.WebSearch, WebSearchLimits.DefaultMaxCallsPerStep),
                agentType, agentDefinitionId);
            return searches.OwnProvider ? 0 : Math.Max(0, searches.CreditsMillis);
        }
        catch (Exception)
        {
            // The plan never throws: a pricer that cannot answer leaves the step at its token hold.
            return 0;
        }
    }

    /// <summary>
    /// The agent whose looks <paramref name="step"/> may take, or null when it cannot take any. For a Parallel step it is
    /// the first of its agents (its own, then the extra ones) that holds the tool.
    /// </summary>
    private static (AgentType Type, Guid AgentDefinitionId)? MomentLookAgentOf(WorkflowStep step, IReadOnlyDictionary<Guid, AgentType> agentTypes)
    {
        static bool MayLook(AgentType? type) => type is { } t && ToolGroupCatalog.GroupsFor(t).Contains(ToolGroup.MomentLook);

        switch (step.StepType)
        {
            case StepType.Agent:
                return ColdStartStepPricing.AgentTypeOf(step.AgentDefinitionId, step, agentTypes) is { } own && MayLook(own)
                    ? (own, step.AgentDefinitionId)
                    : null;
            case StepType.EditRoom:
                return MayLook(AgentType.VideoStoryEditor) ? (AgentType.VideoStoryEditor, step.AgentDefinitionId) : null;
            case StepType.Parallel:
                foreach (Guid branch in ColdStartStepPricing.BranchAgentIds(step))
                {
                    if (ColdStartStepPricing.AgentTypeOf(branch, step, agentTypes) is { } type && MayLook(type))
                        return (type, branch);
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The worst case of one step's looks at moments: <see cref="MomentLookLimits.MaxLooksPerStep"/> looks, each
    /// <see cref="StepUsageProfiles.EventLookInputTokens"/> in and <see cref="StepUsageProfiles.EventLookMaxOutputTokens"/>
    /// out (the describer's own ceiling),
    /// on the Vision model a describing analysis of the run names (the dearest when several do). 0 when no analysis of the
    /// run describes events, when the model is the organization's own, or when the pricer fails.
    /// </summary>
    private static long MomentLookAddOn(IProfileQuantityPricer pricer, IReadOnlyList<WorkflowStep> steps, AgentType agentType, Guid agentDefinitionId)
    {
        long worst = 0;
        foreach (Guid? providerId in steps
            .Where(s => s.StepType == StepType.VideoAnalyze)
            .Select(s => StepUsageProfiles.EventLookProvider(s.VideoAnalyzeConfigJson))
            .Where(p => p.Describes)
            .Select(p => p.ProviderId)
            .Distinct())
        {
            worst = Math.Max(worst, LooksMillis(pricer, MomentLookLimits.MaxLooksPerStep, providerId, agentType, agentDefinitionId));
        }

        return worst;
    }

    /// <summary>
    /// The worst case of a VideoAnalyze step's own looks at events (<see cref="StepUsageProfiles.EventLookWorstCase"/>);
    /// 0 when it takes none, when the model is the organization's own, or when the pricer fails.
    /// </summary>
    private static long EventLookAddOn(IProfileQuantityPricer pricer, WorkflowStep step) =>
        StepUsageProfiles.EventLookWorstCase(step.VideoAnalyzeConfigJson) is { } looks
            ? LooksMillis(pricer, looks.Looks, looks.ProviderId, AgentType.Custom, null)
            : 0;

    /// <summary>
    /// <paramref name="looks"/> looks at their worst case: each <see cref="StepUsageProfiles.EventLookInputTokens"/> in and
    /// <see cref="StepUsageProfiles.EventLookMaxOutputTokens"/> out, on the Vision model <paramref name="providerId"/>
    /// (null is the default one). An organization's own model costs nothing; a pricer that cannot answer gives 0.
    /// </summary>
    private static long LooksMillis(IProfileQuantityPricer pricer, int looks, Guid? providerId, AgentType agentType, Guid? agentDefinitionId)
    {
        try
        {
            ProfileQuote input = pricer.Price(
                new ProfileQuantity(UsageKind.LlmInputTokens, ProfilePriceTarget.Vision, looks * StepUsageProfiles.EventLookInputTokens, providerId),
                agentType, agentDefinitionId);
            ProfileQuote output = pricer.Price(
                new ProfileQuantity(UsageKind.LlmOutputTokens, ProfilePriceTarget.Vision, looks * StepUsageProfiles.EventLookMaxOutputTokens, providerId),
                agentType, agentDefinitionId);
            return (input.OwnProvider ? 0 : Math.Max(0, input.CreditsMillis)) + (output.OwnProvider ? 0 : Math.Max(0, output.CreditsMillis));
        }
        catch (Exception)
        {
            // The plan never throws: a pricer that cannot answer leaves the step at its token hold.
            return 0;
        }
    }

    private static long[] WithSearchAddOns(long[] holds, long[] addOns, long[] sourceAddOns)
    {
        for (int i = 0; i < holds.Length; i++)
        {
            holds[i] += addOns[i] + sourceAddOns[i];
        }

        return holds;
    }

    /// <summary>A charged step the estimator could only price from B0's defaults; the fixed-price types (VideoGenerate, AudioGenerate) never are.</summary>
    public static bool IsCold(StepType stepType, StepCreditEstimate estimate)
        => estimate.Basis == CreditEstimateBasis.Default
            && !StepCreditDefaults.IsDeclaredPriced(stepType)
            && !StepCreditDefaults.IsNotCharged(stepType)
            // A fixed-price media step: re-planning it up to a share of the cold agent steps' range would hold far more than it can cost.
            && stepType != StepType.AudioCleanup
            && stepType != StepType.VideoEnhance;
}
