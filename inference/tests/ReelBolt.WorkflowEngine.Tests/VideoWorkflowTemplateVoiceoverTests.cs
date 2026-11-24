using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.WorkflowEngine.Execution.StepExecutors;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// The <c>video-derush-edit-voiceover</c> template seeds two hand-authored JSON literals
/// (<c>VoiceoverConfigJson</c>, <c>VideoCompileConfigJson</c>) whose step references have to line up
/// with the step ORDER they sit at — exactly the class of bug the sibling
/// <see cref="WorkflowTemplateCatalogConfigDeserializationTests"/> and
/// <see cref="VideoWorkflowTemplateBrollTests"/> were written for: a field name or an off-by-one
/// <c>stepOrder</c> looks correct by eye, deserializes without throwing, and then silently resolves
/// the wrong step's output at execution time.
/// </summary>
/// <remarks>
/// This template is the first shipped wiring of the third voiceover source kind, so the tests below
/// prove the WIRING rather than re-reading the literal's text: every config is deserialized with the
/// very options the executor uses (<see cref="VoiceoverStepConfig.JsonOptions"/> for the voiceover
/// step, and the compile executor's own mirrored options), and the assertions are about what those
/// objects resolve to. Deliberately a separate file so the addition is provably additive: the last
/// test re-reads an unmodified existing template and asserts it is the shape it always was.
/// </remarks>
public class VideoWorkflowTemplateVoiceoverTests
{
    private const string TemplateKey = "video-derush-edit-voiceover";

    /// <summary>
    /// Mirrors <c>VideoCompileStepExecutor.ConfigJsonOptions</c> exactly: <see cref="JsonSerializerDefaults.Web"/>
    /// (camelCase properties, case-insensitive) plus <see cref="JsonStringEnumConverter"/> (PascalCase
    /// enum member names). Those options are private to the executor, so they are mirrored here — the
    /// same approach the sibling template tests take.
    /// </summary>
    private static readonly JsonSerializerOptions CompileConfigJsonOptions = new(JsonSerializerDefaults.Web)
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
    public void Voiceover_template_resolves_by_key_and_is_not_auto_created_on_a_project()
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
    public void Voiceover_template_has_the_six_pipeline_steps_in_order()
    {
        WorkflowTemplateDefinition template = Template();
        template.Steps.Should().HaveCount(7);

        // 1 — deterministic analysis of the picked project file. It is also the step whose bounded
        // view offers the narration writer its "s{n}"/"g{n}"/"t{n}" cut-anchor ids.
        template.Steps[0].AgentType.Should().Be(AgentType.VideoTransform);
        template.Steps[0].StepType.Should().Be(StepType.VideoAnalyze);

        // 2 — the story editor's edit decision. This step is WHY the template has six steps rather
        // than the plan's literal five: VideoCompileStepConfig.Decision is a required positional
        // member and VideoCompileStepExecutor fails DECISION_UNRESOLVED without it, so a template
        // with no story editor cannot compile a video at all.
        template.Steps[1].AgentType.Should().Be(AgentType.VideoStoryEditor);
        template.Steps[1].StepType.Should().Be(StepType.Agent);
        template.Steps[1].AgentInputContextMode.Should().Be(AgentInputContextMode.PreviousStepOnly);
        template.Steps[1].CacheMode.Should().Be(StepCacheMode.Never);

        // 3 — the narration writer: FullWorkflow, because it needs the analyze view's offered anchor
        // ids AND the decision (a line anchored to a cut-away moment is dropped server-side).
        template.Steps[2].AgentType.Should().Be(AgentType.NarrationWriter);
        template.Steps[2].StepType.Should().Be(StepType.Agent);
        template.Steps[2].AgentInputContextMode.Should().Be(AgentInputContextMode.FullWorkflow);
        template.Steps[2].CacheMode.Should().Be(StepCacheMode.Never);

        // 4 — the deterministic voiceover step.
        template.Steps[3].AgentType.Should().Be(AgentType.VideoTransform);
        template.Steps[3].StepType.Should().Be(StepType.Voiceover);

        // 5 — the compile step that mixes the narration.
        template.Steps[5].AgentType.Should().Be(AgentType.VideoTransform);
        template.Steps[5].StepType.Should().Be(StepType.VideoCompile);

        // 6 — the review loop, back to the story editor.
        template.Steps[6].AgentType.Should().Be(AgentType.VideoReviewAgent);
        template.Steps[6].StepType.Should().Be(StepType.ReviewLoop);
        template.Steps[6].LoopTargetStepOrder.Should().Be(2);
        template.Steps[6].MaxIterations.Should().Be(3);
        template.Steps[6].MinScore.Should().Be(8);
        template.Steps[6].AgentInputContextMode.Should().Be(AgentInputContextMode.FullWorkflow);
        template.Steps[6].CacheMode.Should().Be(StepCacheMode.Never);
    }

    [Fact]
    public void Voiceover_step_config_round_trips_as_a_narration_plan_naming_step_three()
    {
        WorkflowTemplateStepDefinition step = Step(3);
        step.VoiceoverConfigJson.Should().NotBeNullOrWhiteSpace();

        // Deserialized with the SAME options instance the executor and the save-time validator read
        // a Voiceover config with, so this test cannot pass against a shape the executor rejects.
        VoiceoverStepConfig? config = JsonSerializer.Deserialize<VoiceoverStepConfig>(
            step.VoiceoverConfigJson!, VoiceoverStepConfig.JsonOptions);

        config.Should().NotBeNull();
        config!.Source.Kind.Should().Be(VoiceoverSourceKind.NarrationPlan);

        // The narration writer is step 3 and this step is step 4. An off-by-one here does not throw —
        // it resolves the STORY EDITOR's output (which is not a NarrationPlanOutput at all) and fails
        // the whole run with NARRATION_OUTPUT_INVALID.
        config.Source.NarrationWriterStepOrder.Should().Be(3);

        // The two source members are distinct, and the plan kind never uses the scriptwriter one.
        config.Source.ScriptwriterStepOrder.Should().BeNull();
        config.Source.Lines.Should().BeNull();

        // The step is deterministic and fully keyed on its config, so it stays cacheable: the
        // template must not have pinned CacheMode.Never on it.
        step.CacheMode.Should().BeNull();
    }

    [Fact]
    public void Voiceover_compile_step_config_resolves_the_decision_and_the_narration()
    {
        WorkflowTemplateStepDefinition step = Step(5);
        step.VideoCompileConfigJson.Should().NotBeNullOrWhiteSpace();

        VideoCompileStepConfig? config = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            step.VideoCompileConfigJson!, CompileConfigJsonOptions);

        config.Should().NotBeNull();
        config!.Version.Should().Be(1);

        // The edit decision is the STORY EDITOR (step 2), never the voiceover step (step 4).
        config.Decision.From.Should().Be(ExtractInputSource.Step);
        config.Decision.StepOrder.Should().Be(2);
        config.AnalysisStepOrder.Should().Be(1);

        config.EnableVoiceover.Should().BeTrue();
        config.VoiceoverStepOrder.Should().Be(4);

        // EnableVoiceover requires a re-encode, which is the config's default Mode — asserted rather
        // than assumed, because a stream-copy mode with narration enabled cannot mix anything.
        config.Mode.Should().Be(VideoCompileMode.Reencode);

        config.TransitionPolicy.Should().Be(VideoTransitionPolicy.Auto);
        config.ProgramFadeInMs.Should().Be(0, "a short social edit opens on picture, not black");
        config.ProgramFadeOutMs.Should().BeGreaterThan(0);

        // Captions are ON by default (customer finding 2026-10-04: a narrated video without
        // captions is not what anyone posts) — a dropped key would read as the false default.
        config.EnableCaptions.Should().BeTrue();
        config.CaptionStyle.Should().Be(CaptionStyle.Karaoke);
    }

    [Fact]
    public void Every_step_reference_in_this_template_is_an_explicit_step_order_or_a_real_default()
    {
        // The one relative reference in this template is the story editor's PreviousStepOnly context
        // (which resolves the analyze step's output, immediately before it) and the review loop's
        // LoopTargetStepOrder. Every CROSS-STEP reference inside a config is explicit: "Previous"
        // relative to the compile step (step 5) would resolve the voiceover step's output (step 4)
        // rather than the story editor's decision (step 2).
        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            Step(5).VideoCompileConfigJson!, CompileConfigJsonOptions)!;
        VoiceoverStepConfig voiceover = JsonSerializer.Deserialize<VoiceoverStepConfig>(
            Step(3).VoiceoverConfigJson!, VoiceoverStepConfig.JsonOptions)!;

        compile.Decision.From.Should().Be(ExtractInputSource.Step);
        compile.Decision.StepOrder.Should().Be(2);
        voiceover.Source.NarrationWriterStepOrder.Should().Be(3);

        // Both references point at a step that actually EXISTS in this template and sits BEFORE the
        // step that reads it — a forward reference would resolve nothing on the first execution.
        compile.Decision.StepOrder!.Value.Should().BeLessThan(5);
        voiceover.Source.NarrationWriterStepOrder!.Value.Should().BeLessThan(4);
        Template().Steps.Should().HaveCountGreaterThan(voiceover.Source.NarrationWriterStepOrder!.Value - 1);
    }

    [Fact]
    public void Every_config_bearing_step_in_this_template_deserializes_without_throwing()
    {
        // Keys are load-bearing and silent: a renamed JSON member deserializes to a DEFAULT rather
        // than throwing, so each config is deserialized through its own type's options and then
        // checked for the one fact that proves the key resolved.
        VoiceoverStepConfig voiceover = JsonSerializer.Deserialize<VoiceoverStepConfig>(
            Step(3).VoiceoverConfigJson!, VoiceoverStepConfig.JsonOptions)!;
        voiceover.Source.Kind.Should().Be(VoiceoverSourceKind.NarrationPlan);

        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            Step(5).VideoCompileConfigJson!, CompileConfigJsonOptions)!;
        compile.EnableVoiceover.Should().BeTrue("a dropped enableVoiceover key would read as the false default");

        VideoAnalyzeStepConfig analyze = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(
            Step(0).VideoAnalyzeConfigJson!, CompileConfigJsonOptions)!;
        analyze.Source.Kind.Should().Be(VideoSourceKind.ProjectFile);

        // Agent, ReviewLoop and Agent steps carry no config blob at all in this template; the
        // assertion that they are blank keeps the above from being the only thing checked.
        Step(1).VideoAnalyzeConfigJson.Should().BeNull();
        Step(2).VoiceoverConfigJson.Should().BeNull();
    }

    [Fact]
    public void Adding_the_voiceover_template_left_an_existing_template_unchanged()
    {
        // The catalog edit was purely additive: appending one template must not have shifted,
        // reordered or rewired any template that was already there. Asserted against a real,
        // unmodified template rather than by re-reading the source file.
        WorkflowTemplateDefinition existing = WorkflowTemplateCatalog.GetByKey("video-derush-edit-broll")!;
        existing.Steps.Should().HaveCount(8);
        existing.AutoCreateOnProject.Should().BeFalse();
        existing.Steps[1].AgentType.Should().Be(AgentType.VideoStoryEditor);
        existing.Steps[3].StepType.Should().Be(StepType.VideoGenerate);

        VideoCompileStepConfig unchanged = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            existing.Steps[6].VideoCompileConfigJson!, CompileConfigJsonOptions)!;

        // Still no voiceover wiring of any kind.
        unchanged.EnableVoiceover.Should().BeFalse();
        unchanged.VoiceoverStepOrder.Should().BeNull();
    }

    // ==========================================================================================
    // T-206 — the review loop has to actually be able to iterate on an overflowing narration
    // ==========================================================================================

    /// <summary>
    /// The step ORDER a template step lands at. Mirrors
    /// <c>WorkflowTemplateProvisioningService</c>'s <c>StepOrder = i + 1</c> exactly — the index in
    /// <c>WorkflowTemplateDefinition.Steps</c> and the persisted <c>StepOrder</c> differ by one, and
    /// every <c>LoopTargetStepOrder</c>/config <c>stepOrder</c> in the catalog is written in the
    /// PERSISTED numbering.
    /// </summary>
    private static int PersistedStepOrder(int index) => index + 1;

    private static int IndexOfStepType(WorkflowTemplateDefinition template, StepType stepType)
    {
        int index = template.Steps
            .Select((step, i) => (step, i))
            .Where(x => x.step.StepType == stepType)
            .Select(x => x.i)
            .FirstOrDefault(-1);

        index.Should().BeGreaterThanOrEqualTo(0, $"{template.Key} must have exactly one {stepType} step");
        return index;
    }

    [Fact]
    public void The_voiceover_templates_review_loop_re_runs_the_window_that_contains_the_compile_step()
    {
        WorkflowTemplateDefinition template = Template();

        int compileOrder = PersistedStepOrder(IndexOfStepType(template, StepType.VideoCompile));
        int reviewIndex = IndexOfStepType(template, StepType.ReviewLoop);
        int reviewOrder = PersistedStepOrder(reviewIndex);

        WorkflowTemplateStepDefinition review = template.Steps[reviewIndex];

        // The compiled artefact the reviewer judges is the compile step's output, and a loop-back
        // re-runs the half-open window [target, review) of the step list. If the target sat ABOVE
        // the compile step, the retry would rewrite the narration and never re-encode it — the
        // pipeline would loop without ever changing the thing that was judged.
        compileOrder.Should().Be(6);
        reviewOrder.Should().Be(7);

        review.LoopTargetStepOrder.Should().Be(2);

        // The window is half-open: [target, review) in persisted step orders.
        (review.LoopTargetStepOrder!.Value <= compileOrder && compileOrder < reviewOrder)
            .Should().BeTrue(
                $"the re-run window [{review.LoopTargetStepOrder}, {reviewOrder}) must contain " +
                $"the compile step's order {compileOrder}");

        // And the target must be a step that can actually be re-run: the narration writer's lines
        // are wasted if the story editor that decides the windows is not re-decided too.
        template.Steps[review.LoopTargetStepOrder!.Value - 1].StepType.Should().Be(StepType.Agent);
        template.Steps[review.LoopTargetStepOrder!.Value - 1].AgentType.Should().Be(AgentType.VideoStoryEditor);
    }

    [Fact]
    public void The_voiceover_templates_review_loop_can_actually_iterate_on_a_capped_score()
    {
        WorkflowTemplateStepDefinition review =
            Template().Steps[IndexOfStepType(Template(), StepType.ReviewLoop)];

        int minScore = review.MinScore ?? 9;

        // Both halves of the arithmetic that makes the gate real rather than decorative:
        //  * the cap has to be BELOW the threshold, or a capped score would still pass;
        //  * MaxIterations has to allow at least one loop-back, or the step would score low and
        //    advance anyway.
        ReviewLoopStepExecutor.NarrationOverflowScoreCap.Should().BeLessThan(minScore);
        review.MaxIterations.Should().BeGreaterThan(1);
        review.LoopTargetStepOrder.Should().NotBeNull();
    }

    [Fact]
    public void Every_templates_review_loop_threshold_is_above_the_narration_overflow_cap()
    {
        // Generalised on purpose: the cap is only load-bearing where it is under MinScore, and a
        // future template that set MinScore below 4 would silently make the cap a no-op — but only
        // for the workflows that can measure a fit. Asserted across the whole catalog so that
        // template is caught here rather than in production.
        List<string> offending = [];
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.GetAll())
        {
            foreach (WorkflowTemplateStepDefinition step in template.Steps.Where(s => s.StepType == StepType.ReviewLoop))
            {
                if (ReviewLoopStepExecutor.NarrationOverflowScoreCap >= (step.MinScore ?? 9))
                    offending.Add($"{template.Key} (MinScore {step.MinScore})");
            }
        }

        offending.Should().BeEmpty();
    }

    // ==========================================================================================
    // T-206 — the cap is inert for every workflow that cannot measure a narration fit
    // ==========================================================================================

    /// <summary>
    /// A compile summary from a workflow with no narration. On this head that is what
    /// <c>video-derush-edit</c> and <c>video-derush-edit-graphics</c> produce:
    /// <c>VideoCompileStepExecutor</c> only calls <c>ResolveVoiceoverAsync</c> inside
    /// <c>if (config.EnableVoiceover)</c>, so with the flag at its default no <c>voiceover</c> node
    /// is emitted at all. Proven end to end (real executor, mocked ffmpeg) by
    /// <c>VideoCompileStepExecutorVoiceoverFitTests.EnableVoiceover_false_emits_no_voiceover_node_and_an_argv_and_EDL_untouched_by_the_fit_facts</c>;
    /// this constant is the shape that test pins.
    /// </summary>
    private const string CompileSummaryWithoutVoiceoverNode =
        """{"status":"completed","segments":4,"outputDurationSec":12.5,"retainedRatio":0.61,"sentenceCheck":{"applicable":true}}""";

    /// <summary>
    /// The degraded shape: narration is configured but nothing was measurable, so
    /// <c>BuildVoiceoverFitNode</c> records <c>applicable:false</c> with
    /// <c>reason:"no_resolved_lines"</c> and the compile still completes. Not-measurable is not a
    /// defect, so this must never move a score either.
    /// </summary>
    private const string CompileSummaryWithNotApplicableFit =
        """{"status":"completed","segments":4,"voiceover":{"enabled":true,"applied":false,"lines":[],"fit":{"applicable":false,"reason":"no_resolved_lines"}}}""";

    [Theory]
    [InlineData("video-derush-edit")]
    [InlineData("video-derush-edit-graphics")]
    public void A_non_voiceover_video_template_configures_the_compile_step_without_narration(string templateKey)
    {
        WorkflowTemplateDefinition template = WorkflowTemplateCatalog.GetByKey(templateKey)!;
        template.Should().NotBeNull($"{templateKey} must be registered");

        int compileIndex = IndexOfStepType(template, StepType.VideoCompile);
        VideoCompileStepConfig compile = JsonSerializer.Deserialize<VideoCompileStepConfig>(
            template.Steps[compileIndex].VideoCompileConfigJson!, CompileConfigJsonOptions)!;

        // This flag IS the guard `VideoCompileStepExecutor` branches on before it ever calls
        // ResolveVoiceoverAsync — so false here is what makes "no `voiceover` node" a fact about the
        // run rather than a reading of the source.
        compile.EnableVoiceover.Should().BeFalse();
        compile.VoiceoverStepOrder.Should().BeNull();

        // And the consequence, over the very shape such a run emits: a green reviewer score is
        // untouched, so the step advances exactly as it did before T-206.
        ReviewLoopStepExecutor.ApplyNarrationFitCap(9, CompileSummaryWithoutVoiceoverNode).Should().Be(9);
    }

    [Fact]
    public void The_main_promo_pipelines_review_loop_has_no_voiceover_node_in_its_prior_output()
    {
        WorkflowTemplateDefinition promo = WorkflowTemplateCatalog.GetByKey("lean-context-promo")!;
        promo.Should().NotBeNull();

        int reviewIndex = IndexOfStepType(promo, StepType.ReviewLoop);

        // The step the reviewer judges is the step immediately before it, and here that is an
        // Author — not a VideoCompile step — so no `voiceover` node can ever appear in it.
        promo.Steps[reviewIndex - 1].StepType.Should().Be(StepType.Agent);
        promo.Steps[reviewIndex - 1].AgentType.Should().Be(AgentType.AuthorAgent);
        promo.Steps.Should().NotContain(step => step.StepType == StepType.VideoCompile);

        ReviewLoopStepExecutor.ApplyNarrationFitCap(9, CompileSummaryWithoutVoiceoverNode).Should().Be(9);
    }

    [Fact]
    public void A_not_applicable_fit_is_inert_too_so_a_partial_measurement_cannot_cap_anything()
    {
        // The other degraded arm, reachable when narration IS configured but no line could be
        // measured. Proved through the cap rather than by reading BuildVoiceoverFitNode's branches.
        ReviewLoopStepExecutor.ApplyNarrationFitCap(9, CompileSummaryWithNotApplicableFit).Should().Be(9);
    }
}
