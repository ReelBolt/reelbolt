using Microsoft.Extensions.AI;

namespace ReelBolt.Shared.Inference;

/// <summary>
/// The house reasoning-effort vocabulary for workflow agents: where it is validated, how it parses
/// onto <see cref="ReasoningEffort"/>, and the one precedence rule that picks a value for a run.
/// </summary>
/// <remarks>
/// <para>
/// The set is <c>none</c>, <c>low</c>, <c>medium</c>, <c>xhigh</c> and is deliberately NOT the generic
/// <see cref="ReasoningEffort"/> enum's (which adds <c>High</c>). It is the exact vocabulary the
/// deployed Qwen3.8 chat template accepts when thinking is enabled — its own
/// <c>resolved_reasoning_effort not in (...)</c> guard turns anything else into a raised Jinja
/// exception, i.e. a 500 on every request the agent makes — plus <c>none</c>, which vLLM
/// special-cases to skip the &lt;think&gt; block entirely. <c>high</c> is therefore NOT accepted:
/// every provider path has to tolerate every member of this set, and a self-hosted Qwen endpoint does
/// not tolerate <c>high</c>. DeepSeek and Anthropic both tolerate the whole set (see
/// <see cref="ReasoningEffortMapper"/>), so the restriction costs them nothing.
/// </para>
/// <para>
/// <b>Wire values.</b> <c>Microsoft.Extensions.AI.OpenAI</c> maps <c>ReasoningEffort.None/Low/Medium/ExtraHigh</c>
/// onto exactly <c>"none"</c>, <c>"low"</c>, <c>"medium"</c>, <c>"xhigh"</c> (read from the pinned 10.10.x
/// assembly, and applied only when the raw <c>ChatCompletionOptions</c> carries no level of its own).
/// So an agent states its effort provider-neutrally as <see cref="ChatOptions.Reasoning"/> and the
/// OpenAI-shaped paths are byte-identical to the old per-agent <c>RawRepresentationFactory</c>.
/// </para>
/// </remarks>
public static class AgentReasoningEffort
{
    /// <summary>The accepted values, lower-case, in ascending order of effort.</summary>
    public static readonly IReadOnlyList<string> Vocabulary = ["none", "low", "medium", "xhigh"];

    /// <summary>Whether <paramref name="value"/> is one of <see cref="Vocabulary"/> (case-insensitive).</summary>
    public static bool IsValid(string? value) => Normalize(value) is not null;

    /// <summary>The canonical lower-case spelling of <paramref name="value"/>, or null when it is not in the vocabulary.</summary>
    public static string? Normalize(string? value)
    {
        string? trimmed = value?.Trim().ToLowerInvariant();
        return trimmed is not null && Vocabulary.Contains(trimmed) ? trimmed : null;
    }

    /// <summary>The provider-neutral enum for a vocabulary value, or null when it is not in the vocabulary.</summary>
    public static ReasoningEffort? ToGeneric(string? value) => Normalize(value) switch
    {
        "none" => ReasoningEffort.None,
        "low" => ReasoningEffort.Low,
        "medium" => ReasoningEffort.Medium,
        "xhigh" => ReasoningEffort.ExtraHigh,
        _ => null
    };

    /// <summary>
    /// The effort a run uses: the agent definition's stored value, else the configured value
    /// (<c>Agents:&lt;name&gt;:ReasoningEffort</c>) or the agent class's own default. A stored value
    /// outside the vocabulary is skipped rather than trusted — the API validates on write, so this is
    /// only reachable through a hand-edited row, and falling through is safer than a 500 per request.
    /// </summary>
    public static string? Resolve(string? definitionValue, string? configuredOrDefaultValue) =>
        Normalize(definitionValue) ?? Normalize(configuredOrDefaultValue);
}
