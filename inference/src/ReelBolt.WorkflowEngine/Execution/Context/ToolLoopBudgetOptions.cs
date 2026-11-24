using Microsoft.Extensions.Configuration;

namespace ReelBolt.WorkflowEngine.Execution.Context;

/// <summary>
/// Configuration for the deterministic, non-LLM compaction of an agent's OWN tool-calling loop —
/// see <see cref="ToolLoopCompactingChatClient"/> for the mechanism and the safety contract.
/// Bound at <see cref="SectionName"/>.
///
/// <para>
/// This is the sibling of <see cref="AgentInputBudgetOptions"/>, and the two bound different
/// things. That one bounds the STEP INPUT composed from prior steps' outputs, once, before the
/// agent starts. This one bounds what the agent accumulates AFTER it starts: every tool result
/// stays in the message list and is re-sent on every following model call, so one raw
/// <c>remotion render</c> result of ~130 KB is paid for again on each of the next ~40 calls.
/// All sizes are in characters, used as a token proxy, the same convention the input budget uses.
/// </para>
/// </summary>
public sealed class ToolLoopBudgetOptions
{
    public const string SectionName = "WorkflowEngine:ToolLoopBudget";

    /// <summary>
    /// Master on/off switch. When false every message list is forwarded exactly as the loop built
    /// it, and a run behaves as it did before this feature existed (the iteration limits and the
    /// per-run statistics still apply).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Soft ceiling, in characters, on everything sent to the model in ONE call: the instructions,
    /// every message, every tool call and every tool result. Input at or under it is forwarded
    /// byte-identical. Over it, tool results (oldest first) and then older tool-call arguments are
    /// digested until the total fits or nothing more may be touched. It is a ceiling the
    /// compactor works toward, not a hard guarantee: the instructions, the original prompt and the
    /// single most recent tool result are never touched, so a conversation made only of those can
    /// still exceed it.
    /// </summary>
    public int MaxContextChars { get; set; } = 200_000;

    /// <summary>
    /// How many of the most recent tool results stay verbatim while older results are digested.
    /// Floored at 1 in use: the latest result is the one the model is reacting to, so it is never
    /// digested. The window only shrinks (oldest of the window first) when digesting everything
    /// older was not enough to fit <see cref="MaxContextChars"/>.
    /// </summary>
    public int RecentToolResultsVerbatim { get; set; } = 4;

    /// <summary>
    /// The size, in characters, a digested tool result or tool-call argument is cut down to (plus
    /// a short marker line). A value already at or under this size is never touched.
    /// </summary>
    public int DigestMaxChars { get; set; } = 2_000;

    /// <summary>
    /// Whether, as a last resort, string arguments of OLDER tool calls (a <c>WriteSandboxFile</c>
    /// carrying a whole TSX file is the usual offender) are digested too. The call itself, its id
    /// and its name always stay, so the call/result pairing the wire format requires is intact.
    /// </summary>
    public bool CompactCallArguments { get; set; } = true;

    /// <summary>
    /// Hard ceiling, in characters, on one model call to a provider that binds signed reasoning to
    /// the conversation (Anthropic). There the prefix ahead of the latest signed thinking block is
    /// frozen and cannot be compacted further (see <see cref="ToolLoopCompactor"/>), so with one
    /// tool call per turn the context grows by every new result. When what would be sent — the
    /// frozen prefix plus the pending results, after every compaction still allowed — is over this
    /// ceiling, the call is not made and the step fails with <c>CONTEXT_TOO_LARGE</c>
    /// (<see cref="ContextTooLargeException"/>, refunded). 0 or less switches the check off.
    /// Decided by the program lead (QA-P0-10); documented in docs/workflow-execution-optimization.md §5.
    /// </summary>
    public int HardMaxChars { get; set; } = 900_000;

    public static ToolLoopBudgetOptions From(IConfiguration configuration)
    {
        ToolLoopBudgetOptions options = new();
        configuration.GetSection(SectionName).Bind(options);
        return options;
    }
}

/// <summary>
/// Limits on the tool-calling loop itself, applied to the <c>FunctionInvokingChatClient</c> that
/// <see cref="ToolLoopPipeline"/> builds. Bound at <see cref="SectionName"/>. The defaults are the
/// Microsoft.Extensions.AI 10.10.0 defaults, so leaving the section out changes nothing.
/// </summary>
public sealed class ToolLoopOptions
{
    public const string SectionName = "WorkflowEngine:ToolLoop";

    /// <summary>
    /// <c>FunctionInvokingChatClient.MaximumIterationsPerRequest</c>: the number of model
    /// round-trips that may end in tool calls. When it is reached the client makes ONE more call
    /// with the function declarations stripped, so the model is forced to answer in text; that
    /// answer is a forced stop, not a finished job, and the run reports it.
    /// </summary>
    public int MaxIterations { get; set; } = 40;

    /// <summary>
    /// <c>FunctionInvokingChatClient.MaximumConsecutiveErrorsPerRequest</c>: consecutive
    /// iterations whose tool invocation threw before the exception is rethrown to the caller.
    /// </summary>
    public int MaxConsecutiveErrors { get; set; } = 3;

    public static ToolLoopOptions From(IConfiguration configuration)
    {
        ToolLoopOptions options = new();
        configuration.GetSection(SectionName).Bind(options);
        // FunctionInvokingChatClient rejects < 1 and < 0 respectively with an exception from its
        // setter; a typo in configuration must not turn into a failure on every agent run.
        options.MaxIterations = Math.Max(1, options.MaxIterations);
        options.MaxConsecutiveErrors = Math.Max(0, options.MaxConsecutiveErrors);
        return options;
    }
}
