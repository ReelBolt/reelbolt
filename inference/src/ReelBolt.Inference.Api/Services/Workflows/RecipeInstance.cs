using System.Text.Json;
using System.Text.Json.Nodes;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Briefs;
using ReelBolt.Shared.Briefs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;

namespace ReelBolt.Inference.Api.Services.Workflows;

/// <summary>
/// A recipe turned into workflow steps, server side: the template's steps, the brief's bound settings and the closed
/// patches (<see cref="RecipePatches"/>), in that order. Pure and deterministic, so the tool that proposes the
/// workflow and the card that describes it build the very same steps.
/// </summary>
public static class RecipeInstance
{
    /// <summary>The agent types the patch catalog can add or look for; the built-in rows of these are loaded when a patch is asked for.</summary>
    public static readonly IReadOnlyList<AgentType> PatchAgentTypes =
    [
        AgentType.VideoTransform, AgentType.VideoStoryEditor, AgentType.NarrationWriter, AgentType.MusicSupervisor,
        AgentType.SoundDesigner, AgentType.MotionGraphicsPlanner, AgentType.ScriptwriterAgent, AgentType.DirectorAgent,
        AgentType.OverlayPlanner,
    ];

    /// <summary>The agent types the template's own steps use (including parallel members).</summary>
    public static IReadOnlySet<AgentType> AgentTypesOf(WorkflowTemplateDefinition template) =>
        template.Steps.Select(s => s.AgentType)
            .Concat(template.Steps.Where(s => s.ParallelAgentTypes is { Count: > 0 }).SelectMany(s => s.ParallelAgentTypes!))
            .ToHashSet();

    /// <summary>
    /// The steps of <paramref name="template"/> with <paramref name="patchIds"/> (already normalized) applied and the
    /// brief's bound settings filled in. <c>Steps</c> is null with the plain reason in <c>Refusal</c> when a patch cannot apply.
    /// </summary>
    public static RecipePatchOutcome Build(
        WorkflowTemplateDefinition template,
        IReadOnlyList<string> patchIds,
        RunBrief? brief,
        IReadOnlyDictionary<AgentType, Guid> agentIds)
    {
        // The three video apps carry their extras as fields of the app page, so a patch has nothing to add to.
        if (patchIds.Count > 0 && VideoAppTemplates.Keys.Contains(template.Key))
            return RecipePatchOutcome.Refuse($"{patchIds[0]}: this app has a field for that: set it on the app page instead of patching the steps.");
        List<CreateWorkflowStepRequest> steps =
            [.. template.Steps.Select((s, i) => AssistantStepShapes.FromTemplateStep(s, i + 1, agentIds))];
        RecipePatchOutcome patched = RecipePatches.ApplyAll(steps, patchIds, agentIds);
        return patched.Steps is null ? patched : RecipePatchOutcome.Ok(Bind(patched.Steps, brief));
    }

    /// <summary>
    /// The brief fields a deterministic step carries in its own config (docs/design/workflow-apps.md 3.4): the narration
    /// language and project voice on a Voiceover step, the target length on the compile step. Only where the recipe left
    /// the setting empty, exactly as the run-time override does; every other brief field travels with the run.
    /// </summary>
    public static IReadOnlyList<CreateWorkflowStepRequest> Bind(IReadOnlyList<CreateWorkflowStepRequest> steps, RunBrief? brief)
    {
        if (brief is null)
            return steps;

        string? language = brief.Language is { } l && RunBriefValidator.TryNormalizeLanguage(l, out string normalized) ? normalized : null;
        return
        [
            .. steps.Select(step => step.StepType switch
            {
                nameof(StepType.Voiceover) when step.VoiceoverConfigJson is { } json && (language is not null || brief.Voice?.ProjectVoiceId is not null)
                    => step with { VoiceoverConfigJson = SetIfEmpty(json, ("language", language is null ? null : JsonValue.Create(language)),
                        ("projectVoiceId", brief.Voice?.ProjectVoiceId is { } voice ? JsonValue.Create(voice) : null)) },
                nameof(StepType.VideoCompile) when step.VideoCompileConfigJson is { } json && brief.LengthSec is { } length
                    => step with { VideoCompileConfigJson = SetIfEmpty(json, ("targetDurationSec", JsonValue.Create(length))) },
                _ => step,
            }),
        ];
    }

    /// <summary>The brief as stored in a confirmation payload: valid fields only, language normalized, nulls omitted.</summary>
    public static string? NormalizeBrief(RunBrief? brief)
    {
        if (brief is null)
            return null;
        RunBrief normalized = brief with
        {
            Language = brief.Language is { } l && RunBriefValidator.TryNormalizeLanguage(l, out string a) ? a : brief.Language,
            SourceLanguage = brief.SourceLanguage is { } s && RunBriefValidator.TryNormalizeLanguage(s, out string b) ? b : brief.SourceLanguage,
            DesignSystem = brief.DesignSystem is null ? null : RunBriefValidator.NormalizeDesignSystem(brief.DesignSystem) ?? brief.DesignSystem,
        };
        string json = JsonSerializer.Serialize(normalized, RunBriefJson.Options);
        return json == "{}" ? null : json;
    }

    private static string SetIfEmpty(string json, params (string Key, JsonNode? Value)[] members)
    {
        JsonObject obj = JsonNode.Parse(json)!.AsObject();
        foreach ((string key, JsonNode? value) in members)
        {
            if (value is null)
                continue;
            bool empty = obj[key] is null
                || (obj[key] is JsonValue existing && existing.TryGetValue(out int number) && number == 0);
            if (empty)
                obj[key] = value;
        }

        return obj.ToJsonString();
    }
}
