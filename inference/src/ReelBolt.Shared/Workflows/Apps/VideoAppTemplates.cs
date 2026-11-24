using System.Text.Json;
using ReelBolt.Shared.Data.Models;
using static ReelBolt.Shared.Workflows.Apps.WorkflowTemplateAppInputs;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The three video apps that replace the footage template zoo (docs/design/video-events-analysis.md section 7): "Edit my
/// video", "Languages" and "Effects on a clip". Each is one template whose typed inputs choose what the run does: a step
/// the person did not switch on sits on a branch an input-decided Conditional leaves out, so the run holds, charges and
/// lints only what is on (the cost badge stays derived, <see cref="AppCostClass"/>). The old template keys stay
/// resolvable as hidden aliases of these (<see cref="Aliases"/>). Egress consent ships off and no input can reach it.
/// <para>
/// Step orders are never written by hand: a step is named, and <c>@@name@@</c> in a branch or a config becomes its order.
/// </para>
/// </summary>
public static class VideoAppTemplates
{
    /// <summary>"Edit my video".</summary>
    public const string EditMyVideoKey = "edit-my-video";

    /// <summary>"Languages".</summary>
    public const string LanguagesKey = "languages";

    /// <summary>"Effects on a clip".</summary>
    public const string EffectsOnAClipKey = "effects-on-a-clip";

    /// <summary>The keys of the three apps, in listing order.</summary>
    public static readonly IReadOnlyList<string> Keys = [EditMyVideoKey, LanguagesKey, EffectsOnAClipKey];

    /// <summary>The three apps.</summary>
    internal static IReadOnlyList<WorkflowTemplateDefinition> Build() => [EditMyVideo(), Languages(), EffectsOnAClip()];

    /// <summary>One old template key: the app it opens now and the input values it presets.</summary>
    public sealed record Alias(string Key, string App, IReadOnlyDictionary<string, JsonElement> Presets);

    private static Alias A(string key, string app, params (string Id, string Json)[] presets) =>
        new(key, app, presets.ToDictionary(p => p.Id, p => J(p.Json), StringComparer.Ordinal));

    /// <summary>
    /// The footage template keys that now open one of the three apps. They stay resolvable (a workflow made from one keeps its own
    /// steps), are hidden from every list, and creating from one of these keys creates the app with these values preset.
    /// </summary>
    public static readonly IReadOnlyList<Alias> Aliases =
    [
        A("video-derush-edit", EditMyVideoKey),
        A(WorkflowTemplateCatalog.TestimonialReelTemplateKey, EditMyVideoKey),
        A("video-derush-edit-graphics", EditMyVideoKey, ("graphics", "\"titles\"")),
        A("video-derush-edit-music", EditMyVideoKey, ("music", "\"on\"")),
        A("video-derush-edit-room", EditMyVideoKey),
        A("video-derush-edit-graphics-room", EditMyVideoKey, ("graphics", "\"callouts\"")),
        A("video-derush-edit-grade-room", EditMyVideoKey, ("colourLook", "\"natural\"")),
        A("video-derush-edit-sfx", EditMyVideoKey, ("soundEffects", "\"subtle\"")),
        A("video-derush-edit-broll", EditMyVideoKey, ("bRoll", "true")),
        A("video-derush-edit-dailies", EditMyVideoKey, ("bRoll", "true")),
        A("video-derush-edit-voiceover", EditMyVideoKey, ("narrate", "true"), ("captionsStyle", "\"karaoke\"")),
        A(WorkflowTemplateCatalog.NarratedStoryTemplateKey, EditMyVideoKey, ("narrate", "true"), ("captionsStyle", "\"karaoke\"")),
        A("video-derush-edit-pickups", EditMyVideoKey, ("fixLines", "true")),
        A("video-derush-edit-dub", LanguagesKey, ("dub", "\"voice\"")),
        A(WorkflowTemplateCatalog.SubtitlesTemplateKey, LanguagesKey),
        A(WorkflowTemplateCatalog.SubtitlesTranslatedTemplateKey, LanguagesKey, ("translate", "true")),
        A(WorkflowTemplateCatalog.RegionEffectTemplateKey, EffectsOnAClipKey, ("effect", "\"blur\"")),
        A(WorkflowTemplateCatalog.GreenScreenInsertTemplateKey, EffectsOnAClipKey, ("effect", "\"screen\"")),
    ];

    /// <summary>The preset values by old key, in the shape <see cref="WorkflowTemplateAppInputs.SupersedeDefaults"/> takes.</summary>
    internal static IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> AliasDefaults =>
        Aliases.ToDictionary(a => a.Key, a => a.Presets, StringComparer.Ordinal);

    /// <summary>The app a footage key opens, or null.</summary>
    public static string? AppFor(string key) => Aliases.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))?.App;


    private sealed record Named(string Key, WorkflowTemplateStepDefinition Step);

    /// <summary>Gives each named step its order and writes the orders into the branches and configs that name them.</summary>
    private static IReadOnlyList<WorkflowTemplateStepDefinition> Order(IReadOnlyList<Named> named, out IReadOnlyDictionary<string, int> orders)
    {
        Dictionary<string, int> map = new(StringComparer.Ordinal);
        for (int i = 0; i < named.Count; i++)
            map[named[i].Key] = i + 1;
        orders = map;

        string? Sub(string? text)
        {
            if (text is null || !text.Contains("@@", StringComparison.Ordinal))
                return text;
            foreach ((string key, int order) in map)
                text = text.Replace($"@@{key}@@", order.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
            if (text.Contains("@@", StringComparison.Ordinal))
                throw new InvalidOperationException($"A step order is not resolved: {text}");
            return text;
        }

        return [.. named.Select(n => n.Step with
        {
            TrueBranchStepOrder = Sub(n.Step.TrueBranchStepOrder),
            FalseBranchStepOrder = Sub(n.Step.FalseBranchStepOrder),
            VideoAnalyzeConfigJson = Sub(n.Step.VideoAnalyzeConfigJson),
            VideoCompileConfigJson = Sub(n.Step.VideoCompileConfigJson),
            VideoGenerateConfigJson = Sub(n.Step.VideoGenerateConfigJson),
            VoiceoverConfigJson = Sub(n.Step.VoiceoverConfigJson),
            ColorGradeRoomConfigJson = Sub(n.Step.ColorGradeRoomConfigJson),
        })];
    }

    private static WorkflowTemplateStepDefinition If(string label, string expression, string whenTrue, string whenFalse) =>
        new(AgentType.ExtractTransform, label, StepType.Conditional,
            ConditionExpression: expression, TrueBranchStepOrder: whenTrue, FalseBranchStepOrder: whenFalse);

    private static WorkflowTemplateStepDefinition Agent(AgentType type, string label, AgentInputContextMode context = AgentInputContextMode.FullWorkflow) =>
        new(type, label, AgentInputContextMode: context, CacheMode: StepCacheMode.Never);

    // ---- 1. Edit my video ----

    private const string AnalyzeJson = """
        {"version":1,"subShotMode":"Content","minSubShotSec":1.2,"maxSubShotSec":4,"maxSubShotsPerShot":8,"source":{"kind":"ProjectFile"},"offerMusicTracks":false,"offerSfxClips":false,"emitOverlayPlacements":false,"analyzeEvents":true,"outline":true,"appInputs":{"offerMusicTracks":{"input":"music","values":{"off":false,"on":true,"onBeat":true}},"offerSfxClips":{"input":"soundEffects","values":{"off":false,"subtle":true,"strong":true}},"emitOverlayPlacements":{"input":"graphics","values":{"off":false,"titles":true,"callouts":true}},"transcription":{"input":"captionsStyle","values":{"off":"Optional","subtitle":"Required","karaoke":"Required","punch":"Required","bold":"Required"}},"sceneDetection":{"input":"whatIsIt","values":{"footage":"Sensitive","animation":"Standard","screen":"Standard"}}}}
        """;

    /// <summary>The settings both compiles share: the cut, the captions and the transitions.</summary>
    private const string CompileBase = """
        "version":1,"decision":{"from":"Step","stepOrder":@@story@@},"analysisStepOrder":@@analyze@@,"mode":"Reencode","captionStyle":"Subtitle","transitionPolicy":"Auto","programFadeInMs":500,"programFadeOutMs":800,"programAudioFadeInMs":300,"programAudioFadeOutMs":900,"minSegmentMs":800
        """;

    private const string CaptionBindings = """
        "enableCaptions":{"input":"captionsStyle","values":{"off":false,"subtitle":true,"karaoke":true,"punch":true,"bold":true}},"captionStyle":{"input":"captionsStyle","values":{"subtitle":"Subtitle","karaoke":"Karaoke","punch":"Punch","bold":"Bold"}},"transitionPolicy":"transitions","captionSource":{"input":"captionsStyle","values":{"off":"Voiceover","subtitle":"Transcript","karaoke":"Transcript","punch":"Transcript","bold":"Transcript"}}
        """;

    private const string FullPlans = """
        "enableSfx":false,"sfxPlan":{"from":"Step","stepOrder":@@sfx@@},"enableMusic":false,"musicPlan":{"from":"Step","stepOrder":@@music@@},"beatSync":"Off","enableMotion":false,"motionPlan":{"from":"Step","stepOrder":@@motion@@},"enableGraphics":false,"graphicsPlan":{"from":"Step","stepOrder":@@graphics@@},"enableColorGrade":false,"colorGradePlan":{"from":"Step","stepOrder":@@colour@@},"enableGeneratedClips":false,"generatedClips":{"from":"Step","stepOrder":@@brollGenerate@@},"maxGeneratedClips":4,"enableVoiceover":false,"voiceoverStepOrder":@@narrationVoice@@,"enablePickups":false,"pickupVoiceoverStepOrder":@@pickupVoice@@,"cutOnEvents":false
        """;

    private const string FullBindings = """
        "enableSfx":{"input":"soundEffects","values":{"off":false,"subtle":true,"strong":true}},"enableMusic":{"input":"music","values":{"off":false,"on":true,"onBeat":true}},"beatSync":{"input":"music","values":{"off":"Off","on":"Off","onBeat":"Beat"}},"enableMotion":{"input":"motion","values":{"off":false,"subtle":true,"dynamic":true}},"enableGraphics":{"input":"graphics","values":{"off":false,"titles":true,"callouts":true}},"enableColorGrade":{"input":"colourLook","values":{"off":false,"natural":true,"warm":true,"cool":true,"cinematic":true}},"enableGeneratedClips":"bRoll","enableVoiceover":"narrate","enablePickups":"fixLines","cutOnEvents":"cutOnEvents"
        """;

    private static string FullCompileJson() => "{" + CompileBase + "," + FullPlans + ",\"appInputs\":{" + CaptionBindings + "," + FullBindings + "}}";

    private static string LiteCompileJson() => "{" + CompileBase + ",\"appInputs\":{" + CaptionBindings + "}}";

    private static WorkflowTemplateDefinition EditMyVideo()
    {
        List<Named> steps =
        [
            new("analyze", new(AgentType.VideoTransform, "Analyze the video", StepType.VideoAnalyze, VideoAnalyzeConfigJson: AnalyzeJson)),
            new("story", Agent(AgentType.VideoStoryEditor, "Decide which spans to keep", AgentInputContextMode.PreviousStepOnly)),
            // Lite mode: a plain cut with captions and transitions, no extras and no AI review, then the run ends.
            new("lite", If("Lite mode?", "input.quickMode", "@@liteCompile@@", "@@sfxIf@@")),
            new("liteCompile", new(AgentType.VideoTransform, "Compile the edited video", StepType.VideoCompile, VideoCompileConfigJson: LiteCompileJson())),
            new("liteEnd", new(AgentType.ExtractTransform, "Finish here", StepType.Conditional, TrueBranchStepOrder: "end")),
            new("sfxIf", If("Sound effects?", "input.soundEffects != 'off'", "@@sfx@@", "@@musicIf@@")),
            new("sfx", Agent(AgentType.SoundDesigner, "Plan sound-effect cues")),
            new("musicIf", If("Music?", "input.music != 'off'", "@@music@@", "@@motionIf@@")),
            new("music", Agent(AgentType.MusicSupervisor, "Pick background music")),
            new("motionIf", If("Camera moves and zooms?", "input.motion != 'off'", "@@motion@@", "@@graphicsIf@@")),
            new("motion", Agent(AgentType.MotionDirector, "Plan moves, ramps and flashes on the action")),
            new("graphicsIf", If("Titles and callouts?", "input.graphics != 'off'", "@@graphics@@", "@@colourIf@@")),
            new("graphics", Agent(AgentType.MotionGraphicsPlanner, "Plan titles and callouts")),
            new("colourIf", If("Colour look?", "input.colourLook != 'off'", "@@colour@@", "@@brollIf@@")),
            new("colour", new(AgentType.VideoTransform, "Choose the colour look", StepType.ColorGradeRoom,
                ColorGradeRoomConfigJson: """{"version":1,"view":{"from":"Step","stepOrder":@@analyze@@}}""", CacheMode: StepCacheMode.Never)),
            new("brollIf", If("Extra AI shots?", "input.bRoll", "@@brollPlan@@", "@@narrateIf@@")),
            new("brollPlan", Agent(AgentType.ShotDirector, "Plan the extra shots")),
            // The permission to send the person's media stays off (WL015); the cap covers four clips.
            new("brollGenerate", new(AgentType.VideoTransform, "Generate the extra shots", StepType.VideoGenerate,
                VideoGenerateConfigJson: """
                    {"planSource":"StepRef","plan":{"from":"Step","stepOrder":@@brollPlan@@},"analysis":{"from":"Step","stepOrder":@@analyze@@},"model":"MiniMax-H3","resolution":"768P","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":4,"maxSpendCredits":833.3333333333333333333333333,"pollIntervalSeconds":10,"timeoutSeconds":1800,"allowSourceMediaEgress":false,"appInputs":{"@model":"bRollModel","maxClips":"bRollShots","maxSpendCredits":"bRollCredits"}}
                    """)),
            new("narrateIf", If("Voice-over?", "input.narrate", "@@narration@@", "@@pickupIf@@")),
            new("narration", Agent(AgentType.NarrationWriter, "Write the voice-over lines")),
            new("narrationVoice", new(AgentType.VideoTransform, "Speak the voice-over", StepType.Voiceover,
                VoiceoverConfigJson: """{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":@@narration@@}}""")),
            new("pickupIf", If("Fix flubbed lines?", "input.fixLines", "@@pickup@@", "@@compile@@")),
            new("pickup", Agent(AgentType.PickupPlanner, "Plan pickups for flubbed lines")),
            new("pickupVoice", new(AgentType.VideoTransform, "Re-record the pickups", StepType.Voiceover,
                VoiceoverConfigJson: """{"source":{"kind":"PickupPlan","pickupPlannerStepOrder":@@pickup@@}}""")),
            new("compile", new(AgentType.VideoTransform, "Compile the edited video", StepType.VideoCompile, VideoCompileConfigJson: FullCompileJson())),
        ];
        // The review loop follows the compile directly and goes back to the story editor (explicit orders, never "Previous").
        int storyOrder = steps.FindIndex(n => n.Key == "story") + 1;
        steps.Add(new("review", new(AgentType.VideoReviewAgent, "Review edit quality", StepType.ReviewLoop,
            ReviewLoopConfigJson: WorkflowTemplateCatalog.ShippedReviewLoopConfigJson,
            LoopTargetStepOrder: storyOrder, MaxIterations: 3, MinScore: 8,
            AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never)));

        return new WorkflowTemplateDefinition(
            Key: EditMyVideoKey,
            Name: "Edit my video",
            Description: "Upload raw footage, a screen recording or a finished animation and get a tight edit. Switch on what you want added: sound effects, music, zooms and camera moves, titles, a colour look, extra AI shots, a voice-over or captions. Uses credits, but only for what you switch on.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps: Order(steps, out _),
            AppManifest: EditMyVideoManifest());
    }

    private static AppManifestInput Files(string role, string label, int min, int max, bool? more = null) =>
        new(AppManifestVocabulary.KindFiles, Role: role, Label: label, Min: min, Max: max, More: more);

    private static AppManifestInput Brief(string field, string label, bool? more = null, string? def = null, string? choices = null) =>
        new(AppManifestVocabulary.KindBrief, Field: field, Label: label, More: more,
            Default: def is null ? null : J(def),
            Choices: choices is null ? null : JsonSerializer.Deserialize<List<JsonElement>>(choices));

    private static AppManifest EditMyVideoManifest() => new(
        Version: AppManifestVocabulary.Version2,
        Title: "Edit my video",
        Tagline: "Raw footage, a screen recording or a finished animation, cut tight, with only the extras you switch on.",
        Category: "footage",
        SuggestedStylePreset: "fast-social-reel",
        Inputs:
        [
            Files("video", "Your video", 1, 20),
            Input("whatIsIt", "choice", "What is it", def: "\"footage\"", help: "Tells the editor what to look for.",
                options: [Option("footage", "rawFootage", "Raw footage"), Option("animation", "finishedAnimation", "Finished animation"), Option("screen", "screenRecording", "Screen recording")]),
            Brief(BriefFieldConsumers.LengthSec, "Length", def: "60", choices: "[15,30,60,90]"),
            Brief(BriefFieldConsumers.Format, "Shape", more: true),
            Input("soundEffects", "choice", "Sound effects", def: "\"off\"",
                help: "Hits, whooshes and clicks placed on the action.", options: Levels("off", "subtle", "strong")),
            Input("captionsStyle", "choice", "Captions", def: "\"off\"", help: "Shows the spoken words on screen.",
                options: [Option("off", "off", "Off"), Option("subtitle", "subtitle", "Plain"), Option("karaoke", "karaoke", "Word by word"), Option("punch", "punch", "One big word"), Option("bold", "bold", "Big and bold")]),
            Input("quickMode", "toggle", "Lite mode (cheaper)", def: "false",
                help: "A plain cut with captions and transitions: the extras below are ignored and there is no AI review."),
            new AppManifestInput(AppManifestVocabulary.KindNotes, Label: "What should it keep or show?"),
            Input("music", "choice", "Music", def: "\"off\"", more: true,
                help: "Off by default. On mixes a track from this project's music files under the voices, lowered while people speak.",
                options: [Option("off", "off", "Off"), Option("on", "on", "On"), Option("onBeat", "onBeat", "On, cutting to the beat")]),
            Files("music", "Your music track", 0, 1, more: true),
            Input("motion", "choice", "Zooms and camera moves", def: "\"subtle\"", more: true,
                help: "On by default, subtle: pushes, pulls and shakes placed on the action in the picture. Choose Off for none.", options: Levels("off", "subtle", "dynamic")),
            Input("speedRamps", "toggle", "Speed ramps", def: "true", more: true,
                help: "On by default: slow motion and speed-ups on the action. Works with zooms and camera moves."),
            Input("flashes", "toggle", "Flashes", def: "true", more: true,
                help: "On by default: a short flash on the hits. Works with zooms and camera moves."),
            Input("graphics", "choice", "Graphics", def: "\"off\"", more: true,
                help: "Words and shapes drawn over the picture where it has room.",
                options: [Option("off", "off", "Off"), Option("titles", "titles", "Titles"), Option("callouts", "callouts", "Callouts")]),
            Input("transitions", "choice", "Transitions", def: "\"Auto\"", more: true,
                options: [Option("Auto", "autoTransitions", "Automatic"), Option("Expressive", "expressive", "Expressive")]),
            Input("colourLook", "choice", "Colour look", def: "\"off\"", more: true,
                help: "A grade chosen by an AI colour team.",
                options: [Option("off", "off", "Off"), Option("natural", "natural", "Natural"), Option("warm", "warm", "Warm"), Option("cool", "cool", "Cool"), Option("cinematic", "cinematic", "Cinematic")]),
            Input("bRoll", "toggle", "Extra AI shots", def: "false", more: true, showWhen: ("quickMode", "false"),
                help: "Up to four short AI shots cut in. They are paid, with a fixed cap you see before you run."),
            Input("narrate", "toggle", "Voice-over", def: "false", more: true, showWhen: ("quickMode", "false"),
                help: "A narration written for what is kept, spoken and captioned. Pick its language and voice below."),
            Input("bRollShots", "number", "Most extra shots", def: "4", more: true, min: 1, max: 4, step: 1,
                help: "The most extra AI shots to plan and make. Fewer shots cost less."),
            Input("bRollCredits", "number", "Most credits to spend on extra shots", def: "834", min: 1, max: 100000, step: 1,
                help: "The most credits the extra AI shots may cost this run."),
            ModelInput("bRollModel", "Video model for the extra shots", "VideoGeneration"),
            Brief(BriefFieldConsumers.Language, "Voice-over language", more: true),
            Brief(BriefFieldConsumers.Voice, "Voice-over voice", more: true),
            Input("fixLines", "toggle", "Fix flubbed lines", def: "false", more: true,
                help: "Re-records stumbled lines in your project voice. Needs speech in the video."),
            Input("cutOnEvents", "toggle", "Cut on the action", def: "true", more: true, showWhen: ("quickMode", "false"),
                help: "On by default: lets cuts land on things that happen in the picture."),
            Brief(BriefFieldConsumers.StylePreset, "Style", more: true),
        ],
        Result: new AppManifestResult("video"),
        Advanced: new AppManifestAdvanced(true),
        RequestTemplate: string.Join(". ",
            "What the video is: {{input.whatIsIt}}",
            "Sound effects: {{input.soundEffects}}",
            "Music: {{input.music}}",
            "Zooms and camera moves: {{input.motion}}",
            "Speed ramps on the action: {{input.speedRamps}}",
            "Flashes on the hits: {{input.flashes}}",
            "Graphics: {{input.graphics}}",
            "Colour look: {{input.colourLook}}",
            "Most extra AI shots: {{input.bRollShots}}",
            "Fix flubbed lines with pickups: {{input.fixLines}}"));

    // ---- 2. Languages ----

    private const string TranscribeJson = """
        {"version":1,"source":{"kind":"ProjectFile"},"transcription":"Required","vision":"Off","maxSegmentTextChars":2000,"maxViewSegments":1000,"maxOutputChars":100000}
        """;

    private const string SubtitleBindings = """
        "appInputs":{"enableCaptions":{"input":"subtitles","values":{"off":false,"burned":true}},"captionSource":{"input":"subtitles","values":{"off":"Voiceover","burned":"Transcript"}}}
        """;

    private static WorkflowTemplateDefinition Languages()
    {
        List<Named> steps =
        [
            new("analyze", new(AgentType.VideoTransform, "Listen to the video", StepType.VideoAnalyze, VideoAnalyzeConfigJson: TranscribeJson)),
            new("dubIf", If("Dub?", "input.dub != 'off'", "@@dubStory@@", "@@translateIf@@")),
            new("translateIf", If("Translate?", "input.translate", "@@extract@@", "@@plain@@")),
            new("plain", new(AgentType.VideoTransform, "Burn in the subtitles", StepType.VideoCompile, VideoCompileConfigJson:
                "{\"version\":1,\"decision\":{\"from\":\"Step\",\"stepOrder\":@@analyze@@},\"analysisStepOrder\":@@analyze@@,\"keepWholeSources\":true,\"mode\":\"Reencode\",\"captionStyle\":\"Subtitle\"," + SubtitleBindings + "}")),
            new("plainEnd", new(AgentType.ExtractTransform, "Finish here", StepType.Conditional, TrueBranchStepOrder: "end")),
            new("extract", new(AgentType.ExtractTransform, "Reduce the transcript to what is said", StepType.Extract, ExtractConfigJson: """
                {"version":1,"operation":"Project","inputs":{"source":{"from":"Step","stepOrder":1}},"path":"view.segments","fields":["text"],"idField":"id","maxOutputChars":100000,"expect":{"minItems":1}}
                """)),
            new("translate", Agent(AgentType.NarrationTranslator, "Translate what is said into the other language", AgentInputContextMode.PreviousStepOnly)),
            new("translated", new(AgentType.VideoTransform, "Burn in the translated subtitles", StepType.VideoCompile, VideoCompileConfigJson:
                "{\"version\":1,\"decision\":{\"from\":\"Step\",\"stepOrder\":@@analyze@@},\"analysisStepOrder\":@@analyze@@,\"keepWholeSources\":true,\"mode\":\"Reencode\",\"enableCaptions\":true,\"captionSource\":\"Transcript\",\"captionTranslationStepOrder\":@@translate@@,\"captionStyle\":\"Subtitle\"}")),
            new("translatedEnd", new(AgentType.ExtractTransform, "Finish here", StepType.Conditional, TrueBranchStepOrder: "end")),
            new("dubStory", Agent(AgentType.VideoStoryEditor, "Decide which spans to keep", AgentInputContextMode.PreviousStepOnly)),
            new("dubWriter", Agent(AgentType.NarrationWriter, "Write narration lines")),
            new("dubTranslate", Agent(AgentType.NarrationTranslator, "Translate the narration into the target language", AgentInputContextMode.PreviousStepOnly)),
            new("dubVoice", new(AgentType.VideoTransform, "Speak the dub", StepType.Voiceover,
                VoiceoverConfigJson: """{"source":{"kind":"NarrationPlan","narrationWriterStepOrder":@@dubTranslate@@}}""")),
            new("dubCompile", new(AgentType.VideoTransform, "Compile the dubbed video", StepType.VideoCompile, VideoCompileConfigJson:
                "{\"version\":1,\"decision\":{\"from\":\"Step\",\"stepOrder\":@@dubStory@@},\"analysisStepOrder\":@@analyze@@,\"enableVoiceover\":true,\"voiceoverStepOrder\":@@dubVoice@@,\"voiceoverMode\":\"ReplaceDialogue\",\"enableCaptions\":false,\"captionStyle\":\"Subtitle\",\"transitionPolicy\":\"Auto\",\"programFadeInMs\":500,\"programFadeOutMs\":800,\"programAudioFadeInMs\":300,\"programAudioFadeOutMs\":900,\"minSegmentMs\":800,\"appInputs\":{\"enableCaptions\":{\"input\":\"subtitles\",\"values\":{\"off\":false,\"burned\":true}}}}")),
        ];
        int storyOrder = steps.FindIndex(n => n.Key == "dubStory") + 1;
        steps.Add(new("review", new(AgentType.VideoReviewAgent, "Review edit quality", StepType.ReviewLoop,
            ReviewLoopConfigJson: WorkflowTemplateCatalog.ShippedReviewLoopConfigJson,
            LoopTargetStepOrder: storyOrder, MaxIterations: 3, MinScore: 8,
            AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never)));

        return new WorkflowTemplateDefinition(
            Key: LanguagesKey,
            Name: "Languages",
            Description: "Add subtitles to a video, translate them into another language, or dub the video into that language. Subtitles are burned in and come as SRT and VTT files. Uses credits for the speech-to-text, the translation and, for a dub, the voice.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps: Order(steps, out _),
            AppManifest: new AppManifest(
                Version: AppManifestVocabulary.Version2,
                Title: "Languages",
                Tagline: "Subtitles, translated subtitles or a dub in another language, for a video you already have.",
                Category: "footage",
                Inputs:
                [
                    Files("video", "Your video", 1, 1),
                    Input("subtitles", "choice", "Subtitles", def: "\"burned\"", help: "Burned into the picture; SRT and VTT files come with them.",
                        options: [Option("off", "off", "Off"), Option("burned", "burned", "Burned in")]),
                    Input("translate", "toggle", "Translate the subtitles", def: "false", help: "Into the language chosen below, always burned in. Off keeps the spoken language."),
                    Brief(BriefFieldConsumers.Language, "Target language"),
                    Input("dub", "choice", "Dub", def: "\"off\"", help: "Replaces the voices with a spoken translation in the target language. The lips are not re-synced.",
                        options: [Option("off", "off", "Off"), Option("voice", "voice", "Spoken translation")]),
                    Brief(BriefFieldConsumers.Voice, "Dub voice", more: true),
                    Brief(BriefFieldConsumers.SourceLanguage, "Spoken language", more: true),
                    new AppManifestInput(AppManifestVocabulary.KindNotes, Label: "Anything to keep in mind?", More: true),
                ],
                Result: new AppManifestResult("video"),
                Advanced: new AppManifestAdvanced(true),
                RequestTemplate: "Dub: {{input.dub}}"));
    }

    // ---- 3. Effects on a clip ----

    private static WorkflowTemplateDefinition EffectsOnAClip()
    {
        List<Named> steps =
        [
            new("screenIf", If("Put content on a screen?", "input.effect == 'screen'", "@@screenAnalyze@@", "@@followIf@@")),
            new("followIf", If("Follow a subject?", "input.effect == 'follow'", "@@followAnalyze@@", "@@blurAnalyze@@")),
            new("blurAnalyze", new(AgentType.VideoTransform, "Find the object you named", StepType.VideoAnalyze, VideoAnalyzeConfigJson: """
                {"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","trackObjects":[{"label":"{{input.hideTarget}}","method":"Segmentation"}],"allowSourceMediaEgress":false,"maxSegmentationSeconds":60}
                """)),
            new("blurCompile", new(AgentType.VideoTransform, "Hide it through the whole clip", StepType.VideoCompile, VideoCompileConfigJson: """
                {"version":1,"decision":{"from":"Step","stepOrder":@@blurAnalyze@@},"analysisStepOrder":@@blurAnalyze@@,"keepWholeSources":true,"mode":"Reencode","regionEffects":[{"target":"{{input.hideTarget}}","effect":"Blur"}],"appInputs":{"regionEffects[0].effect":"hideWith"}}
                """)),
            new("blurEnd", new(AgentType.ExtractTransform, "Finish here", StepType.Conditional, TrueBranchStepOrder: "end")),
            new("followAnalyze", new(AgentType.VideoTransform, "Find the subject you named", StepType.VideoAnalyze, VideoAnalyzeConfigJson: """
                {"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","trackObjects":[{"label":"{{input.followTarget}}","method":"Segmentation"}],"allowSourceMediaEgress":false,"maxSegmentationSeconds":60}
                """)),
            new("followCompile", new(AgentType.VideoTransform, "Follow it with the camera", StepType.VideoCompile, VideoCompileConfigJson: """
                {"version":1,"decision":{"from":"Step","stepOrder":@@followAnalyze@@},"analysisStepOrder":@@followAnalyze@@,"keepWholeSources":true,"mode":"Reencode","followSubject":[{"trackId":"g0"}]}
                """)),
            new("followEnd", new(AgentType.ExtractTransform, "Finish here", StepType.Conditional, TrueBranchStepOrder: "end")),
            new("screenAnalyze", new(AgentType.VideoTransform, "Find the green screen", StepType.VideoAnalyze, VideoAnalyzeConfigJson: """
                {"version":1,"source":{"kind":"ProjectFile"},"detectInsertRegions":true,"transcription":"Off","vision":"Off"}
                """)),
            new("screenCompile", new(AgentType.VideoTransform, "Put the content on the screen", StepType.VideoCompile, VideoCompileConfigJson: """
                {"version":1,"decision":{"from":"Step","stepOrder":@@screenAnalyze@@},"analysisStepOrder":@@screenAnalyze@@,"keepWholeSources":true,"mode":"Reencode","enableInserts":true}
                """)),
        ];

        return new WorkflowTemplateDefinition(
            Key: EffectsOnAClipKey,
            Name: "Effects on a clip",
            Description: "Blur or pixelate one object through a whole clip, put a picture or video on a screen in your clip, or have the camera follow a subject. Needs permission to send the clip to the tracking service, which stays off until you allow it in the first step. Uses credits for tracking an object; a screen insert uses only processing time.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps: Order(steps, out _),
            AppManifest: new AppManifest(
                Version: AppManifestVocabulary.Version2,
                Title: "Effects on a clip",
                Tagline: "Blur an object, put content on a screen, or follow a subject, through a whole clip.",
                Category: "footage",
                Inputs:
                [
                    Files("video", "Your clip", 1, 1),
                    Input("effect", "choice", "What to do", def: "\"blur\"",
                        options: [Option("blur", "blurObject", "Blur an object"), Option("screen", "contentOnScreen", "Put content on a screen"), Option("follow", "followSubject", "Follow a subject")]),
                    Input("hideTarget", "shortText", "What to hide", required: true, maxLength: 120, showWhen: ("effect", "\"blur\""),
                        help: "Name the object in plain words. It is followed through the whole clip.", example: "the car's number plate"),
                    Input("hideWith", "choice", "How to hide it", def: "\"Blur\"", showWhen: ("effect", "\"blur\""),
                        options: [Option("Blur", "blur", "Blur"), Option("Pixelate", "pixelate", "Pixelate")]),
                    Input("followTarget", "shortText", "What to follow", required: true, maxLength: 120, showWhen: ("effect", "\"follow\""),
                        help: "Name the subject in plain words. The camera keeps it in frame.", example: "the dancer in the red jacket"),
                    Files("insertContent", "What goes on the screen", 0, 1),
                ],
                Result: new AppManifestResult("video"),
                Advanced: new AppManifestAdvanced(true)));
    }

    private static IReadOnlyList<AppInputOption> Levels(params string[] values) =>
        [.. values.Select(v => Option(v, v, char.ToUpperInvariant(v[0]) + v[1..]))];
}
