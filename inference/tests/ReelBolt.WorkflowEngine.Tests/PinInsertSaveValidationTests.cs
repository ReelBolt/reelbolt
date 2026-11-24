using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-206: a PinInsert step is validated at save time (shape, its analysis, its pins' Step and StepResult references).</summary>
public sealed class PinInsertSaveValidationTests
{
    private static CreateWorkflowStepRequest Pin(string? configJson, int order = 4) =>
        new(Guid.NewGuid(), order, null, null, StepType: "PinInsert", PinInsertConfigJson: configJson);

    private static CreateWorkflowStepRequest Typed(int order, string type) =>
        new(Guid.NewGuid(), order, null, null, StepType: type);

    private const string Valid = """{"analysis":{"from":"Step","stepOrder":1},"pins":[{"content":{"from":"Step","stepOrder":2}}]}""";

    [Fact]
    public void A_missing_or_invalid_config_is_refused_and_names_the_step()
    {
        StepConfigSaveValidator.ValidateStep(Pin(null)).Should().ContainSingle()
            .Which.Should().Contain("Step 4 (PinInsert)").And.Contain("PinInsertConfigJson");
        StepConfigSaveValidator.ValidateStep(Pin("""{"analysis":{"from":"Previous"},"pins":[]}""")).Should().ContainSingle()
            .Which.Should().Contain("at least one");
    }

    [Fact]
    public void A_valid_workflow_passes_the_cross_step_checks()
    {
        InsertContentRefSaveValidator.Validate([Typed(1, "VideoAnalyze"), Typed(2, "Agent"), Pin(Valid)]).Should().BeEmpty();
    }

    [Fact]
    public void The_analysis_must_be_an_earlier_VideoAnalyze_step()
    {
        IReadOnlyList<string> errors = InsertContentRefSaveValidator.Validate([Typed(1, "VideoCompile"), Typed(2, "Agent"), Pin(Valid)]);

        errors.Should().ContainSingle().Which.Should().Contain("earlier VideoAnalyze step");
    }

    [Fact]
    public void Step_content_must_be_an_earlier_step_that_renders()
    {
        const string later = """{"analysis":{"from":"Step","stepOrder":1},"pins":[{"content":{"from":"Step","stepOrder":5}}]}""";
        InsertContentRefSaveValidator.Validate([Typed(1, "VideoAnalyze"), Pin(later), Typed(5, "Agent")])
            .Should().ContainSingle().Which.Should().Contain("earlier step");

        InsertContentRefSaveValidator.Validate([Typed(1, "VideoAnalyze"), Typed(2, "Extract"), Pin(Valid)])
            .Should().ContainSingle().Which.Should().Contain("renders a video or image");
    }

    [Fact]
    public void A_StepResult_pin_is_checked_against_the_project_and_a_still_is_accepted()
    {
        Guid id = Guid.NewGuid();
        string config = $$$"""{"analysis":{"from":"Previous"},"pins":[{"regionId":"r0","content":{"from":"StepResult","stepResultId":"{{{id}}}"}}]}""";

        StepConfigSaveValidator.ValidateStepResultSources([Pin(config)], _ => StepResultSourceVerdict.NotFound)
            .Should().ContainSingle().Which.Should().Contain("was not found in this project");
        StepConfigSaveValidator.ValidateStepResultSources([Pin(config)], _ => StepResultSourceVerdict.Image).Should().BeEmpty();
    }

    [Theory]
    [InlineData("ExtractFrame")]
    [InlineData("ThumbnailGenerate")]
    public void A_StepResult_source_of_a_frame_or_thumbnail_step_is_checked_against_the_project(string type)
    {
        string config = $$$"""{"source":{"kind":"StepResult","stepResultId":"{{{Guid.NewGuid()}}}"}}""";
        CreateWorkflowStepRequest step = type == "ExtractFrame"
            ? new(Guid.NewGuid(), 2, null, null, StepType: type, ExtractFrameConfigJson: config)
            : new(Guid.NewGuid(), 2, null, null, StepType: type, ThumbnailGenerateConfigJson: config);

        StepConfigSaveValidator.ValidateStepResultSources([step], _ => StepResultSourceVerdict.NotFound)
            .Should().ContainSingle().Which.Should().Contain("was not found in this project");
        StepConfigSaveValidator.ValidateStepResultSources([step], _ => StepResultSourceVerdict.Usable).Should().BeEmpty();
    }
}
