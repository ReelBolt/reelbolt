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
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.PublicApi;

/// <summary>
/// The public-api migration chain on a fresh real Postgres (skipped unless <c>REELBOLT_TEST_PG</c> is set):
/// the API and the engine context both migrate to head with their production history tables, the seven API
/// migrations and the three engine migrations that make up public-api are in the history in order, and the
/// chain reaches the pre-public-api state on a Down and returns on the next Up.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class PublicApiMigrationsPostgresTests
{
    /// <summary>The seven API migrations of public-api, oldest first.</summary>
    private static readonly string[] ApiMigrations =
    [
        "20261103083606_AddPublicApiKeys",
        "20261103084723_AddApiKeySpend",
        "20261103085312_AddPublicApiPlumbing",
        "20261103085812_AddApiRunRequests",
        "20261103090253_AddWebhooks",
        "20261103090842_AddWebhookDeliveryProjectId",
        "20261103100000_ConvergePowerBiReadOnlyRoleGrants"
    ];

    /// <summary>The three engine migrations of public-api, oldest first.</summary>
    private static readonly string[] EngineMigrations =
    [
        "20261103084728_AddExecutionApiKeyId",
        "20261103090850_AddApiRunCorrelationIndex",
        "20261103100000_ConvergePowerBiReadOnlyRoleEngineGrants"
    ];

    /// <summary>All ten, in the ordinal order <see cref="PublicApiHistoryAsync"/> returns them.</summary>
    private static readonly string[] PublicApiMigrations =
        [.. ApiMigrations.Concat(EngineMigrations).OrderBy(id => id, StringComparer.Ordinal)];

    /// <summary>The API migration applied just before <c>AddPublicApiKeys</c> (master's last).</summary>
    private const string ApiBeforePublicApi = "20261102181832_AddMarketingContactNamesAndSync";

    /// <summary>The engine migration applied just before <c>AddExecutionApiKeyId</c>.</summary>
    private const string EngineBeforePublicApi = "20261102173831_AddAudioCleanupStep";

    private const string ApiHistory = "__EFMigrationsHistory_Api";
    private const string EngineHistory = "__EFMigrationsHistory_Workflow";

    [PostgresFact]
    public async Task Both_contexts_migrate_to_head_and_the_ten_public_api_migrations_roll_back_and_forward()
    {
        string baseConnection = PostgresTestEnvironment.BaseConnectionString()!;
        string dbName = "reelbolt_public_api_migrations_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            string connection = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
            // The history tables are the ones the services configure, so the rows asserted below live where
            // production would put them. The API runs first: its InitialCreate owns projects and
            // agent_definitions, which the engine's first migration references.
            DbContextOptions<InferenceApiDbContext> apiOptions = new DbContextOptionsBuilder<InferenceApiDbContext>()
                .UseNpgsql(connection, o => o.MigrationsHistoryTable(ApiHistory)).Options;
            DbContextOptions<WorkflowEngineDbContext> engineOptions = new DbContextOptionsBuilder<WorkflowEngineDbContext>()
                .UseNpgsql(connection, o => o.MigrationsHistoryTable(EngineHistory)).Options;

            await MigrateApiAsync(apiOptions, target: null);
            await MigrateEngineAsync(engineOptions, target: null);

            (await PublicApiHistoryAsync(connection)).Should().Equal(PublicApiMigrations,
                because: "all ten public-api migrations apply to a fresh database, in timestamp order");

            // --- Down to the pre-public-api migration ------------------------------------------------
            await MigrateApiAsync(apiOptions, ApiBeforePublicApi);
            await MigrateEngineAsync(engineOptions, EngineBeforePublicApi);

            (await HistoryAsync(connection, ApiHistory, ApiMigrations)).Should().BeEmpty(
                because: "the seven API migrations rolled back");
            (await HistoryAsync(connection, EngineHistory, EngineMigrations)).Should().BeEmpty(
                because: "the three engine migrations rolled back");

            // --- Up again ---------------------------------------------------------------------------
            await MigrateApiAsync(apiOptions, target: null);
            await MigrateEngineAsync(engineOptions, target: null);

            (await PublicApiHistoryAsync(connection)).Should().Equal(PublicApiMigrations,
                because: "the same ten apply again after the rollback");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task MigrateApiAsync(DbContextOptions<InferenceApiDbContext> options, string? target)
    {
        await using InferenceApiDbContext db = new(options);
        if (target is null)
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.GetService<IMigrator>().MigrateAsync(target);
        }
    }

    private static async Task MigrateEngineAsync(DbContextOptions<WorkflowEngineDbContext> options, string? target)
    {
        await using WorkflowEngineDbContext db = new(options);
        if (target is null)
        {
            await db.Database.MigrateAsync();
        }
        else
        {
            await db.GetService<IMigrator>().MigrateAsync(target);
        }
    }

    /// <summary>The expected public-api ids that are present in either history table, in chronological order.</summary>
    private static async Task<List<string>> PublicApiHistoryAsync(string connection)
    {
        List<string> api = await HistoryAsync(connection, ApiHistory, PublicApiMigrations);
        List<string> engine = await HistoryAsync(connection, EngineHistory, PublicApiMigrations);
        return [.. api.Concat(engine).OrderBy(id => id, StringComparer.Ordinal)];
    }

    private static async Task<List<string>> HistoryAsync(string connection, string table, IEnumerable<string> candidates)
    {
        HashSet<string> wanted = [.. candidates];
        await using NpgsqlConnection conn = new(connection);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = new($"SELECT \"MigrationId\" FROM \"{table}\"", conn);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        List<string> found = [];
        while (await reader.ReadAsync())
        {
            string id = reader.GetString(0);
            if (wanted.Contains(id)) found.Add(id);
        }

        return found;
    }
}
