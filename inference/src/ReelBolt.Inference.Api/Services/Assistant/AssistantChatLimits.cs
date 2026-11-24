using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;

namespace ReelBolt.Inference.Api.Services.Assistant;

/// <summary>
/// The server-enforced bounds on what a single assistant turn may cost — how long one user message
/// may be, how much of a thread is replayed into the next prompt, how much the model may write, and
/// how much it may think before it writes — and the per-turn <see cref="ChatOptions"/> those resolve
/// into. Every one of them is per-deployment configurable with a sane default, following the same
/// convention as <c>Assistant:MaxExecutionsPerHour</c>.
/// </summary>
/// <remarks>
/// These exist because assistant threads became <b>server-persisted</b>. While history was a
/// client-side buffer that died with the page, an oversized message cost exactly one turn. Now the
/// message is written to <c>assistant_messages.content</c> and replayed into <i>every later prompt
/// for the life of that thread</i>, and <c>LoadHistoryAsync</c> fed the whole thread back with no
/// window and no cap — so one large message inflated that thread permanently, not once.
/// </remarks>
public static class AssistantChatLimits
{
    /// <summary>Config key for <see cref="MaxMessageChars"/>.</summary>
    public const string MaxMessageCharsKey = "Assistant:MaxMessageChars";

    /// <summary>Config key for <see cref="MaxReplayMessages"/>.</summary>
    public const string MaxReplayMessagesKey = "Assistant:MaxReplayMessages";

    /// <summary>
    /// The default cap on one user message, in characters. Generous for a typed chat turn —
    /// including a pasted workflow JSON — while still bounding what a single turn can add to a
    /// thread for every turn that follows it.
    /// </summary>
    public const int DefaultMaxMessageChars = 32_000;

    /// <summary>
    /// The default cap on how many of a thread's <b>most recent</b> persisted messages are replayed
    /// into the prompt. A "message" is one persisted turn row, so the default is 20 exchanges —
    /// comfortably more context than a conversation needs, and far less than a thread can grow to.
    /// </summary>
    public const int DefaultMaxReplayMessages = 40;

    /// <summary>Config key for the per-turn output-token budget.</summary>
    public const string MaxOutputTokensKey = "Assistant:MaxOutputTokens";

    /// <summary>
    /// The default allowance of output tokens for the assistant's <b>visible answer</b> on one
    /// model turn. 16,384 is the ceiling ReelBolt already sent on the Anthropic path (the factory's
    /// own default), so a deployment that configures nothing keeps the budget it had; what changes
    /// is that the number is now the ANSWER's share rather than the whole turn's.
    /// </summary>
    public const int DefaultAnswerTokens = 16_384;

    /// <summary>
    /// Tokens held back for the model's own reasoning on a provider that charges that reasoning
    /// against the same ceiling as the answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the headroom the reported bug is about. Under Anthropic's <c>thinking.type=adaptive</c>
    /// and Google's native mapping of <c>reasoning_effort</c>, a turn's reasoning and its visible
    /// answer are drawn from ONE <c>max_tokens</c> budget: a model that "thinks a lot" can spend the
    /// whole ceiling before it writes a word, and what the user gets is a sentence cut off in the
    /// middle with nothing to say it was cut. ReelBolt sends no thinking configuration at all
    /// (see <c>AnthropicChatOptionsAdapter</c>), so that reasoning phase is unbounded within the
    /// ceiling — reserving a share for it is the only lever available without mapping the project's
    /// vLLM/Qwen reasoning vocabulary onto <c>ChatOptions.Reasoning</c>, which the Anthropic doc
    /// records as a deliberate follow-up.
    /// </para>
    /// <para>
    /// 8,192 is the same order as the floor the adapter already applies to a caller-supplied
    /// Anthropic ceiling (<c>AnthropicChatOptionsAdapter.MinMaxOutputTokens</c>, 4,096 — "a ceiling
    /// sized for a non-thinking backend is spent on reasoning"), doubled, because that floor exists
    /// to let thinking happen at all and this one exists to let thinking happen AND leave an answer
    /// behind. It doubles the effective Anthropic turn ceiling from 16,384 to 24,576, well under
    /// half of a current Claude model's 64k output limit.
    /// </para>
    /// </remarks>
    public const int ReasoningHeadroomTokens = 8_192;

    /// <summary>Config key for <see cref="MaxRoundTripInputTokens"/>.</summary>
    public const string MaxRoundTripInputTokensKey = "Assistant:MaxRoundTripInputTokens";

    /// <summary>Config key for <see cref="MaxToolCallsPerStep"/>.</summary>
    public const string MaxToolCallsPerStepKey = "Assistant:MaxToolCallsPerStep";

    /// <summary>Config key for <see cref="MaxToolIterations"/>.</summary>
    public const string MaxToolIterationsKey = "Assistant:MaxToolIterations";

    /// <summary>
    /// The default cap on model round-trips in one assistant turn's tool loop (<c>FunctionInvokingChatClient.MaximumIterationsPerRequest</c>;
    /// the library default is 40). At the cap the loop makes one last call without tools and the model must answer in text.
    /// </summary>
    public const int DefaultMaxToolIterations = 80;

    /// <summary>The default cap on tool calls one model step may make; the rest get a plain refusal.</summary>
    public const int DefaultMaxToolCallsPerStep = 8;

    /// <summary>
    /// The most input tokens one model round-trip of a charged turn may send; a larger request is
    /// refused before it runs. The default is the replay window at its largest: <see cref="MaxReplayMessages"/>
    /// messages of <see cref="MaxMessageChars"/> characters, at four characters a token.
    /// </summary>
    public static int MaxRoundTripInputTokens(IConfiguration? configuration) =>
        Resolve(configuration, MaxRoundTripInputTokensKey,
            (int)Math.Min(int.MaxValue, (long)MaxReplayMessages(configuration) * MaxMessageChars(configuration) / 4));

    /// <summary>The most model round-trips one assistant turn's tool loop may make (min 1).</summary>
    public static int MaxToolIterations(IConfiguration? configuration) =>
        Resolve(configuration, MaxToolIterationsKey, DefaultMaxToolIterations);

    /// <summary>The most tool calls one model step may make.</summary>
    public static int MaxToolCallsPerStep(IConfiguration? configuration) =>
        Resolve(configuration, MaxToolCallsPerStepKey, DefaultMaxToolCallsPerStep);

    /// <summary>
    /// The output tokens a round-trip is pre-claimed at: the ceiling sent (<see cref="MaxOutputTokens"/>), or, on a
    /// provider that is sent none, the default answer allowance plus the reasoning headroom.
    /// </summary>
    public static int ClaimedOutputTokens(IConfiguration? configuration, InferenceProviderKind? kind) =>
        MaxOutputTokens(configuration, kind) ?? DefaultAnswerTokens + ReasoningHeadroomTokens;

    /// <summary>Config key for <see cref="DetachedTurnSeconds"/>.</summary>
    public const string DetachedTurnSecondsKey = "Assistant:DetachedTurnSeconds";

    /// <summary>
    /// The default time a turn keeps running with NOBODY attached to it — the browser that asked for it went away
    /// (closed panel, navigation, reload, a phone screen switching off) and no other screen re-attached — before it
    /// is cancelled. A turn nobody is watching must not hold a workspace's turn-concurrency slot and its credit hold
    /// for its whole wall clock, so this is the bound that releases them.
    /// </summary>
    /// <remarks>
    /// Not the same bound as <see cref="MaxTurnSeconds"/>: that is the absolute ceiling whether or not anyone is
    /// connected. This one counts only unattended time: it does not run while a re-attached screen is reading the
    /// turn, nor while the turn is held on a permission card (the card has its own timeout,
    /// <c>Assistant:ApprovalTimeoutSeconds</c>, and stays answerable). A turn it cancels stores an
    /// "interrupted" assistant message saying what it did before it stopped.
    /// </remarks>
    public const int DefaultDetachedTurnSeconds = 300;

    /// <summary>How long a turn may keep running after its client disconnected, in seconds.</summary>
    public static int DetachedTurnSeconds(IConfiguration? configuration) =>
        Resolve(configuration, DetachedTurnSecondsKey, DefaultDetachedTurnSeconds);

    /// <summary>Config key for <see cref="MaxTurnSeconds"/>.</summary>
    public const string MaxTurnSecondsKey = "Assistant:MaxTurnSeconds";

    /// <summary>
    /// The default hard wall-clock ceiling of one assistant chat turn or MCP tool call. The stale-hold sweep
    /// (<c>NonWorkflowChargeService.StaleTurnHoldAge</c>) must stay at least twice this long.
    /// </summary>
    public const int DefaultMaxTurnSeconds = 1800;

    /// <summary>The hard wall-clock ceiling of one turn, connected or not, in seconds.</summary>
    public static int MaxTurnSeconds(IConfiguration? configuration) =>
        Resolve(configuration, MaxTurnSecondsKey, DefaultMaxTurnSeconds);

    /// <summary>The largest single user message this deployment accepts, in characters.</summary>
    public static int MaxMessageChars(IConfiguration? configuration) =>
        Resolve(configuration, MaxMessageCharsKey, DefaultMaxMessageChars);

    /// <summary>How many of a thread's newest messages this deployment replays into a prompt.</summary>
    public static int MaxReplayMessages(IConfiguration? configuration) =>
        Resolve(configuration, MaxReplayMessagesKey, DefaultMaxReplayMessages);

    /// <summary>
    /// Whether this provider draws its reasoning from the same ceiling as the visible answer, so a
    /// turn's budget has to carry headroom for both. Anthropic is the case this codebase has already
    /// measured (a room seat's whole contribution came back as "Materially the" under a 220-token
    /// ceiling); Gemini is included on the same ground the provider tester already special-cases it
    /// for (<c>InferenceProviderTester.PingMaxOutputTokens</c> allocates Gemini and DeepSeek 1,024
    /// tokens of reasoning headroom where an OpenAI-shaped backend gets 128). DeepSeek's
    /// <c>deepseek-reasoner</c> is deliberately NOT here: it reports reasoning separately from the
    /// answer and caps it on its own, so its answer allowance is already the whole answer.
    /// </summary>
    public static bool ChargesReasoningAgainstTheAnswerCeiling(InferenceProviderKind? kind) =>
        kind is InferenceProviderKind.Anthropic or InferenceProviderKind.Gemini;

    /// <summary>
    /// The output-token ceiling to send for this turn, or <c>null</c> to send none and leave the
    /// provider's own default in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resolved value is the ANSWER's allowance plus <see cref="ReasoningHeadroomTokens"/> on a
    /// provider that shares the ceiling (<see cref="ChargesReasoningAgainstTheAnswerCeiling"/>) — an
    /// allowance the deployment asked for is a promise about the answer, not about the reasoning the
    /// model happens to do first.
    /// </para>
    /// <para>
    /// <b>When a ceiling is sent at all</b> is a deliberate decision per provider, not an
    /// oversight. The Anthropic Messages API <i>requires</i> <c>max_tokens</c> on every request, so
    /// there is no "leave the default alone" option there and the choice is only ever between the
    /// SDK's built-in number and ours — ours wins. Every other kind accepts a request with no
    /// ceiling, and ReelBolt cannot size one for it: an OpenAI-compatible row may point at a
    /// self-hosted vLLM server whose context window is whatever the operator passed to
    /// <c>--max-model-len</c>, where a ceiling ReelBolt invented could turn a working turn into a
    /// 400. So those paths send a ceiling only when the deployment explicitly configures one, and
    /// otherwise stay exactly as they are today.
    /// </para>
    /// </remarks>
    public static int? MaxOutputTokens(IConfiguration? configuration, InferenceProviderKind? kind)
    {
        bool configured = TryResolve(configuration, MaxOutputTokensKey, out int answer);
        int allowance = configured ? answer : DefaultAnswerTokens;

        if (ChargesReasoningAgainstTheAnswerCeiling(kind))
        {
            return allowance + ReasoningHeadroomTokens;
        }

        return configured ? allowance : null;
    }

    /// <summary>Config key for the per-turn reasoning effort.</summary>
    public const string ReasoningEffortKey = "Assistant:ReasoningEffort";

    /// <summary>
    /// The value that means "let the provider decide": no reasoning configuration travels at all,
    /// which is exactly what every assistant turn did before this key existed. It is the escape
    /// hatch for a deployment whose provider rejects a reasoning field outright.
    /// </summary>
    public const string ProviderDefaultReasoningEffort = "auto";

    /// <summary>
    /// The reasoning effort an assistant chat turn is sent with when the deployment configures
    /// nothing: <see cref="ReasoningEffort.None"/> — thinking OFF.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the reported bug (issue #228: "assistant thinks too much in chat"). The assistant
    /// sent no reasoning configuration at all, so every turn ran at whatever the provider defaults
    /// to — the most thinking a reasoning-capable backend will do — and a chat reply that should
    /// take a second spent that second (and its tokens) deliberating first. Thinking is invisible
    /// in the reply, so the only symptom a user sees is a slow, expensive answer.
    /// </para>
    /// <para>
    /// OFF rather than "a bit less" is the house default, not a new opinion. Every other
    /// latency-sensitive surface in ReelBolt already asks for it by name:
    /// <c>EditRoomStepConfig</c>/<c>GraphicsRoomStepConfig</c>/<c>ColorGradeRoomStepConfig</c> all
    /// default their participants' <c>ReasoningEffort</c> to <c>"none"</c>, and
    /// <c>ReelBoltAgentBase</c>'s remarks record the measured reason (<c>none</c> makes the
    /// deployed vLLM/Qwen template skip the thinking scaffold entirely — 0 reasoning tokens, and a
    /// shorter rendered prompt). The chat assistant was the one interactive surface left at the
    /// provider's own default.
    /// </para>
    /// <para>
    /// Raise it with <see cref="ReasoningEffortKey"/> where the added deliberation earns its
    /// latency: the assistant does agentic work (composing workflows through tools) and a
    /// deployment may prefer <c>low</c> or <c>medium</c> for that.
    /// </para>
    /// </remarks>
    public static readonly ReasoningEffort DefaultReasoningEffort = ReasoningEffort.None;

    /// <summary>
    /// The lowest effort the Anthropic path is ever sent: <see cref="ReasoningEffort.Low"/>, never
    /// <see cref="ReasoningEffort.None"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="AnthropicChatOptionsAdapter"/> maps <see cref="ReasoningEffort.None"/> onto
    /// <c>thinking.type=disabled</c>, which a model that always thinks rejects with a 400 — turning
    /// "stop thinking so much" into "the assistant stops answering" on exactly the deployments that
    /// cannot comply. Anthropic's documented way to ask for less is the other half of that same
    /// mapping: <c>output_config.effort</c>, which every supported model accepts. So on this one
    /// kind the floor is <c>low</c>: the issue's "switch thinking off, or at least bring it down",
    /// with "at least" applying where "off" is a hard error.
    /// </remarks>
    public static readonly ReasoningEffort AnthropicReasoningFloor = ReasoningEffort.Low;

    /// <summary>
    /// The reasoning effort to send for this turn, or <c>null</c> to send no reasoning
    /// configuration and leave the provider's own default in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The vocabulary is the one the rest of the codebase already uses —
    /// <c>none</c>, <c>low</c>, <c>medium</c>, <c>xhigh</c> — deliberately, because it is the same
    /// set <c>AgentReasoningEffort.Vocabulary</c> restricts agent turns to and the same set
    /// the deployed Qwen chat template accepts. The generic <see cref="ReasoningEffort"/> enum's
    /// own <c>High</c> is therefore NOT accepted: this template's guard rejects it, and a 500 on
    /// every assistant turn is a worse outcome than an unsupported setting being ignored.
    /// </para>
    /// <para>
    /// Anything unparseable — including the empty string — falls back to
    /// <see cref="DefaultReasoningEffort"/> rather than to "no configuration", the same rule
    /// <see cref="Resolve"/> applies to every other bound: a typo must not silently mean something
    /// other than what the operator was trying to set. The opt-out is spelled
    /// <see cref="ProviderDefaultReasoningEffort"/>, explicitly.
    /// </para>
    /// </remarks>
    public static ReasoningEffort? ReasoningEffortFor(IConfiguration? configuration, InferenceProviderKind? kind)
    {
        string? raw = configuration?[ReasoningEffortKey];

        if (string.Equals(raw?.Trim(), ProviderDefaultReasoningEffort, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ReasoningEffort effort = ParseReasoningEffort(raw) ?? DefaultReasoningEffort;

        return kind is InferenceProviderKind.Anthropic && effort == ReasoningEffort.None
            ? AnthropicReasoningFloor
            : effort;
    }

    /// <summary>
    /// The <see cref="ChatOptions"/> one assistant turn is run with: the output ceiling of
    /// <see cref="MaxOutputTokens"/> and the reasoning effort of
    /// <see cref="ReasoningEffortFor"/>, composed in the one place both are known.
    /// </summary>
    /// <remarks>
    /// <c>null</c> — a request with no options at all, byte-identical to the shape every turn had
    /// before either key existed — is returned only when NEITHER is configured AND the deployment
    /// opted out of the reasoning default, i.e. <c>Assistant:ReasoningEffort=auto</c> on a kind
    /// ReelBolt sends no ceiling to. That keeps a self-host install that wants to be left alone
    /// exactly as it was.
    /// </remarks>
    public static ChatOptions? BuildTurnOptions(IConfiguration? configuration, InferenceProviderKind? kind)
    {
        int? maxOutputTokens = MaxOutputTokens(configuration, kind);
        ReasoningEffort? effort = ReasoningEffortFor(configuration, kind);

        if (maxOutputTokens is null && effort is null)
        {
            // Prompt caching (initiative anthropic-prompt-caching): the assistant's system block is
            // NOT stable across turns — it carries the session context (user, org, page, project ids)
            // and the docs retrieved for this message. A system breakpoint there would write a cache
            // entry every turn and read none, so this path asks for the newest-turn breakpoint only:
            // the session block is constant for the whole turn, so the in-turn tool loop still hits.
            // Anthropic is the only kind that gets a non-null options object here (it carries only the
            // mode); every other kind keeps the byte-identical null shape.
            return kind == InferenceProviderKind.Anthropic
                ? new ChatOptions().WithMode(AnthropicPromptCacheMode.TurnOnly)
                : null;
        }

        ChatOptions options = new()
        {
            MaxOutputTokens = maxOutputTokens,
            Reasoning = effort is { } value ? new ReasoningOptions { Effort = value } : null
        };

        if (kind == InferenceProviderKind.Anthropic)
        {
            options.WithMode(AnthropicPromptCacheMode.TurnOnly);
        }

        return options;
    }

    /// <summary>
    /// The house reasoning-effort vocabulary as <see cref="ReasoningEffort"/>, or <c>null</c> when
    /// the value is not one of them. See <see cref="ReasoningEffortFor"/> for why <c>high</c> is
    /// absent and why <c>xhigh</c> is not spelled <c>ExtraHigh</c>.
    /// </summary>
    private static ReasoningEffort? ParseReasoningEffort(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "none" => ReasoningEffort.None,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "xhigh" => ReasoningEffort.ExtraHigh,
        _ => null
    };

    /// <summary>
    /// The explicit, human-readable marker a truncated assistant reply ends with, naming the bound it
    /// hit. It exists so a reader of the thread can SEE that the reply was cut — a silently shorter
    /// answer is indistinguishable from a model that simply stopped talking.
    /// </summary>
    /// <remarks>
    /// It carries the resolved cap rather than a second hardcoded number, so the message always
    /// names the bound actually enforced on this deployment.
    /// </remarks>
    public static string TruncationMarker(int maxMessageChars) =>
        $"\n\n[ReelBolt truncated this reply: it exceeded {maxMessageChars} characters, the maximum "
        + "length of one stored message. The remainder was not saved. Ask a follow-up to continue.]";

    /// <summary>
    /// Bounds one assistant reply to <paramref name="maxMessageChars"/>, appending
    /// <see cref="TruncationMarker"/> when it had to cut anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An assistant turn is capped for the same reason a user turn is: it is persisted to
    /// <c>assistant_messages</c> and replayed into every later prompt of that thread. A model that
    /// answers at length would otherwise inflate the thread permanently, exactly as one oversized
    /// user message did — the replay window does not save it, it only multiplies it.
    /// </para>
    /// <para>
    /// Truncate-with-a-marker rather than reject, because this is the ONE bound that runs <i>after</i>
    /// the model call: the tokens are already spent and the turn already exists, so a 400 here would
    /// throw away a reply the caller paid for and leave the user turn persisted beside nothing. The
    /// marker is what keeps that trade honest.
    /// </para>
    /// <para>
    /// A cap too small to hold the marker degrades to a bare cut at the cap. The marker must never
    /// push the row back OVER the bound it exists to enforce.
    /// </para>
    /// </remarks>
    public static string TruncateAssistantReply(string reply, int maxMessageChars)
    {
        if (reply.Length <= maxMessageChars)
        {
            return reply;
        }

        string marker = TruncationMarker(maxMessageChars);

        if (marker.Length >= maxMessageChars)
        {
            return reply[..maxMessageChars];
        }

        return string.Concat(reply.AsSpan(0, maxMessageChars - marker.Length), marker);
    }

    /// <summary>
    /// Reads one bound. A configured value must be a <b>positive</b> integer to take effect: zero,
    /// negative and unparseable values all fall back to the default rather than disabling the bound
    /// they configure, because "someone mistyped the config" must never silently mean "unbounded
    /// prompt" — the failure mode this whole type exists to prevent.
    /// </summary>
    /// <remarks>
    /// Indexer rather than <c>GetValue</c>: a missing key and an absent configuration both have to
    /// resolve to the default here, and this is the only form that stays total for both.
    /// </remarks>
    private static int Resolve(IConfiguration? configuration, string key, int fallback) =>
        TryResolve(configuration, key, out int value) ? value : fallback;

    /// <summary>
    /// The same rule as <see cref="Resolve"/>, reporting whether the key carried a usable value at
    /// all. Callers that must distinguish "the deployment asked for X" from "nothing was configured"
    /// — the output budget, which is only sent when it was asked for — need the difference, and
    /// inferring it from the resolved value would read the default as a configuration.
    /// </summary>
    private static bool TryResolve(IConfiguration? configuration, string key, out int value) =>
        int.TryParse(configuration?[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value > 0;

    /// <summary>
    /// The provider's own finish reason for "I stopped because I ran out of output tokens", as
    /// <c>Microsoft.Extensions.AI</c> spells it (<c>ChatFinishReason.Length</c>). Anthropic's
    /// <c>max_tokens</c> stop reason is mapped onto this by the SDK, which is the whole point: the
    /// information was already on the response and was being thrown away.
    /// </summary>
    public const string LengthFinishReason = "length";

    /// <summary>Whether a provider finish reason means the reply was cut off by the output budget.</summary>
    public static bool IsModelTruncation(string? finishReason) =>
        string.Equals(finishReason, LengthFinishReason, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The AI SDK data-stream <c>finish</c> vocabulary (<c>stop</c>, <c>length</c>,
    /// <c>content-filter</c>, <c>tool-calls</c>, <c>error</c>, <c>other</c>, <c>unknown</c>) for a
    /// <c>Microsoft.Extensions.AI</c> finish reason.
    /// </summary>
    /// <remarks>
    /// The two vocabularies are close but not identical — <c>ChatFinishReason.ToolCalls</c> is
    /// <c>tool_calls</c> with an underscore and <c>ContentFilter</c> is <c>content_filter</c>, while
    /// the stream protocol hyphenates both — so this is a mapping, never a pass-through. An absent
    /// reason keeps the pre-existing <c>stop</c>: a provider that reports nothing must not be
    /// reported to the client as having stopped for a reason it never claimed.
    /// </remarks>
    public static string StreamFinishReason(string? finishReason) => finishReason switch
    {
        null or "" => "stop",
        "stop" => "stop",
        "length" => "length",
        "tool_calls" => "tool-calls",
        "content_filter" => "content-filter",
        _ => "other"
    };

    /// <summary>
    /// The marker appended to an assistant reply the <b>model</b> cut off — the visible counterpart
    /// of <see cref="TruncationMarker"/>, which reports the character bound applied after the call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A half sentence streamed as though it were a finished answer is the defect this exists to
    /// close: the user has no way to tell "the assistant decided to stop there" from "the assistant
    /// ran out of room", and the second one looks like the assistant is confused. The marker is
    /// appended to the SAME string that is persisted and streamed, so the thread the user reads and
    /// the thread the model replays agree — exactly as the character bound does it.
    /// </para>
    /// <para>
    /// <paramref name="maxOutputTokens"/> is the ceiling ReelBolt actually sent, or null when it
    /// sent none and the provider's own default applied. Naming a number the deployment never
    /// configured would be worse than saying nothing, so the marker only quotes one it knows.
    /// </para>
    /// </remarks>
    public static string ModelTruncationMarker(int? maxOutputTokens) =>
        maxOutputTokens is int tokens
            ? $"\n\n[ReelBolt: this reply is incomplete \u2014 the assistant reached the {tokens}-token "
                + "limit for one reply, which covers any reasoning it did as well as what you can "
                + "read, before it had finished. Ask a follow-up to continue.]"
            : "\n\n[ReelBolt: this reply is incomplete \u2014 the assistant reached the maximum length "
                + "of one reply before it had finished. Ask a follow-up to continue.]";

}
