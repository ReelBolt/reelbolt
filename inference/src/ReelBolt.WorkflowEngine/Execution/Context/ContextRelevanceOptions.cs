namespace ReelBolt.WorkflowEngine.Execution.Context;

/// <summary>
/// Opt-in, DEFAULT-OFF configuration for the context-relevance signal that
/// <see cref="IContextRelevanceSelector"/> adds to <see cref="AgentInputBudget"/>. Bound at
/// <see cref="SectionName"/> — a NESTED section of the budget's own
/// <c>WorkflowEngine:ContextBudget</c> — so the relevance pass reads as what it is: an optional
/// refinement of the budget, never a feature that stands on its own.
///
/// <para>
/// <b>Default off is the contract, not a convenience.</b> With <see cref="Enabled"/> false, with no
/// <see cref="ProviderId"/> resolving to a usable Decision provider, with the plain concatenation
/// already under budget, or on ANY error, no request is made and
/// <see cref="AgentInputBudget.Apply"/> takes the exact same code path (and produces the exact
/// same string) it produced before this feature existed. Turning this on can only change WHICH
/// droppable prior-step part is dropped first when the input is already over budget — never
/// whether something is dropped at all, and never anything outside the prompt.
/// </para>
/// </summary>
public sealed class ContextRelevanceOptions
{
    public const string SectionName = "WorkflowEngine:ContextBudget:Relevance";

    /// <summary>
    /// Master on/off switch. Default <c>false</c>: nothing about the budget changes, no Decision
    /// request is made, and the drop phase stays oldest-first.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The decision provider to ask, resolved through
    /// <c>IInferenceProviderResolver.ResolveDecisionAsync</c>. Null means "the single enabled
    /// <c>Capability == Decision</c> default row", which is also the only way an operator who has
    /// configured exactly one Decision provider needs to think about this.
    /// </summary>
    public Guid? ProviderId { get; set; }

    /// <summary>
    /// Minimum answer CONFIDENCE for a relevance judgement to be trusted enough to reorder the
    /// drop phase. A coin-flip answer (confidence at or below this) is treated as "no signal" and
    /// that candidate keeps today's oldest-first treatment — deliberately placed AFTER every
    /// trusted judgement, so an unsure model can never cause a confident one's ranking to be
    /// overridden. Renormalized binary-Choice confidences are always &gt;= 0.5, so the default is a
    /// real filter rather than a formality.
    /// </summary>
    public double AcceptAt { get; set; } = 0.6;

    /// <summary>
    /// Upper bound on how many candidates are described AND asked about in the single Decision
    /// call. Clamped in code as well (see <c>ContextRelevanceSelector.HardMaxCandidates</c>) so a
    /// mistyped configuration can never produce an unbounded question list — the logprob decision
    /// client issues one completion per question, so this is directly a cost ceiling.
    /// </summary>
    public int MaxCandidates { get; set; } = 12;

    /// <summary>
    /// Hard character cap on the composed Decision state. Enforced by construction (candidates
    /// whose descriptor would not fit are left out of BOTH the state and the question set, so the
    /// model is never asked about something it cannot see) and clamped in code.
    /// </summary>
    public int MaxStateChars { get; set; } = 4_000;
}
