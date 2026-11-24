#nullable enable
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>saas-promotions T-003: admin org search and manual Promo credit grants.</summary>
public sealed class OrgCreditsAdminControllerTests : PromotionTestBase
{
    [Fact]
    public async Task A_grant_writes_a_promo_ledger_grant_and_a_row()
    {
        Guid request = Guid.NewGuid();
        DateTime expires = Now.AddDays(60);

        IActionResult result = await Credits().Grant(Org, new GrantCreditsDto(request, 250, expires, "  goodwill  "), default);

        StatusOf(result).Should().Be(201);
        JsonElement body = JsonOf(result);
        body.GetProperty("credits").GetDouble().Should().Be(250);
        body.GetProperty("reason").GetString().Should().Be("goodwill");
        body.GetProperty("grantedByUserId").GetGuid().Should().Be(AdminId);
        await using InferenceApiDbContext db = NewDb();
        CreditGrant grant = await db.CreditGrants.SingleAsync();
        grant.Source.Should().Be(CreditGrantSource.Promo);
        grant.AmountMillis.Should().Be(250_000);
        grant.ExpiresAt!.Value.Should().BeCloseTo(expires, TimeSpan.FromSeconds(1));
        AdminCreditGrant row = await db.AdminCreditGrants.SingleAsync();
        row.RequestId.Should().Be(request);
        (await db.CreditLedgerEntries.SingleAsync(e => e.Id == row.LedgerEntryId)).IdempotencyKey.Should().Be($"admin-grant:{request}");
    }

    [Fact]
    public async Task A_replayed_request_id_returns_the_same_row_and_one_ledger_entry()
    {
        Guid request = Guid.NewGuid();
        GrantCreditsDto dto = new(request, 100, Now.AddDays(30), "support");

        IActionResult first = await Credits().Grant(Org, dto, default);
        IActionResult second = await Credits().Grant(Org, dto, default);

        StatusOf(first).Should().Be(201);
        StatusOf(second).Should().Be(200);
        JsonOf(second).GetProperty("id").GetGuid().Should().Be(JsonOf(first).GetProperty("id").GetGuid());
        await using InferenceApiDbContext db = NewDb();
        (await db.AdminCreditGrants.CountAsync()).Should().Be(1);
        (await db.CreditLedgerEntries.CountAsync()).Should().Be(1);
        (await BalanceMillisAsync()).Should().Be(100_000);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task A_missing_reason_is_400(string? reason)
    {
        IActionResult result = await Credits().Grant(Org, new GrantCreditsDto(Guid.NewGuid(), 100, Now.AddDays(30), reason), default);

        StatusOf(result).Should().Be(400);
        (await BalanceMillisAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_expiry_in_the_past_is_400()
    {
        StatusOf(await Credits().Grant(Org, new GrantCreditsDto(Guid.NewGuid(), 100, Now.AddDays(-1), "x"), default)).Should().Be(400);
        StatusOf(await Credits().Grant(Org, new GrantCreditsDto(Guid.NewGuid(), 100, Now.AddHours(12), "x"), default)).Should().Be(400);
        StatusOf(await Credits().Grant(Org, new GrantCreditsDto(Guid.NewGuid(), 100, Now.AddYears(3).AddDays(1), "x"), default)).Should().Be(400);
        StatusOf(await Credits().Grant(Org, new GrantCreditsDto(Guid.NewGuid(), 0, Now.AddDays(30), "x"), default)).Should().Be(400);
        StatusOf(await Credits().Grant(Org, new GrantCreditsDto(Guid.Empty, 10, Now.AddDays(30), "x"), default)).Should().Be(400);
        (await BalanceMillisAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Grants_to_an_unknown_org_or_the_platform_org_are_404()
    {
        (await Credits().Grant(Guid.NewGuid(), new GrantCreditsDto(Guid.NewGuid(), 10, Now.AddDays(30), "x"), default)).Should().BeOfType<NotFoundResult>();
        (await Credits().Grant(WellKnownOrganizations.PlatformId, new GrantCreditsDto(Guid.NewGuid(), 10, Now.AddDays(30), "x"), default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Grants_list_newest_first()
    {
        await Credits().Grant(Org, new GrantCreditsDto(Guid.NewGuid(), 10, Now.AddDays(30), "first"), default);
        Clock.Advance(TimeSpan.FromMinutes(1));
        await Credits().Grant(Org, new GrantCreditsDto(Guid.NewGuid(), 20, Now.AddDays(30), "second"), default);

        JsonElement body = JsonOf(await Credits().ListGrants(Org, default));

        body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("reason").GetString()).Should().Equal("second", "first");
    }

    [Fact]
    public async Task Org_search_matches_name_or_owner_email_and_never_the_platform_org()
    {
        await Credits().Grant(Org, new GrantCreditsDto(Guid.NewGuid(), 10, Now.AddDays(30), "x"), default);

        var byName = JsonOf(await Credits().SearchOrgs("acme", default)).GetProperty("items").EnumerateArray().ToList();
        var byEmail = JsonOf(await Credits().SearchOrgs("OWNER@ACME", default)).GetProperty("items").EnumerateArray().ToList();
        var all = JsonOf(await Credits().SearchOrgs(null, default)).GetProperty("items").EnumerateArray().ToList();
        var platform = JsonOf(await Credits().SearchOrgs("platform", default)).GetProperty("items").EnumerateArray().ToList();

        byName.Should().ContainSingle();
        byName[0].GetProperty("id").GetGuid().Should().Be(Org);
        byName[0].GetProperty("ownerEmail").GetString().Should().Be("owner@acme.test");
        byName[0].GetProperty("balance").GetDouble().Should().Be(10);
        byEmail.Should().ContainSingle();
        all.Select(i => i.GetProperty("id").GetGuid()).Should().NotContain(WellKnownOrganizations.PlatformId);
        platform.Should().BeEmpty();

        var mine = JsonOf(await Credits().UserOrgs(OwnerId, default)).GetProperty("items").EnumerateArray().ToList();
        mine.Select(i => i.GetProperty("id").GetGuid()).Should().Equal(Org);
        JsonOf(await Credits().UserOrgs(AdminId, default)).GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Admin_search_by_user_id_never_surfaces_a_service_account()
    {
        Guid svc = Guid.NewGuid();
        await using (InferenceApiDbContext db = NewDb())
        {
            db.ApplicationUsers.Add(new ReelBolt.Shared.Data.Models.ApplicationUser { Id = svc, Email = "apikey+s@service.invalid", DisplayName = "ci", IsServiceAccount = true });
            db.OrganizationMemberships.Add(new ReelBolt.Shared.Data.Models.OrganizationMembership { OrganizationId = Org, UserId = svc, Role = OrganizationRole.Member });
            await db.SaveChangesAsync();
        }

        JsonOf(await Credits().UserOrgs(svc, default)).GetProperty("items").EnumerateArray().Should().BeEmpty();
        JsonOf(await Credits().UserOrgs(OwnerId, default)).GetProperty("items").EnumerateArray().Should().NotBeEmpty();
    }
}
