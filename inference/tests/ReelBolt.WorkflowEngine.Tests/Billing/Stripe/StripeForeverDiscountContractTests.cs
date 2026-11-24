using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripeKit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>Provider contract for forever discounts (the alpha founder price): 50 %, forever, no code, every sellable plan.</summary>
public sealed class StripeForeverDiscountContractTests
{
    private static readonly Guid PromotionId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly string CouponId = $"rb_promo_{PromotionId}";
    private static readonly DateTime EndsAt = new(2027, 1, 31, 22, 59, 0, DateTimeKind.Utc);

    private static ProviderDiscountSpec Founder(DateTime? expires = null, string[]? prices = null)
        => new(PromotionId, "Founder price", PromotionDiscountType.Percentage, "50", null, true, null, null, prices ?? [], expires);

    private sealed class FailOnAnyCall : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Stripe must not be called: " + request.Method + " " + request.RequestUri);
        }
    }

    // ---- rule 1: forever coupon

    [Fact]
    public async Task Rule1_ForeverCoupon_IsDurationForever_Percent50_AppliesToTheProductsOfTheRestrictedPrices()
    {
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/prices/price_creator", new { id = "price_creator", product = "prod_creator" })
            .On("GET", "/v1/prices/price_team", new { id = "price_team", product = "prod_team" })
            .On("POST", "/v1/coupons", new { id = CouponId });

        string id = await Provider(fake).CreateDiscountAsync(Founder(prices: ["price_creator", "price_team"]), default);

        id.Should().Be(CouponId);
        fake.To("/v1/prices/price_creator").Should().ContainSingle();
        fake.To("/v1/prices/price_team").Should().ContainSingle();
        StripeCall c = fake.To("/v1/coupons").Single();
        c.F("id").Should().Be(CouponId);
        c.F("duration").Should().Be("forever");
        c.F("percent_off").Should().Be("50");
        c.HasF("duration_in_months").Should().BeFalse();
        c.HasF("amount_off").Should().BeFalse();
        c.HasF("redeem_by").Should().BeFalse("no expiry given");
        c.Form.Where(p => p.Key == "applies_to[products][]").Select(p => p.Value).Should().Equal("prod_creator", "prod_team");
    }

    // ---- rule 2: expiry is only a redeem_by belt, never a removal

    [Fact]
    public async Task Rule2_ExpiresAt_BecomesRedeemByOnly()
    {
        StripeFake fake = new StripeFake().On("POST", "/v1/coupons", new { id = CouponId });

        await Provider(fake).CreateDiscountAsync(Founder(expires: EndsAt), default);

        EndsAt.Should().Be(new DateTime(2027, 1, 31, 22, 59, 0, DateTimeKind.Utc));
        StripeCall c = fake.To("/v1/coupons").Single();
        c.F("redeem_by").Should().Be("1801436340");
        c.F("duration").Should().Be("forever", "the expiry never shortens the discount of existing subscribers");
        c.HasF("duration_in_months").Should().BeFalse();
    }

    [Fact]
    public async Task Rule2_UpdateDiscount_ExpiryClearAndPause_NeverReachStripe()
    {
        FailOnAnyCall handler = new();
        StripeBillingProvider provider = new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(Options()), new FixedClock(Now), null, (_, _) => Task.CompletedTask);

        await provider.UpdateDiscountAsync(CouponId, null, EndsAt, false, default);
        await provider.UpdateDiscountAsync(CouponId, null, null, true, default);
        await provider.UpdateDiscountAsync(CouponId, false, null, false, default);
        await provider.UpdateDiscountAsync(CouponId, false, EndsAt, true, default);

        handler.Calls.Should().Be(0);
    }

    // ---- rule 3: checkout

    private static readonly Plan Creator = new() { Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, ProviderPriceIdsJson = """{"stripe":{"monthly":"price_creator"}}""" };

    private static StripeFake CheckoutFake() => new StripeFake()
        .On("POST", "/v1/checkout/sessions", new { id = "cs_test_1", url = "https://checkout.stripe.com/c/pay/cs_test_1" });

    [Fact]
    public async Task Rule3_Checkout_CouponId_IsExactlyOneDiscountsCoupon_AndNoAllowPromotionCodes()
    {
        StripeFake fake = CheckoutFake();

        await Provider(fake).CreateCheckoutAsync(new CheckoutRequest(StripePayloads.OrgId, Guid.NewGuid(), null, Creator, 1, null, "cus_1", DiscountId: CouponId, PromotionId: PromotionId), default);

        StripeCall c = fake.To("/v1/checkout/sessions").Single();
        c.F("discounts[0][coupon]").Should().Be(CouponId);
        c.Form.Count(p => p.Key.StartsWith("discounts[")).Should().Be(1);
        c.HasF("discounts[0][promotion_code]").Should().BeFalse();
        c.HasF("allow_promotion_codes").Should().BeFalse();
    }

    [Fact]
    public async Task Rule3_Checkout_PromoId_IsSentAsPromotionCode()
    {
        StripeFake fake = CheckoutFake();

        await Provider(fake).CreateCheckoutAsync(new CheckoutRequest(StripePayloads.OrgId, Guid.NewGuid(), null, Creator, 1, null, "cus_1", DiscountId: "promo_ABC"), default);

        StripeCall c = fake.To("/v1/checkout/sessions").Single();
        c.F("discounts[0][promotion_code]").Should().Be("promo_ABC");
        c.HasF("discounts[0][coupon]").Should().BeFalse();
        c.Form.Count(p => p.Key.StartsWith("discounts[")).Should().Be(1);
        c.HasF("allow_promotion_codes").Should().BeFalse();
    }

    // ---- rule 4: plan change keeps / removes the discount

    private static StripeFake SubscriptionFake() => new StripeFake()
        .On("GET", "/v1/subscriptions/sub_1", SubscriptionJson(price: "price_creator", quantity: 1))
        .On("POST", "/v1/subscriptions/sub_1", new { id = "sub_1" });

    [Theory]
    [InlineData(PlanChangeTiming.Immediately)]
    [InlineData(PlanChangeTiming.NextBillingPeriod)]
    public async Task Rule4_ChangePlan_Unchanged_HasNoDiscountsKeyAtAll(PlanChangeTiming timing)
    {
        StripeFake fake = SubscriptionFake();

        await Provider(fake).ChangePlanAsync("sub_1", [new BillingLineItem("price_pro", 1)], timing, PlanChangeDiscount.Unchanged, default);

        StripeCall post = fake.Calls.Single(c => c.Method == HttpMethod.Post);
        post.FKeys.Should().NotContain(k => k.StartsWith("discounts"));
    }

    [Theory]
    [InlineData(PlanChangeTiming.Immediately)]
    [InlineData(PlanChangeTiming.NextBillingPeriod)]
    public async Task Rule4_ChangePlan_Remove_SendsDiscountsAsEmptyString(PlanChangeTiming timing)
    {
        StripeFake fake = SubscriptionFake();

        await Provider(fake).ChangePlanAsync("sub_1", [new BillingLineItem("price_pro", 1)], timing, PlanChangeDiscount.Remove, default);

        StripeCall post = fake.Calls.Single(c => c.Method == HttpMethod.Post);
        post.HasF("discounts").Should().BeTrue();
        post.F("discounts").Should().Be("");
    }

    // ---- rule 5: reading the subscription back

    [Fact]
    public async Task Rule5_GetSubscription_ForeverCoupon_HasTheCouponIdAndNoEnd()
    {
        object sub = SubscriptionJson(discounts: new object[] { new { id = "di_1", source = new { type = "coupon", coupon = CouponId } } });
        StripeFake fake = new StripeFake().On("GET", "/v1/subscriptions/sub_1", sub);

        ProviderSubscriptionState s = await Provider(fake).GetSubscriptionAsync("sub_1", default);

        s.Discount.Should().NotBeNull();
        s.Discount!.Id.Should().Be(CouponId);
        s.Discount.EndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Rule5_GetSubscription_DiscountWithEnd_SetsEndsAt()
    {
        object sub = SubscriptionJson(discounts: new object[] { new { id = "di_1", source = new { type = "coupon", coupon = CouponId }, end = Unix(EndsAt) } });
        StripeFake fake = new StripeFake().On("GET", "/v1/subscriptions/sub_1", sub);

        ProviderSubscriptionState s = await Provider(fake).GetSubscriptionAsync("sub_1", default);

        s.Discount.Should().Be(new ProviderSubscriptionDiscount(CouponId, EndsAt));
    }

    [Fact]
    public async Task Rule5_GetSubscription_WithoutDiscounts_HasNullDiscount()
    {
        StripeFake fake = new StripeFake().On("GET", "/v1/subscriptions/sub_1", SubscriptionJson());

        ProviderSubscriptionState s = await Provider(fake).GetSubscriptionAsync("sub_1", default);

        s.Discount.Should().BeNull();
    }

    // ---- rule 6: preview passes Stripe's integers through

    [Theory]
    [InlineData(2900, 1450, 1450, "€29.00", "€14.50", "€14.50")]
    [InlineData(2900, 77, 3333, "€29.00", "€0.77", "€33.33")]
    public async Task Rule6_Preview_FollowsStripesIntegersExactly_NoArithmetic(long subtotal, long discount, long total, string fSub, string fDisc, string fTotal)
    {
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/prices/price_creator", new { id = "price_creator", currency = "eur" })
            .On("POST", "/v1/invoices/create_preview", new
            {
                currency = "eur",
                subtotal,
                total,
                total_discount_amounts = new[] { new { amount = discount } },
                total_taxes = Array.Empty<object>(),
                lines = new { data = new[] { new { amount = subtotal, quantity = 1, discount_amounts = new[] { new { amount = discount } } } } }
            });

        PricePreview p = await Provider(fake).PreviewPriceAsync([new BillingLineItem("price_creator", 1)], CouponId, default);

        fake.To("/v1/invoices/create_preview").Single().F("discounts[0][coupon]").Should().Be(CouponId);
        (p.SubtotalMinor, p.DiscountMinor, p.TotalMinor).Should().Be((subtotal, discount, total));
        p.RawDiscount.Should().Be(discount.ToString());
        p.Subtotal.Should().Be(fSub);
        p.Discount.Should().Be(fDisc);
        p.Total.Should().Be(fTotal);
        p.Total.Should().Be(StripeMoneyFormatter.Format(total, "EUR"), "the strings come from the formatter over the same integers");
    }
}
