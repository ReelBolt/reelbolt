#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Migrations;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Billing;

/// <summary>
/// billing-paid-amounts T-002: the one-shot <c>billing_payments</c> backfill against a real PostgreSQL.
/// Each test migrates a database of its own, inserts synthetic <c>billing_webhook_events</c> rows, runs
/// <see cref="BillingPaymentsMigrationSql.Backfill"/> and asserts on the promoted rows. Env-gated like
/// every Postgres test (<see cref="PostgresTestEnvironment"/>): without <c>REELBOLT_TEST_PG</c> (or
/// <c>REELBOLT_TEST_DATABASE_URL</c>) the tests skip rather than pass.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public sealed class BillingPaymentsBackfillPostgresTests
{
    /// <summary>One migrated throwaway database, dropped when disposed.</summary>
    private sealed class Pg : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _admin;
        private readonly string _name;

        private Pg(NpgsqlDataSource admin, string name, DbContextOptions<InferenceApiDbContext> options)
        {
            _admin = admin;
            _name = name;
            Options = options;
        }

        public DbContextOptions<InferenceApiDbContext> Options { get; }

        public static async Task<Pg?> CreateAsync()
        {
            string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
            if (string.IsNullOrWhiteSpace(baseConnection))
            {
                return null;
            }

            string name = "reelbolt_backfill_test_" + Guid.NewGuid().ToString("N")[..12];
            NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {name}"))
            {
                await create.ExecuteNonQueryAsync();
            }

            Pg pg = new(
                admin,
                name,
                new DbContextOptionsBuilder<InferenceApiDbContext>()
                    .UseNpgsql(PostgresTestEnvironment.ForDatabase(baseConnection, name))
                    .Options);
            await using InferenceApiDbContext setup = pg.NewDb();
            await setup.GetService<IMigrator>().MigrateAsync();
            return pg;
        }

        public InferenceApiDbContext NewDb() => new(Options);

        /// <summary>Stores one retained webhook payload, exactly as the processor writes it.</summary>
        public async Task AddEventAsync(string provider, string eventId, string eventType, string payloadJson, string? outcome = null)
        {
            await using InferenceApiDbContext db = NewDb();
            db.BillingWebhookEvents.Add(new BillingWebhookEvent
            {
                Id = Guid.NewGuid(),
                Provider = provider,
                EventId = eventId,
                EventType = eventType,
                PayloadJson = payloadJson,
                ReceivedAt = DateTime.UtcNow,
                Outcome = outcome,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>An organization our own records hold: the only way a payload's claim can corroborate.</summary>
        public async Task<Guid> AddOrganizationAsync()
        {
            await using InferenceApiDbContext db = NewDb();
            Guid id = Guid.NewGuid();
            db.Organizations.Add(new Organization { Id = id, Name = "o", Slug = "o-" + id.ToString("N"), Kind = OrganizationKind.Team });
            await db.SaveChangesAsync();
            return id;
        }

        /// <summary>A plan row, because a subscription has a foreign key to one.</summary>
        public async Task<Guid> AddPlanAsync()
        {
            await using InferenceApiDbContext db = NewDb();
            Guid id = Guid.NewGuid();
            db.Plans.Add(new Plan
            {
                Id = id,
                Key = "plan-" + id.ToString("N")[..8],
                Name = "Creator",
                Kind = PlanKind.Subscription,
                PriceMonthlyUsd = 29,
                MonthlyCreditsMillis = 1_000_000,
                TopUpAllowed = true,
                SortOrder = 1,
                ProviderPriceIdsJson = """{"paddle":{"monthly":"pri_creator"}}""",
            });
            await db.SaveChangesAsync();
            return id;
        }

        public async Task AddSubscriptionAsync(string provider, string providerSubscriptionId, Guid organizationId, Guid planId)
        {
            await using InferenceApiDbContext db = NewDb();
            db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                PlanId = planId,
                Provider = provider,
                ProviderSubscriptionId = providerSubscriptionId,
                Status = SubscriptionStatus.Active,
            });
            await db.SaveChangesAsync();
        }

        public async Task AddBillingCustomerAsync(string provider, string providerCustomerId, Guid organizationId)
        {
            await using InferenceApiDbContext db = NewDb();
            db.BillingCustomers.Add(new BillingCustomer
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                Provider = provider,
                ProviderCustomerId = providerCustomerId,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>A row the webhook processor already wrote (a deploy that raced a live webhook).</summary>
        public async Task InsertPaymentAsync(string provider, string transactionId, long amountMinor)
        {
            await using InferenceApiDbContext db = NewDb();
            db.BillingPayments.Add(new BillingPayment
            {
                Id = Guid.NewGuid(),
                Provider = provider,
                ProviderTransactionId = transactionId,
                AmountMinor = amountMinor,
                OccurredAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>Runs the backfill exactly as the migration does.</summary>
        public async Task RunBackfillAsync()
        {
            await using InferenceApiDbContext db = NewDb();
            // The migration sends this through migrationBuilder.Sql, which emits the statement verbatim.
            // EF's ExecuteSqlRaw would instead read the jsonb path braces ('{data,id}') as format
            // placeholders and refuse to parse, so the statement goes to the context's own connection.
            NpgsqlConnection connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(BillingPaymentsMigrationSql.Backfill, connection);
            await command.ExecuteNonQueryAsync();
        }

        /// <summary>Runs one statement verbatim, exactly as <c>migrationBuilder.Sql</c> emits it.</summary>
        public async Task RunSqlAsync(string sql)
        {
            await using InferenceApiDbContext db = NewDb();
            NpgsqlConnection connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using NpgsqlCommand command = new(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
            await using (NpgsqlCommand drop = _admin.CreateCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)"))
            {
                await drop.ExecuteNonQueryAsync();
            }

            await _admin.DisposeAsync();
        }
    }

    private static string JsonStringOrNull(string? value) => value is null ? "null" : $"\"{value}\"";

    /// <summary>
    /// The real <c>transaction.completed</c> payload read in production, verbatim, with only the
    /// organization id and the ids substituted. With no <paramref name="subscriptionId"/> the payload's
    /// <c>subscription_id</c> is the literal <c>null</c> it carries in production.
    /// </summary>
    private static string ProductionPayload(Guid organization, string eventId = "evt_1", string transactionId = "txn_1", string? subscriptionId = null) => $$$"""
        {"event_id":"{{{eventId}}}","event_type":"transaction.completed","occurred_at":"2026-10-17T09:12:33.000000Z","data":{"id":"{{{transactionId}}}","subscription_id":{{{JsonStringOrNull(subscriptionId)}}},"currency_code":"USD","custom_data":{"organization_id":"{{{organization}}}"},"details":{"totals":{"grand_total":"1450","currency_code":"USD","fee":"123","earnings":"1065","tax":"262","subtotal":"2377","discount":"1189"}},"payments":[{"amount":"1450","status":"captured"}]}}
        """;

    [PostgresFact]
    public async Task Backfill_promotes_the_amount_currency_and_fee_from_the_stored_payload()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid organization = await pg.AddOrganizationAsync();
        await pg.AddEventAsync("paddle", "evt_1", "transaction.completed", ProductionPayload(organization));

        await pg.RunBackfillAsync();

        await using InferenceApiDbContext db = pg.NewDb();
        BillingPayment row = await db.BillingPayments.AsNoTracking().SingleAsync();
        row.Provider.Should().Be("paddle");
        row.ProviderTransactionId.Should().Be("txn_1");
        row.ProviderSubscriptionId.Should().BeNull("subscription_id was null in the payload");
        row.OrganizationId.Should().Be(organization, "the payload named an organization our own records hold");
        row.AmountMinor.Should().Be(1450, "grand_total 1450, in minor units, exactly as the provider sent it");
        row.CurrencyCode.Should().Be("USD");
        row.FeeMinor.Should().Be(123, "the Paddle fee, in minor units");
        row.OccurredAt.Should().Be(new DateTime(2026, 10, 17, 9, 12, 33, DateTimeKind.Utc), "occurred_at comes from the payload, not the delivery");
    }

    [PostgresFact]
    public async Task Backfill_is_idempotent_and_never_overwrites_a_row_the_webhook_already_wrote()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid organization = await pg.AddOrganizationAsync();
        await pg.AddEventAsync("paddle", "evt_1", "transaction.completed", ProductionPayload(organization, transactionId: "txn_backfilled"));
        // A deploy that raced a live webhook: the row for txn_already_written exists before the backfill runs.
        await pg.InsertPaymentAsync("paddle", "txn_already_written", 9999);
        await pg.AddEventAsync("paddle", "evt_2", "transaction.completed", ProductionPayload(organization, eventId: "evt_2", transactionId: "txn_already_written"));

        await pg.RunBackfillAsync();
        await pg.RunBackfillAsync();

        await using InferenceApiDbContext db = pg.NewDb();
        List<BillingPayment> rows = await db.BillingPayments.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2, "a second run adds nothing, and the already-recorded row is not duplicated");
        rows.Single(r => r.ProviderTransactionId == "txn_backfilled").AmountMinor.Should().Be(1450);
        rows.Single(r => r.ProviderTransactionId == "txn_already_written").AmountMinor.Should().Be(9999,
            "ON CONFLICT DO NOTHING, never DO UPDATE: the webhook's own row wins");
    }

    [PostgresFact]
    public async Task Backfill_skips_a_payload_with_no_parseable_amount_and_non_completed_events()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        // A transaction.completed payload with no details.totals at all.
        await pg.AddEventAsync("paddle", "evt_no_totals", "transaction.completed",
            """{"event_id":"evt_no_totals","event_type":"transaction.completed","data":{"id":"txn_no_totals","currency_code":"USD"}}""");
        // A transaction.completed payload whose grand_total is present but not a number.
        await pg.AddEventAsync("paddle", "evt_bad_amount", "transaction.completed",
            """{"event_id":"evt_bad_amount","event_type":"transaction.completed","data":{"id":"txn_bad_amount","details":{"totals":{"grand_total":"not-a-number","currency_code":"USD","fee":"123"}}}}""");
        // A non-completed event that does carry a totals block: only transaction.completed is promoted.
        await pg.AddEventAsync("paddle", "evt_updated", "transaction.updated",
            """{"event_id":"evt_updated","event_type":"transaction.updated","data":{"id":"txn_updated","details":{"totals":{"grand_total":"1450","currency_code":"USD","fee":"123"}}}}""");

        await pg.RunBackfillAsync();

        await using InferenceApiDbContext db = pg.NewDb();
        (await db.BillingPayments.CountAsync()).Should().Be(0,
            "a payload with no parseable grand_total is skipped, never recorded as 0, and a non-completed event yields no row");
    }

    /// <summary>
    /// The single most important property: a payload of another shape must never abort the migration, and
    /// with it the API's startup. Every cast is regex-guarded and each row is inserted under its own
    /// exception handler, so a hostile value is skipped (or its field left null) rather than thrown.
    /// </summary>
    [PostgresFact]
    public async Task Backfill_never_aborts_on_a_payload_of_another_shape()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid organization = await pg.AddOrganizationAsync();
        // Parseable amount, but every other promoted value is of another shape: a non-uuid organization,
        // a non-numeric fee, and an occurred_at that is the right shape yet not a real calendar date.
        await pg.AddEventAsync("paddle", "evt_hostile", "transaction.completed",
            """{"event_id":"evt_hostile","event_type":"transaction.completed","occurred_at":"2026-02-31T00:00:00Z","data":{"id":"txn_hostile","currency_code":"USD","custom_data":{"organization_id":"not-a-uuid"},"details":{"totals":{"grand_total":"1450","currency_code":"USD","fee":"not-a-number"}}}}""");
        // A grand_total of the right shape but far beyond what a bigint can hold.
        await pg.AddEventAsync("paddle", "evt_overflow", "transaction.completed",
            """{"event_id":"evt_overflow","event_type":"transaction.completed","data":{"id":"txn_overflow","details":{"totals":{"grand_total":"999999999999999999999999999999","currency_code":"USD"}}}}""");
        await pg.AddEventAsync("paddle", "evt_clean", "transaction.completed", ProductionPayload(organization, eventId: "evt_clean", transactionId: "txn_clean"));

        Func<Task> run = pg.RunBackfillAsync;
        await run.Should().NotThrowAsync("a payload of another shape must never abort the migration");

        await using InferenceApiDbContext db = pg.NewDb();
        List<BillingPayment> rows = await db.BillingPayments.AsNoTracking().ToListAsync();
        rows.Should().HaveCount(2, "the hostile-but-parseable row is kept; the overflowing amount is skipped, never recorded as 0");
        BillingPayment hostile = rows.Single(r => r.ProviderTransactionId == "txn_hostile");
        hostile.AmountMinor.Should().Be(1450);
        hostile.OrganizationId.Should().BeNull("the payload's organization was not a uuid, so the row stays unattributed");
        hostile.FeeMinor.Should().BeNull("the payload's fee was not a number, so no fee is claimed");
        rows.Should().NotContain(r => r.ProviderTransactionId == "txn_overflow");
    }

    /// <summary>
    /// The doc comment's other skip shape: a payload whose promoted values are the right shape but too
    /// long for their columns. <c>currency_code</c> is <c>character varying(10)</c> and
    /// <c>provider_transaction_id</c> is <c>character varying(200)</c>, so the INSERT raises
    /// <c>string_data_right_truncation</c>; the per-row handler catches it and the row is skipped whole
    /// rather than aborting the migration. A clean payload is included so the test shows the statement
    /// ran, not that it silently did nothing.
    /// </summary>
    [PostgresFact]
    public async Task Backfill_skips_a_payload_whose_promoted_values_do_not_fit_their_columns()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid organization = await pg.AddOrganizationAsync();
        string overLongCurrency = new('C', 11);       // the column is character varying(10)
        string overLongTransactionId = new('t', 201); // the column is character varying(200)
        // A parseable amount, but a currency_code one character past what its column can hold.
        await pg.AddEventAsync("paddle", "evt_long_currency", "transaction.completed",
            """{"event_id":"evt_long_currency","event_type":"transaction.completed","data":{"id":"txn_long_currency","currency_code":"__CURRENCY__","custom_data":{"organization_id":"__ORG__"},"details":{"totals":{"grand_total":"1450","currency_code":"__CURRENCY__","fee":"123"}}}}"""
                .Replace("__CURRENCY__", overLongCurrency)
                .Replace("__ORG__", organization.ToString()));
        // A parseable amount, but a transaction id one character past what its column can hold.
        await pg.AddEventAsync("paddle", "evt_long_txn", "transaction.completed",
            """{"event_id":"evt_long_txn","event_type":"transaction.completed","data":{"id":"__TXN__","currency_code":"USD","details":{"totals":{"grand_total":"1450","currency_code":"USD","fee":"123"}}}}"""
                .Replace("__TXN__", overLongTransactionId));
        await pg.AddEventAsync("paddle", "evt_clean", "transaction.completed", ProductionPayload(organization, eventId: "evt_clean", transactionId: "txn_clean"));

        Func<Task> run = pg.RunBackfillAsync;
        await run.Should().NotThrowAsync("a promoted value too long for its column must never abort the migration");

        await using InferenceApiDbContext db = pg.NewDb();
        List<BillingPayment> rows = await db.BillingPayments.AsNoTracking().ToListAsync();
        rows.Should().ContainSingle("each over-long payload is skipped whole, never truncated and never recorded; only the clean payload is promoted");
        rows[0].ProviderTransactionId.Should().Be("txn_clean");
    }

    // ------------------------------------------------------------------ attribution is corroborated

    [PostgresFact]
    public async Task Backfill_prefers_the_subscriptions_organization_over_the_payloads_claim()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid subscriptionOrganization = await pg.AddOrganizationAsync();
        Guid claimedOrganization = await pg.AddOrganizationAsync();
        Guid plan = await pg.AddPlanAsync();
        await pg.AddSubscriptionAsync("paddle", "sub_1", subscriptionOrganization, plan);
        // The payload names a real but different organization: our own subscription row must win.
        await pg.AddEventAsync("paddle", "evt_1", "transaction.completed", ProductionPayload(claimedOrganization, subscriptionId: "sub_1"));

        await pg.RunBackfillAsync();

        await using InferenceApiDbContext db = pg.NewDb();
        BillingPayment row = await db.BillingPayments.AsNoTracking().SingleAsync();
        row.OrganizationId.Should().Be(subscriptionOrganization, "the matching subscriptions row is the corroborated attribution");
        row.ProviderSubscriptionId.Should().Be("sub_1", "the pair comes from the same data.subscription_id the join used, so it can never disagree");
        row.AmountMinor.Should().Be(1450);
    }

    [PostgresFact]
    public async Task Backfill_leaves_the_organization_null_when_nothing_corroborates_it()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        // A well-formed organization id that no organizations row holds, and no matching subscription.
        await pg.AddEventAsync("paddle", "evt_1", "transaction.completed", ProductionPayload(Guid.NewGuid()));

        await pg.RunBackfillAsync();

        await using InferenceApiDbContext db = pg.NewDb();
        BillingPayment row = await db.BillingPayments.AsNoTracking().SingleAsync();
        row.OrganizationId.Should().BeNull("an uncorroborated claim is never trusted, so the row stays unattributed");
        row.AmountMinor.Should().Be(1450, "the money was still taken: it is recorded unattributed, never dropped");
    }

    [PostgresFact]
    public async Task Backfill_resolves_the_organization_from_the_billing_customer_when_the_payload_names_none()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid organization = await pg.AddOrganizationAsync();
        await pg.AddBillingCustomerAsync("paddle", "ctm_1", organization);
        // No custom_data.organization_id and no subscription: the billing customer is the last resort.
        await pg.AddEventAsync("paddle", "evt_1", "transaction.completed",
            """{"event_id":"evt_1","event_type":"transaction.completed","data":{"id":"txn_1","customer_id":"ctm_1","currency_code":"USD","details":{"totals":{"grand_total":"1450","currency_code":"USD","fee":"123"}}}}""");

        await pg.RunBackfillAsync();

        await using InferenceApiDbContext db = pg.NewDb();
        BillingPayment row = await db.BillingPayments.AsNoTracking().SingleAsync();
        row.OrganizationId.Should().Be(organization, "the matching billing_customers row is the last corroborated resort");
        row.AmountMinor.Should().Be(1450);
    }

    [PostgresFact]
    public async Task Backfill_records_a_payment_the_runtime_did_not_apply()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid organization = await pg.AddOrganizationAsync();
        // The processor recorded the event but could not act on it; Paddle captured the money regardless.
        await pg.AddEventAsync("paddle", "evt_1", "transaction.completed", ProductionPayload(organization), outcome: "Unresolved");

        await pg.RunBackfillAsync();

        await using InferenceApiDbContext db = pg.NewDb();
        BillingPayment row = await db.BillingPayments.AsNoTracking().SingleAsync();
        row.AmountMinor.Should().Be(1450, "every completed transaction with a stated amount is recorded, whatever the processor did with it");
        row.OrganizationId.Should().Be(organization);
    }

    [PostgresFact]
    public async Task Backfill_leaves_out_a_completed_payload_with_no_transaction_id()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        // A parseable amount but no data.id: the row cannot be keyed, so it is left out (and counted).
        await pg.AddEventAsync("paddle", "evt_1", "transaction.completed",
            """{"event_id":"evt_1","event_type":"transaction.completed","data":{"currency_code":"USD","details":{"totals":{"grand_total":"1450","currency_code":"USD","fee":"123"}}}}""");

        Func<Task> run = pg.RunBackfillAsync;
        await run.Should().NotThrowAsync("a payload without a transaction id must not abort the migration");

        await using InferenceApiDbContext db = pg.NewDb();
        (await db.BillingPayments.CountAsync()).Should().Be(0, "a row cannot be keyed without a transaction id, so none is written");
    }

    [PostgresFact]
    public async Task Backfill_skips_an_amount_that_cannot_be_a_charge()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        // 30 digits: of the right shape, far beyond what a bigint can hold, so it cannot be a real charge.
        await pg.AddEventAsync("paddle", "evt_1", "transaction.completed",
            """{"event_id":"evt_1","event_type":"transaction.completed","data":{"id":"txn_1","currency_code":"USD","details":{"totals":{"grand_total":"999999999999999999999999999999","currency_code":"USD","fee":"123"}}}}""");

        await pg.RunBackfillAsync();

        await using InferenceApiDbContext db = pg.NewDb();
        (await db.BillingPayments.CountAsync()).Should().Be(0, "an amount that cannot be a charge is skipped, never recorded and never a 0 row");
    }

    [PostgresFact]
    public async Task Totals_backfill_fills_a_recorded_payment_from_its_payload_and_leaves_unsent_members_null()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        await pg.InsertPaymentAsync("paddle", "txn_free", 0);
        await pg.InsertPaymentAsync("paddle", "txn_sparse", 2900);
        await pg.AddEventAsync("paddle", "evt_free", "transaction.completed",
            """{"event_id":"evt_free","data":{"id":"txn_free","details":{"totals":{"subtotal":"7900","discount":"7900","tax":"0","total":"0","grand_total":"0","earnings":"0"}}}}""");
        await pg.AddEventAsync("paddle", "evt_sparse", "transaction.completed",
            """{"event_id":"evt_sparse","data":{"id":"txn_sparse","details":{"totals":{"grand_total":"2900","subtotal":"abc"}}}}""");

        await pg.RunSqlAsync(BillingRevenueDiscountMigrationSql.BackfillPaymentTotals);
        await pg.RunSqlAsync(BillingRevenueDiscountMigrationSql.BackfillPaymentTotals);

        await using InferenceApiDbContext db = pg.NewDb();
        BillingPayment free = await db.BillingPayments.AsNoTracking().SingleAsync(p => p.ProviderTransactionId == "txn_free");
        (free.SubtotalMinor, free.DiscountMinor, free.TaxMinor, free.TotalMinor, free.EarningsMinor).Should().Be((7900L, 7900L, 0L, 0L, 0L));
        BillingPayment sparse = await db.BillingPayments.AsNoTracking().SingleAsync(p => p.ProviderTransactionId == "txn_sparse");
        (sparse.SubtotalMinor, sparse.DiscountMinor, sparse.TaxMinor, sparse.TotalMinor, sparse.EarningsMinor).Should().Be((null, null, null, null, null));
    }

    [PostgresFact]
    public async Task Refund_backfill_promotes_approved_adjustments_once_and_skips_the_rest()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        const string approved = """{"event_id":"E","occurred_at":"2026-10-17T09:12:33.000000Z","data":{"id":"adj_1","action":"refund","status":"approved","transaction_id":"txn_1","currency_code":"USD","totals":{"total":"2000","tax":"360"}}}""";
        await pg.AddEventAsync("paddle", "evt_a", "adjustment.created", approved.Replace("\"E\"", "\"evt_a\""));
        await pg.AddEventAsync("paddle", "evt_b", "adjustment.updated", approved.Replace("\"E\"", "\"evt_b\""));
        await pg.AddEventAsync("paddle", "evt_p", "adjustment.created", approved.Replace("adj_1", "adj_2").Replace("approved", "pending_approval"));
        await pg.AddEventAsync("paddle", "evt_c", "adjustment.created", approved.Replace("adj_1", "adj_3").Replace("\"refund\"", "\"credit\""));

        await pg.RunSqlAsync(BillingRevenueDiscountMigrationSql.BackfillRefunds);
        await pg.RunSqlAsync(BillingRevenueDiscountMigrationSql.BackfillRefunds);

        await using InferenceApiDbContext db = pg.NewDb();
        BillingRefund row = await db.BillingRefunds.AsNoTracking().SingleAsync();
        (row.ProviderAdjustmentId, row.ProviderTransactionId, row.AmountMinor, row.TaxMinor, row.CurrencyCode)
            .Should().Be(("adj_1", "txn_1", 2000L, 360L, "USD"));
        row.OccurredAt.Should().Be(new DateTime(2026, 10, 17, 9, 12, 33, DateTimeKind.Utc));
    }

    private static string StripeInvoice(string id, string reason, long paid, long subtotal, long discount, Guid? organization, string subscription = "sub_1") => $$$"""
        {"id":"evt_{{{id}}}","type":"invoice.paid","created":1792000000,"data":{"object":{"id":"{{{id}}}","object":"invoice","billing_reason":"{{{reason}}}","amount_paid":{{{paid}}},"currency":"usd","customer":"cus_1",
         "subtotal":{{{subtotal}}},"total":{{{subtotal - discount}}},"total_excluding_tax":{{{subtotal - discount}}},"total_discount_amounts":[{"amount":{{{discount}}}}],
         "parent":{"type":"subscription_details","subscription_details":{"subscription":"{{{subscription}}}","metadata":{"organization_id":"{{{organization}}}"}
         }},"lines":{"data":[]}}
         }}
        """;

    [PostgresFact]
    public async Task Stripe_backfill_fills_totals_inserts_the_missing_zero_invoices_once_and_attributes_the_organization()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        Guid organization = await pg.AddOrganizationAsync();
        Guid plan = await pg.AddPlanAsync();
        await pg.AddSubscriptionAsync("stripe", "sub_1", organization, plan);

        // A recorded paying invoice whose totals predate the columns; a never-recorded 100%-off first invoice and renewal;
        // an invoice of a reason the processor does not act on; one claiming an organization that does not exist.
        await pg.InsertPaymentAsync("stripe", "in_paid", 2900);
        await pg.AddEventAsync("stripe", "evt_in_paid", "invoice.paid", StripeInvoice("in_paid", "subscription_cycle", 2900, 2900, 0, organization));
        await pg.AddEventAsync("stripe", "evt_in_zero1", "invoice.paid", StripeInvoice("in_zero1", "subscription_create", 0, 2900, 2900, organization));
        await pg.AddEventAsync("stripe", "evt_in_zero2", "invoice.paid", StripeInvoice("in_zero2", "subscription_cycle", 0, 2900, 2900, organization));
        await pg.AddEventAsync("stripe", "evt_in_manual", "invoice.paid", StripeInvoice("in_manual", "manual", 0, 2900, 2900, organization));
        await pg.AddEventAsync("stripe", "evt_in_ghost", "invoice.paid", StripeInvoice("in_ghost", "subscription_cycle", 0, 2900, 2900, Guid.NewGuid(), subscription: "sub_unknown"));

        await pg.RunSqlAsync(StripeRevenueBackfillMigrationSql.FillTotals);
        await pg.RunSqlAsync(StripeRevenueBackfillMigrationSql.InsertMissingInvoices);
        await pg.RunSqlAsync(StripeRevenueBackfillMigrationSql.FillTotals);
        await pg.RunSqlAsync(StripeRevenueBackfillMigrationSql.InsertMissingInvoices);

        await using InferenceApiDbContext db = pg.NewDb();
        List<BillingPayment> rows = await db.BillingPayments.AsNoTracking().OrderBy(p => p.ProviderTransactionId).ToListAsync();
        rows.Select(r => r.ProviderTransactionId).Should().Equal("in_ghost", "in_paid", "in_zero1", "in_zero2");

        BillingPayment paid = rows.Single(r => r.ProviderTransactionId == "in_paid");
        (paid.AmountMinor, paid.SubtotalMinor, paid.DiscountMinor, paid.TaxMinor, paid.TotalMinor).Should().Be((2900L, 2900L, 0L, 0L, 2900L));

        BillingPayment zero = rows.Single(r => r.ProviderTransactionId == "in_zero1");
        (zero.AmountMinor, zero.SubtotalMinor, zero.DiscountMinor, zero.TotalMinor, zero.CurrencyCode, zero.ProviderSubscriptionId, zero.OrganizationId)
            .Should().Be((0L, 2900L, 2900L, 0L, "USD", "sub_1", (Guid?)organization));
        zero.OccurredAt.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1792000000).UtcDateTime);

        rows.Single(r => r.ProviderTransactionId == "in_ghost").OrganizationId.Should().BeNull("a claimed organization that does not exist is never attributed");
    }

    [PostgresFact]
    public async Task Stripe_backfill_promotes_succeeded_refunds_and_lost_disputes_once()
    {
        await using Pg? pg = await Pg.CreateAsync();
        if (pg is null) return;

        await pg.AddEventAsync("stripe", "evt_r1", "refund.created", """{"id":"evt_r1","type":"refund.created","created":1792000000,"data":{"object":{"id":"re_1","status":"succeeded","payment_intent":"pi_1","amount":1450,"currency":"eur"}}}""");
        await pg.AddEventAsync("stripe", "evt_r2", "refund.updated", """{"id":"evt_r2","type":"refund.updated","created":1792000100,"data":{"object":{"id":"re_1","status":"succeeded","payment_intent":"pi_1","amount":1450,"currency":"eur"}}}""");
        await pg.AddEventAsync("stripe", "evt_r3", "refund.created", """{"id":"evt_r3","type":"refund.created","created":1792000000,"data":{"object":{"id":"re_2","status":"pending","payment_intent":"pi_1","amount":100,"currency":"eur"}}}""");
        await pg.AddEventAsync("stripe", "evt_d1", "charge.dispute.closed", """{"id":"evt_d1","type":"charge.dispute.closed","created":1792000200,"data":{"object":{"id":"du_1","status":"lost","payment_intent":"pi_2","amount":2900,"currency":"usd"}}}""");
        await pg.AddEventAsync("stripe", "evt_d2", "charge.dispute.closed", """{"id":"evt_d2","type":"charge.dispute.closed","created":1792000200,"data":{"object":{"id":"du_2","status":"won","payment_intent":"pi_3","amount":900,"currency":"usd"}}}""");

        await pg.RunSqlAsync(StripeRevenueBackfillMigrationSql.BackfillRefunds);
        await pg.RunSqlAsync(StripeRevenueBackfillMigrationSql.BackfillRefunds);

        await using InferenceApiDbContext db = pg.NewDb();
        List<BillingRefund> rows = await db.BillingRefunds.AsNoTracking().OrderBy(r => r.ProviderAdjustmentId).ToListAsync();
        rows.Select(r => (r.ProviderAdjustmentId, r.ProviderTransactionId, r.AmountMinor, r.TaxMinor, r.CurrencyCode)).Should().Equal(
            ("du_1", "pi_2", 2900L, (long?)null, "USD"),
            ("re_1", "pi_1", 1450L, (long?)null, "EUR"));
    }
}
