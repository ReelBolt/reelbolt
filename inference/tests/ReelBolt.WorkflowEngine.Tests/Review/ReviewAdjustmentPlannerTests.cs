using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// The planner behind review-loop adjustments (docs/design/review-loop-adjustments.md): which proposals apply, which are
/// refused and why, where the loop re-runs from (paid steps included), and the change requests a review may ask for.
/// </summary>
public class ReviewAdjustmentPlannerTests
{
    private const string FreeAnalyze = """{"transcription":"Off","vision":"Off"}""";
    private const string PaidAnalyze = """{"transcription":"Optional"}""";
    private const string Compile = """{"version":1,"enableGraphics":true,"enableSfx":true,"overlayFontSizePct":5,"maxSfxCueSeconds":4.0,"targetDurationSec":30}""";

    /// <summary>AudioGenerate(1) -> VideoAnalyze(2) -> EditRoom(3, loop target) -> VideoCompile(4) -> Review(5): the Caffè Lume shape.</summary>
    private static List<WorkflowStep> Lume(string analyze = FreeAnalyze) =>
    [
        Step(1, StepType.AudioGenerate),
        Step(2, StepType.VideoAnalyze, analyze: analyze),
        Step(3, StepType.EditRoom),
        Step(4, StepType.VideoCompile, compile: Compile),
        Step(5, StepType.ReviewLoop, loopTarget: 3),
    ];

    private static ReviewAdjustmentRequest Request(List<WorkflowStep> steps, bool loopsBack = true, bool snapshot = true, int? brief = null) =>
        new(steps, steps.Single(s => s.StepType == StepType.ReviewLoop), snapshot, loopsBack, brief);

    private static ReviewAdjustmentProposal P(int step, string setting, object value, string reason = "because") =>
        new(step, setting, JsonValue.Create(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)), reason);

    [Fact]
    public void An_in_range_compile_change_applies_with_from_and_to_and_needs_no_wider_rerun()
    {
        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Lume()), [P(4, "overlayFontSizePct", 8, "title is 2% high")]);

        plan.Decisions.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            StepOrder = 4,
            Setting = "overlayFontSizePct",
            From = "5",
            To = "8",
            Status = "applied",
            Reason = "title is 2% high"
        });
        plan.Changes.Should().ContainSingle().Which.StepOrder.Should().Be(4);
        JsonNode.Parse(plan.Changes[0].ConfigJson)!["overlayFontSizePct"]!.GetValue<int>().Should().Be(8);
        plan.RerunFromStepOrder.Should().BeNull("the compile runs after the loop target anyway");
    }

    [Theory]
    [InlineData(4, "overlayFontSizePct", "40", "between")]
    [InlineData(4, "allowSourceMediaEgress", "true", "never adjustable")]
    [InlineData(4, "maxSpendCredits", "1", "never adjustable")]
    [InlineData(4, "videoCodec", "libx265", "not on the list")]
    [InlineData(9, "overlayFontSizePct", "8", "no such step")]
    [InlineData(1, "prompt", "louder drums", "prompts are written by the user")]
    [InlineData(1, "maxSpendCredits", "100", "never adjustable")]
    [InlineData(1, "takes", "4", "never adjustable")]
    [InlineData(5, "maxIterations", "9", "after the review")]
    [InlineData(4, "overlayFontSizePct", "5", "already has that value")]
    public void Bad_proposals_are_refused_with_a_reason_and_change_nothing(int step, string setting, string value, string why)
    {
        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Lume()), [P(step, setting, value)]);

        plan.Decisions.Should().ContainSingle().Which.Status.Should().Be("refused");
        plan.Decisions[0].Why.Should().Contain(why);
        plan.HasChanges.Should().BeFalse();
    }

    [Fact]
    public void Nothing_applies_when_the_loop_is_not_going_round_again_or_the_run_has_no_snapshot_of_its_own()
    {
        ReviewAdjustmentPlanner.Plan(Request(Lume(), loopsBack: false), [P(4, "overlayFontSizePct", 8)]).HasChanges.Should().BeFalse();
        ReviewAdjustmentPlan noSnapshot = ReviewAdjustmentPlanner.Plan(Request(Lume(), snapshot: false), [P(4, "overlayFontSizePct", 8)]);
        noSnapshot.HasChanges.Should().BeFalse();
        noSnapshot.Decisions[0].Why.Should().Contain("copy of its own workflow");
    }

    [Fact]
    public void An_upstream_analysis_change_widens_the_rerun_to_that_step_and_reuses_the_paid_step_before_it_is_never_touched()
    {
        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Lume()),
            [P(2, "maxOutputChars", 60000, "placements were dropped for budget"), P(4, "maxSfxCueSeconds", 1.5)]);

        plan.Decisions.Should().OnlyContain(d => d.IsApplied);
        plan.RerunFromStepOrder.Should().Be(2);
        plan.ReusedStepOrders.Should().BeEmpty("the AudioGenerate step (1) sits before the rerun and is never re-entered");
    }

    [Fact]
    public void A_transcribing_analysis_may_now_be_re_run_by_a_review_for_its_budget()
    {
        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Lume(PaidAnalyze)), [P(2, "maxOutputChars", 60000)]);

        plan.Decisions.Should().ContainSingle().Which.IsApplied.Should().BeTrue("a paid step may be re-run; it is held and charged normally");
        plan.RerunFromStepOrder.Should().Be(2);
    }

    [Fact]
    public void A_paid_step_between_the_change_and_the_loop_target_that_reads_it_re_runs_with_it()
    {
        // VideoAnalyze(1) -> EditRoom(2, reads the analysis, paid) -> GraphicsRoom(3, loop target) -> Compile(4) -> Review(5)
        List<WorkflowStep> steps =
        [
            Step(1, StepType.VideoAnalyze, analyze: FreeAnalyze),
            Step(2, StepType.EditRoom),
            Step(3, StepType.GraphicsRoom),
            Step(4, StepType.VideoCompile, compile: Compile),
            Step(5, StepType.ReviewLoop, loopTarget: 3),
        ];

        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(steps), [P(1, "maxOutputChars", 60000)]);

        plan.Decisions.Should().ContainSingle().Which.IsApplied.Should().BeTrue();
        plan.RerunFromStepOrder.Should().Be(1);
        plan.ReusedStepOrders.Should().BeEmpty("the room reads the analysis, so it runs again (and is charged again)");
    }

    [Fact]
    public void A_paid_step_in_the_widened_stretch_that_does_not_read_the_change_is_reused_not_re_run()
    {
        // VideoAnalyze(1) -> AudioGenerate(2, reads nothing) -> EditRoom(3, loop target) -> Compile(4) -> Review(5)
        List<WorkflowStep> steps =
        [
            Step(1, StepType.VideoAnalyze, analyze: FreeAnalyze),
            Step(2, StepType.AudioGenerate),
            Step(3, StepType.EditRoom),
            Step(4, StepType.VideoCompile, compile: Compile),
            Step(5, StepType.ReviewLoop, loopTarget: 3),
        ];

        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(steps), [P(1, "maxSubShotsPerShot", 4)]);

        plan.Decisions.Should().ContainSingle().Which.IsApplied.Should().BeTrue();
        plan.RerunFromStepOrder.Should().Be(1);
        plan.ReusedStepOrders.Should().Equal(2);
    }

    [Fact]
    public void A_target_length_moves_around_the_brief()
    {
        ReviewAdjustmentPlanner.Plan(Request(Lume(), brief: 20), [P(4, "targetDurationSec", 24)]).HasChanges.Should().BeTrue();
        ReviewAdjustmentPlanner.Plan(Request(Lume(), brief: 20), [P(4, "targetDurationSec", 30)]).HasChanges.Should().BeFalse();
    }

    [Fact]
    public void At_most_six_proposals_count_and_a_setting_is_changed_once()
    {
        List<ReviewAdjustmentProposal> many = [.. Enumerable.Range(0, 8).Select(i => P(4, "minSegmentMs", 400 + i * 100))];

        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(Lume()), many);

        plan.Decisions.Should().HaveCount(8);
        plan.Decisions.Count(d => d.IsApplied).Should().Be(1);
        plan.Decisions.Skip(1).Take(5).Should().OnlyContain(d => d.Why!.Contains("twice"));
        plan.Decisions.Skip(6).Should().OnlyContain(d => d.Why!.Contains("more than 6"));
    }

    [Fact]
    public void An_adjustment_never_changes_the_step_hold_estimate()
    {
        // Billing: the allowlisted settings never enter a step's cost profile, so an adjusted step holds what it held.
        List<WorkflowStep> steps = Lume();
        ReviewAdjustmentPlan plan = ReviewAdjustmentPlanner.Plan(Request(steps),
            [P(2, "maxOutputChars", 90000), P(2, "maxSubShotsPerShot", 2), P(4, "overlayFontSizePct", 9), P(4, "maxSfxCueSeconds", 1),
             P(4, "musicGainDb", -12), P(4, "outputFit", "Pad")]);
        plan.Decisions.Should().OnlyContain(d => d.IsApplied);

        foreach (ReviewAdjustmentChange change in plan.Changes)
        {
            WorkflowStep before = steps.Single(s => s.StepOrder == change.StepOrder);
            WorkflowStep after = Step(before.StepOrder, before.StepType);
            ReviewAdjustableSettings.SetConfigJson(after, change.ConfigJson);
            StepUsageProfiles.For(after, null).Should().BeEquivalentTo(StepUsageProfiles.For(before, null));
            StepCreditDefaults.IsComputeOnly(after).Should().Be(StepCreditDefaults.IsComputeOnly(before));
        }
    }

    [Fact]
    public void The_reviewer_reason_is_sanitized_and_capped()
    {
        string reason = ReviewAdjustmentPlanner.SanitizeReason("a\u0000b\u202E  c\n" + new string('x', 400));
        reason.Should().StartWith("ab c x");
        reason.Should().NotContain("\u0000").And.NotContain("\u202E");
        reason.Length.Should().BeLessThanOrEqualTo(ReviewAdjustableSettings.MaxReasonChars);
    }

    [Fact]
    public void Only_eligible_steps_are_offered()
    {
        // AudioGenerate (1) takes a change request, the analysis (2) and the compile (4) have settings; the room has neither.
        ReviewAdjustmentPlanner.AdjustableSteps(Request(Lume(PaidAnalyze))).Select(s => s.StepOrder).Should().Equal(1, 2, 4);
        ReviewAdjustmentPlanner.AdjustableSteps(Request(Lume())).Select(s => s.StepOrder).Should().Equal(1, 2, 4);
    }

    internal static WorkflowStep Step(int order, StepType type, string? analyze = null, string? compile = null, int? loopTarget = null) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = type,
        VideoAnalyzeConfigJson = analyze,
        VideoCompileConfigJson = compile,
        LoopTargetStepOrder = loopTarget,
        MaxIterations = 3,
        MinScore = 8,
        AgentDefinition = new AgentDefinition { Id = Guid.NewGuid(), Name = type.ToString(), AgentType = AgentType.VideoTransform },
    };
}
