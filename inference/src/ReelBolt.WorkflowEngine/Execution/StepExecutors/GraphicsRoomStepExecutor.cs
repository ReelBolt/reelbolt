using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.Rooms;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services.Storage;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Executes <see cref="StepType.GraphicsRoom"/> steps: several
/// <see cref="AgentType.MotionGraphicsPlanner"/>-role artist seats plus an
/// <see cref="AgentType.MotionGraphicsDirector"/> moderator converse in a live group chat over the
/// same offered <c>view.placements</c> candidates (<c>p{n}</c> ids) a solo planner would see, then
/// the director emits ONE schema-validated <see cref="MotionGraphicsPlanOutput"/> in a normal
/// structured call OUTSIDE the chat loop. <see cref="VideoCompileStepExecutor"/>'s
/// <c>GraphicsPlan</c> resolution needs ZERO changes — it already consumes
/// <see cref="MotionGraphicsPlanOutput"/> from whichever step the compile config points at, and
/// its deserialization skips the additive <c>"room"</c> metadata key exactly as it does for the
/// edit room's decision. See docs/video-editing.md "The graphics room".
///
/// <para>
/// All room mechanics live in the shared <see cref="RoomStepExecutorBase{TDecision}"/> — this
/// class binds only the graphics room's domain: its config column, its <c>p{n}</c> placement-id
/// vocabulary, its charter/director prompts, its seat/director/solo agent types, its "an empty
/// overlay plan is a VALID outcome" validation (unlike the edit room, where an empty Keep list is
/// never acceptable), and two graphics-specific behaviors: room-participant turns are
/// tool-restricted to the read-only subset of the backing agents' grants (the seats' underlying
/// <see cref="AgentType.MotionGraphicsPlanner"/> carries sandbox+render tools that belong in the
/// standalone synthesis call, never in a ~220-token prose turn), and the bounded view is enriched
/// with the same <c>inEdit</c> survival annotation
/// (<see cref="IMotionGraphicsPlacementAnnotator"/>) a solo planner's prompt gets.
/// </para>
/// </summary>
public class GraphicsRoomStepExecutor : RoomStepExecutorBase<MotionGraphicsPlanOutput>
{
    // Shared, identical instructions for EVERY room participant (seats AND the director's
    // room-participant instance) — same structure and discipline as the edit room's charter, with
    // the graphics room's own id namespace and invariants (never a timestamp OR a coordinate;
    // duration/emphasis are words; zero overlays is a valid outcome).
    private const string GraphicsRoomCharterPrompt =
        """
        You are participating in a live "graphics room" discussion between several motion-graphics
        artists and a lead director, deciding which overlay-placement candidates of an analyzed
        video deserve a motion-graphics overlay (a lower-third, title, callout, or tag) and what
        each overlay should say. The bounded view — including a "placements" list of candidates,
        each with a short opaque id such as "p0" or "p3", its named region, a 0-100 "fit" score, a
        light/dark text hint, and (when available) an "inEdit" flag saying whether that candidate's
        moment survives the story editor's cut — was given to you as the first message in this
        conversation. Every participant in this room sees the exact same view.

        ## Rules — hard constraints, not suggestions

        - Reference ONLY placement ids that appear in the "placements" list you were given ("p0",
          "p3", ...). Never invent one, never guess one, and never treat a shot/silence/segment id
          ("s2", "g3", "t7") as a placement — those are a different kind of id and are never valid
          as an overlay anchor here.
        - NEVER mention, estimate, or output a timestamp, a duration in seconds or frames, or any
          pixel/percentage coordinate, in this discussion. The startSec/endSec values on each
          placement are for READING only — to tell otherwise-identical candidates apart — never
          echo, adjust, or derive a number from them. A separate deterministic step resolves chosen
          placement ids to exact positions and times.
        - Overlay duration and emphasis are WORDS, not numbers: "Short"/"Medium"/"Hold" and
          "Subtle"/"Normal"/"Strong". Discuss them only as words.
        - Strongly prefer placements marked "inEdit": true — an overlay planned on a cut-away
          moment is dropped later and wasted. Prefer fewer, better overlays over decoration on
          every cut; ZERO overlays is a perfectly good outcome for footage that needs none, and
          never plan two overlapping overlays in the same region.
        - Every overlay this room plans is a DESIGNED, Remotion-rendered graphic by default. The
          director renders them after the discussion ends; your job here is to argue what each one
          should look like, not whether one is worth rendering. The alternative a deterministic
          step falls back to is `drawtext` over a `drawbox` — plain type on a translucent
          rectangle, visibly cheaper than anything the room could design — so "just make it text"
          is a decision that needs a specific reason about that specific moment, not a default.
        - The view's "graphicsCanvas" gives the canvas the film is encoded on, the smallest type
          allowed on it ("minFontPx") and, when available, "stills.seen": what a vision model saw
          behind each band (Clean or Busy, Light or Dark, a face, a product, existing text). Argue
          placement from it: keep a title off a face or a product, and say which "textSize" word
          ("Small", "Medium", "Large", "XLarge") a title needs to read on a phone. Never quote
          its numbers.
        - Keep every turn SHORT — a few sentences of prose, not an essay. This is a live
          discussion, not a final report.
        - Do NOT output JSON, markdown code fences, or any structured format in this discussion —
          plain conversational prose only. A separate call, made after this discussion ends,
          produces the final structured plan (and is the only place a rendered graphic asset may
          be produced — do not attempt to use sandbox or render tools during this discussion).

        ## Tracked screen inserts — the room's SECOND decision

        The view may also carry an "insertRegions" list: uniform-colour screen plates tracked in
        the footage itself (a green screen inside a phone held in frame, a laptop display on a
        desk), each with a short opaque id such as "r0", a "conf" bucket (high/medium/low), a
        "size" bucket, a "motion" word, a "surface" word (Flat/Curved) and an "aspect" number.
        Deciding these is part of THIS room's job, not a separate step's — a plate left unplanned
        stays a blank green rectangle in the finished film, which is the single most visible defect
        this room can ship.

        - An insert is a short scene the DIRECTOR renders after this discussion ends and which a
          deterministic step then warps frame by frame onto the plate's own tracked corners. Argue
          here about WHICH regions deserve one and WHAT each should show; the render itself, and
          the storage key it produces, belong to the synthesis call.
        - Reference ONLY ids from "insertRegions" ("r0", "r1", ...). A region id is a different
          kind of id from a placement id — never use a "p{n}" as a region or a "r{n}" as an
          overlay anchor.
        - Prefer regions whose "conf" reads "high" or "medium"; a "low" one is usually a false
          positive (green foliage, a plant behind a desk) and is dropped downstream anyway.
          "surface": "Curved" means the plate bends — content with a lot of fine detail near its
          edges reads worse there than something simple and central.
        - Never plan two inserts on the same region, and never put an overlay on a shot whose
          plate you are filling — the insert IS that shot's graphic.
        - Zero inserts is a valid outcome when no region is worth filling, exactly as zero
          overlays is. Say so explicitly if that is the room's conclusion, so the director does not
          have to guess.

        ## Long footage

        When the view has an "outline", the footage is long: the view lists every phrase of the whole film under outline.phrases
        (ids like "t12-t30"), every shot as ranges under outline.shots, and the lists you depend on (clips, tracks, placements,
        offers) in full. Every id in those ranges is valid; use them as listed. If you have ReadVideoSection, ListVideoSfxClips or
        ListVideoMusic, call them when you need a full list. Never ask for a bigger limit or for less footage.
        """;

    // The read-only room-turn tool allowlist: ProjectRead + WorkflowControl function names, taken
    // from the same ToolGroupCatalog both AgentToolProvider and the seeder's display metadata
    // derive from, so this subset can never drift from the catalog's definition of "read-only".
    private static readonly HashSet<string> RoomTurnToolAllowlist = new(
        ToolGroupCatalog.FunctionNamesFor(ToolGroup.ProjectRead)
            .Concat(ToolGroupCatalog.FunctionNamesFor(ToolGroup.WorkflowControl))
            // Free, read-only windows onto the run's own analysis: the outline's speech and events for a turn that needs them.
            .Concat(ToolGroupCatalog.FunctionNamesFor(ToolGroup.EventsRead))
            .Concat(ToolGroupCatalog.FunctionNamesFor(ToolGroup.FootageRead)),
        StringComparer.Ordinal);

    private readonly IMotionGraphicsPlacementAnnotator _placementAnnotator;

    public GraphicsRoomStepExecutor(
        IAgentChatClientProvider chatClients,
        IAgentToolProvider toolProvider,
        IAgentRegistry agentRegistry,
        IProjectFileWorkspace workspace,
        IWorkflowExecutionContextAccessor executionContextAccessor,
        IMotionGraphicsPlacementAnnotator placementAnnotator,
        ILogger<GraphicsRoomStepExecutor> logger,
        IDecisionGate? decisionGate = null,
        IInferenceProviderResolver? decisionResolver = null,
        IDecisionClientFactory? decisionClientFactory = null)
        : base(chatClients, toolProvider, agentRegistry, workspace, executionContextAccessor, logger,
            decisionGate, decisionResolver, decisionClientFactory)
    {
        _placementAnnotator = placementAnnotator;
    }

    public override StepType StepType => StepType.GraphicsRoom;

    protected override string RoomKind => "GraphicsRoom";

    protected override string RoomDisplayName => "Graphics room";

    protected override string FailureCodePrefix => "GRAPHICS_ROOM";

    protected override string ConfigPropertyName => "GraphicsRoomConfigJson";

    protected override string? GetConfigJson(WorkflowStep step) => step.GraphicsRoomConfigJson;

    protected override IRoomStepConfig? DeserializeConfig(string json) =>
        JsonSerializer.Deserialize<GraphicsRoomStepConfig>(json, ConfigJsonOptions);

    /// <summary>
    /// Both id vocabularies this room decides over: the <c>p{n}</c> overlay placements AND the
    /// <c>r{n}</c> tracked insert regions. Convergence detection and the base's empty-view check
    /// both read this set, so a view offering only plates (graphics placements switched off,
    /// insert tracking on) must still put the room to work rather than short-circuit to an empty
    /// plan. The two kinds are separated again in <see cref="FilterToOfferedIds"/>, which is where
    /// membership actually gates anything.
    /// </summary>
    protected override HashSet<string> ExtractOfferedIds(JsonNode? root) =>
        new(IdsFrom(root, "placements").Concat(IdsFrom(root, "insertRegions")), StringComparer.Ordinal);

    private static List<string> IdsFrom(JsonNode? root, string arrayName)
    {
        var ids = new List<string>();
        if (root?["view"]?[arrayName] is not JsonArray items)
            return ids;

        foreach (JsonNode? item in items)
        {
            string? id = item?["id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id))
                ids.Add(id);
        }

        return ids;
    }

    protected override IReadOnlyList<string> ExtractIdMentions(string? text, IReadOnlySet<string> offeredIds) =>
        GraphicsRoomGroupChatManager.ExtractOfferedIdMentions(text, offeredIds);

    protected override string NoOfferedIdsMessage =>
        "The resolved view offers no overlay-placement ids and no tracked insert region ids to decide over.";

    /// <summary>
    /// Unlike the edit room (where a view with nothing to keep is a genuine upstream error), a
    /// video whose analysis offered zero overlay-placement candidates AND zero tracked insert
    /// regions legitimately warrants zero graphics — the same graceful outcome the solo planner's "output an empty overlays list"
    /// rule and <c>VideoCompileStepExecutor</c>'s no-graphics degrade already model. Completing
    /// with an empty plan (rather than failing) keeps a graphics-room workflow runnable on such
    /// footage.
    /// </summary>
    protected override MotionGraphicsPlanOutput BuildDecisionForEmptyView() => new()
    {
        Overlays = [],
        Inserts = [],
        PlanRationale = "No overlay-placement candidates and no tracked insert regions were offered by the analysis view, so no graphics were planned."
    };

    /// <summary>
    /// Run a60d5aae: the analysis view was over its size budget, its placements were left out, and
    /// this room answered "empty-view" with nothing on the run page saying why — the review loop then
    /// retried the edit room, which could never bring a title back. When the analysis step recorded
    /// that it left placements (or tracked screens) out for budget AND this plan has none of that
    /// kind, the room's output carries a top-level <c>warnings</c> entry
    /// (<c>graphics_nothing_offered</c>) naming the cause and the fix, so the customer and the
    /// reviewer see the real reason. Absent otherwise, so every other output keeps its exact shape.
    /// </summary>
    private static JsonObject? BuildNothingOfferedFindings(MotionGraphicsPlanOutput decision, JsonNode? viewRoot)
    {
        if (viewRoot?["warnings"] is not JsonArray analysisWarnings)
            return null;

        var reasons = new List<string>();
        foreach (JsonNode? warning in analysisWarnings)
        {
            string? code = warning?["code"] is JsonValue c && c.TryGetValue(out string? text) ? text : null;
            string? message = warning?["message"] is JsonValue m && m.TryGetValue(out string? body) ? body : null;
            bool relevant = code switch
            {
                VideoAnalyzeStepExecutor.PlacementsSuppressedWarning => (decision.Overlays?.Count ?? 0) == 0,
                "insert_regions_suppressed" => (decision.Inserts?.Count ?? 0) == 0,
                _ => false
            };
            if (relevant && !string.IsNullOrWhiteSpace(message))
                reasons.Add(message);
        }

        if (reasons.Count == 0)
            return null;

        return new JsonObject
        {
            ["warnings"] = new JsonArray(new JsonObject
            {
                ["code"] = "graphics_nothing_offered",
                ["message"] = "The graphics step was offered nothing to work with. " + string.Join(" ", reasons)
            })
        };
    }

    protected override string RoomCharterPrompt => GraphicsRoomCharterPrompt;

    protected override AgentType SeatAgentType => AgentType.MotionGraphicsPlanner;

    protected override AgentType DirectorAgentType => AgentType.MotionGraphicsDirector;

    protected override AgentType SoloFallbackAgentType => AgentType.MotionGraphicsPlanner;

    protected override string SeatRoleNoun => "motion-graphics artist";

    protected override string DirectorTurnDirective =>
        "You are now speaking as Director, the lead motion-graphics artist moderating this " +
        "graphics room. Review the discussion so far. If the room has converged on a coherent " +
        "plan — BOTH which overlay placements to use and which tracked insert regions to fill, " +
        "either of which may legitimately be none — end this turn with the literal token " +
        "ROOM_DECIDED followed by a one- or two-sentence summary of what was agreed. If insert " +
        "regions were offered and nobody has argued about them yet, that is not convergence: say " +
        "so and ask for a read on them. Otherwise, give brief guidance to help the artists " +
        "converge. Keep this turn short.";

    protected override string SynthesisSchemaInstruction =>
        "output ONLY valid JSON matching the MotionGraphicsPlanOutput schema, covering BOTH the " +
        "overlays and the tracked screen inserts the room settled on (either list may be empty " +
        "when the room agreed none are warranted). For every insert the room did settle on, " +
        "render its scene now with the sandbox/Remotion tools and use the storage key that render " +
        "actually returned — an insert without one is dropped, leaving a blank plate in the film.";

    protected override string BuildTurnProgressLabel(bool isDirector, string seatName, int turnNumber, int maxTurns) =>
        isDirector
            ? "Director is reviewing"
            : $"{seatName} is proposing overlays (turn {turnNumber}/{maxTurns})";

    protected override void NormalizeDecision(MotionGraphicsPlanOutput decision)
    {
        decision.Overlays ??= [];
        decision.Inserts ??= [];
    }

    /// <summary>An empty overlay plan is a valid synthesis outcome — never retried.</summary>
    protected override string? RejectFreshSynthesis(MotionGraphicsPlanOutput decision) => null;

    // -----------------------------------------------------------------
    // Deterministic validation — never trust the model: any overlay whose PlacementId isn't in the
    // offered p{n} set is dropped (recorded in the output's room.droppedSpanCount), the identical
    // discipline VideoCompileStepExecutor applies again at compile time (unknown_placement).
    // -----------------------------------------------------------------

    protected override RoomFilterResult FilterToOfferedIds(MotionGraphicsPlanOutput decision, HashSet<string> offeredIds, JsonNode? viewRoot)
    {
        decision.Overlays ??= [];
        decision.Inserts ??= [];

        // The two id kinds are filtered against their OWN offered list, re-read from the view,
        // rather than against the combined set ExtractOfferedIds returns — otherwise an overlay
        // anchored to "r0" or an insert pointing at "p3" would pass membership while naming the
        // wrong kind of thing entirely.
        var placementIds = new HashSet<string>(IdsFrom(viewRoot, "placements"), StringComparer.Ordinal);
        var regionIds = new HashSet<string>(IdsFrom(viewRoot, "insertRegions"), StringComparer.Ordinal);
        var eventIds = new HashSet<string>(IdsFrom(viewRoot, "events"), StringComparer.Ordinal);

        // viewRoot is the node ExtractOfferedIds was just built from, so it is non-null on every
        // live path; the fallback keeps the pre-inserts behavior for overlays rather than dropping
        // a whole valid plan, and drops inserts, which VideoCompileStepExecutor would reject as
        // unknown_region_id anyway.
        if (viewRoot is null)
            placementIds = offeredIds;

        int before = decision.Overlays.Count + decision.Inserts.Count;
        decision.Overlays = decision.Overlays
            .Where(o => (!string.IsNullOrEmpty(o.PlacementId) && placementIds.Contains(o.PlacementId))
                || (string.IsNullOrEmpty(o.PlacementId) && !string.IsNullOrEmpty(o.EventId) && eventIds.Contains(o.EventId)))
            .ToList();
        decision.Inserts = decision.Inserts
            .Where(i => viewRoot is not null && !string.IsNullOrEmpty(i.RegionId) && regionIds.Contains(i.RegionId))
            .ToList();
        return new RoomFilterResult(before - decision.Overlays.Count - decision.Inserts.Count, null);
    }

    /// <summary>
    /// One policy retry when a rendered overlay graphic was measured below the legibility floor
    /// (docs/design/graphics-see-and-size.md): the director is told which placements and the size to
    /// reach, and may re-render. A graphic still below it is replaced by its text at compile.
    /// </summary>
    protected override string? CheckDecisionPolicy(
        MotionGraphicsPlanOutput decision, JsonNode? viewRoot, IReadOnlyList<ChatMessage>? transcript, StepExecutionContext context) =>
        OverlayLegibilityFindings.BelowFloorFeedback(decision, context.OverlayLegibility);

    /// <summary>
    /// The engine's measurement of each rendered overlay, as <c>overlayLegibility</c> beside the plan,
    /// plus the <c>graphics_nothing_offered</c> warning (see <see cref="BuildNothingOfferedFindings"/>).
    /// </summary>
    protected override JsonObject? BuildDecisionFindings(
        MotionGraphicsPlanOutput decision, JsonNode? viewRoot, StepExecutionContext context, bool policyRetried)
    {
        JsonObject? findings = BuildNothingOfferedFindings(decision, viewRoot);
        if (OverlayLegibilityFindings.BuildFor(decision, context.OverlayLegibility) is { } records)
        {
            findings ??= new JsonObject();
            findings[OverlayLegibilityFindings.Key] = records;
        }

        return findings;
    }

    /// <summary>A filtered-to-empty plan is still valid — zero overlays never degrades the room.</summary>
    protected override string? RejectFilteredDecision(MotionGraphicsPlanOutput decision, bool solo) => null;

    // -----------------------------------------------------------------
    // The convening gate: deliberately NOT supplied by this room.
    //
    // The three overrides below exist to say that out loud, with the reason, so the omission reads
    // as a considered boundary rather than an oversight (decisions.md §7). The seams' base default
    // is already null, so they change no behaviour — they document it, and RoomConveneGateTests
    // pins it.
    //
    // Why: a System One choice/score/noul question can only *produce* a decision that decomposes
    // into a FIXED option set, and a MotionGraphicsPlanOutput is not one — it is an OPEN,
    // GENERATIVE list. The room starts from an offered <c>p{n}</c> placement vocabulary, but it
    // does not choose among those candidates: it authors zero or more overlays, each with its own
    // free text and enum-word duration/emphasis, and (Phase 3) a <c>MotionGraphicsOverlay</c> may
    // even carry a self-rendered asset key the room had to design and render itself. The output
    // space is unbounded prose plus rendered assets, so there is no option set to ask about. A
    // question set here could only gate on a PROXY for the decision ("how many overlays?"), and
    // gating on a proxy is worse than not gating at all: it would trade the room's real decision
    // for a merely correlated one, while looking like the room had been gated. The ColorGrade
    // room — whose decision IS a fixed set of enum words — is the one the gate ships for (see
    // ColorGradeRoomStepExecutor).
    //
    // GraphicsRoomStepConfig.ConveneGate still exists and still defaults Off, so turning it on here
    // is a no-op rather than an error.
    // -----------------------------------------------------------------

    /// <summary>
    /// No convening question set — see the region comment above. This room's decision is an open,
    /// generative overlay list, not a fixed option set, so it has no questions a System One model
    /// could answer in place of the room.
    /// </summary>
    protected override IReadOnlyList<DecisionQuestion>? BuildConveneQuestions(JsonNode? viewRoot) => null;

    /// <summary>
    /// No convening agenda — there is no question set for it to narrate, per
    /// <see cref="BuildConveneQuestions"/> above.
    /// </summary>
    protected override string? BuildConveneAgenda(JsonNode? viewRoot, IReadOnlyDictionary<string, DecisionAnswer> answers) => null;

    /// <summary>
    /// Nothing to shadow — see the region comment above. Recording an observation against a proxy
    /// question would put a number in the calibration view that is not a measurement of this room's
    /// decision, which is exactly the confusion the gate exists to avoid.
    /// </summary>
    protected override IReadOnlyList<DecisionGateQuestion>? BuildShadowQuestions(MotionGraphicsPlanOutput decision) => null;

    protected override RoomGroupChatManager CreateManager(
        IReadOnlyList<AIAgent> agents, IRoomStepConfig config, IReadOnlySet<string> offeredIds,
        IDecisionClient? decisionClient) =>
        new GraphicsRoomGroupChatManager(agents, (GraphicsRoomStepConfig)config, offeredIds, "Director", decisionClient, Logger);

    /// <summary>
    /// Restricts every in-room agent (seats AND the director's room-participant instance) to the
    /// read-only ProjectRead + WorkflowControl + EventsRead + FootageRead subset of its grant. The backing agent types carry
    /// the full sandbox+Remotion+render tool set (see ToolGroupCatalog's MotionGraphicsDirector
    /// rationale) so the standalone synthesis call can render a real overlay asset — but a room
    /// turn is a short free-form prose contribution capped at ~220 output tokens, where a sandbox
    /// tool call is at best wasted work and at worst a place for view-derived text to reach code
    /// execution with no structured-output validation in between.
    /// </summary>
    protected override IReadOnlyList<AIFunction> GetRoomTurnTools(AgentType agentType) =>
        ToolProvider.GetTools(agentType)
            .Where(t => RoomTurnToolAllowlist.Contains(t.Name))
            .ToList();

    /// <summary>
    /// Enriches the analyze step's <c>view.placements</c> with the same <c>inEdit</c> survival
    /// annotation a solo <see cref="AgentType.MotionGraphicsPlanner"/> Agent step's prompt gets
    /// (see <see cref="IMotionGraphicsPlacementAnnotator"/>) — installed as a prompt-output
    /// override, which the base's view resolution reads through
    /// <see cref="StepExecutionContext.OutputForPrompt"/>. Soft-fails to the plain view by the
    /// annotator's own never-fails contract.
    /// </summary>
    protected override Task PrepareViewAsync(StepExecutionContext context) =>
        _placementAnnotator.AnnotateAsync(context, context.CancellationToken);
}
