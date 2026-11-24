#nullable enable
using System;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>choose-plan-server-side T-001: checkout refuses an account whose email the database does not show as verified.</summary>
public sealed class CheckoutEmailGateTests : PromotionTestBase
{
    private sealed class ClaimUser(Guid userId, Guid org, bool claim) : ICurrentUser
    {
        public Guid UserId => userId;
        public string Email => "u@example.com";
        public bool IsAuthenticated => true;
        public Guid? OrganizationId => org;
        public OrganizationRole? OrgRole => OrganizationRole.Owner;
        public bool IsPlatformAdmin => false;
        public bool EmailVerified => claim;
    }

    private Guid AddUser(bool verified)
    {
        Guid id = Guid.NewGuid();
        using InferenceApiDbContext db = NewDb();
        db.ApplicationUsers.Add(new ApplicationUser { Id = id, Email = $"{id:N}@example.test", DisplayName = "u", EmailVerifiedAt = verified ? DateTime.UtcNow : null });
        db.OrganizationMemberships.Add(new OrganizationMembership { OrganizationId = Org, UserId = id, Role = OrganizationRole.Owner });
        db.SaveChanges();
        return id;
    }

    private BillingController As(Guid userId, bool claim)
    {
        InferenceApiDbContext db = NewDb();
        BillingController c = new(
            db, new ClaimUser(userId, Org, claim), Billing(true), [Provider],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, Clock),
            new BillingControllerTests.FakeTenant(), Options.Create(new PaddleOptions()),
            NullLogger<BillingController>.Instance, promotions: Service(db));
        c.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
        return c;
    }

    [Fact]
    public async Task Unverified_account_is_refused_before_the_provider_or_a_reservation()
    {
        Guid user = AddUser(verified: false);
        Seed(PromotionKind.PlanDiscount);

        IActionResult result = await As(user, claim: false).Checkout(new CheckoutRequestDto("creator", null, null), default);

        StatusOf(result).Should().Be(403);
        JsonOf(result).GetProperty("code").GetString().Should().Be("EMAIL_NOT_VERIFIED");
        JsonOf(result).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();
        Provider.LastCheckout.Should().BeNull();
        await using InferenceApiDbContext db = NewDb();
        (await db.PromotionCheckouts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Verified_account_checks_out_as_before()
    {
        StatusOf(await BillingApi().Checkout(new CheckoutRequestDto("creator", null, null), default)).Should().Be(200);
        Provider.LastCheckout.Should().NotBeNull();
    }

    [Fact]
    public async Task A_stale_verified_claim_does_not_beat_an_unverified_database()
    {
        Guid user = AddUser(verified: false);
        IActionResult result = await As(user, claim: true).Checkout(new CheckoutRequestDto("creator", null, null), default);

        StatusOf(result).Should().Be(403);
        Provider.LastCheckout.Should().BeNull();
    }

    [Fact]
    public async Task A_database_verified_account_is_allowed_even_when_the_claim_says_false()
    {
        Guid user = AddUser(verified: true);
        StatusOf(await As(user, claim: false).Checkout(new CheckoutRequestDto("creator", null, null), default)).Should().Be(200);
    }
}
