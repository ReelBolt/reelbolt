#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// the cold-start hold re-plan: on a cold start the LOW edge of the run's usual range is split across the cold
/// steps in proportion to their single-execution typical (the pure allocation floors a share at 1
/// millicredit). The run plan then holds, per cold step, the larger of that share and the
/// step's previous default hold, so the cold-start hold re-plan only ever raises a hold, never lowers one.
/// </summary>
public sealed class ColdStartHoldPlanTests
{
    // -----------------------------------------------------------------
    // The allocation (pure)
    // -----------------------------------------------------------------

    [Fact]
    public void The_shares_sum_exactly_to_the_cold_low_edge()
    {
        RunStepInput[] cold = [Cold(1, 10_001), Cold(2, 20_003), Cold(3, 30_007)];
        long low = RunCreditRange.Combine(cold, []).LowMillis;

        IReadOnlyDictionary<int, long> plan = ColdStartHoldPlan.Allocate(cold, []);

        low.Should().BePositive();
        plan.Keys.Should().BeEquivalentTo([1, 2, 3]);
        plan.Values.Sum().Should().Be(low);
    }

    [Fact]
    public void Each_share_is_proportional_to_its_single_execution_typical()
    {
        RunStepInput[] cold = [Cold(1, 10_000), Cold(2, 20_000), Cold(3, 70_000)];
        long low = RunCreditRange.Combine(cold, []).LowMillis;

        IReadOnlyDictionary<int, long> plan = ColdStartHoldPlan.Allocate(cold, []);

        foreach (RunStepInput step in cold)
        {
            double exact = low * (double)step.Cost.TypicalMillis / 100_000d;
            ((double)plan[step.StepOrder]).Should().BeApproximately(exact, 1d);
        }

        plan[3].Should().BeGreaterThan(plan[2]).And.BeGreaterThan(plan[1]);
    }

    [Fact]
    public void A_zero_typical_step_still_holds_one_millicredit()
    {
        // An own-provider chat step is priced at 0: its share is 0, but it must still reserve,
        // because a step with no reservation is never settled and any compute it does would be free.
        RunStepInput[] cold = [Cold(1, 0), Cold(2, 40_000)];
        long low = RunCreditRange.Combine(cold, []).LowMillis;

        IReadOnlyDictionary<int, long> plan = ColdStartHoldPlan.Allocate(cold, []);

        plan[1].Should().Be(ColdStartHoldPlan.MinimumHoldMillis).And.Be(1);
        plan[2].Should().Be(low);
    }

    [Fact]
    public void A_run_whose_cold_steps_all_cost_nothing_holds_one_millicredit_each_and_never_divides_by_zero()
    {
        RunStepInput[] cold = [Cold(1, 0), Cold(2, 0), Cold(3, 0)];

        IReadOnlyDictionary<int, long> plan = ColdStartHoldPlan.Allocate(cold, [RunCreditRange.ColdStartLoop(3, 1, 3)]);

        plan.Values.Should().Equal(1, 1, 1);
    }

    [Fact]
    public void No_cold_steps_gives_an_empty_plan()
    {
        ColdStartHoldPlan.Allocate([], []).Should().BeEmpty();
        ColdStartHoldPlan.Allocate([], [RunCreditRange.ColdStartLoop(2, 1, 3)]).Should().BeEmpty();
    }

    [Fact]
    public void A_loop_body_counts_once_in_the_weights_but_one_to_two_passes_in_the_low_edge()
    {
        // Step 1 sits outside the loop, steps 2-3 are its body (target 2, loop step 3).
        RunStepInput[] cold = [Cold(1, 30_000), Cold(2, 30_000), Cold(3, 10_000)];
        LoopInput loop = RunCreditRange.ColdStartLoop(3, 2, 3);
        long withLoop = RunCreditRange.Combine(cold, [loop]).LowMillis;
        long withoutLoop = RunCreditRange.Combine(cold, []).LowMillis;

        IReadOnlyDictionary<int, long> plan = ColdStartHoldPlan.Allocate(cold, [loop]);

        withLoop.Should().BeGreaterThan(withoutLoop, "the body runs 1.5 times on average, so L grows");
        plan.Values.Sum().Should().Be(withLoop);
        // Single-execution weights: the outside step and the first body step weigh the same.
        Math.Abs(plan[1] - plan[2]).Should().BeLessThanOrEqualTo(1);
    }

    [Theory]
    [InlineData(new long[] { 1_000 })]
    [InlineData(new long[] { 9_766, 20_000, 4_883 })]
    [InlineData(new long[] { 1, 2, 3, 5, 8, 13, 21 })]
    [InlineData(new long[] { 250_000, 1, 0, 77_777 })]
    public void No_share_is_above_its_own_typical_and_the_plan_is_never_above_the_run_typical(long[] typicals)
    {
        RunStepInput[] cold = [.. typicals.Select((t, i) => Cold(i + 1, t))];
        RunRange range = RunCreditRange.Combine(cold, []);

        IReadOnlyDictionary<int, long> plan = ColdStartHoldPlan.Allocate(cold, []);

        foreach (RunStepInput step in cold)
        {
            plan[step.StepOrder].Should().BeLessThanOrEqualTo(Math.Max(1, step.Cost.TypicalMillis));
        }

        // Above the run typical only by the 1-millicredit floors, at most one per step.
        plan.Values.Sum().Should().BeLessThanOrEqualTo(range.TypicalMillis + cold.Length);
        range.LowMillis.Should().BeLessThanOrEqualTo(range.TypicalMillis);
    }

    // -----------------------------------------------------------------
    // The shared entry point: history, cold, not charged, VideoGenerate
    // -----------------------------------------------------------------

    [Fact]
    public void Mixed_history_keeps_the_p90_and_the_low_edge_comes_from_the_cold_steps_only()
    {
        Guid measured = Guid.NewGuid();
        WorkflowStep[] steps =
        [
            AgentStep(1, measured, AgentType.NarrationWriter),
            AgentStep(2, Guid.NewGuid(), AgentType.DirectorAgent),
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, AgentDefinitionId = Guid.NewGuid() }
        ];
        var history = new StepCreditEstimator(
            [.. new long[] { 1_000, 2_000, 3_000, 4_000 }.Select(c => new StepCreditSample(StepType.Agent, measured, c))]);
        // Step 2 is an Agent step of an UNSEEN agent: the step-type bucket has 4 samples, so it is
        // history too. Only the compile is cold.
        RunHoldPlan plan = RunCreditHolds.Plan(steps, history, null, AgentTypesOf(steps), SeedProfilePricer.Instance);

        plan.HoldsMillis[0].Should().Be(history.Estimate(StepType.Agent, measured).HoldMillis);
        plan.HoldsMillis[1].Should().Be(history.Estimate(StepType.Agent, steps[1].AgentDefinitionId).HoldMillis);
        long compileLow = RunCreditRange.Combine(
            [new RunStepInput(3, ColdStartStepPricing.PriceProfile(steps[2], null, AgentTypesOf(steps), SeedProfilePricer.Instance).Cost, 0)],
            []).LowMillis;
        plan.HoldsMillis[2].Should().Be(Math.Max(DefaultHold(steps[2]), compileLow));
        plan.ColdLowMillis.Should().Be(compileLow);
        plan.RequiredMillis.Should().Be(plan.HoldsMillis.Sum());
    }

    [Fact]
    public void Two_cold_steps_and_one_history_step_split_the_low_edge_of_the_cold_two()
    {
        Guid measured = Guid.NewGuid();
        WorkflowStep[] steps =
        [
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.VideoAnalyze, AgentDefinitionId = measured },
            AgentStep(2, Guid.NewGuid(), AgentType.DirectorAgent),
            AgentStep(3, Guid.NewGuid(), AgentType.NarrationWriter)
        ];
        var history = new StepCreditEstimator(
            [.. new long[] { 50_000, 60_000, 70_000 }.Select(c => new StepCreditSample(StepType.VideoAnalyze, measured, c))]);
        IReadOnlyDictionary<Guid, AgentType> types = AgentTypesOf(steps);

        RunHoldPlan plan = RunCreditHolds.Plan(steps, history, null, types, SeedProfilePricer.Instance);

        RunStepInput[] cold = [.. steps.Skip(1).Select(s =>
            new RunStepInput(s.StepOrder, ColdStartStepPricing.PriceProfile(s, types[s.AgentDefinitionId], types, SeedProfilePricer.Instance).Cost, 0))];
        long low = RunCreditRange.Combine(cold, []).LowMillis;

        IReadOnlyDictionary<int, long> shares = ColdStartHoldPlan.Allocate(cold, low);

        plan.HoldsMillis[0].Should().Be(68_000); // percentile_cont(0.9) of 50k, 60k, 70k
        plan.HoldsMillis[1].Should().Be(Math.Max(DefaultHold(steps[1]), shares[2]));
        plan.HoldsMillis[2].Should().Be(Math.Max(DefaultHold(steps[2]), shares[3]));
        (plan.HoldsMillis[1] + plan.HoldsMillis[2]).Should().BeGreaterThanOrEqualTo(low);
        plan.ColdLowMillis.Should().Be(low);
    }

    [Fact]
    public void Not_charged_steps_hold_nothing_and_video_generate_keeps_its_declared_price_outside_the_plan()
    {
        WorkflowStep[] steps =
        [
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.Conditional, AgentDefinitionId = Guid.NewGuid() },
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoGenerate, AgentDefinitionId = Guid.NewGuid() },
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoGenerate, AgentDefinitionId = Guid.NewGuid() },
            AgentStep(4, Guid.NewGuid(), AgentType.DirectorAgent)
        ];
        var cold = new StepCreditEstimator([]);

        RunHoldPlan plan = RunCreditHolds.Plan(steps, cold, new Dictionary<int, long> { [2] = 160_000 }, AgentTypesOf(steps), SeedProfilePricer.Instance);

        plan.HoldsMillis[0].Should().Be(0);
        plan.HoldsMillis[1].Should().Be(160_000);
        plan.HoldsMillis[2].Should().Be(StepCreditDefaults.MillisFor(StepType.VideoGenerate).Low, "an undeclared video step keeps today's fallback");
        plan.HoldsMillis[3].Should().Be(Math.Max(DefaultHold(steps[3]), plan.ColdLowMillis), "the agent step is the only cold step");
        plan.UsedFallback.Should().BeFalse();
    }

    [Fact]
    public void A_pricer_that_throws_falls_back_to_the_B0_low_per_cold_step_and_never_throws()
    {
        WorkflowStep[] steps = [AgentStep(1, Guid.NewGuid(), AgentType.DirectorAgent),
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = Guid.NewGuid() }];

        RunHoldPlan plan = RunCreditHolds.Plan(steps, new StepCreditEstimator([]), null, AgentTypesOf(steps), new ThrowingPricer());

        plan.UsedFallback.Should().BeTrue();
        plan.HoldsMillis.Should().Equal(
            StepCreditDefaults.MillisFor(StepType.Agent).Low, StepCreditDefaults.MillisFor(StepType.VideoCompile).Low);
    }

    // -----------------------------------------------------------------
    // a cold step never holds less than its previous default
    // -----------------------------------------------------------------

    [Fact]
    public void A_zero_typical_cold_step_holds_its_previous_default_and_never_one_millicredit()
    {
        // Every profile priced at 0 (own provider): every share is 0. Before the cold-start hold re-plan each step held B0's
        // default low; the plan must keep that, not drop to the 1-millicredit allocation floor.
        WorkflowStep[] steps = [AgentStep(1, Guid.NewGuid(), AgentType.DirectorAgent),
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = Guid.NewGuid() }];

        RunHoldPlan plan = RunCreditHolds.Plan(steps, new StepCreditEstimator([]), null, AgentTypesOf(steps), new FixedPricer(0, OwnProvider: true));

        plan.UsedFallback.Should().BeFalse();
        plan.HoldsMillis.Should().Equal(DefaultHold(steps[0]), DefaultHold(steps[1]));
        plan.HoldsMillis.Should().OnlyContain(h => h > ColdStartHoldPlan.MinimumHoldMillis);
    }

    [Fact]
    public void A_cold_step_whose_share_exceeds_its_previous_default_holds_the_share()
    {
        // Every quantity priced high enough that the low edge, and so each share, is above B0's row.
        WorkflowStep[] steps = [AgentStep(1, Guid.NewGuid(), AgentType.DirectorAgent),
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoCompile, AgentDefinitionId = Guid.NewGuid() }];
        IReadOnlyDictionary<Guid, AgentType> types = AgentTypesOf(steps);
        var pricer = new FixedPricer(50_000_000, OwnProvider: false);

        RunHoldPlan plan = RunCreditHolds.Plan(steps, new StepCreditEstimator([]), null, types, pricer);

        RunStepInput[] cold = [.. steps.Select(s => new RunStepInput(
            s.StepOrder, ColdStartStepPricing.PriceProfile(s, ColdStartStepPricing.AgentTypeOf(s.AgentDefinitionId, s, types), types, pricer).Cost, 0))];
        IReadOnlyDictionary<int, long> shares = ColdStartHoldPlan.Allocate(cold, []);
        shares[1].Should().BeGreaterThan(DefaultHold(steps[0]), "the precondition: the raise applies");
        shares[2].Should().BeGreaterThan(DefaultHold(steps[1]), "the precondition: the raise applies");
        plan.HoldsMillis.Should().Equal(shares[1], shares[2]);
        plan.RequiredMillis.Should().Be(plan.ColdLowMillis);
    }

    [Fact]
    public void Each_cold_step_holds_the_larger_of_its_previous_default_and_its_share()
    {
        WorkflowStep[] steps =
        [
            AgentStep(1, Guid.NewGuid(), AgentType.NarrationWriter),
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.VideoAnalyze, AgentDefinitionId = Guid.NewGuid() },
            new WorkflowStep { Id = Guid.NewGuid(), StepOrder = 3, StepType = StepType.VideoCompile, AgentDefinitionId = Guid.NewGuid() }
        ];
        IReadOnlyDictionary<Guid, AgentType> types = AgentTypesOf(steps);

        RunHoldPlan plan = RunCreditHolds.Plan(steps, new StepCreditEstimator([]), null, types, SeedProfilePricer.Instance);

        RunStepInput[] cold = [.. steps.Select(s => new RunStepInput(
            s.StepOrder, ColdStartStepPricing.PriceProfile(s, ColdStartStepPricing.AgentTypeOf(s.AgentDefinitionId, s, types), types, SeedProfilePricer.Instance).Cost, 0))];
        IReadOnlyDictionary<int, long> shares = ColdStartHoldPlan.Allocate(cold, []);
        for (int i = 0; i < steps.Length; i++)
        {
            plan.HoldsMillis[i].Should().Be(Math.Max(DefaultHold(steps[i]), shares[steps[i].StepOrder]));
            plan.HoldsMillis[i].Should().BeGreaterThanOrEqualTo(DefaultHold(steps[i]));
        }

        plan.RequiredMillis.Should().BeGreaterThanOrEqualTo(Math.Max(plan.ColdLowMillis, steps.Sum(DefaultHold)));
    }

    [Fact]
    public void An_empty_run_holds_nothing()
    {
        RunHoldPlan plan = RunCreditHolds.Plan([], new StepCreditEstimator([]), null, new Dictionary<Guid, AgentType>(), SeedProfilePricer.Instance);

        plan.HoldsMillis.Should().BeEmpty();
        plan.RequiredMillis.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// <summary>A cold step with a skewed band (p25 0.7m, p75 1.6m), as the profiles have.</summary>
    private static RunStepInput Cold(int order, long typical) => new(
        order,
        new StepCostDistribution(typical, (long)Math.Round(typical * 0.7), (long)Math.Round(typical * 1.6)),
        0);

    private static WorkflowStep AgentStep(int order, Guid agent, AgentType type) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.Agent,
        AgentDefinitionId = agent,
        AgentDefinition = new AgentDefinition { Id = agent, Name = type.ToString(), AgentType = type }
    };

    private static IReadOnlyDictionary<Guid, AgentType> AgentTypesOf(IEnumerable<WorkflowStep> steps)
        => steps.Where(s => s.AgentDefinition is not null)
            .GroupBy(s => s.AgentDefinitionId)
            .ToDictionary(g => g.Key, g => g.First().AgentDefinition.AgentType);

    /// <summary>The hold the step had before the cold-start hold re-plan: B0's default low on a cold start.</summary>
    private static long DefaultHold(WorkflowStep step)
        => new StepCreditEstimator([]).Estimate(step.StepType, step.AgentDefinitionId).HoldMillis;

    private sealed class FixedPricer(long millis, bool OwnProvider) : IProfileQuantityPricer
    {
        public ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
            => quantity.Target == ProfilePriceTarget.WebSearch ? new(0, OwnProvider) : new(millis, OwnProvider); // the search term has its own tests
    }

    private sealed class ThrowingPricer : IProfileQuantityPricer
    {
        public ProfileQuote Price(ProfileQuantity quantity, AgentType chatAgentType, Guid? chatAgentDefinitionId)
            => throw new InvalidOperationException("pricing down");
    }
}
