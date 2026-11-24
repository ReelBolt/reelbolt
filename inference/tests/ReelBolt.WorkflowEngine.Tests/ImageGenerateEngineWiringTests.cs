#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AwesomeAssertions;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Workflows;
using ReelBolt.Shared.Workflows.Apps;
using ReelBolt.Shared.Workflows.Replay;
using ReelBolt.WorkflowEngine.Execution.Caching;
using ReelBolt.WorkflowEngine.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>The engine-wide decisions every new step type needs (cache, credits, failures, providers, run rate, cost class).</summary>
public sealed class ImageGenerateEngineWiringTests
{
    private const string GenerateJson = """{"mode":"Generate","prompt":"a fox"}""";

    private static WorkflowStep Image(int order, string? configJson = GenerateJson) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = StepType.ImageGenerate,
        ImageGenerateConfigJson = configJson
    };

    [Fact]
    public void ImageGenerate_is_not_step_cached_but_CacheMode_Always_still_overrides()
    {
        new StepCachePolicy().IsCacheable(Image(1)).Should().BeFalse();
        new StepCachePolicy().IsCacheable(new WorkflowStep { StepType = StepType.ImageGenerate, CacheMode = StepCacheMode.Always }).Should().BeTrue();
    }

    [Fact]
    public void ImageGenerate_is_charged_fixed_price_and_not_agent_shaped()
    {
        StepCreditDefaults.IsNotCharged(StepType.ImageGenerate).Should().BeFalse();
        (long low, long high) = StepCreditDefaults.MillisFor(StepType.ImageGenerate);
        low.Should().BePositive();
        high.Should().BeGreaterThan(low);
        StepCreditDefaults.IsDeclaredPriced(StepType.ImageGenerate).Should().BeTrue();
        ColdStartStepPricing.IsAgentShaped(StepType.ImageGenerate).Should().BeFalse();
        StepUsageProfiles.For(StepType.ImageGenerate, null).Quantities.Should().BeEmpty();
        RunCreditHolds.IsCold(StepType.ImageGenerate, new StepCreditEstimate(1, 2, 1, CreditEstimateBasis.Default)).Should().BeFalse();
    }

    [Fact]
    public void A_declared_image_price_wins_over_history_and_is_labelled_as_image()
    {
        StepCreditEstimate estimate = new StepCreditEstimator([]).Estimate(StepType.ImageGenerate, null, 77_000);

        estimate.HoldMillis.Should().Be(77_000);
        estimate.Basis.Should().Be(CreditEstimateBasis.ImageGeneration);
    }

    [Fact]
    public void ImageGenerate_needs_no_plan_feature_only_credits()
    {
        PlanGate.FeatureFor(StepType.ImageGenerate).Should().BeNull();
    }

    [Theory]
    [InlineData("NO_IMAGE_PROVIDER", FailureClass.UserInput)]
    [InlineData("IMAGE_REQUEST_INVALID", FailureClass.UserInput)]
    [InlineData("IMAGE_VENDOR_REJECTED", FailureClass.UserInput)]
    [InlineData("SOURCE_NOT_IMAGE", FailureClass.UserInput)]
    [InlineData("EGRESS_REFUSED", FailureClass.UserInput)]
    [InlineData("IMAGE_PROVIDER_CONFIG", FailureClass.PlatformInternal)]
    [InlineData("IMAGE_EGRESS_UNAVAILABLE", FailureClass.PlatformInternal)]
    [InlineData("IMAGE_GENERATE_FAILED", FailureClass.PlatformInternal)]
    [InlineData("IMAGE_VENDOR_RETRYABLE", FailureClass.PlatformProviderError)]
    [InlineData("IMAGE_EMPTY", FailureClass.PlatformProviderError)]
    [InlineData("SUBMIT_OUTCOME_UNKNOWN", FailureClass.PlatformProviderError)]
    [InlineData("RESERVATION_ORPHANED", FailureClass.PlatformInternal)]
    [InlineData("MODEL_UNPRICED", FailureClass.PlatformInternal)]
    public void The_image_failure_codes_are_classified_deliberately(string code, FailureClass expected) =>
        FailureClassifier.ForCode(code).Should().Be(expected);

    [Theory]
    [InlineData("Generate", InferenceProviderCapability.ImageGeneration)]
    [InlineData("Edit", InferenceProviderCapability.ImageEdit)]
    public void The_named_provider_is_a_reference_of_the_modes_capability(string mode, InferenceProviderCapability capability)
    {
        Guid id = Guid.NewGuid();
        string json = $$"""{"mode":"{{mode}}","prompt":"x","providerId":"{{id}}"}""";

        StepProviderReferences.Extract(StepType.ImageGenerate, imageGenerateConfigJson: json)
            .Should().ContainSingle().Which.Should().Be(new StepProviderReference(capability, id, "providerId"));
        StepProviderReferences.Extract(StepType.ImageGenerate, imageGenerateConfigJson: GenerateJson).Should().BeEmpty();
        StepProviderReferences.Extract(StepType.ImageGenerate, imageGenerateConfigJson: "not json").Should().BeEmpty();
    }

    [Fact]
    public void The_step_cache_key_moves_with_the_image_config_and_is_unchanged_without_one()
    {
        var builder = new StepCacheKeyBuilder();
        StepCacheKeyInputs inputs = new()
        {
            ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            StepType = StepType.ImageGenerate,
            AgentType = AgentType.VideoStoryEditor,
            AgentDefinitionId = Guid.Empty,
            AgentInputContextMode = AgentInputContextMode.PreviousStepOnly,
            ProjectFileFingerprint = "fp",
            MaxIterations = 3
        };

        string without = builder.Build(inputs);
        builder.Build(inputs with { ImageGenerateConfigJson = null }).Should().Be(without);
        builder.Build(inputs with { ImageGenerateConfigJson = GenerateJson }).Should().NotBe(without);
    }

    [Fact]
    public void The_run_rate_is_always_paid()
    {
        RunRateClassifier.IsStepFree(Image(1)).Should().BeFalse();
        RunRateClassifier.Classify([Image(1)]).Should().Be(RunRateBucket.Paid);
    }

    [Fact]
    public void The_app_cost_class_names_generation()
    {
        AppCostClass.Derive([new AppStepFacts(StepType.ImageGenerate, null, false)]).Should().Be(AppCostClass.UsesCreditsAndAiShots);
    }

    [Fact]
    public void The_config_column_exists_and_the_card_is_declared()
    {
        typeof(WorkflowStep).GetProperty(nameof(WorkflowStep.ImageGenerateConfigJson)).Should().NotBeNull();
        typeof(ImageGenerateStepConfig).GetCustomAttributes(typeof(StepCardAttribute), false).Should().ContainSingle();
    }
}
