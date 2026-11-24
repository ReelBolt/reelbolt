using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.DesignSystems.Views;
using ReelBolt.WorkflowEngine.Services.DesignSystems;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Engine;

/// <summary>
/// P3-06 exactness coverage for the engine's role map: every <see cref="AgentType"/> is enumerated
/// and asserted against the one declared view set, so a new role (or a stray default) can never
/// widen the ambient blocks silently. The registry must stay a delegate of the Shared table.
/// </summary>
public class RegistryExactnessTests
{
    private static readonly Dictionary<AgentType, (DesignView? Design, ProductView? Product)> Expected = new()
    {
        // Phase 2.
        [AgentType.RemotionComponentTranslator] = (DesignView.Tokens, ProductView.Structure),
        [AgentType.AnimationStrategyAgent] = (DesignView.Motion, ProductView.Overview),
        [AgentType.ScriptwriterAgent] = (DesignView.Voice, ProductView.Overview),
        [AgentType.DirectorAgent] = (DesignView.Identity, ProductView.Overview),
        [AgentType.AuthorAgent] = (DesignView.Tokens, ProductView.Structure),
        [AgentType.ReviewAgent] = (DesignView.Conformance, ProductView.Overview),

        // Phase 3.
        [AgentType.CompositionReviser] = (DesignView.Tokens, ProductView.Structure),
        [AgentType.MotionGraphicsPlanner] = (DesignView.GraphicsPalette, null),
        [AgentType.MotionGraphicsDirector] = (DesignView.GraphicsPalette, null),
        [AgentType.VideoReviewAgent] = (DesignView.Conformance, null),

        // Phase 4.
        [AgentType.Colorist] = (DesignView.GradeHint, null),
        [AgentType.ColorGradeDirector] = (DesignView.GradeHint, null),
    };

    [Fact]
    public void Every_agent_type_has_exactly_its_declared_views()
    {
        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            (DesignView? design, ProductView? product) = DesignSystemRoleRegistry.ViewsFor(agentType);

            if (Expected.TryGetValue(agentType, out (DesignView? Design, ProductView? Product) expected))
            {
                design.Should().Be(expected.Design, agentType.ToString());
                product.Should().Be(expected.Product, agentType.ToString());
            }
            else
            {
                design.Should().BeNull(agentType.ToString());
                product.Should().BeNull(agentType.ToString());
            }
        }
    }

    [Fact]
    public void Analyzers_and_unlisted_roles_get_no_block()
    {
        foreach (AgentType agentType in new[]
                 {
                     AgentType.CodeStructureAnalyzer,
                     AgentType.DependencyAnalyzer,
                     AgentType.ComponentInventoryAnalyzer,
                     AgentType.RouteAndApiAnalyzer,
                     AgentType.StyleAndThemeExtractor,
                     AgentType.FileSummarizerAgent,
                     AgentType.ExtractTransform,
                     AgentType.VideoStoryEditor,
                     AgentType.VideoTransform,
                     AgentType.MusicSupervisor,
                     AgentType.VideoEditDirector,
                     AgentType.SoundDesigner,
                     AgentType.Assistant,
                     AgentType.Custom,
                 })
        {
            (DesignView? design, ProductView? product) = DesignSystemRoleRegistry.ViewsFor(agentType);
            design.Should().BeNull(agentType.ToString());
            product.Should().BeNull(agentType.ToString());
        }
    }

    [Fact]
    public void The_engine_registry_is_the_shared_table()
    {
        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            DesignSystemRoleRegistry.ViewsFor(agentType)
                .Should().Be(DesignSystemRoleViews.ViewsFor(agentType), agentType.ToString());
        }
    }
}
