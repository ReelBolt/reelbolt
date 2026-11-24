using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Executes <see cref="StepType.DailiesRoom"/> steps (issue #93 phase 4): several
/// <see cref="AgentType.TakeSelector"/>-role judge seats plus an
/// <see cref="AgentType.DailiesDirector"/> moderator converse in a live group chat over a
/// MULTI-SOURCE <c>VideoAnalyze</c> view — one analyzed source per generated take, each named by
/// its <c>v{n}t{k}</c> clip id in <c>view.sources[].clipId</c> — then the director emits ONE
/// schema-validated <see cref="TakeSelectionOutput"/> in a normal structured call OUTSIDE the chat
/// loop. <see cref="VideoCompileStepExecutor"/>'s <c>TakeSelection</c> resolution consumes it to
/// place only the chosen take of each planned shot. See docs/video-generation.md "Dailies:
/// choosing between takes".
///
/// <para>
/// All room mechanics live in the shared <see cref="RoomStepExecutorBase{TDecision}"/> — this
/// class binds only the dailies room's domain: its config column, its take-id vocabulary, its
/// charter/director prompts, its seat/director/solo agent types, and its validation shape. The
/// id-anchored discipline is identical to the edit room's: every selected take id not in the
/// offered set is dropped, and a selection naming TWO takes of the same planned shot keeps only
/// the first (the compile step places at most one take per shot). An EMPTY selection is a valid
/// outcome (the compile step then keeps each shot's first delivered take), so — like the graphics
/// room and unlike the edit room — an empty decision never triggers the solo fallback. A view with
/// zero takes completes with an empty selection instead of failing.
/// </para>
/// </summary>
public class DailiesRoomStepExecutor : RoomStepExecutorBase<TakeSelectionOutput>
{
    private const string DailiesRoomCharterPrompt =
        """
        You are participating in a live "dailies room" discussion between several judges and a
        director, deciding which GENERATED take of each planned shot the compile step should
        place. The bounded view — a "sources" list where each entry carries a "clipId" (an opaque
        take id such as "v1t1", "v1t2" or "v2") and a "src" index, plus "shots" measured from each
        take with descriptor WORDS (motion, camera move, colour temperature, tone, saturation,
        exposure, sharpness when measured) and, when available, a short caption — was given to you
        as the first message in this conversation. Every participant sees the exact same view.
        Takes sharing a "v{n}" prefix ("v1t1", "v1t2") are alternatives for the SAME planned shot.

        ## Rules — hard constraints, not suggestions

        - Ground every argument in take ids that appear in the "sources" list you were given and
          in the view's MEASURED words and captions. Never invent a take id or a measurement, and
          never treat a shot/silence/segment/placement/region id ("s2", "g1", "t3", "p1", "r0")
          as a take — those are different kinds of id and are never a valid choice.
        - NEVER mention, estimate, or output a score, a number, a percentage, a frame index, a
          timestamp, a duration, a resolution, a URL, a file path or a storage key. The
          startSec/endSec values on each shot are for READING only — to tell shots apart — never
          echo them. The comparison is made in words.
        - Argue for AT MOST ONE take per planned shot. Agreeing that a shot's takes are
          indistinguishable, and leaving it unselected, is a perfectly good outcome.
        - Keep every turn SHORT — a few sentences of prose, not an essay. This is a live
          discussion, not a final report.
        - Do NOT output JSON, markdown code fences, or any structured format in this
          discussion — plain conversational prose only. A separate call, made after this
          discussion ends, produces the final structured selection.
        """;

    private static readonly Regex TakeIdPattern = new(@"^v(\d{1,4})(?:t\d{1,2})?$", RegexOptions.Compiled);

    public DailiesRoomStepExecutor(
        IAgentChatClientProvider chatClients,
        IAgentToolProvider toolProvider,
        IAgentRegistry agentRegistry,
        IProjectFileWorkspace workspace,
        IWorkflowExecutionContextAccessor executionContextAccessor,
        ILogger<DailiesRoomStepExecutor> logger,
        IDecisionGate? decisionGate = null,
        IInferenceProviderResolver? decisionResolver = null,
        IDecisionClientFactory? decisionClientFactory = null)
        : base(chatClients, toolProvider, agentRegistry, workspace, executionContextAccessor, logger,
            decisionGate, decisionResolver, decisionClientFactory)
    {
    }

    public override StepType StepType => StepType.DailiesRoom;

    protected override string RoomKind => "DailiesRoom";

    protected override string RoomDisplayName => "Dailies room";

    protected override string FailureCodePrefix => "DAILIES_ROOM";

    protected override string ConfigPropertyName => "DailiesRoomConfigJson";

    protected override string? GetConfigJson(WorkflowStep step) => step.DailiesRoomConfigJson;

    protected override IRoomStepConfig? DeserializeConfig(string json) =>
        JsonSerializer.Deserialize<DailiesRoomStepConfig>(json, ConfigJsonOptions);

    /// <summary>
    /// The offered vocabulary is the set of generated-take ids the analyze step tagged its sources
    /// with (<c>view.sources[].clipId</c>) — the ONLY ids a selection may name. A source with no
    /// clipId (a real clip analyzed alongside) is not a take and is not offered.
    /// </summary>
    protected override HashSet<string> ExtractOfferedIds(JsonNode? root) =>
        new(ExtractOfferedTakeIds(root), StringComparer.Ordinal);

    internal static IEnumerable<string> ExtractOfferedTakeIds(JsonNode? root)
    {
        if (root?["view"]?["sources"] is not JsonArray sources)
            yield break;

        foreach (JsonNode? item in sources)
        {
            string? id = item?["clipId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(id) && TakeIdPattern.IsMatch(id))
                yield return id;
        }
    }

    protected override IReadOnlyList<string> ExtractIdMentions(string? text, IReadOnlySet<string> offeredIds) =>
        DailiesRoomGroupChatManager.ExtractOfferedIdMentions(text, offeredIds);

    protected override string NoOfferedIdsMessage =>
        "The resolved view offers no generated takes (no sources[].clipId) to choose between.";

    /// <summary>
    /// A view with zero takes offers nothing to choose — completing with an empty selection
    /// (rather than failing) keeps the workflow runnable, and downstream the compile step simply
    /// keeps each shot's first delivered take, exactly as it does with no selection step at all.
    /// </summary>
    protected override TakeSelectionOutput BuildDecisionForEmptyView() => new()
    {
        Selected = [],
        PlanRationale = "No generated takes were offered by the analysis view, so no take selection was made."
    };

    protected override string RoomCharterPrompt => DailiesRoomCharterPrompt;

    protected override AgentType SeatAgentType => AgentType.TakeSelector;

    protected override AgentType DirectorAgentType => AgentType.DailiesDirector;

    protected override AgentType SoloFallbackAgentType => AgentType.TakeSelector;

    /// <summary>
    /// The judges and the director are told which of the project's people and objects the takes show
    /// (docs/design/avatar-objects.md "Avatars and objects in planned shots"): the names and kinds in
    /// <c>view.sources[].subjects</c>, as a <c>[PROJECT SUBJECTS]</c> block. Nothing when no take shows one.
    /// </summary>
    protected override IDisposable? EnterViewScope(JsonNode? viewRoot) =>
        Services.Avatars.ProjectSubjectsAmbient.EnterJudgedTakes(Services.Avatars.ProjectSubjectsBlock.ShownIn(viewRoot));

    protected override string SeatRoleNoun => "dailies judge";

    protected override string DirectorTurnDirective =>
        "You are now speaking as Director, moderating this dailies room. Review the discussion so " +
        "far. If the room has converged on one take per planned shot — including possibly " +
        "agreeing that some shot's takes are indistinguishable and may be left unselected — end " +
        "this turn with the literal token ROOM_DECIDED followed by a one- or two-sentence summary " +
        "of which takes were chosen. Otherwise, give brief guidance to help the judges converge. " +
        "Keep this turn short.";

    protected override string SynthesisSchemaInstruction =>
        "output ONLY valid JSON matching the TakeSelectionOutput schema (an empty `selected` list " +
        "is valid when the room left every shot to its first take).";

    protected override string BuildTurnProgressLabel(bool isDirector, string seatName, int turnNumber, int maxTurns) =>
        isDirector
            ? "Director is reviewing"
            : $"{seatName} is comparing takes (turn {turnNumber}/{maxTurns})";

    protected override void NormalizeDecision(TakeSelectionOutput decision)
    {
        decision.Selected ??= [];
        decision.PlanRationale ??= string.Empty;
        foreach (TakeSelection selection in decision.Selected)
        {
            selection.TakeId ??= string.Empty;
            selection.Reason ??= string.Empty;
        }
    }

    /// <summary>Every fresh synthesis is acceptable — an empty selection is a first-class outcome.</summary>
    protected override string? RejectFreshSynthesis(TakeSelectionOutput decision) => null;

    /// <summary>
    /// Deterministic validation — never trust the model: a selection whose take id is not in the
    /// offered <c>v{n}t{k}</c> set is dropped, and a SECOND selection for an already-chosen planned
    /// shot (same <c>v{n}</c> prefix) is dropped too, so the decision carries at most one take per
    /// shot. Both counts are surfaced on the output's <c>room</c> metadata.
    /// </summary>
    protected override RoomFilterResult FilterToOfferedIds(TakeSelectionOutput decision, HashSet<string> offeredIds, JsonNode? viewRoot)
    {
        decision.Selected ??= [];
        int droppedUnoffered = 0;
        int droppedDuplicateShot = 0;
        int droppedMismatched = 0;
        HashSet<string> rejectedTakes = MismatchedTakesWithAlternative(viewRoot);
        var seenShots = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<TakeSelection>(decision.Selected.Count);
        foreach (TakeSelection selection in decision.Selected)
        {
            string id = selection.TakeId?.Trim() ?? string.Empty;
            if (!offeredIds.Contains(id))
            {
                droppedUnoffered++;
                continue;
            }

            // A take the object check found to be another object is never picked while the shot has a take that is not.
            if (rejectedTakes.Contains(id))
            {
                droppedMismatched++;
                continue;
            }

            Match match = TakeIdPattern.Match(id);
            string shotKey = match.Success ? match.Groups[1].Value : id;
            if (!seenShots.Add(shotKey))
            {
                droppedDuplicateShot++;
                continue;
            }

            selection.TakeId = id;
            kept.Add(selection);
        }

        decision.Selected = kept;

        var extraMeta = new JsonObject { ["droppedDuplicateShotSelectionCount"] = droppedDuplicateShot };
        if (droppedMismatched > 0)
            extraMeta["droppedMismatchedTakeCount"] = droppedMismatched;
        return new RoomFilterResult(droppedUnoffered + droppedDuplicateShot + droppedMismatched, extraMeta);
    }

    /// <summary>
    /// The take ids of <c>view.sources[]</c> whose subject check came back <c>mismatch</c> (docs/design/avatar-objects.md
    /// "Object fidelity"), limited to takes whose shot (the <c>v{n}</c> number) has another take that is not a mismatch: a
    /// shot whose every take mismatches keeps its choice, so no shot is emptied.
    /// </summary>
    internal static HashSet<string> MismatchedTakesWithAlternative(JsonNode? root)
    {
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        if (root?["view"]?["sources"] is not JsonArray sources)
            return rejected;

        var mismatched = new List<(string Id, string Shot)>();
        var cleanShots = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? item in sources)
        {
            string? id = item?["clipId"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id) || TakeIdPattern.Match(id) is not { Success: true } match)
                continue;

            bool bad = item?["subjects"] is JsonArray subjects
                && subjects.Any(s => s?["fidelity"] is JsonValue f && f.TryGetValue(out string? word) && word == Services.Avatars.SubjectFidelityStatus.Mismatch);
            if (bad)
                mismatched.Add((id, match.Groups[1].Value));
            else
                cleanShots.Add(match.Groups[1].Value);
        }

        foreach ((string id, string shot) in mismatched)
        {
            if (cleanShots.Contains(shot))
                rejected.Add(id);
        }

        return rejected;
    }

    /// <summary>Every filtered decision is acceptable — an empty selection means "first take per shot" downstream.</summary>
    protected override string? RejectFilteredDecision(TakeSelectionOutput decision, bool solo) => null;

    // -----------------------------------------------------------------
    // Convening gate: NOT supplied. A take selection is an id-anchored subset of an offered list
    // that changes with every view, not a fixed option set a System One model could answer in
    // place of the room — the same structural reason the edit room overrides these to null. The
    // ColorGrade room is the one the gate ships for. DailiesRoomStepConfig.ConveneGate still exists
    // and still defaults Off, so turning it on here is a no-op rather than an error.
    // -----------------------------------------------------------------

    protected override IReadOnlyList<DecisionQuestion>? BuildConveneQuestions(JsonNode? viewRoot) => null;

    protected override string? BuildConveneAgenda(JsonNode? viewRoot, IReadOnlyDictionary<string, DecisionAnswer> answers) => null;

    protected override IReadOnlyList<DecisionGateQuestion>? BuildShadowQuestions(TakeSelectionOutput decision) => null;

    protected override RoomGroupChatManager CreateManager(
        IReadOnlyList<AIAgent> agents, IRoomStepConfig config, IReadOnlySet<string> offeredIds,
        IDecisionClient? decisionClient) =>
        new DailiesRoomGroupChatManager(agents, (DailiesRoomStepConfig)config, offeredIds, "Director", decisionClient, Logger);
}
