using System;
using System.Collections.Generic;
using System.Net.Http;
using AwesomeAssertions;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="ShotPlanCostEstimator"/> prices a WHOLE plan before the first clip is submitted, so the
/// step's existing spend gate can refuse an unaffordable plan as a whole rather than discovering it
/// half-way through — after some clips are already paid for and cannot be un-paid. Two properties
/// carry that: the estimate always delegates to <see cref="VideoGenerationPricing"/> (one pricing
/// path, or the gate and the ledger disagree about what was spent), and anything it cannot price
/// throws instead of contributing zero (a zero estimate passes every cap and credits the ledger with
/// spend that never happened).
/// </summary>
public class ShotPlanCostEstimatorTests
{
    /// <summary>
    /// The REAL capability table, via the production <c>Describe</c> path — no I/O, no paid call.
    /// The point of using it here is that 768P really prices at 0.08/s for this model; a hand-copied
    /// literal could silently disagree with the shipped table the ledger is charged against.
    /// </summary>
    private static readonly VideoGenerationCapabilities MiniMaxH3 =
        new MiniMaxVideoGenerationClient(new HttpClient(), "test-key-never-sent").Describe("MiniMax-H3");

    private static VideoGenerationCapabilities Caps(params (string Res, decimal Price)[] prices)
    {
        var table = new Dictionary<string, decimal>();
        foreach (var (res, price) in prices)
        {
            table[res] = price;
        }

        return new VideoGenerationCapabilities(table, 4, 15, new[] { "16:9" });
    }

    private static ShotPlan Shot(string duration) => new()
    {
        Purpose = "Cutaway",
        Anchor = "s1",
        Camera = "Static",
        Duration = duration,
        Prompt = "A chef plating pasta.",
        Reason = "Bridges the cut.",
    };

    // ---------------------------------------------------------------------
    // The arithmetic
    // ---------------------------------------------------------------------

    [Fact]
    public void EstimateWholePlanUsd_short_plus_long_at_768P_times_two_takes_is_the_sum_of_each_clip()
    {
        // Short snaps to 4s and Long to 15s on MiniMax-H3, so two takes of each clip cost
        // (0.08 * 4 + 0.08 * 15) * 2. This is the number the spend gate charges against every cap.
        var plan = new[] { Shot("Short"), Shot("Long") };

        decimal estimate = ShotPlanCostEstimator.EstimateWholePlanUsd(MiniMaxH3, "768P", plan, takes: 2);

        estimate.Should().Be((0.08m * 4 + 0.08m * 15) * 2,
            "the whole-plan estimate must equal the sum of VideoGenerationPricing's per-clip price for each shot, multiplied by takes — anything else and the gate reserves a different amount than the clips actually cost");
    }

    [Fact]
    public void EstimateWholePlanUsd_equals_per_clip_pricing_summed_and_multiplied()
    {
        // The same invariant expressed against VideoGenerationPricing directly, at a resolution with
        // a DIFFERENT price (2K), so a hard-coded 768P price could not accidentally satisfy it.
        var plan = new[] { Shot("Short"), Shot("Medium"), Shot("Long") };

        decimal expected = 0m;
        foreach (var shot in plan)
        {
            int seconds = ShotPlanParameterMapper.SnapDurationSeconds(MiniMaxH3, shot.Duration);
            expected += VideoGenerationPricing.EstimateClipCostUsd(MiniMaxH3, "2K", seconds);
        }

        expected *= 3;

        ShotPlanCostEstimator.EstimateWholePlanUsd(MiniMaxH3, "2K", plan, takes: 3).Should().Be(expected,
            "this class owns only the plan-level arithmetic (which word snaps where, and how many takes); the per-clip price and the resolution lookup must stay owned by VideoGenerationPricing with one implementation");
    }

    [Fact]
    public void EstimateWholePlanUsd_takes_multiplies_the_whole_plan()
    {
        var plan = new[] { Shot("Short"), Shot("Long") };

        decimal oneTake = ShotPlanCostEstimator.EstimateWholePlanUsd(MiniMaxH3, "768P", plan, takes: 1);
        decimal threeTakes = ShotPlanCostEstimator.EstimateWholePlanUsd(MiniMaxH3, "768P", plan, takes: 3);

        oneTake.Should().BeGreaterThan(0m, "a non-empty plan is never free");
        threeTakes.Should().Be(oneTake * 3,
            "each take is a separate paid clip, so ordering three takes of every shot must cost three times as much — an estimate that ignored takes would let a plan through the gate at a third of its real price");
    }

    [Fact]
    public void EstimateWholePlanUsd_empty_plan_is_zero()
    {
        // The one legitimate zero: there is genuinely nothing to buy. A plan the director chose to
        // leave empty must not be blocked by the spend gate.
        ShotPlanCostEstimator.EstimateWholePlanUsd(MiniMaxH3, "768P", Array.Empty<ShotPlan>(), takes: 1)
            .Should().Be(0m,
                "an empty plan orders no clips, so it costs nothing and must pass every cap");
    }

    [Fact]
    public void EstimateWholePlanUsd_empty_plan_is_still_zero_under_multiple_takes()
    {
        ShotPlanCostEstimator.EstimateWholePlanUsd(MiniMaxH3, "768P", Array.Empty<ShotPlan>(), takes: 5)
            .Should().Be(0m,
                "takes multiplies nothing when there are no shots; the result must stay the exact decimal zero, not a multiplied artefact");
    }

    // ---------------------------------------------------------------------
    // Fail-closed guards
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void EstimateWholePlanUsd_non_positive_takes_throws(int takes)
    {
        // `takes < 1` means the step would order nothing, so a "0m" answer would read as "this plan is
        // free" and sail through the gate. It is a config fault and belongs at the surface, not in
        // the ledger as a credit for spend that never happened.
        Action act = () => ShotPlanCostEstimator.EstimateWholePlanUsd(
            MiniMaxH3, "768P", new[] { Shot("Short") }, takes);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "takes = {0} would order no clips at all, so pricing the plan as 0m would falsely satisfy every budget cap",
            takes);
    }

    [Fact]
    public void EstimateWholePlanUsd_takes_is_rejected_before_any_pricing_happens()
    {
        // The takes guard fires first, so a config fault is reported as a config fault even when the
        // plan is ALSO unpriceable. Otherwise the operator would chase the resolution instead of the
        // off-by-one that actually broke the step.
        Action act = () => ShotPlanCostEstimator.EstimateWholePlanUsd(
            MiniMaxH3, "1080P-not-a-real-resolution", new[] { Shot("Short") }, takes: 0);

        act.Should().Throw<ArgumentOutOfRangeException>(
            "an invalid takes value must be reported before the resolver is even consulted, because it is the caller's configuration that is wrong");
    }

    [Fact]
    public void EstimateWholePlanUsd_resolution_with_no_price_throws_instead_of_estimating_zero()
    {
        // This is the proof that the estimator DELEGATES rather than carrying its own price table: a
        // second table would silently answer here, and then the gate and the ledger would disagree
        // about what the run is about to spend.
        Action act = () => ShotPlanCostEstimator.EstimateWholePlanUsd(
            MiniMaxH3, "1080P", new[] { Shot("Short") }, takes: 1);

        act.Should().Throw<ArgumentException>(
            "a resolution this model has no price for must throw rather than contribute 0m, because 0m passes every cap and, once persisted, credits the daily ledger");
    }

    [Fact]
    public void EstimateWholePlanUsd_unresolvable_duration_word_throws()
    {
        Action act = () => ShotPlanCostEstimator.EstimateWholePlanUsd(
            MiniMaxH3, "768P", new[] { Shot("Epic") }, takes: 1);

        act.Should().Throw<ArgumentException>(
            "a duration word that cannot be snapped has no price, and guessing one would reserve a spend that does not describe what was ordered");
    }

    [Fact]
    public void EstimateWholePlanUsd_null_shot_in_the_plan_throws()
    {
        // A null element cannot be priced, and skipping it would under-count the plan — the same
        // under-quoting failure as inventing a duration.
        Action act = () => ShotPlanCostEstimator.EstimateWholePlanUsd(
            MiniMaxH3, "768P", new ShotPlan[] { Shot("Short"), null!, Shot("Long") }, takes: 1);

        act.Should().Throw<ArgumentException>(
            "an unreadable shot must not be counted as free, or the estimate would under-quote the plan it is meant to gate");
    }

    [Fact]
    public void EstimateWholePlanUsd_null_capabilities_throws()
    {
        Action act = () => ShotPlanCostEstimator.EstimateWholePlanUsd(
            null!, "768P", new[] { Shot("Short") }, takes: 1);

        act.Should().Throw<ArgumentNullException>(
            "a null capability table means the model was never resolved, so no per-clip price exists to sum");
    }

    [Fact]
    public void EstimateWholePlanUsd_null_shots_throws()
    {
        Action act = () => ShotPlanCostEstimator.EstimateWholePlanUsd(
            MiniMaxH3, "768P", null!, takes: 1);

        act.Should().Throw<ArgumentNullException>(
            "a null plan is not an empty plan; treating it as free would let a missing plan pass the spend gate");
    }

    [Fact]
    public void EstimateWholePlanUsd_non_positive_clip_price_throws()
    {
        // The guard belongs to VideoGenerationPricing, and this test proves the estimator surfaces it
        // rather than swallowing it into a sum.
        var freeModel = Caps(("768P", 0m));

        Action act = () => ShotPlanCostEstimator.EstimateWholePlanUsd(
            freeModel, "768P", new[] { Shot("Short") }, takes: 1);

        act.Should().Throw<InvalidOperationException>(
            "a non-positive per-clip price would make the whole plan look free, so it must fail closed through the single shared pricing path");
    }
}
