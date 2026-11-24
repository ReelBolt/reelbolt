using System;
using System.Collections.Generic;
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

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// Release-blocker regression: a database migrated from zero, on a cluster where the <c>powerbi_ro</c>
/// role already exists, must reach head for both contexts in deploy order (API first, engine after).
/// <para>
/// Roles are cluster-wide, but migrations are per-database: e2e stacks and new installs run the
/// operator script before the first migration, so the grant migrations really do see
/// <c>powerbi_ro</c> on a database whose tables are still empty. The original grant migrations are
/// frozen to the text they had when they were created; the head convergence migrations re-apply the
/// current grant set. Referencing the live registry constants from the old migrations aborted here
/// with 42703, because the constants name columns (<c>application_users.is_service_account</c>,
/// <c>workflow_executions.api_key_id</c>) that only a later migration creates.
/// </para>
/// <para>
/// The role is cluster-wide and must not be dropped at the end: another test or the operator script
/// may own it. It is created <c>NOLOGIN</c> only when missing, and the per-run database is dropped
/// with <c>FORCE</c> in <c>finally</c>.
/// </para>
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class PowerBiReadOnlyGrantsFreshDatabaseMigrationTests
{
    private const string Role = "powerbi_ro";

    [PostgresFact]
    public async Task FreshDatabase_MigratesFromZero_WithClusterWideRolePresent()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_powerbi_fresh_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);

        // Cluster-wide and intentionally not dropped at the end.
        await using (NpgsqlCommand ensureRole = admin.CreateCommand(
            $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{Role}') THEN CREATE ROLE {Role} NOLOGIN; END IF; END $$;"))
        {
            await ensureRole.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        string connectionString = PostgresTestEnvironment.ForDatabase(baseConnection, dbName);
        try
        {
            // Compose startup order: the Inference API migrates first, then the engine.
            await using (InferenceApiDbContext api = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(connectionString).Options))
            {
                await api.GetService<IMigrator>().MigrateAsync();
                (await api.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            }

            await using WorkflowEngineDbContext engine = new WorkflowEngineDbContext(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(connectionString).Options);
            await engine.GetService<IMigrator>().MigrateAsync();
            (await engine.Database.GetPendingMigrationsAsync()).Should().BeEmpty();

            await using NpgsqlConnection c = new NpgsqlConnection(connectionString);
            await c.OpenAsync();

            async Task<bool> HasColumn(string table, string column)
            {
                await using NpgsqlCommand cmd = new NpgsqlCommand(
                    $"SELECT has_column_privilege('{Role}', '{table}', '{column}', 'SELECT')", c);
                return (bool)(await cmd.ExecuteScalarAsync())!;
            }

            // The columns the frozen migrations could not name are granted at head.
            (await HasColumn("application_users", "is_service_account")).Should().BeTrue();
            (await HasColumn("application_users", "locale")).Should().BeTrue();
            (await HasColumn("workflow_executions", "api_key_id")).Should().BeTrue();
            (await HasColumn("workflow_executions", "first_started_at")).Should().BeTrue();
            (await HasColumn("workflow_executions", "attention_json")).Should().BeTrue();

            // ... and the withdrawn column the creation-date grant still named is withdrawn again.
            (await HasColumn("agent_definitions", "config_json")).Should().BeFalse();
            (await HasColumn("workflow_executions", "style_config_json")).Should().BeFalse();
        }
        finally
        {
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
