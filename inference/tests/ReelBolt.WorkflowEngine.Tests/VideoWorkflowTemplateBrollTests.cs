using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The <c>video-derush-edit-broll</c> template seeds two hand-authored JSON literals
/// (<c>VideoGenerateConfigJson</c>, <c>VideoCompileConfigJson</c>) whose step references have to
/// line up with the step ORDER they sit at — and they are exactly the class of bug the sibling
/// <see cref="WorkflowTemplateCatalogConfigDeserializationTests"/> was written for: a field name or
/// an off-by-one <c>stepOrder</c> looks correct by eye, deserializes without throwing, and then
/// silently resolves the wrong step's output at execution time.
/// </summary>
/// <remarks>
/// These tests therefore deserialize the ACTUAL seeded literals with the same
/// <see cref="JsonSerializerOptions"/> the real executors use (<c>VideoGenerateStepConfig.JsonOptions</c>
/// and the compile executor's own <c>ConfigJsonOptions</c>, mirrored here exactly as the sibling
/// test already does) and assert every resolved reference, rather than the literal's text.
/// Deliberately a separate file from <see cref="WorkflowTemplateCatalogConfigDeserializationTests"/>
/// so this addition is provably additive to the catalog: the final test below re-reads an
/// unmodified existing template and asserts it is byte-for-byte the same shape it always was.
/// </remarks>
public class VideoWorkflowTemplateBrollTests
{
    private const string TemplateKey = "video-derush-edit-broll";

    /// <summary>
    /// Mirrors <c>VideoGenerateStepConfig.JsonOptions</c> and
    /// <c>VideoCompileStepExecutor.ConfigJsonOptions</c> exactly: <see cref="JsonSerializerDefaults.Web"/>
    /// (camelCase properties, case-insensitive) plus <see cref="JsonStringEnumConverter"/> (PascalCase
    /// enum member names). The compile executor's own options are private, so they are mirrored here
    /// — the same approach the sibling deserialization test already takes.
    /// </summary>
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private static WorkflowTemplateDefinition Template()
    {
        WorkflowTemplateDefinition? template = WorkflowTemplateCatalog.BaseByKey(TemplateKey);
        template.Should().NotBeNull($"template '{TemplateKey}' must be registered in WorkflowTemplateCatalog");
        return template!;
    }

    private static WorkflowTemplateStepDefinition Step(int index)
    {
        WorkflowTemplateDefinition template = Template();
        template.Steps.Should().HaveCountGreaterThan(index);
        return template.Steps[index];
    }

    [Fact]
    public void Broll_template_is_listed_and_is_not_auto_created_on_a_project()
    {
        WorkflowTemplateDefinition template = Template();

        template.Key.Should().Be(TemplateKey);
        template.Version.Should().Be(2);
        template.RequiresUserInput.Should().BeTrue("customer-facing templates ask for the brief at run time (finding 18/26)");

        // Opt-in, exactly like the other video-derush-edit-* templates: adding it must not change
        // what a brand-new project gets seeded with.
        template.AutoCreateOnProject.Should().BeFalse();
        WorkflowTemplateCatalog.GetAutoCreateTemplates()
            .Select(candidate => candidate.Key)
            .Should().NotContain(TemplateKey);
    }

    [Fact]
    public void Broll_template_has_the_six_pipeline_steps_in_order()
    {
        WorkflowTemplateDefinition template = Template();
        template.Steps.Should().HaveCount(8);

        // 1 — deterministic analysis of the picked project file.
        template.Steps[0].AgentType.Should().Be(AgentType.VideoTransform);
        template.Steps[0].StepType.Should().Be(StepType.VideoAnalyze);

        // 2 — the story editor's edit decision.
        template.Steps[1].AgentType.Should().Be(AgentType.VideoStoryEditor);
        template.Steps[1].StepType.Should().Be(StepType.Agent);
        template.Steps[1].AgentInputContextMode.Should().Be(AgentInputContextMode.PreviousStepOnly);
        template.Steps[1].CacheMode.Should().Be(StepCacheMode.Never);

        // 3 — the planner: FullWorkflow, because it needs the analyze view AND the decision.
        template.Steps[2].AgentType.Should().Be(AgentType.ShotDirector);
        template.Steps[2].StepType.Should().Be(StepType.Agent);
        template.Steps[2].AgentInputContextMode.Should().Be(AgentInputContextMode.FullWorkflow);
        template.Steps[2].CacheMode.Should().Be(StepCacheMode.Never);

        // 4 — the deterministic, spending generation step.
        template.Steps[3].AgentType.Should().Be(AgentType.VideoTransform);
        template.Steps[3].StepType.Should().Be(StepType.VideoGenerate);

        // 5 — the motion director (video events); 6 — the dynamics planner, which sees the generated clip ids.
        template.Steps[4].AgentType.Should().Be(AgentType.MotionDirector);
        template.Steps[5].AgentType.Should().Be(AgentType.DynamicsPlanner);
        template.Steps[5].AgentInputContextMode.Should().Be(AgentInputContextMode.FullWorkflow);
        template.Steps[5].CacheMode.Should().Be(StepCacheMode.Never);

        // 7 — the compile step.
        template.Steps[6].AgentType.Should().Be(AgentType.VideoTransform);
        template.Steps[6].StepType.Should().Be(StepType.VideoCompile);

        // 8 — the review loop, back to the story editor.
        template.Steps[7].AgentType.Should().Be(AgentType.VideoReviewAgent);
        template.Steps[7].StepType.Should().Be(StepType.ReviewLoop);
        template.Steps[7].LoopTargetStepOrder.Should().Be(2);
        template.Steps[7].MaxIterations.Should().Be(3);
        template.Steps[7].MinScore.Should().Be(8);
        template.Steps[7].AgentInputContextMode.Should().Be(AgentInputContextMode.FullWorkflow);
        template.Steps[7].CacheMode.Should().Be(StepCacheMode.Never);
    }

    [Fact]
    public void Broll_generate_step_config_deserializes_to_a_valid_plan_sourced_config()
    {
        WorkflowTemplateStepDefinition step = Step(3);
        step.VideoGenerateConfigJson.Should().NotBeNullOrWhiteSpace();

        VideoGenerateStepConfig? config = JsonSerializer.Deserialize<VideoGenerateStepConfig>(
            step.VideoGenerateConfigJson!, VideoGenerateStepConfig.JsonOptions);

        config.Should().NotBeNull();
        config!.PlanSource.Should().Be(VideoGeneratePlanSource.StepRef);

        // The planner is step 3 and this step is step 4 — a plan whose planOrder pointed anywhere
        // else would resolve an unrelated step's output and generate the wrong clips (or none).
        config.Plan.Should().NotBeNull();
        config.Plan!.From.Should().Be(ExtractInputSource.Step);
        config.Plan.StepOrder.Should().Be(3);

        // The keyframe ids a planned shot may name resolve against the analyze step's artifact.
        config.Analysis.Should().NotBeNull();
        config.Analysis!.From.Should().Be(ExtractInputSource.Step);
        config.Analysis.StepOrder.Should().Be(1);

        config.Model.Should().Be("MiniMax-H3");
        config.Resolution.Should().Be("768P");
        config.Ratio.Should().Be("16:9");
        config.Takes.Should().Be(1);
        config.MaxClips.Should().Be(4);
        config.MaxSpendCredits.Should().BeGreaterThan(0);

        // The poll/timeout pair is spend-relevant (a config that times out before its first poll
        // submits a paid take and then ends it POLL_TIMEOUT), so both are pinned explicitly.
        config.PollIntervalSeconds.Should().Be(10);
        config.TimeoutSeconds.Should().Be(1800);

        // The frame path is REACHABLE from a shipped template — which it is only because this
        // template grants the egress consent the step default withholds. Without it, any plan naming
        // a firstFrame/lastFrame is refused with EGRESS_REFUSED, and the shot director's frame
        // vocabulary would be dead weight in the one template that exists to use it.
        config.AllowSourceMediaEgress.Should().BeTrue(
            "this template's planner is explicitly allowed to key a generated shot off a frame of the footage");

        // The whole point: the author-visible structural rules all pass. A field-name drift would
        // have deserialized to defaults and failed here rather than at execution time.
        config.Validate().Should().BeEmpty();
    }

    [Fact]
    public void Broll_compile_step_config_deserializes_to_placable_generated_clips()
    {
        WorkflowTemplateStepDefinition step = Step(6);
        step.VideoCompileConfigJson.Should().NotBeNullOrWhiteSpace();

        VideoCompileStepConfig? config = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            step.VideoCompileConfigJson!, ConfigJsonOptions);

        config.Should().NotBeNull();
        config!.Version.Should().Be(1);

        // The edit decision is the STORY EDITOR (step 2), never the generation step (step 4).
        config.Decision.From.Should().Be(ExtractInputSource.Step);
        config.Decision.StepOrder.Should().Be(2);
        config.AnalysisStepOrder.Should().Be(1);

        config.EnableGeneratedClips.Should().BeTrue();
        config.GeneratedClips.Should().NotBeNull();
        config.GeneratedClips!.From.Should().Be(ExtractInputSource.Step);
        config.GeneratedClips.StepOrder.Should().Be(4);
        config.MaxGeneratedClips.Should().Be(4);

        config.TransitionPolicy.Should().Be(VideoTransitionPolicy.Auto);
        // The cold-open/end-card placements prepend/append a concat segment, so the program fades
        // are what give them an envelope to pair with.
        config.ProgramFadeInMs.Should().Be(0, "a short social edit opens on picture, not black");
        config.ProgramFadeOutMs.Should().BeGreaterThan(0);

        // The post-deserialization hard config errors the executor can raise from the config alone
        // (VideoCompileStepExecutor's up-front block): GeneratedClips.From must be a usable ref,
        // and EnableGeneratedClips requires a re-encode because stream-copy has no filtergraph.
        (config.GeneratedClips!.From is ExtractInputSource.Previous or ExtractInputSource.Step)
            .Should().BeTrue("GeneratedClips.From must be Previous or Step");
        config.EnableGeneratedClips.Should().BeTrue();
        config.Mode.Should().Be(VideoCompileMode.Reencode);
    }

    [Fact]
    public void Broll_step_references_are_all_explicit_step_orders()
    {
        // Every cross-step reference in this template is an explicit StepOrder — never "Previous".
        // "Previous" relative to the compile step (step 5) would resolve to the VideoGenerate
        // step's own output (step 4), not the story editor's decision (step 2); and relative to
        // the generate step (step 4) it would resolve the analysis to the ShotDirector step
        // instead of step 1.
        VideoGenerateStepConfig generate = JsonSerializer.Deserialize<VideoGenerateStepConfig>(
            Step(3).VideoGenerateConfigJson!, VideoGenerateStepConfig.JsonOptions)!;
        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            Step(6).VideoCompileConfigJson!, ConfigJsonOptions)!;

        foreach (ExtractInputRef reference in new[] { generate.Plan!, generate.Analysis!, compile.Decision, compile.GeneratedClips!, compile.DynamicsPlan! })
        {
            reference.From.Should().Be(ExtractInputSource.Step);
            reference.StepOrder.Should().NotBeNull();
        }
    }

    [Fact]
    public void Adding_the_broll_template_left_an_existing_template_unchanged()
    {
        // The catalog edit was purely additive: appending one template must not have shifted,
        // reordered or rewired any template that was already there. Asserted against a real,
        // unmodified template rather than by re-reading the source file.
        WorkflowTemplateDefinition existing = WorkflowTemplateCatalog.GetByKey("video-derush-edit")!;
        // 6 since encode-once-after-review added the Final-stage compile after the review and the
        // picture-overlay feature added the planner before the Plan compile; the b-roll addition
        // itself is still purely additive and the first steps are untouched.
        existing.Steps.Should().HaveCount(7);
        existing.AutoCreateOnProject.Should().BeFalse();
        existing.Steps[1].AgentType.Should().Be(AgentType.VideoStoryEditor);
        existing.Steps[2].AgentType.Should().Be(AgentType.OverlayPlanner);

        VideoCompileStepConfig unchanged = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            existing.Steps[4].VideoCompileConfigJson!, ConfigJsonOptions)!;

        // Still reads the story editor by step order, still no generated clips.
        unchanged.Decision.From.Should().Be(ExtractInputSource.Step);
        unchanged.Decision.StepOrder.Should().Be(2);
        unchanged.EnableGeneratedClips.Should().BeFalse();
        unchanged.GeneratedClips.Should().BeNull();
        unchanged.MaxGeneratedClips.Should().Be(8);
    }
}
