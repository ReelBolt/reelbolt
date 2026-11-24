using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

/// <summary>
/// product-events-funnel T-002: the Inference API's one writer, <see cref="ProductEventLog"/>, against a
/// real PostgreSQL. The class exists for the atomicity of <c>ClaimAndAppendAsync</c> — a funnel claim and
/// its <c>product_events</c> row are one statement, so the two can never diverge, and the call that did
/// not win the claim writes no row at all. <c>AppendAsync</c> is the plain insert a consent decision
/// takes, and it moves no claim.
/// <para>
/// Follows <see cref="Data.ProductEventsSchemaTests"/>: a per-test database, <see cref="PostgresTestEnvironment"/>,
/// and <c>DROP DATABASE ... WITH (FORCE)</c> in <c>finally</c>.
/// </para>
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class ProductEventLogTests
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Org = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>Runs <paramref name="body"/> against a freshly migrated database, then drops it.</summary>
    private static async Task WithDatabase(Func<ProductEventLog, NpgsqlConnection, Task> body)
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_product_event_log_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            await using InferenceApiDbContext db = new(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options);
            await db.GetService<IMigrator>().MigrateAsync();

            await using NpgsqlConnection c = new(csb.ConnectionString);
            await c.OpenAsync();

            await body(new ProductEventLog(db), c);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource dropAdmin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = dropAdmin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> CountAsync(NpgsqlConnection c, string table)
    {
        await using NpgsqlCommand cmd = new($"SELECT count(*) FROM {table}", c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task ClaimAndAppend_returns_true_once_and_leaves_exactly_one_row()
    {
        await WithDatabase(async (log, c) =>
        {
            bool first = await log.ClaimAndAppendAsync(User, Org, "alpha.first_project", ProductEventSources.Client, CancellationToken.None);
            bool second = await log.ClaimAndAppendAsync(User, Org, "alpha.first_project", ProductEventSources.Client, CancellationToken.None);

            first.Should().BeTrue("the first call wins the claim");
            second.Should().BeFalse("the claim and its row are one statement, so the call that loses writes nothing");

            (await CountAsync(c, "product_events")).Should().Be(1L, "one won claim leaves exactly one row");
            (await CountAsync(c, "alpha_funnel_claims")).Should().Be(1L);
        });
    }

    [PostgresFact]
    public async Task ClaimAndAppend_writes_the_row_with_the_source_and_subject_it_was_given()
    {
        await WithDatabase(async (log, c) =>
        {
            await log.ClaimAndAppendAsync(User, Org, "alpha.first_run", ProductEventSources.Client, CancellationToken.None);

            await using NpgsqlCommand cmd = new(
                "SELECT user_id, organization_id, event, source, occurred_at FROM product_events", c);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
            (await r.ReadAsync()).Should().BeTrue();

            r.GetGuid(0).Should().Be(User);
            r.GetGuid(1).Should().Be(Org);
            r.GetString(2).Should().Be("alpha.first_run");
            r.GetString(3).Should().Be(ProductEventSources.Client);
            r.GetFieldValue<DateTime>(4).Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5),
                "occurred_at is stamped by the writer, not supplied by a caller");
        });
    }

    [PostgresFact]
    public async Task ClaimAndAppend_for_a_different_event_appends_a_second_row()
    {
        await WithDatabase(async (log, c) =>
        {
            (await log.ClaimAndAppendAsync(User, Org, "alpha.first_project", ProductEventSources.Client, CancellationToken.None))
                .Should().BeTrue();
            (await log.ClaimAndAppendAsync(User, Org, "alpha.first_run", ProductEventSources.Client, CancellationToken.None))
                .Should().BeTrue("a different event is a different claim");

            (await CountAsync(c, "product_events")).Should().Be(2L);
            (await CountAsync(c, "alpha_funnel_claims")).Should().Be(2L);
        });
    }

    [PostgresFact]
    public async Task ClaimAndAppend_with_no_organization_writes_a_person_only_row()
    {
        await WithDatabase(async (log, c) =>
        {
            (await log.ClaimAndAppendAsync(User, null, "alpha.first_project", ProductEventSources.Client, CancellationToken.None))
                .Should().BeTrue("a client event belongs to a person and may have no organization");

            await using NpgsqlCommand cmd = new("SELECT organization_id FROM product_events", c);
            (await cmd.ExecuteScalarAsync()).Should().Be(DBNull.Value);
        });
    }

    [PostgresFact]
    public async Task Append_writes_a_plain_consent_row_and_moves_no_claim()
    {
        await WithDatabase(async (log, c) =>
        {
            await log.AppendAsync(new ProductEvent
            {
                UserId = User,
                // A consent row is person-only by design: the controller passes no organization id.
                OrganizationId = null,
                Event = "telemetry.consent.declined",
                OccurredAt = DateTimeOffset.UtcNow,
                Source = ProductEventSources.Consent,
            }, CancellationToken.None);

            (await CountAsync(c, "product_events")).Should().Be(1L);
            (await CountAsync(c, "alpha_funnel_claims")).Should().Be(0L,
                "a consent decision is a recorded choice, not a funnel claim");

            await using NpgsqlCommand cmd = new("SELECT source, user_id, organization_id FROM product_events", c);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
            (await r.ReadAsync()).Should().BeTrue();
            r.GetString(0).Should().Be(ProductEventSources.Consent);
            r.GetGuid(1).Should().Be(User);
            r.IsDBNull(2).Should().BeTrue("a consent row carries no organization id");
        });
    }

    [PostgresFact]
    public async Task Append_records_every_decision_including_a_repeat()
    {
        await WithDatabase(async (log, c) =>
        {
            // Review Focus 2: grant -> decline -> grant leaves three rows; the reader takes the latest.
            foreach (string name in new[] { "telemetry.consent.granted", "telemetry.consent.declined", "telemetry.consent.granted" })
            {
                await log.AppendAsync(new ProductEvent
                {
                    UserId = User,
                    OrganizationId = null,
                    Event = name,
                    OccurredAt = DateTimeOffset.UtcNow,
                    Source = ProductEventSources.Consent,
                }, CancellationToken.None);
            }

            (await CountAsync(c, "product_events")).Should().Be(3L,
                "the table is append-only: a later decision is a later row");
        });
    }

    // ---- product-events-funnel T-004: the organization-activation claim (alpha.first_payment) ----------

    [PostgresFact]
    public async Task ClaimAndAppendFirstPayment_is_at_most_once_per_organization()
    {
        await WithDatabase(async (log, c) =>
        {
            bool first = await log.ClaimAndAppendFirstPaymentAsync(Org, User, CancellationToken.None);
            bool second = await log.ClaimAndAppendFirstPaymentAsync(Org, null, CancellationToken.None);

            first.Should().BeTrue("the first paid transaction activates the organization");
            second.Should().BeFalse(
                "activation is one row per organization, whatever person the second transaction resolves to");

            (await CountAsync(c, "product_events")).Should().Be(1L);
            (await CountAsync(c, "alpha_funnel_claims")).Should().Be(0L,
                "the activation claim is the product_events index, not the OTLP counter's claim table");
        });
    }

    [PostgresFact]
    public async Task ClaimAndAppendFirstPayment_writes_the_server_row_the_organization_names()
    {
        await WithDatabase(async (log, c) =>
        {
            await log.ClaimAndAppendFirstPaymentAsync(Org, User, CancellationToken.None);

            await using NpgsqlCommand cmd = new(
                "SELECT user_id, organization_id, event, source FROM product_events", c);
            await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
            (await r.ReadAsync()).Should().BeTrue();

            r.GetGuid(0).Should().Be(User);
            r.GetGuid(1).Should().Be(Org);
            r.GetString(2).Should().Be(ProductEventLog.FirstPaymentEvent);
            r.GetString(3).Should().Be(ProductEventSources.Server);
        });
    }

    [PostgresFact]
    public async Task ClaimAndAppendFirstPayment_with_no_person_writes_a_person_less_row()
    {
        await WithDatabase(async (log, c) =>
        {
            (await log.ClaimAndAppendFirstPaymentAsync(Org, null, CancellationToken.None))
                .Should().BeTrue("a payment belongs to an organization and may have no resolvable person");

            await using NpgsqlCommand cmd = new("SELECT user_id FROM product_events", c);
            (await cmd.ExecuteScalarAsync()).Should().Be(DBNull.Value);
        });
    }

    [PostgresFact]
    public async Task The_first_payment_index_is_unique_per_organization_and_partial()
    {
        await WithDatabase(async (log, c) =>
        {
            await using NpgsqlCommand index = new(
                "SELECT count(*) FROM pg_indexes WHERE tablename = 'product_events' "
                + "AND indexname = 'ux_product_events_first_payment_organization' "
                + "AND indexdef LIKE '%UNIQUE%' AND indexdef LIKE '%organization_id%' "
                + "AND indexdef LIKE '%alpha.first_payment%'", c);
            ((long)(await index.ExecuteScalarAsync())!).Should().Be(1L,
                "the activation index exists, is unique, and is partial on the first_payment event");

            await log.ClaimAndAppendFirstPaymentAsync(Org, User, CancellationToken.None);

            // A raw second activation row for the same organization is refused by the index, not a convention.
            async Task InsertRawAsync(string eventName)
            {
                await using NpgsqlCommand insert = new(
                    "INSERT INTO product_events (organization_id, event, occurred_at, source) "
                    + "VALUES (@org, @event, now(), 'server')", c);
                insert.Parameters.Add(new NpgsqlParameter("org", NpgsqlDbType.Uuid) { Value = Org });
                insert.Parameters.AddWithValue("event", eventName);
                await insert.ExecuteNonQueryAsync();
            }

            Func<Task> duplicate = () => InsertRawAsync(ProductEventLog.FirstPaymentEvent);
            (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505",
                "one organization cannot have two activation rows");

            // A different event for the same organization is NOT blocked: the index is partial.
            await InsertRawAsync("alpha.workspace_created");
            (await CountAsync(c, "product_events")).Should().Be(2L);
        });
    }

    // ---- the visit beacon's one statement (docs/design/new-user-funnel.md sections 5 and 6) ----

    private static readonly Guid Visitor = Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479");

    private static VisitorAttribution Touch(Guid visitor, string label, string? content = null, DateTimeOffset? at = null)
    {
        // ONE clock read for both stamps, as the controller does (it uses one "now"). Two reads can land
        // in different microseconds, which PostgreSQL keeps, and first_touch_at = created_at then fails.
        DateTimeOffset now = at ?? DateTimeOffset.UtcNow;
        return new VisitorAttribution
        {
            VisitorId = visitor,
            SourceLabel = label,
            UtmSource = label == "direct" ? null : label,
            UtmMedium = "community",
            UtmCampaign = "launch-2026-11",
            UtmContent = content,
            UtmTerm = null,
            ReferrerHost = "news.ycombinator.com",
            LandingPath = "/for/software-teams",
            FirstTouchAt = now,
            CreatedAt = now,
        };
    }

    private static ProductEvent Visit(Guid visitor, string name, string? detail, DateTimeOffset? at = null) => new()
    {
        VisitorId = visitor,
        Event = name,
        Detail = detail,
        Source = ProductEventSources.Client,
        OccurredAt = at ?? DateTimeOffset.UtcNow,
    };

    private static DateTimeOffset Window => DateTimeOffset.UtcNow.AddMinutes(-30);

    [PostgresFact]
    public async Task RecordVisit_writes_the_event_and_the_first_touch_in_one_statement()
    {
        await WithDatabase(async (log, c) =>
        {
            bool recorded = await log.RecordVisitAsync(
                Touch(Visitor, "hackernews", "hn-launch"), Visit(Visitor, "site.visit", "hackernews"), Window, CancellationToken.None);
            recorded.Should().BeTrue();

            await using (NpgsqlCommand cmd = new(
                "SELECT source_label, utm_source, utm_medium, utm_campaign, utm_content, utm_term, referrer_host, landing_path, "
                + "first_touch_at = created_at FROM visitor_attribution WHERE visitor_id = @v", c))
            {
                cmd.Parameters.AddWithValue("v", Visitor);
                await using NpgsqlDataReader r = await cmd.ExecuteReaderAsync();
                (await r.ReadAsync()).Should().BeTrue();
                r.GetString(0).Should().Be("hackernews");
                r.GetString(1).Should().Be("hackernews");
                r.GetString(2).Should().Be("community");
                r.GetString(3).Should().Be("launch-2026-11");
                r.GetString(4).Should().Be("hn-launch");
                r.IsDBNull(5).Should().BeTrue("a null field is stored as NULL, not as an empty string");
                r.GetString(6).Should().Be("news.ycombinator.com");
                r.GetString(7).Should().Be("/for/software-teams");
                r.GetBoolean(8).Should().BeTrue();
                (await r.ReadAsync()).Should().BeFalse();
            }

            await using NpgsqlCommand ev = new(
                "SELECT visitor_id, user_id IS NULL, organization_id IS NULL, event, source, detail FROM product_events", c);
            await using NpgsqlDataReader e = await ev.ExecuteReaderAsync();
            (await e.ReadAsync()).Should().BeTrue();
            e.GetGuid(0).Should().Be(Visitor);
            e.GetBoolean(1).Should().BeTrue();
            e.GetBoolean(2).Should().BeTrue();
            e.GetString(3).Should().Be("site.visit");
            e.GetString(4).Should().Be(ProductEventSources.Client);
            e.GetString(5).Should().Be("hackernews");
            (await e.ReadAsync()).Should().BeFalse();
        });
    }

    [PostgresFact]
    public async Task RecordVisit_keeps_the_first_touch_of_a_visitor_and_never_rewrites_it()
    {
        await WithDatabase(async (log, c) =>
        {
            await log.RecordVisitAsync(Touch(Visitor, "hackernews", "hn-launch"), Visit(Visitor, "site.visit", "hackernews"), Window, CancellationToken.None);
            await log.RecordVisitAsync(Touch(Visitor, "reddit", "other-link"), Visit(Visitor, "site.page_view", "pricing"), Window, CancellationToken.None);
            await log.RecordVisitAsync(Touch(Visitor, "direct"), Visit(Visitor, "signup.started", "direct"), Window, CancellationToken.None);

            (await CountAsync(c, "visitor_attribution")).Should().Be(1L);
            await using NpgsqlCommand cmd = new("SELECT source_label || '/' || utm_content FROM visitor_attribution", c);
            ((string)(await cmd.ExecuteScalarAsync())!).Should().Be("hackernews/hn-launch", "ON CONFLICT DO NOTHING: first touch wins");
            (await CountAsync(c, "product_events")).Should().Be(3L, "the later events are still recorded");
        });
    }

    [PostgresFact]
    public async Task RecordVisit_drops_a_repeat_of_the_same_visitor_event_and_detail_inside_the_window_only()
    {
        await WithDatabase(async (log, c) =>
        {
            Guid other = Guid.Parse("0b0e7c5e-6d0f-4a0c-9d55-2c9a4b1f0a11");

            async Task<bool> Send(Guid visitor, string name, string? detail) =>
                await log.RecordVisitAsync(Touch(visitor, "direct"), Visit(visitor, name, detail), Window, CancellationToken.None);

            (await Send(Visitor, "site.page_view", "home")).Should().BeTrue();
            (await Send(Visitor, "site.page_view", "home")).Should().BeFalse("the same page inside the window is a repeat");
            (await Send(Visitor, "site.page_view", "pricing")).Should().BeTrue("another page is another row");
            (await Send(Visitor, "site.cta_click", "pricing")).Should().BeTrue("another event with the same detail is another row");
            (await Send(Visitor, "signup.submitted", null)).Should().BeTrue();
            (await Send(Visitor, "signup.submitted", null)).Should().BeFalse("two rows without a detail are the same detail");
            (await Send(other, "site.page_view", "home")).Should().BeTrue("another visitor is another row");
            (await CountAsync(c, "product_events")).Should().Be(5L);

            // A row older than the window no longer suppresses anything.
            await using (NpgsqlCommand age = new(
                "UPDATE product_events SET occurred_at = now() - interval '31 minutes' WHERE visitor_id = @v AND event = 'site.page_view' AND detail = 'home'", c))
            {
                age.Parameters.AddWithValue("v", Visitor);
                (await age.ExecuteNonQueryAsync()).Should().Be(1);
            }

            (await Send(Visitor, "site.page_view", "home")).Should().BeTrue();
            (await CountAsync(c, "product_events")).Should().Be(6L);
            (await CountAsync(c, "visitor_attribution")).Should().Be(2L, "one first touch per visitor, however many events");
        });
    }

    [PostgresFact]
    public async Task RecordVisit_stores_the_first_touch_even_when_the_event_itself_is_a_repeat()
    {
        await WithDatabase(async (log, c) =>
        {
            // A visit row written before first touches existed: the event is a repeat, the first touch is new.
            await log.AppendAsync(Visit(Visitor, "site.visit", "direct"), CancellationToken.None);

            bool recorded = await log.RecordVisitAsync(Touch(Visitor, "direct"), Visit(Visitor, "site.visit", "direct"), Window, CancellationToken.None);

            recorded.Should().BeFalse();
            (await CountAsync(c, "product_events")).Should().Be(1L);
            (await CountAsync(c, "visitor_attribution")).Should().Be(1L);
        });
    }

    [PostgresFact]
    public async Task RecordVisit_refuses_a_row_that_is_not_a_visitor_only_row_of_that_visitor()
    {
        await WithDatabase(async (log, c) =>
        {
            ProductEvent withUser = Visit(Visitor, "site.visit", "direct");
            withUser.UserId = User;
            ProductEvent otherVisitor = Visit(Guid.Parse("0b0e7c5e-6d0f-4a0c-9d55-2c9a4b1f0a11"), "site.visit", "direct");
            ProductEvent noVisitor = Visit(Visitor, "site.visit", "direct");
            noVisitor.VisitorId = null;

            foreach (ProductEvent bad in new[] { withUser, otherVisitor, noVisitor })
            {
                Func<Task> act = () => log.RecordVisitAsync(Touch(Visitor, "direct"), bad, Window, CancellationToken.None);
                await act.Should().ThrowAsync<ArgumentException>();
            }

            (await CountAsync(c, "product_events")).Should().Be(0L);
            (await CountAsync(c, "visitor_attribution")).Should().Be(0L);
        });
    }
}
