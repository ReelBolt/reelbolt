namespace ReelBolt.Inference.Api.Agents;

/// <summary>
/// Result from running an agent, including output and token usage metadata.
/// </summary>
public class AgentRunResult
{
    public required string Output { get; init; }
    public int TokensUsed { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }

    /// <summary>
    /// Why the provider stopped generating, in its own vocabulary
    /// (<c>Microsoft.Extensions.AI</c>'s <c>ChatFinishReason.Value</c>: <c>stop</c>,
    /// <c>length</c>, <c>tool_calls</c>, <c>content_filter</c>). Null when the provider reported
    /// none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the difference between "the model finished" and "the model ran out of room", and it
    /// is the only way a caller can tell a complete answer from half of one. It was previously
    /// discarded here — <c>AgentResponse.FinishReason</c> carries it for every provider
    /// (<c>ChatClientAgent</c> propagates the LAST chat response's reason, including through a
    /// tool-calling loop), so dropping it made a truncated reply indistinguishable from a finished
    /// one all the way out to the client.
    /// </para>
    /// <para>
    /// Raw rather than a parsed enum on purpose: a reason this code has never seen must survive to
    /// the caller as what the provider actually said, not as a mapped guess.
    /// </para>
    /// </remarks>
    public string? FinishReason { get; init; }
}
