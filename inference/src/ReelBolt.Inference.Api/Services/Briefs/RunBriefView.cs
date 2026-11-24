using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Styles;

namespace ReelBolt.Inference.Api.Services.Briefs;

/// <summary>
/// The composed brief a run record returns: the stored fields plus the style, tone, format and resolution read
/// back from their own columns, so the UI shows exactly what a run used. The run's request text is not repeated
/// here (it is the response's <c>UserRequest</c>).
/// </summary>
public static class RunBriefView
{
    public static RunBrief? Compose(WorkflowExecution execution) => Compose(
        execution.RunBriefJson, execution.StyleConfigJson, execution.OutputFormatOverride, execution.OutputResolution);

    public static RunBrief? Compose(string? runBriefJson, string? styleConfigJson, RunOutputFormat? format, int? resolution)
    {
        StoredRunBrief? stored = RunBriefJson.TryParse(runBriefJson);
        RunStyleSnapshot? style = StyleJson.TryParseSnapshot(styleConfigJson);

        RunBrief brief = new(
            Language: stored?.Language,
            SourceLanguage: stored?.SourceLanguage,
            LengthSec: stored?.LengthSec,
            Format: format?.ToString(),
            Resolution: resolution is { } shortSide ? OutputResolution.Label(shortSide) : null,
            StylePreset: style?.PresetKey,
            StyleId: style?.StyleId,
            // A one-off style has neither a preset key nor a saved id; its config is the whole answer.
            Style: style is { PresetKey: null, StyleId: null } ? style.Config : null,
            Tone: style?.Config.Tone,
            DesignSystem: stored?.DesignSystem,
            Voice: stored?.Voice,
            SourceFiles: stored?.SourceFiles,
            OverlayCardAudio: stored?.OverlayCardAudio,
            Avatar: stored?.Avatar,
            Inputs: stored?.Inputs,
            Pace: stored?.Pace);

        return brief == new RunBrief() ? null : brief;
    }
}
