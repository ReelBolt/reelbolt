#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.BiReadOnlyRole;
using ReelBolt.Inference.Api.Services.Marketing.Links;
using ReelBolt.Shared.Acquisition;
using ReelBolt.Shared.Data.Models.Marketing;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Data.BiReadOnlyRole;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Marketing.Links;

/// <summary>
/// The new-user funnel schema and its two PostgreSQL-only statements against a real PostgreSQL
/// (docs/design/new-user-funnel.md sections 5 and 6): what the <c>AddAcquisitionFunnel</c> migration
/// creates and refuses, the BI grant it carries, the atomic click-counter upsert under concurrency, the
/// unique-code race, and the per-link funnel statement against seeded people.
/// <para>
/// Follows <see cref="Data.ProductEventsSchemaTests"/> and <see cref="Data.SoftOpenFunnelQueryTests"/>:
/// a per-test database, <see cref="PostgresTestEnvironment"/>, and <c>DROP DATABASE ... WITH (FORCE)</c>
/// in <c>finally</c>.
/// </para>
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class AcquisitionFunnelPostgresTests
{
    private static readonly string[] FunnelTables =
        ["marketing_links", "marketing_link_daily_stats", "visitor_attribution", "user_onboarding_profiles"];

    /// <summary>A SQL expression passed through as written, not as a bound value.</summary>
    private sealed record Raw(string Sql);

    /// <summary>Runs <paramref name="body"/> against a freshly migrated database (API, then engine when asked), then drops it.</summary>
    private static async Task WithDatabase(Func<string, NpgsqlConnection, Task> body, bool engine = false, int maxPoolSize = PostgresTestEnvironment.DefaultMaxPoolSize)
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_acquisition_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        string connectionString = PostgresTestEnvironment.ForDatabase(baseConnection, dbName, maxPoolSize);
        try
        {
            await using (InferenceApiDbContext api = NewDb(connectionString))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            if (engine)
            {
                await using WorkflowEngineDbContext engineDb = new(
                    new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connectionString).Options);
                await engineDb.GetService<IMigrator>().MigrateAsync();
            }

            await using NpgsqlConnection c = new(connectionString);
            await c.OpenAsync();
            await body(connectionString, c);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource dropAdmin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = dropAdmin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static InferenceApiDbContext NewDb(string connectionString)
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connectionString).Options);

    private static async Task<object?> Scalar(NpgsqlConnection c, string sql, params object[] values)
    {
        await using NpgsqlCommand cmd = new(sql, c);
        foreach (object value in values) cmd.Parameters.Add(new NpgsqlParameter { Value = value });
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task Exec(NpgsqlConnection c, string sql, params object[] values)
    {
        await using NpgsqlCommand cmd = new(sql, c);
        foreach (object value in values) cmd.Parameters.Add(new NpgsqlParameter { Value = value });
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string?> SqlStateOf(NpgsqlConnection c, string sql)
    {
        try
        {
            await Exec(c, sql);
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
    }

    private const string LinkColumns = "id, code, channel, medium, campaign, placement, destination_path, created_at";

    private static string LinkValues(string code, string channel = "hackernews", string medium = "community", string destination = "/")
        => $"gen_random_uuid(), '{code}', '{channel}', '{medium}', 'launch', 'somewhere', '{destination}', now()";

    // ---- schema ----

    [PostgresFact]
    public async Task The_migration_creates_the_four_tables_and_the_capture_column_as_the_design_names_them()
    {
        await WithDatabase(async (_, c) =>
        {
            async Task<List<string>> Columns(string table)
            {
                List<string> rows = [];
                await using NpgsqlCommand cmd = new(
                    "SELECT column_name || ' ' || data_type || coalesce('(' || character_maximum_length || ')', '') || "
                    + "CASE is_nullable WHEN 'NO' THEN ' not null' ELSE '' END "
                    + "FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t ORDER BY ordinal_position", c);
                cmd.Parameters.AddWithValue("t", table);
                await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
                return rows;
            }

            (await Columns("marketing_links")).Should().Equal(
                "id uuid not null",
                "code character varying(40) not null",
                "channel character varying(32) not null",
                "medium character varying(32) not null",
                "campaign character varying(64) not null",
                "placement character varying(160) not null",
                "destination_path character varying(200) not null",
                "persona character varying(32)",
                "cost_minor bigint",
                "cost_currency character varying(3)",
                "created_at timestamp with time zone not null",
                "created_by_user_id uuid",
                "archived_at timestamp with time zone");

            (await Columns("marketing_link_daily_stats")).Should().Equal(
                "link_id uuid not null",
                "day date not null",
                "clicks integer not null",
                "bot_hits integer not null");

            (await Columns("visitor_attribution")).Should().Equal(
                "visitor_id uuid not null",
                "source_label character varying(64) not null",
                "utm_source character varying(64)",
                "utm_medium character varying(64)",
                "utm_campaign character varying(64)",
                "utm_content character varying(64)",
                "utm_term character varying(64)",
                "referrer_host character varying(100)",
                "landing_path character varying(120)",
                "first_touch_at timestamp with time zone not null",
                "created_at timestamp with time zone not null");

            (await Columns("user_onboarding_profiles")).Should().Equal(
                "user_id uuid not null",
                "persona character varying(32)",
                "material character varying(32)",
                "suggested_persona character varying(32)",
                "answered_at timestamp with time zone",
                "skipped_at timestamp with time zone",
                "created_at timestamp with time zone not null",
                "updated_at timestamp with time zone not null");

            (await Columns("user_attribution")).Should().Contain("capture character varying(16)");

            async Task<string?> KeyColumns(string table, string type) => (string?)await Scalar(
                c,
                "SELECT string_agg(a.attname, ',' ORDER BY k.ord) FROM pg_constraint con "
                + "JOIN unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord) ON true "
                + "JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum "
                + "WHERE con.conrelid = ('public.' || $1)::regclass AND con.contype = $2::\"char\"",
                table, type);

            (await KeyColumns("marketing_links", "p")).Should().Be("id");
            (await KeyColumns("marketing_link_daily_stats", "p")).Should().Be("link_id,day");
            (await KeyColumns("visitor_attribution", "p")).Should().Be("visitor_id");
            (await KeyColumns("user_onboarding_profiles", "p")).Should().Be("user_id");

            (await Scalar(c, "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ux_marketing_links_code'"))
                .Should().BeOfType<string>().Which.Should().Contain("UNIQUE").And.Contain("(code)");
            (await Scalar(c, "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ix_visitor_attribution_utm_content_first_touch_at'"))
                .Should().BeOfType<string>().Which.Should().Contain("(utm_content, first_touch_at)").And.Contain("utm_content IS NOT NULL");
            (await Scalar(c, "SELECT indexdef FROM pg_indexes WHERE schemaname = 'public' AND indexname = 'ix_visitor_attribution_first_touch_at'"))
                .Should().NotBeNull();
        });
    }

    [PostgresFact]
    public async Task The_check_constraints_are_exactly_the_closed_sets_of_the_vocabulary()
    {
        await WithDatabase(async (_, c) =>
        {
            async Task<string> Check(string name) => (string)(await Scalar(
                c, "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = $1 AND contype = 'c'", name))!;

            static string[] Tokens(string definition) => System.Text.RegularExpressions.Regex
                .Matches(definition, "'([a-z0-9]+)'").Select(m => m.Groups[1].Value).ToArray();

            Tokens(await Check("ck_marketing_links_channel")).Should().BeEquivalentTo(AcquisitionVocabulary.LinkChannels);
            Tokens(await Check("ck_marketing_links_medium")).Should().BeEquivalentTo(AcquisitionVocabulary.LinkMedia);
            Tokens(await Check("ck_marketing_links_persona")).Should().BeEquivalentTo(AcquisitionVocabulary.Personas);
            Tokens(await Check("ck_user_onboarding_profiles_persona")).Should().BeEquivalentTo(AcquisitionVocabulary.Personas);
            Tokens(await Check("ck_user_onboarding_profiles_suggested_persona")).Should().BeEquivalentTo(AcquisitionVocabulary.Personas);
            Tokens(await Check("ck_user_onboarding_profiles_material")).Should().BeEquivalentTo(AcquisitionVocabulary.Materials);
            Tokens(await Check("ck_user_attribution_capture")).Should().BeEquivalentTo(AcquisitionVocabulary.AttributionCaptures);
        });
    }

    [PostgresFact]
    public async Task The_database_refuses_a_token_outside_its_closed_set_and_a_destination_on_another_host()
    {
        await WithDatabase(async (_, c) =>
        {
            const string checkViolation = "23514";
            string insertLink = $"INSERT INTO marketing_links ({LinkColumns}) VALUES ";

            (await SqlStateOf(c, insertLink + $"({LinkValues("ok-link")})")).Should().BeNull();
            (await SqlStateOf(c, insertLink + $"({LinkValues("bad-channel", channel: "twitter")})")).Should().Be(checkViolation);
            (await SqlStateOf(c, insertLink + $"({LinkValues("bad-medium", medium: "cpc")})")).Should().Be(checkViolation);
            (await SqlStateOf(c, insertLink + $"({LinkValues("no-slash", destination: "pricing")})")).Should().Be(checkViolation);
            (await SqlStateOf(c, insertLink + $"({LinkValues("other-host", destination: "//evil.example/x")})")).Should().Be(checkViolation);
            (await SqlStateOf(c, "UPDATE marketing_links SET persona = 'developer' WHERE code = 'ok-link'")).Should().Be(checkViolation);
            (await SqlStateOf(c, "UPDATE marketing_links SET persona = 'creator' WHERE code = 'ok-link'")).Should().BeNull();
            (await SqlStateOf(c, "UPDATE marketing_links SET cost_minor = 100 WHERE code = 'ok-link'"))
                .Should().Be(checkViolation, "an amount needs a currency");
            (await SqlStateOf(c, "UPDATE marketing_links SET cost_minor = -1, cost_currency = 'EUR' WHERE code = 'ok-link'")).Should().Be(checkViolation);
            (await SqlStateOf(c, "UPDATE marketing_links SET cost_minor = 100, cost_currency = 'EUR' WHERE code = 'ok-link'")).Should().BeNull();

            (await SqlStateOf(c, insertLink + $"({LinkValues("ok-link")})")).Should().Be("23505", "a code names one link");

            (await SqlStateOf(c,
                "INSERT INTO marketing_link_daily_stats (link_id, day, clicks, bot_hits) "
                + "SELECT id, current_date, -1, 0 FROM marketing_links WHERE code = 'ok-link'")).Should().Be(checkViolation);
            (await SqlStateOf(c,
                "INSERT INTO marketing_link_daily_stats (link_id, day, clicks, bot_hits) VALUES (gen_random_uuid(), current_date, 1, 0)"))
                .Should().Be("23503", "a counter belongs to a link");

            // The two user tables: a user row first (the Go API writes both at account creation).
            Guid user = Guid.NewGuid();
            await Exec(c,
                "INSERT INTO application_users (id, email, display_name, created_at, is_admin, must_change_password) "
                + "VALUES ($1, 'p@example.test', 'P', now(), false, false)", user);
            string insertProfile = "INSERT INTO user_onboarding_profiles (user_id, suggested_persona, created_at, updated_at) VALUES ";
            (await SqlStateOf(c, insertProfile + $"('{user}', 'developer', now(), now())")).Should().Be(checkViolation);
            (await SqlStateOf(c, insertProfile + $"('{user}', 'creator', now(), now())")).Should().BeNull();
            (await SqlStateOf(c, $"UPDATE user_onboarding_profiles SET persona = 'nobody' WHERE user_id = '{user}'")).Should().Be(checkViolation);
            (await SqlStateOf(c, $"UPDATE user_onboarding_profiles SET material = 'video' WHERE user_id = '{user}'")).Should().Be(checkViolation);
            (await SqlStateOf(c, $"UPDATE user_onboarding_profiles SET persona = 'other', material = 'idea', answered_at = now() WHERE user_id = '{user}'"))
                .Should().BeNull();

            string insertAttribution = "INSERT INTO user_attribution (user_id, source_label, created_at, capture) VALUES ";
            (await SqlStateOf(c, insertAttribution + $"('{user}', 'direct', now(), 'guess')")).Should().Be(checkViolation);
            (await SqlStateOf(c, insertAttribution + $"('{user}', 'direct', now(), 'url')")).Should().BeNull();
            (await SqlStateOf(c, $"UPDATE user_attribution SET capture = 'cookie' WHERE user_id = '{user}'")).Should().BeNull();
            (await SqlStateOf(c, $"UPDATE user_attribution SET capture = NULL WHERE user_id = '{user}'"))
                .Should().BeNull("rows older than the column keep a null capture");
        });
    }

    [PostgresFact]
    public async Task Deleting_a_link_removes_its_counters_and_deleting_a_user_removes_the_profile_and_keeps_their_links()
    {
        await WithDatabase(async (_, c) =>
        {
            Guid user = Guid.NewGuid();
            await Exec(c,
                "INSERT INTO application_users (id, email, display_name, created_at, is_admin, must_change_password) "
                + "VALUES ($1, 'admin@example.test', 'A', now(), true, false)", user);
            await Exec(c, "INSERT INTO user_onboarding_profiles (user_id, created_at, updated_at) VALUES ($1, now(), now())", user);
            await Exec(c,
                $"INSERT INTO marketing_links ({LinkColumns}, created_by_user_id) VALUES ({LinkValues("kept")}, $1), ({LinkValues("gone")}, $1)", user);
            await Exec(c,
                "INSERT INTO marketing_link_daily_stats (link_id, day, clicks, bot_hits) SELECT id, current_date, 3, 1 FROM marketing_links");

            await Exec(c, "DELETE FROM marketing_links WHERE code = 'gone'");
            (await Scalar(c, "SELECT count(*) FROM marketing_link_daily_stats")).Should().Be(1L, "the deleted link's counter went with it");

            await Exec(c, "DELETE FROM application_users WHERE id = $1", user);
            (await Scalar(c, "SELECT count(*) FROM user_onboarding_profiles")).Should().Be(0L, "the profile is the person's");
            (await Scalar(c, "SELECT count(*) FROM marketing_links WHERE code = 'kept' AND created_by_user_id IS NULL"))
                .Should().Be(1L, "a link outlives the admin who created it");
        });
    }

    // ---- the BI grant ----

    /// <summary>
    /// The role is created if absent and deliberately not dropped: roles are cluster-wide and the suite
    /// already leaves <c>powerbi_ro</c> behind (see <see cref="Data.ProductEventsSchemaTests"/>).
    /// </summary>
    [PostgresFact]
    public async Task The_migration_grants_powerbi_ro_select_on_the_four_tables_when_the_role_exists()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        await using (NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection))
        await using (NpgsqlCommand role = admin.CreateCommand(
            "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'powerbi_ro') THEN "
            + "CREATE ROLE powerbi_ro LOGIN; END IF; END $$;"))
        {
            await role.ExecuteNonQueryAsync();
        }

        await WithDatabase(async (_, c) =>
        {
            foreach (string table in FunnelTables)
            {
                (await Scalar(c, $"SELECT has_table_privilege('powerbi_ro', 'public.{table}', 'SELECT')"))
                    .Should().Be(true, "the creation migration grants powerbi_ro SELECT on {0} when the role exists", table);
                foreach (string write in new[] { "INSERT", "UPDATE", "DELETE", "TRUNCATE" })
                {
                    (await Scalar(c, $"SELECT has_table_privilege('powerbi_ro', 'public.{table}', '{write}')"))
                        .Should().Be(false, "the grant on {0} is read-only", table);
                }
            }

            (await Scalar(c, "SELECT has_column_privilege('powerbi_ro', 'public.user_attribution', 'capture', 'SELECT')"))
                .Should().Be(true, "user_attribution is granted table-wide, so the new column rides the existing grant");

            PowerBiReadOnlyRoleSql.FullSelectTables.Should().Contain(FunnelTables,
                "the operator script's registry grants the same four tables, so a fresh database converges to the same ACL");
        });
    }

    // ---- the counter ----

    [PostgresFact]
    public async Task Concurrent_hits_on_one_link_and_day_are_all_counted_in_one_row()
    {
        await WithDatabase(async (connectionString, c) =>
        {
            Guid link = Guid.NewGuid();
            await Exec(c, $"INSERT INTO marketing_links ({LinkColumns}) VALUES ('{link}', 'hn-launch', 'hackernews', 'community', 'launch', 'p', '/', now())");
            DateOnly day = new(2026, 11, 11);

            // No row exists yet, so the first statements race on the INSERT itself: the conflict target
            // turns the losers into updates instead of unique violations.
            const int clicks = 48, bots = 16;
            IEnumerable<Task> hits = Enumerable.Range(0, clicks + bots).Select(i => Task.Run(async () =>
            {
                await using InferenceApiDbContext db = NewDb(connectionString);
                await new TrackedLinkService(db).RecordHitAsync(link, day, bot: i >= clicks, CancellationToken.None);
            }));
            await Task.WhenAll(hits);

            (await Scalar(c, "SELECT count(*) FROM marketing_link_daily_stats")).Should().Be(1L, "one row per link and day");
            (await Scalar(c, "SELECT clicks FROM marketing_link_daily_stats WHERE link_id = $1 AND day = $2", link, day))
                .Should().Be(clicks, "no concurrent click was lost");
            (await Scalar(c, "SELECT bot_hits FROM marketing_link_daily_stats WHERE link_id = $1 AND day = $2", link, day))
                .Should().Be(bots);

            // Another day and another link each get their own row.
            await using InferenceApiDbContext one = NewDb(connectionString);
            await new TrackedLinkService(one).RecordHitAsync(link, day.AddDays(1), bot: false, CancellationToken.None);
            (await Scalar(c, "SELECT count(*) FROM marketing_link_daily_stats")).Should().Be(2L);
            (await Scalar(c, "SELECT clicks FROM marketing_link_daily_stats WHERE day = $1", day.AddDays(1))).Should().Be(1);
        }, maxPoolSize: 20);
    }

    [PostgresFact]
    public async Task The_counter_upsert_is_one_statement_on_the_primary_key()
    {
        TrackedLinkService.RecordHitSql.Should().StartWith("INSERT INTO marketing_link_daily_stats (link_id, day, clicks, bot_hits)")
            .And.Contain("ON CONFLICT (link_id, day) DO UPDATE SET")
            .And.Contain("clicks = marketing_link_daily_stats.clicks + EXCLUDED.clicks")
            .And.Contain("bot_hits = marketing_link_daily_stats.bot_hits + EXCLUDED.bot_hits")
            .And.NotContain(";");

        await WithDatabase(async (connectionString, c) =>
        {
            // A hit on a link that does not exist fails on the foreign key and leaves nothing behind;
            // the redirect catches it, so it never reaches the person who clicked.
            await using InferenceApiDbContext db = NewDb(connectionString);
            Func<Task> act = () => new TrackedLinkService(db).RecordHitAsync(Guid.NewGuid(), new DateOnly(2026, 11, 11), false, CancellationToken.None);
            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");
            (await Scalar(c, "SELECT count(*) FROM marketing_link_daily_stats")).Should().Be(0L);
        });
    }

    [PostgresFact]
    public async Task Two_admins_creating_the_same_code_at_once_leave_one_link_and_the_loser_reads_link_code_taken()
    {
        await WithDatabase(async (connectionString, c) =>
        {
            // The first admin's insert is in flight (uncommitted): the second admin's existence check
            // cannot see it, so only the unique index can decide.
            await using NpgsqlConnection first = new(connectionString);
            await first.OpenAsync();
            await using NpgsqlTransaction tx = await first.BeginTransactionAsync();
            await using (NpgsqlCommand insert = new(
                $"INSERT INTO marketing_links ({LinkColumns}) VALUES ({LinkValues("hn-launch", channel: "reddit", medium: "social")})", first, tx))
            {
                await insert.ExecuteNonQueryAsync();
            }

            await using InferenceApiDbContext db = NewDb(connectionString);
            Task<MarketingLink> second = Task.Run(() => new TrackedLinkService(db).CreateAsync(
                new TrackedLinkInput("hn-launch", "hackernews", "community", "launch-2026-11", "Show HN", "/", null, null, null),
                Guid.Empty,
                CancellationToken.None));

            // Wait until the second insert is blocked on the first one's uncommitted row, then let the first win.
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while ((long)(await Scalar(
                       c, "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'"))! == 0)
            {
                second.IsCompleted.Should().BeFalse("the second insert must wait for the first transaction to end");
                (DateTime.UtcNow < deadline).Should().BeTrue("the second insert should block on the unique index within 30 seconds");
                await Task.Delay(50);
            }

            await tx.CommitAsync();

            TrackedLinkRefusal refusal = (await ((Func<Task>)(() => second)).Should().ThrowAsync<TrackedLinkRefusal>()).Which;
            refusal.Code.Should().Be("link_code_taken");
            (await Scalar(c, "SELECT channel FROM marketing_links WHERE code = 'hn-launch'")).Should().Be("reddit", "the first link is the one that exists");
            (await Scalar(c, "SELECT count(*) FROM marketing_links")).Should().Be(1L);
        });
    }

    [PostgresFact]
    public async Task The_admin_service_creates_lists_edits_and_resolves_links_on_postgres()
    {
        await WithDatabase(async (connectionString, c) =>
        {
            Guid admin = Guid.NewGuid();
            await Exec(c,
                "INSERT INTO application_users (id, email, display_name, created_at, is_admin, must_change_password) "
                + "VALUES ($1, 'admin@example.test', 'A', now(), true, false)", admin);

            MarketingLink created;
            await using (InferenceApiDbContext db = NewDb(connectionString))
            {
                created = await new TrackedLinkService(db).CreateAsync(
                    new TrackedLinkInput("hn-launch", "hackernews", "community", "launch-2026-11", "Show HN", "/for/software-teams", "software", 2500, "USD"),
                    admin,
                    CancellationToken.None);
            }

            (await Scalar(c, "SELECT created_by_user_id FROM marketing_links WHERE code = 'hn-launch'")).Should().Be(admin);

            DateOnly day = new(2026, 11, 11);
            await using (InferenceApiDbContext db = NewDb(connectionString))
            {
                TrackedLinkService service = new(db);
                MarketingLink? active = await service.FindActiveAsync("hn-launch", CancellationToken.None);
                active!.Id.Should().Be(created.Id);
                await service.RecordHitAsync(active.Id, day, bot: false, CancellationToken.None);
                await service.RecordHitAsync(active.Id, day.AddDays(1), bot: false, CancellationToken.None);
                await service.RecordHitAsync(active.Id, day.AddDays(1), bot: true, CancellationToken.None);
            }

            await using (InferenceApiDbContext db = NewDb(connectionString))
            {
                TrackedLinkWithClicks listed = (await new TrackedLinkService(db).ListAsync(CancellationToken.None)).Single();
                listed.Link.Code.Should().Be("hn-launch");
                listed.Link.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
                listed.TotalClicks.Should().Be(2, "clicks of both days; the bot hit is not a click");
            }

            await using (InferenceApiDbContext db = NewDb(connectionString))
            {
                TrackedLinkWithClicks? patched = await new TrackedLinkService(db).PatchAsync(
                    created.Id,
                    System.Text.Json.JsonDocument.Parse("""{"archived":true,"persona":null,"costMinor":null,"costCurrency":null}""").RootElement,
                    CancellationToken.None);
                patched!.Link.ArchivedAt.Should().NotBeNull();
                patched.Link.Persona.Should().BeNull();
                patched.TotalClicks.Should().Be(2);
            }

            await using (InferenceApiDbContext db = NewDb(connectionString))
            {
                (await new TrackedLinkService(db).FindActiveAsync("hn-launch", CancellationToken.None))
                    .Should().BeNull("an archived link is not followed");
                (await new TrackedLinkService(db).FindActiveAsync("no-such-link", CancellationToken.None)).Should().BeNull();
            }

            (await Scalar(c, "SELECT cost_minor IS NULL AND cost_currency IS NULL AND persona IS NULL AND archived_at IS NOT NULL FROM marketing_links"))
                .Should().Be(true);
        });
    }

    // ---- the funnel ----

    [PostgresFact]
    public async Task The_funnel_counts_each_link_and_the_unattributed_accounts_from_seeded_people_and_runs_as_powerbi_ro()
    {
        await WithDatabase(async (connectionString, c) =>
        {
            // The role comes from the operator script, not from a migration.
            await Exec(c, PowerBiReadOnlyRoleSql.Up);
            await Exec(c, PowerBiReadOnlyRoleEngineSql.Up);

            // Seed without foreign keys: the rows are about what the statement means, not how they were written.
            await Exec(c, "SET session_replication_role = replica");

            async Task<Dictionary<string, (string DataType, string? Default, bool Identity)>> RequiredColumns(string table)
            {
                Dictionary<string, (string, string?, bool)> required = new();
                await using NpgsqlCommand cmd = new(
                    "SELECT column_name, data_type, column_default, is_identity = 'YES' FROM information_schema.columns "
                    + "WHERE table_schema = 'public' AND table_name = @t AND is_nullable = 'NO'", c);
                cmd.Parameters.AddWithValue("t", table);
                await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    required[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3));
                }

                return required;
            }

            static string Filler(string dataType) => dataType switch
            {
                "uuid" => "gen_random_uuid()",
                "character varying" or "text" or "character" => "substr(gen_random_uuid()::text, 1, 8)",
                "timestamp with time zone" or "timestamp without time zone" => "now()",
                "integer" or "bigint" or "smallint" or "numeric" or "double precision" or "real" => "0",
                "boolean" => "false",
                "jsonb" => "'{}'::jsonb",
                "json" => "'{}'::json",
                "bytea" => "'\\x'::bytea",
                "date" => "current_date",
                _ => throw new InvalidOperationException($"No seed filler for a NOT NULL column of type {dataType}; extend Filler."),
            };

            async Task Insert(string table, params (string Column, object? Value)[] values)
            {
                Dictionary<string, (string DataType, string? Default, bool Identity)> required = await RequiredColumns(table);
                List<string> columns = [];
                List<string> expressions = [];
                List<NpgsqlParameter> parameters = [];
                foreach ((string column, object? value) in values)
                {
                    columns.Add(column);
                    if (value is Raw raw)
                    {
                        expressions.Add(raw.Sql);
                    }
                    else
                    {
                        string name = "p" + parameters.Count;
                        expressions.Add("@" + name);
                        parameters.Add(new NpgsqlParameter(name, value ?? DBNull.Value));
                    }
                }

                foreach ((string column, (string dataType, string? columnDefault, bool identity)) in required)
                {
                    if (columns.Contains(column) || identity || columnDefault is not null) continue;
                    columns.Add(column);
                    expressions.Add(Filler(dataType));
                }

                await using NpgsqlCommand insert = new(
                    $"INSERT INTO {table} ({string.Join(", ", columns.Select(x => '"' + x + '"'))}) VALUES ({string.Join(", ", expressions)})", c);
                insert.Parameters.AddRange(parameters.ToArray());
                await insert.ExecuteNonQueryAsync();
            }

            // One clock for the whole test, so a run across midnight UTC cannot move the window under the seed.
            DateTime now = DateTime.UtcNow;
            DateOnly today = DateOnly.FromDateTime(now);
            const int days = 30;
            DateOnly sinceDay = today.AddDays(-(days - 1));
            DateTime windowStart = new(sinceDay, TimeOnly.MinValue, DateTimeKind.Utc);
            DateTime Ago(int d) => now.AddDays(-d);
            Guid Id() => Guid.NewGuid();

            // ---- links: newest first is empty, hn-launch, yt-demo (archived) ----
            Guid hn = Id(), yt = Id(), empty = Id();
            async Task Link(Guid id, string code, string channel, string medium, int createdDaysAgo, bool archived = false, long? cost = null) =>
                await Insert("marketing_links",
                    ("id", id), ("code", code), ("channel", channel), ("medium", medium), ("campaign", "launch-2026-11"),
                    ("placement", "where " + code + " is posted"), ("destination_path", "/for/creators"),
                    ("cost_minor", cost), ("cost_currency", cost is null ? null : "USD"),
                    ("created_at", Ago(createdDaysAgo)), ("archived_at", archived ? Ago(1) : null));
            await Link(hn, "hn-launch", "hackernews", "community", createdDaysAgo: 20, cost: 2500);
            await Link(yt, "yt-demo", "youtube", "video", createdDaysAgo: 60, archived: true);
            await Link(empty, "empty", "blog", "owned", createdDaysAgo: 1);

            async Task Stat(Guid link, DateOnly day, int clicks, int bots) =>
                await Insert("marketing_link_daily_stats", ("link_id", link), ("day", day), ("clicks", clicks), ("bot_hits", bots));
            await Stat(hn, today, 5, 2);
            await Stat(hn, sinceDay, 3, 1);                 // the first day of the window counts
            await Stat(hn, sinceDay.AddDays(-1), 100, 50);  // the day before it does not
            await Stat(yt, today.AddDays(-1), 7, 0);

            async Task Visitor(string? code, DateTime firstTouch) =>
                await Insert("visitor_attribution",
                    ("visitor_id", Id()), ("source_label", "hackernews"), ("utm_content", code),
                    ("first_touch_at", firstTouch), ("created_at", firstTouch));
            await Visitor("hn-launch", Ago(2));
            await Visitor("hn-launch", Ago(5));
            await Visitor("hn-launch", Ago(40));            // before the window
            await Visitor("yt-demo", Ago(1));
            await Visitor(null, Ago(1));                    // no link code
            await Visitor("hand-written", Ago(1));          // a code no link has

            // ---- accounts ----
            async Task<Guid> User(DateTime createdAt, bool verified, bool serviceAccount = false, bool platformAdmin = false)
            {
                Guid id = Id();
                await Insert("application_users",
                    ("id", id), ("email", id.ToString("N") + "@example.test"), ("created_at", createdAt),
                    ("email_verified_at", verified ? createdAt : null), ("is_service_account", serviceAccount),
                    ("is_admin", platformAdmin));
                return id;
            }

            async Task Attribution(Guid user, string? code, string capture = "cookie") =>
                await Insert("user_attribution",
                    ("user_id", user), ("source_label", "hackernews"), ("utm_content", code), ("capture", capture), ("created_at", now));

            async Task Profile(Guid user, bool answered = false, bool skipped = false) =>
                await Insert("user_onboarding_profiles",
                    ("user_id", user), ("suggested_persona", "creator"),
                    ("persona", answered ? "creator" : null), ("material", answered ? "footage" : null),
                    ("answered_at", answered ? now : null), ("skipped_at", skipped ? now : null),
                    ("created_at", now), ("updated_at", now));

            async Task Project(Guid owner) =>
                await Insert("projects", ("id", Id()), ("owner_id", owner), ("created_at", now));

            async Task<Guid> Execution(Guid user)
            {
                Guid id = Id();
                await Insert("workflow_executions", ("id", id), ("initiated_by_user_id", user), ("status", "Passed"), ("queued_at", now));
                return id;
            }

            async Task StepResult(Guid execution, string status, string? outputKey) =>
                await Insert("workflow_step_results",
                    ("id", Id()), ("workflow_execution_id", execution), ("status", status),
                    ("output_storage_key", outputKey), ("completed_at", now));

            async Task Paid(Guid user) =>
                await Insert("product_events",
                    ("user_id", user), ("organization_id", Id()), ("event", "alpha.first_payment"), ("source", "server"), ("occurred_at", now));

            string VideoKey(Guid execution) => $"projects/{Id()}/outputFiles/{execution}/{Id():N}/out.mp4";

            // a1 (hn-launch): the whole path.
            Guid a1 = await User(Ago(5), verified: true);
            await Attribution(a1, "hn-launch");
            await Profile(a1, answered: true);
            await Project(a1);
            Guid e1 = await Execution(a1);
            await StepResult(e1, "Completed", VideoKey(e1));
            await Paid(a1);

            // a2 (hn-launch, captured from the URL): verified, skipped the questions, ran, never finished a video.
            Guid a2 = await User(Ago(4), verified: true);
            await Attribution(a2, "hn-launch", capture: "url");
            await Profile(a2, skipped: true);
            await Project(a2);
            Guid e2 = await Execution(a2);
            await StepResult(e2, "Completed", $"projects/{Id()}/outputFiles/{e2}/step-1-frame.png");
            await StepResult(e2, "Failed", VideoKey(e2));

            // a3 (hn-launch): signed up and stopped; the profile row holds only the landing page's suggestion.
            Guid a3 = await User(Ago(3), verified: false);
            await Attribution(a3, "hn-launch");
            await Profile(a3);

            // a4 (hn-launch): signed up before the window and did everything; not in the cohort.
            Guid a4 = await User(Ago(40), verified: true);
            await Attribution(a4, "hn-launch");
            await Profile(a4, answered: true);
            await Project(a4);
            Guid e4 = await Execution(a4);
            await StepResult(e4, "Completed", VideoKey(e4));
            await Paid(a4);

            // a5 (yt-demo): verified and answered, nothing else.
            Guid a5 = await User(Ago(2), verified: true);
            await Attribution(a5, "yt-demo");
            await Profile(a5, answered: true);

            // a6: no attribution row at all (never consented, URL capture off). Unattributed.
            Guid a6 = await User(Ago(2), verified: true);
            await Project(a6);

            // a7: an attribution row with no link code (came from a plain referrer). Unattributed; paid.
            Guid a7 = await User(Ago(1), verified: false);
            await Attribution(a7, null);
            await Paid(a7);

            // a8: a code no link has (a hand-written URL). It belongs to no link, so it is unattributed:
            // every account of the window is in exactly one row.
            Guid a8 = await User(Ago(1), verified: true);
            await Attribution(a8, "hand-written");
            await Project(a8);

            // a9: an API-key service account. Not a person: in no count.
            Guid a9 = await User(Ago(1), verified: true, serviceAccount: true);
            await Project(a9);

            // a12 / a13: platform admins, the operator's own accounts (the seeded first admin is one). One
            // followed the hn-launch link and did everything, one has no attribution. Not acquired: in no count.
            Guid a12 = await User(Ago(3), verified: true, platformAdmin: true);
            await Attribution(a12, "hn-launch");
            await Profile(a12, answered: true);
            await Project(a12);
            Guid e12 = await Execution(a12);
            await StepResult(e12, "Completed", VideoKey(e12));
            await Paid(a12);
            Guid a13 = await User(Ago(2), verified: true, platformAdmin: true);
            await Project(a13);

            // a10 / a11 (yt-demo): one minute inside and one minute outside the window's first instant.
            Guid a10 = await User(windowStart.AddMinutes(1), verified: false);
            await Attribution(a10, "yt-demo");
            Guid a11 = await User(windowStart.AddMinutes(-1), verified: true);
            await Attribution(a11, "yt-demo");

            await Exec(c, "RESET session_replication_role");

            // ---- read it the way the admin API does ----
            TrackedLinkFunnel funnel;
            await using (InferenceApiDbContext db = NewDb(connectionString))
            {
                funnel = await new TrackedLinkFunnelReader(db).ReadAsync(sinceDay, CancellationToken.None);
            }

            funnel.Links.Select(l => l.Code).Should().Equal(new[] { "empty", "hn-launch", "yt-demo" }, "newest link first");

            TrackedLinkFunnelRow hnRow = funnel.Links.Single(l => l.Code == "hn-launch");
            hnRow.LinkId.Should().Be(hn);
            hnRow.Channel.Should().Be("hackernews");
            hnRow.Medium.Should().Be("community");
            hnRow.Campaign.Should().Be("launch-2026-11");
            hnRow.Placement.Should().Be("where hn-launch is posted");
            hnRow.DestinationPath.Should().Be("/for/creators");
            hnRow.Archived.Should().BeFalse();
            hnRow.CostMinor.Should().Be(2500);
            hnRow.CostCurrency.Should().Be("USD");
            hnRow.Clicks.Should().Be(8, "5 today and 3 on the window's first day; the 100 of the day before are outside");
            hnRow.BotHits.Should().Be(3);
            hnRow.Visitors.Should().Be(2, "the visitor first seen 40 days ago is outside the window");
            hnRow.Accounts.Should().Be(
                new FunnelAccountSteps(Signups: 3, Verified: 2, Onboarded: 1, FirstProject: 2, FirstRun: 2, FirstVideo: 1, Paid: 1),
                "a1 a2 a3 signed up in the window (a4 did 40 days ago; a12 is a platform admin); a skip is not an answer; a frame image and a Failed step are not a finished video");

            TrackedLinkFunnelRow ytRow = funnel.Links.Single(l => l.Code == "yt-demo");
            ytRow.Archived.Should().BeTrue("an archived link keeps its row and its history");
            ytRow.CostMinor.Should().BeNull();
            ytRow.CostCurrency.Should().BeNull();
            ytRow.Clicks.Should().Be(7);
            ytRow.BotHits.Should().Be(0);
            ytRow.Visitors.Should().Be(1);
            ytRow.Accounts.Should().Be(
                new FunnelAccountSteps(Signups: 2, Verified: 1, Onboarded: 1, FirstProject: 0, FirstRun: 0, FirstVideo: 0, Paid: 0),
                "a5 and a10 (one minute inside the window); a11 is one minute outside it");

            TrackedLinkFunnelRow emptyRow = funnel.Links.Single(l => l.Code == "empty");
            (emptyRow.Clicks, emptyRow.BotHits, emptyRow.Visitors).Should().Be((0L, 0L, 0L));
            emptyRow.Accounts.Should().Be(new FunnelAccountSteps(0, 0, 0, 0, 0, 0, 0), "a link nobody used still has a row of zeros");

            funnel.Unattributed.Should().Be(
                new FunnelAccountSteps(Signups: 3, Verified: 2, Onboarded: 0, FirstProject: 2, FirstRun: 0, FirstVideo: 0, Paid: 1),
                "a6 (no attribution row), a7 (no link code) and a8 (a code no link has); a9 is a service account and a13 a platform admin");

            // Nobody is left out and nobody is counted twice: the link rows and the unattributed row are the
            // cohort. It used to be one short for every account whose utm_content named no link.
            long cohort = (long)(await Scalar(c,
                "SELECT count(*) FROM application_users WHERE created_at >= $1 AND NOT is_service_account AND NOT is_admin",
                windowStart))!;
            cohort.Should().Be(8, "a1 a2 a3 a5 a6 a7 a8 a10");
            (funnel.Links.Sum(l => l.Accounts.Signups) + funnel.Unattributed.Signups).Should().Be(cohort);
            (funnel.Links.Sum(l => l.Accounts.Verified) + funnel.Unattributed.Verified).Should().Be(
                (long)(await Scalar(c,
                    "SELECT count(*) FROM application_users WHERE created_at >= $1 AND NOT is_service_account AND NOT is_admin "
                    + "AND email_verified_at IS NOT NULL",
                    windowStart))!);
            (funnel.Links.Sum(l => l.Accounts.Paid) + funnel.Unattributed.Paid).Should().Be(2, "a1 and a7; a4 is outside the window and a12 an admin");

            // A one-day window is today only.
            await using (InferenceApiDbContext db = NewDb(connectionString))
            {
                TrackedLinkFunnel todayOnly = await new TrackedLinkFunnelReader(db).ReadAsync(today, CancellationToken.None);
                todayOnly.Links.Single(l => l.Code == "hn-launch").Clicks.Should().Be(5);
                todayOnly.Links.Single(l => l.Code == "yt-demo").Clicks.Should().Be(0);
            }

            // ---- the same statement as the BI role: every column it reads is granted ----
            await using NpgsqlConnection bi = new(connectionString);
            await bi.OpenAsync();
            await Exec(bi, "SET ROLE powerbi_ro");
            await using NpgsqlCommand asBi = new(TrackedLinkFunnelReader.FunnelSql, bi);
            asBi.Parameters.AddWithValue("since", windowStart);
            asBi.Parameters.AddWithValue("since_day", sinceDay);
            List<(string Kind, string? Code, long Clicks, long Signups, long Paid)> rows = [];
            await using (NpgsqlDataReader reader = await asBi.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rows.Add((reader.GetString(0), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(12), reader.GetInt64(15), reader.GetInt64(21)));
                }
            }

            rows.Should().Equal(
                ("link", "empty", 0L, 0L, 0L),
                ("link", "hn-launch", 8L, 3L, 1L),
                ("link", "yt-demo", 7L, 2L, 0L),
                ("unattributed", null, 0L, 3L, 1L));

            // ---- how it is planned: every step is a set joined once, never a sub-select per account ----
            // A per-account EXISTS over workflow_executions (no index on initiated_by_user_id) is fast only
            // while the planner chooses to hash it; when it does not, it scans the table once per account.
            string plan = await ExplainAsync(c, TrackedLinkFunnelReader.FunnelSql, windowStart, sinceDay);
            plan.Should().NotContain("SubPlan", "the statement must not evaluate a sub-select per row:\n" + plan);
            plan.Should().Contain("workflow_executions").And.Contain("workflow_step_results");
        }, engine: true);
    }

    /// <summary>The plan of <paramref name="sql"/> as EXPLAIN prints it, one node per line.</summary>
    private static async Task<string> ExplainAsync(NpgsqlConnection c, string sql, DateTime since, DateOnly sinceDay)
    {
        await using NpgsqlCommand explain = new("EXPLAIN (COSTS OFF) " + sql, c);
        explain.Parameters.AddWithValue("since", since);
        explain.Parameters.AddWithValue("since_day", sinceDay);
        List<string> lines = [];
        await using NpgsqlDataReader reader = await explain.ExecuteReaderAsync();
        while (await reader.ReadAsync()) lines.Add(reader.GetString(0));
        return string.Join('\n', lines);
    }

    [PostgresFact]
    public async Task The_funnel_of_a_database_with_no_links_and_no_accounts_is_empty_with_a_zero_unattributed_row()
    {
        await WithDatabase(async (connectionString, _) =>
        {
            await using InferenceApiDbContext db = NewDb(connectionString);
            TrackedLinkFunnel funnel = await new TrackedLinkFunnelReader(db).ReadAsync(new DateOnly(2026, 10, 13), CancellationToken.None);
            funnel.Links.Should().BeEmpty();
            funnel.Unattributed.Should().Be(new FunnelAccountSteps(0, 0, 0, 0, 0, 0, 0));
        }, engine: true);
    }
}
