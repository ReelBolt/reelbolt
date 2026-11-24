#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>saas-promotions T-003: <c>POST /api/v1/billing/promotions/redeem</c> and the code-guessing limiter.</summary>
public sealed class PromotionRedeemTests : PromotionTestBase
{
    private Task<IActionResult> Redeem(string code, Guid? user = null, OrganizationRole role = OrganizationRole.Owner)
        => Promotions(user, role).Redeem(new RedeemPromotionDto(code), default);

    [Fact]
    public async Task Redeem_grants_promo_credits_with_the_expiry()
    {
        Guid id = Seed(PromotionKind.CreditCode);

        IActionResult result = await Redeem(" welcome50 ");

        StatusOf(result).Should().Be(200);
        var body = JsonOf(result);
        body.GetProperty("credits").GetDouble().Should().Be(50);
        body.GetProperty("headline").GetString().Should().Be("CreditCode headline");
        DateTime expires = body.GetProperty("expiresAt").GetDateTime().ToUniversalTime();
        expires.Should().BeCloseTo(Now.AddDays(30), TimeSpan.FromSeconds(1));

        await using InferenceApiDbContext db = NewDb();
        CreditGrant grant = await db.CreditGrants.SingleAsync(g => g.OrganizationId == Org);
        grant.Source.Should().Be(CreditGrantSource.Promo);
        grant.AmountMillis.Should().Be(50_000);
        grant.ExpiresAt!.Value.Should().BeCloseTo(Now.AddDays(30), TimeSpan.FromSeconds(1));
        PromotionRedemption row = await db.PromotionRedemptions.SingleAsync();
        row.Kind.Should().Be(PromotionRedemptionKind.CreditCodeRedeemed);
        row.PromotionId.Should().Be(id);
        row.OrganizationId.Should().Be(Org);
        row.UserId.Should().Be(OwnerId);
        row.CreditsMillis.Should().Be(50_000);
        row.LedgerEntryId.Should().NotBeNull();
        (await db.Promotions.SingleAsync(p => p.Id == id)).RedemptionCount.Should().Be(1);
    }

    [Fact]
    public async Task Redeem_requires_owner_or_billing_role()
    {
        Seed(PromotionKind.CreditCode);

        (await Redeem("WELCOME50", role: OrganizationRole.Member)).Should().BeOfType<ForbidResult>();
        (await Redeem("WELCOME50", role: OrganizationRole.Billing)).Should().BeOfType<OkObjectResult>();
    }

    [Fact]
    public async Task A_second_redeem_by_the_same_org_is_409()
    {
        Seed(PromotionKind.CreditCode);

        (await Redeem("WELCOME50")).Should().BeOfType<OkObjectResult>();
        IActionResult second = await Redeem("WELCOME50");

        StatusOf(second).Should().Be(409);
        ErrorCode(second).Should().Be("promo_already_redeemed");
        (await BalanceMillisAsync()).Should().Be(50_000);
    }

    [Fact]
    public async Task Global_max_reached_is_the_uniform_promo_code_invalid()
    {
        Guid id = Seed(PromotionKind.CreditCode, p => p.MaxRedemptions = 1);
        await using (InferenceApiDbContext db = NewDb())
        {
            await db.Promotions.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.RedemptionCount, 1));
        }

        IActionResult result = await Redeem("WELCOME50");

        // T-011 S-13: an exhausted code is indistinguishable from an unknown one.
        StatusOf(result).Should().Be(400);
        ErrorCode(result).Should().Be("promo_code_invalid");
        (await BalanceMillisAsync()).Should().Be(0);
        await using InferenceApiDbContext check = NewDb();
        (await check.Promotions.SingleAsync(p => p.Id == id)).RedemptionCount.Should().Be(1);
        (await check.PromotionRedemptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_ledger_replay_rolls_back_and_answers_already_redeemed()
    {
        // The race a concurrent twin loses: its per-org check read zero, but the winner's grant (same
        // idempotency key) has landed by the time it reaches the ledger.
        Guid id = Seed(PromotionKind.CreditCode);
        await using (InferenceApiDbContext db = NewDb())
        {
            await new CreditLedger(db).GrantAsync(Org, CreditGrantSource.Promo, 50_000, Now.AddDays(30), $"promo:{id}:{Org}:1");
        }

        IActionResult result = await Redeem("WELCOME50");

        StatusOf(result).Should().Be(409);
        ErrorCode(result).Should().Be("promo_already_redeemed");
        await using InferenceApiDbContext check = NewDb();
        (await check.CreditGrants.CountAsync()).Should().Be(1);
        (await check.Promotions.SingleAsync(p => p.Id == id)).RedemptionCount.Should().Be(0, "the increment rolls back with the replay");
        (await check.PromotionRedemptions.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("NOSUCHCODE")]
    [InlineData("PAUSED01")]
    [InlineData("EXPIRED01")]
    [InlineData("LATER001")]
    [InlineData("PLANCODE1")]
    [InlineData("bad code!")]
    public async Task Invalid_codes_return_a_byte_identical_promo_code_invalid_body(string code)
    {
        Seed(PromotionKind.CreditCode, p => { p.Code = "PAUSED01"; p.Status = PromotionStatus.Paused; });
        Seed(PromotionKind.CreditCode, p => { p.Code = "EXPIRED01"; p.EndsAt = Now.AddMinutes(-1); });
        Seed(PromotionKind.CreditCode, p => { p.Code = "LATER001"; p.StartsAt = Now.AddDays(1); });
        Seed(PromotionKind.PlanDiscount, p => p.Code = "PLANCODE1");
        // The reference body is a plainly unknown code; every other reason must be indistinguishable from it.
        string reference = Body(await Promotions(VerifiedUser()).Redeem(new RedeemPromotionDto("ZZZZZZZZ"), default));

        IActionResult result = await Promotions(VerifiedUser()).Redeem(new RedeemPromotionDto(code), default);

        StatusOf(result).Should().Be(400);
        Body(result).Should().Be(reference);
        ErrorCode(result).Should().Be("promo_code_invalid");
        (await BalanceMillisAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_eleventh_failure_in_an_hour_is_429()
    {
        Seed(PromotionKind.CreditCode);
        for (int i = 0; i < 10; i++)
        {
            StatusOf(await Redeem($"WRONG{i:000}")).Should().Be(400);
        }

        IActionResult eleventh = await Redeem("WELCOME50");

        StatusOf(eleventh).Should().Be(429);
        ErrorCode(eleventh).Should().Be("promo_rate_limited");
        (await BalanceMillisAsync()).Should().Be(0, "a valid code is refused too once the limit is hit");

        // A user of another org is unaffected (T-011 S-09: the org is limited as a whole), and the window slides.
        PromotionsController elsewhere = WithHttp(new PromotionsController(
            new PromoUser(VerifiedUser(), Guid.NewGuid(), OrganizationRole.Owner, false), Billing(true), Service(NewDb()), NoPlanPrices));
        StatusOf(await elsewhere.Redeem(new RedeemPromotionDto("WRONG999"), default)).Should().Be(400);
        Clock.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        StatusOf(await Redeem("WELCOME50")).Should().Be(200);
    }

    [Fact]
    public async Task Billing_disabled_is_404()
    {
        Seed(PromotionKind.CreditCode);

        (await Promotions(enabled: false).Redeem(new RedeemPromotionDto("WELCOME50"), default)).Should().BeOfType<NotFoundResult>();
        (await Anonymous(enabled: false).Active(default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Active_lists_only_automatic_in_window_promotions_without_codes_or_ids()
    {
        Seed(PromotionKind.PlanDiscount, p => { p.PublicHeadline = "Launch sale"; p.TargetPlanKeysJson = """["creator"]"""; p.DurationKind = PromotionDuration.Months; p.DurationMonths = 3; });
        Seed(PromotionKind.PackBonus, p => { p.PublicHeadline = "Bonus"; p.TargetPackCreditsJson = "[500]"; });
        Seed(PromotionKind.PackDiscount, p => { p.Code = "PACKCODE"; });
        Seed(PromotionKind.PackDiscount, p => p.Status = PromotionStatus.Paused);
        Seed(PromotionKind.PackDiscount, p => p.StartsAt = Now.AddDays(2));
        Seed(PromotionKind.CreditCode);

        IActionResult result = await Anonymous().Active(default);

        string body = Body(result);
        body.Should().NotContain("PACKCODE").And.NotContain("WELCOME50").And.NotContain("dsc_").And.NotContain("\"id\"");
        var items = JsonOf(result).GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("kind").GetString()).Should().BeEquivalentTo("planDiscount", "packBonus");
        var plan = items.Single(i => i.GetProperty("kind").GetString() == "planDiscount");
        plan.GetProperty("headline").GetString().Should().Be("Launch sale");
        plan.GetProperty("discountType").GetString().Should().Be("percentage");
        plan.GetProperty("percentOff").GetDouble().Should().Be(20);
        plan.GetProperty("planKeys").EnumerateArray().Select(k => k.GetString()).Should().Equal("creator");
        plan.GetProperty("durationKind").GetString().Should().Be("months");
        plan.GetProperty("durationMonths").GetInt32().Should().Be(3);
        var bonus = items.Single(i => i.GetProperty("kind").GetString() == "packBonus");
        bonus.GetProperty("bonusPercent").GetInt32().Should().Be(20);
        bonus.GetProperty("packCredits").EnumerateArray().Select(k => k.GetInt32()).Should().Equal(500);
    }
}

/// <summary>Two simultaneous redeems by one org, on separate connections to one database file.</summary>
public sealed class PromotionRedeemConcurrencyTests() : PromotionTestBase(onDisk: true)
{
    [Fact]
    public async Task Two_concurrent_redeems_produce_exactly_one_ledger_grant()
    {
        Guid id = Seed(PromotionKind.CreditCode);

        IActionResult[] results = await Task.WhenAll(
            Task.Run(() => Promotions().Redeem(new RedeemPromotionDto("WELCOME50"), default)),
            Task.Run(() => Promotions().Redeem(new RedeemPromotionDto("WELCOME50"), default)));

        results.Count(r => StatusOf(r) == 200).Should().Be(1);
        results.Where(r => StatusOf(r) != 200).Select(ErrorCode).Should().Equal("promo_already_redeemed");
        await using InferenceApiDbContext db = NewDb();
        (await db.CreditGrants.CountAsync(g => g.OrganizationId == Org)).Should().Be(1);
        (await db.CreditLedgerEntries.CountAsync(e => e.IdempotencyKey!.StartsWith("promo:"))).Should().Be(1);
        (await db.PromotionRedemptions.CountAsync()).Should().Be(1);
        (await db.Promotions.SingleAsync(p => p.Id == id)).RedemptionCount.Should().Be(1);
    }
}
