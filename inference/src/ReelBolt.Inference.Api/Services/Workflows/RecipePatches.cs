using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>One entry of the closed patch catalog, as the assistant reads it.</summary>
/// <param name="Id">The patch id the model passes (camelCase, e.g. <c>addMusic</c>).</param>
/// <param name="Summary">What the patch does, in one plain sentence.</param>
/// <param name="NeedsAudioFiles">True when the project must hold audio files for the patch to be useful (music, sound effects).</param>
public sealed record RecipePatchInfo(string Id, string Summary, bool NeedsAudioFiles);

/// <summary>The result of one patch: the new steps, or the plain reason it cannot apply (never an improvised graph).</summary>
public sealed record RecipePatchOutcome(IReadOnlyList<CreateWorkflowStepRequest>? Steps, string? Refusal)
{
    public static RecipePatchOutcome Ok(IReadOnlyList<CreateWorkflowStepRequest> steps) => new(steps, null);

    public static RecipePatchOutcome Refuse(string reason) => new(null, reason);
}

/// <summary>
/// The CLOSED patch catalog of the recipe-first assistant (docs/design/workflow-apps.md 6.1, decision Q4): seven
/// deterministic graph transforms over a recipe's steps, each insertion before the compile step with every later
/// step reference renumbered. A patch never invents a step the platform has no template pattern for: on a graph it
/// does not fit (no compile step, the feature already on, a stream-copy compile) it REFUSES with a plain reason.
/// Pure: no database, no clock, no model; the same steps and patch always give the same steps.
/// </summary>
/// <remarks>
/// The goldens (RecipePatchesTests) pin the footage-family patches against the matching built-in templates, so a
/// patch and its recipe cannot drift apart.
/// </remarks>
public static class RecipePatches
{
    public const string AddVoiceover = "addVoiceover";
    public const string AddCaptions = "addCaptions";
    public const string AddMusic = "addMusic";
    public const string AddSfx = "addSfx";
    public const string SwapToRoom = "swapToRoom";
    public const string AddColorGrade = "addColorGrade";
    public const string AddGraphics = "addGraphics";
    public const string AddMediaOverlay = "addMediaOverlay";

    /// <summary>The catalog, in the canonical order patches are applied in (so a set of ids gives one graph).</summary>
    public static readonly IReadOnlyList<RecipePatchInfo> Catalog =
    [
        new(SwapToRoom, "Replace the single story editor with an editing team that debates the cut.", false),
        new(AddVoiceover, "Write narration lines and speak them with a voice, mixed into the film.", false),
        new(AddCaptions, "Burn captions of the narration into the film (needs the narration).", false),
        new(AddMusic, "Mix a background music track from the project's files under the film.", true),
        new(AddSfx, "Place sound effects from the project's files on the key cuts.", true),
        new(AddColorGrade, "Give the film one consistent colour look chosen by a team of colourists.", false),
        new(AddGraphics, "Add on-screen titles, name tags and callouts where the picture has room.", false),
        new(AddMediaOverlay, "Show a picture or video over the film at the moment the agent chooses.", false),
    ];

    /// <summary>The ids of <see cref="Catalog"/>.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. Catalog.Select(p => p.Id)];

    /// <summary>
    /// Normalizes the requested ids: trimmed, matched case-insensitively to the catalog's spelling, de-duplicated and put
    /// in canonical order. An id outside the catalog is returned in <paramref name="unknown"/>.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? requested, out IReadOnlyList<string> unknown)
    {
        HashSet<string> chosen = new(StringComparer.Ordinal);
        List<string> bad = [];
        foreach (string raw in requested ?? [])
        {
            string? match = Ids.FirstOrDefault(id => string.Equals(id, raw?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match is null)
                bad.Add(raw ?? string.Empty);
            else
                chosen.Add(match);
        }

        unknown = bad;
        return [.. Ids.Where(chosen.Contains)];
    }

    /// <summary>Applies the (normalized) patches in order; the first that cannot apply refuses the whole set.</summary>
    public static RecipePatchOutcome ApplyAll(
        IReadOnlyList<CreateWorkflowStepRequest> steps,
        IEnumerable<string> normalizedPatchIds,
        IReadOnlyDictionary<AgentType, Guid> agentIds)
    {
        // Every recipe a patch produces is the encode-every-pass shape: the features a patch adds (narration,
        // music, graphics, captions, sound effects, colour grading) are exactly the ones encode-once refuses,
        // because their outcome only exists in the encoded file the review would be judging. So a patched
        // workflow is first folded back from the template's plan/final split to one ordinary compile.
        IReadOnlyList<CreateWorkflowStepRequest> current = WithoutEncodeOnce(steps);
        foreach (string id in normalizedPatchIds)
        {
            RecipePatchOutcome outcome = Apply(id, current, agentIds);
            if (outcome.Refusal is not null)
                return RecipePatchOutcome.Refuse($"{id}: {outcome.Refusal}");
            current = outcome.Steps!;
        }

        return RecipePatchOutcome.Ok(current);
    }

    /// <summary>
    /// The graph with an encode-once split folded back into one compile: the Plan step keeps its (otherwise
    /// identical) config without <c>encodeStage</c>, the Final step is dropped, and the steps are renumbered.
    /// A graph that is not split is returned as it is.
    /// </summary>
    private static IReadOnlyList<CreateWorkflowStepRequest> WithoutEncodeOnce(IReadOnlyList<CreateWorkflowStepRequest> steps)
    {
        List<CreateWorkflowStepRequest> ordered = [.. steps.OrderBy(s => s.StepOrder)];
        int plan = ordered.FindIndex(s => string.Equals(StageOf(s), "Plan", StringComparison.OrdinalIgnoreCase));
        if (plan < 0)
            return ordered;

        int final = ordered.FindLastIndex(s => s.StepOrder > ordered[plan].StepOrder
            && string.Equals(StageOf(s), "Final", StringComparison.OrdinalIgnoreCase));
        if (final < 0)
            return ordered;

        List<CreateWorkflowStepRequest> kept = [.. ordered.Where((_, i) => i != final)];
        kept[plan] = kept[plan] with { VideoCompileConfigJson = StripStage(kept[plan].VideoCompileConfigJson!) };
        return Renumbered(kept);
    }

    private static string? StageOf(CreateWorkflowStepRequest step)
    {
        if (step.StepType != nameof(StepType.VideoCompile) || string.IsNullOrWhiteSpace(step.VideoCompileConfigJson))
            return null;
        try
        {
            return JsonNode.Parse(step.VideoCompileConfigJson) is JsonObject o
                && o["encodeStage"] is JsonValue v && v.TryGetValue(out string? stage)
                    ? stage
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string StripStage(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject o)
            return json;
        return o.Remove("encodeStage") ? o.ToJsonString() : json;
    }

    /// <summary>Applies one patch to the steps (ordered, 1-based <c>StepOrder</c>).</summary>
    public static RecipePatchOutcome Apply(string patchId, IReadOnlyList<CreateWorkflowStepRequest> steps, IReadOnlyDictionary<AgentType, Guid> agentIds)
    {
        Graph graph = new([.. steps.OrderBy(s => s.StepOrder)], agentIds);
        return patchId switch
        {
            AddVoiceover => graph.IsPromo ? PromoVoiceover(graph) : FootageVoiceover(graph),
            AddCaptions => OnFootage(graph, AddCaptionsToCompile),
            AddMusic => OnFootage(graph, g => PlanStep(g, AgentType.MusicSupervisor, "Pick background music", "offerMusicTracks", "enableMusic", "musicPlan", "background music")),
            AddSfx => OnFootage(graph, g => PlanStep(g, AgentType.SoundDesigner, "Plan sound-effect cues", "offerSfxClips", "enableSfx", "sfxPlan", "sound effects")),
            AddGraphics => OnFootage(graph, g => PlanStep(g, AgentType.MotionGraphicsPlanner, "Plan motion graphics overlays", "emitOverlayPlacements", "enableGraphics", "graphicsPlan", "on-screen graphics")),
            AddMediaOverlay => OnFootage(graph, MediaOverlay),
            AddColorGrade => OnFootage(graph, ColorGrade),
            SwapToRoom => OnFootage(graph, Room),
            _ => RecipePatchOutcome.Refuse("this is not a patch the platform offers"),
        };
    }

    private const string NoCut = "this recipe has no edit to change (it cuts nothing from footage), so the patch does not apply.";

    private static RecipePatchOutcome OnFootage(Graph graph, Func<Graph, RecipePatchOutcome> patch) =>
        !graph.HasCut ? RecipePatchOutcome.Refuse(NoCut) : patch(graph);

    // ------------------------------------------------------------------ the transforms

    private static RecipePatchOutcome FootageVoiceover(Graph graph)
    {
        if (!graph.HasCut)
            return RecipePatchOutcome.Refuse(NoCut);
        if (graph.Steps.Any(s => s.StepType == nameof(StepType.Voiceover)))
            return RecipePatchOutcome.Refuse("the workflow already has a narration step.");
        if (graph.CompileMode is { } mode && string.Equals(mode, "StreamCopy", StringComparison.OrdinalIgnoreCase))
            return RecipePatchOutcome.Refuse("the film is copied without re-encoding, which cannot carry a narration.");

        int at = graph.InsertOrder;
        List<CreateWorkflowStepRequest> steps = Shifted(graph.Steps, at, 2);
        steps.Insert(graph.InsertIndex, Agent(graph, AgentType.NarrationWriter, at, "Write narration lines", fullWorkflow: true));
        steps.Insert(graph.InsertIndex + 1, Deterministic(
            graph, at + 1, "Synthesize the narration", StepType.Voiceover,
            voiceoverConfig: $$$"""{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":{{{at}}}}}"""));
        int compile = graph.CompileIndex + 2;
        steps[compile] = steps[compile] with
        {
            VideoCompileConfigJson = PinPrevious(InsertAfter(steps[compile].VideoCompileConfigJson!, "analysisStepOrder",
                ("enableVoiceover", JsonValue.Create(true)), ("voiceoverStepOrder", JsonValue.Create(at + 1))), graph.PreviousOfCompile),
        };
        return RecipePatchOutcome.Ok(Renumbered(steps));
    }

    private static RecipePatchOutcome PromoVoiceover(Graph graph)
    {
        if (graph.Steps.Any(s => s.StepType == nameof(StepType.Voiceover)))
            return RecipePatchOutcome.Refuse("the workflow already has a narration step.");
        int scriptwriter = graph.IndexOfAgent(AgentType.ScriptwriterAgent);
        int director = graph.IndexOfAgent(AgentType.DirectorAgent);
        if (scriptwriter < 0 || director < 0 || director < scriptwriter)
            return RecipePatchOutcome.Refuse("the recipe has no script step to narrate.");

        // Before the Director, so the measured line lengths drive the shot timing (audio first), as the narrated reel does.
        int at = graph.Steps[director].StepOrder;
        List<CreateWorkflowStepRequest> steps = Shifted(graph.Steps, at, 1);
        steps.Insert(director, Deterministic(
            graph, at, "Record the narration", StepType.Voiceover,
            voiceoverConfig: $$$"""{"source":{"kind":"ScriptScenes","scriptwriterStepOrder":{{{graph.Steps[scriptwriter].StepOrder}}}}}"""));
        return RecipePatchOutcome.Ok(Renumbered(steps));
    }

    private static RecipePatchOutcome AddCaptionsToCompile(Graph graph)
    {
        JsonObject compile = JsonNode.Parse(graph.Compile.VideoCompileConfigJson!)!.AsObject();
        bool narrated = graph.Steps.Any(s => s.StepType == nameof(StepType.Voiceover))
            && compile["enableVoiceover"] is JsonValue narration && narration.TryGetValue(out bool on) && on;
        if (!narrated && graph.Steps.Any(s => s.StepType == nameof(StepType.Voiceover)))
            return RecipePatchOutcome.Refuse("the voice step in this workflow records pickup lines, not a narration of the film, so there is nothing to burn captions from.");
        if (!narrated)
            return RecipePatchOutcome.Refuse("captions are burned from a narration, and this workflow has none: add the voiceover first.");
        if (compile["enableCaptions"] is JsonValue captions && captions.TryGetValue(out bool already) && already)
            return RecipePatchOutcome.Refuse("the film already has captions.");

        CreateWorkflowStepRequest changed = graph.Compile with
        {
            VideoCompileConfigJson = InsertAfter(graph.Compile.VideoCompileConfigJson!, "voiceoverStepOrder",
                ("enableCaptions", JsonValue.Create(true)), ("captionStyle", JsonValue.Create("Karaoke"))),
        };
        List<CreateWorkflowStepRequest> steps = [.. graph.Steps];
        steps[graph.CompileIndex] = changed;
        return RecipePatchOutcome.Ok(steps);
    }

    /// <summary>Music, sound effects and graphics: a planning agent before the compile, one analyze flag, two compile members.</summary>
    private static RecipePatchOutcome PlanStep(
        Graph graph, AgentType agent, string label, string analyzeFlag, string enableKey, string planKey, string what)
    {
        string? refusal = CompileRefusal(graph, enableKey, what);
        if (refusal is not null)
            return RecipePatchOutcome.Refuse(refusal);

        int at = graph.InsertOrder;
        List<CreateWorkflowStepRequest> steps = Shifted(graph.Steps, at, 1);
        steps.Insert(graph.InsertIndex, Agent(graph, agent, at, label, fullWorkflow: true));
        steps[graph.AnalyzeIndex] = steps[graph.AnalyzeIndex] with
        {
            VideoAnalyzeConfigJson = SetMember(steps[graph.AnalyzeIndex].VideoAnalyzeConfigJson!, analyzeFlag, JsonValue.Create(true)),
        };
        int compile = graph.CompileIndex + 1;
        steps[compile] = steps[compile] with
        {
            VideoCompileConfigJson = PinPrevious(InsertAfter(steps[compile].VideoCompileConfigJson!, "analysisStepOrder",
                (enableKey, JsonValue.Create(true)), (planKey, StepRef(at))), graph.PreviousOfCompile),
        };
        return RecipePatchOutcome.Ok(Renumbered(steps));
    }

    /// <summary>
    /// Pictures and videos over the film: an <see cref="AgentType.OverlayPlanner"/> before the compile, the
    /// analyze step's <c>emitOverlayOffers</c> flag and the compile's <c>mediaOverlayPlan</c> reference.
    /// </summary>
    private static RecipePatchOutcome MediaOverlay(Graph graph)
    {
        if (graph.IndexOfAgent(AgentType.OverlayPlanner) >= 0)
            return RecipePatchOutcome.Refuse("the workflow already plans pictures or videos over the film.");
        JsonObject compile = JsonNode.Parse(graph.Compile.VideoCompileConfigJson!)!.AsObject();
        if (compile["mediaOverlayPlan"] is not null)
            return RecipePatchOutcome.Refuse("the workflow already shows pictures or videos over the film.");
        if (compile["mode"] is JsonValue mode && mode.TryGetValue(out string? text)
            && string.Equals(text, "StreamCopy", StringComparison.OrdinalIgnoreCase))
            return RecipePatchOutcome.Refuse("the film is copied without re-encoding, which cannot carry pictures or videos over it.");

        int at = graph.InsertOrder;
        List<CreateWorkflowStepRequest> steps = Shifted(graph.Steps, at, 1);
        steps.Insert(graph.InsertIndex, Agent(graph, AgentType.OverlayPlanner, at, "Plan picture overlays", fullWorkflow: true));
        steps[graph.AnalyzeIndex] = steps[graph.AnalyzeIndex] with
        {
            VideoAnalyzeConfigJson = SetMember(steps[graph.AnalyzeIndex].VideoAnalyzeConfigJson!, "emitOverlayOffers", JsonValue.Create(true)),
        };
        int compileIndex = graph.CompileIndex + 1;
        steps[compileIndex] = steps[compileIndex] with
        {
            VideoCompileConfigJson = PinPrevious(InsertAfter(steps[compileIndex].VideoCompileConfigJson!, "analysisStepOrder",
                ("mediaOverlayPlan", StepRef(at))), graph.PreviousOfCompile),
        };
        return RecipePatchOutcome.Ok(Renumbered(steps));
    }

    private static RecipePatchOutcome ColorGrade(Graph graph)
    {
        string? refusal = CompileRefusal(graph, "enableColorGrade", "a colour grade");
        if (refusal is not null)
            return RecipePatchOutcome.Refuse(refusal);

        int at = graph.InsertOrder;
        List<CreateWorkflowStepRequest> steps = Shifted(graph.Steps, at, 1);
        steps.Insert(graph.InsertIndex, Deterministic(
            graph, at, "Color grade room deliberation", StepType.ColorGradeRoom,
            colorGradeConfig: $$$"""{"version":1,"view":{"from":"Step","stepOrder":{{{graph.Steps[graph.AnalyzeIndex].StepOrder}}}}}"""));
        int compile = graph.CompileIndex + 1;
        steps[compile] = steps[compile] with
        {
            VideoCompileConfigJson = PinPrevious(InsertAfter(steps[compile].VideoCompileConfigJson!, "analysisStepOrder",
                ("enableColorGrade", JsonValue.Create(true)), ("colorGradePlan", StepRef(at))), graph.PreviousOfCompile),
        };
        return RecipePatchOutcome.Ok(Renumbered(steps));
    }

    private static RecipePatchOutcome Room(Graph graph)
    {
        int editor = graph.IndexOfAgent(AgentType.VideoStoryEditor);
        if (graph.Steps.Any(s => s.StepType == nameof(StepType.EditRoom)))
            return RecipePatchOutcome.Refuse("the workflow already has an editing team.");
        if (editor < 0 || graph.Steps[editor].StepType is not (null or nameof(StepType.Agent)))
            return RecipePatchOutcome.Refuse("the workflow has no single story editor to swap for an editing team.");

        List<CreateWorkflowStepRequest> steps = [.. graph.Steps];
        CreateWorkflowStepRequest original = steps[editor];
        steps[editor] = Deterministic(
            graph, original.StepOrder, "Edit room deliberation", StepType.EditRoom,
            editRoomConfig: """{"version":1,"view":{"from":"Previous"}}""");
        // The compile read the editor's decision as "the step before me"; say which step that is, as the room recipe does.
        steps[graph.CompileIndex] = steps[graph.CompileIndex] with
        {
            VideoCompileConfigJson = PinPrevious(steps[graph.CompileIndex].VideoCompileConfigJson!, graph.PreviousOfCompile),
        };
        return RecipePatchOutcome.Ok(steps);
    }

    // ------------------------------------------------------------------ shared pieces

    private static string? CompileRefusal(Graph graph, string enableKey, string what)
    {
        JsonObject compile = JsonNode.Parse(graph.Compile.VideoCompileConfigJson!)!.AsObject();
        if (compile[enableKey] is JsonValue on && on.TryGetValue(out bool enabled) && enabled)
            return $"the film already has {what}.";
        if (compile["mode"] is JsonValue mode && mode.TryGetValue(out string? text)
            && string.Equals(text, "StreamCopy", StringComparison.OrdinalIgnoreCase))
            return $"the film is copied without re-encoding, which cannot carry {what}.";
        return null;
    }

    private static JsonNode StepRef(int order) => new JsonObject { ["from"] = "Step", ["stepOrder"] = order };

    private static CreateWorkflowStepRequest Agent(Graph graph, AgentType type, int order, string label, bool fullWorkflow) =>
        new(graph.AgentId(type), order, null, label,
            StepType: nameof(StepType.Agent),
            MaxIterations: 3,
            AgentInputContextMode: fullWorkflow ? "FullWorkflow" : "PreviousStepOnly",
            CacheMode: "Never");

    private static CreateWorkflowStepRequest Deterministic(
        Graph graph, int order, string label, StepType type,
        string? voiceoverConfig = null, string? colorGradeConfig = null, string? editRoomConfig = null) =>
        new(graph.AgentId(AgentType.VideoTransform), order, null, label,
            StepType: type.ToString(),
            MaxIterations: 3,
            VoiceoverConfigJson: voiceoverConfig,
            ColorGradeRoomConfigJson: colorGradeConfig,
            EditRoomConfigJson: editRoomConfig,
            CacheMode: type is StepType.ColorGradeRoom or StepType.EditRoom ? "Never" : null);

    /// <summary>The steps with every one at or after <paramref name="at"/> moved <paramref name="by"/> later, and every reference to them with it.</summary>
    private static List<CreateWorkflowStepRequest> Shifted(IReadOnlyList<CreateWorkflowStepRequest> steps, int at, int by) =>
    [
        .. steps.Select(step => ShiftReferences(step, at, by) with { StepOrder = step.StepOrder >= at ? step.StepOrder + by : step.StepOrder }),
    ];

    /// <summary>Gives the steps their position as order (1-based), so the list is gap-free whatever was inserted.</summary>
    private static List<CreateWorkflowStepRequest> Renumbered(List<CreateWorkflowStepRequest> steps) =>
        [.. steps.Select((s, i) => s with { StepOrder = i + 1 })];

    private static CreateWorkflowStepRequest ShiftReferences(CreateWorkflowStepRequest step, int at, int by)
    {
        static string? Json(string? json, int at, int by) => json is null ? null : ShiftJson(json, at, by);

        return step with
        {
            LoopTargetStepOrder = step.LoopTargetStepOrder is { } loop && loop >= at ? loop + by : step.LoopTargetStepOrder,
            TrueBranchStepOrder = ShiftOrderText(step.TrueBranchStepOrder, at, by),
            FalseBranchStepOrder = ShiftOrderText(step.FalseBranchStepOrder, at, by),
            SelectedPriorStepOrdersJson = Json(step.SelectedPriorStepOrdersJson, at, by),
            ExtractConfigJson = Json(step.ExtractConfigJson, at, by),
            VideoAnalyzeConfigJson = Json(step.VideoAnalyzeConfigJson, at, by),
            VideoCompileConfigJson = Json(step.VideoCompileConfigJson, at, by),
            EditRoomConfigJson = Json(step.EditRoomConfigJson, at, by),
            GraphicsRoomConfigJson = Json(step.GraphicsRoomConfigJson, at, by),
            ColorGradeRoomConfigJson = Json(step.ColorGradeRoomConfigJson, at, by),
            ConditionalConfigJson = Json(step.ConditionalConfigJson, at, by),
            ReviewLoopConfigJson = Json(step.ReviewLoopConfigJson, at, by),
            VideoGenerateConfigJson = Json(step.VideoGenerateConfigJson, at, by),
            VoiceoverConfigJson = Json(step.VoiceoverConfigJson, at, by),
            ThumbnailGenerateConfigJson = Json(step.ThumbnailGenerateConfigJson, at, by),
            DailiesRoomConfigJson = Json(step.DailiesRoomConfigJson, at, by),
            TimelineRenderConfigJson = Json(step.TimelineRenderConfigJson, at, by),
            DesignSystemConfigJson = Json(step.DesignSystemConfigJson, at, by),
            ExtractFrameConfigJson = Json(step.ExtractFrameConfigJson, at, by),
            AudioGenerateConfigJson = Json(step.AudioGenerateConfigJson, at, by),
            PinInsertConfigJson = Json(step.PinInsertConfigJson, at, by),
            AudioCleanupConfigJson = Json(step.AudioCleanupConfigJson, at, by),
            VideoEnhanceConfigJson = Json(step.VideoEnhanceConfigJson, at, by),
            ImageGenerateConfigJson = Json(step.ImageGenerateConfigJson, at, by),
        };
    }

    private static string? ShiftOrderText(string? text, int at, int by) =>
        int.TryParse(text, out int order) && order >= at ? (order + by).ToString(System.Globalization.CultureInfo.InvariantCulture) : text;

    /// <summary>
    /// Shifts every step reference in a config: a number under a member named <c>stepOrder</c> or ending in
    /// <c>StepOrder</c> (case-insensitive), and every number of an array. A config that does not parse, or has nothing
    /// to shift, comes back byte-for-byte.
    /// </summary>
    internal static string ShiftJson(string json, int at, int by)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return json;
        }

        if (root is null)
            return json;
        bool changed = root is JsonArray list ? ShiftArray(list, at, by) : ShiftNode(root, at, by);
        return changed ? root.ToJsonString() : json;
    }

    private static bool ShiftArray(JsonArray array, int at, int by)
    {
        bool changed = false;
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is JsonValue v && v.TryGetValue(out int n) && n >= at)
            {
                array[i] = n + by;
                changed = true;
            }
        }

        return changed;
    }

    private static bool ShiftNode(JsonNode node, int at, int by)
    {
        bool changed = false;
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(p => p.Key).ToList())
                {
                    JsonNode? child = obj[key];
                    bool isRef = key.Equals("stepOrder", StringComparison.OrdinalIgnoreCase)
                        || key.EndsWith("StepOrder", StringComparison.OrdinalIgnoreCase);
                    if (isRef && child is JsonValue v && v.TryGetValue(out int n))
                    {
                        if (n >= at)
                        {
                            obj[key] = n + by;
                            changed = true;
                        }
                    }
                    else if (child is not null)
                    {
                        changed |= ShiftNode(child, at, by);
                    }
                }

                break;
            case JsonArray arr:
                foreach (JsonNode? item in arr)
                {
                    if (item is not null)
                        changed |= ShiftNode(item, at, by);
                }

                break;
        }

        return changed;
    }

    /// <summary>
    /// A member set to <c>{"from":"Previous"}</c> means "the step right before the compile"; once a step is inserted there
    /// it would silently mean the inserted one, so it is pinned to the step it meant (<paramref name="previousOrder"/>).
    /// </summary>
    private static string PinPrevious(string json, int previousOrder)
    {
        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        foreach (string key in obj.Select(p => p.Key).ToList())
        {
            if (obj[key] is JsonObject child && child["from"] is JsonValue from && from.TryGetValue(out string? kind)
                && string.Equals(kind, "Previous", StringComparison.OrdinalIgnoreCase))
                obj[key] = StepRef(previousOrder);
        }

        return obj.ToJsonString();
    }

    private static string SetMember(string json, string key, JsonNode value)
    {
        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        obj[key] = value;
        return obj.ToJsonString();
    }

    /// <summary>Adds members right after <paramref name="afterKey"/> (at the end when the key is absent), keeping the others' order.</summary>
    private static string InsertAfter(string json, string afterKey, params (string Key, JsonNode Value)[] members)
    {
        JsonObject source = JsonNode.Parse(json)!.AsObject();
        JsonObject rebuilt = [];
        bool placed = false;
        foreach ((string key, JsonNode? value) in source.Select(p => (p.Key, p.Value)).ToList())
        {
            source.Remove(key);
            if (members.Any(m => m.Key == key))
                continue;
            rebuilt[key] = value;
            if (key == afterKey)
            {
                foreach ((string k, JsonNode v) in members)
                    rebuilt[k] = v.DeepClone();
                placed = true;
            }
        }

        if (!placed)
        {
            foreach ((string k, JsonNode v) in members)
                rebuilt[k] = v.DeepClone();
        }

        return rebuilt.ToJsonString();
    }

    /// <summary>The ordered steps plus the places the transforms look for.</summary>
    private sealed class Graph(List<CreateWorkflowStepRequest> steps, IReadOnlyDictionary<AgentType, Guid> agentIds)
    {
        public List<CreateWorkflowStepRequest> Steps { get; } = steps;

        public Guid AgentId(AgentType type) => agentIds[type];

        public int CompileIndex { get; } = steps.FindIndex(s => s.StepType == nameof(StepType.VideoCompile) && s.VideoCompileConfigJson is not null);

        public int AnalyzeIndex { get; } = steps.FindIndex(s => s.StepType == nameof(StepType.VideoAnalyze) && s.VideoAnalyzeConfigJson is not null);

        /// <summary>A footage cut: an analyze step and a compile step that assembles the kept spans of a decision.</summary>
        public bool HasCut =>
            CompileIndex >= 0 && AnalyzeIndex >= 0
            && JsonNode.Parse(Compile.VideoCompileConfigJson!) is JsonObject compile && compile["decision"] is not null
            && !(compile["keepWholeSources"] is JsonValue whole && whole.TryGetValue(out bool keep) && keep);

        /// <summary>The order of the step right before the compile (what a <c>Previous</c> reference of the compile means).</summary>
        public int PreviousOfCompile => CompileIndex > 0 ? Steps[CompileIndex - 1].StepOrder : 0;

        public CreateWorkflowStepRequest Compile => Steps[CompileIndex];

        public int CompileOrder => Steps[CompileIndex].StepOrder;

        /// <summary>
        /// Where a planner step goes: right before the compile, or before the motion director that stands right before it,
        /// so a patched workflow has the same order as the recipe made with the planner (planner, then motion director).
        /// </summary>
        public int InsertIndex => CompileIndex > 0 && IsMotionDirector(Steps[CompileIndex - 1]) ? CompileIndex - 1 : CompileIndex;

        public int InsertOrder => Steps[InsertIndex].StepOrder;

        private bool IsMotionDirector(CreateWorkflowStepRequest step) =>
            step.StepType == nameof(StepType.Agent) && agentIds.TryGetValue(AgentType.MotionDirector, out Guid director) && step.AgentDefinitionId == director;

        /// <summary>The promo family: a script and a director write the film, and no compile step cuts footage.</summary>
        public bool IsPromo => !HasCut && CompileIndex < 0 && IndexOfAgent(AgentType.ScriptwriterAgent) >= 0;

        public string? CompileMode =>
            CompileIndex >= 0 && JsonNode.Parse(Compile.VideoCompileConfigJson!) is JsonObject o && o["mode"] is JsonValue m && m.TryGetValue(out string? text)
                ? text
                : null;

        public int IndexOfAgent(AgentType type)
        {
            if (!agentIds.TryGetValue(type, out Guid id))
                return -1;
            return Steps.FindIndex(s => s.AgentDefinitionId == id && (s.StepType is null or nameof(StepType.Agent)));
        }
    }
}
