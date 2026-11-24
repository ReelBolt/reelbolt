using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Regression test: found live producing the alpha launch film, after two loop-backs produced
/// near-identical review scores (2/10 twice, then 5/10 twice).
///
/// The <c>ReviewLoop</c> feedback plumbing already existed and worked — the executor extracts the
/// score/issues on a loop-back, carries them in a step-order window, and hands them to the target
/// step's context via <c>RecordRetryFeedback</c>. But the composed guidance was only appended
/// inside <see cref="StepExecutionContext.BuildAgentInput"/>, and a ROOM step does not go through
/// it: <c>RoomStepExecutorBase</c> composes its own opening message, synthesis prompt and
/// solo-fallback prompt. So the feedback reached a solo <c>Agent(VideoStoryEditor)</c> sitting
/// behind a ReviewLoop and silently stopped applying the moment that step was swapped for an
/// <c>EditRoom</c> — the room re-ran blind to why its previous cut had been rejected.
///
/// This is the SAME bug as the user-request one already fixed on those three prompts, which is
/// why the fix is the same shape: a shared method on the context rather than a literal repeated
/// per call site.
/// </summary>
public class RoomRetryGuidanceTests
{
    private static StepExecutionContext Context(string? userRequest = null) => new()
    {
        Execution = new WorkflowExecution { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid() },
        Step = new WorkflowStep { StepOrder = 13, StepType = StepType.EditRoom },
        AllSteps = [],
        AccumulatedOutput = "",
        CurrentStepIndex = 0,
        IterationCount = 1,
        CorrelationId = "test",
        StepOutputHistory = [],
        UserRequest = userRequest
    };

    [Fact]
    public void Recorded_review_feedback_reaches_a_room_prompt()
    {
        StepExecutionContext ctx = Context();
        ctx.RecordRetryFeedback(1, "score 2/10. Output duration is only 21.96s against a 30-42s requirement.");

        string prompt = ctx.WithRetryGuidance("## Bounded view\n{...}");

        prompt.Should().Contain("Retry Guidance");
        prompt.Should().Contain("21.96s", "the room must be told what was actually wrong");
        prompt.Should().Contain("Attempt 1");
    }

    [Fact]
    public void With_nothing_recorded_the_prompt_is_returned_untouched()
    {
        const string original = "## Bounded view\n{...}";

        Context().WithRetryGuidance(original).Should().BeSameAs(original,
            "a first attempt must be byte-identical to the pre-fix behaviour");
    }

    [Fact]
    public void Guidance_and_user_request_compose_without_either_displacing_the_other()
    {
        StepExecutionContext ctx = Context(userRequest: "Keep both chroma plates.");
        ctx.RecordRetryFeedback(1, "score 3/10. The hero render block is missing entirely.");

        string prompt = ctx.WithRetryGuidance(ctx.WithUserRequest("## Bounded view"));

        prompt.Should().Contain("User Request");
        prompt.Should().Contain("Keep both chroma plates.");
        prompt.Should().Contain("Retry Guidance");
        prompt.Should().Contain("hero render block is missing");
        prompt.IndexOf("User Request", StringComparison.Ordinal)
            .Should().BeLessThan(prompt.IndexOf("Retry Guidance", StringComparison.Ordinal),
                "the newest signal goes last, where BuildAgentInput has always put it");
    }

    [Fact]
    public void An_agent_step_and_a_room_step_are_told_the_same_thing_in_the_same_words()
    {
        // The whole point of sharing the method: the two paths cannot drift apart again.
        StepExecutionContext agentCtx = Context();
        StepExecutionContext roomCtx = Context();
        const string feedback = "score 5/10. Only 8 beats against the required 10-14.";
        agentCtx.RecordRetryFeedback(2, feedback);
        roomCtx.RecordRetryFeedback(2, feedback);

        string agentTail = agentCtx.WithRetryGuidance("X")["X".Length..];
        string roomTail = roomCtx.WithRetryGuidance("Y")["Y".Length..];

        roomTail.Should().Be(agentTail);
    }

    [Fact]
    public void Only_the_most_recent_attempts_are_carried_so_the_prompt_cannot_grow_without_bound()
    {
        StepExecutionContext ctx = Context();
        for (int i = 1; i <= 6; i++)
            ctx.RecordRetryFeedback(i, $"score {i}/10. issue number {i}.");

        string prompt = ctx.WithRetryGuidance("base");

        prompt.Should().Contain("issue number 6");
        prompt.Should().NotContain("issue number 1", "the oldest feedback is dropped");
    }
}
