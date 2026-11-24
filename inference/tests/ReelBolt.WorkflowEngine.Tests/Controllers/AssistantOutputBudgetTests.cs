using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Controllers;

/// <summary>
/// The assistant's OUTPUT-TOKEN budget, and the visible signal when the model hits it.
/// </summary>
/// <remarks>
/// <para>
/// The reported bug is "it thinks for a long time and then gets cut off mid sentence". There was no
/// setting to look at: nothing in the shipped configuration sized an assistant turn, no
/// <c>ChatOptions.MaxOutputTokens</c> was ever set on the assistant path, and the provider's own
/// <c>finishReason</c> — the one piece of evidence that the reply was cut — was dropped by
/// <c>AgentRunResult</c> and replaced by a hardcoded <c>stop</c> in the SSE frame. On a provider
/// that charges its reasoning against the same ceiling as the answer (Anthropic under
/// <c>thinking.type=adaptive</c>, Gemini's native <c>reasoning_effort</c> mapping) a long thinking
/// phase therefore took the budget and the answer that followed it was silently short.
/// </para>
/// <para>
/// These tests pin both halves of the fix with FAKES — no provider is contacted and no API key is
/// read, which is the only form this repository has ever used for this seam: a provider row is a
/// database row, and "no provider configured" is a first-class tested state.
/// </para>
/// </remarks>
public class AssistantOutputBudgetTests
{
    private static IConfiguration ConfigurationWith(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static IConfiguration Empty() => new ConfigurationBuilder().Build();

    // ---------------------------------------------------------------------
    // The budget arithmetic
    // ---------------------------------------------------------------------

    /// <summary>
    /// The Anthropic path is the one ReelBolt has already measured: a model thinks first and those
    /// tokens are charged against <c>max_tokens</c>. The turn's ceiling is therefore the answer's
    /// allowance PLUS headroom for the reasoning that comes before it — never the allowance alone.
    /// </summary>
    [Fact]
    public void MaxOutputTokens_OnAProviderThatSharesTheCeiling_ReservesReasoningHeadroom()
    {
        Assert.Equal(
            AssistantChatLimits.DefaultAnswerTokens + AssistantChatLimits.ReasoningHeadroomTokens,
            AssistantChatLimits.MaxOutputTokens(Empty(), InferenceProviderKind.Anthropic));
    }

    /// <summary>
    /// A configured value is a promise about the ANSWER, so the headroom is added on top of it
    /// rather than taken out of it. This is the arithmetic the old code got wrong: a ceiling the
    /// operator sized for a non-thinking backend was spent on reasoning and the answer was cut.
    /// </summary>
    [Theory]
    [InlineData("4096", InferenceProviderKind.Anthropic)]
    [InlineData("8192", InferenceProviderKind.Anthropic)]
    [InlineData("4096", InferenceProviderKind.Gemini)]
    [InlineData("8192", InferenceProviderKind.Gemini)]
    public void MaxOutputTokens_OnAConfiguredSharedCeiling_AddsHeadroomToTheAnswer(string configured, InferenceProviderKind kind)
    {
        int answer = int.Parse(configured);

        Assert.Equal(
            answer + AssistantChatLimits.ReasoningHeadroomTokens,
            AssistantChatLimits.MaxOutputTokens(ConfigurationWith((AssistantChatLimits.MaxOutputTokensKey, configured)), kind));
    }

    /// <summary>
    /// A generous configured value is honoured as asked, with the headroom on top — the ceiling is a
    /// floor for the answer's share, never a cap on it.
    /// </summary>
    [Fact]
    public void MaxOutputTokens_OnAConfiguredSharedCeiling_DoesNotCapTheAnswer()
    {
        Assert.Equal(
            40_000 + AssistantChatLimits.ReasoningHeadroomTokens,
            AssistantChatLimits.MaxOutputTokens(
                ConfigurationWith((AssistantChatLimits.MaxOutputTokensKey, "40000")),
                InferenceProviderKind.Anthropic));
    }

    /// <summary>
    /// Gemini is included in the shared-ceiling set on the ground this repo already acts on:
    /// <c>InferenceProviderTester.PingMaxOutputTokens</c> gives Gemini and DeepSeek 1,024 tokens of
    /// reasoning headroom where an OpenAI-shaped backend gets 128.
    /// </summary>
    [Fact]
    public void ChargesReasoningAgainstTheAnswerCeiling_MatchesTheProvidersThatDo()
    {
        Assert.True(AssistantChatLimits.ChargesReasoningAgainstTheAnswerCeiling(InferenceProviderKind.Anthropic));
        Assert.True(AssistantChatLimits.ChargesReasoningAgainstTheAnswerCeiling(InferenceProviderKind.Gemini));

        // deepseek-reasoner reports and caps its reasoning separately from the answer, so its answer
        // allowance is already the whole answer; the OpenAI-shaped backends are left alone entirely.
        Assert.False(AssistantChatLimits.ChargesReasoningAgainstTheAnswerCeiling(InferenceProviderKind.DeepSeek));
        Assert.False(AssistantChatLimits.ChargesReasoningAgainstTheAnswerCeiling(InferenceProviderKind.OpenAICompatible));
        Assert.False(AssistantChatLimits.ChargesReasoningAgainstTheAnswerCeiling(InferenceProviderKind.AzureOpenAI));
        Assert.False(AssistantChatLimits.ChargesReasoningAgainstTheAnswerCeiling(null));
    }

    /// <summary>
    /// On every other kind — and when the provider cannot be resolved at all — no ceiling is sent
    /// unless the deployment asked for one. A self-hosted OpenAI-compatible row's context window is
    /// whatever its operator passed to <c>--max-model-len</c>, and inventing a ceiling there could
    /// turn a working turn into a 400 that the Anthropic path (where <c>max_tokens</c> is mandatory)
    /// can never hit.
    /// </summary>
    [Theory]
    [InlineData(InferenceProviderKind.OpenAICompatible)]
    [InlineData(InferenceProviderKind.AzureOpenAI)]
    [InlineData(InferenceProviderKind.DeepSeek)]
    [InlineData(null)]
    public void MaxOutputTokens_OnAProviderReelBoltCannotSize_SendsNothingUnlessAsked(InferenceProviderKind? kind)
    {
        Assert.Null(AssistantChatLimits.MaxOutputTokens(Empty(), kind));

        Assert.Equal(
            2_048,
            AssistantChatLimits.MaxOutputTokens(
                ConfigurationWith((AssistantChatLimits.MaxOutputTokensKey, "2048")), kind));
    }

    /// <summary>
    /// Zero, negative and unparseable values are "not configured", not "unbounded" and not
    /// "literally zero": the same rule every other <see cref="AssistantChatLimits"/> bound uses, so
    /// a mistyped key can never remove the ceiling it was meant to set.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    [InlineData("")]
    public void MaxOutputTokens_WithAnUnusableConfiguredValue_FallsBackToTheDefault(string configured)
    {
        Assert.Equal(
            AssistantChatLimits.DefaultAnswerTokens + AssistantChatLimits.ReasoningHeadroomTokens,
            AssistantChatLimits.MaxOutputTokens(
                ConfigurationWith((AssistantChatLimits.MaxOutputTokensKey, configured)),
                InferenceProviderKind.Anthropic));

        Assert.Null(AssistantChatLimits.MaxOutputTokens(
            ConfigurationWith((AssistantChatLimits.MaxOutputTokensKey, configured)),
            InferenceProviderKind.OpenAICompatible));
    }

    /// <summary>
    /// The default is not a regression dressed up as a fix: the ceiling the Anthropic path actually
    /// sends is at least the number it sent before this change (the SDK default the factory passed
    /// to <c>AsIChatClient</c>), so nothing that fitted before stops fitting now.
    /// </summary>
    [Fact]
    public void MaxOutputTokens_DefaultAnthropicCeiling_IsNotBelowTheCeilingItReplaces()
    {
        int? ceiling = AssistantChatLimits.MaxOutputTokens(Empty(), InferenceProviderKind.Anthropic);

        Assert.NotNull(ceiling);
        Assert.True(
            ceiling >= 16_384,
            $"the Anthropic ceiling was 16384 before this change, but is now {ceiling}");
    }

    // ---------------------------------------------------------------------
    // The finish-reason vocabulary
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>Microsoft.Extensions.AI</c> and the AI SDK data-stream protocol are close but not the
    /// same vocabulary, so the mapping is explicit — and an absent reason keeps the pre-existing
    /// <c>stop</c>, because a provider that reports nothing must not be reported to the client as
    /// having stopped for a reason it never claimed.
    /// </summary>
    [Theory]
    [InlineData("stop", "stop")]
    [InlineData("length", "length")]
    [InlineData("tool_calls", "tool-calls")]
    [InlineData("content_filter", "content-filter")]
    [InlineData("pause_turn", "other")]
    [InlineData(null, "stop")]
    [InlineData("", "stop")]
    public void StreamFinishReason_MapsOntoTheStreamVocabulary(string? finishReason, string expected) =>
        Assert.Equal(expected, AssistantChatLimits.StreamFinishReason(finishReason));

    [Theory]
    [InlineData("length", true)]
    [InlineData("LENGTH", true)]
    [InlineData("stop", false)]
    [InlineData("tool_calls", false)]
    [InlineData(null, false)]
    public void IsModelTruncation_OnlyForTheLengthReason(string? finishReason, bool expected) =>
        Assert.Equal(expected, AssistantChatLimits.IsModelTruncation(finishReason));

    /// <summary>
    /// The marker names the bound the deployment actually sent, and stays silent about the number
    /// when ReelBolt sent none — quoting a ceiling nobody configured would be worse than saying
    /// nothing.
    /// </summary>
    [Fact]
    public void ModelTruncationMarker_NamesTheCeilingOnlyWhenOneWasSent()
    {
        string named = AssistantChatLimits.ModelTruncationMarker(24_576);
        Assert.Contains("24576", named, StringComparison.Ordinal);
        Assert.Contains("incomplete", named, StringComparison.OrdinalIgnoreCase);

        string anonymous = AssistantChatLimits.ModelTruncationMarker(null);
        Assert.DoesNotContain("24576", anonymous, StringComparison.Ordinal);
        Assert.Contains("incomplete", anonymous, StringComparison.OrdinalIgnoreCase);

        Assert.NotEqual(named, anonymous);
    }

    // ---------------------------------------------------------------------
    // The endpoint
    // ---------------------------------------------------------------------

    /// <summary>
    /// The budget reaches the chat client as <c>ChatOptions.MaxOutputTokens</c> — the whole point of
    /// resolving it here rather than leaving the provider to pick.
    /// </summary>
    [Fact]
    public async Task ChatStream_OnAnthropic_SendsTheBudgetWithReasoningHeadroom()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, userId);

        var seen = new List<ChatOptions?>();
        (AssistantChatController controller, _) = AssistantChatControllerTests.CreateController(
            db,
            userId,
            FakeProvider.Create("A short answer.", InferenceProviderKind.Anthropic, seen),
            configuration: Empty());

        await controller.ChatStream(new AssistantChatRequest(thread.Id, "Hello"), CancellationToken.None);

        ChatOptions? options = Assert.Single(seen);
        Assert.NotNull(options);
        Assert.Equal(
            AssistantChatLimits.DefaultAnswerTokens + AssistantChatLimits.ReasoningHeadroomTokens,
            options!.MaxOutputTokens);
    }

    /// <summary>
    /// ...and on a provider ReelBolt cannot size, the request carries no ceiling at all, so an
    /// operator's self-hosted backend keeps the default it always had.
    /// </summary>
    [Fact]
    public async Task ChatStream_OnAProviderReelBoltCannotSize_SendsNoCeiling()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, userId);

        var seen = new List<ChatOptions?>();
        (AssistantChatController controller, _) = AssistantChatControllerTests.CreateController(
            db,
            userId,
            FakeProvider.Create("A short answer.", InferenceProviderKind.OpenAICompatible, seen),
            configuration: Empty());

        await controller.ChatStream(new AssistantChatRequest(thread.Id, "Hello"), CancellationToken.None);

        ChatOptions? options = Assert.Single(seen);
        Assert.Null(options?.MaxOutputTokens);
    }

    /// <summary>
    /// The whole reported symptom, pinned: the provider says <c>length</c>, so the HALF SENTENCE the
    /// model produced is marked as incomplete — in the persisted row AND in the streamed text
    /// (one string, so the thread on screen and the thread the model replays agree) — and the
    /// <c>finish</c> frame carries the real reason instead of a hardcoded <c>stop</c>.
    /// </summary>
    [Fact]
    public async Task ChatStream_WhenTheModelIsCutOff_MarksTheReplyAndReportsLength()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, userId);

        const string half = "The best way to do this is to start with the ana";
        var seen = new List<ChatOptions?>();
        (AssistantChatController controller, System.IO.MemoryStream body) = AssistantChatControllerTests.CreateController(
            db,
            userId,
            FakeProvider.Create(half, InferenceProviderKind.Anthropic, seen, ChatFinishReason.Length),
            configuration: Empty());

        await controller.ChatStream(new AssistantChatRequest(thread.Id, "Hello"), CancellationToken.None);

        AssistantMessage turn = db.AssistantMessages.Single(m => m.ThreadId == thread.Id && m.Role == "assistant");

        Assert.StartsWith(half, turn.Content, StringComparison.Ordinal);
        Assert.EndsWith(
            AssistantChatLimits.ModelTruncationMarker(AssistantChatLimits.MaxOutputTokens(Empty(), InferenceProviderKind.Anthropic)),
            turn.Content,
            StringComparison.Ordinal);

        List<JsonElement> frames = AssistantChatControllerTests.ParseDataLines(body)
            .Where(line => line != "[DONE]")
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        string streamed = frames
            .Where(e => e.GetProperty("type").GetString() == "text-delta")
            .Select(e => e.GetProperty("textDelta").GetString()!)
            .Single();
        Assert.Equal(turn.Content, streamed);

        JsonElement finish = frames.Single(e => e.GetProperty("type").GetString() == "finish");
        Assert.Equal("length", finish.GetProperty("finishReason").GetString());
    }

    /// <summary>
    /// The counterpart: a reply the model finished normally is stored, streamed and reported
    /// verbatim. Without this, "mark every reply truncated" would pass the test above.
    /// </summary>
    [Fact]
    public async Task ChatStream_WhenTheModelFinishedNormally_AddsNothingAndReportsStop()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, userId);

        const string reply = "Sure — I added a Review step after the Author step.";
        var seen = new List<ChatOptions?>();
        (AssistantChatController controller, System.IO.MemoryStream body) = AssistantChatControllerTests.CreateController(
            db,
            userId,
            FakeProvider.Create(reply, InferenceProviderKind.Anthropic, seen, ChatFinishReason.Stop),
            configuration: Empty());

        await controller.ChatStream(new AssistantChatRequest(thread.Id, "Add a review step"), CancellationToken.None);

        AssistantMessage turn = db.AssistantMessages.Single(m => m.ThreadId == thread.Id && m.Role == "assistant");
        Assert.Equal(reply, turn.Content);

        List<JsonElement> frames = AssistantChatControllerTests.ParseDataLines(body)
            .Where(line => line != "[DONE]")
            .Select(line => JsonDocument.Parse(line).RootElement)
            .ToList();

        JsonElement finish = frames.Single(e => e.GetProperty("type").GetString() == "finish");
        Assert.Equal("stop", finish.GetProperty("finishReason").GetString());
    }

    /// <summary>
    /// A provider that reports NO finish reason is treated exactly as it was before this change:
    /// the reply is stored whole and the frame says <c>stop</c>. The evidence that a cut happened
    /// is the provider's to give; ReelBolt does not invent one.
    /// </summary>
    [Fact]
    public async Task ChatStream_WhenTheProviderReportsNoFinishReason_BehavesAsBefore()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, userId);

        const string reply = "All done.";
        var seen = new List<ChatOptions?>();
        (AssistantChatController controller, System.IO.MemoryStream body) = AssistantChatControllerTests.CreateController(
            db,
            userId,
            FakeProvider.Create(reply, InferenceProviderKind.Anthropic, seen, finishReason: null),
            configuration: Empty());

        await controller.ChatStream(new AssistantChatRequest(thread.Id, "Hello"), CancellationToken.None);

        AssistantMessage turn = db.AssistantMessages.Single(m => m.ThreadId == thread.Id && m.Role == "assistant");
        Assert.Equal(reply, turn.Content);

        JsonElement finish = AssistantChatControllerTests.ParseDataLines(body)
            .Where(line => line != "[DONE]")
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Single(e => e.GetProperty("type").GetString() == "finish");

        Assert.Equal("stop", finish.GetProperty("finishReason").GetString());
    }

    /// <summary>
    /// Both bounds can bite on one turn, and whichever cuts the visible text is the one that gets
    /// to say so: a model cut AND an over-long reply ends on the character marker, which is the
    /// bound that actually shortened what the reader sees.
    /// </summary>
    [Fact]
    public async Task ChatStream_WhenBothBoundsBite_TheReaderSeesTheLastOne()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, userId);

        IConfiguration configuration = ConfigurationWith((AssistantChatLimits.MaxMessageCharsKey, "2000"));
        var seen = new List<ChatOptions?>();
        (AssistantChatController controller, _) = AssistantChatControllerTests.CreateController(
            db,
            userId,
            FakeProvider.Create(new string('y', 5_000), InferenceProviderKind.Anthropic, seen, ChatFinishReason.Length),
            configuration: configuration);

        await controller.ChatStream(new AssistantChatRequest(thread.Id, "Hello"), CancellationToken.None);

        AssistantMessage turn = db.AssistantMessages.Single(m => m.ThreadId == thread.Id && m.Role == "assistant");

        Assert.True(turn.Content.Length <= 2_000, $"the stored turn must stay within the cap, was {turn.Content.Length}");
        Assert.EndsWith(AssistantChatLimits.TruncationMarker(2_000), turn.Content, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // Scaffolding
    // ---------------------------------------------------------------------

    private static AssistantThread AddThread(InferenceApiDbContext db, Guid userId)
    {
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrganizationId = TestTenancy.OrgOf(userId),
            Title = "New chat",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.AssistantThreads.Add(thread);
        db.SaveChanges();
        return thread;
    }

    /// <summary>
    /// An <see cref="IAgentChatClientProvider"/> that answers with a canned reply carrying a chosen
    /// finish reason, reports a chosen provider kind, and records the <see cref="ChatOptions"/> each
    /// model call was made with.
    /// </summary>
    /// <remarks>
    /// Hand-rolled rather than a Moq mock of the default interface method
    /// <see cref="IAgentChatClientProvider.ResolveProviderAsync"/>, whose interception is an
    /// implementation detail of the mocking library and not something a test about budget
    /// arithmetic should depend on. The title call the endpoint makes after the first exchange is
    /// answered separately and kept OUT of the captured options, so the list is exactly the
    /// assistant turn's own calls.
    /// </remarks>
    private sealed class FakeProvider : IAgentChatClientProvider
    {
        private readonly IChatClient _client;
        private readonly ResolvedInferenceProvider? _provider;

        private FakeProvider(IChatClient client, ResolvedInferenceProvider? provider)
        {
            _client = client;
            _provider = provider;
        }

        public static FakeProvider Create(
            string reply,
            InferenceProviderKind? kind,
            List<ChatOptions?> options,
            ChatFinishReason? finishReason = null)
        {
            var client = new FakeChatClient(reply, finishReason, options);
            ResolvedInferenceProvider? resolved = kind is { } k
                ? new ResolvedInferenceProvider(
                    Guid.NewGuid(), "test-provider", k, "https://provider.invalid/", "test-model", "not-required", 60)
                : null;

            return new FakeProvider(client, resolved);
        }

        public ValueTask<IChatClient> GetAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) =>
            ValueTask.FromResult(_client);

        public ValueTask<ResolvedInferenceProvider?> ResolveProviderAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) =>
            ValueTask.FromResult(_provider);
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly string _reply;
        private readonly ChatFinishReason? _finishReason;
        private readonly List<ChatOptions?> _options;

        public FakeChatClient(string reply, ChatFinishReason? finishReason, List<ChatOptions?> options)
        {
            _reply = reply;
            _finishReason = finishReason;
            _options = options;
        }

        public ChatClientMetadata Metadata => new("assistant-output-budget-fake");

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatMessage> list = messages.ToList();
            if (list.Count > 0 && list[0].Role == ChatRole.System
                && list[0].Text.StartsWith("You name chat conversations", StringComparison.Ordinal))
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Test title")));
            }

            _options.Add(options);

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply))
            {
                FinishReason = _finishReason
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The assistant turn runs non-streaming through AIAgent.RunAsync.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
