using System.Text.Json;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The curated app manifests the templates ship with, by template key. A template without an entry here uses the
/// manifest derived from its steps. A manifest never bumps the template's <c>Version</c>: it is presentation only.
/// </summary>
public static class WorkflowTemplateAppManifests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    private static AppManifestInput Brief(string field, string label, string? defaultJson = null, string? choicesJson = null) =>
        new(AppManifestVocabulary.KindBrief, Field: field, Label: label,
            Default: defaultJson is null ? null : Json(defaultJson),
            Choices: choicesJson is null ? null : JsonSerializer.Deserialize<List<JsonElement>>(choicesJson));

    private static AppManifest Promo(string title, string tagline, bool withDesign) => new(
        Version: AppManifestVocabulary.Version,
        Title: title,
        Tagline: tagline,
        Category: "promo",
        SuggestedStylePreset: "fast-social-reel",
        Inputs: new List<AppManifestInput?>
        {
            Brief(BriefFieldConsumers.Language, "Language"),
            Brief(BriefFieldConsumers.LengthSec, "Length", "30", "[15,30,60]"),
            Brief(BriefFieldConsumers.Format, "Shape"),
            Brief(BriefFieldConsumers.StylePreset, "Style"),
            withDesign ? Brief(BriefFieldConsumers.DesignSystem, "Design") : null,
            new AppManifestInput(AppManifestVocabulary.KindNotes, Label: "Who is it for and what should it show?")
        }.OfType<AppManifestInput>().ToList(),
        Result: new AppManifestResult("video"),
        Advanced: new AppManifestAdvanced(true));

    // The footage family: the clips come from the project (a video slot filled at run time), the rest is the brief.
    // `overlay: true` adds the optional picture/video slot and the card-sound choice of the overlay feature.
    private static AppManifest Footage(
        string title, string tagline, string notesLabel, string? defaultLength = "60", string? lengthChoices = "[30,60,90]",
        bool narration = false, bool resolution = false, bool overlay = false) => new(
        Version: AppManifestVocabulary.Version,
        Title: title,
        Tagline: tagline,
        Category: "footage",
        SuggestedStylePreset: "fast-social-reel",
        Inputs: new List<AppManifestInput?>
        {
            new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "video", Label: "Your clips", Min: 1, Max: 20),
            overlay ? new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "overlay", Label: "A picture or video to show", Min: 0, Max: 4) : null,
            Brief(BriefFieldConsumers.SourceLanguage, "Spoken language"),
            narration ? Brief(BriefFieldConsumers.Language, "Narration language") : null,
            Brief(BriefFieldConsumers.LengthSec, "Length", defaultLength, lengthChoices),
            Brief(BriefFieldConsumers.Format, "Shape"),
            resolution ? Brief(BriefFieldConsumers.Resolution, "Resolution") : null,
            narration ? Brief(BriefFieldConsumers.Voice, "Narration") : null,
            Brief(BriefFieldConsumers.StylePreset, "Style"),
            overlay ? Brief(BriefFieldConsumers.OverlayCardAudio, "Under a full-screen picture", "\"agent\"", "[\"agent\",\"keep\",\"mute\"]") : null,
            new AppManifestInput(AppManifestVocabulary.KindNotes, Label: notesLabel)
        }.OfType<AppManifestInput>().ToList(),
        Result: new AppManifestResult("video"),
        Advanced: new AppManifestAdvanced(true));

    public static readonly IReadOnlyDictionary<string, AppManifest> ByKey = new Dictionary<string, AppManifest>(StringComparer.Ordinal)
    {
        [WorkflowTemplateCatalog.StarterTemplateKey] = Promo(
            "Promo video from your code",
            "Turns your app's code into a short promo of its real screens.",
            withDesign: false),
        [WorkflowTemplateCatalog.QuickWinPromoDsTemplateKey] = Promo(
            "Promo video in your brand",
            "A short promo of your app, built from your approved brand and product profile.",
            withDesign: true),
        ["quick-win-promo-plates"] = Promo(
            "Promo video with AI backgrounds",
            "A short promo of your app, with a few soft AI-generated backgrounds behind the scenes.",
            withDesign: false),
        ["lean-context-promo"] = Promo(
            "Promo video from your code (lighter)",
            "The same promo of your app, reading less of your code so big projects run faster.",
            withDesign: false),
        ["video-derush-edit-room"] = Footage(
            "Highlight edit by an editing team",
            "Several AI editors debate the cut before a lead editor decides: slower, more considered.",
            "What should it keep?"),
        ["video-derush-edit-music"] = Footage(
            "Footage edit with background music",
            "Your clips cut down to the good parts, with a music track mixed under the voices.",
            "What mood should the music have?"),
        ["video-derush-edit-voiceover"] = Footage(
            "Footage edit with narration",
            "Your clips cut down, then a narration written for what is kept, spoken and captioned.",
            "What is the topic and the tone?",
            narration: true),
        ["video-derush-edit-broll"] = Footage(
            "Footage edit with AI extra shots",
            "Your clips cut down, plus a few extra shots made by AI: an opening, cutaways or an end card.",
            "Describe the extra shots you want",
            resolution: true),
        [WorkflowTemplateCatalog.GreenScreenInsertTemplateKey] = new AppManifest(
            Version: AppManifestVocabulary.Version,
            Title: "Put a screen on a green screen",
            Tagline: "Finds the green screen in your clip and follows it as it moves, then puts your picture or video on it.",
            Category: "footage",
            Inputs:
            [
                new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "video", Label: "Your clip with the green screen", Min: 1, Max: 1),
                new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "insertContent", Label: "What goes on the screen", Min: 1, Max: 1),
                Brief(BriefFieldConsumers.Resolution, "Resolution")
            ],
            Result: new AppManifestResult("video"),
            Advanced: new AppManifestAdvanced(true)),
        ["video-derush-edit"] = Footage(
            "Highlight edit from footage",
            "Your clips, cut down to the good parts: dead air and false starts gone.",
            "What should it keep?",
            overlay: true),
        // The curated trigger recipe (docs/design/picture-overlay.md §8.5): the base's eight inputs plus
        // the overlay slot and card-sound choice. The manifest cap is 8, so resolution is left to the
        // style preset; the design's notes question is shortened to fit the 40-character label cap.
        [WorkflowTemplateCatalog.TestimonialReelTemplateKey] = Footage(
            "Testimonial reel from a talking-head clip and a screenshot",
            "Your talking-head clip cut down, with a review, message or result shown as a card or corner inset.",
            "What is the clip about and what to show?",
            overlay: true),
        [WorkflowTemplateCatalog.SoundEffectsTemplateKey] = new AppManifest(
            Version: AppManifestVocabulary.Version,
            Title: "Sound effects for your video",
            Tagline: "Adds pops, whooshes and clicks where things happen on screen. Nothing is cut.",
            Category: "footage",
            SuggestedStylePreset: null,
            Inputs:
            [
                new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "video", Label: "Your video", Min: 1, Max: 1),
                new AppManifestInput(AppManifestVocabulary.KindNotes, Label: "What should the sounds feel like?")
            ],
            Result: new AppManifestResult("video"),
            Advanced: new AppManifestAdvanced(true)),
        [WorkflowTemplateCatalog.SubtitlesTemplateKey] = new AppManifest(
            Version: AppManifestVocabulary.Version,
            Title: "Subtitles for your video",
            Tagline: "Burns in what is said and gives you SRT and VTT files, nothing else changed.",
            Category: "footage",
            SuggestedStylePreset: null,
            Inputs:
            [
                new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "video", Label: "Your video", Min: 1, Max: 1),
                Brief(BriefFieldConsumers.SourceLanguage, "Spoken language")
            ],
            Result: new AppManifestResult("video"),
            Advanced: new AppManifestAdvanced(true)),
        [WorkflowTemplateCatalog.SubtitlesTranslatedTemplateKey] = new AppManifest(
            Version: AppManifestVocabulary.Version,
            Title: "Subtitles in another language",
            Tagline: "Translates what is said, keeps the timing, burns it in and gives you SRT and VTT files.",
            Category: "footage",
            SuggestedStylePreset: null,
            Inputs:
            [
                new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "video", Label: "Your video", Min: 1, Max: 1),
                Brief(BriefFieldConsumers.SourceLanguage, "Spoken language"),
                Brief(BriefFieldConsumers.Language, "Subtitle language")
            ],
            Result: new AppManifestResult("video"),
            Advanced: new AppManifestAdvanced(true)),
    };
}
