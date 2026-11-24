using AwesomeAssertions;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-206: a review loop cannot pass a compile that was asked to put content on tracked screens and put none there,
/// and the brief back to the editor says which regions were dropped and why.
/// </summary>
public sealed class InsertsNotAppliedCapTests
{
    private const string NotApplied = """
        {"status":"completed","inserts":{"enabled":true,"applied":false,"appliedInsertCount":0,"requested":2,
         "droppedInserts":[{"regionId":"r0","reason":"confidence_below_threshold"},{"regionId":"r1","reason":"content_not_found"}]}}
        """;

    [Fact]
    public void Requested_and_not_applied_caps_the_score_below_the_pass_mark_and_the_early_stop_cannot_skip_it()
    {
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, NotApplied).Should().Be(ReviewLoopStepExecutor.InsertsNotAppliedScoreCap);
        ReviewLoopStepExecutor.InsertsNotAppliedScoreCap.Should().BeLessThan(8, "every video template's MinScore is 8");
        ReviewLoopStepExecutor.DeterministicCapsWouldFire(NotApplied).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"inserts":{"enabled":true,"applied":true,"requested":2,"droppedInserts":[{"regionId":"r1","reason":"cut_away"}]}}""")]
    [InlineData("""{"inserts":{"enabled":true,"applied":false,"requested":0,"reason":"No GraphicsPlan configured"}}""")]
    [InlineData("""{"status":"completed"}""")]
    [InlineData("""{"inserts":{"applied":"false","requested":"2"}}""")]
    [InlineData("not json")]
    public void It_is_inert_for_every_other_shape(string compile) =>
        ReviewLoopStepExecutor.ApplyDeterministicCaps(9, compile).Should().Be(9);

    [Fact]
    public void A_cap_never_raises_a_score() =>
        ReviewLoopStepExecutor.ApplyDeterministicCaps(3, NotApplied).Should().Be(3);

    [Fact]
    public void The_feedback_names_each_region_that_was_not_applied_and_why()
    {
        const string review = """{"overallScore":9,"summary":"Looks great.","criteria":[]}""";

        ReviewLoopStepExecutor.CappedReviewScore scored = ReviewLoopStepExecutor.ScoreReview(review, NotApplied);
        string? brief = ReviewLoopStepExecutor.ExtractFeedbackSummary(review, 8, scored);

        scored.Score.Should().Be(6);
        scored.CapReason.Should().Be("screen inserts not applied (r0: confidence_below_threshold; r1: content_not_found)");
        brief.Should().Contain("capped from 9: screen inserts not applied (r0: confidence_below_threshold; r1: content_not_found)");
    }

    [Fact]
    public void When_the_interleave_cap_fires_too_the_reason_still_names_the_regions()
    {
        // Both caps have the same ceiling, so both fire on one compile: the inserts reason is the one
        // that names the regions and their drop reasons, and it must survive (QA-P2-9).
        const string both = """
            {"status":"completed","interleave":{"required":true,"satisfied":false},"inserts":{"enabled":true,
             "applied":false,"appliedInsertCount":0,"requested":2,
             "droppedInserts":[{"regionId":"r0","reason":"confidence_below_threshold"},{"regionId":"r1","reason":"content_not_found"}]}}
            """;
        const string review = """{"overallScore":9,"summary":"Looks great.","criteria":[]}""";

        ReviewLoopStepExecutor.ApplyInsertsNotAppliedCap(9, both).Should().Be(ReviewLoopStepExecutor.InsertsNotAppliedScoreCap);
        ReviewLoopStepExecutor.ApplyInterleaveCap(9, both).Should().Be(ReviewLoopStepExecutor.InterleaveScoreCap);

        ReviewLoopStepExecutor.CappedReviewScore scored = ReviewLoopStepExecutor.ScoreReview(review, both);
        string? brief = ReviewLoopStepExecutor.ExtractFeedbackSummary(review, 8, scored);

        scored.Score.Should().Be(6);
        scored.CapReason.Should().Be("screen inserts not applied (r0: confidence_below_threshold; r1: content_not_found)");
        brief.Should().Contain("r0: confidence_below_threshold").And.Contain("r1: content_not_found");
    }
}
