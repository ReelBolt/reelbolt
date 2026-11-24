using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.AI;
using OpenAI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Pins what each provider kind is actually sent for an agent's reasoning effort. The wire-level
/// tests run the real SDKs over a stub transport and inspect the serialized request body, because a
/// unit test of the options object alone cannot show that a raw field survives into the request.
/// </summary>
public class ReasoningEffortMapperTests
{
    private static ChatOptions WithEffort(ReasoningEffort effort) =>
        new() { Temperature = 0.3f, Reasoning = new ReasoningOptions { Effort = effort } };

    // ---- the mapper itself -------------------------------------------------------------------

    [Fact]
    public void Anthropic_none_is_floored_to_low_without_mutating_the_callers_options()
    {
        ChatOptions original = WithEffort(ReasoningEffort.None);

        ChatOptions? projected = ReasoningEffortMapper.Project(original, InferenceProviderKind.Anthropic);

        projected!.Reasoning!.Effort.Should().Be(ReasoningEffort.Low);
        original.Reasoning!.Effort.Should().Be(ReasoningEffort.None, "the instance is a singleton agent's shared field");
        projected.Temperature.Should().Be(0.3f, "everything else is carried over");
    }

    [Theory]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.ExtraHigh)]
    public void Anthropic_other_efforts_pass_through_as_the_same_instance(ReasoningEffort effort)
    {
        ChatOptions original = WithEffort(effort);

        ReasoningEffortMapper.Project(original, InferenceProviderKind.Anthropic).Should().BeSameAs(original);
    }

    [Theory]
    [InlineData(InferenceProviderKind.AzureOpenAI)]
    [InlineData(InferenceProviderKind.OpenAICompatible)]
    [InlineData(InferenceProviderKind.Gemini)]
    public void Other_kinds_are_untouched_even_for_none(InferenceProviderKind kind)
    {
        ChatOptions original = WithEffort(ReasoningEffort.None);

        ReasoningEffortMapper.Project(original, kind).Should().BeSameAs(original);
    }

    [Fact]
    public void Options_with_no_reasoning_are_returned_as_is_for_every_kind()
    {
        ChatOptions original = new() { Temperature = 0.3f };

        foreach (InferenceProviderKind kind in Enum.GetValues<InferenceProviderKind>())
        {
            ReasoningEffortMapper.Project(original, kind).Should().BeSameAs(original);
        }

        ReasoningEffortMapper.Project(null, InferenceProviderKind.DeepSeek).Should().BeNull();
    }

    [Theory]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.Medium)]
    [InlineData(ReasoningEffort.ExtraHigh)]
    public void OpenAI_platform_with_reasoning_on_drops_sampling_params_without_mutating_the_callers_options(ReasoningEffort effort)
    {
        ChatOptions original = WithEffort(effort);
        original.TopP = 0.95f;
        original.TopK = 40;
        original.MaxOutputTokens = 1234;

        ChatOptions? projected = ReasoningEffortMapper.Project(original, InferenceProviderKind.OpenAICompatible, openAIPlatform: true);

        projected.Should().NotBeSameAs(original);
        projected!.Temperature.Should().BeNull();
        projected.TopP.Should().BeNull();
        projected.TopK.Should().BeNull();
        projected.Reasoning!.Effort.Should().Be(effort, "the effort itself is kept");
        projected.MaxOutputTokens.Should().Be(1234, "everything else is carried over");
        original.Temperature.Should().Be(0.3f, "the instance is a singleton agent's shared field");
        original.TopP.Should().Be(0.95f);
        original.TopK.Should().Be(40);
    }

    [Fact]
    public void OpenAI_platform_with_effort_none_keeps_sampling_params()
    {
        ChatOptions original = WithEffort(ReasoningEffort.None);

        ReasoningEffortMapper.Project(original, InferenceProviderKind.OpenAICompatible, openAIPlatform: true)
            .Should().BeSameAs(original);
    }

    [Theory]
    [InlineData(ReasoningEffort.Low)]
    [InlineData(ReasoningEffort.ExtraHigh)]
    public void Self_hosted_OpenAI_compatible_keeps_sampling_params_with_reasoning_on(ReasoningEffort effort)
    {
        ChatOptions original = WithEffort(effort);
        original.TopP = 0.95f;

        ChatOptions? projected = ReasoningEffortMapper.Project(original, InferenceProviderKind.OpenAICompatible);

        projected.Should().BeSameAs(original, "vLLM/Ollama/speaches accept temperature and top_p, so nothing is dropped");
    }

    [Fact]
    public async Task Self_hosted_OpenAI_compatible_chat_request_carries_effort_and_temperature_together()
    {
        ChatOptions options = WithEffort(ReasoningEffort.Low);
        options.TopP = 0.95f;

        JsonElement body = await SendThroughOpenAiAsync(InferenceProviderKind.OpenAICompatible, options);

        body.GetProperty("reasoning_effort").GetString().Should().Be("low");
        body.GetProperty("temperature").GetSingle().Should().BeApproximately(0.3f, 0.0001f);
        body.GetProperty("top_p").GetSingle().Should().BeApproximately(0.95f, 0.0001f);
    }

    [Fact]
    public async Task OpenAI_compatible_with_effort_none_keeps_temperature_on_the_wire()
    {
        JsonElement body = await SendThroughOpenAiAsync(InferenceProviderKind.OpenAICompatible, WithEffort(ReasoningEffort.None));

        body.GetProperty("reasoning_effort").GetString().Should().Be("none");
        body.GetProperty("temperature").GetSingle().Should().BeApproximately(0.3f, 0.0001f);
    }

    [Fact]
    public void ChatClientFactory_wraps_an_OpenAI_platform_client_in_the_reasoning_projection()
    {
        ResolvedInferenceProvider provider = new(
            Guid.NewGuid(), "oa", InferenceProviderKind.OpenAICompatible, "https://api.openai.com/v1", "gpt-test", "sk-test", 30);

        using IChatClient client = new ChatClientFactory().Get(provider);

        client.GetService<ReasoningEffortChatClient>().Should().NotBeNull();
    }

    // ---- DeepSeek, on the wire ---------------------------------------------------------------

    [Fact]
    public async Task DeepSeek_none_sends_thinking_disabled_and_no_reasoning_effort()
    {
        JsonElement body = await SendThroughOpenAiAsync(InferenceProviderKind.DeepSeek, WithEffort(ReasoningEffort.None));

        body.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
        body.TryGetProperty("reasoning_effort", out _).Should().BeFalse(
            "DeepSeek documents low/high/max only and says nothing about 'none'");
        body.GetProperty("model").GetString().Should().Be("deepseek-test", "the patch must not displace the normal fields");
        body.GetProperty("messages").GetArrayLength().Should().Be(1);
    }

    [Theory]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
    public async Task DeepSeek_other_efforts_send_reasoning_effort_and_leave_thinking_alone(ReasoningEffort effort, string wire)
    {
        JsonElement body = await SendThroughOpenAiAsync(InferenceProviderKind.DeepSeek, WithEffort(effort));

        body.GetProperty("reasoning_effort").GetString().Should().Be(wire);
        body.TryGetProperty("thinking", out _).Should().BeFalse("thinking is on by default at 'high' and is only ever switched OFF");
    }

    [Fact]
    public async Task DeepSeek_none_keeps_a_raw_representation_the_caller_already_supplied()
    {
        ChatOptions options = WithEffort(ReasoningEffort.None);
        options.RawRepresentationFactory = _ => new OpenAI.Chat.ChatCompletionOptions { EndUserId = "u-1" };

        JsonElement body = await SendThroughOpenAiAsync(InferenceProviderKind.DeepSeek, options);

        body.GetProperty("user").GetString().Should().Be("u-1");
        body.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
    }

    // ---- OpenAI-shaped kinds are byte-identical to the old per-agent raw factory -----------------

    [Theory]
    [InlineData(ReasoningEffort.None, "none")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.ExtraHigh, "xhigh")]
    public async Task OpenAI_compatible_receives_the_house_vocabulary_unchanged(ReasoningEffort effort, string wire)
    {
        JsonElement body = await SendThroughOpenAiAsync(InferenceProviderKind.OpenAICompatible, WithEffort(effort));

        body.GetProperty("reasoning_effort").GetString().Should().Be(wire);
        body.TryGetProperty("thinking", out _).Should().BeFalse();
    }

    // ---- Anthropic, on the wire --------------------------------------------------------------

    [Fact]
    public async Task Anthropic_none_never_reaches_the_wire_as_thinking_disabled()
    {
        JsonElement body = await SendThroughAnthropicAsync(WithEffort(ReasoningEffort.None));

        body.GetProperty("thinking").GetProperty("type").GetString().Should().NotBe("disabled",
            "a model that always thinks answers thinking.type=disabled with a 400");
        body.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
    }

    [Fact]
    public async Task Anthropic_low_is_sent_as_output_config_effort_low()
    {
        JsonElement body = await SendThroughAnthropicAsync(WithEffort(ReasoningEffort.Low));

        body.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("low");
    }

    // ---- the factory wires the DeepSeek wrapper ------------------------------------------------

    [Fact]
    public void ChatClientFactory_wraps_a_DeepSeek_client_in_the_reasoning_projection()
    {
        ResolvedInferenceProvider provider = new(
            Guid.NewGuid(), "ds", InferenceProviderKind.DeepSeek, "https://api.deepseek.com/", "deepseek-flash", "sk-test", 30);

        using IChatClient client = new ChatClientFactory().Get(provider);

        client.GetService<ReasoningEffortChatClient>().Should().NotBeNull();
    }

    // ---- vocabulary ----------------------------------------------------------------------------

    [Theory]
    [InlineData("none", "none")]
    [InlineData("LOW", "low")]
    [InlineData(" xhigh ", "xhigh")]
    [InlineData("high", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Vocabulary_normalizes_and_rejects_high(string? input, string? expected)
    {
        AgentReasoningEffort.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Resolve_prefers_the_definition_then_the_configured_value_and_skips_an_invalid_stored_value()
    {
        AgentReasoningEffort.Resolve("none", "xhigh").Should().Be("none");
        AgentReasoningEffort.Resolve(null, "xhigh").Should().Be("xhigh");
        AgentReasoningEffort.Resolve("bogus", "low").Should().Be("low");
        AgentReasoningEffort.Resolve(null, null).Should().BeNull();
    }

    // ---- OpenAI platform rows on the Responses API ------------------------------

    private static ResolvedInferenceProvider OpenAiRow(string endpoint) =>
        new(Guid.NewGuid(), "oa", InferenceProviderKind.OpenAICompatible, endpoint, "gpt-test", "sk-test", 30);

    private const string ResponsesOk =
        "{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"gpt-test\"," +
        "\"output\":[{\"type\":\"message\",\"id\":\"msg_1\",\"role\":\"assistant\",\"status\":\"completed\"," +
        "\"content\":[{\"type\":\"output_text\",\"text\":\"{\\\"verdict\\\":\\\"ok\\\"}\",\"annotations\":[]}]}]," +
        "\"usage\":{\"input_tokens\":10,\"input_tokens_details\":{\"cached_tokens\":4}," +
        "\"output_tokens\":5,\"output_tokens_details\":{\"reasoning_tokens\":2},\"total_tokens\":15}}";

    private const string ChatCompletionsOk =
        "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"gpt-test\"," +
        "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"verdict\\\":\\\"ok\\\"}\"},\"finish_reason\":\"stop\"}]}";

    private sealed record Verdict(string verdict);

    private static ChatOptions AgentShapedOptions(ReasoningEffort effort) => new()
    {
        Temperature = 0.3f,
        TopP = 0.9f,
        MaxOutputTokens = 1234,
        Reasoning = new ReasoningOptions { Effort = effort },
        ResponseFormat = ChatResponseFormat.ForJsonSchema<Verdict>(),
        Tools = [AIFunctionFactory.Create((string path) => "contents", "ReadProjectFile", "Reads a file.")]
    };

    [Fact]
    public void ChatClientFactory_builds_an_api_openai_com_row_on_the_responses_api()
    {
        using IChatClient client = new ChatClientFactory().Get(OpenAiRow("https://api.openai.com/v1"));

#pragma warning disable OPENAI001
        client.GetService<OpenAI.Responses.ResponsesClient>().Should().NotBeNull();
#pragma warning restore OPENAI001
        client.GetService<OpenAI.Chat.ChatClient>().Should().BeNull();
        client.GetService<OpenAIResponsesStatelessChatClient>().Should().NotBeNull();
        client.GetService<ReasoningEffortChatClient>().Should().NotBeNull("the sampling projection stays outermost");
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", true)]
    [InlineData("https://api.openai.com/v1/", true)]
    [InlineData("https://api.openai.com", true)]
    [InlineData("http://api.openai.com/v1", false)]
    [InlineData("https://API.OpenAI.COM/v1", true)]
    [InlineData("  https://api.openai.com/v1  ", true)]
    [InlineData("https://api.openai.com.evil.com/v1", false)]
    [InlineData("https://evil.com/api.openai.com", false)]
    [InlineData("https://evil.com/api.openai.com/v1", false)]
    [InlineData("https://api.openai.com@evil.com/v1", false)]
    [InlineData("https://myresource.openai.azure.com/openai/v1", false)]
    [InlineData("https://openai.example.com/v1", false)]
    [InlineData("http://vllm:8000/v1", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void Responses_routing_is_chosen_only_for_the_exact_api_openai_com_host(string endpoint, bool expected)
    {
        ChatClientFactory.UsesOpenAIResponsesApi(OpenAiRow(endpoint)).Should().Be(expected);
    }

    [Theory]
    [InlineData(InferenceProviderKind.AzureOpenAI)]
    [InlineData(InferenceProviderKind.DeepSeek)]
    [InlineData(InferenceProviderKind.Gemini)]
    [InlineData(InferenceProviderKind.Anthropic)]
    public void Responses_routing_is_for_OpenAI_compatible_rows_only(InferenceProviderKind kind)
    {
        ResolvedInferenceProvider row = new(Guid.NewGuid(), "x", kind, "https://api.openai.com/v1", "m", "k", 30);

        ChatClientFactory.UsesOpenAIResponsesApi(row).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://vllm:8000/v1")]
    [InlineData("https://openai.example.com/v1")]
    [InlineData("https://api.openai.com.evil.example/v1")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/openai/")]
    public void ChatClientFactory_keeps_every_other_openai_compatible_endpoint_on_chat_completions(string endpoint)
    {
        using IChatClient client = new ChatClientFactory().Get(OpenAiRow(endpoint));

        client.GetService<OpenAI.Chat.ChatClient>().Should().NotBeNull();
#pragma warning disable OPENAI001
        client.GetService<OpenAI.Responses.ResponsesClient>().Should().BeNull();
#pragma warning restore OPENAI001
        client.GetService<OpenAIResponsesStatelessChatClient>().Should().BeNull();
        client.GetService<ReasoningEffortChatClient>().Should().BeNull("a Chat Completions endpoint is built exactly as before");
    }

    [Fact]
    public async Task Api_openai_com_row_sends_an_agent_call_to_v1_responses_with_effort_tools_and_schema()
    {
        SequencedHandler handler = new(ResponsesOk);
        IChatClient client = ChatClientFactory.BuildOpenAICompatible(
            OpenAiRow("https://api.openai.com/v1"), new HttpClientPipelineTransport(new HttpClient(handler)));

        ChatResponse response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.System, "be brief"), new ChatMessage(ChatRole.User, "hi")],
            AgentShapedOptions(ReasoningEffort.Low));

        handler.Paths.Should().ContainSingle().Which.Should().Be("/v1/responses");
        JsonElement body = handler.Bodies[0];
        body.GetProperty("model").GetString().Should().Be("gpt-test");
        body.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        body.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        body.TryGetProperty("temperature", out _).Should().BeFalse("reasoning is on, so sampling params are dropped");
        body.TryGetProperty("top_p", out _).Should().BeFalse();
        body.GetProperty("max_output_tokens").GetInt32().Should().Be(1234);
        body.GetProperty("store").GetBoolean().Should().BeFalse("calls are stateless");
        body.TryGetProperty("previous_response_id", out _).Should().BeFalse();
        body.GetProperty("include").EnumerateArray().Select(e => e.GetString())
            .Should().Contain("reasoning.encrypted_content");
        JsonElement tool = body.GetProperty("tools").EnumerateArray().Should().ContainSingle().Subject;
        tool.GetProperty("type").GetString().Should().Be("function");
        tool.GetProperty("name").GetString().Should().Be("ReadProjectFile");
        JsonElement format = body.GetProperty("text").GetProperty("format");
        format.GetProperty("type").GetString().Should().Be("json_schema");
        format.GetProperty("schema").GetProperty("properties").TryGetProperty("verdict", out _).Should().BeTrue();
        body.GetProperty("input").EnumerateArray().Select(i => i.GetProperty("role").GetString())
            .Should().Equal("system", "user");

        response.Text.Should().Be("{\"verdict\":\"ok\"}");
        response.ConversationId.Should().BeNull("with store=false nothing is kept server side");
        response.Usage!.InputTokenCount.Should().Be(10);
        response.Usage.CachedInputTokenCount.Should().Be(4);
        response.Usage.OutputTokenCount.Should().Be(5);
        response.Usage.ReasoningTokenCount.Should().Be(2);
    }

    [Fact]
    public async Task Api_openai_com_row_with_effort_none_keeps_temperature_and_asks_for_no_encrypted_reasoning()
    {
        SequencedHandler handler = new(ResponsesOk);
        IChatClient client = ChatClientFactory.BuildOpenAICompatible(
            OpenAiRow("https://api.openai.com/v1"), new HttpClientPipelineTransport(new HttpClient(handler)));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], AgentShapedOptions(ReasoningEffort.None));

        JsonElement body = handler.Bodies[0];
        body.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("none");
        body.GetProperty("temperature").GetSingle().Should().BeApproximately(0.3f, 0.0001f);
        body.GetProperty("store").GetBoolean().Should().BeFalse();
        body.TryGetProperty("include", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Other_openai_compatible_endpoint_still_sends_chat_completions()
    {
        SequencedHandler handler = new(ChatCompletionsOk);
        IChatClient client = ChatClientFactory.BuildOpenAICompatible(
            OpenAiRow("http://vllm:8000/v1"), new HttpClientPipelineTransport(new HttpClient(handler)));

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], AgentShapedOptions(ReasoningEffort.Low));

        handler.Paths.Should().ContainSingle().Which.Should().Be("/v1/chat/completions");
        JsonElement body = handler.Bodies[0];
        body.GetProperty("reasoning_effort").GetString().Should().Be("low");
        body.GetProperty("temperature").GetSingle().Should().BeApproximately(0.3f, 0.0001f, "self-hosted servers keep their sampling params");
        body.GetProperty("top_p").GetSingle().Should().BeApproximately(0.9f, 0.0001f);
        body.TryGetProperty("store", out _).Should().BeFalse();
        body.GetProperty("response_format").GetProperty("type").GetString().Should().Be("json_schema");
    }

    [Fact]
    public async Task Responses_tool_loop_replays_encrypted_reasoning_and_the_call_statelessly()
    {
        const string ToolCallTurn =
            "{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"status\":\"completed\",\"model\":\"gpt-test\"," +
            "\"output\":[" +
            "{\"type\":\"reasoning\",\"id\":\"rs_1\",\"summary\":[],\"encrypted_content\":\"ENC-1\"}," +
            "{\"type\":\"function_call\",\"id\":\"fc_1\",\"call_id\":\"call_1\",\"name\":\"ReadProjectFile\"," +
            "\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\",\"status\":\"completed\"}]," +
            "\"usage\":{\"input_tokens\":8,\"output_tokens\":3,\"total_tokens\":11}}";
        SequencedHandler handler = new(ToolCallTurn, ResponsesOk);
        IChatClient client = new FunctionInvokingChatClient(ChatClientFactory.BuildOpenAICompatible(
            OpenAiRow("https://api.openai.com/v1"), new HttpClientPipelineTransport(new HttpClient(handler))));

        ChatResponse response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "read a.txt")], AgentShapedOptions(ReasoningEffort.Low));

        response.Text.Should().Be("{\"verdict\":\"ok\"}");
        handler.Paths.Should().Equal("/v1/responses", "/v1/responses");
        JsonElement second = handler.Bodies[1];
        second.GetProperty("store").GetBoolean().Should().BeFalse();
        second.TryGetProperty("previous_response_id", out _).Should().BeFalse("the full history is resent instead");
        JsonElement[] input = second.GetProperty("input").EnumerateArray().ToArray();
        input.Select(i => i.TryGetProperty("type", out JsonElement t) ? t.GetString() : "message")
            .Should().Equal("message", "reasoning", "function_call", "function_call_output");
        input[1].GetProperty("encrypted_content").GetString().Should().Be("ENC-1");
        input[2].GetProperty("call_id").GetString().Should().Be("call_1");
        input[3].GetProperty("call_id").GetString().Should().Be("call_1");
        input[3].GetProperty("output").GetString().Should().Contain("contents");
    }

    [Fact]
    public void Reasoning_without_encrypted_state_is_dropped_from_the_history_and_nothing_else_is()
    {
        ChatMessage user = new(ChatRole.User, "hi");
        ChatMessage assistant = new(ChatRole.Assistant,
        [
            new TextReasoningContent("thought") { AdditionalProperties = new() { ["reasoningItemId"] = "rs_x" } },
            new TextReasoningContent("kept") { ProtectedData = "ENC" },
            new FunctionCallContent("call_1", "ReadProjectFile")
        ]);
        ChatMessage[] history = [user, assistant];

        ChatMessage[] sent = OpenAIResponsesStatelessChatClient.DropUnreplayableReasoning(history).ToArray();

        sent[0].Should().BeSameAs(user);
        sent[1].Contents.Should().HaveCount(2);
        sent[1].Contents[0].Should().BeOfType<TextReasoningContent>().Which.ProtectedData.Should().Be("ENC");
        assistant.Contents.Should().HaveCount(3, "the caller's history is never mutated");
        OpenAIResponsesStatelessChatClient.DropUnreplayableReasoning([user]).Should().Equal(user);
    }

    private sealed class SequencedHandler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public List<JsonElement> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            Paths.Add(request.RequestUri!.AbsolutePath);
            Bodies.Add(JsonDocument.Parse(text).RootElement.Clone());
            string json = responses[Math.Min(Bodies.Count - 1, responses.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }

    // ---- plumbing -------------------------------------------------------------------------------

    private static async Task<JsonElement> SendThroughOpenAiAsync(InferenceProviderKind kind, ChatOptions options)
    {
        CapturingHandler handler = new(
            "{\"id\":\"c1\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"deepseek-test\"," +
            "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}");

        OpenAIClient client = new(
            new ApiKeyCredential("sk-test"),
            new OpenAIClientOptions
            {
                Endpoint = new Uri("http://stub.invalid/v1/"),
                Transport = new HttpClientPipelineTransport(new HttpClient(handler)),
                RetryPolicy = new ClientRetryPolicy(0)
            });

        IChatClient chat = client.GetChatClient("deepseek-test").AsIChatClient();
        // The same wrapper ChatClientFactory.BuildDeepSeek applies; for other kinds it is a no-op.
        chat = new ReasoningEffortChatClient(chat, kind);

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        return handler.RequestBody;
    }

    private static async Task<JsonElement> SendThroughAnthropicAsync(ChatOptions options)
    {
        CapturingHandler handler = new(
            "{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-test\"," +
            "\"content\":[{\"type\":\"text\",\"text\":\"ok\"}],\"stop_reason\":\"end_turn\",\"stop_sequence\":null," +
            "\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}");

        Anthropic.AnthropicClient client = new(new Anthropic.Core.ClientOptions
        {
            ApiKey = "sk-ant-test",
            BaseUrl = "http://stub.invalid",
            HttpClient = new HttpClient(handler),
            MaxRetries = 0
        });

        IChatClient chat = new AnthropicChatOptionsAdapter(client.AsIChatClient("claude-test", 8192));

        await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        return handler.RequestBody;
    }

    private sealed class CapturingHandler(string responseJson) : HttpMessageHandler
    {
        public JsonElement RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBody = JsonDocument.Parse(text).RootElement.Clone();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }
}
