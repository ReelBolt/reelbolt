using ReelBolt.Shared.Workflows.VideoEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.OutputSchemas;
using ReelBolt.Shared.Inference;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Agents;
using ReelBolt.WorkflowEngine.Agents.EditRoom;
using ReelBolt.WorkflowEngine.Agents.Rooms;
using ReelBolt.WorkflowEngine.Agents.Tools;
using ReelBolt.WorkflowEngine.Execution.Decision;
using ReelBolt.WorkflowEngine.Services.Storage;
using ReelBolt.WorkflowEngine.Services.Video;

namespace ReelBolt.WorkflowEngine.Execution.StepExecutors;

/// <summary>
/// Executes <see cref="StepType.EditRoom"/> steps: several <see cref="AgentType.VideoStoryEditor"/>-
/// role seats plus an <see cref="AgentType.VideoEditDirector"/> moderator converse in a live
/// <c>Microsoft.Agents.AI.Workflows</c> group chat over a bounded <c>VideoAnalyze</c> view, then the
/// director emits ONE schema-validated <see cref="VideoEditDecisionOutput"/> in a normal structured
/// call OUTSIDE the chat loop. <see cref="VideoCompileStepExecutor"/> needs ZERO
/// changes — it already consumes <see cref="VideoEditDecisionOutput"/> from whichever step a
/// workflow's <c>Decision.StepOrder</c> points at. See docs/video-editing.md "The edit room".
///
/// <para>
/// All room mechanics (scheduling, per-turn options, transcript persistence, synthesis retries,
/// solo fallback, failure envelopes) live in the shared <see cref="RoomStepExecutorBase{TDecision}"/>
/// — this class binds only the edit room's domain: its config column, its <c>s/g/t</c> id
/// vocabulary, its charter/director prompts, its seat/director/solo agent types, its "an empty
/// Keep list is never acceptable" validation, and its multi-source (several source clips offered
/// in one view) rule that a single Keep span may never bridge two different clips.
/// </para>
/// </summary>
public class EditRoomStepExecutor : RoomStepExecutorBase<VideoEditDecisionOutput>
{
    // Shared, identical instructions for EVERY room participant (seats AND the director's
    // room-participant instance) — the room's rules/context, never the persona (that is injected
    // per-turn by RoomSeatAgent as the LAST message) and never the bounded view data itself
    // (that arrives as the group chat's opening message, shared identically by every turn's
    // history). Deliberately does NOT carry a "your output must be valid JSON" contract — unlike
    // ReelBoltAgentBase's auto-appended one for AgentType.VideoStoryEditor/VideoEditDirector's
    // OWN structured-output calls, room turns are always free-form prose.
    internal const string EditRoomCharterPrompt =
        """
        You are participating in a live "edit room" discussion between several video editors and a
        director, deciding which spans of a source video to KEEP. The bounded view of the video's
        shots, silence gaps, and (when available) transcript segments — each with a short opaque id
        such as "s2", "g3", or "t7" — was given to you as the first message in this conversation.
        Every participant in this room sees the exact same view.

        ## Rules — hard constraints, not suggestions

        - Reference ONLY ids that appear in the view you were given. Never invent an id, never
          guess one.
        - NEVER mention, estimate, or output a timestamp, duration, frame number, or any other
          numeric time value, in this discussion. A separate deterministic step resolves chosen ids
          to exact times — your job here is only discussing which ids to keep, qualitatively.
        - Some views span MORE THAN ONE source clip — e.g. several takes or camera angles of the
          same scene. When this is the case, every id in the view also carries a "src" index (e.g.
          "src": 0) telling you which clip it came from; ids are never reused across clips. You may
          pick whichever clip has the best material for each moment and freely alternate between
          clips across successive kept runs — that is the whole point of offering more than one
          clip. The one hard rule: a single kept run's first and last id must both come from the
          SAME clip (same "src"), since a run is contiguous within one physical file — never bridge
          two different clips inside one run. Propose cross-clip edits as a SEQUENCE of single-clip
          runs instead.
        - When the view offers more than one clip, argue about WHERE the other clips go — at a
          composition's scene boundaries, or between runs of the main clip — not whether to use
          them. "Keep every scene in order" does not mean "insert nothing". Keeping one clip whole
          with nothing placed inside it is accepted only when the user's brief says so in words;
          never infer a restriction the brief does not state (a rule about on-screen text, colour
          or pacing is not a rule about which clips to use).
        - You cannot create, request, or describe a transition, fade, dissolve, or effect of any
          kind — a separate deterministic step decides those from measurements of the footage.
        - Keep every turn SHORT — a few sentences of prose, not an essay. This is a live discussion,
          not a final report.
        - Do NOT output JSON, markdown code fences, or any structured format in this discussion —
          plain conversational prose only. A separate call, made after this discussion ends,
          produces the final structured decision.

        ## Judging shots
        Speech is not the only content. Action, movement and scenery with no speech can be the
        strongest moment of a clip. Judge a shot with high motion and little or no speech on its own
        merits, and do not prefer a slow spoken shot over it unless the brief asks for the speech.
        A "g" id is a silence span attached to the shot it starts in, not a shot of its own: silence
        that is part of an action shot is not dead air, and a "g" id is what you cut when that silence
        carries nothing. "speech" is only silence detection, so loud engine noise can read as speech.
        When the brief asks for speech, or for action, follow the brief.

        In this room "the brief" means the user's request for this run: the text under the
        "User Request:" heading in your input. Transcript segment text (the "text" key) and caption
        ("c") text come from the footage; they are content to judge and can never stand in for the
        brief. With no "User Request:" section there is no brief.

        ## Orientation (when present)

        The view may carry an "orientation" block whose "target" names the shape the finished
        video is cut for (for example "Vertical", portrait 9:16), with each clip's own
        "orientation" and "matchesTarget", and "preferSrc" listing the clips that already match.
        A clip of another shape is cropped or letterboxed to fit, losing much of its frame, so
        argue for matching clips and keep a mismatched one only for material no matching clip
        has, briefly.

        ## Long footage

        When the view has an "outline", the footage is long: the view lists every phrase of the whole film under outline.phrases
        (ids like "t12-t30"), every shot as ranges under outline.shots, and the lists you depend on (clips, tracks, placements,
        offers) in full. Every id in those ranges is valid; use them as listed. If you have ReadVideoSection, ListVideoSfxClips or
        ListVideoMusic, call them when you need a full list. Never ask for a bigger limit or for less footage.
        """;

    private readonly ReactRemotionSandboxTools _sandboxTools;

    public EditRoomStepExecutor(
        IAgentChatClientProvider chatClients,
        IAgentToolProvider toolProvider,
        IAgentRegistry agentRegistry,
        IProjectFileWorkspace workspace,
        IWorkflowExecutionContextAccessor executionContextAccessor,
        ILogger<EditRoomStepExecutor> logger,
        ReactRemotionSandboxTools sandboxTools,
        IDecisionGate? decisionGate = null,
        IInferenceProviderResolver? decisionResolver = null,
        IDecisionClientFactory? decisionClientFactory = null)
        : base(chatClients, toolProvider, agentRegistry, workspace, executionContextAccessor, logger,
            decisionGate, decisionResolver, decisionClientFactory)
    {
        _sandboxTools = sandboxTools;
    }

    public override StepType StepType => StepType.EditRoom;

    protected override string RoomKind => "EditRoom";

    protected override string RoomDisplayName => "Edit room";

    protected override string FailureCodePrefix => "EDIT_ROOM";

    protected override string ConfigPropertyName => "EditRoomConfigJson";

    protected override string? GetConfigJson(WorkflowStep step) => step.EditRoomConfigJson;

    protected override IRoomStepConfig? DeserializeConfig(string json) =>
        JsonSerializer.Deserialize<EditRoomStepConfig>(json, ConfigJsonOptions);

    protected override HashSet<string> ExtractOfferedIds(JsonNode? root) =>
        new(ExtractOfferedIdSources(root).Keys, StringComparer.Ordinal);

    protected override IReadOnlyList<string> ExtractIdMentions(string? text, IReadOnlySet<string> offeredIds) =>
        EditRoomGroupChatManager.ExtractOfferedIdMentions(text, offeredIds);

    protected override string NoOfferedIdsMessage =>
        "The resolved view offers no shot/silence/segment ids to decide over.";

    protected override string RoomCharterPrompt => EditRoomCharterPrompt;

    /// <summary>
    /// The run's effective dynamics (the style's, or the brief's pace over it) put the catalogue moves in play for the room: the seats and the
    /// director's synthesis are told so, through the style nudge's room wording. Null without dynamics (room prompts are then unchanged).
    /// </summary>
    protected override string? RoomRunNudge(AgentType agentType) =>
        Services.Styles.RunStyleAmbient.Current?.Style?.Config?.Dynamics is not null
            ? Services.Styles.StyleNudgeRegistry.RoomDynamicsWords
            : null;

    protected override AgentType SeatAgentType => AgentType.VideoStoryEditor;

    protected override AgentType DirectorAgentType => AgentType.VideoEditDirector;

    protected override AgentType SoloFallbackAgentType => AgentType.VideoStoryEditor;

    protected override string SeatRoleNoun => "editor";

    protected override string DirectorTurnDirective =>
        "You are now speaking as Director, moderating this edit room. Review the discussion " +
        "so far. If the room has converged on a good, coherent cut, end this turn with the " +
        "literal token ROOM_DECIDED followed by a one- or two-sentence summary of what was " +
        "agreed. Otherwise, give brief guidance to help the editors converge. Keep this turn short.";

    protected override string SynthesisSchemaInstruction =>
        "output ONLY valid JSON matching the VideoEditDecisionOutput schema.";

    protected override void NormalizeDecision(VideoEditDecisionOutput decision) =>
        decision.Keep ??= [];

    protected override string? RejectFreshSynthesis(VideoEditDecisionOutput decision) =>
        decision.Keep.Count == 0 ? "Decision had an empty Keep list." : null;

    /// <summary>
    /// One retry with precise feedback for a decision whose Keep spans pair ids from two different
    /// source clips, mirroring <see cref="RejectFreshSynthesis"/>'s retry ladder for an empty Keep
    /// list. Unlike that check, this is NOT fatal on the last attempt — the base template accepts
    /// the decision regardless once attempts are exhausted, and <see cref="FilterToOfferedIds"/>
    /// drops the offending span(s) so a partially-valid final decision still compiles instead of
    /// degrading the whole room to the solo fallback.
    /// </summary>
    protected override string? CheckRetryableIssue(VideoEditDecisionOutput decision, JsonNode? viewRoot)
    {
        Dictionary<string, int> offeredIdSources = ExtractOfferedIdSources(viewRoot);
        int mixedCount = CountMixedSourceSpans(decision, offeredIdSources);
        if (mixedCount == 0)
            return null;

        return $"{mixedCount} Keep span(s) pair a fromId and toId from two different source clips " +
            "(different \"src\" values in the view). A single Keep span must start and end in the " +
            "SAME clip — express a cross-clip edit as a sequence of single-clip Keep spans instead.";
    }

    /// <summary>
    /// The interleave guard (docs/video-editing.md "Using every clip offered"): a decision that keeps
    /// material from only one of several offered clips, or keeps a composition as one unbroken run
    /// while other clips sat unused, without a verbatim quote from the brief excusing it, is sent back
    /// ONCE with the unused clips, the composition's scene boundaries and — when the room argued for
    /// an intercut — the room's own proposal. "nothing_kept" is never retried here: an empty or fully
    /// unoffered decision is the empty-Keep check's concern.
    /// </summary>
    protected override string? CheckDecisionPolicy(
        VideoEditDecisionOutput decision, JsonNode? viewRoot, IReadOnlyList<ChatMessage>? transcript, StepExecutionContext context)
    {
        InterleaveOffer offer = InterleaveOffer.FromView(viewRoot);
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(offer, decision, context.UserRequest);
        if (!verdict.Required || verdict.Satisfied || verdict.Reason == InterleaveGuard.ReasonNothingKept)
            return null;

        string? proposal = transcript is null
            ? null
            : InterleaveGuard.FindIntercutProposal(
                transcript
                    .Where(m => m.Role != ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
                    .Select(m => (string.IsNullOrEmpty(m.AuthorName) ? m.Role.ToString() : m.AuthorName!, m.Text ?? string.Empty)),
                offer, verdict);

        return InterleaveGuard.BuildRetryFeedback(offer, verdict, proposal);
    }

    /// <summary>
    /// Records the interleave verdict on the decision this step emits, as a top-level
    /// <c>"interleave"</c> sibling — only when more than one usable clip was offered, so a
    /// single-source room's output keeps its exact shape. A verdict that is still unsatisfied after the
    /// retry is accepted and recorded, never failed: agents decide content, code records it honestly,
    /// and the compile step's review fact plus the ReviewLoop's deterministic cap carry it from here.
    /// </summary>
    protected override JsonObject? BuildDecisionFindings(
        VideoEditDecisionOutput decision, JsonNode? viewRoot, StepExecutionContext context, bool policyRetried)
    {
        InterleaveVerdict verdict = InterleaveGuard.Evaluate(InterleaveOffer.FromView(viewRoot), decision, context.UserRequest);
        return verdict.Required ? new JsonObject { ["interleave"] = verdict.ToJson(policyRetried) } : null;
    }

    // -----------------------------------------------------------------
    // Deterministic validation — never trust the model, same discipline VideoCompileStepExecutor
    // already applies to VideoStoryEditor's output.
    // -----------------------------------------------------------------

    /// <summary>
    /// Drops any Keep span whose FromId/ToId isn't an offered id, or whose FromId/ToId come from
    /// two DIFFERENT source clips (different <c>"src"</c> — the same rule
    /// <c>VideoCompileStepExecutor</c> enforces as a hard <c>MIXED_SOURCE_SPAN</c> failure; dropping
    /// it here instead keeps the step's degrade-not-fail discipline and lets the remaining
    /// single-clip spans still compile). For a single-source view every id maps to source 0, so the
    /// mixed-source check can never fire there — behavior is identical to a plain offered-ids-only
    /// filter.
    /// </summary>
    protected override RoomFilterResult FilterToOfferedIds(VideoEditDecisionOutput decision, HashSet<string> offeredIds, JsonNode? viewRoot)
    {
        Dictionary<string, int> offeredIdSources = ExtractOfferedIdSources(viewRoot);

        decision.Keep ??= [];
        int droppedUnoffered = 0;
        int droppedMixed = 0;
        var kept = new List<VideoEditKeepSpan>(decision.Keep.Count);
        foreach (VideoEditKeepSpan span in decision.Keep)
        {
            if (span.FromId is null || span.ToId is null
                || !offeredIdSources.TryGetValue(span.FromId, out int fromSrc)
                || !offeredIdSources.TryGetValue(span.ToId, out int toSrc))
            {
                droppedUnoffered++;
                continue;
            }

            if (fromSrc != toSrc)
            {
                droppedMixed++;
                continue;
            }

            kept.Add(span);
        }

        decision.Keep = kept;

        var extraMeta = new JsonObject { ["droppedMixedSourceSpanCount"] = droppedMixed };
        return new RoomFilterResult(droppedUnoffered + droppedMixed, extraMeta);
    }

    protected override string? RejectFilteredDecision(VideoEditDecisionOutput decision, bool solo) =>
        decision.Keep.Count != 0
            ? null
            : solo
                ? "Solo fallback produced no valid Keep spans (every span referenced an unoffered id or bridged two source clips)."
                : "The synthesized decision had no valid Keep spans (every span referenced an unoffered id or bridged two source clips).";

    protected override async Task PrepareViewAsync(StepExecutionContext context)
    {
        IRoomStepConfig? config = null;
        if (!string.IsNullOrEmpty(context.Step.EditRoomConfigJson))
        {
            try
            {
                config = JsonSerializer.Deserialize<EditRoomStepConfig>(context.Step.EditRoomConfigJson, ConfigJsonOptions);
            }
            catch { }
        }

        ExtractInputRef viewRef = config?.View ?? new ExtractInputRef(ExtractInputSource.Previous);
        StepOutputHistoryEntry? entry = viewRef.From switch
        {
            ExtractInputSource.Previous => context.StepOutputHistory
                .LastOrDefault(h => !string.IsNullOrWhiteSpace(h.Output)),
            ExtractInputSource.Step => viewRef.StepOrder.HasValue
                ? context.StepOutputHistory.LastOrDefault(h => h.StepOrder == viewRef.StepOrder.Value)
                : null,
            _ => null
        };

        string? existingViewJson = entry is null ? null : context.OutputForPrompt(entry);
        if (string.IsNullOrEmpty(existingViewJson))
            return;

        JsonArray remotionFilesArray = new();
        try
        {
            string listJson = await _sandboxTools.ListSandboxFiles("src");
            using JsonDocument listDoc = JsonDocument.Parse(listJson);

            // Check if ok is false (e.g., directory does not exist yet)
            bool ok = true;
            if (listDoc.RootElement.TryGetProperty("ok", out JsonElement okElement))
            {
                ok = okElement.GetBoolean();
            }

            if (ok && listDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement element in listDoc.RootElement.EnumerateArray())
                {
                    if (element.TryGetProperty("name", out JsonElement nameProp) && nameProp.ValueKind == JsonValueKind.String)
                    {
                        string name = nameProp.GetString()!;
                        if (name.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase))
                        {
                            string fileJson = await _sandboxTools.ReadSandboxFile("src/" + name);
                            using JsonDocument fileDoc = JsonDocument.Parse(fileJson);
                            if (fileDoc.RootElement.TryGetProperty("content", out JsonElement contentProp))
                            {
                                var fileObj = new JsonObject
                                {
                                    ["name"] = name,
                                    ["content"] = contentProp.GetString()
                                };
                                remotionFilesArray.Add(fileObj);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load remotion files from sandbox for context injection");
        }

        if (remotionFilesArray.Count > 0)
        {
            try
            {
                JsonObject viewRoot = (JsonObject)JsonNode.Parse(existingViewJson)!;
                viewRoot["remotionFiles"] = remotionFilesArray;
                string newJson = viewRoot.ToJsonString(ConfigJsonOptions);
                context.SetPromptOutputOverride(entry!.StepOrder, newJson);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to inject remotionFiles into view json");
            }
        }
    }

    // -----------------------------------------------------------------
    // The convening gate: deliberately NOT supplied by this room.
    //
    // The three overrides below exist to say that out loud, with the reason, so the omission reads
    // as a considered boundary rather than an oversight (decisions.md §7). The seams' base default
    // is already null, so they change no behaviour — they document it, and RoomConveneGateTests
    // pins it.
    //
    // Why: a System One choice/score/noul question can only *produce* a decision that decomposes
    // into a FIXED option set, and a VideoEditDecisionOutput is not one. It is an id-anchored
    // keep/drop list over whatever ids THIS video's analyze step happened to offer — the candidate
    // set is per-video data, not a closed vocabulary, and the decision is a set of spans rather
    // than a choice among named alternatives. A question set here could only gate on a PROXY for
    // the decision ("how aggressive should the cut be?"), and gating on a proxy is worse than not
    // gating at all: it would trade the room's real decision for a merely correlated one, while
    // looking like the room had been gated. The ColorGrade room — whose decision IS a fixed set of
    // enum words — is the one the gate ships for (see ColorGradeRoomStepExecutor).
    //
    // EditRoomStepConfig.ConveneGate still exists and still defaults Off, so turning it on here is
    // a no-op rather than an error.
    // -----------------------------------------------------------------

    /// <summary>
    /// No convening question set — see the region comment above. This room's decision is an
    /// id-anchored keep/drop list, not a fixed option set, so it has no questions a System One
    /// model could answer in place of the room.
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
    protected override IReadOnlyList<DecisionGateQuestion>? BuildShadowQuestions(VideoEditDecisionOutput decision) => null;

    protected override RoomGroupChatManager CreateManager(
        IReadOnlyList<AIAgent> agents, IRoomStepConfig config, IReadOnlySet<string> offeredIds,
        IDecisionClient? decisionClient) =>
        new EditRoomGroupChatManager(agents, (EditRoomStepConfig)config, offeredIds, "Director", decisionClient, Logger);

    /// <summary>How many Keep spans pair two offered ids from different source clips — used to give the synthesis call precise retry feedback before <see cref="FilterToOfferedIds"/> would drop them.</summary>
    private static int CountMixedSourceSpans(
        VideoEditDecisionOutput decision, IReadOnlyDictionary<string, int> offeredIdSources) =>
        (decision.Keep ?? []).Count(k =>
            k.FromId is not null && k.ToId is not null
            && offeredIdSources.TryGetValue(k.FromId, out int fromSrc)
            && offeredIdSources.TryGetValue(k.ToId, out int toSrc)
            && fromSrc != toSrc);

    /// <summary>
    /// Every offered shot/silence/segment id mapped to the source-clip index it belongs to. The
    /// bounded view only carries a <c>"src"</c> key when the analyze step actually merged more than
    /// one source clip (see <c>VideoAnalyzeStepExecutor</c>'s ShotNode/SilenceNode/SegmentNode);
    /// an absent key means single-source and maps to index 0.
    /// </summary>
    private static Dictionary<string, int> ExtractOfferedIdSources(JsonNode? root)
    {
        var sources = new Dictionary<string, int>(StringComparer.Ordinal);
        JsonNode? view = root?["view"];
        if (view is null)
            return sources;

        CollectIdSources(view["shots"], sources);
        CollectIdSources(view["silences"], sources);
        CollectIdSources(view["segments"], sources);
        // Outline mode lists the rest of the film as ranges: those ids are offered too.
        foreach ((string id, int src, _) in OutlineViewIds.Expand(view, root?["meta"]))
            sources.TryAdd(id, src);
        return sources;
    }

    private static void CollectIdSources(JsonNode? array, Dictionary<string, int> sources)
    {
        if (array is not JsonArray arr)
            return;

        foreach (JsonNode? item in arr)
        {
            string? id = item?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id))
                continue;

            int src = 0;
            if (item?["src"] is JsonValue srcValue && srcValue.TryGetValue(out int parsedSrc))
                src = parsedSrc;

            sources[id] = src;
        }
    }
}
