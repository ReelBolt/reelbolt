using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
/// The assistant's REASONING effort — how much the model is allowed to think before it answers
/// (issue #228, "assistant thinks too much in chat").
/// </summary>
/// <remarks>
/// <para>
/// The reported symptom is that a chat reply takes too long and spends too many tokens. The cause
/// is not in the prompt and not in the tools: the assistant sent <b>no reasoning configuration at
/// all</b>, so every turn ran at whatever the provider defaults to — the most deliberation a
/// reasoning-capable backend will do — and thinking is invisible in the reply, so all a user sees
/// is a slow, expensive answer.
/// </para>
/// <para>
/// These tests pin the whole chain, in the order it can fail: the resolved effort per provider kind
/// (with the Anthropic floor, where "off" is a 400 rather than a cheaper turn), the
/// <see cref="ChatOptions"/> the turn is actually run with, and — the part that makes it a fix
/// rather than a preference — the <b>wire</b>. The last tests point the real
/// <see cref="ChatClientFactory"/> at a loopback stub and read the JSON body the provider would
/// have received, with and without the change, so "the assistant asks for less thinking" is a
/// measured request rather than an assertion about a field we set.
/// </para>
/// </remarks>
public class AssistantReasoningTests
{
    private static IConfiguration ConfigurationWith(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value))
            .Build();

    private static IConfiguration Empty() => new ConfigurationBuilder().Build();

    // ---------------------------------------------------------------------
    // The effort this turn is sent with, per provider kind
    // ---------------------------------------------------------------------

    /// <summary>
    /// Nothing configured means thinking OFF — not "the provider's default", which is what the
    /// assistant sent before this key existed and what the reported bug is about.
    /// </summary>
    [Theory]
    [InlineData(InferenceProviderKind.DeepSeek)]
    [InlineData(InferenceProviderKind.OpenAICompatible)]
    [InlineData(InferenceProviderKind.Gemini)]
    [InlineData(InferenceProviderKind.AzureOpenAI)]
    public void ReasoningEffortFor_WithNothingConfigured_TurnsThinkingOff(InferenceProviderKind kind)
    {
        Assert.Equal(ReasoningEffort.None, AssistantChatLimits.ReasoningEffortFor(Empty(), kind));
    }

    /// <summary>
    /// Anthropic is the one kind that cannot be told "none": the SDK maps
    /// <see cref="ReasoningEffort.None"/> onto <c>thinking.type=disabled</c>, which a model that
    /// always thinks rejects with a 400. There the floor is <c>low</c> — the issue's "switch
    /// thinking off, or at least bring it down", with "at least" applying where "off" is an error.
    /// </summary>
    [Fact]
    public void ReasoningEffortFor_OnAnthropic_UsesTheFloorRatherThanOff()
    {
        Assert.Equal(ReasoningEffort.Low, AssistantChatLimits.ReasoningEffortFor(Empty(), InferenceProviderKind.Anthropic));

        // ...including when the deployment explicitly asks for none, which is the default
        // everywhere else and would otherwise be a broken assistant on this one kind.
        Assert.Equal(
            ReasoningEffort.Low,
            AssistantChatLimits.ReasoningEffortFor(ConfigurationWith((AssistantChatLimits.ReasoningEffortKey, "none")), InferenceProviderKind.Anthropic));
    }

    /// <summary>An effort the deployment asks for is the effort it gets, on either kind.</summary>
    [Theory]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("xhigh", ReasoningEffort.ExtraHigh)]
    [InlineData("MEDIUM", ReasoningEffort.Medium)]
    public void ReasoningEffortFor_HonoursTheConfiguredValue(string configured, ReasoningEffort expected)
    {
        Assert.Equal(
            expected,
            AssistantChatLimits.ReasoningEffortFor(ConfigurationWith((AssistantChatLimits.ReasoningEffortKey, configured)), InferenceProviderKind.DeepSeek));
    }

    /// <summary>
    /// <c>high</c> is deliberately not part of the vocabulary: the deployed Qwen chat template's
    /// own guard rejects it (<c>AgentReasoningEffort.Vocabulary</c> says so), and a 500 on
    /// every assistant turn is a worse outcome than an unsupported setting being ignored.
    /// </summary>
    [Theory]
    [InlineData("high")]
    [InlineData("High")]
    [InlineData("extrahigh")]
    public void ReasoningEffortFor_WithAValueOutsideTheHouseVocabulary_FallsBackToTheDefault(string configured)
    {
        Assert.Equal(
            AssistantChatLimits.DefaultReasoningEffort,
            AssistantChatLimits.ReasoningEffortFor(ConfigurationWith((AssistantChatLimits.ReasoningEffortKey, configured)), InferenceProviderKind.DeepSeek));
    }

    /// <summary>
    /// A typo falls back to the default, never to "no configuration" — the same rule every other
    /// <see cref="AssistantChatLimits"/> bound applies, so a mistyped key cannot silently restore
    /// the provider's own (maximal) thinking.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("yes please")]
    [InlineData("2")]
    public void ReasoningEffortFor_WithAnUnusableValue_FallsBackToTheDefault(string configured)
    {
        Assert.Equal(
            AssistantChatLimits.DefaultReasoningEffort,
            AssistantChatLimits.ReasoningEffortFor(ConfigurationWith((AssistantChatLimits.ReasoningEffortKey, configured)), InferenceProviderKind.DeepSeek));
    }

    /// <summary>
    /// The explicit opt-out: <c>auto</c> sends no reasoning configuration at all, which is the
    /// request shape every turn had before this key existed — the escape hatch for a provider that
    /// rejects a reasoning field outright.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData(" auto ")]
    public void ReasoningEffortFor_WithTheProviderDefaultSentinel_SendsNoReasoningConfiguration(string configured)
    {
        IConfiguration configuration = ConfigurationWith((AssistantChatLimits.ReasoningEffortKey, configured));

        Assert.Null(AssistantChatLimits.ReasoningEffortFor(configuration, InferenceProviderKind.DeepSeek));

        // The floor is a floor on a value, not a reason to send one: "auto" on Anthropic is still
        // "send nothing", not "send low".
        Assert.Null(AssistantChatLimits.ReasoningEffortFor(configuration, InferenceProviderKind.Anthropic));
    }

    // ---------------------------------------------------------------------
    // The options the turn is composed with
    // ---------------------------------------------------------------------

    /// <summary>
    /// The turn's options carry both bounds, in the one place both are known — the ceiling on the
    /// Anthropic path, where the Messages API requires one, plus the effort.
    /// </summary>
    [Fact]
    public void BuildTurnOptions_CarriesBothTheCeilingAndTheEffort()
    {
        ChatOptions? options = AssistantChatLimits.BuildTurnOptions(Empty(), InferenceProviderKind.Anthropic);

        Assert.NotNull(options);
        Assert.Equal(
            AssistantChatLimits.DefaultAnswerTokens + AssistantChatLimits.ReasoningHeadroomTokens,
            options!.MaxOutputTokens);
        Assert.Equal(ReasoningEffort.Low, options.Reasoning?.Effort);
    }

    /// <summary>
    /// On a kind ReelBolt cannot size, the effort is what the turn carries — the ceiling is still
    /// not sent, because an OpenAI-compatible row's context window is its operator's
    /// <c>--max-model-len</c> and a ceiling ReelBolt invented there could 400.
    /// </summary>
    [Fact]
    public void BuildTurnOptions_OnAProviderReelBoltCannotSize_CarriesTheEffortAndNoCeiling()
    {
        ChatOptions? options = AssistantChatLimits.BuildTurnOptions(Empty(), InferenceProviderKind.DeepSeek);

        Assert.NotNull(options);
        Assert.Null(options!.MaxOutputTokens);
        Assert.Equal(ReasoningEffort.None, options.Reasoning?.Effort);
    }

    /// <summary>
    /// With the opt-out set and no ceiling configured there is NOTHING to carry, so the turn is run
    /// with no options object at all — byte-identical to the request shape the assistant used
    /// before either key existed. A deployment that wants to be left alone is left alone.
    /// </summary>
    [Fact]
    public void BuildTurnOptions_WithTheOptOutAndNoCeiling_SendsNoOptionsAtAll()
    {
        IConfiguration configuration = ConfigurationWith((AssistantChatLimits.ReasoningEffortKey, "auto"));

        Assert.Null(AssistantChatLimits.BuildTurnOptions(configuration, InferenceProviderKind.OpenAICompatible));

        // ...but a configured ceiling still travels, opt-out or not.
        ChatOptions? sized = AssistantChatLimits.BuildTurnOptions(
            ConfigurationWith(
                (AssistantChatLimits.ReasoningEffortKey, "auto"),
                (AssistantChatLimits.MaxOutputTokensKey, "2048")),
            InferenceProviderKind.OpenAICompatible);

        Assert.NotNull(sized);
        Assert.Equal(2_048, sized!.MaxOutputTokens);
        Assert.Null(sized.Reasoning);
    }

    // ---------------------------------------------------------------------
    // The turn the endpoint actually runs
    // ---------------------------------------------------------------------

    /// <summary>
    /// End to end through the endpoint: the model call the assistant makes is the one carrying the
    /// effort, so this is not a helper nobody calls.
    /// </summary>
    [Fact]
    public async Task ChatStream_RunsTheTurnWithThinkingOff()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, userId);

        var seen = new List<ChatOptions?>();
        (AssistantChatController controller, _) = AssistantChatControllerTests.CreateController(
            db,
            userId,
            CaptureProvider.Create("A short answer.", InferenceProviderKind.DeepSeek, seen),
            configuration: Empty());

        await controller.ChatStream(new AssistantChatRequest(thread.Id, "Hello"), CancellationToken.None);

        ChatOptions? options = Assert.Single(seen);
        Assert.Equal(ReasoningEffort.None, options?.Reasoning?.Effort);
    }

    /// <summary>
    /// ...and on Anthropic the same turn is sent the floor instead, because "none" is the one value
    /// that provider answers with a 400.
    /// </summary>
    [Fact]
    public async Task ChatStream_OnAnthropic_RunsTheTurnAtTheFloor()
    {
        using InferenceApiDbContext db = AssistantChatControllerTests.CreateContext();
        Guid userId = Guid.NewGuid();
        AssistantThread thread = AddThread(db, userId);

        var seen = new List<ChatOptions?>();
        (AssistantChatController controller, _) = AssistantChatControllerTests.CreateController(
            db,
            userId,
            CaptureProvider.Create("A short answer.", InferenceProviderKind.Anthropic, seen),
            configuration: Empty());

        await controller.ChatStream(new AssistantChatRequest(thread.Id, "Hello"), CancellationToken.None);

        ChatOptions? options = Assert.Single(seen);
        Assert.Equal(ReasoningEffort.Low, options?.Reasoning?.Effort);
    }

    // ---------------------------------------------------------------------
    // The wire
    // ---------------------------------------------------------------------

    /// <summary>
    /// The reproduction and the fix, in one measured pair. The same client, the same request, two
    /// option objects: <c>null</c> — exactly what every assistant turn used to send — produces a
    /// body with no reasoning field at all, and the resolved turn options produce DeepSeek's own
    /// spelling of "thinking off", <c>"thinking":{"type":"disabled"}</c>.
    /// </summary>
    /// <remarks>
    /// Pointed at a loopback stub rather than a provider: the JSON body is the whole claim, and a
    /// test that needs an API key is a test that never runs. The client is the real
    /// <see cref="ChatClientFactory"/> one, so the mapping measured here is the mapping the
    /// assistant's turn goes through.
    /// </remarks>
    [Fact]
    public async Task TheTurnOptions_ReachDeepSeek_AsThinkingDisabled()
    {
        var (bodies, server, port) = StartStubServer(2, OpenAiShapedReply);
        using HttpListener listener = server.Listener;

        try
        {
            IChatClient client = new ChatClientFactory().Get(Provider(
                InferenceProviderKind.DeepSeek, $"http://127.0.0.1:{port}/v1", "deepseek-flash", "test-key"));

            await client.GetResponseAsync(Messages());

            // BEFORE: no reasoning configuration at all — the reported bug's mechanism.
            Assert.False(ReadJson(bodies[0]).TryGetProperty("reasoning_effort", out _));

            await client.GetResponseAsync(Messages(), AssistantChatLimits.BuildTurnOptions(Empty(), InferenceProviderKind.DeepSeek));

            // AFTER: the deployment's default (thinking off), in the field THIS provider reads.
            // DeepSeek documents reasoning_effort as low/high/max only and switches thinking off with
            // a body field, so "none" is sent as thinking:{type:disabled} and never as reasoning_effort
            // (ReasoningEffortMapper). Sending reasoning_effort:"none" - what this test used to assert -
            // left DeepSeek's default-on thinking running.
            JsonElement turn = ReadJson(bodies[1]);
            Assert.Equal("disabled", turn.GetProperty("thinking").GetProperty("type").GetString());
            Assert.False(turn.TryGetProperty("reasoning_effort", out _));
        }
        finally
        {
            server.Listener.Stop();
            await server.Done;
        }
    }

    /// <summary>
    /// The same field on the ONE OpenAI-shaped kind whose client class differs: Azure OpenAI goes
    /// through <c>AzureOpenAIClient</c> rather than <c>OpenAIClient</c>, so "the OpenAI-shaped kinds
    /// all carry it" is measured here rather than assumed from the shared option type.
    /// </summary>
    [Fact]
    public async Task TheTurnOptions_ReachAzureOpenAI_AsReasoningEffortNone()
    {
        var (bodies, server, port) = StartStubServer(2, OpenAiShapedReply);
        using HttpListener listener = server.Listener;

        try
        {
            IChatClient client = new ChatClientFactory().Get(Provider(
                InferenceProviderKind.AzureOpenAI, $"http://127.0.0.1:{port}", "test-model", "test-key"));

            await client.GetResponseAsync(Messages());
            Assert.False(ReadJson(bodies[0]).TryGetProperty("reasoning_effort", out _));

            await client.GetResponseAsync(Messages(), AssistantChatLimits.BuildTurnOptions(Empty(), InferenceProviderKind.AzureOpenAI));
            Assert.Equal("none", ReadJson(bodies[1]).GetProperty("reasoning_effort").GetString());
        }
        finally
        {
            server.Listener.Stop();
            await server.Done;
        }
    }

    /// <summary>
    /// The same pair on Anthropic, whose request shape is not OpenAI's and whose mapping this
    /// repository owns (through <see cref="AnthropicChatOptionsAdapter"/>): the unconfigured turn
    /// carries neither <c>thinking</c> nor <c>output_config</c>, and the assistant's turn carries
    /// Anthropic's own "do less" spelling — <c>output_config.effort=low</c> with thinking left
    /// adaptive — never <c>thinking.type=disabled</c>, which is the 400 this path avoids.
    /// </summary>
    [Fact]
    public async Task TheTurnOptions_ReachAnthropic_AsOutputConfigEffort()
    {
        var (bodies, server, port) = StartStubServer(2, AnthropicShapedReply);
        using HttpListener listener = server.Listener;

        try
        {
            IChatClient client = new ChatClientFactory().Get(Provider(
                InferenceProviderKind.Anthropic, $"http://127.0.0.1:{port}", "claude-sonnet-5", "sk-ant-api03-test"));

            await client.GetResponseAsync(Messages());

            JsonElement before = ReadJson(bodies[0]);
            Assert.False(before.TryGetProperty("thinking", out _));
            Assert.False(before.TryGetProperty("output_config", out _));

            await client.GetResponseAsync(Messages(), AssistantChatLimits.BuildTurnOptions(Empty(), InferenceProviderKind.Anthropic));

            JsonElement after = ReadJson(bodies[1]);
            Assert.Equal("low", after.GetProperty("output_config").GetProperty("effort").GetString());
            Assert.Equal("adaptive", after.GetProperty("thinking").GetProperty("type").GetString());
        }
        finally
        {
            server.Listener.Stop();
            await server.Done;
        }
    }

    // ---------------------------------------------------------------------
    // Scaffolding
    // ---------------------------------------------------------------------

    private const string OpenAiShapedReply =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""";

    private const string AnthropicShapedReply =
        """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-5","content":[{"type":"text","text":"hi"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}""";

    private static IEnumerable<ChatMessage> Messages() => [new ChatMessage(ChatRole.User, "hi")];

    private static ResolvedInferenceProvider Provider(
        InferenceProviderKind kind, string endpoint, string model, string apiKey) =>
        new(Guid.NewGuid(), "wire-test-provider", kind, endpoint, model, apiKey, 30);

    private static JsonElement ReadJson(string body) => JsonDocument.Parse(body).RootElement;

    private sealed record StubServer(HttpListener Listener, Task Done);

    /// <summary>
    /// A loopback server answering exactly <paramref name="expectedRequests"/> requests with
    /// <paramref name="reply"/> and recording each request body. The port is reserved and released
    /// first, the same pattern the speech-synthesis client tests use.
    /// </summary>
    private static (List<string> Bodies, StubServer Server, int Port) StartStubServer(int expectedRequests, string reply)
    {
        int port = ReserveFreeLoopbackPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var bodies = new List<string>();

        Task done = Task.Run(async () =>
        {
            for (int i = 0; i < expectedRequests; i++)
            {
                HttpListenerContext context = await listener.GetContextAsync();

                using (var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding))
                {
                    bodies.Add(await reader.ReadToEndAsync());
                }

                byte[] payload = Encoding.UTF8.GetBytes(reply);
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = payload.Length;
                await context.Response.OutputStream.WriteAsync(payload);
                context.Response.Close();
            }
        });

        return (bodies, new StubServer(listener, done), port);
    }

    /// <summary>
    /// Binds an ephemeral loopback port and releases it, so <see cref="HttpListener"/> can be given
    /// a port the OS is not otherwise handing out.
    /// </summary>
    private static int ReserveFreeLoopbackPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

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
    /// An <see cref="IAgentChatClientProvider"/> that reports a chosen provider kind and records
    /// the <see cref="ChatOptions"/> of the assistant's own model call — never the title call the
    /// endpoint makes after the first exchange, which is a different seam.
    /// </summary>
    private sealed class CaptureProvider : IAgentChatClientProvider
    {
        private readonly IChatClient _client;
        private readonly ResolvedInferenceProvider? _provider;

        private CaptureProvider(IChatClient client, ResolvedInferenceProvider? provider)
        {
            _client = client;
            _provider = provider;
        }

        public static CaptureProvider Create(string reply, InferenceProviderKind? kind, List<ChatOptions?> options) =>
            new(
                new CaptureChatClient(reply, options),
                kind is { } k
                    ? new ResolvedInferenceProvider(Guid.NewGuid(), "test-provider", k, "https://provider.invalid/", "test-model", "not-required", 60)
                    : null);

        public ValueTask<IChatClient> GetAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) =>
            ValueTask.FromResult(_client);

        public ValueTask<ResolvedInferenceProvider?> ResolveProviderAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct) =>
            ValueTask.FromResult(_provider);
    }

    private sealed class CaptureChatClient : IChatClient
    {
        private readonly string _reply;
        private readonly List<ChatOptions?> _options;

        public CaptureChatClient(string reply, List<ChatOptions?> options)
        {
            _reply = reply;
            _options = options;
        }

        public ChatClientMetadata Metadata => new("assistant-reasoning-fake");

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
                FinishReason = ChatFinishReason.Stop
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
