using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution;
using ReelBolt.WorkflowEngine.Execution.Context;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// WP B6b: the mapping from a failed step to the class that decides who pays for the work it
/// consumed (docs/design/saas-metering.md "Failure classes"), plus the drift guard the spec asks
/// for — a new SCREAMING_SNAKE code constant with no mapping fails the build rather than silently
/// classifying as unmapped.
/// </summary>
public sealed class FailureClassifierTests
{
    [Theory]
    [InlineData("CONFIG_INVALID", FailureClass.UserInput)]
    [InlineData("INPUT_MISSING", FailureClass.UserInput)]
    [InlineData("INSERT_CONTENT_MISSING", FailureClass.UserInput)]
    [InlineData("INSERT_NOT_APPLIED", FailureClass.UserInput)]
    [InlineData("INSERT_CONTENT_UNUSABLE", FailureClass.UserInput)]
    [InlineData("MEDIA_OVERLAY_REQUIRES_REENCODE", FailureClass.UserInput)]
    [InlineData("MEDIA_OVERLAY_REF_INVALID", FailureClass.UserInput)]
    [InlineData("MEDIA_OVERLAY_CONTENT_NOT_FOUND", FailureClass.UserInput)]
    [InlineData("MEDIA_OVERLAY_CONTENT_UNUSABLE", FailureClass.UserInput)]
    [InlineData("MEDIA_OVERLAY_SPAN_INVALID", FailureClass.UserInput)]
    [InlineData("MEDIA_OVERLAY_OPACITY_INVALID", FailureClass.UserInput)]
    [InlineData("MEDIA_OVERLAY_LIMIT_EXCEEDED", FailureClass.UserInput)]
    [InlineData("INSERT_FILTER_UNAVAILABLE", FailureClass.PlatformInternal)]
    [InlineData("CAPTIONS_REQUIRE_REENCODE", FailureClass.UserInput)]
    [InlineData("VOICE_CONSENT_MISSING", FailureClass.UserInput)]
    [InlineData("NARRATION_TOO_MANY_WORDS", FailureClass.UserInput)]
    [InlineData("GLOSSARY_UNAVAILABLE", FailureClass.PlatformInternal)]
    [InlineData("ENCODE_FAILED", FailureClass.PlatformInternal)]
    [InlineData("CENSOR_UNVERIFIED", FailureClass.PlatformInternal)]
    [InlineData("INSERT_TRACKING_FAILED", FailureClass.PlatformInternal)]
    [InlineData("UNEXPECTED_ERROR", FailureClass.PlatformInternal)]
    [InlineData("TRANSCRIPTION_FAILED", FailureClass.PlatformProviderError)]
    [InlineData("ALL_TAKES_FAILED", FailureClass.PlatformProviderError)]
    [InlineData("AGENT_OUTPUT_SCHEMA_INVALID", FailureClass.PlatformProviderError)]
    [InlineData("SUBMIT_OUTCOME_UNKNOWN", FailureClass.PlatformProviderError)]
    [InlineData("INSUFFICIENT_CREDITS", FailureClass.InsufficientCredits)]
    [InlineData("PLAN_NOT_ENTITLED", FailureClass.EntitlementDenied)]
    [InlineData("COMPOSITION_SOURCE_NOT_FOUND", FailureClass.UserInput)]
    [InlineData("CONTINUE_COMPOSITION_REQUIRES_AUTHORING_AGENT", FailureClass.UserInput)]
    [InlineData("COMPOSITION_RESTORE_FAILED", FailureClass.PlatformInternal)]
    public void Coded_failures_map_to_who_pays(string code, FailureClass expected)
        => FailureClassifier.ForCode(code).Should().Be(expected);

    /// <summary>
    /// Prompted region effects (T-001b). Everything <c>RegionEffectRules.Validate</c> refuses, plus the
    /// analyze step's own source-length cap, is pure CONFIG: an effect list the author typed, or a clip
    /// longer than the <c>MaxSegmentationSeconds</c> they chose. Nothing was bought on any of these
    /// paths, and charging a user a second time for a typo is exactly the error this table exists to
    /// prevent — so each keeps its consumption as <see cref="FailureClass.UserInput"/>.
    /// </summary>
    [Theory]
    [InlineData("EFFECT_INVALID")]
    [InlineData("EFFECT_REQUIRES_REENCODE")]
    [InlineData("EFFECT_TARGET_INVALID")]
    [InlineData("EFFECT_STRENGTH_OUT_OF_RANGE")]
    [InlineData("EFFECT_FEATHER_OUT_OF_RANGE")]
    [InlineData("EFFECT_OPACITY_OUT_OF_RANGE")]
    [InlineData("EFFECT_LIMIT_EXCEEDED")]
    [InlineData("SEGMENTATION_SOURCE_TOO_LONG")]
    public void Region_effect_config_refusals_are_the_authors_to_fix(string code)
        => FailureClassifier.ForCode(code).Should().Be(FailureClass.UserInput);

    /// <summary>
    /// The other side of the same family, asserted rather than left implicit: a target the analyze step
    /// never looked for, two tracks sharing a prompt, and a mask whose bytes disagree with the artifact are
    /// OUR side and refund. The contrast is the point — neither family is mapped by prefix, so a new code
    /// in either must be classified on its own. The one member of that family that is finer-grained than
    /// the plain platform reading — the segmentation vendor's own failure — is asserted just below.
    /// </summary>
    [Theory]
    [InlineData("EFFECT_TARGET_UNVERIFIED")]
    [InlineData("EFFECT_TARGET_AMBIGUOUS")]
    [InlineData("EFFECT_TARGET_EMPTY")]
    [InlineData("SEGMENTATION_MASK_UNVERIFIED")]
    [InlineData("SEGMENTATION_BAD_RESPONSE")]
    public void Provider_and_verification_failures_beside_them_stay_refunded(string code)
        => FailureClassifier.ForCode(code).Should().Be(FailureClass.PlatformInternal);

    /// <summary>
    /// <c>SEGMENTATION_PROVIDER_ERROR</c> names the vendor rather than the platform: a failed request or
    /// a decode the vendor's own contract does not support is the PROVIDER's, so it carries
    /// <see cref="FailureClass.PlatformProviderError"/>. It is still refunded and still never the author's —
    /// the mapping is more precise than <see cref="FailureClass.PlatformInternal"/>, not a different
    /// decision about who pays.
    /// </summary>
    [Fact]
    public void A_segmentation_provider_failure_is_the_vendors_and_still_refunds()
    {
        FailureClass mapped = FailureClassifier.ForCode("SEGMENTATION_PROVIDER_ERROR");

        mapped.Should().Be(FailureClass.PlatformProviderError);
        CreditGate.IsRefundable(mapped).Should().BeTrue("a vendor failure is refunded like every other platform class");
    }

    [Fact]
    public void An_effect_code_we_have_never_seen_falls_through_to_the_refund()
    {
        // The fallthrough, in the family where a mistake would be most tempting: no EFFECT_* prefix is
        // recognised, so an unmapped future code is refunded rather than charged on the strength of it.
        FailureClassifier.TryGetCode("EFFECT_SOMETHING_NEW", out _).Should().BeFalse();
        FailureClassifier.ForCode("EFFECT_SOMETHING_NEW").Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public void An_unmapped_code_refunds_by_default()
    {
        // Refund-by-default is deliberate: charging a user for a failure we cannot explain is the
        // worse error, and B12 reconciliation reports the refund rate so the trade is revisitable.
        FailureClassifier.ForCode("SOMETHING_WE_NEVER_SEEN").Should().Be(FailureClass.PlatformInternal);
        FailureClassifier.ForCode(null).Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public void Refundable_matches_the_gates_own_table()
    {
        foreach ((string code, FailureClass expected) in FailureClassifier.Codes)
        {
            bool refundable = CreditGate.IsRefundable(expected);
            refundable.Should().Be(
                expected is not (FailureClass.UserInput or FailureClass.UserCancelled
                    or FailureClass.ByoProviderError or FailureClass.InsufficientCredits
                    or FailureClass.EntitlementDenied
                    // D12: the runner class is non-refundable too — a modified or broken customer
                    // machine must not be able to mint credit by failing verification. This list and
                    // CreditGate.IsRefundable must say the same thing; that is the whole point of
                    // this test.
                    or FailureClass.Runner),
                because: $"code {code} classified as {expected} must agree with the gate's refund rule");
        }
    }

    [Theory]
    [InlineData("CONFIG_INVALID", FailureClass.UserInput)]
    [InlineData("ENCODE_FAILED", FailureClass.PlatformInternal)]
    [InlineData("PROVIDER_NOT_FOUND", FailureClass.UserInput)]
    public void The_failure_envelope_of_a_deterministic_executor_names_the_code(string code, FailureClass expected)
    {
        string output = $"{{\"status\":\"failed\",\"error\":{{\"code\":\"{code}\",\"message\":\"boom\"}}}}";

        FailureClassifier.CodeFromOutput(output).Should().Be(code);
        FailureClassifier.Classify(FailureClass.None, FailureClassifier.CodeFromOutput(output), null).Should().Be(expected);
    }

    [Fact]
    public void The_video_generate_failure_envelope_names_the_code_too()
    {
        // VideoGenerateStepExecutor.CreateErrorOutput puts it in meta.failed, not error.code.
        const string output = "{\"clips\":[],\"meta\":{\"failed\":\"POLL_TIMEOUT\",\"error\":\"timed out\"}}";

        FailureClassifier.CodeFromOutput(output).Should().Be("POLL_TIMEOUT");
        FailureClassifier.ForCode(FailureClassifier.CodeFromOutput(output)).Should().Be(FailureClass.PlatformProviderError);
    }

    [Fact]
    public void An_agent_steps_prose_output_names_no_code()
    {
        FailureClassifier.CodeFromOutput("The model produced no parseable JSON object.").Should().BeNull();
        FailureClassifier.CodeFromOutput("{\"view\":null}").Should().BeNull();
        FailureClassifier.CodeFromOutput(null).Should().BeNull();
    }

    [Fact]
    public void A_message_can_only_supply_a_code_we_already_know()
    {
        // The retry wrapper's own message, which is where the code of the last attempt survives.
        FailureClassifier.CodeFromMessage(
            "Step 3 (VideoCompile) failed after 1 attempts. Last error: ENCODE_FAILED: ffmpeg exited 1")
            .Should().Be("ENCODE_FAILED");

        FailureClassifier.CodeFromMessage("the model gave up and returned NOT_A_REAL_CODE").Should().BeNull();
        FailureClassifier.CodeFromMessage("no code here").Should().BeNull();
    }

    [Fact]
    public void An_unpriced_model_refusal_is_a_known_code_and_ours()
    {
        FailureClassifier.TryGetCode(ModelUnpricedException.FailureCode, out FailureClass mapped).Should().BeTrue();
        mapped.Should().Be(FailureClass.PlatformInternal);
        FailureClassifier.CodeFromException(new ModelUnpricedException(InferenceProviderCapability.Chat, "m"))
            .Should().Be(ModelUnpricedException.FailureCode);
    }

    [Fact]
    public void A_context_too_large_stop_is_a_known_code_ours_and_refunded()
    {
        FailureClassifier.TryGetCode(ContextTooLargeException.FailureCode, out FailureClass mapped).Should().BeTrue();
        mapped.Should().Be(FailureClass.PlatformInternal);
        ContextTooLargeException thrown = new(950_000, 900_000);
        FailureClassifier.CodeFromException(thrown).Should().Be("CONTEXT_TOO_LARGE");
        FailureClassifier.Classify(FailureClass.None, FailureClassifier.CodeFromException(thrown), thrown)
            .Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public void AgentWorkflowException_is_the_users_input()
    {
        FailureClassifier.ForException(new AgentWorkflowException("missing data")).Should().Be(FailureClass.UserInput);
    }

    [Fact]
    public void A_timed_out_agent_run_is_ours()
        => FailureClassifier.ForException(new TimeoutException("timed out after 10800 seconds"))
            .Should().Be(FailureClass.PlatformInternal);

    [Fact]
    public void A_cancellation_follows_the_registrys_recorded_provenance()
    {
        // The registry records WHY; a stop the user never asked for (transport kill, restart) must
        // never be charged to them as their own.
        FailureClassifier.ForException(new OperationCanceledException(), "Stopped by user 42")
            .Should().Be(FailureClass.UserCancelled);
        FailureClassifier.ForException(new OperationCanceledException(), ExecutionCancellationRegistry.UnknownReason)
            .Should().Be(FailureClass.PlatformInternal);
        FailureClassifier.IsUserStop("Stopped by user 42").Should().BeTrue();
        FailureClassifier.IsUserStop(ExecutionCancellationRegistry.UnknownReason).Should().BeFalse();
        FailureClassifier.IsUserStop(null).Should().BeFalse();
    }

    [Fact]
    public void A_credit_refusal_carries_its_own_class_and_code()
    {
        var refusal = new CreditRefusalException(FailureClass.InsufficientCredits, "INSUFFICIENT_CREDITS", "nope");
        FailureClassifier.ForException(refusal).Should().Be(FailureClass.InsufficientCredits);
        FailureClassifier.CodeFromException(refusal).Should().Be("INSUFFICIENT_CREDITS");
    }

    [Fact]
    public void Classify_prefers_a_class_assigned_where_the_failure_was_produced()
    {
        // A caller that knows the provider owner (the one thing the executor cannot see) wins over
        // the table's platform reading.
        FailureClassifier.Classify(FailureClass.ByoProviderError, "TRANSCRIPTION_FAILED", null)
            .Should().Be(FailureClass.ByoProviderError);

        // Otherwise: the code, then the exception, then refund-by-default.
        FailureClassifier.Classify(FailureClass.None, "CONFIG_INVALID", null).Should().Be(FailureClass.UserInput);
        FailureClassifier.Classify(FailureClass.None, null, new TimeoutException()).Should().Be(FailureClass.PlatformInternal);
        FailureClassifier.Classify(FailureClass.None, null, null).Should().Be(FailureClass.PlatformInternal);
    }

    [Fact]
    public void The_refusal_of_a_gate_that_could_not_be_evaluated_is_our_fault_not_the_users()
    {
        var step = new WorkflowStep { StepOrder = 2, StepType = StepType.Agent };

        CreditRefusalException insufficient = CreditRefusalException.For(
            new CreditReservation(CreditReserveOutcome.InsufficientCredits, null, "balance too low", 20_000, 100), step);
        insufficient.Code.Should().Be("INSUFFICIENT_CREDITS");
        insufficient.FailureClass.Should().Be(FailureClass.InsufficientCredits);
        insufficient.Message.Should().Contain("INSUFFICIENT_CREDITS").And.Contain("Step 2");

        CreditRefusalException denied = CreditRefusalException.For(
            new CreditReservation(CreditReserveOutcome.EntitlementDenied, null, "PLAN_NOT_ENTITLED:Rooms"), step);
        denied.Code.Should().Be("PLAN_NOT_ENTITLED");
        denied.FailureClass.Should().Be(FailureClass.EntitlementDenied);
    }

    // -----------------------------------------------------------------
    // The drift guard the spec asks for: "every \"[A-Z_]+\" failure-code constant has a mapping".
    // -----------------------------------------------------------------

    private static readonly Regex ScreamingSnake = new("\"([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)\"", RegexOptions.Compiled);

    [Fact]
    public void Every_SCREAMING_SNAKE_literal_in_the_engine_is_either_classified_or_a_documented_non_code()
    {
        string engineRoot = EngineSourceRoot();
        var unmapped = new SortedSet<string>(StringComparer.Ordinal);
        var found = new SortedSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(engineRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in ScreamingSnake.Matches(File.ReadAllText(file)))
            {
                string literal = match.Groups[1].Value;
                found.Add(literal);
                if (!FailureClassifier.TryGetCode(literal, out _) && !FailureClassifier.NonCodeLiterals.Contains(literal))
                    unmapped.Add(literal);
            }
        }

        found.Should().NotBeEmpty("the scan must actually read the engine's source tree");
        unmapped.Should().BeEmpty(
            because: "every failure code must have a class in FailureClassifier (docs/design/saas-metering.md, " +
            "WP B6b's drift guard): add it to the table, or to NonCodeLiterals when it is not a failure code at all");
    }

    private static string EngineSourceRoot([System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        DirectoryInfo? dir = new FileInfo(sourceFile).Directory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "ReelBolt.WorkflowEngine")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the test runs inside the repository, under inference/tests/");
        return Path.Combine(dir!.FullName, "src", "ReelBolt.WorkflowEngine");
    }
}
