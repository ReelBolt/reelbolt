using System;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// QA-P2-8: a <c>VideoAnalyze</c> that calls no provider (<see cref="StepCreditDefaults.IsComputeOnly"/>) holds
/// its compute (processing time) part on <b>every</b> estimator branch. Once step-type history existed, the p90 of
/// token-bearing runs was held and shown for a step that would never spend those tokens.
/// </summary>
public sealed class ComputeOnlyHoldTests
{
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();

    private static (long Low, long High) ComputeOnly(decimal? markup = null) =>
        StepCreditDefaults.MillisFor(StepType.VideoAnalyze, markup ?? PricingDefaults.DefaultMarkup, computeOnly: true);

    private static (long Low, long High) WithTokenPart(decimal? markup = null) =>
        StepCreditDefaults.MillisFor(StepType.VideoAnalyze, markup ?? PricingDefaults.DefaultMarkup);

    /// <summary>Three token-bearing VideoAnalyze runs of one agent: enough for both history buckets.</summary>
    private static StepCreditEstimator HistoryUnder(Guid agent) => new(
    [
        new StepCreditSample(StepType.VideoAnalyze, agent, 40_000),
        new StepCreditSample(StepType.VideoAnalyze, agent, 50_000),
        new StepCreditSample(StepType.VideoAnalyze, agent, 60_000)
    ]);

    [Fact]
    public void The_exact_agent_branch_reads_that_agents_history_and_the_compute_part_when_it_calls_no_provider()
    {
        StepCreditEstimator history = HistoryUnder(AgentA);

        StepCreditEstimate priced = history.Estimate(StepType.VideoAnalyze, AgentA);
        priced.Basis.Should().Be(CreditEstimateBasis.History);
        priced.HoldMillis.Should().BeGreaterThan(ComputeOnly().Low, "a token-bearing run holds its tokens");

        StepCreditEstimate computeOnly = history.Estimate(StepType.VideoAnalyze, AgentA, computeOnly: true);
        computeOnly.Basis.Should().Be(CreditEstimateBasis.History, "the branch is still taken; only the tokens are dropped");
        computeOnly.HoldMillis.Should().Be(ComputeOnly().Low);
        computeOnly.HighMillis.Should().Be(ComputeOnly().High);
        computeOnly.HoldMillis.Should().BeLessThan(priced.HoldMillis, "the agent's token-bearing p90 is not held");
    }

    [Fact]
    public void The_step_type_branch_reads_the_types_history_and_the_compute_part_when_it_calls_no_provider()
    {
        // Every sample belongs to another agent, so only the step-type bucket answers.
        StepCreditEstimator history = HistoryUnder(AgentB);

        StepCreditEstimate priced = history.Estimate(StepType.VideoAnalyze, AgentA);
        priced.Basis.Should().Be(CreditEstimateBasis.StepTypeHistory);
        priced.HoldMillis.Should().BeGreaterThan(ComputeOnly().Low);

        StepCreditEstimate computeOnly = history.Estimate(StepType.VideoAnalyze, AgentA, computeOnly: true);
        computeOnly.Basis.Should().Be(CreditEstimateBasis.StepTypeHistory);
        computeOnly.HoldMillis.Should().Be(ComputeOnly().Low);
        computeOnly.HighMillis.Should().Be(ComputeOnly().High);
        computeOnly.HoldMillis.Should().BeLessThan(priced.HoldMillis, "the step type's token-bearing p90 is not held");
    }

    [Fact]
    public void The_cold_start_branch_prices_the_compute_part_only()
    {
        StepCreditEstimate computeOnly = new StepCreditEstimator([]).Estimate(StepType.VideoAnalyze, null, computeOnly: true);

        computeOnly.Basis.Should().Be(CreditEstimateBasis.Default);
        computeOnly.HoldMillis.Should().Be(ComputeOnly().Low);
        computeOnly.HighMillis.Should().Be(ComputeOnly().High);
        computeOnly.HoldMillis.Should().BeLessThan(WithTokenPart().Low, "the token part is not held");
    }

    [Fact]
    public void The_live_markup_prices_the_compute_part_on_a_branch_that_has_history()
    {
        StepCreditEstimate computeOnly = HistoryUnder(AgentA)
            .Estimate(StepType.VideoAnalyze, AgentA, markup: 2m, computeOnly: true);

        computeOnly.HoldMillis.Should().Be(ComputeOnly(2m).Low);
        computeOnly.HighMillis.Should().Be(ComputeOnly(2m).High);
    }

    [Fact]
    public void The_flag_does_not_move_another_step_type_off_its_history()
    {
        StepCreditEstimator history = new(
        [
            new StepCreditSample(StepType.VideoCompile, null, 10_000),
            new StepCreditSample(StepType.VideoCompile, null, 20_000),
            new StepCreditSample(StepType.VideoCompile, null, 30_000)
        ]);

        StepCreditEstimate estimate = history.Estimate(StepType.VideoCompile, null, computeOnly: true);

        estimate.Basis.Should().Be(CreditEstimateBasis.StepTypeHistory);
        estimate.HoldMillis.Should().BeGreaterThan(StepCreditDefaults.MillisFor(StepType.VideoCompile).Low);
    }
}
