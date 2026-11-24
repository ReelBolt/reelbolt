using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Agents.Rooms;

/// <summary>
/// Deterministic scheduler/terminator shared by every multi-agent "room" step type's
/// <c>Microsoft.Agents.AI.Workflows</c> group chat. Not LLM-driven — every seat sees the exact
/// same bounded view and can speak to any part of it, so an intelligent "who should speak next"
/// decision would only double a room's LLM-call cost for no benefit. A concrete room supplies
/// only its offered-id vocabulary (the regex that recognizes its own id namespace in free-form
/// turn text — <c>[sgt]\d+</c> for the edit room, <c>p\d+</c> for the graphics room) via a thin
/// subclass; the round-robin schedule, sentinel detection, convergence detection, turn
/// observation, and ceiling handling below are identical for every room. See
/// docs/video-editing.md "The edit room" / "The graphics room".
///
/// <para>
/// Fully unit-testable with zero network calls: it never makes an HTTP/model call itself, only
/// orchestrating which already-constructed <see cref="AIAgent"/> speaks next based on message
/// history it is handed. See <c>EditRoomGroupChatManagerTests</c>/<c>GraphicsRoomGroupChatManagerTests</c>.
/// </para>
/// </summary>
public abstract class RoomGroupChatManager : GroupChatManager
{
    /// <summary>The literal token every room's director prompt is instructed to emit once the room has converged on a decision.</summary>
    public const string SentinelToken = "ROOM_DECIDED";

    /// <summary>The <see cref="DecisionQuestion.Name"/> of the advisory "has the room converged?" question (kind <see cref="DecisionKind.Noul"/>).</summary>
    public const string ConvergedQuestionName = "converged?";

    /// <summary>The <see cref="DecisionQuestion.Name"/> of the advisory "who should speak next?" question (kind <see cref="DecisionKind.Choice"/>).</summary>
    public const string NextSpeakerQuestionName = "next_speaker";

    /// <summary>How many of the most recent observed turns are rendered into the decision state. Bounds the state; the full transcript is never sent.</summary>
    private const int DecisionStateTurnCount = 8;

    /// <summary>Per-turn character cap inside the decision state — a single long turn must not crowd out the rest of the window.</summary>
    private const int DecisionStateTurnChars = 600;

    /// <summary>Total character cap for the rendered decision state.</summary>
    private const int DecisionStateMaxChars = 4000;

    private const string ConvergedQuestionInstructions =
        "You are observing a multi-agent editorial room. Based on the most recent turns, has the room " +
        "converged on a final decision, i.e. would continuing to deliberate add nothing? Answer yes if " +
        "the participants now agree and the remaining disagreement is cosmetic.";

    private const string NextSpeakerQuestionInstructions =
        "You are observing a multi-agent editorial room. Based on the most recent turns, which " +
        "participant should speak next to move the room toward a decision? Pick exactly one of the " +
        "offered participant names.";

    /// <summary>
    /// How many interchangeable registrations EVERY logical participant — each seat AND the
    /// director — occupies in <c>allParticipants</c>, which is therefore laid out in consecutive
    /// pairs: <c>[seat0, seat0, seat1, seat1, ..., director, director]</c>. See the constructor's
    /// remarks for why one registration per participant is not enough.
    /// </summary>
    public const int ParticipantAliasCount = 2;

    private readonly IReadOnlyList<AIAgent> _allParticipants;
    private readonly IRoomStepConfig _config;
    private readonly IReadOnlySet<string> _offeredIds;
    private readonly string _directorSeatName;
    private readonly Regex _idMentionPattern;
    private readonly int _editorCount;
    private readonly IDecisionClient? _decisionClient;
    private readonly ILogger _logger;

    /// <summary>
    /// The registered SEAT names, in seat order, de-duplicated — the legal answer set for the
    /// advisory <c>next_speaker</c> question. Derived once from <see cref="_allParticipants"/> at
    /// the same stride its seat-name scan uses (<c>s * ParticipantAliasCount</c>), so it can only
    /// ever contain names this room actually registered: the director is never a candidate (it
    /// moderates rather than proposing), and no name is invented.
    /// </summary>
    private readonly IReadOnlyList<string> _seatNames;

    private readonly List<HashSet<string>> _roundMentionHistory = [];
    private HashSet<string> _currentRoundMentions = new(StringComparer.Ordinal);
    private int _lastRoundIndexObserved = -1;

    /// <summary>
    /// Every message this manager has observed via <see cref="UpdateHistoryAsync"/>, in arrival
    /// order (the opening message first) — never mutated back into the canonical history returned to
    /// the framework.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>FIXED (T-204, disclosed by T-205 ruling R3).</b> This accumulator used to record only the
    /// OPENING message, ever. <c>Microsoft.Agents.AI.Workflows</c> 1.22.0's
    /// <c>GroupChatHost.TakeTurnAsync</c> hands <see cref="UpdateHistoryAsync"/> a <b>per-turn
    /// delta</b>, not the cumulative history its name suggests (decompiled and confirmed against the
    /// shipped assembly), so the old <c>_observedTurns.Count</c>-based start index re-read the same
    /// single element every turn and appended nothing. The fix appends each call's contents in full,
    /// deduplicated by reference through <see cref="_observedTurnSet"/>, which stays correct under
    /// either convention. The fix is KEPT — it is a genuine bug in this file.
    /// </para>
    /// <para>
    /// <b>Blast radius, verified against this code (not assumed).</b> No persisted artifact reads
    /// this property: the room's transcript artifact and <c>chatTranscriptJson</c> are built by
    /// <c>RoomStepExecutorBase</c> from the framework's <c>WorkflowOutputEvent</c> and from the
    /// turn-completed callback's captured turns, and <c>meta.turnCount</c> from
    /// <c>roomResult.Transcript.Count</c>. Nothing in production consumes this property outside this
    /// class (one test reads it). Inside the class it feeds exactly two things:
    /// <see cref="RoomDecisionRecord.TurnIndex"/> (<c>_observedTurns.Count - 1</c>) and — the
    /// load-bearing one — <see cref="BuildDecisionState"/>, the bounded rendering sent as the
    /// advisory questions' <see cref="DecisionState.Content"/> and hashed into the memoizer key.
    /// </para>
    /// <para>
    /// So, concretely: <see cref="RoomDecisionMode.Off"/> (the default, and zero decision calls)
    /// behaves identically to before, and NO persisted artifact changes a single byte. The only
    /// observable change is on the opt-in <see cref="RoomDecisionMode.Questions"/> /
    /// <see cref="RoomDecisionMode.Adaptive"/> path, where the decision state now carries the real
    /// last-N turns (so the question context and the memo key both change) and
    /// <see cref="RoomDecisionRecord.TurnIndex"/> becomes correct instead of always 0. QA and
    /// Security review must test THAT surface, not the transcript artifacts.
    /// </para>
    /// </remarks>
    public IReadOnlyList<ChatMessage> ObservedTurns => _observedTurns;
    private readonly List<ChatMessage> _observedTurns = [];

    /// <summary>
    /// Reference-identity guard for <see cref="_observedTurns"/>, and the half of the T-204 fix that
    /// makes it correct under EITHER calling convention.
    /// </summary>
    /// <remarks>
    /// Required because 1.22.0's <c>GroupChatHost.TakeTurnAsync</c> passes
    /// <see cref="UpdateHistoryAsync"/> only the messages that arrived SINCE THE PREVIOUS TURN (a
    /// delta — decompiled and confirmed against the shipped assembly), not the cumulative history its
    /// name suggests: the accumulator therefore has to append each call's contents in full.
    /// Deduplicating by reference keeps that correct under either convention, so a future version
    /// that hands over the cumulative list cannot double up every turn.
    /// </remarks>
    private readonly HashSet<ChatMessage> _observedTurnSet = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// One recorded advisory answer, in the order it was produced. Additive metadata only — nothing
    /// in the scheduler or the terminator reads it, and no downstream consumer is required to.
    /// </summary>
    /// <param name="TurnIndex">
    /// Zero-based position of the just-completed DIRECTOR turn among <see cref="ObservedTurns"/>
    /// (the opening message is index 0), i.e. the room turn the answer is about. Deterministic and
    /// free of timestamps/GUIDs. Correct rather than always <c>0</c> only since the T-204 accumulator
    /// fix (T-205 ruling R3) — before it <see cref="ObservedTurns"/> never grew past the opening
    /// message, so this index was pinned at <c>0</c> for every recorded answer.
    /// </param>
    /// <param name="Question">The <see cref="DecisionQuestion.Name"/> answered (<c>converged?</c> or <c>next_speaker</c>).</param>
    /// <param name="Kind">The question's <see cref="DecisionKind"/>.</param>
    /// <param name="Choice">The chosen option for a <see cref="DecisionKind.Choice"/> question; null otherwise.</param>
    /// <param name="Noul">The renormalized probability for a <see cref="DecisionKind.Noul"/> question; null otherwise.</param>
    /// <param name="Confidence">The model's own confidence, when it reported one; null otherwise (<see cref="DecisionKind.Noul"/> has no confidence by contract).</param>
    public sealed record RoomDecisionRecord(
        int TurnIndex,
        string Question,
        DecisionKind Kind,
        string? Choice,
        double? Noul,
        double? Confidence);

    /// <summary>
    /// Every advisory answer this room recorded, in call order. Empty whenever decision scheduling is
    /// <see cref="RoomDecisionMode.Off"/>, no decision client was supplied, or every attempt
    /// soft-failed — all of which are normal, not errors.
    /// </summary>
    public IReadOnlyList<RoomDecisionRecord> DecisionRecords => _decisionRecords;
    private readonly List<RoomDecisionRecord> _decisionRecords = [];

    /// <summary>
    /// The most recent advisory <c>next_speaker</c> answer, or null if none was recorded. Exposed for
    /// a future adaptive scheduler to read; <b>the current scheduler never consults it</b> — the
    /// round-robin and the termination checks are unchanged by anything recorded here.
    /// </summary>
    public string? LastNextSpeaker { get; private set; }

    /// <summary>
    /// Why <see cref="ShouldTerminateAsync"/> last returned <c>true</c>: <c>"sentinel"</c> or
    /// <c>"converged"</c>. Null while the room is still running or if it ran to the
    /// <see cref="GroupChatManager.MaximumIterationCount"/> ceiling instead (the executor should
    /// treat a still-null value after the run ends as <c>"ceiling"</c>).
    /// </summary>
    public string? TerminationReason { get; private set; }

    /// <summary>
    /// Extracts offered-id-vocabulary mentions from free-form turn text — shared by convergence
    /// tracking here and by each executor's chat-turn event reporting. <paramref name="pattern"/>
    /// must capture the candidate id as group 1 (each room's subclass exposes a convenience
    /// wrapper bound to its own pattern).
    /// </summary>
    public static IReadOnlyList<string> ExtractIdMentions(string? text, IReadOnlySet<string> offeredIds, Regex pattern)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        List<string> found = [];
        foreach (Match m in pattern.Matches(text))
        {
            string id = m.Groups[1].Value;
            if (offeredIds.Contains(id) && !found.Contains(id))
                found.Add(id);
        }

        return found;
    }

    /// <param name="allParticipants">
    /// Every logical participant registered <see cref="ParticipantAliasCount"/> times in
    /// consecutive slots — the seats first, in the same order as the config's
    /// <c>EffectiveSeats</c>, then the director last:
    /// <c>[seat0, seat0, seat1, seat1, ..., director, director]</c>. Each alias is a separate
    /// <see cref="RoomSeatAgent"/> wrapping its OWN inner agent instance (same resolved chat
    /// client/instructions/tools; see the remarks below for why sharing one inner instance across
    /// two wrappers does not work).
    /// </param>
    /// <param name="config">The step's resolved room configuration (schedule/termination knobs only are read here).</param>
    /// <param name="offeredIds">The offered id vocabulary extracted from the bounded view, used for convergence detection only.</param>
    /// <param name="directorSeatName">The <see cref="AIAgent.Name"/> the director's <see cref="RoomSeatAgent"/> wrapper reports — used to recognise the director's own turns in history.</param>
    /// <param name="idMentionPattern">The compiled regex recognizing this room's offered-id namespace in free-form turn text (candidate id captured as group 1).</param>
    /// <param name="decisionClient">
    /// The advisory decision client this room asks about its own progress, or null for "no decision
    /// provider configured" — which is both the default and this install's real state. Null is
    /// indistinguishable from <see cref="RoomDecisionMode.Off"/> in effect: the room runs exactly as
    /// it always has. Appended as an optional trailing parameter so every existing construction site
    /// (and every test fixture) keeps compiling unchanged.
    /// </param>
    /// <param name="logger">Logger for the soft-failure warnings this path emits; null falls back to a no-op logger.</param>
    /// <remarks>
    /// <c>Microsoft.Agents.AI.Workflows</c> 1.22.0's internal <c>GroupChatHost.TakeTurnAsync</c>
    /// (decompiled and confirmed against the shipped 1.22.0 assembly — absent from 1.0.0-rc2)
    /// added a guard that was not there before: if <c>SelectNextAgentAsync</c> returns the SAME
    /// registered participant that took the immediately preceding turn, the host treats the room
    /// as having nothing left to say and ends it right there, without ever invoking that agent
    /// again — silently short-circuiting the turn instead of sending it. That collides head-on
    /// with this scheduler's design in TWO places: the director speaks every remaining turn once
    /// the round-robin phase ends, and a room configured with a single seat repeats that seat
    /// across rounds. Either one ends the room early under 1.22.0.
    /// <para>
    /// Rather than special-case the phases that happen to repeat today, EVERY participant gets
    /// <see cref="ParticipantAliasCount"/> interchangeable registrations and
    /// <c>SelectNextAgentAsync</c> alternates between them on turn parity, which makes
    /// "never the same registration twice in a row" hold arithmetically for any schedule (see that
    /// method's remarks for the one-line proof). Aliasing costs nothing semantically: every
    /// registration receives the identical growing history each turn — the group chat broadcasts
    /// <c>_history</c> to every registered participant, not a per-participant slice — so which
    /// alias answers a given turn is a scheduling-identity detail only, invisible to the transcript
    /// (aliases of one participant share a <see cref="AIAgent.Name"/>, the director's being
    /// <paramref name="directorSeatName"/>), to the sentinel and convergence checks, and to the
    /// model itself.
    /// </para>
    /// <para>
    /// A participant's aliases must be built from SEPARATE inner agent instances, not one inner
    /// agent wrapped twice: <c>AIAgent.Id</c> defaults to a fresh random GUID per instance, but
    /// <c>DelegatingAIAgent</c> (what <see cref="RoomSeatAgent"/> is) forwards its <c>IdCore</c> to
    /// <c>InnerAgent.Id</c>. <c>AgentWorkflowBuilder</c> derives each participant's internal
    /// executor-binding id from Name+Id, and a participant's aliases must keep the SAME Name — so a
    /// shared inner agent gives those aliases the identical Id too, and
    /// <c>GroupChatWorkflowBuilder.Build()</c> throws ("Cannot bind executor with ID '...' because
    /// an executor with the same ID but different instance is already bound.") the moment the
    /// workflow is built. Confirmed live against the real 1.22.0 assembly. See
    /// <c>RoomStepExecutorBase.RunRoomAsync</c>, which builds each alias's inner agent independently
    /// for exactly this reason.
    /// </para>
    /// </remarks>
    protected RoomGroupChatManager(
        IReadOnlyList<AIAgent> allParticipants,
        IRoomStepConfig config,
        IReadOnlySet<string> offeredIds,
        string directorSeatName,
        Regex idMentionPattern,
        IDecisionClient? decisionClient = null,
        ILogger? logger = null)
    {
        if (allParticipants.Count % ParticipantAliasCount != 0)
        {
            throw new ArgumentException(
                $"Every room participant must be registered exactly {ParticipantAliasCount} times, so the " +
                $"participant list's length must be a multiple of {ParticipantAliasCount}.",
                nameof(allParticipants));
        }

        if (allParticipants.Count < 2 * ParticipantAliasCount)
        {
            throw new ArgumentException(
                $"A room needs at least one seat plus the director, each registered {ParticipantAliasCount} times.",
                nameof(allParticipants));
        }

        _allParticipants = allParticipants;
        _config = config;
        _offeredIds = offeredIds;
        _directorSeatName = directorSeatName;
        _idMentionPattern = idMentionPattern;
        _editorCount = (allParticipants.Count / ParticipantAliasCount) - 1;
        _decisionClient = decisionClient;
        _logger = logger ?? NullLogger.Instance;

        List<string> seatNames = new(_editorCount);
        for (int s = 0; s < _editorCount; s++)
        {
            string name = _allParticipants[s * ParticipantAliasCount].Name;
            if (!seatNames.Contains(name, StringComparer.Ordinal))
                seatNames.Add(name);
        }

        _seatNames = seatNames;
    }

    /// <summary>
    /// Picks the LOGICAL speaker — round-robin over the seats for the config's <c>Rounds</c> full
    /// passes, then the director for every turn after that — and then returns one of that
    /// participant's <see cref="ParticipantAliasCount"/> interchangeable registrations, chosen by
    /// the turn's parity.
    /// </summary>
    /// <remarks>
    /// The parity term is what upholds the invariant the constructor's remarks describe: this
    /// method must never return the SAME registration on two consecutive turns. It holds for every
    /// seat count, every <c>Rounds</c> value, and any future change to the logical schedule above,
    /// because it is arithmetic rather than a property of the schedule — for consecutive turns
    /// <c>i</c> and <c>i+1</c> the returned indices are
    /// <c>2·logical(i) + (i%2)</c> and <c>2·logical(i+1) + ((i+1)%2)</c>, and those can only be
    /// equal if <c>2·(logical(i) - logical(i+1))</c> equals ±1, which no integer satisfies.
    /// <para>
    /// Relying on the schedule instead is exactly what broke before: alternating aliases only in
    /// the director phase left the round-robin phase able to repeat a registration whenever a room
    /// was configured with a SINGLE seat and <c>Rounds &gt;= 2</c> (the default), which selected
    /// that one seat twice in a row and ended the room before the director ever spoke.
    /// </para>
    /// </remarks>
    protected override ValueTask<AIAgent> SelectNextAgentAsync(
        IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        int i = IterationCount;

        // Seats occupy logical indices 0.._editorCount-1; the director is the one after them.
        int logical = i < _editorCount * _config.Rounds
            ? i % _editorCount
            : _editorCount;

        AIAgent next = _allParticipants[(logical * ParticipantAliasCount) + (i % ParticipantAliasCount)];
        return ValueTask.FromResult(next);
    }

    /// <summary>
    /// Pure pass-through: records every NEW message from this turn's delta into
    /// <see cref="ObservedTurns"/>, then returns <paramref name="history"/> completely unchanged.
    /// Deliberately never filters/trims — the framework treats this return value as the new
    /// canonical transcript, not a per-turn view, so returning anything other than the untouched
    /// input would corrupt it.
    ///
    /// <para>
    /// In <see cref="RoomDecisionMode.Questions"/> (and, today, <see cref="RoomDecisionMode.Adaptive"/>)
    /// it additionally asks the two advisory decision questions ONCE per completed director turn —
    /// after the bookkeeping above, and never in a way the return value can reflect. The answers are
    /// recorded on <see cref="DecisionRecords"/> and change nothing else; any failure is swallowed
    /// with a warning. Making this method async is safe: the framework awaits it either way.
    /// </para>
    /// <para>
    /// The bookkeeping line below is the whole of the T-204 accumulator fix, disclosed here under
    /// T-205 ruling R3. WHY: <c>history</c> is the turn's DELTA in 1.22.0 (see
    /// <see cref="_observedTurnSet"/>'s remarks), so every call's messages are appended. WHAT IT
    /// REPLACED: a <c>_observedTurns.Count</c>-based start index, which assumed cumulative history
    /// and therefore re-read the same single element on every turn — <see cref="ObservedTurns"/>
    /// silently contained only the opening message for the room's entire life. BLAST RADIUS (read
    /// off this code, not assumed): no persisted artifact changes — the transcript artifact,
    /// <c>chatTranscriptJson</c> and <c>meta.turnCount</c> all come from
    /// <c>RoomStepExecutorBase</c>'s <c>WorkflowOutputEvent</c>/captured turns, and never from
    /// <see cref="ObservedTurns"/>. <see cref="RoomDecisionMode.Off"/> — the default — is likewise
    /// untouched. The real change is confined to the opt-in
    /// <see cref="RoomDecisionMode.Questions"/>/<see cref="RoomDecisionMode.Adaptive"/> path, via
    /// <see cref="BuildDecisionState"/> (its content and the memo key derived from it) and
    /// <see cref="RoomDecisionRecord.TurnIndex"/>. See <see cref="ObservedTurns"/> for the full
    /// disclosure.
    /// </para>
    /// </summary>
    protected override async ValueTask<IEnumerable<ChatMessage>> UpdateHistoryAsync(
        IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        // T-204 fix (kept, T-205 ruling R3): `history` is the turn's DELTA, so every call's messages
        // are appended. The previous "start at _observedTurns.Count" indexing assumed cumulative
        // history and silently recorded only the opening message, never another turn.
        foreach (ChatMessage message in history)
        {
            if (_observedTurnSet.Add(message))
                _observedTurns.Add(message);
        }

        TrackConvergence(history);

        await AskDecisionQuestionsAsync(history, cancellationToken);

        return history;
    }

    /// <summary>
    /// Asks the two advisory questions — in ONE <see cref="IDecisionClient.DecideAsync"/> call —
    /// when the newest turn of this <paramref name="newestTurns"/> delta is the director's, the
    /// room's config enables decision scheduling, and a client was supplied. Records both answers,
    /// or nothing at all.
    /// <para>
    /// <paramref name="newestTurns"/> is the delta <see cref="UpdateHistoryAsync"/> was handed
    /// (not the cumulative transcript), so its last element is the turn that has just completed.
    /// Note the one-turn boundary 1.22.0 imposes: <c>GroupChatHost.TakeTurnAsync</c> checks
    /// <c>ShouldTerminateAsync</c> BEFORE calling <c>UpdateHistoryAsync</c>, so the very last turn
    /// of a room that ends on the ceiling/sentinel/convergence is never surfaced here and is never
    /// asked about — deliberate and harmless, since that turn is the one no answer could act on.
    /// </para>
    /// <para>
    /// Soft-failure by contract: a missing client, a thrown error, a timeout, a result missing one
    /// of the asked questions, or a <c>next_speaker</c> answer outside the offered seat set all
    /// degrade to "asked nothing, recorded nothing" plus a warning log. Nothing here can fail,
    /// shorten, or reorder a room. The only exception re-thrown is the caller's own cancellation,
    /// which is a teardown rather than a decision failure.
    /// </para>
    /// </summary>
    private async ValueTask AskDecisionQuestionsAsync(IReadOnlyList<ChatMessage> newestTurns, CancellationToken ct)
    {
        if (_config.DecisionScheduling == RoomDecisionMode.Off || _decisionClient is null)
            return;

        // Only a completed DIRECTOR turn triggers a question round — seat turns are the round-robin
        // proposing material, the director's are the room's progress checkpoints.
        if (newestTurns.Count == 0 ||
            !string.Equals(newestTurns[^1].AuthorName, _directorSeatName, StringComparison.Ordinal))
        {
            return;
        }

        string stateContent = BuildDecisionState();
        if (stateContent.Length == 0)
            return;

        List<DecisionQuestion> questions =
        [
            new(ConvergedQuestionName, DecisionKind.Noul, ConvergedQuestionInstructions, [])
        ];

        // A Choice question's Options are documented as 2..255 values, so a room that registered
        // fewer than two DISTINCT seats has no meaningful next-speaker question to ask (a one-option
        // choice would violate the transport contract and could only ever return that one seat).
        // "No seats" and "one seat" therefore both skip this half and still ask converged?.
        bool askNextSpeaker = _seatNames.Count >= 2;
        if (askNextSpeaker)
            questions.Add(new DecisionQuestion(NextSpeakerQuestionName, DecisionKind.Choice, NextSpeakerQuestionInstructions, _seatNames));

        int turnIndex = _observedTurns.Count - 1;

        try
        {
            DecisionResult result = await _decisionClient.DecideAsync(new DecisionState(stateContent), questions, ct);

            if (result is null || !result.Answers.TryGetValue(ConvergedQuestionName, out DecisionAnswer? converged) ||
                converged.Kind != DecisionKind.Noul || converged.Noul is null)
            {
                _logger.LogWarning(
                    "Room decision call for {RoomKind} returned no usable '{Question}' answer; recording nothing. " +
                    "The room's schedule and termination are unaffected",
                    GetType().Name,
                    ConvergedQuestionName);
                return;
            }

            DecisionAnswer? nextSpeaker = null;
            if (askNextSpeaker)
            {
                if (!result.Answers.TryGetValue(NextSpeakerQuestionName, out nextSpeaker) ||
                    nextSpeaker.Kind != DecisionKind.Choice ||
                    nextSpeaker.Choice is null ||
                    !_seatNames.Contains(nextSpeaker.Choice, StringComparer.Ordinal))
                {
                    _logger.LogWarning(
                        "Room decision call for {RoomKind} returned no usable '{Question}' answer drawn from the offered seat set; " +
                        "recording nothing. The room's schedule and termination are unaffected",
                        GetType().Name,
                        NextSpeakerQuestionName);
                    return;
                }
            }

            _decisionRecords.Add(new RoomDecisionRecord(
                turnIndex, ConvergedQuestionName, DecisionKind.Noul, null, converged.Noul, converged.Confidence));

            if (nextSpeaker is not null)
            {
                _decisionRecords.Add(new RoomDecisionRecord(
                    turnIndex, NextSpeakerQuestionName, DecisionKind.Choice, nextSpeaker.Choice, null, nextSpeaker.Confidence));
                LastNextSpeaker = nextSpeaker.Choice;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Room decision questions failed for {RoomKind}; recording nothing. The room's schedule and termination are unaffected",
                GetType().Name);
        }
    }

    /// <summary>
    /// A bounded, deterministic rendering of the room's most recent turns for the decision state:
    /// the last <see cref="DecisionStateTurnCount"/> observed turns, in observation order, each
    /// truncated to <see cref="DecisionStateTurnChars"/> and the whole thing capped at
    /// <see cref="DecisionStateMaxChars"/>. Deliberately carries no timestamps, no GUIDs and no
    /// dictionary iteration — the same room history always renders the same string — and never the
    /// full unbounded transcript.
    /// </summary>
    /// <remarks>
    /// <b>The load-bearing consumer of <see cref="ObservedTurns"/>, and therefore the one place the
    /// T-204 accumulator fix is observable (T-205 ruling R3).</b> This string is both the
    /// <see cref="DecisionState.Content"/> of the room's advisory questions and, transitively, part
    /// of the memoizer key derived from it. Before the fix <c>_observedTurns</c> only ever held the
    /// opening message, so this state could never contain a turn of actual deliberation; after it,
    /// the state carries the real last-N turns. On the opt-in
    /// <see cref="RoomDecisionMode.Questions"/>/<see cref="RoomDecisionMode.Adaptive"/> path that
    /// changes both the question context and the memo key. It changes no persisted artifact and
    /// nothing at all under <see cref="RoomDecisionMode.Off"/> — see <see cref="ObservedTurns"/>.
    /// </remarks>
    private string BuildDecisionState()
    {
        int start = Math.Max(0, _observedTurns.Count - DecisionStateTurnCount);

        StringBuilder sb = new();
        for (int i = start; i < _observedTurns.Count; i++)
        {
            ChatMessage turn = _observedTurns[i];
            // The opening message carries no AuthorName (it is the user turn holding the bounded
            // view), so fall back to its chat role rather than emitting an anonymous line.
            string author = turn.AuthorName is { Length: > 0 } name ? name : turn.Role.Value;
            string text = turn.Text ?? string.Empty;
            if (text.Length > DecisionStateTurnChars)
                text = text[..DecisionStateTurnChars] + "…";

            string line = $"{author}: {text}\n";
            if (sb.Length + line.Length > DecisionStateMaxChars)
                break;

            sb.Append(line);
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Ceiling check first (mirrors <see cref="GroupChatManager"/>'s own default
    /// <c>ShouldTerminateAsync</c> implementation — <c>IterationCount &gt;= MaximumIterationCount</c>
    /// — which overriding this method entirely SHADOWS rather than composes with; the ceiling is
    /// NOT enforced automatically just because <c>MaximumIterationCount</c> is set, contrary to an
    /// earlier assumption here corrected after decompiling the real rc2 assembly. This method must
    /// therefore check it explicitly, unconditionally, before any mode-specific logic), then the
    /// sentinel check (director's most recent turn contains <see cref="SentinelToken"/>) and/or
    /// offered-id-mention convergence check across consecutive seat rounds, gated by the config's
    /// <c>Termination</c>. <see cref="EditRoomTerminationMode.FixedTurns"/> skips both
    /// mode-specific checks so only the ceiling can end the room. Safe to call at iteration 0
    /// (empty/opening-only history).
    /// </summary>
    protected override ValueTask<bool> ShouldTerminateAsync(
        IReadOnlyList<ChatMessage> history, CancellationToken cancellationToken)
    {
        if (IterationCount >= MaximumIterationCount)
            return ValueTask.FromResult(true);

        if (_config.Termination == EditRoomTerminationMode.FixedTurns)
            return ValueTask.FromResult(false);

        if (history.Count > 0 &&
            _config.Termination is EditRoomTerminationMode.SentinelOnly or EditRoomTerminationMode.SentinelOrConverged)
        {
            ChatMessage last = history[^1];
            if (string.Equals(last.AuthorName, _directorSeatName, StringComparison.Ordinal) &&
                (last.Text?.Contains(SentinelToken, StringComparison.Ordinal) ?? false))
            {
                TerminationReason = "sentinel";
                return ValueTask.FromResult(true);
            }
        }

        if (_config.Termination is EditRoomTerminationMode.Converged or EditRoomTerminationMode.SentinelOrConverged)
        {
            int rounds = _roundMentionHistory.Count;
            int minRounds = Math.Max(1, _config.MinConvergenceRounds);
            if (rounds >= minRounds)
            {
                HashSet<string> reference = _roundMentionHistory[^1];
                bool allEqual = true;
                for (int r = rounds - minRounds; r < rounds - 1; r++)
                {
                    if (!_roundMentionHistory[r].SetEquals(reference))
                    {
                        allEqual = false;
                        break;
                    }
                }

                if (allEqual)
                {
                    TerminationReason = "converged";
                    return ValueTask.FromResult(true);
                }
            }
        }

        return ValueTask.FromResult(false);
    }

    /// <summary>Clears every piece of internal state so this manager instance could, in principle, drive a second independent run.</summary>
    protected override void Reset()
    {
        _observedTurns.Clear();
        _observedTurnSet.Clear();
        _roundMentionHistory.Clear();
        _currentRoundMentions = new HashSet<string>(StringComparer.Ordinal);
        _lastRoundIndexObserved = -1;
        _decisionRecords.Clear();
        LastNextSpeaker = null;
        TerminationReason = null;
    }

    /// <summary>
    /// Extracts offered-id mentions from each newly observed SEAT turn (never the director's —
    /// the director doesn't propose ids, it moderates) and accumulates them per round; a round's
    /// mention set is finalized (appended to <see cref="_roundMentionHistory"/>) once every seat
    /// in that round has spoken.
    /// </summary>
    private void TrackConvergence(IReadOnlyList<ChatMessage> history)
    {
        if (_editorCount <= 0)
            return;

        // IterationCount is the number of turns ALREADY COMPLETED when this callback fires (see
        // the class's "called BEFORE any agent has spoken" contract for iteration 0) — walk only
        // the seat-authored messages in history that arrived since the last call.
        for (int i = 0; i < history.Count; i++)
        {
            ChatMessage msg = history[i];
            if (string.Equals(msg.AuthorName, _directorSeatName, StringComparison.Ordinal))
                continue;

            // Seat s owns the alias pair starting at s * ParticipantAliasCount, so the seat NAMES
            // are at every ParticipantAliasCount'th slot — NOT at 0.._editorCount-1, which under
            // the paired layout would walk off the end of the seats and miss the last one entirely.
            bool isEditorSeat = false;
            for (int s = 0; s < _editorCount; s++)
            {
                if (string.Equals(msg.AuthorName, _allParticipants[s * ParticipantAliasCount].Name, StringComparison.Ordinal))
                {
                    isEditorSeat = true;
                    break;
                }
            }

            if (!isEditorSeat)
                continue;

            int globalTurnIndex = i; // 0-based position among ALL messages including the opening one
            if (globalTurnIndex <= _lastRoundIndexObserved)
                continue;

            foreach (Match m in _idMentionPattern.Matches(msg.Text ?? string.Empty))
            {
                if (_offeredIds.Contains(m.Groups[1].Value))
                    _currentRoundMentions.Add(m.Groups[1].Value);
            }

            _lastRoundIndexObserved = globalTurnIndex;
        }

        // A round is complete once IterationCount is a positive multiple of _editorCount (i.e.
        // every seat has just taken its Nth turn) and we haven't already recorded that round.
        // Bounded to the round-robin phase only (IterationCount <= editorCount*Rounds) — once the
        // director starts speaking, IterationCount can coincidentally be a multiple of
        // _editorCount again with no new seat turns since the last recorded round, which must
        // never be mistaken for a freshly completed seat round.
        if (IterationCount > 0 && IterationCount <= _editorCount * _config.Rounds && IterationCount % _editorCount == 0)
        {
            int roundNumber = IterationCount / _editorCount;
            if (_roundMentionHistory.Count < roundNumber)
            {
                _roundMentionHistory.Add(new HashSet<string>(_currentRoundMentions, StringComparer.Ordinal));
                _currentRoundMentions = new HashSet<string>(StringComparer.Ordinal);
            }
        }
    }
}
