namespace ReelBolt.WorkflowEngine.Execution.Guardrails;

/// <summary>
/// How a <see cref="GuardrailVerdict"/> was reached. The tag is load-bearing: callers (and the log)
/// must be able to tell a deterministic rejection apart from a model's opinion, because only the
/// latter is subject to provider availability.
/// </summary>
public enum GuardrailKind
{
    /// <summary>Nothing was screened — no hard rule matched and no provider was consulted.</summary>
    None,

    /// <summary>A <see cref="HardRuleSet"/> entry matched. Deterministic and unconditional.</summary>
    HardRule,

    /// <summary>A decision model was consulted and returned an answer.</summary>
    Probabilistic,
}

/// <summary>
/// A guardrail verdict. <see cref="Reason"/> is non-null for every rejection and for an
/// allowed-but-screened call; it is null when nothing was screened.
/// </summary>
public sealed record GuardrailVerdict(bool Allowed, string? Reason, GuardrailKind Kind)
{
    /// <summary>
    /// Not screened: no hard rule matched and no decision provider was resolved. This is the
    /// install's normal path — there are zero <c>Decision</c> provider rows.
    /// </summary>
    public static GuardrailVerdict Allow { get; } = new(true, null, GuardrailKind.None);

    /// <summary>
    /// Screened and allowed, so a caller can distinguish "not screened" from "screened, passed".
    /// </summary>
    /// <remarks>
    /// The ticket specified this helper as an overload <c>Allow(string? reason)</c>; C# rejects a
    /// property and a method sharing a name (CS0102), so it is spelled <c>AllowScreened</c>.
    /// </remarks>
    public static GuardrailVerdict AllowScreened(string? reason) =>
        new(true, reason, GuardrailKind.Probabilistic);

    /// <summary>Rejected. Prefer the <see cref="HardRuleSet"/> / screener paths that set the tag.</summary>
    public static GuardrailVerdict Reject(string reason, GuardrailKind kind) => new(false, reason, kind);
}

/// <summary>
/// A single tool call awaiting screening. <paramref name="ArgumentsJson"/> is the tool's raw
/// arguments blob; the screener summarises it and bounds it with
/// <see cref="GuardrailConfig.StateMaxChars"/> — it is never forwarded verbatim.
/// </summary>
public sealed record GuardrailToolCall(string ToolName, string ArgumentsJson, string? TaskBrief);

/// <summary>
/// A single input span (an on-screen-text caption or an ASR transcript span) awaiting screening.
/// Screening only ever <em>flags</em> a span — dropping it is not a thing this API can express,
/// because on-screen text is routinely legitimate content (see plan §B3).
/// </summary>
public sealed record GuardrailSpan(string SpanId, string Text, string Site);
