#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Replay;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>An ImageGenerate step is validated at save time by its own arm, never skipped by the `_ =>` default.</summary>
public sealed class ImageGenerateSaveValidationTests
{
    private static CreateWorkflowStepRequest Step(string? configJson) =>
        new(Guid.NewGuid(), 3, null, null, StepType: "ImageGenerate", ImageGenerateConfigJson: configJson);

    [Fact]
    public void A_missing_config_is_refused()
    {
        StepConfigSaveValidator.ValidateStep(Step(null)).Should().ContainSingle()
            .Which.Should().Contain("Step 3 (ImageGenerate)").And.Contain("ImageGenerateConfigJson");
    }

    [Fact]
    public void Invalid_json_is_refused() =>
        StepConfigSaveValidator.ValidateStep(Step("{nope")).Should().ContainSingle().Which.Should().Contain("not valid JSON");

    [Fact]
    public void A_json_null_is_refused() =>
        StepConfigSaveValidator.ValidateStep(Step("null")).Should().ContainSingle().Which.Should().Contain("JSON object");

    [Fact]
    public void A_range_error_is_refused_and_names_the_step()
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(Step("""{"mode":"Generate","prompt":"a fox","variations":9}"""));

        errors.Should().ContainSingle().Which.Should().Contain("Step 3 (ImageGenerate)").And.Contain("Variations");
    }

    [Fact]
    public void An_edit_without_a_source_is_refused() =>
        StepConfigSaveValidator.ValidateStep(Step("""{"mode":"Edit","instruction":"warmer"}"""))
            .Should().ContainSingle().Which.Should().Contain("Source is required");

    [Fact]
    public void A_valid_generate_config_is_accepted() =>
        StepConfigSaveValidator.ValidateStep(Step("""{"mode":"Generate","prompt":"a fox","variations":2,"maxSpendCredits":50}"""))
            .Should().BeEmpty();

    [Fact]
    public void The_request_and_the_response_carry_the_config()
    {
        var response = new WorkflowStepResponse(Guid.NewGuid(), Guid.NewGuid(), 1, null, null, "ImageGenerate",
            ImageGenerateConfigJson: "{}");
        response.ImageGenerateConfigJson.Should().Be("{}");
        Step("{}").ImageGenerateConfigJson.Should().Be("{}");
    }

    [Fact]
    public void The_named_provider_reaches_the_ownership_check_under_the_modes_capability()
    {
        Guid id = Guid.NewGuid();
        StepProviderReferences.Extract(StepType.ImageGenerate,
                imageGenerateConfigJson: $$"""{"mode":"Generate","prompt":"x","providerId":"{{id}}"}""")
            .Select(r => (r.Capability, r.ProviderId)).Should().Equal((InferenceProviderCapability.ImageGeneration, id));
        StepProviderReferences.Extract(StepType.ImageGenerate,
                imageGenerateConfigJson: $$"""{"mode":"Edit","instruction":"x","providerId":"{{id}}"}""")
            .Select(r => (r.Capability, r.ProviderId)).Should().Equal((InferenceProviderCapability.ImageEdit, id));
    }

    [Fact]
    public void An_edit_source_naming_a_step_output_depends_on_that_step()
    {
        var steps = new List<WorkflowStep>
        {
            new() { Id = Guid.NewGuid(), StepOrder = 1, StepType = StepType.ExtractFrame },
            new()
            {
                Id = Guid.NewGuid(), StepOrder = 2, StepType = StepType.ImageGenerate,
                ImageGenerateConfigJson = """{"mode":"Edit","instruction":"x","source":{"kind":"StepOutput","stepOrder":1}}"""
            }
        };

        WorkflowStepDependencyGraph.Build(steps).DependsOn(2).Keys.Should().Contain(1);
    }
}
