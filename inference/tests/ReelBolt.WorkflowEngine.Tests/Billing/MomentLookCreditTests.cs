using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.VideoEvents;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The hold of a step that may look at single moments (<c>LookAtMoment</c>): its token hold plus the worst case of its
/// looks on the Vision model, the same way a step with web search holds its searches. Only in a run whose analysis
/// describes events, since no other run keeps a strip to look at.
/// </summary>
public class MomentLookCreditTests
{
    private const long InputMillis = 700;
    private const long OutputMillis = 300;

    private static readonly decimal LookInput = MomentLookLimits.MaxLooksPerStep * StepUsageProfiles.EventLookInputTokens;
    private static readonly decimal LookOutput = MomentLookLimits.MaxLooksPerStep * StepUsageProfiles.EventLookMaxOutputTokens;

    /// <summary>Quotes the looks' worst case on the vision target, and prices everything else at the seed constants.</summary>
    /// <remarks>
    /// <c>free</c> prices the looks at nothing: the baseline of a comparison. The same run priced by the seed pricer is
    /// not a baseline, because the seed pricer prices the looks too; and a run whose analysis describes nothing is not
    /// one either, because the analysis step's own cost moves the share every cold step holds.
    /// </remarks>
    private sealed class LookQuotePricer(bool ownProvider = false, long scale = 1, bool free = false) : IProfileQuantityPricer
    {
        public List<ProfileQuantity> LookAsks { get; } = [];

        public ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
        {
            if (quantity.Target == ProfilePriceTarget.Vision && (quantity.Quantity == LookInput || quantity.Quantity == LookOutput))
            {
                LookAsks.Add(quantity);
                long millis = free ? 0 : (quantity.Kind == UsageKind.LlmInputTokens ? InputMillis : OutputMillis) * (quantity.ProviderId is null ? 1 : scale);
                return new ProfileQuote(millis, ownProvider);
            }

            return SeedProfilePricer.Instance.Price(quantity, chatAgentType, chatAgentDefinitionId);
        }
    }

    private static WorkflowStep Analyze(int order, bool describe, Guid? visionProviderId = null, int? mostLooks = null)
    {
        VideoAnalyzeStepConfig config = new VideoAnalyzeStepConfig(Version: 1, Source: new VideoSourceRef(VideoSourceKind.ProjectFile))
            with
        { AnalyzeEvents = true, DescribeEvents = describe, VisionProviderId = visionProviderId, MaxDescribedEvents = mostLooks };
        return new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = order,
            StepType = StepType.VideoAnalyze,
            AgentDefinitionId = Guid.NewGuid(),
            VideoAnalyzeConfigJson = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } })
        };
    }

    /// <summary>The hold of the LAST step of a run made of <paramref name="analyses"/> and then one step of <paramref name="agent"/>.</summary>
    private static long HoldOf(AgentType agent, IProfileQuantityPricer pricer, StepType stepType = StepType.Agent, params WorkflowStep[] analyses)
    {
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = analyses.Length + 1, StepType = stepType, AgentDefinitionId = Guid.NewGuid() };
        Dictionary<Guid, AgentType> types = new() { [step.AgentDefinitionId] = agent };
        foreach (WorkflowStep analysis in analyses)
            types[analysis.AgentDefinitionId] = AgentType.VideoTransform;
        return RunCreditHolds.Plan([.. analyses, step], new StepCreditEstimator([]), null, types, pricer).HoldsMillis[^1];
    }

    [Theory]
    [InlineData(AgentType.SoundDesigner)]
    [InlineData(AgentType.VideoStoryEditor)]
    [InlineData(AgentType.MotionDirector)]
    public void An_agent_that_may_look_holds_its_token_hold_plus_the_worst_case_of_its_looks(AgentType agent)
    {
        WorkflowStep analysis = Analyze(1, describe: true);
        long without = HoldOf(agent, new LookQuotePricer(free: true), StepType.Agent, analysis);
        LookQuotePricer pricer = new();

        HoldOf(agent, pricer, StepType.Agent, analysis).Should().Be(without + InputMillis + OutputMillis);
        pricer.LookAsks.Select(q => (q.Kind, q.Quantity)).Should().BeEquivalentTo(new[]
        {
            (UsageKind.LlmInputTokens, LookInput),
            (UsageKind.LlmOutputTokens, LookOutput)
        });
    }

    [Theory]
    [InlineData(AgentType.SoundDesigner)]
    [InlineData(AgentType.VideoStoryEditor)]
    [InlineData(AgentType.MotionDirector)]
    public void In_a_run_whose_analysis_describes_nothing_there_is_no_strip_to_look_at_and_nothing_is_added_or_asked(AgentType agent)
    {
        LookQuotePricer pricer = new();

        HoldOf(agent, pricer).Should().Be(HoldOf(agent, SeedProfilePricer.Instance));
        HoldOf(agent, pricer, StepType.Agent, Analyze(1, describe: false))
            .Should().Be(HoldOf(agent, SeedProfilePricer.Instance, StepType.Agent, Analyze(1, describe: false)));
        pricer.LookAsks.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AgentType.MusicSupervisor)]
    [InlineData(AgentType.MotionGraphicsPlanner)]
    [InlineData(AgentType.Custom)]
    [InlineData(AgentType.ScriptwriterAgent)]
    public void An_agent_without_the_tool_is_unchanged_and_never_asks_for_the_price(AgentType agent)
    {
        LookQuotePricer pricer = new();
        WorkflowStep analysis = Analyze(1, describe: true);

        HoldOf(agent, pricer, StepType.Agent, analysis).Should().Be(HoldOf(agent, SeedProfilePricer.Instance, StepType.Agent, analysis));
        pricer.LookAsks.Should().BeEmpty();
    }

    [Fact]
    public void A_vision_model_the_organization_brought_adds_nothing()
    {
        WorkflowStep analysis = Analyze(1, describe: true);

        HoldOf(AgentType.SoundDesigner, new LookQuotePricer(ownProvider: true), StepType.Agent, analysis)
            .Should().Be(HoldOf(AgentType.SoundDesigner, new LookQuotePricer(free: true), StepType.Agent, analysis));
    }

    [Fact]
    public void The_looks_are_priced_on_the_vision_model_the_analysis_names_and_on_the_dearest_when_two_analyses_differ()
    {
        Guid named = Guid.NewGuid();
        WorkflowStep[] analyses = [Analyze(1, describe: true), Analyze(2, describe: true, named)];
        long without = HoldOf(AgentType.SoundDesigner, new LookQuotePricer(free: true), StepType.Agent, analyses);
        LookQuotePricer pricer = new(scale: 3);

        HoldOf(AgentType.SoundDesigner, pricer, StepType.Agent, analyses)
            .Should().Be(without + 3 * (InputMillis + OutputMillis));
        pricer.LookAsks.Select(q => q.ProviderId).Distinct().Should().BeEquivalentTo(new Guid?[] { null, named });
    }

    [Fact]
    public void An_edit_room_holds_the_looks_of_its_solo_fallback_and_no_other_step_type_does()
    {
        WorkflowStep analysis = Analyze(1, describe: true);
        LookQuotePricer pricer = new();

        HoldOf(AgentType.VideoTransform, pricer, StepType.EditRoom, analysis)
            .Should().Be(HoldOf(AgentType.VideoTransform, new LookQuotePricer(free: true), StepType.EditRoom, analysis) + InputMillis + OutputMillis);

        LookQuotePricer other = new();
        HoldOf(AgentType.SoundDesigner, other, StepType.VideoCompile, analysis)
            .Should().Be(HoldOf(AgentType.SoundDesigner, SeedProfilePricer.Instance, StepType.VideoCompile, analysis));
        other.LookAsks.Should().BeEmpty();
    }

    [Fact]
    public void The_worst_case_of_a_look_is_the_describers_own_output_ceiling_not_the_typical_answer()
    {
        // The describer asks the model with this ceiling, so a reasoning Vision model can bill all of it per look.
        StepUsageProfiles.EventLookMaxOutputTokens.Should().Be(600);
        StepUsageProfiles.EventLookMaxOutputTokens.Should().BeGreaterThan(StepUsageProfiles.EventLookOutputTokens, "300 stays the typical figure of the shown estimate");
        typeof(ReelBolt.WorkflowEngine.Services.Video.Events.VisionEventDescriber)
            .GetField("MaxOutputTokens", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetRawConstantValue().Should().Be((int)StepUsageProfiles.EventLookMaxOutputTokens);
    }

    /// <summary>The hold of a Parallel step (after one describing analysis) whose own agent and extra agents are as given.</summary>
    private static long ParallelHold(IProfileQuantityPricer pricer, AgentType own, params AgentType[] extras)
    {
        WorkflowStep analysis = Analyze(1, describe: true);
        Guid[] extraIds = [.. extras.Select(_ => Guid.NewGuid())];
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            StepOrder = 2,
            StepType = StepType.Parallel,
            AgentDefinitionId = Guid.NewGuid(),
            ParallelAgentIdsJson = extraIds.Length > 0 ? JsonSerializer.Serialize(extraIds) : null
        };
        Dictionary<Guid, AgentType> types = new() { [analysis.AgentDefinitionId] = AgentType.VideoTransform, [step.AgentDefinitionId] = own };
        for (int i = 0; i < extras.Length; i++)
            types[extraIds[i]] = extras[i];
        return RunCreditHolds.Plan([analysis, step], new StepCreditEstimator([]), null, types, pricer).HoldsMillis[^1];
    }

    [Fact]
    public void A_parallel_step_holds_the_looks_once_when_its_own_agent_or_any_extra_agent_may_look()
    {
        long looks = InputMillis + OutputMillis;

        // Its own agent may look.
        ParallelHold(new LookQuotePricer(), AgentType.SoundDesigner)
            .Should().Be(ParallelHold(new LookQuotePricer(free: true), AgentType.SoundDesigner) + looks);

        // Only an extra agent may look.
        ParallelHold(new LookQuotePricer(), AgentType.MusicSupervisor, AgentType.ScriptwriterAgent, AgentType.MotionDirector)
            .Should().Be(ParallelHold(new LookQuotePricer(free: true), AgentType.MusicSupervisor, AgentType.ScriptwriterAgent, AgentType.MotionDirector) + looks);

        // Three agents that may look share the step's one counter: one add-on, asked once.
        LookQuotePricer three = new();
        ParallelHold(three, AgentType.SoundDesigner, AgentType.SoundDesigner, AgentType.VideoStoryEditor)
            .Should().Be(ParallelHold(new LookQuotePricer(free: true), AgentType.SoundDesigner, AgentType.SoundDesigner, AgentType.VideoStoryEditor) + looks);
        three.LookAsks.Should().HaveCount(2, "one input and one output ask");

        // No agent of the step may look: nothing added, nothing asked.
        LookQuotePricer none = new();
        ParallelHold(none, AgentType.MusicSupervisor, AgentType.ScriptwriterAgent)
            .Should().Be(ParallelHold(SeedProfilePricer.Instance, AgentType.MusicSupervisor, AgentType.ScriptwriterAgent));
        none.LookAsks.Should().BeEmpty();
    }

    [Fact]
    public void An_analysis_with_zero_looks_prices_none_keeps_no_strip_and_so_adds_no_look_add_on()
    {
        WorkflowStep zero = Analyze(1, describe: true, mostLooks: 0);
        WorkflowStep three = Analyze(1, describe: true, mostLooks: 3);

        StepUsageProfiles.EventLookProvider(zero.VideoAnalyzeConfigJson).Describes.Should().BeFalse();
        StepUsageProfiles.For(zero, null).Should().BeEquivalentTo(StepUsageProfiles.For(Analyze(1, describe: false), null), "0 looks cost nothing: it is the analysis it would be with describing off");
        StepUsageProfiles.EventLookProfile(zero).Should().BeNull();
        StepUsageProfiles.EventLookProfile(three)!.Quantities.Where(q => q.Target == ProfilePriceTarget.Vision).Select(q => q.Quantity)
            .Should().Equal(new decimal[] { 3 * StepUsageProfiles.EventLookInputTokens, 3 * StepUsageProfiles.EventLookOutputTokens }, "the typical 300 out stays the shown figure of the analysis");
        StepUsageProfiles.For(three, null).Quantities.Should().NotContain(q => q.Target == ProfilePriceTarget.Vision, "the looks have their own profile, beside the analysis");

        LookQuotePricer pricer = new();
        HoldOf(AgentType.SoundDesigner, pricer, StepType.Agent, zero)
            .Should().Be(HoldOf(AgentType.SoundDesigner, SeedProfilePricer.Instance, StepType.Agent, zero));
        pricer.LookAsks.Should().BeEmpty("no strip is kept, so there is nothing to look at");
    }

    // ── the analysis step's own looks at events ───────────────────────────────

    private const long InRate = 10;
    private const long OutRate = 40;
    private const long ShownPerLook = StepUsageProfiles.EventLookInputTokens * InRate + StepUsageProfiles.EventLookOutputTokens * OutRate;
    private const long HeldPerLook = StepUsageProfiles.EventLookInputTokens * InRate + StepUsageProfiles.EventLookMaxOutputTokens * OutRate;

    /// <summary>Prices every Vision token (dear enough to move a cold share if it were in one); the rest at the seed constants.</summary>
    private sealed class VisionTokenPricer(Guid? dearProvider = null, bool ownProvider = false, bool free = false) : IProfileQuantityPricer
    {
        public List<ProfileQuantity> Asked { get; } = [];

        public ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
        {
            if (quantity.Target != ProfilePriceTarget.Vision)
                return SeedProfilePricer.Instance.Price(quantity, chatAgentType, chatAgentDefinitionId);
            Asked.Add(quantity);
            long rate = (quantity.Kind == UsageKind.LlmInputTokens ? InRate : OutRate) * (dearProvider is { } dear && quantity.ProviderId == dear ? 5 : 1);
            return new ProfileQuote(free ? 0 : (long)quantity.Quantity * rate, ownProvider);
        }
    }

    /// <summary>The hold of one analysis step on its own; with <paramref name="history"/>, four past analyses of any kind.</summary>
    private static long AnalysisHold(WorkflowStep analysis, IProfileQuantityPricer pricer, bool history = false)
    {
        Dictionary<Guid, AgentType> types = new() { [analysis.AgentDefinitionId] = AgentType.VideoTransform };
        StepCreditEstimator estimator = new(
            history ? [.. Enumerable.Range(1, 4).Select(i => new StepCreditSample(StepType.VideoAnalyze, analysis.AgentDefinitionId, i * 7_000L))] : [],
            StepCreditEstimator.DefaultMinSamples);
        return RunCreditHolds.Plan([analysis], estimator, null, types, pricer).HoldsMillis[0];
    }

    [Theory]
    [InlineData(null, 30, "left to the length of the video: the most an automatic count gives")]
    [InlineData(3, 3, "the author's count")]
    [InlineData(40, 40, "the hard cap, in full")]
    [InlineData(100, 40, "never more than the step can take")]
    public void A_describing_analysis_holds_the_most_looks_it_can_take_once_cold_or_with_history(int? mostLooks, int held, string why)
    {
        WorkflowStep analysis = Analyze(1, describe: true, mostLooks: mostLooks);

        foreach (bool history in new[] { false, true })
        {
            long without = AnalysisHold(analysis, new VisionTokenPricer(free: true), history);
            VisionTokenPricer pricer = new();

            // Exactly the add-on above the same plan with free looks: the looks are in no other part of the hold, so a
            // cold step does not hold them twice and a step with history holds them at all.
            AnalysisHold(analysis, pricer, history).Should().Be(without + held * HeldPerLook, "{0} (history: {1})", why, history);
            pricer.Asked.Should().Contain(q => q.Kind == UsageKind.LlmOutputTokens && q.Quantity == held * StepUsageProfiles.EventLookMaxOutputTokens,
                "each look is held at the describer's own output ceiling");
        }
    }

    [Fact]
    public void An_analysis_that_takes_no_looks_holds_none_and_asks_for_no_price()
    {
        foreach (WorkflowStep none in new[] { Analyze(1, describe: false), Analyze(1, describe: true, mostLooks: 0) })
        {
            foreach (bool history in new[] { false, true })
            {
                VisionTokenPricer pricer = new();
                AnalysisHold(none, pricer, history).Should().Be(AnalysisHold(none, SeedProfilePricer.Instance, history));
                pricer.Asked.Should().BeEmpty();
            }
        }
    }

    [Fact]
    public void The_looks_are_held_on_the_vision_model_the_step_names_and_an_organizations_own_model_holds_none()
    {
        Guid chosen = Guid.NewGuid();
        WorkflowStep named = Analyze(1, describe: true, chosen, mostLooks: 10);
        long without = AnalysisHold(named, new VisionTokenPricer(free: true), history: true);

        // The model the run's app chose is bound into the step as its visionProviderId: the hold follows it, history or not.
        VisionTokenPricer pricer = new(dearProvider: chosen);
        AnalysisHold(named, pricer, history: true).Should().Be(without + 5 * 10 * HeldPerLook);
        pricer.Asked.Should().OnlyContain(q => q.ProviderId == chosen);

        AnalysisHold(named, new VisionTokenPricer(ownProvider: true), history: true).Should().Be(without);
    }

    [Theory]
    [InlineData(null, 20, 12, 30)]
    [InlineData(3, 3, 3, 3)]
    [InlineData(40, 40, 40, 40)]
    [InlineData(100, 40, 40, 40)]
    public void The_shown_estimate_never_shows_more_looks_than_the_step_can_take_and_an_explicit_count_shows_exactly_itself(
        int? mostLooks, int typical, int low, int high)
    {
        WorkflowStep analysis = Analyze(1, describe: true, mostLooks: mostLooks);
        Dictionary<Guid, AgentType> types = new() { [analysis.AgentDefinitionId] = AgentType.VideoTransform };
        VisionTokenPricer pricer = new();

        StepCostDistribution shown = ColdStartStepPricing.PriceProfile(analysis, AgentType.VideoTransform, types, pricer).Cost;
        StepCostDistribution analysisOnly = ColdStartStepPricing.PriceProfile(analysis, AgentType.VideoTransform, types, pricer, withoutEventLooks: true).Cost;

        (shown.TypicalMillis - analysisOnly.TypicalMillis).Should().Be(typical * ShownPerLook);
        (shown.P25Millis - analysisOnly.P25Millis).Should().Be(low * ShownPerLook);
        (shown.P75Millis - analysisOnly.P75Millis).Should().Be(high * ShownPerLook);
        high.Should().BeLessThanOrEqualTo(VisionLookDefaults.MaxEventLooks);

        // The analysis itself keeps its own range: the looks' factors are theirs alone.
        analysisOnly.Should().Be(ColdStartStepPricing.PriceProfile(Analyze(1, describe: true, mostLooks: 0), AgentType.VideoTransform, types, SeedProfilePricer.Instance).Cost);
    }

    [Fact]
    public void The_dry_run_names_the_look_quantities_so_the_price_table_prices_them()
    {
        Guid named = Guid.NewGuid();
        WorkflowStep analysis = Analyze(1, describe: true, named);
        WorkflowStep step = new() { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.Agent, AgentDefinitionId = Guid.NewGuid() };
        Dictionary<Guid, AgentType> types = new() { [step.AgentDefinitionId] = AgentType.SoundDesigner };
        RecordingProfilePricer recorder = new();

        RunCreditHolds.Plan([analysis, step], new StepCreditEstimator([]), null, types, recorder);

        recorder.Requests.Should().Contain(k => k.Target == ProfilePriceTarget.Vision && k.Kind == UsageKind.LlmInputTokens && k.Quantity == LookInput && k.ProviderId == named);
        recorder.Requests.Should().Contain(k => k.Target == ProfilePriceTarget.Vision && k.Kind == UsageKind.LlmOutputTokens && k.Quantity == LookOutput && k.ProviderId == named);

        // The analysis step's own looks too: the worst case its hold takes, and the typical figures its estimate shows.
        decimal worst = VisionLookDefaults.MaxAutoEventLooks, typical = VisionLookDefaults.TypicalEventLooks;
        recorder.Requests.Should().Contain(k => k.Target == ProfilePriceTarget.Vision && k.Quantity == worst * StepUsageProfiles.EventLookInputTokens && k.ProviderId == named);
        recorder.Requests.Should().Contain(k => k.Target == ProfilePriceTarget.Vision && k.Quantity == worst * StepUsageProfiles.EventLookMaxOutputTokens && k.ProviderId == named);
        RecordingProfilePricer shown = new();
        ColdStartStepPricing.PriceProfile(analysis, AgentType.VideoTransform, types, shown);
        shown.Requests.Should().Contain(k => k.Target == ProfilePriceTarget.Vision && k.Quantity == typical * StepUsageProfiles.EventLookOutputTokens && k.ProviderId == named);
    }
}
