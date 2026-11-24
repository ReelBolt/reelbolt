using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Replay;

/// <summary>
/// What every step of a workflow READS from the steps before it, derived from the step definitions
/// alone (docs/design/composition-revisions.md "Replaying what depends on the film"). A composition
/// revision uses it to decide which steps of the original run must run again after the film is
/// re-rendered — every step that depends, directly or transitively, on the render — and which can be
/// kept as they were.
///
/// <para>
/// <b>The rule is "when in doubt, it depends".</b> Over-inclusion costs a re-run; under-inclusion ships
/// a stale deliverable, which is the bug this exists to fix. So every channel a step can read through
/// is modelled, and every case the analysis cannot read precisely (an unparseable config, a custom
/// mapping, a workflow with branches) falls back to "depends on every earlier step":
/// </para>
/// <list type="bullet">
/// <item><b>Prompt context</b> (<c>Agent</c>, <c>Parallel</c>, <c>ReviewLoop</c>): by the step's
/// effective <see cref="AgentInputContextMode"/> — <c>FullWorkflow</c> and <c>CustomMappedSubset</c>
/// read everything before them, <c>PreviousStepOnly</c> the previous step, <c>SelectedPriorSteps</c>
/// the listed ones.</item>
/// <item><b>Accumulated output</b> (<c>ForEach</c>, <c>Conditional</c>, <c>ReviewLoop</c>'s facts):
/// the previous step.</item>
/// <item><b>Typed references</b>: <see cref="VideoSourceRef"/> (<c>StepOutput n</c>, and
/// <c>PreviousStepOutput</c> = every earlier step), <see cref="ExtractInputRef"/> (<c>Previous</c>,
/// <c>Step n</c>), <c>AnalysisStepOrder</c> (and, transitively, the media that analysis read, which a
/// compile re-reads), the compile's decision and plan references, voiceover source steps.</item>
/// <item><b>The sandbox</b>: an agent with any sandbox tool reads the workspace every earlier
/// sandbox-writing step (and the <c>Revise</c> step) left behind.</item>
/// </list>
/// <para>
/// A dependency always points BACKWARD (a smaller step order): control flow — a review loop's target, a
/// conditional's branches — is not a data dependency, and a reference to a later step can never
/// resolve at runtime (history only holds steps that already ran).
/// </para>
/// </summary>
public sealed class WorkflowStepDependencyGraph
{
    private readonly Dictionary<int, IReadOnlyDictionary<int, string>> _dependsOn;
    private readonly Dictionary<int, WorkflowStep> _byOrder;

    private WorkflowStepDependencyGraph(IReadOnlyList<WorkflowStep> steps, Dictionary<int, IReadOnlyDictionary<int, string>> dependsOn)
    {
        Steps = steps;
        _dependsOn = dependsOn;
        _byOrder = steps.GroupBy(s => s.StepOrder).ToDictionary(g => g.Key, g => g.First());
    }

    /// <summary>The steps, ascending by order.</summary>
    public IReadOnlyList<WorkflowStep> Steps { get; }

    /// <summary>The step at <paramref name="stepOrder"/>, or null.</summary>
    public WorkflowStep? Step(int stepOrder) => _byOrder.GetValueOrDefault(stepOrder);

    /// <summary>
    /// The steps <paramref name="stepOrder"/> reads directly, each with a short reason (for the plan's
    /// audit trail and the tests). Empty for an unknown order.
    /// </summary>
    public IReadOnlyDictionary<int, string> DependsOn(int stepOrder) =>
        _dependsOn.TryGetValue(stepOrder, out IReadOnlyDictionary<int, string>? deps) ? deps : new Dictionary<int, string>();

    /// <summary>
    /// Every step that depends on <paramref name="stepOrder"/>, directly or transitively (the step itself
    /// excluded). These are the steps whose output may differ once that step's output has changed.
    /// </summary>
    public IReadOnlySet<int> DownstreamOf(int stepOrder)
    {
        HashSet<int> downstream = [];
        HashSet<int> frontier = [stepOrder];
        foreach (WorkflowStep step in Steps)
        {
            if (step.StepOrder <= stepOrder)
                continue;
            if (DependsOn(step.StepOrder).Keys.Any(frontier.Contains))
            {
                downstream.Add(step.StepOrder);
                frontier.Add(step.StepOrder);
            }
        }

        return downstream;
    }

    /// <summary>
    /// The steps whose video is a FINAL deliverable of the workflow: every <c>VideoCompile</c> step whose
    /// output no later step consumes as a video source (a compile that is re-analysed and re-cut is an
    /// intermediate), in step order. When the workflow has no such compile, the deliverable is the
    /// film render itself (<paramref name="renderStepOrder"/>), when given.
    /// </summary>
    public IReadOnlyList<int> FinalVideoStepOrders(int? renderStepOrder)
    {
        HashSet<int> consumed = [];
        foreach (WorkflowStep step in Steps)
        {
            foreach (int source in MediaSourcesOf(step))
                consumed.Add(source);
        }

        List<int> finals = [.. Steps
            // A Plan-stage compile (encode-once) writes no video at all; the one Final-stage compile after the
            // review is the deliverable.
            .Where(s => s.StepType == StepType.VideoCompile
                && CompileEncodeStages.Of(s) != CompileEncodeStage.Plan
                && !consumed.Contains(s.StepOrder))
            .Select(s => s.StepOrder)];
        if (finals.Count == 0 && renderStepOrder is { } render)
            finals.Add(render);
        return finals;
    }

    /// <summary>
    /// True for a step that makes an EDITORIAL decision anchored to the ids a <c>VideoAnalyze</c> step
    /// offered — which shots to keep, which overlays, which music, which cues, which narration lines, which
    /// grade. "Keep the same cut" reuses these when the re-rendered film's analysis still offers the same
    /// ids; re-running them is what re-cuts the film.
    /// </summary>
    public static bool IsEditDecisionStep(WorkflowStep step)
    {
        if (step.StepType is StepType.EditRoom or StepType.GraphicsRoom or StepType.ColorGradeRoom)
            return true;
        if (step.StepType != StepType.Agent)
            return false;

        AgentType agentType = step.AgentDefinition?.AgentType ?? AgentType.Custom;
        return EditDecisionAgentTypes.Contains(agentType)
               || (step.AgentDefinition?.OutputSchemaName is { } schema && EditDecisionSchemas.Contains(schema));
    }

    /// <summary>
    /// True for a step that can respond to a review's critique when a review loop re-runs it (a model
    /// makes it). A deterministic step re-run with the same inputs produces the same output, so looping a
    /// review back over only deterministic steps cannot change anything.
    /// </summary>
    public static bool IsModelStep(WorkflowStep step) => step.StepType is
        StepType.Agent or StepType.Parallel or StepType.ForEach or
        StepType.EditRoom or StepType.GraphicsRoom or StepType.ColorGradeRoom;

    private static readonly HashSet<AgentType> EditDecisionAgentTypes =
    [
        AgentType.VideoStoryEditor,
        AgentType.VideoEditDirector,
        AgentType.MotionGraphicsPlanner,
        AgentType.MotionGraphicsDirector,
        AgentType.MusicSupervisor,
        AgentType.SoundDesigner,
        AgentType.Colorist,
        AgentType.ColorGradeDirector,
        AgentType.ShotDirector,
        AgentType.NarrationWriter,
        AgentType.NarrationTranslator,
        AgentType.PickupPlanner,
        AgentType.MotionDirector,
        AgentType.DynamicsPlanner
    ];

    private static readonly HashSet<string> EditDecisionSchemas = new(StringComparer.Ordinal)
    {
        "VideoEditDecisionOutput",
        "MotionGraphicsPlanOutput",
        "MusicPlanOutput",
        "SfxPlanOutput",
        "ColorGradePlanOutput",
        "GeneratedShotPlanOutput",
        "NarrationPlanOutput",
        "PickupPlanOutput",
        "MotionPlanOutput",
        "DynamicsPlanOutput"
    };

    // ------------------------------------------------------------------
    // Building
    // ------------------------------------------------------------------

    /// <summary>Analyses <paramref name="steps"/> (any order; agent definitions should be loaded).</summary>
    public static WorkflowStepDependencyGraph Build(IEnumerable<WorkflowStep> steps)
    {
        List<WorkflowStep> ordered = [.. steps.OrderBy(s => s.StepOrder)];
        // A branch can skip the previous step at runtime, and "previous" then resolves further back, so
        // in a workflow with branches every "previous step" reference reads as "any earlier step".
        bool hasBranches = ordered.Any(s => s.StepType == StepType.Conditional);
        Builder builder = new(ordered, hasBranches);

        Dictionary<int, IReadOnlyDictionary<int, string>> dependsOn = [];
        foreach (WorkflowStep step in ordered)
            dependsOn[step.StepOrder] = builder.DependenciesOf(step);
        return new WorkflowStepDependencyGraph(ordered, dependsOn);
    }

    private sealed class Builder(List<WorkflowStep> steps, bool hasBranches)
    {
        public IReadOnlyDictionary<int, string> DependenciesOf(WorkflowStep step)
        {
            Dictionary<int, string> deps = [];
            int order = step.StepOrder;

            switch (step.StepType)
            {
                case StepType.Agent:
                case StepType.Parallel:
                    AddPromptContext(deps, step);
                    AddSandbox(deps, step);
                    break;

                case StepType.ReviewLoop:
                    AddPromptContext(deps, step);
                    AddPrevious(deps, order, "the review reads the previous step's facts");
                    AddSandbox(deps, step);
                    break;

                case StepType.ForEach:
                    AddPrevious(deps, order, "iterates the previous step's output");
                    AddSandbox(deps, step);
                    break;

                case StepType.Conditional:
                    AddPrevious(deps, order, "evaluates the previous step's output");
                    break;

                case StepType.Extract:
                    AddExtractInputs(deps, step);
                    break;

                case StepType.VideoAnalyze:
                    if (!AddVideoSources(deps, order, Parse(step.VideoAnalyzeConfigJson), "analyses its video"))
                        AddAllEarlier(deps, order, "analysis sources could not be read");
                    AddPinnedInsertEventSources(deps, step);
                    break;

                case StepType.VideoCompile:
                    AddCompileInputs(deps, step);
                    break;

                case StepType.EditRoom:
                case StepType.ColorGradeRoom:
                    {
                        string? json = step.StepType == StepType.EditRoom ? step.EditRoomConfigJson : step.ColorGradeRoomConfigJson;
                        JsonObject? config = Parse(json);
                        if (json is not null && config is null)
                            AddAllEarlier(deps, order, "room config could not be read");
                        else
                            AddRef(deps, order, Child(config, "view"), "the room's view", defaultToPrevious: true);
                        break;
                    }

                case StepType.GraphicsRoom:
                    // The graphics room's view is annotated with the story editor's decision, and its
                    // director may render an asset in the sandbox: it reads more than its view.
                    AddAllEarlier(deps, order, "the graphics room reads the edit and the sandbox");
                    break;

                case StepType.VideoGenerate:
                    AddVideoGenerateInputs(deps, step);
                    break;

                case StepType.Voiceover:
                    AddVoiceoverInputs(deps, step);
                    break;

                case StepType.ThumbnailGenerate:
                    {
                        JsonObject? config = Parse(step.ThumbnailGenerateConfigJson);
                        if (Int(config, "analysisStepOrder") is { } analysis)
                            AddAnalysis(deps, order, analysis, "the thumbnail's analysis");
                        else
                            AddAllEarlier(deps, order, "thumbnail config could not be read");

                        // QA-P2-2: a Source naming another step's output (e.g. an AtTime frame of a compiled film) is read
                        // too; without it a re-rendered film would replay a stale cached thumbnail.
                        if (Child(config, "source") is not null
                            && !AddVideoSources(deps, order, config, "the video the thumbnail is taken from"))
                            AddAllEarlier(deps, order, "thumbnail source could not be read");
                        break;
                    }

                case StepType.ExtractFrame:
                    if (!AddVideoSources(deps, order, Parse(step.ExtractFrameConfigJson), "the video it grabs a frame from"))
                        AddAllEarlier(deps, order, "frame source could not be read");
                    break;

                case StepType.AudioCleanup:
                    if (!AddVideoSources(deps, order, Parse(step.AudioCleanupConfigJson), "the audio or video it cleans"))
                        AddAllEarlier(deps, order, "audio cleanup source could not be read");
                    break;

                case StepType.VideoEnhance:
                    if (!AddVideoSources(deps, order, Parse(step.VideoEnhanceConfigJson), "the video it enhances"))
                        AddAllEarlier(deps, order, "enhance source could not be read");
                    break;

                case StepType.ImageGenerate:
                    // Generate reads nothing from the run (a prompt and a provider row); Edit reads its one source picture.
                    if (Child(Parse(step.ImageGenerateConfigJson), "source") is not null
                        && !AddVideoSources(deps, order, Parse(step.ImageGenerateConfigJson), "the picture it edits"))
                        AddAllEarlier(deps, order, "image source could not be read");
                    break;

                case StepType.PinInsert:
                    {
                        // T-206: the analysis whose offered screens it pins, and every earlier step a pin's
                        // content names (a re-render there must change this plan's cache key).
                        JsonObject? config = Parse(step.PinInsertConfigJson);
                        if (config is null)
                        {
                            AddAllEarlier(deps, order, "pin config could not be read");
                            break;
                        }

                        AddRef(deps, order, Child(config, "analysis"), "the analysis whose screens it pins", defaultToPrevious: true);
                        if (Child(config, "pins") is JsonArray pins)
                        {
                            foreach (JsonNode? pin in pins)
                                AddInsertContentStep(deps, order, Child(pin as JsonObject, "content"));
                        }

                        break;
                    }

                case StepType.AudioGenerate:
                    // T-108: an operator-written prompt (or composition plan) and a provider row: it reads nothing
                    // from the run. Later steps find its takes as project files, not through its output.
                    break;

                case StepType.Revise:
                case StepType.DesignGraphic:
                    // Restores a saved version (or designs a graphic) from its own request row.
                    break;

                case StepType.TimelineRender:
                    // Renders a stored editor timeline (edit_timelines, named by its config) — a document the
                    // editor wrote, never an earlier step's output — so it depends on no step above it.
                    break;

                case StepType.DesignSystem:
                    {
                        JsonObject? config = Parse(step.DesignSystemConfigJson);
                        if (config is null)
                        {
                            AddAllEarlier(deps, order, "design system config could not be read");
                            break;
                        }

                        switch (EnumName(Child(config, "op"), ["Parse", "Compose", "Require"]))
                        {
                            case "Compose" when Child(config, "compose") is JsonObject compose:
                                if (Int(compose, "parseStepOrder") is { } parseOrder && parseOrder > 0 && parseOrder < order)
                                    Add(deps, parseOrder, "the parse artifact the compose reads");
                                foreach (int fallback in IntArray(compose, "designFallbackStepOrders"))
                                    if (fallback > 0 && fallback < order)
                                        Add(deps, fallback, "the design fallback step");
                                foreach (int fallback in IntArray(compose, "productFallbackStepOrders"))
                                    if (fallback > 0 && fallback < order)
                                        Add(deps, fallback, "the product fallback step");
                                break;

                            case "Parse":
                            case "Require":
                                // Neither reads an earlier step's output.
                                break;

                            default:
                                AddAllEarlier(deps, order, "the design system op could not be read");
                                break;
                        }

                        break;
                    }

                default:
                    AddAllEarlier(deps, order, $"{step.StepType} steps are not analysed in detail");
                    break;
            }

            deps.Remove(order);
            return deps;
        }

        // ---- channels ----

        private void AddPromptContext(Dictionary<int, string> deps, WorkflowStep step)
        {
            int order = step.StepOrder;
            switch (AgentInputContextDefaults.ResolveEffectiveMode(step))
            {
                case AgentInputContextMode.PreviousStepOnly:
                    AddPrevious(deps, order, "reads the previous step");
                    break;

                case AgentInputContextMode.SelectedPriorSteps:
                    {
                        List<int>? selected = null;
                        try
                        {
                            selected = string.IsNullOrWhiteSpace(step.SelectedPriorStepOrdersJson)
                                ? []
                                : JsonSerializer.Deserialize<List<int>>(step.SelectedPriorStepOrdersJson);
                        }
                        catch (JsonException)
                        {
                            selected = null;
                        }

                        if (selected is null)
                        {
                            AddAllEarlier(deps, order, "selected prior steps could not be read");
                            break;
                        }

                        foreach (int prior in selected.Where(p => p > 0 && p < order))
                            Add(deps, prior, "a selected prior step");
                        break;
                    }

                case AgentInputContextMode.CustomMappedSubset:
                    // A mapping that resolves empty falls back to the whole workflow.
                    AddAllEarlier(deps, order, "a custom input mapping");
                    break;

                default:
                    AddAllEarlier(deps, order, "reads the whole workflow");
                    break;
            }
        }

        private void AddSandbox(Dictionary<int, string> deps, WorkflowStep step)
        {
            if (!ReadsSandbox(step))
                return;
            foreach (WorkflowStep earlier in steps.Where(s => s.StepOrder < step.StepOrder && WritesSandbox(s)))
                Add(deps, earlier.StepOrder, "reads the sandbox it left");
        }

        private void AddExtractInputs(Dictionary<int, string> deps, WorkflowStep step)
        {
            JsonObject? config = Parse(step.ExtractConfigJson);
            if (Child(config, "inputs") is not JsonObject inputs)
            {
                AddAllEarlier(deps, step.StepOrder, "extract inputs could not be read");
                return;
            }

            foreach (KeyValuePair<string, JsonNode?> input in inputs)
                AddRef(deps, step.StepOrder, input.Value, $"extract input '{input.Key}'", defaultToPrevious: false);
        }

        private void AddCompileInputs(Dictionary<int, string> deps, WorkflowStep step)
        {
            int order = step.StepOrder;
            JsonObject? config = Parse(step.VideoCompileConfigJson);
            if (config is null)
            {
                AddAllEarlier(deps, order, "compile config could not be read");
                return;
            }

            AddRef(deps, order, Child(config, "decision"), "the edit decision", defaultToPrevious: true);
            if (Int(config, "analysisStepOrder") is { } analysis && analysis > 0)
                AddAnalysis(deps, order, analysis, "the analysis it cuts against");
            foreach ((string key, string why) in PlanRefs)
            {
                if (Child(config, key) is { } plan)
                    AddRef(deps, order, plan, why, defaultToPrevious: false);
            }

            foreach (string key in (string[])["voiceoverStepOrder", "pickupVoiceoverStepOrder"])
            {
                if (Int(config, key) is { } voiceover && voiceover > 0)
                    Add(deps, voiceover, "the narration it mixes");
            }

            // Lane 3e: transcript captions swap in the text a translation step produced, so re-running that step must
            // change this compile's cache key and mark it stale, wherever the step sits.
            if (Int(config, "captionTranslationStepOrder") is { } translation && translation > 0 && translation < order)
                Add(deps, translation, "the caption translation");

            // T-106: an insert named by reference to an earlier step of this run (the fallback and each per-plate
            // entry) is read from that step's output, so re-rendering it must change this compile's cache key.
            AddInsertContentStep(deps, order, Child(config, "insertContentRef"));
            if (Child(config, "insertContents") is JsonArray assignments)
            {
                foreach (JsonNode? assignment in assignments)
                    AddInsertContentStep(deps, order, Child(assignment as JsonObject, "insertContentRef"));
            }

            // Pictures and videos over the film: each mediaOverlays[].content names the same kind of
            // earlier output, so re-rendering that step must change this compile's cache key too.
            if (Child(config, "mediaOverlays") is JsonArray mediaOverlays)
            {
                foreach (JsonNode? overlay in mediaOverlays)
                    AddInsertContentStep(deps, order, Child(overlay as JsonObject, "content"));
            }
        }

        private void AddInsertContentStep(Dictionary<int, string> deps, int order, JsonNode? reference)
        {
            switch (InsertStepOrder(reference))
            {
                case { Readable: false }:
                    AddAllEarlier(deps, order, "an insert reference could not be read");
                    break;
                case { StepOrder: { } stepOrder } when stepOrder < order:
                    Add(deps, stepOrder, "the render it inserts");
                    break;
            }
        }

        private static readonly (string Key, string Why)[] PlanRefs =
        [
            ("graphicsPlan", "the graphics plan"),
            ("mediaOverlayPlan", "the overlay plan"),
            ("musicPlan", "the music plan"),
            ("colorGradePlan", "the colour grade"),
            ("sfxPlan", "the sound-effect plan"),
            ("dynamicsPlan", "the dynamics plan"),
            ("generatedClips", "the generated clips")
        ];

        /// <summary>An analysis step, plus the media that analysis read — a compile cuts those files.</summary>
        private void AddAnalysis(Dictionary<int, string> deps, int order, int analysisOrder, string why)
        {
            if (analysisOrder >= order)
                return;
            Add(deps, analysisOrder, why);
            WorkflowStep? analysis = steps.FirstOrDefault(s => s.StepOrder == analysisOrder);
            if (analysis?.StepType == StepType.VideoAnalyze
                && !AddVideoSources(deps, analysisOrder, Parse(analysis.VideoAnalyzeConfigJson), "the video it cuts"))
            {
                AddAllEarlier(deps, analysisOrder, "the analysis's sources could not be read");
            }
        }

        private void AddVideoGenerateInputs(Dictionary<int, string> deps, WorkflowStep step)
        {
            JsonObject? config = Parse(step.VideoGenerateConfigJson);
            if (config is null)
            {
                AddAllEarlier(deps, step.StepOrder, "video generation config could not be read");
                return;
            }

            // The keyframe images are read from earlier steps whatever the plan source.
            if (Child(config, "firstFrame") is { } firstFrame)
                AddRef(deps, step.StepOrder, firstFrame, "the first-frame image", defaultToPrevious: false);
            if (Child(config, "lastFrame") is { } lastFrame)
                AddRef(deps, step.StepOrder, lastFrame, "the last-frame image", defaultToPrevious: false);

            if (!string.Equals(Str(config, "planSource"), "StepRef", StringComparison.OrdinalIgnoreCase))
                return; // an operator-written prompt reads nothing from the run

            AddRef(deps, step.StepOrder, Child(config, "plan"), "the shot plan", defaultToPrevious: true);
            if (Child(config, "analysis") is { } analysis)
                AddRef(deps, step.StepOrder, analysis, "the analysis the shots anchor to", defaultToPrevious: false);
        }

        private void AddVoiceoverInputs(Dictionary<int, string> deps, WorkflowStep step)
        {
            int order = step.StepOrder;
            JsonObject? config = Parse(step.VoiceoverConfigJson);
            JsonObject? source = Child(config, "source") as JsonObject;
            if (config is null || source is null)
            {
                AddAllEarlier(deps, order, "voiceover config could not be read");
                return;
            }

            // Pronunciation "Auto" derives terms from every earlier step's output.
            if (string.Equals(Str(config, "pronunciationDictionary"), "Auto", StringComparison.OrdinalIgnoreCase))
                AddAllEarlier(deps, order, "pronunciation terms come from the whole run");

            foreach (string key in (string[])["scriptwriterStepOrder", "narrationWriterStepOrder", "pickupPlannerStepOrder"])
            {
                if (Int(source, key) is { } source0 && source0 > 0)
                    Add(deps, source0, "the lines it speaks");
            }
        }

        /// <summary>
        /// T-110: an analysis that offers SFX clips also offers the events of the render a downstream
        /// SFX+inserts compile of it pins by <c>{"from":"Step"}</c> reference — so it reads that earlier step.
        /// </summary>
        private void AddPinnedInsertEventSources(Dictionary<int, string> deps, WorkflowStep analyze)
        {
            if (Child(Parse(analyze.VideoAnalyzeConfigJson), "offerSfxClips") is not JsonValue offer ||
                !offer.TryGetValue(out bool offersSfx) || !offersSfx)
            {
                return;
            }

            foreach (WorkflowStep compile in steps.Where(s => s.StepType == StepType.VideoCompile && s.StepOrder > analyze.StepOrder))
            {
                JsonObject? config = Parse(compile.VideoCompileConfigJson);
                if (Int(config, "analysisStepOrder") != analyze.StepOrder || !Flag(config, "enableSfx") || !Flag(config, "enableInserts"))
                    continue;

                List<JsonObject> refs = [];
                if (Child(config, "insertContentRef") is JsonObject configRef)
                    refs.Add(configRef);
                if (Child(config, "insertContents") is JsonArray entries)
                {
                    foreach (JsonNode? entry in entries)
                    {
                        if (Child(entry as JsonObject, "insertContentRef") is JsonObject entryRef)
                            refs.Add(entryRef);
                    }
                }

                foreach (JsonObject reference in refs)
                {
                    switch (InsertStepOrder(reference))
                    {
                        case { Readable: false }:
                            AddAllEarlier(deps, analyze.StepOrder, "a pinned insert reference could not be read");
                            break;
                        case { StepOrder: { } rendered } when rendered < analyze.StepOrder:
                            Add(deps, rendered, "the pinned render whose events it offers");
                            break;
                    }
                }
            }

            static bool Flag(JsonObject? node, string name) =>
                Child(node, name) is JsonValue value && value.TryGetValue(out bool on) && on;
        }

        /// <summary>Adds the media sources of a VideoAnalyze config. False when the config cannot be read.</summary>
        private bool AddVideoSources(Dictionary<int, string> deps, int order, JsonObject? config, string why)
        {
            if (config is null)
                return false;

            List<JsonNode> sources = [];
            if (Child(config, "source") is JsonObject single)
                sources.Add(single);
            if (Child(config, "sources") is JsonArray many)
                sources.AddRange(many.Where(n => n is not null)!);
            if (sources.Count == 0)
                return false;

            foreach (JsonNode source in sources)
            {
                string? kind = EnumName(Child(source as JsonObject, "kind"), ["ProjectFile", "StepOutput", "PreviousStepOutput"]);
                switch (kind)
                {
                    case "ProjectFile":
                        break;
                    case "StepOutput" when Int(source as JsonObject, "stepOrder") is { } stepOrder:
                        if (stepOrder < order)
                            Add(deps, stepOrder, why);
                        break;
                    case "PreviousStepOutput":
                        AddAllEarlier(deps, order, why + " (the latest earlier video)");
                        break;
                    default:
                        return false;
                }
            }

            return true;
        }

        /// <summary>An <see cref="ExtractInputRef"/>-shaped node: {from, stepOrder}.</summary>
        private void AddRef(Dictionary<int, string> deps, int order, JsonNode? node, string why, bool defaultToPrevious)
        {
            if (node is null)
            {
                if (defaultToPrevious)
                    AddPrevious(deps, order, why);
                return;
            }

            string? from = EnumName(Child(node as JsonObject, "from"), ["Previous", "Step", "Accumulated", "ProjectFiles"]);
            switch (from)
            {
                case "Previous":
                case "Accumulated":
                    AddPrevious(deps, order, why);
                    break;
                case "Step" when Int(node as JsonObject, "stepOrder") is { } stepOrder:
                    if (stepOrder < order)
                        Add(deps, stepOrder, why);
                    break;
                case "ProjectFiles":
                    break;
                default:
                    AddAllEarlier(deps, order, why + " (unreadable reference)");
                    break;
            }
        }

        private void AddPrevious(Dictionary<int, string> deps, int order, string why)
        {
            if (hasBranches)
            {
                AddAllEarlier(deps, order, why + " (the workflow branches)");
                return;
            }

            WorkflowStep? previous = steps.LastOrDefault(s => s.StepOrder < order);
            if (previous is not null)
                Add(deps, previous.StepOrder, why);
        }

        private void AddAllEarlier(Dictionary<int, string> deps, int order, string why)
        {
            foreach (WorkflowStep earlier in steps.Where(s => s.StepOrder < order))
                Add(deps, earlier.StepOrder, why);
        }

        private static void Add(Dictionary<int, string> deps, int stepOrder, string why) => deps.TryAdd(stepOrder, why);
    }

    // ------------------------------------------------------------------
    // Sandbox and media helpers
    // ------------------------------------------------------------------

    private static IReadOnlyList<ToolGroup> GroupsOf(WorkflowStep step) =>
        ToolGroupCatalog.GroupsFor(step.AgentDefinition?.AgentType ?? AgentType.Custom);

    private static bool ReadsSandbox(WorkflowStep step) =>
        step.StepType == StepType.Parallel
        || (step.StepType is StepType.Agent or StepType.ForEach or StepType.ReviewLoop
            && GroupsOf(step).Any(g => g is ToolGroup.SandboxBrowse or ToolGroup.SandboxMetadata or ToolGroup.SandboxLint
                or ToolGroup.SandboxAuthoring or ToolGroup.SandboxRender or ToolGroup.SandboxBlocks));

    private static bool WritesSandbox(WorkflowStep step) =>
        step.StepType is StepType.Revise or StepType.DesignGraphic or StepType.GraphicsRoom or StepType.Parallel
        || (step.StepType is StepType.Agent or StepType.ForEach or StepType.ReviewLoop
            && GroupsOf(step).Any(g => g is ToolGroup.SandboxAuthoring or ToolGroup.SandboxRender or ToolGroup.SandboxBlocks));

    /// <summary>True for a step whose output can be a video another step consumes.</summary>
    private static bool ProducesVideo(WorkflowStep step) =>
        step.StepType is StepType.VideoCompile or StepType.VideoGenerate or StepType.Revise
        || (step.StepType == StepType.Agent && GroupsOf(step).Contains(ToolGroup.SandboxRender));

    /// <summary>The steps whose VIDEO <paramref name="step"/> consumes (for <see cref="FinalVideoStepOrders"/>).</summary>
    private IEnumerable<int> MediaSourcesOf(WorkflowStep step)
    {
        if (step.StepType != StepType.VideoAnalyze)
            yield break;

        JsonObject? config = Parse(step.VideoAnalyzeConfigJson);
        List<JsonNode> sources = [];
        if (Child(config, "source") is JsonObject single)
            sources.Add(single);
        if (Child(config, "sources") is JsonArray many)
            sources.AddRange(many.Where(n => n is not null)!);

        foreach (JsonNode source in sources)
        {
            string? kind = EnumName(Child(source as JsonObject, "kind"), ["ProjectFile", "StepOutput", "PreviousStepOutput"]);
            if (kind == "StepOutput" && Int(source as JsonObject, "stepOrder") is { } stepOrder)
            {
                yield return stepOrder;
            }
            else if (kind == "PreviousStepOutput")
            {
                WorkflowStep? latest = Steps.LastOrDefault(s => s.StepOrder < step.StepOrder && ProducesVideo(s));
                if (latest is not null)
                    yield return latest.StepOrder;
            }
        }
    }

    // ------------------------------------------------------------------
    // Tolerant JSON reading: configs are camelCase in practice, but the engine deserializes them
    // case-insensitively and accepts enum names or numbers, so this reads them the same way.
    // ------------------------------------------------------------------

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode? Child(JsonObject? node, string name)
    {
        if (node is null)
            return null;
        foreach (KeyValuePair<string, JsonNode?> property in node)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
                return property.Value;
        }

        return null;
    }

    private static int? Int(JsonObject? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue(out int number) ? number : null;

    /// <summary>Every integer entry of a JSON array member, skipping anything not an integer.</summary>
    private static IEnumerable<int> IntArray(JsonObject? node, string name)
    {
        if (Child(node, name) is not JsonArray array)
            yield break;
        foreach (JsonNode? entry in array)
        {
            if (entry is JsonValue value && value.TryGetValue(out int number))
                yield return number;
        }
    }

    private static string? Str(JsonObject? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static readonly JsonSerializerOptions EngineConfigOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>
    /// S-63: an insert-content reference bound exactly as the engine binds it (Web options: any member case, a
    /// number written as a string, a padded kind), so the graph sees every <c>from=Step</c> the engine resolves.
    /// <c>Readable</c> is false when the engine could not bind it either (the caller then depends on every
    /// earlier step); <c>StepOrder</c> is set only for a positive <c>from=Step</c> order.
    /// </summary>
    private static (bool Readable, int? StepOrder) InsertStepOrder(JsonNode? reference)
    {
        if (reference is null)
            return (true, null);
        InsertContentRef? bound;
        try
        {
            bound = reference.Deserialize<InsertContentRef>(EngineConfigOptions);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
        {
            return (false, null);
        }

        return bound is { Kind: InsertContentRef.StepKind, StepOrder: > 0 and var order } ? (true, order) : (true, null);
    }

    /// <summary>An enum value written as its name (any case) or its ordinal, normalised to the name.</summary>
    private static string? EnumName(JsonNode? node, string[] names)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out string? text))
            return names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
        if (value.TryGetValue(out int ordinal) && ordinal >= 0 && ordinal < names.Length)
            return names[ordinal];
        return null;
    }
}
