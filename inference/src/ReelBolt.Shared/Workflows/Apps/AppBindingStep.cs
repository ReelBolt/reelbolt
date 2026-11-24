using ReelBolt.Shared.Data.Models;

namespace ReelBolt.Shared.Workflows.Apps;

/// <summary>
/// The slice of one workflow step the app-input logic reads and writes (docs/design/app-inputs.md 5): its type, its
/// one config blob (the column of its type), its condition and branches, and its agent. One shape for stored steps
/// (<see cref="From(WorkflowStep)"/>), save requests (the API maps its DTO) and template steps (<see cref="FromTemplate"/>), so the validator, the
/// binder and the branch resolver see exactly the step that will run.
/// </summary>
public sealed record AppBindingStep(
    int StepOrder,
    StepType StepType,
    string? ConfigJson = null,
    string? ConditionExpression = null,
    string? TrueBranchStepOrder = null,
    string? FalseBranchStepOrder = null,
    int? LoopTargetStepOrder = null,
    Guid? AgentDefinitionId = null,
    string? Name = null)
{
    /// <summary>A stored step.</summary>
    public static AppBindingStep From(WorkflowStep step) => new(
        step.StepOrder, step.StepType, ConfigJsonOf(step), step.ConditionExpression, step.TrueBranchStepOrder,
        step.FalseBranchStepOrder, step.LoopTargetStepOrder, step.AgentDefinitionId, step.Label);

    /// <summary>The steps of a template, numbered from 1 in order (no agent ids: templates name agent types).</summary>
    public static IReadOnlyList<AppBindingStep> FromTemplate(WorkflowTemplateDefinition template) =>
        [.. template.Steps.Select((s, i) => new AppBindingStep(
            i + 1, s.StepType, ConfigJsonOf(s.StepType, s), s.ConditionExpression, s.TrueBranchStepOrder,
            s.FalseBranchStepOrder, s.LoopTargetStepOrder, null, s.Label))];

    /// <summary>The config blob of a stored step's own type, or null for a type that carries none.</summary>
    public static string? ConfigJsonOf(WorkflowStep s) => s.StepType switch
    {
        StepType.Extract => s.ExtractConfigJson,
        StepType.VideoAnalyze => s.VideoAnalyzeConfigJson,
        StepType.VideoCompile => s.VideoCompileConfigJson,
        StepType.EditRoom => s.EditRoomConfigJson,
        StepType.GraphicsRoom => s.GraphicsRoomConfigJson,
        StepType.ColorGradeRoom => s.ColorGradeRoomConfigJson,
        StepType.Conditional => s.ConditionalConfigJson,
        StepType.ReviewLoop => s.ReviewLoopConfigJson,
        StepType.VideoGenerate => s.VideoGenerateConfigJson,
        StepType.Voiceover => s.VoiceoverConfigJson,
        StepType.ThumbnailGenerate => s.ThumbnailGenerateConfigJson,
        StepType.DailiesRoom => s.DailiesRoomConfigJson,
        StepType.TimelineRender => s.TimelineRenderConfigJson,
        StepType.DesignSystem => s.DesignSystemConfigJson,
        StepType.ExtractFrame => s.ExtractFrameConfigJson,
        StepType.AudioGenerate => s.AudioGenerateConfigJson,
        StepType.PinInsert => s.PinInsertConfigJson,
        StepType.AudioCleanup => s.AudioCleanupConfigJson,
        StepType.VideoEnhance => s.VideoEnhanceConfigJson,
        StepType.ImageGenerate => s.ImageGenerateConfigJson,
        _ => null
    };

    /// <summary>Writes a bound config back into the column of the step's own type (the snapshot binding, lane L3).</summary>
    public static void SetConfigJson(WorkflowStep s, string? json)
    {
        switch (s.StepType)
        {
            case StepType.Extract: s.ExtractConfigJson = json; break;
            case StepType.VideoAnalyze: s.VideoAnalyzeConfigJson = json; break;
            case StepType.VideoCompile: s.VideoCompileConfigJson = json; break;
            case StepType.EditRoom: s.EditRoomConfigJson = json; break;
            case StepType.GraphicsRoom: s.GraphicsRoomConfigJson = json; break;
            case StepType.ColorGradeRoom: s.ColorGradeRoomConfigJson = json; break;
            case StepType.Conditional: s.ConditionalConfigJson = json; break;
            case StepType.ReviewLoop: s.ReviewLoopConfigJson = json; break;
            case StepType.VideoGenerate: s.VideoGenerateConfigJson = json; break;
            case StepType.Voiceover: s.VoiceoverConfigJson = json; break;
            case StepType.ThumbnailGenerate: s.ThumbnailGenerateConfigJson = json; break;
            case StepType.DailiesRoom: s.DailiesRoomConfigJson = json; break;
            case StepType.TimelineRender: s.TimelineRenderConfigJson = json; break;
            case StepType.DesignSystem: s.DesignSystemConfigJson = json; break;
            case StepType.ExtractFrame: s.ExtractFrameConfigJson = json; break;
            case StepType.AudioGenerate: s.AudioGenerateConfigJson = json; break;
            case StepType.PinInsert: s.PinInsertConfigJson = json; break;
            case StepType.AudioCleanup: s.AudioCleanupConfigJson = json; break;
            case StepType.VideoEnhance: s.VideoEnhanceConfigJson = json; break;
            case StepType.ImageGenerate: s.ImageGenerateConfigJson = json; break;
        }
    }

    internal static string? ConfigJsonOf(StepType type, WorkflowTemplateStepDefinition s) => type switch
    {
        StepType.Extract => s.ExtractConfigJson,
        StepType.VideoAnalyze => s.VideoAnalyzeConfigJson,
        StepType.VideoCompile => s.VideoCompileConfigJson,
        StepType.EditRoom => s.EditRoomConfigJson,
        StepType.GraphicsRoom => s.GraphicsRoomConfigJson,
        StepType.ColorGradeRoom => s.ColorGradeRoomConfigJson,
        StepType.Conditional => s.ConditionalConfigJson,
        StepType.ReviewLoop => s.ReviewLoopConfigJson,
        StepType.VideoGenerate => s.VideoGenerateConfigJson,
        StepType.Voiceover => s.VoiceoverConfigJson,
        StepType.ThumbnailGenerate => s.ThumbnailGenerateConfigJson,
        StepType.DailiesRoom => s.DailiesRoomConfigJson,
        StepType.TimelineRender => s.TimelineRenderConfigJson,
        StepType.DesignSystem => s.DesignSystemConfigJson,
        StepType.ExtractFrame => s.ExtractFrameConfigJson,
        StepType.AudioGenerate => s.AudioGenerateConfigJson,
        StepType.PinInsert => s.PinInsertConfigJson,
        StepType.AudioCleanup => s.AudioCleanupConfigJson,
        StepType.VideoEnhance => s.VideoEnhanceConfigJson,
        StepType.ImageGenerate => s.ImageGenerateConfigJson,
        _ => null
    };
}
