using System.Text.Json.Serialization;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Shared.Workflows;

public sealed record WorkflowTemplateStepDefinition(
    AgentType AgentType,
    string Label,
    StepType StepType = StepType.Agent,
    string? ConditionExpression = null,
    string? LoopSourceExpression = null,
    int? LoopTargetStepOrder = null,
    int MaxIterations = 3,
    int? MinScore = null,
    string? InputMappingJson = null,
    AgentInputContextMode? AgentInputContextMode = null,
    IReadOnlyList<int>? SelectedPriorStepOrders = null,
    string? TrueBranchStepOrder = null,
    string? FalseBranchStepOrder = null,
    IReadOnlyList<AgentType>? ParallelAgentTypes = null,
    string? ExtractConfigJson = null,
    string? VideoAnalyzeConfigJson = null,
    string? VideoCompileConfigJson = null,
    string? EditRoomConfigJson = null,
    string? GraphicsRoomConfigJson = null,
    string? ColorGradeRoomConfigJson = null,
    string? ConditionalConfigJson = null,
    string? ReviewLoopConfigJson = null,
    // Editorial/creative-judgment steps (translation, strategy, scriptwriting, directing,
    // authoring, review, and every video-editorial agent/room) are pinned to Never so a rerun
    // always gets a fresh decision instead of silently replaying a stale one from the cache.
    // Deterministic extraction/analysis steps (the five *Analyzer agents, Extract, VideoAnalyze,
    // VideoCompile) are left at Default, which the built-in StepCachePolicy already treats as
    // cacheable for a read-only/deterministic step. See docs/workflow-execution-optimization.md.
    StepCacheMode? CacheMode = null,
    string? ThumbnailGenerateConfigJson = null,
    string? VideoGenerateConfigJson = null,
    string? VoiceoverConfigJson = null,
    string? DailiesRoomConfigJson = null,
    string? TimelineRenderConfigJson = null,
    string? DesignSystemConfigJson = null,
    string? ExtractFrameConfigJson = null,
    string? AudioGenerateConfigJson = null,
    string? PinInsertConfigJson = null,
    string? AudioCleanupConfigJson = null,
    string? VideoEnhanceConfigJson = null,
    string? ImageGenerateConfigJson = null);

public sealed record WorkflowTemplateDefinition(
    string Key,
    string Name,
    string Description,
    int Version,
    bool AutoCreateOnProject,
    bool RequiresUserInput,
    IReadOnlyList<WorkflowTemplateStepDefinition> Steps,
    // The curated app manifest (docs/workflow-apps.md), copied onto the workflow at provisioning. Null = the app
    // view derives one from the steps. A manifest alone never bumps Version.
    AppManifest? AppManifest = null,
    // ---- app inputs (docs/design/app-inputs.md 9.5, 14), appended ----
    // Hidden: kept for the workflows already made from it, but left out of every list (the gallery, the template list).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Hidden = false,
    // The template a hidden one was merged into: creating from this key creates that one (WorkflowTemplateAppInputs).
    string? SupersededBy = null,
    // The agent each agent-model input of the manifest picks for (input id to agent type): a template names agent
    // types, so provisioning writes the built-in agent's id into the input's "agent" member.
    IReadOnlyDictionary<string, AgentType>? AppInputAgents = null);

public static class WorkflowTemplateCatalog
{
    public const string StarterTemplateKey = "quick-win-promo";

    /// <summary>The story-first narrated template (script written before the cut).</summary>
    public const string NarratedStoryTemplateKey = "narrated-story";

    /// <summary>Builds the project's brand and product profile from its design files and source code.</summary>
    public const string ProductProfileFromCodeTemplateKey = "product-profile-from-code";

    /// <summary>
    /// The profile template's Conditionals evaluate the Parse step's (step 1) output by
    /// <see cref="ConditionalStepConfig.SourceStepOrder"/>, so the check after the product analyzers
    /// reads <c>$.view.fallbackDesign</c> from Parse rather than from the last analyzer.
    /// </summary>
    private const string ReadParseView = """{"version":1,"mode":"Expression","sourceStepOrder":1}""";

    /// <summary>Promo video built from an approved profile instead of reading source code.</summary>
    public const string QuickWinPromoDsTemplateKey = "quick-win-promo-ds";

    /// <summary>The -ds promo video with AI-generated background plates.</summary>
    public const string QuickWinPromoPlatesDsTemplateKey = "quick-win-promo-plates-ds";

    /// <summary>
    /// The prompted-region-effects walkthrough: name an object in plain words, and blur exactly that
    /// object through the whole clip. Two deterministic steps and no editorial decision, so it is the
    /// cheapest way to see the feature work end to end.
    /// </summary>
    public const string RegionEffectTemplateKey = "video-region-effect";

    /// <summary>Puts a screen recording (or any video or image) onto a green plate in a clip; no agent, no model credit.</summary>
    public const string GreenScreenInsertTemplateKey = "green-screen-insert";

    /// <summary>Burns the words spoken in a finished video back in as subtitles and writes SRT and VTT files; no agent, no editing (lane 3e).</summary>
    public const string SubtitlesTemplateKey = "video-subtitles";

    /// <summary>The same, with the subtitles translated into the brief's language: one translation step and no other model use.</summary>
    public const string SubtitlesTranslatedTemplateKey = "video-subtitles-translated";

    /// <summary>Sound effects added under a finished video, nothing cut (docs/video-editing.md "Sound effects only").</summary>
    public const string SoundEffectsTemplateKey = "video-sound-effects";

    /// <summary>
    /// The curated talking-head-plus-screenshot recipe the trigger run lands on
    /// (docs/design/picture-overlay.md §8.5): the base edit pre-wired with the OverlayPlanner, the
    /// open <c>overlay</c> slot and the card-sound choice.
    /// </summary>
    public const string TestimonialReelTemplateKey = "testimonial-reel";

    /// <summary>
    /// Avatars phase 2 (docs/design/avatars.md): one inline <c>VideoGenerate</c> step that shows a project avatar.
    /// The avatar comes from the run brief (<c>avatar.avatarId</c>) or is set on the step; the step's permission to
    /// send media ships OFF and is given only in the app. Never auto-created.
    /// </summary>
    public const string SpokespersonClipTemplateKey = "spokesperson-clip";

    /// <summary>
    /// Avatars phase 3 (docs/design/avatars.md "Phased delivery"), the still-then-animate path: a short script, spoken in
    /// the voice linked to the run's avatar, then one talking-head clip of that avatar on fal.ai
    /// (<c>minimax/h3-max/lip-sync/image-to-video</c>: a still made from the avatar's pictures says the narration). The
    /// avatar comes from the run brief; the step's permission to send media ships OFF. Never auto-created.
    /// </summary>
    public const string TalkingAvatarBudgetTemplateKey = "talking-avatar-budget";

    /// <summary>
    /// The one avatar recipe of the app-input edition (docs/design/app-inputs.md 9.5): a "Lite mode (cheaper)" switch picks
    /// between the spokesperson path and the still-then-animate path. Replaces the two keys above, which stay,
    /// hidden, for the workflows already made from them.
    /// </summary>
    public const string AvatarClipTemplateKey = "avatar-clip";

    /// <summary>
    /// True for the recipes that show an avatar. Every one of them is written for a PERSON (a spokesperson, a talking
    /// head, an explainer in their own voice), so they are offered and created only while avatars of people are on
    /// (<see cref="Apps.AppStepFacts.PersonAvatarsEnabled"/>); the objects switch alone does not list them. A recipe that
    /// works with an object would be listed while either switch is on, and none exists yet.
    /// </summary>
    public static bool IsAvatarTemplate(string? key) =>
        key is not null && (string.Equals(key, SpokespersonClipTemplateKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, TalkingAvatarBudgetTemplateKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, AvatarClipTemplateKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, WorkflowTemplatePresets.AvatarExplainerKey, StringComparison.OrdinalIgnoreCase));

    private static readonly IReadOnlyList<WorkflowTemplateDefinition> _authoredTemplates =
    [
        new(
            Key: StarterTemplateKey,
            Name: "Promo video from your code",
            Description: "Turns your app's source code into a polished promo video of its real screens. Upload your code, then say in your brief who it is for, what to show, how long it should be and the format (\"15 s vertical reel\", \"60 s landscape demo\"). Silent unless you add a Voiceover step.",
            Version: 1,
            AutoCreateOnProject: true,
            RequiresUserInput: true,
            Steps:
            [
                new(AgentType.CodeStructureAnalyzer, "Analyze code structure", AgentInputContextMode: AgentInputContextMode.FullWorkflow),
                new(AgentType.DependencyAnalyzer, "Analyze UI dependencies", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.ComponentInventoryAnalyzer, "Inventory components", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.RouteAndApiAnalyzer, "Map routes and API", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.StyleAndThemeExtractor, "Extract style tokens", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.RemotionComponentTranslator, "Translate to Remotion scenes", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.AnimationStrategyAgent, "Create animation strategy", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly, CacheMode: StepCacheMode.Never),
                new(AgentType.ScriptwriterAgent, "Draft narration and captions", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly, CacheMode: StepCacheMode.Never),
                new(AgentType.DirectorAgent, "Build shot direction", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.AuthorAgent, "Assemble and render master composition", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.ReviewAgent, "Review quality and compliance", StepType.ReviewLoop, LoopTargetStepOrder: 10, MaxIterations: 2, MinScore: 8, AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "quick-win-promo-plates",
            Name: "Promo video from your code with AI backgrounds",
            Description: "The promo video from your code, with one or two short abstract AI-generated backgrounds (soft drifting colour, no text, no people) that the animated scenes can play over. Generation costs real money and is capped per run. Say in your brief what the video is about and the mood you want. Needs a video-generation provider.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(AgentType.CodeStructureAnalyzer, "Analyze code structure", AgentInputContextMode: AgentInputContextMode.FullWorkflow),
                new(AgentType.DependencyAnalyzer, "Analyze UI dependencies", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.ComponentInventoryAnalyzer, "Inventory components", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.RouteAndApiAnalyzer, "Map routes and API", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.StyleAndThemeExtractor, "Extract style tokens", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.RemotionComponentTranslator, "Translate to Remotion scenes", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.AnimationStrategyAgent, "Create animation strategy", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly, CacheMode: StepCacheMode.Never),
                new(AgentType.ScriptwriterAgent, "Draft narration and captions", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly, CacheMode: StepCacheMode.Never),
                new(AgentType.DirectorAgent, "Build shot direction", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Generate background plates",
                    StepType.VideoGenerate,
                    // Inline, operator-written prompt: no agent authors it and nothing project-derived
                    // leaves the host. One take, at most two clips, a small hard cap.
                    VideoGenerateConfigJson: """
                        {"planSource":"Inline","inlinePrompt":"Abstract, slowly drifting soft-focus gradient background plate with gentle light leaks and subtle particle motion. No text, no logos, no people, no recognizable objects. Calm, premium, loopable.","model":"MiniMax-H3","resolution":"768P","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":2,"maxSpendCredits":500,"pollIntervalSeconds":10,"timeoutSeconds":1800}
                        """),
                new(AgentType.AuthorAgent, "Assemble and render master composition", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.ReviewAgent, "Review quality and compliance", StepType.ReviewLoop, LoopTargetStepOrder: 11, MaxIterations: 2, MinScore: 8, AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "lean-context-promo",
            Name: "Promo video from your code (lighter)",
            Description: "The same promo video from your app's source code, reading less of your code at each step, so large projects run faster and cost less. Upload your code and describe the video you want in your brief. Silent unless you add a Voiceover step.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(AgentType.CodeStructureAnalyzer, "Analyze code structure", AgentInputContextMode: AgentInputContextMode.FullWorkflow),
                new(AgentType.DependencyAnalyzer, "Analyze UI dependencies", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.ComponentInventoryAnalyzer, "Inventory components", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.RouteAndApiAnalyzer, "Map routes and API", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.StyleAndThemeExtractor, "Extract style tokens", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(
                    AgentType.ExtractTransform,
                    "Reduce component inventory to a lean view",
                    StepType.Extract,
                    ExtractConfigJson: """
                        {"version":1,"operation":"Project","inputs":{"source":{"from":"Step","stepOrder":3}},"path":"$.components","fields":["name","filePath","responsibility"],"take":40,"maxOutputChars":8000,"expect":{"minItems":1}}
                        """),
                new(AgentType.RemotionComponentTranslator, "Translate to Remotion scenes (lean context)", AgentInputContextMode: AgentInputContextMode.SelectedPriorSteps, SelectedPriorStepOrders: [1, 2, 4, 5, 6], CacheMode: StepCacheMode.Never),
                new(AgentType.AnimationStrategyAgent, "Create animation strategy (lean context)", AgentInputContextMode: AgentInputContextMode.SelectedPriorSteps, SelectedPriorStepOrders: [5, 6, 7], CacheMode: StepCacheMode.Never),
                new(AgentType.ScriptwriterAgent, "Draft narration and captions", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly, CacheMode: StepCacheMode.Never),
                new(AgentType.DirectorAgent, "Build shot direction (lean context)", AgentInputContextMode: AgentInputContextMode.SelectedPriorSteps, SelectedPriorStepOrders: [1, 4, 5, 6, 7, 8, 9], CacheMode: StepCacheMode.Never),
                new(AgentType.AuthorAgent, "Assemble and render master composition (lean context)", AgentInputContextMode: AgentInputContextMode.SelectedPriorSteps, SelectedPriorStepOrders: [6, 7, 8, 9, 10], CacheMode: StepCacheMode.Never),
                new(AgentType.ReviewAgent, "Review quality and compliance", StepType.ReviewLoop, LoopTargetStepOrder: 11, MaxIterations: 2, MinScore: 8, AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "cinematic-feature-spotlight",
            Name: "Quick vertical reel",
            Description: "A short, punchy, narrated feature spotlight for social media, built from your app's source code. Upload your code and ask for what you want, e.g. \"15 s vertical reel about the timeline\" — any reel, short, story or TikTok comes out vertical 9:16. Needs a voice provider for the narration.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(AgentType.CodeStructureAnalyzer, "Analyze cinematic candidate surfaces", AgentInputContextMode: AgentInputContextMode.FullWorkflow),
                new(AgentType.ComponentInventoryAnalyzer, "Find hero components", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.StyleAndThemeExtractor, "Extract brand visuals", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.RemotionComponentTranslator, "Translate hero scenes", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.AnimationStrategyAgent, "Plan cinematic transitions", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.ScriptwriterAgent, "Write emotional feature hooks", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                // A social reel with no sound loses its viewers (customer finding 2026-10-04: the
                // reel shipped mute). The promo path's only audio source is the narration the
                // Author stages with StageVoiceoverAudio, so the reel speaks the Scriptwriter's
                // per-scene voiceover (step 6). Placed BEFORE the Director so its measured line
                // lengths drive the shot timing (audio-first, docs/voiceover.md). Left at the
                // Default cache mode: deterministic and content-addressed, like every Voiceover step.
                new(
                    AgentType.VideoTransform,
                    "Record the narration",
                    StepType.Voiceover,
                    VoiceoverConfigJson: """
                        {"source":{"kind":"ScriptScenes","scriptwriterStepOrder":6}}
                        """),
                new(AgentType.DirectorAgent, "Define reveal sequence", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.AuthorAgent, "Assemble cinematic master cut", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.ReviewAgent, "Cinematic quality review", StepType.ReviewLoop, LoopTargetStepOrder: 9, MaxIterations: 3, MinScore: 9, AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "story-arc-launch-trailer",
            Name: "Narrated launch trailer",
            Description: "A narrated launch trailer built from your app's source code: a hook, the build-up, the reveal and a call to action. Upload your code and describe the launch in your brief (audience, message, length, format). Needs a voice provider for the narration.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(AgentType.CodeStructureAnalyzer, "Map narrative assets", AgentInputContextMode: AgentInputContextMode.FullWorkflow),
                new(AgentType.RouteAndApiAnalyzer, "Extract user journey flow", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.StyleAndThemeExtractor, "Extract mood and tone tokens", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.RemotionComponentTranslator, "Translate story scenes", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.ScriptwriterAgent, "Write narrative arc script", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                // Same narration wiring as the vertical reel above: the Scriptwriter (step 5) is
                // spoken before the Director plans the shots, so the trailer is never mute.
                new(
                    AgentType.VideoTransform,
                    "Record the narration",
                    StepType.Voiceover,
                    VoiceoverConfigJson: """
                        {"source":{"kind":"ScriptScenes","scriptwriterStepOrder":5}}
                        """),
                new(AgentType.DirectorAgent, "Create trailer shotlist", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.AnimationStrategyAgent, "Plan suspense pacing", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.AuthorAgent, "Assemble and render trailer", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.ReviewAgent, "Trailer impact review", StepType.ReviewLoop, LoopTargetStepOrder: 9, MaxIterations: 3, MinScore: 9, AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit",
            Name: "Highlight edit from footage",
            Description: "Upload your video clips and get a tight edit with the dead air, false starts and filler taken out. Choose your clips in the first step; say what to keep or how long it should be in your brief.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // Source.Kind=ProjectFile with no ProjectFileId, deliberately: this is the
                    // first step in the workflow, so Source.Kind=PreviousStepOutput would fail
                    // SOURCE_UNRESOLVED on every single execution (there is no prior step's
                    // output to resolve — found by Copilot review). ProjectFile fails the same
                    // way when unconfigured, but the workflow builder's source picker for
                    // ProjectFile visibly shows "no file selected", making it obvious the user
                    // needs to pick one before running — unlike PreviousStepOutput, which reads
                    // as already-configured.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"emitOverlayOffers":true,"outline":true,"analyzeEvents":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                // Pictures and videos over the cut (docs/design/picture-overlay.md §8.5): reads the analyze
                // step's offered media/windows and the story editor's decision. Editorial judgment, so Never.
                new(
                    AgentType.OverlayPlanner,
                    "Plan picture overlays",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video",
                    StepType.VideoCompile,
                    // EncodeStage=Plan: the review reads the plan (sentence check, interleave, segments, length), never the
                    // pixels or the sound, so every pass only plans and the one encode is the "Render the final video" step
                    // after the review. See docs/video-editing.md "Encoding once after the review". The decision and the
                    // overlay plan are pinned by step order (the story editor at 2, the planner at 3) so the review's
                    // rerun passes re-resolve the same steps.
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"mediaOverlayPlan":{"from":"Step","stepOrder":3},"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"encodeStage":"Plan","cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":4}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    // Loop back to step 2 (the story editor) — step 1 (VideoAnalyze) is
                    // deterministic and produces the same bounded view every time, so there is
                    // nothing for a retry to gain from re-running it. Mirrors the main pipeline's
                    // ReviewLoop wiring (quick-win-promo, StarterTemplateKey above) exactly:
                    // LoopTargetStepOrder/MaxIterations/MinScore, FullWorkflow context so the
                    // review agent sees the analysis view, the story editor's decision, the
                    // planner's overlay plan and the compile step's own deterministic sentenceCheck.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                // The one encode: the same compile as step 4, reading the story editor's latest decision by step order
                // (step 2) and the planner's overlay plan (step 3), run once after the review has passed or run out of rounds.
                new(
                    AgentType.VideoTransform,
                    "Render the final video",
                    StepType.VideoCompile,
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"mediaOverlayPlan":{"from":"Step","stepOrder":3},"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"encodeStage":"Final","cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":4}}
                        """)
            ]),
        new(
            Key: TestimonialReelTemplateKey,
            Name: "Testimonial reel from a talking-head clip and a screenshot",
            Description: "Upload a talking-head clip and a screenshot — a review, a message, a result. We cut the clip and show the screenshot as a full-screen card or a corner inset at the moment the agent chooses.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // ProjectFile source for the same reason as video-derush-edit's first step: this is
                    // the first step, so PreviousStepOutput would fail SOURCE_UNRESOLVED on every run.
                    // emitOverlayOffers is why the `overlay` slot below is accepted by the manifest.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"emitOverlayOffers":true,"analyzeEvents":true,"outline":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                // The pre-wired planner (docs/design/picture-overlay.md §8.5): it reads the analyze
                // step's offered media/windows and the story editor's decision, so FullWorkflow.
                new(
                    AgentType.OverlayPlanner,
                    "Plan picture overlays",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video",
                    StepType.VideoCompile,
                    // Same encode-once split as video-derush-edit: Plan for the review, Final once after
                    // it. The decision and the overlay plan are pinned by step order (the story editor at
                    // 2, the planner at 3) so the review's rerun passes resolve the same steps.
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"mediaOverlayPlan":{"from":"Step","stepOrder":3},"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"encodeStage":"Plan","cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":4}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    // Back to the story editor (step 2), mirroring the base edit: step 1 is deterministic
                    // and gains nothing from a rerun.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Render the final video",
                    StepType.VideoCompile,
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"mediaOverlayPlan":{"from":"Step","stepOrder":3},"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"encodeStage":"Final","cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":4}}
                        """)
            ]),
        new(
            Key: "video-derush-edit-graphics",
            Name: "Footage edit with titles and callouts",
            Description: "A highlight edit of your video clips, plus on-screen titles, name tags and callouts placed where the picture has room. Upload your clips; put the names and key points to show in your brief.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // Same ProjectFile-source rationale as video-derush-edit's first step above
                    // (this is the first step, so PreviousStepOutput would fail SOURCE_UNRESOLVED
                    // on every run) — plus emitOverlayPlacements:true to derive Phase 3's
                    // view.placements candidates alongside the usual cut-anchor ids.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"emitOverlayPlacements":true,"outline":true,"analyzeEvents":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.MotionGraphicsPlanner,
                    "Plan motion graphics overlays",
                    // FullWorkflow (not PreviousStepOnly): this agent needs BOTH the analyze
                    // step's view.placements (step 1) and the story editor's decision (step 2),
                    // not merely the immediately-preceding step's output.
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video with graphics",
                    StepType.VideoCompile,
                    // Decision/GraphicsPlan reference their source steps explicitly by
                    // StepOrder — "Previous" relative to THIS step would resolve to the
                    // MotionGraphicsPlanner step's output (step 3), not the story editor's
                    // decision (step 2).
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"enableGraphics":true,"graphicsPlan":{"from":"Step","stepOrder":3},"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":4}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    // Loop back to step 2 (the story editor) so a low score re-runs the story
                    // editor, the motion-graphics planner, and the compile step in sequence —
                    // step 1 (VideoAnalyze) is deterministic and need not rerun. Same
                    // MinScore/MaxIterations/FullWorkflow pattern as video-derush-edit above.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit-music",
            Name: "Footage edit with background music",
            Description: "A highlight edit of your video clips with a background music track mixed under the voices. Upload a music track to your project files first — there is no built-in music library yet — and upload your clips.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // Same ProjectFile-source rationale as video-derush-edit's first step above
                    // (this is the first step, so PreviousStepOutput would fail SOURCE_UNRESOLVED
                    // on every run) — plus offerMusicTracks:true to derive the "m{n}" music-track
                    // candidates the music supervisor picks from.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"offerMusicTracks":true,"outline":true,"analyzeEvents":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.MusicSupervisor,
                    "Pick background music",
                    // FullWorkflow (not PreviousStepOnly): this agent needs BOTH the analyze
                    // step's view.musicTracks (step 1) and the story editor's decision (step 2),
                    // not merely the immediately-preceding step's output.
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video with music",
                    StepType.VideoCompile,
                    // Decision/MusicPlan reference their source steps explicitly by StepOrder —
                    // "Previous" relative to THIS step would resolve to the MusicSupervisor step's
                    // output (step 3), not the story editor's decision (step 2).
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"enableMusic":true,"musicPlan":{"from":"Step","stepOrder":3},"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":4}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    // Loop back to step 2 so a low score re-runs the story editor, the music
                    // supervisor, and the compile step in sequence — step 1 (VideoAnalyze) is
                    // deterministic and need not rerun. Same MinScore/MaxIterations/FullWorkflow
                    // pattern as video-derush-edit/video-derush-edit-graphics above.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit-room",
            Name: "Highlight edit, decided by an editing team",
            Description: "A highlight edit where several AI editors debate the cut before a lead editor decides, then a reviewer checks it. Slower and uses more tokens than the single-editor edit, in exchange for a more considered cut. Upload your video clips.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // Same ProjectFile-source rationale as video-derush-edit's first step above
                    // (this is the first step, so PreviousStepOutput would fail SOURCE_UNRESOLVED
                    // on every run).
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"outline":true,"analyzeEvents":true}
                        """),
                new(
                    // The step's own AgentDefinitionId FK is satisfied by the same deterministic
                    // VideoTransform placeholder VideoAnalyze/VideoCompile steps use — the room's
                    // actual LLM seats/director are resolved independently by EditRoomStepExecutor
                    // from EditRoomConfigJson, never from this step's own AgentDefinitionId.
                    AgentType.VideoTransform,
                    "Edit room deliberation",
                    StepType.EditRoom,
                    EditRoomConfigJson: """
                        {"version":1,"view":{"from":"Previous"}}
                        """,
                    // The built-in StepCachePolicy treats EditRoom as cacheable by default (it is
                    // deterministic-looking from a step-type standpoint), but its whole purpose is
                    // a fresh multi-agent editorial deliberation each run — pin it to Never so a
                    // rerun never silently replays a stale room decision.
                    CacheMode: StepCacheMode.Never),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video",
                    StepType.VideoCompile,
                    // Decision/AnalysisStepOrder reference their source steps explicitly by
                    // StepOrder — "Previous" relative to the compile step already resolves
                    // correctly here (the EditRoom step immediately precedes it and emits the
                    // exact same VideoEditDecisionOutput shape a solo VideoStoryEditor step
                    // would), kept explicit anyway for the same auditability the other video
                    // templates' compile steps already follow.
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":3}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    // Loop back to step 2 (the edit room) — step 1 (VideoAnalyze) is deterministic
                    // and produces the same bounded view every time, so there is nothing for a
                    // retry to gain from re-running it. Same MinScore/MaxIterations/FullWorkflow
                    // pattern as video-derush-edit above.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit-graphics-room",
            Name: "Footage edit with titles, designed by a graphics team",
            Description: "A highlight edit of your video clips with titles and callouts that a team of AI graphic designers argues over before a lead designer decides. Slower and uses more tokens than the single-designer version. Upload your clips; put names and key points in your brief.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // Same ProjectFile-source rationale as video-derush-edit's first step above
                    // (this is the first step, so PreviousStepOutput would fail SOURCE_UNRESOLVED
                    // on every run) — plus emitOverlayPlacements:true to derive the view.placements
                    // candidates the graphics room deliberates over.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"emitOverlayPlacements":true,"outline":true,"analyzeEvents":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    // The step's own AgentDefinitionId FK is satisfied by the same deterministic
                    // VideoTransform placeholder VideoAnalyze/VideoCompile/EditRoom steps use — the
                    // room's actual LLM seats/director are resolved independently by
                    // GraphicsRoomStepExecutor from GraphicsRoomConfigJson, never from this step's
                    // own AgentDefinitionId.
                    AgentType.VideoTransform,
                    "Graphics room deliberation",
                    StepType.GraphicsRoom,
                    // View references the analyze step explicitly by StepOrder — "Previous"
                    // relative to THIS step would resolve to the story editor's decision (step 2),
                    // not the analyze envelope carrying view.placements (step 1). The story
                    // editor's decision still reaches the room: the executor annotates each
                    // placement with the same inEdit survival flag a solo planner's prompt gets.
                    GraphicsRoomConfigJson: """
                        {"version":1,"view":{"from":"Step","stepOrder":1}}
                        """,
                    // GraphicsRoom is already never-cacheable under the built-in StepCachePolicy
                    // (its director may render an asset) — set explicitly anyway so this template
                    // does not silently depend on that built-in default staying what it is today.
                    CacheMode: StepCacheMode.Never),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video with graphics",
                    StepType.VideoCompile,
                    // Decision/GraphicsPlan reference their source steps explicitly by StepOrder —
                    // "Previous" relative to THIS step would resolve to the graphics room's plan
                    // (step 3), not the story editor's decision (step 2). GraphicsPlan pointing at
                    // the GraphicsRoom step works UNCHANGED because that step emits the exact same
                    // MotionGraphicsPlanOutput shape a solo MotionGraphicsPlanner step would (the
                    // additive "room" metadata key is skipped by deserialization).
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"enableGraphics":true,"graphicsPlan":{"from":"Step","stepOrder":3},"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":4}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    // Loop back to step 2 (the story editor) so a low score re-runs the story
                    // editor, the graphics room, and the compile step in sequence — step 1
                    // (VideoAnalyze) is deterministic and need not rerun. Same
                    // MinScore/MaxIterations/FullWorkflow pattern as the other video templates.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit-grade-room",
            Name: "Footage edit with a colour grade",
            Description: "A highlight edit of your video clips with one consistent colour look (warm, cool, filmic, muted…) chosen by a team of AI colourists from what your footage actually looks like. Upload your video clips.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // Same ProjectFile-source rationale as video-derush-edit's first step above
                    // (this is the first step, so PreviousStepOutput would fail SOURCE_UNRESOLVED
                    // on every run). The default AnalyzeVisuals:true already emits the measured
                    // per-shot colour temperature/tone/saturation words the grade room argues
                    // from — no extra analyze flag is needed.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"outline":true,"analyzeEvents":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    // The step's own AgentDefinitionId FK is satisfied by the same deterministic
                    // VideoTransform placeholder VideoAnalyze/VideoCompile/EditRoom/GraphicsRoom
                    // steps use — the room's actual LLM seats/director are resolved independently
                    // by ColorGradeRoomStepExecutor from ColorGradeRoomConfigJson, never from
                    // this step's own AgentDefinitionId.
                    AgentType.VideoTransform,
                    "Color grade room deliberation",
                    StepType.ColorGradeRoom,
                    // View references the analyze step explicitly by StepOrder — "Previous"
                    // relative to THIS step would resolve to the story editor's decision (step
                    // 2), not the analyze envelope carrying the shots' measured colour
                    // descriptors (step 1). Same explicit-reference rationale as the graphics
                    // room's own View.
                    ColorGradeRoomConfigJson: """
                        {"version":1,"view":{"from":"Step","stepOrder":1}}
                        """,
                    // The built-in StepCachePolicy treats ColorGradeRoom as cacheable by default,
                    // but like EditRoom its whole purpose is a fresh editorial deliberation each
                    // run — pin it to Never so a rerun never silently replays a stale grade.
                    CacheMode: StepCacheMode.Never),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video with grade",
                    StepType.VideoCompile,
                    // Decision/ColorGradePlan reference their source steps explicitly by
                    // StepOrder — "Previous" relative to THIS step would resolve to the grade
                    // room's plan (step 3), not the story editor's decision (step 2).
                    // ColorGradePlan pointing at the ColorGradeRoom step works because that step
                    // emits the exact same ColorGradePlanOutput shape a solo Colorist step would
                    // (the additive "room" metadata key is skipped by deserialization).
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"enableColorGrade":true,"colorGradePlan":{"from":"Step","stepOrder":3},"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":4}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    // Loop back to step 2 (the story editor) so a low score re-runs the story
                    // editor, the grade room, and the compile step in sequence — step 1
                    // (VideoAnalyze) is deterministic and need not rerun. Same
                    // MinScore/MaxIterations/FullWorkflow pattern as the other video templates.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit-sfx",
            Name: "Footage edit with sound effects",
            Description: "A highlight edit of your video clips with short sound effects (whooshes, clicks, stings) on the key cuts. Add a starter set of effects from the built-in sound effects library, or upload your own, and upload your clips.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // Same ProjectFile-source rationale as video-derush-edit's first step above
                    // (this is the first step, so PreviousStepOutput would fail SOURCE_UNRESOLVED
                    // on every run) — plus offerSfxClips:true to derive the "x{n}" SFX-clip
                    // candidates the sound designer picks from.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"offerSfxClips":true,"outline":true,"analyzeEvents":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.SoundDesigner,
                    "Plan sound-effect cues",
                    // FullWorkflow (not PreviousStepOnly): this agent needs BOTH the analyze
                    // step's view.sfxClips + offered anchor ids (step 1) and the story editor's
                    // decision (step 2) — a cue anchored to a cut-away moment is silently
                    // dropped, so knowing what was kept matters.
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video with sound effects",
                    StepType.VideoCompile,
                    // Decision/SfxPlan reference their source steps explicitly by StepOrder —
                    // "Previous" relative to THIS step would resolve to the SoundDesigner step's
                    // output (step 3), not the story editor's decision (step 2).
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"enableSfx":true,"sfxPlan":{"from":"Step","stepOrder":3},"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":4}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    // Loop back to step 2 so a low score re-runs the story editor, the sound
                    // designer, and the compile step in sequence — step 1 (VideoAnalyze) is
                    // deterministic and need not rerun. Same MinScore/MaxIterations/FullWorkflow
                    // pattern as the other video templates.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit-broll",
            Name: "Footage edit with AI-generated b-roll",
            Description: "A highlight edit of your video clips plus extra shots generated by AI — a cold open, cutaways or an end card. Generation costs real money and is capped per run. Upload your clips and describe the extra shots in your brief. Needs a video-generation provider.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // Same ProjectFile-source rationale as video-derush-edit's first step above
                    // (this is the first step, so PreviousStepOutput would fail SOURCE_UNRESOLVED
                    // on every run). The bounded view this emits is what offers the shot director
                    // the "s{n}"/"g{n}"/"t{n}" cut-anchor ids its planned shots hang off.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"outline":true,"analyzeEvents":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.ShotDirector,
                    "Plan generated b-roll shots",
                    // FullWorkflow (not PreviousStepOnly): this agent needs BOTH the analyze
                    // step's bounded view with the offered "s{n}"/"g{n}"/"t{n}" cut-anchor ids
                    // (step 1) and the story editor's decision (step 2) — a shot anchored to a
                    // moment that was cut away is dropped server-side, so knowing what was kept
                    // matters.
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Generate planned b-roll clips",
                    StepType.VideoGenerate,
                    // Plan/Analysis reference their source steps explicitly by StepOrder, never
                    // "Previous". "Previous" resolves to the LAST completed step's output — which
                    // at this step is the ShotDirector's own output, not the VideoAnalyze
                    // artifact. Plan would happen to survive that, Analysis would not: the
                    // keyframe ids a planned shot names must resolve against step 1.
                    // CacheMode is deliberately left at Default: the step is deterministic and
                    // fully keyed on its config, so an identical re-run must not pay twice.
                    //
                    // allowSourceMediaEgress IS set, and it is the template's own consent rather
                    // than a default anyone tripped over: this is the b-roll template, whose whole
                    // point is generated shots that cut together with the footage, and the shot
                    // director is explicitly allowed to name a first/last frame for that. Left
                    // false (the per-step default), ANY plan naming a frame is refused outright
                    // with EGRESS_REFUSED, which would make the planner's frame vocabulary dead
                    // weight in the one template that exists to use it. What the grant buys is
                    // written down for the operator in docs/user-guide/recipes/generated-b-roll.md
                    // "Privacy": one frame per named reference, posted to the generation provider.
                    // The assistant never turns this on: a workflow it creates from this template starts with
                    // it off (AssistantEgressConsentGuard); only the app's template picker keeps the grant.
                    VideoGenerateConfigJson: """
                        {"planSource":"StepRef","plan":{"from":"Step","stepOrder":3},"analysis":{"from":"Step","stepOrder":1},"model":"MiniMax-H3","resolution":"768P","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":4,"maxSpendCredits":833.3333333333333333333333333,"pollIntervalSeconds":10,"timeoutSeconds":1800,"allowSourceMediaEgress":true}
                        """),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                // Optional dynamic edit: once the generated clips (and, in the dailies edit, the take selection) exist, plans the beat-aligned
                // moves, including a BeatAlternate of a source shot against a generated clip. Words and offered ids only; the compile reads it
                // through dynamicsPlan and ignores it unless the run asks for a pace.
                new(
                    AgentType.DynamicsPlanner,
                    "Plan beat-aligned dynamics",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video with generated clips",
                    StepType.VideoCompile,
                    // Decision/GeneratedClips reference their source steps explicitly by
                    // StepOrder — "Previous" relative to THIS step would resolve to the
                    // VideoGenerate step's output (step 4), not the story editor's decision
                    // (step 2).
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"enableGeneratedClips":true,"generatedClips":{"from":"Step","stepOrder":4},"maxGeneratedClips":4,"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":5},"dynamicsPlan":{"from":"Step","stepOrder":6}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    // Loop back to step 2 so a low score re-runs the story editor, the shot
                    // director, the generation step, and the compile step in sequence — step 1
                    // (VideoAnalyze) is deterministic and need not rerun. Same
                    // MinScore/MaxIterations/FullWorkflow pattern as the other video templates.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit-voiceover",
            Name: "Footage edit with narration and captions",
            Description: "Cuts your video clips first, then writes a narration line for the kept shots, speaks it and burns in captions. Upload your clips and give the topic, tone and length in your brief. Needs a voice provider. For a story written before the cut, use Narrated product story.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // Same ProjectFile-source rationale as video-derush-edit's first step above
                    // (this is the first step, so PreviousStepOutput would fail SOURCE_UNRESOLVED
                    // on every run). The bounded view this emits both feeds the story editor's
                    // decision and offers the narration writer the "s{n}"/"g{n}"/"t{n}" cut-anchor
                    // ids its lines hang off.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"outline":true,"analyzeEvents":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.NarrationWriter,
                    "Write narration lines",
                    // FullWorkflow (not PreviousStepOnly): this agent needs BOTH the analyze step's
                    // offered cut-anchor ids (step 1) and the story editor's decision (step 2) — a
                    // line anchored to a moment that was cut away is dropped server-side, so
                    // knowing what was kept matters.
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Synthesize the narration",
                    StepType.Voiceover,
                    // The third source kind: lines come from the NarrationWriter step's
                    // NarrationPlanOutput (step 3) rather than from the config. The plan carries no
                    // start time by construction, so the voiceover step DECLARES the line positions
                    // itself, back-to-back in plan order — narration-first placement, whose picture
                    // fit the compile step measures afterwards. CacheMode is left at Default on
                    // purpose: the step is deterministic and fully keyed on its config, and its
                    // per-line WAVs are already content-addressed, so an identical re-run pays only
                    // a HEAD + ffprobe and must not be forced out of the cache.
                    VoiceoverConfigJson: """
                        {"source":{"kind":"NarrationPlan","narrationWriterStepOrder":3}}
                        """),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video with narration",
                    StepType.VideoCompile,
                    // Decision/Voiceover reference their source steps explicitly by StepOrder —
                    // "Previous" relative to THIS step would resolve to the voiceover step's output
                    // (step 4), not the story editor's decision (step 2). Mode is left at its
                    // Reencode default, which EnableVoiceover (and EnableCaptions) require. Captions
                    // are ON: a narrated video without captions is not what anyone posts (customer
                    // finding 2026-10-04). Karaoke needs word timings (a Transcription provider);
                    // without them each line degrades with no_word_alignment and the node says so.
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"enableVoiceover":true,"voiceoverStepOrder":4,"enableCaptions":true,"captionStyle":"Karaoke","transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":5}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    // Loop back to step 2 so a low score re-runs the story editor, the narration
                    // writer, the voiceover step, and the compile step in sequence — step 1
                    // (VideoAnalyze) is deterministic and need not rerun. Same
                    // MinScore/MaxIterations/FullWorkflow pattern as the other video templates.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit-pickups",
            Name: "Fix flubbed lines in a talking-head video",
            Description: "Needs talking-head footage with clear speech. Finds the sentences the presenter stumbled on, re-records them in the presenter's own cloned voice and swaps them in; the lips are not re-synced. Set up a consented voice in the project's Voices tab first, and choose it in the re-record step.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    // Transcription is what makes a pickup possible: the planner needs the words of
                    // each t{n} segment to find the flub, and the compile step needs the segment's
                    // window to mute. Required rather than Optional, so a run without ASR fails
                    // loudly here instead of planning nothing downstream.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"transcription":"Required","outline":true,"analyzeEvents":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.PickupPlanner,
                    "Plan pickups for flubbed takes",
                    // FullWorkflow: needs the transcript segments (step 1) AND the kept spans (step 2).
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Re-record the pickups",
                    StepType.Voiceover,
                    // PickupPlan source: lines come from the planner's PickupPlanOutput (step 3). The
                    // template cannot know a project's consented voice id, so projectVoiceId is left
                    // for the author to set; without it the pickups speak in the provider's default
                    // voice, which is honest but not a pickup.
                    VoiceoverConfigJson: """
                        {"source":{"kind":"PickupPlan","pickupPlannerStepOrder":3}}
                        """),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video with pickups spliced in",
                    StepType.VideoCompile,
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"enablePickups":true,"pickupVoiceoverStepOrder":4,"transitionPolicy":"Auto","minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":5}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit-dub",
            Name: "Dubbed video in another language",
            Description: "Needs footage with speech. Writes a narration for your edit, translates it into the language you name in your brief, speaks the translation in place of the original voices and burns in matching subtitles; the lips are not re-synced. Needs a voice provider.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    VideoAnalyzeConfigJson: """
                        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"analyzeEvents":true,"outline":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.NarrationWriter,
                    "Write narration lines",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.NarrationTranslator,
                    "Translate the narration into the target language",
                    // PreviousStepOnly: the translator needs exactly the plan it translates; the
                    // target language arrives in the run's user request.
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Synthesize the dub",
                    StepType.Voiceover,
                    // The translated plan (step 4) is a NarrationPlanOutput like the original, so the
                    // same source kind reads it. `language` is provenance for the output; the speech
                    // providers detect the language from the text itself.
                    VoiceoverConfigJson: """
                        {"source":{"kind":"NarrationPlan","narrationWriterStepOrder":4}}
                        """),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile the dubbed video",
                    StepType.VideoCompile,
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"enableVoiceover":true,"voiceoverStepOrder":5,"voiceoverMode":"ReplaceDialogue","enableCaptions":true,"captionStyle":"Subtitle","transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":6}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: NarratedStoryTemplateKey,
            Name: "Narrated product story",
            Description: "Writes a short story from your brief first, then picks and orders your video clips to tell it, speaks the narration and burns in captions. Upload your clips (screen recordings, product shots, b-roll) and say in your brief what the video is about, who it is for and how long it should be. Choose vertical, square or landscape in the compile step. Needs a voice provider.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                // Story FIRST (customer finding 2026-10-04, "no story line to follow, random
                // clips"): in video-derush-edit-voiceover the editor picks spans and the narration
                // writer then captions each kept clip after the fact, so the script describes clips
                // instead of telling a story. Here the narration writer runs BEFORE the editor —
                // from the brief and the analysed footage — and the editor keeps and ORDERS clips to
                // serve that script. No new agent type: NarrationPlanOutput is already anchor-id
                // based, which is exactly a "script that names the picture under each line", and
                // both prompts carry a story-first section for this order.
                new(
                    AgentType.VideoTransform,
                    "Analyze your clips",
                    StepType.VideoAnalyze,
                    // Vision captions are Optional: the story writer chooses each line's shot from
                    // what the shot shows, and a deployment with no Vision provider still runs.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"source":{"kind":"ProjectFile"},"vision":"Optional","analyzeEvents":true,"outline":true}
                        """),
                new(
                    AgentType.NarrationWriter,
                    "Write the story",
                    // FullWorkflow: the only prior step is the analysis, plus the user request (the
                    // brief) every agent step receives. No edit decision exists yet, which is what
                    // puts the writer in its story-first mode.
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoStoryEditor,
                    "Choose and order clips for the story",
                    // FullWorkflow (not PreviousStepOnly): the editor needs the analysis view's ids
                    // (step 1) AND the script (step 2). Across clips the compile step plays Keep spans
                    // in list order, so the story's line order maps onto the decision's span order;
                    // within one clip spans always play in source order.
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Record the narration",
                    StepType.Voiceover,
                    VoiceoverConfigJson: """
                        {"source":{"kind":"NarrationPlan","narrationWriterStepOrder":2}}
                        """),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile the narrated video",
                    StepType.VideoCompile,
                    // Explicit step orders: "Previous" relative to this step would resolve the
                    // voiceover step (4), not the editor's decision (3). Captions ON (Karaoke); the
                    // output format is left at Source for the customer to choose in this step.
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":3},"analysisStepOrder":1,"enableVoiceover":true,"voiceoverStepOrder":4,"enableCaptions":true,"captionStyle":"Karaoke","transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":5}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review the finished video",
                    StepType.ReviewLoop,
                    // Back to the STORY (step 2), so a low score rewrites the script and re-cuts,
                    // re-records and re-compiles it — the window [2, 6) contains the compile step.
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: "video-derush-edit-dailies",
            Name: "Footage edit with AI b-roll, best take chosen by a panel",
            Description: "The AI b-roll edit with a second opinion: every extra shot is generated twice, a panel of AI judges watches both takes and keeps the better one, and only the chosen takes go into your video. Costs about twice as much per run as the b-roll edit and is capped. Upload your clips and describe the extra shots in your brief. Needs a video-generation provider.",
            // Version 2: video events on by default (event analysis, a MotionDirector plan, cut on events). A project on "... v1" keeps its saved steps.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Analyze source video",
                    StepType.VideoAnalyze,
                    VideoAnalyzeConfigJson: """
                        {"version":1,"source":{"kind":"ProjectFile"},"analyzeEvents":true,"outline":true}
                        """),
                new(
                    AgentType.VideoStoryEditor,
                    "Decide which spans to keep",
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.ShotDirector,
                    "Plan generated b-roll shots",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Generate two takes per planned shot",
                    StepType.VideoGenerate,
                    // takes:2 is the whole point: ids become v{n}t1/v{n}t2. MaxClips 8 still caps
                    // the step, so a four-shot plan x two takes fills it exactly; a bigger plan is trimmed in
                    // plan order (later shots skipped, never submitted). Look matching and camera
                    // continuity read the analyze step's measured descriptors through `analysis`.
                    VideoGenerateConfigJson: """
                        {"planSource":"StepRef","plan":{"from":"Step","stepOrder":3},"analysis":{"from":"Step","stepOrder":1},"model":"MiniMax-H3","resolution":"768P","durationSeconds":6,"ratio":"16:9","takes":2,"maxClips":8,"maxSpendCredits":2666.6666666666666666666666666,"pollIntervalSeconds":10,"timeoutSeconds":1800,"lookMatching":true,"cameraContinuity":true}
                        """),
                new(
                    AgentType.VideoTransform,
                    "Measure every generated take",
                    StepType.VideoAnalyze,
                    // One analyzed source PER delivered take, each tagged with its v{n}t{k} clip id
                    // in view.sources[].clipId — the ids the dailies room chooses between. Visuals
                    // on, transcription off: a generated b-roll clip has no narration to transcribe.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"source":{"kind":"GeneratedClips","stepOrder":4},"sources":[{"kind":"GeneratedClips","stepOrder":4}],"transcription":"Off","detectSilence":false}
                        """),
                new(
                    AgentType.VideoTransform,
                    "Dailies room: pick one take per shot",
                    StepType.DailiesRoom,
                    // View: Step 5 — explicit, since Previous resolves to step 5 anyway today but an
                    // inserted step would silently re-point it.
                    DailiesRoomConfigJson: """
                        {"version":1,"view":{"from":"Step","stepOrder":5}}
                        """,
                    CacheMode: StepCacheMode.Never),
                // Video events on by default: moves, speed ramps and flashes on the events the analysis found (ids and words only).
                new(
                    AgentType.MotionDirector,
                    "Plan motion on events",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                // Optional dynamic edit: once the generated clips (and, in the dailies edit, the take selection) exist, plans the beat-aligned
                // moves, including a BeatAlternate of a source shot against a generated clip. Words and offered ids only; the compile reads it
                // through dynamicsPlan and ignores it unless the run asks for a pace.
                new(
                    AgentType.DynamicsPlanner,
                    "Plan beat-aligned dynamics",
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Compile edited video with the chosen takes",
                    StepType.VideoCompile,
                    // Every reference is an explicit StepOrder: Previous relative to this step is
                    // the dailies room's selection, not the story editor's decision.
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":2},"analysisStepOrder":1,"enableGeneratedClips":true,"generatedClips":{"from":"Step","stepOrder":4},"takeSelection":{"from":"Step","stepOrder":6},"maxGeneratedClips":4,"transitionPolicy":"Auto","programFadeInMs":0,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800,"cutOnEvents":true,"enableMotion":true,"motionPlan":{"from":"Step","stepOrder":7},"dynamicsPlan":{"from":"Step","stepOrder":8}}
                        """),
                new(
                    AgentType.VideoReviewAgent,
                    "Review edit quality",
                    StepType.ReviewLoop,
                    LoopTargetStepOrder: 2,
                    MaxIterations: 3,
                    MinScore: 8,
                    AgentInputContextMode: AgentInputContextMode.FullWorkflow,
                    CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: RegionEffectTemplateKey,
            Name: "Blur one object through the whole clip",
            Description: "Keeps all of your footage exactly as it was filmed and blurs a single object you name in plain words — a number plate, a face, a speedometer — following it as it moves, shrinks and goes behind things. Pick your clip in the first step and type what to blur. Sending that clip to the tracking service is a separate permission and it is OFF: switch it on in the first step, or the run stops before anything leaves this server.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Find the object you named",
                    StepType.VideoAnalyze,
                    // Same ProjectFile-source rationale as video-derush-edit's first step above: this is
                    // the first step, so PreviousStepOutput would fail SOURCE_UNRESOLVED on every run,
                    // and ProjectFile visibly reads as "no file selected" until the author picks one.
                    //
                    // method=Segmentation is the explicit opt-in: "the speedometer" IS the hosted
                    // model's prompt, and the feature tracker is not used on this path.
                    // allowSourceMediaEgress stays FALSE on purpose — a template must never pre-consent
                    // to a customer's footage leaving this host. The step refuses with EGRESS_REFUSED,
                    // naming the exact clip, until the author turns it on; the description says so.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","trackObjects":[{"label":"the speedometer","method":"Segmentation"}],"allowSourceMediaEgress":false,"maxSegmentationSeconds":60}
                        """),
                new(
                    AgentType.VideoTransform,
                    "Blur it through the whole clip",
                    StepType.VideoCompile,
                    // This workflow ADDS an effect and never cuts, so KeepWholeSources keeps every
                    // analyzed source whole in source order and no editor decision is ever resolved
                    // (Decision stays required by the record's shape). Mode=Reencode because masking an
                    // effect onto the picture has no stream-copy equivalent (EFFECT_REQUIRES_REENCODE).
                    // The target is the prompt label the analyze step tracked under — a name the
                    // customer typed, never an opaque id.
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"keepWholeSources":true,"mode":"Reencode","regionEffects":[{"target":"the speedometer","effect":"Blur"}]}
                        """)
            ]),
        new(
            Key: ProductProfileFromCodeTemplateKey,
            Name: "Build brand and product profile",
            Description: "Reads your design files and app source code to build the brand and product profile your promo videos reuse. Files are read for free first; AI is used only for what is missing, shown with an estimate before it runs.",
            // Version 2: the Conditional at step 8 re-checks the design flag after the
            // product analyzers, so a missing product map no longer also runs (and charges) the style
            // extractor. Every Conditional reads the Parse step's view by sourceStepOrder 1, never the
            // previous step's output. A project still on "... v1" keeps its saved steps; the build route
            // provisions v2 by name.
            Version: 2,
            AutoCreateOnProject: false,
            RequiresUserInput: false,
            Steps:
            [
                new(AgentType.ExtractTransform, "Read design files and code", StepType.DesignSystem, DesignSystemConfigJson: """{"op":"Parse","version":1,"parse":{}}"""),
                new(
                    AgentType.ExtractTransform,
                    "Product profile missing?",
                    StepType.Conditional,
                    ConditionExpression: "$.view.fallbackProduct",
                    TrueBranchStepOrder: "4",
                    FalseBranchStepOrder: "3",
                    ConditionalConfigJson: ReadParseView),
                new(
                    AgentType.ExtractTransform,
                    "Design missing?",
                    StepType.Conditional,
                    ConditionExpression: "$.view.fallbackDesign",
                    TrueBranchStepOrder: "9",
                    FalseBranchStepOrder: "10",
                    ConditionalConfigJson: ReadParseView),
                new(AgentType.CodeStructureAnalyzer, "Analyze code structure", AgentInputContextMode: AgentInputContextMode.FullWorkflow),
                new(AgentType.DependencyAnalyzer, "Analyze UI dependencies", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.ComponentInventoryAnalyzer, "Inventory components", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.RouteAndApiAnalyzer, "Map routes and API", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(
                    AgentType.ExtractTransform,
                    "Design also missing?",
                    StepType.Conditional,
                    ConditionExpression: "$.view.fallbackDesign",
                    TrueBranchStepOrder: "9",
                    FalseBranchStepOrder: "10",
                    ConditionalConfigJson: ReadParseView),
                new(AgentType.StyleAndThemeExtractor, "Extract style tokens", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(AgentType.ExtractTransform, "Build brand and product profile", StepType.DesignSystem, DesignSystemConfigJson: """{"op":"Compose","version":1,"compose":{"parseStepOrder":1,"designFallbackStepOrders":[9],"productFallbackStepOrders":[4,5,6,7],"ingest":true}}""")
            ]),
        new(
            Key: QuickWinPromoDsTemplateKey,
            Name: "Promo video from your profile",
            Description: "Builds a promo video of your app from your approved brand and product profile, so it does not re-analyse your code on every run. Savings have not been measured yet. Build a profile first.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(AgentType.ExtractTransform, "Require approved profile", StepType.DesignSystem, DesignSystemConfigJson: """{"op":"Require","version":1,"require":{"requireDesign":true,"requireProductMap":true}}"""),
                new(AgentType.RemotionComponentTranslator, "Translate to Remotion scenes", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.AnimationStrategyAgent, "Create animation strategy", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly, CacheMode: StepCacheMode.Never),
                new(AgentType.ScriptwriterAgent, "Draft narration and captions", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly, CacheMode: StepCacheMode.Never),
                new(AgentType.DirectorAgent, "Build shot direction", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.AuthorAgent, "Assemble and render master composition", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.ReviewAgent, "Review quality and compliance", StepType.ReviewLoop, LoopTargetStepOrder: 6, MaxIterations: 2, MinScore: 8, AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: QuickWinPromoPlatesDsTemplateKey,
            Name: "Promo video from your profile with AI backgrounds",
            Description: "The promo video from your profile, with one or two short abstract AI-generated backgrounds the animated scenes can play over. Needs an approved profile and a video-generation provider.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(AgentType.ExtractTransform, "Require approved profile", StepType.DesignSystem, DesignSystemConfigJson: """{"op":"Require","version":1,"require":{"requireDesign":true,"requireProductMap":true}}"""),
                new(AgentType.RemotionComponentTranslator, "Translate to Remotion scenes", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.AnimationStrategyAgent, "Create animation strategy", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly, CacheMode: StepCacheMode.Never),
                new(AgentType.ScriptwriterAgent, "Draft narration and captions", AgentInputContextMode: AgentInputContextMode.PreviousStepOnly, CacheMode: StepCacheMode.Never),
                new(AgentType.DirectorAgent, "Build shot direction", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Generate background plates",
                    StepType.VideoGenerate,
                    // Byte-identical to quick-win-promo-plates' inline plate prompt and caps.
                    VideoGenerateConfigJson: """
                        {"planSource":"Inline","inlinePrompt":"Abstract, slowly drifting soft-focus gradient background plate with gentle light leaks and subtle particle motion. No text, no logos, no people, no recognizable objects. Calm, premium, loopable.","model":"MiniMax-H3","resolution":"768P","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":2,"maxSpendCredits":500,"pollIntervalSeconds":10,"timeoutSeconds":1800}
                        """),
                new(AgentType.AuthorAgent, "Assemble and render master composition", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(AgentType.ReviewAgent, "Review quality and compliance", StepType.ReviewLoop, LoopTargetStepOrder: 7, MaxIterations: 2, MinScore: 8, AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never)
            ]),
        new(
            Key: GreenScreenInsertTemplateKey,
            Name: "Green-screen insert",
            Description: "Puts a screen recording, video or picture onto a green screen in your clip, following the screen as it moves, even when something passes in front of it. No AI model is used; only processing time is charged. Choose the clip and the picture or video that goes on the screen when you run it.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: false,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Find the green screen",
                    StepType.VideoAnalyze,
                    // The plate video's id is filled in when the workflow is made (the assistant's
                    // ProposeGreenScreenInsert does it). Transcription and Vision are Off so the step calls no
                    // model or vendor and RunRateClassifier counts the run in the free bucket. A copy applied from
                    // the template picker has no id yet, so "placeholders" names the field to fill in — inert JSON
                    // the typed config ignores, and the one thing that tells the user (and the assistant reading
                    // the step) which clip the step wants.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"source":{"kind":"ProjectFile"},"detectInsertRegions":true,"transcription":"Off","vision":"Off","placeholders":{"source.projectFileId":"the clip that has the green screen: set source.projectFileId to a video or picture in this project"}}
                        """),
                new(
                    AgentType.VideoTransform,
                    "Put the content on the screen",
                    StepType.VideoCompile,
                    // No editor agent: keepWholeSources keeps the clip whole and only adds the insert. The
                    // content is named by insertContentRef, left out here because a reference with no id does not
                    // pass save validation; ProposeGreenScreenInsert adds it (a project file, or a prior render).
                    // "placeholders" names that field so a template-picker copy says what it still needs.
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"keepWholeSources":true,"mode":"Reencode","enableInserts":true,"placeholders":{"insertContentRef":"the content to put on the screen: set insertContentRef to {\"from\":\"ProjectFile\",\"projectFileId\":\"<file id>\"}, or to {\"from\":\"Step\",\"stepOrder\":<an earlier step>} for a render"}}
                        """)
            ]),
        new(
            Key: SpokespersonClipTemplateKey,
            Name: "Spokesperson clip",
            Description: "Makes a short AI video clip of a real person from one of this project's avatars (someone who gave their consent). Choose the avatar when you run it, write what they do in the step's prompt, and allow this step to send media in the app: the person's pictures, short videos and voice samples are sent to the video provider. Every clip is marked as AI-generated. Uses credits for each clip.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Make the spokesperson clip",
                    StepType.VideoGenerate,
                    // No avatar id: the run brief's avatar fills an inline step that names none. The permission to
                    // send the person's media ships off (only the customer turns it on, in the app; WL015). The cap
                    // covers one 6 s take plus the most input material the avatar limits allow (eight pictures,
                    // 15 s of reference video) on MiniMax-H3 at 768P. "placeholders" names what is still needed.
                    VideoGenerateConfigJson: """
                        {"planSource":"Inline","inlinePrompt":"The person looks into the camera and greets the viewer warmly in a bright, softly lit studio. Medium close-up, steady camera.","model":"MiniMax-H3","resolution":"768P","durationSeconds":6,"ratio":"9:16","takes":1,"maxClips":1,"maxSpendCredits":400,"allowSourceMediaEgress":false,"placeholders":{"avatar":"choose the avatar in the run brief (avatar.avatarId) or set avatarId on this step, then allow sending media for this step in the app"}}
                        """)
            ]),
        new(
            Key: TalkingAvatarBudgetTemplateKey,
            Name: "Talking avatar (budget)",
            Description: "A cheaper clip of a consenting person from one of this project's avatars saying a few sentences in their own linked voice: it writes the words, speaks them and animates one still picture of the person (5 to 15 seconds). Choose the avatar when you run it, say what they should say in your brief, and allow the last step to send media in the app. Marked as AI-generated. Uses credits.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(AgentType.ScriptwriterAgent, "Write what the person says", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Speak it in the avatar's voice",
                    StepType.Voiceover,
                    // VoiceFromAvatar: the voice linked to the run's avatar, through the voice consent guard; a brief
                    // that names a voice still wins.
                    VoiceoverConfigJson: """
                        {"source":{"kind":"ScriptScenes","scriptwriterStepOrder":1},"voiceFromAvatar":true}
                        """),
                new(
                    AgentType.VideoTransform,
                    "Make the talking clip",
                    StepType.VideoGenerate,
                    // The still-then-animate path: no avatar id (the brief's fills it), the narration of step 2 drives
                    // the mouth, the prompt sets the still's look. 15 s at 768P is $1.20 of video plus one still; the
                    // cap leaves room for both. The permission to send media ships off (WL015, set only in the app).
                    VideoGenerateConfigJson: """
                        {"planSource":"Inline","inlinePrompt":"A bright, softly lit studio with a plain light background; the person in a smart casual top, framed from the chest up.","model":"minimax/h3-max/lip-sync/image-to-video","resolution":"768P","durationSeconds":15,"ratio":"9:16","takes":1,"maxClips":1,"maxSpendCredits":250,"timeoutSeconds":1200,"avatarAudio":{"from":"Step","stepOrder":2},"allowSourceMediaEgress":false,"placeholders":{"avatar":"choose the avatar in the run brief (avatar.avatarId), then allow sending media for this step in the app","provider":"needs a fal.ai VideoGeneration model minimax/h3-max/lip-sync/image-to-video and a fal.ai ImageEdit model (google/nano-banana-2.1/edit) as the defaults"}}
                        """)
            ]),
        new(
            Key: SoundEffectsTemplateKey,
            Name: "Sound effects for your video",
            Description: "Adds short sound effects to a finished video without cutting anything: a pop where something appears, a whoosh on a move or a scene change, a shutter on a camera flash. It finds the moments by looking at the picture, picks sounds from the built-in sound library (and from your own sound files, if the project has any) and mixes them in under the voice. Pick your video in the first step. Needs a chat provider.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Find the moments in the video",
                    StepType.VideoAnalyze,
                    // analyzeEvents: the free, local detector finds what happens in the picture (cuts, things appearing,
                    // moving, flashing, text coming in) and offers each as an e{n} anchor with plain words. outline: the
                    // sound designer reads the whole film in sections. offerSfxClips + includeSfxLibrary: the built-in
                    // library first, the project's own audio files after. Transcription and Vision Off: finding the
                    // moments calls no provider and needs no speech-to-text provider; the moments come from the picture.
                    // describeEvents: when the workspace has a Vision provider, the step then sends three small frames
                    // of some moments to it to say what they show, and keeps the frames of the ones it did not get to
                    // so the sound designer can ask about a few more (LookAtMoment). So frames leave the host and the
                    // run counts as paid; with no Vision provider nothing is sent or kept. ProjectFile source for the
                    // same reason as video-derush-edit's first step.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","vision":"Off","analyzeEvents":true,"outline":true,"offerSfxClips":true,"includeSfxLibrary":true,"describeEvents":true}
                        """),
                new(
                    AgentType.SoundDesigner,
                    "Choose a sound for each moment",
                    // PreviousStepOnly: there is no editing decision to read, only the analysis view (the offered
                    // sounds and the e{n} moments). The plan is ids and words; the compile step resolves the times.
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly,
                    CacheMode: StepCacheMode.Never),
                new(
                    AgentType.VideoTransform,
                    "Mix the sound effects in",
                    StepType.VideoCompile,
                    // No editor agent: keepWholeSources keeps the video whole, so nothing of a finished edit is cut
                    // and every moment keeps its time. Reencode because mixing sounds into the audio has no
                    // stream-copy equivalent. maxSfxCues: a film full of graphics takes many more cues than the
                    // default of eight; the sound designer still decides how many.
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Step","stepOrder":1},"analysisStepOrder":1,"keepWholeSources":true,"mode":"Reencode","enableSfx":true,"sfxPlan":{"from":"Step","stepOrder":2},"maxSfxCues":80}
                        """)
            ]),
        new(
            Key: SubtitlesTemplateKey,
            Name: "Subtitles for your video",
            Description: "Adds subtitles to a finished video without changing anything else. It listens to what is said, burns the words in at the right moments and gives you SRT and VTT subtitle files to download. Pick your clip in the first step and say which language is spoken in your brief. Needs a speech-to-text provider.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Listen to the video",
                    StepType.VideoAnalyze,
                    // Transcription Required: a subtitles job with no words fails here, naming the missing
                    // speech-to-text provider, instead of returning the clip unchanged. Vision Off and no
                    // detection extras keep the step to the audio it needs. ProjectFile source for the same
                    // reason as video-derush-edit's first step: this is the first step, so PreviousStepOutput
                    // would fail SOURCE_UNRESOLVED on every run.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"source":{"kind":"ProjectFile"},"transcription":"Required","vision":"Off"}
                        """),
                new(
                    AgentType.VideoTransform,
                    "Burn in the subtitles",
                    StepType.VideoCompile,
                    // No editor agent: keepWholeSources keeps the clip whole, so nothing is cut and the
                    // transcript's own timing is the output's timing. Reencode because burning text into
                    // the picture has no stream-copy equivalent (CAPTIONS_REQUIRE_REENCODE).
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"keepWholeSources":true,"mode":"Reencode","enableCaptions":true,"captionSource":"Transcript","captionStyle":"Subtitle"}
                        """)
            ]),
        new(
            Key: SubtitlesTranslatedTemplateKey,
            Name: "Subtitles in another language",
            Description: "Adds subtitles to a finished video in the language you name in your brief, with the original timing, and gives you SRT and VTT subtitle files to download. It listens to what is said, translates the words and burns them in. Pick your clip in the first step. Needs a speech-to-text provider and a chat provider.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps:
            [
                new(
                    AgentType.VideoTransform,
                    "Listen to the video",
                    StepType.VideoAnalyze,
                    // The view the projection reads cuts each segment's text at maxSegmentTextChars (160 by default),
                    // and a translation of a cut text would replace the whole segment: raise the bounds so a
                    // segment reaches the translator in full.
                    VideoAnalyzeConfigJson: """
                        {"version":1,"source":{"kind":"ProjectFile"},"transcription":"Required","vision":"Off","maxSegmentTextChars":2000,"maxViewSegments":1000,"maxOutputChars":100000}
                        """),
                new(
                    AgentType.ExtractTransform,
                    "Reduce the transcript to what is said",
                    StepType.Extract,
                    // The translator gets exactly the transcript segments: their ids (t0, t1, ...) are the
                    // anchors the translation comes back keyed by, so the subtitles keep the original
                    // timing. At least one segment, or the run stops here, naming the missing speech.
                    ExtractConfigJson: """
                        {"version":1,"operation":"Project","inputs":{"source":{"from":"Step","stepOrder":1}},"path":"view.segments","fields":["text"],"idField":"id","maxOutputChars":100000,"expect":{"minItems":1}}
                        """),
                new(
                    AgentType.NarrationTranslator,
                    "Translate what is said into the subtitle language",
                    // PreviousStepOnly: the translator needs exactly the segments it translates; the target
                    // language arrives from the run's brief.
                    AgentInputContextMode: AgentInputContextMode.PreviousStepOnly),
                new(
                    AgentType.VideoTransform,
                    "Burn in the subtitles",
                    StepType.VideoCompile,
                    VideoCompileConfigJson: """
                        {"version":1,"decision":{"from":"Previous"},"analysisStepOrder":1,"keepWholeSources":true,"mode":"Reencode","enableCaptions":true,"captionSource":"Transcript","captionTranslationStepOrder":3,"captionStyle":"Subtitle"}
                        """)
            ])
    ];

    /// <summary>
    /// The review loop every shipped template carries (docs/design/review-loop-adjustments.md): the reviewer may change
    /// settings of earlier steps and ask for the changes a customer can request (paid steps re-run and are charged to the
    /// run, bounded by MaxIterations and the credit gate), and it sees the film: sampled frames (the default count), the
    /// timeline and audio facts, and targeted looks. The decision early stop stays off (<c>enabled</c> of the loop itself).
    /// </summary>
    public const string ShippedReviewLoopConfigJson =
        """{"version":1,"allowAdjustments":true,"visualCheck":{"enabled":true,"timelineFacts":true,"audioFacts":true,"targetedLooks":true,"qualityChecks":true}}""";

    /// <summary>
    /// The authored templates with their curated app manifests attached (<see cref="WorkflowTemplateAppManifests"/>) and
    /// every review loop given <see cref="ShippedReviewLoopConfigJson"/>.
    /// </summary>
    private static readonly IReadOnlyList<WorkflowTemplateDefinition> _templates =
        _authoredTemplates
            .Select(template => WorkflowTemplateAppManifests.ByKey.TryGetValue(template.Key, out AppManifest? manifest)
                ? template with { AppManifest = manifest }
                : template)
            .Select(template => template with
            {
                Steps = [.. template.Steps.Select(step => step.StepType == StepType.ReviewLoop && step.ReviewLoopConfigJson is null
                    ? step with { ReviewLoopConfigJson = ShippedReviewLoopConfigJson }
                    : step)]
            })
            .ToList();

    /// <summary>
    /// The templates as served (docs/design/app-inputs.md 12.2, lane L7): the base list with its app inputs, placeholders and
    /// bindings, the merged <see cref="AvatarClipTemplateKey"/> and the two avatar templates it replaces hidden; built on
    /// first use.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<WorkflowTemplateDefinition>> _servedTemplates =
        new(() => WorkflowTemplateAppInputs.Edition(_templates));

    /// <summary>The authored templates before app inputs are laid over them (the base of <see cref="GetAll()"/>); for tests that pin what the app-input layer changes.</summary>
    internal static IReadOnlyList<WorkflowTemplateDefinition> BaseTemplates => _templates;

    /// <summary>A base template by key (see <see cref="BaseTemplates"/>).</summary>
    internal static WorkflowTemplateDefinition? BaseByKey(string key) =>
        _templates.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    private static readonly Lazy<IReadOnlyList<WorkflowTemplateDefinition>> _servedTemplatesNoAvatarInput =
        new(() => WithoutAvatarInput(_servedTemplates.Value));

    /// <summary>Copies of the templates whose manifest carries no brief Avatar input (the same list when none does).</summary>
    private static IReadOnlyList<WorkflowTemplateDefinition> WithoutAvatarInput(IReadOnlyList<WorkflowTemplateDefinition> templates)
    {
        static bool IsAvatarInput(Apps.AppManifestInput i) => i.Field == Apps.BriefFieldConsumers.Avatar;
        if (!templates.Any(t => t.AppManifest?.Inputs?.Any(IsAvatarInput) == true))
            return templates;
        return [.. templates.Select(t => t.AppManifest?.Inputs?.Any(IsAvatarInput) == true
            ? t with { AppManifest = t.AppManifest with { Inputs = [.. t.AppManifest.Inputs.Where(i => !IsAvatarInput(i))] } }
            : t)];
    }

    /// <summary>
    /// Every template, with its app inputs, hidden and superseded templates included (a workflow made from one still finds
    /// its template).
    /// </summary>
    public static IReadOnlyList<WorkflowTemplateDefinition> GetAll() =>
        Apps.AppStepFacts.AvatarsEnabled ? _servedTemplates.Value : _servedTemplatesNoAvatarInput.Value;

    /// <summary>The templates a person or the gallery may pick from: <see cref="GetAll()"/> without the hidden ones.</summary>
    public static IReadOnlyList<WorkflowTemplateDefinition> GetListed() =>
        GetAll().Where(template => !template.Hidden && (Apps.AppStepFacts.PersonAvatarsEnabled || !IsAvatarTemplate(template.Key))).ToList();

    public static IReadOnlyList<WorkflowTemplateDefinition> GetAutoCreateTemplates() =>
        GetListed().Where(template => template.AutoCreateOnProject).ToList();

    public static WorkflowTemplateDefinition? GetByKey(string key) =>
        GetAll().FirstOrDefault(template =>
            string.Equals(template.Key, key, StringComparison.OrdinalIgnoreCase));

    public static string BuildVersionedWorkflowName(WorkflowTemplateDefinition template) =>
        $"{template.Name} v{template.Version}";
}
