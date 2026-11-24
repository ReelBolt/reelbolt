#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-108: an AudioGenerate step is validated at save time by its own arm, never skipped by the `_ =>` default.</summary>
public sealed class AudioGenerateSaveValidationTests
{
    private static CreateWorkflowStepRequest Step(string? configJson) =>
        new(Guid.NewGuid(), 3, null, null, StepType: "AudioGenerate", AudioGenerateConfigJson: configJson);

    [Fact]
    public void A_missing_config_is_refused()
    {
        StepConfigSaveValidator.ValidateStep(Step(null)).Should().ContainSingle()
            .Which.Should().Contain("Step 3 (AudioGenerate)").And.Contain("AudioGenerateConfigJson");
    }

    [Fact]
    public void Invalid_json_is_refused() =>
        StepConfigSaveValidator.ValidateStep(Step("{nope")).Should().ContainSingle().Which.Should().Contain("not valid JSON");

    [Fact]
    public void A_json_null_is_refused() =>
        StepConfigSaveValidator.ValidateStep(Step("null")).Should().ContainSingle().Which.Should().Contain("JSON object");

    [Fact]
    public void A_catalogue_range_error_is_refused_and_names_the_step()
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(
            Step("""{"kind":"SoundEffect","prompt":"whoosh","durationSec":45}"""));

        errors.Should().ContainSingle().Which.Should().Contain("Step 3 (AudioGenerate)").And.Contain("DurationSec");
    }

    [Fact]
    public void A_valid_config_is_accepted() =>
        StepConfigSaveValidator.ValidateStep(
            Step("""{"kind":"Music","prompt":"warm pop, no vocals","durationSec":30,"instrumental":true,"takes":2,"maxSpendCredits":400}"""))
            .Should().BeEmpty();

    [Fact]
    public void The_request_and_the_response_carry_the_config()
    {
        var response = new WorkflowStepResponse(Guid.NewGuid(), Guid.NewGuid(), 1, null, null, "AudioGenerate",
            AudioGenerateConfigJson: "{}");
        response.AudioGenerateConfigJson.Should().Be("{}");
        Step("{}").AudioGenerateConfigJson.Should().Be("{}");
    }

    [Fact]
    public void The_named_provider_reaches_the_ownership_check()
    {
        Guid id = Guid.NewGuid();
        var references = ReelBolt.Shared.Workflows.StepProviderReferences.Extract(
            StepType.AudioGenerate, audioGenerateConfigJson: $$"""{"kind":"Music","prompt":"x","durationSec":3,"providerId":"{{id}}"}""");
        references.Select(r => r.ProviderId).Should().Equal(id);
    }
}
