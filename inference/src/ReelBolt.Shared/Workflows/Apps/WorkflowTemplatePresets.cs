using ReelBolt.Shared.Data.Models;
using static ReelBolt.Shared.Workflows.Apps.WorkflowTemplateAppInputs;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The preset recipes (docs/design/recipe-presets.md): new built-in apps that are not video edits and show what the dynamic
/// inputs can do, a choice that reveals other fields (<c>showWhen</c>), a switch that picks a branch, a mapped binding that
/// writes several settings, the avatar and model pickers and the brief fields. Each is listed (so it shows in the gallery)
/// and none is created on a new project; the cost badge is derived from the steps (<see cref="AppCostClass"/>), never typed.
/// Egress consent ships off in every step that carries it and no input binds it (the allowlist forbids it), every paid
/// generation step carries a spend cap, and a review loop, where a recipe has one, ships the shared review-loop config.
/// <para>
/// Marketplace hook: <see cref="Keys"/> is the one list of these recipes by key, so an official-recipe offer can name them
/// without scanning the catalog; nothing else needs to change when the marketplace lands.
/// </para>
/// </summary>
public static class WorkflowTemplatePresets
{
    /// <summary>"Product promo, your way".</summary>
    public const string ProductPromoKey = "product-promo-your-way";

    /// <summary>"Avatar explainer from a topic".</summary>
    public const string AvatarExplainerKey = "avatar-explainer";

    /// <summary>"Music or sound effect".</summary>
    public const string MusicOrSoundEffectKey = "music-or-sound-effect";

    /// <summary>"Thumbnail and cover text".</summary>
    public const string ThumbnailKey = "thumbnail-and-cover-text";

    /// <summary>"Read my text aloud".</summary>
    public const string ReadAloudKey = "read-text-aloud";

    /// <summary>"AI video clip from your idea".</summary>
    public const string AiClipKey = "ai-clip-from-idea";

    /// <summary>The keys of every preset recipe, in listing order.</summary>
    public static readonly IReadOnlyList<string> Keys =
        [ProductPromoKey, AvatarExplainerKey, MusicOrSoundEffectKey, ThumbnailKey, ReadAloudKey, AiClipKey];

    /// <summary>The preset recipes, built on the base templates (the promo steps are copied from the starter promo).</summary>
    internal static IReadOnlyList<WorkflowTemplateDefinition> Build(IReadOnlyList<WorkflowTemplateDefinition> baseTemplates) =>
    [
        ProductPromo(baseTemplates.First(t => t.Key == WorkflowTemplateCatalog.StarterTemplateKey)),
        AvatarExplainer(),
        MusicOrSoundEffect(),
        Thumbnail(),
        ReadAloud(),
        AiClip(),
    ];

    private static AppManifestInput BriefField(string field, string label, bool? more = null, string? def = null, string? choices = null) =>
        new(AppManifestVocabulary.KindBrief, Field: field, Label: label, More: more,
            Default: def is null ? null : J(def),
            Choices: choices is null ? null : System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(choices));

    // ---- 1. Product promo, your way ----

    private static WorkflowTemplateDefinition ProductPromo(WorkflowTemplateDefinition starter)
    {
        // Steps 1 to 8 are the starter promo's own (code analysis to the script); the switch then decides whether the
        // script is spoken before the director plans the shots (audio first), and the director, author and review follow.
        List<WorkflowTemplateStepDefinition> steps = [.. starter.Steps.Take(8)];
        steps.Add(new(AgentType.ExtractTransform, "Spoken voiceover?", StepType.Conditional,
            ConditionExpression: "input.voiceover", TrueBranchStepOrder: "10", FalseBranchStepOrder: "11"));
        steps.Add(new(AgentType.VideoTransform, "Record the narration", StepType.Voiceover,
            VoiceoverConfigJson: """
                {"source":{"kind":"ScriptScenes","scriptwriterStepOrder":8},"appInputs":{"@model":"voiceModel"}}
                """));
        steps.Add(starter.Steps[8]);
        steps.Add(starter.Steps[9]);
        steps.Add(starter.Steps[10] with { LoopTargetStepOrder = 12 });

        return new(
            Key: ProductPromoKey,
            Name: "Product promo, your way",
            Description: "A short promo video of your product, made from the code in this project. Say who it is for and what it should show, pick the call to action, the language, the length and the shape, and switch on a spoken voiceover if you want one. Uses credits.",
            Version: 1,
            AutoCreateOnProject: false,
            RequiresUserInput: true,
            Steps: steps,
            AppManifest: new AppManifest(
                Version: AppManifestVocabulary.Version2,
                Title: "Product promo, your way",
                Tagline: "A short promo of your product from this project's code, with an optional spoken voiceover.",
                Category: "promo",
                SuggestedStylePreset: "fast-social-reel",
                RequestTemplate: "Made for this audience: {{input.audience}}. End the video with this call to action: {{input.callToAction}}",
                Inputs:
                [
                    new AppManifestInput(AppManifestVocabulary.KindNotes, Label: "What should it show?"),
                    Input("audience", "shortText", "Who it is for", required: true, maxLength: 120,
                        help: "The people the video speaks to.", example: "busy café owners"),
                    CallToAction() with { Required = true },
                    BriefField(BriefFieldConsumers.Language, "Language"),
                    BriefField(BriefFieldConsumers.LengthSec, "Length", def: "30", choices: "[15,30,60]"),
                    BriefField(BriefFieldConsumers.Format, "Shape"),
                    Input("voiceover", "toggle", "Spoken voiceover", def: "false",
                        help: "A narrator reads the script over the video. Uses a voice."),
                    BriefField(BriefFieldConsumers.Voice, "Narrator voice", more: true),
                    Tone(),
                    ModelInput("voiceModel", "Voice model", "SpeechSynthesis", showWhen: ("voiceover", "true")),
                    AgentModel("scriptModel", "Script model"),
                    AgentModel("builderModel", "Video builder model"),
                ],
                Result: new AppManifestResult("video"),
                Advanced: new AppManifestAdvanced(true)),
            AppInputAgents: new Dictionary<string, AgentType>(StringComparer.Ordinal)
            {
                ["scriptModel"] = AgentType.ScriptwriterAgent,
                ["builderModel"] = AgentType.AuthorAgent,
            });
    }

    // ---- 2. Avatar explainer from a topic ----

    // The step's "model" is only a hint: a talking head (avatarAudio) runs on the workspace's LipSync capability, and with no
    // providerId bound it uses that capability's own model (LipSyncStepResolution). The app's liteModel input binds both.
    private const string LipSyncClip = """
        {"planSource":"Inline","inlinePrompt":"A bright, softly lit studio with a plain light background; the person in a smart casual top, framed from the chest up.","model":"minimax/h3-max/lip-sync/image-to-video","resolution":"768P","durationSeconds":15,"ratio":"9:16","takes":1,"maxClips":1,"maxSpendCredits":250,"timeoutSeconds":1200,"avatarAudio":{"from":"Step","stepOrder":__AUDIO__},"allowSourceMediaEgress":false,"appInputs":{"ratio":"clipShape","@model":"liteModel","@stillModel":"pictureModel"},"placeholders":{"avatar":"choose the avatar in the app, then allow sending media for this step in the app","provider":"needs a fal.ai Lip-sync model (default: minimax/h3-max/lip-sync/image-to-video) and a fal.ai ImageEdit model (google/nano-banana-2.1/edit) as the defaults"}}
        """;

    private static WorkflowTemplateDefinition AvatarExplainer() => new(
        Key: AvatarExplainerKey,
        Name: "Avatar explainer from a topic",
        Description: "A short clip of a real person from one of this project's avatars (someone who gave their consent) explaining a topic in their own linked voice. Give a topic and a tone and the words are written for you, or switch on \"I will write the words\" and they are spoken exactly as you type them. One still picture of the person is animated to speak (about 15 seconds at most). Allow the clip step to send media in the app. Every clip is marked as AI-generated. Uses credits.",
        Version: 1,
        AutoCreateOnProject: false,
        RequiresUserInput: false,
        Steps:
        [
            new(AgentType.ExtractTransform, "Own words?", StepType.Conditional,
                ConditionExpression: "input.ownWords", TrueBranchStepOrder: "2", FalseBranchStepOrder: "5"),
            // The own-words branch comes first: the person's text, spoken as written.
            new(AgentType.VideoTransform, "Say the words in the avatar's voice", StepType.Voiceover,
                VoiceoverConfigJson: """
                    {"source":{"kind":"Inline","lines":[{"text":"Hello!","startSec":0}]},"voiceFromAvatar":true,"appInputs":{"@spokenText":"explainerWords"}}
                    """),
            new(AgentType.VideoTransform, "Make the talking clip from your words", StepType.VideoGenerate,
                VideoGenerateConfigJson: LipSyncClip.Replace("__AUDIO__", "2", StringComparison.Ordinal)),
            new(AgentType.ExtractTransform, "Finish here", StepType.Conditional, TrueBranchStepOrder: "end"),
            // The written branch: the writer turns the topic into a few spoken sentences, the avatar's voice speaks them.
            new(AgentType.ScriptwriterAgent, "Write what the person says", AgentInputContextMode: AgentInputContextMode.FullWorkflow, CacheMode: StepCacheMode.Never),
            new(AgentType.VideoTransform, "Speak it in the avatar's voice", StepType.Voiceover,
                VoiceoverConfigJson: """
                    {"source":{"kind":"ScriptScenes","scriptwriterStepOrder":5},"voiceFromAvatar":true}
                    """),
            new(AgentType.VideoTransform, "Make the talking clip", StepType.VideoGenerate,
                VideoGenerateConfigJson: LipSyncClip.Replace("__AUDIO__", "6", StringComparison.Ordinal)),
        ],
        AppManifest: new AppManifest(
            Version: AppManifestVocabulary.Version2,
            Title: "Avatar explainer from a topic",
            Tagline: "A real person from one of your avatars explains a topic, from your topic or your own words.",
            Category: "promo",
            RequestTemplate: "Explain this topic in a few short, plain spoken sentences that take under 15 seconds to say, as a person talking to the camera: {{input.topic}}",
            Inputs:
            [
                new AppManifestInput(AppManifestVocabulary.KindBrief, Field: BriefFieldConsumers.Avatar, Label: "Avatar"),
                Input("ownWords", "toggle", "I will write the words", def: "false",
                    help: "Off: you give a topic and the words are written for you. On: the person says exactly what you type."),
                Input("topic", "longText", "Topic", required: true, maxLength: 1500, showWhen: ("ownWords", "false"),
                    help: "What the person should explain, in a sentence or two.", example: "Why our new autumn menu uses local ingredients"),
                Input("explainerWords", "longText", "What they say", required: true, maxLength: 1500, showWhen: ("ownWords", "true"),
                    help: "Spoken word for word, up to about 15 seconds. A blank line starts a new spoken line.", example: "Hi, I'm Giulia. Our autumn menu is here, and everything on it is grown nearby."),
                new AppManifestInput(AppManifestVocabulary.KindBrief, Field: BriefFieldConsumers.Tone, Label: "Tone", More: false),
                BriefField(BriefFieldConsumers.Language, "Language"),
                Input("clipShape", "shape", "Shape", def: "\"9:16\"", choices: "[\"9:16\",\"16:9\",\"1:1\"]"),
                ModelInput("liteModel", "Lip-sync model", "LipSync"),
                ModelInput("pictureModel", "Picture model", "ImageEdit"),
                AgentModel("scriptModel", "Script model"),
            ],
            Result: new AppManifestResult("video"),
            Advanced: new AppManifestAdvanced(true)),
        AppInputAgents: new Dictionary<string, AgentType>(StringComparer.Ordinal) { ["scriptModel"] = AgentType.ScriptwriterAgent });

    // ---- 3. Music or sound effect ----

    private static WorkflowTemplateDefinition MusicOrSoundEffect() => new(
        Key: MusicOrSoundEffectKey,
        Name: "Music or sound effect",
        Description: "Makes a music track or a sound effect from a short description and saves each take as an audio file in this project. Choose which one you want, describe it, set the length and how many takes to make. Every take is capped in credits. Uses credits.",
        Version: 1,
        AutoCreateOnProject: false,
        RequiresUserInput: false,
        Steps:
        [
            new(AgentType.ExtractTransform, "Music?", StepType.Conditional,
                ConditionExpression: "input.kind == 'Music'", TrueBranchStepOrder: "2", FalseBranchStepOrder: "4"),
            // Music: 3 to 300 s per take; 4 takes of 2 minutes at the dearest list price stay well inside the cap.
            new(AgentType.VideoTransform, "Make the music", StepType.AudioGenerate,
                AudioGenerateConfigJson: """
                    {"kind":"Music","prompt":"{{input.musicPrompt}}","durationSec":30,"takes":1,"maxSpendCredits":1500,"registerProjectFile":true,"appInputs":{"durationSec":"musicLength","takes":"audioTakes","@model":"musicModel"}}
                    """),
            new(AgentType.ExtractTransform, "Finish here", StepType.Conditional, TrueBranchStepOrder: "end"),
            // Sound effect: 0.5 to 30 s per take, a few credits at most.
            new(AgentType.VideoTransform, "Make the sound effect", StepType.AudioGenerate,
                AudioGenerateConfigJson: """
                    {"kind":"SoundEffect","prompt":"{{input.sfxPrompt}}","durationSec":2,"takes":1,"maxSpendCredits":100,"registerProjectFile":true,"appInputs":{"durationSec":"sfxLength","takes":"audioTakes","@model":"sfxModel"}}
                    """),
        ],
        AppManifest: new AppManifest(
            Version: AppManifestVocabulary.Version2,
            Title: "Music or sound effect",
            Tagline: "Describe a music track or a sound effect and get it as an audio file.",
            Category: "utility",
            Inputs:
            [
                Input("kind", "choice", "What to make", def: "\"Music\"",
                    options: [Option("Music", "music", "Music"), Option("SoundEffect", "soundEffect", "Sound effect")]),
                Input("musicPrompt", "longText", "Describe the music", required: true, maxLength: 1000, showWhen: ("kind", "\"Music\""),
                    help: "The mood, the instruments and the pace.", example: "Warm acoustic guitar, easy pace, hopeful"),
                Input("sfxPrompt", "longText", "Describe the sound", required: true, maxLength: 500, showWhen: ("kind", "\"SoundEffect\""),
                    help: "What makes the sound and where it happens.", example: "A wooden door creaking open in a quiet hall"),
                Input("musicLength", "duration", "Length", def: "30", choices: "[15,30,60,120]", showWhen: ("kind", "\"Music\"")),
                Input("sfxLength", "duration", "Length", def: "2", choices: "[1,2,4,8]", showWhen: ("kind", "\"SoundEffect\"")),
                Input("audioTakes", "number", "Takes", min: 1, max: 4, step: 1, unit: "takes", def: "1",
                    help: "How many versions to make and choose from. More takes cost more."),
                ModelInput("musicModel", "Music model", "MusicGeneration", showWhen: ("kind", "\"Music\"")),
                ModelInput("sfxModel", "Sound effect model", "SoundEffectGeneration", showWhen: ("kind", "\"SoundEffect\"")),
            ],
            Result: new AppManifestResult("audio"),
            Advanced: new AppManifestAdvanced(true)));

    // ---- 4. Thumbnail and cover text ----

    private static WorkflowTemplateDefinition Thumbnail() => new(
        Key: ThumbnailKey,
        Name: "Thumbnail and cover text",
        Description: "Picks a cover image from your video, either the best frame found for you or the frame at a time you give, and writes a short line of text on it. Choose your video and the cover text; the result is a picture saved in this project. No credits.",
        Version: 1,
        AutoCreateOnProject: false,
        RequiresUserInput: false,
        Steps:
        [
            // Local analysis only (shots and silence): no transcript and no vision model, so nothing is sent anywhere.
            // An open ProjectFile source is filled with the video chosen in the app.
            new(AgentType.VideoTransform, "Look at the video", StepType.VideoAnalyze,
                VideoAnalyzeConfigJson: """
                    {"version":1,"source":{"kind":"ProjectFile"},"transcription":"Off","vision":"Off"}
                    """),
            new(AgentType.ExtractTransform, "Pick the frame myself?", StepType.Conditional,
                ConditionExpression: "input.moment == 'AtTime'", TrueBranchStepOrder: "5", FalseBranchStepOrder: "3"),
            new(AgentType.VideoTransform, "Choose the best frame", StepType.ThumbnailGenerate,
                ThumbnailGenerateConfigJson: """
                    {"version":1,"analysisStepOrder":1,"selection":"Auto","registerProjectFile":true,"overlayText":"{{input.coverText}}","overlayPosition":"LowerThird"}
                    """),
            new(AgentType.ExtractTransform, "Finish here", StepType.Conditional, TrueBranchStepOrder: "end"),
            new(AgentType.VideoTransform, "Take the frame at that time", StepType.ThumbnailGenerate,
                ThumbnailGenerateConfigJson: """
                    {"version":1,"analysisStepOrder":1,"selection":"AtTime","atTimeSec":0,"registerProjectFile":true,"overlayText":"{{input.coverText}}","overlayPosition":"LowerThird","appInputs":{"atTimeSec":"atTime"}}
                    """),
        ],
        AppManifest: new AppManifest(
            Version: AppManifestVocabulary.Version2,
            Title: "Thumbnail and cover text",
            Tagline: "A cover picture from your video, with a line of text on it.",
            Category: "utility",
            Inputs:
            [
                new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "video", Label: "Your video", Min: 1, Max: 1),
                Input("moment", "choice", "Which frame", def: "\"Auto\"",
                    options: [Option("Auto", "auto", "Pick the best one"), Option("AtTime", "atTime", "Use a time I choose")]),
                Input("atTime", "number", "Time in the video", min: 0, max: 3600, step: 1, unit: "seconds", def: "0", showWhen: ("moment", "\"AtTime\""),
                    help: "The second of the video to use as the cover."),
                Input("coverText", "shortText", "Cover text", maxLength: 90,
                    help: "A short line written on the cover. Leave it empty for a picture only.", example: "Our autumn menu"),
            ],
            Result: new AppManifestResult("image"),
            Advanced: new AppManifestAdvanced(true)));

    // ---- 5. Read my text aloud ----

    private static WorkflowTemplateDefinition ReadAloud() => new(
        Key: ReadAloudKey,
        Name: "Read my text aloud",
        Description: "Turns your text into a spoken audio file with the voice and language you choose, word for word, and saves it in this project. A blank line starts a new spoken paragraph. Uses credits and a voice.",
        Version: 1,
        AutoCreateOnProject: false,
        RequiresUserInput: false,
        Steps:
        [
            new(AgentType.VideoTransform, "Read the text aloud", StepType.Voiceover,
                VoiceoverConfigJson: """
                    {"source":{"kind":"Inline","lines":[{"text":"Hello!","startSec":0}]},"appInputs":{"@spokenText":"textToSpeak","@model":"voiceModel"}}
                    """),
        ],
        AppManifest: new AppManifest(
            Version: AppManifestVocabulary.Version2,
            Title: "Read my text aloud",
            Tagline: "Your text, read aloud in the voice and language you pick.",
            Category: "utility",
            Inputs:
            [
                Input("textToSpeak", "longText", "Text to read", required: true, maxLength: 5000,
                    help: "Spoken word for word. A blank line starts a new paragraph.", example: "Welcome to our shop. We are open every day from nine to six."),
                BriefField(BriefFieldConsumers.Voice, "Voice"),
                BriefField(BriefFieldConsumers.Language, "Language"),
                ModelInput("voiceModel", "Voice model", "SpeechSynthesis"),
            ],
            Result: new AppManifestResult("audio"),
            Advanced: new AppManifestAdvanced(true)));

    // ---- 6. AI video clip from your idea ----

    private static WorkflowTemplateDefinition AiClip() => new(
        Key: AiClipKey,
        Name: "AI video clip from your idea",
        Description: "Describes a scene and gets a short AI-generated video clip of it, with no people from your project. Pick the effort: a quick draft costs the least, the best of two makes two versions to choose from. The cost of every run is capped. Uses credits.",
        Version: 1,
        AutoCreateOnProject: false,
        RequiresUserInput: false,
        Steps:
        [
            // The effort choice writes two settings at once (length and takes). The cap covers the dearest choice: two
            // 10 s takes at the list price of the default model.
            new(AgentType.VideoTransform, "Make the clip", StepType.VideoGenerate,
                VideoGenerateConfigJson: """
                    {"planSource":"Inline","inlinePrompt":"{{input.idea}} {{input.mood}}","model":"MiniMax-H3","resolution":"768P","durationSeconds":6,"ratio":"16:9","takes":1,"maxClips":2,"maxSpendCredits":800,"timeoutSeconds":1800,"allowSourceMediaEgress":false,"appInputs":{"ratio":"clipShape","@model":"videoModel","durationSeconds":{"input":"effort","values":{"draft":6,"standard":10,"bestOfTwo":10}},"takes":{"input":"effort","values":{"draft":1,"standard":1,"bestOfTwo":2}}}}
                    """),
        ],
        AppManifest: new AppManifest(
            Version: AppManifestVocabulary.Version2,
            Title: "AI video clip from your idea",
            Tagline: "Describe a scene and get a short AI video clip of it.",
            Category: "promo",
            Inputs:
            [
                Input("idea", "longText", "Your idea", required: true, maxLength: 1500,
                    help: "What happens in the clip, in a sentence or two.", example: "Steam rising from a fresh espresso on a wooden counter, morning light"),
                Input("mood", "shortText", "Mood and look", maxLength: 120,
                    help: "Optional: colours, light and pace.", example: "warm, golden, slow"),
                Input("effort", "choice", "How much effort", def: "\"standard\"",
                    help: "Draft is the cheapest. Best of two makes two 10-second versions.",
                    options: [Option("draft", "draft", "Draft"), Option("standard", "standard", "Standard"), Option("bestOfTwo", "bestOfTwo", "Best of two")]),
                Input("clipShape", "shape", "Shape", def: "\"16:9\"", choices: "[\"9:16\",\"16:9\",\"1:1\"]"),
                ModelInput("videoModel", "Video model", "VideoGeneration"),
            ],
            Result: new AppManifestResult("video"),
            Advanced: new AppManifestAdvanced(true)));
}
