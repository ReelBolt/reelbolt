#nullable enable
using System.Collections.Generic;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Services.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>stripe-billing-provider T-002: which registered provider serves which request.</summary>
public sealed class BillingProviderSelectorTests
{
    private readonly BillingControllerTests.FakeProvider _paddle = new("paddle");
    private readonly BillingControllerTests.FakeProvider _stripe = new("stripe");

    private IReadOnlyList<IBillingProvider> Both => [_paddle, _stripe];

    [Fact]
    public void Active_IsTheConfiguredProviderNamedBySelection_CaseInsensitively()
    {
        BillingProviderSelector.Active(Both, new BillingProviderOptions { Provider = "Stripe" }).Should().BeSameAs(_stripe);
        BillingProviderSelector.Active(Both, new BillingProviderOptions { Provider = "PADDLE" }).Should().BeSameAs(_paddle);
        BillingProviderSelector.Active(Both, new BillingProviderOptions()).Should().BeSameAs(_paddle, "Paddle is the default");
    }

    [Fact]
    public void Active_WithNoSelectionObject_IsTheFirstConfiguredProvider()
    {
        BillingProviderSelector.Active([new BillingControllerTests.FakeProvider("a", configured: false), _stripe], null).Should().BeSameAs(_stripe);
    }

    [Fact]
    public void Active_IsNullWhenTheSelectedProviderIsNotRegisteredOrNotConfigured()
    {
        BillingProviderSelector.Active([_paddle], new BillingProviderOptions { Provider = "Stripe" }).Should().BeNull();
        BillingProviderSelector.Active([new BillingControllerTests.FakeProvider("stripe", configured: false)], new BillingProviderOptions { Provider = "Stripe" }).Should().BeNull();
    }

    [Fact]
    public void ByName_FindsARegisteredProvider()
    {
        BillingProviderSelector.ByName(Both, "STRIPE").Should().BeSameAs(_stripe);
        BillingProviderSelector.ByName(Both, "other").Should().BeNull();
        BillingProviderSelector.ByName(Both, null).Should().BeNull();
    }

    [Fact]
    public void ForSubscription_PrefersTheRowsProvider_ElseTheActiveOne()
    {
        BillingProviderOptions stripeActive = new() { Provider = "Stripe" };

        BillingProviderSelector.ForSubscription(Both, stripeActive, "paddle").Should().BeSameAs(_paddle);
        BillingProviderSelector.ForSubscription(Both, stripeActive, null).Should().BeSameAs(_stripe);
        BillingProviderSelector.ForSubscription(Both, stripeActive, "gone").Should().BeSameAs(_stripe);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Paddle", true)]
    [InlineData("stripe", true)]
    [InlineData(" STRIPE ", true)]
    [InlineData("braintree", false)]
    public void Options_ValidatorAcceptsOnlyKnownProviders(string? value, bool valid)
    {
        BillingProviderOptions options = value is null ? new BillingProviderOptions() : new BillingProviderOptions { Provider = value };

        new BillingProviderOptionsValidator().Validate(null, options).Succeeded.Should().Be(valid);
    }

    [Fact]
    public void Options_DefaultIsPaddle() => new BillingProviderOptions().Selected.Should().Be("Paddle");
}
