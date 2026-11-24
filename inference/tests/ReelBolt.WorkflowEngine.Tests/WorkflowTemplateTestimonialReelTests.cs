using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Lint;
using ReelBolt.WorkflowEngine.Tests.Workflows.Lint;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The curated testimonial recipe (docs/design/picture-overlay.md §8.5): a talking head plus a
/// screenshot, pre-wired with the OverlayPlanner and the Plan/Final compile split. The shape is
/// asserted literally so a renumbering or a lost config reference fails here rather than in a run.
/// </summary>
public class WorkflowTemplateTestimonialReelTests
{
    private static WorkflowTemplateDefinition Template() =>
        WorkflowTemplateCatalog.BaseByKey(WorkflowTemplateCatalog.TestimonialReelTemplateKey)!;

    private static JsonElement Json(string? raw) => JsonDocument.Parse(raw!).RootElement;

    [Fact]
    public void The_recipe_is_registered_with_the_customer_copy_the_trigger_run_expects()
    {
        WorkflowTemplateDefinition template = Template();

        template.Name.Should().Be("Testimonial reel from a talking-head clip and a screenshot");
        template.Description.Should().Be(
            "Upload a talking-head clip and a screenshot — a review, a message, a result. We cut the clip and show the screenshot as a full-screen card or a corner inset at the moment the agent chooses.");
        template.Version.Should().Be(2);
        template.AutoCreateOnProject.Should().BeFalse();
        template.RequiresUserInput.Should().BeTrue();
        template.AppManifest.Should().NotBeNull();
    }

    [Fact]
    public void The_recipe_has_the_six_step_shape_with_the_planner_and_both_compile_references()
    {
        WorkflowTemplateDefinition template = Template();

        template.Steps.Should().HaveCount(7);
        template.Steps.Select(s => s.StepType).Should().Equal(
            StepType.VideoAnalyze, StepType.Agent, StepType.Agent, StepType.Agent, StepType.VideoCompile, StepType.ReviewLoop, StepType.VideoCompile);
        template.Steps.Select(s => s.AgentType).Should().Equal(
            AgentType.VideoTransform, AgentType.VideoStoryEditor, AgentType.OverlayPlanner,
            AgentType.MotionDirector, AgentType.VideoTransform, AgentType.VideoReviewAgent, AgentType.VideoTransform);

        JsonElement analyze = Json(template.Steps[0].VideoAnalyzeConfigJson);
        analyze.GetProperty("source").GetProperty("kind").GetString().Should().Be("ProjectFile");
        analyze.GetProperty("source").TryGetProperty("projectFileId", out _).Should().BeFalse("an open slot, not a pinned file");
        analyze.GetProperty("emitOverlayOffers").GetBoolean().Should().BeTrue();

        template.Steps[1].AgentInputContextMode.Should().Be(AgentInputContextMode.PreviousStepOnly);
        template.Steps[1].CacheMode.Should().Be(StepCacheMode.Never);
        template.Steps[2].AgentInputContextMode.Should().Be(AgentInputContextMode.FullWorkflow);
        template.Steps[2].CacheMode.Should().Be(StepCacheMode.Never);

        JsonElement plan = Json(template.Steps[4].VideoCompileConfigJson);
        plan.GetProperty("decision").GetProperty("from").GetString().Should().Be("Step");
        plan.GetProperty("decision").GetProperty("stepOrder").GetInt32().Should().Be(2);
        plan.GetProperty("analysisStepOrder").GetInt32().Should().Be(1);
        plan.GetProperty("mediaOverlayPlan").GetProperty("from").GetString().Should().Be("Step");
        plan.GetProperty("mediaOverlayPlan").GetProperty("stepOrder").GetInt32().Should().Be(3);
        plan.GetProperty("encodeStage").GetString().Should().Be("Plan");

        WorkflowTemplateStepDefinition review = template.Steps[5];
        review.LoopTargetStepOrder.Should().Be(2);
        review.MaxIterations.Should().Be(3);
        review.MinScore.Should().Be(8);
        review.AgentInputContextMode.Should().Be(AgentInputContextMode.FullWorkflow);
        review.CacheMode.Should().Be(StepCacheMode.Never);

        JsonElement final = Json(template.Steps[6].VideoCompileConfigJson);
        final.GetProperty("decision").GetProperty("stepOrder").GetInt32().Should().Be(2);
        final.GetProperty("analysisStepOrder").GetInt32().Should().Be(1);
        final.GetProperty("mediaOverlayPlan").GetProperty("stepOrder").GetInt32().Should().Be(3);
        final.GetProperty("encodeStage").GetString().Should().Be("Final");
    }

    [Fact]
    public async Task The_recipe_lints_clean_of_the_overlay_and_graph_rules()
    {
        IReadOnlyList<LintFinding> findings = await LintKit.LintAsync(LintKit.FromTemplate(Template()));

        string[] overlayRules = ["WL004", "WL010", "WL018", "WL023", "WL024"];
        findings.Where(f => overlayRules.Contains(f.RuleId)).Should().BeEmpty();
        findings.Where(f => f.Severity == LintSeverity.Error).Should().BeEmpty("the shipped recipe must be runnable as-is");
    }
}
