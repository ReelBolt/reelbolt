namespace ReelBolt.WorkflowEngine.Execution.Guardrails;

/// <summary>
/// Whether the probabilistic half of guardrail screening runs at all. <see cref="Off"/> is the
/// default and the shipped value: with it, <see cref="IGuardrailScreener.ScreenToolCallAsync"/>
/// never resolves a Decision provider, so a tool call behaves exactly as it did before guardrails
/// existed. Mirrors every other decision-facing site in this initiative (plan §D3) — the mechanism
/// ships, the enforcement stays opt-in.
/// </summary>
public enum GuardrailMode
{
    Off,
    On,
}

/// <summary>
/// One deterministic denylist entry, evaluated by <see cref="HardRuleSet"/> over a tool call's name
/// and arguments before any provider is resolved.
/// </summary>
/// <remarks>
/// The rules are configuration, not code, because the useful denylist is install-specific (which
/// paths a project must never let an agent touch, which commands must never be run). Per plan §D5
/// an empty or absent configuration therefore means <em>no hard rules</em> — which is what the
/// shipped <c>appsettings.json</c> carries, so guardrails are inert out of the box.
/// <para>
/// Matching is an ordinal, case-insensitive substring test (never a regular expression): a regex is
/// a second language with its own failure modes, and a rule that silently fails to compile is
/// exactly the kind of guardrail that is worse than no guardrail. <see cref="Pattern"/> empty or
/// whitespace makes the rule inert — see <see cref="HardRuleSet"/>.
/// </para>
/// </remarks>
public sealed record GuardrailHardRule
{
    /// <summary>Human-readable rule name, echoed into the rejection reason and the log.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The denylisted substring. Empty/whitespace makes the rule inert.</summary>
    public string Pattern { get; init; } = string.Empty;

    /// <summary>
    /// Restricts the rule to one tool name (ordinal-ignore-case). Null/empty applies the rule to
    /// every tool call.
    /// </summary>
    public string? ToolName { get; init; }
}

/// <summary>
/// Options bound from configuration section <see cref="SectionName"/> (<c>WorkflowEngine:Guardrails</c>).
/// Deliberately small: two knobs and the optional hard-rule denylist.
/// </summary>
public sealed class GuardrailConfig
{
    public const string SectionName = "WorkflowEngine:Guardrails";

    /// <summary>
    /// Default bound on the screening state handed to the decision model. Bounded on purpose: the
    /// state must be an arguments <em>summary</em> (tool name, task brief, per-argument excerpts),
    /// never a full file body, and Jev's decision window is small.
    /// </summary>
    public const int DefaultStateMaxChars = 4000;

    /// <summary>
    /// Gates the probabilistic screen only. Hard rules are unconditional by design (plan §D5) and
    /// are evaluated first regardless of this value — but the shipped configuration carries no hard
    /// rules, so <see cref="GuardrailMode.Off"/> is indistinguishable from no screener at all.
    /// </summary>
    public GuardrailMode ToolScreening { get; set; } = GuardrailMode.Off;

    /// <summary>
    /// Upper bound, in characters, on the screening state. Values below a small floor are raised to
    /// that floor by <see cref="GuardrailScreener"/> so a misconfigured <c>0</c> cannot silently
    /// turn every screen into a question about nothing.
    /// </summary>
    public int StateMaxChars { get; set; } = DefaultStateMaxChars;

    /// <summary>Deterministic denylist entries. Empty (the default) ⇒ no hard rules.</summary>
    public List<GuardrailHardRule> HardRules { get; set; } = [];
}
