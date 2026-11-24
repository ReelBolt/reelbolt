using System.ComponentModel;

namespace ReelBolt.Shared.Workflows;

/// <summary>
/// JSON configuration for a <c>StepType.DailiesRoom</c> workflow step — the take-selection
/// analogue of <see cref="EditRoomStepConfig"/>/<see cref="GraphicsRoomStepConfig"/>/
/// <see cref="ColorGradeRoomStepConfig"/>, built on the same shared room infrastructure
/// (<see cref="IRoomStepConfig"/>, <c>RoomGroupChatManager</c>/<c>RoomStepExecutorBase</c> in the
/// WorkflowEngine). Several take-selector seats plus a dailies director deliberate over a
/// MULTI-SOURCE <c>VideoAnalyze</c> view whose <c>view.sources[].clipId</c> entries name the
/// generated takes (<c>v{n}t{k}</c>) a <c>VideoGenerate</c> step delivered — one analyzed source
/// per take, with the measured per-shot descriptors of each — then the director synthesizes ONE
/// schema-validated <c>TakeSelectionOutput</c>. A <c>VideoCompile</c> step's <c>TakeSelection</c>
/// reference consumes it to place only the chosen take of each planned shot. Deserialized from
/// <c>WorkflowStep.DailiesRoomConfigJson</c>. Versioned, every field defaulted so old/partial
/// JSON still deserializes — same discipline as <see cref="EditRoomStepConfig"/>. See
/// docs/video-generation.md "Dailies: choosing between takes".
///
/// <para>
/// Seats reuse the room-generic <see cref="EditRoomSeat"/> record and
/// <see cref="EditRoomTerminationMode"/> enum — see <see cref="IRoomStepConfig"/>'s naming note.
/// </para>
/// </summary>
[StepCard(
    "Has several selector agents and a director choose the best take of each generated shot.",
    ["Generated takes from an earlier VideoGenerate step, analysed by VideoAnalyze"],
    "One take selection the compile can use",
    ReelBolt.Shared.Workflows.Apps.AppCostClass.UsesCredits,
    ReadableBy = ["VideoCompile"],
    PairsWith = ["VideoCompile: places only the chosen take of each shot"],
    OutputContract = "TakeSelectionOutput")]
public sealed record DailiesRoomStepConfig(
    int Version = 1,
    /// <summary>
    /// Which step's bounded <c>{view, meta}</c> envelope every seat and the director are shown.
    /// This should point at the <c>VideoAnalyze</c> step whose <c>Sources</c> list a
    /// <c>GeneratedClips</c> reference to the <c>VideoGenerate</c> step — usually an explicit
    /// <c>Step</c> reference. Only <c>Previous</c>/<c>Step</c> are meaningful.
    /// </summary>
    ExtractInputRef? View = null,
    /// <summary>Take-selector seats, round-robin scheduled. Null/empty resolves to <see cref="DefaultSeats"/> at execution time.</summary>
    IReadOnlyList<EditRoomSeat>? Seats = null,
    /// <summary>How many full round-robin passes over every seat happen before the director speaks.</summary>
    int Rounds = 2,
    /// <summary>Hard turn ceiling (maps to <c>GroupChatManager.MaximumIterationCount</c>), clamped 2..20.</summary>
    int MaxTurns = 8,
    /// <summary>Per-agent-definition override for the director seat; null resolves to the built-in <c>AgentType.DailiesDirector</c> agent.</summary>
    Guid? DirectorAgentDefinitionId = null,
    EditRoomTerminationMode Termination = EditRoomTerminationMode.SentinelOrConverged,
    /// <summary>Consecutive rounds with an unchanged offered-take-id-mention set required before convergence ends the room early.</summary>
    int MinConvergenceRounds = 2,
    /// <summary>Per-turn output token cap injected into every seat's/the room-participant director's <c>ChatOptions.MaxOutputTokens</c>.</summary>
    int MaxTurnTokens = 220,
    /// <summary>Soft cap on how much of the room transcript is rendered into the synthesis prompt (oldest turns dropped first beyond this).</summary>
    int MaxHistoryChars = 40000,
    /// <summary>Sampling temperature injected into every take-selector seat's turn.</summary>
    float Temperature = 0.7f,
    /// <summary>Sampling temperature injected into the director's ROOM-PARTICIPANT turns only — the standalone synthesis call uses <c>DailiesDirectorAgent</c>'s own <c>AgentModelSettings</c> default instead.</summary>
    float DirectorTemperature = 0.3f,
    /// <summary>Reasoning-effort value injected into every room-participant turn — must pass <c>ReelBoltAgentBase.ValidReasoningEfforts</c>.</summary>
    string ReasoningEffort = "none",
    /// <summary>Hard wall-clock budget for the whole group-chat run (not the later synthesis call).</summary>
    int RoomTimeoutSeconds = 1200,
    /// <summary>Whether the full, unabridged room transcript is uploaded as this step's <c>ArtifactStorageKey</c>. Never authoritative.</summary>
    bool PersistTranscript = true,
    /// <summary>Whether a <c>WorkflowStepChatTurn</c> integration event is published per completed turn (progress reporting always happens regardless).</summary>
    bool StreamTurns = true,
    /// <summary>Whether a failed room falls back to one ordinary solo <c>AgentType.TakeSelector</c> call.</summary>
    bool FallbackToSoloSelector = true,
    /// <summary>Retry attempts for the standalone structured-output synthesis call when the result is unparseable.</summary>
    int MaxSynthesisAttempts = 2,
    /// <summary>How this room uses the convening gate. Off (the default) is byte-identically today's behaviour — see <see cref="ConveneGateMode"/>. The dailies room supplies no convening question set, so every mode other than Off is an explicit no-op here, like the edit and graphics rooms.</summary>
    ConveneGateMode ConveneGate = ConveneGateMode.Off,
    /// <summary>Convening-gate confidence threshold; only read when <see cref="ConveneGate"/> is not <see cref="ConveneGateMode.Off"/>.</summary>
    double ConveneAcceptAt = 0.85,
    /// <summary>Convening-gate minimum top-two probability margin; only read when <see cref="ConveneGate"/> is not <see cref="ConveneGateMode.Off"/>.</summary>
    double ConveneMinMargin = 0.25,
    /// <summary>Per-step Decision-provider override for the convening gate; null resolves to the default Capability=Decision row, or to none. Only read when <see cref="ConveneGate"/> is not <see cref="ConveneGateMode.Off"/>.</summary>
    Guid? ConveneProviderId = null,
    /// <summary>Whether the room asks an advisory decision model about its own progress. Absent means <see cref="RoomDecisionMode.Off"/>.</summary>
    [property: Description(
        "Off (default) makes no decision call. Questions asks two advisory questions per director " +
        "turn and records the answers without changing the schedule or termination. Adaptive is " +
        "declared but NOT IMPLEMENTED: it currently behaves exactly like Questions, nothing acts " +
        "on the answers, and it is reserved for a later phase.")]
    RoomDecisionMode DecisionScheduling = RoomDecisionMode.Off) : IRoomStepConfig
{
    /// <summary>
    /// The three built-in seats used whenever <see cref="Seats"/> is null/empty, dividing the
    /// actual take-judging decision space (does the take match its neighbours, does its motion
    /// read as the planned camera word, is the image technically clean) rather than forcing
    /// arbitrary personas. All three resolve to the same built-in <c>AgentType.TakeSelector</c>
    /// agent; only the persona differs — the same one-agent-many-personas pattern
    /// <see cref="EditRoomStepConfig.DefaultSeats"/> established.
    /// </summary>
    public static readonly IReadOnlyList<EditRoomSeat> DefaultSeats =
    [
        new("ContinuityJudge", "argues for continuity — which take's measured colour temperature, tone and saturation words sit closest to the real footage it will cut against, and which would announce itself as generated"),
        new("MotionJudge", "argues for motion — which take's measured motion class and camera-move word match what the shot was planned to do, and which looks static, jittery or over-busy"),
        new("ImageJudge", "argues for image quality — exposure (crushed or blown words), sharpness when measured, letterboxing, and any caption that reports artefacts or the wrong subject")
    ];

    /// <summary>The effective seat list: <see cref="Seats"/> when non-empty, otherwise <see cref="DefaultSeats"/>.</summary>
    public IReadOnlyList<EditRoomSeat> EffectiveSeats => Seats is { Count: > 0 } ? Seats : DefaultSeats;

    /// <summary><see cref="MaxTurns"/> clamped to the documented 2..20 range.</summary>
    public int ClampedMaxTurns => Math.Clamp(MaxTurns, 2, 20);

    /// <summary><see cref="IRoomStepConfig"/>'s room-generic name for <see cref="FallbackToSoloSelector"/> (JSON property stays <c>fallbackToSoloSelector</c>).</summary>
    bool IRoomStepConfig.FallbackToSolo => FallbackToSoloSelector;

    ConveneGateMode IRoomStepConfig.ConveneGate => ConveneGate;

    double IRoomStepConfig.ConveneAcceptAt => ConveneAcceptAt;

    double IRoomStepConfig.ConveneMinMargin => ConveneMinMargin;

    Guid? IRoomStepConfig.ConveneProviderId => ConveneProviderId;
}
