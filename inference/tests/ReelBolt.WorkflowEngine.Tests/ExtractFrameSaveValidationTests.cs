using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>T-105: an ExtractFrame step is validated at save time, never silently skipped by the `_ =>` default.</summary>
public sealed class ExtractFrameSaveValidationTests
{
    private static CreateWorkflowStepRequest Step(string? configJson) =>
        new(Guid.NewGuid(), 4, null, null, StepType: "ExtractFrame", ExtractFrameConfigJson: configJson);

    [Fact]
    public void A_missing_config_is_refused()
    {
        StepConfigSaveValidator.ValidateStep(Step(null)).Should().ContainSingle()
            .Which.Should().Contain("Step 4 (ExtractFrame)").And.Contain("ExtractFrameConfigJson");
    }

    [Fact]
    public void Invalid_json_is_refused()
    {
        StepConfigSaveValidator.ValidateStep(Step("{nope")).Should().ContainSingle()
            .Which.Should().Contain("not valid JSON");
    }

    [Fact]
    public void A_domain_error_is_refused_and_names_the_step()
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(
            Step("""{"source":{"kind":"StepOutput","stepOrder":2},"at":"Time"}"""));

        errors.Should().ContainSingle().Which.Should().Contain("Step 4 (ExtractFrame)").And.Contain("AtSec");
    }

    [Fact]
    public void A_valid_config_is_accepted()
    {
        StepConfigSaveValidator.ValidateStep(
            Step("""{"source":{"kind":"StepOutput","stepOrder":2},"at":"Last","format":"jpeg","maxLongSide":1280}"""))
            .Should().BeEmpty();
    }
}
