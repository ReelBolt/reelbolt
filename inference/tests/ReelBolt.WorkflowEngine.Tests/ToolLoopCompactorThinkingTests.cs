using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using ReelBolt.WorkflowEngine.Execution.Context;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Tool-loop compaction against Anthropic's thinking-block rule. The Anthropic SDK maps a
/// <c>thinking</c> block to <see cref="TextReasoningContent"/> with its signature in
/// <see cref="TextReasoningContent.ProtectedData"/>, and the API rejects a block when "it was
/// created in a different conversation, or the messages before it have changed since"
/// (Anthropic SDK 12.55.0, <c>BetaThinkingBlockBinding.PrefixMismatchBehavior</c>). The fake
/// provider below enforces exactly that rule, so compaction that rewrites anything ahead of a
/// signed thinking block fails the way production did:
/// <c>400 invalid_request_error: ... Invalid signature in thinking block</c>.
/// </summary>
public class ToolLoopCompactorThinkingTests
{
    private const int ResultChars = 100_000;

    [Fact]
    public async Task A_tool_loop_over_budget_never_changes_what_preceded_a_signed_thinking_block()
    {
        BindingCheckingChatClient provider = new(toolCallTurns: 6);
        ToolLoopRunStats stats = new();
        IChatClient client = ToolLoopPipeline.Wrap(
            provider,
            new ToolLoopBudgetOptions { MaxContextChars = 250_000, RecentToolResultsVerbatim = 2 },
            new ToolLoopOptions(),
            stats);

        int reads = 0;
        AIFunction read = AIFunctionFactory.Create(() => PayloadFor(reads++, ResultChars), "read_file");

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "author the scene")],
            new ChatOptions { Tools = [read] });

        response.Text.Should().Be("done");
        provider.Calls.Should().Be(7);
        stats.PeakContextChars.Should().BeGreaterThan(600_000);
    }

    [Fact]
    public void Assistant_messages_with_reasoning_are_forwarded_unchanged_and_every_call_keeps_its_result()
    {
        // Large string arguments too, so the last-resort call-argument phase has something to take.
        List<ChatMessage> messages = [new(ChatRole.User, "author the scene")];
        for (int i = 0; i < 5; i++)
        {
            messages.Add(ThinkingAssistant(i, writeChars: 60_000));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", PayloadFor(i, ResultChars))]));
        }

        ToolLoopCompactor.Outcome outcome = ToolLoopCompactor.Compact(
            messages, 0, new ToolLoopBudgetOptions { MaxContextChars = 200_000 }, bindsSignedReasoning: true);

        outcome.Messages.Should().HaveCount(messages.Count);
        for (int m = 0; m < messages.Count; m++)
        {
            if (!messages[m].Contents.OfType<TextReasoningContent>().Any())
                continue;
            outcome.Messages[m].Contents.Should().Equal(messages[m].Contents, $"message {m} carries a thinking block");
        }

        List<string> callIds = outcome.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToList();
        List<string> resultIds = outcome.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId).ToList();
        callIds.Should().Equal(resultIds);
    }

    [Fact]
    public void Results_after_the_last_thinking_block_are_still_digested_except_the_latest()
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "author the scene"),
            ThinkingAssistant(0, writeChars: 0, parallelCalls: 3),
            new(ChatRole.Tool, [new FunctionResultContent("call0", PayloadFor(0, ResultChars))]),
            new(ChatRole.Tool, [new FunctionResultContent("call0b", PayloadFor(1, ResultChars))]),
            new(ChatRole.Tool, [new FunctionResultContent("call0c", PayloadFor(2, ResultChars))]),
        ];

        ToolLoopCompactor.Outcome outcome = ToolLoopCompactor.Compact(
            messages, 0, new ToolLoopBudgetOptions { MaxContextChars = 150_000, RecentToolResultsVerbatim = 1 },
            bindsSignedReasoning: true);

        outcome.Messages[1].Should().BeSameAs(messages[1]);
        ResultText(outcome.Messages[2]).Should().StartWith("[compacted:");
        ResultText(outcome.Messages[3]).Should().StartWith("[compacted:");
        ResultText(outcome.Messages[4]).Should().Be(PayloadFor(2, ResultChars));
        outcome.ForwardedChars.Should().BeLessThan(outcome.RawChars);
    }

    [Fact]
    public void Unsigned_reasoning_does_not_freeze_compaction()
    {
        List<ChatMessage> messages = [new(ChatRole.User, "author the scene")];
        for (int i = 0; i < 4; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new TextReasoningContent($"plan {i}"), new FunctionCallContent($"call{i}", "read_file")]));
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", PayloadFor(i, ResultChars))]));
        }

        ToolLoopCompactor.Outcome outcome = ToolLoopCompactor.Compact(
            messages, 0, new ToolLoopBudgetOptions { MaxContextChars = 250_000, RecentToolResultsVerbatim = 2 },
            bindsSignedReasoning: true);

        ResultText(outcome.Messages[2]).Should().StartWith("[compacted:");
    }

    [Fact]
    public void The_real_Anthropic_client_chain_reports_the_provider_name_the_freeze_is_keyed_on()
    {
        // ToolLoopCompactingChatClient decides "Anthropic" from ChatClientMetadata.ProviderName; this
        // pins that the SDK adapter reports it and that ReelBolt's adapter lets it through.
        using Anthropic.AnthropicClient sdk = new(new Anthropic.Core.ClientOptions { ApiKey = "sk-ant-test" });
        using IChatClient chain = new ReelBolt.Shared.Inference.AnthropicChatOptionsAdapter(sdk.AsIChatClient("claude-test", 1_000));

        chain.GetService<ChatClientMetadata>()!.ProviderName.Should().Be("anthropic");
    }

    [Fact]
    public void OpenAI_encrypted_reasoning_compacts_exactly_as_unsigned_reasoning_does()
    {
        // The OpenAI Responses adapter maps reasoning.encrypted_content to ProtectedData too
        // (ChatClientFactory asks for it whenever reasoning is on). That state is not bound to the
        // prefix, so it must not freeze compaction: the result is the pre-T-018 compaction.
        List<ChatMessage> encrypted = [new(ChatRole.User, "author the scene")];
        List<ChatMessage> plain = [new(ChatRole.User, "author the scene")];
        for (int i = 0; i < 4; i++)
        {
            encrypted.Add(new ChatMessage(ChatRole.Assistant,
                [new TextReasoningContent($"plan {i}") { ProtectedData = $"gAAAAB-enc-{i}" }, new FunctionCallContent($"call{i}", "read_file")]));
            plain.Add(new ChatMessage(ChatRole.Assistant,
                [new TextReasoningContent($"plan {i}"), new FunctionCallContent($"call{i}", "read_file")]));
            encrypted.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", PayloadFor(i, ResultChars))]));
            plain.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", PayloadFor(i, ResultChars))]));
        }

        ToolLoopBudgetOptions budget = new() { MaxContextChars = 250_000, RecentToolResultsVerbatim = 2 };
        ToolLoopCompactor.Outcome fromEncrypted = ToolLoopCompactor.Compact(encrypted, 0, budget, bindsSignedReasoning: false);
        ToolLoopCompactor.Outcome fromPlain = ToolLoopCompactor.Compact(plain, 0, budget, bindsSignedReasoning: false);

        ResultText(fromEncrypted.Messages[2]).Should().StartWith("[compacted:");
        fromEncrypted.ForwardedChars.Should().Be(fromPlain.ForwardedChars);
        fromEncrypted.ForwardedChars.Should().BeLessThanOrEqualTo(250_000);
        for (int m = 0; m < encrypted.Count; m++)
        {
            if (encrypted[m].Role == ChatRole.Tool)
                ResultText(fromEncrypted.Messages[m]).Should().Be(ResultText(fromPlain.Messages[m]), $"message {m}");
        }
    }

    [Fact]
    public async Task An_OpenAI_tool_loop_with_encrypted_reasoning_keeps_compacting_under_budget()
    {
        BindingCheckingChatClient provider = new(toolCallTurns: 6, providerName: "openai");
        ToolLoopRunStats stats = new();
        IChatClient client = ToolLoopPipeline.Wrap(
            provider,
            new ToolLoopBudgetOptions { MaxContextChars = 250_000, RecentToolResultsVerbatim = 2 },
            new ToolLoopOptions(),
            stats);

        int reads = 0;
        AIFunction read = AIFunctionFactory.Create(() => PayloadFor(reads++, ResultChars), "read_file");

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "author the scene")],
            new ChatOptions { Tools = [read] });

        response.Text.Should().Be("done");
        stats.PeakForwardedChars.Should().BeLessThanOrEqualTo(250_000);
        stats.CompactedCalls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_frozen_prefix_over_the_hard_ceiling_fails_with_CONTEXT_TOO_LARGE_before_the_provider_is_called()
    {
        BindingCheckingChatClient provider = new(toolCallTurns: 20);
        IChatClient client = ToolLoopPipeline.Wrap(
            provider,
            new ToolLoopBudgetOptions { MaxContextChars = 250_000, RecentToolResultsVerbatim = 2, HardMaxChars = 450_000 },
            new ToolLoopOptions(),
            new ToolLoopRunStats());

        int reads = 0;
        AIFunction read = AIFunctionFactory.Create(() => PayloadFor(reads++, ResultChars), "read_file");

        Func<Task> run = () => client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "author the scene")],
            new ChatOptions { Tools = [read] });

        ContextTooLargeException thrown = (await run.Should().ThrowAsync<ContextTooLargeException>()).Which;
        thrown.Code.Should().Be("CONTEXT_TOO_LARGE");
        thrown.Message.Should().Contain(
            "This step read more than the model can hold at once. Ask it to read files in slices (outline + line ranges) instead of whole files.");
        // Four results of 100k each fit under 450k; the fifth would not, and that call is never made.
        provider.Calls.Should().Be(5);
    }

    [Fact]
    public async Task The_hard_ceiling_does_not_apply_to_OpenAI_runs()
    {
        BindingCheckingChatClient provider = new(toolCallTurns: 6, providerName: "openai");
        IChatClient client = ToolLoopPipeline.Wrap(
            provider,
            new ToolLoopBudgetOptions { MaxContextChars = 10_000_000, HardMaxChars = 150_000 },
            new ToolLoopOptions(),
            new ToolLoopRunStats());

        int reads = 0;
        AIFunction read = AIFunctionFactory.Create(() => PayloadFor(reads++, ResultChars), "read_file");

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "author the scene")],
            new ChatOptions { Tools = [read] });

        response.Text.Should().Be("done");
    }

    [Fact]
    public async Task A_prefix_that_already_holds_digests_is_replayed_byte_identical_on_every_later_call()
    {
        // Compaction happens BEFORE the first signed block: the run starts from a history of big
        // unsigned tool results (an earlier turn's reads), already over budget on call one. So the
        // prefix the first signature is bound to contains digests, and every later call must
        // re-derive exactly those digests.
        BindingCheckingChatClient provider = new(toolCallTurns: 4);
        IChatClient client = ToolLoopPipeline.Wrap(
            provider,
            new ToolLoopBudgetOptions { MaxContextChars = 250_000, RecentToolResultsVerbatim = 1 },
            new ToolLoopOptions(),
            new ToolLoopRunStats());

        List<ChatMessage> history = [new(ChatRole.User, "author the scene")];
        for (int i = 0; i < 4; i++)
        {
            history.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"old{i}", "read_file")]));
            history.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"old{i}", PayloadFor(50 + i, ResultChars))]));
        }

        history.Add(new ChatMessage(ChatRole.User, "now write it"));

        int reads = 0;
        AIFunction read = AIFunctionFactory.Create(() => PayloadFor(reads++, ResultChars), "read_file");

        ChatResponse response = await client.GetResponseAsync(history, new ChatOptions { Tools = [read] });

        response.Text.Should().Be("done");
        provider.Calls.Should().Be(5);
        provider.BoundPrefixes[0].Should().Contain("[compacted:");
        for (int call = 1; call < provider.Sent.Count; call++)
            provider.Sent[call].Should().StartWith(provider.BoundPrefixes[0], $"call {call} replays the first signed prefix");
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static ChatMessage ThinkingAssistant(int index, int writeChars, int parallelCalls = 1)
    {
        List<AIContent> contents = [new TextReasoningContent($"thinking {index}") { ProtectedData = $"sig-{index}" }];
        for (int c = 0; c < parallelCalls; c++)
        {
            string id = c == 0 ? $"call{index}" : $"call{index}{(char)('a' + c)}";
            Dictionary<string, object?> arguments = new() { ["path"] = $"src/Scene{index}.tsx" };
            if (writeChars > 0)
                arguments["content"] = PayloadFor(100 + index, writeChars);
            contents.Add(new FunctionCallContent(id, "write_file", arguments));
        }

        return new ChatMessage(ChatRole.Assistant, contents);
    }

    private static string PayloadFor(int index, int chars)
    {
        StringBuilder builder = new();
        for (int line = 0; builder.Length < chars; line++)
            builder.Append("line-").Append(index).Append('-').Append(line).Append('\n');
        return builder.ToString(0, chars);
    }

    private static string ResultText(ChatMessage message) => ((FunctionResultContent)message.Contents[0]).Result switch
    {
        string s => s,
        JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText(),
        null => string.Empty,
        object o => o.ToString()!,
    };

    /// <summary>
    /// An in-memory provider that answers with signed thinking plus one tool call per turn, and
    /// enforces Anthropic's block binding: a signed thinking block sent back must be preceded by
    /// exactly the messages that were sent when it was created.
    /// </summary>
    private sealed class BindingCheckingChatClient(int toolCallTurns, string providerName = "anthropic") : IChatClient
    {
        private readonly Dictionary<string, string> _boundPrefix = [];

        public int Calls { get; private set; }

        /// <summary>Every prefix a signature was bound to, in the order the blocks were produced.</summary>
        public List<string> BoundPrefixes { get; } = [];

        /// <summary>The serialized list of every call, as sent.</summary>
        public List<string> Sent { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatMessage> sent = messages.ToList();
            Sent.Add(Serialize(sent));
            for (int m = 0; providerName == "anthropic" && m < sent.Count; m++)
            {
                foreach (TextReasoningContent thinking in sent[m].Contents.OfType<TextReasoningContent>())
                {
                    string signature = thinking.ProtectedData!;
                    if (!_boundPrefix.TryGetValue(signature, out string? bound) || bound != Serialize(sent.Take(m)))
                        throw new InvalidOperationException(
                            $"400 invalid_request_error: messages.{m}.content.0: Invalid signature in thinking block");
                }
            }

            int turn = Calls++;
            if (turn >= toolCallTurns)
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));

            string sig = $"sig-{turn}";
            _boundPrefix[sig] = Serialize(sent);
            BoundPrefixes.Add(_boundPrefix[sig]);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new TextReasoningContent($"thinking {turn}") { ProtectedData = sig },
                new FunctionCallContent($"call{turn}", "read_file"),
            ])));
        }

        private static string Serialize(IEnumerable<ChatMessage> messages)
        {
            StringBuilder builder = new();
            foreach (ChatMessage message in messages)
            {
                builder.Append(message.Role.Value).Append('|');
                foreach (AIContent content in message.Contents)
                {
                    builder.Append(content switch
                    {
                        TextContent text => "text:" + text.Text,
                        TextReasoningContent reasoning => "thinking:" + reasoning.Text + ":" + reasoning.ProtectedData,
                        FunctionCallContent call => "call:" + call.CallId + ":" + call.Name + ":" + JsonSerializer.Serialize(call.Arguments),
                        FunctionResultContent result => "result:" + result.CallId + ":" + JsonSerializer.Serialize(result.Result),
                        _ => content.GetType().Name,
                    }).Append('\u001f');
                }

                builder.Append('\u001e');
            }

            return builder.ToString();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata(providerName) : null;

        public void Dispose()
        {
        }
    }
}
