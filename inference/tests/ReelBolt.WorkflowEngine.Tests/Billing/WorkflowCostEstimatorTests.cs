using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Metering;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Tests.Pricing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// WP B7: the pre-run cost estimate. Covers the three things the WP is judged on — the estimate
/// maths (percentiles over the recorded usage, and which bucket wins), the cold-start path (an
/// organization with no history must neither hold nothing nor hold without bound), and the way the
/// engine's per-step hold is now sized from it (replacing B6b's flat placeholder).
/// </summary>
public sealed class WorkflowCostEstimatorTests
{
    /// <summary>Yesterday: inside the default window, whatever day the suite runs on.</summary>
    private static readonly DateTime Past = DateTime.UtcNow.AddDays(-1);

    // -----------------------------------------------------------------
    // The maths
    // -----------------------------------------------------------------

    [Fact]
    public void Percentile_cont_matches_the_postgres_definition()
    {
        long[] sample = [10, 20, 30, 40];

        // rank = p * (n - 1), linearly interpolated between the two closest ranks: exactly what
        // percentile_cont does, so an estimate here is the number B0's SQL would have reported.
        CreditPercentiles.Continued(sample, 0.5).Should().Be(25);
        CreditPercentiles.Continued(sample, 0.9).Should().Be(37);
        CreditPercentiles.Continued(sample, 0).Should().Be(10);
        CreditPercentiles.Continued(sample, 1).Should().Be(40);
        CreditPercentiles.Continued([], 0.9).Should().Be(0);
        CreditPercentiles.Continued([7], 0.9).Should().Be(7);
    }

    [Fact]
    public void The_estimate_is_the_p50_and_p90_of_the_organizations_own_history()
    {
        var agent = Guid.NewGuid();
        var estimator = new StepCreditEstimator(Enumerable.Range(1, 10)
            .Select(i => new StepCreditSample(StepType.Agent, agent, i * 1_000)));

        StepCreditEstimate estimate = estimator.Estimate(StepType.Agent, agent);

        estimate.Basis.Should().Be(CreditEstimateBasis.History);
        estimate.LowMillis.Should().Be(5_500);   // percentile_cont(0.5) of 1..10 (thousands)
        estimate.HighMillis.Should().Be(9_100);  // percentile_cont(0.9) of 1..10
        estimate.HoldMillis.Should().Be(9_100);  // B0: reserve at the p90
        estimate.FromHistory.Should().BeTrue();
    }

    [Fact]
    public void A_thin_agent_bucket_falls_back_to_the_step_type_bucket()
    {
        var measured = Guid.NewGuid();
        var unseen = Guid.NewGuid();
        List<StepCreditSample> samples = [.. Enumerable.Range(1, 6)
            .Select(i => new StepCreditSample(StepType.Agent, measured, i * 10_000))];
        samples.Add(new StepCreditSample(StepType.Agent, unseen, 1_000));
        samples.Add(new StepCreditSample(StepType.Agent, unseen, 2_000));

        StepCreditEstimate estimate = new StepCreditEstimator(samples).Estimate(StepType.Agent, unseen);

        // Two observations are not enough to price THIS agent. The step-type pool is
        // [1,2,10,20,30,40,50,60] thousands, whose p90 is 53,000: what the estimator used. The thin
        // bucket's own 1,900 would have been the answer had it been trusted.
        estimate.Basis.Should().Be(CreditEstimateBasis.StepTypeHistory);
        estimate.HoldMillis.Should().Be(53_000);
    }

    [Fact]
    public void Step_types_that_are_not_charged_hold_nothing_however_much_history_says()
    {
        var agent = Guid.NewGuid();
        var estimator = new StepCreditEstimator(Enumerable.Range(0, 10)
            .Select(_ => new StepCreditSample(StepType.VideoGenerate, agent, 500_000)));

        // VideoGenerate left this loop in B6c: it is chargeable now, so history sizes its hold like
        // any other step. Only the two step types that make no provider call at all remain free.
        foreach (StepType free in new[] { StepType.Conditional, StepType.Extract })
        {
            StepCreditEstimate estimate = estimator.Estimate(free, agent);
            estimate.Basis.Should().Be(CreditEstimateBasis.NotCharged);
            estimate.LowMillis.Should().Be(0);
            estimate.HighMillis.Should().Be(0);
            estimate.HoldMillis.Should().Be(0);
        }
    }

    [Fact]
    public void Even_a_measured_zero_does_not_make_a_range_whose_top_is_below_its_bottom()
    {
        var agent = Guid.NewGuid();
        var estimator = new StepCreditEstimator(Enumerable.Range(0, 5)
            .Select(i => new StepCreditSample(StepType.Agent, agent, i == 4 ? 1_000 : 0)));

        StepCreditEstimate estimate = estimator.Estimate(StepType.Agent, agent);

        estimate.LowMillis.Should().BeLessThanOrEqualTo(estimate.HighMillis);
        estimate.HighMillis.Should().Be(estimate.HoldMillis);
    }

    // -----------------------------------------------------------------
    // Cold start
    // -----------------------------------------------------------------

    [Fact]
    public void An_organization_with_no_history_holds_the_low_edge_of_its_usual_range_and_never_a_silent_zero()
    {
        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom([], [
            Step(1, StepType.Agent),
            Step(2, StepType.Agent),
            Step(3, StepType.VideoCompile)
        ]);

        estimate.HasHistory.Should().BeFalse();
        estimate.Steps.Should().OnlyContain(s => s.Basis == nameof(CreditEstimateBasis.Default));
        estimate.LowMillis.Should().BePositive();
        estimate.HighMillis.Should().BeGreaterThan(estimate.LowMillis);

        // the cold-start hold re-plan: each cold step holds the larger of B0's default low and its share
        // of the LOW edge of the run's usual range (the displayed LowMillis, since the whole run is cold
        // and has no display history). So the requirement is never below the low nor below the old
        // figure, and never zero.
        long before = estimate.Steps.Sum(s => StepCreditDefaults.MillisFor(Enum.Parse<StepType>(s.StepType)).Low);
        estimate.RequiredMillis.Should().BeGreaterThanOrEqualTo(estimate.LowMillis);
        estimate.RequiredMillis.Should().BeGreaterThanOrEqualTo(before);
        estimate.Steps.Should().OnlyContain(s => s.HoldMillis >= StepCreditDefaults.MillisFor(Enum.Parse<StepType>(s.StepType)).Low);
        estimate.Steps.Sum(s => s.HoldMillis).Should().Be(estimate.RequiredMillis);
        // Proportional: the two identical agent steps hold the same, give or take the rounding unit.
        Math.Abs(estimate.Steps[0].HoldMillis - estimate.Steps[1].HoldMillis).Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public void The_hold_a_one_step_cold_start_run_will_take_is_the_larger_of_the_old_default_and_its_low_edge()
    {
        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom([], [Step(1, StepType.Agent)]);

        // A one-step run: its share is the whole low edge, its own displayed p25; the old default wins
        // when it is larger (the cold-start hold re-plan only raises a cold hold, never lowers one).
        estimate.RequiredMillis.Should().Be(Math.Max(StepCreditDefaults.MillisFor(StepType.Agent).Low, estimate.Steps.Single().LowMillis));
        estimate.RequiredMillis.Should().BeGreaterThanOrEqualTo(estimate.LowMillis);

        // And it is a real, non-zero hold: enough to keep the reserve -> settle path alive rather
        // than short-circuiting the gate into taking no reservation at all.
        estimate.RequiredMillis.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_workflow_of_only_uncosted_steps_is_honestly_free()
    {
        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom([], [
            Step(1, StepType.Conditional),
            Step(2, StepType.Extract)
        ]);

        estimate.LowMillis.Should().Be(0);
        estimate.RequiredMillis.Should().Be(0);
        estimate.Steps.Should().OnlyContain(s => s.Basis == nameof(CreditEstimateBasis.NotCharged));
    }

    [Fact]
    public void The_run_estimate_sums_the_steps_and_carries_the_per_step_breakdown()
    {
        var agent = Guid.NewGuid();
        List<StepCreditSample> samples = [.. Enumerable.Range(1, 4)
            .Select(i => new StepCreditSample(StepType.Agent, agent, i * 1_000))];

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom(samples, [
            Step(1, StepType.Agent, agent),
            Step(2, StepType.Agent, agent)
        ]);

        estimate.HasHistory.Should().BeTrue();
        estimate.Steps.Should().HaveCount(2);
        estimate.Steps[0].StepOrder.Should().Be(1);
        estimate.Steps[0].Basis.Should().Be(nameof(CreditEstimateBasis.History));
        // The displayed typical sums the steps; the band narrows (independent steps), so Low/High are
        // no longer 2x a step's own p25/p75.
        estimate.TypicalMillis.Should().Be(2 * estimate.Steps[0].TypicalMillis);
        estimate.LowMillis.Should().BeLessThanOrEqualTo(estimate.TypicalMillis);
        estimate.HighMillis.Should().BeGreaterThanOrEqualTo(estimate.TypicalMillis);
        estimate.RequiredMillis.Should().Be(2 * estimate.Steps[0].HoldMillis);
    }

    // -----------------------------------------------------------------
    // Text-prompted segmentation: shown and held agree (T-001d)
    // -----------------------------------------------------------------

    /// <summary>
    /// One segmentation target on the step's default 60 s cap: `ceil(60 × 30 / 16) = 113` frame-units at
    /// 1,667 millicredits each (<see cref="SegmentationCreditPricing"/>), the figure the hold reserves.
    /// </summary>
    private const long OneSegmentationTargetMillis = 188_371;

    /// <summary>One target, <c>method = Segmentation</c>, no explicit cap (so the 60 s default applies).</summary>
    private const string OneSegmentationTargetConfig =
        """{"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","trackObjects":[{"label":"the speedometer","method":"Segmentation"}]}""";

    /// <summary>The same step with a feature-tracked target instead of a segmentation one: the add-on is 0.</summary>
    private const string NoSegmentationTargetConfig =
        """{"version":1,"source":{"kind":"ProjectFile"},"transcription":"On","trackObjects":[{"label":"the speedometer","method":"Feature"}]}""";

    [Fact]
    public void A_VideoAnalyze_step_shows_the_segmentation_add_on_its_hold_already_carries()
    {
        var agent = Guid.NewGuid();
        List<StepCreditSample> samples = [.. Enumerable.Range(1, 4)
            .Select(i => new StepCreditSample(StepType.VideoAnalyze, agent, i * 10_000))];

        WorkflowCostEstimate withoutTarget = WorkflowCostEstimator.EstimateFrom(
            samples, [Step(1, StepType.VideoAnalyze, agent)]);
        WorkflowCostEstimate segmented = WorkflowCostEstimator.EstimateFrom(
            samples, [AnalyzeStep(agent, OneSegmentationTargetConfig)]);

        long addOn = SegmentationCreditPricing.AddOnMillis(OneSegmentationTargetConfig);
        addOn.Should().Be(OneSegmentationTargetMillis);

        // SHOWN: the low, the typical and the high each carry the whole add-on. A per-target purchase the
        // clip's analysis time does not change moves the band without widening it (both the p25 and the
        // p75 shift by the same figure), which is exactly why the shown range was the smaller of the two.
        WorkflowStepCostEstimate before = withoutTarget.Steps.Single();
        WorkflowStepCostEstimate after = segmented.Steps.Single();
        after.LowMillis.Should().Be(before.LowMillis + addOn);
        after.TypicalMillis.Should().Be(before.TypicalMillis + addOn);
        after.HighMillis.Should().Be(before.HighMillis + addOn);
        segmented.LowMillis.Should().Be(withoutTarget.LowMillis + addOn);
        segmented.TypicalMillis.Should().Be(withoutTarget.TypicalMillis + addOn);
        segmented.HighMillis.Should().Be(withoutTarget.HighMillis + addOn);

        // HELD: the same figure, once. RequiredMillis is the hold plan's sum — the p90 plus the add-on —
        // and the display change must not make the reservation count the purchase a second time.
        long p90 = new StepCreditEstimator(samples, StepCreditEstimator.DefaultMinSamples)
            .Estimate(StepType.VideoAnalyze, agent)
            .HoldMillis;
        after.HoldMillis.Should().Be(p90 + addOn);
        segmented.RequiredMillis.Should().Be(after.HoldMillis);
        segmented.RequiredMillis.Should().Be(withoutTarget.RequiredMillis + addOn);
    }

    [Fact]
    public void A_VideoAnalyze_step_with_no_segmentation_target_is_unchanged()
    {
        var agent = Guid.NewGuid();
        List<StepCreditSample> samples = [.. Enumerable.Range(1, 4)
            .Select(i => new StepCreditSample(StepType.VideoAnalyze, agent, i * 10_000))];

        WorkflowCostEstimate bare = WorkflowCostEstimator.EstimateFrom(samples, [Step(1, StepType.VideoAnalyze, agent)]);
        WorkflowCostEstimate featureOnly = WorkflowCostEstimator.EstimateFrom(
            samples, [AnalyzeStep(agent, NoSegmentationTargetConfig)]);

        SegmentationCreditPricing.AddOnMillis(NoSegmentationTargetConfig).Should().Be(0);
        featureOnly.Steps.Should().Equal(bare.Steps);
        featureOnly.LowMillis.Should().Be(bare.LowMillis);
        featureOnly.TypicalMillis.Should().Be(bare.TypicalMillis);
        featureOnly.HighMillis.Should().Be(bare.HighMillis);
        featureOnly.RequiredMillis.Should().Be(bare.RequiredMillis);
    }

    // -----------------------------------------------------------------
    // The engine's per-step hold (replacing B6b's placeholder)
    // -----------------------------------------------------------------

    [Fact]
    public async Task The_engine_hold_is_B0s_default_when_the_organization_has_no_history()
    {
        using var fixture = new EngineFixture();
        long hold = await fixture.Estimator.HoldMillisAsync(fixture.OrgId, StepType.Agent, Guid.NewGuid(), CancellationToken.None);

        hold.Should().Be(StepCreditDefaults.MillisFor(StepType.Agent).Low);
        hold.Should().BePositive();
    }

    [Fact]
    public async Task The_engine_hold_is_the_organizations_own_p90_once_it_has_history()
    {
        using var fixture = new EngineFixture();
        Guid agent = await fixture.SeedHistoryAsync(StepType.Agent, [1_000, 2_000, 3_000, 4_000, 5_000]);

        long hold = await fixture.Estimator.HoldMillisAsync(fixture.OrgId, StepType.Agent, agent, CancellationToken.None);

        hold.Should().Be(4_600); // percentile_cont(0.9) of 1..5 (thousands)
        hold.Should().NotBe(StepCreditDefaults.MillisFor(StepType.Agent).Low);
    }

    [Fact]
    public async Task A_cached_step_result_is_not_part_of_the_estimate()
    {
        using var fixture = new EngineFixture();
        Guid agent = await fixture.SeedHistoryAsync(StepType.Agent, [1_000, 2_000, 3_000, 4_000, 5_000], cachedCredits: [0, 0, 0]);

        // Five charged thousands plus three cached zeros: including the cache hits would drag the p90
        // towards zero, which is exactly why B0's own measurement query excluded them too.
        long hold = await fixture.Estimator.HoldMillisAsync(fixture.OrgId, StepType.Agent, agent, CancellationToken.None);
        hold.Should().Be(4_600);
    }

    [Fact]
    public async Task The_engine_never_throws_when_the_history_cannot_be_read()
    {
        var estimator = new UsageHistoryCreditHoldEstimator(
            new ThrowingFactory(),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<UsageHistoryCreditHoldEstimator>.Instance);

        long hold = await estimator.HoldMillisAsync(Guid.NewGuid(), StepType.Agent, Guid.NewGuid(), CancellationToken.None);

        // Billing must not be able to fail a step the gate would have allowed: an unreadable history
        // degrades to B0's default, never to a throw and never to zero.
        hold.Should().Be(StepCreditDefaults.MillisFor(StepType.Agent).Low);
    }

    [Fact]
    public async Task A_step_type_that_is_never_charged_holds_nothing()
    {
        using var fixture = new EngineFixture();
        await fixture.SeedHistoryAsync(StepType.Extract, [500_000, 500_000, 500_000]);

        long hold = await fixture.Estimator.HoldMillisAsync(
            fixture.OrgId, StepType.Extract, null, CancellationToken.None);

        hold.Should().Be(0);
    }

    [Fact]
    public async Task A_video_generate_step_is_chargeable_and_holds_its_recorded_history()
    {
        using var fixture = new EngineFixture();
        await fixture.SeedHistoryAsync(StepType.VideoGenerate, [120_000, 200_000, 240_000]);

        long hold = await fixture.Estimator.HoldMillisAsync(
            fixture.OrgId, StepType.VideoGenerate, null, CancellationToken.None);

        // B6c: VideoGenerate used to answer 0 here — and a zero hold takes no reservation, so the
        // VideoGenSeconds credit rows the step's own takes recorded were never settled. It is a
        // chargeable step type now, so its recorded usage sizes the hold like any other.
        // percentile_cont(0.9) of [120k, 200k, 240k] = 200k + 0.8 x 40k.
        hold.Should().Be(232_000);
        StepCreditDefaults.MillisFor(StepType.VideoGenerate).Low.Should().BePositive();
    }

    // -----------------------------------------------------------------
    // The Inference API estimator, end to end over a real (in-memory) context
    // -----------------------------------------------------------------

    [Fact]
    public async Task The_api_estimator_reads_the_ledger_and_prices_the_run_from_it()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        Guid agent = Guid.NewGuid();
        SeedHistory(db, org, StepType.Agent, agent, [2_000, 4_000, 6_000, 8_000, 10_000]);
        await db.SaveChangesAsync();

        WorkflowCostEstimator estimator = ApiEstimator(db);
        WorkflowCostEstimate estimate = await estimator.EstimateAsync(org, [Step(1, StepType.Agent, agent)], CancellationToken.None);

        estimate.HasHistory.Should().BeTrue();
        estimate.Steps.Single().Basis.Should().Be(nameof(CreditEstimateBasis.History));
        estimate.RequiredMillis.Should().Be(9_200); // percentile_cont(0.9) of 2,4,6,8,10 (thousands)
    }

    [Fact]
    public async Task The_api_estimator_ignores_another_organizations_usage()
    {
        using var db = NewApiContext();
        Guid mine = Guid.NewGuid();
        Guid theirs = Guid.NewGuid();
        SeedHistory(db, theirs, StepType.Agent, Guid.NewGuid(), [900_000, 900_000, 900_000]);
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(
            mine, [Step(1, StepType.Agent, Guid.NewGuid())], CancellationToken.None);

        estimate.HasHistory.Should().BeFalse();
        estimate.Steps.Single().Basis.Should().Be(nameof(CreditEstimateBasis.Default));
    }

    [Fact]
    public async Task Billing_disabled_estimates_nothing_and_refuses_nothing()
    {
        using var db = NewApiContext();
        WorkflowCostEstimator estimator = ApiEstimator(db, enabled: false);

        WorkflowCostEstimate estimate = await estimator.EstimateAsync(
            Guid.NewGuid(), [Step(1, StepType.Agent)], CancellationToken.None);
        WorkflowPreflightResult preflight = await estimator.PreflightAsync(
            Guid.NewGuid(), [Step(1, StepType.Agent)], CancellationToken.None);

        estimate.BillingEnabled.Should().BeFalse();
        estimate.RequiredMillis.Should().Be(0);
        preflight.Refused.Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // The display range (separate from the hold)
    // -----------------------------------------------------------------

    [Fact]
    public async Task With_five_past_runs_the_range_is_their_p25_to_p75()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep step) = SeedWorkflow(db, StepType.Agent, AgentType.DirectorAgent);
        foreach (long total in new long[] { 10_000, 20_000, 30_000, 40_000, 50_000 })
        {
            SeedRun(db, org, workflow, (step, [total]));
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(org, [step], CancellationToken.None);

        estimate.RangeBasis.Should().Be("PastRuns");
        estimate.PastRunCount.Should().Be(5);
        estimate.TypicalMillis.Should().Be(30_000);
        estimate.LowMillis.Should().Be(20_000);
        estimate.HighMillis.Should().Be(40_000);
        // The hold is untouched: five samples of 10k..50k, p90 = 46k.
        estimate.RequiredMillis.Should().Be(46_000);
    }

    [Fact]
    public async Task Zero_credit_runs_do_not_count_towards_the_past_runs_range()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep step) = SeedWorkflow(db, StepType.Agent, AgentType.DirectorAgent);
        foreach (long total in new long[] { 0, 0, 10_000, 20_000, 30_000 })
        {
            SeedRun(db, org, workflow, (step, [total]));
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(org, [step], CancellationToken.None);

        // Two all-BYO (zero) runs leave three: fewer than five, so the per-step path answers.
        estimate.PastRunCount.Should().Be(3);
        estimate.RangeBasis.Should().Be("StepHistory");
        estimate.Steps.Single().Basis.Should().Be(nameof(CreditEstimateBasis.History));
        estimate.Steps.Single().TypicalMillis.Should().Be(20_000);
    }

    [Fact]
    public async Task Runs_from_before_the_last_edit_of_the_workflow_are_not_past_runs()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep step) = SeedWorkflow(db, StepType.Agent, AgentType.DirectorAgent);
        foreach (long total in new long[] { 10_000, 20_000, 30_000, 40_000, 50_000 })
        {
            SeedRun(db, org, workflow, (step, [total]));
        }
        workflow.UpdatedAt = DateTime.UtcNow; // saved after every one of those runs
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(org, [step], CancellationToken.None);

        estimate.PastRunCount.Should().Be(0);
        estimate.RangeBasis.Should().NotBe("PastRuns");
    }

    [Fact]
    public async Task Past_runs_count_only_at_the_same_output_resolution()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep step) = SeedWorkflow(db, StepType.Agent, AgentType.DirectorAgent);
        foreach (long total in new long[] { 10_000, 20_000, 30_000, 40_000, 50_000 })
        {
            SeedRun(db, org, workflow, (step, [total]));
        }
        foreach (WorkflowExecution run in db.WorkflowExecutions.Local)
        {
            run.OutputResolution = 2160; // every past run rendered at 4K
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate at4k = await ApiEstimator(db).EstimateAsync(org, [step], 2160, CancellationToken.None);
        WorkflowCostEstimate atAuto = await ApiEstimator(db).EstimateAsync(org, [step], CancellationToken.None);

        at4k.RangeBasis.Should().Be("PastRuns");
        at4k.PastRunCount.Should().Be(5);
        at4k.TypicalMillis.Should().Be(30_000, "4K runs describe a 4K run, unscaled");
        atAuto.PastRunCount.Should().Be(0, "a 4K run's total does not describe an Auto run");
        atAuto.RangeBasis.Should().NotBe("PastRuns");
    }

    [Fact]
    public async Task Zero_credit_samples_are_not_display_history()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        Guid agent = Guid.NewGuid();
        SeedHistory(db, org, StepType.Agent, agent, [0, 0, 0, 0, 5_000]);
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(org, [Step(1, StepType.Agent, agent)], CancellationToken.None);

        // The hold still sees all five (its query is unchanged); the display sees one, too few to trust.
        estimate.HasHistory.Should().BeTrue();
        estimate.Steps.Single().Basis.Should().Be(nameof(CreditEstimateBasis.Default));
        estimate.Steps.Single().TypicalMillis.Should().BePositive();
        estimate.RangeBasis.Should().Be("FirstRun");
    }

    [Fact]
    public async Task An_agent_type_bucket_is_used_before_the_cold_start_profile()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        Guid measured = Guid.NewGuid();
        Guid unseen = Guid.NewGuid();
        db.AgentDefinitions.Add(new AgentDefinition { Id = measured, Name = "a", AgentType = AgentType.NarrationWriter });
        db.AgentDefinitions.Add(new AgentDefinition { Id = unseen, Name = "b", AgentType = AgentType.NarrationWriter });
        SeedHistory(db, org, StepType.Agent, measured, [2_000, 4_000, 6_000]);
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(org, [Step(1, StepType.Agent, unseen)], CancellationToken.None);

        WorkflowStepCostEstimate step = estimate.Steps.Single();
        step.Basis.Should().Be(nameof(CreditEstimateBasis.AgentTypeHistory));
        step.TypicalMillis.Should().Be(4_000);
        step.LowMillis.Should().Be(3_000);
        step.HighMillis.Should().Be(5_000);
        estimate.RangeBasis.Should().Be("StepHistory");
    }

    [Fact]
    public async Task An_own_provider_agent_step_shows_zero_and_says_why()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedInferenceProvider(Guid.NewGuid(), "mine", InferenceProviderKind.Anthropic, "https://x",
                "claude-opus-5-5", "key", 60, OrganizationId: org, IsPlatformManaged: false));

        WorkflowCostEstimate estimate = await ApiEstimator(db, resolver.Object, SeedRates()).EstimateAsync(
            org, [Step(1, StepType.Agent)], CancellationToken.None);

        WorkflowStepCostEstimate step = estimate.Steps.Single();
        step.Basis.Should().Be(nameof(CreditEstimateBasis.OwnProvider));
        step.TypicalMillis.Should().Be(0);
        step.HighMillis.Should().Be(0);
        estimate.SpreadReasons.Should().Contain("own_provider_free");
        // the chat is priced at 0 on the org's own provider, so the
        // step's share of the low edge is 0 — it keeps its previous default hold (never 1 millicredit),
        // so a reservation exists and settlement charges any compute it does.
        estimate.RequiredMillis.Should().Be(StepCreditDefaults.MillisFor(StepType.Agent).Low);
        step.HoldMillis.Should().Be(StepCreditDefaults.MillisFor(StepType.Agent).Low);
    }

    [Fact]
    public async Task A_pricing_failure_falls_back_to_seed_constants_and_never_throws()
    {
        using var db = NewApiContext();
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<AgentType>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("resolver down"));
        var rates = new Mock<IModelPriceService>();
        rates.Setup(r => r.PriceAsync(It.IsAny<PriceRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnpricedUsageException(UsageKind.EncodeOutputSeconds, InferenceProviderKind.OpenAICompatible, "1080p"));

        WorkflowCostEstimate estimate = await ApiEstimator(db, resolver.Object, rates.Object).EstimateAsync(
            Guid.NewGuid(), [Step(1, StepType.Agent), Step(2, StepType.VideoCompile)], CancellationToken.None);

        long agentSeed = StepUsageProfiles.For(StepType.Agent, null).Quantities
            .Sum(q => StepUsageProfiles.SeedFallbackMillis(q.Kind, q.Quantity));
        long compileSeed = StepUsageProfiles.For(StepType.VideoCompile, null).Quantities
            .Sum(q => StepUsageProfiles.SeedFallbackMillis(q.Kind, q.Quantity));

        estimate.Steps[0].TypicalMillis.Should().Be(agentSeed).And.BePositive();
        estimate.Steps[1].TypicalMillis.Should().Be(compileSeed).And.BePositive();
        estimate.Steps.Should().OnlyContain(s => s.Basis == nameof(CreditEstimateBasis.Default));
        estimate.LowMillis.Should().BePositive();
    }

    [Fact]
    public async Task Loop_passes_come_from_history_once_there_are_three_runs()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep body) = SeedWorkflow(db, StepType.Agent, AgentType.DirectorAgent);
        WorkflowStep loop = AddStep(db, workflow, 2, StepType.ReviewLoop, AgentType.VideoReviewAgent, loopTarget: 1, maxIterations: 3);
        for (int i = 0; i < 3; i++)
        {
            // Every past run passed review on the first pass: one result of the loop step each.
            SeedRun(db, org, workflow, (body, [1_000]), (loop, [500]));
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate cold = WorkflowCostEstimator.EstimateFrom([], [body, loop]);
        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(org, [body, loop], CancellationToken.None);

        cold.Steps.Should().OnlyContain(s => s.ExpectedRuns == 1.5);
        estimate.Steps.Should().OnlyContain(s => s.ExpectedRuns == 1.0);
    }

    [Fact]
    public async Task Runs_executed_from_their_snapshot_feed_the_loop_passes_of_the_source_workflow()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep body) = SeedWorkflow(db, StepType.Agent, AgentType.DirectorAgent);
        WorkflowStep loop = AddStep(db, workflow, 2, StepType.ReviewLoop, AgentType.VideoReviewAgent, loopTarget: 1, maxIterations: 3);
        for (int i = 0; i < 3; i++)
        {
            // Each run's results reference its own ExecutionSnapshot's copies of the steps.
            SeedSnapshotRun(db, org, workflow, (body, [1_000]), (loop, [500]));
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(org, [body, loop], CancellationToken.None);

        estimate.Steps.Should().OnlyContain(s => s.ExpectedRuns == 1.0, "three snapshotted runs each passed review on the first pass");
    }

    [Fact]
    public async Task A_snapshotted_run_total_leaves_out_its_fixed_price_step_like_a_legacy_run()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep body) = SeedWorkflow(db, StepType.Agent, AgentType.DirectorAgent);
        WorkflowStep video = AddStep(db, workflow, 2, StepType.VideoGenerate, AgentType.DirectorAgent);
        foreach (long total in new long[] { 10_000, 20_000, 30_000, 40_000, 50_000 })
        {
            SeedSnapshotRun(db, org, workflow, (body, [total]), (video, [7_000]));
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(org, [body, video], CancellationToken.None);

        estimate.RangeBasis.Should().Be("PastRuns");
        estimate.PastRunCount.Should().Be(5);
        estimate.LowMillis.Should().Be(20_000, "the VideoGenerate step is shown as 'up to', never inside the past-runs range");
        estimate.HighMillis.Should().Be(40_000);
    }

    [Fact]
    public async Task A_superseded_result_is_not_a_review_pass()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep body) = SeedWorkflow(db, StepType.Agent, AgentType.DirectorAgent);
        WorkflowStep loop = AddStep(db, workflow, 2, StepType.ReviewLoop, AgentType.VideoReviewAgent, loopTarget: 1, maxIterations: 3);
        for (int i = 0; i < 3; i++)
        {
            // A retried run: the first attempt's loop result is kept for audit, stamped superseded.
            SeedSnapshotRun(db, org, workflow, (body, [1_000]), (loop, [500, 500]));
        }
        await db.SaveChangesAsync();
        foreach (WorkflowStepResult first in db.WorkflowStepResults.Local
            .Where(r => r.Status == StepStatus.Completed && r.ExecutedAt == Past.AddMinutes(-1)))
        {
            first.SupersededAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(org, [body, loop], CancellationToken.None);

        estimate.Steps.Should().OnlyContain(s => s.ExpectedRuns == 1.0);
    }

    [Fact]
    public void An_undeclared_video_generation_step_is_shown_as_up_to_the_max_clips_ceiling()
    {
        WorkflowStep[] steps = [Step(1, StepType.Agent), Step(2, StepType.VideoGenerate)];
        long ceiling = StepUsageProfiles.SeedFallbackMillis(UsageKind.VideoGenSeconds, 60m);

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom(
            [], steps, null, DisplayCreditInputs.Empty, SeedProfilePricer.Instance);
        WorkflowCostEstimate agentOnly = WorkflowCostEstimator.EstimateFrom(
            [], [steps[0]], null, DisplayCreditInputs.Empty, SeedProfilePricer.Instance);

        WorkflowStepCostEstimate video = estimate.Steps[1];
        video.FixedMaxMillis.Should().Be(ceiling).And.BePositive();
        video.TypicalMillis.Should().Be(0);
        video.LowMillis.Should().Be(0);
        video.HighMillis.Should().Be(0);
        estimate.FixedMaxMillis.Should().Be(ceiling);
        estimate.TypicalMillis.Should().Be(agentOnly.TypicalMillis);
        estimate.LowMillis.Should().Be(agentOnly.LowMillis);
        estimate.HighMillis.Should().Be(agentOnly.HighMillis);
        estimate.SpreadReasons.Should().Contain(CreditSpreadReasons.VideoGenerationMaxClips)
            .And.NotContain(CreditSpreadReasons.VideoGenerationInReviewLoop);
        // The hold is B0's per-step-type default, exactly as before.
        estimate.Steps[1].HoldMillis.Should().Be(StepCreditDefaults.MillisFor(StepType.VideoGenerate).Low);
    }

    [Fact]
    public void An_undeclared_video_generation_step_in_a_review_loop_reaches_its_ceiling_max_iterations_times()
    {
        WorkflowStep video = Step(1, StepType.VideoGenerate);
        WorkflowStep loop = Step(2, StepType.ReviewLoop);
        loop.LoopTargetStepOrder = 1;
        loop.MaxIterations = 3;
        long ceiling = StepUsageProfiles.SeedFallbackMillis(UsageKind.VideoGenSeconds, 60m);

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom(
            [], [video, loop], null, DisplayCreditInputs.Empty, SeedProfilePricer.Instance);

        estimate.FixedMaxMillis.Should().Be(3 * ceiling);
        estimate.Steps[0].FixedMaxMillis.Should().Be(ceiling);
        estimate.SpreadReasons.Should().Contain(CreditSpreadReasons.VideoGenerationMaxClips)
            .And.Contain(CreditSpreadReasons.VideoGenerationInReviewLoop);
    }

    [Fact]
    public void Video_generation_history_never_moves_it_into_the_range()
    {
        Guid agent = Guid.NewGuid();
        WorkflowStep video = Step(1, StepType.VideoGenerate, agent);
        DisplayCreditInputs display = DisplayCreditInputs.Empty with
        {
            StepSamples = [.. new long[] { 100_000, 200_000, 300_000 }.Select(c => new DisplayStepSample(StepType.VideoGenerate, null, agent, c))]
        };

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom([], [video], null, display, SeedProfilePricer.Instance);

        estimate.Steps.Single().FixedMaxMillis.Should().BePositive();
        estimate.TypicalMillis.Should().Be(0);
        estimate.HighMillis.Should().Be(0);
    }

    [Fact]
    public async Task The_video_generation_ceiling_is_priced_with_the_resolved_provider()
    {
        using var db = NewApiContext();
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedVideoGenerationProvider(Guid.NewGuid(), "video", InferenceProviderKind.MiniMax, "https://x",
                "default", "key", 60));
        var rates = new Mock<IModelPriceService>();
        rates.Setup(r => r.PriceAsync(
                It.Is<PriceRequest>(q => q.Unit == UsageKind.VideoGenSeconds && q.Kind == InferenceProviderKind.MiniMax
                    && q.Capability == InferenceProviderCapability.VideoGeneration && q.ModelCode == "default"
                    && q.BillingMode == UsageBillingMode.Platform && q.Quantity == 60m),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PriceQuote(123_456, 0m, Guid.Empty));

        WorkflowCostEstimate estimate = await ApiEstimator(db, resolver.Object, rates.Object).EstimateAsync(
            Guid.NewGuid(), [Step(1, StepType.VideoGenerate)], CancellationToken.None);

        estimate.FixedMaxMillis.Should().Be(123_456);
        estimate.Steps.Single().FixedMaxMillis.Should().Be(123_456);
        estimate.RequiredMillis.Should().Be(StepCreditDefaults.MillisFor(StepType.VideoGenerate).Low);
    }

    [Fact]
    public async Task An_unresolvable_video_generation_provider_prices_the_ceiling_at_the_seed_constants()
    {
        using var db = NewApiContext();
        var resolver = new Mock<IInferenceProviderResolver>();
        resolver.Setup(r => r.ResolveVideoGenerationAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no video provider"));

        WorkflowCostEstimate estimate = await ApiEstimator(db, resolver.Object, SeedRates()).EstimateAsync(
            Guid.NewGuid(), [Step(1, StepType.VideoGenerate)], CancellationToken.None);

        estimate.FixedMaxMillis.Should().Be(StepUsageProfiles.SeedFallbackMillis(UsageKind.VideoGenSeconds, 60m)).And.BePositive();
    }

    [Fact]
    public async Task Past_run_totals_leave_out_an_undeclared_video_generation_step()
    {
        using var db = NewApiContext();
        Guid org = Guid.NewGuid();
        (WorkflowDefinition workflow, WorkflowStep step) = SeedWorkflow(db, StepType.Agent, AgentType.DirectorAgent);
        WorkflowStep video = AddStep(db, workflow, 2, StepType.VideoGenerate, AgentType.Custom);
        foreach (long total in new long[] { 10_000, 20_000, 30_000, 40_000, 50_000 })
        {
            SeedRun(db, org, workflow, (step, [total]), (video, [500_000]));
        }
        await db.SaveChangesAsync();

        // No video-generation client factory here, so the step has no declared price: it is still fixed-price.
        WorkflowCostEstimate estimate = await ApiEstimator(db).EstimateAsync(org, [step, video], CancellationToken.None);

        estimate.RangeBasis.Should().Be("PastRuns");
        estimate.TypicalMillis.Should().Be(30_000);
        estimate.LowMillis.Should().Be(20_000);
        estimate.HighMillis.Should().Be(40_000);
        estimate.FixedMaxMillis.Should().BePositive();
    }

    [Fact]
    public void Video_story_editor_uses_its_measured_factors_now_that_staging_has_three_samples()
    {
        // Staging p25/p50/p75 3.4/6.8/6.8: 0.50 and 1.00, the top floored to 1.33.
        StepUsageProfile profile = StepUsageProfiles.ForAgent(AgentType.VideoStoryEditor);

        profile.LowFactor.Should().Be(0.50);
        profile.HighFactor.Should().Be(StepUsageProfiles.MinHighFactor);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static WorkflowCostEstimator ApiEstimator(
        InferenceApiDbContext db, IInferenceProviderResolver resolver, IModelPriceService rates)
        => new(new ServiceScopes(db, resolver, rates), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = true }),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);

    private static IModelPriceService SeedRates() => LegacyCardPrices.Seed();

    private static (WorkflowDefinition Workflow, WorkflowStep Step) SeedWorkflow(
        InferenceApiDbContext db, StepType type, AgentType agentType)
    {
        WorkflowDefinition workflow = new()
        {
            Id = Guid.NewGuid(),
            Name = "w",
            ProjectId = Guid.NewGuid(),
            UpdatedAt = Past.AddDays(-1)
        };
        db.WorkflowDefinitions.Add(workflow);
        return (workflow, AddStep(db, workflow, 1, type, agentType));
    }

    private static WorkflowStep AddStep(
        InferenceApiDbContext db, WorkflowDefinition workflow, int order, StepType type, AgentType agentType,
        int? loopTarget = null, int maxIterations = 3)
    {
        AgentDefinition agent = new() { Id = Guid.NewGuid(), Name = agentType.ToString(), AgentType = agentType };
        db.AgentDefinitions.Add(agent);
        WorkflowStep step = new()
        {
            Id = Guid.NewGuid(),
            WorkflowDefinitionId = workflow.Id,
            StepOrder = order,
            StepType = type,
            AgentDefinitionId = agent.Id,
            LoopTargetStepOrder = loopTarget,
            MaxIterations = maxIterations
        };
        db.WorkflowSteps.Add(step);
        return step;
    }

    /// <summary>One Passed execution of <paramref name="workflow"/>: a charged step result per credit figure given.</summary>
    private static void SeedRun(InferenceApiDbContext db, Guid org, WorkflowDefinition workflow, params (WorkflowStep Step, long[] Credits)[] results)
    {
        Guid executionId = Guid.NewGuid();
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = workflow.Id,
            ProjectId = workflow.ProjectId,
            OrganizationId = org,
            Status = ExecutionStatus.Passed,
            QueuedAt = Past,
            StartedAt = Past
        });

        foreach ((WorkflowStep step, long[] credits) in results)
        {
            foreach (long value in credits)
            {
                Guid resultId = Guid.NewGuid();
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = resultId,
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = step.Id,
                    Status = StepStatus.Completed,
                    ExecutedAt = Past
                });
                db.UsageEvents.Add(Usage(org, workflow.ProjectId, executionId, resultId, value));
            }
        }
    }

    /// <summary>
    /// One Passed execution of <paramref name="workflow"/> run from its own ExecutionSnapshot, as the
    /// engine runs every Standard workflow: the steps cloned into a kind-5 definition, every result on a
    /// clone. A second credit figure for the same step is an earlier attempt (one minute older).
    /// </summary>
    private static void SeedSnapshotRun(InferenceApiDbContext db, Guid org, WorkflowDefinition workflow, params (WorkflowStep Step, long[] Credits)[] results)
    {
        Guid executionId = Guid.NewGuid();
        WorkflowDefinition snapshot = new()
        {
            Id = Guid.NewGuid(),
            Name = workflow.Name + " (run snapshot)",
            ProjectId = workflow.ProjectId,
            Kind = WorkflowDefinitionKind.ExecutionSnapshot,
            UpdatedAt = Past
        };
        db.WorkflowDefinitions.Add(snapshot);
        db.WorkflowExecutions.Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = workflow.Id,
            SnapshotDefinitionId = snapshot.Id,
            ProjectId = workflow.ProjectId,
            OrganizationId = org,
            Status = ExecutionStatus.Passed,
            QueuedAt = Past,
            StartedAt = Past
        });

        foreach ((WorkflowStep step, long[] credits) in results)
        {
            WorkflowStep clone = ReelBolt.Shared.Workflows.Replay.WorkflowStepCloner.Clone(step, snapshot.Id);
            db.WorkflowSteps.Add(clone);
            for (int i = 0; i < credits.Length; i++)
            {
                Guid resultId = Guid.NewGuid();
                db.WorkflowStepResults.Add(new WorkflowStepResult
                {
                    Id = resultId,
                    WorkflowExecutionId = executionId,
                    WorkflowStepId = clone.Id,
                    Status = StepStatus.Completed,
                    ExecutedAt = credits.Length > 1 && i == 0 ? Past.AddMinutes(-1) : Past
                });
                db.UsageEvents.Add(Usage(org, workflow.ProjectId, executionId, resultId, credits[i]));
            }
        }
    }

    /// <summary>A scope factory serving the seeded context plus a provider resolver and a price service.</summary>
    private sealed class ServiceScopes(InferenceApiDbContext db, IInferenceProviderResolver resolver, IModelPriceService rates)
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object GetService(Type serviceType) =>
            serviceType == typeof(InferenceApiDbContext) ? db
            : serviceType == typeof(IInferenceProviderResolver) ? resolver
            : serviceType == typeof(IModelPriceService) ? rates
            : null;

        public void Dispose()
        {
        }
    }

    private static WorkflowStep Step(int order, StepType type, Guid? agent = null) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = type,
        AgentDefinitionId = agent ?? Guid.NewGuid()
    };

    /// <summary>A <see cref="StepType.VideoAnalyze"/> step carrying <paramref name="configJson"/> as its persisted config.</summary>
    private static WorkflowStep AnalyzeStep(Guid agent, string configJson)
    {
        WorkflowStep step = Step(1, StepType.VideoAnalyze, agent);
        step.VideoAnalyzeConfigJson = configJson;
        return step;
    }

    private static InferenceApiDbContext NewApiContext() => new(
        new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static WorkflowCostEstimator ApiEstimator(InferenceApiDbContext db, bool enabled = true)
        => new(new SingleContextScopes(db), Options.Create(new ReelBolt.Shared.Billing.BillingOptions { Enabled = enabled }),
            Options.Create(new StepCreditEstimateOptions()),
            NullLogger<WorkflowCostEstimator>.Instance);

    /// <summary>
    /// Seeds <paramref name="credits"/> as one completed step result per sample, each carrying a single
    /// <c>usage_events</c> row, plus <paramref name="cachedCredits"/> cache-hit results. Written
    /// against <see cref="DbContext"/> so both services' contexts can be seeded with the same shape.
    /// </summary>
    private static void SeedHistory(
        DbContext db, Guid org, StepType type, Guid agent, long[] credits, long[]? cachedCredits = null)
    {
        Guid definitionId = Guid.NewGuid();
        Guid projectId = Guid.NewGuid();
        Guid executionId = Guid.NewGuid();
        Guid stepId = Guid.NewGuid();

        db.Set<WorkflowDefinition>().Add(new WorkflowDefinition { Id = definitionId, Name = "w", ProjectId = projectId });
        db.Set<WorkflowExecution>().Add(new WorkflowExecution
        {
            Id = executionId,
            WorkflowDefinitionId = definitionId,
            ProjectId = projectId,
            OrganizationId = org,
            Status = ExecutionStatus.Passed,
            QueuedAt = Past
        });
        db.Set<WorkflowStep>().Add(new WorkflowStep
        {
            Id = stepId,
            WorkflowDefinitionId = definitionId,
            StepOrder = 1,
            StepType = type,
            AgentDefinitionId = agent
        });

        foreach (long value in credits)
        {
            Guid resultId = Guid.NewGuid();
            db.Set<WorkflowStepResult>().Add(new WorkflowStepResult
            {
                Id = resultId,
                WorkflowExecutionId = executionId,
                WorkflowStepId = stepId,
                Status = StepStatus.Completed,
                ExecutedAt = Past
            });
            db.Set<UsageEvent>().Add(Usage(org, projectId, executionId, resultId, value));
        }

        foreach (long value in cachedCredits ?? [])
        {
            Guid resultId = Guid.NewGuid();
            db.Set<WorkflowStepResult>().Add(new WorkflowStepResult
            {
                Id = resultId,
                WorkflowExecutionId = executionId,
                WorkflowStepId = stepId,
                Status = StepStatus.Completed,
                FromCache = true,
                ExecutedAt = Past
            });
            db.Set<UsageEvent>().Add(Usage(org, projectId, executionId, resultId, value));
        }
    }

    private static UsageEvent Usage(Guid org, Guid projectId, Guid executionId, Guid stepResultId, long credits) => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = org,
        ProjectId = projectId,
        ExecutionId = executionId,
        StepResultId = stepResultId,
        Source = UsageSource.Workflow,
        Kind = UsageKind.LlmInputTokens,
        Quantity = 1,
        ProviderKind = InferenceProviderKind.OpenAICompatible,
        ModelName = "test",
        CreditsMillis = credits,
        IdempotencyKey = $"{stepResultId:N}:0",
        OccurredAt = Past
    };

    /// <summary>A scope factory handing every caller the one context the test seeded.</summary>
    private sealed class SingleContextScopes(InferenceApiDbContext db) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new FixedScope(db);

        private sealed class FixedScope(InferenceApiDbContext db) : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FixedProvider(db);
            public void Dispose()
            {
            }
        }

        private sealed class FixedProvider(InferenceApiDbContext db) : IServiceProvider
        {
            public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? db : null;
        }
    }

    /// <summary>An engine fixture whose history lives in an in-memory <see cref="WorkflowEngineDbContext"/>.</summary>
    private sealed class EngineFixture : IDisposable
    {
        private readonly DbContextOptions<WorkflowEngineDbContext> _options =
            new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

        public EngineFixture()
        {
            Estimator = new UsageHistoryCreditHoldEstimator(
                new Factory(_options),
                Options.Create(new StepCreditEstimateOptions()),
                NullLogger<UsageHistoryCreditHoldEstimator>.Instance);
        }

        public Guid OrgId { get; } = Guid.NewGuid();

        public UsageHistoryCreditHoldEstimator Estimator { get; }

        /// <summary>Seeds one completed step result per sample, returns the agent they ran under.</summary>
        public async Task<Guid> SeedHistoryAsync(StepType type, long[] credits, long[]? cachedCredits = null)
        {
            Guid agent = Guid.NewGuid();
            await using WorkflowEngineDbContext db = new(_options);
            SeedHistory(db, OrgId, type, agent, credits, cachedCredits);
            await db.SaveChangesAsync();
            return agent;
        }

        public void Dispose()
        {
            // The in-memory provider holds no unmanaged state; the contexts are all disposed.
        }

        private sealed class Factory(DbContextOptions<WorkflowEngineDbContext> options)
            : IDbContextFactory<WorkflowEngineDbContext>
        {
            public WorkflowEngineDbContext CreateDbContext() => new(options);
        }
    }

    private sealed class ThrowingFactory : IDbContextFactory<WorkflowEngineDbContext>
    {
        public WorkflowEngineDbContext CreateDbContext() => throw new InvalidOperationException("no database");
    }
}
