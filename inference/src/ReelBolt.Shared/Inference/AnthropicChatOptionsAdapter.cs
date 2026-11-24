using System.Linq;
using Microsoft.Extensions.AI;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// Removes the <see cref="ChatOptions"/> settings that Claude cannot accept, before delegating to
/// an Anthropic <see cref="IChatClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ReelBoltAgentBase.BuildChatOptions</c> builds ONE <see cref="ChatOptions"/> instance per
/// agent, in the agent's constructor, and every agent is registered as a singleton — while the
/// provider behind it is resolved per run. So the agent cannot know whether it is about to talk to
/// an OpenAI-shaped backend or to Claude, and the options it produces are shaped for the former.
/// Two distinct things have to be dropped here:
/// </para>
/// <list type="number">
/// <item>
/// <b>Sampling parameters.</b> Anthropic deprecated <c>temperature</c>, <c>top_p</c> and
/// <c>top_k</c> server-side, and the SDK's own <c>[Obsolete]</c> text is explicit that this is not
/// merely advisory: "Models released after Claude Opus 4.6 do not support setting temperature. A
/// value of 1.0 will be accepted for backwards compatibility, all other values will be rejected
/// with a 400 error" (and likewise: any <c>top_k</c> is rejected, <c>top_p</c> is rejected below
/// 0.99). EVERY agent in this solution sets a <c>Temperature</c> (0.2–0.8) and some set
/// <c>TopP</c>, so forwarding them would make every agent run against a current Claude model fail
/// with an HTTP 400.
/// </item>
/// <item>
/// <b>The raw representation.</b> <see cref="ChatOptions.RawRepresentationFactory"/> is how the
/// per-agent <c>ReasoningEffort</c> reaches the wire, and it produces an
/// <c>OpenAI.Chat.ChatCompletionOptions</c> — the wrong SDK's type. Anthropic's adapter documents
/// this property as its own escape hatch for provider-native options, so handing it a foreign SDK's
/// type is at best silently ignored. Nothing is lost by dropping it: it carries only an
/// <c>ReasoningEffort</c>, which on this path is expressed through
/// <see cref="ChatOptions.Reasoning"/> instead — this class never touches that property, so a
/// caller that sets it (<c>AssistantChatLimits.BuildTurnOptions</c> does, for every assistant
/// turn) has it forwarded unchanged.
/// </item>
/// </list>
/// <para>
/// Doing this at the client boundary — the one place that knows the provider kind — keeps the Azure
/// and OpenAI-compatible paths byte-identical and needs no provider awareness in
/// <c>ReelBoltAgentBase</c>.
/// </para>
/// <para>
/// Consequences worth knowing. A per-agent temperature is NOT applied on the Anthropic path. A
/// per-agent reasoning effort IS: <c>ReelBoltAgentBase</c> states it provider-neutrally as
/// <see cref="ChatOptions.Reasoning"/>, which this class never strips, so it reaches the SDK as
/// <c>output_config.effort</c>; the only change made here is the <c>low</c> floor
/// (<see cref="ReasoningEffortMapper"/>). An agent that states no effort gets no thinking
/// configuration at all, so the model thinks at its own default effort and
/// those tokens count against <c>max_tokens</c> (see
/// <c>ChatClientFactory.AnthropicDefaultMaxOutputTokens</c>).
/// </para>
/// <para>
/// What the two effort settings look like on the wire was captured rather than assumed, by running
/// this adapter over the real SDK against a stub transport:
/// <see cref="ReasoningEffort.Low"/> produces
/// <c>"output_config":{"effort":"low"},"thinking":{"type":"adaptive"}</c>, while
/// <see cref="ReasoningEffort.None"/> produces <c>"thinking":{"type":"disabled"}</c> and no
/// <c>output_config</c> at all. That second shape is why nothing in ReelBolt sends <c>none</c>
/// here: a model that always thinks rejects <c>thinking.type=disabled</c> with a 400, which is a
/// broken assistant rather than a cheaper one. <c>AssistantChatLimits.AnthropicReasoningFloor</c>
/// is the floor that keeps the assistant's default effort (<c>none</c> everywhere else) out of
/// this one path.
/// </para>
/// </remarks>
public sealed class AnthropicChatOptionsAdapter : DelegatingChatClient
{
    /// <summary>
    /// Anthropic's Messages API silently validates the <c>system</c> field on every OAuth
    /// (<c>Authorization: Bearer sk-ant-oat…</c>)-authenticated request: for every model except
    /// Haiku, the entire FIRST block of <c>system</c> must be exactly this string — as its own
    /// separate block, not text concatenated into a larger one — or the API returns an unhelpful
    /// HTTP 400 <c>invalid_request_error</c> with message "Error". Undocumented; reproduced live in
    /// https://github.com/anthropics/claude-code/issues/40515, whose proof section is the source of
    /// this exact wording. Applied only when the resolved provider has opted into
    /// <see cref="ReelBolt.Shared.Data.Models.InferenceProvider.PersonalOAuthMode"/> — ReelBolt is
    /// not actually Claude Code (see docs/anthropic-provider.md's "what this deliberately is not"),
    /// so this is never sent to a Console-API-key-authenticated provider.
    /// </summary>
    public const string ClaudeCodeIdentityPrompt = "You are Claude Code, Anthropic's official CLI for Claude.";

    /// <summary>
    /// Floor applied to a caller-supplied <see cref="ChatOptions.MaxOutputTokens"/> on the
    /// Anthropic path. Only ever RAISES a smaller value; a caller asking for more keeps it, and a
    /// caller asking for nothing still gets <c>ChatClientFactory.AnthropicDefaultMaxOutputTokens</c>.
    /// <para>
    /// A current Claude model thinks before it writes, and those tokens are charged against
    /// <c>max_tokens</c> — so a ceiling sized for a non-thinking backend is spent on reasoning and
    /// the visible answer is truncated mid-sentence, or never starts. This is not a hypothesis:
    /// <c>InferenceProvidersController.PingMaxOutputTokens</c> already special-cases the same
    /// hazard for connectivity pings, and a room step (<c>RoomSeatAgent</c>, whose
    /// <c>MaxTurnTokens</c> defaults to 220) reproduced it live — one seat's whole contribution to
    /// an edit-room deliberation came back as the 14 characters "Materially the". Nothing surfaced
    /// that as an error: a truncated turn is a valid turn, so the room simply proceeded with one
    /// participant silently reduced to noise.
    /// </para>
    /// <para>
    /// Clamping here rather than at each call site keeps provider awareness in the one class that
    /// has it — the same argument that puts the sampling-parameter removal below in this adapter —
    /// and leaves both OpenAI-shaped paths byte-identical. Callers that want SHORT output still get
    /// it: brevity comes from the prompt, and this only changes where the hard cut-off lands.
    /// </para>
    /// </summary>
    public const int MinMaxOutputTokens = 4096;

    private readonly IDisposable? _ownedClient;
    private readonly bool _personalOAuthMode;

    /// <param name="innerClient">The SDK's <see cref="IChatClient"/> adapter to delegate to.</param>
    /// <param name="ownedClient">
    /// The underlying <c>AnthropicClient</c>, whose lifetime this wrapper takes over.
    /// <para>
    /// It has to be passed explicitly because nothing else disposes it: the SDK's own
    /// <c>AnthropicChatClient.Dispose</c> is an empty method (verified in the assembly — its IL
    /// body is a bare <c>ret</c>), so the adapter returned by <c>AsIChatClient</c> does not own the
    /// client it wraps. Without this the <c>AnthropicClient</c> — which is
    /// <see cref="IDisposable"/>, and holds an <c>HttpClient</c> — would be created and never
    /// released, which is what CodeQL's "Missing Dispose call on local IDisposable" flags.
    /// </para>
    /// <para>
    /// Note this only makes ownership explicit and correct; it does not change when disposal
    /// happens today. <c>ChatClientFactory</c> caches every client it builds for the lifetime of
    /// the process and is not itself disposable, which is deliberate — these are connection-pooled
    /// clients shared across executions, exactly like the two OpenAI arms.
    /// </para>
    /// </param>
    /// <param name="personalOAuthMode">
    /// See <see cref="ReelBolt.Shared.Data.Models.InferenceProvider.PersonalOAuthMode"/>. When
    /// true, every request this instance sends gets <see cref="ClaudeCodeIdentityPrompt"/> prepended
    /// as its own leading system block (see <see cref="WithIdentityPrompt"/>).
    /// </param>
    public AnthropicChatOptionsAdapter(IChatClient innerClient, IDisposable? ownedClient = null, bool personalOAuthMode = false)
        : base(innerClient)
    {
        _ownedClient = ownedClient;
        _personalOAuthMode = personalOAuthMode;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ownedClient?.Dispose();
        }

        base.Dispose(disposing);
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        (ChatOptions? preparedOptions, IEnumerable<ChatMessage> preparedMessages) = Prepare(messages, options);
        return base.GetResponseAsync(preparedMessages, preparedOptions, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        (ChatOptions? preparedOptions, IEnumerable<ChatMessage> preparedMessages) = Prepare(messages, options);
        return base.GetStreamingResponseAsync(preparedMessages, preparedOptions, cancellationToken);
    }

    /// <summary>
    /// The order matters. The OAuth identity prompt must stay the FIRST system block, so it is
    /// inserted before caching runs; caching then promotes a stable instructions string to the
    /// last system block and marks that block, which caches identity + tools + system together.
    /// Stripping OpenAI-shaped settings runs first so the caching step sees the options that will
    /// actually be sent and never has to clone them twice.
    /// </summary>
    private (ChatOptions? Options, IEnumerable<ChatMessage> Messages) Prepare(
        IEnumerable<ChatMessage> messages, ChatOptions? options) =>
        AnthropicPromptCaching.Apply(Normalize(options), WithIdentityPrompt(messages));

    /// <summary>
    /// Inserts <see cref="ClaudeCodeIdentityPrompt"/> as its own leading system-role
    /// <see cref="ChatMessage"/> when <see cref="_personalOAuthMode"/> is set — immediately before
    /// the first existing system message, or at the very front when there is none (an agent with no
    /// instructions, or a bare "ping" connectivity test). It must land as a separate message/content
    /// part rather than text appended to an existing one: verified by reflecting the Anthropic SDK's
    /// own request-building code (<c>AnthropicChatClient.CreateMessageParams</c>) that each
    /// <see cref="TextContent"/> across the system-role message(s) becomes its own block in the
    /// wire-level <c>system</c> array — which is exactly the shape
    /// https://github.com/anthropics/claude-code/issues/40515 confirmed the API requires. Returns
    /// the input unchanged (no allocation) when the mode is off or the phrase is already the lead.
    /// </summary>
    private IEnumerable<ChatMessage> WithIdentityPrompt(IEnumerable<ChatMessage> messages)
    {
        if (!_personalOAuthMode)
            return messages;

        List<ChatMessage> list = messages as List<ChatMessage> ?? messages.ToList();

        int firstSystemIndex = list.FindIndex(m => m.Role == ChatRole.System);
        if (firstSystemIndex >= 0 && AlreadyPrefixed(list[firstSystemIndex]))
            return list;

        int insertAt = firstSystemIndex >= 0 ? firstSystemIndex : 0;
        List<ChatMessage> result = new(list.Count + 1);
        result.AddRange(list.Take(insertAt));
        result.Add(new ChatMessage(ChatRole.System, ClaudeCodeIdentityPrompt));
        result.AddRange(list.Skip(insertAt));
        return result;
    }

    private static bool AlreadyPrefixed(ChatMessage systemMessage) =>
        systemMessage.Contents.OfType<TextContent>().FirstOrDefault()?.Text
            .StartsWith(ClaudeCodeIdentityPrompt, StringComparison.Ordinal) == true;

    /// <summary>
    /// Returns <paramref name="options"/> untouched when there is nothing to remove, so the case
    /// with nothing to do allocates nothing. Otherwise clones — never mutates the caller's
    /// instance, which is a singleton agent's field shared across concurrent executions and across
    /// providers. Mutating it would silently strip sampling settings from that agent's later runs
    /// against OpenAI too.
    /// </summary>
    private static ChatOptions? Normalize(ChatOptions? options)
    {
        if (options is null)
            return null;

        // The floor comes first so every check below sees the effort that will actually be sent.
        options = ReasoningEffortMapper.Project(options, ReelBolt.Shared.Data.Models.InferenceProviderKind.Anthropic)!;

        bool needsChange = options.RawRepresentationFactory is not null
            || options.Temperature is not null
            || options.TopP is not null
            || options.TopK is not null
            || IsBelowThinkingFloor(options.MaxOutputTokens);

        if (!needsChange)
            return options;

        ChatOptions normalized = options.Clone();
        normalized.RawRepresentationFactory = null;
        normalized.Temperature = null;
        normalized.TopP = null;
        normalized.TopK = null;

        if (IsBelowThinkingFloor(normalized.MaxOutputTokens))
            normalized.MaxOutputTokens = MinMaxOutputTokens;

        return normalized;
    }

    private static bool IsBelowThinkingFloor(int? maxOutputTokens) =>
        maxOutputTokens is int max && max < MinMaxOutputTokens;
}
