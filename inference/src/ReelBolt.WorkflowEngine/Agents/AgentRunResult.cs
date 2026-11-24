namespace ReelBolt.WorkflowEngine.Agents;

/// <summary>
/// Result from running an agent, including output and token usage metadata.
/// </summary>
public class AgentRunResult
{
    public required string Output { get; init; }
    public int TokensUsed { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }

    /// <summary>The provider row that served this run; null for the legacy config fallback or when unresolved.</summary>
    public Guid? PrimaryProviderId { get; init; }

    public string? PrimaryModelName { get; init; }
    public IReadOnlyList<AgentToolCallTrace> ToolCalls { get; init; } = [];
    public IReadOnlyList<string> Reasoning { get; init; } = [];
    public bool Success { get; init; } = true;
    public string? FailureReason { get; init; }

    /// <summary>Model calls the run's tool loop made (null when the run did not go through the loop).</summary>
    public int? ModelCalls { get; init; }

    /// <summary>The largest context, in characters, the loop assembled before compaction.</summary>
    public int? PeakContextChars { get; init; }

    /// <summary>The most characters compaction removed from any single model call.</summary>
    public int? CharsCompacted { get; init; }

    /// <summary>
    /// True when the run ended because the tool loop ran out of iterations and the answer was forced
    /// with tools disabled, rather than because the model stopped asking for tools.
    /// </summary>
    public bool ToolLoopCapReached { get; init; }
}

public class AgentToolCallTrace
{
    public required string ToolName { get; init; }
    public string? Arguments { get; init; }
    public string? Result { get; init; }
}
