using System.Globalization;
using System.Text;
using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Execution.Context;

namespace ReelBolt.WorkflowEngine.Execution;

/// <summary>
/// <paramref name="OutputStorageKey"/>/<paramref name="ArtifactStorageKey"/> mirror the same
/// fields on <see cref="WorkflowStepResult"/> for the step that produced this history entry —
/// carried here so <c>VideoSourceKind.StepOutput</c>/<c>PreviousStepOutput</c> (a prior render or
/// VideoCompile step) and cross-step artifact resolution (a prior VideoAnalyze step, referenced
/// by <c>VideoCompileStepConfig.AnalysisStepOrder</c>) can resolve within the same execution
/// without a DB round-trip. Both default to null so every pre-existing 3-arg call site
/// (Extract/Agent/etc. history entries, which never set either) keeps compiling unchanged.
/// </summary>
public record StepOutputHistoryEntry(
    int StepOrder,
    string StepLabel,
    string Output,
    string? OutputStorageKey = null,
    string? ArtifactStorageKey = null);

/// <summary>
/// Context passed to each step executor containing all needed state.
/// </summary>
public class StepExecutionContext
{
    private readonly List<string> _retryFeedback = new();
    private string? _reviewFeedback;
    private readonly Dictionary<int, string> _promptOutputOverrides = new();

    public required WorkflowExecution Execution { get; init; }
    public required WorkflowStep Step { get; init; }
    public required List<WorkflowStep> AllSteps { get; init; }
    public required string AccumulatedOutput { get; init; }
    public required int CurrentStepIndex { get; init; }
    public required int IterationCount { get; init; }
    public required string CorrelationId { get; init; }
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Ordered history of completed step outputs so context modes can resolve inputs
    /// without querying persisted step results.
    /// </summary>
    public required IReadOnlyList<StepOutputHistoryEntry> StepOutputHistory { get; init; }

    /// <summary>
    /// Numbers from earlier passes of the review loop this context is running, rendered by
    /// <c>ReviewLoopStepExecutor.RenderReviewPassHistory</c> (T-007). Null on the loop's first pass —
    /// the loop-back prunes the earlier outputs from <see cref="StepOutputHistory"/>, so this is the
    /// only memory the reviewer has of them — and null means nothing is appended to the prompt.
    /// </summary>
    public string? ReviewPassHistory { get; init; }

    /// <summary>
    /// Last concrete agent input built during this context lifetime.
    /// </summary>
    public string? LastResolvedAgentInput { get; private set; }

    /// <summary>
    /// Deterministic prompt-context budget applied to <c>FullWorkflow</c>/<c>SelectedPriorSteps</c>
    /// concatenation — see <see cref="AgentInputBudget"/>'s doc comment for the full safety
    /// contract (prompt-only; never touches <see cref="StepOutputHistory"/>, the persisted
    /// <c>WorkflowStepResult.OutputJson</c>, or any deterministic by-<c>StepOrder</c> consumer).
    /// Null (the default for every existing 3/4-arg construction site, including every test that
    /// builds this type directly) means budgeting is off — <see cref="BuildFullWorkflowInput"/>
    /// and <see cref="BuildSelectedPriorStepsInput"/> then produce EXACTLY what they did before
    /// this feature existed, byte-for-byte.
    /// </summary>
    public AgentInputBudgetOptions? BudgetOptions { get; init; }

    /// <summary>
    /// A short, human-readable line describing the last time <see cref="ApplyBudget"/> actually
    /// changed something (e.g. <c>"context budget: 412000→158900 chars (3 steps digested, 1
    /// dropped)"</c>), for <c>WorkflowExecutorService</c> to log. Reset to null at the start of
    /// every <see cref="ApplyBudget"/> call and only set when digesting or dropping actually
    /// happened — a budgeted call that already fit leaves this null, same as an unbudgeted one.
    /// </summary>
    public string? LastBudgetSummary { get; private set; }

    /// <summary>
    /// Free-text user request provided at execution time.
    /// Null when the workflow was executed without user input.
    /// </summary>
    public string? UserRequest { get; init; }

    /// <summary>
    /// The <see cref="WorkflowStepResult"/> id for this attempt — set by
    /// <see cref="WorkflowExecutorService"/> once the row is created (after this context is first
    /// constructed), so it is <see cref="Guid.Empty"/> for the brief window before that. Mutable
    /// (not <c>init</c>) for that reason.
    /// </summary>
    public Guid StepResultId { get; set; }

    /// <summary>
    /// The millicredits held for this step result, set by <see cref="WorkflowExecutorService"/> right after the hold
    /// is taken; null when nothing was held (billing off, a BYO-only or zero-estimate step, a cache hit). A step
    /// that may make a bounded number of OPTIONAL extra calls (a review loop's targeted looks) reads it to skip a
    /// call the hold cannot cover, rather than run past it.
    /// </summary>
    public long? CreditHoldMillis { get; set; }

    /// <summary>
    /// Wired by <see cref="WorkflowExecutorService"/> to publish an ephemeral
    /// <c>WorkflowStepProgress</c> event via <see cref="IWorkflowEventPublisher"/>. Null in any
    /// context built without that wiring (e.g. a unit test constructing this directly) — callers
    /// should always go through <see cref="ReportProgressAsync"/>, which no-ops when this is null,
    /// rather than invoking the delegate directly. The three trailing <c>int?</c> parameters carry
    /// an optional running cumulative token tally (total/input/output) — see
    /// <see cref="ReportProgressAsync"/>'s doc comment.
    /// </summary>
    public Func<string, int?, int?, int?, int?, CancellationToken, Task>? ProgressReporter { get; set; }

    /// <summary>
    /// Reports a lightweight, best-effort progress checkpoint for a long-running step (e.g.
    /// <c>VideoAnalyze</c>/<c>VideoCompile</c>/<c>EditRoom</c>) — a short stage label, a 0-100
    /// percent when one is naturally available (e.g. real ffmpeg encode progress), and an optional
    /// running cumulative token tally when the caller has genuine mid-run visibility into token
    /// usage (e.g. <c>EditRoomStepExecutor</c> summing each completed turn's usage). Never
    /// persisted, never throws (progress reporting must never be able to fail the step it is
    /// reporting on); a no-op when <see cref="ProgressReporter"/> was never wired. Token parameters
    /// default to null — a step executor with no such visibility (the overwhelming majority) must
    /// leave them unset rather than guess/estimate a value.
    /// </summary>
    public async Task ReportProgressAsync(
        string stage,
        int? percentComplete = null,
        int? tokensUsedSoFar = null,
        int? inputTokensSoFar = null,
        int? outputTokensSoFar = null)
    {
        if (ProgressReporter is null)
            return;

        try
        {
            await ProgressReporter(stage, percentComplete, tokensUsedSoFar, inputTokensSoFar, outputTokensSoFar, CancellationToken);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            // Execution is winding down anyway — swallow, same as any other best-effort signal.
        }
        catch
        {
            // Progress reporting is pure UI signal (see WorkflowStepProgress's doc comment) — a
            // publish failure (e.g. a transient RabbitMQ hiccup) must never fail the step itself.
        }
    }

    /// <summary>
    /// Wired by <see cref="WorkflowExecutorService"/> to publish an append-only
    /// <c>WorkflowStepChatTurn</c> event via <see cref="IWorkflowEventPublisher"/>, alongside (not
    /// instead of) <see cref="ProgressReporter"/> — see <see cref="ReportChatTurnAsync"/>. Null in
    /// any context built without that wiring; callers should always go through
    /// <see cref="ReportChatTurnAsync"/>, which no-ops when this is null.
    /// </summary>
    public Func<int, int?, string, string, string, IReadOnlyList<string>, CancellationToken, Task>? ChatTurnReporter { get; set; }

    /// <summary>
    /// Reports one completed turn of a <c>StepType.EditRoom</c> group-chat run (a seat's or the
    /// director's) as an append-only, sequence-numbered event — see
    /// <see cref="Shared.IntegrationEvents.WorkflowStepChatTurn"/>'s doc comment for why this is a
    /// separate mechanism from the ephemeral/supersedable <see cref="ReportProgressAsync"/>. Never
    /// throws (best-effort, like progress reporting); a no-op when <see cref="ChatTurnReporter"/>
    /// was never wired.
    /// </summary>
    public async Task ReportChatTurnAsync(
        int turnIndex, int? totalTurns, string speaker, string speakerRole, string text, IReadOnlyList<string> idsMentioned)
    {
        if (ChatTurnReporter is null)
            return;

        try
        {
            await ChatTurnReporter(turnIndex, totalTurns, speaker, speakerRole, text, idsMentioned, CancellationToken);
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            // Execution is winding down anyway — swallow, same as any other best-effort signal.
        }
        catch
        {
            // Chat-turn reporting is transcript/UI signal, not execution history — a publish
            // failure must never fail the step itself.
        }
    }

    /// <summary>
    /// Stores retry feedback (for example schema validation errors) so the next
    /// agent attempt can self-correct based on the previous failure.
    /// </summary>
    public IReadOnlyList<string> RetryFeedback => _retryFeedback.AsReadOnly();

    /// <summary>
    /// Prompt-ready retry guidance text: the review-loop revision brief (when one was recorded with
    /// <see cref="RecordReviewFeedback"/>), then the schema-retry block (when one was recorded with
    /// <see cref="RecordRetryFeedback"/>). Empty when neither has been recorded. Read directly by the
    /// steps that hand guidance to a runner of their own (Revise, GraphicDesign, ForEach).
    /// </summary>
    public string RetryGuidance => string.Join("\n\n", GuidanceSections().Select(section => section.Body));

    /// <summary>
    /// Appends the execution's <see cref="UserRequest"/> to a prompt, in the one wording every
    /// step type uses, or returns <paramref name="prompt"/> unchanged when there is no request.
    /// <para>
    /// Public because a room step (<c>RoomStepExecutorBase</c>) does NOT go through
    /// <see cref="BuildAgentInput"/> — it composes its own opening message, synthesis prompt and
    /// solo-fallback prompt from the bounded view — and for a while composed all three without the
    /// user request at all. An <c>EditRoom</c> step is documented as a drop-in replacement for a
    /// solo <c>Agent(VideoStoryEditor)</c> step, so a workflow's only channel for creative
    /// direction silently stopped applying the moment you swapped one for the other. Sharing this
    /// method rather than repeating the literal keeps the two paths saying the same thing.
    /// </para>
    /// </summary>
    public string WithUserRequest(string prompt) =>
        string.IsNullOrWhiteSpace(UserRequest)
            ? prompt
            : $"{prompt}\n\n---\nUser Request:\n{UserRequest}";

    /// <summary>
    /// Appends what a previous attempt was told was wrong — a <c>ReviewLoop</c>'s extracted
    /// score/issues on a loop-back, or a schema-retry reason — in the one wording every step type
    /// uses, or returns <paramref name="prompt"/> unchanged when there is nothing to say.
    /// <para>
    /// Public for exactly the reason <see cref="WithUserRequest"/> is, and it is the same bug
    /// twice: a room step does not go through <see cref="BuildAgentInput"/>, so the feedback the
    /// executor had already captured and handed to this context reached a solo
    /// <c>Agent(VideoStoryEditor)</c> step behind a <c>ReviewLoop</c> and silently stopped
    /// applying the moment you swapped that step for an <c>EditRoom</c>. The room then re-ran
    /// completely blind to why its previous cut had been rejected, which is why looping produced
    /// near-identical scores instead of converging — observed on the alpha launch film as 2/10
    /// twice and then 5/10 twice, where the only thing that ever actually changed between
    /// attempts was a human editing the brief.
    /// </para>
    /// <para>
    /// Review-loop feedback and schema-retry feedback are two different messages and are rendered
    /// as two sections. A review loop's critique goes under its own heading
    /// (<see cref="ReviewFeedbackHeading"/>), with no JSON-schema sentence after it: that sentence
    /// told an Author whose film had been judged weak to fix its JSON, and in the promo runs the
    /// loop-back render came out byte-identical to the reviewed one. The "Retry Guidance" block and
    /// its schema sentence stay for real schema retries.
    /// </para>
    /// </summary>
    public string WithRetryGuidance(string prompt)
    {
        if (_reviewFeedback is null && _retryFeedback.Count == 0)
            return prompt;

        StringBuilder composed = new(prompt);
        foreach ((string? title, string body) in GuidanceSections())
        {
            composed.Append("\n\n---\n");
            if (title is not null)
                composed.Append(title).Append('\n');
            composed.Append(body);
        }

        return composed.ToString();
    }

    /// <summary>
    /// The heading a review loop's feedback is rendered under; <c>{0}</c> is the reviewer's score
    /// and <c>{1}</c> the step's pass mark (<c>MinScore</c>).
    /// </summary>
    internal const string ReviewFeedbackHeading =
        "Reviewer feedback on your previous render (score {0}/10, needs {1}): revise the composition to address every point";

    /// <summary>Cap on recorded review-loop feedback (a full multi-criterion verdict).</summary>
    internal const int MaxReviewFeedbackChars = 6000;

    /// <summary>Cap on each recorded schema-retry / failure reason.</summary>
    internal const int MaxRetryFeedbackChars = 2000;

    /// <summary>Same bound as <c>ReviewLoopStepExecutor.MaxPreviousManifestNoteChars</c>.</summary>
    private const int MaxPreviousManifestNoteChars = 2000;

    private const string SchemaRetrySentence = "Return output that strictly matches the expected JSON schema and is valid JSON.";

    /// <summary>The guidance sections in prompt order, each with its optional title line.</summary>
    private IEnumerable<(string? Title, string Body)> GuidanceSections()
    {
        if (_reviewFeedback is not null)
            yield return (null, _reviewFeedback);

        if (_retryFeedback.Count > 0)
        {
            yield return ("Retry Guidance:", string.Join("\n", _retryFeedback.Select(message => $"- {message}"))
                + "\n" + SchemaRetrySentence);
        }
    }

    /// <summary>
    /// Builds the final string input for an agent step, applying the effective
    /// step-level mode and appending the optional user request.
    /// <para>
    /// Synchronous and relevance-free on purpose: this is the path every caller that is NOT the
    /// <c>Agent</c> step executor uses (<c>ReviewLoopStepExecutor</c>, <c>ParallelStepExecutor</c>
    /// and <c>WorkflowExecutorService</c>'s own pre-persist resolution), and it must stay
    /// byte-identical to what it produced before the relevance pass existed. Only
    /// <see cref="BuildAgentInputAsync"/> may consult the optional relevance selector.
    /// </para>
    /// </summary>
    public string BuildAgentInput() => BuildAgentInputCore(relevance: null);

    /// <summary>
    /// The asynchronous twin of <see cref="BuildAgentInput"/>, used by the <c>Agent</c> step
    /// executor alone, which is the only path that may spend a Decision call on ordering the drop
    /// phase of an over-budget prompt.
    ///
    /// <para>
    /// Produces a string identical to <see cref="BuildAgentInput"/> in every case except one: the
    /// budget is enabled AND over its cap AND a Decision provider actually answered AND the
    /// answers were confident enough to trust — i.e. only when the prompt was already going to
    /// lose content. Everything else (feature disabled, no provider, under budget, any error,
    /// cancellation, or either structurally exempt context mode) short-circuits to
    /// <see cref="BuildAgentInput"/>'s exact behaviour.
    /// </para>
    /// </summary>
    public async Task<string> BuildAgentInputAsync(CancellationToken ct)
    {
        ContextRelevanceOrdering? relevance = await ResolveContextRelevanceOrderingAsync(ct);
        return BuildAgentInputCore(relevance);
    }

    private string BuildAgentInputCore(ContextRelevanceOrdering? relevance)
    {
        AgentInputContextMode effectiveMode = AgentInputContextResolver.ResolveEffectiveMode(Step);
        string baseInput = effectiveMode switch
        {
            AgentInputContextMode.FullWorkflow => ApplyBudget(FullWorkflowHistory(), relevance),
            AgentInputContextMode.PreviousStepOnly => BuildPreviousStepInput(),
            AgentInputContextMode.SelectedPriorSteps => ApplyBudget(SelectedPriorStepsHistory(), relevance),
            AgentInputContextMode.CustomMappedSubset => BuildCustomMappedSubsetInput(),
            _ => ApplyBudget(FullWorkflowHistory(), relevance)
        };

        string composedInput = WithUserRequest(baseInput);

        composedInput = WithRetryGuidance(composedInput);

        LastResolvedAgentInput = composedInput;
        return composedInput;
    }

    /// <summary>
    /// Asks the wired <see cref="IContextRelevanceSelector"/> — when there is one and the feature
    /// is actually needed — for the order the budget's droppable parts should be given up in.
    /// Returns null for every "not worth it / cannot do it" case, which is the overwhelming
    /// majority of steps:
    /// <list type="bullet">
    /// <item>no budget options, no selector, or the feature disabled;</item>
    /// <item><see cref="AgentInputContextMode.PreviousStepOnly"/> or
    /// <see cref="AgentInputContextMode.CustomMappedSubset"/> — structurally exempt, exactly like
    /// the budget itself, so no call is made for them at all;</item>
    /// <item>fewer than two droppable parts — a single droppable part drops first under either
    /// rule, so a decision could not change anything;</item>
    /// <item>the plain concatenation already fits — nothing will be dropped, so a call would cost
    /// a request and change nothing;</item>
    /// <item>any error or cancellation whatsoever — relevance can never fail or alter a step.</item>
    /// </list>
    /// </summary>
    private async Task<ContextRelevanceOrdering?> ResolveContextRelevanceOrderingAsync(CancellationToken ct)
    {
        AgentInputBudgetOptions? options = BudgetOptions;
        IContextRelevanceSelector? selector = options?.RelevanceSelector;
        if (options is null || selector is null || !options.Enabled || options.MaxInputChars <= 0)
            return null;

        AgentInputContextMode effectiveMode = AgentInputContextResolver.ResolveEffectiveMode(Step);
        if (effectiveMode is not (AgentInputContextMode.FullWorkflow or AgentInputContextMode.SelectedPriorSteps))
            return null;

        List<StepOutputHistoryEntry> history = effectiveMode == AgentInputContextMode.FullWorkflow
            ? FullWorkflowHistory()
            : SelectedPriorStepsHistory();

        List<(int StepOrder, string StepLabel, string Output)> parts =
            history.Select(h => (h.StepOrder, h.StepLabel, h.Output)).ToList();

        if (parts.Count == 0 || AgentInputBudget.MeasureConcatenatedChars(parts) <= options.MaxInputChars)
            return null;

        IReadOnlyList<(int StepOrder, string StepLabel, string Output)> droppable =
            AgentInputBudget.SelectDroppableParts(parts, options);

        if (droppable.Count < 2)
            return null;

        List<ContextRelevanceCandidate> candidates = droppable
            .Select(p => new ContextRelevanceCandidate(
                Key: $"step:{p.StepOrder}",
                Kind: ContextRelevanceCandidateKind.Step,
                StepOrder: p.StepOrder,
                Label: p.StepLabel,
                Meta: $"{p.Output.Length} chars",
                Descriptor: p.Output))
            .ToList();

        try
        {
            return await selector.SelectDropOrderAsync(Execution.ProjectId, candidates, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The execution is winding down; the agent call this prompt feeds carries the same
            // token and unwinds on its own. Cancellation is not a relevance failure.
            return null;
        }
        catch
        {
            // Defense in depth on top of the selector's own never-throws contract (the same
            // discipline AgentStepExecutor applies around the Decision shadow gate): a bug in an
            // implementation must not be able to touch this step's prompt.
            return null;
        }
    }

    /// <summary>
    /// Records what a deterministic (non-agent) step, such as an Extract step, actually
    /// consumed as its input — a compact descriptor, not the raw upstream blob — so
    /// <c>ResolveInputJsonForPersistence</c> persists something meaningful instead of falling
    /// back to the full accumulated output.
    /// </summary>
    public void RecordResolvedInput(string? value) => LastResolvedAgentInput = value;

    /// <summary>
    /// Substitutes an enriched rendering of an already-completed step's output for PROMPT-BUILDING
    /// purposes only — <see cref="StepOutputHistory"/> itself, the persisted
    /// <c>WorkflowStepResult.OutputJson</c> of the step being overridden, and every deterministic
    /// consumer that resolves a step's output by <c>StepOrder</c> (e.g.
    /// <c>VideoCompileStepExecutor</c>'s decision/plan/artifact resolution) all keep seeing the
    /// original, unmodified output.
    ///
    /// <para>
    /// Added for exactly one caller: <see cref="StepExecutors.MotionGraphicsPlacementAnnotator"/>,
    /// which marks each <c>view.placements</c> candidate with whether it survives the story
    /// editor's cut — information that only exists once BOTH the analyze step and the story-editor
    /// step have completed, and therefore cannot be baked into the analyze step's own output. An
    /// override must only ever ADD derived, server-computed context to a step's output; it is not
    /// a mechanism for rewriting what a step actually produced.
    /// </para>
    /// </summary>
    public void SetPromptOutputOverride(int stepOrder, string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return;

        _promptOutputOverrides[stepOrder] = output;
    }

    /// <summary>
    /// The prompt-facing output for <paramref name="entry"/> — its override if one was installed
    /// by <see cref="SetPromptOutputOverride"/>, otherwise its own output verbatim.
    /// </summary>
    private StepOutputHistoryEntry ForPrompt(StepOutputHistoryEntry entry) =>
        _promptOutputOverrides.TryGetValue(entry.StepOrder, out string? overridden)
            ? entry with { Output = overridden }
            : entry;

    /// <summary>
    /// Public accessor for the prompt-facing output of one history entry — the override installed
    /// by <see cref="SetPromptOutputOverride"/> when one exists, otherwise the entry's own output
    /// verbatim. Added for <c>RoomStepExecutorBase</c>'s bounded-view resolution, so a room step
    /// (the graphics room's inEdit annotation) sees the same enriched rendering an Agent step's
    /// prompt would — for a step with no override installed this is exactly
    /// <see cref="StepOutputHistoryEntry.Output"/>. Deterministic by-StepOrder consumers
    /// (e.g. <c>VideoCompileStepExecutor</c>) must keep reading the raw output instead — see
    /// <see cref="SetPromptOutputOverride"/>'s contract.
    /// </summary>
    public string OutputForPrompt(StepOutputHistoryEntry entry) => ForPrompt(entry).Output;

    /// <summary>
    /// B6b: how the most recent failed attempt of THIS step was classified
    /// (<see cref="Services.Billing.FailureClassifier"/>), recorded where the failure is produced so
    /// the executor's outer catch neither re-derives it from a message nor loses it — the retry
    /// wrapper turns every attempt into a bare <c>InvalidOperationException</c>, which is exactly
    /// the boundary the code and the class used to be discarded at
    /// (docs/design/saas-metering.md "Where a failure surfaces today", shape 4).
    /// <para>
    /// <see cref="FailureClass.None"/> means "no attempt classified a failure" — a successful step,
    /// or an attempt whose failure is only decidable by the executor (a cancellation, whose class
    /// comes from the cancellation registry's recorded provenance).
    /// </para>
    /// </summary>
    public FailureClass LastFailureClass { get; private set; } = FailureClass.None;

    /// <summary>The code that came with <see cref="LastFailureClass"/>, when one did. See that member.</summary>
    public string? LastFailureCode { get; private set; }

    /// <summary>
    /// The last failed attempt's <see cref="StepExecutionResult.UserMessage"/> (null when it had none;
    /// the last attempt wins). Only the run-level message reads it, and only for a user failure class.
    /// </summary>
    public string? LastFailureUserMessage { get; private set; }

    /// <summary>
    /// Records the classification of one failed attempt. Later calls win: the class the executor
    /// persists and charges by is the LAST attempt's, matching the retry wrapper's own
    /// "failed after N attempts. Last error: …" message.
    /// </summary>
    public void RecordFailure(FailureClass failureClass, string? code, string? userMessage = null)
    {
        LastFailureClass = failureClass;
        LastFailureUserMessage = userMessage;
        if (!string.IsNullOrWhiteSpace(code))
            LastFailureCode = code;
    }

    /// <summary>
    /// Failed full sandbox renders recorded by <c>ReactRemotionSandboxTools</c> during this step.
    /// ONE ledger per step: this context is reused by every attempt of
    /// <c>WorkflowExecutorService.ExecuteStepWithRetryAsync</c>, so the count (and the per-step cap
    /// that reads it) spans attempts rather than restarting with each new execution-context scope.
    /// </summary>
    public RenderFailureLedger RenderFailures { get; } = new();

    /// <summary>
    /// The motion-graphics legibility side channel of this step (docs/design/graphics-see-and-size.md):
    /// the output canvas and floor the step designs for, and what the render tool measured for each
    /// rendered graphic. Configured only by <c>MotionGraphicsPlacementAnnotator</c>; inert otherwise.
    /// </summary>
    public OverlayLegibilityLedger OverlayLegibility { get; } = new();

    /// <summary>
    /// Records a review loop's verdict on the previous iteration for this (loop-back) step:
    /// <paramref name="feedback"/> is <c>ReviewLoopStepExecutor.ExtractFeedbackSummary</c>'s text,
    /// capped at <see cref="MaxReviewFeedbackChars"/>; <paramref name="previousManifestNote"/> is the
    /// optional scene list of the render that was reviewed, capped at 2000 chars. Rendered by
    /// <see cref="WithRetryGuidance"/> under <see cref="ReviewFeedbackHeading"/>, after the context
    /// budget, so the budget can never trim it. A later call replaces an earlier one: only the
    /// latest verdict describes the render being revised.
    /// </summary>
    public void RecordReviewFeedback(int score, int minScore, string? feedback, string? previousManifestNote = null)
    {
        if (string.IsNullOrWhiteSpace(feedback))
            return;

        string body = Cap(feedback.Trim(), MaxReviewFeedbackChars);
        StringBuilder text = new();
        text.Append(string.Format(CultureInfo.InvariantCulture, ReviewFeedbackHeading, score, minScore))
            .Append('\n')
            .Append(body);

        if (!string.IsNullOrWhiteSpace(previousManifestNote))
            text.Append("\n\n").Append(Cap(previousManifestNote.Trim(), MaxPreviousManifestNoteChars));

        _reviewFeedback = text.ToString();
    }

    private static string Cap(string value, int maxChars) => value.Length > maxChars ? value[..maxChars] : value;


    public void RecordRetryFeedback(int attemptNumber, string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return;

        string normalized = Cap(reason.Trim(), MaxRetryFeedbackChars);

        _retryFeedback.Add($"Attempt {attemptNumber}: {normalized}");

        // Keep only the latest few feedback entries to avoid runaway prompt growth.
        while (_retryFeedback.Count > 3)
            _retryFeedback.RemoveAt(0);
    }

    private string BuildPreviousStepInput()
    {
        // "The previous step" means the entry with the greatest StepOrder strictly less than
        // this step's own StepOrder — NOT "the most recently appended entry". Those two
        // coincide as long as StepOutputHistory is append-only, but a ReviewLoop loop-back
        // re-executes earlier steps, appending a second entry for a StepOrder that already
        // exists in history; "most recently appended" would then resolve to whichever step
        // last completed (which can be the ReviewLoop step's own JSON verdict), not the step
        // immediately before this one. See WorkflowExecutorService's loop-back pruning for the
        // complementary belt-and-braces fix.
        StepOutputHistoryEntry? latest = StepOutputHistory
            .Where(h => h.StepOrder < Step.StepOrder && !string.IsNullOrWhiteSpace(h.Output))
            .OrderByDescending(h => h.StepOrder)
            .FirstOrDefault();
        return latest is null ? "[\"Begin analysis of the project.\"]" : ForPrompt(latest).Output;
    }

    /// <summary>
    /// The <c>FullWorkflow</c> history: every earlier completed step with a non-blank output,
    /// ascending, each rendered through <see cref="ForPrompt"/> so a
    /// <see cref="SetPromptOutputOverride"/> is honoured. Split out of the old
    /// <c>BuildFullWorkflowInput</c> so the relevance pass can inspect exactly the same parts the
    /// budget is about to concatenate, rather than a second, drifting copy of this filter.
    /// </summary>
    private List<StepOutputHistoryEntry> FullWorkflowHistory()
    {
        return StepOutputHistory
            .Where(h => h.StepOrder < Step.StepOrder && !string.IsNullOrWhiteSpace(h.Output))
            .OrderBy(h => h.StepOrder)
            .Select(ForPrompt)
            .ToList();
    }

    /// <summary>
    /// The <c>SelectedPriorSteps</c> history — the same filter/narrowing
    /// <c>BuildSelectedPriorStepsInput</c> always applied, extracted for the reason
    /// <see cref="FullWorkflowHistory"/> was. An empty result is not special-cased here:
    /// <see cref="ApplyBudget"/> turns it into the same "Begin analysis of the project."
    /// placeholder the old code returned directly.
    /// </summary>
    private List<StepOutputHistoryEntry> SelectedPriorStepsHistory()
    {
        IReadOnlyList<int> selectedOrders = ParseSelectedStepOrders();
        if (selectedOrders.Count == 0)
            return [];

        HashSet<int> selectedSet = selectedOrders.ToHashSet();
        return StepOutputHistory
            .Where(h => selectedSet.Contains(h.StepOrder) && !string.IsNullOrWhiteSpace(h.Output))
            .OrderBy(h => h.StepOrder)
            .Select(ForPrompt)
            .ToList();
    }

    private string BuildCustomMappedSubsetInput()
    {
        if (string.IsNullOrWhiteSpace(Step.InputMappingJson))
            return ApplyBudget(FullWorkflowHistory(), relevance: null);

        string? mapped = ExpressionEvaluator.ApplyInputMapping(AccumulatedOutput, Step.InputMappingJson);
        if (string.IsNullOrWhiteSpace(mapped))
            return ApplyBudget(FullWorkflowHistory(), relevance: null);

        return mapped;
    }

    private IReadOnlyList<int> ParseSelectedStepOrders()
    {
        if (string.IsNullOrWhiteSpace(Step.SelectedPriorStepOrdersJson))
            return [];

        try
        {
            List<int>? parsed = JsonSerializer.Deserialize<List<int>>(Step.SelectedPriorStepOrdersJson);
            if (parsed is null)
                return [];

            return parsed
                .Where(stepOrder => stepOrder > 0 && stepOrder < Step.StepOrder)
                .Distinct()
                .OrderBy(stepOrder => stepOrder)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Sole entry point for turning a list of already-filtered/ordered history entries into the
    /// prompt string used by <see cref="BuildAgentInputCore"/>'s budgeted modes. Routes through
    /// <see cref="AgentInputBudget.Apply"/> unconditionally — with <see cref="BudgetOptions"/> null,
    /// that call is given a disabled-budget options instance, so it takes the exact same "return
    /// today's format unchanged" fast path <c>Apply</c> takes when enabled but already under
    /// budget. That keeps the concatenation FORMAT defined in exactly one place
    /// (<c>AgentInputBudget.BuildFormatted</c>) instead of drifting into two copies.
    /// </summary>
    /// <param name="relevance">
    /// The optional relevance ranking of the drop phase, or null (the only value the synchronous
    /// <see cref="BuildAgentInput"/> ever passes). It changes only WHICH droppable part is given up
    /// first — never what gets digested, never what the budget is allowed to keep, and never
    /// anything outside the string this method returns.
    /// </param>
    private string ApplyBudget(
        IReadOnlyList<StepOutputHistoryEntry> history,
        ContextRelevanceOrdering? relevance)
    {
        LastBudgetSummary = null;

        if (history.Count == 0)
            return "[\"Begin analysis of the project.\"]";

        List<(int StepOrder, string StepLabel, string Output)> parts =
            history.Select(h => (h.StepOrder, h.StepLabel, h.Output)).ToList();

        AgentInputBudgetOptions effectiveOptions = BudgetOptions ?? DisabledBudgetOptions;
        BudgetedConcatenation result = AgentInputBudget.Apply(parts, effectiveOptions, relevance);

        if (BudgetOptions is not null && (result.DigestedStepCount > 0 || result.DroppedStepCount > 0))
        {
            LastBudgetSummary =
                $"context budget: {result.OriginalChars}→{result.FinalChars} chars " +
                $"({result.DigestedStepCount} steps digested, {result.DroppedStepCount} dropped)";
        }

        return result.Text;
    }

    /// <summary>
    /// Reused across every unbudgeted call so <see cref="ApplyBudget"/> always goes through
    /// <see cref="AgentInputBudget.Apply"/>'s <c>!Enabled</c> path rather than duplicating its
    /// format logic here.
    /// </summary>
    private static readonly AgentInputBudgetOptions DisabledBudgetOptions = new() { Enabled = false };

}

/// <summary>
/// Result from a step execution determining what happens next.
/// </summary>
public class StepExecutionResult
{
    /// <summary>Set by a ReviewLoop step that did not loop back although its score was below the minimum.</summary>
    public ReviewExhausted? ReviewExhausted { get; init; }

    /// <summary>
    /// Set by a ReviewLoop step whose reviewer changed settings of earlier steps (docs/design/review-loop-adjustments.md):
    /// the configs to write to the run's own snapshot and the steps to reuse rather than re-run. Null otherwise, and
    /// always null when the step's <c>allowAdjustments</c> is off.
    /// </summary>
    public ReelBolt.Shared.Workflows.Review.ReviewAdjustmentPlan? ReviewAdjustments { get; init; }

    public required string Output { get; init; }
    public required int NextStepIndex { get; init; }
    public int NewIterationCount { get; init; }
    public int TokensUsed { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }

    /// <summary>Provider row id of the step's primary model call; null when none or unresolvable. Persisted as <see cref="WorkflowStepResult.PrimaryProviderId"/>.</summary>
    public Guid? PrimaryProviderId { get; init; }

    /// <summary>Model name of the step's primary model call. Persisted as <see cref="WorkflowStepResult.PrimaryModelName"/>.</summary>
    public string? PrimaryModelName { get; init; }
    public long DurationMs { get; init; }
    public int AttemptCount { get; init; } = 1;
    public int RetryCount { get; init; }
    public IReadOnlyList<AgentToolCallTrace> ToolCalls { get; init; } = [];
    public IReadOnlyList<string> Reasoning { get; init; } = [];
    public StepStatus Status { get; init; } = StepStatus.Completed;
    public string? ErrorDetails { get; init; }
    public int? IterationNumber { get; init; }

    /// <summary>
    /// S3/MinIO storage key for a video or image artifact produced during this step.
    /// Set by the RenderVideoAndUploadToStorage sandbox tool; keys now follow
    /// "projects/{projectId}/outputFiles/{executionId}/..." rather than the old
    /// "outputs/" prefix.
    /// </summary>
    public string? OutputStorageKey { get; init; }

    /// <summary>
    /// S3/MinIO storage key for a large, non-playable JSON artifact produced during this step
    /// (a VideoAnalyze step's full analysis document, or a VideoCompile step's EDL audit trail).
    /// Mirrors <see cref="WorkflowStepResult.ArtifactStorageKey"/> — kept as a SEPARATE field
    /// from <see cref="OutputStorageKey"/> for the same reason that column is separate: an
    /// outputFiles-prefixed OutputStorageKey is treated as a playable video by OutputsController,
    /// so a JSON artifact must never be confused with one.
    /// </summary>
    public string? ArtifactStorageKey { get; init; }

    /// <summary>
    /// For a room step (<see cref="StepType.EditRoom"/>/<see cref="StepType.GraphicsRoom"/>) only:
    /// the full room transcript (every turn, in order) as a JSON array, mirroring
    /// <see cref="Shared.IntegrationEvents.WorkflowStepChatTurn"/>'s per-turn shape but with the
    /// FULL untruncated turn text (this is DB persistence, not the ~600-char-truncated SSE
    /// broadcast). Copied verbatim onto <see cref="WorkflowStepResult.ChatTranscriptJson"/> by
    /// <c>WorkflowExecutorService</c> so reopening the execution page later shows the complete
    /// discussion, not just whatever a live SSE-connected tab happened to see. Null for every
    /// non-room step type.
    /// </summary>
    public string? ChatTranscriptJson { get; init; }

    /// <summary>
    /// S3/MinIO storage key for a timeline manifest produced during this step (e.g.
    /// a VideoCompile step's timeline document).
    /// Mirrors <see cref="WorkflowStepResult.TimelineStorageKey"/> — nullable, set only when
    /// the step produces a timeline artifact.
    /// </summary>
    public string? TimelineStorageKey { get; init; }

    /// <summary>
    /// B6b: who bears the cost of the work this FAILED step consumed — assigned at the point of
    /// production by an executor that knows (a coded failure, a BYO provider), never guessed. Left
    /// at <see cref="FailureClass.None"/> when the executor has nothing to say, in which case
    /// <c>WorkflowExecutorService</c> classifies it from the code and the exception
    /// (docs/design/saas-metering.md "Failure classes"). Persisted as
    /// <see cref="WorkflowStepResult.FailureClass"/>.
    /// </summary>
    public FailureClass FailureClass { get; init; } = FailureClass.None;

    /// <summary>The machine-readable failure code that came with <see cref="FailureClass"/>, if any.</summary>
    public string? FailureCode { get; init; }

    /// <summary>
    /// Text written for the user by first-party code, in the run's language, safe to show verbatim as the
    /// run's error. Opt-in: null for everything else. The run shows it only when the step's final
    /// <see cref="FailureClass"/> is a user class; for a platform class the generic text is used.
    /// </summary>
    public string? UserMessage { get; init; }

    /// <summary>
    /// False when a FAILED result is deterministic (bad config, malformed upstream output): the same
    /// input fails the same way, so <c>WorkflowExecutorService</c> stops at once instead of burning
    /// the remaining attempts. Defaults to true (transient/provider failures retry).
    /// </summary>
    public bool Retryable { get; init; } = true;

    /// <summary>
    /// D12: where this step's work actually ran. The runner-aware executors (D7b/D8) set it from the
    /// target D6's selector stored on <see cref="StepExecutionContext"/>; every executor that predates
    /// the compute fabric leaves the default, which is exactly what the Local target means.
    /// Persisted as <see cref="WorkflowStepResult.ComputeTarget"/>.
    /// </summary>
    public ComputeTargetKind ComputeTarget { get; init; } = ComputeTargetKind.Local;

    /// <summary>
    /// D12: the <c>runner_devices</c> row that produced this result. Persisted as
    /// <see cref="WorkflowStepResult.RunnerId"/>.
    /// </summary>
    public Guid? RunnerId { get; init; }

    /// <summary>
    /// D12: the <c>runner_jobs</c> row whose completion produced this result. Persisted as
    /// <see cref="WorkflowStepResult.RunnerJobId"/>.
    /// </summary>
    public Guid? RunnerJobId { get; init; }

    /// <summary>
    /// D12: whether this result's bytes came from the ORGANIZATION'S OWN runner — DERIVED from
    /// <see cref="ComputeTarget"/>, never an independently settable flag, so it is structurally
    /// impossible for the tag and the target to disagree (plan step 5: "FromRunner = (target ==
    /// OrgRunner)"). <see cref="ComputeTargetKind.CloudPool"/> is one of OUR workers: its output is
    /// verified too, but it is trusted, so it is not a runner result and stays cacheable.
    /// <para>
    /// This is the one value <c>IStepCachePolicy.IsAdmissible</c> excludes, and the engine writes it
    /// verbatim onto <see cref="WorkflowStepResult.FromRunner"/>.
    /// </para>
    /// </summary>
    public bool FromRunner => ComputeTarget == ComputeTargetKind.OrgRunner;

    /// <summary>
    /// WebSearch calls made while this step ran (run-wide counter delta, so a parallel branch can only over-count).
    /// Set by the engine. A result with any is never written to the step-result cache: it depends on the live web.
    /// </summary>
    public int WebSearchCalls { get; set; }
}

/// <summary>
/// T-009: the failed full renders of ONE step, counted PER COMPOSITION. <c>ReactRemotionSandboxTools</c>
/// records each failed <c>render</c> command here (the model gets its usual tool error, unchanged)
/// and refuses further renders of that composition once <c>Compute:Render:MaxFailedRendersPerStep</c>
/// is reached. Different compositions never accumulate against each other, so a multi-composition
/// planner is not capped by failures on compositions it is not rendering now.
/// <c>AgentStepExecutor</c> fails the step with <c>RENDER_FAILED</c> naming the exit/kill only when
/// the step is left with no usable output — never a step that already has its artifact.
///
/// <para>
/// The ledger lives on <see cref="StepExecutionContext"/> rather than on the execution-context
/// scope: a scope is opened per ATTEMPT, and the cap must count failures across the retry wrapper's
/// attempts (same step result lineage). It reaches the tool through <see cref="Publish"/> — an
/// <see cref="AsyncLocal{T}"/> the executor sets around the agent run, exactly like
/// <c>AgentRunBudget</c>.
/// </para>
/// </summary>
public sealed class RenderFailureLedger
{
    /// <summary>Longest sentence kept: the run shows a first-party step message up to this size.</summary>
    internal const int MaxMessageChars = 600;

    private static readonly AsyncLocal<RenderFailureLedger?> Ambient = new();

    /// <summary>The ledger of the agent run this async flow belongs to, if any.</summary>
    public static RenderFailureLedger? Current => Ambient.Value;

    private sealed class Entry
    {
        public int Failed;
        public string? LastMessage;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byComposition = new(StringComparer.Ordinal);
    private readonly HashSet<string> _terminalCompositions = new(StringComparer.Ordinal);
    private string? _lastMessage;
    private bool _terminalRefused;

    /// <summary>Makes this ledger visible to the tool calls made inside the returned scope, and restores the previous one on dispose.</summary>
    public IDisposable Publish()
    {
        RenderFailureLedger? previous = Ambient.Value;
        Ambient.Value = this;
        return new Restore(previous);
    }

    /// <summary>Failed full renders recorded for <paramref name="compositionId"/>, across every attempt of the step.</summary>
    public int FailedCount(string compositionId)
    {
        lock (_gate)
            return _byComposition.TryGetValue(compositionId, out Entry? entry) ? entry.Failed : 0;
    }

    /// <summary>Whether any composition of this step has a failed full render.</summary>
    public bool HasFailed { get { lock (_gate) return _byComposition.Count > 0; } }

    /// <summary>The most recent failure's bounded, user-facing sentence, whichever composition it was for.</summary>
    public string? LastMessage { get { lock (_gate) return _lastMessage; } }

    /// <summary>The last sentence recorded for <paramref name="compositionId"/>, or null when it never failed.</summary>
    public string? LastMessageFor(string compositionId)
    {
        lock (_gate)
            return _byComposition.TryGetValue(compositionId, out Entry? entry) ? entry.LastMessage : null;
    }

    /// <summary>Whether the per-composition cap refused a further render (of any composition).</summary>
    public bool TerminalRefused { get { lock (_gate) return _terminalRefused; } }

    /// <summary>Whether the per-composition cap refused a render of <paramref name="compositionId"/>.</summary>
    public bool TerminalRefusedFor(string compositionId)
    {
        lock (_gate)
            return _terminalCompositions.Contains(compositionId);
    }

    /// <summary>Records one failed render command of <paramref name="compositionId"/>.</summary>
    public void RecordFailure(string compositionId, string message)
    {
        lock (_gate)
        {
            if (!_byComposition.TryGetValue(compositionId, out Entry? entry))
                _byComposition[compositionId] = entry = new Entry();

            entry.Failed++;
            entry.LastMessage = Bound(message);
            _lastMessage = entry.LastMessage;
        }
    }

    /// <summary>Records that a render call of <paramref name="compositionId"/> was refused by the cap (without executing it).</summary>
    public void MarkTerminalRefused(string compositionId)
    {
        lock (_gate)
        {
            _terminalCompositions.Add(compositionId);
            _terminalRefused = true;
        }
    }

    private static string Bound(string? text) => string.IsNullOrWhiteSpace(text) ? string.Empty
        : text.Length <= MaxMessageChars ? text : text[..(MaxMessageChars - 1)].TrimEnd() + "…";

    private sealed class Restore(RenderFailureLedger? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>A review loop that stopped (iterations exhausted or no valid loop target) below its minimum score.</summary>
public sealed record ReviewExhausted(int StepOrder, int Score, int MinScore, int Iterations)
{
    /// <summary>The <c>workflow_executions.attention_json</c> payload.</summary>
    public static string ToAttentionJson(ReviewExhausted r) => System.Text.Json.JsonSerializer.Serialize(new
    {
        reason = "review_below_minimum",
        stepOrder = r.StepOrder,
        score = r.Score,
        minScore = r.MinScore,
        iterations = r.Iterations
    });
}
