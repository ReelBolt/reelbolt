#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using ReelBolt.Inference.Api.Data;
using ReelBolt.Shared.Billing;
using ReelBolt.Shared.Data.Models;
using ReelBolt.Shared.Data.Models.Billing;
using ReelBolt.Shared.Metering;
using ReelBolt.Shared.Tenancy;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Data;

/// <summary>
/// Builds, once for the class, the ways a database reaches the least-privilege roles of
/// <c>infra/postgres/engine-role.sql</c> and <c>infra/postgres/gateway-role.sql</c>:
/// <list type="bullet">
/// <item><b>fresh</b>: the API migrates as the admin, both scripts run, then the engine's migrations run in
/// the <c>--migrate-only</c> mode as the MIGRATOR role (so the owner role owns what they create), then the
/// scripts run again (grants on the engine tables can only be made once those tables exist);</item>
/// <item><b>cut-over</b>: the API and the engine both migrated as the admin (production today), then the
/// scripts run and hand the engine tables to the owner role;</item>
/// <item><b>earlier revision</b>: as cut-over, but the runtime role owns the engine tables and may create in
/// the schema (what the first revision of the script left behind), then the scripts run.</item>
/// </list>
/// The roles are cluster-wide, so each run uses its own role names and drops them at the end. The password is
/// an obviously fake test value.
/// </summary>
public sealed class LeastPrivilegeRolesFixture : IAsyncLifetime
{
    public const string EngineScript = "infra/postgres/engine-role.sql";
    public const string GatewayScript = "infra/postgres/gateway-role.sql";
    private const string PowerBiRole = "powerbi_ro";

    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10];
    private string? _base;

    public bool Available => _base is not null;
    public string EngineRole => "rb_engine_test_" + _suffix;
    public string OwnerRole => "rb_engine_owner_test_" + _suffix;
    public string MigratorRole => "rb_engine_migrator_test_" + _suffix;
    public string GatewayRole => "rb_gateway_test_" + _suffix;
    public string FakePassword => "fake-test-password-not-a-secret-" + _suffix;
    public string FreshDatabase => "reelbolt_lp_fresh_" + _suffix;
    public string CutoverDatabase => "reelbolt_lp_cutover_" + _suffix;
    public string EarlierDatabase => "reelbolt_lp_earlier_" + _suffix;
    public string EmptyDatabase => "reelbolt_lp_empty_" + _suffix;
    public const string CheckScript = "infra/postgres/check-roles.sql";

    public static readonly string[] EngineOwnedTables =
    [
        "workflow_definitions", "workflow_steps", "workflow_executions", "workflow_step_results", "review_scores",
        "workflow_step_cache_entries", "decision_observations", "external_generation_jobs", "runner_jobs",
        "runner_sessions", "__EFMigrationsHistory_Workflow"
    ];

    private string[] Databases => [FreshDatabase, CutoverDatabase, EarlierDatabase, EmptyDatabase];

    public string FreshSnapshot { get; private set; } = string.Empty;
    public string FreshSnapshotAfterRerun { get; private set; } = string.Empty;
    public string CutoverSnapshot { get; private set; } = string.Empty;
    public string CutoverSnapshotAfterRerun { get; private set; } = string.Empty;
    public string EarlierSnapshot { get; private set; } = string.Empty;
    public int MigrateOnlyExitCode { get; private set; } = -1;

    /// <summary>The engine's configuration: only connection strings and the startup switch, nothing else.</summary>
    public IConfiguration EngineConfiguration(string defaultConnection, string? migrationConnection = null, string? migrateAtStartup = null)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = defaultConnection,
            ["ConnectionStrings:MigrationConnection"] = migrationConnection,
            [EngineDatabaseStartup.MigrateAtStartupKey] = migrateAtStartup
        }).Build();

    public string Admin(string database) => PostgresTestEnvironment.ForDatabase(_base!, database, 4);

    public string AsRole(string database, string role)
        => new NpgsqlConnectionStringBuilder(_base!) { Database = database, Username = role, Password = FakePassword, MaxPoolSize = 4 }.ConnectionString;

    public InferenceApiDbContext ApiDb(string database)
        => new(new DbContextOptionsBuilder<InferenceApiDbContext>().UseNpgsql(Admin(database)).Options);

    public WorkflowEngineDbContext EngineDb(string connectionString)
        => new(new DbContextOptionsBuilder<WorkflowEngineDbContext>()
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable("__EFMigrationsHistory_Workflow")).Options);

    public Task ApplyEngineScriptAsync(string database)
        => PsqlScriptRunner.RunAsync(Admin(database), EngineScript, new Dictionary<string, string>
        {
            ["engine_role"] = EngineRole,
            ["engine_owner_role"] = OwnerRole,
            ["engine_migrator_role"] = MigratorRole,
            ["engine_password"] = FakePassword,
            ["engine_migrator_password"] = FakePassword
        });

    public Task ApplyGatewayScriptAsync(string database)
        => PsqlScriptRunner.RunAsync(Admin(database), GatewayScript, new Dictionary<string, string>
        {
            ["gateway_role"] = GatewayRole,
            ["engine_owner_role"] = OwnerRole,
            ["engine_migrator_role"] = MigratorRole,
            ["gateway_password"] = FakePassword
        });

    public async Task ApplyScriptsAsync(string database)
    {
        await ApplyEngineScriptAsync(database);
        await ApplyGatewayScriptAsync(database);
    }

    /// <summary>The read-only check an operator or a monitor runs: throws when a role holds more than rows.</summary>
    public Task CheckRolesAsync(string database)
        => PsqlScriptRunner.RunAsync(Admin(database), CheckScript, new Dictionary<string, string>
        {
            ["engine_role"] = EngineRole,
            ["gateway_role"] = GatewayRole,
            ["engine_owner_role"] = OwnerRole,
            ["engine_migrator_role"] = MigratorRole
        });

    public async Task<T> ScalarAsync<T>(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    public async Task ExecuteAsync(string connectionString, string sql)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Every owner and every granted privilege in schema public, the schema's and the database's own ACL and
    /// the roles' attributes, memberships and settings, one per line and sorted: two databases in the same privilege state give the
    /// same text.
    /// </summary>
    public Task<string> SnapshotAsync(string database) => ScalarAsync<string>(Admin(database), """
        SELECT coalesce(string_agg(line, E'\n' ORDER BY line), '') FROM (
          SELECT format('owner %s %s %s', c.relkind, c.relname, pg_get_userbyid(c.relowner)) AS line
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'S')
          UNION ALL
          SELECT format('table %s %s %s grantable=%s', c.relname,
                   CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END, a.privilege_type, a.is_grantable)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace, aclexplode(c.relacl) a
           WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm', 'S')
          UNION ALL
          SELECT format('column %s.%s %s %s', c.relname, t.attname,
                   CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END, a.privilege_type)
            FROM pg_attribute t JOIN pg_class c ON c.oid = t.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace, aclexplode(t.attacl) a
           WHERE n.nspname = 'public' AND t.attacl IS NOT NULL
          UNION ALL
          SELECT format('schema %s %s',
                   CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END, a.privilege_type)
            FROM pg_namespace n, aclexplode(n.nspacl) a WHERE n.nspname = 'public'
          UNION ALL
          SELECT format('database %s %s',
                   CASE WHEN a.grantee = 0 THEN 'PUBLIC' ELSE pg_get_userbyid(a.grantee) END, a.privilege_type)
            FROM pg_database d, aclexplode(d.datacl) a WHERE d.datname = current_database()
          UNION ALL
          SELECT format('role %s super=%s createdb=%s createrole=%s replication=%s bypassrls=%s login=%s limit=%s',
                   rolname, rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolcanlogin, rolconnlimit)
            FROM pg_roles WHERE rolname = ANY (@roles)
          UNION ALL
          SELECT format('member %s in %s', pg_get_userbyid(m.member), pg_get_userbyid(m.roleid))
            FROM pg_auth_members m WHERE pg_get_userbyid(m.member)::text = ANY (@roles)
          UNION ALL
          SELECT format('setting %s %s', r.rolname, array_to_string(c.setconfig, ','))
            FROM pg_db_role_setting c JOIN pg_roles r ON r.oid = c.setrole WHERE r.rolname = ANY (@roles)
        ) s
        """, new NpgsqlParameter("roles", new[] { EngineRole, OwnerRole, MigratorRole, GatewayRole }));

    public async Task InitializeAsync()
    {
        _base = PostgresTestEnvironment.BaseConnectionString();
        if (_base is null)
        {
            return;
        }

        // Cluster-wide and intentionally not dropped: the engine's migrations grant columns to it when it exists.
        await ExecuteAsync(Admin("postgres"),
            $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{PowerBiRole}') THEN CREATE ROLE {PowerBiRole} NOLOGIN; END IF; END $$;");
        foreach (string database in Databases)
        {
            await ExecuteAsync(Admin("postgres"), $"CREATE DATABASE {database}");
        }

        // Fresh: the migrator role runs the engine's migrations in the migrate-only mode. The runtime
        // connection string is there too, as on a real host, and must not be the one used.
        await using (InferenceApiDbContext api = ApiDb(FreshDatabase))
        {
            await api.Database.MigrateAsync();
        }

        await ApplyScriptsAsync(FreshDatabase);
        MigrateOnlyExitCode = await EngineDatabaseStartup.RunMigrateOnlyAsync(
            EngineConfiguration(AsRole(FreshDatabase, EngineRole), AsRole(FreshDatabase, MigratorRole)), NullLogger.Instance);
        await ApplyScriptsAsync(FreshDatabase);
        FreshSnapshot = await SnapshotAsync(FreshDatabase);
        await ApplyScriptsAsync(FreshDatabase);
        FreshSnapshotAfterRerun = await SnapshotAsync(FreshDatabase);

        // Cut-over: everything migrated as the admin first.
        await using (InferenceApiDbContext api = ApiDb(CutoverDatabase))
        {
            await api.Database.MigrateAsync();
        }

        await using (WorkflowEngineDbContext engine = EngineDb(Admin(CutoverDatabase)))
        {
            await engine.Database.MigrateAsync();
        }

        await ApplyScriptsAsync(CutoverDatabase);
        CutoverSnapshot = await SnapshotAsync(CutoverDatabase);
        await ApplyScriptsAsync(CutoverDatabase);
        CutoverSnapshotAfterRerun = await SnapshotAsync(CutoverDatabase);

        // Earlier revision of the script: the runtime role owns the engine tables and may create in the schema.
        await using (InferenceApiDbContext api = ApiDb(EarlierDatabase))
        {
            await api.Database.MigrateAsync();
        }

        await using (WorkflowEngineDbContext engine = EngineDb(Admin(EarlierDatabase)))
        {
            await engine.Database.MigrateAsync();
        }

        foreach (string table in EngineOwnedTables)
        {
            await ExecuteAsync(Admin(EarlierDatabase), $"ALTER TABLE public.\"{table}\" OWNER TO {EngineRole}");
        }

        await ExecuteAsync(Admin(EarlierDatabase), $"GRANT USAGE, CREATE ON SCHEMA public TO {EngineRole}");
        await ApplyScriptsAsync(EarlierDatabase);
        EarlierSnapshot = await SnapshotAsync(EarlierDatabase);
    }

    public async Task DisposeAsync()
    {
        if (_base is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();
        foreach (string database in Databases)
        {
            await ExecuteAsync(Admin("postgres"), $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }

        foreach (string role in new[] { EngineRole, MigratorRole, OwnerRole, GatewayRole })
        {
            await ExecuteAsync(Admin("postgres"), $"DROP ROLE IF EXISTS {role}");
        }

        NpgsqlConnection.ClearAllPools();
    }
}

/// <summary>
/// The engine and the runner gateway log in as least-privilege roles (RB-SEC-002): these tests run the
/// operator scripts on a real PostgreSQL and prove the roles can do what the code does and nothing else.
/// </summary>
[Collection("Postgres")]
[Trait("Category", "Postgres")]
public class LeastPrivilegeRolesPostgresTests(LeastPrivilegeRolesFixture fixture) : IClassFixture<LeastPrivilegeRolesFixture>
{
    private const string Denied = "42501";

    private static readonly string[] EngineOwnedTables = LeastPrivilegeRolesFixture.EngineOwnedTables;

    private string Db => fixture.FreshDatabase;
    private string EngineConnection => fixture.AsRole(Db, fixture.EngineRole);
    private string GatewayConnection => fixture.AsRole(Db, fixture.GatewayRole);

    [PostgresFact]
    public void Fresh_install_and_cut_over_end_in_the_same_privilege_state_and_a_rerun_changes_nothing()
    {
        fixture.FreshSnapshot.Should().Contain($"role {fixture.EngineRole} ").And.Contain($"role {fixture.GatewayRole} ");
        fixture.FreshSnapshotAfterRerun.Should().Be(fixture.FreshSnapshot, "applying the scripts twice changes nothing");
        fixture.CutoverSnapshotAfterRerun.Should().Be(fixture.CutoverSnapshot, "applying the scripts twice changes nothing");
        fixture.CutoverSnapshot.Should().Be(fixture.FreshSnapshot,
            "handing the tables of an engine that migrated as a superuser over to the owner role ends where a fresh install does");
        fixture.EarlierSnapshot.Should().Be(fixture.FreshSnapshot,
            "a database where the runtime role owned the engine tables (the script's first revision) converges too");
    }

    [PostgresFact]
    public async Task The_owner_role_owns_the_engine_tables_in_every_order_and_powerbi_grants_survive()
    {
        fixture.MigrateOnlyExitCode.Should().Be(0, "the migrate-only mode ran the engine's migrations as the migrator role");
        foreach (string database in new[] { fixture.FreshDatabase, fixture.CutoverDatabase, fixture.EarlierDatabase })
        {
            foreach (string table in EngineOwnedTables)
            {
                string owner = await fixture.ScalarAsync<string>(fixture.Admin(database),
                    "SELECT pg_get_userbyid(c.relowner)::text FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace "
                    + "WHERE n.nspname = 'public' AND c.relname = @t", new NpgsqlParameter("t", table));
                owner.Should().Be(fixture.OwnerRole, $"{table} in {database}");
            }

            // The engine's migrations grant these to powerbi_ro; that needs ownership, or a grant that survives the hand-over.
            (await ColumnAsync(database, "powerbi_ro", "workflow_executions", "status", "SELECT")).Should().BeTrue(database);
            (await ColumnAsync(database, "powerbi_ro", "workflow_step_results", "failure_code", "SELECT")).Should().BeTrue(database);
            (await ColumnAsync(database, "powerbi_ro", "workflow_executions", "result_json", "SELECT")).Should().BeFalse(database);

            // Nothing in the schema belongs to a role the render host can log in as, or to the migrator itself.
            (await fixture.ScalarAsync<long>(fixture.Admin(database),
                "SELECT count(*) FROM pg_class c WHERE pg_get_userbyid(c.relowner)::text = ANY (@roles)",
                new NpgsqlParameter("roles", new[] { fixture.EngineRole, fixture.GatewayRole, fixture.MigratorRole }))).Should().Be(0, database);
            await fixture.CheckRolesAsync(database);
        }
    }

    [PostgresFact]
    public async Task What_the_migrator_creates_belongs_to_the_owner_role()
    {
        string migrator = fixture.AsRole(Db, fixture.MigratorRole);
        (await fixture.ScalarAsync<string>(migrator, "SELECT current_user::text")).Should().Be(fixture.OwnerRole);
        (await fixture.ScalarAsync<string>(migrator, "SELECT session_user::text")).Should().Be(fixture.MigratorRole);

        // What a NEW migration would do: the table is the owner role's, and the runtime role has nothing on it
        // until the role script runs again.
        await fixture.ExecuteAsync(migrator, "CREATE TABLE public.rb_probe_new_table (id integer)");
        try
        {
            (await fixture.ScalarAsync<string>(fixture.Admin(Db),
                "SELECT pg_get_userbyid(relowner)::text FROM pg_class WHERE relname = 'rb_probe_new_table'")).Should().Be(fixture.OwnerRole);
            await RefusedAsync(EngineConnection, "SELECT * FROM rb_probe_new_table");
        }
        finally
        {
            await fixture.ExecuteAsync(migrator, "DROP TABLE public.rb_probe_new_table");
        }
    }

    [PostgresFact]
    public async Task The_runtime_role_starts_without_migrating_and_refuses_to_start_while_a_migration_is_pending()
    {
        // Migrated by the migrator: the runtime role only reads the history table and finds nothing pending.
        await using (WorkflowEngineDbContext db = fixture.EngineDb(EngineConnection))
        {
            await EngineDatabaseStartup.PrepareAsync(db, fixture.EngineConfiguration(EngineConnection, migrateAtStartup: "false"), NullLogger.Instance);
        }

        // With the default (migrate at startup) the runtime role cannot do it: it may not create anything.
        await using (WorkflowEngineDbContext db = fixture.EngineDb(EngineConnection))
        {
            Func<Task> migrate = () => db.Database.MigrateAsync();
            await migrate.Should().ThrowAsync<Exception>("the runtime role holds no DDL right");
        }

        // One migration behind (the control host has not run the migrator yet): startup is refused by name.
        string history = "public.\"__EFMigrationsHistory_Workflow\"";
        string last = await fixture.ScalarAsync<string>(fixture.Admin(fixture.EarlierDatabase),
            $"SELECT max(\"MigrationId\")::text FROM {history}");
        string version = await fixture.ScalarAsync<string>(fixture.Admin(fixture.EarlierDatabase),
            $"SELECT \"ProductVersion\"::text FROM {history} WHERE \"MigrationId\" = '{last}'");
        await fixture.ExecuteAsync(fixture.Admin(fixture.EarlierDatabase), $"DELETE FROM {history} WHERE \"MigrationId\" = '{last}'");
        try
        {
            string runtime = fixture.AsRole(fixture.EarlierDatabase, fixture.EngineRole);
            await using WorkflowEngineDbContext db = fixture.EngineDb(runtime);
            Func<Task> start = () => EngineDatabaseStartup.PrepareAsync(db, fixture.EngineConfiguration(runtime, migrateAtStartup: "false"), NullLogger.Instance);
            PendingMigrationsException refusal = (await start.Should().ThrowAsync<PendingMigrationsException>()).Which;
            refusal.Pending.Should().Equal(last);
            refusal.Message.Should().Contain(last).And.Contain("--migrate-only").And.Contain("Database:MigrateAtStartup");
        }
        finally
        {
            await fixture.ExecuteAsync(fixture.Admin(fixture.EarlierDatabase),
                $"INSERT INTO {history} (\"MigrationId\", \"ProductVersion\") VALUES ('{last}', '{version}')");
        }

        // Nothing migrated at all: every migration is pending, and the check itself needs no table to exist.
        await fixture.ApplyScriptsAsync(fixture.EmptyDatabase);
        string empty = fixture.AsRole(fixture.EmptyDatabase, fixture.EngineRole);
        await using WorkflowEngineDbContext none = fixture.EngineDb(empty);
        Func<Task> startEmpty = () => EngineDatabaseStartup.PrepareAsync(none, fixture.EngineConfiguration(empty, migrateAtStartup: "false"), NullLogger.Instance);
        (await startEmpty.Should().ThrowAsync<PendingMigrationsException>()).Which.Pending.Should().NotBeEmpty();
    }

    [PostgresFact]
    public async Task The_runtime_role_cannot_plant_code_that_another_login_would_run()
    {
        // The escalation this design closes: a role that owns a table can attach a trigger to it, and the
        // trigger's function runs as whoever writes next (the superuser API login). Every step is refused.
        string[] refused =
        [
            "CREATE FUNCTION public.rb_evil() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END'",
            "CREATE FUNCTION pg_temp.rb_evil() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END'",
            "CREATE TRIGGER rb_evil BEFORE INSERT ON workflow_definitions FOR EACH ROW EXECUTE FUNCTION suppress_redundant_updates_trigger()",
            "CREATE TRIGGER rb_evil BEFORE INSERT ON workflow_executions FOR EACH ROW EXECUTE FUNCTION suppress_redundant_updates_trigger()",
            "CREATE TRIGGER rb_evil BEFORE INSERT ON project_files FOR EACH ROW EXECUTE FUNCTION suppress_redundant_updates_trigger()",
            "CREATE RULE rb_evil AS ON INSERT TO workflow_definitions DO ALSO NOTIFY rb_evil",
            "CREATE INDEX rb_evil ON workflow_executions (status)",
            "ALTER TABLE workflow_executions ADD COLUMN rb_evil integer",
            "ALTER TABLE workflow_definitions ENABLE ROW LEVEL SECURITY",
            "TRUNCATE workflow_step_cache_entries",
            "DROP TABLE workflow_definitions",
            "CREATE TABLE public.rb_evil (id integer)",
            "CREATE TEMPORARY TABLE rb_evil (id integer)",
            "CREATE SCHEMA rb_evil",
            "CREATE VIEW public.rb_evil AS SELECT 1",
            $"SET ROLE {fixture.OwnerRole}",
            $"SET ROLE {fixture.MigratorRole}"
        ];
        foreach (string sql in refused)
        {
            await RefusedAsync(EngineConnection, sql);
            await RefusedAsync(GatewayConnection, sql);
        }
    }

    [PostgresFact]
    public async Task The_scripts_and_the_check_stop_when_a_runtime_owned_trigger_function_exists()
    {
        // What a past compromise of the old design would have left behind, planted here as the admin.
        string database = fixture.CutoverDatabase;
        string before = await fixture.SnapshotAsync(database);
        await fixture.ExecuteAsync(fixture.Admin(database),
            "CREATE FUNCTION public.rb_rogue() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END'");
        try
        {
            await fixture.ExecuteAsync(fixture.Admin(database), $"ALTER FUNCTION public.rb_rogue() OWNER TO {fixture.EngineRole}");
            await fixture.ExecuteAsync(fixture.Admin(database),
                "CREATE TRIGGER rb_rogue BEFORE INSERT ON workflow_definitions FOR EACH ROW EXECUTE FUNCTION public.rb_rogue()");

            foreach (Func<Task> run in new Func<Task>[]
            {
                () => fixture.ApplyEngineScriptAsync(database),
                () => fixture.ApplyScriptsAsync(database),
                () => fixture.CheckRolesAsync(database)
            })
            {
                PostgresException stop = (await run.Should().ThrowAsync<PostgresException>()).Which;
                stop.MessageText.Should().Contain("least-privilege check failed").And.Contain("rb_rogue").And.Contain("remove it by hand");
            }

            // The function alone (no trigger yet) is already a finding: the runtime role must own nothing.
            await fixture.ExecuteAsync(fixture.Admin(database), "DROP TRIGGER rb_rogue ON workflow_definitions");
            Func<Task> check = () => fixture.CheckRolesAsync(database);
            (await check.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("owns: function");
        }
        finally
        {
            await fixture.ExecuteAsync(fixture.Admin(database), "DROP TRIGGER IF EXISTS rb_rogue ON workflow_definitions");
            await fixture.ExecuteAsync(fixture.Admin(database), "DROP FUNCTION IF EXISTS public.rb_rogue()");
        }

        // Nothing was dropped or half applied by the refused runs, and the clean database passes again.
        await fixture.ApplyScriptsAsync(database);
        await fixture.CheckRolesAsync(database);
        (await fixture.SnapshotAsync(database)).Should().Be(before);
    }

    [PostgresFact]
    public async Task The_runtime_role_holds_exactly_its_rights_on_the_engine_tables()
    {
        Dictionary<string, string[]> expected = new()
        {
            ["workflow_definitions"] = ["SELECT", "INSERT"],
            ["workflow_steps"] = ["SELECT", "INSERT"],
            ["workflow_executions"] = ["SELECT", "UPDATE"],
            ["workflow_step_results"] = ["SELECT", "INSERT", "UPDATE"],
            ["review_scores"] = ["SELECT", "INSERT"],
            ["workflow_step_cache_entries"] = ["SELECT", "INSERT", "UPDATE", "DELETE"],
            ["decision_observations"] = ["SELECT", "INSERT", "UPDATE"],
            ["external_generation_jobs"] = ["SELECT", "INSERT", "UPDATE"],
            ["runner_jobs"] = ["SELECT"],
            ["runner_sessions"] = ["SELECT"],
            ["\"__EFMigrationsHistory_Workflow\""] = ["SELECT"]
        };
        expected.Should().HaveCount(EngineOwnedTables.Length);
        foreach ((string table, string[] granted) in expected)
        {
            foreach (string privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "REFERENCES", "TRIGGER" })
            {
                (await TableAsync(Db, fixture.EngineRole, table, privilege)).Should().Be(granted.Contains(privilege), $"{privilege} on {table}");
            }
        }
        // Review-loop adjustments update the two adjustable settings columns of a snapshot step, nothing else.
        (await ColumnAsync(Db, fixture.EngineRole, "workflow_steps", "video_compile_config_json", "UPDATE")).Should().BeTrue();
        (await ColumnAsync(Db, fixture.EngineRole, "workflow_steps", "video_analyze_config_json", "UPDATE")).Should().BeTrue();
        (await ColumnAsync(Db, fixture.EngineRole, "workflow_steps", "agent_definition_id", "UPDATE")).Should().BeFalse();
    }

    [PostgresFact]
    public async Task The_scripts_run_on_an_empty_database_twice()
    {
        await fixture.ApplyScriptsAsync(fixture.EmptyDatabase);
        await fixture.ApplyScriptsAsync(fixture.EmptyDatabase);
        await fixture.CheckRolesAsync(fixture.EmptyDatabase);

        foreach ((string role, bool mayCreate) in new[] { (fixture.OwnerRole, true), (fixture.EngineRole, false), (fixture.GatewayRole, false) })
        {
            (await fixture.ScalarAsync<bool>(fixture.Admin(fixture.EmptyDatabase),
                "SELECT has_schema_privilege(@r, 'public', 'CREATE')", new NpgsqlParameter("r", role))).Should().Be(mayCreate, role);
        }
    }

    [PostgresFact]
    public async Task The_roles_have_least_privilege_attributes_and_only_the_migrator_has_a_membership()
    {
        foreach ((string role, string login, int limit, string[] memberOf) in new[]
        {
            (fixture.EngineRole, "t", 40, Array.Empty<string>()),
            (fixture.GatewayRole, "t", 28, Array.Empty<string>()),
            (fixture.OwnerRole, "f", -1, Array.Empty<string>()),
            (fixture.MigratorRole, "t", 4, new[] { fixture.OwnerRole })
        })
        {
            string attributes = await fixture.ScalarAsync<string>(fixture.Admin(Db),
                "SELECT format('login=%s super=%s createdb=%s createrole=%s replication=%s bypassrls=%s limit=%s', "
                + "rolcanlogin, rolsuper, rolcreatedb, rolcreaterole, rolreplication, rolbypassrls, rolconnlimit) "
                + "FROM pg_roles WHERE rolname = @r", new NpgsqlParameter("r", role));
            attributes.Should().Be($"login={login} super=f createdb=f createrole=f replication=f bypassrls=f limit={limit}", role);

            (await fixture.ScalarAsync<string>(fixture.Admin(Db),
                "SELECT coalesce(string_agg(pg_get_userbyid(m.roleid)::text, ','), '') FROM pg_auth_members m "
                + "JOIN pg_roles r ON r.oid = m.member WHERE r.rolname = @r",
                new NpgsqlParameter("r", role))).Should().Be(string.Join(',', memberOf), role);
        }
    }

    [PostgresFact]
    public async Task The_engine_role_can_read_every_table_and_column_its_context_maps_except_the_withheld_secrets()
    {
        await using WorkflowEngineDbContext model = fixture.EngineDb(EngineConnection);
        string[] withheld = ["application_users.password_hash", "application_users.signup_ip", "api_keys.secret_hash"];
        List<string> unreadable = [];
        foreach (IEntityType entity in model.Model.GetEntityTypes().Where(e => e.GetTableName() is not null))
        {
            string table = entity.GetTableName()!;
            foreach (IProperty property in entity.GetProperties())
            {
                string column = property.GetColumnName();
                bool readable = await ColumnAsync(Db, fixture.EngineRole, table, column, "SELECT");
                if (readable == withheld.Contains($"{table}.{column}"))
                {
                    unreadable.Add($"{table}.{column} readable={readable}");
                }
            }
        }

        unreadable.Should().BeEmpty("the engine role reads exactly the mapped columns minus the withheld ones (infra/postgres/engine-role.sql)");
    }

    [PostgresFact]
    public async Task The_engine_role_settles_credits_and_records_usage_through_the_real_code()
    {
        Seed seed = await SeedAsync();
        await using (InferenceApiDbContext api = fixture.ApiDb(Db))
        {
            (await new CreditLedger(api).GrantAsync(seed.Organization, CreditGrantSource.Plan, 1_000_000, null, Key())).Ok.Should().BeTrue();
        }

        // Reserve under an API key (reads api_keys by projection, inserts credit_ledger_entries and api_key_spend,
        // updates credit_accounts), then settle (updates credit_grants and api_key_spend).
        CreditLedgerResult reserve;
        await using (WorkflowEngineDbContext db = fixture.EngineDb(EngineConnection))
        {
            reserve = await new CreditLedger(db).ReserveAsync(seed.Organization, 10_000, Key(), new CreditLedgerContext(ApiKeyId: seed.ApiKey));
        }

        reserve.Ok.Should().BeTrue(reserve.Error);
        await using (WorkflowEngineDbContext db = fixture.EngineDb(EngineConnection))
        {
            CreditLedgerResult settle = await new CreditLedger(db).SettleAsync(seed.Organization, reserve.Entry!.Id, 4_000, Key());
            settle.Ok.Should().BeTrue(settle.Error);
        }

        // A workspace with no account yet: the ledger inserts credit_accounts and credit_grants as the engine role.
        Seed second = await SeedAsync();
        await using (WorkflowEngineDbContext db = fixture.EngineDb(EngineConnection))
        {
            CreditLedgerResult grant = await new CreditLedger(db).GrantAsync(second.Organization, CreditGrantSource.Plan, 5_000, null, Key());
            grant.Ok.Should().BeTrue(grant.Error);
        }

        ServiceCollection services = new();
        services.AddDbContext<WorkflowEngineDbContext>(o => o.UseNpgsql(EngineConnection));
        await using ServiceProvider provider = services.BuildServiceProvider();
        string usageKey = Key();
        await new EfUsageEventSink<WorkflowEngineDbContext>(provider.GetRequiredService<IServiceScopeFactory>()).WriteAsync(
        [
            new UsageEvent { Id = Guid.NewGuid(), OrganizationId = seed.Organization, Quantity = 1, ModelName = "test-model", IdempotencyKey = usageKey }
        ], CancellationToken.None);
        (await fixture.ScalarAsync<long>(EngineConnection, "SELECT count(*) FROM usage_events WHERE idempotency_key = @k",
            new NpgsqlParameter("k", usageKey))).Should().Be(1);
    }

    [PostgresFact]
    public async Task The_engine_role_performs_one_real_write_per_granted_table_and_operation()
    {
        Seed seed = await SeedAsync();
        await using WorkflowEngineDbContext db = fixture.EngineDb(EngineConnection);

        // The engine's tables: it inserts a definition (the run snapshot) and updates a run the API inserted.
        WorkflowDefinition definition = new() { Id = Guid.NewGuid(), Name = "least-privilege", ProjectId = seed.Project };
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        Guid executionId = Guid.NewGuid();
        await using (WorkflowEngineDbContext api = fixture.EngineDb(fixture.Admin(Db)))
        {
            api.WorkflowExecutions.Add(new WorkflowExecution
            {
                Id = executionId,
                WorkflowDefinitionId = definition.Id,
                ProjectId = seed.Project,
                OrganizationId = seed.Organization
            });
            await api.SaveChangesAsync();
        }

        WorkflowExecution execution = await db.WorkflowExecutions.SingleAsync(e => e.Id == executionId);
        execution.IterationCount = 1;
        await db.SaveChangesAsync();

        // workflow_steps: INSERT (the snapshot) and the review-loop adjustment, through the code that writes it.
        Guid agentId = Guid.NewGuid();
        await using (InferenceApiDbContext api = fixture.ApiDb(Db))
        {
            api.AgentDefinitions.Add(new AgentDefinition { Id = agentId, Name = "least-privilege", AgentType = AgentType.VideoTransform, IsBuiltIn = true });
            await api.SaveChangesAsync();
        }

        WorkflowStep step = new() { Id = Guid.NewGuid(), WorkflowDefinitionId = definition.Id, AgentDefinitionId = agentId, StepOrder = 1, StepType = StepType.VideoCompile };
        db.WorkflowSteps.Add(step);
        await db.SaveChangesAsync();
        ReelBolt.Shared.Workflows.Review.ReviewAdjustableSettings.SetConfigJson(step, "{\"crf\":20}");
        await db.SaveChangesAsync();
        step.StepType = StepType.VideoAnalyze;
        ReelBolt.Shared.Workflows.Review.ReviewAdjustableSettings.SetConfigJson(step, "{\"fps\":2}");
        Func<Task> otherColumn = () => db.SaveChangesAsync();
        (await otherColumn.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be("42501", "step_type is not an adjustable column");
        db.Entry(step).Property(s => s.StepType).CurrentValue = StepType.VideoCompile;
        db.Entry(step).Property(s => s.StepType).IsModified = false;
        await db.SaveChangesAsync();

        // project_files: INSERT, UPDATE, DELETE.
        ProjectFile file = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = seed.Project,
            OriginalFileName = "out.mp4",
            StorageKey = $"projects/{seed.Project}/out.mp4",
            StorageBucket = "test-bucket",
            MimeType = "video/mp4",
            SizeBytes = 1
        };
        db.ProjectFiles.Add(file);
        await db.SaveChangesAsync();
        file.OutputProducedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ProjectFiles.Remove(file);
        await db.SaveChangesAsync();

        // media_derivatives: INSERT, UPDATE.
        MediaDerivative derivative = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = seed.Project,
            SourceStorageKey = "src",
            SourceETag = "etag",
            ParamsHash = Guid.NewGuid().ToString("N"),
            CacheKey = Guid.NewGuid().ToString("N"),
            StorageKey = "derived",
            StorageBucket = "test-bucket"
        };
        db.MediaDerivatives.Add(derivative);
        await db.SaveChangesAsync();
        derivative.SizeBytes = 2;
        await db.SaveChangesAsync();

        // composition_workspaces: INSERT, UPDATE; composition_workspace_versions: INSERT.
        CompositionWorkspace workspace = new()
        {
            Id = Guid.NewGuid(),
            ProjectId = seed.Project,
            OrganizationId = seed.Organization,
            OriginExecutionId = execution.Id
        };
        db.CompositionWorkspaces.Add(workspace);
        await db.SaveChangesAsync();
        CompositionWorkspaceVersion version = new()
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.Id,
            ProjectId = seed.Project,
            VersionNumber = 1,
            StorageKey = "workspace/v1.tar",
            Sha256 = new string('a', 64),
            SizeBytes = 1,
            FileCount = 1
        };
        db.CompositionWorkspaceVersions.Add(version);
        workspace.LatestVersionNumber = 1;
        await db.SaveChangesAsync();

        // composition_revisions: UPDATE of result_version_id only. edit_image_graphic_requests: UPDATE of result_* only.
        (Guid revisionId, Guid requestId) = await SeedRequestsAsync(seed, workspace.Id, execution.Id, definition.Id);
        CompositionRevision revision = await db.CompositionRevisions.SingleAsync(r => r.Id == revisionId);
        revision.ResultVersionId = version.Id;
        await db.SaveChangesAsync();
        EditImageGraphicRequest request = await db.EditImageGraphicRequests.SingleAsync(r => r.Id == requestId);
        request.ResultStorageKey = "graphics/result.png";
        request.ResultWidth = 10;
        request.ResultRecordedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // content_moderation_decisions: INSERT.
        db.ContentModerationDecisions.Add(new ContentModerationDecision
        {
            Id = Guid.NewGuid(),
            OrganizationId = seed.Organization,
            Surface = "test",
            Action = "allow",
            ContentKind = "text",
            ContentSha256 = new string('b', 64),
            ContentLength = 1,
            ProviderKind = "test",
            Model = "test-model",
            ModelSnapshot = "test-model",
            ExpiresAt = DateTime.UtcNow.AddDays(1)
        });
        await db.SaveChangesAsync();

        // Advisory locks (CreditLedger, VideoGenerationBudgetGate) and the sweeper's FOR UPDATE SKIP LOCKED claim.
        await using NpgsqlConnection connection = new(EngineConnection);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await using (NpgsqlCommand advisory = new("SELECT pg_advisory_xact_lock(hashtext('least-privilege'))", connection, transaction))
        {
            await advisory.ExecuteNonQueryAsync();
        }

        await using (NpgsqlCommand claim = new(
            "SELECT id FROM workflow_executions WHERE id = @id FOR UPDATE SKIP LOCKED", connection, transaction))
        {
            claim.Parameters.AddWithValue("id", execution.Id);
            (await claim.ExecuteScalarAsync()).Should().Be(execution.Id);
        }

        await transaction.CommitAsync();
    }

    [PostgresFact]
    public async Task The_engine_role_is_refused_everything_outside_its_grants()
    {
        // The tables named below exist, so a refusal is a privilege refusal and not a missing table.
        foreach (string table in new[] { "platform_secrets", "user_tokens", "application_users", "api_keys", "projects" })
        {
            (await fixture.ScalarAsync<bool>(fixture.Admin(Db), $"SELECT to_regclass('public.{table}') IS NOT NULL")).Should().BeTrue(table);
        }

        string[] refused =
        [
            "SELECT password_hash FROM application_users",
            "SELECT signup_ip FROM application_users",
            "SELECT * FROM application_users",
            "SELECT secret_hash FROM api_keys",
            "SELECT * FROM api_keys",
            "SELECT * FROM platform_secrets",
            "SELECT count(*) FROM platform_secrets",
            "SELECT * FROM user_tokens",
            "SELECT count(*) FROM user_tokens",
            "COPY workflow_definitions (name) FROM PROGRAM 'id'",
            "COPY (SELECT 1) TO PROGRAM 'cat'",
            "SELECT pg_read_file('/etc/passwd')",
            "CREATE ROLE rb_should_not_exist",
            "CREATE DATABASE rb_should_not_exist",
            "ALTER TABLE projects ADD COLUMN rb_should_not_exist integer",
            "DROP TABLE projects",
            "TRUNCATE project_files",
            "DELETE FROM projects",
            "DELETE FROM workflow_executions",
            "DELETE FROM workflow_definitions",
            "UPDATE workflow_definitions SET name = 'x'",
            "INSERT INTO runner_sessions DEFAULT VALUES",
            "UPDATE runner_jobs SET status = 'x'",
            "DELETE FROM \"__EFMigrationsHistory_Workflow\"",
            "DELETE FROM usage_events",
            "DELETE FROM credit_ledger_entries",
            "UPDATE projects SET name = 'x'",
            "UPDATE credit_ledger_entries SET note = 'x'",
            "UPDATE composition_revisions SET instruction = 'x'",
            "UPDATE edit_image_graphic_requests SET description = 'x'",
            "INSERT INTO platform_settings (key) VALUES ('x')",
            "UPDATE application_users SET is_admin = true",
            "UPDATE api_keys SET revoked_at = NULL"
        ];
        foreach (string sql in refused)
        {
            await RefusedAsync(EngineConnection, sql);
        }

        // The columns it is granted on the two partial tables still read.
        await fixture.ExecuteAsync(EngineConnection, "SELECT id, email, is_admin FROM application_users LIMIT 1");
        await fixture.ExecuteAsync(EngineConnection, "SELECT id, organization_id, revoked_at, expires_at, daily_cap_millis FROM api_keys LIMIT 1");
    }

    [PostgresFact]
    public async Task The_gateway_role_holds_exactly_its_privilege_matrix()
    {
        string role = fixture.GatewayRole;
        foreach (string table in new[] { "runner_devices", "runner_jobs", "runner_sessions", "platform_settings" })
        {
            foreach (string privilege in new[] { "SELECT", "INSERT", "UPDATE" })
            {
                (await TableAsync(Db, role, table, privilege)).Should().BeTrue($"{privilege} on {table}");
            }
        }

        Dictionary<string, string[]> columns = new()
        {
            ["workflow_executions"] = ["id", "status", "workflow_definition_id", "organization_id"],
            ["workflow_definitions"] = ["id", "name", "project_id"],
            ["projects"] = ["id", "name", "organization_id"],
            ["organizations"] = ["id", "name"]
        };
        foreach ((string table, string[] granted) in columns)
        {
            (await TableAsync(Db, role, table, "SELECT")).Should().BeFalse($"{table} is column-level");
            List<string> all = await ColumnsAsync(table);
            foreach (string column in all)
            {
                (await ColumnAsync(Db, role, table, column, "SELECT")).Should().Be(granted.Contains(column), $"{table}.{column}");
                (await ColumnAsync(Db, role, table, column, "UPDATE")).Should().BeFalse($"{table}.{column}");
            }

            granted.Should().BeSubsetOf(all);
        }

        // Nothing else: no table outside the eight, and no DELETE, TRUNCATE, REFERENCES or TRIGGER anywhere.
        string[] listed = ["runner_devices", "runner_jobs", "runner_sessions", "platform_settings", .. columns.Keys];
        (await fixture.ScalarAsync<long>(fixture.Admin(Db), """
            SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
             WHERE n.nspname = 'public' AND c.relkind IN ('r', 'p', 'v', 'm')
               AND (has_table_privilege(@r, c.oid, 'DELETE, TRUNCATE, REFERENCES, TRIGGER')
                    OR (c.relname <> ALL (@listed)
                        AND (has_table_privilege(@r, c.oid, 'SELECT, INSERT, UPDATE')
                             OR has_any_column_privilege(@r, c.oid, 'SELECT, INSERT, UPDATE, REFERENCES'))))
            """, new NpgsqlParameter("r", role), new NpgsqlParameter("listed", listed))).Should().Be(0);
        (await fixture.ScalarAsync<bool>(fixture.Admin(Db), "SELECT has_schema_privilege(@r, 'public', 'CREATE')",
            new NpgsqlParameter("r", role))).Should().BeFalse();
        (await fixture.ScalarAsync<bool>(fixture.Admin(Db), "SELECT has_schema_privilege(@r, 'public', 'USAGE')",
            new NpgsqlParameter("r", role))).Should().BeTrue();
    }

    [PostgresFact]
    public async Task The_gateway_role_runs_its_statements_and_is_refused_ddl_and_everything_else()
    {
        // The shapes of the gateway's own SQL (sandbox/cmd/runner-gateway: display.go, queue.go, switches.go, sessions.go, pool_nodes.go).
        string[] allowed =
        [
            """
            SELECT coalesce(o.name, ''), coalesce(p.name, ''), coalesce(w.name, ''), p.id::text, w.id::text, e.id::text
              FROM workflow_executions e
              JOIN workflow_definitions w ON w.id = e.workflow_definition_id
              JOIN projects p ON p.id = w.project_id AND p.organization_id = gen_random_uuid()
              JOIN organizations o ON o.id = p.organization_id
             WHERE e.id = gen_random_uuid() AND e.organization_id = gen_random_uuid()
            """,
            "SELECT j.id FROM runner_jobs j WHERE EXISTS (SELECT 1 FROM workflow_executions e WHERE e.id = j.execution_id AND e.status::text <> 'Running') LIMIT 1",
            "SELECT id FROM runner_jobs ORDER BY priority DESC, created_at ASC, id ASC LIMIT 1 FOR UPDATE SKIP LOCKED",
            "UPDATE runner_jobs SET updated_at = now() WHERE id = gen_random_uuid()",
            "UPDATE runner_sessions SET last_op_at = now() WHERE id = gen_random_uuid()",
            "UPDATE runner_devices SET revoked_at = now() WHERE id = gen_random_uuid()",
            "SELECT key, paused, pool_slot_cap FROM platform_settings",
            "UPDATE platform_settings SET paused = paused WHERE key = 'rb-no-such-key'",
            "SELECT pg_advisory_xact_lock(hashtextextended('least-privilege', 0))",
            "LISTEN runner_jobs_new",
            "SELECT pg_notify('runner_jobs_new', 'x')"
        ];
        foreach (string sql in allowed)
        {
            await fixture.ExecuteAsync(GatewayConnection, sql);
        }

        string[] refused =
        [
            "CREATE TABLE public.rb_should_not_exist (id integer)",
            "ALTER TABLE runner_jobs ADD COLUMN rb_should_not_exist integer",
            "DROP TABLE runner_jobs",
            "CREATE INDEX rb_should_not_exist ON runner_jobs (status)",
            "TRUNCATE runner_jobs",
            "DELETE FROM runner_jobs",
            "DELETE FROM runner_devices",
            "CREATE ROLE rb_should_not_exist",
            "CREATE DATABASE rb_should_not_exist",
            "SELECT result_json FROM workflow_executions",
            "SELECT * FROM projects",
            "SELECT slug FROM organizations",
            "UPDATE projects SET name = 'x'",
            "SELECT email FROM application_users",
            "SELECT * FROM platform_secrets",
            "SELECT * FROM user_tokens",
            "SELECT * FROM api_keys",
            "SELECT * FROM credit_accounts",
            "COPY (SELECT 1) TO PROGRAM 'cat'"
        ];
        foreach (string sql in refused)
        {
            await RefusedAsync(GatewayConnection, sql);
        }
    }

    /// <summary>
    /// Every place code can hide on a table, planted on an engine table as the admin (as a former owner could
    /// have). Each entry: the statements that plant it, the statements that remove it, and the object the
    /// refusal must name. <c>{owner}</c> is the owner role, <c>{evt}</c> a throwaway role name.
    /// </summary>
    private static readonly Dictionary<string, (string[] Plant, string[] Remove, string Named)> HidingPlaces = new()
    {
        ["column default"] = (
            ["CREATE FUNCTION public.rb_hide() RETURNS integer LANGUAGE sql AS 'SELECT 1'",
             "ALTER TABLE review_scores ADD COLUMN rb_hide_col integer DEFAULT public.rb_hide()"],
            ["ALTER TABLE review_scores DROP COLUMN IF EXISTS rb_hide_col", "DROP FUNCTION IF EXISTS public.rb_hide()"],
            "function public.rb_hide()"),
        ["generated column"] = (
            ["CREATE FUNCTION public.rb_hide(integer) RETURNS integer LANGUAGE sql IMMUTABLE AS 'SELECT $1'",
             "ALTER TABLE review_scores ADD COLUMN rb_hide_col integer GENERATED ALWAYS AS (public.rb_hide(1)) STORED"],
            ["ALTER TABLE review_scores DROP COLUMN IF EXISTS rb_hide_col", "DROP FUNCTION IF EXISTS public.rb_hide(integer)"],
            "function public.rb_hide(integer)"),
        ["rule"] = (
            ["CREATE RULE rb_hide_rule AS ON INSERT TO review_scores DO ALSO NOTIFY rb_hide"],
            ["DROP RULE IF EXISTS rb_hide_rule ON review_scores"],
            "rule rb_hide_rule on review_scores"),
        ["check constraint"] = (
            ["CREATE FUNCTION public.rb_hide(integer) RETURNS integer LANGUAGE sql IMMUTABLE AS 'SELECT $1'",
             "ALTER TABLE review_scores ADD CONSTRAINT rb_hide_check CHECK (public.rb_hide(1) = 1)"],
            ["ALTER TABLE review_scores DROP CONSTRAINT IF EXISTS rb_hide_check", "DROP FUNCTION IF EXISTS public.rb_hide(integer)"],
            "CHECK constraint rb_hide_check"),
        ["expression index"] = (
            ["CREATE FUNCTION public.rb_hide(integer) RETURNS integer LANGUAGE sql IMMUTABLE AS 'SELECT $1'",
             "CREATE INDEX rb_hide_expr_idx ON review_scores ((public.rb_hide(1)))"],
            ["DROP INDEX IF EXISTS public.rb_hide_expr_idx", "DROP FUNCTION IF EXISTS public.rb_hide(integer)"],
            "index rb_hide_expr_idx"),
        ["partial index predicate"] = (
            ["CREATE FUNCTION public.rb_hide(integer) RETURNS integer LANGUAGE sql IMMUTABLE AS 'SELECT $1'",
             "CREATE INDEX rb_hide_pred_idx ON review_scores (id) WHERE public.rb_hide(1) = 1"],
            ["DROP INDEX IF EXISTS public.rb_hide_pred_idx", "DROP FUNCTION IF EXISTS public.rb_hide(integer)"],
            "index rb_hide_pred_idx"),
        ["row security"] = (
            ["ALTER TABLE review_scores ENABLE ROW LEVEL SECURITY"],
            ["ALTER TABLE review_scores DISABLE ROW LEVEL SECURITY"],
            "row security is enabled on review_scores"),
        ["policy"] = (
            ["CREATE POLICY rb_hide_policy ON review_scores USING (true)"],
            ["DROP POLICY IF EXISTS rb_hide_policy ON review_scores"],
            "policy rb_hide_policy on review_scores"),
        ["trigger with an owner-role function"] = (
            ["CREATE FUNCTION public.rb_hide_trg() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END'",
             "ALTER FUNCTION public.rb_hide_trg() OWNER TO {owner}",
             "CREATE TRIGGER rb_hide_trigger BEFORE INSERT ON review_scores FOR EACH ROW EXECUTE FUNCTION public.rb_hide_trg()"],
            ["DROP TRIGGER IF EXISTS rb_hide_trigger ON review_scores", "DROP FUNCTION IF EXISTS public.rb_hide_trg()"],
            "trigger rb_hide_trigger on review_scores"),
        ["domain type with a check"] = (
            ["CREATE DOMAIN public.rb_hide_domain AS integer CHECK (VALUE > 0)",
             "ALTER TABLE review_scores ADD COLUMN rb_hide_col public.rb_hide_domain"],
            ["ALTER TABLE review_scores DROP COLUMN IF EXISTS rb_hide_col", "DROP DOMAIN IF EXISTS public.rb_hide_domain"],
            "type public.rb_hide_domain"),
        ["event trigger"] = (
            ["CREATE ROLE {evt} SUPERUSER",
             "CREATE FUNCTION public.rb_hide_evt() RETURNS event_trigger LANGUAGE plpgsql AS 'BEGIN END'",
             "CREATE EVENT TRIGGER rb_hide_event ON ddl_command_start EXECUTE FUNCTION public.rb_hide_evt()",
             "ALTER EVENT TRIGGER rb_hide_event OWNER TO {evt}",
             "ALTER ROLE {evt} NOSUPERUSER"],
            ["DROP EVENT TRIGGER IF EXISTS rb_hide_event", "DROP FUNCTION IF EXISTS public.rb_hide_evt()", "DROP ROLE IF EXISTS {evt}"],
            "event trigger not owned by a superuser: rb_hide_event")
    };

    [PostgresTheory]
    [InlineData("column default")]
    [InlineData("generated column")]
    [InlineData("rule")]
    [InlineData("check constraint")]
    [InlineData("expression index")]
    [InlineData("partial index predicate")]
    [InlineData("row security")]
    [InlineData("policy")]
    [InlineData("trigger with an owner-role function")]
    [InlineData("domain type with a check")]
    [InlineData("event trigger")]
    public async Task Code_hidden_on_an_engine_table_stops_both_role_scripts_and_the_check(string place)
    {
        // A trigger is not the only place: each of these runs as whoever writes the row next and survives a
        // change of owner. The check allows only what the engine's migrations create, so each one is refused.
        HidingPlaces.Should().HaveCount(11);
        (string[] plant, string[] remove, string named) = HidingPlaces[place];
        string database = fixture.CutoverDatabase;
        string Fill(string sql) => sql.Replace("{owner}", fixture.OwnerRole).Replace("{evt}", fixture.EngineRole + "_evt");
        try
        {
            foreach (string sql in plant)
            {
                await fixture.ExecuteAsync(fixture.Admin(database), Fill(sql));
            }

            string planted = await fixture.SnapshotAsync(database);
            foreach ((string script, Func<Task> run) in new (string, Func<Task>)[]
            {
                ("engine-role.sql", () => fixture.ApplyEngineScriptAsync(database)),
                ("gateway-role.sql", () => fixture.ApplyGatewayScriptAsync(database)),
                ("check-roles.sql", () => fixture.CheckRolesAsync(database))
            })
            {
                PostgresException stop = (await run.Should().ThrowAsync<PostgresException>($"{script} must refuse a {place}")).Which;
                stop.MessageText.Should().Contain("least-privilege check failed, nothing was changed").And.Contain(named, script);
                stop.MessageText.Should().Contain("remove it by hand");
            }

            (await fixture.SnapshotAsync(database)).Should().Be(planted, "a refused run changes nothing and drops nothing");
        }
        finally
        {
            foreach (string sql in remove)
            {
                await fixture.ExecuteAsync(fixture.Admin(database), Fill(sql));
            }
        }

        await fixture.ApplyScriptsAsync(database);
        await fixture.CheckRolesAsync(database);
    }

    [PostgresFact]
    public async Task A_default_planted_by_a_runtime_owner_stops_the_hand_over()
    {
        // The table still belongs to the runtime role (the script's first revision), which planted a default.
        // The hand-over to the owner role must not go through with it in place.
        string database = fixture.EarlierDatabase;
        string before = await fixture.SnapshotAsync(database);
        await fixture.ExecuteAsync(fixture.Admin(database), $"ALTER TABLE review_scores OWNER TO {fixture.EngineRole}");
        try
        {
            await fixture.ExecuteAsync(fixture.Admin(database), "CREATE FUNCTION public.rb_hide() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
            await fixture.ExecuteAsync(fixture.Admin(database), $"ALTER FUNCTION public.rb_hide() OWNER TO {fixture.EngineRole}");
            await fixture.ExecuteAsync(fixture.Admin(database), "ALTER TABLE review_scores ADD COLUMN rb_hide_col integer DEFAULT public.rb_hide()");

            Func<Task> handOver = () => fixture.ApplyEngineScriptAsync(database);
            PostgresException stop = (await handOver.Should().ThrowAsync<PostgresException>()).Which;
            stop.MessageText.Should().Contain("default or generated expression of column rb_hide_col on review_scores")
                .And.Contain("function public.rb_hide()").And.Contain("owns: ");

            (await fixture.ScalarAsync<string>(fixture.Admin(database),
                "SELECT pg_get_userbyid(relowner)::text FROM pg_class WHERE relname = 'review_scores'"))
                .Should().Be(fixture.EngineRole, "the hand-over was refused, so the owner did not change");
        }
        finally
        {
            await fixture.ExecuteAsync(fixture.Admin(database), "ALTER TABLE review_scores DROP COLUMN IF EXISTS rb_hide_col");
            await fixture.ExecuteAsync(fixture.Admin(database), "DROP FUNCTION IF EXISTS public.rb_hide()");
        }

        // Cleaned by hand: the hand-over goes through and the database is where it was.
        await fixture.ApplyScriptsAsync(database);
        (await fixture.SnapshotAsync(database)).Should().Be(before);
    }

    [PostgresFact]
    public async Task On_an_api_table_only_an_object_of_a_foreign_non_superuser_is_a_finding()
    {
        // project_files is API-owned and the runtime role writes it. The API's owner may attach what it likes.
        string database = fixture.CutoverDatabase;
        try
        {
            await fixture.ExecuteAsync(fixture.Admin(database), "CREATE FUNCTION public.rb_api_fn() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
            await fixture.ExecuteAsync(fixture.Admin(database), "ALTER TABLE project_files ADD COLUMN rb_api_col integer DEFAULT public.rb_api_fn()");
            await fixture.ExecuteAsync(fixture.Admin(database), "CREATE RULE rb_api_rule AS ON INSERT TO project_files DO ALSO NOTIFY rb_api");
            await fixture.CheckRolesAsync(database);
            await fixture.ApplyScriptsAsync(database);

            // The same default, once its function belongs to another non-superuser, is refused by name.
            await fixture.ExecuteAsync(fixture.Admin(database), "ALTER FUNCTION public.rb_api_fn() OWNER TO powerbi_ro");
            foreach (Func<Task> run in new Func<Task>[]
            {
                () => fixture.CheckRolesAsync(database),
                () => fixture.ApplyEngineScriptAsync(database),
                () => fixture.ApplyGatewayScriptAsync(database)
            })
            {
                (await run.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should()
                    .Contain("default or generated expression of column rb_api_col on project_files")
                    .And.Contain("function public.rb_api_fn(), owned by powerbi_ro");
            }
        }
        finally
        {
            await fixture.ExecuteAsync(fixture.Admin(database), "DROP RULE IF EXISTS rb_api_rule ON project_files");
            await fixture.ExecuteAsync(fixture.Admin(database), "ALTER TABLE project_files DROP COLUMN IF EXISTS rb_api_col");
            await fixture.ExecuteAsync(fixture.Admin(database), "DROP FUNCTION IF EXISTS public.rb_api_fn()");
        }

        await fixture.CheckRolesAsync(database);
    }

    [PostgresFact]
    public async Task A_fully_migrated_database_with_the_read_only_role_scripts_applied_passes_the_check()
    {
        // The legitimate setup must not trip the deny-by-default check: API and engine migrations at head, the
        // Power BI and Grafana read-only roles, then the role scripts and the check.
        string database = fixture.FreshDatabase;
        await PsqlScriptRunner.RunAsync(fixture.Admin(database), "infra/powerbi/postgres-readonly-role.sql", new Dictionary<string, string>());

        // The Grafana script is written for one run on one database (bare CREATE ROLE, a <db> placeholder);
        // roles are cluster-wide here and the test server is shared, so the role is created only when missing.
        await fixture.ExecuteAsync(fixture.Admin(database),
            "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'grafana_ro') THEN CREATE ROLE grafana_ro LOGIN; END IF; END $$;");
        string grafana = PsqlScriptRunner.Render(PsqlScriptRunner.Read("infra/grafana/postgres-readonly-role.sql"), new Dictionary<string, string>())
            .Replace("CREATE ROLE grafana_ro LOGIN;", string.Empty)
            .Replace("<db>", database);
        await fixture.ExecuteAsync(fixture.Admin(database), grafana);

        await fixture.ApplyScriptsAsync(database);
        await fixture.CheckRolesAsync(database);
        (await ColumnAsync(database, "grafana_ro", "workflow_step_results", "failure_code", "SELECT")).Should().BeTrue();
        (await ColumnAsync(database, "powerbi_ro", "workflow_executions", "status", "SELECT")).Should().BeTrue();
    }

    [PostgresTheory]
    [InlineData("table grant", "CREATE TABLE IF NOT EXISTS public.rb_platform_secrets (id integer); GRANT SELECT ON public.rb_platform_secrets TO {engine}",
        "DROP TABLE IF EXISTS public.rb_platform_secrets", "{engine} holds SELECT on rb_platform_secrets, which is outside its grant list")]
    [InlineData("column grant", "GRANT UPDATE (agent_definition_id) ON workflow_steps TO {engine}",
        "REVOKE UPDATE (agent_definition_id) ON workflow_steps FROM {engine}", "{engine} holds UPDATE (agent_definition_id) on workflow_steps")]
    [InlineData("sequence update", "CREATE SEQUENCE IF NOT EXISTS public.rb_seq; GRANT UPDATE ON SEQUENCE public.rb_seq TO {gateway}",
        "DROP SEQUENCE IF EXISTS public.rb_seq", "{gateway} holds SELECT or UPDATE on sequence public.rb_seq")]
    [InlineData("predefined role", "GRANT pg_read_all_data TO {migrator}",
        "REVOKE pg_read_all_data FROM {migrator}", "{migrator} is a member of: pg_read_all_data")]
    [InlineData("owner login", "ALTER ROLE {owner} LOGIN",
        "ALTER ROLE {owner} NOLOGIN", "{owner} can log in; the owner role must be NOLOGIN")]
    [InlineData("large object", "SELECT lo_create(424242); ALTER LARGE OBJECT 424242 OWNER TO {gateway}",
        "SELECT lo_unlink(424242)", "{gateway} owns large objects: 424242")]
    public async Task The_check_refuses_anything_beyond_the_privilege_matrix(string what, string plant, string remove, string named)
    {
        // The check run between releases must see what someone changed by hand since the last release.
        string database = fixture.CutoverDatabase;
        string Fill(string sql) => sql.Replace("{engine}", fixture.EngineRole).Replace("{gateway}", fixture.GatewayRole)
            .Replace("{owner}", fixture.OwnerRole).Replace("{migrator}", fixture.MigratorRole);
        try
        {
            await fixture.ExecuteAsync(fixture.Admin(database), Fill(plant));
            Func<Task> check = () => fixture.CheckRolesAsync(database);
            (await check.Should().ThrowAsync<PostgresException>(what)).Which.MessageText.Should()
                .Contain("least-privilege check failed").And.Contain(Fill(named), what);
        }
        finally
        {
            await fixture.ExecuteAsync(fixture.Admin(database), Fill(remove));
        }

        await fixture.CheckRolesAsync(database);
    }

    [PostgresFact]
    public async Task A_misspelled_role_name_fails_the_check()
    {
        Func<Task> check = () => PsqlScriptRunner.RunAsync(fixture.Admin(fixture.CutoverDatabase), LeastPrivilegeRolesFixture.CheckScript,
            new Dictionary<string, string>
            {
                ["engine_role"] = fixture.EngineRole + "_typo",
                ["gateway_role"] = fixture.GatewayRole,
                ["engine_owner_role"] = fixture.OwnerRole,
                ["engine_migrator_role"] = fixture.MigratorRole
            });
        (await check.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should()
            .Contain($"role {fixture.EngineRole}_typo does not exist");
    }

    [PostgresFact]
    public async Task A_catalog_function_overload_planted_in_public_never_runs_as_the_superuser()
    {
        // quote_ident(name) in public is an exact match for pg_class.relname, which the closing block quotes:
        // with the default search_path the superuser session would call it instead of pg_catalog.quote_ident(text).
        string database = fixture.CutoverDatabase;
        try
        {
            await fixture.ExecuteAsync(fixture.Admin(database), "CREATE TABLE public.rb_hijack (n integer)");
            await fixture.ExecuteAsync(fixture.Admin(database),
                "CREATE FUNCTION public.quote_ident(name) RETURNS text LANGUAGE sql AS 'INSERT INTO public.rb_hijack VALUES (1); SELECT pg_catalog.quote_ident($1::text)'");
            (await fixture.ScalarAsync<int>(fixture.Admin(database), "SELECT length(quote_ident(relname)) FROM pg_class WHERE relname = 'rb_hijack'"))
                .Should().BeGreaterThan(0);
            (await fixture.ScalarAsync<long>(fixture.Admin(database), "SELECT count(*) FROM public.rb_hijack"))
                .Should().Be(1, "unpinned, the planted overload wins");

            await fixture.ApplyScriptsAsync(database);
            await fixture.CheckRolesAsync(database);
            (await fixture.ScalarAsync<long>(fixture.Admin(database), "SELECT count(*) FROM public.rb_hijack"))
                .Should().Be(1, "the scripts pin search_path, so the overload never ran inside them");
        }
        finally
        {
            await fixture.ExecuteAsync(fixture.Admin(database), "DROP FUNCTION IF EXISTS public.quote_ident(name)");
            await fixture.ExecuteAsync(fixture.Admin(database), "DROP TABLE IF EXISTS public.rb_hijack");
        }
    }

    private sealed record Seed(Guid Organization, Guid User, Guid Project, Guid ApiKey);

    private static string Key() => Guid.NewGuid().ToString("N");

    /// <summary>As the admin (the API's login): one workspace, user, project and API key.</summary>
    private async Task<Seed> SeedAsync()
    {
        Seed seed = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        await using InferenceApiDbContext api = fixture.ApiDb(Db);
        api.Organizations.Add(new Organization
        {
            Id = seed.Organization,
            Name = "least-privilege",
            Slug = "lp-" + seed.Organization.ToString("N"),
            Kind = OrganizationKind.Team
        });
        api.ApplicationUsers.Add(new ApplicationUser { Id = seed.User, Email = $"{seed.User:N}@example.com", DisplayName = "least-privilege" });
        await api.SaveChangesAsync();
        api.Projects.Add(new Project { Id = seed.Project, Name = "least-privilege", OwnerId = seed.User, OrganizationId = seed.Organization });
        api.ApiKeys.Add(new ApiKey
        {
            Id = seed.ApiKey,
            OrganizationId = seed.Organization,
            CreatedByUserId = seed.User,
            ServiceUserId = seed.User,
            Name = "least-privilege",
            KeyId = seed.ApiKey.ToString("N")[..12],
            SecretHash = "fake-hash-not-a-secret",
            LastFour = "abcd",
            Scopes = ["runs.read"],
            DailyCapMillis = 100_000
        });
        await api.SaveChangesAsync();
        return seed;
    }

    /// <summary>
    /// As the admin: the two API-written request rows the engine later links a result to. The rows point at
    /// records this test has no reason to build (an image document, a source run), so the foreign-key checks
    /// are off for this one insert.
    /// </summary>
    private async Task<(Guid Revision, Guid Request)> SeedRequestsAsync(Seed seed, Guid workspace, Guid execution, Guid definition)
    {
        Guid revision = Guid.NewGuid(), request = Guid.NewGuid();
        await using InferenceApiDbContext api = fixture.ApiDb(Db);
        await api.Database.OpenConnectionAsync();
        await api.Database.ExecuteSqlRawAsync("SET session_replication_role = replica");
        api.CompositionRevisions.Add(new CompositionRevision
        {
            Id = revision,
            ProjectId = seed.Project,
            OrganizationId = seed.Organization,
            WorkspaceId = workspace,
            SourceExecutionId = execution,
            ExecutionId = execution,
            WorkflowDefinitionId = definition,
            Instruction = "least-privilege",
            RequestedByUserId = seed.User
        });
        api.EditImageGraphicRequests.Add(new EditImageGraphicRequest
        {
            Id = request,
            ProjectId = seed.Project,
            OrganizationId = seed.Organization,
            DocumentId = Guid.NewGuid(),
            ExecutionId = execution,
            WorkflowDefinitionId = definition,
            Description = "least-privilege",
            FrameWidth = 100,
            FrameHeight = 100,
            RequestedByUserId = seed.User
        });
        await api.SaveChangesAsync();
        await api.Database.ExecuteSqlRawAsync("SET session_replication_role = DEFAULT");
        return (revision, request);
    }

    private async Task RefusedAsync(string connectionString, string sql)
    {
        Func<Task> act = () => fixture.ExecuteAsync(connectionString, sql);
        (await act.Should().ThrowAsync<PostgresException>(sql)).Which.SqlState.Should().Be(Denied, sql);
    }

    private Task<bool> TableAsync(string database, string role, string table, string privilege)
        => fixture.ScalarAsync<bool>(fixture.Admin(database), "SELECT has_table_privilege(@r, to_regclass(@t), @p)",
            new NpgsqlParameter("r", role), new NpgsqlParameter("t", "public." + table), new NpgsqlParameter("p", privilege));

    private Task<bool> ColumnAsync(string database, string role, string table, string column, string privilege)
        => fixture.ScalarAsync<bool>(fixture.Admin(database), "SELECT has_column_privilege(@r, to_regclass(@t), @c, @p)",
            new NpgsqlParameter("r", role), new NpgsqlParameter("t", "public." + table), new NpgsqlParameter("c", column),
            new NpgsqlParameter("p", privilege));

    private async Task<List<string>> ColumnsAsync(string table)
    {
        string joined = await fixture.ScalarAsync<string>(fixture.Admin(Db),
            "SELECT string_agg(column_name::text, ',') FROM information_schema.columns WHERE table_schema = 'public' AND table_name = @t",
            new NpgsqlParameter("t", table));
        return [.. joined.Split(',')];
    }
}
