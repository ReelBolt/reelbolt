#nullable enable

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing;
using ReelBolt.Inference.Api.Services.Billing.Paddle;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.WorkflowEngine.Tests.Marketing;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripeKit;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>
/// DEC-20: reservations of both providers coexist. The sweeper cancels a reservation through the provider that owns its
/// transaction id, and a Stripe subscription checkout's first invoice completes its discount reservation.
/// </summary>
public sealed class StripePromotionRoutingTests : PromotionTestBase
{
    private PromotionCheckoutSweeper Sweeper(StripeBillingProvider stripe, PaddleBillingProvider paddle, string selected)
    {
        ServiceCollection services = new();
        services.AddScoped(_ => NewDb());
        services.AddSingleton<IBillingProvider>(paddle);
        services.AddSingleton<IBillingProvider>(stripe);
        services.AddSingleton<IOptions<BillingProviderOptions>>(Microsoft.Extensions.Options.Options.Create(new BillingProviderOptions { Provider = selected }));
        ServiceProvider sp = services.BuildServiceProvider();
        return new PromotionCheckoutSweeper(sp.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(PromoOptions), NullLogger<PromotionCheckoutSweeper>.Instance, Clock);
    }

    private void AddExpired(string txn)
    {
        using InferenceApiDbContext db = NewDb();
        db.PromotionCheckouts.Add(new PromotionCheckout
        {
            Id = Guid.NewGuid(),
            ProviderTransactionId = txn,
            OrganizationId = Org,
            UserId = OwnerId,
            DiscountPromotionId = Guid.NewGuid(),
            PriceId = "price_creator",
            CreatedAt = Now.AddHours(-5),
            SupersededAt = Now.AddHours(-4),
            WithinCapsAtReservation = true
        });
        db.SaveChanges();
    }

    [Theory]
    [InlineData("stripe")]
    [InlineData("paddle")]
    public async Task TheSweeper_CancelsEachReservation_ThroughTheProviderThatOwnsItsTransaction(string selected)
    {
        StripeFake stripeFake = new StripeFake().On("POST", "/v1/checkout/sessions/cs_1/expire", new { id = "cs_1" });
        PaddlePayloads.StubHandler paddleHandler = new(_ => PaddlePayloads.Json("""{"data":{"id":"txn_9","status":"draft"}}"""));
        AddExpired("cs_1");
        AddExpired("txn_9");

        int settled = await Sweeper(Provider(stripeFake), PaddlePayloads.Provider(paddleHandler), selected).SweepOnceAsync(Now, default);

        settled.Should().Be(2);
        stripeFake.Calls.Should().ContainSingle().Which.Path.Should().Be("/v1/checkout/sessions/cs_1/expire");
        paddleHandler.Calls.Should().NotBeEmpty().And.OnlyContain(c => c.Url.Contains("txn_9", StringComparison.Ordinal), "Paddle is never asked about a Stripe session, and vice versa");
    }

    [Fact]
    public async Task AReservationWhoseOwnerIsRegisteredButUnconfigured_IsNotRoutedElsewhere_ButKeptOwed_WithAnError()
    {
        StripeFake stripeFake = new();
        PaddlePayloads.StubHandler paddleHandler = new(_ => throw new InvalidOperationException("Paddle must not be asked about a Stripe session"));
        AddExpired("cs_1");
        RecordingLogger<PromotionCheckoutSweeper> log = new();
        ServiceCollection services = new();
        services.AddScoped(_ => NewDb());
        services.AddSingleton<IBillingProvider>(PaddlePayloads.Provider(paddleHandler));
        services.AddSingleton<IBillingProvider>(Provider(stripeFake, new StripeOptions()));
        services.AddSingleton<IOptions<BillingProviderOptions>>(Microsoft.Extensions.Options.Options.Create(new BillingProviderOptions { Provider = "paddle" }));
        ServiceProvider sp = services.BuildServiceProvider();
        PromotionCheckoutSweeper sweeper = new(sp.GetRequiredService<IServiceScopeFactory>(), Microsoft.Extensions.Options.Options.Create(PromoOptions), log, Clock);

        (await sweeper.SweepOnceAsync(Now, default)).Should().Be(0);

        paddleHandler.Calls.Should().BeEmpty();
        stripeFake.Calls.Should().BeEmpty();
        log.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error)
            .Which.Message.Should().Contain("cs_1".Length > 0 ? "not configured" : "");
        await using InferenceApiDbContext db = NewDb();
        PromotionCheckout row = await db.PromotionCheckouts.AsNoTracking().SingleAsync(c => c.ProviderTransactionId == "cs_1");
        row.ProviderCancelledAt.Should().BeNull();
        row.ProviderCancelAttempts.Should().Be(0, "still owed and retryable once Stripe is configured");
    }

    [Fact]
    public async Task StripeCancel_OfANonSessionId_IsAProgrammingError_NotNotCancellable()
    {
        StripeFake fake = new StripeFake().Error("POST", "/v1/checkout/sessions/pi_1/expire", 404, "resource_missing");

        await Provider(fake).Invoking(p => p.CancelTransactionAsync("pi_1", default)).Should().ThrowAsync<ArgumentException>();
        fake.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task StripeCancel_OfASession_TreatsA404And400AsNotCancellable()
    {
        foreach (int status in new[] { 400, 404 })
        {
            StripeFake fake = new StripeFake().Error("POST", "/v1/checkout/sessions/cs_1/expire", status, "resource_invalid");
            (await Provider(fake).CancelTransactionAsync("cs_1", default)).Should().Be(ProviderTransactionCancel.NotCancellable);
        }
    }

    [Fact]
    public void OwnsTransaction_FollowsTheIdPrefixes()
    {
        StripeBillingProvider stripe = Provider(new StripeFake());
        PaddleBillingProvider paddle = PaddlePayloads.Provider();
        foreach (string id in new[] { "cs_test_1", "pi_1", "in_1" })
        {
            stripe.OwnsTransaction(id).Should().BeTrue(id);
            paddle.OwnsTransaction(id).Should().BeFalse(id);
        }

        paddle.OwnsTransaction("txn_1").Should().BeTrue();
        stripe.OwnsTransaction("txn_1").Should().BeFalse();
    }

    [Fact]
    public async Task ASubscriptionCheckoutWithADiscountReservation_IsCompletedByItsFirstInvoice()
    {
        Guid promo = Seed(PromotionKind.PlanDiscount, p => p.ProviderDiscountId = "rb_promo_x");
        await using (InferenceApiDbContext db = NewDb())
        {
            db.PromotionCheckouts.Add(new PromotionCheckout
            {
                Id = Guid.NewGuid(),
                ProviderTransactionId = "cs_sub_1",
                OrganizationId = Org,
                UserId = OwnerId,
                DiscountPromotionId = promo,
                PriceId = "price_creator",
                CreatedAt = Now,
                WithinCapsAtReservation = true
            });
            await db.SaveChangesAsync();
        }

        JsonObject invoice = StripePayloads.Invoice("subscription_create", 2320, StripePayloads.Line(2320, "price_creator", 1, Now));
        invoice["discounts"] = new JsonArray("rb_promo_x");
        invoice["total_discount_amounts"] = new JsonArray(new JsonObject { ["amount"] = 580 });
        string body = StripePayloads.Envelope("invoice.paid", invoice);
        BillingEvent parsed = new StripeEventParser(StripePayloads.Options(), new StripeKit.FixedClock(StripePayloads.Now)).Parse(body, StripePayloads.Signed(body));
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/checkout/sessions", new { data = new[] { new { id = "cs_sub_1" } } })
            .On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() });
        BillingEvent resolved = (await Provider(fake).ResolveAsync(parsed, default)) with { OrganizationId = Org };
        resolved.CheckoutId.Should().Be("cs_sub_1");

        await using (InferenceApiDbContext db = NewDb())
        {
            (await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, Clock).ProcessAsync(resolved, body)).Should().Be(BillingEventOutcome.Applied);
        }

        await using InferenceApiDbContext check = NewDb();
        (await check.PromotionCheckouts.SingleAsync()).CompletedAt.Should().NotBeNull("the reservation is closed by the payment");
        PromotionRedemption redemption = await check.PromotionRedemptions.SingleAsync();
        redemption.UserId.Should().Be(OwnerId, "the buyer comes from the reservation");
        redemption.Kind.Should().Be(PromotionRedemptionKind.DiscountApplied);
        redemption.Overshoot.Should().BeFalse();
    }
}
