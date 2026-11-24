using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Agents.Tools;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-007 of the prompted-region-effects initiative: the shipped template, and the audit that says the
/// workflow assistant needs no new code to build these steps.
///
/// <para>
/// The feature is exposed to customers through exactly two surfaces and no new assistant tool: the
/// workflow config (an analyze target with <c>method = Segmentation</c> plus a compile
/// <c>regionEffects</c> entry) and this template. These tests deserialize the template's own literals
/// with the options the real executors use — the same guard
/// <see cref="WorkflowTemplateCatalogConfigDeserializationTests"/> applies to every other hand-written
/// config — and pin the two properties the initiative cannot afford to get wrong: the shipped egress
/// consent is OFF, and every template stays OFF.
/// </para>
/// </summary>
public class WorkflowTemplateRegionEffectTests
{
    // Mirrors VideoAnalyzeStepExecutor.ConfigJsonOptions / VideoCompileStepExecutor.ConfigJsonOptions.
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string TemplateKey = WorkflowTemplateCatalog.RegionEffectTemplateKey;

    /// <summary>The prompt this template ships — the object the customer is told to rename.</summary>
    private const string Prompt = "the speedometer";

    private static WorkflowTemplateDefinition Template()
    {
        WorkflowTemplateDefinition? template = WorkflowTemplateCatalog.BaseByKey(TemplateKey);
        template.Should().NotBeNull($"template '{TemplateKey}' must be registered in WorkflowTemplateCatalog");
        return template!;
    }

    private static VideoAnalyzeStepConfig AnalyzeConfig() =>
        JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(Template().Steps[0].VideoAnalyzeConfigJson!, ConfigJsonOptions)!;

    private static VideoCompileStepConfig CompileConfig() =>
        JsonSerializer.Deserialize<VideoCompileStepConfig>(Template().Steps[1].VideoCompileConfigJson!, ConfigJsonOptions)!;

    // ---------------------------------------------------------------- the template itself

    [Fact]
    public void The_template_ships_the_two_deterministic_steps_the_walkthrough_needs()
    {
        WorkflowTemplateDefinition template = Template();

        template.Name.Should().Be("Blur one object through the whole clip");
        template.Version.Should().Be(1);
        template.RequiresUserInput.Should().BeTrue("the customer picks the clip and names the object to blur");
        template.AutoCreateOnProject.Should().BeFalse("nothing is provisioned on a project that never asked for it");

        template.Steps.Should().HaveCount(2);
        template.Steps.Select(s => s.StepType).Should().Equal(StepType.VideoAnalyze, StepType.VideoCompile);
        template.Steps.Should().OnlyContain(
            s => s.AgentType == AgentType.VideoTransform,
            "both steps are deterministic — no agent and no editorial decision runs in this workflow");
        template.Steps.Should().OnlyContain(
            s => s.CacheMode == null,
            "deterministic analysis/compile steps stay cacheable through the built-in default");
    }

    [Fact]
    public void The_analyze_step_tracks_the_prompt_with_the_hosted_model_and_keeps_consent_off()
    {
        VideoAnalyzeStepConfig config = AnalyzeConfig();

        config.Version.Should().Be(1);
        config.Source.Kind.Should().Be(
            VideoSourceKind.ProjectFile,
            "the first step has no previous output to read, and ProjectFile visibly reads as unconfigured");

        config.TrackObjects.Should().HaveCount(1);
        ObjectTrackTarget target = config.TrackObjects![0];
        target.Label.Should().Be(Prompt, "for a segmentation target the label IS the model's prompt");
        target.Method.Should().Be(ObjectTrackMethod.Segmentation, "the hosted model is the explicit opt-in, never silent");
        target.Region.Should().BeNull("no box is drawn on a frame — the prompt alone finds the object");
        target.AtSec.Should().BeNull();

        config.Transcription.Should().Be(
            VideoTranscriptionMode.Off,
            "this workflow never reads a transcript, so it must not pay for an ASR pass");
        config.AllowSourceMediaEgress.Should().BeFalse(
            "a template must never pre-consent to a customer's footage leaving this server");
        config.MaxSegmentationSeconds.Should().Be(60, "the shipped cost guard is the documented 60 s default, stated explicitly");
        config.SegmentationProviderId.Should().BeNull(
            "null resolves the deployment's own default VideoSegmentation provider; no provider fails SEGMENTATION_NO_PROVIDER");
        config.MaxSegmentationObjects.Should().Be(4, "the house default of four objects rides the record's own default");
    }

    [Fact]
    public void The_compile_step_blurs_that_prompt_and_keeps_every_source_whole()
    {
        VideoCompileStepConfig config = CompileConfig();

        config.Version.Should().Be(1);
        config.KeepWholeSources.Should().BeTrue("this workflow adds an effect and never cuts, so no editor decision is resolved");
        config.Decision.Should().NotBeNull("the record's shape still requires a decision reference");
        config.Decision!.From.Should().Be(ExtractInputSource.Previous);
        config.AnalysisStepOrder.Should().Be(1);
        config.Mode.Should().Be(VideoCompileMode.Reencode, "masking an effect onto the picture has no stream-copy equivalent");

        config.RegionEffects.Should().HaveCount(1);
        VideoRegionEffect effect = config.RegionEffects![0];
        effect.Target.Should().Be(Prompt, "the target is the label the customer typed, never an opaque id");
        effect.Effect.Should().Be(RegionEffect.Blur);
        effect.Invert.Should().BeFalse("the effect lands on the object, not on everything else");
        effect.Strength.Should().Be(1.0);
        effect.FeatherPx.Should().Be(0);
        effect.Opacity.Should().Be(1.0);

        RegionEffectRules.Validate(config.RegionEffects, config.Mode).Should().BeEmpty(
            "the template ships a list the API's save path and the engine's compile check both accept");
    }

    [Fact]
    public void No_shipped_template_ever_pre_consents_to_source_media_egress()
    {
        // The rule this initiative cannot break twice: a template is chosen by a customer who has read
        // nothing but a one-line blurb, so it must never carry the consent that sends their footage to a
        // hosted vendor. Asserted across the whole catalog so a future template inherits it.
        foreach (WorkflowTemplateDefinition template in WorkflowTemplateCatalog.BaseTemplates)
        {
            foreach (WorkflowTemplateStepDefinition step in template.Steps.Where(s => s.StepType == StepType.VideoAnalyze))
            {
                VideoAnalyzeStepConfig config = JsonSerializer.Deserialize<VideoAnalyzeStepConfig>(
                    step.VideoAnalyzeConfigJson!, ConfigJsonOptions)!;

                config.AllowSourceMediaEgress.Should().BeFalse(
                    $"{template.Key} step '{step.Label}' must not pre-consent to source media egress");
            }
        }

        WorkflowTemplateCatalog.BaseTemplates.Should().Contain(
            t => t.Key == TemplateKey,
            "the loop above must actually cover the segmentation template it exists for");
    }

    [Fact]
    public void The_template_passes_the_same_save_time_validation_a_hand_built_workflow_does()
    {
        WorkflowTemplateDefinition template = Template();
        List<CreateWorkflowStepRequest> steps = [.. template.Steps.Select((s, i) => new CreateWorkflowStepRequest(
            AgentDefinitionId: Guid.NewGuid(),
            StepOrder: i + 1,
            EdgeConditionJson: null,
            Label: s.Label,
            StepType: s.StepType.ToString(),
            LoopTargetStepOrder: s.LoopTargetStepOrder,
            MaxIterations: s.MaxIterations,
            MinScore: s.MinScore,
            ExtractConfigJson: s.ExtractConfigJson,
            VideoAnalyzeConfigJson: s.VideoAnalyzeConfigJson,
            VideoCompileConfigJson: s.VideoCompileConfigJson,
            EditRoomConfigJson: s.EditRoomConfigJson,
            GraphicsRoomConfigJson: s.GraphicsRoomConfigJson,
            ColorGradeRoomConfigJson: s.ColorGradeRoomConfigJson,
            VideoGenerateConfigJson: s.VideoGenerateConfigJson,
            VoiceoverConfigJson: s.VoiceoverConfigJson,
            DailiesRoomConfigJson: s.DailiesRoomConfigJson,
            ConditionExpression: s.ConditionExpression,
            TrueBranchStepOrder: s.TrueBranchStepOrder,
            FalseBranchStepOrder: s.FalseBranchStepOrder,
            DesignSystemConfigJson: s.DesignSystemConfigJson))];

        StepConfigSaveValidator.Validate(steps).Should().BeEmpty($"template '{template.Key}' must be savable as shipped");

        // And the compile check the engine runs, on the same literal the save path just accepted.
        VideoCompileStepConfig compile = CompileConfig();
        RegionEffectRules.Validate(compile.RegionEffects, compile.Mode).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- assistant exposure audit

    [Fact]
    public void The_assistant_already_reads_the_new_fields_from_the_step_schemas_it_is_handed()
    {
        // Assistant exposure is ListStepSchemas (StepConfigSchemaExporter) plus the config blob passed
        // through verbatim (AssistantStepShapes, asserted in the test below). The exporter reflects the
        // config RECORDS, so the segmentation knobs and regionEffects reached the assistant with T-001
        // and no assistant tool, prompt or shape needed a line of its own.
        var exporter = new StepConfigSchemaExporter();

        JsonObject analyze = exporter.GetSchema(StepType.VideoAnalyze).Schema!["properties"]!.AsObject();
        foreach (string name in new[]
                 {
                     "trackObjects", "segmentationProviderId", "allowSourceMediaEgress", "maxSegmentationSeconds",
                     "segmentationDetectionThreshold", "maxSegmentationObjects", "segmentationTimeoutSeconds"
                 })
        {
            analyze.ContainsKey(name).Should().BeTrue($"ListStepSchemas must show the assistant '{name}'");
        }

        // The shape a target carries, as the model receives it: the method enum is what makes the
        // hosted model an explicit opt-in rather than a silent swap for the free tracker.
        JsonNode trackObjects = analyze["trackObjects"]!;
        JsonNode method = trackObjects["items"]!["properties"]!["method"]!;
        method["enum"]!.AsArray().Select(e => e!.GetValue<string>()).Should().Contain(
            [nameof(ObjectTrackMethod.Feature), nameof(ObjectTrackMethod.Segmentation)]);

        JsonObject compile = exporter.GetSchema(StepType.VideoCompile).Schema!["properties"]!.AsObject();
        compile.ContainsKey("regionEffects").Should().BeTrue("ListStepSchemas must show the assistant 'regionEffects'");
        compile["regionEffects"]!["items"]!["properties"]!["effect"]!["enum"]!.AsArray()
            .Select(e => e!.GetValue<string>()).Should().Contain(
                Enum.GetNames<RegionEffect>(),
                "every closed-set effect the engine accepts is in the schema the model reads");
    }

    /// <summary>
    /// The inversion of the tripwire this ticket shipped with. Every other self-documenting field in the repo
    /// writes <c>[property: Description(...)]</c>, because on a positional record a bare <c>[Description]</c>
    /// lands on the CONSTRUCTOR PARAMETER, where <see cref="StepConfigSchemaExporter"/>'s attribute hook
    /// cannot see it. T-001's segmentation knobs and <c>RegionEffects</c> used the bare form, so the model got
    /// the property names and types but not that <c>allowSourceMediaEgress</c> IS the egress consent, or that
    /// an effect list needs <c>Mode = Reencode</c>. They now carry the property-level attribute, so this
    /// asserts what the tripwire asserted the ABSENCE of: the prose reaches the assistant's schema, and the
    /// emitted text IS the record's own attribute text — the record stays the single source of truth, exactly
    /// as <c>StepConfigSchemaExporterTests.GetSchema_EmitsTheCaveat_…</c> pins the T-207 caveats.
    /// <para>
    /// <c>trackObjects</c> is deliberately NOT in the description set: it is a list of
    /// <see cref="ObjectTrackTarget"/> and carries no <c>[Description]</c> of its own, so the model reads its
    /// shape (asserted by <see cref="The_assistant_already_reads_the_new_fields_from_the_step_schemas_it_is_handed"/>)
    /// rather than prose about it. Nothing is skipped silently.
    /// </para>
    /// </summary>
    [Fact]
    public void The_new_fields_descriptions_reach_the_assistant_schema()
    {
        var exporter = new StepConfigSchemaExporter();

        JsonObject analyze = exporter.GetSchema(StepType.VideoAnalyze).Schema!["properties"]!.AsObject();
        JsonObject compile = exporter.GetSchema(StepType.VideoCompile).Schema!["properties"]!.AsObject();

        (string Json, string Clr)[] segmentationKnobs =
        [
            ("segmentationProviderId", nameof(VideoAnalyzeStepConfig.SegmentationProviderId)),
            ("allowSourceMediaEgress", nameof(VideoAnalyzeStepConfig.AllowSourceMediaEgress)),
            ("maxSegmentationSeconds", nameof(VideoAnalyzeStepConfig.MaxSegmentationSeconds)),
            ("segmentationDetectionThreshold", nameof(VideoAnalyzeStepConfig.SegmentationDetectionThreshold)),
            ("maxSegmentationObjects", nameof(VideoAnalyzeStepConfig.MaxSegmentationObjects)),
            ("segmentationTimeoutSeconds", nameof(VideoAnalyzeStepConfig.SegmentationTimeoutSeconds)),
        ];

        DocumentedAmong(analyze, segmentationKnobs.Select(k => k.Json)).Should().Equal(
            segmentationKnobs.Select(k => k.Json),
            "every segmentation knob must carry [property: Description(...)]; a bare [Description] lands on the "
            + "constructor parameter and the exporter's hook cannot lift it");

        foreach ((string json, string clr) in segmentationKnobs)
        {
            analyze[json]!["description"]!.GetValue<string>().Should().Be(
                DescriptionOf<VideoAnalyzeStepConfig>(clr),
                $"the schema must quote '{json}'s own attribute, never a paraphrase of it");
        }

        compile["regionEffects"]!["description"]!.GetValue<string>().Should().Be(
            DescriptionOf<VideoCompileStepConfig>(nameof(VideoCompileStepConfig.RegionEffects)),
            "regionEffects documents the Mode=Reencode requirement, and that sentence is the attribute's");
    }

    /// <summary>The named properties that carry a schema-level <c>description</c> today.</summary>
    private static List<string> DocumentedAmong(JsonObject properties, IEnumerable<string> names) =>
        [.. names.Where(name => properties[name]?["description"] is not null)];

    /// <summary>
    /// The single <c>[property: Description(...)]</c> text on <paramref name="clrPropertyName"/>, read off the
    /// config record itself — the same read the exporter's hook makes, so the two cannot disagree.
    /// </summary>
    private static string DescriptionOf<TConfig>(string clrPropertyName) =>
        typeof(TConfig).GetProperty(clrPropertyName)!
            .GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), inherit: false)
            .Cast<System.ComponentModel.DescriptionAttribute>()
            .Single()
            .Description;

    [Fact]
    public void The_assistant_passes_this_templates_configs_through_untouched_and_the_result_stays_savable()
    {
        // AssistantStepShapes copies VideoAnalyzeConfigJson/VideoCompileConfigJson verbatim, so the
        // assistant can already read this workflow, change one field, and propose the whole list back.
        WorkflowTemplateDefinition template = Template();
        var agentIds = new Dictionary<AgentType, Guid> { [AgentType.VideoTransform] = Guid.NewGuid() };

        List<CreateWorkflowStepRequest> shapes = [];
        for (int i = 0; i < template.Steps.Count; i++)
        {
            WorkflowTemplateStepDefinition step = template.Steps[i];
            CreateWorkflowStepRequest shape = AssistantStepShapes.FromTemplateStep(step, i + 1, agentIds);

            shape.AgentDefinitionId.Should().Be(agentIds[AgentType.VideoTransform]);
            shape.StepType.Should().Be(step.StepType.ToString());
            shape.VideoAnalyzeConfigJson.Should().Be(step.VideoAnalyzeConfigJson, "the config blob crosses the assistant boundary byte for byte");
            shape.VideoCompileConfigJson.Should().Be(step.VideoCompileConfigJson, "the config blob crosses the assistant boundary byte for byte");
            shapes.Add(shape);
        }

        StepConfigSaveValidator.Validate(shapes).Should().BeEmpty(
            "a workflow the assistant read from this template and proposed back is savable unchanged");
    }
}
