using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// The run-level combination behind the displayed "usually A–B" range: per-step medians and IQRs,
/// review loops scaled by their expected passes, and fixed-price steps taken out as "up to".
/// </summary>
public sealed class RunCreditRangeTests
{
    private static RunStepInput Step(int order, long typical, long p25, long p75, long fixedMax = 0)
        => new(order, new StepCostDistribution(typical, p25, p75), fixedMax);

    [Fact]
    public void A_single_step_spans_its_own_p25_to_p75()
    {
        RunRange range = RunCreditRange.Combine([Step(1, 10_000, 8_000, 12_000)], []);

        range.TypicalMillis.Should().Be(10_000);
        // sigma = IQR / 1.349 and the shown band is +-0.674 sigma: exactly the step's own p25..p75.
        range.LowMillis.Should().BeCloseTo(8_000, 2);
        range.HighMillis.Should().BeCloseTo(12_000, 2);
        range.FixedMaxMillis.Should().Be(0);
        range.ExpectedRunsByStepOrder[1].Should().Be(1);
    }

    [Fact]
    public void A_single_skewed_step_spans_exactly_its_own_p25_to_p75()
    {
        // The DirectorAgent shape: p25 = 0.82m, p75 = 2.68m. Each side keeps its own sigma.
        RunRange range = RunCreditRange.Combine([Step(1, 10_000, 8_200, 26_800)], []);

        range.TypicalMillis.Should().Be(10_000);
        range.LowMillis.Should().Be(8_200);
        range.HighMillis.Should().Be(26_800);
    }

    [Fact]
    public void Summing_steps_narrows_the_relative_spread()
    {
        RunRange one = RunCreditRange.Combine([Step(1, 10_000, 8_000, 12_000)], []);
        RunRange four = RunCreditRange.Combine(
            [Step(1, 10_000, 8_000, 12_000), Step(2, 10_000, 8_000, 12_000),
             Step(3, 10_000, 8_000, 12_000), Step(4, 10_000, 8_000, 12_000)], []);

        double singleSpread = (one.HighMillis - one.LowMillis) / (double)one.TypicalMillis;
        double fourSpread = (four.HighMillis - four.LowMillis) / (double)four.TypicalMillis;

        four.TypicalMillis.Should().Be(40_000);
        // Independent steps: sqrt(4) = 2x the absolute width over 4x the typical, so half the relative spread.
        fourSpread.Should().BeApproximately(singleSpread / 2, 0.001);
    }

    [Fact]
    public void A_review_loop_scales_its_body_by_expected_passes_not_MaxIterations()
    {
        // Body: step 1 (6 credits) + the loop step 2 (4 credits) = 10 credits per pass.
        RunRange range = RunCreditRange.Combine(
            [Step(1, 6_000, 6_000, 6_000), Step(2, 4_000, 4_000, 4_000)],
            [RunCreditRange.ColdStartLoop(loopStepOrder: 2, targetStepOrder: 1, maxIterations: 3)]);

        range.TypicalMillis.Should().Be(15_000);
        range.ExpectedRunsByStepOrder[1].Should().Be(1.5);
        range.ExpectedRunsByStepOrder[2].Should().Be(1.5);
        // Var(N) * B^2 = 0.25 * 10k^2 -> sigma 5k -> +-3.37k.
        range.LowMillis.Should().Be(15_000 - 3_370);
        range.HighMillis.Should().Be(15_000 + 3_370);
    }

    [Fact]
    public void A_loop_with_MaxIterations_1_runs_once()
    {
        LoopInput loop = RunCreditRange.ColdStartLoop(2, 1, 1);
        loop.MeanPasses.Should().Be(1);
        loop.PassVariance.Should().Be(0);

        RunRange range = RunCreditRange.Combine(
            [Step(1, 6_000, 6_000, 6_000), Step(2, 4_000, 4_000, 4_000)], [loop]);

        range.TypicalMillis.Should().Be(10_000);
        range.LowMillis.Should().Be(10_000);
        range.HighMillis.Should().Be(10_000);
        range.ExpectedRunsByStepOrder[1].Should().Be(1);
    }

    [Fact]
    public void A_fixed_price_step_is_outside_the_range_and_counted_as_up_to()
    {
        RunRange range = RunCreditRange.Combine(
            [Step(1, 10_000, 8_000, 12_000), Step(2, 999_999, 0, 999_999, fixedMax: 160_000)], []);

        range.TypicalMillis.Should().Be(10_000);
        range.HighMillis.Should().BeCloseTo(12_000, 2);
        range.FixedMaxMillis.Should().Be(160_000);
    }

    [Fact]
    public void A_fixed_price_step_inside_a_loop_counts_MaxIterations_times_in_up_to()
    {
        RunRange range = RunCreditRange.Combine(
            [Step(1, 0, 0, 0, fixedMax: 100_000), Step(2, 4_000, 4_000, 4_000), Step(3, 1_000, 1_000, 1_000)],
            [RunCreditRange.ColdStartLoop(loopStepOrder: 3, targetStepOrder: 1, maxIterations: 3)]);

        range.FixedMaxMillis.Should().Be(300_000);
        range.TypicalMillis.Should().Be(7_500); // (4k + 1k) x 1.5; the fixed step adds nothing to the range
    }

    [Theory]
    [InlineData(int.MinValue)] // what a caller passes for a null LoopTargetStepOrder
    [InlineData(9)]            // past the loop
    [InlineData(0)]            // before the first step
    [InlineData(-4)]
    public void Malformed_loop_targets_never_throw_and_never_invert_the_range(int target)
    {
        List<LoopInput> loops = [new LoopInput(3, target, 1.5, 0.25, 3)];

        RunRange range = RunCreditRange.Combine(
            [Step(1, 1_000, 500, 2_000), Step(2, 2_000, 1_000, 3_000), Step(3, 500, 400, 600)], loops);

        range.LowMillis.Should().BeLessThanOrEqualTo(range.TypicalMillis);
        range.TypicalMillis.Should().BeLessThanOrEqualTo(range.HighMillis);
        // No body: every step, the loop step included, runs once.
        range.TypicalMillis.Should().Be(3_500);
        range.ExpectedRunsByStepOrder[3].Should().Be(1);
    }

    [Fact]
    public void Overlapping_windows_assign_each_step_to_the_narrowest_and_never_invert_the_range()
    {
        RunRange range = RunCreditRange.Combine(
            [Step(1, 1_000, 900, 1_100), Step(2, 2_000, 1_000, 3_000), Step(3, 500, 400, 600), Step(4, 500, 400, 600)],
            [RunCreditRange.ColdStartLoop(4, 1, 3), RunCreditRange.ColdStartLoop(3, 2, 2), new LoopInput(3, 3, 1, 0, 1)]);

        range.ExpectedRunsByStepOrder[1].Should().Be(1.5);
        range.ExpectedRunsByStepOrder[2].Should().Be(1.5);
        range.ExpectedRunsByStepOrder[3].Should().Be(1); // its own one-step window is the narrowest
        range.LowMillis.Should().BeLessThanOrEqualTo(range.HighMillis);
    }

    [Fact]
    public void Low_is_never_negative()
    {
        RunRange range = RunCreditRange.Combine([Step(1, 1_000, 0, 50_000)], []);

        range.LowMillis.Should().Be(0);
        range.HighMillis.Should().BeGreaterThan(range.TypicalMillis);
    }

    [Fact]
    public void An_empty_run_is_zero()
    {
        RunRange range = RunCreditRange.Combine(Array.Empty<RunStepInput>(), []);

        range.TypicalMillis.Should().Be(0);
        range.LowMillis.Should().Be(0);
        range.HighMillis.Should().Be(0);
    }
}
