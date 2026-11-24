using System;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

public class FailureMessagesTests
{
    [Theory]
    [InlineData("DESIGN_COMPOSE_INVALID")]
    [InlineData("SCRIPTWRITER_OUTPUT_INVALID")]
    [InlineData("SEGMENTATION_BAD_RESPONSE")]
    [InlineData("BUDGET_EXCEEDED")]
    [InlineData("PLAN_NOT_ENTITLED")]
    [InlineData("ASSET_UNRESOLVED")]
    [InlineData("ASSET_OUTSIDE_PROJECT")]
    [InlineData("USER_CANCELLED")]
    [InlineData("RUN_STOPPED")]
    [InlineData("RUN_INTERRUPTED")]
    [InlineData("STEP_INTERNAL_ERROR")]
    [InlineData("SOME_UNKNOWN_CODE")]
    [InlineData(null)]
    public void UserMessage_is_plain_and_never_echoes_raw_detail(string? code)
    {
        string raw = "Step 10 (DesignSystem) failed. Path: $.Scenes[0].OnScreenText | LineNumber 3 C:\\x\\y System.Collections.Generic.List`1";
        string message = FailureMessages.UserMessage(code, raw);

        message.Should().NotBeNullOrWhiteSpace();
        message.Should().NotContain("Step 10").And.NotContain("$.").And.NotContain("System.")
            .And.NotContain("LineNumber").And.NotContain("Path:");
        message.Length.Should().BeLessThan(220);
    }

    [Theory]
    [InlineData("ASSET_UNRESOLVED")]
    [InlineData("ASSET_OUTSIDE_PROJECT")]
    public void UserMessage_for_a_missing_clip_file_says_what_to_do(string code)
    {
        FailureMessages.UserMessage(code, "Asset clip x does not resolve").Should().Be(
            "A file this video uses is no longer in the project. Put it back or remove it from the timeline, then render again.");
        FailureClassifier.ForCode(code).Should().Be(FailureClass.UserInput);
    }

    [Theory]
    [InlineData("INSERT_NOT_APPLIED")]
    [InlineData("PIN_INSERT_NO_REGIONS")]
    public void UserMessage_for_no_screen_found_names_what_defeats_the_tracker_and_offers_a_corner(string code)
    {
        string m = FailureMessages.UserMessage(code);
        m.Should().Contain("nothing was changed").And.Contain("small or hand-held").And.Contain("pattern or markers")
            .And.Contain("leave the frame").And.Contain("corner or full screen");
        m.Length.Should().BeLessThan(600);
    }

    [Fact]
    public void UserMessage_for_a_failed_screen_scan_is_ours_and_says_run_it_again()
    {
        string m = FailureMessages.UserMessage("INSERT_TRACKING_FAILED");
        m.Should().Contain("our side").And.Contain("nothing was changed").And.Contain("run it again").And.Contain("contact support").And.Contain("corner");
        m.Length.Should().BeLessThan(600);
        FailureClassifier.ForCode("INSERT_TRACKING_FAILED").Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public void UserMessage_says_whose_fault_it_is()
    {
        FailureMessages.UserMessage("SCRIPTWRITER_OUTPUT_INVALID").Should().Contain("our side");
        FailureMessages.UserMessage("BUDGET_EXCEEDED").Should().Contain("Raise the budget");
        FailureMessages.UserMessage("PLAN_NOT_ENTITLED").Should().Contain("plan");
        FailureMessages.UserMessage("USER_CANCELLED").Should().Contain("Stopped by user");
    }

    [Fact]
    public void Cancellation_gets_a_real_code_by_provenance()
    {
        var ex = new System.Threading.Tasks.TaskCanceledException("A task was canceled.");
        FailureMessages.EnsureCode(null, ex, "Stopped by user 42").Should().Be("USER_CANCELLED");
        FailureMessages.EnsureCode(null, ex, "no stop request was recorded").Should().Be("RUN_STOPPED");
        FailureClassifier.ForCode("USER_CANCELLED").Should().Be(FailureClass.UserCancelled);
        FailureClassifier.ForCode("RUN_STOPPED").Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public void Unknown_exception_gets_the_generic_code_never_empty()
    {
        FailureMessages.EnsureCode(null, new InvalidOperationException("boom"), null).Should().Be("STEP_INTERNAL_ERROR");
        FailureMessages.EnsureResultCode("", null, "boom").Should().Be("STEP_INTERNAL_ERROR");
        FailureMessages.EnsureResultCode(null, null, "x BUDGET_EXCEEDED y").Should().Be("BUDGET_EXCEEDED");
        FailureMessages.EnsureCode("CONFIG_INVALID", new Exception("x")).Should().Be("CONFIG_INVALID");
    }

    [Fact]
    public void Every_code_the_fallbacks_emit_is_classified()
    {
        foreach (string code in new[] { "USER_CANCELLED", "RUN_STOPPED", "STEP_INTERNAL_ERROR", "RUN_INTERRUPTED" })
            FailureClassifier.TryGetCode(code, out _).Should().BeTrue(code);
    }

    private static readonly Exception Flat = new InvalidOperationException("Step 3 failed. Last error: C:\\x\\id");

    [Fact]
    public void RunMessage_refunded_fit_failure_is_specific_not_generic()
    {
        string m = FailureMessages.RunMessage(Flat, FailureClass.PlatformInternal, "NARRATION_DOES_NOT_FIT", "La frase");
        m.Should().Contain("too long for its scene").And.Contain("not charged").And.NotContain("on our side");
    }

    [Fact]
    public void RunMessage_user_class_with_a_message_shows_it()
        => FailureMessages.RunMessage(Flat, FailureClass.UserInput, "NARRATION_DOES_NOT_FIT", "La frase è troppo lunga.")
            .Should().Be("La frase è troppo lunga.");

    [Theory]
    [InlineData(FailureClass.UserInput)]
    [InlineData(FailureClass.EntitlementDenied)]
    [InlineData(FailureClass.InsufficientCredits)]
    [InlineData(FailureClass.ByoProviderError)]
    [InlineData(FailureClass.UserCancelled)]
    public void RunMessage_user_class_without_a_message_never_says_our_side(FailureClass c)
    {
        FailureMessages.RunMessage(Flat, c, "NARRATION_DOES_NOT_FIT", null).Should().NotContain("on our side");
        FailureMessages.RunMessage(Flat, c, null, "  ").Should().NotContain("on our side");
    }

    [Fact]
    public void RunMessage_byo_and_credits_have_their_own_sentences()
    {
        FailureMessages.RunMessage(Flat, FailureClass.ByoProviderError, null, null).Should().Contain("connected to your workspace");
        FailureMessages.RunMessage(Flat, FailureClass.InsufficientCredits, null, null).Should().Contain("not enough credits");
    }

    [Theory]
    [InlineData(FailureClass.PlatformInternal)]
    [InlineData(FailureClass.None)]
    public void RunMessage_platform_class_ignores_the_step_message(FailureClass c)
        => FailureMessages.RunMessage(Flat, c, "PROVIDER_TIMEOUT", "Italian text.")
            .Should().Be(FailureMessages.RunMessage(Flat));

    [Fact]
    public void RunMessage_normalises_the_step_message()
        => FailureMessages.RunMessage(Flat, FailureClass.UserInput, null, "  a \n\t b\u200B \u202Ec\u0007  ")
            .Should().Be("a b c");

    [Fact]
    public void RunMessage_caps_at_the_last_sentence_that_fits()
    {
        string text = string.Join(" ", Enumerable.Repeat(new string('x', 98) + ".", 10));
        string result = FailureMessages.RunMessage(Flat, FailureClass.UserInput, null, text);
        result.Length.Should().BeLessThanOrEqualTo(600);
        result.Should().EndWith(".").And.NotEndWith("…");

        string one = FailureMessages.RunMessage(Flat, FailureClass.UserInput, null, new string('y', 900));
        one.Length.Should().Be(600);
        one.Should().EndWith("…");
    }

    [Fact]
    public void RunMessage_keeps_credit_refusal_text_and_the_old_overload_is_unchanged()
    {
        FailureMessages.RunMessage(new CreditRefusalException(FailureClass.InsufficientCredits, "INSUFFICIENT_CREDITS", "Out of credits."), FailureClass.UserInput, null, "other")
            .Should().Be("Out of credits.");
        FailureMessages.RunMessage(Flat).Should().Be(FailureMessages.UserMessage(FailureMessages.EnsureCode(null, Flat)));
    }
}
