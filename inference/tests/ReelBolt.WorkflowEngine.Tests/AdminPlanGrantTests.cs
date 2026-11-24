using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;
using OrganizationKind = ReelBolt.Shared.Tenancy.OrganizationKind;
using OrganizationRole = ReelBolt.Shared.Tenancy.OrganizationRole;

namespace ReelBolt.WorkflowEngine.Tests;

/// <summary>
/// Manual plan grants: a real <c>subscriptions</c> row with <c>provider = NULL</c>, its audit row,
/// the refusal beside a paid subscription, idempotency by request id, and the lapse at period end.
/// </summary>
public class AdminPlanGrantTests
{
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid ProId = Guid.NewGuid();
    private static readonly Guid TeamId = Guid.NewGuid();
    private static readonly Guid TrialId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AdminId = Guid.NewGuid();

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RecordingCache : IEntitlementCache
    {
        public List<Guid> Invalidated { get; } = [];
        public void Invalidate(Guid organizationId) => Invalidated.Add(organizationId);
        public void InvalidateAll() => Invalidated.Clear();
    }

    private sealed class User(bool admin, OrganizationRole? role = null) : ICurrentUser
    {
        public Guid UserId { get; } = AdminId;
        public string Email => "admin@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => admin;
        public Guid? OrganizationId => admin ? null : OrgId;
        public OrganizationRole? OrgRole => role;
    }

    private static InferenceApiDbContext NewDb() =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<InferenceApiDbContext> SeededAsync()
    {
        InferenceApiDbContext db = NewDb();
        db.Organizations.Add(new Organization { Id = OrgId, Name = "Acme", Slug = "acme", Kind = OrganizationKind.Personal });
        db.Plans.AddRange(
            new Plan { Id = TrialId, Key = "trial", Name = "Trial", Kind = PlanKind.Trial, IsActive = true, SortOrder = 0 },
            new Plan { Id = ProId, Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 49m, IsActive = true, SortOrder = 1 },
            new Plan { Id = TeamId, Key = "team", Name = "Team", Kind = PlanKind.PerSeat, PriceMonthlyUsd = 39m, IsActive = true, SortOrder = 2 });
        await db.SaveChangesAsync();
        return db;
    }

    private static PlanGrantService Service(InferenceApiDbContext db, RecordingCache? cache = null)
        => new(db, cache ?? new RecordingCache(), new FixedTime());

    private static GrantPlanDto Grant(string planKey = "pro", int days = 30, string reason = "test grant", Guid? requestId = null)
        => new(requestId ?? Guid.NewGuid(), planKey, days, null, reason);

    [Fact]
    public async Task Grant_writes_one_provider_less_subscription_and_its_audit_row()
    {
        using InferenceApiDbContext db = await SeededAsync();
        RecordingCache cache = new();

        PromotionOutcome<PlanGrantOutcome> outcome = await Service(db, cache).GrantAsync(OrgId, Grant(), AdminId, default);

        outcome.Error.Should().BeNull();
        AdminSubscriptionGrant row = outcome.Value!.Grant;
        row.OrganizationId.Should().Be(OrgId);
        row.PlanId.Should().Be(ProId);
        row.PlanKey.Should().Be("pro");
        row.Reason.Should().Be("test grant");
        row.GrantedByUserId.Should().Be(AdminId);
        row.PeriodStart.Should().Be(Now.UtcDateTime);
        row.PeriodEnd.Should().Be(Now.UtcDateTime.AddDays(30));
        row.RevokedAt.Should().BeNull();

        Subscription sub = await db.Subscriptions.SingleAsync();
        sub.Id.Should().Be(row.SubscriptionId);
        sub.Provider.Should().BeNull();
        sub.ProviderSubscriptionId.Should().BeNull();
        sub.Status.Should().Be(SubscriptionStatus.Active);
        sub.Seats.Should().Be(1);
        sub.PeriodStart.Should().Be(Now.UtcDateTime);
        sub.PeriodEnd.Should().Be(Now.UtcDateTime.AddDays(30));
        cache.Invalidated.Should().ContainSingle().Which.Should().Be(OrgId);
    }

    [Fact]
    public async Task Grant_refuses_a_live_provider_subscription_and_writes_nothing()
    {
        using InferenceApiDbContext db = await SeededAsync();
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = OrgId,
            PlanId = ProId,
            Provider = "paddle",
            ProviderSubscriptionId = "sub_1",
            Status = SubscriptionStatus.Active,
            Seats = 1
        });
        await db.SaveChangesAsync();

        PromotionOutcome<PlanGrantOutcome> outcome = await Service(db).GrantAsync(OrgId, Grant(), AdminId, default);

        outcome.Error!.Code.Should().Be("PLAN_GRANT_PROVIDER_ACTIVE");
        outcome.Error.Status.Should().Be(409);
        (await db.Subscriptions.CountAsync()).Should().Be(1);
        (await db.AdminSubscriptionGrants.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Grant_is_idempotent_by_request_id()
    {
        using InferenceApiDbContext db = await SeededAsync();
        Guid requestId = Guid.NewGuid();
        PlanGrantService service = Service(db);

        PromotionOutcome<PlanGrantOutcome> first = await service.GrantAsync(OrgId, Grant(requestId: requestId), AdminId, default);
        PromotionOutcome<PlanGrantOutcome> replay = await service.GrantAsync(OrgId, Grant(requestId: requestId), AdminId, default);

        first.Value!.Replayed.Should().BeFalse();
        replay.Value!.Replayed.Should().BeTrue();
        replay.Value.Grant.Id.Should().Be(first.Value.Grant.Id);
        (await db.Subscriptions.CountAsync()).Should().Be(1);
        (await db.AdminSubscriptionGrants.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_same_request_id_with_a_different_grant_is_a_conflict()
    {
        using InferenceApiDbContext db = await SeededAsync();
        Guid requestId = Guid.NewGuid();
        PlanGrantService service = Service(db);
        await service.GrantAsync(OrgId, Grant(requestId: requestId), AdminId, default);

        PromotionOutcome<PlanGrantOutcome> different = await service.GrantAsync(OrgId, Grant(planKey: "team", requestId: requestId), AdminId, default);

        different.Error!.Code.Should().Be("request_id_conflict");
        (await db.Subscriptions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_second_grant_replaces_the_previous_manual_subscription()
    {
        using InferenceApiDbContext db = await SeededAsync();
        PlanGrantService service = Service(db);
        PlanGrantOutcome first = (await service.GrantAsync(OrgId, Grant(), AdminId, default)).Value!;

        PlanGrantOutcome second = (await service.GrantAsync(OrgId, Grant(planKey: "team", days: 10), AdminId, default)).Value!;

        Subscription old = await db.Subscriptions.SingleAsync(s => s.Id == first.Grant.SubscriptionId);
        old.Status.Should().Be(SubscriptionStatus.Canceled);
        old.PeriodEnd.Should().Be(Now.UtcDateTime);
        second.Grant.SupersededSubscriptionId.Should().Be(old.Id);
        Subscription current = await db.Subscriptions.SingleAsync(s => s.Id == second.Grant.SubscriptionId);
        current.Status.Should().Be(SubscriptionStatus.Active);
        current.PlanId.Should().Be(TeamId);
        (await db.Subscriptions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Revoke_ends_the_subscription_now_and_stamps_the_grant()
    {
        using InferenceApiDbContext db = await SeededAsync();
        RecordingCache cache = new();
        PlanGrantService service = Service(db, cache);
        AdminSubscriptionGrant grant = (await service.GrantAsync(OrgId, Grant(), AdminId, default)).Value!.Grant;

        PromotionOutcome<AdminSubscriptionGrant> outcome = await service.RevokeAsync(
            OrgId, grant.Id, new RevokePlanGrantDto("no longer needed"), AdminId, default);

        outcome.Error.Should().BeNull();
        outcome.Value!.RevokedAt.Should().Be(Now.UtcDateTime);
        outcome.Value.RevokedByUserId.Should().Be(AdminId);
        outcome.Value.RevokeReason.Should().Be("no longer needed");
        Subscription sub = await db.Subscriptions.SingleAsync();
        sub.Status.Should().Be(SubscriptionStatus.Canceled);
        sub.PeriodEnd.Should().Be(Now.UtcDateTime);
        cache.Invalidated.Should().Contain(OrgId);
    }

    [Fact]
    public async Task Revoke_is_idempotent()
    {
        using InferenceApiDbContext db = await SeededAsync();
        PlanGrantService service = Service(db);
        AdminSubscriptionGrant grant = (await service.GrantAsync(OrgId, Grant(), AdminId, default)).Value!.Grant;
        await service.RevokeAsync(OrgId, grant.Id, new RevokePlanGrantDto(null), AdminId, default);

        PromotionOutcome<AdminSubscriptionGrant> again = await service.RevokeAsync(
            OrgId, grant.Id, new RevokePlanGrantDto("second"), AdminId, default);

        again.Error.Should().BeNull();
        again.Value!.RevokeReason.Should().BeNull();
    }

    [Fact]
    public async Task Revoke_refuses_a_subscription_that_belongs_to_the_provider()
    {
        using InferenceApiDbContext db = await SeededAsync();
        PlanGrantService service = Service(db);
        AdminSubscriptionGrant grant = (await service.GrantAsync(OrgId, Grant(), AdminId, default)).Value!.Grant;
        // The row the grant wrote was later taken over by the billing provider.
        Subscription sub = await db.Subscriptions.SingleAsync();
        sub.Provider = "paddle";
        await db.SaveChangesAsync();

        PromotionOutcome<AdminSubscriptionGrant> outcome = await service.RevokeAsync(
            OrgId, grant.Id, new RevokePlanGrantDto(null), AdminId, default);

        outcome.Error!.Code.Should().Be("PLAN_GRANT_NOT_MANUAL");
        (await db.AdminSubscriptionGrants.SingleAsync()).RevokedAt.Should().BeNull();
        (await db.Subscriptions.SingleAsync()).Status.Should().Be(SubscriptionStatus.Active);
    }

    [Fact]
    public async Task ListGrantablePlans_excludes_the_trial_plan()
    {
        using InferenceApiDbContext db = await SeededAsync();

        IReadOnlyList<Plan> plans = await Service(db).ListGrantablePlansAsync(default);

        plans.Select(p => p.Key).Should().Equal("pro", "team");
    }

    [Fact]
    public async Task Only_a_platform_admin_reaches_the_controller()
    {
        using InferenceApiDbContext db = await SeededAsync();
        PlanGrantsAdminController controller = Controller(db, new User(admin: false, role: OrganizationRole.Owner));

        (await controller.List(OrgId, default)).Should().BeOfType<ForbidResult>();
        (await controller.Grant(OrgId, Grant(), default)).Should().BeOfType<ForbidResult>();
        (await controller.Plans(default)).Should().BeOfType<ForbidResult>();
        (await db.Subscriptions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Billing_off_answers_404()
    {
        using InferenceApiDbContext db = await SeededAsync();
        PlanGrantsAdminController controller = new(
            db, new User(admin: true), Options.Create(new BillingOptions { Enabled = false }),
            Service(db), new FixedTime());

        (await controller.List(OrgId, default)).Should().BeOfType<NotFoundResult>();
        (await controller.Grant(OrgId, Grant(), default)).Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task Grant_answers_201_then_200_on_replay()
    {
        using InferenceApiDbContext db = await SeededAsync();
        PlanGrantsAdminController controller = Controller(db, new User(admin: true));
        Guid requestId = Guid.NewGuid();

        IActionResult created = await controller.Grant(OrgId, Grant(requestId: requestId), default);
        IActionResult replayed = await controller.Grant(OrgId, Grant(requestId: requestId), default);

        created.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(201);
        replayed.Should().BeOfType<OkObjectResult>();
    }

    private static PlanGrantsAdminController Controller(InferenceApiDbContext db, User user)
        => new(db, user, Options.Create(new BillingOptions { Enabled = true }), Service(db), new FixedTime());

    // ---- the lapse at period end --------------------------------------------------------------

    private sealed class ScopeStub(InferenceApiDbContext db) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public void Dispose() { }
        public object? GetService(Type serviceType) => serviceType == typeof(InferenceApiDbContext) ? db : null;
    }

    [Fact]
    public async Task An_expired_manual_subscription_stops_entitling()
    {
        using InferenceApiDbContext db = await SeededAsync();
        Guid org = Guid.NewGuid();
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            PlanId = ProId,
            Provider = null,
            Status = SubscriptionStatus.Active,
            PeriodStart = Now.UtcDateTime.AddDays(-31),
            PeriodEnd = Now.UtcDateTime.AddDays(-1)
        });
        await db.SaveChangesAsync();

        EfPlanSource<InferenceApiDbContext> source = new(new ScopeStub(db), new FixedTime());

        (await source.GetSubscribedPlanAsync(org, default)).Should().BeNull();
    }

    [Fact]
    public async Task A_provider_subscription_is_not_expired_by_a_stale_period_end()
    {
        using InferenceApiDbContext db = await SeededAsync();
        Guid org = Guid.NewGuid();
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = org,
            PlanId = ProId,
            Provider = "paddle",
            ProviderSubscriptionId = "sub_1",
            Status = SubscriptionStatus.Active,
            PeriodStart = Now.UtcDateTime.AddDays(-31),
            PeriodEnd = Now.UtcDateTime.AddDays(-1)
        });
        await db.SaveChangesAsync();

        EfPlanSource<InferenceApiDbContext> source = new(new ScopeStub(db), new FixedTime());

        (await source.GetSubscribedPlanAsync(org, default))!.Key.Should().Be("pro");
    }

    [Fact]
    public async Task An_expired_manual_subscription_does_not_hide_a_later_grant()
    {
        using InferenceApiDbContext db = await SeededAsync();
        Guid org = Guid.NewGuid();
        db.Subscriptions.AddRange(
            new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = org,
                PlanId = TeamId,
                Provider = null,
                Status = SubscriptionStatus.Active,
                UpdatedAt = Now.UtcDateTime.AddDays(-2),
                PeriodEnd = Now.UtcDateTime.AddDays(-1)
            },
            new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = org,
                PlanId = ProId,
                Provider = null,
                Status = SubscriptionStatus.Active,
                UpdatedAt = Now.UtcDateTime.AddDays(-1),
                PeriodEnd = Now.UtcDateTime.AddDays(10)
            });
        await db.SaveChangesAsync();

        EfPlanSource<InferenceApiDbContext> source = new(new ScopeStub(db), new FixedTime());

        (await source.GetSubscribedPlanAsync(org, default))!.Key.Should().Be("pro");
    }
}
