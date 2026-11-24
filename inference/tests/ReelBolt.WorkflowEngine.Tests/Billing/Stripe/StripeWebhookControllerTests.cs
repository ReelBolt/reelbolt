using System;
using System.IO;
using System.Linq;
using System.Net.Http;
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
using ReelBolt.Inference.Api.Services.Billing.Stripe;
using ReelBolt.Shared.Auth;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Billing.Stripe.StripePayloads;

namespace ReelBolt.WorkflowEngine.Tests.Billing.Stripe;

/// <summary>
/// A hand-signed Stripe webhook through the real <see cref="BillingController"/>, <see cref="StripeBillingProvider"/>
/// (its Stripe API faked) and <see cref="BillingEventProcessor"/> on Sqlite, with Stripe the active provider.
/// </summary>
public sealed class StripeWebhookControllerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public StripeWebhookControllerTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = OrgId, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
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

    private static StripeFake Stripe() => new StripeFake()
        .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { balance_transaction = new { fee = 61 } } });

    private async Task<IActionResult> Deliver(string body, string? signature = null, StripeFake? fake = null)
    {
        InferenceApiDbContext db = NewDb();
        StripeBillingProvider stripe = StripeKit.Provider(fake ?? Stripe(), StripeKit.Options());
        BillingController c = new(
            db, new Anonymous(), Microsoft.Extensions.Options.Options.Create(new BillingOptions { Enabled = true }),
            [stripe],
            new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, new StripeKit.FixedClock(Now)),
            new SystemTenant(), Microsoft.Extensions.Options.Options.Create(new PaddleOptions()), NullLogger<BillingController>.Instance,
            providerSelection: Microsoft.Extensions.Options.Options.Create(new BillingProviderOptions { Provider = "stripe" }));
        DefaultHttpContext http = new();
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        http.Request.Headers[StripeSignature.HeaderName] = signature ?? StripeSignature.Sign(body, Secret, Unix(Now));
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return await c.Webhook("stripe", default);
    }

    private async Task<long> Balance()
    {
        await using InferenceApiDbContext db = NewDb();
        return (await new CreditLedger(db).GetBalanceAsync(OrgId)).BalanceMillis;
    }

    private static string TopUpBody(string id = "evt_t1") => Envelope("checkout.session.completed", TopUpSession(credits: 1000), id: id);

    [Fact]
    public async Task SignedTopUp_GrantsCreditsOnce_AndTheFeeIsRecorded()
    {
        (await Deliver(TopUpBody())).Should().BeOfType<OkObjectResult>().Which.Value!.ToString().Should().Contain("Applied");

        (await Balance()).Should().Be(1_000_000);
        await using InferenceApiDbContext db = NewDb();
        BillingPayment payment = await db.BillingPayments.SingleAsync();
        payment.ProviderTransactionId.Should().Be("pi_1");
        payment.FeeMinor.Should().Be(61);
    }

    [Fact]
    public async Task ADuplicateDelivery_IsIdempotent()
    {
        string body = TopUpBody();

        await Deliver(body);
        (await Deliver(body)).Should().BeOfType<OkObjectResult>().Which.Value!.ToString().Should().Contain("Duplicate");
        await Deliver(body);

        (await Balance()).Should().Be(1_000_000);
        await using InferenceApiDbContext db = NewDb();
        (await db.BillingPayments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ABadSignature_Is400_AndNothingIsStoredOrGranted()
    {
        string body = TopUpBody();

        (await Deliver(body, "t=1,v1=00ff")).Should().BeOfType<BadRequestObjectResult>();
        (await Deliver(body, StripeSignature.Sign(body, "whsec_wrong", Unix(Now)))).Should().BeOfType<BadRequestObjectResult>();
        (await Deliver(body + " ", StripeSignature.Sign(body, Secret, Unix(Now)))).Should().BeOfType<BadRequestObjectResult>();

        await using InferenceApiDbContext db = NewDb();
        (await db.BillingWebhookEvents.CountAsync()).Should().Be(0);
        (await Balance()).Should().Be(0);
    }

    [Fact]
    public async Task AStripeOutageWhileResolving_Answers500_SoStripeRetries_AndGrantsNothing()
    {
        // The fee lookup is best effort and never fails the delivery, but a refund's payment lookup does.
        await Deliver(TopUpBody());
        string refund = Envelope("refund.created", Refund(), id: "evt_r1");
        StripeFake down = new StripeFake().Error("GET", "/v1/invoice_payments", 503, "api_error");

        Func<Task> act = async () => await Deliver(refund, fake: down);

        await act.Should().ThrowAsync<HttpRequestException>();
        (await Balance()).Should().Be(1_000_000, "the claw-back waits for the retry");
    }

    [Fact]
    public async Task ATopUpRefund_ThroughTheProviderResolve_ClawsBackOnce()
    {
        await Deliver(TopUpBody());
        string refund = Envelope("refund.created", Refund(), id: "evt_r1");
        StripeFake fake = new StripeFake()
            .On("GET", "/v1/invoice_payments", new { data = Array.Empty<object>() })
            .On("GET", "/v1/payment_intents/pi_1", new { id = "pi_1", latest_charge = new { amount = 1450, amount_refunded = 1450 } });

        await Deliver(refund, fake: fake);
        await Deliver(Envelope("refund.updated", Refund(), id: "evt_r2"), fake: fake);

        (await Balance()).Should().Be(0);
    }
}

public sealed class StripeRefusalCopyTests
{
    [Theory]
    [InlineData("resource_missing")]
    [InlineData("invoice_payment_intent_requires_action")]
    [InlineData("subscription_payment_intent_requires_action")]
    [InlineData("card_declined")]
    [InlineData("parameter_invalid_empty")]
    [InlineData("lock_timeout")]
    public void StripeCodes_NameStripe_AndPointToManageBilling_WithoutPaddleWording(string code)
    {
        string message = BillingController.ProviderRefusalMessage(code, "Stripe");

        message.Should().Contain("Stripe").And.NotContain("Paddle");
        (message.Contains("Manage billing", StringComparison.Ordinal) || message.Contains("Wait a few minutes", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Fact]
    public void PaddleStringsStayByteIdentical_AndUnknownStripeCodesFallBackToTheGeneric()
    {
        BillingController.ProviderRefusalMessage("lock_timeout", "Paddle").Should().Be(BillingController.GenericProviderRefusalMessage);
        BillingController.ProviderRefusalMessage("subscription_not_active", "Paddle").Should().Be("This subscription is not active. Open Manage in Paddle to update your payment method, then try again.");
        BillingController.ProviderRefusalMessage("something_new", "Stripe").Should().Contain("Stripe").And.NotContain("Paddle");
    }
}
