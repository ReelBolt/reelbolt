using System;
using AwesomeAssertions;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="GeneratedShotEgressPolicy"/> decisions whether a planned shot may send frames of the
/// user's OWN footage to a third-party generation provider as a keyframe. A keyframe is not metadata
/// about the user's media — it IS the user's media, and once posted to a provider's API it cannot be
/// recalled — so absent the workflow author's explicit <c>AllowSourceMediaEgress</c> grant the answer
/// must be a refusal that names exactly which frames were asked for.
/// </summary>
public class GeneratedShotEgressPolicyTests
{
    // ---------------------------------------------------------------------
    // The refusal
    // ---------------------------------------------------------------------

    [Fact]
    public void Evaluate_first_frame_without_consent_is_refused_with_the_stable_code()
    {
        EgressDecision decision = GeneratedShotEgressPolicy.Evaluate("s7", null, allowSourceMediaEgress: false);

        decision.Allowed.Should().BeFalse(
            "a keyframe IS the user's own footage, and sending it to a third party without the workflow author's explicit consent is exactly what must not happen");
        decision.Code.Should().Be(GeneratedShotEgressPolicy.EgressRefusedCode,
            "the executor emits this code in the step's failure payload, so operators and tests must match on one stable constant rather than on prose");
    }

    [Fact]
    public void Evaluate_last_frame_without_consent_is_refused()
    {
        EgressDecision decision = GeneratedShotEgressPolicy.Evaluate(null, "s8", allowSourceMediaEgress: false);

        decision.Allowed.Should().BeFalse(
            "a LAST-frame keyframe is the same act of egress as a first-frame one; keying the refusal on FirstFrame alone would leak footage through the back door");
        decision.Code.Should().Be(GeneratedShotEgressPolicy.EgressRefusedCode,
            "the same stable code must be reported regardless of which keyframe triggered the refusal");
    }

    [Fact]
    public void Evaluate_both_frames_without_consent_is_refused()
    {
        EgressDecision decision = GeneratedShotEgressPolicy.Evaluate("s7", "s8", allowSourceMediaEgress: false);

        decision.Allowed.Should().BeFalse(
            "a shot bracketed by two source keyframes is the largest egress request the plan can make and must certainly be refused without consent");
        decision.Code.Should().Be(GeneratedShotEgressPolicy.EgressRefusedCode,
            "the stable code is what the operator's tooling keys on, whatever combination of frames was asked for");
    }

    [Fact]
    public void Evaluate_refused_reason_names_every_requested_frame_and_how_to_grant_consent()
    {
        // The reason must let the operator act: either grant consent or edit the plan. A refusal that
        // merely said "denied" would leave them with no idea which shot asked for what.
        EgressDecision decision = GeneratedShotEgressPolicy.Evaluate("s7", "s8", allowSourceMediaEgress: false);

        decision.Reason.Should().NotBeNullOrWhiteSpace(
            "a refusal must always carry a human-readable reason, because the failure is surfaced to the operator");
        decision.Reason.Should().Contain("s7").And.Contain("s8",
            "the reason must name exactly which frames were requested, so the operator can find and edit that shot");
        decision.Reason.Should().Contain("AllowSourceMediaEgress",
            "the reason must name the exact setting that would grant consent, or the operator cannot resolve the refusal");
    }

    [Fact]
    public void Evaluate_refused_reason_names_the_trimmed_frame_id()
    {
        // The id is echoed trimmed so the reason quotes the id the operator will actually find in the
        // analysis view, not the padded string the model happened to emit.
        EgressDecision decision = GeneratedShotEgressPolicy.Evaluate("  s7  ", null, allowSourceMediaEgress: false);

        decision.Reason.Should().Contain("firstFrame 's7'",
            "the reason must quote the id as it appears in the offered-id set rather than with the model's stray whitespace");
    }

    [Fact]
    public void Evaluate_refused_reason_names_only_the_frame_that_was_actually_requested()
    {
        EgressDecision decision = GeneratedShotEgressPolicy.Evaluate("s7", null, allowSourceMediaEgress: false);

        decision.Reason.Should().Contain("firstFrame 's7'");
        decision.Reason.Should().NotContain("lastFrame",
            "claiming a last-frame was requested when it was not would send the operator looking for a keyframe that does not exist");
    }

    // ---------------------------------------------------------------------
    // The allowed cases
    // ---------------------------------------------------------------------

    [Fact]
    public void Evaluate_no_keyframes_is_allowed_even_without_consent()
    {
        // A plain text-to-video shot sends nothing of the user's, so requiring consent for it would
        // block the whole feature for no privacy benefit.
        EgressDecision decision = GeneratedShotEgressPolicy.Evaluate(null, null, allowSourceMediaEgress: false);

        decision.Allowed.Should().BeTrue(
            "a shot that needs no keyframe sends none of the user's footage, so it needs no egress consent");
        decision.Code.Should().BeNull("an allowed shot carries no failure code");
        decision.Reason.Should().BeNull("an allowed shot carries no failure reason");
    }

    [Fact]
    public void Evaluate_keyframes_with_explicit_consent_are_allowed()
    {
        EgressDecision decision = GeneratedShotEgressPolicy.Evaluate("s7", "s8", allowSourceMediaEgress: true);

        decision.Allowed.Should().BeTrue(
            "the workflow author's explicit grant is exactly the decision this policy defers to, and it must be honoured for both keyframes");
        decision.Code.Should().BeNull("a consented shot is not a failure and must carry no refusal code");
        decision.Reason.Should().BeNull("a consented shot needs no explanation");
    }

    [Fact]
    public void Evaluate_single_keyframe_with_consent_is_allowed()
    {
        GeneratedShotEgressPolicy.Evaluate("s7", null, allowSourceMediaEgress: true).Allowed.Should().BeTrue(
            "consent applies per step, not per frame, so one consented keyframe must not require anything further");
    }

    // ---------------------------------------------------------------------
    // Blank ids count as absent
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\t", "\n")]
    [InlineData(null, "  ")]
    public void Evaluate_blank_frame_ids_count_as_absent(string? firstFrame, string? lastFrame)
    {
        // A whitespace-only reference is not something any caller can resolve to a real frame, so
        // counting it as "needs a keyframe" would refuse a shot that in fact asks for nothing.
        EgressDecision decision = GeneratedShotEgressPolicy.Evaluate(firstFrame, lastFrame, allowSourceMediaEgress: false);

        decision.Allowed.Should().BeTrue(
            "a blank frame id names no frame, so it requests no egress and must not trigger a refusal");
        decision.Code.Should().BeNull("nothing was refused, so there is no code to report");
    }

    [Fact]
    public void Evaluate_blank_first_frame_with_a_real_last_frame_still_refuses()
    {
        // The blank-tolerant half must not swallow a genuine request in the other field.
        EgressDecision decision = GeneratedShotEgressPolicy.Evaluate("   ", "s8", allowSourceMediaEgress: false);

        decision.Allowed.Should().BeFalse(
            "one real keyframe request is enough to require consent, even when the other field is blank");
        decision.Reason.Should().Contain("s8");
        decision.Reason.Should().NotContain("firstFrame",
            "the blank first-frame field requested nothing and must not be reported as if it had");
    }

    // ---------------------------------------------------------------------
    // The pinned contract
    // ---------------------------------------------------------------------

    [Fact]
    public void Egress_refused_code_is_pinned()
    {
        // This literal is part of the step's failure payload contract — an operator's runbook and any
        // alerting keyed on it break silently if the constant is reworded.
        GeneratedShotEgressPolicy.EgressRefusedCode.Should().Be("EGRESS_REFUSED",
            "the refusal code is matched verbatim by operators and tests, so changing it is a breaking change and must be a visible failure");
    }

    [Fact]
    public void Evaluate_is_a_pure_decision_and_never_throws_for_any_combination()
    {
        // The policy neither uploads nor extracts a frame; it only states a verdict. Throwing here
        // would turn an ordinary editorial mistake into a crashed step rather than a clear refusal.
        string?[] values = { null, "", "   ", "s7", " s8 " };

        foreach (string? first in values)
        {
            foreach (string? last in values)
            {
                foreach (bool consent in new[] { false, true })
                {
                    Action act = () => GeneratedShotEgressPolicy.Evaluate(first, last, consent);
                    act.Should().NotThrow(
                        "every combination of frame ids and consent is an ordinary input this pure decision function must answer, not reject");
                }
            }
        }
    }
}
