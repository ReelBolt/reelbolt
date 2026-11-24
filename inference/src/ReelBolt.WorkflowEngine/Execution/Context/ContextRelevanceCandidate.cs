namespace ReelBolt.WorkflowEngine.Execution.Context;

/// <summary>
/// What kind of context item a <see cref="ContextRelevanceCandidate"/> describes. Only
/// <see cref="Step"/> candidates can actually be dropped — a project file is never part of the
/// concatenated prior-step input this budget shrinks — so <see cref="ProjectFile"/> candidates
/// exist purely to give the Decision model a realistic picture of what the next agent will have
/// available when it ranks the step outputs.
/// </summary>
public enum ContextRelevanceCandidateKind
{
    Step,
    ProjectFile,
}

/// <summary>
/// One thing the relevance pass may ask the Decision model about. Deliberately DESCRIPTOR-ONLY: a
/// label, a short already-computed meta string, and a truncated preview of the underlying text —
/// never the full output — because the composed Decision state is capped hard (see
/// <see cref="ContextRelevanceOptions.MaxStateChars"/>) while a single step output can be
/// megabytes. <see cref="Key"/> is the stable, opaque identity used as the decision question's
/// name and echoed back in <see cref="Shared.Inference.DecisionAnswer.Name"/>.
/// </summary>
public sealed record ContextRelevanceCandidate(
    string Key,
    ContextRelevanceCandidateKind Kind,
    int StepOrder,
    string Label,
    string Meta,
    string Descriptor);

/// <summary>
/// The relevance ranking produced by <see cref="IContextRelevanceSelector"/>, consumed by
/// <see cref="AgentInputBudget.Apply"/> as its optional third argument.
///
/// <para>
/// <see cref="StepOrdersLeastRelevantFirst"/> lists ONLY the step orders the model judged with
/// enough confidence to act on (<see cref="ContextRelevanceOptions.AcceptAt"/>), least relevant
/// first — i.e. exactly the order the budget should give them up in. Any droppable part absent
/// from this list (never asked about, not answered, or answered too tentatively to trust) is
/// dropped AFTER every listed one, in today's oldest-first order. That asymmetry is the whole
/// safety story: an unusable or partial decision can only ever move a part LATER in the drop
/// order than it would have been, never protect it from being dropped, so rule 6 ("even the
/// protected parts alone don't fit — return as-is") still holds unchanged.
/// </para>
/// </summary>
public sealed record ContextRelevanceOrdering(IReadOnlyList<int> StepOrdersLeastRelevantFirst);
