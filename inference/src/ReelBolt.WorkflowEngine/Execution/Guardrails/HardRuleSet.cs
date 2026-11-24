using System.Diagnostics.CodeAnalysis;

namespace ReelBolt.WorkflowEngine.Execution.Guardrails;

/// <summary>
/// The deterministic half of guardrail screening: a denylist over a tool call's name and arguments.
/// Pure — no I/O, no provider, no clock — so it is unconditionally safe to evaluate before anything
/// else, and its result can never be softened by a confident probabilistic answer (plan §D5).
/// </summary>
/// <remarks>
/// Intentional design notes:
/// <list type="bullet">
/// <item>An empty rule set matches nothing, which is the shipped state. Guardrails are inert until
/// an operator configures a rule — there is no "helpful" built-in pattern that could fire on a
/// project nobody anticipated.</item>
/// <item>A rule whose <see cref="GuardrailHardRule.Pattern"/> is empty or whitespace is dropped at
/// construction rather than matching everything: a denylist that rejects every tool call because of
/// one blank config value is a self-inflicted outage.</item>
/// <item>Matching is an ordinal, case-insensitive substring test over the tool name and the raw
/// arguments blob — never a regex, and never a parse of the arguments. A hard rule has to hold on
/// malformed input too, so it must not depend on the JSON being well-formed.</item>
/// <item>The task brief is deliberately <em>not</em> matched: it is free prose describing the goal
/// (and routinely quotes the very filenames the rule denylists), so matching it would reject
/// precisely the calls the agent was asked to make.</item>
/// </list>
/// </remarks>
public sealed class HardRuleSet
{
    /// <summary>The empty rule set: matches nothing, has no cost, and is the default.</summary>
    public static HardRuleSet Empty { get; } = new([]);

    private readonly IReadOnlyList<GuardrailHardRule> _rules;

    public HardRuleSet(IEnumerable<GuardrailHardRule>? rules)
    {
        _rules = rules is null
            ? []
            : rules.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Pattern)).ToArray();
    }

    /// <summary>True when no usable rule is configured, i.e. this set can never reject anything.</summary>
    public bool IsEmpty => _rules.Count == 0;

    /// <summary>
    /// The usable rules, in configuration order — the same order <see cref="TryMatch"/> evaluates
    /// them in. Exposed so the screener can report a rule that names a tool the screening wrapper
    /// never sees: such a rule is inert, and an operator who believes a tool is denied has to be
    /// told rather than left to find out.
    /// </summary>
    public IReadOnlyList<GuardrailHardRule> Rules => _rules;

    /// <summary>
    /// Evaluates every rule in configuration order. Deterministic: the first matching rule wins, so
    /// the reason a call is rejected does not depend on dictionary ordering.
    /// </summary>
    /// <param name="call">The tool call to test.</param>
    /// <param name="reason">The rejection reason, non-null exactly when this returns true.</param>
    public bool TryMatch(GuardrailToolCall call, [NotNullWhen(true)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(call);

        foreach (GuardrailHardRule rule in _rules)
        {
            if (!Matches(rule, call))
                continue;

            reason = string.IsNullOrWhiteSpace(rule.Name)
                ? $"Blocked by a guardrail hard rule (denylisted text '{rule.Pattern}')."
                : $"Blocked by guardrail hard rule '{rule.Name}'.";
            return true;
        }

        reason = null;
        return false;
    }

    private static bool Matches(GuardrailHardRule rule, GuardrailToolCall call)
    {
        if (!string.IsNullOrWhiteSpace(rule.ToolName)
            && !string.Equals(rule.ToolName, call.ToolName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Contains(call.ToolName, rule.Pattern) || Contains(call.ArgumentsJson, rule.Pattern);
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
