#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using ReelBolt.Inference.Api.Controllers;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Billing.Promotions;
using ReelBolt.Shared.Data.Models.Billing;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// billing-concurrency-followups S-11: the in-process gate in front of a promotion checkout. A checkout
/// that cannot take its promotion's gate within <c>Billing:Promotions:CheckoutLockWaitMilliseconds</c>
/// answers a retryable <c>429 checkout_busy</c> instead of queueing on a pooled connection. The Postgres
/// side (the bounded <c>lock_timeout</c>) is in <see cref="PromotionPostgresConcurrencyTests"/>.
/// </summary>
public sealed class PromotionCheckoutBusyTests : PromotionTestBase
{
    private Task<IActionResult> PlanCheckout()
        => BillingApi().Checkout(new CheckoutRequestDto("creator", null, null), default);

    [Fact]
    public void CheckoutBusy_renders_429_checkout_busy_with_retry_after_2()
    {
        IActionResult result = PromotionErrors.CheckoutBusy.ToResult();

        StatusOf(result).Should().Be(429);
        ErrorCode(result).Should().Be("checkout_busy");
        JsonOf(result).GetProperty("message").GetString().Should().NotBeNullOrWhiteSpace();

        DefaultHttpContext http = new();
        ((ObjectResult)result).OnFormatting(new ActionContext(http, new RouteData(), new ActionDescriptor()));
        http.Response.Headers.RetryAfter.ToString().Should().Be("2");
    }

    [Fact]
    public void The_lock_wait_defaults_to_five_seconds()
        => new PromotionOptions().CheckoutLockWaitMilliseconds.Should().Be(5000);

    [Fact]
    public async Task A_checkout_whose_promotion_gate_is_held_past_the_lock_wait_is_checkout_busy_and_reserves_nothing()
    {
        Guid discount = Seed(PromotionKind.PlanDiscount);
        PromoOptions.CheckoutLockWaitMilliseconds = 200;

        using (IDisposable? held = await PromotionLocks.EnterCheckoutGatesAsync([discount], TimeSpan.FromSeconds(10), CancellationToken.None))
        {
            held.Should().NotBeNull();
            DateTime started = DateTime.UtcNow;

            IActionResult busy = await PlanCheckout();

            (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(5));
            StatusOf(busy).Should().Be(429);
            ErrorCode(busy).Should().Be("checkout_busy");
            await using InferenceApiDbContext db = NewDb();
            (await db.PromotionCheckouts.CountAsync()).Should().Be(0, "a busy checkout reserves nothing");
        }

        StatusOf(await PlanCheckout()).Should().Be(200, "the gate is released once its holder is done");
    }

    [Fact]
    public async Task A_busy_checkout_releases_the_gates_it_already_took()
    {
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();

        using (IDisposable? held = await PromotionLocks.EnterCheckoutGatesAsync([second], TimeSpan.FromSeconds(10), CancellationToken.None))
        {
            (await PromotionLocks.EnterCheckoutGatesAsync([first, second], TimeSpan.FromMilliseconds(100), CancellationToken.None))
                .Should().BeNull("the second gate is held");
        }

        using IDisposable? again = await PromotionLocks.EnterCheckoutGatesAsync([first], TimeSpan.FromMilliseconds(100), CancellationToken.None);
        again.Should().NotBeNull("a timed-out attempt gives back the gates it took");
    }

    [Fact]
    public async Task A_busy_checkout_is_not_counted_against_the_hourly_checkout_limit()
    {
        Guid discount = Seed(PromotionKind.PlanDiscount);
        PromoOptions.CheckoutsPerUserPerHour = 1;
        PromoOptions.CheckoutLockWaitMilliseconds = 200;

        using (IDisposable? held = await PromotionLocks.EnterCheckoutGatesAsync([discount], TimeSpan.FromSeconds(10), CancellationToken.None))
        {
            IActionResult busy = await PlanCheckout();

            StatusOf(busy).Should().Be(429);
            ErrorCode(busy).Should().Be("checkout_busy");
        }

        Counters.Counts.Should().BeEmpty("a busy checkout reserves nothing and is not counted");
        StatusOf(await PlanCheckout()).Should().Be(200, "the single allowed checkout of the hour was not spent by the busy attempt");
    }

    [Fact]
    public async Task An_over_limit_user_is_refused_before_the_promotion_gate()
    {
        Guid discount = Seed(PromotionKind.PlanDiscount);
        PromoOptions.CheckoutsPerUserPerHour = 1;
        StatusOf(await PlanCheckout()).Should().Be(200);

        using (IDisposable? held = await PromotionLocks.EnterCheckoutGatesAsync([discount], TimeSpan.FromSeconds(10), CancellationToken.None))
        {
            DateTime started = DateTime.UtcNow;

            IActionResult refused = await PlanCheckout();

            (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(2), "the refusal never queues on the promotion gate");
            StatusOf(refused).Should().Be(429);
            ErrorCode(refused).Should().Be("promo_checkout_rate_limited");
        }

        Counters.Counts.Values.Sum().Should().Be(1, "the refused attempt counts nothing; only the first checkout does");
    }

    [Fact]
    public async Task A_checkout_whose_code_promotion_is_invalid_at_reservation_is_not_counted()
    {
        Guid code = Seed(PromotionKind.PlanDiscount, p => p.Code = "LAUNCH20");
        CheckoutTarget target = new("pri_creator", null);
        PromotionOutcome<CheckoutPromotions> resolved;
        await using (InferenceApiDbContext db = NewDb())
        {
            resolved = await Service(db).ResolveCheckoutPromotionAsync(Org, OwnerId, target, "LAUNCH20", default);
        }

        resolved.Error.Should().BeNull("the code applies when it is resolved");
        await using (InferenceApiDbContext db = NewDb())
        {
            Promotion promotion = await db.Promotions.SingleAsync(p => p.Id == code);
            promotion.Status = PromotionStatus.Paused;
            await db.SaveChangesAsync();
        }

        PromotionOutcome<CheckoutReservation> reserved;
        await using (InferenceApiDbContext db = NewDb())
        {
            reserved = await Service(db).ReserveCheckoutAsync(Org, OwnerId, target, resolved.Value!, default);
        }

        reserved.Error.Should().Be(PromotionErrors.Invalid);
        Counters.Counts.Should().BeEmpty("a checkout whose code no longer applies is not counted");
    }
}
