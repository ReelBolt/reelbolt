#nullable enable
using System;
using System.Collections.Generic;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Metering;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>an ImageGenerate step is fixed-price on the run estimate ("up to"), its declared price winning.</summary>
public sealed class ImageGenerateCostEstimateTests
{
    private static WorkflowStep Step(int order, StepType type) => new()
    {
        Id = Guid.NewGuid(),
        StepOrder = order,
        StepType = type,
        AgentDefinitionId = Guid.NewGuid()
    };

    [Fact]
    public void A_declared_image_price_is_both_the_hold_and_the_up_to_figure()
    {
        WorkflowStep[] steps = [Step(1, StepType.Agent), Step(2, StepType.ImageGenerate)];
        var declared = new Dictionary<int, long> { [2] = 75_000 };

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom(
            [], steps, declared, DisplayCreditInputs.Empty, SeedProfilePricer.Instance);

        WorkflowStepCostEstimate image = estimate.Steps[1];
        image.HoldMillis.Should().Be(75_000);
        image.FixedMaxMillis.Should().Be(75_000);
        image.Basis.Should().Be(nameof(CreditEstimateBasis.ImageGeneration));
        image.TypicalMillis.Should().Be(0, "a fixed-price step leaves the variable range");
        estimate.FixedMaxMillis.Should().Be(75_000);
    }

    [Fact]
    public void An_undeclared_image_step_holds_its_default_and_shows_up_to_its_ceiling()
    {
        WorkflowStep[] steps = [Step(1, StepType.ImageGenerate)];
        (long low, long high) = StepCreditDefaults.MillisFor(StepType.ImageGenerate, SeedProfilePricer.Instance.Markup);

        WorkflowCostEstimate estimate = WorkflowCostEstimator.EstimateFrom(
            [], steps, null, DisplayCreditInputs.Empty, SeedProfilePricer.Instance);

        estimate.Steps[0].HoldMillis.Should().Be(low);
        estimate.Steps[0].FixedMaxMillis.Should().Be(high);
        estimate.RequiredMillis.Should().Be(low);
    }
}
