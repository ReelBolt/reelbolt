using System;
using System.Collections.Generic;
using System.Linq;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;

namespace ReelBolt.WorkflowEngine.Tests.Compositions;

/// <summary>Workflow shapes for the replay tests: the real staging workflow, every derush template, and synthetic edge cases.</summary>
internal static class ReplayShapes
{
    public static WorkflowStep Step(
        int order,
        AgentType agent,
        StepType type = StepType.Agent,
        AgentInputContextMode? mode = null,
        string? analyze = null,
        string? compile = null,
        string? editRoom = null,
        string? voiceover = null,
        string? generate = null,
        int? loopTarget = null,
        string? label = null,
        string? schema = null)
    {
        AgentDefinition definition = new()
        {
            Id = Guid.NewGuid(),
            Name = agent.ToString(),
            AgentType = agent,
            IsBuiltIn = agent != AgentType.Custom,
            OutputSchemaName = schema
        };
        return new WorkflowStep
        {
            Id = Guid.NewGuid(),
            StepOrder = order,
            StepType = type,
            AgentDefinitionId = definition.Id,
            AgentDefinition = definition,
            AgentInputContextMode = mode,
            VideoAnalyzeConfigJson = analyze,
            VideoCompileConfigJson = compile,
            EditRoomConfigJson = editRoom,
            VoiceoverConfigJson = voiceover,
            VideoGenerateConfigJson = generate,
            LoopTargetStepOrder = loopTarget,
            Label = label ?? $"{order}. {agent}",
            MaxIterations = 3
        };
    }

    /// <summary>
    /// Workflow e410f6ae "Animated film + edit room cut" on staging, verbatim configs:
    /// translator → animation strategy → director → AUTHOR (renders) → analyze (render + 5 photos) →
    /// edit room → compile (the deliverable) → review looping back to the edit room.
    /// </summary>
    public static List<WorkflowStep> AnimatedFilmEditRoomCut() =>
    [
        Step(1, AgentType.RemotionComponentTranslator, mode: AgentInputContextMode.FullWorkflow),
        Step(2, AgentType.AnimationStrategyAgent, mode: AgentInputContextMode.PreviousStepOnly),
        Step(3, AgentType.DirectorAgent, mode: AgentInputContextMode.FullWorkflow),
        Step(4, AgentType.AuthorAgent, mode: AgentInputContextMode.FullWorkflow),
        Step(5, AgentType.VideoTransform, StepType.VideoAnalyze, analyze:
            """{"source": {"kind": "StepOutput", "stepOrder": 4}, "vision": "Optional", "sources": [{"kind": "StepOutput", "stepOrder": 4}, {"kind": "ProjectFile", "projectFileId": "f6681b28-8488-4738-ae1f-83a7ba40d4e0"}, {"kind": "ProjectFile", "projectFileId": "0dd299fa-d58c-4ffc-a6c4-8c144cd94db7"}], "version": 1, "transcription": "Optional"}"""),
        Step(6, AgentType.VideoTransform, StepType.EditRoom, editRoom:
            """{"view": {"from": "Previous"}, "seats": [{"name": "Pacing editor", "persona": "x"}], "rounds": 2, "version": 1, "maxTurns": 8, "persistTranscript": true, "fallbackToSoloEditor": true}"""),
        Step(7, AgentType.VideoTransform, StepType.VideoCompile, compile:
            """{"mode": "Reencode", "version": 1, "decision": {"from": "Step", "stepOrder": 6}, "minSegmentMs": 800, "transitionPolicy": "Auto", "analysisStepOrder": 5, "targetDurationSec": 120}"""),
        Step(8, AgentType.VideoReviewAgent, StepType.ReviewLoop, mode: AgentInputContextMode.FullWorkflow, loopTarget: 6)
    ];

    /// <summary>A template's steps as the provisioning service would create them (agent types and configs).</summary>
    public static List<WorkflowStep> FromTemplate(string key)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(key)
            ?? throw new ArgumentException($"No template {key}");
        return [.. template.Steps.Select((d, i) =>
        {
            WorkflowStep step = Step(i + 1, d.AgentType, d.StepType, d.AgentInputContextMode,
                d.VideoAnalyzeConfigJson, d.VideoCompileConfigJson, d.EditRoomConfigJson, d.VoiceoverConfigJson,
                d.VideoGenerateConfigJson, d.LoopTargetStepOrder, d.Label);
            step.ExtractConfigJson = d.ExtractConfigJson;
            step.GraphicsRoomConfigJson = d.GraphicsRoomConfigJson;
            step.ColorGradeRoomConfigJson = d.ColorGradeRoomConfigJson;
            step.ConditionExpression = d.ConditionExpression;
            step.TrueBranchStepOrder = d.TrueBranchStepOrder;
            step.FalseBranchStepOrder = d.FalseBranchStepOrder;
            step.SelectedPriorStepOrdersJson = d.SelectedPriorStepOrders is { } sel ? System.Text.Json.JsonSerializer.Serialize(sel) : null;
            step.MaxIterations = d.MaxIterations;
            return step;
        })];
    }

    /// <summary>
    /// A derush template with an animated film in front of it: an Author step at order 1 renders the film,
    /// every template step moves one place down (every step-order reference in its config with it), and
    /// the first analysis reads the film as well as the project's footage — the "animated film + edit"
    /// shape, for every editing template the catalog ships.
    /// </summary>
    public static List<WorkflowStep> FilmThen(string templateKey)
    {
        List<WorkflowStep> template = FromTemplate(templateKey);
        List<WorkflowStep> steps = [Step(1, AgentType.AuthorAgent, mode: AgentInputContextMode.FullWorkflow)];
        bool firstAnalysis = true;
        foreach (WorkflowStep step in template)
        {
            step.StepOrder += 1;
            if (step.LoopTargetStepOrder is int target)
                step.LoopTargetStepOrder = target + 1;
            step.VideoCompileConfigJson = Shift(step.VideoCompileConfigJson);
            step.EditRoomConfigJson = Shift(step.EditRoomConfigJson);
            step.GraphicsRoomConfigJson = Shift(step.GraphicsRoomConfigJson);
            step.ColorGradeRoomConfigJson = Shift(step.ColorGradeRoomConfigJson);
            step.VoiceoverConfigJson = Shift(step.VoiceoverConfigJson);
            step.VideoGenerateConfigJson = Shift(step.VideoGenerateConfigJson);
            step.VideoAnalyzeConfigJson = Shift(step.VideoAnalyzeConfigJson);
            if (step.StepType == StepType.VideoAnalyze && firstAnalysis)
            {
                step.VideoAnalyzeConfigJson =
                    """{"version":1,"source":{"kind":"StepOutput","stepOrder":1},"sources":[{"kind":"StepOutput","stepOrder":1},{"kind":"ProjectFile","projectFileId":"11111111-1111-1111-1111-111111111111"}]}""";
                firstAnalysis = false;
            }

            steps.Add(step);
        }

        return steps;
    }

    private static string? Shift(string? json) => json is null
        ? null
        : System.Text.RegularExpressions.Regex.Replace(json, "(\"[A-Za-z]*[sS]tepOrder\"\\s*:\\s*)(\\d+)",
            m => m.Groups[1].Value + (int.Parse(m.Groups[2].Value) + 1));

    /// <summary>Every step order mapped to a fresh result id — "the source run finished every step".</summary>
    public static Dictionary<int, Guid> AllRan(IEnumerable<WorkflowStep> steps) =>
        steps.ToDictionary(s => s.StepOrder, _ => Guid.NewGuid());
}
