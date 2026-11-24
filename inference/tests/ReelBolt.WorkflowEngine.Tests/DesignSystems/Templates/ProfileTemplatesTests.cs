using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution;
using StepTypeEnum = ReelBolt.Shared.Data.Models.StepType;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.Templates;

/// <summary>
/// P2-14: the three templates that build and consume a project's brand and product profile —
/// <c>product-profile-from-code</c>, <c>quick-win-promo-ds</c> and <c>quick-win-promo-plates-ds</c> —
/// plus the branch wiring they depend on. The legacy templates are untouched; their hashes stay
/// pinned by <see cref="LegacyTemplatesUnchangedTests"/>.
/// </summary>
public class ProfileTemplatesTests
{
    private static readonly JsonSerializerOptions DesignSystemJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly AgentType[] AnalyzerAgentTypes =
    [
        AgentType.CodeStructureAnalyzer,
        AgentType.DependencyAnalyzer,
        AgentType.ComponentInventoryAnalyzer,
        AgentType.RouteAndApiAnalyzer,
        AgentType.StyleAndThemeExtractor
    ];

    private static readonly AgentType[] EditorialAgentTypes =
    [
        AgentType.RemotionComponentTranslator,
        AgentType.AnimationStrategyAgent,
        AgentType.ScriptwriterAgent,
        AgentType.DirectorAgent,
        AgentType.AuthorAgent,
        AgentType.ReviewAgent
    ];

    private static WorkflowTemplateDefinition Template(string key)
    {
        WorkflowTemplateDefinition? template = WorkflowTemplateCatalog.BaseByKey(key);
        template.Should().NotBeNull($"template '{key}' must be registered");
        return template!;
    }

    private static DesignSystemStepConfig DsConfig(WorkflowTemplateStepDefinition step) =>
        JsonSerializer.Deserialize<DesignSystemStepConfig>(
            step.DesignSystemConfigJson!, DesignSystemJsonOptions)!;

    private static CreateWorkflowStepRequest ToRequest(WorkflowTemplateStepDefinition s, int order) => new(
        AgentDefinitionId: Guid.NewGuid(),
        StepOrder: order,
        EdgeConditionJson: null,
        Label: s.Label,
        StepType: s.StepType.ToString(),
        ConditionExpression: s.ConditionExpression,
        LoopTargetStepOrder: s.LoopTargetStepOrder,
        MaxIterations: s.MaxIterations,
        MinScore: s.MinScore,
        TrueBranchStepOrder: s.TrueBranchStepOrder,
        FalseBranchStepOrder: s.FalseBranchStepOrder,
        ExtractConfigJson: s.ExtractConfigJson,
        VideoAnalyzeConfigJson: s.VideoAnalyzeConfigJson,
        VideoCompileConfigJson: s.VideoCompileConfigJson,
        EditRoomConfigJson: s.EditRoomConfigJson,
        GraphicsRoomConfigJson: s.GraphicsRoomConfigJson,
        ColorGradeRoomConfigJson: s.ColorGradeRoomConfigJson,
        VideoGenerateConfigJson: s.VideoGenerateConfigJson,
        VoiceoverConfigJson: s.VoiceoverConfigJson,
        DailiesRoomConfigJson: s.DailiesRoomConfigJson,
        DesignSystemConfigJson: s.DesignSystemConfigJson,
        ConditionalConfigJson: s.ConditionalConfigJson);

    /// <summary>A tiny walker over the build template: every conditional evaluates its expression against
    /// the Parse step's output (each one names it by <c>sourceStepOrder</c>, pinned below) and jumps to the
    /// named branch target; every other step falls through to the next order.</summary>
    private static List<int> Walk(WorkflowTemplateDefinition template, string parseOutput)
    {
        var visited = new List<int>();
        int order = 1;
        while (order >= 1 && order <= template.Steps.Count)
        {
            visited.Add(order);
            WorkflowTemplateStepDefinition step = template.Steps[order - 1];
            if (step.StepType == StepTypeEnum.Conditional)
            {
                bool branch = ExpressionEvaluator.Evaluate(step.ConditionExpression!, parseOutput);
                order = int.Parse(branch ? step.TrueBranchStepOrder! : step.FalseBranchStepOrder!);
            }
            else
            {
                order += 1;
            }
        }

        return visited;
    }

    [Fact]
    public void Product_profile_from_code_has_the_expected_10_steps()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey);

        template.Name.Should().Be("Build brand and product profile");
        template.Version.Should().Be(2, "v2 re-checks the design flag after the product analyzers");
        template.AutoCreateOnProject.Should().BeFalse();
        template.RequiresUserInput.Should().BeFalse();
        template.Steps.Should().HaveCount(10);

        template.Steps[0].StepType.Should().Be(StepTypeEnum.DesignSystem);
        DsConfig(template.Steps[0]).Op.Should().Be(DesignSystemOp.Parse);

        template.Steps[1].StepType.Should().Be(StepTypeEnum.Conditional);
        template.Steps[1].ConditionExpression.Should().Be("$.view.fallbackProduct");
        template.Steps[1].TrueBranchStepOrder.Should().Be("4");
        template.Steps[1].FalseBranchStepOrder.Should().Be("3");

        template.Steps[2].StepType.Should().Be(StepTypeEnum.Conditional);
        template.Steps[2].ConditionExpression.Should().Be("$.view.fallbackDesign");
        template.Steps[2].TrueBranchStepOrder.Should().Be("9");
        template.Steps[2].FalseBranchStepOrder.Should().Be("10");

        template.Steps.Skip(3).Take(4).Select(s => s.StepType).Should().OnlyContain(t => t == StepTypeEnum.Agent);
        template.Steps.Skip(3).Take(4).Select(s => s.AgentType).Should().Equal(AnalyzerAgentTypes.Take(4));

        template.Steps[7].StepType.Should().Be(StepTypeEnum.Conditional);
        template.Steps[7].ConditionExpression.Should().Be("$.view.fallbackDesign");
        template.Steps[7].TrueBranchStepOrder.Should().Be("9");
        template.Steps[7].FalseBranchStepOrder.Should().Be("10");

        template.Steps[8].StepType.Should().Be(StepTypeEnum.Agent);
        template.Steps[8].AgentType.Should().Be(AgentType.StyleAndThemeExtractor);

        template.Steps[9].StepType.Should().Be(StepTypeEnum.DesignSystem);
        DsConfig(template.Steps[9]).Op.Should().Be(DesignSystemOp.Compose);
    }

    [Fact]
    public void Every_profile_conditional_reads_the_parse_steps_view()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey);
        List<WorkflowTemplateStepDefinition> conditionals = [.. template.Steps.Where(s => s.StepType == StepTypeEnum.Conditional)];

        conditionals.Should().HaveCount(3);
        foreach (WorkflowTemplateStepDefinition step in conditionals)
        {
            ConditionalStepConfig config = JsonSerializer.Deserialize<ConditionalStepConfig>(step.ConditionalConfigJson!, DesignSystemJsonOptions)!;
            config.Mode.Should().Be(ConditionMode.Expression);
            config.SourceStepOrder.Should().Be(1, $"'{step.Label}' must evaluate the Parse step's view, not the previous step's output");
            DsConfig(template.Steps[config.SourceStepOrder!.Value - 1]).Op.Should().Be(DesignSystemOp.Parse);
        }
    }

    [Fact]
    public void Quick_win_promo_ds_has_the_expected_7_steps()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.QuickWinPromoDsTemplateKey);

        template.Name.Should().Be("Promo video from your profile");
        template.Version.Should().Be(1);
        template.AutoCreateOnProject.Should().BeFalse();
        template.RequiresUserInput.Should().BeTrue();
        template.Steps.Should().HaveCount(7);

        template.Steps[0].StepType.Should().Be(StepTypeEnum.DesignSystem);
        DsConfig(template.Steps[0]).Op.Should().Be(DesignSystemOp.Require);

        template.Steps.Skip(1).Take(5).Select(s => s.AgentType).Should().ContainInOrder(new[]
        {
            AgentType.RemotionComponentTranslator,
            AgentType.AnimationStrategyAgent,
            AgentType.ScriptwriterAgent,
            AgentType.DirectorAgent,
            AgentType.AuthorAgent
        });

        template.Steps[6].StepType.Should().Be(StepTypeEnum.ReviewLoop);
    }

    [Fact]
    public void Quick_win_promo_plates_ds_has_the_expected_8_steps()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.QuickWinPromoPlatesDsTemplateKey);

        template.Name.Should().Be("Promo video from your profile with AI backgrounds");
        template.Version.Should().Be(1);
        template.AutoCreateOnProject.Should().BeFalse();
        template.RequiresUserInput.Should().BeTrue();
        template.Steps.Should().HaveCount(8);

        template.Steps[0].StepType.Should().Be(StepTypeEnum.DesignSystem);
        DsConfig(template.Steps[0]).Op.Should().Be(DesignSystemOp.Require);

        WorkflowTemplateStepDefinition plate = template.Steps[5];
        plate.StepType.Should().Be(StepTypeEnum.VideoGenerate);
        plate.AgentType.Should().Be(AgentType.VideoTransform);

        VideoGenerateStepConfig? generate = JsonSerializer.Deserialize<VideoGenerateStepConfig>(
            plate.VideoGenerateConfigJson!, VideoGenerateStepConfig.JsonOptions);
        generate.Should().NotBeNull();
        generate!.PlanSource.Should().Be(VideoGeneratePlanSource.Inline);
        generate.Takes.Should().Be(1);
        generate.MaxClips.Should().Be(2);
        generate.MaxSpendCredits.Should().Be(500m);

        template.Steps[7].StepType.Should().Be(StepTypeEnum.ReviewLoop);
    }

    [Fact]
    public void Conditional_branch_targets_refer_to_existing_steps()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey);
        HashSet<int> orders = Enumerable.Range(1, template.Steps.Count).ToHashSet();

        foreach (WorkflowTemplateStepDefinition step in template.Steps)
        {
            foreach (string? target in new[] { step.TrueBranchStepOrder, step.FalseBranchStepOrder })
            {
                if (target is null)
                    continue;

                int.TryParse(target, out int parsed).Should().BeTrue($"'{target}' must be a step order");
                orders.Should().Contain(parsed);
            }
        }
    }

    [Theory]
    [InlineData(WorkflowTemplateCatalog.QuickWinPromoDsTemplateKey, 6)]
    [InlineData(WorkflowTemplateCatalog.QuickWinPromoPlatesDsTemplateKey, 7)]
    public void Review_loop_targets_the_author_step(string key, int expectedTarget)
    {
        WorkflowTemplateDefinition template = Template(key);
        int reviewIndex = template.Steps.Select((s, i) => (s, i)).Single(x => x.s.StepType == StepTypeEnum.ReviewLoop).i;

        template.Steps[reviewIndex].LoopTargetStepOrder.Should().Be(expectedTarget);
        template.Steps[template.Steps[reviewIndex].LoopTargetStepOrder!.Value - 1].AgentType
            .Should().Be(AgentType.AuthorAgent);
    }

    [Theory]
    [InlineData(WorkflowTemplateCatalog.QuickWinPromoDsTemplateKey)]
    [InlineData(WorkflowTemplateCatalog.QuickWinPromoPlatesDsTemplateKey)]
    public void Ds_templates_have_no_analyzers_and_no_extract(string key)
    {
        WorkflowTemplateDefinition template = Template(key);

        template.Steps.Should().NotContain(s => AnalyzerAgentTypes.Contains(s.AgentType));
        template.Steps.Should().NotContain(s => s.StepType == StepTypeEnum.Extract);
    }

    [Fact]
    public void Product_profile_compose_references_existing_agent_steps()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey);
        DesignSystemStepConfig compose = DsConfig(template.Steps[9]);

        compose.Op.Should().Be(DesignSystemOp.Compose);
        compose.Compose.Should().NotBeNull();
        compose.Compose!.ParseStepOrder.Should().Be(1);
        compose.Compose.DesignFallbackStepOrders.Should().Equal(9);
        compose.Compose.ProductFallbackStepOrders.Should().Equal(4, 5, 6, 7);

        template.Steps[compose.Compose.ParseStepOrder - 1].StepType.Should().Be(StepTypeEnum.DesignSystem);
        foreach (int order in compose.Compose.DesignFallbackStepOrders!)
            template.Steps[order - 1].StepType.Should().Be(StepTypeEnum.Agent);
        foreach (int order in compose.Compose.ProductFallbackStepOrders!)
            template.Steps[order - 1].StepType.Should().Be(StepTypeEnum.Agent);

        string[] keys =
        [
            WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey,
            WorkflowTemplateCatalog.QuickWinPromoDsTemplateKey,
            WorkflowTemplateCatalog.QuickWinPromoPlatesDsTemplateKey
        ];
        foreach (string key in keys)
        {
            WorkflowTemplateDefinition candidate = Template(key);

            foreach (WorkflowTemplateStepDefinition step in candidate.Steps.Where(s => s.StepType == StepTypeEnum.DesignSystem))
                DsConfig(step).Validate().Should().BeEmpty($"{key} step '{step.Label}' must be valid as shipped");

            IEnumerable<CreateWorkflowStepRequest> requests = candidate.Steps.Select((s, i) => ToRequest(s, i + 1));
            StepConfigSaveValidator.Validate(requests).Should().BeEmpty($"template '{key}' must be savable as shipped");
        }
    }

    [Fact]
    public void Cache_modes_match_the_legacy_policy()
    {
        WorkflowTemplateDefinition profileTemplate = Template(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey);
        foreach (WorkflowTemplateStepDefinition step in profileTemplate.Steps.Where(s => AnalyzerAgentTypes.Contains(s.AgentType)))
            step.CacheMode.Should().BeNull();

        foreach (string key in new[]
        {
            WorkflowTemplateCatalog.QuickWinPromoDsTemplateKey,
            WorkflowTemplateCatalog.QuickWinPromoPlatesDsTemplateKey
        })
        {
            WorkflowTemplateDefinition template = Template(key);
            foreach (WorkflowTemplateStepDefinition step in template.Steps.Where(s => EditorialAgentTypes.Contains(s.AgentType)))
                step.CacheMode.Should().Be(StepCacheMode.Never);
        }
    }

    [Fact]
    public void Simulated_walk_proves_the_four_paths()
    {
        WorkflowTemplateDefinition template = Template(WorkflowTemplateCatalog.ProductProfileFromCodeTemplateKey);

        Walk(template, """{"view":{"fallbackProduct":false,"fallbackDesign":false}}""")
            .Should().Equal(1, 2, 3, 10);
        Walk(template, """{"view":{"fallbackProduct":false,"fallbackDesign":true}}""")
            .Should().Equal(1, 2, 3, 9, 10);
        Walk(template, """{"view":{"fallbackProduct":true,"fallbackDesign":false}}""")
            .Should().Equal(1, 2, 4, 5, 6, 7, 8, 10);
        Walk(template, """{"view":{"fallbackProduct":true,"fallbackDesign":true}}""")
            .Should().Equal(1, 2, 4, 5, 6, 7, 8, 9, 10);
    }
}
