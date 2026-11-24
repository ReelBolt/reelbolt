namespace ReelBolt.WorkflowEngine.Execution.Context;

/// <summary>
/// Asks a Decision model to rank the droppable prior-step parts of an over-budget agent prompt by
/// relevance to the CURRENT step, so the budget can give up the least relevant one first instead
/// of the oldest one.
///
/// <para>
/// <b>Contract:</b> returns <c>null</c> — meaning "no ordering available, use today's behaviour" —
/// for every one of: the feature disabled, no Decision provider resolved, fewer than two droppable
/// candidates, an empty/unusable answer, a cancellation, and ANY exception whatsoever (resolver,
/// transport, malformed response). Implementations must never throw and must never mutate
/// anything. Relevance is an observation-only refinement of a prompt: it can never fail a step,
/// change a persisted step output, or alter any by-<c>StepOrder</c> consumer.
/// </para>
/// </summary>
public interface IContextRelevanceSelector
{
    /// <summary>
    /// One Decision call over a bounded, descriptor-only candidate list, returning the step orders
    /// to drop first (see <see cref="ContextRelevanceOrdering"/>), or <c>null</c> when no usable
    /// ordering could be produced.
    /// </summary>
    /// <param name="projectId">
    /// The project whose files enrich the composed Decision state. Their listing is best-effort:
    /// a failure there degrades to "no file context", never to a failed selection.
    /// </param>
    /// <param name="stepCandidates">
    /// The droppable, non-protected prior-step parts, oldest-first — computed by
    /// <see cref="AgentInputBudget.SelectDroppableParts"/> so this side can never disagree with the
    /// budget about which parts are actually droppable.
    /// </param>
    Task<ContextRelevanceOrdering?> SelectDropOrderAsync(
        Guid projectId,
        IReadOnlyList<ContextRelevanceCandidate> stepCandidates,
        CancellationToken ct);
}
