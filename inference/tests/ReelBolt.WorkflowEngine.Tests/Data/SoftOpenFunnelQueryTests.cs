using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Inference.Api.Data.BiReadOnlyRole;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Data.BiReadOnlyRole;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// launch-soft-open T-003: <c>infra/powerbi/soft-open-funnel.sql</c> is the owner's no-code read of the
/// soft-open funnel (the soft-open funnel document), so the file the doc points at is the file this
/// test runs. It migrates a throwaway database in the compose order (Inference API, then engine), applies the
/// operator script's two grant blocks, seeds six people at different depths of the funnel, then runs the WHOLE
/// file as <c>powerbi_ro</c> (<c>SET ROLE</c>). Two things are proven at once: the query's numbers are what the
/// seeded data says (counts per step and per day, the cohort conversion, plan against top-up money, the
/// visitor floor, the consent split), and every column it reads is one the BI role is actually granted, so a
/// grant change that breaks the owner's page fails the build instead of the page.
/// <para>
/// Rows are written with SQL and foreign keys off (<c>session_replication_role = replica</c>, which the
/// superuser test connection may set): this test is about what the query means, not how the rows were written.
/// Every NOT NULL column the test does not name is filled from its data type, so a column added to one of the
/// tables later does not break the seeding. Follows <see cref="ProductEventsFunnelQueryTests"/>.
/// </para>
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class SoftOpenFunnelQueryTests
{
    private const string ScriptRelativePath = "infra/powerbi/soft-open-funnel.sql";

    /// <summary>A SQL expression passed through as written (a timestamp offset), not as a bound value.</summary>
    private sealed record Raw(string Sql);

    [PostgresFact]
    public async Task The_soft_open_funnel_file_runs_as_powerbi_ro_and_counts_each_step_per_day_and_per_cohort()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_soft_open_funnel_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            await using (InferenceApiDbContext api = new(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            await using (WorkflowEngineDbContext engine = new(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(csb.ConnectionString).Options))
            {
                await engine.GetService<IMigrator>().MigrateAsync();
            }

            await using NpgsqlConnection c = new(csb.ConnectionString);
            await c.OpenAsync();

            async Task Exec(string sql)
            {
                await using NpgsqlCommand cmd = new(sql, c);
                await cmd.ExecuteNonQueryAsync();
            }

            // The role comes from the operator script, not from a migration.
            await Exec(PowerBiReadOnlyRoleSql.Up);
            await Exec(PowerBiReadOnlyRoleEngineSql.Up);

            // Seed without foreign keys: the rows are about meaning, not referential integrity.
            await Exec("SET session_replication_role = replica");

            async Task<Dictionary<string, (string DataType, string? Default, bool Identity)>> RequiredColumns(string table)
            {
                Dictionary<string, (string, string?, bool)> required = new();
                await using NpgsqlCommand cmd = new(
                    "SELECT column_name, data_type, column_default, is_identity = 'YES' "
                    + "FROM information_schema.columns "
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
                List<string> columns = new();
                List<string> expressions = new();
                List<NpgsqlParameter> parameters = new();
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
                    if (columns.Contains(column) || identity || columnDefault is not null)
                    {
                        continue;
                    }

                    columns.Add(column);
                    expressions.Add(Filler(dataType));
                }

                await using NpgsqlCommand insert = new(
                    $"INSERT INTO {table} ({string.Join(", ", columns.Select(x => '"' + x + '"'))}) VALUES ({string.Join(", ", expressions)})", c);
                insert.Parameters.AddRange(parameters.ToArray());
                await insert.ExecuteNonQueryAsync();
            }

            static Raw Ago(int days) => new($"now() - interval '{days} days'");

            Guid Id() => Guid.NewGuid();
            Guid u1 = Id(), u2 = Id(), u3 = Id(), u4 = Id(), u5 = Id(), u6 = Id();
            Guid orgA = Id(), orgB = Id();

            async Task User(Guid id, int signedUpDaysAgo, int? verifiedDaysAgo) =>
                await Insert("application_users",
                    ("id", id),
                    ("email", id.ToString("N") + "@example.test"),
                    ("created_at", Ago(signedUpDaysAgo)),
                    ("email_verified_at", verifiedDaysAgo is { } v ? Ago(v) : null));

            async Task Project(Guid owner, int daysAgo) =>
                await Insert("projects", ("id", Id()), ("owner_id", owner), ("created_at", Ago(daysAgo)));

            async Task Execution(Guid executionId, Guid user, int queuedDaysAgo) =>
                await Insert("workflow_executions",
                    ("id", executionId),
                    ("initiated_by_user_id", user),
                    ("status", "Passed"),
                    ("queued_at", Ago(queuedDaysAgo)));

            async Task StepResult(Guid executionId, string status, string? outputKey, int completedDaysAgo) =>
                await Insert("workflow_step_results",
                    ("id", Id()),
                    ("workflow_execution_id", executionId),
                    ("status", status),
                    ("output_storage_key", outputKey),
                    ("completed_at", Ago(completedDaysAgo)));

            async Task Event(Guid? user, string name, string source, int daysAgo, Guid? visitor = null, string? detail = null) =>
                await Insert("product_events",
                    ("user_id", user),
                    ("event", name),
                    ("source", source),
                    ("occurred_at", Ago(daysAgo)),
                    ("visitor_id", visitor),
                    ("detail", detail));

            string videoKey(Guid exec) => $"projects/{Id()}/outputFiles/{exec}/{Id():N}/out.mp4";

            // u1: the whole path. Signed up 5 days ago, verified, project, run, finished video, downloaded,
            //     checkout, paid. Telemetry granted.
            await User(u1, 5, 5);
            await Project(u1, 5);
            Guid e1 = Id();
            await Execution(e1, u1, 5);
            await StepResult(e1, "Completed", videoKey(e1), 5);
            await Event(u1, "activation.output_downloaded", "server", 4);
            await Event(u1, "billing.checkout_started", "server", 3);
            await Event(u1, "alpha.first_payment", "server", 3);
            await Event(u1, "telemetry.consent.granted", "consent", 5);

            // u2: ran, but never finished a video: a frame image, and a video key on a step that Failed.
            await User(u2, 4, 4);
            await Project(u2, 4);
            Guid e2 = Id();
            await Execution(e2, u2, 4);
            await StepResult(e2, "Completed", $"projects/{Id()}/outputFiles/{e2}/step-1-frame.png", 4);
            await StepResult(e2, "Failed", videoKey(e2), 4);

            // u3: signed up and stopped, never verified.
            await User(u3, 4, null);

            // u4: signed up 40 days ago (outside the cohort window); made a project 2 days ago (inside the day window).
            await User(u4, 40, 40);
            await Project(u4, 2);

            // u5: verified by OAuth (no verification event), declined telemetry, did nothing else.
            await User(u5, 3, 3);
            await Event(u5, "telemetry.consent.declined", "consent", 3);

            // u6: verified, opened checkout, never paid.
            await User(u6, 3, 3);
            await Event(u6, "billing.checkout_started", "server", 2);

            // Money: a plan payment and a top-up today, a plan payment yesterday; one plan payment 60 days ago.
            await Insert("billing_payments",
                ("id", Id()), ("provider", "Stripe"), ("provider_transaction_id", "in_1"), ("provider_subscription_id", "sub_1"),
                ("organization_id", orgA), ("amount_minor", 2900L), ("currency_code", "USD"), ("occurred_at", Ago(0)));
            await Insert("billing_payments",
                ("id", Id()), ("provider", "Stripe"), ("provider_transaction_id", "pi_1"), ("provider_subscription_id", null),
                ("organization_id", orgB), ("amount_minor", 1450L), ("currency_code", "USD"), ("occurred_at", Ago(0)));
            await Insert("billing_payments",
                ("id", Id()), ("provider", "Stripe"), ("provider_transaction_id", "in_0"), ("provider_subscription_id", "sub_1"),
                ("organization_id", orgA), ("amount_minor", 2900L), ("currency_code", "USD"), ("occurred_at", Ago(1)));
            await Insert("billing_payments",
                ("id", Id()), ("provider", "Stripe"), ("provider_transaction_id", "in_old"), ("provider_subscription_id", "sub_0"),
                ("organization_id", orgA), ("amount_minor", 9900L), ("currency_code", "USD"), ("occurred_at", Ago(60)));

            // Visitors: two distinct visitors, one of them twice, and one signup.started; one old row.
            Guid v1 = Id(), v2 = Id();
            await Event(null, "site.visit", "client", 2, v1, "google");
            await Event(null, "site.visit", "client", 2, v1, "google");
            await Event(null, "site.visit", "client", 2, v2, "direct");
            await Event(null, "signup.started", "client", 2, v1, "google");
            await Event(null, "site.visit", "client", 90, Id(), "old");

            // ---- run the file as the BI role ---------------------------------------------------------
            await Exec("RESET session_replication_role");
            string script = File.ReadAllText(Path.Combine(RepositoryRoot(), ScriptRelativePath));
            DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

            await using NpgsqlConnection bi = new(csb.ConnectionString);
            await bi.OpenAsync();
            await using (NpgsqlCommand setRole = new("SET ROLE powerbi_ro", bi))
            {
                await setRole.ExecuteNonQueryAsync();
            }

            await using NpgsqlCommand run = new(script, bi);
            await using NpgsqlDataReader reader = await run.ExecuteReaderAsync();

            // 1. The funnel.
            List<(string View, DateOnly? Day, int StepNo, string Step, long Users, decimal? OfSignups, decimal? OfPrevious)> funnel = new();
            while (await reader.ReadAsync())
            {
                funnel.Add((
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : DateOnly.FromDateTime(reader.GetDateTime(1)),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                    reader.IsDBNull(6) ? null : reader.GetDecimal(6)));
            }

            var cohort = funnel.Where(r => r.View == "cohort").OrderBy(r => r.StepNo).ToList();
            cohort.Select(r => (r.Step, r.Users)).Should().Equal(
                ("signed_up", 5L),               // u1 u2 u3 u5 u6; u4 signed up 40 days ago
                ("email_verified", 4L),          // u1 u2 u5 u6
                ("first_project", 2L),           // u1 u2
                ("first_run_started", 2L),       // u1 u2
                ("first_video_finished", 1L),    // u1 only: u2 has a frame image and a Failed step
                ("first_output_downloaded", 1L),
                ("checkout_started", 2L),        // u1 u6
                ("payment_succeeded", 1L));      // u1
            cohort[0].OfSignups.Should().Be(100.0m);
            cohort[0].OfPrevious.Should().BeNull("the first step has no step before it");
            cohort[7].OfSignups.Should().Be(20.0m, "1 of 5 signups paid");
            cohort[2].OfPrevious.Should().Be(50.0m, "2 of the 4 verified people made a project");
            cohort[6].OfPrevious.Should().Be(200.0m, "steps are independent ever-reached counts: 2 checkouts after 1 download is 200 percent of the step before");

            var byDay = funnel.Where(r => r.View == "by_day").ToList();
            long UsersOn(string step, int daysAgo) =>
                byDay.Where(r => r.Step == step && r.Day == today.AddDays(-daysAgo)).Select(r => r.Users).DefaultIfEmpty(0).Sum();
            UsersOn("signed_up", 5).Should().Be(1);
            UsersOn("signed_up", 4).Should().Be(2, "u2 and u3");
            UsersOn("signed_up", 3).Should().Be(2, "u5 and u6");
            byDay.Any(r => r.Step == "signed_up" && r.Day == today.AddDays(-40)).Should().BeFalse("outside the window");
            UsersOn("first_project", 2).Should().Be(1, "u4's project counts on its day even though u4 signed up before the window");
            UsersOn("first_video_finished", 5).Should().Be(1);
            UsersOn("payment_succeeded", 3).Should().Be(1);
            byDay.Where(r => r.Step == "first_video_finished").Sum(r => r.Users).Should().Be(1);

            // 2. Payments: plan against top-up.
            (await reader.NextResultAsync()).Should().BeTrue();
            List<(DateOnly Day, string Kind, long Payments, long Organizations, long AmountMinor)> money = new();
            while (await reader.ReadAsync())
            {
                money.Add((
                    DateOnly.FromDateTime(reader.GetDateTime(0)),
                    reader.GetString(1),
                    reader.GetInt64(4),
                    reader.GetInt64(5),
                    reader.GetInt64(6)));
            }

            money.Should().BeEquivalentTo(new[]
            {
                (today.AddDays(-1), "plan", 1L, 1L, 2900L),
                (today, "plan", 1L, 1L, 2900L),
                (today, "top_up", 1L, 1L, 1450L),
            }, "the 60-day-old payment is outside the window");

            // 3. Visitors: a floor, distinct per day and label.
            (await reader.NextResultAsync()).Should().BeTrue();
            List<(string Event, string Label, long Events, long Visitors)> visits = new();
            while (await reader.ReadAsync())
            {
                visits.Add((reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4)));
            }

            visits.Should().BeEquivalentTo(new[]
            {
                ("signup.started", "google", 1L, 1L),
                ("site.visit", "direct", 1L, 1L),
                ("site.visit", "google", 2L, 1L),
            }, "two google visit rows are one visitor; the 90-day-old row is outside the window");

            // 4. Telemetry choice of the people who signed up in the window.
            (await reader.NextResultAsync()).Should().BeTrue();
            Dictionary<string, long> consent = new();
            while (await reader.ReadAsync())
            {
                consent[reader.GetString(0)] = reader.GetInt64(1);
            }

            consent.Should().BeEquivalentTo(new Dictionary<string, long>
            {
                ["granted"] = 1,
                ["declined"] = 1,
                ["never_asked"] = 3,
            });
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource dropAdmin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = dropAdmin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void The_file_reads_only_the_tables_and_never_a_withheld_column()
    {
        // Comments name the withheld columns on purpose (they say what the file avoids); only code counts.
        string script = string.Join('\n', File.ReadAllLines(Path.Combine(RepositoryRoot(), ScriptRelativePath))
            .Where(line => !line.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        foreach (string withheld in new[]
        {
            "password_hash", "signup_ip", "user_request", "error_message", "result_json", "error_details",
            "input_json", "output_json", "api_key_encrypted", "system_prompt", "feedback_reports",
        })
        {
            script.Should().NotContain(withheld, "the BI role is not granted it and the funnel must never need it");
        }

        script.Should().NotMatchRegex(@"(?im)^\s*(insert|update|delete|drop|alter|create|grant|truncate)\b",
            "the file is read-only reference queries");
    }

    private static string RepositoryRoot()
    {
        for (string? dir = Path.GetDirectoryName(typeof(SoftOpenFunnelQueryTests).Assembly.Location); dir != null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "inference", "ReelBolt.sln")))
            {
                return dir;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
