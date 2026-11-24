using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Ttl = Anthropic.Models.Messages.Ttl;
using ReelBolt.Shared.Inference;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// <c>ReelBoltAgentBase.BuildChatOptions</c> builds one <see cref="ChatOptions"/> per agent, in
/// the constructor of a singleton, and reuses it on every run — long before the per-run provider is
/// known. It sets a Temperature for every agent (and sometimes TopP/TopK), plus an
/// <c>OpenAI.Chat.ChatCompletionOptions</c> through
/// <see cref="ChatOptions.RawRepresentationFactory"/> for any agent with a <c>ReasoningEffort</c>.
/// Claude rejects all three sampling parameters with an HTTP 400 on models released after Opus 4.6,
/// so these tests pin what makes routing such an agent to Anthropic safe: the rejected settings
/// never reach the inner client, and the caller's shared instance is never mutated in the process.
/// </summary>
public class AnthropicChatOptionsAdapterTests
{
    [Fact]
    public async Task GetResponseAsync_strips_the_raw_representation_factory()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new() { RawRepresentationFactory = _ => new object() };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        inner.LastOptions.Should().NotBeNull();
        inner.LastOptions!.RawRepresentationFactory.Should().BeNull();
    }

    [Fact]
    public async Task GetResponseAsync_does_not_mutate_the_callers_options()
    {
        // The agent holds this instance for its lifetime; mutating it would silently disable
        // reasoning_effort for every later run of that agent, including runs that resolve back to
        // an OpenAI provider.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        Func<IChatClient, object?> factory = _ => new object();
        ChatOptions options = new() { RawRepresentationFactory = factory };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        options.RawRepresentationFactory.Should().BeSameAs(factory);
        inner.LastOptions.Should().NotBeSameAs(options);
    }

    [Fact]
    public async Task GetResponseAsync_strips_the_sampling_parameters_Claude_rejects()
    {
        // The Anthropic SDK's own [Obsolete] text: "Models released after Claude Opus 4.6 do not
        // support setting temperature. A value of 1.0 will be accepted for backwards compatibility,
        // all other values will be rejected with a 400 error" — and likewise any top_k, and top_p
        // below 0.99. Every agent in this solution sets a Temperature, so forwarding these would
        // 400 every single agent run against a current Claude model.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new() { Temperature = 0.7f, TopP = 0.9f, TopK = 40 };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        inner.LastOptions!.Temperature.Should().BeNull();
        inner.LastOptions.TopP.Should().BeNull();
        inner.LastOptions.TopK.Should().BeNull();
    }

    [Fact]
    public async Task GetResponseAsync_strips_sampling_parameters_even_with_no_raw_factory()
    {
        // Guards against a fast path keyed only on RawRepresentationFactory: an agent with no
        // ReasoningEffort still sets a Temperature, and would still 400.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new() { Temperature = 0.3f };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        inner.LastOptions.Should().NotBeSameAs(options);
        inner.LastOptions!.Temperature.Should().BeNull();
        options.Temperature.Should().Be(0.3f);
    }

    [Fact]
    public async Task GetResponseAsync_preserves_the_options_Claude_does_accept()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        // MaxOutputTokens is deliberately above MinMaxOutputTokens here: a ceiling the caller set
        // generously is theirs to keep, and only a value UNDER the floor is ever rewritten.
        ChatOptions options = new()
        {
            Temperature = 0.7f,
            MaxOutputTokens = AnthropicChatOptionsAdapter.MinMaxOutputTokens * 2,
            ModelId = "claude-opus-5",
            RawRepresentationFactory = _ => new object()
        };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        inner.LastOptions!.MaxOutputTokens.Should().Be(AnthropicChatOptionsAdapter.MinMaxOutputTokens * 2);
        inner.LastOptions.ModelId.Should().Be("claude-opus-5");
    }

    [Fact]
    public async Task GetResponseAsync_passes_options_through_untouched_when_there_is_nothing_to_strip()
    {
        // The common case must not clone: cloning would be pure overhead on every single call.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new()
        {
            MaxOutputTokens = AnthropicChatOptionsAdapter.MinMaxOutputTokens,
            ModelId = "claude-opus-5"
        };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        inner.LastOptions.Should().BeSameAs(options);
    }

    // --- The thinking-token floor. A current Claude model spends reasoning tokens out of the same
    // max_tokens budget as its visible answer, so a ceiling sized for a non-thinking backend cuts
    // the answer off. Reproduced live by a room step: RoomSeatAgent sets MaxOutputTokens from
    // EditRoomStepConfig.MaxTurnTokens (default 220), and one seat's entire turn in a real
    // edit-room deliberation came back as "Materially the" — a truncated turn is a valid turn, so
    // nothing reported an error and the room proceeded with that participant reduced to noise. ---

    [Fact]
    public async Task GetResponseAsync_raises_a_max_output_tokens_below_the_thinking_floor()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new() { MaxOutputTokens = 220 };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        inner.LastOptions!.MaxOutputTokens.Should().Be(AnthropicChatOptionsAdapter.MinMaxOutputTokens);
    }

    [Fact]
    public async Task GetResponseAsync_does_not_mutate_the_callers_max_output_tokens_when_raising_it()
    {
        // Same shared-singleton hazard the sampling-parameter tests guard: RoomSeatAgent builds one
        // ChatOptions per turn today, but the agent-level instance this adapter also sees is reused
        // across providers, and raising a ceiling on it would leak into later OpenAI runs.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new() { MaxOutputTokens = 220 };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        options.MaxOutputTokens.Should().Be(220);
        inner.LastOptions.Should().NotBeSameAs(options);
    }

    [Fact]
    public async Task GetResponseAsync_leaves_an_unset_max_output_tokens_unset()
    {
        // No ceiling means the caller wants ChatClientFactory.AnthropicDefaultMaxOutputTokens (set
        // on the SDK client itself). Writing the floor in here would silently LOWER that.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new() { Temperature = 0.4f };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        inner.LastOptions!.MaxOutputTokens.Should().BeNull();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_raises_a_max_output_tokens_below_the_thinking_floor()
    {
        // The streaming path takes its own Normalize call — the same half that is easy to forget
        // for the sampling parameters.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new() { MaxOutputTokens = 220 };

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "hi")], options))
        {
        }

        inner.LastOptions!.MaxOutputTokens.Should().Be(AnthropicChatOptionsAdapter.MinMaxOutputTokens);
    }

    [Fact]
    public async Task GetResponseAsync_tolerates_null_options()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null);

        inner.LastOptions.Should().BeNull();
    }

    [Fact]
    public async Task GetStreamingResponseAsync_strips_the_raw_representation_factory()
    {
        // The streaming path is the one an agent run actually takes when a caller streams, so it
        // needs the same treatment — an easy half to forget.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new() { RawRepresentationFactory = _ => new object() };

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
        {
            // drain
        }

        inner.LastOptions.Should().NotBeNull();
        inner.LastOptions!.RawRepresentationFactory.Should().BeNull();
        options.RawRepresentationFactory.Should().NotBeNull();
    }

    [Fact]
    public void Dispose_disposes_the_client_it_was_given_ownership_of()
    {
        // Nothing else will: the Anthropic SDK's own IChatClient wrapper has an empty Dispose (its
        // IL body is a bare `ret`), so the AnthropicClient it wraps is never released unless this
        // adapter does it. CodeQL flagged exactly that as a missing-Dispose leak.
        RecordingChatClient inner = new();
        TrackedDisposable owned = new();
        AnthropicChatOptionsAdapter client = new(inner, owned);

        client.Dispose();

        owned.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_is_safe_when_no_ownership_was_transferred()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);

        Action act = () => client.Dispose();

        act.Should().NotThrow();
    }

    // --- "Dev/OAuth (Personal use only)" identity-prompt injection ---
    //
    // Regression coverage for https://github.com/anthropics/claude-code/issues/40515: Anthropic's
    // Messages API silently requires an OAuth-authenticated request's FIRST system block to be
    // exactly `ClaudeCodeIdentityPrompt`, as its own separate block — omitting it, or merging it
    // into the existing system text, both return an HTTP 400 with the unhelpful message "Error".
    // These tests pin that the "Dev/OAuth (Personal use only)" flag makes that phrase always the
    // leading system-role message.

    [Fact]
    public async Task GetResponseAsync_leaves_messages_untouched_when_personal_oauth_mode_is_off()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner, personalOAuthMode: false);
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, "You are a helpful video editing assistant."),
            new ChatMessage(ChatRole.User, "hi")
        ];

        await client.GetResponseAsync(messages);

        inner.LastMessages.Should().BeEquivalentTo(messages, o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task GetResponseAsync_prepends_the_identity_prompt_as_its_own_leading_system_message()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner, personalOAuthMode: true);

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "You are a helpful video editing assistant."),
            new ChatMessage(ChatRole.User, "hi")
        ]);

        inner.LastMessages.Should().HaveCount(3);
        inner.LastMessages![0].Role.Should().Be(ChatRole.System);
        inner.LastMessages[0].Text.Should().Be(AnthropicChatOptionsAdapter.ClaudeCodeIdentityPrompt);
        inner.LastMessages[1].Role.Should().Be(ChatRole.System);
        inner.LastMessages[1].Text.Should().Be("You are a helpful video editing assistant.");
        inner.LastMessages[2].Role.Should().Be(ChatRole.User);
    }

    [Fact]
    public async Task GetResponseAsync_inserts_the_identity_prompt_as_the_sole_system_message_when_there_is_none()
    {
        // The "ping" connectivity test (InferenceProviderTester.RunTestAsync) sends no system
        // message at all.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner, personalOAuthMode: true);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")]);

        inner.LastMessages.Should().HaveCount(2);
        inner.LastMessages![0].Role.Should().Be(ChatRole.System);
        inner.LastMessages[0].Text.Should().Be(AnthropicChatOptionsAdapter.ClaudeCodeIdentityPrompt);
        inner.LastMessages[1].Role.Should().Be(ChatRole.User);
    }

    [Fact]
    public async Task GetResponseAsync_does_not_duplicate_an_already_prefixed_system_message()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner, personalOAuthMode: true);
        string alreadyPrefixed = AnthropicChatOptionsAdapter.ClaudeCodeIdentityPrompt + " You also help with video editing.";

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, alreadyPrefixed),
            new ChatMessage(ChatRole.User, "hi")
        ]);

        inner.LastMessages.Should().HaveCount(2);
        inner.LastMessages![0].Text.Should().Be(alreadyPrefixed);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_also_prepends_the_identity_prompt()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner, personalOAuthMode: true);

        await foreach (var _ in client.GetStreamingResponseAsync(
        [
            new ChatMessage(ChatRole.System, "You are a helpful video editing assistant."),
            new ChatMessage(ChatRole.User, "hi")
        ]))
        {
            // drain
        }

        inner.LastMessages.Should().HaveCount(3);
        inner.LastMessages![0].Text.Should().Be(AnthropicChatOptionsAdapter.ClaudeCodeIdentityPrompt);
    }

    // --- Prompt caching (initiative anthropic-prompt-caching) -------------------------------------
    //
    // A marker on an unstable prefix caches nothing and still pays the write premium, so these pin
    // the boundary as much as the presence of the marker. Read the initiative's audit.md for the
    // prefix-stability reasoning these tests encode.

    [Fact]
    public async Task SystemAndTurn_promotes_instructions_to_a_marked_system_block()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new ChatOptions { Instructions = "STABLE" }
            .WithMode(AnthropicPromptCacheMode.SystemAndTurn);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        // The SDK adds instructions after the messages' system blocks. Promotion must preserve that
        // position, so the marker sits at the end of tools + system.
        inner.LastOptions!.Instructions.Should().BeNull();
        ChatMessage system = inner.LastMessages!.Single(m => m.Role == ChatRole.System);
        system.Text.Should().Be("STABLE");
        CacheOf(system)!.Ttl!.Value().Should().Be(Ttl.Ttl5m);
        MarkerCount(inner.LastMessages).Should().Be(2, "one system breakpoint and one newest-turn breakpoint");
    }

    [Fact]
    public async Task TurnOnly_leaves_instructions_in_place()
    {
        // The assistant's instructions carry per-turn session context; promoting them to a marked
        // system block would cache a prefix that changes every turn.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new ChatOptions { Instructions = "PER-TURN" }
            .WithMode(AnthropicPromptCacheMode.TurnOnly);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        inner.LastOptions!.Instructions.Should().Be("PER-TURN");
        inner.LastMessages!.Should().NotContain(m => m.Role == ChatRole.System);
        MarkerCount(inner.LastMessages).Should().Be(1, "only the newest-turn breakpoint");
    }

    [Fact]
    public async Task No_mode_leaves_the_request_byte_identical()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new() { Temperature = 0.7f };
        ChatMessage user = new(ChatRole.User, "hi");

        await client.GetResponseAsync([user], options);

        inner.LastOptions.Should().NotBeSameAs(options, "the sampling parameters still have to be stripped");
        MarkerCount(inner.LastMessages!).Should().Be(0);
        inner.LastMessages![0].Should().BeSameAs(user, "no message is cloned when nothing is cached");
        options.AdditionalProperties.Should().BeNull();
    }

    [Fact]
    public async Task Marks_the_last_non_system_message_and_only_that_one()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new ChatOptions().WithMode(AnthropicPromptCacheMode.TurnOnly);
        ChatMessage first = new(ChatRole.User, "a");
        ChatMessage second = new(ChatRole.Assistant, "b");
        ChatMessage last = new(ChatRole.User, "c");

        await client.GetResponseAsync([first, second, last], options);

        MarkerCount(inner.LastMessages!).Should().Be(1);
        CacheOf(inner.LastMessages![2]).Should().NotBeNull();
        CacheOf(inner.LastMessages![0]).Should().BeNull();
        CacheOf(inner.LastMessages![1]).Should().BeNull();
    }

    [Fact]
    public async Task SystemAndTurn_marks_the_last_system_message_when_there_are_no_instructions()
    {
        // Room seats carry their shared charter as a system message, not as ChatOptions.Instructions.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new ChatOptions().WithMode(AnthropicPromptCacheMode.SystemAndTurn);

        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, "room charter"),
            new ChatMessage(ChatRole.User, "seat directive")
        ], options);

        ChatMessage system = inner.LastMessages!.Single(m => m.Role == ChatRole.System);
        CacheOf(system).Should().NotBeNull();
        MarkerCount(inner.LastMessages).Should().Be(2);
    }

    [Fact]
    public async Task Does_not_mutate_the_callers_messages_or_options()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new ChatOptions { Instructions = "STABLE" }
            .WithMode(AnthropicPromptCacheMode.SystemAndTurn);
        ChatMessage user = new(ChatRole.User, "hi");

        await client.GetResponseAsync([user], options);

        options.Instructions.Should().Be("STABLE");
        user.Contents.Should().OnlyContain(c => AnthropicPromptCaching.CacheControlOf(c) == null);
        AnthropicPromptCaching.ModeOf(options).Should().Be(AnthropicPromptCacheMode.SystemAndTurn);
    }

    [Fact]
    public async Task Does_not_accumulate_markers_across_a_tool_loop()
    {
        // The message list is the tool loop's history and is replayed every iteration. An in-place
        // marker would leave one breakpoint per iteration; Anthropic rejects a request with more
        // than four.
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new ChatOptions { Instructions = "STABLE" }
            .WithMode(AnthropicPromptCacheMode.SystemAndTurn);
        List<ChatMessage> history = [new ChatMessage(ChatRole.User, "turn 1")];

        for (int i = 0; i < 3; i++)
        {
            await client.GetResponseAsync(history, options);
            MarkerCount(inner.LastMessages!).Should().Be(2);
            MarkerCount(history).Should().Be(0, "the adapter must never write into the replayed history");
            history.Add(new ChatMessage(ChatRole.Assistant, $"answer {i}"));
            history.Add(new ChatMessage(ChatRole.User, $"turn {i + 2}"));
        }
    }

    [Fact]
    public async Task System_breakpoint_follows_the_identity_block()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner, personalOAuthMode: true);
        ChatOptions options = new ChatOptions { Instructions = "STABLE" }
            .WithMode(AnthropicPromptCacheMode.SystemAndTurn);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        List<ChatMessage> systems = inner.LastMessages!.Where(m => m.Role == ChatRole.System).ToList();
        systems.Should().HaveCount(2);
        systems[0].Text.Should().Be(AnthropicChatOptionsAdapter.ClaudeCodeIdentityPrompt);
        CacheOf(systems[0]).Should().BeNull("the identity block is the leading block, never the breakpoint");
        systems[1].Text.Should().Be("STABLE");
        CacheOf(systems[1]).Should().NotBeNull();
    }

    [Fact]
    public async Task Streaming_path_places_the_same_breakpoints()
    {
        RecordingChatClient inner = new();
        AnthropicChatOptionsAdapter client = new(inner);
        ChatOptions options = new ChatOptions { Instructions = "STABLE" }
            .WithMode(AnthropicPromptCacheMode.SystemAndTurn);

        await foreach (var _ in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options))
        {
            // drain
        }

        MarkerCount(inner.LastMessages!).Should().Be(2);
    }

    [Fact]
    public void WithMode_off_removes_the_key_and_roundtrips_the_other_modes()
    {
        ChatOptions options = new();
        AnthropicPromptCaching.ModeOf(options).Should().Be(AnthropicPromptCacheMode.Off);

        options.WithMode(AnthropicPromptCacheMode.SystemAndTurn);
        AnthropicPromptCaching.ModeOf(options).Should().Be(AnthropicPromptCacheMode.SystemAndTurn);

        options.WithMode(AnthropicPromptCacheMode.TurnOnly);
        AnthropicPromptCaching.ModeOf(options).Should().Be(AnthropicPromptCacheMode.TurnOnly);

        options.WithMode(AnthropicPromptCacheMode.Off);
        AnthropicPromptCaching.ModeOf(options).Should().Be(AnthropicPromptCacheMode.Off);
        options.AdditionalProperties.Should().BeEmpty();
    }

    private static Anthropic.Models.Messages.CacheControlEphemeral? CacheOf(ChatMessage message) =>
        message.Contents.Select(AnthropicPromptCaching.CacheControlOf).LastOrDefault(c => c is not null);

    private static int MarkerCount(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => m.Contents.Count(c => AnthropicPromptCaching.CacheControlOf(c) is not null));

    private sealed class TrackedDisposable : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    /// <summary>
    /// Captures the <see cref="ChatOptions"/> it was handed. Same hand-written-fake approach the
    /// room executor tests use rather than Moq, since <see cref="IChatClient"/> has an async
    /// iterator member that is awkward to express through a mock.
    /// </summary>
    private sealed class RecordingChatClient : IChatClient
    {
        public ChatOptions? LastOptions { get; private set; }
        public List<ChatMessage>? LastMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            LastMessages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            LastMessages = messages.ToList();
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
