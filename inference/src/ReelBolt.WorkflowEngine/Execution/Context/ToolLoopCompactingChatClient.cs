using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace ReelBolt.WorkflowEngine.Execution.Context;

/// <summary>
/// A <see cref="DelegatingChatClient"/> that keeps an agent's tool-calling loop from growing
/// without bound, by compacting the message list before each model call (see
/// <see cref="ToolLoopCompactor"/> for exactly what is rewritten and what never is).
///
/// <para>
/// <b>Where it sits, and why it must be exactly there.</b> <c>FunctionInvokingChatClient</c> owns
/// the loop: it calls the client below it, runs the tools the model asked for, appends the calls
/// and results to ONE accumulating history, and calls the client below it again with that whole
/// history. So the place that sees the growing list on every iteration is BELOW
/// <c>FunctionInvokingChatClient</c> and above the provider client — which is where
/// <see cref="ToolLoopPipeline"/> puts this class. Above it, the client would see one call (the
/// agent's) and never the loop. The framework adds its own <c>FunctionInvokingChatClient</c> only
/// when the client it is handed does not already contain one, so the pipeline builds it here, in
/// order to sit under it (and, as a side effect, to configure its iteration limits).
/// </para>
///
/// <para>
/// Compaction is applied to a COPY on every call. The history <c>FunctionInvokingChatClient</c>
/// holds stays raw, and so does everything built from it afterward — the response messages, the
/// tool-call trace, the reasoning capture. Only what the provider is sent shrinks.
/// </para>
/// </summary>
public sealed class ToolLoopCompactingChatClient : DelegatingChatClient
{
    private readonly ToolLoopBudgetOptions _budget;
    private readonly int _maxIterations;
    private readonly ToolLoopRunStats _stats;
    private readonly bool _bindsSignedReasoning;

    /// <summary>
    /// The provider name Microsoft.Extensions.AI's Anthropic adapter reports in its
    /// <see cref="ChatClientMetadata"/> (Anthropic SDK 12.55.0, <c>AnthropicChatClient.GetService</c>).
    /// </summary>
    internal const string AnthropicProviderName = "anthropic";

    public ToolLoopCompactingChatClient(
        IChatClient innerClient,
        ToolLoopBudgetOptions budget,
        int maxIterations,
        ToolLoopRunStats stats)
        : base(innerClient)
    {
        _budget = budget;
        _maxIterations = maxIterations;
        _stats = stats;
        // Which provider sits below decides whether signed reasoning freezes the prefix: only
        // Anthropic binds a thinking block's signature to the messages before it. ReelBolt's
        // AnthropicChatOptionsAdapter is a DelegatingChatClient, so the SDK client's metadata
        // reaches us through it; OpenAI, Azure and every other kind report another name.
        _bindsSignedReasoning = string.Equals(
            innerClient.GetService<ChatClientMetadata>()?.ProviderName, AnthropicProviderName, StringComparison.OrdinalIgnoreCase);
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ChatMessage> forwarded = Prepare(messages, options);
        ChatResponse response = await base.GetResponseAsync(forwarded, options, cancellationToken);
        _stats.RecordUsage(response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IEnumerable<ChatMessage> forwarded = Prepare(messages, options);
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(forwarded, options, cancellationToken))
        {
            foreach (AIContent content in update.Contents)
            {
                if (content is UsageContent usage)
                    _stats.RecordUsage(usage.Details);
            }

            yield return update;
        }
    }

    private IEnumerable<ChatMessage> Prepare(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        IReadOnlyList<ChatMessage> list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();

        // ChatClientAgent hands the system prompt over as ChatOptions.Instructions, not as a message,
        // but it is part of every call and has to count against the same ceiling.
        int instructionChars = options?.Instructions?.Length ?? 0;

        ToolLoopCompactor.Outcome outcome = ToolLoopCompactor.Compact(list, instructionChars, _budget, _bindsSignedReasoning);
        _stats.RecordCall(outcome.RawChars, outcome.ForwardedChars, _maxIterations);

        // QA-P0-10, decided bound: on Anthropic the frozen prefix plus the pending results can
        // outgrow anything compaction may still touch. Over the hard ceiling, fail the step here
        // with a message the author can act on, instead of sending a call the provider will refuse.
        //
        // QA-P0-30: that frozen prefix is the whole reason the ceiling exists. With no signed
        // thinking block on the list nothing is frozen — every content is compactable — so
        // compaction is the bound and a thinking-off run is never failed here.
        if (_bindsSignedReasoning && HasFrozenPrefix(list)
            && _budget.Enabled && _budget.HardMaxChars > 0 && outcome.ForwardedChars > _budget.HardMaxChars)
            throw new ContextTooLargeException(outcome.ForwardedChars, _budget.HardMaxChars);

        // Under budget: the caller's own enumerable, untouched.
        return ReferenceEquals(outcome.Messages, list) ? messages : outcome.Messages;
    }

    /// <summary>
    /// True when the list carries a signed thinking block, i.e. everything before it is frozen and
    /// compaction may not rewrite it. This is the condition <see cref="ToolLoopCompactor.Compact"/>
    /// keys its anchor on (<c>LastSignedReasoningMessage</c>): the same predicate, so the ceiling and
    /// the freeze agree on what "nothing frozen" means.
    /// </summary>
    private static bool HasFrozenPrefix(IReadOnlyList<ChatMessage> messages)
    {
        foreach (ChatMessage message in messages)
        {
            foreach (AIContent content in message.Contents)
            {
                if (content is TextReasoningContent { ProtectedData.Length: > 0 })
                    return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Builds the tool-calling pipeline for one agent run:
/// <c>FunctionInvokingChatClient</c> → <see cref="ToolLoopCompactingChatClient"/> → the provider
/// client. This is the one line <c>ReelBoltAgentBase.CreateAgentAsync</c> adds.
/// </summary>
public static class ToolLoopPipeline
{
    /// <summary>
    /// Wraps <paramref name="providerClient"/> so the loop built above it is bounded and
    /// observable. The returned client already contains a <c>FunctionInvokingChatClient</c>, which
    /// is what makes <c>ChatClientAgent</c> skip adding its own (it adds one only when
    /// <c>GetService&lt;FunctionInvokingChatClient&gt;()</c> finds none) — that is also how
    /// <see cref="ToolLoopOptions"/> reaches the loop, since <c>AsAIAgent</c> exposes no setting
    /// for its iteration limits. A client that already contains one is returned untouched: it is
    /// someone else's loop, and a second one stacked on it would loop twice.
    /// </summary>
    public static IChatClient Wrap(
        IChatClient providerClient,
        ToolLoopBudgetOptions budget,
        ToolLoopOptions loop,
        ToolLoopRunStats stats)
    {
        if (providerClient.GetService<FunctionInvokingChatClient>() is not null)
            return providerClient;

        // With compaction off the pipeline is still built, so the iteration limits and the run
        // statistics apply; the compactor then simply forwards every list as it is.
        IChatClient compacting = new ToolLoopCompactingChatClient(providerClient, budget, loop.MaxIterations, stats);
        return new FunctionInvokingChatClient(compacting)
        {
            MaximumIterationsPerRequest = loop.MaxIterations,
            MaximumConsecutiveErrorsPerRequest = loop.MaxConsecutiveErrors,
        };
    }
}
