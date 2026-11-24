using AwesomeAssertions;
using ReelBolt.Shared.Metering;
using Xunit;
using ModelStepType = ReelBolt.Shared.Data.Models.StepType;

namespace ReelBolt.WorkflowEngine.Tests.DesignSystems.StepType;

/// <summary>
/// P2-12: a <see cref="ModelStepType.DesignSystem"/> step is zero-rated — its Parse/Compose/Require
/// ops make no provider call of their own, so <c>IsNotCharged</c> is true and the estimator's basis is
/// <see cref="CreditEstimateBasis.NotCharged"/> rather than a cold-start default.
/// </summary>
public sealed class DesignSystemStepCreditTests
{
    [Fact]
    public void The_DesignSystem_default_holds_nothing()
    {
        StepCreditDefaults.MillisFor(ModelStepType.DesignSystem)
            .Should().Be((StepCreditDefaults.NoHold, StepCreditDefaults.NoHold));
    }

    [Fact]
    public void The_DesignSystem_step_type_is_not_charged()
    {
        StepCreditDefaults.IsNotCharged(ModelStepType.DesignSystem).Should().BeTrue();
    }

    [Fact]
    public void The_estimator_reports_the_not_charged_basis_for_DesignSystem()
    {
        StepCreditEstimate estimate = new StepCreditEstimator([]).Estimate(ModelStepType.DesignSystem, null);

        estimate.Basis.Should().Be(CreditEstimateBasis.NotCharged);
        estimate.HoldMillis.Should().Be(0);
    }
}
