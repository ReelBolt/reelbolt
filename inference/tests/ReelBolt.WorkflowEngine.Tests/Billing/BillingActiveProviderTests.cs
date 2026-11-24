#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers.Admin;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Reconcile;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Billing.Stripe;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>billing-revenue-discount T-005: only the active billing provider is revenue; the rest is reported under <c>excluded</c>.</summary>
public sealed class BillingActiveProviderTests
{
    private sealed class Admin : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "a@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => true;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
    }

    private sealed class NoReconciler : IBillingReconciler
    {
        public Task<ReconcileResult> ReconcileAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReconcileResult> ReconcileHeldAsync(Guid organizationId, ReconcileTrigger trigger, bool force, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordChangeRequestedAsync(Guid organizationId, string changeKind, Guid? userId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static async Task<InferenceApiDbContext> SeededAsync()
    {
        InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        Guid plan = Guid.NewGuid();
        Guid paddleOrg = Guid.NewGuid();
        Guid stripeOrg = Guid.NewGuid();
        db.Organizations.AddRange(new Organization { Id = paddleOrg, Name = "p", Slug = "p" }, new Organization { Id = stripeOrg, Name = "s", Slug = "s" });
        db.Plans.Add(new Plan { Id = plan, Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 1, IsActive = true });
        db.Subscriptions.AddRange(
            new Subscription { Id = Guid.NewGuid(), OrganizationId = paddleOrg, PlanId = plan, Provider = "paddle", ProviderSubscriptionId = "sub_p", Status = SubscriptionStatus.Active, CreatedAt = DateTime.UtcNow.AddDays(-20) },
            new Subscription { Id = Guid.NewGuid(), OrganizationId = stripeOrg, PlanId = plan, Provider = "stripe", ProviderSubscriptionId = "sub_s", Status = SubscriptionStatus.Active, CreatedAt = DateTime.UtcNow.AddDays(-20) });
        DateTime at = DateTime.UtcNow.AddDays(-3);
        for (int i = 0; i < 4; i++)
        {
            db.BillingPayments.Add(new BillingPayment { Id = Guid.NewGuid(), Provider = "paddle", ProviderTransactionId = $"txn_{i}", ProviderSubscriptionId = "sub_p", AmountMinor = 7900, CurrencyCode = "USD", OccurredAt = at, CreatedAt = at });
        }

        db.BillingPayments.Add(new BillingPayment { Id = Guid.NewGuid(), Provider = "stripe", ProviderTransactionId = "in_1", ProviderSubscriptionId = "sub_s", AmountMinor = 2900, CurrencyCode = "USD", SubtotalMinor = 7900, DiscountMinor = 5000, OccurredAt = at, CreatedAt = at });
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<BillingAdminOverviewResponse> Overview(InferenceApiDbContext db, bool stripeActive)
    {
        List<IBillingProvider> providers =
        [
            PaddlePayloads.Provider(),
            new StripeBillingProvider(new HttpClient(), Microsoft.Extensions.Options.Options.Create(StripePayloads.Options()), TimeProvider.System)
        ];
        BillingAdminController controller = new(db, new Admin(), new NoReconciler(),
            Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
            Microsoft.Extensions.Options.Options.Create(new BillingReconcileOptions()),
            stripeActive ? providers : null,
            stripeActive ? Microsoft.Extensions.Options.Options.Create(new BillingProviderOptions { Provider = BillingProviderOptions.StripeName }) : null);
        ActionResult<BillingAdminOverviewResponse> result = await controller.Overview(null, null, CancellationToken.None);
        return (BillingAdminOverviewResponse)((OkObjectResult)result.Result!).Value!;
    }

    [Fact]
    public async Task Only_the_active_provider_is_revenue_and_the_others_are_listed_as_excluded()
    {
        using InferenceApiDbContext db = await SeededAsync();
        BillingAdminOverviewResponse body = await Overview(db, stripeActive: true);
        BillingAdminRevenue revenue = body.Revenue;

        revenue.ActiveProvider.Should().Be("stripe");
        revenue.PayingSubscriptions.Should().Be(1);
        revenue.ListPriceMonthlyUsd.Should().Be(79m);
        revenue.CommittedMonthlyUsd.Should().BeApproximately(29m, 0.01m);
        BillingCollectedCurrency usd = revenue.Collected!.Single();
        (usd.Payments, usd.GrossMinor).Should().Be((1, 2900L));
        revenue.CollectedNetUsd.Should().Be(29m);

        BillingExcludedProvider paddle = revenue.Excluded!.Single();
        paddle.Provider.Should().Be("paddle");
        paddle.PayingSubscriptions.Should().Be(1);
        (paddle.Collected.Single().Payments, paddle.Collected.Single().GrossMinor).Should().Be((4, 31600L));

        body.Plans.Single().CommittedMonthlyUsd.Should().BeApproximately(29m, 0.01m, "plan margins count the active provider only");
    }

    [Fact]
    public async Task With_no_active_provider_every_provider_counts_as_before()
    {
        using InferenceApiDbContext db = await SeededAsync();
        BillingAdminRevenue revenue = (await Overview(db, stripeActive: false)).Revenue;

        revenue.ActiveProvider.Should().BeNull();
        revenue.Excluded.Should().BeEmpty();
        revenue.PayingSubscriptions.Should().Be(2);
        revenue.Collected!.Single().Payments.Should().Be(5);
    }
}
