namespace ReelBolt.WorkflowEngine.Execution.Guardrails;

/// <summary>
/// Screens a proposed tool call, or a piece of untrusted input, against the deterministic hard-rule
/// denylist and — when enabled and a Decision provider exists — a probabilistic decision model.
/// </summary>
/// <remarks>
/// <para>
/// <b>Guardrails fail OPEN.</b> No provider configured, provider unreachable, provider times out,
/// malformed answer, any exception at all: the verdict is <see cref="GuardrailVerdict.Allow"/>. This
/// is the inverse polarity of every other decision site in this initiative, which degrade to "no
/// annotation" — a guardrail that turns a provider outage into a workflow failure is a worse bug
/// than the thing it guards.
/// </para>
/// <para>
/// <b>Step-cache key: no new component, and none is needed.</b> Guardrails are evaluated per tool
/// call at run time and never change a step's <em>persisted output</em>: a rejected call returns the
/// tool's existing self-correcting error shape, and the agent's own next turn is what changes the
/// output. <c>ScreenToolCallAsync</c> is not consulted by <c>StepCacheKeyBuilder</c>, and the key
/// already hashes the fully resolved step input — including the agent's system prompt, provider and
/// every step config blob — so there is nothing about screening to fold in. Adding a field to the
/// cache key here would be a key that describes nothing.
/// </para>
/// <para>
/// <b>Evaluation order is load-bearing</b> (plan §D5): hard rules first and unconditionally, then
/// the tool-screening mode gate, and only then a provider resolution. A hard-rule rejection must
/// never cost a provider call, and must never be softened by a confident model answer.
/// </para>
/// </remarks>
public interface IGuardrailScreener
{
    /// <summary>
    /// Screens a proposed tool call. A rejection (<c>Allowed = false</c>) is the caller's signal to
    /// return the tool's self-correcting failure shape instead of running the tool; an allow
    /// (<c>Allowed = true</c>) means "proceed exactly as before".
    /// </summary>
    Task<GuardrailVerdict> ScreenToolCallAsync(GuardrailToolCall call, CancellationToken ct);

    /// <summary>
    /// Screens one input span (an on-screen-text caption or an ASR transcript span) for
    /// instruction-like content. A flagged span is <em>quoted and flagged</em> by the caller, never
    /// dropped — on-screen text is routinely legitimate.
    /// </summary>
    Task<GuardrailVerdict> ScreenSpanAsync(GuardrailSpan span, CancellationToken ct);
}
