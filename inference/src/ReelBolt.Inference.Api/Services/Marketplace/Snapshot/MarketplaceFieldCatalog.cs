using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Services.Marketplace.Snapshot;

/// <summary>How one step-config property is treated on its way into a public recipe.</summary>
internal enum FieldClass
{
    /// <summary>Non-id, non-text: a number, bool or enum, or a short closed-vocabulary string (codec, colour, file name).</summary>
    Keep,

    /// <summary>Free text that can reach a model or a viewer (prompt, label, persona, overlay text, inline voiceover line): sanitised, listed, content-checked.</summary>
    Text,

    /// <summary>An author-owned id that is removed and recorded as an open slot the copier fills.</summary>
    Slot,

    /// <summary>Removed (and refused when <see cref="FieldRule.RefuseCode"/> is set and the value is present).</summary>
    Strip
}

internal sealed record FieldRule(FieldClass Class, string? SlotKind = null, string? RefuseCode = null)
{
    public static readonly FieldRule Keep = new(FieldClass.Keep);
    public static readonly FieldRule Text = new(FieldClass.Text);
    public static readonly FieldRule Strip = new(FieldClass.Strip);

    public static FieldRule Slot(string kind) => new(FieldClass.Slot, kind);

    /// <summary>Stripped, and the whole recipe refused (<c>RUN_BOUND_SOURCE</c>) when a value is present.</summary>
    public static readonly FieldRule RunBound = new(FieldClass.Strip, RefuseCode: "RUN_BOUND_SOURCE");
}

/// <summary>
/// The classification of every non-numeric property reachable from a shareable step config, keyed <c>DeclaringType.Property</c>.
/// Numbers, bools and enums are <see cref="FieldClass.Keep"/> by rule; strings, ids and anything else must be listed here, and a
/// new such property fails <c>MarketplaceCatalogDriftTests</c> until it is (unlisted properties are also dropped at run time).
/// </summary>
internal static class MarketplaceFieldCatalog
{
    public static readonly IReadOnlyDictionary<string, FieldRule> Explicit = Build();

    /// <summary>Whether a property needs no catalog entry (primitive, or a descended record).</summary>
    public static FieldRule? RuleFor(ConfigProperty property) =>
        Explicit.TryGetValue(property.RuleKey, out FieldRule? rule) ? rule
        : property.IsPrimitive ? FieldRule.Keep
        : null;

    /// <summary>True when the walker descends into the property's record type instead of classifying a leaf.</summary>
    public static bool Descends(ConfigProperty property) => property.IsComplex && !Explicit.ContainsKey(property.RuleKey);

    private static Dictionary<string, FieldRule> Build()
    {
        Dictionary<string, FieldRule> r = [];

        // ---- provider ids: an open provider slot (null runs the organization's default provider) ----
        foreach (string key in new[]
        {
            "ConditionalStepConfig.ProviderId", "ReviewLoopStepConfig.ProviderId", "VisualCheckConfig.VisionProviderId",
            "VideoAnalyzeStepConfig.TranscriptionProviderId", "VideoAnalyzeStepConfig.VisionProviderId",
            "VideoAnalyzeStepConfig.ObjectVisionProviderId", "VideoAnalyzeStepConfig.SegmentationProviderId",
            "VideoGenerateStepConfig.ProviderId", "VideoGenerateStepConfig.StillProviderId", "VoiceoverStepConfig.ProviderId", "AudioGenerateStepConfig.ProviderId", "ImageGenerateStepConfig.ProviderId",
            "EditRoomStepConfig.ConveneProviderId", "GraphicsRoomStepConfig.ConveneProviderId",
            "ColorGradeRoomStepConfig.ConveneProviderId", "DailiesRoomStepConfig.ConveneProviderId"
        })
            r[key] = FieldRule.Slot(PortableSlotKinds.Provider);

        // ---- voices ----
        r["VoiceoverStepConfig.ProjectVoiceId"] = FieldRule.Slot(PortableSlotKinds.Voice);
        r["VoiceoverStepConfig.DefaultVoiceId"] = FieldRule.Slot(PortableSlotKinds.Voice);
        r["VoiceoverLine.VoiceId"] = FieldRule.Slot(PortableSlotKinds.Voice);

        // ---- project files: an open file slot ----
        foreach (string key in new[]
        {
            "VideoSourceRef.ProjectFileId", "VideoGenerateStepConfig.PlanProjectFileId", "VideoGenerateStepConfig.FirstFrameProjectFileId",
            "VideoGenerateStepConfig.LastFrameProjectFileId", "VideoAnalyzeStepConfig.MusicBeatTrackProjectFileId",
            "ThumbnailGenerateStepConfig.FontProjectFileId", "VideoCompileStepConfig.MusicTrackProjectFileId",
            "VideoCompileStepConfig.InsertContentProjectFileId", "VideoGenerateStepConfig.AvatarAudioProjectFileId", "VideoInsertContentAssignment.ProjectFileId", "InsertContentRef.ProjectFileId"
        })
            r[key] = FieldRule.Slot(PortableSlotKinds.File);

        // ---- run-bound sources: stripped and the recipe refused ----
        foreach (string key in new[]
        {
            "VideoSourceRef.StepResultId", "InsertContentRef.StepResultId", "ThumbnailGenerateStepConfig.AnalysisStepResultId",
            "VideoCompileStepConfig.AnalysisStepResultId"
        })
            r[key] = FieldRule.RunBound;

        // ---- agents: custom ones refuse in the builder; a built-in seat or director is dropped to its default ----
        foreach (string key in new[]
        {
            "EditRoomSeat.AgentDefinitionId", "EditRoomStepConfig.DirectorAgentDefinitionId", "GraphicsRoomStepConfig.DirectorAgentDefinitionId",
            "ColorGradeRoomStepConfig.DirectorAgentDefinitionId", "DailiesRoomStepConfig.DirectorAgentDefinitionId"
        })
            r[key] = FieldRule.Strip;

        // ---- forced off: the copier never inherits the author's consent to upload source media ----
        r["VideoGenerateStepConfig.AllowSourceMediaEgress"] = FieldRule.Strip;

        // ---- avatars: a consented likeness bound to the author's project never travels; the copier picks their own in the run brief ----
        r["VideoGenerateStepConfig.AvatarId"] = FieldRule.Strip;
        // Several subjects per clip: the same rule for every id that names an avatar of the author's project (a person
        // or an object), with the roles chosen for it; the planner's allowlist is a list of such ids too.
        r["VideoGenerateStepConfig.Subjects"] = FieldRule.Strip;
        r["VideoGenerateSubject.AvatarId"] = FieldRule.Strip;
        r["VideoGenerateStepConfig.PlannedSubjectIds"] = FieldRule.Strip;
        r["VideoAnalyzeStepConfig.AllowSourceMediaEgress"] = FieldRule.Strip;

        // ---- closed-vocabulary strings (codec, colour, file name, path, vendor model id): kept only when short and plain ----
        foreach (string key in new[]
        {
            "PinInsertEntry.RegionId", "InsertContentRef.From",
            "AudioGenerateStepConfig.Model", "AudioGenerateStepConfig.FileNamePrefix",
            "ImageGenerateStepConfig.AspectRatio", "ImageGenerateStepConfig.FileNamePrefix", "ImageGenerateStepConfig.Resolution",
            "ExtractFrameStepConfig.Format", "DesignSystemParseOptions.ExtraGlobs",
            "DailiesRoomStepConfig.ReasoningEffort", "ThumbnailGenerateStepConfig.OutputFileName",
            "ThumbnailGenerateStepConfig.OverlayFontColor", "ThumbnailGenerateStepConfig.OverlayBoxColor",
            "VoiceoverStepConfig.Model", "VoiceoverStepConfig.Language",
            "VoiceoverLine.AnchorId", "VideoGenerateStepConfig.Model",
            "VideoGenerateStepConfig.Resolution", "VideoGenerateStepConfig.Ratio",
            "ColorGradeRoomStepConfig.ReasoningEffort", "GraphicsRoomStepConfig.ReasoningEffort",
            "EditRoomStepConfig.ReasoningEffort", "VideoCompileStepConfig.OutputFileName",
            "VideoCompileStepConfig.VideoCodec", "VideoCompileStepConfig.AudioCodec",
            "VideoCompileStepConfig.Preset", "VideoCompileStepConfig.OverlayFontColor",
            "VideoCompileStepConfig.OverlayBoxColor", "VideoCompileStepConfig.ProgramFadeColor",
            "VideoCompileStepConfig.CaptionFontColor", "VideoCompileStepConfig.CaptionBoxColor",
            "VideoCompileStepConfig.CaptionHighlightColor", "VideoInsertContentAssignment.RegionId",
            "VideoAnalyzeStepConfig.Language", "VideoAnalyzeStepConfig.InsertRegionColor",
            "ExtractStepConfig.Path", "ExtractStepConfig.Fields",
            "ExtractStepConfig.IdField", "ExtractStepConfig.IdPrefix",
            "ExtractStepConfig.SortBy", "ExtractStepConfig.IdsPath",
            "ExtractStepConfig.RecordsPath", "ExtractStepConfig.Categories",
            "ExtractStepConfig.IncludeExtensions", "ExtractStepConfig.ExcludePathContains",
            "ExtractExpectation.RequiredPaths", "ExtractExpectation.NonEmptyStringPaths"
        })
            r[key] = FieldRule.Keep;

        // ---- free text ----
        foreach (string key in new[]
        {
            "ConditionalStepConfig.DecisionQuestion", "VideoGenerateStepConfig.InlinePrompt", "EditRoomSeat.Name", "EditRoomSeat.Persona",
            "ObjectTrackTarget.Label", "VoiceoverLine.Text", "PronunciationOverride.Term", "PronunciationOverride.Ipa",
            "ThumbnailGenerateStepConfig.OverlayText", "AudioGenerateStepConfig.Prompt", "AudioGenerateStepConfig.CompositionPlanJson",
            "VideoAnalyzeStepConfig.SubjectLabel", "VideoCompileStepConfig.CensorLabels", "VideoRegionEffect.Target",
            "VideoEnhanceStepConfig.Prompt", "ImageGenerateStepConfig.Prompt", "ImageGenerateStepConfig.Instruction", "ImageGenerateStepConfig.ItemList"
        })
            r[key] = FieldRule.Text;

        // ---- audio cleanup: the hosted-model provider is an open slot; the upload consent is never carried (a copy starts with it off) ----
        r["AudioCleanupStepConfig.ProviderId"] = FieldRule.Slot(PortableSlotKinds.Provider);
        r["AudioCleanupStepConfig.AllowSourceMediaEgress"] = FieldRule.Strip;

        // ---- video enhance: same as audio cleanup, the hosted provider is an open slot and the upload consent is never carried ----
        r["VideoEnhanceStepConfig.ProviderId"] = FieldRule.Slot(PortableSlotKinds.Provider);
        r["VideoEnhanceStepConfig.AllowSourceMediaEgress"] = FieldRule.Strip;
        // Extend: the target aspect is a closed vocabulary (16:9, 9:16, 1:1, 4:5); its prompt is free text (the Text list above)
        r["VideoEnhanceStepConfig.TargetAspect"] = FieldRule.Keep;

        // ---- image generate: the provider is an open slot; the upload consent is never carried (a copy starts with it off) ----
        r["ImageGenerateStepConfig.AllowSourceMediaEgress"] = FieldRule.Strip;

        // ---- follow a tracked subject: a track id of the analysis, a closed shape (r1, g2) ----
        r["FollowSubjectRequest.TrackId"] = FieldRule.Keep;
        r["EventMoveRequest.EventId"] = FieldRule.Keep;
        r["FlashCueRequest.EventId"] = FieldRule.Keep;
        r["VideoRegionEffect.FromEventId"] = FieldRule.Keep;
        r["VideoRegionEffect.ToEventId"] = FieldRule.Keep;
        r["SpeedRampRequest.EventId"] = FieldRule.Keep;

        // ---- camera log profile per source index: a closed vocabulary (see ConfigWalker.KeepSourceInputProfiles) ----
        r["VideoCompileStepConfig.SourceInputProfiles"] = FieldRule.Keep;
        r["VideoAnalyzeStepConfig.SourceInputProfiles"] = FieldRule.Keep;

        return r;
    }
}
