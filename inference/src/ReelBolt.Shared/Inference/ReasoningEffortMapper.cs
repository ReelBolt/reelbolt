using System.Text;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Projects the provider-neutral <see cref="ChatOptions.Reasoning"/> an agent asks for onto what one
/// provider kind can actually be sent. The one place that knows these differences.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>
/// <b>Anthropic — floor of <c>low</c>.</b> The SDK maps <see cref="ReasoningEffort.None"/> onto
/// <c>thinking.type=disabled</c>, which a model that always thinks rejects with a 400. The chat
/// assistant already learned this (<c>AssistantChatLimits.AnthropicReasoningFloor</c>); this applies the
/// same floor to every other caller. <see cref="ReasoningEffort.Low"/> is
/// <c>output_config.effort=low</c> with <c>thinking.type=adaptive</c> (captured against the real SDK in
/// <c>AnthropicChatOptionsAdapterTests</c>).
/// </item>
/// <item>
/// <b>DeepSeek — <c>none</c> means <c>thinking: {"type":"disabled"}</c>.</b> From DeepSeek's thinking-mode
/// guide (api-docs.deepseek.com/guides/thinking_mode): the switch is the request-body field
/// <c>{"thinking": {"type": "enabled/disabled"}}</c> (OpenAI format, i.e. <c>extra_body</c> in the Python
/// SDK); thinking is on by default at effort <c>high</c>; <c>reasoning_effort</c> accepts
/// <c>low</c>/<c>high</c>/<c>max</c> and maps minimal→low, low→low, medium→high, high→high, xhigh→high,
/// max→max, ultra→max. The guide does not say what <c>reasoning_effort: "none"</c> does, and it is not
/// one of the accepted values — so it is never sent: <c>none</c> becomes the disable field and no
/// <c>reasoning_effort</c> at all. Every other member of the house vocabulary is passed through, since
/// DeepSeek documents the mapping of all three (medium→high, xhigh→high). The guide also states
/// <c>temperature</c> is accepted but has no effect in thinking mode, which is a further reason the
/// disable field matters for agents that set one.
/// </item>
/// <item>
/// <b>OpenAI platform (api.openai.com) — no sampling parameters while reasoning is on.</b> OpenAI refuses
/// <c>temperature</c> other than 1 and any <c>top_p</c> from a reasoning model with an effort other than
/// <c>none</c>, so <c>temperature</c>/<c>top_p</c>/<c>top_k</c> are not sent then. Other OpenAI-compatible
/// endpoints are not touched: they keep both the sampling parameters and <c>reasoning_effort</c>.
/// </item>
/// <item>Everything else (Azure OpenAI, other OpenAI-compatible endpoints, Gemini): unchanged.</item>
/// </list>
/// </remarks>
public static class ReasoningEffortMapper
{
    /// <summary>The Anthropic floor: the lowest effort that path is ever sent.</summary>
    public static readonly ReasoningEffort AnthropicFloor = ReasoningEffort.Low;

    /// <summary>The body field value that switches DeepSeek's thinking mode off, as it is spelled on the wire.</summary>
    public const string DeepSeekThinkingDisabledJson = "{\"type\":\"disabled\"}";

    /// <summary>
    /// Returns <paramref name="options"/> unchanged when this kind needs nothing, otherwise a clone —
    /// never a mutation, because the caller's instance is a singleton agent's field shared across runs
    /// and providers.
    /// </summary>
    /// <param name="openAIPlatform">
    /// True for an OpenAI-compatible row pointed at api.openai.com (see <see cref="ChatClientFactory.UsesOpenAIResponsesApi"/>).
    /// Only then are sampling parameters dropped while reasoning is on; every other OpenAI-compatible endpoint
    /// (vLLM, Ollama, speaches, Gemini's compatibility layer) keeps them.
    /// </param>
    public static ChatOptions? Project(ChatOptions? options, InferenceProviderKind kind, bool openAIPlatform = false)
    {
        if (options?.Reasoning?.Effort is not ReasoningEffort effort)
        {
            return options;
        }

        switch (kind)
        {
            case InferenceProviderKind.Anthropic when effort == ReasoningEffort.None:
                {
                    ChatOptions floored = options.Clone();
                    floored.Reasoning = new ReasoningOptions { Effort = AnthropicFloor, Output = options.Reasoning.Output };
                    return floored;
                }

            case InferenceProviderKind.DeepSeek when effort == ReasoningEffort.None:
                {
                    ChatOptions disabled = options.Clone();
                    disabled.Reasoning = null;
                    Func<IChatClient, object?>? inherited = options.RawRepresentationFactory;
                    disabled.RawRepresentationFactory = client => DisableThinking(inherited?.Invoke(client));
                    return disabled;
                }

            // OpenAI's reasoning models answer 400 to a temperature other than 1 or to any top_p while reasoning
            // is on; effort "none" accepts both. Scoped to api.openai.com: self-hosted OpenAI-compatible servers
            // accept temperature and top_p, so dropping them there would change their output for no reason.
            case InferenceProviderKind.OpenAICompatible when openAIPlatform && effort != ReasoningEffort.None:
                {
                    ChatOptions sampled = options.Clone();
                    sampled.Temperature = null;
                    sampled.TopP = null;
                    sampled.TopK = null;
                    return sampled;
                }

            default:
                return options;
        }
    }

    private static object DisableThinking(object? inherited)
    {
#pragma warning disable OPENAI001, SCME0001 // Experimental upstream: ChatCompletionOptions.Patch is the supported raw-field hook in OpenAI 2.x.
        OpenAI.Chat.ChatCompletionOptions chatOptions = inherited as OpenAI.Chat.ChatCompletionOptions ?? new();
        chatOptions.Patch.Set("$.thinking"u8, Encoding.UTF8.GetBytes(DeepSeekThinkingDisabledJson));
        return chatOptions;
#pragma warning restore OPENAI001, SCME0001
    }
}

/// <summary>
/// Applies <see cref="ReasoningEffortMapper"/> for one provider kind at the client boundary — the one
/// place that knows the kind — so agents stay provider-agnostic. Anthropic does the same inside
/// <see cref="AnthropicChatOptionsAdapter"/>.
/// </summary>
public sealed class ReasoningEffortChatClient(IChatClient inner, InferenceProviderKind kind, bool openAIPlatform = false)
    : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(messages, ReasoningEffortMapper.Project(options, kind, openAIPlatform), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(messages, ReasoningEffortMapper.Project(options, kind, openAIPlatform), cancellationToken);
}
