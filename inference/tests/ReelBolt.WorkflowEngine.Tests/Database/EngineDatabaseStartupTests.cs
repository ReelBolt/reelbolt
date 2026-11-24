#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using ReelBolt.WorkflowEngine.Data;
using ReelBolt.WorkflowEngine.Tests.Infrastructure;
using Xunit;

namespace ReelBolt.WorkflowEngine.Tests.Database;

/// <summary>
/// The engine's startup switch (<c>Database:MigrateAtStartup</c>) and its <c>--migrate-only</c> mode: the
/// render host's engine must be able to start without any right to change the schema, and say clearly why it
/// will not start when the schema is behind.
/// </summary>
public class EngineDatabaseStartupTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData(" False ", false)]
    public void MigrateAtStartup_defaults_to_true_and_reads_a_boolean(string? raw, bool expected)
        => EngineDatabaseStartup.MigrateAtStartup(Config((EngineDatabaseStartup.MigrateAtStartupKey, raw))).Should().Be(expected);

    [Theory]
    [InlineData("no")]
    [InlineData("0")]
    [InlineData("flase")]
    public void MigrateAtStartup_refuses_a_value_that_is_not_a_boolean(string raw)
    {
        Action read = () => EngineDatabaseStartup.MigrateAtStartup(Config((EngineDatabaseStartup.MigrateAtStartupKey, raw)));
        read.Should().Throw<InvalidOperationException>().WithMessage("*Database:MigrateAtStartup*");
    }

    [Fact]
    public void The_shipped_appsettings_keep_migrating_at_startup()
    {
        IConfiguration shipped = new ConfigurationBuilder()
            .AddJsonFile(System.IO.Path.Combine(PsqlScriptRunner.RepoRoot(), "inference", "src", "ReelBolt.WorkflowEngine", "appsettings.json"), optional: false)
            .Build();
        shipped[EngineDatabaseStartup.MigrateAtStartupKey].Should().Be("True");
        EngineDatabaseStartup.MigrateAtStartup(shipped).Should().BeTrue("a single host keeps today's behaviour with no new setting");
    }

    [Theory]
    [InlineData(new[] { "--migrate-only" }, true)]
    [InlineData(new[] { "--urls", "http://localhost:1", "--MIGRATE-ONLY" }, true)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--migrate" }, false)]
    public void IsMigrateOnly_reads_the_command_line(string[] args, bool expected)
        => EngineDatabaseStartup.IsMigrateOnly(args).Should().Be(expected);

    [Fact]
    public void The_migration_connection_wins_and_the_default_connection_is_the_fallback()
    {
        EngineDatabaseStartup.MigrationConnectionString(Config(
            ("ConnectionStrings:DefaultConnection", "Host=runtime"),
            ("ConnectionStrings:MigrationConnection", "Host=migrator"))).Should().Be("Host=migrator");
        EngineDatabaseStartup.MigrationConnectionString(Config(
            ("ConnectionStrings:DefaultConnection", "Host=runtime"),
            ("ConnectionStrings:MigrationConnection", " "))).Should().Be("Host=runtime");
        EngineDatabaseStartup.MigrationConnectionString(Config(("ConnectionStrings:DefaultConnection", "Host=runtime"))).Should().Be("Host=runtime");
    }

    [Fact]
    public async Task Migrate_only_fails_with_exit_code_1_when_no_connection_string_is_configured()
        => (await EngineDatabaseStartup.RunMigrateOnlyAsync(Config(), NullLogger.Instance)).Should().Be(1);

    [Fact]
    public async Task Migrate_only_fails_with_exit_code_1_when_the_database_cannot_be_reached()
    {
        // Port 1 on loopback refuses at once; nothing else (RabbitMQ, JWT, storage) is configured at all.
        IConfiguration config = Config(("ConnectionStrings:MigrationConnection", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=fake;Timeout=2"));
        (await EngineDatabaseStartup.RunMigrateOnlyAsync(config, NullLogger.Instance)).Should().Be(1);
    }

    [Fact]
    public async Task With_migrations_off_the_engine_refuses_to_start_while_one_is_pending_and_names_them()
    {
        await using SqliteConnection connection = new("DataSource=:memory:");
        await connection.OpenAsync();
        await using WorkflowEngineDbContext db = new(new DbContextOptionsBuilder<WorkflowEngineDbContext>().UseSqlite(connection).Options);
        List<string> all = [.. db.Database.GetMigrations()];
        all.Should().NotBeEmpty();

        Func<Task> start = () => EngineDatabaseStartup.PrepareAsync(
            db, Config((EngineDatabaseStartup.MigrateAtStartupKey, "false")), NullLogger.Instance);

        PendingMigrationsException refusal = (await start.Should().ThrowAsync<PendingMigrationsException>()).Which;
        refusal.Pending.Should().Equal(all);
        refusal.Message.Should().Contain(all[0]).And.Contain(all[^1])
            .And.Contain("Database:MigrateAtStartup").And.Contain("--migrate-only").And.Contain("infra/postgres/engine-role.sql");

        // It did not create anything: not even the history table.
        await using SqliteCommand tables = connection.CreateCommand();
        tables.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table'";
        ((long)(await tables.ExecuteScalarAsync())!).Should().Be(0);
    }
}
