using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Rooms;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Briefs;
using ReelBolt.WorkflowEngine.Services.Styles;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Shared scaffolding for every multi-agent "room" step type (<see cref="StepType.EditRoom"/>,
/// <see cref="StepType.GraphicsRoom"/>, a future color-grading room, ...): several persona seats
/// plus a director-role moderator converse in a live <c>Microsoft.Agents.AI.Workflows</c> group
/// chat over a bounded view, then the director emits ONE schema-validated
/// <typeparamref name="TDecision"/> in a normal structured call OUTSIDE the chat loop.
///
/// <para>
/// A concrete room supplies, via the abstract members below: its <see cref="StepType"/>/config
/// column/config type, the id-vocabulary extraction and mention regex for its own id namespace,
/// the room charter prompt and director turn directive, which <see cref="AgentType"/>s back the
/// seats/director/solo-fallback, and the deterministic decision validation (normalize, filter to
/// offered ids, accept/reject). Everything else — deterministic turn scheduling (via a
/// <see cref="RoomGroupChatManager"/> the subclass constructs), per-turn option injection and
/// prefix-cache-preserving message ordering (<see cref="RoomSeatAgent"/>), live progress +
/// <c>WorkflowStepChatTurn</c> events, transcript persistence (both the MinIO artifact and the
/// DB-persisted <c>ChatTranscriptJson</c>), the retried standalone synthesis call, the
/// solo-fallback degrade, and the never-throws/always-valid-JSON failure envelope — is shared
/// verbatim across rooms.
/// </para>
///
/// <para>
/// Same never-throws, always-valid-JSON discipline as <see cref="VideoAnalyzeStepExecutor"/>/
/// <see cref="VideoCompileStepExecutor"/> — every failure mode degrades to the solo fallback
/// (<see cref="IRoomStepConfig.FallbackToSolo"/>) or, as an absolute last resort, a clean
/// structured failure result.
/// </para>
/// </summary>
public abstract class RoomStepExecutorBase<TDecision> : IStepExecutor where TDecision : class
{
    protected static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // Mirrors VideoCompileStepExecutor.DecisionJsonOptions exactly: no UnmappedMemberHandling
    // override (default Skip), which is why the additive "room" sibling object on output_json is
    // safe to include alongside the decision's own properties — every downstream consumer of the
    // plain schema (VideoCompileStepExecutor's Decision AND GraphicsPlan resolution) simply
    // ignores the extra key.
    protected static readonly JsonSerializerOptions DecisionJsonOptions = new(JsonSerializerDefaults.Web);

    protected static readonly JsonSerializerOptions EnvelopeJsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>The room-participant display name for the director — matches what its RoomSeatAgent wrapper reports via Name.</summary>
    protected const string DirectorSeatName = "Director";

    /// <summary>
    /// The largest delay <see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/> accepts, in
    /// seconds. It measures the delay in milliseconds as a <c>uint</c>, so anything above this
    /// throws <see cref="ArgumentOutOfRangeException"/> rather than merely waiting a long time.
    /// <see cref="IRoomStepConfig.RoomTimeoutSeconds"/> arrives from the step's client-supplied
    /// config JSON, so the room clamps to it at the use site instead of trusting the value.
    /// </summary>
    private const int MaxRoomTimeoutSeconds = 4_294_967;

    /// <summary>
    /// Result of <see cref="FilterToOfferedIds"/>: how many id-bearing entries were dropped, plus
    /// an optional room-generic breakdown a concrete room wants surfaced on the output's
    /// <c>room</c> metadata object (e.g. the edit room's <c>droppedMixedSourceSpanCount</c>). Keys
    /// in <see cref="ExtraMeta"/> are merged into <c>roomMeta</c> verbatim — a room whose extra key
    /// collides with a base key (<c>seats</c>, <c>rounds</c>, ...) would silently overwrite it, so
    /// pick names that read as domain-specific.
    /// </summary>
    protected readonly record struct RoomFilterResult(int Dropped, JsonObject? ExtraMeta);

    protected readonly IAgentChatClientProvider ChatClients;
    protected readonly IAgentToolProvider ToolProvider;
    protected readonly IAgentRegistry AgentRegistry;
    protected readonly IProjectFileWorkspace Workspace;
    protected readonly IWorkflowExecutionContextAccessor ExecutionContextAccessor;
    protected readonly ILogger Logger;

    /// <summary>
    /// The convening gate — optional by construction. <c>IDecisionGate</c> is registered as a
    /// singleton in the real container, so the three room executors receive it through ordinary
    /// constructor injection; the trailing optional parameter exists purely so the existing test
    /// construction sites (which pass no gate) keep compiling untouched, and so an unregistered
    /// gate is a legal, inert configuration rather than a host-startup failure. A null gate is
    /// ALWAYS tolerated — the shadow arm no-ops on it.
    /// </summary>
    protected readonly IDecisionGate? DecisionGate;

    /// The Decision-capability provider resolver, used ONLY when a room's
    /// <see cref="IRoomStepConfig.DecisionScheduling"/> is not <see cref="RoomDecisionMode.Off"/>.
    /// Null (the default, and what every existing direct-construction test site gets) simply means
    /// "no decision provider" — the room runs exactly as it does with scheduling off.
    /// </summary>
    protected readonly IInferenceProviderResolver? DecisionResolver;

    /// <summary>Builds the concrete <see cref="IDecisionClient"/> for a resolved Decision provider. Null disables decision scheduling, same as <see cref="DecisionResolver"/>.</summary>
    protected readonly IDecisionClientFactory? DecisionClientFactory;

    protected RoomStepExecutorBase(
        IAgentChatClientProvider chatClients,
        IAgentToolProvider toolProvider,
        IAgentRegistry agentRegistry,
        IProjectFileWorkspace workspace,
        IWorkflowExecutionContextAccessor executionContextAccessor,
        ILogger logger,
        IDecisionGate? decisionGate = null,
        IInferenceProviderResolver? decisionResolver = null,
        IDecisionClientFactory? decisionClientFactory = null)
    {
        ChatClients = chatClients;
        ToolProvider = toolProvider;
        AgentRegistry = agentRegistry;
        Workspace = workspace;
        ExecutionContextAccessor = executionContextAccessor;
        Logger = logger;
        DecisionGate = decisionGate;
        DecisionResolver = decisionResolver;
        DecisionClientFactory = decisionClientFactory;
    }

    // ---------------------------------------------------------------------
    // The domain seam — what a concrete room must (or may) supply.
    // ---------------------------------------------------------------------

    public abstract StepType StepType { get; }

    /// <summary>Log/name token for this room's step type, e.g. "EditRoom".</summary>
    protected abstract string RoomKind { get; }

    /// <summary>Sentence-case display name used in degrade messages, e.g. "Edit room".</summary>
    protected abstract string RoomDisplayName { get; }

    /// <summary>Failure-code prefix, e.g. "EDIT_ROOM" (yields EDIT_ROOM_CONFIG_INVALID / EDIT_ROOM_FAILED).</summary>
    protected abstract string FailureCodePrefix { get; }

    /// <summary>The name of the step's config JSON property, e.g. "EditRoomConfigJson" — used in config-error messages.</summary>
    protected abstract string ConfigPropertyName { get; }

    /// <summary>Reads this room's config JSON off the step row.</summary>
    protected abstract string? GetConfigJson(WorkflowStep step);

    /// <summary>Deserializes this room's config record (may throw <see cref="JsonException"/>; the template catches it).</summary>
    protected abstract IRoomStepConfig? DeserializeConfig(string json);

    /// <summary>Extracts this room's offered id vocabulary from the resolved bounded view.</summary>
    protected abstract HashSet<string> ExtractOfferedIds(JsonNode? viewRoot);

    /// <summary>Extracts this room's offered-id mentions from free-form turn text (for chat-turn events).</summary>
    protected abstract IReadOnlyList<string> ExtractIdMentions(string? text, IReadOnlySet<string> offeredIds);

    /// <summary>The failure message when the view offers zero ids to decide over.</summary>
    protected abstract string NoOfferedIdsMessage { get; }

    /// <summary>
    /// Optional escape hatch for a room whose decision space can legitimately be empty: return a
    /// non-null decision to complete the step successfully (without running the room at all) when
    /// the resolved view offers zero ids. The default (null) fails the step with VIEW_UNRESOLVED —
    /// the edit room's behavior, where a view with nothing to keep is a genuine upstream error.
    /// </summary>
    protected virtual TDecision? BuildDecisionForEmptyView() => null;

    /// <summary>The shared, identical instructions for EVERY room participant (seats AND the director's room-participant instance).</summary>
    protected abstract string RoomCharterPrompt { get; }

    /// <summary>The AgentType every seat's inner agent resolves through (per-seat AgentDefinitionId overrides still apply).</summary>
    protected abstract AgentType SeatAgentType { get; }

    /// <summary>The AgentType the director resolves through — both as room participant and for the standalone synthesis call.</summary>
    protected abstract AgentType DirectorAgentType { get; }

    /// <summary>The AgentType the solo fallback resolves through.</summary>
    protected abstract AgentType SoloFallbackAgentType { get; }

    /// <summary>
    /// A scope the room's agents run inside once the view is read (seat turns, the synthesis call and the solo
    /// fallback), or null for none, the default: an ambient fact the room derives from its view. Disposed when the step
    /// ends.
    /// </summary>
    protected virtual IDisposable? EnterViewScope(JsonNode? viewRoot) => null;

    /// <summary>The role noun used in the default seat directive, e.g. "editor".</summary>
    protected abstract string SeatRoleNoun { get; }

    /// <summary>The director's per-turn room-participant directive (appended last, after the shared prefix).</summary>
    protected abstract string DirectorTurnDirective { get; }

    /// <summary>Instruction sentence naming the synthesis schema, e.g. "output ONLY valid JSON matching the VideoEditDecisionOutput schema."</summary>
    protected abstract string SynthesisSchemaInstruction { get; }

    /// <summary>Normalizes a freshly deserialized decision in place (e.g. null-list coalescing). Applied on both the synthesis and solo paths.</summary>
    protected abstract void NormalizeDecision(TDecision decision);

    /// <summary>
    /// Rejects a freshly synthesized (already normalized, not yet filtered) decision by returning
    /// a retry-guidance error, or null to accept. The edit room rejects an empty Keep list here;
    /// the graphics room accepts an empty plan (zero overlays is a valid outcome).
    /// </summary>
    protected abstract string? RejectFreshSynthesis(TDecision decision);

    /// <summary>
    /// Drops every id-bearing entry not in <paramref name="offeredIds"/> (and any other
    /// room-specific structural rule — e.g. the edit room additionally drops a Keep span whose two
    /// ids come from different source clips). <paramref name="viewRoot"/> is the same parsed
    /// bounded view <see cref="ExtractOfferedIds"/> was built from, re-offered here for a room that
    /// needs per-id metadata beyond plain membership (source-clip index, region metadata, ...)
    /// without the base template having to model that metadata generically.
    /// </summary>
    protected abstract RoomFilterResult FilterToOfferedIds(TDecision decision, HashSet<string> offeredIds, JsonNode? viewRoot);

    /// <summary>
    /// Rejects an already-filtered decision by returning a degrade/error message, or null to
    /// accept. <paramref name="solo"/> distinguishes the solo-fallback path's message wording.
    /// </summary>
    protected abstract string? RejectFilteredDecision(TDecision decision, bool solo);

    /// <summary>
    /// Optional soft-reject of a freshly normalized, already-<see cref="RejectFreshSynthesis"/>-
    /// accepted decision: return retry guidance to spend one more synthesis attempt (mirrors
    /// <see cref="RejectFreshSynthesis"/>'s retry), or null to accept as-is. Unlike
    /// <see cref="RejectFreshSynthesis"/>, this issue is NOT fatal on the last attempt — the
    /// template accepts the decision regardless once <c>attempt == maxAttempts</c>, trusting
    /// <see cref="FilterToOfferedIds"/> to clean up whatever the issue was (the edit room's
    /// mixed-source-span check: worth one retry with precise feedback, but a span still bridging
    /// two clips on the final attempt is dropped rather than degrading the whole room).
    /// </summary>
    protected virtual string? CheckRetryableIssue(TDecision decision, JsonNode? viewRoot) => null;

    /// <summary>
    /// Optional content POLICY check over a decision that already passed every structural check —
    /// return actionable feedback to spend exactly ONE dedicated extra call (not counted against
    /// <see cref="IRoomStepConfig.MaxSynthesisAttempts"/>, so a mixed-source retry can never use it
    /// up), or null to accept. The extra call's answer replaces the decision only when it passes this
    /// same check and survives <see cref="FilterToOfferedIds"/>; otherwise the original decision
    /// stands and the room records the finding through <see cref="BuildDecisionFindings"/> — a policy
    /// miss is never a degrade and never a failure. Runs on the synthesis path and on the solo
    /// fallback path alike. <paramref name="transcript"/> is the room's discussion when one ran
    /// (null otherwise), so the feedback can quote the room's own proposal back to the director. The
    /// edit room's interleave guard is the one implementation (docs/video-editing.md "Using every
    /// clip offered").
    /// </summary>
    protected virtual string? CheckDecisionPolicy(
        TDecision decision, JsonNode? viewRoot, IReadOnlyList<ChatMessage>? transcript, StepExecutionContext context) => null;

    /// <summary>
    /// Optional top-level findings to record beside the final decision on <c>output_json</c> (siblings
    /// of the decision's own members and of <c>"room"</c>, which they may never replace). Called once,
    /// with the decision this step actually emits, on every success path. Null (the default) adds
    /// nothing, so a room without a policy keeps its output byte-identical.
    /// <paramref name="policyRetried"/> says whether <see cref="CheckDecisionPolicy"/>'s retry was spent.
    /// </summary>
    protected virtual JsonObject? BuildDecisionFindings(
        TDecision decision, JsonNode? viewRoot, StepExecutionContext context, bool policyRetried) => null;

    /// <summary>
    /// Constructs this room's <see cref="RoomGroupChatManager"/> (the subclass binds its own
    /// id-mention pattern and config type). <paramref name="decisionClient"/> is the already-resolved
    /// advisory decision client — null whenever <see cref="IRoomStepConfig.DecisionScheduling"/> is
    /// <see cref="RoomDecisionMode.Off"/>, no Decision provider is configured, or resolution failed;
    /// the manager treats null as "ask nothing".
    /// </summary>
    protected abstract RoomGroupChatManager CreateManager(
        IReadOnlyList<AIAgent> agents, IRoomStepConfig config, IReadOnlySet<string> offeredIds,
        IDecisionClient? decisionClient);

    /// <summary>Per-seat room-turn directive. Default mirrors the edit room's original wording via <see cref="SeatRoleNoun"/>.</summary>
    protected virtual string BuildSeatDirective(EditRoomSeat seat) =>
        $"You are now speaking as {seat.Name}, the {seat.Persona} {SeatRoleNoun} in this room.";

    /// <summary>Progress label for a completed turn. Default mirrors the edit room's original wording.</summary>
    protected virtual string BuildTurnProgressLabel(bool isDirector, string seatName, int turnNumber, int maxTurns) =>
        isDirector
            ? "Director is reviewing"
            : $"{seatName} is proposing a cut (turn {turnNumber}/{maxTurns})";

    /// <summary>The group chat's opening message carrying the bounded view — the shared prompt-prefix every turn hits the cache against.</summary>
    protected virtual string BuildOpeningMessage(string viewJson) =>
        $"Bounded analysis view for this {RoomDisplayName.ToLowerInvariant()} (every participant sees this same view):\n\n" + viewJson;

    /// <summary>
    /// Room-turn tool scope for an in-room agent (a seat's or the director's room-participant
    /// instance). Defaults to the agent type's full <see cref="IAgentToolProvider"/> grant — right
    /// for rooms whose agent types are minimal-read-only anyway (the edit room); a room whose
    /// backing agents carry wider grants (the graphics room's sandbox+render set) overrides this
    /// to restrict in-room turns. <c>LookAtMoment</c> is left out of every room turn: it spends, and several seats
    /// asking about the same moments would pay for one answer several times. The solo fallback is an ordinary
    /// run of its agent and keeps it.
    /// </summary>
    protected virtual IReadOnlyList<AIFunction> GetRoomTurnTools(AgentType agentType) =>
        [.. ToolProvider.GetTools(agentType).Where(t => !PaidLookToolNames.Contains(t.Name))];

    private static readonly HashSet<string> PaidLookToolNames =
        new(ToolGroupCatalog.FunctionNamesFor(ToolGroup.MomentLook), StringComparer.Ordinal);

    /// <summary>
    /// Hook invoked before the bounded view is resolved — e.g. to install a prompt-output override
    /// enriching the view (the graphics room's inEdit annotation). Default no-op. Must never throw
    /// for a recoverable reason: a view-enrichment failure should degrade to the plain view.
    /// </summary>
    protected virtual Task PrepareViewAsync(StepExecutionContext context) => Task.CompletedTask;

    // ---------------------------------------------------------------------
    // The convening-gate seams — see IRoomStepConfig.ConveneGate. Same shape of hook as
    // BuildDecisionForEmptyView/PrepareViewAsync above: the default IS "no gate", so the three
    // concrete rooms compile and behave identically until one opts in by overriding.
    //
    // DecisionQuestion is the WIRE question (name/kind/instructions/options — what a decision
    // model is asked) while DecisionGateQuestion is the GATE question (the wire question plus the
    // observation `Site` and the room's own answer as `SystemTwoAnswer`). That asymmetry is
    // deliberate: the pre-room convening path asks about the VIEW and has no answer yet, whereas
    // the post-synthesis shadow path always knows what the room decided and records it. Hence the
    // two seams' different return types.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The questions a pre-room convening gate asks over the bounded view, or null/empty for "this
    /// room has no convening question set" — the default, and the edit and graphics rooms' explicit
    /// answer (decision 7).
    ///
    /// <para>
    /// Read by <see cref="EvaluateConveneGateAsync"/> at the dispatch point in
    /// <see cref="ExecuteAsync"/>, and ONLY when the mode is <see cref="ConveneGateMode.Gate"/>: a
    /// null/empty return there is what makes the Gate mode a no-op for a room that supplies no
    /// question set, rather than an error or an empty provider call.
    /// </para>
    /// </summary>
    protected virtual IReadOnlyList<DecisionQuestion>? BuildConveneQuestions(JsonNode? viewRoot) => null;

    /// <summary>
    /// The agenda text the convening gate appends to the room's opening message when it ESCALATED
    /// ("here is what the room still needs to settle"), or null for none — the default.
    ///
    /// <para>
    /// Called by <see cref="EvaluateConveneGateAsync"/> only on the escalating branch, and only
    /// when the outcome carried a result; a null return there leaves the opening message
    /// byte-identical to what it was before the agenda existed. This is the seam a room overrides
    /// to reuse <see cref="RenderConveneAgenda"/> over its own question set — see
    /// <c>ColorGradeRoomStepExecutor</c>.
    /// </para>
    /// </summary>
    protected virtual string? BuildConveneAgenda(JsonNode? viewRoot, IReadOnlyDictionary<string, DecisionAnswer> answers) => null;

    /// <summary>
    /// The gate questions a Shadow-mode room records AFTER synthesis, each carrying the room's own
    /// synthesized value as <see cref="DecisionGateQuestion.SystemTwoAnswer"/> so the observation's
    /// <c>agreed</c> outcome compares the decision model against what this room actually decided.
    /// Null/empty (the default) means "nothing to observe" and skips the gate entirely.
    /// </summary>
    protected virtual IReadOnlyList<DecisionGateQuestion>? BuildShadowQuestions(TDecision decision) => null;

    /// <summary>
    /// The hard ceiling on the convening agenda block, in characters.
    ///
    /// <para>
    /// The bound exists because the agenda is appended to the room's OPENING MESSAGE — turn zero of
    /// the group chat — so every character of it is re-sent on every turn AND counts against the
    /// same history budget <see cref="IRoomStepConfig.MaxHistoryChars"/> protects. It is therefore
    /// a small fraction of that budget (600 of the 40 000 default — 1.5%), not a number that could
    /// approach it: a wide question set must be able to lose its tail rather than push the view out
    /// of the budget it shares. <see cref="RenderConveneAgenda"/> is what enforces the cap.
    /// </para>
    /// </summary>
    internal const int MaxConveneAgendaChars = 600;

    /// <summary>
    /// Formats the fast model's answers into the room's convening agenda: for every question the
    /// model was asked and SPLIT on, one sentence naming the two leading options and their
    /// probabilities, framed as the disagreement the room has to settle. Returns <c>null</c> when
    /// nothing qualifies — an all-confident (or empty, or degenerate) answer set produces no block
    /// at all, so the room's opening message stays exactly what it was before this feature existed.
    ///
    /// <para>
    /// Why an agenda at all: the point of convening on a decision model is that when it is split,
    /// its disagreement becomes the room's FIRST order of business. A room convened on a question
    /// the fast model was already 0.44/0.41 on, and not told which question that was, simply
    /// re-derives the same split from scratch.
    /// </para>
    ///
    /// <para>
    /// Pure and total by construction: it reads no config, touches no service, and returns null
    /// rather than throwing for every degenerate input, so a caller may append its result to the
    /// opening message unconditionally.
    /// </para>
    ///
    /// <para>
    /// <b>Internal</b> (via <c>InternalsVisibleTo</c>) rather than protected: this is a formatter
    /// the tests and the base class share, not part of a room's subclassing contract — no room
    /// needs to vary how a split is worded, only WHICH questions it is asked.
    /// </para>
    /// </summary>
    /// <param name="questions">The wire questions the gate asked, in the order it asked them.</param>
    /// <param name="answers">The gate's answers, keyed by <see cref="DecisionQuestion.Name"/>.</param>
    internal static string? RenderConveneAgenda(
        IReadOnlyList<DecisionQuestion> questions,
        IReadOnlyDictionary<string, DecisionAnswer> answers)
    {
        // Null-tolerant even though both parameters are non-nullable by signature: the answer
        // dictionary arrives from a provider response, so "the model returned nothing usable" is a
        // real runtime state, and this formatter's contract is that it never throws.
        if (questions is null || answers is null || questions.Count == 0)
            return null;

        // One candidate sentence per split question, kept with its margin so the sharpest
        // disagreement can lead (see the ordering below) and with its index so the order is fully
        // deterministic when two margins tie.
        var candidates = new List<(double Margin, int Order, string Sentence)>();

        for (int i = 0; i < questions.Count; i++)
        {
            DecisionQuestion question = questions[i];

            if (!answers.TryGetValue(question.Name, out DecisionAnswer? answer) || answer is null)
                continue;

            // Nullable in practice (it is deserialized from a provider response), and fewer than
            // two entries means there is no top-two to name — both contribute nothing rather than
            // an invented or duplicated "Option X 0.00 vs Option X 0.00" line.
            IReadOnlyDictionary<string, double>? probabilities = answer.Probabilities;
            if (probabilities is null || probabilities.Count < 2)
                continue;

            // Descending by probability, ties broken ordinally by option name — a dictionary's own
            // enumeration order is not a contract, so a tie must not render differently run to run.
            List<KeyValuePair<string, double>> ranked = probabilities
                .OrderByDescending(p => p.Value)
                .ThenBy(p => p.Key, StringComparer.Ordinal)
                .Take(2)
                .ToList();

            string topOption = ranked[0].Key;
            double topP = ranked[0].Value;
            string secondOption = ranked[1].Key;
            double secondP = ranked[1].Value;

            // InvariantCulture + "0.00": the probabilities are machine-read numbers and this text
            // is prose a model must be able to copy an option name out of. A machine-local culture
            // must never turn 0.44 into 0,44.
            candidates.Add((
                topP - secondP,
                i,
                $"The fast model split on {ShortQuestionName(question.Name)}: " +
                $"{topOption} {topP.ToString("0.00", CultureInfo.InvariantCulture)} vs " +
                $"{secondOption} {secondP.ToString("0.00", CultureInfo.InvariantCulture)}. " +
                "Resolve that disagreement first."));
        }

        if (candidates.Count == 0)
            return null;

        // Most-contested-first: the smallest top-minus-second margin is the split the room is most
        // likely to be able to settle, so it leads — and therefore it is also the sentence that
        // survives the cap below when a wide question set has to lose its tail.
        candidates.Sort((a, b) =>
        {
            int byMargin = a.Margin.CompareTo(b.Margin);
            return byMargin != 0 ? byMargin : a.Order.CompareTo(b.Order);
        });

        // Keep whole sentences until the cap is reached, then STOP — never truncate. Half a
        // sentence, half a word or half a number is worse than one sentence fewer: a model told to
        // "resolve Filmic 0." or to pick between "Wa" and "Warm" has been handed a corrupted option
        // name, which is precisely the failure the id/word anchoring discipline elsewhere in this
        // pipeline exists to prevent.
        var kept = new List<string>();
        int length = 0;

        foreach (string sentence in candidates.Select(c => c.Sentence))
        {
            int separator = kept.Count == 0 ? 0 : 1;
            if (length + separator + sentence.Length > MaxConveneAgendaChars)
                break;

            kept.Add(sentence);
            length += separator + sentence.Length;
        }

        // A single sentence longer than the cap on its own (very long option names) means no agenda
        // is better than a truncated one.
        return kept.Count == 0 ? null : string.Join("\n", kept);
    }

    /// <summary>
    /// The short form of a question name for the agenda PROSE: its last dot-segment, so the agenda
    /// reads "Look" rather than "Colorist.Look". The full dotted name remains the wire key used to
    /// look the answer up and every observation column's site — this shortens what the room READS
    /// and renames nothing (see <c>ColorGradeRoomStepExecutor.BuildConveneQuestions</c>, which sets
    /// <c>Name = "Colorist.Look"</c> precisely so the room and the solo agent share one namespace).
    /// </summary>
    private static string ShortQuestionName(string name)
    {
        int lastDot = name.LastIndexOf('.');
        return lastDot >= 0 && lastDot + 1 < name.Length ? name[(lastDot + 1)..] : name;
    }

    // ---------------------------------------------------------------------
    // The template.
    // ---------------------------------------------------------------------

    public async Task<StepExecutionResult> ExecuteAsync(StepExecutionContext context)
    {
        Stopwatch sw = Stopwatch.StartNew();
        var usage = new RoomUsage();
        WorkflowStep step = context.Step;

        // Every agent tool invoked during the room (each seat's ProjectFileAgentTools calls) and
        // during the solo fallback resolves its WorkflowExecutionContext via this AsyncLocal-backed
        // accessor — confirmed live to be load-bearing (a room run whose scope was missing failed
        // its solo fallback with "No workflow execution context is available for project file
        // tools."). AsyncLocal flows correctly through the awaited
        // InProcessExecution.RunStreamingAsync call chain once this scope is actually opened.
        using IDisposable _ = ExecutionContextAccessor.BeginScope(
            context.Execution.Id,
            context.Execution.ProjectId,
            context.CorrelationId,
            context.Execution.OrganizationId,
            stepResultId: context.StepResultId);
        // The graphics room's legibility side channel (docs/design/graphics-see-and-size.md): visible
        // to the synthesis call's render tool. Inert for every room whose view never configures it.
        using IDisposable legibilityScope = context.OverlayLegibility.Publish();

        try
        {
            IRoomStepConfig? config;
            try
            {
                string? configJson = GetConfigJson(step);
                if (string.IsNullOrWhiteSpace(configJson))
                    return Failure(context, sw, $"{FailureCodePrefix}_CONFIG_INVALID", $"{RoomKind} step has no {ConfigPropertyName} configured.");

                config = DeserializeConfig(configJson);
            }
            catch (JsonException ex)
            {
                return Failure(context, sw, $"{FailureCodePrefix}_CONFIG_INVALID", $"{ConfigPropertyName} is not valid JSON: {ex.Message}");
            }

            if (config is null)
                return Failure(context, sw, $"{FailureCodePrefix}_CONFIG_INVALID", $"{ConfigPropertyName} deserialized to null.");

            await PrepareViewAsync(context);

            ExtractInputRef viewRef = config.View ?? new ExtractInputRef(ExtractInputSource.Previous);
            (string? viewJson, string? viewError) = ResolveViewJson(context, viewRef);
            if (viewJson is null)
                return Failure(context, sw, "VIEW_UNRESOLVED", viewError ?? "Could not resolve the bounded analysis view input.");

            JsonNode? viewRoot;
            try
            {
                viewRoot = JsonNode.Parse(viewJson);
            }
            catch (JsonException ex)
            {
                return Failure(context, sw, "VIEW_UNRESOLVED", $"View input is not valid JSON: {ex.Message}");
            }

            HashSet<string> offeredIds = ExtractOfferedIds(viewRoot);
            // What the room's agents are told about the view beyond the view itself (the dailies room: which project
            // subjects the takes show), for the whole room, the synthesis and the solo fallback. Null for every other room.
            using IDisposable? viewScope = EnterViewScope(viewRoot);
            if (offeredIds.Count == 0)
            {
                TDecision? emptyViewDecision = BuildDecisionForEmptyView();
                if (emptyViewDecision is null)
                    return Failure(context, sw, "VIEW_UNRESOLVED", NoOfferedIdsMessage);

                context.RecordResolvedInput(JsonSerializer.Serialize(new
                {
                    view = new { from = viewRef.From.ToString(), stepOrder = viewRef.StepOrder },
                    offeredIdCount = 0,
                    seats = config.EffectiveSeats.Select(s => s.Name).ToArray()
                }));

                return Success(context, sw, config, emptyViewDecision,
                    observedTurnCount: 0, terminationReason: "empty-view",
                    synthesisAttempts: 0, droppedCount: 0, extraRoomMeta: null,
                    degraded: false, degradeReason: null,
                    transcriptArtifactStorageKey: null, chatTranscriptJson: null,
                    viewRoot: viewRoot, policyRetried: false);
            }

            context.RecordResolvedInput(JsonSerializer.Serialize(new
            {
                view = new { from = viewRef.From.ToString(), stepOrder = viewRef.StepOrder },
                offeredIdCount = offeredIds.Count,
                seats = config.EffectiveSeats.Select(s => s.Name).ToArray()
            }));

            string? degradeReason = null;
            TDecision? decision = null;
            int synthesisAttempts = 0;
            int droppedCount = 0;
            JsonObject? extraRoomMeta = null;
            int observedTurnCount = 0;
            string terminationReason = "room-not-run";
            string? transcriptArtifactStorageKey = null;
            string? chatTranscriptJson = null;

            // Carried out of the room's try block so the solo fallback (and its policy retry) can
            // still quote the room's own discussion when the room degraded after it spoke.
            IReadOnlyList<ChatMessage>? roomTranscript = null;
            bool policyRetried = false;

            // ---------------------------------------------------------------------------------
            // THE CONVENING DISPATCH POINT — the `Gate` arm of IRoomStepConfig.ConveneGate runs
            // here: after the offered-ids gate and RecordResolvedInput above, before RunRoomAsync
            // below, so it conditions on viewRoot, offeredIds and config, and only once the
            // resolved input has been recorded.
            //
            // A fully ACCEPTED outcome means the room is NOT convened — the step advances on the
            // room's own solo fallback with terminationReason "not-convened". Anything else
            // (escalation, no provider, a gate-level timeout, any failure, or a room whose seam
            // supplies no question set) convenes as it always did, carrying the gate's agenda into
            // the opening message when the outcome produced one.
            // ---------------------------------------------------------------------------------

            // The gate gets its OWN try/catch, mirroring the shadow arm below and every other
            // DecisionGate call site: a bug in the gate, in building its questions here, or a
            // transport surprise can only ever make this room CONVENE — never fail it, and never
            // leave it acting on half an outcome. A gate-level timeout needs no case of its own:
            // D-004's DecideAsync turns its own deadline into DecisionGateOutcome.NoDecision rather
            // than throwing, so the timeout path arrives at the same "convene" branch as everything
            // else that did not produce a usable answer.
            bool notConvened = false;
            bool gateEscalated = false;
            string? conveneAgenda = null;

            // Read at the attach site below (see AttachConveneSystemTwoAnswersAsync): the Gate arm's
            // compensating half applies only when the room actually ran and synthesized the decision
            // this step emits — not when the gate accepted, not when it had nothing to say, and not
            // when the room degraded to the solo fallback and that fallback's output won instead.
            bool roomSynthesizedDecision = false;

            try
            {
                (notConvened, gateEscalated, conveneAgenda) =
                    await EvaluateConveneGateAsync(context, config, viewRoot);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "{RoomKind} step {StepOrder}: convening gate failed; convening as if the gate were Off.",
                    RoomKind, step.StepOrder);

                // Any gate failure → convene (today's behaviour). The agenda goes with it: it is
                // derived from the very outcome that just failed to arrive. `gateEscalated` stays
                // false: a gate that failed is a gate that wrote no row to complete.
                notConvened = false;
                gateEscalated = false;
                conveneAgenda = null;
            }

            if (notConvened)
            {
                // EVERY convene question cleared both AcceptAt and MinMargin, so the fast model's
                // answer stands in for the room and the room is deliberately NOT convened.
                //
                // This is not a degrade: skipping the room is the gate working as designed, which
                // is why `degradeReason` stays null (and therefore `degraded` stays false) all the
                // way down to the Success call below. The emitted room meta still says so outright
                // ("not-convened"), so an empty transcript is never left for a reader to misread as
                // a fault — and the step advances exactly as a convened one does, because the
                // Success call that emits it is the same one.
                //
                // The decision comes from the room's EXISTING solo fallback, taken unchanged rather
                // than reimplemented: a non-convened step therefore produces the same decision
                // shape, the same offered-ids filtering and the same room-specific validation a
                // degraded one does.
                (decision, RoomFilterResult soloFilter, string? soloError, policyRetried) =
                    await RunSoloFallbackAsync(context, viewJson, offeredIds, viewRoot, usage, transcript: null);

                // A solo fallback that itself fails is the room's EXISTING failure mode — the same
                // ${FailureCodePrefix}_FAILED the degrade branch below returns — not a new one, and
                // never a silently emitted decision.
                if (decision is null)
                {
                    return Failure(
                        context, sw, $"{FailureCodePrefix}_FAILED",
                        soloError ?? "The solo fallback did not produce a usable decision.", usage: usage);
                }

                droppedCount = soloFilter.Dropped;
                extraRoomMeta = soloFilter.ExtraMeta;
                terminationReason = "not-convened";
            }
            else
            {
                try
                {
                    // `conveneAgenda` is the gate's disagreement, carried into the opening message
                    // when the gate ESCALATED, and null whenever it did not — in which case the
                    // opening message is byte-for-byte what it was before this arm existed.
                    RoomRunResult roomResult = await RunRoomAsync(
                        context, config, viewJson, offeredIds, conveneAgenda: conveneAgenda, usage: usage);
                    observedTurnCount = roomResult.Transcript.Count;
                    terminationReason = roomResult.TerminationReason;
                    roomTranscript = roomResult.Transcript;

                    // Built unconditionally (unlike the MinIO artifact below, which is gated behind
                    // PersistTranscript) — this is DB persistence for the execution detail page, not an
                    // opt-in audit artifact, so it must be available even when PersistTranscript is off.
                    if (roomResult.Turns.Count > 0)
                    {
                        chatTranscriptJson = BuildChatTranscriptJson(roomResult.Turns, offeredIds, config.ClampedMaxTurns);
                    }

                    if (config.PersistTranscript && roomResult.Transcript.Count > 0)
                    {
                        transcriptArtifactStorageKey = await TryPersistTranscriptAsync(
                            context, roomResult.Transcript, step.StepOrder);
                    }

                    if (roomResult.Transcript.Count == 0)
                    {
                        degradeReason = "The room produced zero usable turns.";
                    }
                    else
                    {
                        (decision, synthesisAttempts, string? synthesisError, policyRetried) =
                            await SynthesizeDecisionAsync(context, config, viewJson, roomResult.Transcript, viewRoot, offeredIds, usage);

                        if (decision is null)
                        {
                            degradeReason = synthesisError ?? "Synthesis did not produce a usable decision.";
                        }
                        else
                        {
                            RoomFilterResult filterResult = FilterToOfferedIds(decision, offeredIds, viewRoot);
                            droppedCount = filterResult.Dropped;
                            extraRoomMeta = filterResult.ExtraMeta;
                            degradeReason = RejectFilteredDecision(decision, solo: false);

                            // The room's OWN synthesized decision survived to be this step's answer
                            // — which is exactly the condition under which System Two has an answer
                            // worth attaching to the gate's rows. A non-null degradeReason here means
                            // the room's decision was rejected wholesale and the solo fallback below
                            // (or the Failure path) supersedes it, so no attach is warranted for it.
                            roomSynthesizedDecision = degradeReason is null;
                        }
                    }
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "{RoomKind} step {StepOrder}: room run failed; falling back.", RoomKind, step.StepOrder);
                    degradeReason = $"{RoomDisplayName} failed: {ex.Message}";
                }
            }

            bool degraded = degradeReason is not null;

            if (degraded)
            {
                if (!config.FallbackToSolo)
                    return Failure(context, sw, $"{FailureCodePrefix}_FAILED", degradeReason!, transcriptArtifactStorageKey, chatTranscriptJson, usage);

                (decision, RoomFilterResult soloFilter, string? soloError, policyRetried) =
                    await RunSoloFallbackAsync(context, viewJson, offeredIds, viewRoot, usage, roomTranscript);
                if (decision is null)
                    return Failure(context, sw, $"{FailureCodePrefix}_FAILED", soloError ?? degradeReason!, transcriptArtifactStorageKey, chatTranscriptJson, usage);

                droppedCount = soloFilter.Dropped;
                extraRoomMeta = soloFilter.ExtraMeta;
            }

            // Shadow-mode convening gate: post-synthesis calibration observations, strictly
            // additive. Runs after the decision is FINAL (whether it came from the room's director
            // or the solo-fallback degrade) and immediately before Success, so every observation
            // compares the decision model against what this room actually decided. It must change
            // nothing about that decision, this step's status, or the transcript.
            //
            // Defense in depth on top of IDecisionGate.RunShadowAsync's own never-throws contract
            // (a null gate, no Decision provider row, a transport error and a malformed response
            // all degrade to a no-op there) — exactly like AgentStepExecutor's shadow call site:
            // this gets its own try/catch so a bug in the gate, or in building its questions here,
            // can never touch the StepExecutionResult returned below. The room therefore acquires
            // no new failure mode. OperationCanceledException on a genuinely-cancelled context is
            // still rethrown, as everywhere else in this method.
            try
            {
                await RunConveneShadowAsync(context, config, viewJson, decision!);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(
                    ex,
                    "{RoomKind} step {StepOrder}: convening-gate shadow hook failed; ignoring (real decision unaffected).",
                    RoomKind, step.StepOrder);
            }

            // The Gate arm's compensating half — see AttachConveneSystemTwoAnswersAsync. It runs
            // HERE, next to the shadow arm and after the decision is final, because the escalated
            // Gate path is the only one where System Two's answer exists but the gate's own rows
            // were written before it did.
            //
            // Guarded on the ESCALATED path specifically, and the guard is the point rather than a
            // formality: an ACCEPTED outcome means no room ran (there is no System Two answer to
            // attach, and the step advances on the fast model's answer), a NoDecision outcome means
            // the gate wrote no row for this step at all, and a room that degraded to the solo
            // fallback emits an answer this room never synthesized. The method is inert in all of
            // those cases anyway — but asserting the negatives is what proves the arm is not firing
            // where there is nothing to complete.
            //
            // Same never-throws discipline as the shadow arm above, and for the same reason: this is
            // bookkeeping about a decision that has already been made, so it must not be able to
            // change this step's status, output or decision — let alone fail it.
            if (gateEscalated && roomSynthesizedDecision)
            {
                try
                {
                    await AttachConveneSystemTwoAnswersAsync(context, config, decision!);
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(
                        ex,
                        "{RoomKind} step {StepOrder}: convening-gate System Two answer attach failed; " +
                        "ignoring (real decision unaffected).",
                        RoomKind, step.StepOrder);
                }
            }

            return Success(context, sw, config, decision!,
                observedTurnCount, terminationReason, synthesisAttempts, droppedCount, extraRoomMeta,
                degraded, degradeReason, transcriptArtifactStorageKey, chatTranscriptJson, usage,
                viewRoot, policyRetried);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "{RoomKind} step {StepOrder} failed unexpectedly", RoomKind, step.StepOrder);
            return Failure(context, sw, "UNEXPECTED_ERROR", $"Unexpected error: {ex.Message}", usage: usage);
        }
    }

    private StepExecutionResult Success(
        StepExecutionContext context, Stopwatch sw, IRoomStepConfig config, TDecision decision,
        int observedTurnCount, string terminationReason, int synthesisAttempts, int droppedCount, JsonObject? extraRoomMeta,
        bool degraded, string? degradeReason, string? transcriptArtifactStorageKey, string? chatTranscriptJson,
        RoomUsage? usage = null, JsonNode? viewRoot = null, bool policyRetried = false)
    {
        var roomMeta = new JsonObject
        {
            ["seats"] = new JsonArray(config.EffectiveSeats.Select(s => (JsonNode)JsonValue.Create(s.Name)!).ToArray()),
            ["rounds"] = config.Rounds,
            ["maxTurns"] = config.ClampedMaxTurns,
            ["turnCount"] = observedTurnCount,
            ["terminationReason"] = terminationReason,
            ["converged"] = terminationReason == "converged",
            ["synthesisAttempts"] = synthesisAttempts,
            ["droppedSpanCount"] = droppedCount,
            ["degraded"] = degraded,
            ["degradeReason"] = degradeReason,
            ["transcriptArtifactStorageKey"] = transcriptArtifactStorageKey
        };

        if (extraRoomMeta is not null)
        {
            foreach (string key in extraRoomMeta.Select(kv => kv.Key).ToList())
            {
                JsonNode? value = extraRoomMeta[key];
                extraRoomMeta.Remove(key);
                roomMeta[key] = value;
            }
        }

        JsonNode decisionNode = JsonSerializer.SerializeToNode(decision, DecisionJsonOptions)!;
        decisionNode["room"] = roomMeta;

        // Room-specific findings about the FINAL decision (the edit room's interleave verdict), as
        // additive top-level siblings. Never allowed to replace a decision member or "room".
        if (BuildDecisionFindings(decision, viewRoot, context, policyRetried) is { } findings
            && decisionNode is JsonObject decisionObject)
        {
            foreach (string key in findings.Select(kv => kv.Key).ToList())
            {
                JsonNode? value = findings[key];
                findings.Remove(key);
                if (!decisionObject.ContainsKey(key))
                    decisionObject[key] = value;
            }
        }

        return new StepExecutionResult
        {
            Output = decisionNode.ToJsonString(EnvelopeJsonOptions),
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            DurationMs = sw.ElapsedMilliseconds,
            TokensUsed = usage?.Total ?? 0,
            InputTokens = usage?.InputOrNull,
            OutputTokens = usage?.OutputOrNull,
            PrimaryProviderId = usage?.PrimaryProviderId,
            PrimaryModelName = usage?.PrimaryModelName,
            Status = StepStatus.Completed,
            ArtifactStorageKey = transcriptArtifactStorageKey,
            ChatTranscriptJson = chatTranscriptJson
        };
    }

    // ---------------------------------------------------------------------
    // The convening gate, Shadow arm — observations only, never authoritative.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Records one <c>decision_observations</c> row per convened question after synthesis, via
    /// <see cref="IDecisionGate.RunShadowAsync"/>. A no-op unless
    /// <see cref="IRoomStepConfig.ConveneGate"/> is <see cref="ConveneGateMode.Shadow"/>, unless
    /// <see cref="BuildShadowQuestions"/> yields nothing, and unless a gate was injected — so the
    /// <see cref="ConveneGateMode.Off"/> default costs zero work and resolves no Decision provider.
    /// </summary>
    private async Task RunConveneShadowAsync(
        StepExecutionContext context, IRoomStepConfig config, string viewJson, TDecision decision)
    {
        if (config.ConveneGate != ConveneGateMode.Shadow)
            return;

        IReadOnlyList<DecisionGateQuestion>? questions = BuildShadowQuestions(decision);
        if (questions is null || questions.Count == 0)
            return;

        // A null gate is a legal configuration (an unregistered IDecisionGate), not an error.
        if (DecisionGate is null)
            return;

        // Bridged here and ONLY here — ConveneGateMode and DecisionGateMode are two enums with the
        // same three states (see ConveneGateMode's remarks). This arm is Shadow by definition, so
        // the mapping is a constant rather than a translation of config.ConveneGate.
        DecisionGateConfig gateConfig = new(
            Mode: DecisionGateMode.Shadow,
            AcceptAt: config.ConveneAcceptAt,
            MinMargin: config.ConveneMinMargin,
            ProviderId: config.ConveneProviderId);

        // The state is the SAME composed input a solo run of this room would see — the bounded
        // view under the workflow's user request and any prior-iteration retry guidance, byte for
        // byte what RunSoloFallbackAsync hands its solo agent. The room's own decision is
        // deliberately NOT folded in: the gate is then asked the question the room was asked, so
        // its `agreed` outcome means something.
        var state = new DecisionState(context.WithRetryGuidance(context.WithUserRequest(viewJson)));

        // The director's AgentType is the room's own decision-maker type, and it stays HONEST about
        // which surface produced this row: for the grade room that is AgentType.ColorGradeDirector,
        // NOT the solo AgentType.Colorist. The thing this room deliberately SHARES with the solo
        // agent is the questions' Site strings ("Colorist.Look", ...) — the calibration view
        // aggregates decision_observations by (Site, Question) — so a room observation and a solo
        // colorist observation for the same field land on ONE series while each row still says
        // which surface actually produced it.
        await DecisionGate.RunShadowAsync(
            context.Execution.Id, context.Step.StepOrder, DirectorAgentType,
            state, questions, gateConfig, context.CancellationToken);
    }

    // ---------------------------------------------------------------------
    // The convening gate, Gate arm — asks whether the room is needed at all.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Completes the pre-room Gate observation rows once the room HAS run and synthesized the
    /// decision this step emits, via <see cref="IDecisionGate.AttachSystemTwoAnswersAsync"/>.
    ///
    /// <para>
    /// <b>Why this half exists.</b> <c>DecisionGate.DecideAsync</c> writes its
    /// <c>decision_observations</c> rows when it decides, and on the pre-room Gate
    /// path nothing has run yet — so every escalated row it wrote carries no agreement claim at all.
    /// System Two's real answer only exists after the room has synthesized one, and this call is what
    /// fills it in. Without it the calibration view can show a room's escalation rate but never its
    /// agreement rate.
    /// </para>
    ///
    /// <para>
    /// <b>The room persists NOTHING here.</b> This is a targeted UPDATE of rows the gate already
    /// wrote — never an insert — so there is exactly one row per question. A room that also persisted
    /// would write two rows per question and double every room site's counts in the calibration view,
    /// which is the first thing this initiative ships.
    /// </para>
    ///
    /// <para>
    /// <b>The questions come from the SAME seam the Shadow arm uses</b> —
    /// <see cref="BuildShadowQuestions"/> — so the escalated Gate path and the Shadow path record
    /// exactly the same comparison and cannot drift apart. That seam already returns the room's own
    /// synthesized words as <see cref="DecisionGateQuestion.SystemTwoAnswer"/>, so no new plumbing is
    /// needed here. A room that supplies no question set (the edit and graphics rooms, by decision 7)
    /// is therefore a no-op, exactly as it is on the Shadow arm.
    /// </para>
    ///
    /// <para>
    /// <b>The dictionary is keyed by the SHORT name</b> — <see cref="DecisionGateQuestion.Name"/>
    /// ("Look"), never the dotted <c>Site</c> ("Colorist.Look"). <c>DecideAsync</c> persists the
    /// observation's <c>Question</c> column from <c>ToConveneGateQuestion</c>'s short name, and
    /// <c>AttachSystemTwoAnswersAsync</c> matches rows on exactly that column — so a dictionary keyed
    /// by <c>Site</c>, or by the dotted name, would match nothing and silently leave every row null.
    /// That is a no-op rather than an error, which is precisely why it is pinned by a test asserting
    /// the attach keys equal the <c>Name</c> set of the request the Gate arm actually sent.
    /// </para>
    /// </summary>
    private async Task AttachConveneSystemTwoAnswersAsync(
        StepExecutionContext context, IRoomStepConfig config, TDecision decision)
    {
        if (config.ConveneGate != ConveneGateMode.Gate)
            return;

        // A null gate is a legal configuration (an unregistered IDecisionGate), not an error.
        if (DecisionGate is null)
            return;

        IReadOnlyList<DecisionGateQuestion>? questions = BuildShadowQuestions(decision);
        if (questions is null || questions.Count == 0)
            return;

        // Ordinal, and null answers skipped, mirroring AgentStepExecutor's own attach site: a null
        // SystemTwoAnswer means "System Two has no answer for this field", and recording it as an
        // empty-string disagreement would invent an observation that was never made.
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DecisionGateQuestion question in questions)
        {
            if (question.SystemTwoAnswer is not null)
                answers[question.Name] = question.SystemTwoAnswer;
        }

        if (answers.Count == 0)
            return;

        // `agentTokensUsed: null` — the interface's own "leave the stored value alone". The room step
        // DOES report summed usage now (RoomUsage feeds the step result's TokensUsed and input/output
        // split, see Success below), but that sum covers the whole room — every seat turn, synthesis
        // attempt and fallback — not the one counterfactual agent decision this row compares against,
        // so passing it would record a number nobody measured for this question; null keeps the
        // column explicitly unpopulated for rooms. A calibration-view reader must therefore treat a room
        // row's AgentTokensUsed as "not recorded", never as zero.
        await DecisionGate.AttachSystemTwoAnswersAsync(
            context.Execution.Id, context.Step.StepOrder, answers, agentTokensUsed: null, context.CancellationToken);
    }

    /// <summary>
    /// The state the convening Gate transmits to the hosted Decision provider — a CONSTANT,
    /// deliberately, and the substance of the Security <b>high</b> finding this closes
    /// (<c>the decision-engine security review</c>).
    ///
    /// <para>
    /// <b>Why it carries no view content.</b> The room's four grade questions are closed enum words
    /// (Look / Strength / ShadowTone / HighlightTone) and
    /// <c>ColorGradeRoomStepExecutor.BuildConveneQuestions</c> ignores its <c>viewRoot</c> parameter
    /// outright — there is nothing view-dependent to derive. Sending the bounded <c>{view, meta}</c>
    /// envelope here instead would ship <c>segments[].text</c> (the ASR transcript, up to 400 entries
    /// × 160 chars and NOT gated on <c>VisualDetail</c>) and <c>shots[].c</c> (vision-caption prose)
    /// to a third party, which is exactly what the solo Colorist path refuses to do for the same data
    /// and the same question set. The room was less narrow than the solo path it claims equivalence
    /// with.
    /// </para>
    ///
    /// <para>
    /// <b>Why a constant rather than a descriptor projection.</b> A projection exists to keep the
    /// subset of content the questions DO read; these questions read nothing, so the correct
    /// projection is the empty one. A future reader tempted to "improve" this back into a
    /// content-bearing descriptor — or to interpolate anything into it — would re-open a finding
    /// that was closed with this exact shape in mind. Do not.
    /// </para>
    /// </summary>
    private const string ConveneGateStateContent =
        "Answer the room's question set below. Each question lists the options it accepts; pick one option per question.";

    /// <summary>
    /// Asks the decision model whether this room needs to convene, and returns the agenda to open
    /// it with when it does.
    ///
    /// <para>
    /// <b>Two different questions, deliberately not conflated.</b> <c>HasDecision</c> asks whether
    /// the model answered at all — false when no Decision provider resolved, on the gate-level
    /// timeout, and on any failure. <c>Accepted</c> asks whether that answer may be used INSTEAD of
    /// running the room; it is step-level all-or-nothing (decisions.md §4), true only when every
    /// question was answered and every one cleared both <c>AcceptAt</c> and <c>MinMargin</c>, and
    /// therefore never true when <c>HasDecision</c> is false. One <c>!Accepted → convene</c> branch
    /// covers both, but they are different questions and the code below keeps them visibly distinct
    /// — that distinction is the whole reason <see cref="DecisionGateOutcome.NoDecision"/> is a
    /// separate static rather than "an outcome with nothing accepted".
    /// </para>
    ///
    /// <para>
    /// A no-op — "convene", never an error — unless the mode is <see cref="ConveneGateMode.Gate"/>,
    /// a gate is injected, and this room supplies a question set. A room whose seam returns null
    /// (the edit and graphics rooms, by decision 7) therefore keeps exactly today's path even with
    /// the mode set to Gate, and a null gate stays the legal, inert configuration it has always
    /// been.
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>(Accepted, Escalated, Agenda)</c>. <c>Accepted == true</c> means "do not convene", and the
    /// agenda is then always null. Otherwise the room convenes, with the agenda appended to its
    /// opening message when the outcome carried usable answers and null when it did not.
    ///
    /// <para>
    /// <c>Escalated</c> is the third state the two-valued version could not express, and it is what
    /// the answer-attach arm keys on: <c>true</c> only when the gate ANSWERED and did not accept
    /// (<c>HasDecision &amp;&amp; !Accepted</c>). It is deliberately false for every "the gate told us
    /// nothing usable" shape — no provider, a gate-level timeout, a failure, an unrecognised mode, a
    /// room with no question set, a non-<c>Gate</c> mode — because in none of those did the gate write
    /// an observation row for this step, so there is nothing to complete. Collapsing that into
    /// "!Accepted" would make the attach fire on the not-convened path's inverse, where a call would be
    /// meaningless and, worse, unobservable.
    /// </para>
    /// </returns>
    private async Task<(bool Accepted, bool Escalated, string? Agenda)> EvaluateConveneGateAsync(
        StepExecutionContext context, IRoomStepConfig config, JsonNode? viewRoot)
    {
        if (config.ConveneGate != ConveneGateMode.Gate)
            return (false, false, null);

        // A null gate is a legal configuration (an unregistered IDecisionGate), not an error.
        if (DecisionGate is null)
            return (false, false, null);

        IReadOnlyList<DecisionQuestion>? questions = BuildConveneQuestions(viewRoot);
        if (questions is null || questions.Count == 0)
            return (false, false, null);

        DecisionGateConfig gateConfig = new(
            Mode: DecisionGateMode.Gate,
            AcceptAt: config.ConveneAcceptAt,
            MinMargin: config.ConveneMinMargin,
            ProviderId: config.ConveneProviderId);

        // TimeoutSeconds is deliberately left at DecisionGateConfig's own default: the four
        // Convene* members are the whole config surface decision 7 and the plan define for a room,
        // and the gate-level deadline is D-004's to own — not a knob a per-step room config gets a
        // second, competing spelling of.

        // The projection below is not cosmetic — see ToConveneGateQuestion.
        var gateQuestions = questions.Select(ToConveneGateQuestion).ToList();

        // THE STATE IS A CONSTANT — this is the FIX, not a stylistic choice. This state leaves the
        // host: it is transmitted verbatim to a hosted Decision provider. It used to be `viewJson`,
        // the ENTIRE upstream {view, meta} envelope, which VideoAnalyzeStepExecutor fills with
        // `segments[].text` (ASR transcript) and `shots[].c` (vision-caption prose) — media-derived
        // prose that the solo Colorist path deliberately keeps at home for the very same questions.
        // See ConveneGateStateContent for why the correct narrow projection here is the empty one,
        // and why it must not be widened back into a content-bearing descriptor.
        //
        // It is likewise deliberately NOT the workflow's user request, NOT the prior-step output
        // history, and NOT a BuildAgentInput()-equivalent text. (The Shadow arm keeps sending the
        // full composed input, for phase-1 byte-identical behaviour — the two arms' states differ
        // ON PURPOSE.)
        //
        // `SystemTwoAnswer: null` and no `AgentTokensUsed` for the same reason: nothing has run
        // yet, and null is precisely DecisionGateQuestion's "no counterfactual exists" value. It is
        // not "the agent answered nothing" — the observation this writes claims no agreement at
        // all, rather than inventing a disagreement that was never observed.
        var request = new DecisionGateRequest(
            ExecutionId: context.Execution.Id,
            StepOrder: context.Step.StepOrder,
            AgentType: DirectorAgentType,
            Site: $"{RoomKind}.convene",
            State: new DecisionState(ConveneGateStateContent),
            Questions: gateQuestions,
            Config: gateConfig);

        DecisionGateOutcome outcome = await DecisionGate.DecideAsync(request, context.CancellationToken);

        // Question one: did the model answer at all? A false here means the gate is inert — no
        // provider resolved, the gate-level deadline fired, the call failed — and the room
        // convenes, exactly as it does with the gate Off. Not "escalated": nothing was written.
        if (!outcome.HasDecision)
            return (false, false, null);

        // Question two: may that answer be used INSTEAD of running the room? Accepted is
        // all-or-nothing, so there is no partially-accepted state to handle here by construction:
        // either the fast model's answer stands for the whole step and the room is skipped, or the
        // room convenes with the disagreement as its agenda.
        if (outcome.Accepted)
            return (true, false, null);

        // Escalated. The agenda names the splits the room has to settle first; it is null when
        // nothing qualified (an all-confident answer set, or one without usable probabilities), in
        // which case the opening message stays exactly what it was.
        string? agenda = outcome.Result is null
            ? null
            : BuildConveneAgenda(viewRoot, outcome.Result.Answers);

        return (false, true, agenda);
    }

    /// <summary>
    /// Projects one WIRE question (<see cref="DecisionQuestion"/> — what a decision model is asked)
    /// into its GATE shape (<see cref="DecisionGateQuestion"/> — the wire question plus the
    /// observation's <c>Site</c> and System Two's answer).
    ///
    /// <para>
    /// The two shapes disagree about where the call site lives: <see cref="DecisionQuestion"/> has
    /// no <c>Site</c> field at all, while a <c>decision_observations</c> row records <c>Site</c>
    /// and <c>Question</c> as two separate columns. A room's pre-room question set carries the site
    /// inside the NAME — the grade room's questions are the solo colorist's own site strings,
    /// <c>"Colorist.Look"</c> and friends — so the name is split back apart here:
    /// <c>Site = "Colorist.Look"</c>, <c>Name = "Look"</c>.
    /// </para>
    ///
    /// <para>
    /// That split is load-bearing, not tidiness. <c>DecisionGate.ExecuteAsync</c> writes
    /// <c>Site</c> from <see cref="DecisionGateQuestion.Site"/> and looks the answer up by
    /// <see cref="DecisionGateQuestion.Name"/>, and the calibration view aggregates by the
    /// <c>(Site, Question)</c> pair — so recovering the last dot-segment keeps the Gate path's two
    /// columns IDENTICAL to the Shadow path's and to the solo agent's. Sharing that namespace is
    /// the entire point (decisions.md §7); a Gate row that landed on a different series would
    /// silently halve the data the feature exists to collect.
    /// </para>
    /// </summary>
    private static DecisionGateQuestion ToConveneGateQuestion(DecisionQuestion question)
    {
        int lastDot = question.Name.LastIndexOf('.');

        // No dot (or a trailing one) means the name IS the site. Kept general rather than assumed:
        // a future room with a flat question name then still records a coherent row instead of an
        // empty Site or a name no answer can ever be looked up under.
        string name = lastDot >= 0 && lastDot + 1 < question.Name.Length
            ? question.Name[(lastDot + 1)..]
            : question.Name;

        return new DecisionGateQuestion(
            Site: question.Name,
            Name: name,
            Kind: question.Kind,
            Instructions: question.Instructions,
            Options: question.Options,
            SystemTwoAnswer: null);
    }

    // ---------------------------------------------------------------------
    // The group chat run
    // ---------------------------------------------------------------------

    /// <summary>
    /// Sums model usage across every call a room step makes — each seat/director turn, every
    /// synthesis attempt and the solo fallback — so the step result reports real tokens instead of
    /// 0. Null counts (a provider that reports no usage) are never turned into fabricated zeros:
    /// the step reports null input/output unless at least one call reported something.
    /// </summary>
    internal sealed class RoomUsage
    {
        private int _input;
        private int _output;
        private bool _observed;

        public void Add(int? input, int? output)
        {
            if (!input.HasValue && !output.HasValue)
                return;
            _observed = true;
            _input += input ?? 0;
            _output += output ?? 0;
        }

        /// <summary>
        /// Folds in a whole agent call and remembers the first call's provider as the step's primary one
        /// (the director's synthesis, or the solo fallback — the calls whose provider is the room's).
        /// </summary>
        public void Add(AgentRunResult result)
        {
            Add(result.InputTokens, result.OutputTokens);
            if (PrimaryModelName is null && result.PrimaryModelName is not null)
            {
                PrimaryProviderId = result.PrimaryProviderId;
                PrimaryModelName = result.PrimaryModelName;
            }
        }

        public Guid? PrimaryProviderId { get; private set; }
        public string? PrimaryModelName { get; private set; }
        public int Total => _input + _output;
        public int? InputOrNull => _observed ? _input : null;
        public int? OutputOrNull => _observed ? _output : null;
    }

    private sealed record RoomRunResult(
        IReadOnlyList<ChatMessage> Transcript, string TerminationReason, IReadOnlyList<RoomTurnResult> Turns);

    private async Task<RoomRunResult> RunRoomAsync(
        StepExecutionContext context, IRoomStepConfig config, string viewJson, HashSet<string> offeredIds,
        string? conveneAgenda = null, RoomUsage? usage = null)
    {
        CancellationToken ct = context.CancellationToken;

        List<EditRoomSeat> seats = config.EffectiveSeats.ToList();

        // Resolved ONCE per room run, before any participant is built and before CreateManager —
        // never per turn. Off (the default) short-circuits before touching the resolver at all, so
        // a room that does not opt in performs exactly the I/O it always did (none). Every failure
        // mode — no provider configured, a resolver throw, a factory throw — degrades to null with
        // a warning; a decision provider that is missing or broken must never fail, shorten or
        // reorder a room.
        IDecisionClient? decisionClient = await ResolveDecisionClientAsync(config, ct);
        int turnCounter = 0;

        // Running cumulative token tally across every turn observed so far in this room — real
        // mid-run visibility (see RoomSeatAgent.ExtractUsage), never estimated. Stays null
        // (reported as such on WorkflowStepProgress) until at least one turn actually reports usage,
        // since a provider that never reports usage must not be misrepresented as "0 tokens used".
        int cumulativeInputTokens = 0;
        int cumulativeOutputTokens = 0;
        bool anyUsageObserved = false;

        // Captured for EVERY turn (regardless of config.StreamTurns, which only gates the live SSE
        // broadcast) so the DB-persisted ChatTranscriptJson can be built from this — the seat's/
        // director's ACTUAL persona name, not the raw group-chat transcript's ChatMessage.AuthorName,
        // which carries the underlying model agent's name (e.g. "VideoStoryEditor") rather than the
        // seat's display name, since RoomSeatAgent only overrides AuthorName on its fallback
        // path, not on a normal successful turn. Using this list instead of the raw transcript keeps
        // the persisted history attributed exactly the way a live SSE-connected tab already saw it.
        var capturedTurns = new List<RoomTurnResult>();

        async Task OnTurnCompleted(RoomTurnResult result)
        {
            int idx = turnCounter++;
            bool isDirector = string.Equals(result.SeatName, DirectorSeatName, StringComparison.Ordinal);
            string speakerRole = isDirector ? "director" : "editor";
            string label = BuildTurnProgressLabel(isDirector, result.SeatName, idx + 1, config.ClampedMaxTurns);
            int percent = (int)Math.Clamp(Math.Round(100.0 * (idx + 1) / Math.Max(1, config.ClampedMaxTurns)), 0, 100);

            capturedTurns.Add(result);
            usage?.Add(result.InputTokens, result.OutputTokens);

            if (result.InputTokens.HasValue || result.OutputTokens.HasValue)
            {
                anyUsageObserved = true;
                cumulativeInputTokens += result.InputTokens ?? 0;
                cumulativeOutputTokens += result.OutputTokens ?? 0;
            }

            await context.ReportProgressAsync(
                label,
                percent,
                tokensUsedSoFar: anyUsageObserved ? cumulativeInputTokens + cumulativeOutputTokens : null,
                inputTokensSoFar: anyUsageObserved ? cumulativeInputTokens : null,
                outputTokensSoFar: anyUsageObserved ? cumulativeOutputTokens : null);

            if (config.StreamTurns)
            {
                IReadOnlyList<string> ids = ExtractIdMentions(result.Text, offeredIds);
                await context.ReportChatTurnAsync(idx, config.ClampedMaxTurns, result.SeatName, speakerRole, result.Text, ids);
            }
        }

        // EVERY participant — each seat and the director alike — is registered
        // RoomGroupChatManager.ParticipantAliasCount times, as that many SEPARATE inner-agent
        // instances. See RoomGroupChatManager's constructor remarks for the full reasoning; the
        // short version is that Microsoft.Agents.AI.Workflows 1.22.0's group-chat host refuses to
        // re-invoke the SAME registered participant on two consecutive turns (it silently ends the
        // room instead), and the scheduler repeats a participant in two places: the director speaks
        // every remaining turn after the round-robin phase, and a SINGLE-seat room repeats that one
        // seat across rounds. Aliasing every participant lets SelectNextAgentAsync guarantee
        // "never the same registration twice in a row" arithmetically, for any schedule, instead of
        // patching whichever phase happens to repeat today.
        //
        // Building INDEPENDENT inner agents per alias (rather than one inner agent wrapped by two
        // RoomSeatAgent instances) is load-bearing, not cosmetic: AIAgent.Id defaults to a fresh
        // random GUID per instance, but DelegatingAIAgent (what RoomSeatAgent is) forwards IdCore
        // to InnerAgent.Id, so two wrappers around the SAME inner agent would carry the IDENTICAL
        // Id. AgentWorkflowBuilder derives each participant's internal executor-binding id from
        // Name + Id, and a participant's aliases must keep the SAME Name (the transcript, sentinel
        // and convergence checks all key off it), so a shared inner agent collides and
        // GroupChatWorkflowBuilder.Build() throws "Cannot bind executor with ID '...' because an
        // executor with the same ID but different instance is already bound." (confirmed by
        // decompiling and exercising the real 1.22.0 assembly). Separate BuildInnerAgentAsync calls
        // give each alias its own random Id while resolving through the identical chat
        // client/instructions/tools — functionally still one participant, just two object
        // instances. The extra calls are cheap: the chat client itself is cached by
        // ChatClientFactory, so an alias costs an agent wrapper, not a connection or a round trip.
        List<AIAgent> allParticipants = new((seats.Count + 1) * RoomGroupChatManager.ParticipantAliasCount);
        foreach (EditRoomSeat seat in seats)
        {
            for (int alias = 0; alias < RoomGroupChatManager.ParticipantAliasCount; alias++)
            {
                AIAgent inner = await BuildInnerAgentAsync(SeatAgentType, seat.AgentDefinitionId, ct);
                allParticipants.Add(new RoomSeatAgent(
                    inner, seat.Name, BuildSeatDirective(seat), config.Temperature, Math.Max(16, config.MaxTurnTokens),
                    config.ReasoningEffort, OnTurnCompleted));
            }
        }

        // The director's own aliases, same rule and same reasoning as the seats above.
        for (int alias = 0; alias < RoomGroupChatManager.ParticipantAliasCount; alias++)
        {
            AIAgent directorInner = await BuildInnerAgentAsync(DirectorAgentType, config.DirectorAgentDefinitionId, ct);
            allParticipants.Add(new RoomSeatAgent(
                directorInner, DirectorSeatName, DirectorTurnDirective, config.DirectorTemperature,
                Math.Max(16, config.MaxTurnTokens), config.ReasoningEffort, OnTurnCompleted));
        }

        HashSet<string> offeredIdsReadOnly = offeredIds;
        RoomGroupChatManager? capturedManager = null;
        Workflow workflow = AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(agents =>
            {
                RoomGroupChatManager manager = CreateManager(agents, config, offeredIdsReadOnly, decisionClient);
                manager.MaximumIterationCount = config.ClampedMaxTurns;
                capturedManager = manager;
                return manager;
            })
            .AddParticipants(allParticipants)
            .Build();

        // The user request AND any prior-iteration review feedback are appended HERE rather than
        // inside BuildOpeningMessage so a room that overrides the opening wording cannot silently
        // drop the workflow's creative direction, or the reason a previous cut was rejected — the
        // same failure twice. See StepExecutionContext.WithUserRequest/WithRetryGuidance.
        //
        // The convening agenda joins them for exactly the same reason, and is appended LAST,
        // outside both calls: it is the gate's own answer set re-derived on every attempt, so a
        // RETRIED room must be handed the same agenda again — folding it into BuildOpeningMessage
        // would put it behind WithRetryGuidance (ordering it before the retry feedback a caller
        // expects to read last) and inside the override a room can replace. Null/whitespace is a
        // strict no-op: no separator, no blank line, byte for byte the pre-agenda message.
        string openingMessage = context.WithRetryGuidance(
            context.WithUserRequest(BuildOpeningMessage(viewJson)));

        if (!string.IsNullOrWhiteSpace(conveneAgenda))
            openingMessage = $"{openingMessage}\n\n{conveneAgenda}";

        using CancellationTokenSource roomCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Clamped at BOTH ends. The lower end was already guarded, but the upper end was not, and
        // RoomTimeoutSeconds is read straight out of the step's client-supplied config JSON. A value
        // above MaxRoomTimeoutSeconds makes CancelAfter throw ArgumentOutOfRangeException, which the
        // room's own never-throws handling catches and turns into a DEGRADE — so an absurd number
        // does not merely set a long deadline, it discards the room and hands the step to the solo
        // fallback, which fails outright when that agent is not registered. A number a caller should
        // never have sent must not be able to fail a step; clamping keeps the room running instead.
        roomCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.RoomTimeoutSeconds, 1, MaxRoomTimeoutSeconds)));
        CancellationToken roomCt = roomCts.Token;

        await context.ReportProgressAsync($"Starting {RoomDisplayName.ToLowerInvariant()}", 0);

        List<ChatMessage>? transcript = null;
        string terminationReason = "ceiling";

        try
        {
            await using StreamingRun run = await InProcessExecution.RunStreamingAsync(
                workflow, openingMessage, context.StepResultId.ToString("D"), roomCt);

            // The opening message is only BUFFERED by GroupChatHost (a ChatProtocolExecutor with
            // AutoSendTurnToken=false, confirmed by decompiling the real rc2 assembly) — a TurnToken
            // must be sent explicitly to actually kick off the first turn-selection cycle. Without
            // this the run produces zero agent turns and never emits a WorkflowOutputEvent.
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            await foreach (WorkflowEvent evt in run.WatchStreamAsync(roomCt))
            {
                if (evt is WorkflowOutputEvent outputEvent && outputEvent.Is<List<ChatMessage>>())
                {
                    transcript = outputEvent.As<List<ChatMessage>>();
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // RoomTimeoutSeconds fired, not the caller's own token — degrade to whatever turns
            // already happened rather than propagating.
            terminationReason = "timeout";
        }

        // RoomGroupChatManager.TerminationReason is set only for an early "sentinel"/"converged"
        // stop; a still-null value here means the room ran to the MaxTurns ceiling instead (the
        // "ceiling" default already assigned above), unless the timeout branch above already
        // overrode it.
        if (terminationReason == "ceiling" && capturedManager?.TerminationReason is { } managerReason)
            terminationReason = managerReason;

        return new RoomRunResult(transcript ?? [], terminationReason, capturedTurns);
    }

    /// <summary>
    /// Resolves this run's advisory decision client — the exact pattern
    /// <see cref="Decision.DecisionGate"/> uses (resolver, then client factory, both keyed on the
    /// Decision capability) — or null when the room did not opt in, no provider is configured, or
    /// anything at all went wrong. Resolution happens ONCE per room run and never per turn.
    /// </summary>
    private async Task<IDecisionClient?> ResolveDecisionClientAsync(IRoomStepConfig config, CancellationToken ct)
    {
        if (config.DecisionScheduling == RoomDecisionMode.Off)
            return null;

        if (DecisionResolver is null || DecisionClientFactory is null)
        {
            Logger.LogWarning(
                "{RoomKind} requested DecisionScheduling={Mode} but no decision provider resolver is available; " +
                "running the room without decision questions",
                RoomKind,
                config.DecisionScheduling);
            return null;
        }

        try
        {
            ResolvedDecisionProvider? provider = await DecisionResolver.ResolveDecisionAsync(null, ct);
            if (provider is null)
            {
                Logger.LogWarning(
                    "{RoomKind} requested DecisionScheduling={Mode} but no Decision provider is configured; " +
                    "running the room without decision questions",
                    RoomKind,
                    config.DecisionScheduling);
                return null;
            }

            return DecisionClientFactory.Get(provider);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(
                ex,
                "{RoomKind} could not resolve a decision client for DecisionScheduling={Mode}; " +
                "running the room without decision questions",
                RoomKind,
                config.DecisionScheduling);
            return null;
        }
    }

    /// <summary>
    /// Extra room wording a run's style asks for (the edit room's dynamics), appended to a seat's charter and to the director's synthesis
    /// request; null (the default, and always for a run without it) leaves every prompt byte-identical.
    /// </summary>
    protected virtual string? RoomRunNudge(AgentType agentType) => null;

    private async Task<AIAgent> BuildInnerAgentAsync(AgentType agentType, Guid? agentDefinitionId, CancellationToken ct)
    {
        IChatClient chatClient = await ChatClients.GetAsync(agentType, agentDefinitionId, ct);
        IReadOnlyList<AIFunction> tools = GetRoomTurnTools(agentType);
        // The run's editing style reaches every room participant too (docs/editing-styles.md):
        // the charter is unchanged when the run has no style. The pinned design and product
        // profile (docs/design-system.md) follow the style in that order.
        // The dailies room's judges are also told which project subjects the takes show (docs/design/avatar-objects.md).
        return chatClient.AsAIAgent(
            instructions: Services.Avatars.ProjectSubjectsAmbient.Decorate(
                DesignSystemAmbient.Decorate(
                    Services.Styles.StyleNudgeService.Append(
                        RunStyleAmbient.Decorate(RunBriefAmbient.Decorate(RoomCharterPrompt, agentType), agentType),
                        RoomRunNudge(agentType)),
                    agentType),
                agentType),
            name: agentType.ToString(),
            tools: tools.Cast<AITool>().ToList());
    }

    // ---------------------------------------------------------------------
    // Synthesis — a normal ReelBoltAgentBase-style structured-output call, OUTSIDE the group chat
    // ---------------------------------------------------------------------

    private async Task<(TDecision? Decision, int Attempts, string? Error, bool PolicyRetried)> SynthesizeDecisionAsync(
        StepExecutionContext context, IRoomStepConfig config, string viewJson, IReadOnlyList<ChatMessage> transcript,
        JsonNode? viewRoot, HashSet<string> offeredIds, RoomUsage usage)
    {
        IReelBoltAgent? director = AgentRegistry.GetByType(DirectorAgentType, config.DirectorAgentDefinitionId);
        if (director is null)
            return (null, 0, $"AgentType.{DirectorAgentType} is not registered.", false);

        string transcriptText = RenderTranscript(transcript, config.MaxHistoryChars);
        string basePrompt =
            $"The {RoomDisplayName.ToLowerInvariant()} discussion has ended. Here is the bounded analysis view again, followed " +
            "by the full room transcript. Synthesize the room's discussion into the FINAL decision " +
            $"now — {SynthesisSchemaInstruction}\n\n" +
            "## Bounded view\n\n" + viewJson + "\n\n## Room transcript\n\n" + transcriptText;
        basePrompt = context.WithRetryGuidance(context.WithUserRequest(basePrompt));
        if (RoomRunNudge(DirectorAgentType) is { } roomNudge)
            basePrompt += "\n\n" + roomNudge;

        int maxAttempts = Math.Max(1, config.MaxSynthesisAttempts);
        string? lastError = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            string prompt = attempt == 1
                ? basePrompt
                : $"{basePrompt}\n\n---\nRetry guidance (attempt {attempt}): {lastError}\nReturn output that strictly matches the expected JSON schema and is valid JSON.";

            AgentRunResult result;
            try
            {
                result = await director.RunAsync(prompt, config.DirectorAgentDefinitionId, context.CancellationToken);
                usage.Add(result);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                continue;
            }

            if (!result.Success)
            {
                lastError = result.FailureReason ?? "The director invoked FailWorkflow.";
                continue;
            }

            string? json = RobustJsonExtractor.ExtractJsonObject(result.Output);
            if (json is null)
            {
                lastError = "No recognizable JSON object in the director's output.";
                continue;
            }

            TDecision? decision;
            try
            {
                decision = JsonSerializer.Deserialize<TDecision>(json, DecisionJsonOptions);
            }
            catch (JsonException ex)
            {
                lastError = ex.Message;
                continue;
            }

            if (decision is null)
            {
                lastError = "Decision deserialized to null.";
                continue;
            }

            NormalizeDecision(decision);
            string? rejection = RejectFreshSynthesis(decision);
            if (rejection is not null)
            {
                lastError = rejection;
                continue;
            }

            string? retryIssue = CheckRetryableIssue(decision, viewRoot);
            if (retryIssue is not null && attempt < maxAttempts)
            {
                lastError = retryIssue;
                continue;
            }

            (TDecision policyDecision, bool policyRetried) = await ApplyPolicyRetryAsync(
                context, director, config.DirectorAgentDefinitionId, basePrompt, decision,
                offeredIds, viewRoot, transcript, solo: false, usage);
            return (policyDecision, policyRetried ? attempt + 1 : attempt, null, policyRetried);
        }

        return (null, maxAttempts, lastError ?? "Synthesis failed for an unknown reason.", false);
    }

    /// <summary>
    /// The one dedicated retry <see cref="CheckDecisionPolicy"/> buys. Returns the decision to use and
    /// whether the retry was spent. The retried answer wins only when it parses, passes
    /// <see cref="RejectFreshSynthesis"/>, still has something left after <see cref="FilterToOfferedIds"/>
    /// (checked on a copy — this method never mutates the decision it returns on the synthesis path),
    /// AND passes the policy; anything else keeps <paramref name="decision"/>, so a policy retry can
    /// never turn a usable decision into a degrade. Never throws except on the caller's own cancellation.
    /// </summary>
    private async Task<(TDecision Decision, bool Retried)> ApplyPolicyRetryAsync(
        StepExecutionContext context, IReelBoltAgent agent, Guid? agentDefinitionId, string basePrompt,
        TDecision decision, HashSet<string> offeredIds, JsonNode? viewRoot, IReadOnlyList<ChatMessage>? transcript,
        bool solo, RoomUsage usage)
    {
        string? policy;
        try
        {
            policy = CheckDecisionPolicy(decision, viewRoot, transcript, context);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{RoomKind}: decision policy check failed; accepting the decision as-is.", RoomKind);
            return (decision, false);
        }

        if (policy is null)
            return (decision, false);

        string prompt =
            $"{basePrompt}\n\n---\nPolicy check (one retry): {policy}\n" +
            "Return output that strictly matches the expected JSON schema and is valid JSON.";

        try
        {
            AgentRunResult result = await agent.RunAsync(prompt, agentDefinitionId, context.CancellationToken);
            usage.Add(result);
            if (!result.Success)
                return (decision, true);

            string? json = RobustJsonExtractor.ExtractJsonObject(result.Output);
            if (json is null)
                return (decision, true);

            TDecision? candidate = JsonSerializer.Deserialize<TDecision>(json, DecisionJsonOptions);
            if (candidate is null)
                return (decision, true);

            NormalizeDecision(candidate);
            if (RejectFreshSynthesis(candidate) is not null)
                return (decision, true);

            TDecision? probe = JsonSerializer.Deserialize<TDecision>(json, DecisionJsonOptions);
            if (probe is null)
                return (decision, true);
            NormalizeDecision(probe);
            FilterToOfferedIds(probe, offeredIds, viewRoot);
            if (RejectFilteredDecision(probe, solo) is not null)
                return (decision, true);

            if (CheckDecisionPolicy(candidate, viewRoot, transcript, context) is not null)
                return (decision, true);

            return (candidate, true);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{RoomKind}: decision policy retry failed; keeping the original decision.", RoomKind);
            return (decision, true);
        }
    }

    private static string RenderTranscript(IReadOnlyList<ChatMessage> transcript, int maxChars)
    {
        var sb = new StringBuilder();
        foreach (ChatMessage m in transcript)
        {
            string speaker = string.IsNullOrEmpty(m.AuthorName) ? m.Role.ToString() : m.AuthorName;
            sb.Append(speaker).Append(": ").Append(m.Text ?? string.Empty).Append('\n');
        }

        string full = sb.ToString();
        if (maxChars <= 0 || full.Length <= maxChars)
            return full;

        // Keep the TAIL (most recent turns) — synthesis cares most about where the room actually
        // ended up, not how it opened.
        return "...[earlier turns omitted]...\n" + full[^Math.Max(0, maxChars - 32)..];
    }

    // ---------------------------------------------------------------------
    // Solo fallback — the pre-room single-agent pipeline, unchanged
    // ---------------------------------------------------------------------

    private async Task<(TDecision? Decision, RoomFilterResult Filter, string? Error, bool PolicyRetried)> RunSoloFallbackAsync(
        StepExecutionContext context, string viewJson, HashSet<string> offeredIds, JsonNode? viewRoot, RoomUsage usage,
        IReadOnlyList<ChatMessage>? transcript)
    {
        (TDecision? decision, RoomFilterResult filter, string? error) =
            await RunSoloFallbackCoreAsync(context, viewJson, offeredIds, viewRoot, usage);
        if (decision is null)
            return (null, filter, error, false);

        IReelBoltAgent? soloAgent = AgentRegistry.GetByType(SoloFallbackAgentType, null);
        if (soloAgent is null)
            return (decision, filter, null, false);

        (TDecision chosen, bool retried) = await ApplyPolicyRetryAsync(
            context, soloAgent, null, context.WithRetryGuidance(context.WithUserRequest(viewJson)), decision,
            offeredIds, viewRoot, transcript, solo: true, usage);

        if (!ReferenceEquals(chosen, decision))
        {
            // The retried answer replaced the decision: it has not been through the filter yet.
            filter = FilterToOfferedIds(chosen, offeredIds, viewRoot);
        }

        return (chosen, filter, null, retried);
    }

    private async Task<(TDecision? Decision, RoomFilterResult Filter, string? Error)> RunSoloFallbackCoreAsync(
        StepExecutionContext context, string viewJson, HashSet<string> offeredIds, JsonNode? viewRoot, RoomUsage usage)
    {
        IReelBoltAgent? soloAgent = AgentRegistry.GetByType(SoloFallbackAgentType, null);
        if (soloAgent is null)
            return (null, default, $"AgentType.{SoloFallbackAgentType} is not registered for the solo fallback.");

        AgentRunResult result;
        try
        {
            result = await soloAgent.RunAsync(
                context.WithRetryGuidance(context.WithUserRequest(viewJson)), null, context.CancellationToken);
            usage.Add(result);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, default, $"Solo fallback failed: {ex.Message}");
        }

        if (!result.Success)
            return (null, default, result.FailureReason ?? "The solo fallback agent invoked FailWorkflow.");

        string? json = RobustJsonExtractor.ExtractJsonObject(result.Output);
        if (json is null)
            return (null, default, "Solo fallback output had no recognizable JSON object.");

        TDecision? decision;
        try
        {
            decision = JsonSerializer.Deserialize<TDecision>(json, DecisionJsonOptions);
        }
        catch (JsonException ex)
        {
            return (null, default, $"Solo fallback output was not valid JSON: {ex.Message}");
        }

        if (decision is null)
            return (null, default, "Solo fallback output deserialized to null.");

        NormalizeDecision(decision);
        RoomFilterResult filterResult = FilterToOfferedIds(decision, offeredIds, viewRoot);
        string? rejection = RejectFilteredDecision(decision, solo: true);
        if (rejection is not null)
            return (null, filterResult, rejection);

        return (decision, filterResult, null);
    }

    // ---------------------------------------------------------------------
    // View resolution — the same Previous/Step pattern VideoCompileStepExecutor.ResolveDecisionJson
    // established for Decision/GraphicsPlan/MusicPlan.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Resolves an <see cref="ExtractInputRef"/> (<c>Previous</c>/<c>Step</c> only) to the raw JSON
    /// output of that step. Reads through <see cref="StepExecutionContext.OutputForPrompt"/> so a
    /// prompt-output override installed by <see cref="PrepareViewAsync"/> (the graphics room's
    /// inEdit annotation) is honored; for rooms that install none, this is byte-identical to
    /// reading the entry's own output.
    /// </summary>
    private static (string? Json, string? Error) ResolveViewJson(StepExecutionContext context, ExtractInputRef viewRef)
    {
        StepOutputHistoryEntry? entry = viewRef.From switch
        {
            ExtractInputSource.Previous => context.StepOutputHistory
                .LastOrDefault(h => !string.IsNullOrWhiteSpace(h.Output)),
            ExtractInputSource.Step => viewRef.StepOrder.HasValue
                ? context.StepOutputHistory.LastOrDefault(h => h.StepOrder == viewRef.StepOrder.Value)
                : null,
            _ => null
        };

        string? content = entry is null ? null : context.OutputForPrompt(entry);

        if (string.IsNullOrWhiteSpace(content))
            return (null, "View input resolved to empty content.");

        string? extracted = RobustJsonExtractor.ExtractJsonObject(content);
        return extracted is null ? (null, "View input did not contain a recognizable JSON object.") : (extracted, null);
    }

    // ---------------------------------------------------------------------
    // Transcript artifact — NON-AUTHORITATIVE. Free-form turn text may contain a model-invented
    // timestamp as harmless prose; nothing must ever parse it back into a decision. See
    // docs/video-editing.md "The edit room".
    // ---------------------------------------------------------------------

    private async Task<string?> TryPersistTranscriptAsync(
        StepExecutionContext context, IReadOnlyList<ChatMessage> transcript, int stepOrder)
    {
        try
        {
            var turns = transcript.Select(m => new
            {
                speaker = string.IsNullOrEmpty(m.AuthorName) ? m.Role.ToString() : m.AuthorName,
                role = string.Equals(m.AuthorName, DirectorSeatName, StringComparison.Ordinal) ? "director" : "editor",
                text = m.Text ?? string.Empty
            }).ToList();

            string json = JsonSerializer.Serialize(new
            {
                nonAuthoritative = true,
                note = "Free-form room discussion, for audit/UI display only. Never parse this back into a decision — the synthesized, deterministically-validated decision on this step's output_json is the only authoritative result.",
                turns
            }, EnvelopeJsonOptions);

            string tempPath = Path.Combine(Path.GetTempPath(), $"room-transcript-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(tempPath, json, context.CancellationToken);
            try
            {
                string fileName = $"video-analysis/{context.Execution.Id:D}/step-{stepOrder}-room-transcript.json";
                return await Workspace.UploadArtifactAsync(
                    context.Execution.ProjectId, tempPath, fileName, "application/json", context.CancellationToken);
            }
            finally
            {
                try { File.Delete(tempPath); } catch { /* best-effort scratch cleanup */ }
            }
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "{RoomKind} step {StepOrder}: persisting transcript artifact failed; continuing without it.", RoomKind, stepOrder);
            return null;
        }
    }

    private StepExecutionResult Failure(
        StepExecutionContext context, Stopwatch sw, string code, string message,
        string? artifactStorageKey = null, string? chatTranscriptJson = null, RoomUsage? usage = null)
    {
        Logger.LogWarning("{RoomKind} step {StepOrder} failed: [{Code}] {Message}", RoomKind, context.Step.StepOrder, code, message);

        var envelope = new JsonObject
        {
            ["status"] = "failed",
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };

        return new StepExecutionResult
        {
            Output = envelope.ToJsonString(EnvelopeJsonOptions),
            NextStepIndex = context.CurrentStepIndex + 1,
            NewIterationCount = context.IterationCount,
            DurationMs = sw.ElapsedMilliseconds,
            TokensUsed = usage?.Total ?? 0,
            InputTokens = usage?.InputOrNull,
            OutputTokens = usage?.OutputOrNull,
            PrimaryProviderId = usage?.PrimaryProviderId,
            PrimaryModelName = usage?.PrimaryModelName,
            Status = StepStatus.Failed,
            ErrorDetails = message,
            ArtifactStorageKey = artifactStorageKey,
            ChatTranscriptJson = chatTranscriptJson
        };
    }

    /// <summary>
    /// Builds the DB-persisted transcript array for <see cref="StepExecutionResult.ChatTranscriptJson"/>
    /// — per-turn shape mirrors <see cref="Shared.IntegrationEvents.WorkflowStepChatTurn"/> (turnIndex,
    /// speaker, speakerRole, text, truncated, idsMentioned, totalTurns) but always carries the FULL
    /// untruncated turn text, since this is DB persistence rather than the ~600-char-truncated SSE
    /// broadcast <see cref="WorkflowEventPublisher.ClipChatTurn"/> produces — so <c>truncated</c> is
    /// always <c>false</c> here. Built from the SAME <see cref="RoomTurnResult"/> list the live
    /// <c>WorkflowStepChatTurn</c> SSE events are reported from (<c>RunRoomAsync</c>'s captured
    /// turns), NOT the raw group-chat <c>ChatMessage</c> transcript — that transcript's
    /// <c>AuthorName</c> carries the underlying model agent's name (e.g. "VideoStoryEditor"), not the
    /// seat's persona display name, on a normal successful turn. Using the same source as the live
    /// events keeps the persisted history attributed exactly the way a live SSE-connected tab saw it.
    /// The <c>speakerRole</c> values stay the room-generic pair "editor"/"director" for every room —
    /// they are a seat/moderator dichotomy the UI styles by, not a job title. Returns <c>null</c>
    /// for zero turns.
    /// </summary>
    private string? BuildChatTranscriptJson(
        IReadOnlyList<RoomTurnResult> turns, HashSet<string> offeredIds, int totalTurns)
    {
        if (turns.Count == 0)
            return null;

        var persisted = turns.Select((t, index) => new
        {
            turnIndex = index,
            totalTurns,
            speaker = t.SeatName,
            speakerRole = string.Equals(t.SeatName, DirectorSeatName, StringComparison.Ordinal) ? "director" : "editor",
            text = t.Text,
            truncated = false,
            idsMentioned = ExtractIdMentions(t.Text, offeredIds)
        }).ToList();

        return JsonSerializer.Serialize(persisted, EnvelopeJsonOptions);
    }
}
