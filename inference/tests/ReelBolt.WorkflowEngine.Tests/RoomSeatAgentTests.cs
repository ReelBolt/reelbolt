using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Inference;
using ReelBolt.WorkflowEngine.Agents.Rooms;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <see cref="RoomSeatAgent"/> wraps a chat-client-backed inner <see cref="AIAgent"/> (constructed
/// with <c>name: agentType.ToString()</c>, e.g. "VideoStoryEditor" — see
/// <c>RoomStepExecutorBase.BuildInnerAgentAsync</c>) so the room's transcript reads with the seat's
/// persona name instead. These tests use an inner <see cref="FakeAgent"/> that deliberately returns
/// messages/updates authored as the underlying AGENT TYPE name, never the seat name, so a passing
/// assertion proves the override actually happened rather than merely echoing an inner agent that
/// already had the right name.
/// </summary>
public class RoomSeatAgentTests
{
    private const string InnerAgentTypeName = "VideoStoryEditor";
    private const string SeatName = "PacingEditor";

    private static RoomSeatAgent BuildSeatAgent(AIAgent innerAgent) =>
        new(
            innerAgent,
            seatName: SeatName,
            turnDirective: "speak as PacingEditor",
            temperature: 0.5f,
            maxOutputTokens: 256,
            reasoningEffort: null,
            onTurnCompleted: _ => Task.CompletedTask);

    [Fact]
    public async Task RunAsync_overrides_AuthorName_to_the_seat_name_on_a_successful_turn()
    {
        RoomSeatAgent seatAgent = BuildSeatAgent(new FakeAgent(InnerAgentTypeName, "I think we should keep s1."));

        AgentResponse response = await seatAgent.RunAsync("opening message", session: null, cancellationToken: CancellationToken.None);

        response.Messages.Should().NotBeEmpty();
        response.Messages.Should().OnlyContain(m => m.AuthorName == SeatName,
            "the raw transcript's AuthorName must read with the seat's persona name, not the underlying agent type name");
    }

    [Fact]
    public async Task RunStreamingAsync_overrides_AuthorName_to_the_seat_name_on_a_successful_turn()
    {
        RoomSeatAgent seatAgent = BuildSeatAgent(new FakeAgent(InnerAgentTypeName, "Agreed, and also s2."));

        List<AgentResponseUpdate> updates = [];
        await foreach (AgentResponseUpdate update in seatAgent.RunStreamingAsync(
            "opening message", session: null, cancellationToken: CancellationToken.None))
        {
            updates.Add(update);
        }

        updates.Should().NotBeEmpty();
        updates.Should().OnlyContain(u => u.AuthorName == SeatName,
            "streamed updates must also read with the seat's persona name, not the underlying agent type name");
    }

    /// <summary>Minimal concrete <see cref="AgentSession"/> — never inspected by these tests, only needs to exist to satisfy <see cref="AIAgent"/>'s abstract session methods.</summary>
    private sealed class FakeSession : AgentSession
    {
    }

    [Fact]
    public async Task Room_seat_turns_opt_into_system_and_turn_prompt_caching()
    {
        // The shared room charter arrives through the inner agent's Instructions and the per-seat
        // directive is appended last, so the system prefix is stable for the whole room. See the
        // initiative's audit.md.
        FakeAgent inner = new(InnerAgentTypeName, "Agreed.");
        RoomSeatAgent seatAgent = BuildSeatAgent(inner);

        await seatAgent.RunAsync("opening message", session: null, cancellationToken: CancellationToken.None);

        ChatClientAgentRunOptions runOptions = inner.LastOptions.Should().BeOfType<ChatClientAgentRunOptions>().Subject;
        AnthropicPromptCaching.ModeOf(runOptions.ChatOptions).Should()
            .Be(AnthropicPromptCacheMode.SystemAndTurn);
    }

    /// <summary>
    /// Trivial concrete <see cref="AIAgent"/> standing in for a real chat-client-backed agent.
    /// Deliberately authors its messages/updates as <paramref name="name"/> (the underlying agent
    /// TYPE name in production, e.g. "VideoStoryEditor") so a test wrapping this in
    /// <see cref="RoomSeatAgent"/> can prove the wrapper overrides that to the seat's persona name.
    /// </summary>
    private sealed class FakeAgent : AIAgent
    {
        private readonly string _name;
        private readonly string _text;

        public FakeAgent(string name, string text)
        {
            _name = name;
            _text = text;
        }

        public override string Name => _name;

        /// <summary>Captures the per-turn options <see cref="RoomSeatAgent"/> injects.</summary>
        public AgentRunOptions? LastOptions { get; private set; }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeSession());

        protected override ValueTask<System.Text.Json.JsonElement> SerializeSessionCoreAsync(
            AgentSession session, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new System.Text.Json.JsonElement());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            System.Text.Json.JsonElement serializedState, System.Text.Json.JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentSession>(new FakeSession());

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, _text) { AuthorName = _name }));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LastOptions = options;
            await Task.Yield();
            yield return new AgentResponseUpdate(ChatRole.Assistant, _text) { AuthorName = _name };
        }
    }
}
