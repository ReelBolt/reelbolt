using System;
using System.IO;
using System.Linq;
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
using ReelBolt.Inference.Api.Data.BiReadOnlyRole;
using ReelBolt.Inference.Api.Services.Telemetry;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;
using static ReelBolt.WorkflowEngine.Tests.Data.AcquisitionQueryFiles;

namespace ReelBolt.WorkflowEngine.Tests.Services.Telemetry;

/// <summary>security-headers T-003: the aggregate table, its one-statement upsert, the overflow fold, the sweep and the read file.</summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class CspReportPostgresTests
{
    private static readonly DateOnly Today = new(2026, 11, 25);

    private static async Task WithDatabase(Func<string, Task> body)
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_csp_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        string cs = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
        try
        {
            await using InferenceApiDbContext db = new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(cs).Options);
            await db.GetService<IMigrator>().MigrateAsync();
            await body(cs);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlDataSource dropAdmin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
            await using NpgsqlCommand drop = dropAdmin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static InferenceApiDbContext Db(string cs) =>
        new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(cs).Options);

    private static CspViolationKey Key(string origin = "https://cdn.example.com", string path = "/pricing", string ua = "chrome") =>
        new("csp", "report", "img-src", origin, path, ua);

    private static async Task<long> Scalar(string cs, string sql)
    {
        await using NpgsqlConnection c = new(cs);
        await c.OpenAsync();
        await using NpgsqlCommand cmd = new(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [PostgresFact]
    public async Task The_upsert_counts_a_repeated_key_in_one_row_and_keeps_distinct_keys_apart()
    {
        await WithDatabase(async cs =>
        {
            await using InferenceApiDbContext db = Db(cs);
            CspReportStore store = new(db);
            for (int i = 0; i < 3; i++) await store.RecordAsync(Key(), Today, 2000, CancellationToken.None);
            await store.RecordAsync(Key(ua: "firefox"), Today, 2000, CancellationToken.None);
            await store.RecordAsync(Key(), Today.AddDays(1), 2000, CancellationToken.None);

            (await Scalar(cs, "SELECT count(*) FROM csp_violation_counts")).Should().Be(3);
            (await Scalar(cs, "SELECT count FROM csp_violation_counts WHERE ua_family = 'chrome' AND day = '2026-11-25'")).Should().Be(3);
        });
    }

    [PostgresFact]
    public async Task Past_the_daily_cap_new_keys_fold_into_the_overflow_row_but_known_keys_still_count()
    {
        await WithDatabase(async cs =>
        {
            await using InferenceApiDbContext db = Db(cs);
            CspReportStore store = new(db);
            for (int i = 0; i < 3; i++) await store.RecordAsync(Key(path: "/p" + i), Today, 3, CancellationToken.None);
            for (int i = 0; i < 5; i++) await store.RecordAsync(Key(path: "/hostile-" + i), Today, 3, CancellationToken.None);
            await store.RecordAsync(Key(path: "/p0"), Today, 3, CancellationToken.None);

            (await Scalar(cs, "SELECT count(*) FROM csp_violation_counts WHERE day = '2026-11-25'")).Should().Be(4, "three keys plus one overflow row");
            (await Scalar(cs, "SELECT count FROM csp_violation_counts WHERE document_path = '(overflow)' AND blocked_origin = '(overflow)'")).Should().Be(5);
            (await Scalar(cs, "SELECT count FROM csp_violation_counts WHERE document_path = '/p0'")).Should().Be(2, "an existing key is never folded");
            (await Scalar(cs, "SELECT count(*) FROM csp_violation_counts WHERE document_path LIKE '/hostile%'")).Should().Be(0);

            // The next day has its own budget.
            await store.RecordAsync(Key(path: "/hostile-9"), Today.AddDays(1), 3, CancellationToken.None);
            (await Scalar(cs, "SELECT count(*) FROM csp_violation_counts WHERE document_path = '/hostile-9'")).Should().Be(1);
        });
    }

    [PostgresFact]
    public async Task The_table_has_exactly_the_allowlisted_columns()
    {
        await WithDatabase(async cs =>
        {
            await using NpgsqlConnection c = new(cs);
            await c.OpenAsync();
            await using NpgsqlCommand cmd = new(
                "SELECT string_agg(column_name, ',' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_name = 'csp_violation_counts'", c);
            (await cmd.ExecuteScalarAsync()).Should().Be(
                "day,report_type,disposition,effective_directive,blocked_origin,document_path,ua_family,count");
        });
    }

    [PostgresFact]
    public async Task The_sweep_deletes_rows_older_than_the_retention_window()
    {
        await WithDatabase(async cs =>
        {
            await using (InferenceApiDbContext db = Db(cs))
            {
                CspReportStore store = new(db);
                await store.RecordAsync(Key(), Today.AddDays(-91), 2000, CancellationToken.None);
                await store.RecordAsync(Key(), Today.AddDays(-89), 2000, CancellationToken.None);
                await store.RecordAsync(Key(), Today, 2000, CancellationToken.None);
            }

            ServiceCollection services = new();
            services.AddDbContext<InferenceApiDbContext>(o => o.UseNpgsql(cs));
            await using ServiceProvider sp = services.BuildServiceProvider();
            CspReportRetentionService sweep = new(
                sp.GetRequiredService<IServiceScopeFactory>(), Options.Create(new AlphaTelemetryOptions()), NullLogger<CspReportRetentionService>.Instance);
            (await sweep.SweepOnceAsync(Today, CancellationToken.None)).Should().Be(1);
            (await Scalar(cs, "SELECT count(*) FROM csp_violation_counts")).Should().Be(2);
        });
    }

    [PostgresFact]
    public async Task The_read_file_is_one_statement_that_powerbi_ro_can_run_and_ranks_the_last_14_days()
    {
        await WithDatabase(async cs =>
        {
            await using NpgsqlConnection admin = new(cs);
            await admin.OpenAsync();
            foreach (string sql in new[] { PowerBiReadOnlyRoleSql.Up })
            {
                await using NpgsqlCommand grant = new(sql, admin);
                await grant.ExecuteNonQueryAsync();
            }

            DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
            await using (InferenceApiDbContext db = Db(cs))
            {
                CspReportStore store = new(db);
                for (int i = 0; i < 3; i++) await store.RecordAsync(Key(origin: "https://big.example.com"), today, 2000, CancellationToken.None);
                await store.RecordAsync(Key(origin: "https://small.example.com"), today.AddDays(-2), 2000, CancellationToken.None);
                await store.RecordAsync(Key(origin: "https://old.example.com"), today.AddDays(-30), 2000, CancellationToken.None);
            }

            string sqlText = Flatten(ReadFile("infra/powerbi/csp-violations.sql"));
            await using NpgsqlConnection bi = new(cs);
            await bi.OpenAsync();
            await using (NpgsqlCommand role = new("SET ROLE powerbi_ro; SET TimeZone = 'UTC'", bi))
            {
                await role.ExecuteNonQueryAsync();
            }

            string csv;
            using (TextReader reader = await bi.BeginTextExportAsync($"COPY ( {sqlText} ) TO STDOUT WITH (FORMAT csv, HEADER true)"))
            {
                csv = await reader.ReadToEndAsync();
            }

            var records = ParseCsv(csv);
            records[0].Should().Equal(
                "report_type", "disposition", "effective_directive", "blocked_origin", "document_path", "reports", "first_day", "last_day", "browsers");
            records.Skip(1).Select(r => r[3]).Should().Equal(
                new[] { "https://big.example.com", "https://small.example.com" }, "ranked by count, and the 30-day-old row is outside the window");
            records[1][5].Should().Be("3");
        });
    }
}
