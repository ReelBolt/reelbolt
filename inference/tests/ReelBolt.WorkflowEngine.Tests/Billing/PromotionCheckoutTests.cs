#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>saas-promotions T-003: promo codes and automatic promotions on checkout, preview and the summary.</summary>
public sealed class PromotionCheckoutTests : PromotionTestBase
{
    [Fact]
    public async Task A_valid_code_passes_the_discount_id()
    {
        Guid id = Seed(PromotionKind.PlanDiscount, p => p.Code = "LAUNCH20");

        IActionResult result = await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null, "launch20"), default);

        StatusOf(result).Should().Be(200);
        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");
        Provider.LastCheckout.PromotionId.Should().Be(id);
        Provider.LastCheckout.BonusPromotionId.Should().BeNull();
        JsonOf(result).GetProperty("discount").GetProperty("headline").GetString().Should().Be("PlanDiscount headline");
    }

    [Fact]
    public async Task An_automatic_discount_applies_with_no_code()
    {
        Guid id = Seed(PromotionKind.PlanDiscount);

        IActionResult result = await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null), default);

        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");
        Provider.LastCheckout.PromotionId.Should().Be(id);
        JsonOf(result).GetProperty("discount").ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task A_founder_price_closes_to_new_checkouts_at_endsAt()
    {
        Seed(PromotionKind.PlanDiscount, p =>
        {
            p.Name = "Founder price";
            p.DurationKind = PromotionDuration.Forever;
            p.EndsAt = Now.AddDays(1);
        });

        await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null), default);
        Provider.LastCheckout!.DiscountId.Should().Be("dsc_plan");

        // Someone who cancelled and comes back after endsAt is a NEW checkout: no founder price.
        Clock.Advance(TimeSpan.FromDays(2));
        await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null), default);
        Provider.LastCheckout!.DiscountId.Should().BeNull();
    }

    [Fact]
    public async Task No_promotion_means_a_null_discount()
    {
        IActionResult result = await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null), default);

        Provider.LastCheckout!.DiscountId.Should().BeNull();
        JsonOf(result).GetProperty("discount").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_automatic_discount_not_targeting_the_plan_is_not_applied()
    {
        Seed(PromotionKind.PlanDiscount, p => p.TargetPriceIdsJson = """["pri_team"]""");

        await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null), default);

        Provider.LastCheckout!.DiscountId.Should().BeNull();
    }

    [Theory]
    [InlineData("NOPE1234")]
    [InlineData("PAUSED20")]
    public async Task An_invalid_code_is_400_with_zero_provider_calls(string code)
    {
        Seed(PromotionKind.PlanDiscount, p => { p.Code = "PAUSED20"; p.Status = PromotionStatus.Paused; });

        IActionResult result = await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null, code), default);

        StatusOf(result).Should().Be(400);
        ErrorCode(result).Should().Be("promo_code_invalid");
        Provider.LastCheckout.Should().BeNull();
        Provider.PreviewCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_code_for_a_pack_used_on_a_plan_is_400()
    {
        Seed(PromotionKind.PackDiscount, p => p.Code = "PACK10");

        IActionResult result = await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null, "PACK10"), default);

        StatusOf(result).Should().Be(400);
        ErrorCode(result).Should().Be("promo_code_invalid");
        Provider.LastCheckout.Should().BeNull();
    }

    [Fact]
    public async Task A_code_already_used_by_the_org_up_to_its_limit_is_invalid()
    {
        Guid id = Seed(PromotionKind.PlanDiscount, p => p.Code = "ONCEONLY");
        await using (InferenceApiDbContext db = NewDb())
        {
            db.PromotionRedemptions.Add(new PromotionRedemption { Id = Guid.NewGuid(), PromotionId = id, OrganizationId = Org, Kind = PromotionRedemptionKind.DiscountApplied, ProviderTransactionId = "txn_x" });
            await db.SaveChangesAsync();
        }

        StatusOf(await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null, "ONCEONLY"), default)).Should().Be(400);
    }

    [Fact]
    public async Task A_top_up_with_an_automatic_bonus_passes_the_bonus_promotion_id()
    {
        await SubscribeAsync();
        Guid bonus = Seed(PromotionKind.PackBonus);

        IActionResult result = await BillingApi().Checkout(new CheckoutRequestDto(null, null, 500), default);

        StatusOf(result).Should().Be(200);
        Provider.LastCheckout!.TopUpCredits.Should().Be(500);
        Provider.LastCheckout.BonusPromotionId.Should().Be(bonus);
        Provider.LastCheckout.DiscountId.Should().BeNull();
    }

    [Fact]
    public async Task A_bonus_code_on_a_pack_resolves_the_bonus_and_keeps_the_automatic_discount()
    {
        await SubscribeAsync();
        Guid bonus = Seed(PromotionKind.PackBonus, p => p.Code = "BONUS30");
        Guid discount = Seed(PromotionKind.PackDiscount);

        await BillingApi().Checkout(new CheckoutRequestDto(null, null, 1000, "BONUS30"), default);

        Provider.LastCheckout!.BonusPromotionId.Should().Be(bonus);
        Provider.LastCheckout.PromotionId.Should().Be(discount);
        Provider.LastCheckout.DiscountId.Should().Be("dsc_pack");
    }

    [Fact]
    public async Task Preview_returns_the_provider_strings_verbatim()
    {
        Seed(PromotionKind.PlanDiscount, p => p.Code = "LAUNCH20");

        IActionResult result = await BillingApi().Preview(new CheckoutPreviewRequestDto("creator", null, null, "LAUNCH20"), default);

        StatusOf(result).Should().Be(200);
        JsonElement body = JsonOf(result);
        body.GetProperty("currencyCode").GetString().Should().Be("USD");
        body.GetProperty("subtotal").GetString().Should().Be("$20.00");
        body.GetProperty("discount").GetString().Should().Be("-$4.00");
        body.GetProperty("total").GetString().Should().Be("$16.00");
        body.GetProperty("promotion").GetProperty("kind").GetString().Should().Be("planDiscount");
        body.GetProperty("promotion").GetProperty("headline").GetString().Should().Be("PlanDiscount headline");
        body.GetProperty("bonusCredits").ValueKind.Should().Be(JsonValueKind.Null);
        Provider.LastPreviewDiscountId.Should().Be("dsc_plan");
    }

    [Fact]
    public async Task Preview_reports_bonus_credits_for_a_pack()
    {
        Seed(PromotionKind.PackBonus);

        JsonElement body = JsonOf(await BillingApi().Preview(new CheckoutPreviewRequestDto(null, null, 500, null), default));

        body.GetProperty("bonusCredits").GetInt32().Should().Be(100);
        body.GetProperty("promotion").ValueKind.Should().Be(JsonValueKind.Null);
        Provider.LastPreviewDiscountId.Should().BeNull();
    }

    [Fact]
    public async Task Preview_with_an_invalid_code_is_400_without_calling_the_provider()
    {
        IActionResult result = await BillingApi().Preview(new CheckoutPreviewRequestDto("creator", null, null, "NOPE1234"), default);

        StatusOf(result).Should().Be(400);
        ErrorCode(result).Should().Be("promo_code_invalid");
        Provider.PreviewCalls.Should().Be(0);
    }

    [Fact]
    public async Task Preview_and_checkout_failures_count_towards_the_limiter()
    {
        for (int i = 0; i < 10; i++)
        {
            await BillingApi().Preview(new CheckoutPreviewRequestDto("creator", null, null, $"NOPE{i:0000}"), default);
        }

        IActionResult result = await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null, "NOPE9999"), default);

        StatusOf(result).Should().Be(429);
        ErrorCode(result).Should().Be("promo_rate_limited");
    }

    [Fact]
    public async Task Preview_requires_owner_or_billing_and_billing_enabled()
    {
        (await BillingApi(OrganizationRole.Member).Preview(new CheckoutPreviewRequestDto("creator", null, null, null), default)).Should().BeOfType<ForbidResult>();
        (await BillingApi(enabled: false).Preview(new CheckoutPreviewRequestDto("creator", null, null, null), default)).Should().BeOfType<NotFoundResult>();
        (await BillingApi(enabled: false).Checkout(new CheckoutRequestDto("creator", null, null, "LAUNCH20"), default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Summary_lists_the_promo_grant_with_its_expiry()
    {
        await using (InferenceApiDbContext db = NewDb())
        {
            await new CreditLedger(db).GrantAsync(Org, CreditGrantSource.Promo, 50_000, Now.AddDays(30), "promo:test");
            await new CreditLedger(db).GrantAsync(Org, CreditGrantSource.TopUp, 500_000, Now.AddDays(300), "topup:test");
            await new CreditLedger(db).GrantAsync(Org, CreditGrantSource.Promo, 1_000, Now.AddDays(-1), "promo:expired");
        }

        JsonElement credits = JsonOf(await BillingApi().Summary(default)).GetProperty("credits");

        var grants = credits.GetProperty("grants").EnumerateArray().ToList();
        grants.Select(g => g.GetProperty("source").GetString()).Should().Equal("promo", "topUp");
        grants[0].GetProperty("remaining").GetDouble().Should().Be(50);
        grants[0].GetProperty("expiresAt").GetDateTime().ToUniversalTime().Should().BeCloseTo(Now.AddDays(30), TimeSpan.FromSeconds(1));
    }
}
