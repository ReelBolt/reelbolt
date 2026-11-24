#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ReelBolt.Inference.Api.Controllers.Admin;
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
/// billing-revenue-discount T-001: a Paddle payload through the parser and the processor into
/// <c>billing_payments</c> / <c>billing_refunds</c>, and those rows through the admin overview. A 100%
/// discounted subscription must read as 0 collected and 0 committed, never as its list price.
/// </summary>
public sealed class BillingRevenueDiscountTests : IDisposable
{
    private static readonly Guid Org = Guid.NewGuid();
    private static readonly Guid ProPlan = Guid.NewGuid();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public BillingRevenueDiscountTests()
    {
        _connection.Open();
        using InferenceApiDbContext db = NewDb();
        db.Database.EnsureCreated();
        db.Organizations.Add(new Organization { Id = Org, Name = "o", Slug = "o", Kind = OrganizationKind.Team });
        db.Plans.Add(new Plan { Id = ProPlan, Key = "pro", Name = "Pro", Kind = PlanKind.Subscription, PriceMonthlyUsd = 79, MonthlyCreditsMillis = 3_500_000, IsActive = true });
        db.Subscriptions.Add(new Subscription
        {
            Id = Guid.NewGuid(),
            OrganizationId = Org,
            PlanId = ProPlan,
            Provider = "paddle",
            ProviderSubscriptionId = "sub_01",
            Status = SubscriptionStatus.Active,
            Seats = 1,
            CreatedAt = DateTime.UtcNow.AddDays(-20)
        });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private InferenceApiDbContext NewDb()
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseSqlite(_connection).Options);

    private sealed class Admin : ICurrentUser
    {
        public Guid UserId { get; } = Guid.NewGuid();
        public string Email => "a@example.com";
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => true;
        public Guid? OrganizationId => null;
        public ReelBolt.Shared.Tenancy.OrganizationRole? OrgRole => null;
    }

    private static DateTimeOffset Ago(int days) => DateTimeOffset.UtcNow.AddDays(-days);

    private static string Totals(long subtotal, long discount, long tax, long total, long grand, string currency = "USD", long fee = 0, long earnings = 0) =>
        $$"""{"subtotal":"{{subtotal}}","discount":"{{discount}}","tax":"{{tax}}","total":"{{total}}","grand_total":"{{grand}}","fee":"{{fee}}","earnings":"{{earnings}}","currency_code":"{{currency}}"}""";

    private static string Transaction(string eventId, DateTimeOffset at, string txn, string totals, string currency = "USD", string? subscription = "sub_01") =>
        PaddlePayloads.Envelope(eventId, "transaction.completed", at, $$$"""
            {"id":"{{{txn}}}","status":"completed","customer_id":"ctm_01","subscription_id":{{{(subscription is null ? "null" : $"\"{subscription}\"")}}},
             "origin":"web","currency_code":"{{{currency}}}","details":{"totals":{{{totals}}}},
             "items":[{"price_id":"pri_pro","price":{"id":"pri_pro","product_id":"pro_01"},"quantity":1}],
             "custom_data":{"organization_id":"{{{Org}}}","kind":"subscription"}}
            """);

    private static string Adjustment(string eventId, DateTimeOffset at, string adjustment, string txn, long total, long tax, string action = "refund") =>
        PaddlePayloads.Envelope(eventId, "adjustment.updated", at, $$$"""
            {"id":"{{{adjustment}}}","action":"{{{action}}}","type":"partial","status":"approved","transaction_id":"{{{txn}}}","subscription_id":"sub_01","customer_id":"ctm_01",
             "currency_code":"USD","totals":{"subtotal":"{{{total - tax}}}","tax":"{{{tax}}}","total":"{{{total}}}","fee":"0","earnings":"0","currency_code":"USD"}}
            """);

    private static BillingEvent Parse(string body) =>
        PaddlePayloads.Provider().VerifyAndParseWebhook(body, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Paddle-Signature"] = PaddlePayloads.Sign(body)
        });

    private async Task<BillingEventOutcome> Process(string body)
    {
        await using InferenceApiDbContext db = NewDb();
        return await new BillingEventProcessor(db, NullLogger<BillingEventProcessor>.Instance, TimeProvider.System).ProcessAsync(Parse(body), body);
    }

    private async Task<BillingAdminRevenue> Revenue()
    {
        await using InferenceApiDbContext db = NewDb();
        BillingAdminController controller = new(db, new Admin(), new NoReconciler(),
            Options.Create(new BillingOptions { Enabled = true }), Options.Create(new ReelBolt.Inference.Api.Services.Billing.Reconcile.BillingReconcileOptions()));
        ActionResult<BillingAdminOverviewResponse> result = await controller.Overview(null, null, CancellationToken.None);
        return ((BillingAdminOverviewResponse)((OkObjectResult)result.Result!).Value!).Revenue;
    }

    private sealed class NoReconciler : ReelBolt.Inference.Api.Services.Billing.Reconcile.IBillingReconciler
    {
        public Task<ReelBolt.Inference.Api.Services.Billing.Reconcile.ReconcileResult> ReconcileAsync(Guid organizationId, ReelBolt.Inference.Api.Services.Billing.Reconcile.ReconcileTrigger trigger, bool force, CancellationToken ct) => throw new NotSupportedException();
        public Task<ReelBolt.Inference.Api.Services.Billing.Reconcile.ReconcileResult> ReconcileHeldAsync(Guid organizationId, ReelBolt.Inference.Api.Services.Billing.Reconcile.ReconcileTrigger trigger, bool force, CancellationToken ct) => throw new NotSupportedException();
        public Task RecordChangeRequestedAsync(Guid organizationId, string changeKind, Guid? userId, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void The_parser_reads_the_whole_totals_block_and_a_missing_member_stays_null()
    {
        BillingEvent full = Parse(Transaction("evt_1", Ago(1), "txn_1", Totals(7900, 7900, 0, 0, 0)));
        full.SubtotalMinor.Should().Be(7900);
        full.DiscountMinor.Should().Be(7900);
        full.TaxMinor.Should().Be(0);
        full.TotalMinor.Should().Be(0);
        full.EarningsMinor.Should().Be(0);
        full.AmountMinor.Should().Be(0);
        full.IsPaidTransaction.Should().BeTrue();

        BillingEvent sparse = Parse(Transaction("evt_2", Ago(1), "txn_2", """{"grand_total":"2900","currency_code":"USD"}"""));
        sparse.AmountMinor.Should().Be(2900);
        sparse.SubtotalMinor.Should().BeNull();
        sparse.DiscountMinor.Should().BeNull();
        sparse.TaxMinor.Should().BeNull();
        sparse.TotalMinor.Should().BeNull();
        sparse.EarningsMinor.Should().BeNull();
    }

    [Fact]
    public async Task A_fully_discounted_subscription_is_recorded_with_zeros_and_contributes_nothing()
    {
        (await Process(Transaction("evt_1", Ago(2), "txn_1", Totals(7900, 7900, 0, 0, 0)))).Should().Be(BillingEventOutcome.Applied);

        await using (InferenceApiDbContext db = NewDb())
        {
            BillingPayment row = await db.BillingPayments.AsNoTracking().SingleAsync();
            (row.AmountMinor, row.SubtotalMinor, row.DiscountMinor, row.TaxMinor, row.TotalMinor, row.EarningsMinor)
                .Should().Be((0L, 7900L, 7900L, 0L, 0L, 0L));
        }

        BillingAdminRevenue revenue = await Revenue();
        revenue.ListPriceMonthlyUsd.Should().Be(79m);
        revenue.CommittedMonthlyUsd.Should().Be(0m);
        revenue.WindowCommittedUsd.Should().Be(0m);
        revenue.PayingSubscriptions.Should().Be(1);
        revenue.FullyDiscountedSubscriptions.Should().Be(1);
        revenue.DiscountedSubscriptions.Should().Be(1);
        revenue.SubscriptionsWithoutPayment.Should().Be(0);
        BillingCollectedCurrency usd = revenue.Collected!.Single();
        (usd.CurrencyCode, usd.Payments, usd.ZeroAmountPayments, usd.GrossMinor, usd.NetMinor).Should().Be(("USD", 1, 1, 0L, 0L));
        revenue.CollectedNetUsd.Should().Be(0m);
    }

    [Fact]
    public async Task A_partial_discount_commits_the_charged_fraction()
    {
        await Process(Transaction("evt_1", Ago(2), "txn_1", Totals(7900, 3950, 0, 3950, 3950)));

        BillingAdminRevenue revenue = await Revenue();
        revenue.CommittedMonthlyUsd.Should().Be(39.5m);
        revenue.ListPriceMonthlyUsd.Should().Be(79m);
        revenue.DiscountedSubscriptions.Should().Be(1);
        revenue.FullyDiscountedSubscriptions.Should().Be(0);
        revenue.Collected!.Single().GrossMinor.Should().Be(3950);
        revenue.Collected!.Single().DiscountMinor.Should().Be(3950);
    }

    [Fact]
    public async Task A_renewal_at_full_price_after_a_once_only_discount_commits_the_list_price_again()
    {
        await Process(Transaction("evt_1", Ago(10), "txn_1", Totals(7900, 7900, 0, 0, 0)));
        await Process(Transaction("evt_2", Ago(1), "txn_2", Totals(7900, 0, 0, 7900, 7900)));

        BillingAdminRevenue revenue = await Revenue();
        revenue.CommittedMonthlyUsd.Should().Be(79m);
        revenue.DiscountedSubscriptions.Should().Be(0);
        revenue.FullyDiscountedSubscriptions.Should().Be(0);
        BillingCollectedCurrency usd = revenue.Collected!.Single();
        (usd.Payments, usd.ZeroAmountPayments, usd.GrossMinor, usd.SubscriptionGrossMinor, usd.TopUpGrossMinor).Should().Be((2, 1, 7900L, 7900L, 0L));
    }

    [Fact]
    public async Task A_subscription_with_no_recorded_payment_counts_at_list_price_and_is_counted()
    {
        BillingAdminRevenue revenue = await Revenue();
        revenue.CommittedMonthlyUsd.Should().Be(79m);
        revenue.SubscriptionsWithoutPayment.Should().Be(1);
        revenue.Collected.Should().BeEmpty();
        revenue.CollectedNetUsd.Should().BeNull();
    }

    [Fact]
    public async Task A_refund_subtracts_and_a_replayed_adjustment_does_not_subtract_twice()
    {
        await Process(Transaction("evt_1", Ago(3), "txn_1", Totals(7900, 0, 1738, 9638, 9638, fee: 500)));
        await Process(Adjustment("evt_r1", Ago(2), "adj_1", "txn_1", total: 2000, tax: 360));
        await Process(Adjustment("evt_r2", Ago(2), "adj_1", "txn_1", total: 2000, tax: 360));

        await using (InferenceApiDbContext db = NewDb())
        {
            (await db.BillingRefunds.CountAsync()).Should().Be(1);
        }

        BillingCollectedCurrency usd = (await Revenue()).Collected!.Single();
        usd.GrossMinor.Should().Be(9638);
        usd.TaxMinor.Should().Be(1738);
        usd.FeeMinor.Should().Be(500);
        usd.Refunds.Should().Be(1);
        usd.RefundedMinor.Should().Be(2000);
        usd.NetMinor.Should().Be(9638 - 1738 - (2000 - 360));
    }

    [Fact]
    public async Task A_replayed_or_renumbered_transaction_is_counted_once()
    {
        string body = Transaction("evt_1", Ago(2), "txn_1", Totals(7900, 0, 0, 7900, 7900));
        await Process(body);
        await Process(body);
        await Process(Transaction("evt_other", Ago(2), "txn_1", Totals(7900, 0, 0, 7900, 7900)));

        (await Revenue()).Collected!.Single().Payments.Should().Be(1);
    }

    [Fact]
    public async Task Two_currencies_are_never_summed()
    {
        await Process(Transaction("evt_1", Ago(2), "txn_1", Totals(7900, 0, 0, 7900, 7900)));
        await Process(Transaction("evt_2", Ago(2), "txn_2", Totals(5000, 0, 0, 5000, 5000, "EUR"), currency: "EUR", subscription: null));
        await Process(Transaction("evt_3", Ago(2), "txn_3", Totals(100000, 0, 0, 100000, 100000, "JPY"), currency: "JPY", subscription: null));

        BillingAdminRevenue revenue = await Revenue();
        revenue.Collected!.Select(c => c.CurrencyCode).Should().Equal("EUR", "JPY", "USD");
        revenue.Collected!.Single(c => c.CurrencyCode == "EUR").GrossMinor.Should().Be(5000);
        revenue.Collected!.Single(c => c.CurrencyCode == "EUR").TopUpGrossMinor.Should().Be(5000);
        revenue.Collected!.Single(c => c.CurrencyCode == "JPY").MinorUnitExponent.Should().Be(0);
        revenue.Collected!.Single(c => c.CurrencyCode == "USD").GrossMinor.Should().Be(7900);
        revenue.CollectedNetUsd.Should().Be(79m);
        revenue.OtherCurrencyPayments.Should().Be(2);
    }

    [Fact]
    public async Task Tax_is_pro_rata_when_a_provider_credit_covered_part_of_the_total()
    {
        await Process(Transaction("evt_1", Ago(2), "txn_1", Totals(8333, 0, 1667, 10000, 5000)));

        BillingCollectedCurrency usd = (await Revenue()).Collected!.Single();
        usd.GrossMinor.Should().Be(5000);
        usd.TaxMinor.Should().Be(834);
        usd.NetMinor.Should().Be(5000 - 834);
    }

    [Fact]
    public async Task A_payment_that_states_no_tax_is_counted_as_unknown_tax()
    {
        await Process(Transaction("evt_1", Ago(2), "txn_1", """{"grand_total":"2900","currency_code":"USD"}"""));

        BillingCollectedCurrency usd = (await Revenue()).Collected!.Single();
        usd.PaymentsWithUnknownTax.Should().Be(1);
        usd.FeeMinor.Should().BeNull();
        usd.NetMinor.Should().Be(2900);
    }
}
