using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Assistant;
using ReelBolt.Inference.Api.Services.PlatformDocs;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Lint;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Agents;

/// <summary>
/// The step catalog (docs/design/workflow-apps.md 7.1) and its drift guard: every StepType has a card, each card agrees
/// with the linter's rule table and the cost-class derivation, and no price ever appears in a card.
/// </summary>
public class StepCatalogTests
{
    public static IEnumerable<object[]> AllStepTypes() => Enum.GetValues<StepType>().Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(AllStepTypes))]
    public void Every_step_type_has_a_card(StepType stepType)
    {
        StepCatalog.AttributeFor(stepType).Should().NotBeNull(
            $"{stepType} needs a [StepCard] on its config type (or on the StepType member when it has no config record)");
        StepCard card = StepCatalog.Get(stepType);
        card.StepType.Should().Be(stepType.ToString());
        card.Purpose.Should().NotBeNullOrWhiteSpace();
        card.Produces.Shape.Should().NotBeNullOrWhiteSpace();
        card.Needs.Should().NotBeEmpty();
    }

    [Fact]
    public void GetAll_returns_one_card_per_step_type_in_enum_order()
    {
        StepCatalog.GetAll().Select(c => c.StepType).Should().Equal(Enum.GetValues<StepType>().Select(t => t.ToString()));
    }

    [Theory]
    [MemberData(nameof(AllStepTypes))]
    public void Card_cost_class_is_the_derived_one(StepType stepType)
    {
        StepCatalog.AttributeFor(stepType)!.CostClass.Should().Be(StepCatalog.DeriveCostClass(stepType));
        StepCatalog.Get(stepType).CostClass.Should().Be(StepCatalog.DeriveCostClass(stepType));
    }

    [Theory]
    [MemberData(nameof(AllStepTypes))]
    public void Card_needs_come_from_the_linter_rule_table_and_mistakes_name_real_rules(StepType stepType)
    {
        StepCard card = StepCatalog.Get(stepType);
        foreach (StepCardMistake mistake in card.CommonMistakes)
        {
            StepCompatibilityRule rule = StepCompatibilityRules.All.Single(r => r.Id == mistake.RuleId);
            rule.Name.Should().Be(mistake.Name);
            card.Needs.Should().Contain(n => n.LintRule == rule.Id,
                "a rule the card names must also surface as a need, from the same table the linter runs");
        }
        card.Needs.Where(n => n.LintRule is not null).Select(n => n.LintRule!)
            .Should().OnlyContain(id => StepCompatibilityRules.All.Any(r => r.Id == id));
    }

    [Theory]
    [MemberData(nameof(AllStepTypes))]
    public void Every_rule_that_applies_to_a_step_type_is_on_its_card_and_nothing_else(StepType stepType)
    {
        string[] expected = StepCompatibilityRules.All.Where(r => StepCompatibilityRules.AppliesTo(r.Id).Contains(stepType)).Select(r => r.Id).ToArray();
        StepCatalog.Get(stepType).CommonMistakes.Select(m => m.RuleId).Should().Equal(expected);
    }

    [Fact]
    public void Rule_step_table_pins_the_known_linter_placements()
    {
        // The step types the linter branches on (WorkflowLinter / StepGraphLinter), pinned from the rule table.
        StepCompatibilityRules.AppliesTo("WL001").Should().BeEquivalentTo(new[] { StepType.Voiceover });
        StepCompatibilityRules.AppliesTo("WL006").Should().BeEquivalentTo(new[] { StepType.Voiceover, StepType.VideoGenerate, StepType.AudioGenerate, StepType.VideoAnalyze, StepType.AudioCleanup, StepType.ImageGenerate });
        StepCompatibilityRules.AppliesTo("WL007").Should().Contain([StepType.EditRoom, StepType.GraphicsRoom, StepType.ColorGradeRoom, StepType.VideoGenerate, StepType.AudioGenerate]);
        StepCompatibilityRules.AppliesTo("WL010").Should().BeEquivalentTo(new[] { StepType.Agent, StepType.VideoAnalyze, StepType.VideoCompile });
        StepCompatibilityRules.AppliesTo("WL015").Should().BeEquivalentTo(new[] { StepType.VideoAnalyze, StepType.VideoGenerate, StepType.AudioCleanup, StepType.VideoEnhance, StepType.ImageGenerate });
        StepCompatibilityRules.AppliesTo("WL016").Should().BeEmpty("WL016 is a brief-level rule, not a step's");
        StepCompatibilityRules.All.Select(r => r.Id).Where(id => StepCompatibilityRules.AppliesTo(id).Count > 0)
            .Should().OnlyContain(id => StepCompatibilityRules.All.Any(r => r.Id == id));
    }

    [Fact]
    public void Thumbnail_generate_calls_no_model_so_costs_no_credits()
    {
        StepCatalog.DeriveCostClass(StepType.ThumbnailGenerate).Should().Be(AppCostClass.NoCredits);
    }

    [Fact]
    public void Needs_are_conditional_where_the_config_allows_another_path_and_never_repeat()
    {
        StepCatalog.Get(StepType.Voiceover).Needs.Select(n => n.Description)
            .Should().Contain(d => d.Contains("inline")).And.Contain(d => d.Contains("project voice"));
        StepCatalog.Get(StepType.VideoGenerate).Needs.Select(n => n.Description).Should().Contain(d => d.Contains("inline"));
        StepCatalog.Get(StepType.VideoAnalyze).Needs.Select(n => n.Description).Should().Contain(d => d.Contains("Optional"));
        foreach (StepCard card in StepCatalog.GetAll())
            card.Needs.Select(n => n.Description).Should().OnlyHaveUniqueItems(card.StepType);
    }

    [Fact]
    public void Need_kinds_are_kinds_of_need_not_fix_names()
    {
        StepCatalog.GetAll().SelectMany(c => c.Needs).Select(n => n.Kind).Distinct()
            .Should().OnlyContain(k => new[] { "step", "file", "provider", "plan", "designSystem", "voice", "permission", "setting", "input" }.Contains(k));
    }

    [Fact]
    public void Output_contracts_are_named_where_a_schema_exists()
    {
        (StepType Step, string Contract)[] expected =
        [
            (StepType.ColorGradeRoom, "ColorGradePlanOutput"), (StepType.GraphicsRoom, "MotionGraphicsPlanOutput"),
            (StepType.PinInsert, "MotionGraphicsPlanOutput"), (StepType.ReviewLoop, "ReviewOutput"),
            (StepType.Revise, "CompositionRevisionOutput"), (StepType.DesignGraphic, "GraphicDesignOutput"),
            (StepType.EditRoom, "VideoEditDecisionOutput"), (StepType.DailiesRoom, "TakeSelectionOutput")
        ];
        foreach ((StepType step, string contract) in expected)
            StepCatalog.Get(step).Produces.OutputContract.Should().Be(contract);
    }

    [Fact]
    public void Fact_chunks_stay_inside_the_chunk_size_cap()
    {
        PlatformDocsGeneratedFacts.Build().Should().OnlyContain(c => c.Text.Length <= 2400);
    }

    [Theory]
    [MemberData(nameof(AllStepTypes))]
    public void Reads_from_run_match_the_brief_field_registry(StepType stepType)
    {
        HashSet<string> registry = BriefFieldConsumers.FieldsReadBy(new AppStepFacts(stepType)).ToHashSet();
        StepCatalog.Get(stepType).ReadsFromRun.Should().BeEquivalentTo(registry);
    }

    [Fact]
    public void Pairings_name_real_step_types_and_output_contracts_exist()
    {
        foreach (StepCard card in StepCatalog.GetAll())
        {
            card.PairsWith.Select(p => p.Next).Should().OnlyContain(n => Enum.IsDefined(typeof(StepType), n),
                $"{card.StepType} pairs only with real step types");
            if (card.Produces.OutputContract is { } contract)
                typeof(StepType).Assembly.GetTypes().Should().Contain(t => t.Name == contract && t.Namespace == "ReelBolt.Shared.Data.OutputSchemas");
        }
    }

    [Fact]
    public void Config_fields_are_included_for_a_single_card_and_left_out_of_the_full_list()
    {
        StepCatalog.Get(StepType.VideoCompile).ConfigFields.Should().NotBeNull();
        StepCatalog.Get(StepType.Agent).ConfigFields.Should().BeNull("Agent has no config record");
        StepCatalog.GetAll().Should().OnlyContain(c => c.ConfigFields == null);
    }

    [Fact]
    public void No_price_appears_in_any_card_or_recipe_fact()
    {
        Regex money = new(@"[$€£]|\bUSD\b|\bEUR\b|\bcents?\b|\bper (second|minute|image|token|credit)\b|\d+(\.\d+)?\s*(credits|usd|dollars)\b", RegexOptions.IgnoreCase);
        List<string> texts = [];
        texts.AddRange(StepCatalog.GetAll().Select(c => JsonSerializer.Serialize(c)));
        texts.AddRange(StepCatalog.GetAll().Select(StepCatalog.FactText));
        texts.AddRange(StepCatalog.GetTemplateFacts().Select(StepCatalog.FactText));
        foreach (string text in texts)
            money.IsMatch(text).Should().BeFalse($"no price belongs in the catalog (cost class only): {text}");
    }

    [Fact]
    public void Template_facts_cover_every_template_with_a_cost_class()
    {
        IReadOnlyList<TemplateFact> facts = StepCatalog.GetTemplateFacts();
        facts.Select(f => f.Key).Should().Equal(WorkflowTemplateCatalog.GetAll().Select(t => t.Key));
        facts.Should().OnlyContain(f => f.StepTypes.Count > 0);
        string[] classes = [AppCostClass.NoCredits, AppCostClass.UsesCredits, AppCostClass.UsesCreditsAndVoice, AppCostClass.UsesCreditsAndAiShots];
        facts.Select(f => f.CostClass).Should().OnlyContain(c => classes.Contains(c));
    }

    [Fact]
    public void Generated_fact_chunks_are_one_per_step_type_and_per_template_with_metadata()
    {
        IReadOnlyList<PlatformDocsChunk> chunks = PlatformDocsGeneratedFacts.Build();
        chunks.Count.Should().Be(Enum.GetValues<StepType>().Length + WorkflowTemplateCatalog.GetAll().Count);
        chunks.Should().OnlyContain(c => c.Meta.Kind == "generated-fact" && c.Audience == PlatformDocsAudience.Developer && c.Url == "");
        chunks.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        chunks.Count(c => c.Meta.StepTypes.Count == 1 && c.Meta.TemplateKeys.Count == 0)
            .Should().Be(Enum.GetValues<StepType>().Length);
        chunks.Where(c => c.Meta.TemplateKeys.Count == 1).SelectMany(c => c.Meta.TemplateKeys)
            .Should().Equal(WorkflowTemplateCatalog.GetAll().Select(t => t.Key));
        chunks.Select(c => c.Meta.CostClass).Should().OnlyContain(k => PlatformDocsMetadata.CostClasses.Contains(k!));
        chunks.Single(c => c.Meta.StepTypes.SequenceEqual(new[] { "Voiceover" }) && c.Meta.TemplateKeys.Count == 0)
            .Text.Should().Contain("Uses credits and a voice");
    }

    [Fact]
    public void Generated_facts_enter_the_corpus_hash_only_when_requested()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gf-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(root, "a.md"), "# A\n\n## S\n\nbody text here\n");
            PlatformDocsCorpus plain = PlatformDocsCorpus.Load(root, 2400);
            PlatformDocsCorpus withFacts = PlatformDocsCorpus.Load(root, 2400, includeGeneratedFacts: true);
            withFacts.Chunks.Count.Should().BeGreaterThan(plain.Chunks.Count);
            withFacts.CorpusHash.Should().NotBe(plain.CorpusHash);
            withFacts.Chunks.Count(c => c.Meta.Kind == "generated-fact").Should().Be(PlatformDocsGeneratedFacts.Build().Count);
        }
        finally
        {
            System.IO.Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GetStepCatalog_tool_is_registered_read_only_and_answers_one_or_all()
    {
        using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid user = Guid.NewGuid();
        AssistantToolProvider provider = AssistantToolTestFactory.CreateProvider(db, new FakeCurrentUser(user));
        AIFunction tool = provider.GetTools(new AssistantToolContext(user)).Single(t => t.Name == "GetStepCatalog");

        AssistantPlatformTools.Tools["GetStepCatalog"].Kind.Should().Be(AssistantToolCostKind.Free);
        AssistantToolProgress.CaptionedTools.Should().Contain("GetStepCatalog");
        tool.Description.Should().NotContain("$");

        string all = JsonSerializer.Serialize(await tool.InvokeAsync(new AIFunctionArguments()));
        using (JsonDocument doc = JsonDocument.Parse(all))
            doc.RootElement.GetProperty("steps").GetArrayLength().Should().Be(Enum.GetValues<StepType>().Length);

        string one = JsonSerializer.Serialize(await tool.InvokeAsync(new AIFunctionArguments { ["stepType"] = "voiceover" }));
        using (JsonDocument doc = JsonDocument.Parse(one))
        {
            doc.RootElement.GetProperty("stepType").GetString().Should().Be("Voiceover");
            doc.RootElement.GetProperty("configFields").ValueKind.Should().Be(JsonValueKind.Object);
        }

        string bad = JsonSerializer.Serialize(await tool.InvokeAsync(new AIFunctionArguments { ["stepType"] = "Nope" }));
        bad.Should().Contain("Unknown step type");
    }
}
