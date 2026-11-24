using System;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

/// <summary>
/// product-analytics: the widened subject CHECK accepts a visitor-only row, still refuses a row with no
/// subject at all, and the retention sweep deletes only OLD visitor-only rows.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class ProductEventsRetentionTests
{
    private static async Task WithDatabase(Func<string, Task> body)
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_pe_retention_" + Guid.NewGuid().ToString("N")[..12];
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
            await body(csb.ConnectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource dropAdmin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = dropAdmin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task ExecAsync(NpgsqlConnection c, string sql)
    {
        await using NpgsqlCommand cmd = new(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    [PostgresFact]
    public async Task Subject_check_accepts_visitor_only_and_refuses_no_subject()
    {
        await WithDatabase(async cs =>
        {
            await using NpgsqlConnection c = new(cs);
            await c.OpenAsync();
            await ExecAsync(c, "INSERT INTO product_events (visitor_id, event, occurred_at, source, detail) "
                + "VALUES (gen_random_uuid(), 'site.visit', now(), 'client', 'direct')");
            Func<Task> none = () => ExecAsync(c, "INSERT INTO product_events (event, occurred_at, source) VALUES ('site.visit', now(), 'client')");
            (await none.Should().ThrowAsync<PostgresException>()).Which.ConstraintName.Should().Be("ck_product_events_subject");
        });
    }

    [PostgresFact]
    public async Task Sweep_deletes_only_old_visitor_only_rows()
    {
        await WithDatabase(async cs =>
        {
            await using NpgsqlConnection c = new(cs);
            await c.OpenAsync();
            await ExecAsync(c, "INSERT INTO product_events (visitor_id, event, occurred_at, source) VALUES "
                + "(gen_random_uuid(), 'site.visit', now() - interval '14 months', 'client'),"
                + "(gen_random_uuid(), 'site.visit', now() - interval '1 month', 'client')");
            await ExecAsync(c, "INSERT INTO product_events (user_id, event, occurred_at, source) VALUES "
                + "(gen_random_uuid(), 'alpha.signup', now() - interval '20 months', 'server')");
            await ExecAsync(c, "INSERT INTO product_events (organization_id, event, occurred_at, source) VALUES "
                + "(gen_random_uuid(), 'alpha.workspace_created', now() - interval '20 months', 'server')");

            var services = new ServiceCollection();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(cs));
            await using ServiceProvider sp = services.BuildServiceProvider();
            var svc = new ProductEventsRetentionService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AlphaTelemetryOptions { VisitRetentionMonths = 13 }),
                NullLogger<ProductEventsRetentionService>.Instance);

            int deleted = await svc.SweepOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

            deleted.Should().Be(1);
            await using NpgsqlCommand count = new("SELECT count(*) FROM product_events", c);
            ((long)(await count.ExecuteScalarAsync())!).Should().Be(3L);
        });
    }

    [PostgresFact]
    public async Task Sweep_deletes_a_visitors_first_touch_with_their_rows_at_the_same_cutoff()
    {
        await WithDatabase(async cs =>
        {
            await using NpgsqlConnection c = new(cs);
            await c.OpenAsync();
            Guid old = Guid.NewGuid(), recent = Guid.NewGuid(), returning = Guid.NewGuid();

            async Task Touch(Guid visitor, string age) =>
                await ExecAsync(c, "INSERT INTO visitor_attribution (visitor_id, source_label, utm_content, first_touch_at, created_at) "
                    + $"VALUES ('{visitor}', 'hackernews', 'hn-launch', now() - interval '{age}', now() - interval '{age}')");

            async Task Event(Guid visitor, string name, string age) =>
                await ExecAsync(c, "INSERT INTO product_events (visitor_id, event, occurred_at, source, detail) "
                    + $"VALUES ('{visitor}', '{name}', now() - interval '{age}', 'client', 'home')");

            // old: everything past the cutoff. recent: everything inside it. returning: a first touch past
            // the cutoff and one event on each side of it.
            await Touch(old, "14 months");
            await Event(old, "site.visit", "14 months");
            await Event(old, "site.page_view", "14 months");
            await Touch(recent, "12 months");
            await Event(recent, "site.visit", "12 months");
            await Touch(returning, "14 months");
            await Event(returning, "site.visit", "14 months");
            await Event(returning, "site.page_view", "1 month");

            // A user's attribution is account data: it lives as long as the user, whatever its age.
            await ExecAsync(c, "INSERT INTO application_users (id,email,display_name,created_at,must_change_password,is_admin) "
                + "VALUES ('11111111-1111-1111-1111-111111111111','keep@t.local','keep',now(),false,false)");
            await ExecAsync(c, "INSERT INTO user_attribution (user_id, source_label, visitor_id, first_touch_at, created_at) "
                + $"VALUES ('11111111-1111-1111-1111-111111111111', 'hackernews', '{old}', now() - interval '14 months', now() - interval '14 months')");

            var services = new ServiceCollection();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(cs));
            await using ServiceProvider sp = services.BuildServiceProvider();
            var svc = new ProductEventsRetentionService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AlphaTelemetryOptions { VisitRetentionMonths = 13 }),
                NullLogger<ProductEventsRetentionService>.Instance);

            int deleted = await svc.SweepOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None);

            deleted.Should().Be(5, "three old events and two old first touches");
            (await CountAsync(c, $"SELECT count(*) FROM visitor_attribution WHERE visitor_id = '{old}'")).Should().Be(0L);
            (await CountAsync(c, $"SELECT count(*) FROM product_events WHERE visitor_id = '{old}'")).Should().Be(0L);
            (await CountAsync(c, $"SELECT count(*) FROM visitor_attribution WHERE visitor_id = '{recent}'")).Should().Be(1L);
            (await CountAsync(c, $"SELECT count(*) FROM product_events WHERE visitor_id = '{recent}'")).Should().Be(1L);
            (await CountAsync(c, $"SELECT count(*) FROM visitor_attribution WHERE visitor_id = '{returning}'")).Should().Be(0L,
                "a first touch is judged by its own age: it is data collected 14 months ago");
            (await CountAsync(c, $"SELECT count(*) FROM product_events WHERE visitor_id = '{returning}'")).Should().Be(1L,
                "the recent event of the same visitor stays");
            (await CountAsync(c, "SELECT count(*) FROM user_attribution")).Should().Be(1L, "the visitor sweep never touches account data");

            // A second sweep finds nothing left to delete.
            (await svc.SweepOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Should().Be(0);

            // The configured months are the cutoff for both tables.
            var shorter = new ProductEventsRetentionService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AlphaTelemetryOptions { VisitRetentionMonths = 6 }),
                NullLogger<ProductEventsRetentionService>.Instance);
            (await shorter.SweepOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Should().Be(2, "the 12-month-old visitor: one event, one first touch");
            (await CountAsync(c, "SELECT count(*) FROM visitor_attribution")).Should().Be(0L);
            (await CountAsync(c, "SELECT count(*) FROM product_events")).Should().Be(1L, "only the one-month-old event is left");
        });
    }

    [PostgresFact]
    public async Task Neither_sweep_deletes_a_tracked_links_daily_counters_however_old()
    {
        await WithDatabase(async cs =>
        {
            await using NpgsqlConnection c = new(cs);
            await c.OpenAsync();
            Guid link = Guid.NewGuid(), visitor = Guid.NewGuid();
            await ExecAsync(c, "INSERT INTO marketing_links (id, code, channel, medium, campaign, placement, destination_path, created_at) "
                + $"VALUES ('{link}', 'hn-launch', 'hackernews', 'community', 'launch-2026-11', 'Show HN', '/', now() - interval '5 years')");
            await ExecAsync(c, "INSERT INTO marketing_link_daily_stats (link_id, day, clicks, bot_hits) VALUES "
                + $"('{link}', (now() - interval '5 years')::date, 40, 7), "
                + $"('{link}', (now() - interval '14 months')::date, 12, 3), "
                + $"('{link}', now()::date, 2, 0)");
            // The visitor rows of the same link, 14 months old, are personal data and do expire.
            await ExecAsync(c, "INSERT INTO visitor_attribution (visitor_id, source_label, utm_content, first_touch_at, created_at) "
                + $"VALUES ('{visitor}', 'hackernews', 'hn-launch', now() - interval '14 months', now() - interval '14 months')");
            await ExecAsync(c, "INSERT INTO product_events (visitor_id, event, occurred_at, source, detail) "
                + $"VALUES ('{visitor}', 'site.visit', now() - interval '14 months', 'client', 'hackernews')");

            var services = new ServiceCollection();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(cs));
            await using ServiceProvider sp = services.BuildServiceProvider();
            // The shortest retention either sweep accepts: one month.
            var svc = new ProductEventsRetentionService(
                sp.GetRequiredService<IServiceScopeFactory>(),
                Options.Create(new AlphaTelemetryOptions { VisitRetentionMonths = 1, RetentionMonths = 1 }),
                NullLogger<ProductEventsRetentionService>.Instance);

            (await svc.SweepOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Should().Be(2, "the visitor's event and first touch");
            await svc.SweepAccountDataAsync(DateTimeOffset.UtcNow, CancellationToken.None);

            (await CountAsync(c, "SELECT count(*) FROM visitor_attribution")).Should().Be(0L);
            (await CountAsync(c, "SELECT count(*) FROM marketing_links")).Should().Be(1L);
            (await CountAsync(c, "SELECT count(*) FROM marketing_link_daily_stats")).Should().Be(3L,
                "a per-link, per-day count names no visitor, address or browser: it is not personal data and is the link's history");
            (await CountAsync(c, "SELECT coalesce(sum(clicks), 0) FROM marketing_link_daily_stats")).Should().Be(54L);
            (await CountAsync(c, "SELECT coalesce(sum(bot_hits), 0) FROM marketing_link_daily_stats")).Should().Be(10L);
        });
    }

    [Fact]
    public void The_visitor_first_touch_keeps_for_thirteen_months_by_default()
    {
        new AlphaTelemetryOptions().VisitRetentionMonths.Should().Be(13);
    }

    [Fact]
    public void No_retention_statement_names_the_tracked_link_tables()
    {
        // The link tables hold counters and the operator's own link list: no person, so no expiry. Every
        // DELETE of the telemetry retention sweep is a literal in this one file.
        string source = System.IO.File.ReadAllText(System.IO.Path.Combine(
            InferenceRoot(), "src", "ReelBolt.Inference.Api", "Services", "Telemetry", "ProductEventsRetentionService.cs"));

        source.Should().Contain("DELETE FROM visitor_attribution WHERE first_touch_at < {0}");
        source.Should().NotContain("marketing_link");
    }

    private static string InferenceRoot()
    {
        for (string dir = System.IO.Path.GetDirectoryName(typeof(ProductEventsRetentionTests).Assembly.Location);
            dir != null;
            dir = System.IO.Path.GetDirectoryName(dir))
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, "ReelBolt.sln"))) return dir;
        }

        throw new InvalidOperationException("The inference solution root was not found.");
    }

    private static ProductEventsRetentionService Build(string cs, int months, out ServiceProvider sp)
    {
        var services = new ServiceCollection();
        services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(cs));
        sp = services.BuildServiceProvider();
        return new ProductEventsRetentionService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AlphaTelemetryOptions { RetentionMonths = months }),
            NullLogger<ProductEventsRetentionService>.Instance);
    }

    private static async Task<long> CountAsync(NpgsqlConnection c, string sql)
    {
        await using NpgsqlCommand cmd = new(sql, c);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task Account_sweep_deletes_old_account_rows_keeps_recent_and_consent_evidence()
    {
        await WithDatabase(async cs =>
        {
            await using NpgsqlConnection c = new(cs);
            await c.OpenAsync();
            await ExecAsync(c, "INSERT INTO application_users (id,email,display_name,created_at,must_change_password,is_admin) "
                + "VALUES ('11111111-1111-1111-1111-111111111111','keep@t.local','keep',now(),false,false)");
            await ExecAsync(c, "INSERT INTO product_events (user_id, event, occurred_at, source) VALUES "
                + "(gen_random_uuid(), 'alpha.signup', now() - interval '13 months', 'server'),"
                + "(gen_random_uuid(), 'alpha.signup', now() - interval '2 months', 'server'),"
                + "('11111111-1111-1111-1111-111111111111', 'telemetry.consent.granted', now() - interval '20 months', 'consent'),"
                + "(gen_random_uuid(), 'telemetry.consent.declined', now() - interval '20 months', 'consent'),"
                + "('11111111-1111-1111-1111-111111111111', 'telemetry.consent.declined', now() - interval '2 months', 'consent')");
            await ExecAsync(c, "INSERT INTO product_events (organization_id, event, occurred_at, source) VALUES "
                + "(gen_random_uuid(), 'telemetry.consent.granted', now() - interval '20 months', 'consent')");
            await ExecAsync(c, "INSERT INTO alpha_funnel_claims (user_id, event, claimed_at) VALUES "
                + "(gen_random_uuid(), 'alpha.signup', now() - interval '13 months'),"
                + "(gen_random_uuid(), 'alpha.signup', now() - interval '1 month')");
            await ExecAsync(c, "INSERT INTO product_events (organization_id, event, occurred_at, source) VALUES "
                + "(gen_random_uuid(), 'alpha.workspace_created', now() - interval '13 months', 'server')");
            await ExecAsync(c, "INSERT INTO product_events (visitor_id, event, occurred_at, source) VALUES "
                + "(gen_random_uuid(), 'site.visit', now() - interval '12.5 months', 'client')");
            await ExecAsync(c, "INSERT INTO feedback_reports (id, user_id, organization_id, kind, message, context_json, created_at) VALUES "
                + "(gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'bug', 'old', '{}', now() - interval '13 months'),"
                + "(gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'bug', 'new', '{}', now() - interval '1 month')");

            ProductEventsRetentionService svc = Build(cs, 12, out ServiceProvider sp);
            await using (sp)
            {
                (await svc.SweepAccountDataAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Should().Be(6);
                // visitor-only row at 12.5 months is untouched by the account sweep and by the 13-month visit sweep
                (await svc.SweepOnceAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Should().Be(0);
            }

            (await CountAsync(c, "SELECT count(*) FROM product_events")).Should().Be(4L);
            // only the existing user's consent rows survive; the deleted-user and user-less ones are gone
            (await CountAsync(c, "SELECT count(*) FROM product_events WHERE source = 'consent' "
                + "AND user_id = '11111111-1111-1111-1111-111111111111'")).Should().Be(2L);
            (await CountAsync(c, "SELECT count(*) FROM product_events WHERE source = 'consent'")).Should().Be(2L);
            (await CountAsync(c, "SELECT count(*) FROM alpha_funnel_claims")).Should().Be(1L);
            (await CountAsync(c, "SELECT count(*) FROM feedback_reports WHERE message = 'new'")).Should().Be(1L);
            (await CountAsync(c, "SELECT count(*) FROM feedback_reports")).Should().Be(1L);
        });
    }

    [PostgresFact]
    public async Task Account_sweep_honours_configured_months()
    {
        await WithDatabase(async cs =>
        {
            await using NpgsqlConnection c = new(cs);
            await c.OpenAsync();
            await ExecAsync(c, "INSERT INTO product_events (user_id, event, occurred_at, source) VALUES "
                + "(gen_random_uuid(), 'alpha.signup', now() - interval '5 months', 'server'),"
                + "(gen_random_uuid(), 'alpha.signup', now() - interval '1 month', 'server')");
            ProductEventsRetentionService svc = Build(cs, 3, out ServiceProvider sp);
            await using (sp)
            {
                (await svc.SweepAccountDataAsync(DateTimeOffset.UtcNow, CancellationToken.None)).Should().Be(1);
            }

            (await CountAsync(c, "SELECT count(*) FROM product_events")).Should().Be(1L);
        });
    }
}
