using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
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

/// <summary>
/// B9b: signed Paddle payloads through the real controller, provider and processor on Sqlite. The
/// provider's clock is pinned to the payload time so the timestamp tolerance does not need wall-clock luck.
/// </summary>
public sealed class PaddleWebhookEndToEndTests : IDisposable
{
    private static readonly DateTimeOffset Now = PaddlePayloads.Now;

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Guid _org = Guid.NewGuid();

    public PaddleWebhookEndToEndTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = _org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.AddRange(
            new Plan { Id = Guid.NewGuid(), Key = "creator", Name = "Creator", Kind = PlanKind.Subscription, MonthlyCreditsMillis = 1_000_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_creator"}}""" },
            new Plan { Id = Guid.NewGuid(), Key = "team", Name = "Team", Kind = PlanKind.PerSeat, MinSeats = 3, MonthlyCreditsMillis = 1_500_000, TopUpAllowed = true, ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_team"}}""" });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private sealed class Anonymous : ICurrentUser
    {
        public Guid UserId => Guid.Empty;
        public string Email => "";
        public bool IsAuthenticated => false;
        public Guid? OrganizationId => null;
        public OrganizationRole? OrgRole => null;
        public bool IsPlatformAdmin => false;
    }

    private sealed class SystemTenant : ITenantContext
    {
        public Guid? OrganizationId => null;
        public bool IsSystem => true;
        public IDisposable BeginSystemScope() => new Nop();
        public IDisposable BeginOrganizationScope(Guid organizationId) => new Nop();
        private sealed class Nop : IDisposable { public void Dispose() { } }
    }

    private async Task<IActionResult> Deliver(string body, string? signature = null, bool enabled = true)
    {
        InferenceApiDbContext db = NewDb();
        BillingController c = new(
            db, new Anonymous(), Options.Create(new BillingOptions { Enabled = enabled }),
            [PaddlePayloads.Provider(now: Now)],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new PaddlePayloads.FixedTime(Now)),
            new SystemTenant(), Options.Create(new PaddleOptions()), NullLogger<BillingController>.Instance);
        DefaultHttpContext http = new();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Request.Headers["Paddle-Signature"] = signature ?? PaddlePayloads.Sign(body);
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return await c.Webhook("paddle", default);
    }

    private async Task<long> Balance()
    {
        await using InferenceApiDbContext db = NewDb();
        return await db.CreditAccounts.AsNoTracking().Where(a => a.OrganizationId == _org).Select(a => a.BalanceMillis).SingleOrDefaultAsync();
    }

    private static string Outcome(IActionResult r) => ((OkObjectResult)r).Value!.ToString()!;

    [Fact]
    public async Task BadSignature_Is400_NothingStored_NothingGranted()
    {
        string body = PaddlePayloads.Subscription("evt_1", Now, _org, type: "subscription.created");

        (await Deliver(body, "ts=1759752000;h1=00ff")).Should().BeOfType<BadRequestObjectResult>();
        (await Deliver(body, PaddlePayloads.Sign(body, secret: "wrong"))).Should().BeOfType<BadRequestObjectResult>();

        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.CountAsync()).Should().Be(0);
        (await db.Subscriptions.CountAsync()).Should().Be(0);
        (await Balance()).Should().Be(0);
    }

    [Fact]
    public async Task BillingDisabled_Is404EvenWithAValidSignature()
        => (await Deliver(PaddlePayloads.Subscription("evt_1", Now, _org), enabled: false)).Should().BeOfType<NotFoundResult>();

    [Fact]
    public async Task SubscriptionCreated_GrantsPlanCredits_ReplayGrantsOnce()
    {
        string body = PaddlePayloads.Subscription("evt_1", Now, _org, type: "subscription.created");

        Outcome(await Deliver(body)).Should().Contain("Applied");
        Outcome(await Deliver(body)).Should().Contain("Duplicate");
        Outcome(await Deliver(body)).Should().Contain("Duplicate");

        (await Balance()).Should().Be(1_000_000);
        await using InferenceApiDbContext db = NewDb();
        Subscription sub = await db.Subscriptions.SingleAsync();
        sub.Provider.Should().Be("paddle");
        sub.ProviderSubscriptionId.Should().Be("sub_01");
        sub.Status.Should().Be(SubscriptionStatus.Active);
        (await db.BillingCustomers.SingleAsync()).ProviderCustomerId.Should().Be("ctm_01");
    }

    [Fact]
    public async Task TeamSubscription_GrantsPerSeat()
    {
        await Deliver(PaddlePayloads.Subscription("evt_1", Now, _org, type: "subscription.created", price: "pri_team", qty: 4));

        (await Balance()).Should().Be(6_000_000);
    }

    [Fact]
    public async Task OutOfOrder_CancelDeliveredBeforeAnOlderUpdate_StaysCanceled()
    {
        await Deliver(PaddlePayloads.Subscription("evt_cancel", Now.AddHours(2), _org, status: "canceled", type: "subscription.canceled"));
        string late = PaddlePayloads.Subscription("evt_update", Now.AddHours(1), _org, status: "active");

        Outcome(await Deliver(late)).Should().Contain("Stale");

        await using InferenceApiDbContext db = NewDb();
        (await db.Subscriptions.SingleAsync()).Status.Should().Be(SubscriptionStatus.Canceled);
    }

    [Fact]
    public async Task Renewal_RefillsOnce_AndTheSecondEventForTheSamePeriodAddsNothing()
    {
        await Deliver(PaddlePayloads.Subscription("evt_1", Now, _org, type: "subscription.created"));
        DateTimeOffset next = Now.AddMonths(1);
        await Deliver(PaddlePayloads.Subscription("evt_2", next, _org, start: next));
        await Deliver(PaddlePayloads.Subscription("evt_3", next.AddMinutes(1), _org, start: next));

        (await Balance()).Should().Be(2_000_000);
    }

    [Fact]
    public async Task PastDue_ThenCancel_FollowTheStateMachine_WithoutRefill()
    {
        await Deliver(PaddlePayloads.Subscription("evt_1", Now, _org, type: "subscription.created"));
        DateTimeOffset next = Now.AddMonths(1);
        await Deliver(PaddlePayloads.Subscription("evt_2", next, _org, status: "past_due", start: next, type: "subscription.past_due"));
        await using (InferenceApiDbContext db = NewDb())
        {
            (await db.Subscriptions.SingleAsync()).Status.Should().Be(SubscriptionStatus.PastDue);
        }

        await Deliver(PaddlePayloads.Subscription("evt_3", next.AddDays(3), _org, status: "canceled", type: "subscription.canceled"));
        await using InferenceApiDbContext db2 = NewDb();
        (await db2.Subscriptions.SingleAsync()).Status.Should().Be(SubscriptionStatus.Canceled);
        (await Balance()).Should().Be(1_000_000);
    }

    [Fact]
    public async Task ScheduledCancellation_SetsTheFlag_WithoutEndingTheSubscription()
    {
        await Deliver(PaddlePayloads.Subscription("evt_1", Now, _org, type: "subscription.created"));
        await Deliver(PaddlePayloads.Subscription("evt_2", Now.AddDays(1), _org, scheduledChange: "cancel"));

        await using InferenceApiDbContext db = NewDb();
        Subscription sub = await db.Subscriptions.SingleAsync();
        sub.CancelAtPeriodEnd.Should().BeTrue();
        sub.Status.Should().Be(SubscriptionStatus.Active);
    }

    [Fact]
    public async Task TopUp_Grants1000Credits_OnceAndOnlyTheOnce()
    {
        string body = PaddlePayloads.TopUpTransaction("evt_t1", Now, _org);

        await Deliver(body);
        await Deliver(body);
        // Paddle can also redeliver the same payment under a new notification id.
        await Deliver(PaddlePayloads.TopUpTransaction("evt_t2", Now.AddSeconds(1), _org));

        (await Balance()).Should().Be(1_000_000);
        await using InferenceApiDbContext db = NewDb();
        (await db.CreditGrants.SingleAsync()).Source.Should().Be(CreditGrantSource.TopUp);
    }

    [Fact]
    public async Task FullRefundOfTheTopUp_ReturnsTheCredits_OnceAcrossCreatedAndUpdatedEvents()
    {
        await Deliver(PaddlePayloads.TopUpTransaction("evt_t1", Now, _org));

        await Deliver(PaddlePayloads.Refund("evt_r1", Now.AddMinutes(1)));
        await Deliver(PaddlePayloads.Refund("evt_r2", Now.AddMinutes(2)));

        (await Balance()).Should().Be(0);
    }

    [Fact]
    public async Task UnrelatedEvent_IsAcknowledgedAndRecorded()
    {
        string body = PaddlePayloads.Envelope("evt_c", "customer.created", Now, """{"id":"ctm_01"}""");

        Outcome(await Deliver(body)).Should().Contain("Applied");

        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.SingleAsync()).EventType.Should().Be("customer.created");
    }
}
