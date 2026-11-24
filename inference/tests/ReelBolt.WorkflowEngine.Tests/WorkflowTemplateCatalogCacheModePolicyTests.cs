using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Encodes the product rule "editorial/creative-decision steps must never be served from the
/// cross-execution step-result cache; only deterministic extraction/analysis steps may be" as an
/// assertion over every built-in template in <see cref="WorkflowTemplateCatalog"/>, rather than
/// leaving it as a convention a future template author has to remember. A step whose output is an
/// LLM's editorial judgment (which scenes to keep, how to translate/direct/script/author/grade/
/// score a video or composition) must re-run fresh every execution — reusing a stale decision
/// from a prior run would silently overwrite what should be a new judgment call. Deterministic
/// steps that only extract facts from code or media (the five *Analyzer agents, Extract,
/// VideoAnalyze, VideoCompile) are left at the built-in StepCachePolicy default, which already
/// treats them as cacheable.
/// </summary>
public class WorkflowTemplateCatalogCacheModePolicyTests
{
    /// <summary>
    /// Agent types whose Agent-step output is an editorial/creative decision, never a plain fact
    /// extraction. Every step in the catalog using one of these MUST be pinned to
    /// <see cref="StepCacheMode.Never"/>.
    /// </summary>
    private static readonly HashSet<AgentType> EditorialAgentTypes =
    [
        AgentType.RemotionComponentTranslator,
        AgentType.AnimationStrategyAgent,
        AgentType.ScriptwriterAgent,
        AgentType.DirectorAgent,
        AgentType.AuthorAgent,
        AgentType.ReviewAgent,
        AgentType.VideoStoryEditor,
        AgentType.MotionGraphicsPlanner,
        AgentType.MusicSupervisor,
        AgentType.SoundDesigner,
        AgentType.VideoReviewAgent,
        AgentType.Colorist,
    ];

    /// <summary>
    /// Deterministic code/media-fact-extraction agent types. Every step using one of these must be
    /// left at the built-in default (no per-step override), so it stays cacheable.
    /// </summary>
    private static readonly HashSet<AgentType> ExtractionAgentTypes =
    [
        AgentType.CodeStructureAnalyzer,
        AgentType.DependencyAnalyzer,
        AgentType.ComponentInventoryAnalyzer,
        AgentType.RouteAndApiAnalyzer,
        AgentType.StyleAndThemeExtractor,
    ];

    /// <summary>
    /// Multi-agent "room" step types are always an editorial deliberation (EditRoom/ColorGradeRoom
    /// are cacheable under the built-in StepCachePolicy purely by step-type mechanics, but their
    /// whole purpose is a fresh decision every run) — every room step must be pinned to Never.
    /// </summary>
    private static readonly HashSet<StepType> RoomStepTypes =
    [
        StepType.EditRoom,
        StepType.GraphicsRoom,
        StepType.ColorGradeRoom,
        StepType.DailiesRoom,
    ];

    public static IEnumerable<object[]> AllTemplateSteps()
    {
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
        {
            for (int i = 0; i < template.Steps.Count; i++)
                yield return [template.Key, i, template.Steps[i]];
        }
    }

    [Theory]
    [MemberData(nameof(AllTemplateSteps))]
    public void Every_editorial_or_room_step_is_pinned_to_CacheMode_Never(string templateKey, int index, WorkflowTemplateStepDefinition step)
    {
        bool isEditorial = EditorialAgentTypes.Contains(step.AgentType) || RoomStepTypes.Contains(step.StepType);
        if (!isEditorial) return;

        step.CacheMode.Should().Be(
            StepCacheMode.Never,
            $"template '{templateKey}' step {index} ('{step.Label}', {step.AgentType}/{step.StepType}) makes an editorial decision and must never be replayed from cache");
    }

    [Theory]
    [MemberData(nameof(AllTemplateSteps))]
    public void Every_deterministic_extraction_step_is_left_at_the_built_in_default(string templateKey, int index, WorkflowTemplateStepDefinition step)
    {
        bool isDeterministicExtraction = ExtractionAgentTypes.Contains(step.AgentType)
            || step.StepType is StepType.Extract or StepType.VideoAnalyze or StepType.VideoCompile;
        if (!isDeterministicExtraction) return;

        step.CacheMode.Should().BeNull(
            $"template '{templateKey}' step {index} ('{step.Label}', {step.AgentType}/{step.StepType}) is deterministic extraction and should stay cacheable via the built-in default, not carry a per-step override");
    }
}
