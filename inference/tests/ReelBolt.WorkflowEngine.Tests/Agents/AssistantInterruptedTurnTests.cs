using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Assistant;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The record a turn leaves when it stops before it can answer, and the hub's notion of "nobody is attached" that
/// decides when such a turn is cut. A dropped turn never loses its reply or hides a change.
/// </summary>
public class AssistantInterruptedTurnTests
{
    private static AssistantActionFrame Frame(Guid id, string state, string title) =>
        new(id, state, AssistantActionMode.Ask, "project", "normal", title);

    [Fact]
    public void A_turn_that_did_nothing_says_nothing_was_changed()
    {
        var turn = new AssistantInterruptedTurn();

        turn.HasActions.Should().BeFalse();
        turn.Compose(wallClockFired: false).Should().Contain("interrupted").And.Contain("Nothing was changed");
    }

    [Fact]
    public void Each_action_is_listed_with_where_it_ended_and_the_newest_state_wins()
    {
        var turn = new AssistantInterruptedTurn();
        Guid done = Guid.NewGuid();
        Guid declined = Guid.NewGuid();
        Guid started = Guid.NewGuid();
        Guid waiting = Guid.NewGuid();

        turn.Note(Frame(done, AssistantActionState.Pending, "Rename the project"));
        turn.Note(Frame(done, AssistantActionState.Allowed, "Rename the project"));
        turn.Note(Frame(done, AssistantActionState.Done, "Rename the project"));
        turn.Note(Frame(declined, AssistantActionState.Declined, "Delete a file"));
        turn.Note(Frame(started, AssistantActionState.Allowed, "Start the run"));
        turn.Note(Frame(waiting, AssistantActionState.Pending, "Delete the project"));

        string text = turn.Compose(wallClockFired: false);

        turn.HasActions.Should().BeTrue();
        text.Should().Contain("- Rename the project: done.");
        text.Should().Contain("- Delete a file: you chose not to allow it");
        text.Should().Contain("- Start the run: it was allowed and started, but I could not confirm it finished");
        text.Should().Contain("- Delete the project: it was waiting for your OK and was not done.");
        text.Should().NotContain("timed out");
    }

    [Fact]
    public void A_turn_stopped_by_its_time_limit_says_it_took_too_long()
    {
        new AssistantInterruptedTurn().Compose(wallClockFired: true).Should().Contain("took too long");
    }

    [Fact]
    public async Task A_turn_is_unattended_only_without_subscribers_or_an_open_card()
    {
        var hub = new AssistantTurnHub();
        Guid user = Guid.NewGuid();
        AssistantLiveTurn live = hub.Begin(Guid.NewGuid(), user, null);

        live.Touch();
        live.IsUnattendedFor(TimeSpan.FromMilliseconds(100)).Should().BeFalse("it was just looked at");
        await Task.Delay(200);
        live.IsUnattendedFor(TimeSpan.FromMilliseconds(100)).Should().BeTrue();

        // A re-attached subscriber means somebody is looking, however long it has been.
        using (live.Subscribe())
        {
            live.SubscriberCount.Should().Be(1);
            live.IsUnattendedFor(TimeSpan.Zero).Should().BeFalse();
        }

        // Leaving counts as the last time anybody looked.
        live.IsUnattendedFor(TimeSpan.FromSeconds(30)).Should().BeFalse();

        // An open card is waiting on a person and has its own timeout.
        await Task.Delay(120);
        live.MarkAwaitingApproval(Guid.NewGuid());
        live.IsUnattendedFor(TimeSpan.Zero).Should().BeFalse("a card is open");

        live.MarkDecided();
        live.IsUnattendedFor(TimeSpan.FromSeconds(30)).Should().BeFalse("answering a card restarts the clock");
    }

    [Fact]
    public void A_ping_reaches_every_subscriber_and_is_never_replayed()
    {
        var hub = new AssistantTurnHub();
        AssistantLiveTurn live = hub.Begin(Guid.NewGuid(), Guid.NewGuid(), null);
        using AssistantTurnSubscription a = live.Subscribe();
        using AssistantTurnSubscription b = live.Subscribe();

        live.Ping();

        a.Reader.TryRead(out string? first).Should().BeTrue();
        b.Reader.TryRead(out string? second).Should().BeTrue();
        first.Should().Be(": ping\n\n");
        second.Should().Be(": ping\n\n");
        live.Subscribe().Backlog.Should().BeEmpty("a ping belongs to a live connection, not to the replay");
    }
}
