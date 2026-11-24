using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ReelBolt.WorkflowEngine.Execution.Context;

/// <summary>
/// What one agent run did inside its tool-calling loop, written by
/// <see cref="ToolLoopCompactingChatClient"/> as the loop calls it and read by
/// <c>ReelBoltAgentBase.RunAsync</c> when the run ends. One instance per run — never shared
/// between runs, because agents are singletons and several runs of one agent can be in flight.
/// Thread-safe, though the loop makes its model calls one at a time.
/// </summary>
public sealed class ToolLoopRunStats
{
    private readonly object _gate = new();

    private int _modelCalls;
    private long _inputTokens;
    private long _outputTokens;
    private int _peakContextChars;
    private int _peakForwardedChars;
    private int _maxCharsCompacted;
    private int _compactedCalls;
    private bool _iterationCapReached;

    /// <summary>Model calls made by the loop, the first request included.</summary>
    public int ModelCalls { get { lock (_gate) return _modelCalls; } }

    /// <summary>Input tokens summed over every model call, as the provider reported them.</summary>
    public long InputTokens { get { lock (_gate) return _inputTokens; } }

    public long OutputTokens { get { lock (_gate) return _outputTokens; } }

    /// <summary>The largest context, in characters, the loop assembled before compaction.</summary>
    public int PeakContextChars { get { lock (_gate) return _peakContextChars; } }

    /// <summary>The largest context actually sent, in characters, after compaction.</summary>
    public int PeakForwardedChars { get { lock (_gate) return _peakForwardedChars; } }

    /// <summary>
    /// The most characters compaction removed from any single call. Not a sum over calls: the same
    /// old result is digested again on every later call, so a sum would count it dozens of times.
    /// </summary>
    public int MaxCharsCompacted { get { lock (_gate) return _maxCharsCompacted; } }

    /// <summary>How many calls had any compaction applied.</summary>
    public int CompactedCalls { get { lock (_gate) return _compactedCalls; } }

    /// <summary>
    /// True when the loop stopped because it ran out of iterations rather than because the model
    /// stopped asking for tools. <c>FunctionInvokingChatClient</c> makes one extra call, with the
    /// function declarations stripped, once the cap is reached, so that call being made is the
    /// signal: it is call number <c>MaxIterations + 1</c>.
    /// </summary>
    public bool IterationCapReached { get { lock (_gate) return _iterationCapReached; } }

    internal void RecordCall(int rawChars, int forwardedChars, int maxIterations)
    {
        lock (_gate)
        {
            _modelCalls++;
            if (_modelCalls > maxIterations)
                _iterationCapReached = true;

            _peakContextChars = Math.Max(_peakContextChars, rawChars);
            _peakForwardedChars = Math.Max(_peakForwardedChars, forwardedChars);

            int saved = rawChars - forwardedChars;
            if (saved > 0)
            {
                _compactedCalls++;
                _maxCharsCompacted = Math.Max(_maxCharsCompacted, saved);
            }
        }
    }

    internal void RecordUsage(UsageDetails? usage)
    {
        if (usage is null)
            return;

        lock (_gate)
        {
            _inputTokens += usage.InputTokenCount ?? 0;
            _outputTokens += usage.OutputTokenCount ?? 0;
        }
    }
}

/// <summary>
/// The logging seam for the tool-loop summary line. Agents are built by DI with fixed constructor
/// shapes and carry no logger, so the line is written through one process-wide logger that
/// startup sets via <see cref="Configure"/>; until then (and in unit tests) it is a no-op.
/// </summary>
public static class ToolLoopDiagnostics
{
    private static ILogger _logger = NullLogger.Instance;

    public static void Configure(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger("ReelBolt.WorkflowEngine.ToolLoop");

    /// <summary>
    /// One line per agent run: how many model calls, how many input tokens they cost together,
    /// how large the context got and how much of it compaction removed. A run that hit the
    /// iteration cap additionally gets a warning, because "ran out of turns" and "finished" are
    /// otherwise indistinguishable in the log.
    /// </summary>
    public static void LogRun(string agentName, string outcome, ToolLoopRunStats stats, int maxIterations)
    {
        if (stats.ModelCalls == 0)
            return;

        _logger.LogInformation(
            "Agent {AgentName} tool loop {Outcome}: ModelCalls={ModelCalls}, InputTokens={InputTokens}, " +
            "OutputTokens={OutputTokens}, PeakContextChars={PeakContextChars}, " +
            "PeakForwardedChars={PeakForwardedChars}, CharsCompacted={CharsCompacted}, CompactedCalls={CompactedCalls}",
            agentName,
            outcome,
            stats.ModelCalls,
            stats.InputTokens,
            stats.OutputTokens,
            stats.PeakContextChars,
            stats.PeakForwardedChars,
            stats.MaxCharsCompacted,
            stats.CompactedCalls);

        if (stats.IterationCapReached)
        {
            _logger.LogWarning(
                "Agent {AgentName} hit the tool-loop iteration cap ({MaxIterations}) after {ModelCalls} model calls; " +
                "its answer was forced with tools disabled and may be incomplete",
                agentName,
                maxIterations,
                stats.ModelCalls);
        }
    }
}
