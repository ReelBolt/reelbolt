using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems;

namespace ReelBolt.Shared.History;

/// <summary>
/// Canonical JSON bodies for the non-file kinds a project version captures. Every builder returns
/// <see cref="CanonicalJson.Serialize"/> output, so two structurally equal documents produce the
/// same bytes (and therefore the same <c>document_sha256</c>) whatever the order of their JSON keys
/// or the order the rows were read in.
/// </summary>
public static class ProjectHistoryDocuments
{
    /// <summary>
    /// A Standard workflow: its user-meaningful definition fields and every step in
    /// <see cref="WorkflowStep.StepOrder"/> order with every config column. Each step carries the
    /// agent id AND the stable agent identity (type + name) so a restore can re-check it. Result ids
    /// and execution ids never appear.
    /// </summary>
    public static string Workflow(WorkflowDefinition workflow)
    {
        JsonArray steps = new();
        foreach (WorkflowStep step in workflow.Steps
                     .OrderBy(s => s.StepOrder)
                     .ThenBy(s => s.Id))
        {
            steps.Add(StepNode(step));
        }

        JsonObject root = new()
        {
            ["name"] = workflow.Name,
            ["requiresUserInput"] = workflow.RequiresUserInput,
            ["createdVia"] = Str(workflow.CreatedVia),
            ["templateKey"] = Str(workflow.TemplateKey),
            ["appManifest"] = Parse(workflow.AppManifestJson),
            ["steps"] = steps
        };

        return CanonicalJson.Serialize(root);
    }

    /// <summary>A timeline document: its display name, revision and the canonicalized document body.</summary>
    public static string Timeline(EditTimelineEntity timeline)
    {
        JsonObject root = new()
        {
            ["name"] = timeline.Name,
            ["revision"] = timeline.Revision,
            ["document"] = Parse(timeline.DocumentJson)
        };

        return CanonicalJson.Serialize(root);
    }

    /// <summary>The whole pronunciation entry set, sorted by term (ordinal).</summary>
    public static string Pronunciation(IEnumerable<ProjectPronunciationEntry> entries)
    {
        JsonArray array = new();

        foreach (ProjectPronunciationEntry entry in entries
                     .OrderBy(e => e.Term, StringComparer.Ordinal)
                     .ThenBy(e => e.Spelling, StringComparer.Ordinal))
        {
            array.Add(new JsonObject
            {
                ["term"] = entry.Term,
                ["spelling"] = entry.Spelling,
                ["caseSensitive"] = entry.CaseSensitive
            });
        }

        return CanonicalJson.Serialize(array);
    }

    /// <summary>The project's user-editable identity: <c>{name, description}</c>.</summary>
    public static string Project(Project project) => Project(project.Name, project.Description);

    /// <summary>The project's user-editable identity: <c>{name, description}</c>.</summary>
    public static string Project(string name, string? description) =>
        CanonicalJson.Serialize(new JsonObject
        {
            ["name"] = name,
            ["description"] = Str(description)
        });

    /// <summary>Lowercase hex SHA-256 of a canonical body.</summary>
    public static string Sha256(string canonicalJson) => CanonicalJson.Sha256Hex(canonicalJson);

    /// <summary>The UTF-8 byte length of a canonical body (the document row's <c>size_bytes</c>).</summary>
    public static long Utf8Size(string canonicalJson) => Encoding.UTF8.GetByteCount(canonicalJson);

    private static JsonObject StepNode(WorkflowStep step)
    {
        return new JsonObject
        {
            ["order"] = step.StepOrder,
            ["label"] = Str(step.Label),
            ["stepType"] = step.StepType.ToString(),
            ["agentId"] = step.AgentDefinitionId.ToString("D"),
            ["agentName"] = Str(step.AgentDefinition?.Name),
            ["agentType"] = Str(step.AgentDefinition?.AgentType.ToString()),
            ["edgeCondition"] = Parse(step.EdgeConditionJson),
            ["conditionExpression"] = Str(step.ConditionExpression),
            ["loopSourceExpression"] = Str(step.LoopSourceExpression),
            ["loopTargetStepOrder"] = Num(step.LoopTargetStepOrder),
            ["maxIterations"] = step.MaxIterations,
            ["minScore"] = Num(step.MinScore),
            ["inputMapping"] = Parse(step.InputMappingJson),
            ["agentInputContextMode"] = Str(step.AgentInputContextMode?.ToString()),
            ["selectedPriorStepOrders"] = Parse(step.SelectedPriorStepOrdersJson),
            ["trueBranchStepOrder"] = Str(step.TrueBranchStepOrder),
            ["falseBranchStepOrder"] = Str(step.FalseBranchStepOrder),
            ["parallelAgentIds"] = Parse(step.ParallelAgentIdsJson),
            ["extractConfig"] = Parse(step.ExtractConfigJson),
            ["videoAnalyzeConfig"] = Parse(step.VideoAnalyzeConfigJson),
            ["videoCompileConfig"] = Parse(step.VideoCompileConfigJson),
            ["videoGenerateConfig"] = Parse(step.VideoGenerateConfigJson),
            ["editRoomConfig"] = Parse(step.EditRoomConfigJson),
            ["graphicsRoomConfig"] = Parse(step.GraphicsRoomConfigJson),
            ["colorGradeRoomConfig"] = Parse(step.ColorGradeRoomConfigJson),
            ["conditionalConfig"] = Parse(step.ConditionalConfigJson),
            ["reviewLoopConfig"] = Parse(step.ReviewLoopConfigJson),
            ["voiceoverConfig"] = Parse(step.VoiceoverConfigJson),
            ["thumbnailGenerateConfig"] = Parse(step.ThumbnailGenerateConfigJson),
            ["timelineRenderConfig"] = Parse(step.TimelineRenderConfigJson),
            ["dailiesRoomConfig"] = Parse(step.DailiesRoomConfigJson),
            ["designSystemConfig"] = Parse(step.DesignSystemConfigJson),
            ["extractFrameConfig"] = Parse(step.ExtractFrameConfigJson),
            ["audioGenerateConfig"] = Parse(step.AudioGenerateConfigJson),
            ["pinInsertConfig"] = Parse(step.PinInsertConfigJson),
            ["continueComposition"] = Parse(step.ContinueCompositionJson),
            ["registerOutputAsProjectFile"] = step.RegisterOutputAsProjectFile,
            ["cacheMode"] = Str(step.CacheMode?.ToString())
        };
    }

    private static JsonNode? Str(string? value) => value is null ? null : JsonValue.Create(value);

    private static JsonNode? Num(int? value) => value is null ? null : JsonValue.Create(value.Value);

    /// <summary>
    /// Parses a jsonb text column into a JSON node so its keys are canonicalized as JSON, not kept
    /// as an opaque string. Invalid JSON is kept as the raw string (never silently dropped).
    /// </summary>
    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return JsonValue.Create(json);
        }
    }
}
