using System.Text.Json;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Tenancy;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>One recipe (built-in template) as the assistant reads it: what it is for, what it needs and what it reads.</summary>
public sealed record RecipeFitInfo(
    string Key,
    string Title,
    string? Tagline,
    string Category,
    string CostClass,
    IReadOnlyList<string> ForGoals,
    IReadOnlyList<string> Needs,
    IReadOnlyList<string> BriefFieldsRead,
    string? SuggestedStylePreset,
    bool RequiresProfile);

/// <summary>
/// The fit of recipes to a goal and to a project (docs/design/workflow-apps.md 6.1 step 3), computed from the same
/// facts the gallery uses (<see cref="ProjectReadinessFacts.Evaluate"/>) so the assistant and the app page cannot
/// disagree. Pure: no database, no model.
/// </summary>
public static class RecipeFit
{
    // What each recipe is for, in the words a person states a goal in. A key with no entry falls back to its tagline.
    private static readonly IReadOnlyDictionary<string, string[]> Goals = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["quick-win-promo"] = ["promo video of my app from its code", "app demo video", "product promo", "marketing video from source code"],
        ["quick-win-promo-plates"] = ["promo video of my app with AI backgrounds", "promo with generated background clips"],
        ["lean-context-promo"] = ["promo video from a large codebase", "cheaper faster promo from source code"],
        ["quick-win-promo-ds"] = ["promo video in my brand", "promo from my brand profile", "on-brand app video"],
        ["quick-win-promo-plates-ds"] = ["on-brand promo with AI backgrounds"],
        ["cinematic-feature-spotlight"] = ["vertical social reel", "short narrated feature spotlight", "tiktok reel", "instagram reel", "short vertical video"],
        ["story-arc-launch-trailer"] = ["launch trailer", "narrated product trailer", "teaser video"],
        ["narrated-story"] = ["narrated product story from my clips", "voiceover over my footage", "explainer from clips"],
        ["video-derush-edit"] = ["highlight edit from my footage", "cut my clips down", "remove dead air and false starts", "edit raw footage"],
        ["testimonial-reel"] = ["testimonial reel from a talking-head clip and a screenshot", "show a review or testimonial screenshot over the interview", "customer quote over the footage"],
        ["video-derush-edit-graphics"] = ["footage edit with titles", "lower thirds and callouts on my footage", "on-screen text over clips"],
        ["video-derush-edit-graphics-room"] = ["footage edit with designed titles", "graphics team titles over footage"],
        ["video-derush-edit-music"] = ["footage edit with background music", "add music to my clips"],
        ["video-derush-edit-room"] = ["considered edit decided by an editing team", "higher quality cut of my footage"],
        ["video-derush-edit-grade-room"] = ["footage edit with a colour grade", "colour grade my clips"],
        ["video-derush-edit-sfx"] = ["footage edit with sound effects", "add whooshes and stings to my clips"],
        ["video-derush-edit-broll"] = ["footage edit with b-roll", "cover cuts with b-roll"],
        ["video-derush-edit-voiceover"] = ["footage edit with narration and captions", "narrate my clips", "captions on my footage"],
        ["video-derush-edit-pickups"] = ["fix a line in my footage with a pickup", "replace a mistake with a voice pickup"],
        ["video-derush-edit-dub"] = ["dub my video into another language", "translate and dub footage", "localise a video"],
        ["video-derush-edit-dailies"] = ["footage review with dailies", "review my takes"],
        ["video-region-effect"] = ["blur an object in a video", "hide a face or logo in a clip", "region effect on footage"],
        ["green-screen-insert"] = ["put a screen recording on a green screen", "replace a phone screen in a clip", "insert content onto a green plate"],
        ["spokesperson-clip"] = ["a short clip of our spokesperson", "make a video of me presenting", "an AI clip of a person from an avatar"],
        ["talking-avatar-budget"] = ["a cheap clip of our presenter saying a few words", "make my avatar talk", "a talking clip of a person in their own voice"],
        ["avatar-clip"] = ["a short clip of our spokesperson", "make my avatar talk", "a cheap clip of our presenter saying a few words", "an AI clip of a person from an avatar"],
        ["video-sound-effects"] = ["add sound effects to my finished video", "put pops and whooshes on my edit", "sound design for a video without cutting it"],
        ["video-subtitles"] = ["add subtitles to my video", "burn captions into a video", "subtitles from the spoken words"],
        ["video-subtitles-translated"] = ["translate the subtitles of my video", "subtitles in another language", "translate my video with subtitles"],
        ["product-promo-your-way"] = ["a promo video of my product from its code, with an optional voiceover", "promo with a spoken narration", "product promo for a specific audience"],
        ["avatar-explainer"] = ["a real person from our avatars explains a topic", "an avatar video from my own words", "a talking avatar explainer"],
        ["music-or-sound-effect"] = ["make a music track", "a sound effect from a description", "generate music for my video"],
        ["thumbnail-and-cover-text"] = ["a thumbnail for my video with a title on it", "pick a cover image from my video"],
        ["read-text-aloud"] = ["read this text out loud", "turn my script into an audio file", "text to speech"],
        ["animation-from-idea"] = ["an animated video from my idea", "a short animation of text and shapes", "a video with no footage and no code"],
        ["ai-clip-from-idea"] = ["an AI video clip of a scene I describe", "generate a short video from an idea"],
        ["edit-my-video"] = ["edit my video", "cut my footage down", "edit raw footage, a screen recording or a finished animation", "add music, sound effects, zooms, titles or a voice-over to my video", "captions on my footage", "fix a flubbed line with a pickup", "b-roll over my footage"],
        ["languages"] = ["add subtitles to my video", "translate my video into another language", "dub my video into another language", "burn captions into a video"],
        ["effects-on-a-clip"] = ["blur an object in a video", "put a screen recording on a green screen", "make the camera follow a subject", "hide a face or logo in a clip"],
        ["product-profile-from-code"] = ["build my brand and product profile", "extract my design system from code"],
    };

    /// <summary>True when the recipe has a curated list of goals (every catalog recipe must).</summary>
    public static bool HasCuratedGoals(string templateKey) => Goals.ContainsKey(templateKey);

    /// <summary>The recipes whose runs need an approved brand and product profile in the project.</summary>
    public static bool RequiresProfile(WorkflowTemplateDefinition template) =>
        template.Key is WorkflowTemplateCatalog.QuickWinPromoDsTemplateKey or WorkflowTemplateCatalog.QuickWinPromoPlatesDsTemplateKey;

    /// <summary>The recipe as the assistant reads it.</summary>
    public static RecipeFitInfo Describe(WorkflowTemplateDefinition template)
    {
        IReadOnlyList<AppStepFacts> facts = AppStepFacts.FromTemplate(template);
        AppManifest manifest = AppManifestDeriver.Effective(
            template.AppManifest, facts, template.RequiresUserInput, template.Name, FirstSentence(template.Description));
        string[] goals = Goals.TryGetValue(template.Key, out string[]? known)
            ? known
            : [(manifest.Tagline ?? template.Name).TrimEnd('.')];
        return new RecipeFitInfo(
            template.Key,
            manifest.Title ?? template.Name,
            manifest.Tagline,
            manifest.Category ?? "utility",
            AppCostClass.Derive(facts),
            goals,
            NeedsOf(template, facts),
            [.. BriefFieldConsumers.FieldsReadBy(facts).Order(StringComparer.Ordinal)],
            manifest.SuggestedStylePreset,
            RequiresProfile(template));
    }

    /// <summary>What the recipe needs from the project or the workspace, in plain words (no ids).</summary>
    public static IReadOnlyList<string> NeedsOf(WorkflowTemplateDefinition template, IReadOnlyList<AppStepFacts> facts)
    {
        List<string> needs = [];
        if (BriefFieldConsumers.AcceptsFileRole("video", facts)) needs.Add("video clips");
        if (BriefFieldConsumers.AcceptsFileRole("code", facts)) needs.Add("a code archive or repository");
        if (RequiresProfile(template)) needs.Add("an approved brand and product profile");
        if (facts.Any(f => f.StepType == StepType.Voiceover)) needs.Add("a voice provider");
        if (facts.Any(f => f.StepType == StepType.VideoGenerate)) needs.Add("a video-generation provider");
        if (WantsAudioFiles(template, "offerMusicTracks")) needs.Add("a music track");
        if (WantsAudioFiles(template, "offerSfxClips", unlessFlag: "includeSfxLibrary")) needs.Add("sound-effect files");
        return needs;
    }

    /// <summary>
    /// Whether the recipe fits the project now: the gallery's missing list plus the audio files a music or
    /// sound-effects recipe needs. <c>Satisfied</c> is true when nothing is missing.
    /// </summary>
    public static (bool Satisfied, IReadOnlyList<ReadinessMissing> Missing) Evaluate(
        WorkflowTemplateDefinition template, ProjectReadinessSnapshot snapshot)
    {
        IReadOnlyList<AppStepFacts> facts = AppStepFacts.FromTemplate(template);
        List<ReadinessMissing> missing = [.. ProjectReadinessFacts.Evaluate(facts, snapshot, RequiresProfile(template))];
        if (WantsAudioFiles(template, "offerMusicTracks") && snapshot.AudioFiles == 0)
            missing.Add(new ReadinessMissing("music", "Add a music track", "add-files"));
        if (WantsAudioFiles(template, "offerSfxClips", unlessFlag: "includeSfxLibrary") && snapshot.AudioFiles == 0)
            missing.Add(new ReadinessMissing("sfx", "Add sound-effect files", "add-files"));
        return (missing.Count == 0, missing);
    }

    /// <summary>
    /// What the recipe needs that the organization's plan does not include, from the same entitlement facts the
    /// pre-run lint (WL007) and the run start use: multi-agent rooms, video generation, audio generation and
    /// object segmentation. A recipe the plan would refuse is not "ready". Pure.
    /// </summary>
    public static IReadOnlyList<ReadinessMissing> PlanNeeds(WorkflowTemplateDefinition template, OrganizationEntitlementSet set)
    {
        List<ReadinessMissing> missing = [];
        HashSet<string> seen = [];
        foreach (WorkflowTemplateStepDefinition step in template.Steps)
        {
            if (PlanGate.FeatureFor(step.StepType) is not { } feature || PlanGate.CheckStep(step.StepType, set) is null || !seen.Add(feature))
                continue;
            string what = feature switch
            {
                PlanGate.RoomsFeature => "Multi-agent rooms",
                PlanGate.AudioGenerationFeature => "Audio generation",
                _ => "Video generation"
            };
            missing.Add(new ReadinessMissing("plan:" + feature, $"{what} is not included in your plan", "upgrade-plan"));
        }

        if (!set.SegmentationAllowed && seen.Add(PlanGate.SegmentationFeature)
            && template.Steps.Any(s => s.StepType == StepType.VideoAnalyze && s.VideoAnalyzeConfigJson is { } json
                && json.Contains("\"Segmentation\"", StringComparison.Ordinal)))
        {
            missing.Add(new ReadinessMissing("plan:" + PlanGate.SegmentationFeature, "Finding an object by name is not included in your plan", "upgrade-plan"));
        }

        return missing;
    }

    /// <summary>
    /// How well a goal sentence matches a recipe: the number of the goal's words (3+ letters) found among the
    /// recipe's goal phrases, title and tagline. Zero means no signal, not "unfit".
    /// </summary>
    public static int GoalScore(RecipeFitInfo recipe, string? goal)
    {
        if (string.IsNullOrWhiteSpace(goal))
            return 0;
        string haystack = string.Join(' ', recipe.ForGoals.Append(recipe.Title).Append(recipe.Tagline ?? string.Empty)).ToLowerInvariant();
        return goal.ToLowerInvariant()
            .Split([' ', ',', '.', ';', ':', '-', '/', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 3)
            .Distinct()
            .Count(haystack.Contains);
    }

    /// <summary>
    /// True when an analysis step of the recipe offers the project's own audio files under <paramref name="analyzeFlag"/>
    /// and has nothing else to offer. A step that also sets <paramref name="unlessFlag"/> (the built-in sound library)
    /// always has sounds to offer, so the customer's own files are optional there.
    /// </summary>
    private static bool WantsAudioFiles(WorkflowTemplateDefinition template, string analyzeFlag, string? unlessFlag = null) =>
        template.Steps.Any(s => s.StepType == StepType.VideoAnalyze
            && s.VideoAnalyzeConfigJson is { } json
            && FlagIsTrue(json, analyzeFlag)
            && (unlessFlag is null || !FlagIsTrue(json, unlessFlag)));

    private static bool FlagIsTrue(string json, string flag)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(flag, out JsonElement value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? FirstSentence(string text)
    {
        int end = text.IndexOf(". ", StringComparison.Ordinal);
        string sentence = end > 0 ? text[..(end + 1)] : text;
        return sentence.Length <= AppManifestVocabulary.MaxTagline ? sentence : sentence[..(AppManifestVocabulary.MaxTagline - 1)].TrimEnd() + "…";
    }
}
