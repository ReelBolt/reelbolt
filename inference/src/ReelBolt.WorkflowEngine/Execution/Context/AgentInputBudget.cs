namespace ReelBolt.WorkflowEngine.Execution.Context;

/// <summary>
/// Result of <see cref="AgentInputBudget.Apply"/>. <see cref="OriginalChars"/> is the length of
/// today's plain concatenation (before any budgeting); <see cref="FinalChars"/> is
/// <see cref="Text"/>'s actual length, which can still exceed the configured cap when even the
/// protected steps alone don't fit (see rule 6 in <see cref="AgentInputBudget"/>'s doc comment).
/// </summary>
public sealed record BudgetedConcatenation(
    string Text,
    int OriginalChars,
    int FinalChars,
    int DigestedStepCount,
    int DroppedStepCount);

/// <summary>
/// Deterministic, non-LLM budget for the text an agent step's prompt concatenates from prior
/// steps' outputs. Pure and static: given the same <paramref name="parts"/> and
/// <paramref name="options"/> it always produces the same result, with no model call, no I/O, no
/// randomness.
///
/// <para>
/// <b>Safety contract (read this before changing anything here) — this is prompt-only budgeting.</b>
/// It must NEVER change <c>StepExecutionContext.StepOutputHistory</c> itself, NEVER change what
/// <c>WorkflowExecutorService</c> persists as <c>WorkflowStepResult.OutputJson</c>, and NEVER
/// affect any deterministic by-<c>StepOrder</c> consumer (e.g. <c>VideoCompileStepExecutor</c>
/// resolving a decision/plan/artifact by StepOrder straight off the persisted step result). This
/// mirrors <see cref="Execution.StepExecutionContext.SetPromptOutputOverride"/>'s existing
/// contract — an override/budget may only ever change what a PROMPT sees, never the underlying
/// step-result data. <see cref="Execution.StepExecutionContext"/> is the only caller; it applies
/// this to the string it is ABOUT TO hand an agent, never to anything it stores.
/// </para>
///
/// <para>Additional hard rules the caller (<see cref="Execution.StepExecutionContext"/>) upholds
/// by never invoking this type for certain modes/parts, rather than this type trying to detect
/// them itself:</para>
/// <list type="bullet">
/// <item><c>AgentInputContextMode.PreviousStepOnly</c>'s single-step output is NEVER passed
/// through here — it is frequently a machine-consumed contract (a <c>{view, meta}</c> envelope, a
/// structured decision) that the next agent must see byte-for-byte intact.</item>
/// <item><c>AgentInputContextMode.CustomMappedSubset</c>'s mapped output is NEVER passed through
/// here either — the mapping already deliberately narrowed it.</item>
/// <item>Within the parts this type IS given (<c>FullWorkflow</c>/<c>SelectedPriorSteps</c>), the
/// most recent <see cref="AgentInputBudgetOptions.RecentStepsVerbatim"/> entries (by descending
/// <c>StepOrder</c>) are protected from BOTH digesting and dropping — see rule 4/5 below.</item>
/// </list>
///
/// <para><b>Algorithm</b> (only engaged once the plain concatenation actually exceeds
/// <see cref="AgentInputBudgetOptions.MaxInputChars"/>):</para>
/// <list type="number">
/// <item>Disabled, or nothing to concatenate: return today's exact format, unchanged.</item>
/// <item>Build today's format (see <c>BuildFormatted</c>) — this doubles as the fast-path result
/// AND the <see cref="BudgetedConcatenation.OriginalChars"/> measurement.</item>
/// <item>Fits already (or the cap is disabled via a value &lt;= 0): return it unchanged — this is
/// the single most important behavior here, since it means an already-small workflow is
/// byte-identical to pre-budget behavior.</item>
/// <item>Over budget: protect the last <c>RecentStepsVerbatim</c> parts (by <c>StepOrder</c>,
/// stable-sorted ascending so "last N" means "highest N StepOrders"); digest every other part
/// through <see cref="JsonOutputDigest"/> with <c>MaxOutputChars = MaxDigestedStepChars</c>;
/// re-concatenate and measure.</item>
/// <item>Still over budget: drop non-protected parts, replacing each dropped part's body with a
/// one-line marker (the <c>## Step N: Label</c> header stays, so the agent still knows a step
/// existed), re-measuring after each drop and stopping as soon as it fits. With no
/// <paramref name="relevance"/> ordering the drop order is OLDEST-first (ascending StepOrder),
/// exactly as before that optional signal existed; with one, the model-judged least-relevant parts
/// go first and everything the ordering does not list follows in the same oldest-first
/// order.</item>
/// <item>Even the protected parts alone don't fit: return the result as-is — never corrupt a
/// machine-consumed contract just to hit a character count — with the stats still reflecting what
/// was actually digested/dropped.</item>
/// </list>
/// <para>
/// <b>The relevance ordering can only reorder the drop phase.</b> It never adds or removes a
/// droppable part, never changes what gets digested, and cannot protect anything from being
/// dropped — an ordering that lists nothing usable is indistinguishable from no ordering at all,
/// which is exactly what makes it safe to compute from a model. See
/// <see cref="ContextRelevanceOrdering"/>.
/// </para>
/// </summary>
public static class AgentInputBudget
{
    private const string StepSeparator = "\n\n---\n\n";
    private const string EmptyFallback = "[\"Begin analysis of the project.\"]";

    /// <param name="relevance">
    /// Optional relevance ranking of the droppable parts (see <see cref="ContextRelevanceOrdering"/>).
    /// <c>null</c> — the default, and the only value any deployment that has not opted into the
    /// relevance pass ever passes — reproduces the pre-existing oldest-first drop order
    /// byte-for-byte.
    /// </param>
    public static BudgetedConcatenation Apply(
        IReadOnlyList<(int StepOrder, string StepLabel, string Output)> parts,
        AgentInputBudgetOptions options,
        ContextRelevanceOrdering? relevance = null)
    {
        string legacy = BuildFormatted(parts);
        int originalChars = legacy.Length;

        if (!options.Enabled || parts.Count == 0)
            return new BudgetedConcatenation(legacy, originalChars, originalChars, DigestedStepCount: 0, DroppedStepCount: 0);

        if (options.MaxInputChars <= 0 || originalChars <= options.MaxInputChars)
            return new BudgetedConcatenation(legacy, originalChars, originalChars, DigestedStepCount: 0, DroppedStepCount: 0);

        // Ascending StepOrder throughout: "protected" is a suffix of this list (highest
        // StepOrders), "droppable" is the prefix before it.
        (List<(int StepOrder, string StepLabel, string Output)> ordered, int protectedFromIndex) =
            OrderAndSplit(parts, options);

        JsonDigestSettings digestSettings = new(
            MaxOutputChars: options.MaxDigestedStepChars,
            MaxStringChars: options.DigestMaxStringChars,
            MaxArrayItems: options.DigestMaxArrayItems,
            MaxDepth: options.DigestMaxDepth);

        List<(int StepOrder, string StepLabel, string Output)> working = new(ordered.Count);
        int digestedCount = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            (int stepOrder, string stepLabel, string output) = ordered[i];
            if (i >= protectedFromIndex)
            {
                working.Add((stepOrder, stepLabel, output));
                continue;
            }

            string digested = JsonOutputDigest.Digest(output, digestSettings);
            if (digested != output)
                digestedCount++;

            working.Add((stepOrder, stepLabel, digested));
        }

        string afterDigest = BuildFormatted(working);
        if (afterDigest.Length <= options.MaxInputChars)
            return new BudgetedConcatenation(afterDigest, originalChars, afterDigest.Length, digestedCount, DroppedStepCount: 0);

        int droppedCount = 0;
        foreach (int i in BuildDropOrder(ordered, protectedFromIndex, relevance))
        {
            (int stepOrder, string stepLabel, string originalOutput) = ordered[i];
            string marker =
                $"_[Step {stepOrder} output omitted by context budget ({originalOutput.Length} chars) " +
                "— re-read it with the project/file tools if you need it.]_";
            working[i] = (stepOrder, stepLabel, marker);
            droppedCount++;

            string candidate = BuildFormatted(working);
            if (candidate.Length <= options.MaxInputChars)
                return new BudgetedConcatenation(candidate, originalChars, candidate.Length, digestedCount, droppedCount);
        }

        // Rule 6: every droppable part is already gone and it still doesn't fit — the remaining
        // (protected) parts are a machine-consumed contract we must not further mutilate. Return
        // as-is; the stats still tell the caller exactly what happened.
        string finalText = BuildFormatted(working);
        return new BudgetedConcatenation(finalText, originalChars, finalText.Length, digestedCount, droppedCount);
    }

    /// <summary>
    /// Length of today's plain concatenation for <paramref name="parts"/> — the same string this
    /// type builds internally as its <c>OriginalChars</c> measurement.
    ///
    /// <para>
    /// Exists for callers that must decide something BEFORE calling <see cref="Apply"/>: the one
    /// current caller is <see cref="Execution.StepExecutionContext"/>'s optional relevance pass,
    /// which asks a Decision model for a drop order only when the plain concatenation is actually
    /// over budget — an under-budget input never drops anything, so a decision there would cost a
    /// request and change nothing. Deliberately NOT a prompt builder: the prompt still comes from
    /// <see cref="Apply"/> alone, so the concatenation format keeps exactly one definition.
    /// </para>
    /// </summary>
    public static int MeasureConcatenatedChars(
        IReadOnlyList<(int StepOrder, string StepLabel, string Output)> parts) =>
        BuildFormatted(parts).Length;

    /// <summary>
    /// The DROPPABLE subset of <paramref name="parts"/> — everything outside the protected most
    /// recent <see cref="AgentInputBudgetOptions.RecentStepsVerbatim"/> entries — in the same
    /// ascending-<c>StepOrder</c> order the drop phase works through.
    ///
    /// <para>
    /// Public so the relevance pass can be told exactly which parts the budget could actually give
    /// up, instead of re-deriving that rule and drifting from it. It is the single definition of
    /// "droppable": <see cref="Apply"/> itself computes the protected/droppable split through the
    /// same private helper.
    /// </para>
    /// </summary>
    public static IReadOnlyList<(int StepOrder, string StepLabel, string Output)> SelectDroppableParts(
        IReadOnlyList<(int StepOrder, string StepLabel, string Output)> parts,
        AgentInputBudgetOptions options)
    {
        (List<(int StepOrder, string StepLabel, string Output)> ordered, int protectedFromIndex) =
            OrderAndSplit(parts, options);

        return ordered.Take(protectedFromIndex).ToList();
    }

    /// <summary>
    /// The one place the protected/droppable split is defined: parts ordered ascending by
    /// <c>StepOrder</c> (so "protected" is a suffix of the list and "droppable, oldest first" is
    /// the prefix before it), plus the index at which the protected suffix begins.
    /// </summary>
    private static (List<(int StepOrder, string StepLabel, string Output)> Ordered, int ProtectedFromIndex)
        OrderAndSplit(
            IReadOnlyList<(int StepOrder, string StepLabel, string Output)> parts,
            AgentInputBudgetOptions options)
    {
        List<(int StepOrder, string StepLabel, string Output)> ordered = parts.OrderBy(p => p.StepOrder).ToList();
        int recentCount = Math.Clamp(options.RecentStepsVerbatim, 0, ordered.Count);
        return (ordered, ordered.Count - recentCount);
    }

    /// <summary>
    /// The indices of <paramref name="ordered"/> to drop, in the order they should be given up.
    /// Without a relevance ordering this is simply 0..protectedFromIndex-1 (oldest-first, the
    /// pre-existing behaviour, byte-for-byte). With one, the step orders the model ranked are
    /// emitted first — least relevant first, so the least relevant part is dropped first — and
    /// every other droppable part follows in the same oldest-first order, which guarantees the
    /// list is always a permutation of the same droppable indices. That is what keeps rule 6
    /// (drop everything droppable, then return the protected parts as-is) reachable no matter what
    /// the model answered.
    /// </summary>
    private static List<int> BuildDropOrder(
        List<(int StepOrder, string StepLabel, string Output)> ordered,
        int protectedFromIndex,
        ContextRelevanceOrdering? relevance)
    {
        List<int> dropOrder = new(protectedFromIndex);

        if (relevance is not null && relevance.StepOrdersLeastRelevantFirst.Count > 0)
        {
            Dictionary<int, int> indexByStepOrder = new(protectedFromIndex);
            for (int i = 0; i < protectedFromIndex; i++)
                indexByStepOrder[ordered[i].StepOrder] = i;

            HashSet<int> emitted = new(protectedFromIndex);
            foreach (int stepOrder in relevance.StepOrdersLeastRelevantFirst)
            {
                // A step order the ordering names but which is not droppable here (protected now,
                // or absent) is ignored rather than trusted: the budget decides what it may drop,
                // never the model.
                if (indexByStepOrder.TryGetValue(stepOrder, out int index) && emitted.Add(index))
                    dropOrder.Add(index);
            }

            for (int i = 0; i < protectedFromIndex; i++)
            {
                if (emitted.Add(i))
                    dropOrder.Add(i);
            }

            return dropOrder;
        }

        for (int i = 0; i < protectedFromIndex; i++)
            dropOrder.Add(i);

        return dropOrder;
    }

    /// <summary>
    /// The one definition of "today's concatenation format" — copied character-for-character
    /// from the pre-budget <c>StepExecutionContext.BuildConcatenated</c>: a single part returns
    /// its output verbatim (no header), more than one part is joined with
    /// <c>"## Step {order}: {label}"</c> headers separated by <c>"\n\n---\n\n"</c>, and zero parts
    /// falls back to a fixed prompt-safe placeholder.
    /// </summary>
    private static string BuildFormatted(IReadOnlyList<(int StepOrder, string StepLabel, string Output)> parts)
    {
        if (parts.Count == 0)
            return EmptyFallback;
        if (parts.Count == 1)
            return parts[0].Output;

        return string.Join(StepSeparator, parts.Select(p => $"## Step {p.StepOrder}: {p.StepLabel}\n{p.Output}"));
    }
}
