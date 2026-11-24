using System.Text.Json;
using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// Computes the manifest of any workflow from its steps (docs/design/workflow-apps.md 4.1): the brief fields some
/// step reads, a video slot for a step that takes a project video file, the notes box when the workflow asks for a
/// request, and the format and style pickers when something is style-aware. Pure and deterministic; the result is
/// never persisted, so it improves for free when <see cref="BriefFieldConsumers"/> does. An explicit manifest wins
/// over the derived one member by member (<see cref="Effective"/>).
/// </summary>
public static class AppManifestDeriver
{
    // The order the inputs are shown in, top to bottom.
    private static readonly (string Field, string Label)[] DerivedBriefFields =
    [
        (BriefFieldConsumers.Language, "Language"),
        (BriefFieldConsumers.LengthSec, "Length"),
        (BriefFieldConsumers.Format, "Shape"),
        (BriefFieldConsumers.Voice, "Narration"),
        (BriefFieldConsumers.StylePreset, "Style"),
        (BriefFieldConsumers.DesignSystem, "Design"),
        (BriefFieldConsumers.Pace, "Pace"),
    ];

    /// <summary>The manifest derived from the steps alone (title and tagline left null: the caller owns the name).</summary>
    public static AppManifest Derive(IReadOnlyList<AppStepFacts> steps, bool requiresUserInput, string? suggestedStylePreset = null)
    {
        IReadOnlySet<string> read = BriefFieldConsumers.FieldsReadBy(steps);
        List<AppManifestInput> inputs = [];
        foreach ((string field, string label) in DerivedBriefFields)
        {
            if (read.Contains(field))
                inputs.Add(new AppManifestInput(AppManifestVocabulary.KindBrief, Field: field, Label: label));
        }

        // The avatar picker only where the brief's avatar reaches a step (an inline generate step that names none):
        // a plan-sourced or framed step would ignore it, so a b-roll app never shows a picker it does not honour.
        if (steps.Any(s => s.TakesBriefAvatar))
            inputs.Add(new AppManifestInput(AppManifestVocabulary.KindBrief, Field: BriefFieldConsumers.Avatar, Label: "Avatar"));

        if (BriefFieldConsumers.AcceptsFileRole("video", steps))
            inputs.Add(new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "video", Label: "Your clips", Min: 1));

        if (BriefFieldConsumers.AcceptsFileRole("overlay", steps))
            inputs.Add(new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "overlay", Label: "A picture or video to show", Min: 0, Max: 4));

        if (requiresUserInput)
            inputs.Add(new AppManifestInput(AppManifestVocabulary.KindNotes, Label: "Anything else?"));

        // The pace keeps its slot: when the list is over the shown cap, the last other input drops, never the pace.
        int paceAt = inputs.FindIndex(i => i.Field == BriefFieldConsumers.Pace);
        List<AppManifestInput> shown = [.. inputs.Take(AppManifestVocabulary.MaxInputs)];
        if (paceAt >= AppManifestVocabulary.MaxInputs)
        {
            shown.RemoveAt(shown.Count - 1);
            shown.Add(inputs[paceAt]);
        }

        return new AppManifest(
            Version: AppManifestVocabulary.Version,
            Category: DeriveCategory(steps),
            Inputs: shown,
            Result: new AppManifestResult(DeriveResultKind(steps)),
            Advanced: new AppManifestAdvanced(true));
    }

    /// <summary>
    /// The manifest the app view shows: each member the author set wins over the derived one (the inputs list as a
    /// whole), then the title falls back to the workflow's name and the tagline to <paramref name="fallbackTagline"/>.
    /// </summary>
    public static AppManifest Effective(
        AppManifest? explicitManifest, IReadOnlyList<AppStepFacts> steps, bool requiresUserInput,
        string workflowName, string? fallbackTagline = null)
    {
        AppManifest derived = Derive(steps, requiresUserInput, explicitManifest?.SuggestedStylePreset);
        AppManifest source = explicitManifest ?? new AppManifest();
        IReadOnlyList<AppManifestInput>? effectiveInputs = source.Inputs ?? derived.Inputs;
        // An author's own input list still gets the pace when a compile reads it: the control is how a run asks for the
        // dynamic edit, and no template's list names it. It always takes a shown slot (see below).
        if (source.Inputs is { } own && derived.Inputs?.FirstOrDefault(i => i.Field == BriefFieldConsumers.Pace) is { } pace
            && !own.Any(i => i.Kind == AppManifestVocabulary.KindBrief && i.Field == BriefFieldConsumers.Pace))
        {
            // The pace keeps a shown slot: a full list moves its lowest-priority shown input (the last that is not a
            // required file slot) under "More options", so no catalogued template can push the pace out.
            List<AppManifestInput> list = [.. own];
            int shownCount = list.Count(i => !AppInputPlacement.IsUnderMore(i));
            if (shownCount >= AppManifestVocabulary.MaxInputs)
            {
                int victim = list.FindLastIndex(i => !AppInputPlacement.IsUnderMore(i)
                    && !(i.Kind == AppManifestVocabulary.KindFiles && i.Min is > 0));
                if (victim >= 0)
                    list[victim] = list[victim] with { More = true };
            }

            list.Add(pace);
            effectiveInputs = list;
        }

        return new AppManifest(
            Version: AppManifestVocabulary.Version,
            Title: NullIfBlank(source.Title) ?? NullIfBlank(AppManifestValidator.SanitizeText(workflowName, AppManifestVocabulary.MaxTitle)) ?? "Workflow",
            Tagline: NullIfBlank(source.Tagline) ?? fallbackTagline,
            Category: NullIfBlank(source.Category) ?? derived.Category,
            SuggestedStylePreset: NullIfBlank(source.SuggestedStylePreset),
            Inputs: effectiveInputs,
            Result: source.Result ?? derived.Result,
            Advanced: source.Advanced ?? derived.Advanced);
    }

    /// <summary>
    /// Read-time upgrade of a manifest stored before its template gained an input: a green-screen app saved with one slot
    /// gets the "What goes on the screen" slot after its clip slot. Nothing else in the stored manifest is touched, and
    /// a manifest that already has the slot (or that is not this template's) is returned as is.
    /// </summary>
    public static AppManifest? UpgradeStored(AppManifest? stored, string? templateKey)
    {
        if (stored?.Inputs is not { } inputs || !string.Equals(templateKey, WorkflowTemplateCatalog.GreenScreenInsertTemplateKey, StringComparison.Ordinal))
            return stored;
        if (inputs.Any(i => i.Kind == AppManifestVocabulary.KindFiles && string.Equals(i.Role, "insertContent", StringComparison.Ordinal)))
            return stored;

        List<AppManifestInput> upgraded = [.. inputs];
        int after = upgraded.FindIndex(i => i.Kind == AppManifestVocabulary.KindFiles);
        upgraded.Insert(after + 1, new AppManifestInput(AppManifestVocabulary.KindFiles, Role: "insertContent", Label: "What goes on the screen", Min: 1, Max: 1));
        return stored with { Inputs = upgraded };
    }

    /// <summary>The gallery filter the workflow belongs to: footage, promo, brand or utility.</summary>
    public static string DeriveCategory(IReadOnlyList<AppStepFacts> steps)
    {
        if (steps.Any(s => s.StepType == StepType.VideoAnalyze))
            return "footage";
        if (steps.Any(s => s.StepType == StepType.Agent
            && s.AgentType is AgentType.AuthorAgent or AgentType.ScriptwriterAgent or AgentType.RemotionComponentTranslator))
            return "promo";
        return steps.Any(s => s.StepType == StepType.DesignSystem) ? "brand" : "utility";
    }

    /// <summary>The output the run page leads with.</summary>
    public static string DeriveResultKind(IReadOnlyList<AppStepFacts> steps)
    {
        if (steps.Any(s => s.StepType is StepType.VideoCompile or StepType.TimelineRender or StepType.VideoGenerate
            || (s.StepType == StepType.Agent && s.AgentType == AgentType.AuthorAgent)))
            return "video";
        if (steps.Any(s => s.StepType is StepType.ThumbnailGenerate or StepType.ImageGenerate))
            return "image";
        if (steps.Any(s => s.StepType is StepType.AudioGenerate or StepType.Voiceover))
            return "audio";
        return "files";
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
