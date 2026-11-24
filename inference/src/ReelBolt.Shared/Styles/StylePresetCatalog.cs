namespace ReelBolt.Shared.Styles;

/// <summary>A built-in editing style everyone can pick without saving anything.</summary>
/// <param name="Key">Stable kebab-case key, persisted on runs (<see cref="RunStyleSnapshot.PresetKey"/>) — never rename one.</param>
/// <param name="Name">Display name.</param>
/// <param name="Description">One plain-language sentence for the picker.</param>
/// <param name="Config">The style.</param>
/// <param name="SuggestedFormat">The output shape the picker pre-selects with this preset; never applied on its own.</param>
public sealed record StylePreset(
    string Key,
    string Name,
    string Description,
    StyleConfig Config,
    RunOutputFormat SuggestedFormat = RunOutputFormat.Original);

/// <summary>
/// The built-in presets. Keys are persisted on runs, so a preset may be retuned but its key never
/// renamed or reused for something else.
/// </summary>
public static class StylePresetCatalog
{
    public static readonly IReadOnlyList<StylePreset> All =
    [
        new StylePreset(
            "fast-social-reel",
            "Fast social reel",
            "Punchy and quick: opens on the best moment, hard cuts, big bold social captions, vertical.",
            new StyleConfig(
                Pacing: StylePacing.Fast,
                Storytelling: StyleStorytelling.HookFirst,
                Editing: StyleEditing.JumpCuts,
                Tone: StyleTone.Energetic,
                Captions: StyleCaptions.Bold,
                Color: StyleColor.Vibrant,
                Music: StyleMusicEnergy.High,
                Graphics: StyleGraphicsDensity.Moderate),
            RunOutputFormat.Vertical),
        new StylePreset(
            "cinematic",
            "Cinematic",
            "Slow and atmospheric: long shots, smooth joins, a filmic colour look and minimal text.",
            new StyleConfig(
                Pacing: StylePacing.Slow,
                Storytelling: StyleStorytelling.Montage,
                Editing: StyleEditing.SmoothTransitions,
                Tone: StyleTone.Inspirational,
                Captions: StyleCaptions.Clean,
                Color: StyleColor.Filmic,
                Music: StyleMusicEnergy.Medium,
                Graphics: StyleGraphicsDensity.Minimal),
            RunOutputFormat.Landscape),
        new StylePreset(
            "corporate-explainer",
            "Corporate explainer",
            "Clear and trustworthy: states the problem, shows the answer, clean captions and tidy titles.",
            new StyleConfig(
                Pacing: StylePacing.Medium,
                Storytelling: StyleStorytelling.ProblemSolution,
                Editing: StyleEditing.Mixed,
                Tone: StyleTone.Serious,
                Captions: StyleCaptions.Clean,
                Color: StyleColor.Natural,
                Music: StyleMusicEnergy.Low,
                Graphics: StyleGraphicsDensity.Moderate),
            RunOutputFormat.Landscape),
        new StylePreset(
            "documentary",
            "Documentary",
            "Honest and patient: tells it in order, lets people finish their sentences, natural colour.",
            new StyleConfig(
                Pacing: StylePacing.Slow,
                Storytelling: StyleStorytelling.Chronological,
                Editing: StyleEditing.SmoothTransitions,
                Tone: StyleTone.Serious,
                Captions: StyleCaptions.Clean,
                Color: StyleColor.Natural,
                Music: StyleMusicEnergy.Low,
                Graphics: StyleGraphicsDensity.Minimal)),
        new StylePreset(
            "beat-synced-music-video",
            "Beat-synced music video",
            "Driven by the music: cuts on the beat, a montage of moments, vivid colour and no titles.",
            new StyleConfig(
                Pacing: StylePacing.BeatSynced,
                Storytelling: StyleStorytelling.Montage,
                Editing: StyleEditing.JumpCuts,
                Tone: StyleTone.Energetic,
                Captions: StyleCaptions.Punch,
                Color: StyleColor.Vibrant,
                Music: StyleMusicEnergy.High,
                Graphics: StyleGraphicsDensity.None),
            RunOutputFormat.Vertical),
        new StylePreset(
            "dynamic-edit",
            "Dynamic edit",
            "Cuts that move: fast pacing, beat-aligned punch-ins, alternating shots and quick joins, vertical.",
            new StyleConfig(
                Pacing: StylePacing.Fast,
                Editing: StyleEditing.Mixed,
                Tone: StyleTone.Energetic,
                Music: StyleMusicEnergy.Medium,
                Graphics: StyleGraphicsDensity.Moderate,
                Dynamics: StyleDynamics.Fast),
            RunOutputFormat.Vertical)
    ];

    /// <summary>The preset with this key (ordinal, case-insensitive), or null.</summary>
    public static StylePreset? Find(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : All.FirstOrDefault(p => string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
}
