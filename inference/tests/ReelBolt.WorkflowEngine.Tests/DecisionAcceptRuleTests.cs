using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The shared accept rule itself (T-017): one implementation of "does this answer escalate given
/// these thresholds", parameterised by the <see cref="DecisionAcceptMetric"/> the site declared
/// rather than by the site's name.
///
/// <para>
/// These are the rule's own edges — what each metric reduces, what the two metrics that carry a
/// top-two margin do with it, and what happens to every input that is not a real number. The claim
/// that matters — that the executors and the calibration aggregation agree over the same persisted
/// row — is made in <see cref="DecisionCalibrationEscalationParityTests"/>, not here.
/// </para>
/// </summary>
public sealed class DecisionAcceptRuleTests
{
    private const double AcceptAt = 0.85;
    private const double MinMargin = 0.25;
    private const int MinScore = 9;

    // ---------------------------------------------------------------------
    // Top — phase 1's universal rule, and the default for a row with no declaration
    // ---------------------------------------------------------------------

    [Fact]
    public void Top_does_not_escalate_when_the_top_clears_the_threshold_and_the_margin_clears_the_floor()
    {
        var probabilities = new Dictionary<string, double> { ["None"] = 0.90, ["Warm"] = 0.10 };

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Top, null, DecisionKind.Choice, probabilities, null, AcceptAt, MinMargin)
            .Should().BeFalse();
    }

    [Fact]
    public void Top_escalates_when_the_top_is_below_the_threshold()
    {
        var probabilities = new Dictionary<string, double> { ["None"] = 0.60, ["Warm"] = 0.10 };

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Top, null, DecisionKind.Choice, probabilities, null, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    [Fact]
    public void Top_escalates_when_the_top_two_margin_is_below_the_floor()
    {
        // top 0.90 clears 0.85, but the 0.15 gap is narrower than the 0.25 floor.
        var probabilities = new Dictionary<string, double> { ["None"] = 0.90, ["Warm"] = 0.75 };

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Top, null, DecisionKind.Choice, probabilities, null, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    [Fact]
    public void Top_reads_a_Noul_answer_as_the_two_point_set_and_keeps_the_phase_one_zero_information_behaviour()
    {
        // p = 0.0 is the zero-information answer: TopTwo reads {0.0, 1.0} as (top 1.0, second 0.0),
        // so Top does NOT escalate it. Phase-1 semantics, three other tickets are built on it, and
        // this test states the arithmetic rather than arguing for it.
        DecisionAcceptRule
            .Escalates(
                DecisionAcceptMetric.Top, null, DecisionKind.Noul, new Dictionary<string, double>(), 0.0, AcceptAt, MinMargin)
            .Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // AtOrAbove — the ReviewLoop site: a sum over the rubric, never the top
    // ---------------------------------------------------------------------

    [Fact]
    public void AtOrAbove_escalates_on_a_confident_top_whose_mass_sits_below_the_cutoff()
    {
        // The defect in one row: top 0.90 with a wide 0.84 margin, but no mass at or above level 9 —
        // so the site that sums rejects a row the top/margin rule would call accepted.
        var probabilities = new Dictionary<string, double> { ["6"] = 0.90, ["5"] = 0.06, ["7"] = 0.04 };

        DecisionAcceptRule.MetricValue(DecisionAcceptMetric.AtOrAbove, MinScore, DecisionKind.Score, probabilities, null)
            .Should().Be(0.0);

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.AtOrAbove, MinScore, DecisionKind.Score, probabilities, null, AcceptAt, MinMargin)
            .Should().BeTrue();

        // ... while the universal rule accepts the very same row.
        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Top, MinScore, DecisionKind.Score, probabilities, null, AcceptAt, MinMargin)
            .Should().BeFalse();
    }

    [Fact]
    public void AtOrAbove_accepts_when_the_mass_at_or_above_the_cutoff_clears_the_threshold()
    {
        var probabilities = new Dictionary<string, double> { ["9"] = 0.60, ["10"] = 0.30, ["8"] = 0.10 };

        DecisionAcceptRule.MetricValue(DecisionAcceptMetric.AtOrAbove, MinScore, DecisionKind.Score, probabilities, null)
            .Should().BeApproximately(0.90, 1e-9);

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.AtOrAbove, MinScore, DecisionKind.Score, probabilities, null, AcceptAt, MinMargin)
            .Should().BeFalse();
    }

    [Fact]
    public void AtOrAbove_still_escalates_a_bimodal_split_whose_sum_clears_the_threshold()
    {
        // pAtOrAbove is 1.0, and it must still escalate: the margin check runs first.
        var probabilities = new Dictionary<string, double> { ["9"] = 0.50, ["10"] = 0.45, ["8"] = 0.05 };

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.AtOrAbove, MinScore, DecisionKind.Score, probabilities, null, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    [Fact]
    public void AtOrAbove_fails_closed_when_the_cutoff_is_missing_and_reports_the_metric_as_uncomputable()
    {
        var probabilities = new Dictionary<string, double> { ["9"] = 0.99, ["8"] = 0.01 };

        DecisionAcceptRule.MetricValue(DecisionAcceptMetric.AtOrAbove, null, DecisionKind.Score, probabilities, null)
            .Should().BeNull();

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.AtOrAbove, null, DecisionKind.Score, probabilities, null, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    [Fact]
    public void AtOrAbove_fails_closed_when_no_key_is_a_rubric_level()
    {
        // The logprob client's Score answer: a weighted score, but a map nothing can be summed from.
        var probabilities = new Dictionary<string, double> { ["low"] = 0.60, ["high"] = 0.40 };

        DecisionAcceptRule.MetricValue(DecisionAcceptMetric.AtOrAbove, MinScore, DecisionKind.Score, probabilities, null)
            .Should().BeNull();

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.AtOrAbove, MinScore, DecisionKind.Score, probabilities, null, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("11")]
    [InlineData("not-a-level")]
    public void AtOrAbove_ignores_keys_outside_the_rubric(string label)
    {
        var probabilities = new Dictionary<string, double> { [label] = 0.99 };

        DecisionAcceptRule.MetricValue(DecisionAcceptMetric.AtOrAbove, MinScore, DecisionKind.Score, probabilities, null)
            .Should().BeNull();
    }

    // ---------------------------------------------------------------------
    // Affirmative — the Conditional site: the Noul value, and no margin
    // ---------------------------------------------------------------------

    [Fact]
    public void Affirmative_compares_the_affirmative_probability_against_the_threshold()
    {
        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Affirmative, null, DecisionKind.Noul, new Dictionary<string, double>(), 0.90, AcceptAt, MinMargin)
            .Should().BeFalse();

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Affirmative, null, DecisionKind.Noul, new Dictionary<string, double>(), 0.10, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    [Fact]
    public void Affirmative_reads_the_opposite_way_from_Top_on_the_same_answer()
    {
        // p_yes 0.10 ⇒ TopTwo is (0.90, 0.10): a reader applying the top rule calls this answer
        // confident and accepted, while the site that asked the question rejects it.
        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Top, null, DecisionKind.Noul, new Dictionary<string, double>(), 0.10, AcceptAt, MinMargin)
            .Should().BeFalse();

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Affirmative, null, DecisionKind.Noul, new Dictionary<string, double>(), 0.10, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    [Fact]
    public void Affirmative_applies_no_top_two_margin()
    {
        // p_yes 0.50 ⇒ a 0.00 top-two gap, which the margin floor would reject; the Affirmative
        // metric has no margin term, so a threshold below 0.50 accepts it.
        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Affirmative, null, DecisionKind.Noul, new Dictionary<string, double>(), 0.50, 0.40, MinMargin)
            .Should().BeFalse();

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Top, null, DecisionKind.Noul, new Dictionary<string, double>(), 0.50, 0.40, MinMargin)
            .Should().BeTrue();
    }

    [Fact]
    public void Affirmative_fails_closed_when_the_answer_carries_no_affirmative_probability()
    {
        DecisionAcceptRule.MetricValue(DecisionAcceptMetric.Affirmative, null, DecisionKind.Choice, new Dictionary<string, double>(), null)
            .Should().BeNull();

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Affirmative, null, DecisionKind.Choice, new Dictionary<string, double>(), null, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // Fail closed on everything that is not a number
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_non_finite_probability_escalates(double bad)
    {
        var probabilities = new Dictionary<string, double> { ["9"] = 0.90, ["8"] = bad };

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Top, null, DecisionKind.Score, probabilities, null, AcceptAt, MinMargin)
            .Should().BeTrue();

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.AtOrAbove, MinScore, DecisionKind.Score, probabilities, null, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_non_finite_affirmative_probability_escalates(double bad)
    {
        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Affirmative, null, DecisionKind.Noul, new Dictionary<string, double>(), bad, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(double.NaN, 0.25)]
    [InlineData(0.85, double.NaN)]
    [InlineData(double.PositiveInfinity, 0.25)]
    [InlineData(0.85, double.NegativeInfinity)]
    public void A_threshold_that_is_not_a_number_escalates_rather_than_reading_as_accepted(double acceptAt, double minMargin)
    {
        // `x < NaN` is false, so without this guard a broken threshold would silently accept
        // everything — the one direction an accept rule must never fail in.
        var probabilities = new Dictionary<string, double> { ["None"] = 0.99, ["Warm"] = 0.01 };

        DecisionAcceptRule
            .Escalates(DecisionAcceptMetric.Top, null, DecisionKind.Choice, probabilities, null, acceptAt, minMargin)
            .Should().BeTrue();
    }

    [Fact]
    public void An_unrecognised_metric_escalates_and_reports_no_metric_value()
    {
        var probabilities = new Dictionary<string, double> { ["None"] = 0.99, ["Warm"] = 0.01 };
        var unknown = (DecisionAcceptMetric)99;

        DecisionAcceptRule.MetricValue(unknown, null, DecisionKind.Choice, probabilities, null).Should().BeNull();

        DecisionAcceptRule
            .Escalates(unknown, null, DecisionKind.Choice, probabilities, null, AcceptAt, MinMargin)
            .Should().BeTrue();
    }

    // ---------------------------------------------------------------------
    // The reduction itself
    // ---------------------------------------------------------------------

    [Fact]
    public void TopTwo_takes_the_two_largest_values_descending()
    {
        var probabilities = new Dictionary<string, double> { ["a"] = 0.10, ["b"] = 0.70, ["c"] = 0.20 };

        DecisionAcceptRule.TopTwo(DecisionKind.Choice, probabilities, null).Should().Be((0.70, 0.20));
    }

    [Fact]
    public void TopTwo_reads_a_single_option_as_a_whole_margin()
    {
        DecisionAcceptRule.TopTwo(DecisionKind.Choice, new Dictionary<string, double> { ["only"] = 0.80 }, null)
            .Should().Be((0.80, 0.0));
    }

    [Fact]
    public void TopTwo_reads_an_empty_map_as_the_two_point_set()
    {
        DecisionAcceptRule.TopTwo(DecisionKind.Score, new Dictionary<string, double>(), null)
            .Should().Be((1.0, 0.0));
    }

    [Theory]
    [InlineData(DecisionAcceptMetric.Top, true)]
    [InlineData(DecisionAcceptMetric.AtOrAbove, true)]
    [InlineData(DecisionAcceptMetric.Affirmative, false)]
    public void Only_the_metrics_that_need_it_carry_the_top_two_margin(DecisionAcceptMetric metric, bool needsMargin)
    {
        DecisionAcceptRule.RequiresTopTwoMargin(metric).Should().Be(needsMargin);
    }
}
