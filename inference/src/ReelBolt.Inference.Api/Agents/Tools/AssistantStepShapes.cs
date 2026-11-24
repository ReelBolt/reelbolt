using System.Text.Json;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.Inference.Api.Agents.Tools;

/// <summary>
/// Projects stored workflow steps and template steps into the exact <see cref="CreateWorkflowStepRequest"/>
/// shape <c>ValidateWorkflow</c>/<c>ProposeWorkflow</c> accept, so the assistant can read a workflow
/// (or a template), change one field, and propose the whole list back without re-typing any config.
/// </summary>
internal static class AssistantStepShapes
{
    internal static CreateWorkflowStepRequest FromStep(WorkflowStep s) => new(
        s.AgentDefinitionId,
        s.StepOrder,
        s.EdgeConditionJson,
        s.Label,
        StepType: s.StepType.ToString(),
        ConditionExpression: s.ConditionExpression,
        LoopSourceExpression: s.LoopSourceExpression,
        LoopTargetStepOrder: s.LoopTargetStepOrder,
        MaxIterations: s.MaxIterations,
        MinScore: s.MinScore,
        InputMappingJson: s.InputMappingJson,
        AgentInputContextMode: s.AgentInputContextMode?.ToString(),
        SelectedPriorStepOrdersJson: s.SelectedPriorStepOrdersJson,
        TrueBranchStepOrder: s.TrueBranchStepOrder,
        FalseBranchStepOrder: s.FalseBranchStepOrder,
        ParallelAgentIdsJson: s.ParallelAgentIdsJson,
        ExtractConfigJson: s.ExtractConfigJson,
        VideoAnalyzeConfigJson: s.VideoAnalyzeConfigJson,
        VideoCompileConfigJson: s.VideoCompileConfigJson,
        EditRoomConfigJson: s.EditRoomConfigJson,
        GraphicsRoomConfigJson: s.GraphicsRoomConfigJson,
        ColorGradeRoomConfigJson: s.ColorGradeRoomConfigJson,
        ConditionalConfigJson: s.ConditionalConfigJson,
        ReviewLoopConfigJson: s.ReviewLoopConfigJson,
        CacheMode: s.CacheMode?.ToString(),
        VideoGenerateConfigJson: s.VideoGenerateConfigJson,
        VoiceoverConfigJson: s.VoiceoverConfigJson,
        ThumbnailGenerateConfigJson: s.ThumbnailGenerateConfigJson,
        DailiesRoomConfigJson: s.DailiesRoomConfigJson, TimelineRenderConfigJson: s.TimelineRenderConfigJson,
        DesignSystemConfigJson: s.DesignSystemConfigJson,
        ContinueCompositionJson: s.ContinueCompositionJson,
        RegisterOutputAsProjectFile: s.RegisterOutputAsProjectFile,
        ExtractFrameConfigJson: s.ExtractFrameConfigJson,
        AudioGenerateConfigJson: s.AudioGenerateConfigJson,
        PinInsertConfigJson: s.PinInsertConfigJson,
        AudioCleanupConfigJson: s.AudioCleanupConfigJson,
        VideoEnhanceConfigJson: s.VideoEnhanceConfigJson,
        ImageGenerateConfigJson: s.ImageGenerateConfigJson);

    /// <summary>
    /// A template step with its agent TYPE resolved to the built-in agent row's id, exactly as
    /// <c>WorkflowTemplateProvisioningService</c> does when a template is applied from the UI.
    /// </summary>
    internal static CreateWorkflowStepRequest FromTemplateStep(
        WorkflowTemplateStepDefinition s,
        int stepOrder,
        IReadOnlyDictionary<AgentType, Guid> builtInAgentIds)
    {
        string? parallel = s.ParallelAgentTypes is { Count: > 0 }
            ? JsonSerializer.Serialize(s.ParallelAgentTypes.Distinct()
                .Where(builtInAgentIds.ContainsKey)
                .Select(t => builtInAgentIds[t]))
            : null;

        return new CreateWorkflowStepRequest(
            builtInAgentIds[s.AgentType],
            stepOrder,
            null,
            s.Label,
            StepType: s.StepType.ToString(),
            ConditionExpression: s.ConditionExpression,
            LoopSourceExpression: s.LoopSourceExpression,
            LoopTargetStepOrder: s.LoopTargetStepOrder,
            MaxIterations: s.MaxIterations,
            MinScore: s.MinScore,
            InputMappingJson: s.InputMappingJson,
            AgentInputContextMode: s.AgentInputContextMode?.ToString(),
            SelectedPriorStepOrdersJson: s.SelectedPriorStepOrders is { Count: > 0 }
                ? JsonSerializer.Serialize(s.SelectedPriorStepOrders)
                : null,
            TrueBranchStepOrder: s.TrueBranchStepOrder,
            FalseBranchStepOrder: s.FalseBranchStepOrder,
            ParallelAgentIdsJson: parallel,
            ExtractConfigJson: s.ExtractConfigJson,
            VideoAnalyzeConfigJson: s.VideoAnalyzeConfigJson,
            VideoCompileConfigJson: s.VideoCompileConfigJson,
            EditRoomConfigJson: s.EditRoomConfigJson,
            GraphicsRoomConfigJson: s.GraphicsRoomConfigJson,
            ColorGradeRoomConfigJson: s.ColorGradeRoomConfigJson,
            ConditionalConfigJson: s.ConditionalConfigJson,
            ReviewLoopConfigJson: s.ReviewLoopConfigJson,
            CacheMode: s.CacheMode?.ToString(),
            VideoGenerateConfigJson: s.VideoGenerateConfigJson,
            VoiceoverConfigJson: s.VoiceoverConfigJson,
            ThumbnailGenerateConfigJson: s.ThumbnailGenerateConfigJson,
            DailiesRoomConfigJson: s.DailiesRoomConfigJson, TimelineRenderConfigJson: s.TimelineRenderConfigJson,
            DesignSystemConfigJson: s.DesignSystemConfigJson,
            ExtractFrameConfigJson: s.ExtractFrameConfigJson,
            AudioGenerateConfigJson: s.AudioGenerateConfigJson,
            PinInsertConfigJson: s.PinInsertConfigJson,
            AudioCleanupConfigJson: s.AudioCleanupConfigJson,
            VideoEnhanceConfigJson: s.VideoEnhanceConfigJson,
            ImageGenerateConfigJson: s.ImageGenerateConfigJson);
    }

    /// <summary>Bounds a free-text field echoed to the model, with a visible marker when cut.</summary>
    internal static string? Truncate(string? text, int max) =>
        text is null || text.Length <= max ? text : text[..max] + $"… [truncated, {text.Length - max} more chars]";
}
