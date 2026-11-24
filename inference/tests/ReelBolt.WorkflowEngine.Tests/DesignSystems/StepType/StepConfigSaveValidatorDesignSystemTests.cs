using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Controllers.Dto;
using ReelBolt.Inference.Api.Services.Workflows;
using ReelBolt.Shared.Data.Models;
using Xunit;
using ModelStepType = ReelBolt.Shared.Data.Models.StepType;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.StepType;

/// <summary>
/// Save-time validator coverage for <see cref="ModelStepType.DesignSystem"/>: the dedicated arm
/// (missing/blank, invalid JSON, not a JSON object, plus <c>Validate()</c>) and the workflow-aware
/// Compose fallback checks that only the whole-step-list pass can make. Every step is built by
/// deserializing raw JSON with the web defaults, the same way the HTTP binder and the assistant
/// propose/apply path do.
/// </summary>
public sealed class StepConfigSaveValidatorDesignSystemTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static CreateWorkflowStepRequest Step(string stepType, int stepOrder, string? designSystemConfigJson = null)
    {
        var node = new JsonObject
        {
            ["agentDefinitionId"] = Guid.NewGuid().ToString(),
            ["stepOrder"] = stepOrder,
            ["stepType"] = stepType
        };

        if (designSystemConfigJson is not null)
            node["designSystemConfigJson"] = designSystemConfigJson;

        return JsonSerializer.Deserialize<CreateWorkflowStepRequest>(node.ToJsonString(), WebJson)!;
    }

    private static CreateWorkflowStepRequest DesignSystemStep(int stepOrder, string? configJson = null) =>
        Step(nameof(ModelStepType.DesignSystem), stepOrder, configJson);

    private static CreateWorkflowStepRequest AgentStep(int stepOrder) =>
        Step(nameof(ModelStepType.Agent), stepOrder);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_DesignSystem_step_without_a_config_is_rejected(string? configJson)
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(DesignSystemStep(4, configJson));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("DesignSystemConfigJson is required.");
    }

    [Fact]
    public void A_DesignSystem_step_whose_config_is_not_valid_JSON_is_rejected()
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(DesignSystemStep(4, "{bogus"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("DesignSystemConfigJson is not valid JSON:");
    }

    [Fact]
    public void A_DesignSystem_step_whose_config_is_not_a_JSON_object_is_rejected()
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(DesignSystemStep(4, "null"));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("DesignSystemConfigJson must be a JSON object.");
    }

    [Fact]
    public void A_DesignSystem_step_whose_config_is_a_JSON_scalar_is_rejected_as_invalid_JSON()
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(DesignSystemStep(4, "\"just a string\""));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("DesignSystemConfigJson is not valid JSON:");
    }

    [Fact]
    public void A_DesignSystem_step_with_a_JSON_config_that_fails_Validate_is_rejected()
    {
        IReadOnlyList<string> errors = StepConfigSaveValidator.ValidateStep(
            DesignSystemStep(4, """{"op":"Compose","compose":{"parseStepOrder":0}}"""));

        errors.Should().ContainSingle();
        errors[0].Should().Contain("parseStepOrder");
    }

    [Fact]
    public void Compose_fallback_steps_that_are_missing_not_agent_or_not_before_it_are_rejected()
    {
        var steps = new List<CreateWorkflowStepRequest>
        {
            DesignSystemStep(9,
                """{"op":"Compose","version":1,"compose":{"parseStepOrder":1,"designFallbackStepOrders":[99,3,10]}}"""),
            Step(nameof(ModelStepType.Conditional), 3),
            AgentStep(10)
        };

        IReadOnlyList<string> errors = StepConfigSaveValidator.Validate(steps);

        errors.Should().HaveCount(3);
        errors.Should().Contain(error => error.Contains("fallback step order 99 does not exist."));
        errors.Should().Contain(error => error.Contains("fallback step order 3 is not an Agent step."));
        errors.Should().Contain(error => error.Contains("fallback step order 10 must be before the Compose step."));
    }

    [Fact]
    public void Compose_fallback_steps_that_are_all_earlier_Agent_steps_are_accepted()
    {
        var steps = new List<CreateWorkflowStepRequest>
        {
            AgentStep(1),
            AgentStep(2),
            AgentStep(3),
            DesignSystemStep(9,
                """{"op":"Compose","version":1,"compose":{"parseStepOrder":1,"designFallbackStepOrders":[3],"productFallbackStepOrders":[2]}}""")
        };

        StepConfigSaveValidator.Validate(steps).Should().BeEmpty();
    }

    [Fact]
    public void A_DesignSystem_step_with_a_valid_Parse_config_is_accepted()
    {
        StepConfigSaveValidator.ValidateStep(DesignSystemStep(1, """{"op":"Parse","version":1,"parse":{}}"""))
            .Should().BeEmpty();
    }
}
