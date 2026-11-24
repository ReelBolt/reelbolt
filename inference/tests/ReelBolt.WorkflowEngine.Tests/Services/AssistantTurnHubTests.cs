using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Assistant;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services;

/// <summary>
/// The bounds on the live-turn hub (T-022 Security review): "survives a disconnect" must not mean "grows
/// without limit". A stalled subscriber is dropped, the replay log is capped and says so, and a turn that
/// never reached its <c>finally</c> — or finished long ago — is reclaimed rather than wedging a re-attach.
/// </summary>
public class AssistantTurnHubTests
{
    private static string Delta(int i) => $"data: {{\"type\":\"text-delta\",\"textDelta\":\"{i}\"}}\n\n";

    [Fact]
    public async Task A_subscriber_that_stops_reading_is_dropped_rather_than_buffered_behind()
    {
        var hub = new AssistantTurnHub();
        AssistantLiveTurn live = hub.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        AssistantTurnSubscription subscription = live.Subscribe();

        // Far more frames than a subscriber may fall behind by, with nobody reading any of them.
        for (int i = 0; i < AssistantLiveTurn.SubscriberCapacity + 200; i++)
            live.Emit(Delta(i));

        int read = 0;
        await foreach (string _ in subscription.Reader.ReadAllAsync())
            read++;

        read.Should().BeLessThanOrEqualTo(
            AssistantLiveTurn.SubscriberCapacity,
            "a subscriber is bounded; a stalled one is dropped, never buffered behind without limit");
    }

    [Fact]
    public void An_over_budget_log_marks_its_replay_truncated_and_keeps_the_preamble()
    {
        var hub = new AssistantTurnHub();
        AssistantLiveTurn live = hub.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        live.Emit("data: {\"type\":\"start\",\"messageId\":\"11111111-1111-1111-1111-111111111111\"}\n\n");
        live.Emit("data: {\"type\":\"text-start\",\"id\":\"t\"}\n\n");

        for (int i = 0; i < AssistantLiveTurn.MaxLogFrames + 50; i++)
            live.Emit(Delta(i));

        live.ReplayTruncated.Should().BeTrue("the log dropped frames");
        live.Snapshot().Count.Should().BeLessThanOrEqualTo(
            AssistantLiveTurn.MaxLogFrames, "the log is capped by frame count");

        IReadOnlyList<string> backlog = live.Subscribe().Backlog;
        backlog.Should().NotBeEmpty();
        backlog[0].Should().Contain(
            AssistantLiveTurn.ReplayTruncatedType,
            "the client must be TOLD it is seeing a partial replay, not silently get less");
        backlog[1].Should().Contain("\"start\"", "the preamble a client keys the message on is never dropped");
    }

    [Fact]
    public async Task A_turn_that_never_completes_is_reclaimed_at_its_wall_clock()
    {
        var hub = new AssistantTurnHub();
        Guid thread = Guid.NewGuid();
        AssistantLiveTurn live = hub.Begin(thread, Guid.NewGuid(), Guid.NewGuid(), TimeSpan.FromMilliseconds(150));

        hub.Find(thread).Should().BeSameAs(live);

        await Task.Delay(400);

        hub.Find(thread).Should().BeNull(
            "a turn past its own wall clock is reclaimed even when its finally never ran, so it cannot wedge a re-attach");
    }

    [Fact]
    public async Task A_finished_turn_is_evicted_after_its_retention_window()
    {
        var hub = new AssistantTurnHub(completedRetention: TimeSpan.FromMilliseconds(150));
        Guid thread = Guid.NewGuid();
        AssistantLiveTurn live = hub.Begin(thread, Guid.NewGuid(), Guid.NewGuid());
        live.Complete(AssistantTurnState.Completed);

        hub.Find(thread).Should().NotBeNull();

        await Task.Delay(400);

        hub.Find(thread).Should().BeNull("a finished turn's log is evicted after the retention window, not only on capacity");
    }

    [Fact]
    public void Complete_is_idempotent_and_a_late_completion_does_not_reopen_a_finished_turn()
    {
        var hub = new AssistantTurnHub();
        Guid thread = Guid.NewGuid();
        AssistantLiveTurn live = hub.Begin(thread, Guid.NewGuid(), Guid.NewGuid());

        live.Complete(AssistantTurnState.Completed);
        live.Complete(AssistantTurnState.Failed);

        live.State.Should().Be(AssistantTurnState.Completed, "a second completion must not overwrite a terminal state");
    }

    [Fact]
    public void A_subscriber_taken_after_the_turn_ended_still_gets_the_whole_replay()
    {
        var hub = new AssistantTurnHub();
        AssistantLiveTurn live = hub.Begin(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        live.Emit("data: {\"type\":\"start\",\"messageId\":\"11111111-1111-1111-1111-111111111111\"}\n\n");
        live.Emit(Delta(1));
        live.Complete(AssistantTurnState.Completed);

        AssistantTurnSubscription subscription = live.Subscribe();

        subscription.Backlog.Should().Equal(live.Snapshot());
        subscription.Reader.Completion.IsCompleted.Should().BeTrue("a finished turn's subscriber has nothing still to come");
    }
}
