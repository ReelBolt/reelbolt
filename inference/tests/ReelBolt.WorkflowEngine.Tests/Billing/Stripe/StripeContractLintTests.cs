using System;
using System.Net.Http;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>
/// Request-shape lint against <c>stripe-mock</c> (stateless, validates parameter names and types only; it cannot judge flows,
/// proration or webhooks). Run it with
/// <c>docker run --rm -p 12111:12111 stripe/stripe-mock:latest</c> and <c>STRIPE_MOCK_URL=http://localhost:12111</c>;
/// without that variable every test here is skipped.
/// </summary>
public sealed class StripeContractLintTests
{
    private static string? MockUrl => Environment.GetEnvironmentVariable("STRIPE_MOCK_URL");

    private static StripeBillingProvider Provider(bool managedPayments = false)
    {
        StripeOptions options = StripeKit.Options(managedPayments);
        options.BaseUrl = MockUrl!;
        return new StripeBillingProvider(new HttpClient(), Microsoft.Extensions.Options.Options.Create(options), TimeProvider.System);
    }

    [StripeMockFact]
    public async Task Checkout_SubscriptionAndTopUp_AreAccepted()
    {
        Plan plan = new() { Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, ProviderPriceIdsJson = """{"stripe":{"monthly":"price_creator"}}""" };
        Guid org = Guid.NewGuid();

        (await Provider().CreateCheckoutAsync(new CheckoutRequest(org, Guid.NewGuid(), null, plan, 1, null, "cus_1", DiscountId: "rb_founder"), default)).Url.Should().NotBeNullOrEmpty();
        (await Provider(managedPayments: true).CreateCheckoutAsync(new CheckoutRequest(org, Guid.NewGuid(), null, null, 0, 1000, "cus_1"), default)).Url.Should().NotBeNullOrEmpty();
    }

    [StripeMockFact]
    public async Task ChangePlan_Upgrade_AndDowngrade_AreAccepted()
    {
        StripeBillingProvider p = Provider();

        await p.ChangePlanAsync("sub_1", [new BillingLineItem("price_pro", 2)], PlanChangeTiming.Immediately, PlanChangeDiscount.Unchanged, default);
        await p.ChangePlanAsync("sub_1", [new BillingLineItem("price_basic", 1)], PlanChangeTiming.NextBillingPeriod, PlanChangeDiscount.Remove, default);
    }

    [StripeMockFact]
    public async Task Preview_IsAccepted()
        => (await Provider().PreviewPriceAsync([new BillingLineItem("price_creator", 1)], "rb_founder", default)).CurrencyCode.Should().NotBeNullOrEmpty();

    [StripeMockFact]
    public async Task Refund_ByPaymentIntent_IsAccepted()
        => (await Provider().RefundTransactionAsync("pi_1", 100, "contract_lint", "lint:" + Guid.NewGuid(), default)).Id.Should().NotBeNullOrEmpty();
}

/// <summary>Skips at discovery time unless <c>STRIPE_MOCK_URL</c> names a running stripe-mock.</summary>
public sealed class StripeMockFactAttribute : FactAttribute
{
    public StripeMockFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STRIPE_MOCK_URL")))
        {
            Skip = "Requires stripe-mock. Run 'docker run --rm -p 12111:12111 stripe/stripe-mock:latest' and set STRIPE_MOCK_URL=http://localhost:12111.";
        }
    }
}
