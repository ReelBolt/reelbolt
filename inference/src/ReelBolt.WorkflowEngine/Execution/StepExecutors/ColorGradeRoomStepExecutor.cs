using ReelBolt.Shared.Workflows.VideoEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Rooms;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Executes <see cref="StepType.ColorGradeRoom"/> steps: several
/// <see cref="AgentType.Colorist"/>-role colorist seats plus an
/// <see cref="AgentType.ColorGradeDirector"/> moderator converse in a live group chat over the
/// same bounded <c>VideoAnalyze</c> view (its <c>view.shots</c> ids and measured per-shot colour
/// descriptors) a solo colorist would see, then the director emits ONE schema-validated
/// <see cref="ColorGradePlanOutput"/> in a normal structured call OUTSIDE the chat loop.
/// <see cref="VideoCompileStepExecutor"/>'s <c>ColorGradePlan</c> resolution consumes it exactly
/// as it would a solo <see cref="AgentType.Colorist"/> Agent step's output — the additive
/// <c>"room"</c> metadata key is skipped by deserialization, the same contract the edit and
/// graphics rooms established. See docs/video-editing.md "The color grade room".
///
/// <para>
/// All room mechanics live in the shared <see cref="RoomStepExecutorBase{TDecision}"/> — this
/// class binds only the grade room's domain: its config column, its <c>s{n}</c> shot-id
/// deliberation vocabulary, its charter/director prompts, its seat/director/solo agent types,
/// and its distinctive validation shape. Distinctive vs. both prior rooms: the DECISION carries
/// no ids at all (one whole-program grade in enum words), so <see cref="FilterToOfferedIds"/> is
/// a structural no-op — the shot ids exist purely to anchor the deliberation and its convergence
/// detection — and the words themselves are validated instead: an empty <c>Look</c> is rejected
/// at synthesis (retry), an unrecognized one spends one retry with precise guidance and is
/// otherwise left for <see cref="VideoCompileStepExecutor"/>'s own <c>unknown_look_word</c>
/// degrade, and a <c>Look</c> of <c>"None"</c> is a fully valid outcome (like the graphics
/// room's empty plan, unlike the edit room's never-empty Keep list). No tool restriction
/// override is needed either: the backing agent types are minimal read-only by
/// <c>ToolGroupCatalog</c> in both roles, like the edit room and unlike the graphics room.
/// </para>
/// </summary>
public class ColorGradeRoomStepExecutor : RoomStepExecutorBase<ColorGradePlanOutput>
{
    // Shared, identical instructions for EVERY room participant (seats AND the director's
    // room-participant instance) — same structure and discipline as the edit/graphics rooms'
    // charters, with the grade room's own vocabulary and invariants (never a numeric colour
    // value; the treatment is words; "None" is a valid outcome).
    private const string ColorGradeRoomCharterPrompt =
        """
        You are participating in a live "color grade room" discussion between several colorists
        and a supervising colorist, deciding what ONE colour-grade treatment the whole compiled
        edit of an analyzed video should receive. The bounded view — including a "shots" list
        where each shot has a short opaque id such as "s0" or "s2" and, when available, measured
        visual descriptors in WORDS (colour temperature, tone, saturation, exposure, look
        groups) — was given to you as the first message in this conversation. Every participant
        in this room sees the exact same view.

        ## Rules — hard constraints, not suggestions

        - Ground every argument in shot ids that appear in the "shots" list you were given
          ("s0", "s2", ...) and in the view's MEASURED descriptor words. Never invent a shot id
          or a measurement, and never treat a silence/segment/placement id ("g3", "t7", "p1")
          as a shot — those are a different kind of id.
        - NEVER mention, estimate, or output an RGB value, a hex colour, a curve or level
          number, a gamma/gain/lift/contrast/saturation value, a percentage, or a timestamp, in
          this discussion. The startSec/endSec values on each shot are for READING only — to
          tell shots apart — never echo, adjust, or derive a number from them. A separate
          deterministic step resolves the room's word choices to exact filter parameters.
        - The treatment is WORDS, not numbers: a Look of "None", "Warm", "Cool", "Filmic",
          "Vibrant", "Muted", or "Mono"; a Strength of "Subtle", "Normal", or "Strong"; a
          ShadowTone of "Neutral", "Lifted", or "Deepened"; a HighlightTone of "Neutral",
          "Softened", or "Brightened". Discuss them only as words.
        - The decision is ONE whole-program treatment that must suit every kept shot, not just
          the best one. Prefer restraint: "Subtle"/"Normal" over "Strong", and a Look of "None"
          is a perfectly good outcome for footage that is already well exposed and consistent.
        - Keep every turn SHORT — a few sentences of prose, not an essay. This is a live
          discussion, not a final report.
        - Do NOT output JSON, markdown code fences, or any structured format in this
          discussion — plain conversational prose only. A separate call, made after this
          discussion ends, produces the final structured plan.

        ## Long footage

        When the view has an "outline", the footage is long: the view lists every phrase of the whole film under outline.phrases
        (ids like "t12-t30"), every shot as ranges under outline.shots, and the lists you depend on (clips, tracks, placements,
        offers) in full. Every id in those ranges is valid; use them as listed. If you have ReadVideoSection, ListVideoSfxClips or
        ListVideoMusic, call them when you need a full list. Never ask for a bigger limit or for less footage.
        """;

    public ColorGradeRoomStepExecutor(
        IAgentChatClientProvider chatClients,
        IAgentToolProvider toolProvider,
        IAgentRegistry agentRegistry,
        IProjectFileWorkspace workspace,
        IWorkflowExecutionContextAccessor executionContextAccessor,
        ILogger<ColorGradeRoomStepExecutor> logger,
        IDecisionGate? decisionGate = null,
        IInferenceProviderResolver? decisionResolver = null,
        IDecisionClientFactory? decisionClientFactory = null)
        : base(chatClients, toolProvider, agentRegistry, workspace, executionContextAccessor, logger,
            decisionGate, decisionResolver, decisionClientFactory)
    {
    }

    public override StepType StepType => StepType.ColorGradeRoom;

    protected override string RoomKind => "ColorGradeRoom";

    protected override string RoomDisplayName => "Color grade room";

    protected override string FailureCodePrefix => "COLOR_GRADE_ROOM";

    protected override string ConfigPropertyName => "ColorGradeRoomConfigJson";

    protected override string? GetConfigJson(WorkflowStep step) => step.ColorGradeRoomConfigJson;

    protected override IRoomStepConfig? DeserializeConfig(string json) =>
        JsonSerializer.Deserialize<ColorGradeRoomStepConfig>(json, ConfigJsonOptions);

    protected override HashSet<string> ExtractOfferedIds(JsonNode? root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (root?["view"]?["shots"] is JsonArray shots)
        {
            foreach (JsonNode? item in shots)
            {
                string? id = item?["id"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(id))
                    ids.Add(id);
            }
        }

        // Outline mode: shots past the first section are listed as ranges, and are gradable too.
        foreach ((string id, _, bool isShot) in OutlineViewIds.Expand(root?["view"], root?["meta"]))
        {
            if (isShot)
                ids.Add(id);
        }

        return ids;
    }

    protected override IReadOnlyList<string> ExtractIdMentions(string? text, IReadOnlySet<string> offeredIds) =>
        ColorGradeRoomGroupChatManager.ExtractOfferedIdMentions(text, offeredIds);

    protected override string NoOfferedIdsMessage =>
        "The resolved view offers no shots to deliberate a colour grade over.";

    /// <summary>
    /// A view with zero shots offers nothing to grade — completing with an explicit "None" plan
    /// (rather than failing) keeps a grade-room workflow runnable on such input, the same
    /// graceful outcome the graphics room's empty plan models. "None" is itself the schema's
    /// first-class no-grade word, so downstream <c>VideoCompileStepExecutor</c> consumption
    /// needs no special case.
    /// </summary>
    protected override ColorGradePlanOutput BuildDecisionForEmptyView() => new()
    {
        Look = "None",
        Strength = "Normal",
        ShadowTone = "Neutral",
        HighlightTone = "Neutral",
        PlanRationale = "No shots were offered by the analysis view, so no colour grade was planned."
    };

    protected override string RoomCharterPrompt => ColorGradeRoomCharterPrompt;

    protected override AgentType SeatAgentType => AgentType.Colorist;

    protected override AgentType DirectorAgentType => AgentType.ColorGradeDirector;

    protected override AgentType SoloFallbackAgentType => AgentType.Colorist;

    protected override string SeatRoleNoun => "colorist";

    protected override string DirectorTurnDirective =>
        "You are now speaking as Director, the supervising colorist moderating this color grade " +
        "room. Review the discussion so far. If the room has converged on one coherent treatment " +
        "— including possibly agreeing that NO grade (a Look of \"None\") is warranted — end " +
        "this turn with the literal token ROOM_DECIDED followed by a one- or two-sentence " +
        "summary of what was agreed. Otherwise, give brief guidance to help the colorists " +
        "converge. Keep this turn short.";

    protected override string SynthesisSchemaInstruction =>
        "output ONLY valid JSON matching the ColorGradePlanOutput schema (a look of \"None\" is " +
        "valid when the room agreed the footage needs no grade).";

    protected override string BuildTurnProgressLabel(bool isDirector, string seatName, int turnNumber, int maxTurns) =>
        isDirector
            ? "Director is reviewing"
            : $"{seatName} is proposing a grade (turn {turnNumber}/{maxTurns})";

    /// <summary>
    /// Null-coalesces every word property (explicit JSON nulls would otherwise leave CLR nulls
    /// behind the non-null string defaults) — the same hardening the graphics room applies to its
    /// Overlays list. Word VALIDATION deliberately does not happen here: unknown words are a
    /// retry concern (<see cref="CheckRetryableIssue"/>) and, terminally,
    /// <see cref="VideoCompileStepExecutor"/>'s own <c>unknown_look_word</c>/normalize degrade.
    /// </summary>
    protected override void NormalizeDecision(ColorGradePlanOutput decision)
    {
        decision.Look ??= string.Empty;
        decision.Strength ??= string.Empty;
        decision.ShadowTone ??= string.Empty;
        decision.HighlightTone ??= string.Empty;
        decision.Reason ??= string.Empty;
        decision.PlanRationale ??= string.Empty;
    }

    /// <summary>
    /// An empty Look is a hard synthesis defect (the schema's one required decision word —
    /// "no grade" must be said as the word "None", never as silence), retried with guidance.
    /// Everything else, "None" included, is accepted.
    /// </summary>
    protected override string? RejectFreshSynthesis(ColorGradePlanOutput decision) =>
        string.IsNullOrWhiteSpace(decision.Look)
            ? "The plan's `look` was empty. Choose exactly one of: " +
              string.Join(", ", ColorGradeFilterBuilder.AllowedLooks.Select(l => $"\"{l}\"")) +
              " — use \"None\" to decline the grade."
            : null;

    /// <summary>
    /// A non-empty but unrecognized Look word spends one retry with precise guidance; on the
    /// final attempt it is accepted as-is and left to <see cref="VideoCompileStepExecutor"/>'s
    /// own deterministic <c>unknown_look_word</c> degrade — the base template's documented
    /// soft-reject contract.
    /// </summary>
    protected override string? CheckRetryableIssue(ColorGradePlanOutput decision, JsonNode? viewRoot) =>
        !string.IsNullOrWhiteSpace(decision.Look) && !ColorGradeFilterBuilder.AllowedLooks.Contains(decision.Look)
            ? $"The plan's `look` (\"{decision.Look}\") is not a recognized look word. Choose exactly one of: " +
              string.Join(", ", ColorGradeFilterBuilder.AllowedLooks.Select(l => $"\"{l}\"")) + "."
            : null;

    /// <summary>
    /// Structural no-op: a <see cref="ColorGradePlanOutput"/> carries no ids at all (guarded by
    /// <c>ColorGradePlanOutputInvariantTests</c>' shape pin), so there is nothing to filter —
    /// the offered shot ids anchor only the DELIBERATION and its convergence detection, never
    /// the decision.
    /// </summary>
    protected override RoomFilterResult FilterToOfferedIds(ColorGradePlanOutput decision, HashSet<string> offeredIds, JsonNode? viewRoot) =>
        new(0, null);

    /// <summary>Every filtered decision is acceptable — "None" and unknown-word plans both resolve deterministically downstream.</summary>
    protected override string? RejectFilteredDecision(ColorGradePlanOutput decision, bool solo) => null;

    // -----------------------------------------------------------------
    // The convening-gate question set — the ONE room that supplies one.
    //
    // A System One choice/score/noul question can only *produce* a decision that decomposes into a
    // FIXED option set (decisions.md §7), and a ColorGradePlanOutput is exactly that: one
    // whole-program grade in enum words. The edit room's decision is an id-anchored keep/drop list
    // and the graphics room's is an open, generative overlay list, so both override these seams
    // back to null with their reason stated — see their own comments. This room is therefore the
    // only one the convening gate ships for.
    //
    // All THREE convene seams are supplied here — the pre-room questions, the agenda they feed,
    // and the post-synthesis shadow questions — which is what makes the Gate arm live for this room
    // and inert for the other two.
    //
    // The four questions are copied VERBATIM from AgentStepExecutor.BuildColoristQuestions, which
    // is private static to that class (and off limits for this change). The duplication is
    // deliberate rather than incidental: the site strings and the option arrays must stay
    // byte-identical to the solo Colorist agent's, because the calibration view aggregates
    // decision_observations rows by (Site, Question) — the room's series and the solo agent's are
    // ONE series (decisions.md §7). RoomConveneGateTests asserts that byte-equality against the
    // solo builder's own output, so this copy cannot drift silently.
    //
    // Also deliberate, and NOT copied from the solo path: the AgentType on every observation this
    // room records is the room's own DirectorAgentType (AgentType.ColorGradeDirector), applied at
    // the base class's shadow dispatch. The shared SITE is the point; passing the room's
    // observations off as a solo colorist's is not, and AgentType is the column that keeps a room
    // observation honestly attributable to the room.
    // -----------------------------------------------------------------

    /// <summary>
    /// The four fixed grade questions in their WIRE shape (<see cref="DecisionQuestion"/>) — what
    /// the pre-room convening gate asks. Paired with <see cref="BuildConveneAgenda"/>, which renders
    /// the gate's answers over this same set, and with <see cref="BuildShadowQuestions"/>, which
    /// asks the same four questions in their gate shape after synthesis.
    ///
    /// <para>
    /// <paramref name="viewRoot"/> is deliberately unused. The grade decision's option set is
    /// whole-program and closed — <c>Look</c> is one of seven words no matter which shots were
    /// offered — unlike an offered-ids vocabulary such as the edit room's <c>s{n}</c>/<c>g{n}</c>/
    /// <c>t{n}</c>, where the candidate set IS the view. There is nothing view-dependent to derive.
    /// </para>
    /// </summary>
    protected override IReadOnlyList<DecisionQuestion>? BuildConveneQuestions(JsonNode? viewRoot) =>
        ColoristQuestions
            .Select(q => new DecisionQuestion(q.Site, DecisionKind.Choice, q.Instructions, q.Options))
            .ToList();

    /// <summary>
    /// The room's convening agenda: the shared <see cref="RoomStepExecutorBase{TDecision}.RenderConveneAgenda"/>
    /// wording over THIS room's own question set and the answers the gate escalated on, so the
    /// splits the fast model reported become the room's first order of business.
    ///
    /// <para>
    /// The standard wording is used deliberately rather than a grade-specific phrasing. The
    /// sentence is generated from the question's own name and its probabilities ("The fast model
    /// split on Look: Filmic 0.44 vs Warm 0.41. Resolve that disagreement first."), which already
    /// says everything there is to say about a split Look; a bespoke variant would be a second
    /// place for the same fact to drift, and nothing about a colour grade needs saying that the
    /// probabilities do not. Only the QUESTION SET is room-specific — which is why this override
    /// is one line and not a sign of unfinished work.
    /// </para>
    ///
    /// <para>
    /// A null question set returns null instead of throwing: the seam is an override point, and
    /// the opening message it feeds must survive a room that declines to supply questions without
    /// turning a formatting detail into a step failure.
    /// </para>
    /// </summary>
    protected override string? BuildConveneAgenda(JsonNode? viewRoot, IReadOnlyDictionary<string, DecisionAnswer> answers)
    {
        IReadOnlyList<DecisionQuestion>? questions = BuildConveneQuestions(viewRoot);
        return questions is null ? null : RenderConveneAgenda(questions, answers);
    }

    /// <summary>
    /// The same four questions in their GATE shape (<see cref="DecisionGateQuestion"/>), each
    /// carrying this room's own synthesized word as
    /// <see cref="DecisionGateQuestion.SystemTwoAnswer"/> so the recorded observation's
    /// <c>agreed</c> column compares the decision model against what the room actually decided.
    ///
    /// <para>
    /// <see cref="DecisionGateQuestion.Site"/> and <see cref="DecisionGateQuestion.Name"/> are the
    /// SOLO colorist's own strings on purpose — that <c>(Site, Question)</c> pairing is the
    /// calibration view's aggregation key, so a <see cref="StepType.ColorGradeRoom"/> step's
    /// observations land on the same series as a solo <see cref="AgentType.Colorist"/> step's. Only
    /// the recorded <c>AgentType</c> differs (this room's <see cref="DirectorAgentType"/>, not
    /// <see cref="AgentType.Colorist"/>), which is what keeps a room observation attributable to the
    /// room rather than passing it off as a solo one.
    /// </para>
    /// </summary>
    protected override IReadOnlyList<DecisionGateQuestion>? BuildShadowQuestions(ColorGradePlanOutput decision)
    {
        // Positional, in the same order as ColoristQuestions below. NormalizeDecision has already
        // run by the time the shadow arm reads this, so no property is null here.
        string[] answers = [decision.Look, decision.Strength, decision.ShadowTone, decision.HighlightTone];

        return ColoristQuestions
            .Select((q, i) => new DecisionGateQuestion(
                q.Site, q.Name, DecisionKind.Choice, q.Instructions, q.Options, answers[i]))
            .ToList();
    }

    /// <summary>
    /// The solo <see cref="AgentType.Colorist"/> agent's four questions, byte-for-byte — the single
    /// source both seams above project from, so the wire set and the gate set cannot drift apart
    /// from each other either. Kept as <c>(Site, Name, Instructions, Options)</c> rather than as
    /// pre-built question records because the two seams need a different record shape from the same
    /// four rows.
    ///
    /// <para>
    /// <b>Do not "tidy" these strings.</b> <c>Site</c> and the option arrays are the shared
    /// calibration namespace and the fixed option set respectively; a reworded instruction or a
    /// reordered option changes the <c>options_hash</c> the observation stores, and a renamed
    /// <c>Site</c> splits the series the whole feature exists to aggregate.
    /// </para>
    /// </summary>
    private static readonly (string Site, string Name, string Instructions, string[] Options)[] ColoristQuestions =
    [
        ("Colorist.Look", "Look",
            "Whole-program colour-grade look for the compiled edit.",
            ["None", "Warm", "Cool", "Filmic", "Vibrant", "Muted", "Mono"]),
        ("Colorist.Strength", "Strength",
            "Strength of the chosen grade.",
            ["Subtle", "Normal", "Strong"]),
        ("Colorist.ShadowTone", "ShadowTone",
            "Shadow tone adjustment for the grade.",
            ["Neutral", "Lifted", "Deepened"]),
        ("Colorist.HighlightTone", "HighlightTone",
            "Highlight tone adjustment for the grade.",
            ["Neutral", "Softened", "Brightened"]),
    ];

    protected override RoomGroupChatManager CreateManager(
        IReadOnlyList<AIAgent> agents, IRoomStepConfig config, IReadOnlySet<string> offeredIds,
        IDecisionClient? decisionClient) =>
        new ColorGradeRoomGroupChatManager(agents, (ColorGradeRoomStepConfig)config, offeredIds, "Director", decisionClient, Logger);
}
