using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// T-021 exposed <c>ObjectTrackTarget.MatchScope</c> on the form; a field the backend does not read is a
/// control that does nothing, so the SAVE path refuses an unknown <c>VideoAnalyze</c> property. T-024
/// (security remediation) folded that deep check into the shared required/JSON/object validation: the
/// old early return answered "no errors" for an empty or non-object config, which is exactly the
/// fail-open the shared path exists to prevent.
/// </summary>
public class VideoAnalyzeSaveValidationTests
{
    private static CreateWorkflowStepRequest AnalyzeStep(string? configJson) =>
        JsonSerializer.Deserialize<CreateWorkflowStepRequest>(new JsonObject
        {
            ["agentDefinitionId"] = Guid.NewGuid().ToString(),
            ["stepOrder"] = 1,
            ["stepType"] = "VideoAnalyze",
            ["videoAnalyzeConfigJson"] = configJson
        }.ToJsonString(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    private const string Minimal = """{"version":1,"source":{"kind":"PreviousStepOutput"}}""";

    [Fact]
    public void A_missing_config_is_refused_before_anything_else()
    {
        StepConfigSaveValidator.ValidateStep(AnalyzeStep(null)).Should().ContainSingle()
            .Which.Should().Contain("Step 1 (VideoAnalyze)").And.Contain("VideoAnalyzeConfigJson is required");
    }

    [Fact]
    public void A_config_that_is_not_JSON_is_refused()
    {
        StepConfigSaveValidator.ValidateStep(AnalyzeStep("{nope")).Should().ContainSingle()
            .Which.Should().Contain("not valid JSON");
    }

    [Fact]
    public void A_config_that_is_not_an_object_is_refused()
    {
        StepConfigSaveValidator.ValidateStep(AnalyzeStep("null")).Should().ContainSingle()
            .Which.Should().Contain("must be a JSON object");
    }

    [Fact]
    public void A_valid_config_saves()
    {
        StepConfigSaveValidator.ValidateStep(AnalyzeStep(Minimal)).Should().BeEmpty();
    }

    [Fact]
    public void MatchScope_is_a_property_the_backend_reads()
    {
        string config = """
            {"version":1,"source":{"kind":"PreviousStepOutput"},
             "trackObjects":[{"label":"speedometer","method":"Segmentation","matchScope":"SingleTrack"}]}
            """;

        StepConfigSaveValidator.ValidateStep(AnalyzeStep(config)).Should().BeEmpty(
            "MatchScope is honoured end to end; the save path must not refuse it");
    }

    [Fact]
    public void An_unknown_top_level_property_is_refused()
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(
            AnalyzeStep("""{"version":1,"source":{"kind":"PreviousStepOutput"},"matchScop":"SingleTrack"}"""));

        errors.Should().ContainSingle().Which.Should().Contain("unknown property 'matchScop'");
    }

    [Fact]
    public void An_unknown_track_objects_property_is_refused()
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(AnalyzeStep("""
            {"version":1,"source":{"kind":"PreviousStepOutput"},
             "trackObjects":[{"label":"speedometer","method":"Segmentation","scope":"SingleTrack"}]}
            """));

        errors.Should().ContainSingle().Which.Should()
            .Contain("track objects entry 1").And.Contain("unknown property 'scope'");
    }

    [Fact]
    public void The_green_screen_templates_inert_placeholders_key_still_saves()
    {
        // "placeholders" is the template picker's inert note naming the field a copy still needs filled;
        // the typed config ignores it (WorkflowTemplateCatalog, green-screen-insert). It is not drift.
        StepConfigSaveValidator.ValidateStep(AnalyzeStep("""
            {"version":1,"source":{"kind":"ProjectFile"},"detectInsertRegions":true,
             "transcription":"Off","vision":"Off","placeholders":{"source.projectFileId":"the clip"}}
            """)).Should().BeEmpty();
    }

    [Fact]
    public void Every_shipped_templates_analyze_step_still_saves()
    {
        List<(string Template, WorkflowTemplateStepDefinition Step)> analyzeSteps =
        [
            .. WorkflowTemplateCatalog.GetAll().SelectMany(template => template.Steps
                .Where(step => step.StepType == StepType.VideoAnalyze && !string.IsNullOrWhiteSpace(step.VideoAnalyzeConfigJson))
                .Select(step => (template.Key, step)))
        ];

        analyzeSteps.Should().NotBeEmpty("the catalog ships analyze steps; this audit must not pass vacuously");

        foreach ((string template, WorkflowTemplateStepDefinition step) in analyzeSteps)
        {
            StepConfigSaveValidator.ValidateStep(AnalyzeStep(step.VideoAnalyzeConfigJson!))
                .Should().BeEmpty($"template '{template}' must stay savable");
        }
    }
}
