using System.ComponentModel;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// How a room step uses the convening gate — whether the room convenes at all, and whether the
/// decision model only observes or actually decides.
///
/// <para>
/// Deliberately mirrors <c>DecisionGateMode</c> (WorkflowEngine's
/// <c>ReelBolt.WorkflowEngine.Execution.Decision.DecisionGateMode</c>) state for state:
/// <c>Off</c>/<c>Shadow</c>/<c>Gate</c> mean the same three things on both surfaces. It is a
/// SECOND enum rather than a shared one for one structural reason: <c>DecisionGateMode</c> lives
/// in <c>ReelBolt.WorkflowEngine</c>, and <c>ReelBolt.Shared</c> is the UPSTREAM project it
/// references — this file cannot name that type, and a room's config is deserialized from JSON
/// inside <c>ReelBolt.Shared</c> before the engine ever sees it. Duplicating three enum members
/// is the cost of keeping the config surface where every room config record already lives;
/// moving <c>DecisionGateMode</c> down into Shared was rejected as a wider blast radius than the
/// duplication it would avoid. The two are bridged at exactly one place —
/// <c>RoomStepExecutorBase</c>'s shadow arm maps this enum onto <c>DecisionGateMode.Shadow</c> —
/// so a future fourth state has one obvious place to be reconciled.
/// </para>
/// </summary>
public enum ConveneGateMode
{
    /// <summary>No convening gate at all: the room runs exactly as it did before this knob existed. The default.</summary>
    Off,

    /// <summary>Record post-synthesis calibration observations only — never changes the room's decision, status or transcript.</summary>
    Shadow,

    /// <summary>
    /// Let the decision model decide whether the room needs to convene at all. Implemented by the
    /// `Gate` arm in <c>RoomStepExecutorBase</c>, which asks D-004's <c>IDecisionGate.DecideAsync</c>:
    /// a fully ACCEPTED outcome means the room is not convened — the step advances on the room's own
    /// solo fallback with <c>terminationReason</c> <c>"not-convened"</c> — while anything else
    /// (escalation, no Decision provider, a gate-level timeout, any failure, or a room that supplies
    /// no question set) convenes exactly as it does today.
    /// </summary>
    Gate
}

/// Whether — and in what way — a room asks an advisory decision model about its own progress,
/// on top of the deterministic scheduler/terminator it always runs. Set per room step via
/// <see cref="IRoomStepConfig.DecisionScheduling"/> (JSON property <c>decisionScheduling</c>).
///
/// <para>
/// The scheduling/termination contract is IDENTICAL for every member below except <c>Off</c>'s
/// silence: the room still round-robins for <see cref="IRoomStepConfig.Rounds"/> full passes and
/// still hands every remaining turn to the director, and it still terminates only on the
/// director's <c>ROOM_DECIDED</c> sentinel, on offered-id convergence, or at the
/// <see cref="IRoomStepConfig.ClampedMaxTurns"/> ceiling. No member of this enum changes who
/// speaks next or when the room ends.
/// </para>
/// </summary>
public enum RoomDecisionMode
{
    /// <summary>
    /// The default. The room makes ZERO decision calls — no provider is resolved, no client is
    /// constructed, and nothing about the run differs from a build that has no decision support
    /// at all.
    /// </summary>
    Off,

    /// <summary>
    /// After each director turn the room asks two advisory questions in ONE decision call —
    /// <c>converged?</c> (kind <c>Noul</c>) and <c>next_speaker</c> (kind <c>Choice</c>, offering
    /// only the seat names this room actually registered) — and records both answers on
    /// <c>RoomGroupChatManager.DecisionRecords</c>. The answers change nothing: the schedule,
    /// the termination checks and the final decision are exactly what they are with
    /// <see cref="Off"/>. Every failure degrades to "asked nothing, recorded nothing".
    /// </summary>
    Questions,

    /// <summary>
    /// Declared for wave 2, which is the member that will act on the answers (reorder the
    /// schedule, or let a confident convergence answer end the room early). <b>It does not exist
    /// yet: today <see cref="Adaptive"/> behaves IDENTICALLY to <see cref="Questions"/> —
    /// questions asked, answers recorded, schedule untouched.</b> Do not expect any adaptive
    /// behaviour from it.
    /// </summary>
    Adaptive
}

/// <summary>
/// The shared configuration surface every multi-agent "room" step type exposes — the knobs the
/// generic room infrastructure (<c>RoomGroupChatManager</c> / <c>RoomStepExecutorBase</c> in the
/// WorkflowEngine) actually reads. A concrete room's config record
/// (<see cref="EditRoomStepConfig"/>, <see cref="GraphicsRoomStepConfig"/>, a future
/// color-grading room's config, ...) implements this on top of its own JSON shape, so the shared
/// scheduler/executor never needs to know which room it is running.
///
/// <para>
/// Naming note: <see cref="EditRoomSeat"/> and <see cref="EditRoomTerminationMode"/> are the
/// room-GENERIC seat record and termination enum despite their names — they were introduced by
/// the first room (the edit room) and are kept under their original names deliberately, because
/// renaming them would break the persisted-config JSON contract's documentation trail and the
/// existing test suite for zero behavioral gain. Every room's config reuses them as-is (the JSON
/// shape — <c>{name, persona, agentDefinitionId}</c> seats, string enum values — is identical
/// across rooms).
/// </para>
/// </summary>
public interface IRoomStepConfig
{
    /// <summary>Which step's bounded <c>{view, meta}</c> envelope every seat and the director see. Null resolves to <c>Previous</c>.</summary>
    ExtractInputRef? View { get; }

    /// <summary>The effective seat list: the configured seats when non-empty, otherwise the room's built-in defaults.</summary>
    IReadOnlyList<EditRoomSeat> EffectiveSeats { get; }

    /// <summary>How many full round-robin passes over every seat happen before the director speaks.</summary>
    int Rounds { get; }

    /// <summary>The hard turn ceiling, clamped to the documented 2..20 range.</summary>
    int ClampedMaxTurns { get; }

    /// <summary>Per-agent-definition override for the director seat; null resolves to the room's built-in director agent.</summary>
    Guid? DirectorAgentDefinitionId { get; }

    /// <summary>How the room can end early, on top of the hard <see cref="ClampedMaxTurns"/> ceiling.</summary>
    EditRoomTerminationMode Termination { get; }

    /// <summary>Consecutive rounds with an unchanged offered-id-mention set required for convergence to fire.</summary>
    int MinConvergenceRounds { get; }

    /// <summary>Per-turn output token cap injected into every seat's/the room-participant director's turn.</summary>
    int MaxTurnTokens { get; }

    /// <summary>Soft cap on how much of the room transcript is rendered into the synthesis prompt (oldest turns dropped first).</summary>
    int MaxHistoryChars { get; }

    /// <summary>Sampling temperature injected into every seat's turn.</summary>
    float Temperature { get; }

    /// <summary>Sampling temperature injected into the director's ROOM-PARTICIPANT turns only.</summary>
    float DirectorTemperature { get; }

    /// <summary>Reasoning-effort value injected into every room-participant turn.</summary>
    string ReasoningEffort { get; }

    /// <summary>Hard wall-clock budget for the whole group-chat run (not the later synthesis call).</summary>
    int RoomTimeoutSeconds { get; }

    /// <summary>Whether the full room transcript is uploaded as this step's <c>ArtifactStorageKey</c>. Never authoritative.</summary>
    bool PersistTranscript { get; }

    /// <summary>Whether a <c>WorkflowStepChatTurn</c> integration event is published per completed turn.</summary>
    bool StreamTurns { get; }

    /// <summary>
    /// Whether a failed/empty room decision falls back to ONE ordinary solo call of the room's
    /// underlying single-agent equivalent (the existing pre-room pipeline, unchanged). Each
    /// concrete config exposes this under a domain-specific JSON name
    /// (<c>fallbackToSoloEditor</c> / <c>fallbackToSoloPlanner</c>).
    /// </summary>
    bool FallbackToSolo { get; }

    /// <summary>Retry attempts for the standalone structured-output synthesis call.</summary>
    int MaxSynthesisAttempts { get; }

    /// <summary>
    /// How this room uses the convening gate. <see cref="ConveneGateMode.Off"/> (the default
    /// everywhere) is byte-identically today's behaviour: the room convenes unconditionally and
    /// no Decision provider is ever resolved.
    /// </summary>
    ConveneGateMode ConveneGate { get; }

    /// <summary>
    /// Convening-gate confidence threshold — below this the gate considers itself unconvinced and
    /// escalates. Only read when <see cref="ConveneGate"/> is not <see cref="ConveneGateMode.Off"/>.
    /// </summary>
    double ConveneAcceptAt { get; }

    /// <summary>
    /// Convening-gate minimum top-two probability margin; below this the gate escalates even when
    /// the top probability clears <see cref="ConveneAcceptAt"/>. Only read when
    /// <see cref="ConveneGate"/> is not <see cref="ConveneGateMode.Off"/>.
    /// </summary>
    double ConveneMinMargin { get; }

    /// <summary>
    /// Per-step Decision-provider override for the convening gate; null resolves to the single
    /// default <c>Capability = Decision</c> provider row, and to none when there is no such row —
    /// the normal local case, in which the gate is simply inert. Only read when
    /// <see cref="ConveneGate"/> is not <see cref="ConveneGateMode.Off"/>.
    /// </summary>
    Guid? ConveneProviderId { get; }

    /// Whether the room asks an advisory decision model about its own progress —
    /// <see cref="RoomDecisionMode.Off"/> (the default) makes zero decision calls.
    /// <see cref="RoomDecisionMode.Questions"/> (and, today, <see cref="RoomDecisionMode.Adaptive"/>,
    /// which is declared for wave 2 and currently behaves identically) asks two advisory questions
    /// after each director turn and records the answers without changing the schedule or the
    /// termination behaviour in any way. See <see cref="RoomDecisionMode"/>.
    /// </summary>
    [Description(
        "Off (default) makes no decision call. Questions asks two advisory questions per director " +
        "turn and records the answers without changing the schedule or termination. Adaptive is " +
        "declared but NOT IMPLEMENTED: it currently behaves exactly like Questions, nothing acts " +
        "on the answers, and it is reserved for a later phase.")]
    RoomDecisionMode DecisionScheduling { get; }
}
