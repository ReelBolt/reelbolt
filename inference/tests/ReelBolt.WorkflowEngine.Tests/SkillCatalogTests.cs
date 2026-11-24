using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Skills;
using ReelBolt.WorkflowEngine.Execution;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

public class SkillCatalogTests
{
    [Fact]
    public void All_has_exactly_the_five_remotion_three_design_taste_and_one_first_party_skill()
    {
        SkillCatalog.All.Should().HaveCount(9);
        SkillCatalog.All.Where(s => s.Category == SkillCategory.Remotion).Select(s => s.Name).Should().BeEquivalentTo(
            "remotion-create", "remotion-markup", "remotion-render", "remotion-captions", "remotion-multimedia");
        SkillCatalog.All.Where(s => s.Category == SkillCategory.DesignTaste).Select(s => s.Name).Should().BeEquivalentTo(
            "taste-skill", "minimalist-skill", "brutalist-skill");
        SkillCatalog.All.Where(s => s.Category == SkillCategory.ReelBolt).Select(s => s.Name).Should().BeEquivalentTo("reelbolt-blocks");
    }

    [Fact]
    public void DesignTaste_is_appended_after_the_existing_categories_so_no_value_shifts()
    {
        // The enum is append-only: pin the existing members' numeric values.
        ((int)SkillCategory.Remotion).Should().Be(0);
        ((int)SkillCategory.ReelBolt).Should().Be(1);
        ((int)SkillCategory.Custom).Should().Be(2);
        ((int)SkillCategory.DesignTaste).Should().Be(3);
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_unknown_names()
    {
        SkillCatalog.Find("remotion-markup").Should().NotBeNull();
        SkillCatalog.Find("REMOTION-MARKUP").Should().NotBeNull();
        SkillCatalog.Find("does-not-exist").Should().BeNull();
        SkillCatalog.Find("").Should().BeNull();
    }

    [Theory]
    [InlineData(AgentType.CodeStructureAnalyzer)]
    [InlineData(AgentType.DependencyAnalyzer)]
    [InlineData(AgentType.ComponentInventoryAnalyzer)]
    [InlineData(AgentType.RouteAndApiAnalyzer)]
    [InlineData(AgentType.StyleAndThemeExtractor)]
    [InlineData(AgentType.RemotionComponentTranslator)]
    [InlineData(AgentType.AnimationStrategyAgent)]
    [InlineData(AgentType.DirectorAgent)]
    [InlineData(AgentType.ScriptwriterAgent)]
    [InlineData(AgentType.AuthorAgent)]
    [InlineData(AgentType.ReviewAgent)]
    [InlineData(AgentType.FileSummarizerAgent)]
    [InlineData(AgentType.Custom)]
    [InlineData(AgentType.ExtractTransform)]
    [InlineData(AgentType.VideoStoryEditor)]
    [InlineData(AgentType.VideoTransform)]
    [InlineData(AgentType.MotionGraphicsPlanner)]
    public void DefaultsFor_returns_only_names_that_exist_in_the_catalog(AgentType agentType)
    {
        string[] catalogNames = SkillCatalog.All.Select(s => s.Name).ToArray();

        foreach (string skillName in SkillCatalog.DefaultsFor(agentType))
        {
            catalogNames.Should().Contain(skillName,
                $"DefaultsFor({agentType}) referenced a skill name not present in SkillCatalog.All");
        }
    }

    [Fact]
    public void DefaultsFor_covers_every_declared_AgentType_without_throwing()
    {
        // Exhaustive sweep: every enum member, not just the ones spelled out in the Theory above
        // (guards against a newly-added AgentType being silently unmapped).
        foreach (AgentType agentType in Enum.GetValues<AgentType>())
        {
            var names = SkillCatalog.DefaultsFor(agentType);
            names.Should().NotBeNull();

            string[] catalogNames = SkillCatalog.All.Select(s => s.Name).ToArray();
            foreach (string name in names)
            {
                catalogNames.Should().Contain(name);
            }
        }
    }

    [Fact]
    public void The_five_agents_that_used_to_get_RemotionSkills_tools_all_get_a_non_empty_skill_set()
    {
        // These are exactly the five AgentToolProvider arms that previously registered
        // RemotionSkillsAgentTools (SearchRemotionSkills/ReadRemotionSkill/ListAllRemotionSkills).
        AgentType[] previouslyWired =
        [
            AgentType.RemotionComponentTranslator,
            AgentType.AnimationStrategyAgent,
            AgentType.AuthorAgent,
            AgentType.ReviewAgent,
            AgentType.MotionGraphicsPlanner
        ];

        foreach (AgentType agentType in previouslyWired)
        {
            SkillCatalog.DefaultsFor(agentType).Should().NotBeEmpty(
                $"{agentType} previously had Remotion-skill tools and should still get a default skill set");
        }
    }

    [Fact]
    public void MotionGraphicsPlanner_includes_remotion_render_for_transparent_asset_rendering()
    {
        SkillCatalog.DefaultsFor(AgentType.MotionGraphicsPlanner).Should().Contain("remotion-render");
    }

    [Fact]
    public void Every_other_AgentType_not_in_the_curated_list_gets_no_skills_by_default()
    {
        SkillCatalog.DefaultsFor(AgentType.ScriptwriterAgent).Should().BeEmpty();
        SkillCatalog.DefaultsFor(AgentType.Custom).Should().BeEmpty();
        SkillCatalog.DefaultsFor(AgentType.VideoStoryEditor).Should().BeEmpty();
    }

    // --- Design taste assignment ---

    [Theory]
    [InlineData(AgentType.DirectorAgent, new[] { "taste-skill", "minimalist-skill", "brutalist-skill" })]
    [InlineData(AgentType.AuthorAgent, new[]
    {
        "remotion-create", "remotion-markup", "remotion-render", "remotion-captions", "remotion-multimedia",
        "taste-skill", "minimalist-skill", "brutalist-skill", "reelbolt-blocks"
    })]
    [InlineData(AgentType.RemotionComponentTranslator, new[] { "remotion-create", "remotion-markup", "taste-skill" })]
    [InlineData(AgentType.AnimationStrategyAgent, new[] { "remotion-markup", "taste-skill" })]
    [InlineData(AgentType.MotionGraphicsPlanner, new[] { "remotion-markup", "remotion-render", "taste-skill", "reelbolt-blocks" })]
    [InlineData(AgentType.MotionGraphicsDirector, new[] { "remotion-markup", "remotion-render", "taste-skill", "reelbolt-blocks" })]
    public void The_visual_agents_get_exactly_this_skill_set(AgentType agentType, string[] expected)
    {
        SkillCatalog.DefaultsFor(agentType).Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(AgentType.ScriptwriterAgent)]
    [InlineData(AgentType.Colorist)]
    [InlineData(AgentType.ColorGradeDirector)]
    [InlineData(AgentType.VideoStoryEditor)]
    [InlineData(AgentType.VideoEditDirector)]
    [InlineData(AgentType.MusicSupervisor)]
    [InlineData(AgentType.SoundDesigner)]
    [InlineData(AgentType.ShotDirector)]
    [InlineData(AgentType.NarrationWriter)]
    [InlineData(AgentType.NarrationTranslator)]
    [InlineData(AgentType.PickupPlanner)]
    [InlineData(AgentType.VideoReviewAgent)]
    [InlineData(AgentType.ReviewAgent)]
    public void Agents_that_do_not_choose_typography_or_layout_get_no_design_taste_skill(AgentType agentType)
    {
        string[] taste = SkillCatalog.All
            .Where(s => s.Category == SkillCategory.DesignTaste)
            .Select(s => s.Name)
            .ToArray();

        SkillCatalog.DefaultsFor(agentType).Should().NotIntersectWith(taste);
    }

    [Fact]
    public void The_two_style_recipes_are_only_default_for_the_agents_that_apply_a_named_look()
    {
        // Director names the look, Author applies it, and CompositionReviser re-applies it when it
        // revises the Author's film; every other agent has the core skill at most.
        string[] withStyles = Enum.GetValues<AgentType>()
            .Where(t => SkillCatalog.DefaultsFor(t).Contains("minimalist-skill")
                        || SkillCatalog.DefaultsFor(t).Contains("brutalist-skill"))
            .Select(t => t.ToString())
            .ToArray();

        withStyles.Should().BeEquivalentTo(
            nameof(AgentType.DirectorAgent), nameof(AgentType.AuthorAgent), nameof(AgentType.CompositionReviser),
            nameof(AgentType.GraphicDesigner));
    }

    [Fact]
    public void Built_in_step_cache_skills_component_follows_the_code_defaults_not_the_null_column()
    {
        // A built-in's AssignedSkillsJson is always null; its skills come from DefaultsFor. The cache
        // key must therefore hash the resolved set, or a change to the defaults would replay stale output.
        AgentDefinition director = new() { AgentType = AgentType.DirectorAgent, IsBuiltIn = true, AssignedSkillsJson = null };
        AgentDefinition analyzer = new() { AgentType = AgentType.CodeStructureAnalyzer, IsBuiltIn = true };
        AgentDefinition custom = new() { AgentType = AgentType.Custom, IsBuiltIn = false, AssignedSkillsJson = "[\"taste-skill\"]" };
        AgentDefinition customNone = new() { AgentType = AgentType.Custom, IsBuiltIn = false };

        WorkflowExecutorService.ResolvedAgentSkillsJson(director)
            .Should().Be("[\"taste-skill\",\"minimalist-skill\",\"brutalist-skill\"]");
        WorkflowExecutorService.ResolvedAgentSkillsJson(analyzer).Should().BeNull("a built-in with no default skills keeps its key unchanged");
        WorkflowExecutorService.ResolvedAgentSkillsJson(custom).Should().Be("[\"taste-skill\"]");
        WorkflowExecutorService.ResolvedAgentSkillsJson(customNone).Should().BeNull();
        WorkflowExecutorService.ResolvedAgentSkillsJson(null).Should().BeNull();
    }

    // --- Provenance (SourceUrl/SourceCommit) ---
    //
    // The Remotion skills are vendored from an upstream repository in the private tree and written
    // in this repository in the public export (SkillCatalog.RemotionSkillsAreVendored, swapped by
    // the export overlay). Taste skills are vendored in both. The ReelBolt-category skill (reelbolt-blocks) is first-party in both.

    private static IEnumerable<SkillDescriptor> VendoredSkills() =>
        SkillCatalog.All.Where(s => s.Category != SkillCategory.ReelBolt
            && (s.Category != SkillCategory.Remotion || SkillCatalog.RemotionSkillsAreVendored));

    [Fact]
    public void Every_vendored_skill_has_a_populated_SourceUrl_and_SourceCommit()
    {
        // Every vendored skill comes from an upstream repository at a pinned commit
        // (see inference/skills/UPSTREAM.md) — none of them should be null.
        foreach (SkillDescriptor descriptor in VendoredSkills())
        {
            descriptor.SourceUrl.Should().NotBeNullOrWhiteSpace(
                $"{descriptor.Name} is vendored and should carry a provenance link");
            descriptor.SourceCommit.Should().NotBeNullOrWhiteSpace(
                $"{descriptor.Name} is vendored and should carry a pinned commit SHA");
        }
    }

    [Fact]
    public void First_party_skills_carry_no_upstream_provenance()
    {
        foreach (SkillDescriptor descriptor in SkillCatalog.All.Except(VendoredSkills()))
        {
            descriptor.SourceUrl.Should().BeNull($"{descriptor.Name} is written in this repository");
            descriptor.SourceCommit.Should().BeNull($"{descriptor.Name} is written in this repository");
        }
    }

    [Fact]
    public void SourceUrl_points_at_the_exact_vendored_directory_for_this_skill_not_the_repo_root()
    {
        foreach (SkillDescriptor descriptor in VendoredSkills())
        {
            descriptor.SourceUrl.Should().Contain(descriptor.SourceCommit!,
                $"{descriptor.Name}'s SourceUrl should be pinned to its SourceCommit, not a moving ref like main");
            descriptor.SourceUrl.Should().EndWith($"/skills/{descriptor.RelativePath}",
                $"{descriptor.Name}'s SourceUrl should resolve to its own vendored directory, not the bare repo root");
        }
    }

    [Fact]
    public void Remotion_skills_share_one_upstream_repository_and_pinned_commit()
    {
        if (!SkillCatalog.RemotionSkillsAreVendored)
            return; // first-party in this tree: nothing to pin (see First_party_skills_carry_no_upstream_provenance)

        // Matches inference/skills/UPSTREAM.md: one repo, one pinned commit, for the five Remotion skills.
        SkillDescriptor[] remotion = SkillCatalog.All.Where(s => s.Category == SkillCategory.Remotion).ToArray();

        remotion.Select(s => s.SourceCommit).Distinct().Should().ContainSingle();
        remotion.Select(s => s.SourceUrl!.Split("/tree/")[0]).Distinct().Should().ContainSingle()
            .Which.Should().Be(SkillCatalog.RemotionUpstreamRepoUrl);
    }

    [Fact]
    public void DesignTaste_skills_share_one_upstream_repository_and_the_commit_pinned_in_UPSTREAM_md()
    {
        SkillDescriptor[] taste = SkillCatalog.All.Where(s => s.Category == SkillCategory.DesignTaste).ToArray();

        taste.Select(s => s.SourceCommit).Distinct().Should().ContainSingle()
            .Which.Should().Be("717446e07a78d4e6d7918b1fde1a263378eebac9");
        taste.Select(s => s.SourceUrl!.Split("/tree/")[0]).Distinct().Should().ContainSingle()
            .Which.Should().Be("https://github.com/Leonxlnx/taste-skill");
    }
}
