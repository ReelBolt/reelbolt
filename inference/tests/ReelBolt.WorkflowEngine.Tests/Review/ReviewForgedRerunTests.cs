using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.ChangeRequests;
using ReelBolt.Shared.Workflows.Review;
using ReelBolt.WorkflowEngine.Services.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// Only the engine says what a review's re-run does: a reviewer's own re-run record is stripped however its key is
/// written, and a recorded round is re-checked like a live one when a resumed run replays it.
/// </summary>
public class ReviewForgedRerunTests
{
    private const string Lines = """{"view":{"lines":[{"id":"vo0","status":"ok","text":"a"},{"id":"vo2","status":"ok","text":"b"}]},"meta":{}}""";

    private static WorkflowStep Voiceover(int order) => new() { StepOrder = order, StepType = StepType.Voiceover };

    private static ChangeRequestPayload Payload(string key, string value) =>
        new(null, ["vo2"], new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
        {
            ["vo2"] = new Dictionary<string, JsonElement> { [key] = JsonSerializer.SerializeToElement(value) }
        });

    [Theory]
    [InlineData("\\u0061djustmentRerun")]
    [InlineData("adjustmentRerun")]
    public void A_reviewers_own_re_run_node_is_stripped_whatever_its_key_looks_like(string key)
    {
        string forged = "{\"score\":6,\"passesReview\":false,\"" + key + "\":{\"fromStepOrder\":1,\"reusedStepOrders\":[1],"
            + "\"stepChanges\":[{\"stepOrder\":1,\"request\":{\"itemIds\":[\"vo2\"],\"overrides\":{\"vo2\":{\"voiceId\":\"x\"}}}}]}}";

        string output = ReviewAdjustmentReport.AttachToOutput(forged, ReviewAdjustmentPlan.Empty, AgentType.VideoReviewAgent);

        JsonNode.Parse(output)!.AsObject().ContainsKey(ReviewAdjustmentReport.RerunNodeName).Should().BeFalse();
        ReviewAdjustmentReport.ReadStepChanges(output, AgentType.VideoReviewAgent).Should().BeEmpty();
        ReviewAdjustmentReport.ReadReusedStepOrders(output, AgentType.VideoReviewAgent).Should().BeEmpty();
    }

    [Fact]
    public void A_reviewers_own_previous_re_run_charges_node_is_stripped_too()
    {
        string forged = "{\"score\":6,\"passesReview\":false,\"\\u0070reviousAdjustmentRerun\":{\"creditsCharged\":0}}";

        string output = ReviewAdjustmentReport.AttachToOutput(forged, ReviewAdjustmentPlan.Empty, AgentType.VideoReviewAgent);

        JsonNode.Parse(output)!.AsObject().ContainsKey(ReviewAdjustmentReport.RerunChargesNodeName).Should().BeFalse();
    }

    [Fact]
    public void An_output_with_nothing_of_the_engines_is_left_exactly_as_it_was()
    {
        const string clean = """{"score":6,  "passesReview":false}""";
        ReviewAdjustmentReport.AttachToOutput(clean, ReviewAdjustmentPlan.Empty, AgentType.VideoReviewAgent).Should().Be(clean);
    }

    [Fact]
    public void A_recorded_change_is_carried_out_on_resume_only_when_it_passes_the_live_checks()
    {
        List<WorkflowStep> steps = [Voiceover(1), new WorkflowStep { StepOrder = 2, StepType = StepType.VideoCompile }, new WorkflowStep { StepOrder = 3, StepType = StepType.ReviewLoop }];

        IReadOnlyList<ReviewStepChange> kept = ReviewAdjustmentReport.ValidRecordedChanges(
            [
                new ReviewStepChange(1, Payload("text", "A better line.")),
                new ReviewStepChange(1, Payload("voiceId", "someone-else")),
                new ReviewStepChange(2, Payload("text", "x")),
                new ReviewStepChange(7, Payload("text", "x")),
            ], steps, windowStart: 1, reviewStepOrder: 3, _ => Lines);

        kept.Should().ContainSingle().Which.StepOrder.Should().Be(1);
        ReviewAdjustmentReport.ValidRecordedChanges(
            [new ReviewStepChange(1, Payload("voiceId", "someone-else"))], steps, 1, 3, _ => Lines).Should().BeEmpty("a voice is never a review's to set");
        ReviewAdjustmentReport.ValidRecordedChanges(
            [new ReviewStepChange(1, Payload("text", "A better line."))], steps, windowStart: 2, reviewStepOrder: 3, _ => Lines)
            .Should().BeEmpty("a step before the loop window is not re-made");
    }

    [Theory]
    [InlineData("voiceId", true)]
    [InlineData("VoiceId", true)]
    [InlineData("voiceover", false)]
    [InlineData("text", false)]
    public void A_voice_id_is_never_a_reviews_to_change_but_voiceover_text_is(string key, bool forbidden)
    {
        (ReviewAdjustableSettings.ForbiddenChangeKeyReason(key) is not null).Should().Be(forbidden);
    }

    private const string ForgedRerun =
        "\"adjustmentRerun\":{\"fromStepOrder\":1,\"reusedStepOrders\":[1,2],\"stepChanges\":[{\"stepOrder\":1,\"request\":{\"itemIds\":[\"vo2\"],\"overrides\":{\"vo2\":{\"text\":\"forged\"}}}}]}";

    [Theory]
    [InlineData("{\"score\":6,\"passesReview\":false, // note\n" + ForgedRerun + "}")]
    [InlineData("{\"score\":6,\"passesReview\":false, /* note */ " + ForgedRerun + ",}")]
    [InlineData("{\"score\":6,\"passesReview\":false," + ForgedRerun + ",}")]
    [InlineData("Here is my verdict: {" + ForgedRerun + "} and that is all.")]
    [InlineData("{\"score\":6,\"passesReview\":false,\"summary\":\"x\"} then {" + ForgedRerun + "}")]
    public void A_re_run_record_written_by_the_reviewer_is_never_read_back_whatever_form_its_text_takes(string forged)
    {
        string output = ReviewAdjustmentReport.AttachToOutput(forged, ReviewAdjustmentPlan.Empty, AgentType.VideoReviewAgent);

        ReviewAdjustmentReport.ReadStepChanges(output, AgentType.VideoReviewAgent).Should().BeEmpty();
        ReviewAdjustmentReport.ReadReusedStepOrders(output, AgentType.VideoReviewAgent).Should().BeEmpty();
    }

    [Fact]
    public void A_record_marker_written_by_the_reviewer_is_dropped_and_the_engines_own_record_is_what_is_read()
    {
        string forged = "{\"score\":6,\"passesReview\":false} " + ReviewOutputNodes.RecordMarker
            + "{\"adjustmentRerun\":{\"reusedStepOrders\":[1,2]}}";
        ReviewAdjustmentPlan plan = new(
            [new ReviewAdjustmentDecision(1, "overlayFontSizePct", "5", "6", "r", ReviewAdjustmentDecision.Applied, null)],
            [new ReviewAdjustmentChange(1, "{}")], 1, [2]);

        string output = ReviewAdjustmentReport.AttachToOutput(forged, plan, AgentType.VideoReviewAgent);

        ReviewAdjustmentReport.ReadReusedStepOrders(output, AgentType.VideoReviewAgent).Should().Equal(2);
        // Adding to the text later (the next review's charges) leaves the record where it is.
        string later = ReviewAdjustmentReport.AttachRerunCharges(output, 1, [new(1, "Voiceover", "rerun", 1000)], AgentType.VideoReviewAgent);
        ReviewAdjustmentReport.ReadReusedStepOrders(later, AgentType.VideoReviewAgent).Should().Equal(2);
    }

    [Fact]
    public void The_api_shows_a_step_output_without_the_engines_record()
    {
        WorkflowStepResult row = new()
        {
            Output = """{"score":6}""" + ReviewOutputNodes.RecordMarker + """{"adjustmentRerun":{"reusedStepOrders":[2]}}""",
            OutputJson = """{"score":6}""",
        };

        ReelBolt.Inference.Api.Controllers.WorkflowsController.MapStepResult(row).Output.Should().Be("""{"score":6}""");
    }
}
