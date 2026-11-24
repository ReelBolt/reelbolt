using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ReelBolt.WorkflowEngine.Execution.Context;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The tool-loop compaction safety net: a conversation whose tool results pile up inside
/// <c>FunctionInvokingChatClient</c>'s loop must be bounded on the way to the provider, without
/// ever orphaning a tool call, touching the latest result, or changing input that already fits.
/// </summary>
public class ToolLoopCompactingChatClientTests
{
    private const int ResultChars = 100_000;

    // ---------------------------------------------------------------------------------------
    // Compaction of the list forwarded to the provider
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Under_budget_input_is_forwarded_as_the_very_same_list()
    {
        RecordingChatClient inner = new();
        ToolLoopCompactingChatClient client = Client(inner, new ToolLoopBudgetOptions { MaxContextChars = 1_000_000 });
        List<ChatMessage> messages = Conversation(pairs: 3, resultChars: 10_000);
        List<ChatMessage> snapshot = [.. messages];

        await client.GetResponseAsync(messages);

        inner.Calls.Should().ContainSingle();
        inner.Calls[0].Received.Should().BeSameAs(messages);
        messages.Should().Equal(snapshot);
        TextOfResult(inner.Calls[0].Messages[2]).Should().Be(PayloadFor(0, 10_000));
    }

    [Fact]
    public async Task Ten_100kb_results_are_forwarded_under_budget_with_recent_ones_verbatim_and_older_ones_digested()
    {
        RecordingChatClient inner = new();
        ToolLoopBudgetOptions budget = new() { MaxContextChars = 350_000, RecentToolResultsVerbatim = 3, DigestMaxChars = 2_000 };
        List<ChatMessage> messages = Conversation(pairs: 10, resultChars: ResultChars);
        ToolLoopRunStats stats = new();

        await new ToolLoopCompactingChatClient(inner, budget, maxIterations: 40, stats).GetResponseAsync(messages);

        IReadOnlyList<ChatMessage> forwarded = inner.Calls[0].Messages;
        ToolLoopCompactor.MeasureMessages(forwarded).Should().BeLessThanOrEqualTo(budget.MaxContextChars);

        List<FunctionResultContent> results = ResultsOf(forwarded);
        results.Should().HaveCount(10);

        // The latest three are the originals, byte for byte.
        for (int i = 7; i < 10; i++)
            ResultText(results[i]).Should().Be(PayloadFor(i, ResultChars));

        // The seven before them are digested: small, marked, and still starting with their own content.
        for (int i = 0; i < 7; i++)
        {
            string text = ResultText(results[i]);
            text.Should().StartWith($"[compacted: {ResultChars} chars, ");
            text.Length.Should().BeLessThan(2_200);
            text.Should().Contain($"line-{i}-0");
        }

        AssertEveryCallHasItsResult(forwarded);
        stats.PeakContextChars.Should().BeGreaterThan(1_000_000);
        stats.MaxCharsCompacted.Should().BeGreaterThan(600_000);
        stats.CompactedCalls.Should().Be(1);
    }

    [Fact]
    public async Task The_callers_own_messages_are_never_mutated()
    {
        RecordingChatClient inner = new();
        List<ChatMessage> messages = Conversation(pairs: 6, resultChars: ResultChars);
        List<string> before = ResultsOf(messages).Select(ResultText).ToList();
        List<AIContent> contentsBefore = messages.SelectMany(m => m.Contents).ToList();

        await Client(inner, new ToolLoopBudgetOptions { MaxContextChars = 150_000 }).GetResponseAsync(messages);

        ResultsOf(messages).Select(ResultText).Should().Equal(before);
        messages.SelectMany(m => m.Contents).Should().Equal(contentsBefore);
        inner.Calls[0].Received.Should().NotBeSameAs(messages);
    }

    [Fact]
    public async Task The_verbatim_window_shrinks_when_digesting_older_results_is_not_enough_but_the_latest_result_never_does()
    {
        RecordingChatClient inner = new();
        ToolLoopBudgetOptions budget = new() { MaxContextChars = 150_000, RecentToolResultsVerbatim = 4, DigestMaxChars = 2_000 };
        List<ChatMessage> messages = Conversation(pairs: 10, resultChars: ResultChars);

        await Client(inner, budget).GetResponseAsync(messages);

        IReadOnlyList<ChatMessage> forwarded = inner.Calls[0].Messages;
        List<FunctionResultContent> results = ResultsOf(forwarded);
        ResultText(results[9]).Should().Be(PayloadFor(9, ResultChars));
        for (int i = 0; i < 9; i++)
            ResultText(results[i]).Should().StartWith("[compacted:");
        ToolLoopCompactor.MeasureMessages(forwarded).Should().BeLessThanOrEqualTo(budget.MaxContextChars);
        AssertEveryCallHasItsResult(forwarded);
    }

    [Fact]
    public async Task Assistant_text_and_the_original_prompt_are_never_touched()
    {
        RecordingChatClient inner = new();
        string prompt = new('P', 50_000);
        string finalAnswer = new('A', 50_000);
        List<ChatMessage> messages = Conversation(pairs: 4, resultChars: ResultChars, prompt: prompt);
        messages.Add(new ChatMessage(ChatRole.Assistant, finalAnswer));

        await Client(inner, new ToolLoopBudgetOptions { MaxContextChars = 120_000 }).GetResponseAsync(messages);

        IReadOnlyList<ChatMessage> forwarded = inner.Calls[0].Messages;
        forwarded[0].Text.Should().Be(prompt);
        forwarded.Single(m => m.Role == ChatRole.Assistant && m.Contents.OfType<FunctionCallContent>().Count() == 0)
            .Text.Should().Be(finalAnswer);
    }

    [Fact]
    public async Task Json_results_are_digested_structurally_and_stay_parseable()
    {
        RecordingChatClient inner = new();
        string json = JsonSerializer.Serialize(new
        {
            ok = false,
            stdout = string.Join("\n", Enumerable.Range(0, 5_000).Select(i => $"compiling module {i}")),
            files = Enumerable.Range(0, 400).Select(i => $"src/file-{i}.tsx").ToArray(),
        });
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "go"),
            Assistant("c0"), Tool("c0", json),
            Assistant("c1"), Tool("c1", "small latest result"),
        ];

        await Client(inner, new ToolLoopBudgetOptions { MaxContextChars = 20_000, RecentToolResultsVerbatim = 1 })
            .GetResponseAsync(messages);

        string digested = ResultText(ResultsOf(inner.Calls[0].Messages)[0]);
        digested.Should().StartWith($"[compacted: {json.Length} chars, ");
        string body = digested[(digested.IndexOf('\n') + 1)..];
        using JsonDocument document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("ok").GetBoolean().Should().BeFalse();
        body.Length.Should().BeLessThan(2_100);
    }

    [Fact]
    public async Task Plain_text_results_keep_their_first_and_last_lines()
    {
        RecordingChatClient inner = new();
        string log = string.Join("\n", Enumerable.Range(0, 5_000).Select(i => $"log line {i}"));
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "go"),
            Assistant("c0"), Tool("c0", log),
            Assistant("c1"), Tool("c1", "latest"),
        ];

        await Client(inner, new ToolLoopBudgetOptions { MaxContextChars = 10_000, RecentToolResultsVerbatim = 1 })
            .GetResponseAsync(messages);

        string digested = ResultText(ResultsOf(inner.Calls[0].Messages)[0]);
        digested.Should().StartWith("[compacted: ").And.Contain("5000 lines");
        digested.Should().Contain("log line 0\n").And.Contain("log line 4999");
        digested.Should().Contain("middle omitted");
    }

    [Fact]
    public async Task Older_call_arguments_are_shrunk_as_a_last_resort_and_the_pairing_survives()
    {
        RecordingChatClient inner = new();
        string file = string.Join("\n", Enumerable.Range(0, 1_000).Select(i => $"export const a{i} = {i};"));
        List<ChatMessage> messages = [new(ChatRole.User, "go")];
        for (int i = 0; i < 6; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent($"w{i}", "WriteSandboxFile", new Dictionary<string, object?> { ["path"] = $"src/f{i}.tsx", ["content"] = file }),
            ]));
            messages.Add(Tool($"w{i}", "{\"ok\":true}"));
        }

        await Client(inner, new ToolLoopBudgetOptions { MaxContextChars = 40_000 }).GetResponseAsync(messages);

        IReadOnlyList<ChatMessage> forwarded = inner.Calls[0].Messages;
        List<FunctionCallContent> calls = forwarded.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
        calls.Should().HaveCount(6);
        ((string)calls[0].Arguments!["content"]!).Should().StartWith("[compacted:");
        calls[0].Arguments!["path"].Should().Be("src/f0.tsx");
        calls[0].Name.Should().Be("WriteSandboxFile");
        calls[^1].Arguments!["content"].Should().Be(file);
        ToolLoopCompactor.MeasureMessages(forwarded).Should().BeLessThanOrEqualTo(40_000);
        AssertEveryCallHasItsResult(forwarded);
        ((string)((FunctionCallContent)messages[1].Contents[0]).Arguments!["content"]!).Should().Be(file);
    }

    [Fact]
    public async Task Call_arguments_are_left_alone_when_that_is_switched_off()
    {
        RecordingChatClient inner = new();
        string file = new('x', 30_000);
        List<ChatMessage> messages = [new(ChatRole.User, "go")];
        for (int i = 0; i < 4; i++)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"w{i}", "Write", new Dictionary<string, object?> { ["content"] = file })]));
            messages.Add(Tool($"w{i}", "ok"));
        }

        await Client(inner, new ToolLoopBudgetOptions { MaxContextChars = 20_000, CompactCallArguments = false })
            .GetResponseAsync(messages);

        inner.Calls[0].Received.Should().BeSameAs(messages);
    }

    [Fact]
    public async Task Instructions_travel_outside_the_list_but_count_against_the_budget()
    {
        RecordingChatClient inner = new();
        List<ChatMessage> messages = Conversation(pairs: 3, resultChars: 20_000);
        ToolLoopBudgetOptions budget = new() { MaxContextChars = 100_000, RecentToolResultsVerbatim = 1 };

        // 60k of messages + 5k instructions fits ...
        await Client(inner, budget).GetResponseAsync(messages, new ChatOptions { Instructions = new string('I', 5_000) });
        inner.Calls[0].Received.Should().BeSameAs(messages);

        // ... and 60k + 80k does not.
        await Client(inner, budget).GetResponseAsync(messages, new ChatOptions { Instructions = new string('I', 80_000) });
        inner.Calls[1].Received.Should().NotBeSameAs(messages);
        ResultText(ResultsOf(inner.Calls[1].Messages)[0]).Should().StartWith("[compacted:");
        inner.Calls[1].Options!.Instructions.Should().HaveLength(80_000);
    }

    [Fact]
    public async Task Disabled_forwards_everything_untouched()
    {
        RecordingChatClient inner = new();
        List<ChatMessage> messages = Conversation(pairs: 10, resultChars: ResultChars);

        await Client(inner, new ToolLoopBudgetOptions { Enabled = false, MaxContextChars = 1_000 }).GetResponseAsync(messages);

        inner.Calls[0].Received.Should().BeSameAs(messages);
    }

    [Fact]
    public async Task The_streaming_path_compacts_and_counts_usage_too()
    {
        RecordingChatClient inner = new() { Usage = new UsageDetails { InputTokenCount = 123, OutputTokenCount = 7 } };
        ToolLoopRunStats stats = new();
        ToolLoopCompactingChatClient client = new(inner, new ToolLoopBudgetOptions { MaxContextChars = 150_000 }, 40, stats);
        List<ChatMessage> messages = Conversation(pairs: 6, resultChars: ResultChars);

        await foreach (ChatResponseUpdate _ in client.GetStreamingResponseAsync(messages))
        {
        }

        ToolLoopCompactor.MeasureMessages(inner.Calls[0].Messages).Should().BeLessThanOrEqualTo(150_000);
        stats.ModelCalls.Should().Be(1);
        stats.InputTokens.Should().Be(123);
        stats.OutputTokens.Should().Be(7);
    }

    // ---------------------------------------------------------------------------------------
    // QA-P0-30: the hard ceiling is for a frozen prefix only
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task An_Anthropic_call_with_nothing_frozen_is_not_failed_by_the_hard_ceiling()
    {
        // The hard ceiling (QA-P0-10) exists to catch a FROZEN prefix compaction may not touch. A
        // thinking-off Anthropic run carries no signed block, so nothing is frozen and compaction is
        // the only bound: the ceiling must not fail the call. The latest result is never digested, so
        // the forwarded size really is over the ceiling here.
        RecordingChatClient inner = new() { ProviderName = "anthropic" };
        ToolLoopBudgetOptions budget = new() { MaxContextChars = 250_000, HardMaxChars = 300_000 };
        List<ChatMessage> messages = Conversation(pairs: 1, resultChars: 400_000);
        messages.SelectMany(m => m.Contents).OfType<TextReasoningContent>().Should().BeEmpty("the run is thinking-off");

        await Client(inner, budget).GetResponseAsync(messages);

        inner.Calls.Should().ContainSingle("nothing is frozen, so the call is forwarded rather than failed");
        ToolLoopCompactor.MeasureMessages(inner.Calls[0].Messages).Should().BeGreaterThan(budget.HardMaxChars,
            "the call is genuinely over the ceiling; the ceiling simply does not apply with nothing frozen");
    }

    // ---------------------------------------------------------------------------------------
    // The whole pipeline: FunctionInvokingChatClient -> compactor -> provider
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_real_tool_loop_never_sends_more_than_the_budget_but_the_response_keeps_the_raw_history()
    {
        const int Iterations = 12;
        RecordingChatClient provider = new()
        {
            Script = (call, _) => call < Iterations
                ? ToolCall($"call{call}")
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "final answer")),
        };
        AIFunction big = AIFunctionFactory.Create(() => new string('z', ResultChars), "big");
        ToolLoopRunStats stats = new();
        ToolLoopBudgetOptions budget = new() { MaxContextChars = 300_000, RecentToolResultsVerbatim = 2 };
        IChatClient pipeline = ToolLoopPipeline.Wrap(provider, budget, new ToolLoopOptions(), stats);

        ChatResponse response = await pipeline.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "do it")],
            new ChatOptions { Tools = [big], Instructions = "be brief" });

        response.Text.Should().Contain("final answer");
        provider.Calls.Should().HaveCount(Iterations + 1);
        foreach (RecordedCall call in provider.Calls)
        {
            ToolLoopCompactor.MeasureMessages(call.Messages, call.Options?.Instructions?.Length ?? 0)
                .Should().BeLessThanOrEqualTo(budget.MaxContextChars);
            AssertEveryCallHasItsResult(call.Messages);
        }

        // Without compaction the last call would have carried Iterations x 100k.
        stats.PeakContextChars.Should().BeGreaterThan(1_000_000);
        stats.PeakForwardedChars.Should().BeLessThanOrEqualTo(budget.MaxContextChars);
        stats.ModelCalls.Should().Be(Iterations + 1);
        stats.IterationCapReached.Should().BeFalse();

        // FunctionInvokingChatClient's own history, and so the response, is the raw one.
        List<FunctionResultContent> kept = response.Messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();
        kept.Should().HaveCount(Iterations);
        kept.Should().OnlyContain(r => ResultText(r).Length == ResultChars);
    }

    [Fact]
    public async Task Hitting_the_iteration_cap_is_reported_and_the_last_call_has_no_tools()
    {
        RecordingChatClient provider = new()
        {
            Script = (_, options) => options?.Tools is { Count: > 0 }
                ? ToolCall(Guid.NewGuid().ToString("N"))
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "forced answer")),
        };
        AIFunction tool = AIFunctionFactory.Create(() => "ok", "tool");
        ToolLoopRunStats stats = new();
        IChatClient pipeline = ToolLoopPipeline.Wrap(
            provider,
            new ToolLoopBudgetOptions(),
            new ToolLoopOptions { MaxIterations = 3 },
            stats);

        ChatResponse response = await pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [tool] });

        response.Text.Should().Contain("forced answer");
        provider.Calls.Should().HaveCount(4);
        provider.Calls[3].Options?.Tools.Should().BeNullOrEmpty();
        stats.ModelCalls.Should().Be(4);
        stats.IterationCapReached.Should().BeTrue();
    }

    [Fact]
    public async Task Finishing_exactly_on_the_last_allowed_iteration_is_not_reported_as_a_cap()
    {
        RecordingChatClient provider = new()
        {
            Script = (call, _) => call < 2
                ? ToolCall($"c{call}")
                : new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")),
        };
        ToolLoopRunStats stats = new();
        IChatClient pipeline = ToolLoopPipeline.Wrap(
            provider,
            new ToolLoopBudgetOptions(),
            new ToolLoopOptions { MaxIterations = 3 },
            stats);

        await pipeline.GetResponseAsync([new ChatMessage(ChatRole.User, "go")], new ChatOptions { Tools = [AIFunctionFactory.Create(() => "ok", "tool")] });

        stats.ModelCalls.Should().Be(3);
        stats.IterationCapReached.Should().BeFalse("the model stopped asking for tools on its own, on the last call the cap allows");
    }

    [Fact]
    public void A_client_that_already_contains_a_function_loop_is_left_alone()
    {
        IChatClient existing = new FunctionInvokingChatClient(new RecordingChatClient());

        ToolLoopPipeline.Wrap(existing, new ToolLoopBudgetOptions(), new ToolLoopOptions(), new ToolLoopRunStats())
            .Should().BeSameAs(existing);
    }

    [Fact]
    public void The_pipeline_applies_the_configured_iteration_limits()
    {
        IChatClient pipeline = ToolLoopPipeline.Wrap(
            new RecordingChatClient(),
            new ToolLoopBudgetOptions(),
            new ToolLoopOptions { MaxIterations = 7, MaxConsecutiveErrors = 1 },
            new ToolLoopRunStats());

        FunctionInvokingChatClient loop = pipeline.GetService<FunctionInvokingChatClient>()!;
        loop.MaximumIterationsPerRequest.Should().Be(7);
        loop.MaximumConsecutiveErrorsPerRequest.Should().Be(1);
        pipeline.GetService<ToolLoopCompactingChatClient>().Should().NotBeNull();
    }

    // ---------------------------------------------------------------------------------------
    // Configuration
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Options_default_to_the_documented_values_when_the_section_is_absent()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        ToolLoopBudgetOptions budget = ToolLoopBudgetOptions.From(configuration);
        budget.Enabled.Should().BeTrue();
        budget.MaxContextChars.Should().Be(200_000);
        budget.RecentToolResultsVerbatim.Should().Be(4);
        budget.DigestMaxChars.Should().Be(2_000);
        budget.CompactCallArguments.Should().BeTrue();
        budget.HardMaxChars.Should().Be(900_000);

        ToolLoopOptions loop = ToolLoopOptions.From(configuration);
        loop.MaxIterations.Should().Be(40);
        loop.MaxConsecutiveErrors.Should().Be(3);
    }

    [Fact]
    public void Options_bind_from_their_sections_and_a_nonsense_loop_limit_is_clamped()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkflowEngine:ToolLoopBudget:Enabled"] = "false",
            ["WorkflowEngine:ToolLoopBudget:MaxContextChars"] = "123456",
            ["WorkflowEngine:ToolLoopBudget:RecentToolResultsVerbatim"] = "9",
            ["WorkflowEngine:ToolLoopBudget:DigestMaxChars"] = "777",
            ["WorkflowEngine:ToolLoopBudget:HardMaxChars"] = "654321",
            ["WorkflowEngine:ToolLoop:MaxIterations"] = "0",
            ["WorkflowEngine:ToolLoop:MaxConsecutiveErrors"] = "-5",
        }).Build();

        ToolLoopBudgetOptions budget = ToolLoopBudgetOptions.From(configuration);
        budget.Enabled.Should().BeFalse();
        budget.MaxContextChars.Should().Be(123456);
        budget.RecentToolResultsVerbatim.Should().Be(9);
        budget.DigestMaxChars.Should().Be(777);
        budget.HardMaxChars.Should().Be(654321);

        ToolLoopOptions loop = ToolLoopOptions.From(configuration);
        loop.MaxIterations.Should().Be(1);
        loop.MaxConsecutiveErrors.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------

    private static ToolLoopCompactingChatClient Client(RecordingChatClient inner, ToolLoopBudgetOptions budget)
        => new(inner, budget, maxIterations: 40, new ToolLoopRunStats());

    private static string PayloadFor(int index, int chars)
    {
        // Distinct, multi-line, so a digest is recognisably "this result" and not another one.
        System.Text.StringBuilder builder = new();
        for (int line = 0; builder.Length < chars; line++)
            builder.Append("line-").Append(index).Append('-').Append(line).Append('\n');
        return builder.ToString(0, chars);
    }

    private static List<ChatMessage> Conversation(int pairs, int resultChars, string prompt = "make the video")
    {
        List<ChatMessage> messages = [new(ChatRole.User, prompt)];
        for (int i = 0; i < pairs; i++)
        {
            messages.Add(Assistant($"call{i}"));
            messages.Add(Tool($"call{i}", PayloadFor(i, resultChars)));
        }

        return messages;
    }

    private static ChatMessage Assistant(string callId)
        => new(ChatRole.Assistant, [new FunctionCallContent(callId, "render", new Dictionary<string, object?> { ["cmd"] = "npx remotion render" })]);

    private static ChatMessage Tool(string callId, string result)
        => new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    private static ChatResponse ToolCall(string callId)
        => new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, "big")]));

    private static List<FunctionResultContent> ResultsOf(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToList();

    private static string ResultText(FunctionResultContent result) => result.Result switch
    {
        string s => s,
        JsonElement e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText(),
        null => string.Empty,
        object o => o.ToString()!,
    };

    private static string TextOfResult(ChatMessage message) => ResultText((FunctionResultContent)message.Contents[0]);

    private static void AssertEveryCallHasItsResult(IEnumerable<ChatMessage> messages)
    {
        List<AIContent> contents = messages.SelectMany(m => m.Contents).ToList();
        List<string> callIds = contents.OfType<FunctionCallContent>().Select(c => c.CallId).ToList();
        List<string> resultIds = contents.OfType<FunctionResultContent>().Select(r => r.CallId).ToList();

        callIds.Should().OnlyHaveUniqueItems();
        callIds.Should().BeEquivalentTo(resultIds);

        // And in order: a result never precedes its call.
        List<string> seen = [];
        foreach (AIContent content in contents)
        {
            if (content is FunctionCallContent call)
                seen.Add(call.CallId);
            else if (content is FunctionResultContent result)
                seen.Should().Contain(result.CallId);
        }
    }

    /// <param name="Messages">A snapshot: FunctionInvokingChatClient keeps appending to the list it passes down.</param>
    /// <param name="Received">The enumerable exactly as it arrived, for identity checks.</param>
    private sealed record RecordedCall(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options, IEnumerable<ChatMessage> Received);

    private sealed class RecordingChatClient : IChatClient
    {
        public List<RecordedCall> Calls { get; } = [];

        public UsageDetails? Usage { get; init; }

        /// <summary>The provider name this client reports; <c>"anthropic"</c> makes the compactor freeze on signed thinking.</summary>
        public string? ProviderName { get; init; }

        /// <summary>Call index (0-based) and options to the response; defaults to a plain text answer.</summary>
        public Func<int, ChatOptions?, ChatResponse>? Script { get; init; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls.Add(new RecordedCall(messages.ToList(), options, messages));
            ChatResponse response = Script?.Invoke(Calls.Count - 1, options)
                ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"));
            response.Usage ??= Usage;
            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls.Add(new RecordedCall(messages.ToList(), options, messages));
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
            if (Usage is not null)
                yield return new ChatResponseUpdate { Contents = [new UsageContent(Usage)] };
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType == typeof(ChatClientMetadata) && ProviderName is { } name ? new ChatClientMetadata(name) : null;

        public void Dispose()
        {
        }
    }
}
