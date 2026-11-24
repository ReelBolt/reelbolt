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
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// The real-Postgres proof for the <c>powerbi_ro</c> BI-extract role (initiative
/// <c>powerbi-readonly-role</c>). Migrates a throwaway database in the compose order - the Inference
/// API first, the WorkflowEngine after - then applies the operator script's two grant blocks
/// (<c>infra/powerbi/postgres-readonly-role.sql</c>: the role's lifecycle is the script's, and the
/// grant migrations are no-ops on this fresh database because the role is absent) and introspects the
/// live server: the role is genuinely read-only (no write privilege, not merely a session default),
/// the nineteen withheld names are refused at the column level (twenty table-column pairs, since
/// <c>result_json</c> is withheld on both <c>workflow_executions</c> and <c>runner_jobs</c>:
/// <c>agent_definitions.system_prompt</c> and <c>config_json</c>; <c>inference_providers.api_key_encrypted</c>
/// and <c>extra_headers_json</c>; <c>application_users.signup_ip</c> and <c>password_hash</c>;
/// <c>workflow_executions.agent_snapshot_json</c>, <c>error_message</c>, <c>result_json</c>,
/// <c>user_request</c>, <c>style_config_json</c> and <c>run_brief_json</c>;
/// <c>workflow_step_results.error_details</c>, <c>tool_calls_json</c>,
/// <c>reasoning_json</c>, <c>chat_transcript_json</c>, <c>input_json</c>, <c>output_json</c> and
/// <c>output</c>; and <c>runner_jobs.result_json</c>), and every public table is either granted or
/// explicitly excluded. The grant block's own closing assertion is re-checked here from the outside.
/// <para>
/// Follows <see cref="RunnerSchemaTests.MigrationsApply_ToAFreshPostgres_AndEnforceTheRunnerConstraints"/>:
/// a per-test database, <see cref="PostgresTestEnvironment"/>, and <c>DROP DATABASE ... WITH (FORCE)</c>
/// in <c>finally</c>.
/// </para>
/// <para>
/// The fresh database above is granted straight to the final ACL, so it can never hold a stale entry
/// for the convergence to clear - which is why three review rounds found a hole the suite stayed green
/// through. <see cref="PowerBiReadOnlyRole_ConvergesFromStaleColumnGrants_OnAMigratedDatabase"/> seeds
/// the wider, earlier grant a database could already be in and proves the normalization clears it.
/// </para>
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class PowerBiReadOnlyRolePostgresTests
{
    private const string Role = "powerbi_ro";

    /// <summary>The six privileges <c>powerbi_ro</c> must never hold on any public table.</summary>
    private static readonly string[] WritePrivileges = { "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" };

    /// <summary>The never-grant token/key tables that exist in this schema.</summary>
    private static readonly string[] SecretTables =
    {
        "platform_secrets",
        "cloud_storage_connections",
        "cloud_storage_oauth_states",
        "social_accounts",
        "social_oauth_states",
        "github_account_links",
        "github_oauth_states",
        "github_app_installations",
        "github_webhook_deliveries",
        "data_protection_keys",
        "user_tokens",
        "oauth_clients",
        "oauth_authorization_codes",
        "oauth_refresh_tokens"
    };

    [PostgresFact]
    public async Task PowerBiReadOnlyRole_IsGenuinelyReadOnly_OnAFreshDatabase()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_powerbi_test_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            // Compose startup order: the Inference API migrates first, then the engine.
            await using (InferenceApiDbContext api = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            await using WorkflowEngineDbContext engine = new WorkflowEngineDbContext(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(csb.ConnectionString).Options);
            await engine.GetService<IMigrator>().MigrateAsync();

            await using NpgsqlConnection c = new NpgsqlConnection(csb.ConnectionString);
            await c.OpenAsync();

            async Task<object?> Scalar(string sql)
            {
                await using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
                return await cmd.ExecuteScalarAsync();
            }

            async Task<List<string>> Strings(string sql)
            {
                List<string> values = new();
                await using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
                await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    values.Add(reader.GetString(0));
                }

                return values;
            }

            // Address each table by pg_class.oid: the text overload of has_table_privilege parses an
            // unquoted mixed-case name (EF's __EFMigrationsHistory) with identifier folding, folds it
            // to lower case and raises 42P01. The oid overload does no name parsing.
            async Task<bool> TablePrivilege(string table, string privilege) =>
                (bool)(await Scalar(
                    $"SELECT has_table_privilege('{Role}', c.oid, '{privilege}') "
                    + "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
                    + $"WHERE n.nspname = 'public' AND c.relname = '{table}'"))!;

            async Task<bool> ColumnPrivilege(string table, string column) =>
                (bool)(await Scalar(
                    $"SELECT has_column_privilege('{Role}', c.oid, '{column}', 'SELECT') "
                    + "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
                    + $"WHERE n.nspname = 'public' AND c.relname = '{table}'"))!;

            async Task Exec(string sql)
            {
                await using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
                await cmd.ExecuteNonQueryAsync();
            }

            // The role no longer comes from a migration: apply the operator script's two blocks, in the
            // order infra/powerbi/postgres-readonly-role.sql runs them, before the assertions. This is
            // the superuser connection, so CREATE ROLE / ALTER ROLE are permitted.
            await Exec(PowerBiReadOnlyRoleSql.Up);
            await Exec(PowerBiReadOnlyRoleEngineSql.Up);

            // --- the role exists and is hardened ---------------------------------------------------
            await using (NpgsqlCommand roleCmd = new NpgsqlCommand(
                "SELECT rolcanlogin, rolconnlimit, rolconfig FROM pg_roles WHERE rolname = 'powerbi_ro'", c))
            await using (NpgsqlDataReader reader = await roleCmd.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue("powerbi_ro must exist after the API migration");
                reader.GetBoolean(0).Should().BeTrue("the role must be able to LOGIN");
                reader.GetInt32(1).Should().Be(PowerBiReadOnlyRoleSql.ConnectionLimit, "CONNECTION LIMIT 5");
                string[] rolconfig = reader.IsDBNull(2) ? Array.Empty<string>() : (string[])reader.GetValue(2);
                rolconfig.Select(x => x.Replace(" ", string.Empty))
                    .Should().Contain("default_transaction_read_only=on", "the role's session default must be read-only");
            }

            List<string> liveTables = await Strings(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name");

            HashSet<string> granted = new(
                PowerBiReadOnlyRoleSql.FullSelectTables
                    .Concat(PowerBiReadOnlyRoleSql.ColumnSelectTables.Keys)
                    .Concat(PowerBiReadOnlyRoleEngineSql.GrantedTables),
                StringComparer.Ordinal);

            // --- SELECT on the fifteen table-wide API tables -----------------------------------------
            // No engine table is granted table-wide: each of the three carries a withheld column
            // (workflow_executions.result_json, workflow_step_results.input_json, runner_jobs.result_json),
            // so all three are column-level and probed column by column below.
            PowerBiReadOnlyRoleEngineSql.FullSelectTables.Should().BeEmpty(
                "every engine table carries a withheld column, so none may be granted table-wide");
            foreach (string table in PowerBiReadOnlyRoleSql.FullSelectTables.Concat(PowerBiReadOnlyRoleEngineSql.FullSelectTables))
            {
                (await TablePrivilege(table, "SELECT")).Should().BeTrue("powerbi_ro must be able to read {0}", table);
            }

            // The column-level grants are pinned to their final shape: agent_definitions 18,
            // workflow_executions 27 (attention_json, first_started_at and api_key_id added), workflow_step_results 29 (the third review round withheld
            // error_details, tool_calls_json and reasoning_json; the fourth withheld result_json,
            // user_request, chat_transcript_json, input_json, output_json and output; the fifth
            // withheld config_json and style_config_json) and application_users 15 (signup_ip and
            // password_hash withheld; locale and is_service_account are readable BI dimensions).
            PowerBiReadOnlyRoleSql.ColumnSelectTables["agent_definitions"]
                .Should().HaveCount(18, "agent_definitions is an 18-column grant (system_prompt and config_json withheld)");
            PowerBiReadOnlyRoleSql.ColumnSelectTables["application_users"]
                .Should().HaveCount(15, "application_users is a 15-column grant (signup_ip and password_hash withheld)");
            PowerBiReadOnlyRoleEngineSql.ColumnSelectTables.Should().ContainKey("workflow_executions");
            PowerBiReadOnlyRoleEngineSql.ColumnSelectTables.Should().ContainKey("workflow_step_results");
            PowerBiReadOnlyRoleEngineSql.ColumnSelectTables["workflow_executions"]
                .Should().HaveCount(27, "workflow_executions is a 27-column grant (agent_snapshot_json, error_message, result_json, user_request, style_config_json and run_brief_json withheld)");
            PowerBiReadOnlyRoleEngineSql.ColumnSelectTables["workflow_step_results"]
                .Should().HaveCount(29, "workflow_step_results is a 29-column grant (error_details, tool_calls_json, reasoning_json, chat_transcript_json, input_json, output_json and output withheld)");
            PowerBiReadOnlyRoleEngineSql.ColumnSelectTables["runner_jobs"]
                .Should().HaveCount(21, "runner_jobs is a 21-column grant (result_json withheld)");
            (await TablePrivilege("workflow_executions", "SELECT")).Should().BeFalse(
                "workflow_executions must be a column-level grant, not table-wide");
            (await TablePrivilege("workflow_step_results", "SELECT")).Should().BeFalse(
                "workflow_step_results must be a column-level grant, not table-wide");
            (await TablePrivilege("runner_jobs", "SELECT")).Should().BeFalse(
                "runner_jobs must be a column-level grant, not table-wide");

            // --- column grants on every partial table, in both registries ---------------------------
            foreach (KeyValuePair<string, IReadOnlyList<string>> table in
                     PowerBiReadOnlyRoleSql.ColumnSelectTables.Concat(PowerBiReadOnlyRoleEngineSql.ColumnSelectTables))
            {
                foreach (string column in table.Value)
                {
                    (await ColumnPrivilege(table.Key, column)).Should().BeTrue("powerbi_ro must read {0}.{1}", table.Key, column);
                }
            }

            // --- the nineteen withheld columns are refused at the column level -----------------------
            (await ColumnPrivilege("agent_definitions", "system_prompt")).Should().BeFalse("system_prompt must never be granted");
            (await ColumnPrivilege("agent_definitions", "config_json")).Should().BeFalse(
                "config_json is set verbatim from a caller-supplied request and a caller can paste a secret into it; it must never be granted");
            (await ColumnPrivilege("inference_providers", "api_key_encrypted")).Should().BeFalse("api_key_encrypted must never be granted");
            (await ColumnPrivilege("inference_providers", "extra_headers_json")).Should().BeFalse("extra_headers_json must never be granted");
            (await ColumnPrivilege("application_users", "signup_ip")).Should().BeFalse(
                "signup_ip is PII the BI model has no use for and must never be granted");
            (await ColumnPrivilege("application_users", "password_hash")).Should().BeFalse("password_hash must never be granted");
            (await ColumnPrivilege("workflow_executions", "agent_snapshot_json")).Should().BeFalse(
                "agent_snapshot_json serializes each AgentSnapshotEntry.SystemPrompt verbatim and must never be granted");
            (await ColumnPrivilege("workflow_executions", "error_message")).Should().BeFalse(
                "error_message is written from ex.Message and may carry a vendor error body; it must never be granted");
            (await ColumnPrivilege("workflow_executions", "result_json")).Should().BeFalse(
                "result_json is the run's prose result and can restate a secret the caller pasted; it must never be granted");
            (await ColumnPrivilege("workflow_executions", "user_request")).Should().BeFalse(
                "user_request holds the caller's request verbatim and a caller can paste a secret into it; it must never be granted");
            (await ColumnPrivilege("workflow_executions", "style_config_json")).Should().BeFalse(
                "style_config_json carries a freely written StyleConfig.Notes the prompt-injection sanitizer does not redact; it must never be granted");
            (await ColumnPrivilege("workflow_executions", "run_brief_json")).Should().BeFalse(
                "run_brief_json stores the run brief (free-text notes, the caller's file and voice ids) and is withheld like the request text; it must never be granted");
            (await ColumnPrivilege("workflow_step_results", "error_details")).Should().BeFalse(
                "error_details is written from ex.Message and may carry a vendor error body; it must never be granted");
            (await ColumnPrivilege("workflow_step_results", "tool_calls_json")).Should().BeFalse(
                "tool_calls_json serializes arbitrary tool arguments and results (a file read returns file contents) and must never be granted");
            (await ColumnPrivilege("workflow_step_results", "reasoning_json")).Should().BeFalse(
                "reasoning_json is the model's private reasoning and can restate the withheld system prompt; it must never be granted");
            (await ColumnPrivilege("workflow_step_results", "chat_transcript_json")).Should().BeFalse(
                "chat_transcript_json is the model's prose transcript and can restate a secret the caller pasted; it must never be granted");
            (await ColumnPrivilege("workflow_step_results", "input_json")).Should().BeFalse(
                "input_json composes the caller's request and can carry a pasted secret; it must never be granted");
            (await ColumnPrivilege("workflow_step_results", "output_json")).Should().BeFalse(
                "output_json is the model's prose output and can restate a secret the caller pasted; it must never be granted");
            (await ColumnPrivilege("workflow_step_results", "output")).Should().BeFalse(
                "output is the model's prose output and can restate a secret the caller pasted; it must never be granted");
            (await ColumnPrivilege("runner_jobs", "result_json")).Should().BeFalse(
                "result_json holds the runner's whole finish body, which cannot be proven secret-free; it must never be granted");

            // --- no write privilege on any public table (the migration's own assertion, from outside) ---
            List<string> writeOffenders = new();
            foreach (string table in liveTables)
            {
                foreach (string privilege in WritePrivileges)
                {
                    if (await TablePrivilege(table, privilege))
                    {
                        writeOffenders.Add($"{privilege} on {table}");
                    }
                }
            }

            writeOffenders.Should().BeEmpty("powerbi_ro must hold no write privilege on any public table");

            // --- no SELECT on the token/key tables --------------------------------------------------
            foreach (string secret in SecretTables)
            {
                liveTables.Should().Contain(secret, "the guard must actually see {0}", secret);
                (await TablePrivilege(secret, "SELECT")).Should().BeFalse("powerbi_ro must never read {0}", secret);
            }

            // --- completeness against the live schema: every public table is granted or excluded ----
            HashSet<string> excluded = new(PowerBiReadOnlyRoleGuardTests.Excluded.Keys, StringComparer.Ordinal);
            excluded.Add("__EFMigrationsHistory"); // EF's own bookkeeping table; never granted.
            List<string> unclassified = liveTables.Where(t => !granted.Contains(t) && !excluded.Contains(t)).ToList();
            unclassified.Should().BeEmpty(
                "live public table(s) {0} are neither granted to powerbi_ro nor excluded - the nightly BI extract would break silently",
                string.Join(", ", unclassified));

            // --- behaviour: SET ROLE then read works, write is refused by privilege, not the default ---
            await using (NpgsqlCommand setRole = new NpgsqlCommand($"SET ROLE {Role}", c))
            {
                await setRole.ExecuteNonQueryAsync();
            }

            long usageCount = (long)(await Scalar("SELECT count(*) FROM usage_events"))!;
            usageCount.Should().BeGreaterThanOrEqualTo(0, "powerbi_ro must be able to read usage_events");

            Func<Task> insert = async () =>
            {
                await using NpgsqlCommand cmd = new NpgsqlCommand(
                    "INSERT INTO usage_events (id, billing_mode, cost_usd, credits_millis, idempotency_key, kind, model_name, "
                    + "occurred_at, organization_id, provider_kind, quantity, source, unpriced) "
                    + "VALUES (gen_random_uuid(), 'Prepaid', 0, 0, 'powerbi-ro-probe', 'Chat', 'm', now(), gen_random_uuid(), 'OpenAi', 0, 'Api', false)", c);
                await cmd.ExecuteNonQueryAsync();
            };
            (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501",
                "the write must be refused by privilege, not merely discouraged by the session default");

            await using (NpgsqlCommand resetRole = new NpgsqlCommand("RESET ROLE", c))
            {
                await resetRole.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// The convergence proof the fresh-database test above structurally cannot make: that test grants
    /// straight to the final ACL, so it never holds a stale entry for the normalization to clear.
    /// This test seeds the state a database that already applied an earlier, wider revision of the grant
    /// is in - a column-level <c>SELECT</c> on a representative column an earlier review round withheld
    /// (<c>application_users.signup_ip</c>, <c>workflow_executions.agent_snapshot_json</c>,
    /// <c>workflow_step_results.tool_calls_json</c> and <c>reasoning_json</c>; the fourth round
    /// withheld six more, but the table-level <c>REVOKE</c> clears them all the same way) - then runs
    /// the same normalization again
    /// (<see cref="PowerBiReadOnlyRoleSql.Up"/> and <see cref="PowerBiReadOnlyRoleEngineSql.Up"/>, the
    /// two blocks of <c>infra/powerbi/postgres-readonly-role.sql</c>) and
    /// proves each stale grant is cleared while a still-granted column on the same table stays readable.
    /// It fails if the normalization is removed, so it exercises the convergence and not the fresh-DB
    /// path. Follows the same per-test database, <see cref="PostgresTestEnvironment"/> and
    /// <c>DROP DATABASE ... WITH (FORCE)</c> conventions as the test above.
    /// </summary>
    [PostgresFact]
    public async Task PowerBiReadOnlyRole_ConvergesFromStaleColumnGrants_OnAMigratedDatabase()
    {
        string? baseConnection = PostgresTestEnvironment.BaseConnectionString();
        if (string.IsNullOrWhiteSpace(baseConnection))
        {
            return;
        }

        string dbName = "reelbolt_powerbi_stale_" + Guid.NewGuid().ToString("N")[..12];
        await using NpgsqlDataSource admin = PostgresTestEnvironment.CreateAdminDataSource(baseConnection);
        await using (NpgsqlCommand create = admin.CreateCommand($"CREATE DATABASE {dbName}"))
        {
            await create.ExecuteNonQueryAsync();
        }

        NpgsqlConnectionStringBuilder csb = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ForDatabase(baseConnection, dbName));
        try
        {
            // Migrate in the compose order, exactly as the fresh-database test does: the Inference API
            // first, the WorkflowEngine after. The role and its grants no longer come from a migration,
            // so the operator script's two blocks are applied below to reach the state a fully-migrated
            // database is in; the stale entries are seeded on top of that.
            await using (InferenceApiDbContext api = new InferenceApiDbContext(
                new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(csb.ConnectionString).Options))
            {
                await api.GetService<IMigrator>().MigrateAsync();
            }

            await using WorkflowEngineDbContext engine = new WorkflowEngineDbContext(
                new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseNpgsql(csb.ConnectionString).Options);
            await engine.GetService<IMigrator>().MigrateAsync();

            // The superuser connection: it seeds the wider grant the normalization must then clear.
            await using NpgsqlConnection c = new NpgsqlConnection(csb.ConnectionString);
            await c.OpenAsync();

            async Task<object?> Scalar(string sql)
            {
                await using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
                return await cmd.ExecuteScalarAsync();
            }

            async Task Exec(string sql)
            {
                await using NpgsqlCommand cmd = new NpgsqlCommand(sql, c);
                await cmd.ExecuteNonQueryAsync();
            }

            // Address each table by pg_class.oid, the same probe shape the fresh-database test uses, so
            // both tests read the server's privilege model the same way.
            async Task<bool> ColumnPrivilege(string table, string column) =>
                (bool)(await Scalar(
                    $"SELECT has_column_privilege('{Role}', c.oid, '{column}', 'SELECT') "
                    + "FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
                    + $"WHERE n.nspname = 'public' AND c.relname = '{table}'"))!;

            // The role no longer comes from a migration: establish the baseline the migration used to
            // create by applying the operator script's two blocks, exactly as the fresh-database test
            // does. The stale seed and the normalization below then run on top of a real, fully granted
            // powerbi_ro.
            await Exec(PowerBiReadOnlyRoleSql.Up);
            await Exec(PowerBiReadOnlyRoleEngineSql.Up);

            // The migrated grant already withholds all four columns. Pin that first, so the TRUE
            // readings after the seed are the seed's doing and not a probe that always answers TRUE.
            (await ColumnPrivilege("application_users", "signup_ip")).Should().BeFalse(
                "the migrated grant already withholds application_users.signup_ip");
            (await ColumnPrivilege("workflow_executions", "agent_snapshot_json")).Should().BeFalse(
                "the migrated grant already withholds workflow_executions.agent_snapshot_json");
            (await ColumnPrivilege("workflow_step_results", "tool_calls_json")).Should().BeFalse(
                "the migrated grant already withholds workflow_step_results.tool_calls_json");
            (await ColumnPrivilege("workflow_step_results", "reasoning_json")).Should().BeFalse(
                "the migrated grant already withholds workflow_step_results.reasoning_json");

            // Seed the earlier, wider grant a database could already be in: column-level SELECT on each
            // withheld column, granted as the superuser this connection is.
            await Exec($"GRANT SELECT (signup_ip) ON application_users TO {Role}");
            await Exec($"GRANT SELECT (agent_snapshot_json) ON workflow_executions TO {Role}");
            await Exec($"GRANT SELECT (tool_calls_json, reasoning_json) ON workflow_step_results TO {Role}");

            // Confirm the seed actually widened the grant - a test that does not would prove nothing.
            (await ColumnPrivilege("application_users", "signup_ip")).Should().BeTrue(
                "the seed must actually widen the grant on application_users.signup_ip");
            (await ColumnPrivilege("workflow_executions", "agent_snapshot_json")).Should().BeTrue(
                "the seed must actually widen the grant on workflow_executions.agent_snapshot_json");
            (await ColumnPrivilege("workflow_step_results", "tool_calls_json")).Should().BeTrue(
                "the seed must actually widen the grant on workflow_step_results.tool_calls_json");
            (await ColumnPrivilege("workflow_step_results", "reasoning_json")).Should().BeTrue(
                "the seed must actually widen the grant on workflow_step_results.reasoning_json");

            // Run the normalization: the same statement set the operator script applies. Remove
            // these two lines and every FALSE assertion below fails - that is the non-vacuity proof.
            await Exec(PowerBiReadOnlyRoleSql.Up);
            await Exec(PowerBiReadOnlyRoleEngineSql.Up);

            // The normalization clears each stale column grant ...
            (await ColumnPrivilege("application_users", "signup_ip")).Should().BeFalse(
                "the normalization must clear the stale application_users.signup_ip grant");
            (await ColumnPrivilege("workflow_executions", "agent_snapshot_json")).Should().BeFalse(
                "the normalization must clear the stale workflow_executions.agent_snapshot_json grant");
            (await ColumnPrivilege("workflow_step_results", "tool_calls_json")).Should().BeFalse(
                "the normalization must clear the stale workflow_step_results.tool_calls_json grant");
            (await ColumnPrivilege("workflow_step_results", "reasoning_json")).Should().BeFalse(
                "the normalization must clear the stale workflow_step_results.reasoning_json grant");

            // ... without taking a column that is still granted on the same tables with it.
            (await ColumnPrivilege("application_users", "email")).Should().BeTrue(
                "the normalization must leave the granted application_users.email readable");
            (await ColumnPrivilege("workflow_executions", "status")).Should().BeTrue(
                "the normalization must leave the granted workflow_executions.status readable");
            (await ColumnPrivilege("workflow_step_results", "status")).Should().BeTrue(
                "the normalization must leave the granted workflow_step_results.status readable");
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using NpgsqlCommand drop = admin.CreateCommand($"DROP DATABASE IF EXISTS {dbName} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
