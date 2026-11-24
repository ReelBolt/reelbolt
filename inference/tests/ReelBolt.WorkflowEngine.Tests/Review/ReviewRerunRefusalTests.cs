using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Billing;
using ReelBolt.WorkflowEngine.Services.Review;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Review;

/// <summary>
/// A paid step re-run inside a review loop that the platform refuses (a daily/step cap, the credit gate, the plan) stops the
/// loop and keeps the finished film: the run is never failed by it, and the review output says why.
/// </summary>
public class ReviewRerunRefusalTests
{
    [Theory]
    [InlineData("BUDGET_EXCEEDED")]
    [InlineData("API_KEY_BUDGET_EXCEEDED")]
    [InlineData("PLAN_NOT_ENTITLED")]
    [InlineData("INSUFFICIENT_CREDITS")]
    [InlineData("COMPUTE_NOT_AVAILABLE")]
    public void A_refused_paid_rerun_code_stops_the_loop(string code) =>
        ReviewAdjustmentReport.IsRefusedPaidRerun(null, code).Should().BeTrue();

    [Fact]
    public void A_credit_gate_refusal_exception_stops_the_loop() =>
        ReviewAdjustmentReport.IsRefusedPaidRerun(
            new CreditRefusalException(FailureClass.InsufficientCredits, "INSUFFICIENT_CREDITS", "no"), null).Should().BeTrue();

    [Theory]
    [InlineData("PROVIDER_FAILED")]
    [InlineData("CONFIG_INVALID")]
    [InlineData(null)]
    public void An_ordinary_step_failure_still_fails_the_run(string? code) =>
        ReviewAdjustmentReport.IsRefusedPaidRerun(new System.InvalidOperationException("x"), code).Should().BeFalse();

    [Fact]
    public void The_refusal_is_recorded_in_adjustmentRerun_with_a_warning()
    {
        string output = """{"score":6,"adjustmentRerun":{"fromStepOrder":2,"reusedStepOrders":[1],"scope":"this run only"}}""";

        string marked = ReviewAdjustmentReport.AttachRerunRefusal(
            output, null, 4, "VideoGenerate", "BUDGET_EXCEEDED", "Daily video budget reached.");

        JsonObject root = JsonNode.Parse(marked)!.AsObject();
        JsonObject refused = root["adjustmentRerun"]!["refused"]!.AsObject();
        refused["stepOrder"]!.GetValue<int>().Should().Be(4);
        refused["stepType"]!.GetValue<string>().Should().Be("VideoGenerate");
        refused["code"]!.GetValue<string>().Should().Be("BUDGET_EXCEEDED");
        refused["message"]!.GetValue<string>().Should().Contain("Daily video budget");
        root["adjustmentRerun"]!["fromStepOrder"]!.GetValue<int>().Should().Be(2);
        root["warnings"]!.AsArray().Should().ContainSingle(w =>
            w!["code"]!.GetValue<string>() == ReviewAdjustmentReport.RefusedWarningCode);
    }

    [Fact]
    public void The_refusal_is_recorded_even_when_the_review_wrote_no_rerun_node()
    {
        string marked = ReviewAdjustmentReport.AttachRerunRefusal("""{"score":6}""", null, 3, "AudioGenerate", "BUDGET_EXCEEDED", "cap");
        JsonNode.Parse(marked)!["adjustmentRerun"]!["refused"]!["code"]!.GetValue<string>().Should().Be("BUDGET_EXCEEDED");
    }
}
