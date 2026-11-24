using ReelBolt.WorkflowEngine.Execution;
using System;
using AwesomeAssertions;
using ReelBolt.Shared.Agents;
using ReelBolt.Shared.Data.Models;
using ReelBolt.WorkflowEngine.Execution.Caching;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Coverage for <see cref="StepCachePolicy.IsCacheable"/>: the per-StepType decision table, the
/// per-step <see cref="StepCacheMode"/> override, and the Agent-step tool-scope check that reads
/// <see cref="ReelBolt.Shared.Agents.ToolGroupCatalog"/> directly (so it can never drift from what
/// <c>AgentToolProvider</c> actually grants at runtime).
/// </summary>
public class StepCachePolicyTests
{
    private static readonly IStepCachePolicy Policy = new StepCachePolicy();

    private static WorkflowStep AgentStep(AgentType agentType, StepCacheMode? cacheMode = null) => new()
    {
        StepType = StepType.Agent,
        CacheMode = cacheMode,
        AgentDefinition = new AgentDefinition { AgentType = agentType }
    };

    // -----------------------------------------------------------------
    // Per-StepType decision table (deterministic non-Agent step types)
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(StepType.Extract, true)]
    [InlineData(StepType.VideoAnalyze, true)]
    [InlineData(StepType.VideoCompile, true)]
    [InlineData(StepType.EditRoom, true)]
    [InlineData(StepType.ColorGradeRoom, true)]
    [InlineData(StepType.DailiesRoom, true)]
    [InlineData(StepType.GraphicsRoom, false)]
    [InlineData(StepType.Conditional, false)]
    [InlineData(StepType.ForEach, false)]
    [InlineData(StepType.Parallel, false)]
    [InlineData(StepType.ReviewLoop, false)]
    public void IsCacheable_matches_the_default_StepType_table(StepType stepType, bool expected)
    {
        var step = new WorkflowStep
        {
            StepType = stepType,
            // Deterministic step types carry the placeholder agent row; give them one so a naive
            // future implementation that accidentally reads AgentDefinition for these types doesn't
            // NRE instead of reaching the intended StepType arm.
            AgentDefinition = new AgentDefinition { AgentType = AgentType.VideoTransform }
        };

        Policy.IsCacheable(step).Should().Be(expected);
    }

    [Fact]
    public void ReviewLoop_is_never_cacheable_even_with_a_normally_cacheable_agent_type()
    {
        var step = new WorkflowStep
        {
            StepType = StepType.ReviewLoop,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.VideoReviewAgent }
        };

        Policy.IsCacheable(step).Should().BeFalse(
            because: "a ReviewLoop step's whole purpose is to re-judge FRESH output — serving a stale verdict would defeat it");
    }

    /// <summary>
    /// The ReviewAgent's preview grant (ToolGroup.SandboxPreview: read-only stills, no upload) leaves
    /// its cache behaviour exactly as before: inside a ReviewLoop it is never served from cache, and
    /// as a plain Agent step it stays cacheable, because it holds none of
    /// ProjectWrite/SandboxAuthoring/SandboxRender.
    /// </summary>
    [Fact]
    public void ReviewAgent_cache_behaviour_is_unchanged_by_its_preview_grant()
    {
        Policy.IsCacheable(new WorkflowStep
        {
            StepType = StepType.ReviewLoop,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.ReviewAgent }
        }).Should().BeFalse(because: "a ReviewLoop re-judges fresh output");

        Policy.IsCacheable(AgentStep(AgentType.ReviewAgent)).Should().BeTrue(
            because: "SandboxPreview renders scratch stills only; it is not a state-mutating grant");

        ToolGroupCatalog.GroupsFor(AgentType.ReviewAgent).Should().Contain(ToolGroup.SandboxPreview)
            .And.NotContain(ToolGroup.SandboxRender);
    }

    [Fact]
    public void GraphicsRoom_is_never_cacheable()
    {
        var step = new WorkflowStep
        {
            StepType = StepType.GraphicsRoom,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.VideoTransform }
        };

        Policy.IsCacheable(step).Should().BeFalse(
            because: "its director/seats may render a real overlay asset as a side effect of producing their output");
    }

    // -----------------------------------------------------------------
    // Agent-step tool-scope check
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(AgentType.AuthorAgent)]
    [InlineData(AgentType.RemotionComponentTranslator)]
    [InlineData(AgentType.MotionGraphicsPlanner)]
    [InlineData(AgentType.MotionGraphicsDirector)]
    public void Agent_steps_with_state_mutating_tool_grants_are_not_cacheable(AgentType agentType)
    {
        Policy.IsCacheable(AgentStep(agentType)).Should().BeFalse(
            because: $"{agentType} is granted at least one of ProjectWrite/SandboxAuthoring/SandboxRender");
    }

    [Theory]
    [InlineData(AgentType.ScriptwriterAgent)]
    [InlineData(AgentType.DirectorAgent)]
    [InlineData(AgentType.ShotDirector)]
    [InlineData(AgentType.NarrationWriter)]
    public void Agent_steps_granted_WebSearch_stay_cacheable_until_they_actually_search(AgentType agentType)
    {
        Policy.IsCacheable(AgentStep(agentType)).Should().BeTrue(
            because: $"{agentType} had the tool but a step that did not search may be cached; IsAdmissible refuses one that did");
    }

    [Fact]
    public void A_result_that_made_a_web_search_is_never_admissible_even_with_CacheMode_Always()
    {
        WorkflowStep step = AgentStep(AgentType.ScriptwriterAgent);
        step.CacheMode = StepCacheMode.Always;
        Policy.IsCacheable(step).Should().BeTrue();

        Policy.IsAdmissible(new StepExecutionResult { Output = "x", NextStepIndex = 1, WebSearchCalls = 1 }).Should().BeFalse();
        Policy.IsAdmissible(new StepExecutionResult { Output = "x", NextStepIndex = 1, WebSearchCalls = 0 }).Should().BeTrue();
    }

    [Fact]
    public void A_custom_agent_is_never_granted_WebSearch_so_stays_cacheable()
    {
        ToolGroupCatalog.GroupsFor(AgentType.Custom).Should().NotContain(ToolGroup.WebSearch);
        Policy.IsCacheable(AgentStep(AgentType.Custom)).Should().BeTrue(
            because: "a custom agent holds no WebSearch (it has no way to opt in) and no state-mutating group");
    }

    /// <summary>
    /// Decided on purpose: a step that can look at a moment (LookAtMoment) stays cacheable, and a result that did look may
    /// be stored. A look reads frames the analysis stored (they never change) and writes nothing; the analysis is in the
    /// key through the step's input, and the default Vision provider through the resolved-provider fingerprint. Unlike a
    /// web search, the same question on the same pictures has no reason to be asked twice.
    /// </summary>
    [Theory]
    [InlineData(AgentType.SoundDesigner)]
    [InlineData(AgentType.VideoStoryEditor)]
    [InlineData(AgentType.MotionDirector)]
    public void Agent_steps_granted_MomentLook_stay_cacheable_and_a_result_that_looked_may_be_stored(AgentType agentType)
    {
        ToolGroupCatalog.GroupsFor(agentType).Should().Contain(ToolGroup.MomentLook);
        Policy.IsCacheable(AgentStep(agentType)).Should().BeTrue();
        Policy.IsAdmissible(new StepExecutionResult { Output = "x", NextStepIndex = 1 }).Should().BeTrue(
            because: "looks are not counted against admissibility; only a web search or a runner result is refused");
    }

    [Theory]
    [InlineData(AgentType.CodeStructureAnalyzer)]
    [InlineData(AgentType.ReviewAgent)]
    [InlineData(AgentType.VideoStoryEditor)]
    [InlineData(AgentType.Colorist)]
    [InlineData(AgentType.MusicSupervisor)]
    [InlineData(AgentType.SoundDesigner)]
    public void Agent_steps_with_read_only_tool_grants_are_cacheable(AgentType agentType)
    {
        Policy.IsCacheable(AgentStep(agentType)).Should().BeTrue(
            because: $"{agentType} is granted none of ProjectWrite/SandboxAuthoring/SandboxRender");
    }

    // -----------------------------------------------------------------
    // Per-step CacheMode override
    // -----------------------------------------------------------------

    [Fact]
    public void CacheMode_Never_overrides_an_otherwise_cacheable_step()
    {
        var step = new WorkflowStep
        {
            StepType = StepType.Extract,
            CacheMode = StepCacheMode.Never,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.ExtractTransform }
        };

        Policy.IsCacheable(step).Should().BeFalse();
    }

    [Fact]
    public void CacheMode_Always_overrides_an_otherwise_uncacheable_StepType()
    {
        var step = new WorkflowStep
        {
            StepType = StepType.ReviewLoop,
            CacheMode = StepCacheMode.Always,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.VideoReviewAgent }
        };

        Policy.IsCacheable(step).Should().BeTrue();
    }

    [Fact]
    public void CacheMode_Always_overrides_an_otherwise_uncacheable_Agent_tool_grant()
    {
        var step = AgentStep(AgentType.AuthorAgent, StepCacheMode.Always);

        Policy.IsCacheable(step).Should().BeTrue();
    }

    [Fact]
    public void CacheMode_Never_overrides_GraphicsRoom()
    {
        var step = new WorkflowStep
        {
            StepType = StepType.GraphicsRoom,
            CacheMode = StepCacheMode.Never,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.VideoTransform }
        };

        Policy.IsCacheable(step).Should().BeFalse();
    }

    [Fact]
    public void CacheMode_Default_falls_through_to_the_built_in_table()
    {
        var step = new WorkflowStep
        {
            StepType = StepType.Extract,
            CacheMode = StepCacheMode.Default,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.ExtractTransform }
        };

        Policy.IsCacheable(step).Should().BeTrue();
    }

    [Fact]
    public void Null_CacheMode_behaves_the_same_as_Default()
    {
        var step = new WorkflowStep
        {
            StepType = StepType.Extract,
            CacheMode = null,
            AgentDefinition = new AgentDefinition { AgentType = AgentType.ExtractTransform }
        };

        Policy.IsCacheable(step).Should().BeTrue();
    }

    // -----------------------------------------------------------------
    // VideoGenerate: cacheable by default (a replay avoids real spend),
    // and the per-step override still wins in both directions.
    // -----------------------------------------------------------------

    private static WorkflowStep VideoGenerateStep(StepCacheMode? cacheMode) => new()
    {
        StepType = StepType.VideoGenerate,
        CacheMode = cacheMode,
        AgentDefinition = new AgentDefinition { AgentType = AgentType.VideoTransform }
    };

    [Theory]
    [InlineData(StepCacheMode.Default, true)]
    [InlineData(null, true)]
    [InlineData(StepCacheMode.Never, false)]
    [InlineData(StepCacheMode.Always, true)]
    public void VideoGenerate_is_cacheable_by_default_and_honours_the_CacheMode_override(StepCacheMode? cacheMode, bool expected)
    {
        Policy.IsCacheable(VideoGenerateStep(cacheMode)).Should().Be(expected);
    }
}
