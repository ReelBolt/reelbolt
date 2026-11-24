using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The app-input edition of the built-in templates (docs/design/app-inputs.md 12.2 and 9.5, lane L7, as built in 22): the
/// few settings a person changes per run become app inputs with plain labels and an example, fine-tuning and model choices
/// sit under "More options", and the two avatar templates become one, <see cref="WorkflowTemplateCatalog.AvatarClipTemplateKey"/>,
/// with a "Lite mode (cheaper)" switch. Served by <see cref="WorkflowTemplateCatalog.GetAll()"/>. Pure and built once.
/// <para>
/// A template whose steps change here (a placeholder or an <c>appInputs</c> binding in a config) has its
/// <see cref="WorkflowTemplateDefinition.Version"/> bumped once, so a project that already has the old version keeps it
/// and gets the new one as a separate workflow; a template that gains only manifest inputs keeps its version. Labels, help
/// and examples carry catalogue keys (<c>recipes.inputs.&lt;id&gt;.label|help|example</c> in <c>web/messages/*/apps.json</c>)
/// with the English words as the fallback.
/// </para>
/// </summary>
public static class WorkflowTemplateAppInputs
{
    /// <summary>The prefix of every catalogue key a curated recipe input names (resolved in the web's <c>apps</c> namespace).</summary>
    public const string KeyPrefix = "recipes.inputs.";

    /// <summary>
    /// The manifest defaults applied when a hidden template's key is used to create its replacement (9.5): asking for the
    /// budget avatar template creates "Avatar clip" with Lite mode on.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>> SupersedeDefaults =
        new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>(StringComparer.Ordinal)
        {
            [WorkflowTemplateCatalog.SpokespersonClipTemplateKey] = new Dictionary<string, JsonElement> { ["liteMode"] = J("false") },
            [WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey] = new Dictionary<string, JsonElement> { ["liteMode"] = J("true") },
        }.Concat(VideoAppTemplates.AliasDefaults).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    private delegate WorkflowTemplateDefinition Edit(WorkflowTemplateDefinition template);

    private static readonly IReadOnlyDictionary<string, Edit> Edits = new Dictionary<string, Edit>(StringComparer.Ordinal)
    {
        [WorkflowTemplateCatalog.StarterTemplateKey] = Promo,
        ["lean-context-promo"] = Promo,
        [WorkflowTemplateCatalog.QuickWinPromoDsTemplateKey] = Promo,
        ["quick-win-promo-plates"] = Plates,
        [WorkflowTemplateCatalog.QuickWinPromoPlatesDsTemplateKey] = Plates,
        ["cinematic-feature-spotlight"] = t => Audience(t, captions: false),
        ["story-arc-launch-trailer"] = t => Audience(t, captions: false),
        [WorkflowTemplateCatalog.NarratedStoryTemplateKey] = t => Ending(Audience(t, captions: true)),
        ["video-derush-edit-voiceover"] = t => Ending(Captions(t)),
        ["video-derush-edit-dub"] = t => Ending(Captions(t)),
        ["video-derush-edit-music"] = t => Ending(MusicVolume(t)),
        ["video-derush-edit-broll"] = t => Ending(GeneratedShots(t, takes: false)),
        ["video-derush-edit-dailies"] = t => Ending(GeneratedShots(t, takes: true)),
        // Follow-up F1 (docs/design/app-inputs.md 27): the plain edit family and the testimonial reel get a captions switch
        // (captions from the footage's own speech), and every recipe that ships the 800 ms ending fade gets an "Ending fade"
        // switch whose "on" writes exactly what the recipe ships.
        ["video-derush-edit"] = t => Ending(FootageCaptions(t)),
        ["video-derush-edit-room"] = t => Ending(FootageCaptions(t)),
        ["video-derush-edit-graphics"] = t => Ending(FootageCaptions(t)),
        ["video-derush-edit-graphics-room"] = t => Ending(FootageCaptions(t)),
        ["video-derush-edit-grade-room"] = t => Ending(FootageCaptions(t)),
        ["video-derush-edit-sfx"] = t => Ending(FootageCaptions(t)),
        [WorkflowTemplateCatalog.TestimonialReelTemplateKey] = t => Ending(FootageCaptions(t)),
        [WorkflowTemplateCatalog.RegionEffectTemplateKey] = RegionEffect,
        [WorkflowTemplateCatalog.SoundEffectsTemplateKey] = SoundEffects,
        [WorkflowTemplateCatalog.SubtitlesTemplateKey] = CaptionLook,
        [WorkflowTemplateCatalog.SubtitlesTranslatedTemplateKey] = CaptionLook,
    };

    /// <summary>The app-input edition of <paramref name="templates"/> (the catalog's own list, review loops already set).</summary>
    public static IReadOnlyList<WorkflowTemplateDefinition> Edition(IReadOnlyList<WorkflowTemplateDefinition> templates)
    {
        List<WorkflowTemplateDefinition> edition = [];
        foreach (WorkflowTemplateDefinition template in templates)
        {
            if (template.Key is WorkflowTemplateCatalog.SpokespersonClipTemplateKey or WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey)
            {
                // Decision 3 (O3): kept unchanged for the workflows made from them, but never listed again.
                edition.Add(template with { Hidden = true, SupersededBy = WorkflowTemplateCatalog.AvatarClipTemplateKey });
                if (template.Key == WorkflowTemplateCatalog.TalkingAvatarBudgetTemplateKey)
                    edition.Add(AvatarClip());
                continue;
            }

            WorkflowTemplateDefinition served = Edits.TryGetValue(template.Key, out Edit? edit) ? Bump(template, edit(template)) : template;
            // The footage keys open one of the three video apps; the template itself stays for the workflows made from it.
            edition.Add(VideoAppTemplates.AppFor(template.Key) is { } app ? served with { Hidden = true, SupersededBy = app } : served);
        }

        // The preset recipes (WorkflowTemplatePresets): new built-ins that show off the dynamic inputs, listed after the rest.
        edition.AddRange(WorkflowTemplatePresets.Build(templates));
        edition.AddRange(VideoAppTemplates.Build());
        return edition;
    }

    /// <summary>
    /// The manifest a template is created with: its own, with the defaults of <see cref="SupersedeDefaults"/> for the key the
    /// person asked for, and each agent-model input given the agent it picks for (<paramref name="agentIdOf"/>).
    /// </summary>
    public static AppManifest? ManifestForCreate(WorkflowTemplateDefinition template, string requestedKey, Func<AgentType, Guid?> agentIdOf)
    {
        if (template.AppManifest is not { Inputs: { } inputs } manifest)
            return template.AppManifest;
        SupersedeDefaults.TryGetValue(requestedKey, out IReadOnlyDictionary<string, JsonElement>? defaults);
        List<AppManifestInput> resolved = [];
        foreach (AppManifestInput input in inputs)
        {
            AppManifestInput next = input;
            if (input.Id is { } id && defaults is not null && defaults.TryGetValue(id, out JsonElement value))
                next = next with { Default = value };
            if (input.Id is { } agentInput && template.AppInputAgents is { } agents && agents.TryGetValue(agentInput, out AgentType type))
                next = next with { Agent = agentIdOf(type) };
            resolved.Add(next);
        }

        return manifest with { Inputs = resolved };
    }

    // ---- the recipes ----

    private static WorkflowTemplateDefinition Promo(WorkflowTemplateDefinition t) => t with
    {
        AppManifest = WithInputs(t, PromoRequest, [Tone(), CallToAction(), AgentModel("scriptModel", "Script model"), AgentModel("builderModel", "Video builder model")]),
        AppInputAgents = PromoAgents,
    };

    private static WorkflowTemplateDefinition Plates(WorkflowTemplateDefinition t)
    {
        WorkflowTemplateDefinition patched = PatchFirst(t, StepType.VideoGenerate, config =>
        {
            config["inlinePrompt"] = "Abstract, slowly drifting soft-focus gradient background plate in {{input.backgroundLook}}, with gentle light leaks and subtle particle motion. No text, no logos, no people, no recognizable objects. Loopable.";
            config["appInputs"] = new JsonObject { ["@model"] = "backgroundModel" };
        });
        return patched with
        {
            AppManifest = WithInputs(t, PromoRequest,
            [
                Tone(),
                CallToAction(),
                Input("backgroundLook", "shortText", "Background look", maxLength: 120, def: "\"calm, premium colours\"",
                    help: "The colours and mood of the soft moving backgrounds.", example: "deep blue and violet, calm"),
                ModelInput("backgroundModel", "AI background model", "VideoGeneration"),
                AgentModel("scriptModel", "Script model"),
                AgentModel("builderModel", "Video builder model"),
            ]),
            AppInputAgents = PromoAgents,
        };
    }

    private static WorkflowTemplateDefinition Audience(WorkflowTemplateDefinition t, bool captions)
    {
        WorkflowTemplateDefinition patched = captions ? BindCaptions(t) : t;
        List<AppManifestInput> extra = [Input("audience", "shortText", "Who it is for", maxLength: 120,
            help: "The people the video speaks to.", example: "busy café owners")];
        if (captions)
            extra.Add(CaptionsToggle());
        return patched with { AppManifest = WithInputs(t, "Made for this audience: {{input.audience}}", extra) };
    }

    private static WorkflowTemplateDefinition Captions(WorkflowTemplateDefinition t) =>
        BindCaptions(t) with { AppManifest = WithInputs(t, null, [CaptionsToggle()]) };

    /// <summary>
    /// Sound effects on a finished video: the analysis looks at the picture (when the workspace has a Vision model), so
    /// the page offers, under "More options", which model looks, how many looks it may take (empty = the length of the
    /// video decides, 0 = none) and whether filmed footage gets sounds too.
    /// </summary>
    private static WorkflowTemplateDefinition SoundEffects(WorkflowTemplateDefinition t) =>
        PatchFirst(t, StepType.VideoAnalyze, config => config["appInputs"] = new JsonObject
        {
            [AppBindableCatalog.VisionModelKey] = "lookModel",
            ["maxDescribedEvents"] = "mostLooks",
            ["sfxOnFootage"] = "footageSounds",
        }) with
        {
            AppManifest = WithInputs(t, null,
            [
                Input("footageSounds", "toggle", "Sounds on filmed footage too", def: "false", more: true,
                    help: "Off: sounds go on designed moments, not on plain cuts between filmed shots."),
                ModelInput("lookModel", "Model that looks at the picture", "Vision"),
                Input("mostLooks", "number", "Most looks at the picture", more: true, min: 0, max: VideoEvents.VisionLookDefaults.MaxEventLooks, step: 1,
                    help: "Leave it empty and the length of the video decides. 0 switches the looks off. When looks are on, the sound designer may ask for up to 6 more."),
            ]),
        };

    private static WorkflowTemplateDefinition MusicVolume(WorkflowTemplateDefinition t) =>
        PatchFirst(t, StepType.VideoCompile, config => config["appInputs"] = new JsonObject { ["musicGainDb"] = "musicVolume" }) with
        {
            // No default: left empty, the editor sets the level from the music plan, exactly as before.
            AppManifest = WithInputs(t, null,
            [
                Input("musicVolume", "choice", "Music volume", more: true, help: "Leave it empty to let the editor choose.",
                    options: [Option(-24, "quiet", "Quiet"), Option(-16, "balanced", "Balanced"), Option(-10, "loud", "Loud")]),
            ]),
        };

    private static WorkflowTemplateDefinition GeneratedShots(WorkflowTemplateDefinition t, bool takes)
    {
        JsonObject map = new() { ["@model"] = "videoModel" };
        if (takes)
            map["takes"] = "takesPerShot";
        List<AppManifestInput> extra = [];
        if (takes)
        {
            extra.Add(Input("takesPerShot", "number", "Takes per shot", more: true, min: 1, max: 2, step: 1, unit: "takes", def: "2",
                help: "How many versions of each extra shot to make and choose from. More takes cost more."));
        }

        // The most extra shots: the planner is told the number (request sentence) and, where one clip is one shot, the same
        // input is the step's clip ceiling (maxClips). With several takes a shot is more than one clip, so only the sentence.
        // The credits cap stays the author's; the run page and the hold price what the film can cost.
        if (!takes)
            map["maxClips"] = "mostShots";
        // The credits cap: a plain, always-shown number whose default is the cap the recipe ships (rounded up to whole credits).
        string credits = Math.Ceiling(t.Steps.Select(s => s.VideoGenerateConfigJson).Where(j => j is not null)
            .Select(j => JsonNode.Parse(j!)?["maxSpendCredits"]?.GetValue<double>() ?? 0).FirstOrDefault(c => c > 0)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        map["maxSpendCredits"] = "mostCredits";
        extra.Add(Input("mostCredits", "number", "Most credits to spend", min: 1, max: 100000, step: 1, def: credits,
            help: "The most credits the extra AI shots may cost this run. The estimate shows what it will cost."));
        extra.Add(Input("mostShots", "number", "Most extra shots", more: true, min: 1, max: 4, step: 1, def: "4",
            help: "The most extra AI shots to plan and make. Fewer shots cost less."));
        extra.Add(ModelInput("videoModel", "Video model", "VideoGeneration"));
        return PatchFirst(t, StepType.VideoGenerate, config => config["appInputs"] = map) with
        {
            AppManifest = WithInputs(t, "Plan at most {{input.mostShots}} extra AI shots.", extra)
        };
    }

    private static WorkflowTemplateDefinition RegionEffect(WorkflowTemplateDefinition t)
    {
        // One text, written into both the object the analysis follows and the object the effect hides, so they always match.
        WorkflowTemplateDefinition patched = PatchFirst(t, StepType.VideoAnalyze, config => config["trackObjects"]![0]!["label"] = "{{input.whatToHide}}");
        patched = PatchFirst(patched, StepType.VideoCompile, config =>
        {
            config["regionEffects"]![0]!["target"] = "{{input.whatToHide}}";
            // F1: the effect itself, a nested whole value (docs/design/app-inputs.md 27); only the two effects that hide.
            config["appInputs"] = new JsonObject { ["regionEffects[0].effect"] = "hideWith" };
        });
        return patched with
        {
            AppManifest = WithInputs(t, null,
            [
                Input("whatToHide", "shortText", "What to hide", required: true, maxLength: 120,
                    help: "Name the object in plain words. It is followed through the whole clip.", example: "the car's number plate"),
                Input("hideWith", "choice", "How to hide it", def: "\"Blur\"",
                    options: [Option("Blur", "blur", "Blur"), Option("Pixelate", "pixelate", "Pixelate")]),
            ]),
        };
    }

    /// <summary>
    /// F1: captions from the footage's own speech on an edit that has no narration (docs/design/app-inputs.md 27). One switch,
    /// off by default (today's film), sets three settings: the analysis asks for the transcript (<c>Required</c>, so a missing
    /// transcription model stops the run at the first step), and the last compile (the one that encodes) burns captions cut
    /// from it. Off, none of the three moves.
    /// </summary>
    private static WorkflowTemplateDefinition FootageCaptions(WorkflowTemplateDefinition t)
    {
        WorkflowTemplateDefinition patched = PatchFirst(t, StepType.VideoAnalyze, config =>
            Map(config)["transcription"] = Mapped("footageCaptions", ("true", "Required")));
        patched = PatchLast(patched, StepType.VideoCompile, config =>
        {
            JsonObject map = Map(config);
            map["enableCaptions"] = "footageCaptions";
            map["captionSource"] = Mapped("footageCaptions", ("true", "Transcript"));
        });
        return patched with
        {
            AppManifest = AppendInputs(patched,
            [
                Input("footageCaptions", "toggle", "Captions", def: "false", help: "Shows the words spoken in your footage on screen."),
            ]),
        };
    }

    /// <summary>
    /// F1: the "Ending fade" switch (docs/design/app-inputs.md 27). On (the default) writes exactly what the recipe ships (the
    /// picture's 800 ms and the sound's own fade), off writes 0, on every compile that carries the fade.
    /// </summary>
    private static WorkflowTemplateDefinition Ending(WorkflowTemplateDefinition t)
    {
        List<WorkflowTemplateStepDefinition> steps = [.. t.Steps];
        bool any = false;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i].StepType != StepType.VideoCompile
                || JsonNode.Parse(steps[i].VideoCompileConfigJson ?? "{}") is not JsonObject config
                || config["programFadeOutMs"]?.GetValue<int>() is not { } picture || picture <= 0)
                continue;
            JsonObject map = Map(config);
            map["programFadeOutMs"] = Mapped("ending", ("true", picture), ("false", 0));
            if (config["programAudioFadeOutMs"]?.GetValue<int>() is { } sound && sound > 0)
                map["programAudioFadeOutMs"] = Mapped("ending", ("true", sound), ("false", 0));
            steps[i] = steps[i] with { VideoCompileConfigJson = config.ToJsonString() };
            any = true;
        }

        if (!any)
            return t;
        WorkflowTemplateDefinition patched = t with { Steps = steps };
        return patched with
        {
            AppManifest = AppendInputs(patched,
            [
                Input("ending", "toggle", "Ending fade", def: "true", more: true, help: "Fades the picture and the sound out at the very end."),
            ]),
        };
    }

    private static WorkflowTemplateDefinition CaptionLook(WorkflowTemplateDefinition t) =>
        PatchFirst(t, StepType.VideoCompile, config => config["appInputs"] = new JsonObject { ["captionStyle"] = "captionLook" }) with
        {
            AppManifest = WithInputs(t, null,
            [
                Input("captionLook", "choice", "Caption look", def: "\"Subtitle\"",
                    options: [Option("Subtitle", "subtitle", "Plain"), Option("Karaoke", "karaoke", "Word by word"), Option("Punch", "punch", "One big word")]),
            ]),
        };

    /// <summary>
    /// The merged avatar recipe (9.5): a Lite switch, then the still-then-animate branch or the spokesperson branch. Version 2
    /// (F1, docs/design/app-inputs.md 27): the Lite branch speaks "What they say" exactly as written (the Voiceover's own lines,
    /// <c>@spokenText</c>), so version 1's writer step is gone, and the still's picture model is a choice under More options.
    /// </summary>
    private static WorkflowTemplateDefinition AvatarClip() => new(
        Key: WorkflowTemplateCatalog.AvatarClipTemplateKey,
        Name: "Avatar clip",
        Description: "A short AI video clip of a real person from one of this project's avatars (someone who gave their consent). Choose the avatar, say what they do, or switch on Lite mode and write what they say: Lite mode is cheaper and animates one still picture of the person speaking your words, word for word, in their own linked voice. Allow the clip step to send media in the app. Every clip is marked as AI-generated. Uses credits.",
        Version: 2,
        AutoCreateOnProject: false,
        RequiresUserInput: false,
        Steps:
        [
            new(AgentType.ExtractTransform, "Lite mode?", StepType.Conditional,
                ConditionExpression: "input.liteMode", TrueBranchStepOrder: "2", FalseBranchStepOrder: "5"),
            // The Lite branch: the person's own words, spoken as written (one line per paragraph, back to back from 0 s).
            new(AgentType.VideoTransform, "Say the words in the avatar's voice", StepType.Voiceover,
                VoiceoverConfigJson: """
                    {"source":{"kind":"Inline","lines":[{"text":"Hello!","startSec":0}]},"voiceFromAvatar":true,"appInputs":{"@spokenText":"whatTheySay"}}
                    """),
            new(AgentType.VideoTransform, "Make the talking clip", StepType.VideoGenerate,
                // The narration of step 2 drives the length (at most 15 s), so the length is not asked; the shape, the
                // lip-sync model and the still's picture model are. The permission to send media ships off (WL015, set only
                // in the app).
                VideoGenerateConfigJson: """
                    {"planSource":"Inline","inlinePrompt":"A bright, softly lit studio with a plain light background; the person in a smart casual top, framed from the chest up.","model":"minimax/h3-max/lip-sync/image-to-video","resolution":"768P","durationSeconds":15,"ratio":"9:16","takes":1,"maxClips":1,"maxSpendCredits":250,"timeoutSeconds":1200,"avatarAudio":{"from":"Step","stepOrder":2},"allowSourceMediaEgress":false,"appInputs":{"ratio":"clipShape","@model":"liteModel","@stillModel":"pictureModel"},"placeholders":{"avatar":"choose the avatar in the app, then allow sending media for this step in the app","provider":"needs a fal.ai Lip-sync model (default: minimax/h3-max/lip-sync/image-to-video) and a fal.ai ImageEdit model (google/nano-banana-2.1/edit) as the defaults"}}
                    """),
            new(AgentType.ExtractTransform, "Finish here", StepType.Conditional, TrueBranchStepOrder: "end"),
            // The full branch: today's "Spokesperson clip" step. The cap covers a 10 s take plus the most input material
            // the avatar limits allow on MiniMax-H3 at 768P (it was 400 for the 6 s take).
            new(AgentType.VideoTransform, "Make the spokesperson clip", StepType.VideoGenerate,
                VideoGenerateConfigJson: """
                    {"planSource":"Inline","inlinePrompt":"{{input.whatTheyDo}} Medium close-up, steady camera.","model":"MiniMax-H3","resolution":"768P","durationSeconds":6,"ratio":"9:16","takes":1,"maxClips":1,"maxSpendCredits":460,"allowSourceMediaEgress":false,"appInputs":{"durationSeconds":"clipLength","ratio":"clipShape","@model":"videoModel"},"placeholders":{"avatar":"choose the avatar in the app, then allow sending media for this step in the app"}}
                    """),
        ],
        AppManifest: new AppManifest(
            Version: AppManifestVocabulary.Version2,
            Title: "Avatar clip",
            Tagline: "A short clip of a real person from one of your avatars, doing or saying what you write.",
            Category: "promo",
            Inputs:
            [
                new AppManifestInput(AppManifestVocabulary.KindBrief, Field: BriefFieldConsumers.Avatar, Label: "Avatar"),
                Input("liteMode", "toggle", "Lite mode (cheaper)", def: "false",
                    help: "Cheaper: one still picture of the person, animated to speak your words."),
                Input("whatTheyDo", "longText", "What they do", required: true, maxLength: 1500, showWhen: ("liteMode", "false"),
                    example: "Greets the viewer warmly in a bright studio and looks into the camera"),
                Input("whatTheySay", "longText", "What they say", required: true, maxLength: 1500, showWhen: ("liteMode", "true"),
                    help: "Spoken word for word, up to about 15 seconds. A blank line starts a new spoken line.", example: "Welcome to Caffè Lume! Our new autumn menu starts today."),
                Input("clipShape", "shape", "Shape", def: "\"9:16\"", choices: "[\"9:16\",\"16:9\",\"1:1\"]"),
                Input("clipLength", "duration", "Length", def: "6", choices: "[6,10]", showWhen: ("liteMode", "false")),
                ModelInput("videoModel", "Video model", "VideoGeneration", showWhen: ("liteMode", "false")),
                ModelInput("liteModel", "Lip-sync model", "LipSync", showWhen: ("liteMode", "true")),
                ModelInput("pictureModel", "Picture model", "ImageEdit", showWhen: ("liteMode", "true")),
            ],
            Result: new AppManifestResult("video"),
            Advanced: new AppManifestAdvanced(true)));

    // ---- shared pieces ----

    private const string PromoRequest = "End the video with this call to action: {{input.callToAction}}";

    private static readonly IReadOnlyDictionary<string, AgentType> PromoAgents = new Dictionary<string, AgentType>(StringComparer.Ordinal)
    {
        ["scriptModel"] = AgentType.ScriptwriterAgent,
        ["builderModel"] = AgentType.AuthorAgent,
    };

    /// <summary>The brief's tone, which the script writer reads: the promo family had no way to ask for a voice of the video.</summary>
    internal static AppManifestInput Tone() =>
        new(AppManifestVocabulary.KindBrief, Field: BriefFieldConsumers.Tone, Label: "Tone");

    internal static AppManifestInput CallToAction() =>
        Input("callToAction", "shortText", "Call to action", maxLength: 120,
            help: "The last thing the video asks people to do.", example: "Try it free today");

    private static AppManifestInput CaptionsToggle() =>
        Input("captions", "toggle", "Captions", def: "true", help: "Shows the spoken words on screen.");

    private static WorkflowTemplateDefinition BindCaptions(WorkflowTemplateDefinition t) =>
        PatchFirst(t, StepType.VideoCompile, config => config["appInputs"] = new JsonObject { ["enableCaptions"] = "captions" });

    /// <summary>A model choice under "More options"; the job is derived from the step it binds (8.1), the default is the recommended one.</summary>
    internal static AppManifestInput ModelInput(string id, string label, string capability, (string Input, string Value)? showWhen = null) =>
        Input(id, "model", label, more: true, capability: capability, showWhen: showWhen);

    /// <summary>The Chat model of the agent named in the template's <see cref="WorkflowTemplateDefinition.AppInputAgents"/>.</summary>
    internal static AppManifestInput AgentModel(string id, string label) => Input(id, "model", label, more: true, capability: "Chat");

    internal static AppManifestInput Input(
        string id, string type, string label, bool required = false, int? maxLength = null, string? def = null, string? help = null,
        string? example = null, bool? more = null, double? min = null, double? max = null, double? step = null, string? unit = null,
        string? choices = null, IReadOnlyList<AppInputOption>? options = null, string? capability = null,
        (string Input, string Value)? showWhen = null) =>
        new(
            AppManifestVocabulary.KindInput,
            Label: label,
            Default: def is null ? null : J(def),
            Choices: choices is null ? null : JsonSerializer.Deserialize<List<JsonElement>>(choices),
            Min: min,
            Max: max,
            Id: id,
            Type: type,
            Help: help,
            Example: example,
            Required: required ? true : null,
            MaxLength: maxLength,
            Step: step,
            Unit: unit,
            Options: options,
            More: more,
            ShowWhen: showWhen is { } sw ? new AppInputShowWhen(sw.Input, J(sw.Value)) : null,
            Capability: capability,
            LabelKey: $"{KeyPrefix}{id}.label",
            HelpKey: help is null ? null : $"{KeyPrefix}{id}.help",
            ExampleKey: example is null ? null : $"{KeyPrefix}{id}.example");

    internal static AppInputOption Option(string value, string key, string label) =>
        new(J(JsonSerializer.Serialize(value)), label, LabelKey: $"{KeyPrefix}options.{key}");

    internal static AppInputOption Option(double value, string key, string label) =>
        new(J(JsonSerializer.Serialize(value)), label, LabelKey: $"{KeyPrefix}options.{key}");

    /// <summary>The step's <c>appInputs</c> map, made when the config has none.</summary>
    private static JsonObject Map(JsonObject config)
    {
        if (config["appInputs"] is JsonObject map)
            return map;
        map = [];
        config["appInputs"] = map;
        return map;
    }

    /// <summary>A mapped binding (docs/design/app-inputs.md 27): what each listed value of the input writes into the setting.</summary>
    internal static JsonObject Mapped(string inputId, params (string Value, JsonNode? Writes)[] values)
    {
        JsonObject listed = [];
        foreach ((string value, JsonNode? writes) in values)
            listed[value] = writes;
        return new JsonObject { ["input"] = inputId, ["values"] = listed };
    }

    /// <summary>The template's manifest (its own, else the derived one) as version 2 with <paramref name="extra"/> appended; the request template is kept.</summary>
    private static AppManifest AppendInputs(WorkflowTemplateDefinition t, IReadOnlyList<AppManifestInput> extra)
    {
        AppManifest baseManifest = t.AppManifest ?? AppManifestDeriver.Derive(AppStepFacts.FromTemplateIgnoringSwitch(t), t.RequiresUserInput);
        return baseManifest with { Version = AppManifestVocabulary.Version2, Inputs = [.. baseManifest.Inputs ?? [], .. extra] };
    }

    /// <summary>The template's manifest (its curated one, else the one its steps derive) as version 2 with <paramref name="extra"/> appended.</summary>
    private static AppManifest WithInputs(WorkflowTemplateDefinition t, string? requestTemplate, IReadOnlyList<AppManifestInput> extra)
    {
        AppManifest baseManifest = t.AppManifest ?? AppManifestDeriver.Derive(AppStepFacts.FromTemplateIgnoringSwitch(t), t.RequiresUserInput);
        return baseManifest with
        {
            Version = AppManifestVocabulary.Version2,
            Inputs = [.. baseManifest.Inputs ?? [], .. extra],
            RequestTemplate = requestTemplate,
        };
    }

    /// <summary>Edits the config of the first step of <paramref name="type"/> (parsed JSON, never text).</summary>
    private static WorkflowTemplateDefinition PatchFirst(WorkflowTemplateDefinition t, StepType type, Action<JsonObject> edit) =>
        Patch(t, t.Steps.ToList().FindIndex(s => s.StepType == type), type, edit);

    /// <summary>Edits the config of the last step of <paramref name="type"/> (the encoding compile of an encode-once recipe).</summary>
    private static WorkflowTemplateDefinition PatchLast(WorkflowTemplateDefinition t, StepType type, Action<JsonObject> edit) =>
        Patch(t, t.Steps.ToList().FindLastIndex(s => s.StepType == type), type, edit);

    private static WorkflowTemplateDefinition Patch(WorkflowTemplateDefinition t, int index, StepType type, Action<JsonObject> edit)
    {
        if (index < 0)
            throw new InvalidOperationException($"Template {t.Key} has no {type} step to bind.");
        WorkflowTemplateStepDefinition step = t.Steps[index];
        JsonObject config = JsonNode.Parse(AppBindingStep.ConfigJsonOf(type, step) ?? "{}")!.AsObject();
        edit(config);
        string json = config.ToJsonString();
        WorkflowTemplateStepDefinition next = type switch
        {
            StepType.VideoGenerate => step with { VideoGenerateConfigJson = json },
            StepType.VideoCompile => step with { VideoCompileConfigJson = json },
            StepType.VideoAnalyze => step with { VideoAnalyzeConfigJson = json },
            _ => throw new InvalidOperationException($"No binding is written into a {type} step here.")
        };
        List<WorkflowTemplateStepDefinition> steps = [.. t.Steps];
        steps[index] = next;
        return t with { Steps = steps };
    }

    /// <summary>A template whose steps changed gets the next version (a manifest alone never bumps it).</summary>
    private static WorkflowTemplateDefinition Bump(WorkflowTemplateDefinition before, WorkflowTemplateDefinition after) =>
        before.Steps.SequenceEqual(after.Steps) ? after : after with { Version = before.Version + 1 };

    internal static JsonElement J(string raw) => JsonDocument.Parse(raw).RootElement.Clone();
}
