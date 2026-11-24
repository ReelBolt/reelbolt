using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Replay;

namespace ReelBolt.Shared.Workflows.Lint;

/// <summary>
/// The pure half of the pre-run linter: the rules that need only the step list (no database fact), so the
/// same checks run in the API linter, in catalog tests and in the false-positive scan. It covers the step-only
/// members of WL001 to WL026: WL001 to WL005, WL013, WL014, WL019, WL020, WL021, WL022 and WL026
/// (docs/design/workflow-apps.md section 6.3). Reads step configs as tolerantly as the
/// engine does (any member case, enum by name or ordinal) and never throws on a config it cannot read: an
/// unreadable config is the save validators' finding, not a lint finding.
/// </summary>
public static class StepGraphLinter
{
    /// <summary>Evaluates the graph rules over <paramref name="steps"/> (any order; agent definitions should be loaded).</summary>
    public static IReadOnlyList<LintFinding> Evaluate(IEnumerable<WorkflowStep> steps)
    {
        List<WorkflowStep> ordered = [.. steps.OrderBy(s => s.StepOrder)];
        Context context = new(ordered, ordered.Any(s => s.StepType == StepType.Conditional));
        List<LintFinding> findings = [];

        foreach (WorkflowStep step in ordered)
        {
            switch (step.StepType)
            {
                case StepType.Voiceover:
                    Voiceover(context, step, findings);
                    break;
                case StepType.ReviewLoop:
                    ReviewLoop(context, step, findings);
                    break;
                case StepType.VideoCompile:
                    Compile(context, step, findings);
                    break;
            }
        }

        PaidStepsInLoops(context, findings);
        ReviewBeforeFinalCut(context, findings);
        ForwardReferences(context, findings);
        return findings;
    }

    // ------------------------------------------------------------------
    // WL001 voiceover-source-missing
    // ------------------------------------------------------------------

    private static void Voiceover(Context context, WorkflowStep step, List<LintFinding> findings)
    {
        JsonObject? config = LintJson.Parse(step.VoiceoverConfigJson);
        if (LintJson.Child(LintJson.Child(config, "source") as JsonObject, "kind") is null || config is null)
            return;

        JsonObject source = (JsonObject)LintJson.Child(config, "source")!;
        string? kind = LintJson.EnumName(LintJson.Child(source, "kind"), ["Inline", "ScriptScenes", "NarrationPlan", "PickupPlan"]);

        // WL026 voiceover-ignores-script: an inline narration that ignores an earlier step writing the lines.
        if (kind == "Inline" && EarlierScript(context, step) is { } script)
        {
            StepCompatibilityRule rule = StepCompatibilityRules.Get("WL026");
            // Args: stepOrder, stepLabel?, scriptOrder, scriptLabel?, scriptKind.
            findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                rule.Format(
                    ("step", StepPart(step)),
                    ("script", ScriptPart(script))),
                new LintFix(LintFixKind.ChangeSetting, "Point the voiceover at the script", LabelId: "WL026.useScript")));
        }

        (string Member, string Describe, string Code, Func<WorkflowStep, bool> Accepts)? expectation = kind switch
        {
            "ScriptScenes" => ("scriptwriterStepOrder", "a Scriptwriter step", "scriptwriter", s => IsAgent(s, AgentType.ScriptwriterAgent)),
            "NarrationPlan" => ("narrationWriterStepOrder", "a NarrationWriter step", "narrationWriter",
                s => IsAgent(s, AgentType.NarrationWriter) || IsAgent(s, AgentType.NarrationTranslator)),
            "PickupPlan" => ("pickupPlannerStepOrder", "a PickupPlanner step", "pickupPlanner", s => IsAgent(s, AgentType.PickupPlanner)),
            _ => null
        };
        if (expectation is not { } expected)
            return;

        int? target = LintJson.Int(source, expected.Member);
        Problem problem = context.Check(step.StepOrder, target, expected.Accepts, expected.Describe, expected.Code);
        if (target is { } t)
            context.Reported.Add((step.StepOrder, t));
        if (problem.IsProblem)
        {
            StepCompatibilityRule rule = StepCompatibilityRules.Get("WL001");
            // Args: stepOrder, stepLabel?, sourceOrder?, sourceLabel?, problem, expectedKind? (wrongKind only).
            findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                rule.Format(
                    ("step", StepPart(step)),
                    ("source", LintPart.Of(Named(target))),
                    ("problem", LintPart.Of(problem.Text, ProblemTokens(problem, "source")))),
                new LintFix(
                    LintFixKind.RetargetReference,
                    $"Point the voiceover at {expected.Describe}",
                    LabelId: "WL001.retargetSource",
                    Args: LintArgs.Of(("expectedKind", expected.Code))),
                messageKey: $"{rule.Id}.{problem.Code}"));
        }
    }

    /// <summary>
    /// The latest earlier Agent that writes the lines (a script, a narration plan or its translation), or null when
    /// nothing before the voiceover does. A custom or unknown agent is not counted: the finding names a concrete
    /// script writer, so it must be sure one exists.
    /// </summary>
    private static WorkflowStep? EarlierScript(Context context, WorkflowStep voiceover) =>
        context.Steps
            .Where(s => s.StepOrder < voiceover.StepOrder && s.StepType == StepType.Agent
                && s.AgentDefinition?.AgentType is AgentType.ScriptwriterAgent or AgentType.NarrationWriter or AgentType.NarrationTranslator)
            .LastOrDefault();

    /// <summary>The token form of the step that writes the lines: <c>scriptOrder</c>, <c>scriptLabel</c>? and <c>scriptKind</c>.</summary>
    private static LintPart ScriptPart(WorkflowStep script)
    {
        string kind = script.AgentDefinition?.AgentType switch
        {
            AgentType.NarrationWriter => "narrationWriter",
            AgentType.NarrationTranslator => "narrationTranslator",
            _ => "scriptwriter"
        };
        return LintPart.Of(
            Describe(script),
            ("scriptOrder", Num(script.StepOrder)),
            ("scriptLabel", script.Label),
            ("scriptKind", kind));
    }

    // ------------------------------------------------------------------
    // WL003 reviewloop-target-invalid
    // ------------------------------------------------------------------

    /// <summary>
    /// Step types that make something new when a review loop sends the run back over them: the model steps, plus
    /// the dailies room (a token room) and video generation (a new clip on every pass). The executor itself jumps
    /// back to ANY existing step, so this only decides whether the loop can change the result at all.
    /// </summary>
    private static bool CanImproveOnRepeat(WorkflowStep s) =>
        WorkflowStepDependencyGraph.IsModelStep(s) || s.StepType is StepType.DailiesRoom or StepType.VideoGenerate;

    private static void ReviewLoop(Context context, WorkflowStep step, List<LintFinding> findings)
    {
        int? target = step.LoopTargetStepOrder;
        // A target that is missing, the loop itself or later is a defect the executor cannot honour (error).
        Problem problem = context.Check(step.StepOrder, target, _ => true, "a step", "anyStep");
        if (target is { } t)
            context.Reported.Add((step.StepOrder, t));
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL003");
        if (problem.IsProblem)
        {
            // Args: stepOrder, stepLabel?, targetOrder?, targetLabel?, problem.
            findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                rule.Format(
                    ("step", StepPart(step)),
                    ("target", LintPart.Of(Named(target))),
                    ("problem", LintPart.Of(problem.Text, ProblemTokens(problem, "target")))),
                new LintFix(LintFixKind.RetargetReference, "Point the review at the step it should send back", LabelId: "WL003.retargetTarget"),
                messageKey: $"{rule.Id}.{problem.Code}"));
            return;
        }

        // The loop repeats [target, loop): it can only change the result when something in that span makes new
        // output on a repeat (a model step, the dailies room, a video generation). Otherwise it is only a warning:
        // it runs, it just repeats fixed steps.
        if (!context.Steps.Any(s => s.StepOrder >= target && s.StepOrder < step.StepOrder && CanImproveOnRepeat(s)))
        {
            // Args: stepOrder, stepLabel?, targetOrder (a step with no label is still named by Describe above).
            findings.Add(new LintFinding(rule.Id, LintSeverity.Warning, step.StepOrder,
                new LintMessage(
                    $"The review at {Describe(step)} goes back over {Named(target)} and the steps after it, but none of them makes anything new on a repeat. The loop would not improve the result.",
                    LintArgs.Of(("stepOrder", Num(step.StepOrder)), ("stepLabel", step.Label), ("targetOrder", Num(target!.Value)))),
                new LintFix(LintFixKind.RetargetReference, "Point the review at a step a model writes", LabelId: "WL003.retargetModelStep"),
                messageKey: $"{rule.Id}.noImprovement"));
        }
    }

    // ------------------------------------------------------------------
    // VideoCompile: WL002, WL004, WL005, WL019
    // ------------------------------------------------------------------

    private static readonly HashSet<StepType> NeverADecision =
    [
        StepType.Voiceover, StepType.VideoAnalyze, StepType.VideoCompile, StepType.VideoGenerate,
        StepType.AudioGenerate, StepType.ThumbnailGenerate, StepType.TimelineRender, StepType.ExtractFrame,
        StepType.DesignSystem, StepType.PinInsert, StepType.AudioCleanup, StepType.VideoEnhance, StepType.ImageGenerate
    ];

    private static void Compile(Context context, WorkflowStep step, List<LintFinding> findings)
    {
        JsonObject? config = LintJson.Parse(step.VideoCompileConfigJson);
        if (config is null)
            return;

        bool keepWhole = LintJson.Bool(config, "keepWholeSources");
        bool captions = LintJson.Bool(config, "enableCaptions");
        bool voiceover = LintJson.Bool(config, "enableVoiceover");

        // ---- WL002 ----
        if (voiceover)
            CheckVoiceoverWiring(context, step, config, "voiceoverStepOrder", "narration", "narration", findings);
        if (LintJson.Bool(config, "enablePickups"))
            CheckVoiceoverWiring(context, step, config, "pickupVoiceoverStepOrder", "re-recorded lines", "pickups", findings);

        // ---- WL004 ----
        Func<WorkflowStep, bool> decisionOk = s => !NeverADecision.Contains(s.StepType);
        if (!keepWhole)
            CheckRef(context, step, LintJson.Child(config, "decision"), "edit decision", "editDecision", decisionOk,
                "a step that makes the edit decision", "editDecision", findings);

        int? analysis = LintJson.Int(config, "analysisStepOrder");
        if (analysis is { } analysisOrder)
        {
            CheckTarget(context, step, analysisOrder, "analysis", "analysis", s => s.StepType == StepType.VideoAnalyze,
                "a video analysis step", "videoAnalysis", findings);
        }

        if (LintJson.Int(config, "captionTranslationStepOrder") is { } translation &&
            string.Equals(LintJson.Str(config, "captionSource"), "Transcript", StringComparison.OrdinalIgnoreCase))
        {
            CheckTarget(context, step, translation, "caption translation", "captionTranslation", s => IsAgent(s, AgentType.NarrationTranslator),
                "a step that translates the transcript", "transcriptTranslation", findings);
        }

        if (LintJson.Child(config, "takeSelection") is { } takes)
            CheckRef(context, step, takes, "take selection", "takeSelection", decisionOk, "a step that selects takes", "takeSelection", findings);
        (string Key, string Enable, string Name, string Code)[] plans =
        [
            ("graphicsPlan", "enableGraphics", "graphics plan", "graphicsPlan"),
            ("musicPlan", "enableMusic", "music plan", "musicPlan"),
            ("colorGradePlan", "enableColorGrade", "colour grade plan", "colorGradePlan"),
            ("sfxPlan", "enableSfx", "sound-effects plan", "sfxPlan")
        ];
        foreach ((string key, string enable, string name, string code) in plans)
        {
            if (LintJson.Bool(config, enable) && LintJson.Child(config, key) is { } plan)
                CheckRef(context, step, plan, name, code, decisionOk, "a step that writes the plan", "planWriter", findings);
        }

        // The picture-overlay plan is written by an OverlayPlanner agent, never by the steps that make the cut.
        if (LintJson.Child(config, "mediaOverlayPlan") is { } overlayPlan)
        {
            CheckRef(context, step, overlayPlan, "picture overlay plan", "mediaOverlayPlan",
                s => IsAgent(s, AgentType.OverlayPlanner), "a step that plans picture overlays", "overlayPlanner", findings);
        }

        if (LintJson.Bool(config, "enableGeneratedClips") && LintJson.Child(config, "generatedClips") is { } clips)
            CheckRef(context, step, clips, "generated clips", "generatedClips", s => s.StepType == StepType.VideoGenerate,
                "a video generation step", "videoGeneration", findings);

        // ---- WL005 ----
        if (LintJson.EnumName(LintJson.Child(config, "mode"), ["Reencode", "StreamCopy"]) == "StreamCopy")
        {
            // Each feature is a code plus the English words the (unchanged) message renders it as.
            List<(string Code, string Text)> features = [];
            if (LintJson.Bool(config, "enableMusic")) features.Add(("music", "music"));
            if (voiceover) features.Add(("narration", "narration"));
            if (LintJson.Bool(config, "enablePickups")) features.Add(("reRecordedLines", "re-recorded lines"));
            if (LintJson.Bool(config, "enableGraphics")) features.Add(("onScreenGraphics", "on-screen graphics"));
            if (LintJson.Bool(config, "enableSfx")) features.Add(("soundEffects", "sound effects"));
            if (LintJson.Bool(config, "enableColorGrade")) features.Add(("colorGrade", "a colour grade"));
            if (LintJson.Bool(config, "enableInserts")) features.Add(("insertedScreens", "inserted screens"));
            if (LintJson.Bool(config, "enableGeneratedClips")) features.Add(("generatedClips", "generated clips"));
            if (captions) features.Add(("captions", "captions"));
            if (LintJson.EnumName(LintJson.Child(config, "outputFormat"), ["Source", "Vertical", "Square", "Landscape", "Portrait"]) is { } format and not "Source")
                features.Add(("differentOutputFormat", "a different output format"));
            if (LintJson.EnumName(LintJson.Child(config, "voiceoverMode"), ["OverMusic", "DuckDialogue", "ReplaceDialogue"]) is { } vm and not "OverMusic")
                features.Add(("duckedOrReplacedDialogue", "ducked or replaced dialogue"));
            if (LintJson.Child(config, "censorLabels") is JsonArray { Count: > 0 }) features.Add(("censoring", "censoring"));
            if (new[] { "programFadeInMs", "programFadeOutMs", "programAudioFadeInMs", "programAudioFadeOutMs" }.Any(k => LintJson.Int(config, k) > 0))
                features.Add(("fades", "fades"));
            if (LintJson.Child(config, "regionEffects") is JsonArray { Count: > 0 }) features.Add(("regionEffects", "region effects"));
            string? transitions = LintJson.EnumName(LintJson.Child(config, "transitionPolicy"), ["Off", "Auto"]);
            if (transitions is not null && transitions != "Off") features.Add(("transitions", "transitions"));

            if (!LintJson.Bool(config, "allowKeyframeSnapping"))
                features.Add(("keyframesNotSnapped", "cuts that land between keyframes (allowKeyframeSnapping is off)"));

            if (features.Count > 0)
            {
                StepCompatibilityRule rule = StepCompatibilityRules.Get("WL005");
                // Args: stepOrder, stepLabel?, featureCount, feature1…featureN (codes, in message order).
                findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                    rule.Format(
                        ("step", StepPart(step)),
                        ("features", LintPart.Of(
                            string.Join(", ", features.Select(f => f.Text)),
                            LintArgs.Indexed("feature", [.. features.Select(f => f.Code)])))),
                    new LintFix(LintFixKind.ChangeSetting, "Switch the compile to re-encode", LabelId: "WL005.reencode")));
            }
        }

        // ---- WL021 ----
        NarratedFilmUnderVoiceover(context, step, config, findings);

        // ---- WL022 ----
        CensorWithoutFeatureTrack(context, step, config, findings);

        // ---- WL019 ----
        bool transcriptSource = string.Equals(LintJson.Str(config, "captionSource"), "Transcript", StringComparison.OrdinalIgnoreCase);
        // The transcript source is judged on its own whatever the voiceover setting: it never reads the narration.
        if (captions && (transcriptSource ? !HasTranscriptCaptionSource(context, config) : !voiceover))
        {
            StepCompatibilityRule rule = StepCompatibilityRules.Get("WL019");
            // Args: stepOrder, stepLabel?.
            findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                rule.Format(("step", StepPart(step))),
                new LintFix(LintFixKind.ChangeSetting, "Turn on the narration for this compile, or turn captions off", LabelId: "WL019.voiceOrCaptions")));
        }
    }

    private static void CensorWithoutFeatureTrack(Context context, WorkflowStep step, JsonObject config, List<LintFinding> findings)
    {
        if (LintJson.Child(config, "censorLabels") is not JsonArray { Count: > 0 } labels
            || LintJson.Int(config, "analysisStepOrder") is not { } order
            || context.ByOrder.GetValueOrDefault(order) is not { StepType: StepType.VideoAnalyze } analysis
            || LintJson.Child(LintJson.Parse(analysis.VideoAnalyzeConfigJson), "trackObjects") is not JsonArray targets)
            return;

        // label (case-insensitive) -> has at least one feature-method track; a missing method is Feature.
        Dictionary<string, bool> hasFeature = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonObject target in targets.OfType<JsonObject>())
        {
            string? label = LintJson.Str(target, "label")?.Trim();
            if (string.IsNullOrEmpty(label))
                continue;
            string? method = LintJson.EnumName(LintJson.Child(target, "method"), ["Feature", "Segmentation"]);
            hasFeature[label] = hasFeature.GetValueOrDefault(label) || method != "Segmentation";
        }

        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL022");
        foreach (string label in labels.Select(l => (l as JsonValue)?.TryGetValue(out string? v) == true ? v?.Trim() : null)
                     .Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.OrdinalIgnoreCase)!)
        {
            if (hasFeature.TryGetValue(label, out bool feature) && !feature)
                findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                    rule.Format(("label", label)),
                    new LintFix(LintFixKind.ChangeSetting, "Blur this track with a region effect instead of the censor")));
        }
    }

    private static readonly string[] SourceKinds =
        ["ProjectFile", "StepOutput", "PreviousStepOutput", "GeneratedClips", "StepResult", "BriefFiles"];

    private static void NarratedFilmUnderVoiceover(Context context, WorkflowStep step, JsonObject config, List<LintFinding> findings)
    {
        if (!LintJson.Bool(config, "enableVoiceover") || LintJson.Bool(config, "muteSourceAudio"))
            return;
        if (LintJson.EnumName(LintJson.Child(config, "voiceoverMode"), ["OverMusic", "DuckDialogue", "ReplaceDialogue"]) == "ReplaceDialogue")
            return;

        List<JsonObject> refs = [];
        AddSourceRefs(config, refs);
        WorkflowStep reader = step;
        if (LintJson.Int(config, "analysisStepOrder") is { } analysisOrder
            && context.ByOrder.GetValueOrDefault(analysisOrder) is { StepType: StepType.VideoAnalyze } analysis
            && LintJson.Parse(analysis.VideoAnalyzeConfigJson) is { } analyze)
        {
            // The analysis step reads the picture, so its sources resolve relative to it.
            refs.Clear();
            AddSourceRefs(analyze, refs);
            reader = analysis;
        }

        WorkflowStep? narrated = ResolveFrom(context, reader, refs).FirstOrDefault(s => s is not null);
        if (narrated is null)
            return;
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL021");
        // Args: stepOrder, stepLabel?, sourceOrder, sourceLabel?.
        findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
            rule.Format(("step", StepPart(step)), ("source", TargetPart(narrated.StepOrder, narrated, "source"))),
            new LintFix(
                LintFixKind.ChangeSetting,
                "Turn on muteSourceAudio (only the new voiceover, music and sound effects play)",
                LabelId: "WL021.muteSourceAudio")));
    }

    private static void AddSourceRefs(JsonObject config, List<JsonObject> refs)
    {
        if (LintJson.Child(config, "sources") is JsonArray many)
        {
            refs.AddRange(many.OfType<JsonObject>());
            if (many.Count > 0)
                return;
        }

        if (LintJson.Child(config, "source") is JsonObject one)
            refs.Add(one);
    }

    private static IEnumerable<WorkflowStep?> ResolveFrom(Context context, WorkflowStep reader, List<JsonObject> refs)
    {
        foreach (JsonObject reference in refs)
        {
            string? kind = LintJson.EnumName(LintJson.Child(reference, "kind"), SourceKinds);
            WorkflowStep? source = kind switch
            {
                "StepOutput" when LintJson.Int(reference, "stepOrder") is { } n => context.ByOrder.GetValueOrDefault(n),
                "PreviousStepOutput" => context.Steps.Where(s => s.StepOrder < reader.StepOrder && !NoFilmOutput.Contains(s.StepType))
                    .OrderByDescending(s => s.StepOrder).FirstOrDefault(),
                _ => null
            };
            if (source is not null && source.StepOrder < reader.StepOrder && CarriesNarration(context, source))
                yield return source;
        }
    }

    private static readonly HashSet<StepType> NoFilmOutput =
    [
        StepType.Voiceover, StepType.VideoAnalyze, StepType.DesignSystem, StepType.Extract, StepType.ReviewLoop,
        StepType.Conditional, StepType.ExtractFrame, StepType.VideoEnhance
    ];

    private static bool CarriesNarration(Context context, WorkflowStep source)
    {
        if (source.StepType == StepType.Revise)
            return true;
        if (source.StepType == StepType.Agent && !string.IsNullOrWhiteSpace(source.ContinueCompositionJson))
            return true;
        if (source.StepType is StepType.Agent or StepType.TimelineRender
            && context.Steps.Any(s => s.StepType == StepType.Voiceover && s.StepOrder < source.StepOrder))
            return true;
        return source.StepType == StepType.VideoCompile
            && LintJson.Bool(LintJson.Parse(source.VideoCompileConfigJson), "enableVoiceover");
    }

    private static bool HasTranscriptCaptionSource(Context context, JsonObject config)
    {
        // captionSource = Transcript burns the analysis step's own transcript (docs/voiceover.md "Subtitles from the transcript").
        if (!string.Equals(LintJson.Str(config, "captionSource"), "Transcript", StringComparison.OrdinalIgnoreCase))
            return false;
        if (LintJson.Int(config, "analysisStepOrder") is not { } order || context.ByOrder.GetValueOrDefault(order) is not { StepType: StepType.VideoAnalyze } analysis)
            return false;
        JsonObject? analyze = LintJson.Parse(analysis.VideoAnalyzeConfigJson);
        string? mode = LintJson.EnumName(LintJson.Child(analyze, "transcription"), ["Off", "Optional", "Required"]);
        return mode != "Off";
    }

    private static void CheckVoiceoverWiring(
        Context context, WorkflowStep step, JsonObject config, string member, string what, string whatCode, List<LintFinding> findings)
    {
        int? target = LintJson.Int(config, member);
        Problem problem = target is null or <= 0
            ? new Problem(true, "doesNotNameTheStep", "does not say which voiceover step to use")
            : context.Check(step.StepOrder, target, s => s.StepType == StepType.Voiceover, "a Voiceover step", "voiceover");
        if (target is { } t)
            context.Reported.Add((step.StepOrder, t));
        if (!problem.IsProblem)
            return;

        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL002");
        // Args: stepOrder, stepLabel?, voiceKind, problem, targetOrder?, targetLabel?, expectedKind?.
        findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
            rule.Format(
                ("step", StepPart(step)),
                ("problem", LintPart.Of(
                    $"the {what} step it names {problem.Text}",
                    [("voiceKind", whatCode), .. ProblemTokens(problem, "target")]))),
            new LintFix(LintFixKind.RetargetReference, "Point the compile at the Voiceover step", LabelId: "WL002.retargetVoiceover"),
            messageKey: $"{rule.Id}.{problem.Code}"));
    }

    private static void CheckRef(
        Context context, WorkflowStep step, JsonNode? reference, string input, string inputCode,
        Func<WorkflowStep, bool> accepts, string expected, string expectedCode, List<LintFinding> findings)
    {
        if (reference is not JsonObject obj)
            return;
        string? from = LintJson.EnumName(LintJson.Child(obj, "from"), ["Previous", "Step", "Accumulated", "ProjectFiles"]);
        switch (from)
        {
            case "Step":
                CheckTarget(context, step, LintJson.Int(obj, "stepOrder"), input, inputCode, accepts, expected, expectedCode, findings);
                break;
            case "Previous":
                {
                    WorkflowStep? previous = context.Steps.LastOrDefault(s => s.StepOrder < step.StepOrder);
                    if (previous is null)
                    {
                        AddCompileRefFinding(step, input, inputCode, "the step before it",
                            new Problem(true, "firstStep", "does not exist: it is the first step"), findings);
                        break;
                    }

                    // After a branch, "previous" can be any earlier step; only judge the type when there is no branch.
                    if (!context.HasBranches && !accepts(previous))
                    {
                        AddCompileRefFinding(step, input, inputCode, Named(previous.StepOrder),
                            new Problem(true, "previousWrongKind", $"is not {expected}: the step right before it is {Describe(previous)}",
                                previous.StepOrder, previous, expectedCode), findings);
                    }

                    break;
                }
        }
    }

    private static void CheckTarget(
        Context context, WorkflowStep step, int? target, string input, string inputCode,
        Func<WorkflowStep, bool> accepts, string expected, string expectedCode, List<LintFinding> findings)
    {
        Problem problem = context.Check(step.StepOrder, target, accepts, expected, expectedCode);
        if (target is { } t)
            context.Reported.Add((step.StepOrder, t));
        if (problem.IsProblem)
            AddCompileRefFinding(step, input, inputCode, Named(target), problem, findings);
    }

    private static void AddCompileRefFinding(
        WorkflowStep step, string input, string inputCode, string targetText, Problem problem, List<LintFinding> findings)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL004");
        // Args: stepOrder, stepLabel?, input, problem, targetOrder?, targetLabel?, expectedKind?.
        findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
            rule.Format(
                ("step", StepPart(step)),
                ("input", LintPart.Of(input, ("input", inputCode))),
                ("target", LintPart.Of(targetText)),
                ("problem", LintPart.Of(problem.Text, ProblemTokens(problem, "target")))),
            new LintFix(
                LintFixKind.RetargetReference,
                $"Point the {input} at the right step",
                LabelId: "WL004.retargetInput",
                Args: LintArgs.Of(("input", inputCode))),
            messageKey: $"{rule.Id}.{problem.Code}"));
    }

    // ------------------------------------------------------------------
    // WL020 review-before-final-cut
    // ------------------------------------------------------------------

    private static void ReviewBeforeFinalCut(Context context, List<LintFinding> findings)
    {
        StepCompatibilityRule rule = StepCompatibilityRules.Get("WL020");
        foreach (WorkflowStep review in context.Steps.Where(s => s.StepType == StepType.ReviewLoop))
        {
            // A review of a script or an edit decision, ahead of the voice, is a legitimate text review: only a loop
            // whose target produces the film (or has no readable target) is judging a picture without its voice.
            if (review.LoopTargetStepOrder is { } targetOrder
                && context.Steps.FirstOrDefault(s => s.StepOrder == targetOrder) is { } target
                && !ProducesFilm(target))
                continue;

            // Encode-once: the one Final-stage compile is deliberately after the review - it encodes the cut the
            // review ended on, so it is the output being reviewed rather than a cut made after it. A Plan-stage
            // compile before the review is what the review judges.
            bool encodeOnce = context.Steps.Any(s => s.StepOrder < review.StepOrder && CompileEncodeStages.IsPlanStage(s));
            List<WorkflowStep> later = [.. context.Steps.Where(s => s.StepOrder > review.StepOrder
                && s.StepType is StepType.Voiceover or StepType.VideoCompile or StepType.AudioGenerate
                && !(encodeOnce && CompileEncodeStages.Of(s) == CompileEncodeStage.Final))];
            if (later.Count == 0)
                continue;

            (string Code, string Text)[] parts = [.. later.Select(s => s.StepType switch
            {
                StepType.Voiceover => ("narration", $"the narration (step {s.StepOrder})"),
                StepType.AudioGenerate => ("music", $"the music (step {s.StepOrder})"),
                _ => ("finalCut", $"the final cut (step {s.StepOrder})")
            })];
            string joined = parts.Length == 1
                ? parts[0].Text
                : string.Join(", ", parts.Take(parts.Length - 1).Select(p => p.Text)) + " and " + parts[^1].Text;

            // Args: reviewOrder, reviewLabel?, lastOrder, laterCount, later1Kind/later1Order … laterNKind/laterNOrder.
            List<(string, string?)> laterTokens = [];
            for (int i = 0; i < later.Count; i++)
            {
                string index = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                laterTokens.Add(("later" + index + "Kind", parts[i].Code));
                laterTokens.Add(("later" + index + "Order", Num(later[i].StepOrder)));
            }

            int last = later.Max(s => s.StepOrder);
            findings.Add(new LintFinding(rule.Id, rule.Severity, review.StepOrder,
                new LintMessage(
                    rule.Format(("review", LintPart.Of(Num(review.StepOrder))), ("later", LintPart.Of(joined)), ("last", LintPart.Of(Num(last))))
                        .Text,
                    LintArgs.Of(
                        [("reviewOrder", Num(review.StepOrder)), ("reviewLabel", review.Label), ("lastOrder", Num(last)),
                         ("laterCount", later.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)), .. laterTokens])),
                new LintFix(LintFixKind.None, "Move the review after the final cut", LabelId: "WL020.moveReview")));
        }
    }

    /// <summary>
    /// False only for a target that plainly makes text or data (a script, an analysis, an extract); every other step,
    /// the edit/graphics/grade rooms included, feeds the film a review is meant to judge.
    /// </summary>
    private static bool ProducesFilm(WorkflowStep step) => step.StepType switch
    {
        StepType.Agent => IsAgent(step, AgentType.AuthorAgent) || IsAgent(step, AgentType.CompositionReviser)
            || IsAgent(step, AgentType.VideoStoryEditor) || IsAgent(step, AgentType.MotionGraphicsPlanner),
        StepType.Extract or StepType.DesignSystem or StepType.Conditional or StepType.VideoAnalyze
            or StepType.ExtractFrame or StepType.PinInsert or StepType.AudioCleanup or StepType.VideoEnhance => false,
        _ => true
    };

    // ------------------------------------------------------------------
    // WL013 paid-step-in-loop
    // ------------------------------------------------------------------

    private static void PaidStepsInLoops(Context context, List<LintFinding> findings)
    {
        HashSet<(int Paid, int Loop)> seen = [];
        foreach (WorkflowStep loop in context.Steps.Where(s => s.StepType == StepType.ReviewLoop))
        {
            if (loop.LoopTargetStepOrder is not { } target || target >= loop.StepOrder)
                continue;

            foreach (WorkflowStep paid in context.Steps.Where(s =>
                         s.StepOrder >= target && s.StepOrder < loop.StepOrder
                         && StepCompatibilityRules.AppliesTo("WL013").Contains(s.StepType)))
            {
                if (!seen.Add((paid.StepOrder, loop.StepOrder)))
                    continue;
                StepCompatibilityRule rule = StepCompatibilityRules.Get("WL013");
                // Args: stepOrder, stepLabel?, loopOrder.
                findings.Add(new LintFinding(rule.Id, rule.Severity, paid.StepOrder,
                    rule.Format(
                        ("step", StepPart(paid)),
                        ("loop", LintPart.Of(Num(loop.StepOrder), ("loopOrder", Num(loop.StepOrder))))),
                    new LintFix(LintFixKind.RetargetReference, "Start the review loop after this step", LabelId: "WL013.startLoopAfter")));
            }
        }
    }

    // ------------------------------------------------------------------
    // WL014 forward-step-reference
    // ------------------------------------------------------------------

    private static void ForwardReferences(Context context, List<LintFinding> findings)
    {
        foreach (WorkflowStep step in context.Steps)
        {
            HashSet<int> reportedHere = [];
            foreach (int target in References(step))
            {
                if (target < step.StepOrder
                    || context.Reported.Contains((step.StepOrder, target))
                    || !reportedHere.Add(target))
                    continue;

                StepCompatibilityRule rule = StepCompatibilityRules.Get("WL014");
                // Args: stepOrder, stepLabel?, targetOrder.
                findings.Add(new LintFinding(rule.Id, rule.Severity, step.StepOrder,
                    rule.Format(
                        ("step", StepPart(step)),
                        ("target", LintPart.Of(Num(target), ("targetOrder", Num(target))))),
                    new LintFix(LintFixKind.RetargetReference, "Point the input at an earlier step", LabelId: "WL014.earlierStep")));
            }
        }
    }

    private static IEnumerable<int> References(WorkflowStep step)
    {
        foreach (string? json in new[]
                 {
                     step.ExtractConfigJson, step.VideoAnalyzeConfigJson, step.VideoCompileConfigJson, step.VideoGenerateConfigJson,
                     step.EditRoomConfigJson, step.GraphicsRoomConfigJson, step.ColorGradeRoomConfigJson, step.ConditionalConfigJson,
                     step.ReviewLoopConfigJson, step.VoiceoverConfigJson, step.ThumbnailGenerateConfigJson, step.DailiesRoomConfigJson,
                     step.TimelineRenderConfigJson, step.DesignSystemConfigJson, step.ExtractFrameConfigJson, step.AudioGenerateConfigJson,
                     step.PinInsertConfigJson, step.AudioCleanupConfigJson, step.VideoEnhanceConfigJson, step.ImageGenerateConfigJson
                 })
        {
            if (LintJson.Parse(json) is { } root)
            {
                foreach (int order in LintJson.StepOrderValues(root))
                    yield return order;
            }
        }

        if (LintJson.ParseArray(step.SelectedPriorStepOrdersJson) is { } selected)
        {
            foreach (JsonNode? entry in selected)
            {
                if (entry is JsonValue value && value.TryGetValue(out int order))
                    yield return order;
            }
        }
    }

    // ------------------------------------------------------------------
    // Shared
    // ------------------------------------------------------------------

    /// <summary>
    /// The verdict on a reference: <see cref="Text"/> is the English clause the (unchanged) message embeds,
    /// <see cref="Code"/> is the token a client renders it from, and <see cref="Target"/>/<see cref="Found"/> say which
    /// step it names so the finding can carry its order and label. <see cref="Expected"/> is the kind token for
    /// <c>wrongKind</c>.
    /// </summary>
    private readonly record struct Problem(
        bool IsProblem, string Code, string Text, int? Target = null, WorkflowStep? Found = null, string? Expected = null);

    private sealed class Context(List<WorkflowStep> steps, bool hasBranches)
    {
        public List<WorkflowStep> Steps { get; } = steps;

        public bool HasBranches { get; } = hasBranches;

        public Dictionary<int, WorkflowStep> ByOrder { get; } = steps.GroupBy(s => s.StepOrder).ToDictionary(g => g.Key, g => g.First());

        /// <summary>(step, referenced step) pairs a specific rule already judged, so WL014 does not repeat them.</summary>
        public HashSet<(int Step, int Target)> Reported { get; } = [];

        public Problem Check(int ownOrder, int? target, Func<WorkflowStep, bool> accepts, string expected, string expectedCode)
        {
            if (target is null or <= 0)
                return new Problem(true, "isNotSet", "is not set");
            if (!ByOrder.TryGetValue(target.Value, out WorkflowStep? found))
                return new Problem(true, "notInWorkflow", "is not a step in this workflow", target);
            if (target.Value >= ownOrder)
                return new Problem(true, "comesAfter", "comes after it, so its result does not exist yet", target, found);
            return accepts(found)
                ? new Problem(false, "ok", string.Empty)
                : new Problem(true, "wrongKind", $"is not {expected} (it is {Describe(found)})", target, found, expectedCode);
        }
    }

    private static bool IsAgent(WorkflowStep step, AgentType type)
    {
        if (step.StepType != StepType.Agent)
            return false;
        // An unknown or custom agent could emit the schema the source needs; do not judge it.
        AgentType? actual = step.AgentDefinition?.AgentType;
        return actual is null || actual == AgentType.Custom || actual == type;
    }

    private static string Num(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Named(int? order) => order is > 0 ? $"step {order}" : "no step";

    /// <summary>"step 4" or "step 4 (Draft narration)": names, never ids.</summary>
    public static string Describe(WorkflowStep step) =>
        string.IsNullOrWhiteSpace(step.Label) ? $"step {step.StepOrder}" : $"step {step.StepOrder} ({step.Label})";

    /// <summary>The token form of a step: <c>stepOrder</c> plus <c>stepLabel</c> only when the step has one.</summary>
    public static LintPart StepPart(WorkflowStep step) =>
        LintPart.Of(Describe(step), ("stepOrder", Num(step.StepOrder)), ("stepLabel", step.Label));

    /// <summary>
    /// The token form of a referenced step: <c>&lt;prefix&gt;Order</c> (the digits) plus <c>&lt;prefix&gt;Label</c> when the
    /// step exists and has a label. The text is "step N (Label)" when the step is known, "step N"/"no step" otherwise.
    /// </summary>
    private static LintPart TargetPart(int? order, WorkflowStep? found, string prefix = "target") =>
        LintPart.Of(
            found is not null ? Describe(found) : Named(order),
            (prefix + "Order", order is > 0 ? Num(order.Value) : null),
            (prefix + "Label", found?.Label));

    /// <summary>The tokens a <see cref="Problem"/> contributes: <c>problem</c>, the referenced step's order/label (under
    /// <paramref name="prefix"/>) and <c>expectedKind</c> for <c>wrongKind</c>.</summary>
    private static (string Name, string? Value)[] ProblemTokens(Problem problem, string prefix)
    {
        List<(string, string?)> tokens = [("problem", problem.Code)];
        if (problem.Target is > 0)
            tokens.Add((prefix + "Order", Num(problem.Target.Value)));
        if (!string.IsNullOrWhiteSpace(problem.Found?.Label))
            tokens.Add((prefix + "Label", problem.Found.Label));
        if (problem.Expected is not null)
            tokens.Add(("expectedKind", problem.Expected));
        return [.. tokens];
    }
}

/// <summary>Tolerant JSON reading for step configs: the engine binds any member case and enums by name or ordinal.</summary>
internal static class LintJson
{
    public static JsonObject? Parse(string? json)
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

    public static JsonArray? ParseArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static JsonNode? Child(JsonObject? node, string name)
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

    public static int? Int(JsonObject? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue(out int number) ? number : null;

    public static string? Str(JsonObject? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    public static bool Bool(JsonObject? node, string name) =>
        Child(node, name) is JsonValue value && value.TryGetValue(out bool flag) && flag;

    public static string? EnumName(JsonNode? node, string[] names)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out string? text))
            return names.FirstOrDefault(n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
        if (value.TryGetValue(out int ordinal) && ordinal >= 0 && ordinal < names.Length)
            return names[ordinal];
        return null;
    }

    /// <summary>Every integer under a member whose name ends in <c>StepOrder</c> or <c>StepOrders</c>, at any depth.</summary>
    public static IEnumerable<int> StepOrderValues(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    bool single = property.Key.EndsWith("steporder", StringComparison.OrdinalIgnoreCase);
                    bool many = property.Key.EndsWith("steporders", StringComparison.OrdinalIgnoreCase);
                    if (property.Value is null)
                        continue;
                    if (single && property.Value is JsonValue one && one.TryGetValue(out int order) && order > 0)
                        yield return order;
                    else if (many && property.Value is JsonArray list)
                    {
                        foreach (JsonNode? entry in list)
                        {
                            if (entry is JsonValue v && v.TryGetValue(out int o) && o > 0)
                                yield return o;
                        }
                    }
                    else
                    {
                        foreach (int nested in StepOrderValues(property.Value))
                            yield return nested;
                    }
                }

                break;
            case JsonArray array:
                foreach (JsonNode? entry in array)
                {
                    if (entry is not null)
                    {
                        foreach (int nested in StepOrderValues(entry))
                            yield return nested;
                    }
                }

                break;
        }
    }
}
