using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ReelBolt.Inference.Api.Services.Marketplace.Snapshot;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Tests.Marketplace;

internal static class SnapshotFixtures
{
    public static readonly IReadOnlyDictionary<AgentType, Guid> BuiltInIds =
        Enum.GetValues<AgentType>().ToDictionary(t => t, t => new Guid(t.GetHashCode() + 1, 7, 7, 1, 2, 3, 4, 5, 6, 7, 8));

    public static IReadOnlyDictionary<Guid, AgentDefinition> BuiltIns() =>
        BuiltInIds.ToDictionary(kv => kv.Value, kv => new AgentDefinition { Id = kv.Value, AgentType = kv.Key, IsBuiltIn = true, Name = kv.Key.ToString() });

    public static WorkflowDefinition Workflow(WorkflowDefinitionKind kind = WorkflowDefinitionKind.Standard) =>
        new() { Id = Guid.NewGuid(), Name = "wf", ProjectId = Guid.NewGuid(), Kind = kind };

    public static WorkflowStep Step(int order, StepType type, AgentType agent = AgentType.VideoTransform) =>
        new() { Id = Guid.NewGuid(), StepOrder = order, StepType = type, AgentDefinitionId = BuiltInIds[agent], Label = $"Step {order}" };

    public static List<WorkflowStep> FromTemplate(WorkflowTemplateDefinition template)
    {
        List<WorkflowStep> steps = [];
        int order = 1;
        foreach (WorkflowTemplateStepDefinition s in template.Steps)
        {
            WorkflowStep step = new()
            {
                Id = Guid.NewGuid(),
                StepOrder = order++,
                AgentDefinitionId = BuiltInIds[s.AgentType],
                Label = s.Label,
                StepType = s.StepType,
                ConditionExpression = s.ConditionExpression,
                LoopSourceExpression = s.LoopSourceExpression,
                LoopTargetStepOrder = s.LoopTargetStepOrder,
                MaxIterations = s.MaxIterations,
                MinScore = s.MinScore,
                InputMappingJson = s.InputMappingJson,
                AgentInputContextMode = s.AgentInputContextMode,
                SelectedPriorStepOrdersJson = s.SelectedPriorStepOrders is { Count: > 0 } o ? System.Text.Json.JsonSerializer.Serialize(o) : null,
                TrueBranchStepOrder = s.TrueBranchStepOrder,
                FalseBranchStepOrder = s.FalseBranchStepOrder,
                ParallelAgentIdsJson = s.ParallelAgentTypes is { Count: > 0 }
                    ? System.Text.Json.JsonSerializer.Serialize(s.ParallelAgentTypes.Select(t => BuiltInIds[t]))
                    : null,
                ExtractConfigJson = s.ExtractConfigJson,
                VideoAnalyzeConfigJson = s.VideoAnalyzeConfigJson,
                VideoCompileConfigJson = s.VideoCompileConfigJson,
                EditRoomConfigJson = s.EditRoomConfigJson,
                GraphicsRoomConfigJson = s.GraphicsRoomConfigJson,
                ColorGradeRoomConfigJson = s.ColorGradeRoomConfigJson,
                ConditionalConfigJson = s.ConditionalConfigJson,
                ReviewLoopConfigJson = s.ReviewLoopConfigJson,
                CacheMode = s.CacheMode,
                ThumbnailGenerateConfigJson = s.ThumbnailGenerateConfigJson,
                VideoGenerateConfigJson = s.VideoGenerateConfigJson,
                VoiceoverConfigJson = s.VoiceoverConfigJson,
                DailiesRoomConfigJson = s.DailiesRoomConfigJson,
                TimelineRenderConfigJson = s.TimelineRenderConfigJson,
                DesignSystemConfigJson = s.DesignSystemConfigJson,
                ExtractFrameConfigJson = s.ExtractFrameConfigJson,
                AudioGenerateConfigJson = s.AudioGenerateConfigJson,
                PinInsertConfigJson = s.PinInsertConfigJson,
                AudioCleanupConfigJson = s.AudioCleanupConfigJson,
                VideoEnhanceConfigJson = s.VideoEnhanceConfigJson
            };
            steps.Add(step);
        }

        return steps;
    }

    public static void SetConfig(WorkflowStep step, JsonNode config) => StepConfigColumns.Set(step, config.ToJsonString());

    /// <summary>
    /// A config for <paramref name="type"/> with every property present: numbers 1, enums their first member, text "hello text", other
    /// strings "x", and <paramref name="leaf"/> deciding the id and slot-string properties (null leaves the property out).
    /// </summary>
    public static JsonObject Synthesize(Type type, Func<ConfigProperty, JsonNode?> leaf, int depth = 0)
    {
        JsonObject obj = [];
        if (depth > 6)
            return obj;
        foreach (ConfigProperty p in ConfigReflection.PropertiesOf(type))
        {
            JsonNode? value;
            if (p.IsComplex)
            {
                value = p.IsDictionary ? new JsonObject { ["k1"] = Synthesize(p.ValueType, leaf, depth + 1) }
                    : p.IsCollection ? new JsonArray(Synthesize(p.ValueType, leaf, depth + 1))
                    : Synthesize(p.ValueType, leaf, depth + 1);
            }
            else if (p.IsPrimitive)
            {
                JsonNode one = p.ValueType == typeof(bool) ? JsonValue.Create(true)
                    : p.ValueType.IsEnum ? JsonValue.Create(Enum.GetNames(p.ValueType)[0])
                    : JsonValue.Create(1);
                value = p.IsDictionary ? new JsonObject { ["1"] = one } : p.IsCollection ? new JsonArray(one) : one;
            }
            else
            {
                value = leaf(p);
            }

            if (value is not null)
                obj[p.JsonName] = value;
        }

        return obj;
    }
}
