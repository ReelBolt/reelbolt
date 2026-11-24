#nullable enable
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>saas-promotions T-003: <c>/api/v1/billing/admin/promotions</c>.</summary>
public sealed class PromotionsAdminControllerTests : PromotionTestBase
{
    private static JsonElement Patch(string json) => JsonDocument.Parse(json).RootElement;

    private static CreatePromotionDto PlanDiscount(Action<CreatePromotionDto>? edit = null)
    {
        CreatePromotionDto dto = new()
        {
            Kind = "planDiscount",
            Name = "Launch",
            PublicHeadline = "20% off for 3 months",
            DiscountType = "percentage",
            DiscountAmount = "20",
            Duration = "months",
            DurationMonths = 3,
            PlanKeys = ["creator"],
            MaxRedemptions = 100,
        };
        edit?.Invoke(dto);
        return dto;
    }

    [Fact]
    public async Task A_non_admin_gets_403_on_every_admin_action()
    {
        Guid id = Seed(PromotionKind.CreditCode);

        (await Admin(isAdmin: false).List(default)).Should().BeOfType<ForbidResult>();
        (await Admin(isAdmin: false).Get(id, default)).Should().BeOfType<ForbidResult>();
        (await Admin(isAdmin: false).Create(PlanDiscount(), default)).Should().BeOfType<ForbidResult>();
        (await Admin(isAdmin: false).Update(id, Patch("""{"status":"paused"}"""), default)).Should().BeOfType<ForbidResult>();
        (await Admin(isAdmin: false).Redemptions(id, default)).Should().BeOfType<ForbidResult>();
        (await Credits(isAdmin: false).SearchOrgs("acme", default)).Should().BeOfType<ForbidResult>();
        (await Credits(isAdmin: false).UserOrgs(OwnerId, default)).Should().BeOfType<ForbidResult>();
        (await Credits(isAdmin: false).ListGrants(Org, default)).Should().BeOfType<ForbidResult>();
        (await Credits(isAdmin: false).Grant(Org, new GrantCreditsDto(Guid.NewGuid(), 10, Now.AddDays(30), "x"), default)).Should().BeOfType<ForbidResult>();
        Provider.DiscountsCreated.Should().BeEmpty();
    }

    [Fact]
    public async Task Billing_disabled_is_404_on_every_admin_route()
    {
        Guid id = Seed(PromotionKind.CreditCode);

        (await Admin(enabled: false).List(default)).Should().BeOfType<NotFoundResult>();
        (await Admin(enabled: false).Get(id, default)).Should().BeOfType<NotFoundResult>();
        (await Admin(enabled: false).Create(PlanDiscount(), default)).Should().BeOfType<NotFoundResult>();
        (await Admin(enabled: false).Update(id, Patch("""{"status":"paused"}"""), default)).Should().BeOfType<NotFoundResult>();
        (await Admin(enabled: false).Redemptions(id, default)).Should().BeOfType<NotFoundResult>();
        (await Credits(enabled: false).SearchOrgs(null, default)).Should().BeOfType<NotFoundResult>();
        (await Credits(enabled: false).UserOrgs(OwnerId, default)).Should().BeOfType<NotFoundResult>();
        (await Credits(enabled: false).ListGrants(Org, default)).Should().BeOfType<NotFoundResult>();
        (await Credits(enabled: false).Grant(Org, new GrantCreditsDto(Guid.NewGuid(), 10, Now.AddDays(30), "x"), default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Creating_a_plan_discount_sends_the_mapped_spec_to_the_provider()
    {
        IActionResult result = await Admin().Create(PlanDiscount(d => d.EndsAt = Now.AddDays(10)), default);

        StatusOf(result).Should().Be(201);
        ProviderDiscountSpec spec = Provider.DiscountsCreated.Should().ContainSingle().Subject;
        spec.Type.Should().Be(PromotionDiscountType.Percentage);
        spec.Amount.Should().Be("20");
        spec.Recur.Should().BeTrue();
        spec.MaximumRecurringIntervals.Should().Be(3);
        spec.UsageLimit.Should().Be(100);
        spec.RestrictToPriceIds.Should().Equal("pri_creator");
        spec.ExpiresAt.Should().BeCloseTo(Now.AddDays(10), TimeSpan.FromSeconds(1));

        JsonElement dto = JsonOf(result);
        dto.GetProperty("kind").GetString().Should().Be("planDiscount");
        dto.GetProperty("status").GetString().Should().Be("active");
        dto.GetProperty("discountType").GetString().Should().Be("percentage");
        dto.GetProperty("durationKind").GetString().Should().Be("months");
        dto.GetProperty("providerDiscountId").GetString().Should().Be("dsc_1");
        dto.GetProperty("targetPlanKeys").EnumerateArray().Select(e => e.GetString()).Should().Equal("creator");
        dto.GetProperty("targetPriceIds").EnumerateArray().Select(e => e.GetString()).Should().Equal("pri_creator");
        dto.GetProperty("targetPackCredits").GetArrayLength().Should().Be(0);
        dto.GetProperty("redemptions").GetInt32().Should().Be(0);
        dto.TryGetProperty("targetPlanKeysJson", out _).Should().BeFalse();
        spec.PromotionId.Should().Be(dto.GetProperty("id").GetGuid());

        await using InferenceApiDbContext db = NewDb();
        (await db.Promotions.SingleAsync()).ProviderDiscountId.Should().Be("dsc_1");
    }

    [Fact]
    public void Founder_price_preset_is_served_to_platform_admins_only()
    {
        Admin(isAdmin: false).Presets().Should().BeOfType<ForbidResult>();
        Admin(enabled: false).Presets().Should().BeOfType<NotFoundResult>();

        JsonElement founder = JsonOf(Admin().Presets()).GetProperty("founderPrice");
        founder.GetProperty("name").GetString().Should().Be("Founder price");
        founder.GetProperty("publicHeadline").GetString().Should().Be("Founder price: 50% off");
        founder.GetProperty("duration").GetString().Should().Be("forever");
        founder.GetProperty("discountAmount").GetString().Should().Be("50");
        founder.GetProperty("endsAt").GetDateTime().Should().Be(new DateTime(2027, 1, 31, 22, 59, 0, DateTimeKind.Utc));
        founder.GetProperty("code").ValueKind.Should().Be(JsonValueKind.Null);
        founder.GetProperty("planKeys").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Founder_price_preset_is_accepted_and_maps_to_a_forever_all_plans_provider_discount()
    {
        IActionResult result = await Admin().Create(PromotionPresets.FounderPrice, default);

        StatusOf(result).Should().Be(201);
        ProviderDiscountSpec spec = Provider.DiscountsCreated.Should().ContainSingle().Subject;
        spec.Type.Should().Be(PromotionDiscountType.Percentage);
        spec.Amount.Should().Be("50");
        spec.Recur.Should().BeTrue();
        spec.MaximumRecurringIntervals.Should().BeNull();
        spec.ExpiresAt.Should().Be(PromotionPresets.FounderPriceEndsAt);
        spec.RestrictToPriceIds.Should().BeEquivalentTo("pri_creator", "pri_team");

        JsonElement dto = JsonOf(result);
        dto.GetProperty("durationKind").GetString().Should().Be("forever");
        dto.GetProperty("code").ValueKind.Should().Be(JsonValueKind.Null);
        dto.GetProperty("targetPriceIds").GetArrayLength().Should().Be(2);
    }

    [Theory]
    [InlineData("once", false, null)]
    [InlineData("forever", true, null)]
    public async Task Duration_maps_to_recur_and_max_intervals(string duration, bool recur, int? max)
    {
        StatusOf(await Admin().Create(PlanDiscount(d => { d.Duration = duration; d.DurationMonths = null; d.PlanKeys = null; }), default)).Should().Be(201);

        ProviderDiscountSpec spec = Provider.DiscountsCreated.Single();
        spec.Recur.Should().Be(recur);
        spec.MaximumRecurringIntervals.Should().Be(max);
        spec.RestrictToPriceIds.Should().BeEquivalentTo("pri_creator", "pri_team"); // empty target = every sellable plan
    }

    [Fact]
    public async Task A_pack_discount_is_always_once_and_targets_pack_prices()
    {
        IActionResult result = await Admin().Create(new CreatePromotionDto
        {
            Kind = "PackDiscount",
            Name = "Packs",
            DiscountType = "FLAT",
            DiscountAmount = "500",
            CurrencyCode = "USD",
            PackCredits = [1000],
        }, default);

        StatusOf(result).Should().Be(201);
        ProviderDiscountSpec spec = Provider.DiscountsCreated.Single();
        spec.Recur.Should().BeFalse();
        spec.Type.Should().Be(PromotionDiscountType.Flat);
        spec.CurrencyCode.Should().Be("USD");
        spec.RestrictToPriceIds.Should().Equal("pri_topup_1000");
        JsonOf(result).GetProperty("durationKind").GetString().Should().Be("once");
    }

    [Fact]
    public async Task Credit_codes_and_pack_bonuses_make_no_provider_call()
    {
        StatusOf(await Admin().Create(new CreatePromotionDto { Kind = "creditCode", Name = "Free", Code = "free100", Credits = 100 }, default)).Should().Be(201);
        StatusOf(await Admin().Create(new CreatePromotionDto { Kind = "packBonus", Name = "Bonus", BonusPercent = 25 }, default)).Should().Be(201);

        Provider.DiscountsCreated.Should().BeEmpty();
        await using InferenceApiDbContext db = NewDb();
        Promotion code = await db.Promotions.SingleAsync(p => p.Kind == PromotionKind.CreditCode);
        code.Code.Should().Be("FREE100");
        code.CreditsMillis.Should().Be(100_000);
        code.CreditExpiryDays.Should().Be(90);
        Promotion bonus = await db.Promotions.SingleAsync(p => p.Kind == PromotionKind.PackBonus);
        bonus.TargetPriceIdsJson.Should().Be("""["pri_topup_500","pri_topup_1000"]""");
    }

    [Fact]
    public async Task A_provider_4xx_is_502_and_no_row()
    {
        Provider.DiscountFailure = new HttpRequestException("Paddle 400: bad", null, HttpStatusCode.BadRequest);

        IActionResult result = await Admin().Create(PlanDiscount(), default);

        StatusOf(result).Should().Be(502);
        ErrorCode(result).Should().Be("billing_provider_error");
        await using InferenceApiDbContext db = NewDb();
        (await db.Promotions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_overlapping_automatic_promotion_is_409()
    {
        StatusOf(await Admin().Create(PlanDiscount(), default)).Should().Be(201);

        IActionResult overlap = await Admin().Create(PlanDiscount(d => d.PlanKeys = null), default);
        StatusOf(overlap).Should().Be(409);
        ErrorCode(overlap).Should().Be("automatic_promotion_overlap");
        Provider.DiscountsCreated.Should().HaveCount(1, "the overlap is refused before Paddle is called");

        // Disjoint targets, or a code, do not overlap.
        StatusOf(await Admin().Create(PlanDiscount(d => d.PlanKeys = ["team"]), default)).Should().Be(201);
        StatusOf(await Admin().Create(PlanDiscount(d => d.Code = "LAUNCH20"), default)).Should().Be(201);
    }

    [Fact]
    public async Task Reactivating_into_an_overlap_is_409()
    {
        Guid paused = Seed(PromotionKind.PlanDiscount, p => p.Status = PromotionStatus.Paused);
        Seed(PromotionKind.PlanDiscount, p => p.TargetPriceIdsJson = """["pri_team"]""");

        IActionResult result = await Admin().Update(paused, Patch("""{"status":"active"}"""), default);

        StatusOf(result).Should().Be(409);
        ErrorCode(result).Should().Be("automatic_promotion_overlap");
        Provider.DiscountUpdates.Should().BeEmpty();
    }

    [Fact]
    public async Task A_duplicate_code_is_409_promo_code_taken()
    {
        Seed(PromotionKind.CreditCode);

        IActionResult result = await Admin().Create(new CreatePromotionDto { Kind = "creditCode", Name = "Dup", Code = "welcome50", Credits = 5 }, default);

        StatusOf(result).Should().Be(409);
        ErrorCode(result).Should().Be("promo_code_taken");
    }

    [Fact]
    public async Task Invalid_create_bodies_are_400_with_message_and_errors()
    {
        IActionResult result = await Admin().Create(new CreatePromotionDto { Kind = "creditCode", Name = "", Code = "x!", Credits = 0 }, default);

        StatusOf(result).Should().Be(400);
        JsonElement body = JsonOf(result);
        body.GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        body.GetProperty("errors").GetArrayLength().Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task A_target_with_no_price_id_is_400_target_not_purchasable()
    {
        IActionResult result = await Admin().Create(PlanDiscount(d => d.PlanKeys = ["pro"]), default);

        StatusOf(result).Should().Be(400);
        ErrorCode(result).Should().Be("target_not_purchasable");
        Provider.DiscountsCreated.Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_to_paused_calls_the_provider_with_inactive_then_saves()
    {
        Guid id = Seed(PromotionKind.PlanDiscount);
        DateTime ends = Now.AddDays(5);

        IActionResult result = await Admin().Update(id, Patch($$"""{"status":"paused","name":"Renamed","endsAt":"{{ends:O}}"}"""), default);

        StatusOf(result).Should().Be(200);
        var update = Provider.DiscountUpdates.Should().ContainSingle().Subject;
        update.DiscountId.Should().Be("dsc_plan");
        update.Active.Should().BeFalse();
        update.ExpiresAt.Should().BeCloseTo(ends, TimeSpan.FromSeconds(1));
        JsonOf(result).GetProperty("status").GetString().Should().Be("paused");
        await using InferenceApiDbContext db = NewDb();
        Promotion saved = await db.Promotions.SingleAsync();
        saved.Status.Should().Be(PromotionStatus.Paused);
        saved.Name.Should().Be("Renamed");
    }

    [Fact]
    public async Task Patch_saves_nothing_when_the_provider_refuses()
    {
        Guid id = Seed(PromotionKind.PlanDiscount);
        Provider.DiscountFailure = new HttpRequestException("Paddle 400", null, HttpStatusCode.BadRequest);

        StatusOf(await Admin().Update(id, Patch("""{"status":"archived"}"""), default)).Should().Be(502);

        await using InferenceApiDbContext db = NewDb();
        (await db.Promotions.SingleAsync()).Status.Should().Be(PromotionStatus.Active);
    }

    [Fact]
    public async Task Patch_of_a_credit_code_makes_no_provider_call()
    {
        Guid id = Seed(PromotionKind.CreditCode);

        StatusOf(await Admin().Update(id, Patch("""{"status":"paused","clearEndsAt":true}"""), default)).Should().Be(200);

        Provider.DiscountUpdates.Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"discountAmount":"50"}""")]
    [InlineData("""{"code":"NEWCODE1"}""")]
    [InlineData("""{"name":"ok","kind":"packBonus"}""")]
    [InlineData("""{"status":"deleted"}""")]
    public async Task Patch_of_a_value_field_is_400(string json)
    {
        Guid id = Seed(PromotionKind.PlanDiscount);

        IActionResult result = await Admin().Update(id, Patch(json), default);

        StatusOf(result).Should().Be(400);
        Provider.DiscountUpdates.Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_of_an_unknown_promotion_is_404()
        => StatusOf(await Admin().Update(Guid.NewGuid(), Patch("""{"name":"x"}"""), default)).Should().Be(404);

    [Fact]
    public async Task Redemptions_list_newest_first_with_org_names()
    {
        Guid id = Seed(PromotionKind.PlanDiscount);
        await using (InferenceApiDbContext db = NewDb())
        {
            db.PromotionRedemptions.AddRange(
                new PromotionRedemption { Id = Guid.NewGuid(), PromotionId = id, OrganizationId = Org, Kind = PromotionRedemptionKind.DiscountApplied, ProviderTransactionId = "txn_old", DiscountTotal = "580", CurrencyCode = "USD", CreatedAt = Now.AddDays(-2) },
                new PromotionRedemption { Id = Guid.NewGuid(), PromotionId = id, OrganizationId = Org, Kind = PromotionRedemptionKind.DiscountApplied, ProviderTransactionId = "txn_new", DiscountTotal = "580", CurrencyCode = "USD", CreatedAt = Now });
            await db.SaveChangesAsync();
        }

        IActionResult result = await Admin().Redemptions(id, default);

        var items = JsonOf(result).GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("providerTransactionId").GetString()).Should().Equal("txn_new", "txn_old");
        items[0].GetProperty("organizationName").GetString().Should().Be("Acme Studio");
        items[0].GetProperty("kind").GetString().Should().Be("discountApplied");
        items[0].GetProperty("discountTotal").GetString().Should().Be("580");

        JsonElement list = JsonOf(await Admin().List(default));
        list.GetProperty("items")[0].GetProperty("redemptions").GetInt32().Should().Be(2);
        JsonOf(await Admin().Get(id, default)).GetProperty("redemptions").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Redemptions_show_when_a_plan_change_removed_the_discount()
    {
        Guid id = Seed(PromotionKind.PlanDiscount);
        DateTime removedAt = new(2026, 10, 13, 9, 30, 0, DateTimeKind.Utc);
        await using (InferenceApiDbContext db = NewDb())
        {
            db.PromotionRedemptions.AddRange(
                new PromotionRedemption { Id = Guid.NewGuid(), PromotionId = id, OrganizationId = Org, Kind = PromotionRedemptionKind.DiscountApplied, ProviderTransactionId = "txn_kept", ProviderSubscriptionId = "sub_kept", CreatedAt = Now.AddDays(-2) },
                new PromotionRedemption { Id = Guid.NewGuid(), PromotionId = id, OrganizationId = Org, Kind = PromotionRedemptionKind.DiscountApplied, ProviderTransactionId = "txn_removed", ProviderSubscriptionId = "sub_removed", CreatedAt = Now, DiscountRemovedAt = removedAt, DiscountRemovedForPriceId = "pri_team" });
            await db.SaveChangesAsync();
        }

        var items = JsonOf(await Admin().Redemptions(id, default)).GetProperty("items").EnumerateArray().ToList();

        items[0].GetProperty("discountRemovedAt").GetDateTime().Should().Be(removedAt);
        items[1].GetProperty("discountRemovedAt").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
