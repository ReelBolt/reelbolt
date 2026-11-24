namespace ReelBolt.WorkflowEngine.Execution.Decision.CostRouting;

/// <summary>
/// Per-agent opt-in configuration for cost routing on <c>Agent</c> steps, bound from
/// <c>Agents:{agentName}:CostRouting:</c> — see <c>docs/decision-models.md</c> § cost routing.
///
/// <para>
/// <b>Default off, and off means "byte-identical to before this feature existed".</b> Routing is
/// only ever attempted when <see cref="Enabled"/> is <c>true</c> AND <see cref="ExpensiveProviderId"/>
/// names a provider: with either missing there is nothing to escalate TO, so the gate returns a
/// disabled scope and no provider call of any kind is made.
/// </para>
/// </summary>
/// <param name="Enabled">Master switch. Already itself defaulted off at the config layer, so an
/// absent <c>CostRouting</c> section is inert.</param>
/// <param name="CheapProviderId">
/// The tier a CONFIDENT decision runs on. Optional: when null, "keep the cheap one" means exactly
/// what the run would have done anyway — the ordinary resolver precedence (per-agent override →
/// the Chat default → the legacy Azure keys), i.e. no routing action at all. When set, the run is
/// pinned to that provider instead, which is what makes a genuinely two-tier comparison possible
/// (an operator can point the cheap tier at a small local model without touching the per-agent
/// override every other path still uses).
/// </param>
/// <param name="ExpensiveProviderId">
/// The tier a low-confidence decision escalates to. Required for routing to do anything.
/// </param>
/// <param name="AcceptAt">
/// Minimum P(the cheap tier will pass validation and review) for the cheap tier to be trusted.
/// Mirrors <c>DecisionGateConfig.AcceptAt</c>'s default so the two decision sites cannot drift.
/// </param>
/// <param name="MinMargin">
/// Minimum probability margin between the two Noul outcomes. A Noul question is binary, so the
/// margin is <c>|2p - 1|</c> — the exact quantity <c>DecisionGate</c> computes as
/// <c>top - second</c> for a Noul answer — and a decision that is merely a coin flip escalates even
/// when it nominally clears <paramref name="AcceptAt"/>.
/// </param>
/// <param name="DecisionProviderId">
/// The Decision provider to ask. Null means the single default <c>Capability == Decision</c> row,
/// exactly like <c>IDecisionGate</c>. When nothing resolves, routing is a no-op — "no Decision
/// provider configured" stays a first-class, tested state rather than an error.
/// </param>
public sealed record CostRoutingConfig(
    bool Enabled = false,
    Guid? CheapProviderId = null,
    Guid? ExpensiveProviderId = null,
    double AcceptAt = 0.85,
    double MinMargin = 0.25,
    Guid? DecisionProviderId = null);
